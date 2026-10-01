using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RtCli.Modules.Unit
{
    /// <summary>
    /// 插件更新: 用 plugin.yml 里的插件名去 Modrinth 搜索, 取最新版本与本地版本比较,
    /// 下载新 jar 并把旧插件改名为 .disjar(禁用)。
    /// </summary>
    public static partial class Backend
    {
        /// <summary>更新结果(检查与执行共用)</summary>
        public sealed record PluginUpdateResult(
            bool Success,
            string Message,
            bool HasUpdate,
            string LocalVersion,
            string RemoteVersion,
            string ProjectTitle,
            string DownloadUrl,
            string NewFileName);

        private const string ModrinthApi = "https://api.modrinth.com/v2";

        private static readonly HttpClient _modrinthHttp = CreateModrinthClient();

        private static HttpClient CreateModrinthClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            // Modrinth 要求带上可识别的 User-Agent
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", $"RutCitrus/{Program.RtCliVersion} (github.com/psoloi/RutCitrus)");
            return client;
        }

        private static PluginUpdateResult Fail(string message)
            => new(false, message, false, "", "", "", "", "");

        private sealed record ModrinthHit(string Slug, string Title);
        private sealed record ModrinthVersion(string Number, string Url, string FileName);

        /// <summary>
        /// 检查并(可选)执行插件更新。
        /// </summary>
        /// <param name="serverKey">实例标识</param>
        /// <param name="fileName">plugins 目录下的 jar 文件名</param>
        /// <param name="apply">false=只检查, true=下载并替换</param>
        public static async Task<PluginUpdateResult> UpdatePluginAsync(string serverKey, string fileName, bool apply)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileName))
                    return Fail("未指定插件文件");

                var workPath = ResolveWorkPath(serverKey);
                if (workPath == null) return Fail("服务端工作目录未配置或不存在");

                string pluginsDir = Path.Combine(workPath, "plugins");
                string fullPath = Path.Combine(pluginsDir, fileName);
                if (!File.Exists(fullPath)) return Fail($"插件文件不存在: {fileName}");
                if (!IsWithinRoot(pluginsDir, fullPath)) return Fail("非法路径");

                // 名称/版本优先取 plugin.yml, 取不到再退回文件名
                var meta = ReadPluginJarMeta(fullPath);
                string searchName = (meta?.Name ?? "").Trim();
                if (searchName.Length == 0) searchName = StripJarExt(fileName);
                string localVersion = (meta?.Version ?? "").Trim();

                var hit = await FindModrinthProjectAsync(searchName);
                if (hit == null)
                    return Fail($"Modrinth 上未找到与「{searchName}」匹配的插件");

                // 取实例的 MC 版本(服务端 jar 内 version.json 的 id), 用它筛掉不兼容的版本
                var (mcVersion, _) = ServerVersion.Detect(workPath);

                var pick = await FindLatestVersionAsync(hit.Slug, mcVersion);
                if (pick.Version == null)
                    return Fail(pick.NoMatchReason ?? $"Modrinth 项目 {hit.Title} 没有可用版本");
                var version = pick.Version;

                bool hasUpdate = localVersion.Length == 0 || PluginVersion.IsNewer(version.Number, localVersion);
                string summary = $"{hit.Title} (MC {mcVersion ?? "未知"}): 本地 {ShowVersion(localVersion)} → 远端 {version.Number}";

                if (!apply)
                {
                    return new PluginUpdateResult(true,
                        (hasUpdate ? "发现新版本, " : "已是最新版本, ") + summary,
                        hasUpdate, localVersion, version.Number, hit.Title, version.Url, "");
                }

                if (!hasUpdate)
                {
                    return new PluginUpdateResult(true, "已是最新版本, " + summary,
                        false, localVersion, version.Number, hit.Title, version.Url, "");
                }

                byte[] bytes;
                try { bytes = await _modrinthHttp.GetByteArrayAsync(version.Url); }
                catch (Exception ex) { return Fail($"下载失败: {ex.Message}"); }

                if (bytes.Length < 4 || bytes[0] != 0x50 || bytes[1] != 0x4B)
                    return Fail("下载内容不是有效的 jar 文件(缺少 ZIP 头)");

                bool wasDisabled = fileName.EndsWith(".disjar", StringComparison.OrdinalIgnoreCase);
                string oldBase = StripJarExt(fileName);
                string backupNote = "";

                // 旧插件改为禁用后缀; 已经是 .disjar 的保持不动(本来就是禁用态)
                if (!wasDisabled)
                {
                    string disPath = UniquePath(pluginsDir, oldBase, ".disjar");
                    try
                    {
                        File.Copy(fullPath, disPath, overwrite: false);
                        File.Delete(fullPath);
                        backupNote = $", 旧版本已禁用为 {Path.GetFileName(disPath)}";
                    }
                    catch (Exception ex)
                    {
                        // 服务端运行时 jar 会被 Java 占用, 这时不要动原文件
                        return Fail($"旧插件改名失败(服务端可能在运行, 请先停止后再更新): {ex.Message}");
                    }
                }

                string newBase = string.IsNullOrWhiteSpace(version.FileName)
                    ? oldBase + "-" + version.Number
                    : StripJarExt(version.FileName);
                string ext = wasDisabled ? ".disjar" : ".jar";
                string newPath = Path.Combine(pluginsDir, newBase + ext);
                if (File.Exists(newPath)) newPath = UniquePath(pluginsDir, newBase, ext);

                try { File.WriteAllBytes(newPath, bytes); }
                catch (Exception ex) { return Fail($"写入新插件失败: {ex.Message}"); }

                string message = $"已更新 {hit.Title}: {ShowVersion(localVersion)} → {version.Number} " +
                                 $"(新文件 {Path.GetFileName(newPath)}{backupNote})";
                Output.Log(message, 1, "Backend");
                return new PluginUpdateResult(true, message, true, localVersion, version.Number,
                    hit.Title, version.Url, Path.GetFileName(newPath));
            }
            catch (Exception ex)
            {
                return Fail($"更新失败: {ex.Message}");
            }
        }

        // ==================== Modrinth 查询 ====================

        private static async Task<ModrinthHit?> FindModrinthProjectAsync(string name)
        {
            string query = Uri.EscapeDataString(name);
            // facets = [["project_type:plugin"]] (已编码)
            string url = $"{ModrinthApi}/search?query={query}&limit=10&facets=%5B%5B%22project_type%3Aplugin%22%5D%5D";

            string json = await _modrinthHttp.GetStringAsync(url);
            if (JObject.Parse(json)["hits"] is not JArray hits || hits.Count == 0) return null;

            string want = NormalizeName(name);
            ModrinthHit? first = null;
            foreach (var h in hits)
            {
                string slug = h["slug"]?.ToString() ?? "";
                string title = h["title"]?.ToString() ?? "";
                if (slug.Length == 0) continue;

                var hit = new ModrinthHit(slug, title);
                first ??= hit;

                // 名称完全匹配优先(归一化后比较, 忽略大小写/空格/符号)
                if (NormalizeName(slug) == want || NormalizeName(title) == want) return hit;
            }
            return first;
        }

        private sealed record VersionPick(ModrinthVersion? Version, string? NoMatchReason);

        private static async Task<VersionPick> FindLatestVersionAsync(string slug, string? mcVersion)
        {
            string url = $"{ModrinthApi}/project/{Uri.EscapeDataString(slug)}/version";
            string json = await _modrinthHttp.GetStringAsync(url);
            if (JArray.Parse(json) is not JArray versions || versions.Count == 0)
                return new VersionPick(null, null);

            // 先按 MC 版本过滤; 实例版本未知时不过滤
            var candidates = versions.Cast<JToken>().ToList();
            if (!string.IsNullOrWhiteSpace(mcVersion))
            {
                var matched = candidates.Where(v => MatchesMcVersion(v, mcVersion)).ToList();
                if (matched.Count == 0)
                {
                    var supported = candidates
                        .SelectMany(v => v["game_versions"] as JArray ?? new JArray())
                        .Select(g => g.ToString())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    string list = supported.Count == 0
                        ? "未知"
                        : string.Join(", ", supported.Take(12)) + (supported.Count > 12 ? " …" : "");
                    return new VersionPick(null,
                        $"该插件没有支持 Minecraft {mcVersion} 的版本(它支持的版本: {list})");
                }
                candidates = matched;
            }

            // 列表按发布时间倒序: 优先取 release, 没有则取第一条
            JToken? chosen = candidates.FirstOrDefault(v =>
                string.Equals(v["version_type"]?.ToString(), "release", StringComparison.OrdinalIgnoreCase));
            chosen ??= candidates[0];

            string number = chosen["version_number"]?.ToString() ?? "";
            if (number.Length == 0) return new VersionPick(null, null);

            var files = chosen["files"] as JArray;
            JToken? file = files?.FirstOrDefault(f => f["primary"]?.ToObject<bool>() == true) ?? files?.FirstOrDefault();
            string downloadUrl = file?["url"]?.ToString() ?? "";
            if (downloadUrl.Length == 0) return new VersionPick(null, null);

            return new VersionPick(
                new ModrinthVersion(number, downloadUrl, file?["filename"]?.ToString() ?? ""), null);
        }

        /// <summary>版本的 game_versions 是否覆盖该 MC 版本(1.21 与 1.21.4 视为互相兼容)。</summary>
        private static bool MatchesMcVersion(JToken version, string mcVersion)
        {
            if (version["game_versions"] is not JArray arr) return false;
            foreach (var g in arr)
            {
                string s = g.ToString();
                if (s.Length == 0) continue;
                if (string.Equals(s, mcVersion, StringComparison.OrdinalIgnoreCase)) return true;
                if (s.StartsWith(mcVersion + ".", StringComparison.OrdinalIgnoreCase)) return true;
                if (mcVersion.StartsWith(s + ".", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ==================== 小工具 ====================

        private static string NormalizeName(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>去掉 .jar / .disjar 后缀</summary>
        private static string StripJarExt(string? fileName)
        {
            string s = fileName ?? "";
            if (s.EndsWith(".disjar", StringComparison.OrdinalIgnoreCase)) return s.Substring(0, s.Length - 7);
            if (s.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) return s.Substring(0, s.Length - 4);
            return s;
        }

        private static string ShowVersion(string v) => string.IsNullOrWhiteSpace(v) ? "(未知)" : v;

        /// <summary>生成不与现有文件冲突的路径: base.ext → base.2.ext → base.3.ext ...</summary>
        private static string UniquePath(string dir, string baseName, string ext)
        {
            string candidate = Path.Combine(dir, baseName + ext);
            int n = 2;
            while (File.Exists(candidate))
                candidate = Path.Combine(dir, $"{baseName}.{n++}{ext}");
            return candidate;
        }
    }
}
