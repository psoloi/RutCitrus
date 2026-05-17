using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PacketDotNet;
using SharpPcap;
using RtCli.Modules;
using RtCli.Modules.Unit;
using Spectre.Console;
using Rt.Common;

namespace Rt.Common
{
    /// <summary>
    /// 网络包事件参数：包含分析后的数据包信息
    /// </summary>
    public class NetworkPacketEventArgs : EventArgs
    {
        /// <summary>分析后的数据包</summary>
        public AnalyzedPacket Analyzed { get; }
        /// <summary>原始捕获时间戳</summary>
        public DateTime Timestamp { get; }
        /// <summary>原始RawCapture(供高级订阅者使用)</summary>
        public RawCapture? RawCapture { get; }

        public NetworkPacketEventArgs(AnalyzedPacket analyzed, DateTime timestamp, RawCapture? rawCapture)
        {
            Analyzed = analyzed;
            Timestamp = timestamp;
            RawCapture = rawCapture;
        }
    }

    /// <summary>
    /// 全局网络通信监测器(单例)。
    /// 负责网络接口管理、BPF过滤、GetNextPacket轮询抓包，
    /// 并通过 PacketReceived 事件将分析后的数据包分发给订阅者。
    ///
    /// 子功能模块(PacketsLimit/Antibot)订阅此事件进行各自的处理，
    /// 不再各自抓包，避免重复占用设备。
    /// </summary>
    public sealed class NetworkMonitor : IDisposable
    {
        private const string Name = "NetworkMonitor";

        private static readonly Lazy<NetworkMonitor> _instance = new(() => new NetworkMonitor());
        /// <summary>全局单例</summary>
        public static NetworkMonitor Instance => _instance.Value;

        private ILiveDevice? _device;
        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private volatile bool _isRunning = false;

        // ===== 诊断计数器 =====
        private long _totalCapturedPackets;   // 所有捕获的包(含非MC)
        private long _totalMcPackets;          // MC相关包数
        private long _totalMcBytes;            // MC相关字节数
        private long _totalMcC2SPackets;       // 客户端→服务端包数
        private long _totalMcS2CPackets;       // 服务端→客户端包数
        private DateTime _startTime;
        private string _bpfFilter = "";
        private string _interface = "";
        private string _serverIp = "";
        private int _serverPort;

        // ===== Debug模式 =====
        private volatile bool _debugEnabled;
        /// <summary>Debug模式开关：开启后输出每个捕获包的详细信息</summary>
        public bool DebugEnabled
        {
            get => _debugEnabled;
            set
            {
                _debugEnabled = value;
                Output.Log($"Debug模式: {(_debugEnabled ? "[green]开[/]" : "[red]关[/]")}", 1, Name);
            }
        }

        // ===== 事件 =====
        /// <summary>
        /// 数据包接收事件。所有MC相关包(客户端↔服务端)都会触发此事件。
        /// 订阅者在此事件中进行各自的业务处理(发包限制/流量统计等)。
        /// </summary>
        public event EventHandler<NetworkPacketEventArgs>? PacketReceived;

        /// <summary>当前运行状态</summary>
        public bool IsRunning => _isRunning;
        /// <summary>当前抓包接口</summary>
        public ILiveDevice? Device => _device;
        /// <summary>当前BPF过滤器</summary>
        public string BpfFilter => _bpfFilter;
        /// <summary>总捕获包数(含非MC)</summary>
        public long TotalCapturedPackets => Interlocked.Read(ref _totalCapturedPackets);
        /// <summary>MC相关包数</summary>
        public long TotalMcPackets => Interlocked.Read(ref _totalMcPackets);
        /// <summary>MC相关流量(字节)</summary>
        public long TotalMcBytes => Interlocked.Read(ref _totalMcBytes);
        /// <summary>客户端→服务端包数</summary>
        public long TotalMcC2SPackets => Interlocked.Read(ref _totalMcC2SPackets);
        /// <summary>服务端→客户端包数</summary>
        public long TotalMcS2CPackets => Interlocked.Read(ref _totalMcS2CPackets);
        /// <summary>运行时长</summary>
        public TimeSpan Uptime => _isRunning ? DateTime.Now - _startTime : TimeSpan.Zero;

        private NetworkMonitor() { }

        #region 原生库解析

        private static bool _nativeResolverRegistered = false;

