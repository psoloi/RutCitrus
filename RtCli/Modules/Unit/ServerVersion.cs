using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RtCli.Modules.Unit
{
    /// <summary>
    /// 读取 MC 服务端版本。
    /// 优先取服务端核心 jar 内 version.json 的 id(Paper/Vanilla 等都在包根放了 version.json),
    /// 取不到再退回 logs/latest.log 里的 "Starting minecraft server version X"。
    /// 结果按 (工作目录 + jar 修改时间 + 大小) 缓存, jar 换了会自动重新读取。
    /// </summary>
    public static class ServerVersion
    {
        private const string ThisName = "ServerVersion";

        private static readonly object _lock = new();
        private static readonly Dictionary<string, (string JarPath, DateTime WriteUtc, long Size, string Version)> _jarCache
            = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, (DateTime At, string Version)> _logCache
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>日志回退缓存有效期(秒)</summary>
        private const int LogCacheSeconds = 60;

        /// <summary>
        /// 探测实例的 MC 版本。
        /// </summary>
        /// <returns>(版本号, 来源 jar 路径); 读不到返回 (null, null)</returns>
        public static (string? Version, string? JarPath) Detect(string? workPath)
        {
            if (string.IsNullOrWhiteSpace(workPath) || !Directory.Exists(workPath)) return (null, null);

            string root;
            string[] jars;
            try
            {
                root = Path.GetFullPath(workPath);
                jars = Directory.GetFiles(root, "*.jar", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                Output.Debug($"扫描服务端 jar 失败: {ex.Message}", ThisName);
                return (null, null);
            }

            // 1) 依次尝试根目录下的 jar, 取第一个能解析出 version.json id 的
            foreach (var jar in jars.OrderBy(j => j, StringComparer.OrdinalIgnoreCase))
            {
                FileInfo fi;
                try { fi = new FileInfo(jar); }
                catch { continue; }

                lock (_lock)
                {
                    if (_jarCache.TryGetValue(root, out var hit) &&
                        string.Equals(hit.JarPath, jar, StringComparison.OrdinalIgnoreCase) &&
                        hit.WriteUtc == fi.LastWriteTimeUtc && hit.Size == fi.Length)
                    {
                        return (hit.Version, jar);
                    }
                }

                string? version = ReadVersionJsonId(jar);
                if (!string.IsNullOrWhiteSpace(version))
                {
                    lock (_lock) _jarCache[root] = (jar, fi.LastWriteTimeUtc, fi.Length, version);
                    return (version, jar);
                }
            }

            // 2) 回退: 从最新日志里找启动行
            lock (_lock)
            {
                if (_logCache.TryGetValue(root, out var cached) &&
                    (DateTime.UtcNow - cached.At).TotalSeconds < LogCacheSeconds)
                {
                    return (cached.Version, null);
                }
            }

            string? fromLog = ReadFromLog(root);
            if (!string.IsNullOrWhiteSpace(fromLog))
            {
                lock (_lock) _logCache[root] = (DateTime.UtcNow, fromLog);
                return (fromLog, null);
            }

            return (null, null);
        }

        /// <summary>清空缓存(切换实例/重载配置时调用)</summary>
        public static void ClearCache()
        {
            lock (_lock)
            {
                _jarCache.Clear();
                _logCache.Clear();
            }
        }

        /// <summary>从 jar 根目录的 version.json 取 id</summary>
        private static string? ReadVersionJsonId(string jarPath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(jarPath);
                var entry = zip.Entries.FirstOrDefault(e =>
                {
                    string n = e.FullName.Replace('\\', '/').TrimStart('/');
                    return string.Equals(n, "version.json", StringComparison.OrdinalIgnoreCase);
                });
                if (entry == null || entry.Length > 256 * 1024) return null;

                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                string text = reader.ReadToEnd();
                string? id = JObject.Parse(text)["id"]?.ToString();
                if (string.IsNullOrWhiteSpace(id))
                    id = JObject.Parse(text)["name"]?.ToString();
                return string.IsNullOrWhiteSpace(id) ? null : id!.Trim();
            }
            catch (Exception ex)
            {
                Output.Debug($"读取 {Path.GetFileName(jarPath)} 的 version.json 失败: {ex.Message}", ThisName);
                return null;
            }
        }

        private static readonly Regex StartLineRegex = new(
            @"Starting minecraft server version\s+([0-9][\w.\-+]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>从 logs/latest.log 的启动行推断版本</summary>
        private static string? ReadFromLog(string root)
        {
            try
            {
                string log = Path.Combine(root, "logs", "latest.log");
                if (!File.Exists(log)) return null;

                // 只读前 200 行: 启动行一定在最前面
                using var reader = new StreamReader(log, Encoding.UTF8);
                for (int i = 0; i < 200; i++)
                {
                    string? line = reader.ReadLine();
                    if (line == null) break;
                    var m = StartLineRegex.Match(line);
                    if (m.Success) return m.Groups[1].Value;
                }
            }
            catch (Exception ex)
            {
                Output.Debug($"从日志推断服务端版本失败: {ex.Message}", ThisName);
            }
            return null;
        }
    }
}
