using System.Text.Json;
using RtCli.Grpc;

namespace RtPanel.Services
{
    /// <summary>
    /// 面板服务端持续采集服务: 后台定时拉取主机 CPU/内存与各服务端 TPS 状态,
    /// 保存为历史采样点(内存缓冲 + 磁盘持久化), 与浏览器/页面状态无关。
    /// 首页通过 /api/system/history 读取, 取代原先浏览器 localStorage 采集方案。
    /// </summary>
    public class StatsHistoryService : BackgroundService
    {
        private readonly RtCliClientService _client;

        // 历史采样点缓冲(按时间升序)
        private readonly List<StatsPoint> _points = new();
        private readonly object _lock = new();

        // 当前历史保留小时数(来自 panel_data.json, 默认 2 小时)
        private int _retentionHours = 2;

        // 持久化路径: <BaseDirectory>\data\stats_history.json
        private readonly string _dataDir;
        private readonly string _storePath;

        // 每 60 秒同步一次保留时长与写盘一次
        private const long RetentionSyncIntervalMs = 60000;
        private const long PersistIntervalMs = 60000;

        // 磁盘与接口均使用 camelCase, 与首页前端字段保持一致
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public StatsHistoryService(RtCliClientService client)
        {
            _client = client;
            _dataDir = Path.Combine(AppContext.BaseDirectory, "data");
            _storePath = Path.Combine(_dataDir, "stats_history.json");
        }

        /// <summary>当前历史保留小时数(默认 2)</summary>
        public int RetentionHours
        {
            get { lock (_lock) return _retentionHours; }
        }

        /// <summary>最近一个采样点(无数据返回 null)</summary>
        public object? Latest
        {
            get { lock (_lock) return _points.Count > 0 ? _points[^1] : null; }
        }

        /// <summary>返回全部历史采样点(按时间升序的快照)</summary>
        public IReadOnlyList<object> Snapshot()
        {
            lock (_lock)
            {
                var list = new List<object>(_points.Count);
                foreach (var p in _points) list.Add(p);
                return list;
            }
        }

        /// <summary>
        /// 后台循环: 每轮按保留时长决定的间隔采集一次; 连接断开或采集失败时静默跳过。
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 启动时先加载上次持久化的历史并裁剪
            LoadFromDisk();
            await RefreshRetentionAsync();

            long lastRetentionMs = 0;
            long lastPersistMs = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // 每 60 秒同步一次保留时长
                    if (sw.ElapsedMilliseconds - lastRetentionMs >= RetentionSyncIntervalMs)
                    {
                        lastRetentionMs = sw.ElapsedMilliseconds;
                        await RefreshRetentionAsync();
                    }

                    if (_client.IsConnected)
                    {
                        var stats = await _client.GetSystemStatsAsync();
                        if (stats != null && stats.Success)
                        {
                            AppendPoint(stats);

                            // 每 60 秒落盘一次
                            if (sw.ElapsedMilliseconds - lastPersistMs >= PersistIntervalMs)
                            {
                                lastPersistMs = sw.ElapsedMilliseconds;
                                PersistToDisk();
                            }
                        }
                    }
                }
                catch
                {
                    // 超时/异常必须吞掉, 后台线程不能挂
                }

