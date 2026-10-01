using RtCli.Modules.Unit;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace RtCli.Modules.Function
{
    /// <summary>
    /// 每实例独立的运行时状态: 进程/stdin/RCON/管理协议/日志缓冲/查询缓存/崩溃检测。
    /// Analyzer 作为静态门面按实例标识路由到本对象, 使同一 RtCli 进程可并发运行多个实例。
    /// </summary>
    internal sealed class InstanceRuntime
    {
        public InstanceRuntime(string id)
        {
            Id = id;
        }

        /// <summary>实例标识(server_list 的 key)</summary>
        public string Id { get; }

        /// <summary>该实例的配置快照(启动/连接时刷新)</summary>
        public ServerEntry Entry { get; set; } = new ServerEntry();

        /// <summary>归一化后的模式: RUN / RR / RM / RCON</summary>
        public string Mode { get; set; } = "RM";

        public Process? Process { get; set; }
        public StreamWriter? Stdin { get; set; }
        public RconClient? Rcon { get; set; }
        public MCManagementConnection? Management { get; set; }
        public CancellationTokenSource? Cts { get; set; }

        /// <summary>进程运行中，或远程 RCON 已连接</summary>
        public volatile bool Running;

        public int AttachedProcessId;
        public string AttachedWindowTitle = "";
        public string ConnectedServerName = "";

        public DateTime StartTime = DateTime.MinValue;
        public int RestartAttemptCount;
        public volatile bool UserInitiatedStop;
        public int CrashNotified;
        public volatile bool ShutdownInProgress;
        public long LogActivityCounter;

        /// <summary>同一实例的命令发送串行锁(不同实例互不阻塞)</summary>
        public readonly object CommandLock = new object();

        public readonly object LogBufferLock = new object();
        public readonly LogRingBuffer LogBuffer = new LogRingBuffer(5000);

        /// <summary>日志流缓存键(= Id)</summary>
        public string StreamKey = "";

        // 崩溃信号累积(供 AI 相似度判重)
        public readonly object CrashSignalLock = new object();
        public readonly List<string> RecentErrorLines = new List<string>();

        // 客户端指引状态(每实例独立)
        public CancellationTokenSource? ClientGuideCts;
        public volatile bool ClientGuideActive;
        public readonly List<ClientGuideMatch> LastClientGuideMatches = new List<ClientGuideMatch>();
        public readonly object ClientGuideLock = new object();

        // TPS / 在线人数 / CPU 查询缓存(每实例独立节流)
        public DateTime LastTpsQueryTime = DateTime.MinValue;
        public (double T1m, double T5m, double T15m) CachedTps = (-1, -1, -1);
        public DateTime LastListQueryTime = DateTime.MinValue;
        public (int Count, int Max) CachedPlayers = (-1, -1);
        public DateTime LastMcCpuSampleTime = DateTime.MinValue;
        public TimeSpan LastMcCpuTime = TimeSpan.Zero;
        public double CachedMcCpuUsage = -1;

        /// <summary>该实例最新日志的绝对序号</summary>
        public long LogCursor
        {
            get { lock (LogBufferLock) { return LogBuffer.LatestSequence; } }
        }

        public List<string> ReadLogLinesSince(long cursor, out long nextCursor)
        {
            lock (LogBufferLock)
            {
                return LogBuffer.GetSince(cursor, out nextCursor);
            }
        }
    }

    /// <summary>
    /// 实例上下文(AsyncLocal): 在日志处理、命令执行、AI/调度器任务中标记当前归属实例。
    /// 供 Output 日志广播、玩家事件记录等无实例参数的调用路径读取。
    /// </summary>
    internal static class InstanceContext
    {
        private static readonly AsyncLocal<string?> _current = new();

        public static string? Current => _current.Value;

        public static IDisposable Push(string? instanceId)
        {
            string? previous = _current.Value;
            _current.Value = instanceId;
            return new Scope(previous);
        }

        private sealed class Scope : IDisposable
        {
            private readonly string? _previous;
            private bool _disposed;

            public Scope(string? previous) => _previous = previous;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _current.Value = _previous;
            }
        }
    }
}
