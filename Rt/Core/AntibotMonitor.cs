using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RtCli.Modules;
using RtCli.Modules.Unit;
using Spectre.Console;
using Rt.Common;

namespace Rt.Core
{
    /// <summary>
    /// 单个客户端的反机器人监测状态
    /// </summary>
    internal class AntibotClientState
    {
        public string ClientIp { get; set; } = "";
        /// <summary>当前统计周期内的流量(字节)</summary>
        public long CurrentPeriodBytes { get; set; }
        /// <summary>上一统计周期的流量</summary>
        public long LastPeriodBytes { get; set; }
        /// <summary>当前统计周期内的包数</summary>
        public int CurrentPeriodPackets { get; set; }
        /// <summary>上一统计周期的包数</summary>
        public int LastPeriodPackets { get; set; }
        /// <summary>是否处于持续监测窗口</summary>
        public bool InMonitoring { get; set; }
        /// <summary>持续监测窗口起始时间</summary>
        public DateTime MonitorStartTime { get; set; }
        /// <summary>窗口内超阈值周期数</summary>
        public int SustainedExceedCount { get; set; }
        /// <summary>是否已执行封禁(避免重复)</summary>
        public bool ActionExecuted { get; set; }
        /// <summary>封禁冷却结束时间</summary>
        public DateTime ActionCooldownUntil { get; set; }
    }

    /// <summary>
    /// 反机器人流量监测子功能。
    /// 订阅 NetworkMonitor.PacketReceived 事件，统计每个客户端IP的流量，
    /// 检测流量异常增大并执行封禁。
    ///
    /// 检测逻辑:
    ///   1. 每 stat_window_seconds 秒统计一次各客户端IP的流量(字节)
    ///   2. 当某IP流量超过 traffic_threshold 时，进入持续监测
    ///   3. 在 monitor_window_seconds 窗口内，若超阈值周期数达到 sustained_count，执行封禁
    ///   4. 封禁命令通过 RtCli 的 / 指令方法发送(使用 server_key 指定目标服务端)
    ///
    /// 此子功能不自行抓包，依赖 NetworkMonitor 提供数据。
    /// </summary>
    public class AntibotMonitor
    {
        private const string Name = "Antibot";

        private readonly ConcurrentDictionary<string, AntibotClientState> _clientStates = new();
        private CancellationTokenSource? _cts;
        private Task? _monitorTask;
        private volatile bool _isRunning = false;

        // ===== 运行时开关(可通过命令切换) =====
        private volatile bool _notifyEnabled;
        private volatile bool _verboseEnabled;

        // ===== 通知周期累计(NotifyLoop读取并重置) =====
        private long _notifyAccumBytes;
        private long _notifyAccumPackets;
        private DateTime _notifyWindowStart;

        // ===== 通知任务 =====
        private CancellationTokenSource? _notifyCts;
        private Task? _notifyTask;

        public bool IsRunning => _isRunning;
        public bool NotifyEnabled => _notifyEnabled;
        public bool VerboseEnabled => _verboseEnabled;

        /// <summary>切换流量通知开关，返回切换后的状态</summary>
        public bool ToggleNotify()
        {
            _notifyEnabled = !_notifyEnabled;
            Output.Log($"流量通知: {(_notifyEnabled ? "[green]已开启[/]" : "[red]已关闭[/]")}", 1, Name);
            return _notifyEnabled;
        }

        /// <summary>切换详细数据包日志开关，返回切换后的状态</summary>
        public bool ToggleVerbose()
        {
            _verboseEnabled = !_verboseEnabled;
            Output.Log($"详细数据包日志: {(_verboseEnabled ? "[green]已开启[/]" : "[red]已关闭[/]")}", 1, Name);
            return _verboseEnabled;
        }

