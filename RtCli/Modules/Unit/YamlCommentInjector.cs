using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RtCli.Modules.Unit
{
    /// <summary>
    /// YAML 配置注释注入器: 把"键 → 注释"的数据表逐行注入序列化后的 YAML,
    /// 替代各配置生成方法里的巨型 if(StartsWith) 注入链(加键只需登记数据, 不再写分支)。
    /// 匹配规则: 行首以 "key:" 开头即命中(带冒号后缀天然区分 auto_restart 与 auto_restart_max_retries),
    /// 同一键每处出现都注入(与原 if 链行为一致, 如 server_list 内每个服务端条目)。
    /// 支持多区块: 同名键在不同顶层段使用不同注释(如 ai_settings 的 Console_Error 与 ServerAutoAi 段),
    /// 区块自 StartKey 顶层行开始, 遇其他顶层键回到默认区块。
    /// </summary>
    internal static class YamlCommentInjector
    {
        /// <summary>键注释: Key 为 YAML 键名(不含冒号), Text 为注释行(含 # 前缀, 可多行用\n分隔)</summary>
        public sealed record Comment(string Key, string Text, bool BlankLineBefore = true);

        /// <summary>注释区块: StartKey 为 null 表示文件起始的默认区块</summary>
        public sealed class Section
        {
            public string? StartKey;
            public List<Comment> Comments = new();

            public static Section Default(params Comment[] comments)
                => new() { StartKey = null, Comments = comments.ToList() };

            public static Section From(string startKey, params Comment[] comments)
                => new() { StartKey = startKey, Comments = comments.ToList() };
        }

        public static string Inject(string yaml, params Section[] sections)
        {
            var defaultMap = BuildMap(sections.FirstOrDefault(s => string.IsNullOrEmpty(s.StartKey)));
            var namedSections = sections.Where(s => !string.IsNullOrEmpty(s.StartKey)).ToList();

            var sb = new StringBuilder();
            Dictionary<string, Comment>? current = defaultMap;

            foreach (var line in yaml.Split('\n'))
            {
                var trimmed = line.Trim();

                // 顶层键(列首非空白且非列表项/注释): 切换或复位区块
                if (line.Length > 0 && !char.IsWhiteSpace(line[0])
                    && !trimmed.StartsWith("-") && !trimmed.StartsWith("#"))
                {
                    current = defaultMap;
                    foreach (var s in namedSections)
                    {
                        if (trimmed.StartsWith(s.StartKey! + ":", StringComparison.Ordinal))
                        {
                            current = BuildMap(s);
                            break;
                        }
                    }
                }

                if (current != null && trimmed.Length > 0 && TryMatch(current, trimmed, out var comment))
                {
                    if (comment.BlankLineBefore) sb.AppendLine();
                    foreach (var commentLine in comment.Text.Split('\n'))
                        sb.AppendLine(commentLine);
                }

                sb.AppendLine(line.TrimEnd('\r'));
            }

            return sb.ToString();
        }

        private static bool TryMatch(Dictionary<string, Comment> map, string trimmedLine, out Comment comment)
        {
            // 提取行首键名(到冒号), 精确查表; 无冒号的行(标量值/续行)不匹配
            int colon = trimmedLine.IndexOf(':');
            if (colon <= 0) { comment = null!; return false; }
            return map.TryGetValue(trimmedLine.Substring(0, colon), out comment!);
        }

        private static Dictionary<string, Comment> BuildMap(Section? section)
        {
            var map = new Dictionary<string, Comment>(StringComparer.Ordinal);
            if (section == null) return map;
            foreach (var c in section.Comments)
                map[c.Key] = c;
            return map;
        }
    }
}
