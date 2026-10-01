using System;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RtCli.Modules.Unit;

namespace RtCli.Modules.Function
{
    /// <summary>
    /// AI 工具调用审计。
    /// 每次工具调用追加一行 JSONL 到 Content/Data/ai_tool_audit.log,
    /// 便于事后复盘"AI 在什么时候、因哪个任务、改了哪个文件 / 重启了几次"。
    /// 审计失败绝不影响工具本身执行。
    /// </summary>
    internal static class AiToolAudit
    {
        private const string FileName = "ai_tool_audit.log";
        private const long MaxBytes = 5 * 1024 * 1024;

        private static readonly object _lock = new();

        /// <summary>当前任务标识(按异步流转传递), 由 ExecuteTaskAsync 设置</summary>
        private static readonly AsyncLocal<string?> _taskKey = new();

        public static string? CurrentTaskKey
        {
            get => _taskKey.Value;
            set => _taskKey.Value = value;
        }

        private static string Dir => string.IsNullOrWhiteSpace(Config.DataPath) ? "" : Config.DataPath!;

        /// <summary>审计文件路径(数据目录未就绪时返回空串)</summary>
        public static string FilePath
        {
            get
            {
                string dir = Dir;
                return string.IsNullOrEmpty(dir) ? "" : Path.Combine(dir, FileName);
            }
        }

        /// <summary>记录一次工具调用</summary>
        public static void Record(string tool, string? args, string? result, bool ok)
        {
            try
            {
                string dir = Dir;
                if (string.IsNullOrEmpty(dir)) return;

                var entry = new JObject
                {
                    ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["task"] = CurrentTaskKey ?? "-",
                    ["tool"] = tool,
                    ["args"] = Clip(args, 2000),
                    ["result"] = Clip(result, 2000),
                    ["ok"] = ok
                };

                lock (_lock)
                {
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, FileName);
                    Rotate(path);
                    File.AppendAllText(path, entry.ToString(Formatting.None) + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Output.Debug($"工具审计写入失败: {ex.Message}", "AiToolAudit");
            }
        }

        /// <summary>审计文件已记录的总条数(文件不存在返回 0)</summary>
        public static int Count()
        {
            try
            {
                string path = FilePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;
                lock (_lock)
                {
                    int n = 0;
                    using var reader = new StreamReader(path, Encoding.UTF8);
                    while (reader.ReadLine() != null) n++;
                    return n;
                }
            }
            catch (Exception ex)
            {
                Output.Debug($"统计审计条数失败: {ex.Message}", "AiToolAudit");
                return 0;
            }
        }

        /// <summary>读取最近 n 条审计记录(面板/命令查看用), 文件不存在返回空</summary>
        public static System.Collections.Generic.List<string> ReadRecent(int count = 50)
        {
            var lines = new System.Collections.Generic.List<string>();
            try
            {
                string path = FilePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return lines;

                lock (_lock)
                {
                    var all = File.ReadAllLines(path, Encoding.UTF8);
                    int start = Math.Max(0, all.Length - Math.Max(1, count));
                    for (int i = start; i < all.Length; i++)
                        if (!string.IsNullOrWhiteSpace(all[i])) lines.Add(all[i]);
                }
            }
            catch (Exception ex)
            {
                Output.Debug($"读取工具审计失败: {ex.Message}", "AiToolAudit");
            }
            return lines;
        }

        /// <summary>超过阈值时滚动为 .1(只保留一代, 避免无限增长)</summary>
        private static void Rotate(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < MaxBytes) return;
                string bak = path + ".1";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(path, bak);
            }
            catch (Exception ex)
            {
                Output.Debug($"审计日志滚动失败: {ex.Message}", "AiToolAudit");
            }
        }

        private static string Clip(string? s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // 审计落盘前脱敏, 避免密钥/口令/连接串以明文留在磁盘
            s = Output.RedactSensitive(s);
            s = s.Replace("\r", " ").Replace("\n", "\\n");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