        public bool Start()
        {
            if (_isRunning)
            {
                Output.Log("反机器人监测器已在运行", 2, Name);
                return false;
            }

            var nm = NetworkMonitor.Instance;
            if (!nm.IsRunning)
            {
                Output.Log("[red]网络监测器未运行[/]，请先启动 rte monitor start", 3, Name);
                return false;
            }

            var cfg = RtConfig.Current.Antibot;

            // 订阅 NetworkMonitor 事件
            nm.PacketReceived += OnPacketReceived;

            _cts = new CancellationTokenSource();
            _monitorTask = Task.Run(() => MonitorLoop(_cts.Token));

            // 初始化运行时开关
            _notifyEnabled = cfg.NotifyEnabled;
            _verboseEnabled = cfg.VerboseEnabled;
            _notifyWindowStart = DateTime.Now;

            // 启动通知任务
            _notifyCts = new CancellationTokenSource();
            _notifyTask = Task.Run(() => NotifyLoop(_notifyCts.Token));

            _isRunning = true;

            Output.Log($"[green]反机器人监测子功能已启动[/] 流量阈值: {FormatBytes(cfg.TrafficThreshold)}/{cfg.StatWindowSeconds}s | 持续 {cfg.SustainedCount}次 → 封禁 | 窗口: {cfg.MonitorWindowSeconds}s", 1, Name);
            Output.Log($"流量通知: {(_notifyEnabled ? "[green]开[/]" : "[red]关[/]")} (间隔 {cfg.NotifyIntervalSeconds}s) | 详细日志: {(_verboseEnabled ? "[green]开[/]" : "[red]关[/]")}", 1, Name);

            return true;
        }

        public void Stop()
        {
            if (!_isRunning) return;

            NetworkMonitor.Instance.PacketReceived -= OnPacketReceived;

            _cts?.Cancel();
            _notifyCts?.Cancel();

            try { _monitorTask?.Wait(2000); } catch { }
            try { _notifyTask?.Wait(2000); } catch { }

            _clientStates.Clear();
            _isRunning = false;
            Output.Log("反机器人监测子功能已停止", 1, Name);
        }

        /// <summary>NetworkMonitor 数据包事件处理</summary>
        private void OnPacketReceived(object? sender, NetworkPacketEventArgs e)
        {
            try
            {
                var state = _clientStates.GetOrAdd(e.Analyzed.ClientIp, ip => new AntibotClientState
                {
                    ClientIp = ip
                });
                lock (state)
                {
                    state.CurrentPeriodBytes += e.Analyzed.PayloadLength;
                    state.CurrentPeriodPackets++;
                }

                // 详细数据包日志
                if (_verboseEnabled)
                {
                    string dir = e.Analyzed.Direction == PacketDirection.ClientToServer ? "[cyan]C→S[/]" : "[grey]S→C[/]";
                    string time = e.Timestamp.ToString("HH:mm:ss.fff");
                    Output.Log($"[grey][{time}][包][/] {Markup.Escape(e.Analyzed.ClientIp)} {dir} {FormatBytes(e.Analyzed.PayloadLength)} ({e.Analyzed.PayloadLength}B)", 1, Name);
                }
            }
            catch { }
        }

        private void MonitorLoop(CancellationToken token)
        {
            var cfg = RtConfig.Current.Antibot;
            int intervalMs = cfg.StatWindowSeconds * 1000;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    Task.Delay(intervalMs, token).Wait(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (token.IsCancellationRequested) break;

                try
                {
                    ProcessPeriod();
                }
                catch (Exception ex)
                {
                    Output.Log($"反机器人监测循环异常: {Markup.Escape(ex.Message)}", 3, Name);
                }
            }
        }

        /// <summary>周期性流量通知循环(每 notify_interval_seconds 秒输出一次)</summary>
        private void NotifyLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                int intervalMs;
                try
                {
                    intervalMs = Math.Max(1, RtConfig.Current.Antibot.NotifyIntervalSeconds) * 1000;
                }
                catch
                {
                    intervalMs = 15000;
                }

                try
                {
                    Task.Delay(intervalMs, token).Wait(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (token.IsCancellationRequested) break;

                try
                {
                    NotifyTraffic();
                }
                catch (Exception ex)
                {
                    Output.Log($"流量通知循环异常: {Markup.Escape(ex.Message)}", 3, Name);
                }
            }
        }