                // 按当前保留时长决定下次采集间隔
                var interval = IntervalMs(RetentionHours);
                try
                {
                    await Task.Delay(interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            // 退出前落地一次
            PersistToDisk();
        }

        /// <summary>按保留小时数映射采集间隔(与前端原逻辑一致)</summary>
        private static int IntervalMs(int retentionHours)
        {
            if (retentionHours <= 2) return 5000;    // 5s
            if (retentionHours <= 6) return 15000;   // 15s
            if (retentionHours <= 12) return 30000;  // 30s
            if (retentionHours <= 24) return 60000;  // 1min
            return 120000;                            // 2min (48-72h)
        }

        /// <summary>从 panel_data.json 读取历史保留小时数(解析失败使用默认 2)</summary>
        private async Task RefreshRetentionAsync()
        {
            // 未连接时保持上次的保留时长, 避免断线期间被误裁剪为默认值
            if (!_client.IsConnected) return;

            int hours = 2;
            try
            {
                var resp = await _client.GetPanelDataAsync();
                if (resp != null && !string.IsNullOrWhiteSpace(resp.JsonData))
                {
                    using var doc = JsonDocument.Parse(resp.JsonData);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("historyRetention", out var hr) &&
                        hr.ValueKind == JsonValueKind.Number)
                    {
                        hours = hr.GetInt32();
                    }
                }
            }
            catch
            {
                hours = 2; // 解析失败使用默认 2 小时
            }
            if (hours <= 0) hours = 2;

            lock (_lock)
            {
                _retentionHours = hours;
                TrimLocked();
            }
        }

        /// <summary>把一次系统状态转换为历史采样点并追加到缓冲</summary>
        private void AppendPoint(SystemStatsResponse stats)
        {
            var point = new StatsPoint
            {
                T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                HostCpu = stats.CpuUsage,
                HostMemUsed = stats.MemoryUsedMb,
                HostMemTotal = stats.MemoryTotalMb
            };

            foreach (var s in stats.Servers)
            {
                var running = s.IsRunning;
                point.Servers.Add(new ServerStatPoint
                {
                    Key = s.ServerKey ?? "",
                    Name = s.ServerName ?? "",
                    IsRunning = running,
                    Players = running ? s.PlayerCount : -1,
                    Tps1m = running ? s.Tps1M : -1,
                    McCpu = running ? s.McCpuUsage : -1,
                    McMem = running ? s.McMemoryMb : -1
                });
            }

            lock (_lock)
            {
                _points.Add(point);
                TrimLocked();
            }
        }

        /// <summary>按保留时长裁剪内存缓冲(调用方需持有 _lock)</summary>
        private void TrimLocked()
        {
            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)_retentionHours * 3600 * 1000;
            _points.RemoveAll(p => p.T < cutoff);
        }

        /// <summary>加载磁盘历史并裁剪(启动时调用)</summary>
        private void LoadFromDisk()
        {
            try
            {
                if (!File.Exists(_storePath)) return;
                var json = File.ReadAllText(_storePath);
                var loaded = JsonSerializer.Deserialize<List<StatsPoint>>(json, JsonOpts);
                if (loaded == null) return;

                lock (_lock)
                {
                    _points.Clear();
                    _points.AddRange(loaded);
                    TrimLocked();
                }
            }
            catch
            {
                // 历史文件损坏/格式不兼容时忽略, 从空缓冲开始
            }
        }

        /// <summary>把内存缓冲写入磁盘(失败忽略)</summary>
        private void PersistToDisk()
        {
            List<StatsPoint> snapshot;
            lock (_lock)
            {
                snapshot = new List<StatsPoint>(_points);
            }

            try
            {
                Directory.CreateDirectory(_dataDir);
                var json = JsonSerializer.Serialize(snapshot, JsonOpts);
                File.WriteAllText(_storePath, json);
            }
            catch
            {
                // 写盘失败不影响后台采集
            }
        }
    }

    /// <summary>单个历史采样点(字段与首页前端对齐, 序列化为 camelCase)</summary>
    public class StatsPoint
    {
        /// <summary>采样时间(Unix 毫秒时间戳)</summary>
        public long T { get; set; }
        public double HostCpu { get; set; }
        public double HostMemUsed { get; set; }
        public double HostMemTotal { get; set; }
        public List<ServerStatPoint> Servers { get; set; } = new();
    }

    /// <summary>采样点内单个服务端数据(未运行时 玩家/TPS/CPU/内存 均为 -1)</summary>
    public class ServerStatPoint
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsRunning { get; set; }
        public int Players { get; set; }
        public double Tps1m { get; set; }
        public double McCpu { get; set; }
        public double McMem { get; set; }
    }
}