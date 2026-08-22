using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RtCli.Modules;
using RtCli.Modules.Extension;
using Spectre.Console;
using Rt.Common;
using Rt.Core.PacketEvents.Protocol;

namespace Rt.Core.PacketEvents
{
    /// <summary>
    /// 数据包事件功能监测器。
    /// 订阅 NetworkMonitor.PacketReceived，按 client_event_tick / server_event_tick 周期
    /// 聚合客户端/服务端数据包；周期内一旦有数据包发生变化便解码并通过 EventBus 发布
    /// PacketClientEvent / PacketServerEvent。启动/停止时分别发布 PacketInitializeEvent / PacketStopEvent。
    /// </summary>
    public class PacketEventsMonitor : IDisposable
    {
        private const string Name = "PacketEvents";

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly ConcurrentQueue<NetworkPacketEventArgs> _clientPackets = new();
        private readonly ConcurrentQueue<NetworkPacketEventArgs> _serverPackets = new();
        private readonly McDecoder _decoder = new();

        private CancellationTokenSource? _cts;
        private Task? _clientTask;
        private Task? _serverTask;
        private volatile bool _isRunning = false;

        public bool IsRunning => _isRunning;
        public long ClientEventCount => Interlocked.Read(ref _clientEventCount);
        public long ServerEventCount => Interlocked.Read(ref _serverEventCount);
        public int LastClientPacketCount { get; private set; }
        public int LastServerPacketCount { get; private set; }

        private long _clientEventCount;
        private long _serverEventCount;

        /// <summary>启动数据包事件功能：订阅 NetworkMonitor 事件并启动发布循环。</summary>
        public bool Start()
        {
            if (_isRunning)
            {
                Output.Log("数据包事件功能已在运行", 2, Name);
                return false;
            }

            var nm = NetworkMonitor.Instance;
            if (!nm.IsRunning)
            {
                Output.Log("[red]网络监测器未运行[/]，请先启动 rte monitor start", 3, Name);
                return false;
            }

            var cfg = RtConfig.Current.PacketEvents;
            if (cfg.ClientEventTick <= 0 && cfg.ServerEventTick <= 0)
            {
                Output.Log("[red]client_event_tick 与 server_event_tick 均无效(需>0)[/]", 3, Name);
                return false;
            }

            nm.PacketReceived += OnPacketReceived;

            _cts = new CancellationTokenSource();
            if (cfg.ClientEventTick > 0)
                _clientTask = Task.Run(() => PublishLoop(_cts.Token, isClient: true));
            if (cfg.ServerEventTick > 0)
                _serverTask = Task.Run(() => PublishLoop(_cts.Token, isClient: false));

            _isRunning = true;

            EventBus.Publish(new PacketInitializeEvent("数据包事件功能已启动"));
            Output.Log($"[green]数据包事件功能已启动[/] client_event_tick={cfg.ClientEventTick}ms server_event_tick={cfg.ServerEventTick}ms", 1, Name);
            return true;
        }

        /// <summary>停止数据包事件功能：取消订阅并发布 PacketStopEvent。</summary>
        public void Stop()
        {
            if (!_isRunning) return;

            NetworkMonitor.Instance.PacketReceived -= OnPacketReceived;

            var cts = _cts;
            _cts = null;
            cts?.Cancel();
            try { _clientTask?.Wait(2000); } catch { }
            try { _serverTask?.Wait(2000); } catch { }
            cts?.Dispose();
            _clientTask = null;
            _serverTask = null;

            while (_clientPackets.TryDequeue(out _)) { }
            while (_serverPackets.TryDequeue(out _)) { }
            _isRunning = false;

            EventBus.Publish(new PacketStopEvent("数据包事件功能已关闭"));
            Output.Log("数据包事件功能已停止", 1, Name);
        }

        /// <summary>NetworkMonitor 数据包事件处理：按方向入队。</summary>
        private void OnPacketReceived(object? sender, NetworkPacketEventArgs e)
        {
            if (!_isRunning) return;
            try
            {
                if (e.Analyzed.Direction == PacketDirection.ClientToServer)
                    _clientPackets.Enqueue(e);
                else if (e.Analyzed.Direction == PacketDirection.ServerToClient)
                    _serverPackets.Enqueue(e);
            }
            catch { }
        }

        /// <summary>按 tick 周期将队列中的包解码并发布对应事件。</summary>
        private void PublishLoop(CancellationToken token, bool isClient)
        {
            var cfg = RtConfig.Current.PacketEvents;
            int tick = isClient ? cfg.ClientEventTick : cfg.ServerEventTick;
            var queue = isClient ? _clientPackets : _serverPackets;

            while (!token.IsCancellationRequested)
            {
                try { Task.Delay(tick, token).Wait(token); }
                catch (OperationCanceledException) { break; }

                if (token.IsCancellationRequested) break;

                try
                {
                    Flush(queue, isClient, tick);
                }
                catch (Exception ex)
                {
                    Output.Log($"数据包事件发布循环异常: {Markup.Escape(ex.Message)}", 3, Name);
                }
            }
        }