        /// <summary>输出当前流量通知(Sonar风格)</summary>
        private void NotifyTraffic()
        {
            var cfg = RtConfig.Current.Antibot;
            var now = DateTime.Now;

            // 读取并重置累计
            long bytes = Interlocked.Exchange(ref _notifyAccumBytes, 0);
            long packets = Interlocked.Exchange(ref _notifyAccumPackets, 0);
            double elapsed = Math.Max(1, (now - _notifyWindowStart).TotalSeconds);
            _notifyWindowStart = now;

            double bytesPerSec = bytes / elapsed;
            double packetsPerSec = packets / elapsed;

            if (!_notifyEnabled) return;

            // Top 5 IP by last period bytes
            var topIps = new List<(string Ip, long Bytes, int Packets, bool Monitoring)>();
            foreach (var kvp in _clientStates)
            {
                var s = kvp.Value;
                topIps.Add((s.ClientIp, s.LastPeriodBytes, s.LastPeriodPackets, s.InMonitoring));
            }
            topIps.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

            // 检测是否有IP处于监测中(疑似攻击)
            int attackCount = topIps.Count(x => x.Monitoring);

            string header = attackCount > 0
                ? $"[red][流量通知]⚠ 疑似攻击[/] 监测中IP: [red]{attackCount}[/]"
                : $"[cyan][流量通知][/]";

            string summary = $"{header} | 周期: {FormatBytes(bytes)} / {packets}包 | " +
                             $"速率: [yellow]{FormatBytes((long)bytesPerSec)}/s[/] {packetsPerSec:F1}包/s | " +
                             $"客户端: {_clientStates.Count}";

            if (topIps.Count > 0)
            {
                var top = topIps[0];
                string topMark = top.Monitoring ? "[red]" : "[grey]";
                summary += $" | Top: {topMark}{Markup.Escape(top.Ip)}[/] {FormatBytes(top.Bytes)}({top.Packets}包)";
            }

            Output.Log(summary, 1, Name);

            // 多个IP超阈值时，列出Top 3
            if (attackCount > 0 && topIps.Count > 1)
            {
                int showN = Math.Min(3, topIps.Count);
                for (int i = 0; i < showN; i++)
                {
                    var t = topIps[i];
                    if (t.Bytes == 0) break;
                    string mark = t.Monitoring ? "[red]⚠[/] " : "   ";
                    Output.Log($"{mark}#{i + 1} {Markup.Escape(t.Ip)} | {FormatBytes(t.Bytes)} | {t.Packets}包", 1, Name);
                }
            }
        }

        private void ProcessPeriod()
        {
            var cfg = RtConfig.Current.Antibot;
            var now = DateTime.Now;
            long periodTotalBytes = 0;
            long periodTotalPackets = 0;

            foreach (var kvp in _clientStates)
            {
                var state = kvp.Value;
                long periodBytes;
                int periodPackets;
                lock (state)
                {
                    periodBytes = state.CurrentPeriodBytes;
                    periodPackets = state.CurrentPeriodPackets;
                    state.CurrentPeriodBytes = 0;
                    state.CurrentPeriodPackets = 0;
                }
                state.LastPeriodBytes = periodBytes;
                state.LastPeriodPackets = periodPackets;
                periodTotalBytes += periodBytes;
                periodTotalPackets += periodPackets;

                if (now < state.ActionCooldownUntil)
                    continue;

                if (!state.InMonitoring)
                {
                    if (periodBytes > cfg.TrafficThreshold)
                    {
                        state.InMonitoring = true;
                        state.MonitorStartTime = now;
                        state.SustainedExceedCount = 1;
                        state.ActionExecuted = false;
                        Output.Log($"[yellow]客户端 {Markup.Escape(state.ClientIp)} 流量 {FormatBytes(periodBytes)}/{cfg.StatWindowSeconds}s 超过阈值 {FormatBytes(cfg.TrafficThreshold)}，进入持续监测[/]", 2, Name);
                        CheckTriggerAction(state, cfg);
                    }
                }
                else
                {
                    if ((now - state.MonitorStartTime).TotalSeconds >= cfg.MonitorWindowSeconds)
                    {
                        Output.Log($"客户端 {Markup.Escape(state.ClientIp)} 监测窗口结束，超阈值 {state.SustainedExceedCount}/{cfg.SustainedCount} 次，恢复正常监测", 1, Name);
                        state.InMonitoring = false;
                        state.SustainedExceedCount = 0;
                        state.ActionExecuted = false;
                        continue;
                    }

                    if (periodBytes > cfg.TrafficThreshold)
                    {
                        state.SustainedExceedCount++;
                        Output.Log($"客户端 {Markup.Escape(state.ClientIp)} 持续超阈值 {state.SustainedExceedCount}/{cfg.SustainedCount} (本周期 {FormatBytes(periodBytes)})", 2, Name);
                        CheckTriggerAction(state, cfg);
                    }
                }
            }

            // 累计本周期到通知累计(供 NotifyLoop 读取)
            Interlocked.Add(ref _notifyAccumBytes, periodTotalBytes);
            Interlocked.Add(ref _notifyAccumPackets, periodTotalPackets);
        }

