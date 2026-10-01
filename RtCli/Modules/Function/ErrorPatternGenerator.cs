using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RtCli.Modules.Unit;

namespace RtCli.Modules.Function
{
    /// <summary>
    /// .fx genpattern: 从 AI 记忆里的"服务器常见错误"样本生成错误识别表达式,
    /// 去重后追加到 regex_settings.yml 的 custom_base_entries(基础分析条目)。
    /// 生成的表达式必须能编译为 .NET 正则, 且不能与已有内置/自定义表达式重复。
    /// </summary>
    internal static class ErrorPatternGenerator
    {
        private const string ThisName = "PatternGen";
        private const int MaxNewEntries = 5;
        private const int MaxSamples = 30;
        private const int MaxExistingInPrompt = 40;

        /// <summary>追加与落盘的临界区(命令本身在锁外执行, 避免 AI 调用阻塞全域命令锁)</summary>
        private static readonly object _applyLock = new();

        public static (bool Success, string Message) Generate() => GenerateAsync().GetAwaiter().GetResult();

        private static async Task<(bool Success, string Message)> GenerateAsync()
        {
            var memories = AiMemoryManager.Load();
            if (memories.Count == 0)
                return (false, "AI 记忆为空: 请先启用 AI 自动化的记忆功能并让它运行一段时间");

            var samples = CollectErrorSamples(memories);
            if (samples.Count == 0)
                return (false, "记忆中没有可用的错误样本(需勾选「记录服务器常见错误」并积累运行)");

            var existing = ContentManager.GetAllBaseEntries()
                .Select(e => (e.Pattern ?? "").Trim())
                .Where(p => p.Length > 0)
                .ToList();

            string systemPrompt = BuildSystemPrompt();
            string userPrompt = BuildUserPrompt(existing, samples);

            Output.Log($"正在请求 AI 生成错误识别表达式 (模型: {ContentManager.Ai?.Console_Error?.Model})…", 1, ThisName);
            string? reply = await CallAiAsync(systemPrompt, userPrompt);
            if (string.IsNullOrWhiteSpace(reply))
                return (false, "AI 请求失败或返回空内容, 请检查 ai_settings.yml 的 api_endpoint / api_key / model");

            var candidates = ParseCandidates(reply);
            if (candidates.Count == 0)
                return (false, "AI 未返回可解析的表达式(要求为 JSON 数组)");

            // 先做静态校验 + 与已有表达式去重, 再进临界区追加
            var existingKeys = new HashSet<string>(existing.Select(p => p.ToLowerInvariant()), StringComparer.Ordinal);
            var accepted = new List<BaseEntry>();
            var rejected = new List<string>();
            foreach (var c in candidates)
            {
                if (accepted.Count >= MaxNewEntries) break;
                if (!IsUsablePattern(c.Pattern, samples, out string reason))
                {
                    rejected.Add($"{c.Pattern} ({reason})");
                    continue;
                }
                if (!existingKeys.Add(c.Pattern.Trim().ToLowerInvariant())) continue; // 与已有重复
                accepted.Add(c);
            }

            if (accepted.Count == 0)
            {
                string detail = rejected.Count > 0 ? "被拒绝: " + string.Join("; ", rejected.Take(3)) : "全部与现有表达式重复";
                return (false, $"未能生成新的错误识别表达式({detail})");
            }

            int added;
            lock (_applyLock)
            {
                var custom = ContentManager.Regex.CustomBaseEntries ??= new List<BaseEntry>();
                // 再校验一次: 以当前列表为准(可能与并发命令/热重载竞争)
                var nowKeys = new HashSet<string>(
                    ContentManager.GetAllBaseEntries().Select(e => (e.Pattern ?? "").Trim().ToLowerInvariant()),
                    StringComparer.Ordinal);
                var toAdd = new List<BaseEntry>();
                foreach (var c in accepted)
                {
                    if (!nowKeys.Add(c.Pattern.Trim().ToLowerInvariant())) continue;
                    toAdd.Add(c);
                }
                custom.AddRange(toAdd);
                added = toAdd.Count;
                if (added > 0) ContentManager.SaveRegexSettingsFile();
                accepted = toAdd;
            }

            if (added == 0)
                return (false, "未能写入: 生成期间已有相同表达式被加入配置");

            var sb = new StringBuilder();
            sb.AppendLine($"已生成并写入 {added} 条错误识别表达式(custom_base_entries):");
            foreach (var e in accepted)
                sb.AppendLine($"  · {e.Topic} ← {e.Pattern}");
            sb.AppendLine("已即时生效(基础分析下次运行即使用), 并已写入 regex_settings.yml 持久化。");
            string message = sb.ToString().TrimEnd();
            Output.Log(message, 1, ThisName);
            return (true, message);
        }

