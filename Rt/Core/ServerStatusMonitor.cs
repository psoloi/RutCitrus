using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Rt.Common;
using RtCli.Modules;
using RtCli.Modules.Extension;

namespace Rt.Core
{
    /// <summary>
    /// MC 服务器状态监测器。
    /// 按 rt_config.yml 的 status_query.interval_seconds 周期查询 status_query.servers 列表，
    /// 每次查询完成后通过 EventBus 发布 ServerStatusEvent(包含服务器标识与状态信息列表)。
    /// </summary>
    public class ServerStatusMonitor : IDisposable
    {
        private const string Name = "McStatus";

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly Dictionary<string, bool> _lastOnline = new();
        private CancellationTokenSource? _cts;
        private Task? _loopTask;
        private volatile bool _isRunning;

        public bool IsRunning => _isRunning;
        public DateTime? LastQueryTime { get; private set; }
        public ServerStatusEvent? LastEvent { get; private set; }

        /// <summary>启动周期查询(启动后立即执行一次, 之后按配置间隔循环)。</summary>
        public bool Start()
        {
            if (_isRunning)
            {
                Output.Log("MC 服务器状态监测已在运行", 2, Name);
                return false;
            }

            var cfg = RtConfig.Current.StatusQuery;
            var targets = ResolveTargets(cfg);
            if (targets.Count == 0)
            {
                Output.Log("[red]status_query 未配置可用的服务器[/]", 3, Name);
                return false;
            }

            int interval = Math.Max(1, cfg.IntervalSeconds);
            _cts = new CancellationTokenSource();
            _isRunning = true;
            _loopTask = Task.Run(() => LoopAsync(_cts.Token));

            Output.Log($"[green]MC 服务器状态监测已启动[/] 服务器={targets.Count} 间隔={interval}s 事件=ServerStatusEvent", 1, Name);
            return true;
        }

        /// <summary>停止周期查询。</summary>
        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;
            var cts = _cts;
            _cts = null;
            try { cts?.Cancel(); } catch { }
            try { _loopTask?.Wait(2000); } catch { }
            cts?.Dispose();
            _loopTask = null;

            Output.Log("MC 服务器状态监测已停止", 1, Name);
        }