        private void CheckTriggerAction(AntibotClientState state, AntibotSection cfg)
        {
            if (state.ActionExecuted) return;
            if (state.SustainedExceedCount < cfg.SustainedCount) return;

            state.ActionExecuted = true;
            var remaining = cfg.MonitorWindowSeconds - (DateTime.Now - state.MonitorStartTime).TotalSeconds;
            state.ActionCooldownUntil = DateTime.Now.AddSeconds(Math.Max(60, remaining + 60));

            ExecuteAction(state.ClientIp, cfg);
        }

        /// <summary>通过RtCli的/指令方法执行封禁命令</summary>
        private void ExecuteAction(string clientIp, AntibotSection cfg)
        {
            string command = (cfg.Action ?? "minecraft:ban-ip {ip}").Replace("{ip}", clientIp);

            Output.Log($"[red]触发封禁[/]: 客户端 {Markup.Escape(clientIp)} 流量异常，执行命令: [yellow]/{Markup.Escape(command)}[/]", 3, Name);

            try
            {
                var (ok, result) = Backend.SendMcCommand(cfg.ServerKey, command);
                if (ok)
                {
                    Output.Log($"[green]封禁已执行[/]: /{Markup.Escape(command)} | {Markup.Escape(result)}", 1, Name);
                }
                else
                {
                    Output.Log($"[red]封禁执行失败[/]: {Markup.Escape(result)}", 3, Name);
                }
            }
            catch (Exception ex)
            {
                Output.Log($"封禁执行失败: {Markup.Escape(ex.Message)}", 3, Name);
            }
        }

        /// <summary>显示反机器人监测状态</summary>
        public void ShowStatus()
        {
            var cfg = RtConfig.Current.Antibot;

            var table = new Table()
                .Border(TableBorder.Rounded)
                .Title("[green]反机器人流量监测状态[/]");
            table.AddColumn("项目");
            table.AddColumn("值");

            table.AddRow("运行状态", _isRunning ? "[green]运行中[/]" : "[red]已停止[/]");
            table.AddRow("是否启用(配置)", cfg.Enabled ? "[green]是[/]" : "[red]否[/]");
            table.AddRow("流量阈值", $"{FormatBytes(cfg.TrafficThreshold)} / {cfg.StatWindowSeconds}s");
            table.AddRow("达成次数", $"{cfg.SustainedCount} 次");
            table.AddRow("监测窗口", $"{cfg.MonitorWindowSeconds} 秒");
            table.AddRow("封禁动作", Markup.Escape(cfg.Action));
            table.AddRow("服务端标识", Markup.Escape(string.IsNullOrEmpty(cfg.ServerKey) ? "(当前服务端)" : cfg.ServerKey));
            table.AddRow("流量通知", _notifyEnabled ? $"[green]开[/] (每 {cfg.NotifyIntervalSeconds}s)" : "[red]关[/]");
            table.AddRow("详细日志", _verboseEnabled ? "[green]开[/]" : "[red]关[/]");
            table.AddRow("已跟踪客户端", _clientStates.Count.ToString());

            AnsiConsole.Write(table);

            if (_clientStates.Count > 0)
            {
                var clientTable = new Table()
                    .Border(TableBorder.Rounded)
                    .Title("[cyan]客户端流量统计[/]");
                clientTable.AddColumn("客户端IP");
                clientTable.AddColumn("状态");
                clientTable.AddColumn("上周期流量");
                clientTable.AddColumn("上周期包数");
                clientTable.AddColumn("超阈值次数");

                foreach (var kvp in _clientStates)
                {
                    var s = kvp.Value;
                    string stateStr = s.InMonitoring
                        ? "[yellow]监测中[/]"
                        : "[green]正常[/]";
                    clientTable.AddRow(
                        Markup.Escape(s.ClientIp),
                        stateStr,
                        FormatBytes(s.LastPeriodBytes),
                        s.LastPeriodPackets.ToString(),
                        $"{s.SustainedExceedCount}/{cfg.SustainedCount}");
                }
                AnsiConsole.Write(clientTable);
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }
}