        /// <summary>
        /// 预加载 SharpPcap 依赖的原生库(wpcap.dll)。
        /// 在可回收 AssemblyLoadContext 中，P/Invoke 原生库解析可能失败，
        /// 此方法通过显式路径加载并注册 DllImportResolver 解决该问题。
        /// </summary>
        public static void EnsureNativeLibrary()
        {
            if (_nativeResolverRegistered) return;

            try
            {
                var sharpPcapAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "SharpPcap");

                if (sharpPcapAsm != null)
                {
                    NativeLibrary.SetDllImportResolver(sharpPcapAsm, ResolveNativeLibrary);
                }
            }
            catch { }

            _nativeResolverRegistered = true;
        }

        private static IntPtr ResolveNativeLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out IntPtr handle))
                return handle;

            string[] candidates = GetCandidatePaths(libraryName);
            foreach (string path in candidates)
            {
                if (NativeLibrary.TryLoad(path, out handle))
                    return handle;
            }

            return IntPtr.Zero;
        }

        private static string[] GetCandidatePaths(string libraryName)
        {
            string normalized = libraryName.ToLowerInvariant();
            string fileName = normalized.EndsWith(".dll") ? normalized : normalized + ".dll";

            var paths = new List<string>
            {
                System.IO.Path.Combine(Environment.SystemDirectory, fileName),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), fileName),
                $@"C:\Windows\System32\{fileName}",
                $@"C:\Windows\SysWOW64\{fileName}"
            };

            string? npcapsDir = Environment.GetEnvironmentVariable("NPCAP_DIR");
            if (!string.IsNullOrEmpty(npcapsDir))
                paths.Add(System.IO.Path.Combine(npcapsDir, fileName));

            return paths.ToArray();
        }

        #endregion

        #region 设备选择

        /// <summary>选择网络接口。优先物理网卡，排除虚拟网卡和回环。</summary>
        public static ILiveDevice? SelectDevice(string interfaceName)
        {
            var devices = CaptureDeviceList.Instance;
            if (devices.Count == 0)
            {
                Output.Log("未找到任何网络接口(Npcap可能未安装或未启动服务)", 3, Name);
                return null;
            }

            if (!string.IsNullOrWhiteSpace(interfaceName))
            {
                foreach (var d in devices)
                {
                    if (string.Equals(d.Name, interfaceName, StringComparison.OrdinalIgnoreCase) ||
                        (d.Description?.Contains(interfaceName, StringComparison.OrdinalIgnoreCase) ?? false))
                    {
                        return d;
                    }
                }
                Output.Log($"未找到指定接口 {interfaceName}，将使用默认接口", 2, Name);
            }

            // 虚拟网卡关键词(Windows常见)
            string[] virtualKeywords = new[]
            {
                "virtual", "vmware", "virtualbox", "hyper-v", "vpn",
                "tap", "tun", "loopback", "bluetooth", "pseudo",
                "wan miniport", "isatap", "teredo", "6to4",
                "host-only", "nat"
            };

            // 第一轮: 排除回环和虚拟网卡
            foreach (var d in devices)
            {
                string desc = (d.Description ?? d.Name).ToLowerInvariant();
                string dname = d.Name.ToLowerInvariant();
                if (dname.Contains("loopback") || desc.Contains("loopback"))
                    continue;
                if (virtualKeywords.Any(k => desc.Contains(k) || dname.Contains(k)))
                    continue;
                return d;
            }

            // 第二轮: 排除回环
            foreach (var d in devices)
            {
                string desc = (d.Description ?? d.Name).ToLowerInvariant();
                string dname = d.Name.ToLowerInvariant();
                if (dname.Contains("loopback") || desc.Contains("loopback"))
                    continue;
                return d;
            }

            return devices[0];
        }

        #endregion

        /// <summary>
        /// 启动网络监测器。
        /// </summary>
        /// <param name="interfaceName">网络接口名(留空自动选择)</param>
        /// <param name="serverIp">MC服务器IP(空/0.0.0.0=通配)</param>
        /// <param name="serverPort">MC服务器端口</param>
        public bool Start(string interfaceName, string serverIp, int serverPort)
        {
            if (_isRunning)
            {
                Output.Log("网络监测器已在运行", 2, Name);
                return false;
            }

            if (serverPort <= 0)
            {
                Output.Log("[red]配置无效[/]: server_port 未设置", 3, Name);
                return false;
            }

            try
            {
                EnsureNativeLibrary();

                _device = SelectDevice(interfaceName);
                if (_device == null)
                {
                    Output.Log("[red]未找到可用的网络接口[/]", 3, Name);
                    return false;
                }

                // 构建BPF过滤器: server_ip 为空/0.0.0.0 时按端口通配(适配MC监听0.0.0.0)
                string filter = PacketAnalyzer.BuildBpfFilter(serverIp, serverPort);
                _bpfFilter = filter;
                _interface = interfaceName ?? "";
                _serverIp = serverIp ?? "";
                _serverPort = serverPort;

                _device.Open(DeviceModes.Promiscuous, 1000);
                _device.Filter = filter;

                _cts = new CancellationTokenSource();
                _captureTask = Task.Run(() => CaptureLoop(_cts.Token));

                _startTime = DateTime.Now;
                _totalCapturedPackets = 0;
                _totalMcPackets = 0;
                _totalMcBytes = 0;
                _totalMcC2SPackets = 0;
                _totalMcS2CPackets = 0;

                _isRunning = true;

                Output.Log($"[green]网络监测器已启动[/] 接口: [cyan]{Markup.Escape(_device.Name)}[/]", 1, Name);
                Output.Log($"接口描述: {Markup.Escape(_device.Description ?? "?")}", 1, Name);
                Output.Log($"BPF过滤器: [yellow]{Markup.Escape(filter)}[/]", 1, Name);
                string ipDisp = PacketAnalyzer.IsWildcardIp(serverIp) ? "(通配-按端口匹配)" : serverIp;
                Output.Log($"监测目标: {Markup.Escape(ipDisp)}:{serverPort}", 1, Name);

                if (PacketAnalyzer.IsWildcardIp(serverIp))
                    Output.Log("[grey]提示: server_ip 为通配模式，按端口匹配。若抓不到包请用 rte monitor debug 检查[/]", 1, Name);
                else if (serverIp == "127.0.0.1")
                    Output.Log("[yellow]警告: server_ip=127.0.0.1 无法捕获外部攻击流量！请改为服务器对外IP或留空(通配)[/]", 2, Name);

                return true;
            }
            catch (Exception ex)
            {
                Exception real = ex;
                while (real is TypeInitializationException && real.InnerException != null)
                    real = real.InnerException;

                Output.Log($"[red]网络监测器启动失败[/]: {Markup.Escape(real.Message)}", 3, Name);

                if (real is DllNotFoundException || real.Message.Contains("wpcap", StringComparison.OrdinalIgnoreCase))
                {
                    Output.Log("[yellow]原因[/]: 缺少 Npcap 原生库(wpcap.dll)。请从 https://npcap.com/ 下载并安装 Npcap", 3, Name);
                }

                CleanupDevice();
                return false;
            }
        }

        /// <summary>停止网络监测器</summary>
        public void Stop()
        {
            if (!_isRunning) return;

            _cts?.Cancel();

            // 先等待抓包线程退出，再关闭设备
            try { _captureTask?.Wait(2000); } catch { }

            CleanupDevice();

            _isRunning = false;
            Output.Log("网络监测器已停止", 1, Name);
        }

        /// <summary>
        /// 抓包轮询循环(替代事件驱动的StartCapture)。
        /// 在 collectible AssemblyLoadContext 中，SharpPcap 的事件回调(OnPacketArrival)会静默失败，
        /// 因为原生线程无法回调到可回收ALC中的委托。改用同步轮询 GetNextPacket 避免此问题。
        /// </summary>
        private void CaptureLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_device == null) break;

                    var status = _device.GetNextPacket(out var capture);
                    if (status == GetPacketStatus.PacketRead)
                    {
                        ProcessPacket(capture);
                    }
                    else if (status == GetPacketStatus.Error)
                    {
                        Thread.Sleep(50);
                    }
                    // ReadTimeout 时立即循环(Open时设的1000ms超时已阻塞)
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        Output.Log($"抓包异常: {Markup.Escape(ex.Message)}", 3, Name);
                        Thread.Sleep(1000);
                    }
                }
            }
            Output.Log("[grey]抓包循环已退出[/]", 1, Name);
        }

        private void ProcessPacket(PacketCapture capture)
        {
            try
            {
                Interlocked.Increment(ref _totalCapturedPackets);

                RawCapture rawCapture = capture.GetPacket();
                var analyzed = PacketAnalyzer.Analyze(rawCapture, _serverIp, _serverPort);
                if (!analyzed.IsMcRelevant)
                    return;

                Interlocked.Increment(ref _totalMcPackets);
                Interlocked.Add(ref _totalMcBytes, analyzed.PayloadLength);

                if (analyzed.Direction == PacketDirection.ClientToServer)
                    Interlocked.Increment(ref _totalMcC2SPackets);
                else if (analyzed.Direction == PacketDirection.ServerToClient)
                    Interlocked.Increment(ref _totalMcS2CPackets);

                // Debug模式: 输出每个捕获包的详细信息
                if (_debugEnabled)
                {
                    string dir = analyzed.Direction == PacketDirection.ClientToServer ? "[cyan]C→S[/]" : "[grey]S→C[/]";
                    string time = DateTime.Now.ToString("HH:mm:ss.fff");
                    Output.Log($"[grey][{time}][Debug][/] {Markup.Escape(analyzed.ClientIp)} {dir} [yellow]{FormatBytes(analyzed.PayloadLength)}[/] ({analyzed.PayloadLength}B)", 1, Name);
                }

                // 分发给订阅者
                PacketReceived?.Invoke(this, new NetworkPacketEventArgs(analyzed, DateTime.Now, rawCapture));
            }
            catch (Exception ex)
            {
                // 处理异常不影响抓包
                if (_debugEnabled)
                    Output.Log($"包处理异常: {Markup.Escape(ex.Message)}", 3, Name);
            }
        }

        #region 诊断与状态显示

        /// <summary>显示网络监测器状态</summary>
        public void ShowStatus()
        {
            var table = new Table()
                .Border(TableBorder.Rounded)
                .Title("[green]网络监测器状态[/]");
            table.AddColumn("项目");
            table.AddColumn("值");

            TimeSpan uptime = Uptime;

            table.AddRow("运行状态", _isRunning ? "[green]运行中[/]" : "[red]已停止[/]");
            table.AddRow("运行时长", $"{uptime.Hours}h{uptime.Minutes}m{uptime.Seconds}s");
            table.AddRow("抓包接口", _device != null ? Markup.Escape(_device.Name) : "[red]未选择[/]");
            table.AddRow("接口描述", _device != null ? Markup.Escape(_device.Description ?? "?") : "-");
            table.AddRow("BPF过滤器", $"[yellow]{Markup.Escape(_bpfFilter)}[/]");
            string ipDisp = PacketAnalyzer.IsWildcardIp(_serverIp) ? $"[grey](通配)[/] :{_serverPort}" : $"{Markup.Escape(_serverIp)}:{_serverPort}";
            table.AddRow("监测目标", ipDisp);
            table.AddRow("Debug模式", _debugEnabled ? "[green]开[/]" : "[red]关[/]");
            table.AddRow("订阅者数量", PacketReceived?.GetInvocationList()?.Length.ToString() ?? "0");
            table.AddRow("总捕获包数", TotalCapturedPackets.ToString());
            table.AddRow("MC相关包数", TotalMcPackets > 0 ? $"[green]{TotalMcPackets}[/]" : "[red]0[/]");
            table.AddRow("MC相关流量", FormatBytes(TotalMcBytes));
            table.AddRow("C→S包数", TotalMcC2SPackets.ToString());
            table.AddRow("S→C包数", TotalMcS2CPackets.ToString());

            AnsiConsole.Write(table);

            // 诊断警告
            if (_isRunning && uptime.TotalSeconds > 30 && TotalMcPackets == 0)
            {
                Output.Log("[red]⚠ 诊断警告[/]: 运行超过30秒但未捕获到任何MC相关流量!", 3, Name);
                Output.Log($"  - 总捕获包数: {TotalCapturedPackets} (若为0说明接口选错)", 3, Name);
                Output.Log($"  - BPF过滤器: {_bpfFilter}", 3, Name);
                if (_serverIp == "127.0.0.1")
                    Output.Log("  - server_ip=127.0.0.1 无法捕获外部攻击流量！请改为服务器对外IP或留空(通配)", 3, Name);
            }
        }

        /// <summary>显示诊断信息: 管理员权限/Npcap/接口列表/配置检查</summary>
        public void ShowDiagnostics()
        {
            Output.Log("[green]=== 网络监测器诊断 ===[/]", 1, Name);

            // 1. 管理员权限检查
            bool isAdmin = false;
            try
            {
                using var identity = new System.Security.Principal.WindowsIdentity("CURRENT_USER");
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { }
            Output.Log($"管理员权限: {(isAdmin ? "[green]是[/]" : "[red]否[/] (Npcap抓包需要管理员权限！)")} ", isAdmin ? 1 : 3, Name);

            // 2. Npcap/wpcap.dll 检查
            string wpcapPath = System.IO.Path.Combine(Environment.SystemDirectory, "wpcap.dll");
            bool wpcapExists = System.IO.File.Exists(wpcapPath);
            Output.Log($"Npcap安装: {(wpcapExists ? "[green]已安装[/] (" + Markup.Escape(wpcapPath) + ")" : "[red]未找到wpcap.dll！请从 https://npcap.com/ 安装并勾选 WinPcap API-compatible Mode[/]")} ", wpcapExists ? 1 : 3, Name);

            // 3. 枚举网络接口
            try
            {
                EnsureNativeLibrary();
                var devices = CaptureDeviceList.Instance;
                Output.Log($"网络接口总数: {devices.Count}", 1, Name);

                if (devices.Count == 0)
                    Output.Log("[red]✗[/] 未找到任何网络接口！Npcap服务可能未启动", 3, Name);

                var devTable = new Table()
                    .Border(TableBorder.Rounded)
                    .Title("[cyan]可用网络接口[/]");
                devTable.AddColumn("序号");
                devTable.AddColumn("名称");
                devTable.AddColumn("描述");
                devTable.AddColumn("当前选择");

                int idx = 1;
                string? currentName = _device?.Name;

                // 若未运行，预览将选择的接口
                if (!_isRunning && string.IsNullOrEmpty(_interface))
                {
                    try
                    {
                        var preview = SelectDevice("");
                        if (preview != null) currentName = preview.Name;
                    }
                    catch { }
                }

                foreach (var d in devices)
                {
                    bool isCurrent = currentName != null && d.Name == currentName;
                    string mark = isCurrent ? "[green] *[/]" : "";
                    devTable.AddRow($"[cyan]{idx}[/]{mark}", Markup.Escape(d.Name), Markup.Escape(d.Description ?? "?"), isCurrent ? "[green]是[/]" : "");
                    idx++;
                }
                AnsiConsole.Write(devTable);
            }
            catch (Exception ex)
            {
                Output.Log($"枚举接口失败: {Markup.Escape(ex.Message)}", 3, Name);
                Output.Log("[yellow]可能原因: SharpPcap原生库加载失败，请以管理员身份运行RtCli[/]", 3, Name);
            }

            // 4. 配置检查
            Output.Log($"配置接口: '{Markup.Escape(_interface)}' (留空则自动选择物理网卡)", 1, Name);
            Output.Log($"配置 server_ip: '{Markup.Escape(_serverIp)}' | server_port: {_serverPort}", 1, Name);

            if (PacketAnalyzer.IsWildcardIp(_serverIp))
                Output.Log("[green]✓[/] server_ip 为通配模式，按端口匹配(适配MC监听0.0.0.0)", 1, Name);
            else if (_serverIp == "127.0.0.1")
                Output.Log("[red]✗[/] server_ip=127.0.0.1 无法捕获外部攻击流量！请改为服务器对外IP或留空", 3, Name);
            else
                Output.Log($"[yellow]?[/] server_ip={_serverIp} 确认此IP是本机网卡实际IP", 2, Name);

            // 5. 运行时统计
            if (_isRunning)
            {
                TimeSpan uptime = Uptime;
                Output.Log($"运行时长: {uptime.Hours}h{uptime.Minutes}m{uptime.Seconds}s | 总捕获: {TotalCapturedPackets}包 | MC相关: {TotalMcPackets}包 ({FormatBytes(TotalMcBytes)})", 1, Name);
                Output.Log($"当前接口: {Markup.Escape(_device?.Name ?? "?")} | BPF: {Markup.Escape(_bpfFilter)}", 1, Name);

                if (TotalCapturedPackets == 0)
                {
                    Output.Log("[red]✗ 诊断结论: 总捕获包数为0[/]", 3, Name);
                    Output.Log("  可能原因:", 3, Name);
                    Output.Log("  a) 接口选错 - 在上方接口列表中找到有流量的接口", 3, Name);
                    if (!isAdmin)
                        Output.Log("  b) 权限不足 - 请以管理员身份运行RtCli", 3, Name);
                    if (!wpcapExists)
                        Output.Log("  c) Npcap未安装", 3, Name);
                }
                else if (TotalMcPackets == 0)
                {
                    Output.Log("[red]✗ 诊断结论: 有捕获包但无MC相关包[/]", 3, Name);
                    Output.Log($"  a) BPF过滤器 '{Markup.Escape(_bpfFilter)}' 过滤掉了MC流量", 3, Name);
                    Output.Log($"  b) MC服务器端口不是 {_serverPort} - 确认实际端口", 3, Name);
                }
                else
                {
                    Output.Log("[green]✓ 诊断结论: 已捕获到MC相关流量，监测器工作正常[/]", 1, Name);
                }
            }
            else
            {
                Output.Log("[yellow]监测器未运行，请用 rte monitor start 启动后再诊断[/]", 2, Name);
            }
        }

        /// <summary>
        /// 显示最近捕获的数据包详细信息(Debug快照)。
        /// 即使Debug模式关闭，也可用此命令临时查看最近包详情。
        /// </summary>
        public void ShowDebugSnapshot()
        {
            Output.Log("[green]=== 网络监测器 Debug 快照 ===[/]", 1, Name);

            if (!_isRunning)
            {
                Output.Log("[yellow]监测器未运行[/]", 2, Name);
                return;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .Title("[cyan]捕获统计[/]");
            table.AddColumn("项目");
            table.AddColumn("值");

            TimeSpan up = Uptime;
            table.AddRow("运行时长", $"{up.Hours}h{up.Minutes}m{up.Seconds}s");
            table.AddRow("总捕获包数", TotalCapturedPackets.ToString());
            table.AddRow("MC相关包数", TotalMcPackets.ToString());
            table.AddRow("MC相关流量", FormatBytes(TotalMcBytes));
            table.AddRow("C→S包数", TotalMcC2SPackets.ToString());
            table.AddRow("S→C包数", TotalMcS2CPackets.ToString());
            double avgRate = up.TotalSeconds > 0 ? TotalMcPackets / up.TotalSeconds : 0;
            double avgBps = up.TotalSeconds > 0 ? TotalMcBytes / up.TotalSeconds : 0;
            table.AddRow("平均速率", $"{avgRate:F1} 包/s | {FormatBytes((long)avgBps)}/s");
            table.AddRow("Debug模式", _debugEnabled ? "[green]开[/]" : "[red]关[/] (用 rte monitor debug 开启)");
            table.AddRow("订阅者", PacketReceived?.GetInvocationList()?.Length.ToString() ?? "0");

            AnsiConsole.Write(table);

            // 临时抓取最近5秒的包详情
            Output.Log($"[grey]临时抓取最近5秒的数据包详情...[/]", 1, Name);
            int snapshotCount = 0;
            const int maxShow = 50;

            void SnapshotHandler(object? sender, NetworkPacketEventArgs e)
            {
                if (snapshotCount >= maxShow) return;
                Interlocked.Increment(ref snapshotCount);
                string dir = e.Analyzed.Direction == PacketDirection.ClientToServer ? "[cyan]C→S[/]" : "[grey]S→C[/]";
                string time = e.Timestamp.ToString("HH:mm:ss.fff");
                Output.Log($"  [grey]{time}[/] {Markup.Escape(e.Analyzed.ClientIp)} {dir} [yellow]{FormatBytes(e.Analyzed.PayloadLength)}[/] ({e.Analyzed.PayloadLength}B)", 1, Name);
            }

            PacketReceived += SnapshotHandler;
            Thread.Sleep(5000);
            PacketReceived -= SnapshotHandler;

            if (snapshotCount == 0)
            {
                Output.Log("[red]5秒内未捕获到任何MC数据包！[/]", 3, Name);
                if (TotalCapturedPackets == 0)
                    Output.Log("  原因: 接口上无任何流量 - 检查接口选择", 3, Name);
                else
                    Output.Log("  原因: 有流量但非MC相关 - 检查 server_ip/server_port 配置", 3, Name);
            }
            else
            {
                Output.Log($"[green]5秒内捕获到 {snapshotCount} 个MC数据包[/]", 1, Name);
            }
        }

        #endregion

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        private void CleanupDevice()
        {
            try { _device?.Close(); } catch { }
            _device = null;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
