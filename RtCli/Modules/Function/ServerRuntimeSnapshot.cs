using System;

namespace RtCli.Modules.Function
{
    /// <summary>
    /// 服务端运行态快照(进程内单例)。
    ///
    /// 原先 _lastPlayerOnlineTime / _lastCrashTime 散落在 AiAutoRunner 的私有字段里,
    /// 只有"恰好被某个启用任务的触发条件或输入源用到"时才会更新, 于是
    /// idle_minutes 算出来可能变成"开机时长"而不是"空服时长"。
    /// 收拢到这里后, 更新点集中、语义明确, 也便于被工具与面板复用。
    /// </summary>
    internal static class ServerRuntimeSnapshot
    {
        private static readonly object _lock = new();
        private static DateTime _lastPlayerOnlineTime = DateTime.Now;
        private static DateTime? _lastCrashTime;

        /// <summary>最近一次检测到有玩家在线的时间</summary>
        public static DateTime LastPlayerOnlineTime
        {
            get { lock (_lock) return _lastPlayerOnlineTime; }
            set { lock (_lock) _lastPlayerOnlineTime = value; }
        }

        /// <summary>最近一次检测到崩溃的时间(无记录为 null)</summary>
        public static DateTime? LastCrashTime
        {
            get { lock (_lock) return _lastCrashTime; }
            set { lock (_lock) _lastCrashTime = value; }
        }

        /// <summary>距今空服时长(分钟)</summary>
        public static double IdleMinutes
        {
            get { lock (_lock) return (DateTime.Now - _lastPlayerOnlineTime).TotalMinutes; }
        }

        /// <summary>测试/停机时重置</summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _lastPlayerOnlineTime = DateTime.Now;
                _lastCrashTime = null;
            }
        }
    }
}
