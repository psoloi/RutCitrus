using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RtCli.Modules;
using RtCli.Modules.Function;
using RtCli.Modules.Unit;
using Spectre.Console;
using Rt.Common;

namespace Rt.Core.AntiCheat
{
    /// <summary>
    /// 数据包详情(用于 alert/save)
    /// </summary>
    internal class PacketDetail
    {
        public string Time { get; set; } = "";
        public int Size { get; set; }
        public string Direction { get; set; } = "";
    }

    /// <summary>
    /// 单个检测项的违规状态
    /// </summary>
    internal class DetectionState
    {
        /// <summary>当前违规计数</summary>
        public int ViolationCount;
        /// <summary>最近一次违规时间(用于衰减)</summary>
        public DateTime LastViolationTime;
        /// <summary>已触发过的行动阈值 [x] (避免重复执行)</summary>
        public HashSet<int> TriggeredThresholds = new();
        /// <summary>FastPlace: 当前秒内的发包计数</summary>
        public int CurrentSecondPackets;
        /// <summary>FastPlace: 当前秒的起始时间</summary>
        public DateTime CurrentSecondStart;
        /// <summary>FastEat: 上一次 use-item-like 包的时间</summary>
        public DateTime LastUseItemTime;
        /// <summary>最近的包详情(用于 alert/save)</summary>
        public Queue<PacketDetail> RecentPackets = new();
    }

    /// <summary>
    /// 单个客户端的违规状态
    /// </summary>
    internal class ClientViolationState
    {
        public string ClientIp { get; set; } = "";
        /// <summary>各检测项的状态, key = 检测名 (FastPlace, FastEat)</summary>
        public ConcurrentDictionary<string, DetectionState> Detections { get; } = new();
    }

    /// <summary>
    /// 解析后的行动定义
    /// </summary>
    internal class ActionDef
    {
        public int Threshold { get; set; }
        /// <summary>alert / save / ban / command</summary>
        public string ActionType { get; set; } = "";
        /// <summary>命令参数或自定义警报文本</summary>
        public string Args { get; set; } = "";
    }

    /// <summary>
    /// MC服务端外反作弊监测器。
    /// 订阅 NetworkMonitor.PacketReceived，基于数据包大小与时序启发式检测:
    ///   - FastPlace: 客户端1秒内放置方块类发包数过多(20-35字节范围, 正常≤4/s, 作弊≥10/s)
    ///   - FastEat: 客户端使用物品类发包间隔过短(8-15字节范围, 正常≥1600ms, 作弊≤400ms)
    ///
    /// 检测原理(无MC协议解析，基于包大小+时序启发式):
    ///   USE_ITEM_ON (放方块) TCP载荷约 20-35 字节
    ///   USE_ITEM (使用物品/吃食物) TCP载荷约 8-15 字节
    ///
    /// 注意:
    ///   - 在线模式(加密)下无法解析载荷内容，但包大小+时序仍可检测
    ///   - 包大小范围是近似值，可能因MC版本/压缩/分片略有差异
    ///   - 玩家名称暂以 player@IP 形式标识(未解析登录协议)
    /// </summary>
    public class AntiCheatMonitor : IDisposable
    {
        private const string Name = "AntiCheat";
        private const int MaxRecentPackets = 10;

        // 检测参数(包大小范围, 可按需调整)
        private const int FastPlaceMinSize = 20;
        private const int FastPlaceMaxSize = 35;
        private const int FastPlaceNormalRate = 4;   // 正常放方块速率上限(包/秒)
        private const int FastPlaceCheatRate = 10;    // 作弊放方块速率(包/秒)

        private const int FastEatMinSize = 8;
        private const int FastEatMaxSize = 15;
        private const double FastEatNormalMs = 1600;  // 正常吃食物间隔
        private const double FastEatCheatMs = 400;    // 作弊吃食物间隔

        private readonly ConcurrentDictionary<string, ClientViolationState> _clients = new();
        private CancellationTokenSource? _cts;
        private Task? _monitorTask;
        private volatile bool _isRunning = false;

        public bool IsRunning => _isRunning;