        // ================= 样本 / 提示词 =================

        /// <summary>从记忆里挑出像错误的行: 优先"常见错误"类记忆, 不足则从全部记忆中补</summary>
        private static List<string> CollectErrorSamples(List<AiMemoryManager.MemoryEntry> memories)
        {
            var samples = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Take(string? content)
            {
                foreach (var raw in (content ?? "").Split('\n'))
                {
                    if (samples.Count >= MaxSamples) return;
                    string line = raw.Trim();
                    if (line.Length < 8) continue;

                    string lower = line.ToLowerInvariant();
                    bool looksError = lower.Contains("error") || lower.Contains("exception")
                        || lower.Contains("caused by") || lower.Contains("warn")
                        || line.Contains("错误") || line.Contains("异常") || line.Contains("警告");
                    if (!looksError) continue;

                    // 去掉行首时间戳/线程前缀, 只留错误特征
                    string key = Regex.Replace(line, @"^\[[^\]]*\]\s*", "");
                    if (key.Length > 300) key = key.Substring(0, 300);
                    if (seen.Add(key)) samples.Add(key);
                }
            }

            foreach (var m in memories.Where(m => (m.Topic ?? "").Contains("错误"))) Take(m.Content);
            foreach (var m in memories) { if (samples.Count >= MaxSamples) break; Take(m.Content); }

            return samples.Take(MaxSamples).ToList();
        }

        private static string BuildSystemPrompt()
            => "你是 Minecraft 服务端日志分析专家。根据用户提供的错误样本, 生成用于\"错误识别\"的正则表达式条目,\n"
             + "这些条目会写入 RtCli 的 regex_settings.yml → custom_base_entries, 供基础错误分析使用。\n\n"
             + "硬性要求:\n"
             + "1. 只输出 JSON 数组, 不要任何解释、不要 markdown 代码块。\n"
             + "2. 每个元素形如 {\"pattern\":\"...\",\"topic\":\"...\",\"solution\":\"...\",\"action\":\"\"}。\n"
             + "3. pattern 必须是合法的 .NET 正则, 锚定错误的关键特征(异常类名/插件名/关键短语);\n"
             + "   不要包含行首时间戳, 不要匹配整行, 不要过于宽泛(例如只写 error 或 .* 都是无效的)。\n"
             + "4. topic 用简短中文概括问题; solution 给出可操作的处理步骤; 没有额外动作时 action 留空字符串。\n"
             + "5. 不要与\"已有表达式\"重复或语义等价; 最多生成 " + MaxNewEntries + " 条。\n"
             + "6. JSON 字符串里的反斜杠要按 JSON 规则转义。";

        private static string BuildUserPrompt(List<string> existing, List<string> samples)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 已有表达式(请勿重复或语义等价) ===");
            if (existing.Count == 0) sb.AppendLine("(无)");
            else foreach (var p in existing.Take(MaxExistingInPrompt)) sb.AppendLine(p);
            sb.AppendLine();
            sb.AppendLine("=== 错误样本 ===");
            foreach (var s in samples) sb.AppendLine(s);
            return sb.ToString();
        }

        // ================= AI 调用与解析 =================

