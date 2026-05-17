using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using RtCli.Modules;
using RtCli.Modules.Unit;
using Spectre.Console;
using Rt.Common;

namespace Rt.Core
{
    /// <summary>
    /// 客户端发包频率限制状态
    /// </summary>
    internal class PacketsLimitClientState
    {
        public string ClientIp { get; set; } = "";
        /// <summary>当前统计周期(1秒)内的发包数</summary>
        public int CurrentPeriodPackets { get; set; }
        /// <summary>上一统计周期的发包数</summary>
        public int LastPeriodPackets { get; set; }
        /// <summary>是否处于持续监测窗口</summary>
        public bool InMonitoring { get; set; }
        /// <summary>持续监测窗口起始时间</summary>
        public DateTime MonitorStartTime { get; set; }
        /// <summary>窗口内超持续阈值次数</summary>
        public int SustainedExceedCount { get; set; }
        /// <summary>是否已执行过动作(避免短时间重复)</summary>
        public bool ActionExecuted { get; set; }
        /// <summary>动作执行后的冷却结束时间</summary>
        public DateTime ActionCooldownUntil { get; set; }
    }

    /// <summary>
    /// 发包频率限制子功能(原 PacketMonitor 的发包监测功能)。
    /// 订阅 NetworkMonitor.PacketReceived 事件，统计每个客户端的发包频率，
    /// 检测异常发包并触发动作。
    ///
    /// 检测逻辑:
    ///   1. 每秒统计每个客户端的发包数
    ///   2. 发包数超过 trigger_threshold → 进入持续监测
    ///   3. 持续监测窗口内每秒发包数超过 sustained_threshold 记一次"超限"
    ///   4. 超限次数达到 sustained_count → 通过 RtCli / 指令发送 action 命令
    ///
    /// 此子功能不自行抓包，依赖 NetworkMonitor 提供数据。
    /// </summary>
    public class PacketsLimitMonitor
    {
        private const string Name = "PacketsLimit";

        private readonly ConcurrentDictionary<string, PacketsLimitClientState> _clientStates = new();
        private CancellationTokenSource? _cts;
        private Task? _monitorTask;
        private volatile bool _isRunning = false;

        public bool IsRunning => _isRunning;

        /// <summary>启动子功能：订阅 NetworkMonitor 事件并启动监测循环</summary>
        public bool Start()
        {
            if (_isRunning)
            {
                Output.Log("发包限制子功能已在运行", 2, Name);
                return false;
            }

            var nm = NetworkMonitor.Instance;
            if (!nm.IsRunning)
            {
                Output.Log("[red]网络监测器未运行[/]，请先启动 rte monitor start", 3, Name);
                return false;
            }

            var cfg = RtConfig.Current.PacketsLimit;

            // 订阅 NetworkMonitor 事件
            nm.PacketReceived += OnPacketReceived;

            _cts = new CancellationTokenSource();
            _monitorTask = Task.Run(() => MonitorLoop(_cts.Token));

            _isRunning = true;

            Output.Log($"[green]发包限制子功能已启动[/] 触发阈值: {cfg.TriggerThreshold}/s | 持续阈值: {cfg.SustainedThreshold}/s × {cfg.SustainedCount}次 | 窗口: {cfg.MonitorWindowSeconds}s | 动作: {cfg.Action}", 1, Name);
            return true;
        }

        /// <summary>停止子功能：取消订阅并停止监测循环</summary>
        public void Stop()
        {
            if (!_isRunning) return;

            NetworkMonitor.Instance.PacketReceived -= OnPacketReceived;

            _cts?.Cancel();
            try { _monitorTask?.Wait(2000); } catch { }

            _clientStates.Clear();
            _isRunning = false;
            Output.Log("发包限制子功能已停止", 1, Name);
        }

        /// <summary>NetworkMonitor 数据包事件处理</summary>
        private void OnPacketReceived(object? sender, NetworkPacketEventArgs e)
        {
            try
            {
                // 只关心 客户端→服务端 的包
                if (e.Analyzed.Direction != PacketDirection.ClientToServer)
                    return;

                var state = _clientStates.GetOrAdd(e.Analyzed.ClientIp, ip => new PacketsLimitClientState
                {
                    ClientIp = ip
                });
                lock (state)
                {
                    state.CurrentPeriodPackets++;
                }
            }
            catch { }
        }

