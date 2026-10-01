using System.Text.RegularExpressions;

namespace RtCli.Modules.Function
{
    /// <summary>
    /// MC 服务端控制台日志知识的单一来源: 崩溃信号判定 / TPS 解析 / 玩家数与进出服识别。
    /// Analyzer(Intelligence/Scheduler) 之前各自复制一份规则, 修改一处其余不同步造成行为分叉,
    /// 所有解析规则集中在此维护, 其他模块只引用不复制。
    /// </summary>
    internal static class MCConsole
    {
        /// <summary>
        /// 精准崩溃标志(单行命中即判定, 避免误报)。
        /// 覆盖: 崩溃报告标题/保存提示、FATAL 级别、ERROR 级别含 Crash 字样。
        /// 注意: 正常停服序列("Stopping server"/"Shutting down the server")不属于崩溃,
        /// 由 <see cref="IsNormalShutdownLine"/> 单独识别, 调用方应先排除计划内关停。
        /// </summary>
        public static bool IsCrashLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            return line.Contains("---- Minecraft Crash Report ----", StringComparison.OrdinalIgnoreCase)
                || line.Contains("This crash report has been saved to", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Server thread/FATAL", StringComparison.OrdinalIgnoreCase)
                || (line.Contains("Server thread/ERROR", StringComparison.OrdinalIgnoreCase)
                    && line.Contains("Crash", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 正常停服序列标志。用于把"计划内关停"与真实崩溃区分开:
        /// 出现这些行后, 后续的保存/收尾日志(即使含 FATAL/Exception)都不再判定为崩溃。
        /// </summary>
        public static bool IsNormalShutdownLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            return line.Contains("Stopping server", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Stopping the server", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Shutting down the server", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Shutting down server", StringComparison.OrdinalIgnoreCase);
        }

        // ===== TPS 解析 =====
        // Spark 插件格式: "TPS from last 1m, 5m, 15m: 20.0*, 20.0, 19.9" (* 表示采集期间有.tick丢失)
        public static readonly Regex SparkTpsRegex = new(
            @"TPS\s+from\s+last\s+\d+m\s*,\s*\d+m\s*,\s*\d+m\s*:\s*([\d.]+)\s*\*?\s*,\s*([\d.]+)\s*\*?\s*,\s*([\d.]+)\s*\*?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 旧式单值格式: "TPS: 20.0" 或 "TPS from last 1m: 20.0"
        public static readonly Regex SingleTpsRegex = new(
            @"TPS\s*(?:from\s+last\s+\d+m)?\s*:\s*([\d.]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // JSON 格式: "tps": 19.87 (面板/结构化输出)
        public static readonly Regex JsonTpsRegex = new(
            @"""tps""\s*:\s*(\d+(?:\.\d+)?)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>尝试从日志行解析 Spark 三窗口 TPS(1m/5m/15m)</summary>
        public static bool TryParseSparkTps(string line, out double t1m, out double t5m, out double t15m)
        {
            t1m = t5m = t15m = -1;
            if (string.IsNullOrEmpty(line)) return false;
            var m = SparkTpsRegex.Match(line);
            if (!m.Success) return false;
            bool ok = double.TryParse(m.Groups[1].Value, out t1m)
                   && double.TryParse(m.Groups[2].Value, out t5m)
                   && double.TryParse(m.Groups[3].Value, out t15m);
            if (!ok) { t1m = t5m = t15m = -1; }
            return ok;
        }

        /// <summary>尝试从日志行解析单值 TPS(旧式命令返回)</summary>
        public static bool TryParseSingleTps(string line, out double tps)
        {
            tps = -1;
            if (string.IsNullOrEmpty(line)) return false;
            var m = SingleTpsRegex.Match(line);
            if (!m.Success) return false;
            if (!double.TryParse(m.Groups[1].Value, out tps)) tps = -1;
            return tps >= 0;
        }

        /// <summary>尝试从日志行解析 JSON 字段 TPS</summary>
        public static bool TryParseJsonTps(string line, out double tps)
        {
            tps = -1;
            if (string.IsNullOrEmpty(line)) return false;
            var m = JsonTpsRegex.Match(line);
            if (!m.Success) return false;
            if (!double.TryParse(m.Groups[1].Value, out tps)) tps = -1;
            return tps >= 0;
        }

        // ===== 玩家数 / 进出服识别 =====
        // list 命令返回: "There are 2 of a max of 20 players online: ..."
        // 兼容 "There are N ..." 与 "There're N ..." 两种写法(原先 "'re" 分支被强制前置的 \s+ 挡住, 永远命中不了)
        public static readonly Regex ListPlayersRegex = new(
            @"There(?:'re|\s+are)\s+(\d+)\s+of\s+a\s+max\s+of\s+(\d+)\s+players?\s+online",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>解析 list 命令返回的在线玩家数与上限</summary>
        public static bool TryParsePlayerCount(string line, out int count, out int max)
        {
            count = max = -1;
            if (string.IsNullOrEmpty(line)) return false;
            var m = ListPlayersRegex.Match(line);
            if (!m.Success) return false;
            int c, mx;
            if (!int.TryParse(m.Groups[1].Value, out c) || !int.TryParse(m.Groups[2].Value, out mx)) return false;
            count = c; max = mx;
            return true;
        }

        /// <summary>玩家加入行("joined the game")。vanilla 一次加入还会打 "logged in with entity id",
        /// 统计人数时只按本侧事件计数, 避免同一玩家被计两次。</summary>
        public static bool IsPlayerJoinLine(string line)
            => line != null && line.Contains("joined the game", StringComparison.OrdinalIgnoreCase);

        /// <summary>玩家离开行("left the game")。同理只按本侧计数。</summary>
        public static bool IsPlayerLeftLine(string line)
            => line != null && line.Contains("left the game", StringComparison.OrdinalIgnoreCase);
    }
}