        private static async Task<string?> CallAiAsync(string systemPrompt, string userPrompt)
        {
            var cfg = ContentManager.Ai?.Console_Error;
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.ApiEndpoint))
            {
                Output.Log("AI API 地址未配置(ai_settings.yml → api_endpoint)", 2, ThisName);
                return null;
            }

            try
            {
                int timeout = Math.Min(Math.Max(cfg.RequestTimeoutSeconds, 10), 120);
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeout) };
                if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
                    http.DefaultRequestHeaders.Add("Authorization", $"Bearer {cfg.ApiKey}");

                var body = new
                {
                    model = cfg.Model,
                    messages = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt }
                    },
                    max_tokens = cfg.MaxTokens,
                    temperature = 0.2,
                    stream = false
                };

                string json = JsonConvert.SerializeObject(body);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await http.PostAsync(cfg.ApiEndpoint, content);

                if (!response.IsSuccessStatusCode)
                {
                    Output.Log($"AI 请求失败 (HTTP {(int)response.StatusCode})", 3, ThisName);
                    return null;
                }

                string responseText = await response.Content.ReadAsStringAsync();
                var jo = JObject.Parse(responseText);
                return jo["choices"]?[0]?["message"]?["content"]?.ToString();
            }
            catch (TaskCanceledException)
            {
                Output.Log("AI 请求超时", 3, ThisName);
                return null;
            }
            catch (Exception ex)
            {
                Output.Log($"AI 请求异常: {ex.Message}", 3, ThisName);
                return null;
            }
        }

        internal static List<BaseEntry> ParseCandidates(string text)
        {
            var list = new List<BaseEntry>();
            int start = text.IndexOf('[');
            int end = text.LastIndexOf(']');
            if (start < 0 || end <= start) return list;

            try
            {
                var arr = JArray.Parse(text.Substring(start, end - start + 1));
                foreach (var item in arr)
                {
                    string pattern = (item["pattern"] ?? item["Pattern"])?.ToString()?.Trim() ?? "";
                    string topic = (item["topic"] ?? item["Topic"])?.ToString()?.Trim() ?? "";
                    string solution = (item["solution"] ?? item["Solution"])?.ToString()?.Trim() ?? "";
                    string action = (item["action"] ?? item["Action"])?.ToString()?.Trim() ?? "";
                    if (pattern.Length == 0 || topic.Length == 0) continue;
                    list.Add(new BaseEntry(pattern, topic, solution, action));
                }
            }
            catch (Exception ex)
            {
                Output.Log($"AI 返回内容不是有效 JSON: {ex.Message}", 2, ThisName);
            }
            return list;
        }

        /// <summary>正常的启动/运行日志, 生成的表达式不应命中(误报校验)</summary>
        private static readonly string[] BenignSamples =
        {
            "Starting minecraft server version 1.21.4",
            "Done (23.456s)! For help, type \"help\"",
            "Saving chunks for level 'ServerLevel[world]'/minecraft:overworld",
            "Steve joined the game"
        };

        /// <summary>
        /// 表达式是否可用: 长度合理、能编译为 .NET 正则(带超时)、不是万能通配,
        /// 且必须命中至少一条样本、不能命中正常日志(误报校验)
        /// </summary>
        internal static bool IsUsablePattern(string pattern, IReadOnlyList<string>? samples, out string reason)
        {
            reason = "";
            string p = (pattern ?? "").Trim();
            if (p.Length < 4) { reason = "过短"; return false; }
            if (p.Length > 300) { reason = "过长"; return false; }
            if (p == "." || p == ".*" || p == "^.*$" || p == "(?s).*") { reason = "过于宽泛"; return false; }

            Regex regex;
            try
            {
                // 超时防止灾难性回溯的生成结果拖死分析线程
                regex = new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500));
            }
            catch (Exception ex) { reason = "正则无效: " + ex.Message; return false; }

            try
            {
                // 误报校验: 命中常见正常日志说明规则过宽
                foreach (var benign in BenignSamples)
                {
                    if (regex.IsMatch(benign))
                    {
                        reason = "会误报正常日志";
                        return false;
                    }
                }

                // 命中校验: 必须能匹配至少一条作为依据的错误样本
                if (samples != null && samples.Count > 0)
                {
                    bool hit = false;
                    foreach (var sample in samples)
                    {
                        if (regex.IsMatch(sample)) { hit = true; break; }
                    }
                    if (!hit)
                    {
                        reason = "未命中任何样本";
                        return false;
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                reason = "样本匹配超时(疑似灾难性回溯)";
                return false;
            }

            return true;
        }
    }
}