        /// <summary>每秒执行的监测循环</summary>
        private void MonitorLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    Task.Delay(1000, token).Wait(token);
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
                    Output.Log($"发包限制监测循环异常: {Markup.Escape(ex.Message)}", 3, Name);
                }
            }
        }

        private void ProcessPeriod()
        {
            var cfg = RtConfig.Current.PacketsLimit;
            var now = DateTime.Now;

            foreach (var kvp in _clientStates)
            {
                var state = kvp.Value;
                int periodPackets;
                lock (state)
                {
                    periodPackets = state.CurrentPeriodPackets;
                    state.CurrentPeriodPackets = 0;
                }
                state.LastPeriodPackets = periodPackets;

                // 冷却期内不处理
                if (now < state.ActionCooldownUntil)
                    continue;

                if (!state.InMonitoring)
                {
                    // 正常状态：检测是否超过触发阈值
                    if (state.LastPeriodPackets > cfg.TriggerThreshold)
                    {
                        state.InMonitoring = true;
                        state.MonitorStartTime = now;
                        state.SustainedExceedCount = 0;
                        state.ActionExecuted = false;
                        Output.Log($"[yellow]客户端 {Markup.Escape(state.ClientIp)} 发包 {state.LastPeriodPackets}/s 超过触发阈值 {cfg.TriggerThreshold}，进入持续监测[/]", 2, Name);

                        if (state.LastPeriodPackets > cfg.SustainedThreshold)
                        {
                            state.SustainedExceedCount++;
                            CheckTriggerAction(state, cfg);
                        }
                    }
                }
                else
                {
                    // 监测窗口超时，重置
                    if ((now - state.MonitorStartTime).TotalSeconds >= cfg.MonitorWindowSeconds)
                    {
                        Output.Log($"客户端 {Markup.Escape(state.ClientIp)} 监测窗口结束，超限 {state.SustainedExceedCount}/{cfg.SustainedCount} 次，恢复正常监测", 1, Name);
                        state.InMonitoring = false;
                        state.SustainedExceedCount = 0;
                        state.ActionExecuted = false;
                        continue;
                    }

                    if (state.LastPeriodPackets > cfg.SustainedThreshold)
                    {
                        state.SustainedExceedCount++;
                        Output.Log($"客户端 {Markup.Escape(state.ClientIp)} 持续超限 {state.SustainedExceedCount}/{cfg.SustainedCount} (本周期 {state.LastPeriodPackets}/s)", 2, Name);
                        CheckTriggerAction(state, cfg);
                    }
                }
            }
        }

        private void CheckTriggerAction(PacketsLimitClientState state, PacketsLimitSection cfg)
        {
            if (state.ActionExecuted) return;
            if (state.SustainedExceedCount < cfg.SustainedCount) return;

            state.ActionExecuted = true;
            var remaining = cfg.MonitorWindowSeconds - (DateTime.Now - state.MonitorStartTime).TotalSeconds;
            state.ActionCooldownUntil = DateTime.Now.AddSeconds(Math.Max(30, remaining + 30));

            ExecuteAction(state.ClientIp, cfg);
        }

        /// <summary>通过RtCli的/指令方法执行动作命令</summary>
        private void ExecuteAction(string clientIp, PacketsLimitSection cfg)
        {
            string action = cfg.Action?.Trim() ?? "minecraft:ban-ip";
            string command = action.Contains("{ip}")
                ? action.Replace("{ip}", clientIp)
                : $"{action} {clientIp}";

            Output.Log($"[red]触发动作[/]: 客户端 {Markup.Escape(clientIp)} 超限，执行命令: [yellow]/{Markup.Escape(command)}[/]", 3, Name);

            try
            {
                var (ok, result) = Backend.SendMcCommand(cfg.ServerKey, command);
                if (ok)
                    Output.Log($"[green]动作已执行[/]: /{Markup.Escape(command)} | {Markup.Escape(result)}", 1, Name);
                else
                    Output.Log($"[red]动作执行失败[/]: {Markup.Escape(result)}", 3, Name);
            }
            catch (Exception ex)
            {
                Output.Log($"执行动作失败: {Markup.Escape(ex.Message)}", 3, Name);
            }
        }

        /// <summary>显示发包限制状态</summary>
        public void ShowStatus()
        {
            var cfg = RtConfig.Current.PacketsLimit;

            var table = new Table()
                .Border(TableBorder.Rounded)
                .Title("[green]发包频率限制状态[/]");
            table.AddColumn("项目");
            table.AddColumn("值");

            table.AddRow("运行状态", _isRunning ? "[green]运行中[/]" : "[red]已停止[/]");
            table.AddRow("是否启用(配置)", cfg.Enabled ? "[green]是[/]" : "[red]否[/]");
            table.AddRow("触发阈值", $"{cfg.TriggerThreshold} 包/秒");
            table.AddRow("持续阈值", $"{cfg.SustainedThreshold} 包/秒");
            table.AddRow("达成次数", $"{cfg.SustainedCount} 次");
            table.AddRow("监测窗口", $"{cfg.MonitorWindowSeconds} 秒");
            table.AddRow("触发动作", Markup.Escape(cfg.Action));
            table.AddRow("服务端标识", Markup.Escape(string.IsNullOrEmpty(cfg.ServerKey) ? "(当前服务端)" : cfg.ServerKey));
            table.AddRow("已跟踪客户端", _clientStates.Count.ToString());

            AnsiConsole.Write(table);

            if (_clientStates.Count > 0)
            {
                var clientTable = new Table()
                    .Border(TableBorder.Rounded)
                    .Title("[cyan]客户端发包统计[/]");
                clientTable.AddColumn("客户端IP");
                clientTable.AddColumn("状态");
                clientTable.AddColumn("上周期包数");
                clientTable.AddColumn("超限次数");

                foreach (var kvp in _clientStates)
                {
                    var s = kvp.Value;
                    string stateStr = s.InMonitoring ? "[yellow]持续监测中[/]" : "[green]正常[/]";
                    clientTable.AddRow(
                        Markup.Escape(s.ClientIp),
                        stateStr,
                        s.LastPeriodPackets.ToString(),
                        $"{s.SustainedExceedCount}/{cfg.SustainedCount}");
                }
                AnsiConsole.Write(clientTable);
            }
        }
    }
}