        private void Flush(ConcurrentQueue<NetworkPacketEventArgs> queue, bool isClient, int tick)
        {
            var items = new List<NetworkPacketEventArgs>();
            while (queue.TryDequeue(out var item))
                items.Add(item);

            if (items.Count == 0) return;

            var flow = isClient ? PacketFlow.ServerBound : PacketFlow.ClientBound;
            var decoded = new List<DecodedMcPacket>();
            foreach (var item in items)
            {
                if (item.RawCapture == null) continue;
                var list = _decoder.Process(item.RawCapture, flow, item.Analyzed.ClientIp, item.Timestamp);
                if (list.Count > 0)
                    decoded.AddRange(list);
            }

            int count = decoded.Count;
            string json = JsonSerializer.Serialize(decoded, _jsonOpts);

            if (isClient)
            {
                LastClientPacketCount = count;
                Interlocked.Increment(ref _clientEventCount);
                EventBus.Publish(new PacketClientEvent(tick, count, json));
            }
            else
            {
                LastServerPacketCount = count;
                Interlocked.Increment(ref _serverEventCount);
                EventBus.Publish(new PacketServerEvent(tick, count, json));
            }
        }

        /// <summary>显示事件订阅情况与实时数据包详情(5秒观察窗口)。</summary>
        public void ShowDebug()
        {
            var cfg = RtConfig.Current.PacketEvents;

            var statusTable = new Table()
                .Border(TableBorder.Rounded)
                .Title("[green]数据包事件功能状态[/]");
            statusTable.AddColumn("项目");
            statusTable.AddColumn("值");
            statusTable.AddRow("运行状态", _isRunning ? "[green]运行中[/]" : "[red]已停止[/]");
            statusTable.AddRow("是否启用(配置)", cfg.Enabled ? "[green]是[/]" : "[red]否[/]");
            statusTable.AddRow("客户端事件周期", $"{cfg.ClientEventTick} ms");
            statusTable.AddRow("服务端事件周期", $"{cfg.ServerEventTick} ms");
            statusTable.AddRow("已发布客户端事件", ClientEventCount.ToString());
            statusTable.AddRow("已发布服务端事件", ServerEventCount.ToString());
            statusTable.AddRow("最近客户端事件包数", LastClientPacketCount.ToString());
            statusTable.AddRow("最近服务端事件包数", LastServerPacketCount.ToString());
            AnsiConsole.Write(statusTable);

            var eventTable = new Table()
                .Border(TableBorder.Rounded)
                .Title("[cyan]事件订阅情况[/]");
            eventTable.AddColumn("事件");
            eventTable.AddColumn("已订阅处理器数");
            eventTable.AddRow("PacketInitializeEvent", EventBus.GetHandlerCount<PacketInitializeEvent>().ToString());
            eventTable.AddRow("PacketStopEvent", EventBus.GetHandlerCount<PacketStopEvent>().ToString());
            eventTable.AddRow("PacketClientEvent", EventBus.GetHandlerCount<PacketClientEvent>().ToString());
            eventTable.AddRow("PacketServerEvent", EventBus.GetHandlerCount<PacketServerEvent>().ToString());
            AnsiConsole.Write(eventTable);

            if (!_isRunning)
            {
                Output.Log("[yellow]数据包事件功能未运行，无法实时观察数据包[/]", 2, Name);
                return;
            }

            Output.Log("[grey]开始实时观察事件(5秒)...[/]", 1, Name);

            void ClientHandler(PacketClientEvent e) => PrintPacketEvent("C→S", e.TickMs, e.PacketCount, e.Json);
            void ServerHandler(PacketServerEvent e) => PrintPacketEvent("S→C", e.TickMs, e.PacketCount, e.Json);

            EventBus.Subscribe<PacketClientEvent>(ClientHandler);
            EventBus.Subscribe<PacketServerEvent>(ServerHandler);

            Thread.Sleep(5000);

            EventBus.Unsubscribe<PacketClientEvent>(ClientHandler);
            EventBus.Unsubscribe<PacketServerEvent>(ServerHandler);

            Output.Log("[grey]实时观察结束[/]", 1, Name);
        }

        private void PrintPacketEvent(string dir, int tick, int count, string json)
        {
            string preview = json.Length > 1200 ? json.Substring(0, 1200) + "...(截断)" : json;
            Output.Log($"[cyan][{Markup.Escape(dir)}][/] tick={tick}ms 包数={count}", 1, Name);
            Output.Log($"  {Markup.Escape(preview)}", 1, Name);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}