        public bool Start()
        {
            if (_isRunning)
            {
                Output.Log("反作弊监测器已在运行", 2, Name);
                return false;
            }

            var nm = NetworkMonitor.Instance;
            if (!nm.IsRunning)
            {
                Output.Log("[red]网络监测器未运行[/]，请先启动 rte monitor start", 3, Name);
                return false;
            }

            var cfg = RtConfig.Current.AntiCheat;
            nm.PacketReceived += OnPacketReceived;

            _cts = new CancellationTokenSource();
            _monitorTask = Task.Run(() => MonitorLoop(_cts.Token));

            _isRunning = true;

            var enabled = new List<string>();
            if (cfg.FastPlace.Enabled) enabled.Add($"FastPlace(VL={cfg.FastPlace.Vl})");
            if (cfg.FastEat.Enabled) enabled.Add($"FastEat(VL={cfg.FastEat.Vl})");

            Output.Log($"[green]反作弊监测器已启动[/] 检测项: {string.Join(", ", enabled)} | 警报: {(cfg.Alert ? "[green]开[/]" : "[red]关[/]")}", 1, Name);
            return true;
        }

        public void Stop()
        {
            if (!_isRunning) return;

            NetworkMonitor.Instance.PacketReceived -= OnPacketReceived;

            _cts?.Cancel();
            try { _monitorTask?.Wait(2000); } catch { }

            _clients.Clear();
            _isRunning = false;
            Output.Log("反作弊监测器已停止", 1, Name);
        }

        /// <summary>NetworkMonitor 数据包事件处理</summary>
        private void OnPacketReceived(object? sender, NetworkPacketEventArgs e)
        {
            try
            {
                if (e.Analyzed.Direction != PacketDirection.ClientToServer) return;
                if (!e.Analyzed.IsMcRelevant) return;

                string ip = e.Analyzed.ClientIp;
                int size = e.Analyzed.PayloadLength;
                if (size <= 0) return;

                var client = _clients.GetOrAdd(ip, x => new ClientViolationState { ClientIp = x });
                var cfg = RtConfig.Current.AntiCheat;
                var detail = new PacketDetail
                {
                    Time = e.Timestamp.ToString("HH:mm:ss.fff"),
                    Size = size,
                    Direction = "C→S"
                };

                // ===== FastPlace 检测 =====
                if (cfg.FastPlace.Enabled && size >= FastPlaceMinSize && size <= FastPlaceMaxSize)
                {
                    var state = client.Detections.GetOrAdd("FastPlace", _ => new DetectionState
                    {
                        CurrentSecondStart = e.Timestamp
                    });

                    lock (state)
                    {
                        DateTime now = e.Timestamp;
                        if ((now - state.CurrentSecondStart).TotalSeconds >= 1)
                        {
                            state.CurrentSecondPackets = 0;
                            state.CurrentSecondStart = now;
                        }
                        state.CurrentSecondPackets++;
                        EnqueueDetail(state, detail);
                    }
                }

                // ===== FastEat 检测 =====
                if (cfg.FastEat.Enabled && size >= FastEatMinSize && size <= FastEatMaxSize)
                {
                    var state = client.Detections.GetOrAdd("FastEat", _ => new DetectionState());

                    lock (state)
                    {
                        DateTime now = e.Timestamp;
                        if (state.LastUseItemTime != DateTime.MinValue)
                        {
                            double intervalMs = (now - state.LastUseItemTime).TotalMilliseconds;
                            // similarity = (1600 - interval) / (1600 - 400) * 100
                            double similarity = Math.Max(0, Math.Min(100,
                                (FastEatNormalMs - intervalMs) / (FastEatNormalMs - FastEatCheatMs) * 100));

                            CheckViolation(state, "FastEat", ip, similarity, cfg.FastEat, detail);
                        }
                        state.LastUseItemTime = now;
                        EnqueueDetail(state, detail);
                    }
                }
            }
            catch { }
        }

        /// <summary>入队包详情, 维护最大数量</summary>
        private void EnqueueDetail(DetectionState state, PacketDetail detail)
        {
            state.RecentPackets.Enqueue(detail);
            while (state.RecentPackets.Count > MaxRecentPackets)
                state.RecentPackets.Dequeue();
        }

        /// <summary>监测循环: 每秒检查 FastPlace 速率 + 违规衰减</summary>
        private void MonitorLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { Task.Delay(1000, token).Wait(token); }
                catch (OperationCanceledException) { break; }

                if (token.IsCancellationRequested) break;