        private async Task LoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await QueryOnceAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Output.Debug(ex, "MC服务器状态查询", Name);
                }

                int interval = Math.Max(1, RtConfig.Current.StatusQuery.IntervalSeconds);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(interval), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>立即查询一次全部服务器并发布事件。</summary>
        public async Task<ServerStatusEvent> QueryOnceAsync(CancellationToken token = default)
        {
            var cfg = RtConfig.Current.StatusQuery;
            var targets = ResolveTargets(cfg);
            int timeout = Math.Max(1, cfg.TimeoutSeconds);

            var sw = Stopwatch.StartNew();
            var tasks = targets.Select(t => Task.Run(() => QueryOne(t, timeout), token)).ToList();
            var results = (await Task.WhenAll(tasks)).ToList();
            sw.Stop();

            var evt = new ServerStatusEvent(results, JsonSerializer.Serialize(results, _jsonOpts));
            LastQueryTime = DateTime.Now;
            LastEvent = evt;

            EventBus.Publish(evt);
            ReportTransitions(results);

            Output.Debug($"MC 状态查询完成: {results.Count(r => r.Online)}/{results.Count} 在线, 耗时 {sw.ElapsedMilliseconds}ms", Name);
            return evt;
        }

        /// <summary>输出当前配置与最近一次查询结果。</summary>
        public void ShowStatus()
        {
            var cfg = RtConfig.Current.StatusQuery;
            var targets = ResolveTargets(cfg);

            Output.Log($"[green]MC 服务器状态监测[/] 状态={(_isRunning ? "[green]运行中[/]" : "[grey]未运行[/]")} " +
                       $"enabled={cfg.Enabled} 间隔={cfg.IntervalSeconds}s 超时={cfg.TimeoutSeconds}s 服务器={targets.Count}", 1, Name);

            if (LastQueryTime.HasValue)
                Output.Log($"上次查询: {LastQueryTime:yyyy-MM-dd HH:mm:ss}", 1, Name);
            else
                Output.Log("尚未执行查询", 1, Name);

            foreach (var t in targets)
            {
                var r = LastEvent?.Servers.FirstOrDefault(x => x.Id == t.Id);
                if (r == null)
                {
                    Output.Log($"{t.Id} | {t.Host}:{t.Port} | [grey]尚未查询[/]", 1, Name);
                }
                else if (r.Online)
                {
                    Output.Log($"{r.Id} | {r.Host}:{r.Port} | [green]在线[/] | 版本={r.Version} | 玩家={r.OnlinePlayers}/{r.MaxPlayers} | 延迟={r.LatencyMs}ms | MOTD={r.Motd}", 1, Name);
                }
                else
                {
                    Output.Log($"{r.Id} | {r.Host}:{r.Port} | [red]离线[/] | {r.Error}", 1, Name);
                }
            }
        }

        /// <summary>解析配置中的服务器列表; 为空时回退到 default_host/default_port(标识 default)。</summary>
        public static List<StatusQueryServer> ResolveTargets(StatusQuerySection cfg)
        {
            var list = (cfg.Servers ?? new List<StatusQueryServer>())
                .Where(s => s != null && s.Enabled && !string.IsNullOrWhiteSpace(s.Host))
                .ToList();

            foreach (var s in list)
            {
                if (string.IsNullOrWhiteSpace(s.Id))
                    s.Id = $"{s.Host}:{s.Port}";
            }

            if (list.Count == 0)
            {
                list.Add(new StatusQueryServer
                {
                    Id = "default",
                    Host = string.IsNullOrWhiteSpace(cfg.DefaultHost) ? "127.0.0.1" : cfg.DefaultHost,
                    Port = cfg.DefaultPort > 0 ? cfg.DefaultPort : 25565,
                    Enabled = true
                });
            }

            return list;
        }

        private static ServerStatusInfo QueryOne(StatusQueryServer server, int timeoutSeconds)
        {
            var info = new ServerStatusInfo
            {
                Id = server.Id,
                Host = server.Host,
                Port = server.Port
            };

            try
            {
                var (status, latency) = MCPingProtocol.Query(server.Host, server.Port, timeoutSeconds);
                info.Online = true;
                info.LatencyMs = latency;
                info.Version = status.Version?.Name ?? "";
                info.OnlinePlayers = status.Players?.Online ?? 0;
                info.MaxPlayers = status.Players?.Max ?? 0;
                info.Motd = StripColorCodes(status.GetDescriptionText());
            }
            catch (Exception ex)
            {
                info.Online = false;
                info.Error = ex.Message;
            }

            return info;
        }

        /// <summary>在线/离线状态发生变化时输出提示, 避免每个周期重复刷屏。</summary>
        private void ReportTransitions(List<ServerStatusInfo> results)
        {
            foreach (var r in results)
            {
                if (_lastOnline.TryGetValue(r.Id, out bool was))
                {
                    if (was && !r.Online)
                        Output.Log($"[red]服务器 {r.Id} 离线[/]: {r.Error}", 2, Name);
                    else if (!was && r.Online)
                        Output.Log($"[green]服务器 {r.Id} 恢复在线[/] 玩家={r.OnlinePlayers}/{r.MaxPlayers}", 1, Name);
                }
                else if (!r.Online)
                {
                    Output.Log($"[yellow]服务器 {r.Id} 查询失败[/]: {r.Error}", 2, Name);
                }

                _lastOnline[r.Id] = r.Online;
            }
        }

        /// <summary>移除 MOTD 中的 § 颜色/格式代码并压缩换行。</summary>
        private static string StripColorCodes(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var sb = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '§')
                {
                    i++; // 跳过格式代码
                    continue;
                }
                sb.Append(text[i]);
            }

            return sb.ToString().Replace("\r", " ").Replace("\n", " ").Trim();
        }

        public void Dispose()
        {
            Stop();
            _lastOnline.Clear();
        }
    }
}