                try
                {
                    CheckFastPlaceRates();
                    DecayViolations();
                }
                catch (Exception ex)
                {
                    Output.Log($"反作弊监测循环异常: {Markup.Escape(ex.Message)}", 3, Name);
                }
            }
        }

        /// <summary>检查所有客户端的 FastPlace 速率</summary>
        private void CheckFastPlaceRates()
        {
            var cfg = RtConfig.Current.AntiCheat;
            if (!cfg.FastPlace.Enabled) return;

            DateTime now = DateTime.Now;
            foreach (var kvp in _clients)
            {
                if (!kvp.Value.Detections.TryGetValue("FastPlace", out var state)) continue;

                lock (state)
                {
                    // 若当前秒已过, 检查上一秒的计数
                    if ((now - state.CurrentSecondStart).TotalSeconds >= 1)
                    {
                        int rate = state.CurrentSecondPackets;
                        state.CurrentSecondPackets = 0;
                        state.CurrentSecondStart = now;

                        if (rate > FastPlaceNormalRate)
                        {
                            // similarity = (rate - 4) / (10 - 4) * 100
                            double similarity = Math.Max(0, Math.Min(100,
                                (rate - FastPlaceNormalRate) / (double)(FastPlaceCheatRate - FastPlaceNormalRate) * 100));

                            var detail = new PacketDetail
                            {
                                Time = now.ToString("HH:mm:ss.fff"),
                                Size = 0,
                                Direction = $"rate={rate}/s"
                            };
                            CheckViolation(state, "FastPlace", kvp.Key, similarity, cfg.FastPlace, detail);
                        }
                    }
                }
            }
        }

        /// <summary>违规衰减: 超过 decay_minutes 无新违规则清零</summary>
        private void DecayViolations()
        {
            var cfg = RtConfig.Current.AntiCheat;
            DateTime now = DateTime.Now;

            foreach (var client in _clients.Values)
            {
                foreach (var det in client.Detections)
                {
                    var detCfg = det.Key switch
                    {
                        "FastPlace" => cfg.FastPlace,
                        "FastEat" => cfg.FastEat,
                        _ => null
                    };
                    if (detCfg == null || detCfg.DecayMinutes <= 0) continue;

                    lock (det.Value)
                    {
                        if (det.Value.ViolationCount > 0 &&
                            (now - det.Value.LastViolationTime).TotalMinutes >= detCfg.DecayMinutes)
                        {
                            det.Value.ViolationCount = 0;
                            det.Value.TriggeredThresholds.Clear();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 检查是否应增加违规计数并触发行动。
        /// similarity ≥ B% 时计1次违规; 违规计数达到 [x] 阈值时执行对应行动。
        /// </summary>
        private void CheckViolation(DetectionState state, string detection, string ip,
            double similarity, AntiCheatDetectionSection detCfg, PacketDetail detail)
        {
            var (thresholdA, thresholdB) = ParseVl(detCfg.Vl);
            if (thresholdB <= 0) return;

            // 相似度未达阈值, 不计违规
            if (similarity < thresholdB) return;

            state.ViolationCount++;
            state.LastViolationTime = DateTime.Now;

            string player = $"player@{ip}";
            int vl = state.ViolationCount;

            Output.Log($"[yellow][AC][/] {detection} | {Markup.Escape(player)} | 相似度: {similarity:F1}%≥{thresholdB}% | VL: {vl}/{thresholdA} | 包: {detail.Size}B {detail.Direction}", 1, Name);

            // 解析并执行行动
            var actions = ParseActions(detCfg.Actions);
            foreach (var action in actions)
            {
                if (vl >= action.Threshold && !state.TriggeredThresholds.Contains(action.Threshold))
                {
                    state.TriggeredThresholds.Add(action.Threshold);
                    ExecuteAction(action, detection, ip, player, vl, similarity, state);
                }
            }
        }

        /// <summary>解析 vl "A:B" → (A, B)</summary>
        private (int A, int B) ParseVl(string vl)
        {
            if (string.IsNullOrWhiteSpace(vl)) return (10, 45);
            var parts = vl.Split(':');
            if (parts.Length != 2) return (10, 45);
            if (!int.TryParse(parts[0].Trim(), out int a)) a = 10;
            if (!int.TryParse(parts[1].Trim(), out int b)) b = 45;
            if (a <= 0) a = 10;
            if (b <= 0) b = 45;
            return (a, b);
        }

        /// <summary>解析 actions 多行字符串 → ActionDef 列表</summary>
        private List<ActionDef> ParseActions(string actions)
        {
            var result = new List<ActionDef>();
            if (string.IsNullOrWhiteSpace(actions)) return result;

            foreach (var rawLine in actions.Split('\n', '\r'))
            {
                var trimmed = rawLine.Trim();
                if (string.IsNullOrEmpty(trimmed) || !trimmed.StartsWith("[")) continue;

                int closeBracket = trimmed.IndexOf(']');
                if (closeBracket <= 0) continue;

                string thresholdStr = trimmed.Substring(1, closeBracket - 1);
                if (!int.TryParse(thresholdStr.Trim(), out int threshold)) continue;

                string rest = trimmed.Substring(closeBracket + 1).Trim();
                if (string.IsNullOrEmpty(rest)) continue;

                int spaceIdx = rest.IndexOf(' ');
                string actionType, args;
                if (spaceIdx > 0)
                {
                    actionType = rest.Substring(0, spaceIdx).ToLowerInvariant();
                    args = rest.Substring(spaceIdx + 1).Trim();
                }
                else
                {
                    actionType = rest.ToLowerInvariant();
                    args = "";
                }

                result.Add(new ActionDef { Threshold = threshold, ActionType = actionType, Args = args });
            }

            return result;
        }

        /// <summary>执行单个行动</summary>
        private void ExecuteAction(ActionDef action, string detection, string ip, string player,
            int vl, double similarity, DetectionState state)
        {
            var cfg = RtConfig.Current.AntiCheat;
            string details = string.Join("; ", state.RecentPackets.Select(p => $"{p.Time} {p.Size}B {p.Direction}"));

            switch (action.ActionType)
            {
                case "alert":
                    if (cfg.Alert)
                    {
                        if (string.IsNullOrEmpty(action.Args))
                        {
                            Output.Log($"[red][AC警报][/] 玩家 [cyan]{Markup.Escape(player)}[/] 触发 [yellow]{detection}[/] | VL={vl} | 相似度={similarity:F1}% | 详情: {Markup.Escape(details)}", 2, Name);
                        }
                        else
                        {
                            string msg = ReplacePlaceholders(action.Args, player, ip, detection, vl, similarity, details);
                            Output.Log($"[red][AC警报][/] {Markup.Escape(msg)}", 2, Name);
                        }
                    }
                    break;

                case "save":
                    SaveViolation(player, ip, detection, vl, similarity, state);
                    break;

                case "ban":
                    {
                        string banCmd = $"ban-ip {ip}";
                        var (ok, msg) = Backend.SendMcCommand(cfg.ServerKey, banCmd);
                        Output.Log($"[red][AC封禁][/] {Markup.Escape(player)} | /{banCmd} | {(ok ? "[green]成功[/]" : "[red]失败[/]")}: {Markup.Escape(msg)}", 1, Name);
                    }
                    break;

                case "command":
                    {
                        string cmd = ReplacePlaceholders(action.Args, player, ip, detection, vl, similarity, details);
                        bool executed = CommandRegistry.TryExecuteWithArgs(cmd);
                        Output.Log($"[grey][AC命令][/] {Markup.Escape(cmd)} | {(executed ? "[green]已执行[/]" : "[red]未找到命令[/]")}", 1, Name);
                    }
                    break;

                default:
                    Output.Log($"[yellow]未知行动类型: {Markup.Escape(action.ActionType)}[/]", 2, Name);
                    break;
            }
        }

        /// <summary>替换占位符</summary>
        private string ReplacePlaceholders(string template, string player, string ip,
            string detection, int vl, double similarity, string details)
        {
            return template
                .Replace("{player}", player)
                .Replace("{ip}", ip)
                .Replace("{detection}", detection)
                .Replace("{vl}", vl.ToString())
                .Replace("{sim}", similarity.ToString("F1"))
                .Replace("{details}", details);
        }

        /// <summary>保存违规数据到 RtAC_data/data_玩家_时间.json</summary>
        private void SaveViolation(string player, string ip, string detection,
            int vl, double similarity, DetectionState state)
        {
            try
            {
                string dir = Path.Combine(Config.DataPath, "RtAC_data");
                Directory.CreateDirectory(dir);

                string safeName = player.Replace("@", "_").Replace(".", "_").Replace(":", "_");
                string fileName = $"data_{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                string filePath = Path.Combine(dir, fileName);

                var data = new
                {
                    player = player,
                    ip = ip,
                    detection = detection,
                    violation_count = vl,
                    similarity = Math.Round(similarity, 1),
                    trigger_time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    packet_details = state.RecentPackets.Select(p => new
                    {
                        time = p.Time,
                        size = p.Size,
                        direction = p.Direction
                    }).ToList()
                };

                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);

                Output.Log($"[green][AC保存][/] {Markup.Escape(player)} | {detection} | VL={vl} → {Markup.Escape(filePath)}", 1, Name);
            }
            catch (Exception ex)
            {
                Output.Log($"[red]保存违规数据失败[/]: {Markup.Escape(ex.Message)}", 3, Name);
            }
        }

        /// <summary>显示当前反作弊状态</summary>
        public void ShowStatus()
        {
            var cfg = RtConfig.Current.AntiCheat;

            var table = new Table()
                .Border(TableBorder.Rounded)
                .Title("[green]反作弊监测器状态[/]");
            table.AddColumn("项目");
            table.AddColumn("值");

            table.AddRow("运行状态", _isRunning ? "[green]运行中[/]" : "[red]已停止[/]");
            table.AddRow("是否启用(配置)", cfg.Enabled ? "[green]是[/]" : "[red]否[/]");
            table.AddRow("全局警报", cfg.Alert ? "[green]开[/]" : "[red]关[/]");
            table.AddRow("FastPlace", cfg.FastPlace.Enabled
                ? $"[green]启用[/] VL={cfg.FastPlace.Vl} 衰减={cfg.FastPlace.DecayMinutes}min"
                : "[red]禁用[/]");
            table.AddRow("FastEat", cfg.FastEat.Enabled
                ? $"[green]启用[/] VL={cfg.FastEat.Vl} 衰减={cfg.FastEat.DecayMinutes}min"
                : "[red]禁用[/]");
            table.AddRow("已跟踪客户端", _clients.Count.ToString());

            AnsiConsole.Write(table);

            if (_clients.Count > 0)
            {
                var clientTable = new Table()
                    .Border(TableBorder.Rounded)
                    .Title("[cyan]客户端违规统计[/]");
                clientTable.AddColumn("客户端IP");
                clientTable.AddColumn("FastPlace VL");
                clientTable.AddColumn("FastEat VL");
                clientTable.AddColumn("最近违规时间");

                foreach (var kvp in _clients)
                {
                    int fpVl = kvp.Value.Detections.TryGetValue("FastPlace", out var fp) ? fp.ViolationCount : 0;
                    int feVl = kvp.Value.Detections.TryGetValue("FastEat", out var fe) ? fe.ViolationCount : 0;
                    DateTime lastViolation = DateTime.MinValue;
                    if (fp != null && fp.LastViolationTime > lastViolation) lastViolation = fp.LastViolationTime;
                    if (fe != null && fe.LastViolationTime > lastViolation) lastViolation = fe.LastViolationTime;

                    string lastStr = lastViolation == DateTime.MinValue ? "-" : lastViolation.ToString("HH:mm:ss");
                    clientTable.AddRow(
                        Markup.Escape(kvp.Key),
                        fpVl > 0 ? $"[yellow]{fpVl}[/]" : "0",
                        feVl > 0 ? $"[yellow]{feVl}[/]" : "0",
                        lastStr);
                }
                AnsiConsole.Write(clientTable);
            }
        }

        /// <summary>重置指定客户端的违规计数(或所有客户端)</summary>
        public void ResetViolations(string? ip = null)
        {
            if (string.IsNullOrEmpty(ip))
            {
                foreach (var client in _clients.Values)
                {
                    foreach (var det in client.Detections.Values)
                    {
                        lock (det)
                        {
                            det.ViolationCount = 0;
                            det.TriggeredThresholds.Clear();
                        }
                    }
                }
                Output.Log("[green]已重置所有客户端的违规计数[/]", 1, Name);
            }
            else if (_clients.TryGetValue(ip, out var client))
            {
                foreach (var det in client.Detections.Values)
                {
                    lock (det)
                    {
                        det.ViolationCount = 0;
                        det.TriggeredThresholds.Clear();
                    }
                }
                Output.Log($"[green]已重置[/] {Markup.Escape(ip)} 的违规计数", 1, Name);
            }
            else
            {
                Output.Log($"[yellow]未找到客户端[/] {Markup.Escape(ip)}", 2, Name);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
