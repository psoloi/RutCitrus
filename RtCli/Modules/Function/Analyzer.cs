using RtCli.Modules.Extension;
using RtCli.Modules.Unit;
using Spectre.Console;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RtCli.Modules.Function
{
    public class Analyzer
    {
        // ===== 多实例运行时 =====
        // 每个实例一份独立状态(进程/stdin/RCON/管理协议/日志缓冲/查询缓存), 静态 Analyzer 仅做门面路由。
        private static readonly ConcurrentDictionary<string, InstanceRuntime> _runtimes =
            new ConcurrentDictionary<string, InstanceRuntime>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _scanLock = new object();
        private static List<MinecraftServerInfo> _lastScanResults = new List<MinecraftServerInfo>();
        private const int MaxLogBufferSize = 5000;
        // 兼容附加扫描(本机 PID/序号)的串行锁; 命令发送已改为每实例独立锁
        private static readonly object _attachLock = new object();

        /// <summary>当前选中实例的运行时(CLI 默认操作对象)</summary>
        private static InstanceRuntime Rt => GetRuntime(Config.App.CurrentServer);

        /// <summary>解析实例标识(空 = 当前实例)并返回对应运行时(不存在则创建)。</summary>
        internal static InstanceRuntime GetRuntime(string? instanceId)
        {
            // 未显式指定实例时优先使用当前实例上下文(面板按实例下发的命令), 否则回退全局当前实例
            string id = string.IsNullOrWhiteSpace(instanceId)
                ? (InstanceContext.Current ?? Config.App.CurrentServer)
                : instanceId!;
            return _runtimes.GetOrAdd(id, key =>
            {
                var rt = new InstanceRuntime(key);
                RefreshEntry(rt);
                rt.StreamKey = key;
                return rt;
            });
        }

        /// <summary>刷新实例配置快照(未运行时; 运行中保留启动时快照)</summary>
        private static void RefreshEntry(InstanceRuntime rt)
        {
            if (Config.App.ServerList != null && Config.App.ServerList.TryGetValue(rt.Id, out var entry))
                rt.Entry = entry;
            else
                rt.Entry = Config.CurrentServer;
        }

        private static string NormalizeMode(string raw)
        {
            return raw.ToUpperInvariant() switch
            {
                "RUN" => "RUN",
                "RR" => "RR",          // 子进程 + RCON
                "RCON" => "RCON",      // 纯远程 RCON(可直接连接任意远程服务端)
                "ONLYRCON" => "RCON",  // 旧名 OnlyRcon = 新 RCON
                "MANAGEMENT" => "RM",  // 旧名 Management = 新 RM (Run+Management)
                "RM" => "RM",
                _ => "RM"
            };
        }

        // ===== 兼容别名: 指向"当前选中实例"的运行时字段 =====
        private static Process? _serverProcess { get => Rt.Process; set => Rt.Process = value; }
        private static StreamWriter? _serverInput { get => Rt.Stdin; set => Rt.Stdin = value; }
        private static CancellationTokenSource? _outputCts { get => Rt.Cts; set => Rt.Cts = value; }
        private static int _attachedProcessId { get => Rt.AttachedProcessId; set => Rt.AttachedProcessId = value; }
        private static string _attachedWindowTitle { get => Rt.AttachedWindowTitle; set => Rt.AttachedWindowTitle = value; }
        private static string _connectedServerName { get => Rt.ConnectedServerName; set => Rt.ConnectedServerName = value; }
        private static string _currentMode { get => Rt.Mode; set => Rt.Mode = value; }
        private static bool _isRunModeActive { get => Rt.Running; set => Rt.Running = value; }
        private static string _activeInstanceId { get => Rt.Id; set { } }
        private static string _activeStreamKey { get => Rt.StreamKey; set => Rt.StreamKey = value; }
        private static RconClient? _rconClient { get => Rt.Rcon; set => Rt.Rcon = value; }
        private static int _restartAttemptCount { get => Rt.RestartAttemptCount; set => Rt.RestartAttemptCount = value; }
        private static bool _userInitiatedStop { get => Rt.UserInitiatedStop; set => Rt.UserInitiatedStop = value; }
        private static int _crashNotified { get => Rt.CrashNotified; set => Rt.CrashNotified = value; }
        private static bool _shutdownInProgress { get => Rt.ShutdownInProgress; set => Rt.ShutdownInProgress = value; }
        private static DateTime _serverStartTime { get => Rt.StartTime; set => Rt.StartTime = value; }
        private static object _crashSignalLock => Rt.CrashSignalLock;
        private static List<string> _recentErrorLines => Rt.RecentErrorLines;
        private static CancellationTokenSource? _clientGuideCts { get => Rt.ClientGuideCts; set => Rt.ClientGuideCts = value; }
        private static bool _clientGuideActive { get => Rt.ClientGuideActive; set => Rt.ClientGuideActive = value; }
        private static List<ClientGuideMatch> _lastClientGuideMatches => Rt.LastClientGuideMatches;
        private static object _clientGuideLock => Rt.ClientGuideLock;
        internal static object _logBufferLock => Rt.LogBufferLock;
        internal static LogRingBuffer _logBuffer => Rt.LogBuffer;
        private const int MaxErrorSignatureLines = 30;

        // 基础错误库正则缓存(按 pattern 缓存编译结果, 避免每次分析重复编译全部条目)
        private static readonly Dictionary<string, Regex?> _baseEntryRegexCache = new Dictionary<string, Regex?>(StringComparer.Ordinal);
        private static readonly object _baseEntryRegexLock = new object();
        // 玩家事件正则缓存(逐行匹配的高频路径, 必须预编译并设置超时)
        private static readonly Dictionary<string, Regex?> _playerEventRegexCache = new Dictionary<string, Regex?>(StringComparer.Ordinal);
        private static readonly object _playerEventRegexLock = new object();
        // 玩家事件总预筛正则: 全部事件 pattern 合并为一条, 绝大多数日志行只需一次匹配即可快速返回
        private static Regex? _playerEventPrefilter;
        private static PlayerEventConfig? _playerEventPrefilterConfig;
        private static readonly object _playerEventPrefilterLock = new object();
        private const int MaxBaseMatchResults = 8;
        private static readonly TimeSpan DynamicRegexTimeout = TimeSpan.FromMilliseconds(500);

        private static readonly List<ConfigDiffEntry> _lastConfigDiffs = new List<ConfigDiffEntry>();
        private static readonly object _filterLock = new object();
        private static readonly List<PluginEntry> _lastPluginList = new List<PluginEntry>();
        private static readonly string ConfigBackupDir = Path.Combine(Path.GetDirectoryName(Config.DataPath)!, "config_backup");

        private const int StableRunResetMinutes = 5;

        public static IReadOnlyList<MinecraftServerInfo> LastScanResults
        {
            get { lock (_scanLock) { return _lastScanResults.ToList(); } }
        }
        public static string CurrentMode => _currentMode;
        /// <summary>Run模式 - 启动MC服务端作为子进程，通过stdout获取控制台，stdin发送命令</summary>
        public static bool IsRunMode => _currentMode == "RUN";
        /// <summary>Rcon模式 - 不启动服务端，仅通过RCON发送命令并显示返回信息</summary>
        public static bool IsRconMode => _currentMode == "RCON";
        /// <summary>RR模式(Run+Rcon) - 启动服务端读取stdout控制台，通过RCON发送命令</summary>
        public static bool IsRRMode => _currentMode == "RR";
        /// <summary>RM模式(Run+Management) - 启动服务端读取stdout控制台，stdin发送命令，管理协议承担事件/玩家/配置</summary>
        public static bool IsRMMode => _currentMode == "RM";
        // 兼容旧属性名
        public static bool IsOnlyRconMode => _currentMode == "RCON";
        public static bool IsManagementMode => _currentMode == "RM";
        /// <summary>是否需要启动MC服务端作为子进程(Run/RR/RM)</summary>
        public static bool NeedsRunServer => _currentMode == "RUN" || _currentMode == "RR" || _currentMode == "RM";
        /// <summary>是否使用RCON发送命令(Rcon/RR)</summary>
        public static bool UsesRconCommands => _currentMode == "RCON" || _currentMode == "RR";
        /// <summary>是否使用stdin发送命令(Run/RM)</summary>
        public static bool UsesStdinCommands => _currentMode == "RUN" || _currentMode == "RM";
        /// <summary>是否从stdout读取控制台(Run/RR/RM)</summary>
        public static bool ReadsFromStdout => _currentMode == "RUN" || _currentMode == "RR" || _currentMode == "RM";
        /// <summary>是否从日志文件读取控制台(已废弃: 所有模式均使用子进程stdout/stderr)</summary>
        public static bool ReadsFromLogFile => false;
        /// <summary>是否使用服务端管理协议(RM)</summary>
        public static bool UsesManagementProtocol => _currentMode == "RM";
        /// <summary>当前实例的管理协议连接是否已就绪</summary>
        public static bool IsManagementConnected => Rt.Management?.IsConnected == true;
        /// <summary>当前实例的管理协议连接地址</summary>
        public static string ManagementEndpoint => Rt.Management?.Endpoint ?? "";
        /// <summary>获取指定实例的管理协议连接(非 RM 或尚未连接时为 null)</summary>
        public static MCManagementConnection? GetManagementConnection(string? instanceId = null)
            => GetRuntime(instanceId).Management;

        /// <summary>按实例判断是否使用 stdin 命令通道</summary>
        internal static bool UsesStdinCommandsOf(string instanceId)
            => GetRuntime(instanceId).Mode == "RUN" || GetRuntime(instanceId).Mode == "RM";
        internal static bool UsesRconCommandsOf(string instanceId)
            => GetRuntime(instanceId).Mode == "RCON" || GetRuntime(instanceId).Mode == "RR";
        internal static bool UsesManagementProtocolOf(string instanceId)
            => GetRuntime(instanceId).Mode == "RM";

        public static void Initialize(string? instanceId = null)
        {
            var rt = GetRuntime(instanceId);
            if (!rt.Running) RefreshEntry(rt);
            rt.Mode = NormalizeMode(rt.Entry.AnalyzerMode);
        }

        /// <summary>当前选中实例是否正在运行(进程存活或远程 RCON 已连接)</summary>
        public static bool IsRunning(string? instanceId = null) => GetRuntime(instanceId).Running;

        /// <summary>当前所有运行中的实例标识</summary>
        public static IReadOnlyCollection<string> RunningInstanceIds =>
            _runtimes.Values.Where(r => r.Running).Select(r => r.Id).ToList();

        /// <summary>兼容旧属性: 当前运行实例(优先当前选中实例)</summary>
        public static string ActiveInstanceId =>
            IsRunning(Config.App.CurrentServer) ? Config.App.CurrentServer : (RunningInstanceIds.FirstOrDefault() ?? "");

        public static string CurrentModeOf(string? instanceId = null) => GetRuntime(instanceId).Mode;

        /// <summary>当前实例最新日志的绝对序号(用作"从此刻开始"的消费游标)</summary>
        internal static long LogCursor => Rt.LogCursor;

        internal static long LogCursorOf(string? instanceId = null) => GetRuntime(instanceId).LogCursor;

        /// <summary>读取指定实例序号大于 cursor 的日志增量</summary>
        internal static List<string> ReadLogLinesSince(long cursor, out long nextCursor, string? instanceId = null)
            => GetRuntime(instanceId).ReadLogLinesSince(cursor, out nextCursor);

        #region RUN Mode

        public static bool IsRunModeActive => _isRunModeActive;

        public static void StartServer(string? instanceId = null)
        {
            const string ThisProgramName = "Analyzer";

            var rt = GetRuntime(instanceId);
            if (!rt.Running) RefreshEntry(rt);
            if (rt.Mode == "RCON")
            {
                // 纯远程 RCON: 不启动本地进程, 直接连接配置中的远程服务器
                ConnectRemoteRcon(rt.Entry.RconHost, rt.Entry.RconPort, rt.Entry.RconPassword, rt.Id);
                return;
            }

            if (!ValidateStartPrerequisites(rt, out string workPath, out string flags))
                return;

            if (!EnsureEulaAgreedBeforeStart(workPath, rt.Id))
                return;

            try
            {
                rt.Process = BuildServerProcess(rt, workPath, flags);

                rt.Process.Start();
                rt.Process.BeginOutputReadLine();
                rt.Process.BeginErrorReadLine();
                rt.Stdin = rt.Process.StandardInput;
                rt.Stdin.AutoFlush = true;

                rt.Running = true;
                rt.AttachedProcessId = rt.Process.Id;
                rt.ConnectedServerName = rt.Entry.ServerName;
                // 每次启动只清零崩溃/关停标记; 重启计数在"稳定运行"后由进程监视器重置,
                // 否则自动重启会在每次 StartServer 时清零, 使最大重试次数形同虚设
                rt.CrashNotified = 0;
                rt.ShutdownInProgress = false;
                rt.StartTime = DateTime.Now;
                rt.UserInitiatedStop = false;
                rt.LogActivityCounter = 0;

                // 注册日志数据流(服务器启动即开始缓存,停止后保留供 .fx get 分析)
                rt.StreamKey = rt.Id;
                LogStreamStore.BeginServerStream(rt.StreamKey, rt.ConnectedServerName);

                rt.Cts?.Cancel();
                rt.Cts?.Dispose();
                rt.Cts = new CancellationTokenSource();
                var token = rt.Cts.Token;
                _ = Task.Run(() => MonitorServerProcess(rt, token), token);

                Output.Log(I18n.Get("anz_server_started", rt.Process.Id, rt.Mode), 1, ThisProgramName);
                EventBus.Publish(new ServerStartEvent(Config.App.GrpcPort, rt.Id));

                TriggerStartupLittleBackup();

                Output.Log(I18n.Get("anz_workdir_label", workPath), 1, ThisProgramName);
                Output.Log(I18n.Get("anz_flags_label", flags), 1, ThisProgramName);

                StartModeSpecificFollowUp(rt, workPath, ThisProgramName);

                Output.Log(I18n.Get("anz_input_stop_hint"), 1, ThisProgramName);

                Intelligence.StartAutoTips(rt.Id);
            }
            catch (Exception ex)
            {
                Output.ReportError(ex, false, I18n.Get("anz_start_server_failed"));
                CleanupRunMode(rt);
            }
        }

        /// <summary>启动前置校验: 模式/运行状态/工作目录/启动参数; 通过时输出解析后的路径与参数</summary>
        private static bool ValidateStartPrerequisites(InstanceRuntime rt, out string workPath, out string flags)
        {
            const string ThisProgramName = "Analyzer";

            workPath = rt.Entry.WorkPath;
            flags = rt.Entry.RunServerFlags;

            if (!(rt.Mode == "RUN" || rt.Mode == "RR" || rt.Mode == "RM"))
            {
                Output.Log(I18n.Get("anz_rcon_mode_cannot_start"), 2, ThisProgramName);
                return false;
            }

            if (rt.Running)
            {
                Output.Log(I18n.Get("anz_server_already_running"), 2, ThisProgramName);
                return false;
            }

            if (string.IsNullOrWhiteSpace(workPath))
            {
                workPath = FindServerPathFromScan() ?? string.Empty;
                if (string.IsNullOrEmpty(workPath))
                {
                    Output.Log(I18n.Get("anz_workpath_not_configured"), 2, ThisProgramName);
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(flags))
            {
                Output.Log(I18n.Get("anz_flags_not_configured"), 2, ThisProgramName);
                return false;
            }

            if (!Directory.Exists(workPath))
            {
                Output.Log(I18n.Get("anz_workpath_not_exists", workPath), 3, ThisProgramName);
                return false;
            }

            return true;
        }

        /// <summary>
        /// 启动前 EULA 预检查: 检查 eula.txt 并按配置自动同意或询问用户。
        /// 返回 false 表示用户明确拒绝, 调用方不应继续启动(检查本身出错不阻断启动)。
        /// </summary>
        private static bool EnsureEulaAgreedBeforeStart(string workPath, string instanceId)
        {
            const string ThisProgramName = "Analyzer";
            string eulaPath = Path.Combine(workPath, "eula.txt");
            if (!File.Exists(eulaPath)) return true;

            try
            {
                string eulaContent = File.ReadAllText(eulaPath);
                if (!eulaContent.Contains("eula=false")) return true;

                if (Config.App.AutoAgreeEula)
                {
                    eulaContent = eulaContent.Replace("eula=false", "eula=true");
                    File.WriteAllText(eulaPath, eulaContent);
                    Output.Log(I18n.Get("anz_eula_auto_agreed_config"), 1, ThisProgramName);
                    return true;
                }

                Output.Log(I18n.Get("anz_eula_not_agreed_detected"), 2, ThisProgramName);
                Output.Log(I18n.Get("anz_eula_notice"), 1, ThisProgramName);
                bool agree = AnsiConsole.Confirm(I18n.Get("anz_eula_confirm"), false);
                if (!agree)
                {
                    Output.Log(I18n.Get("anz_eula_refused_cannot_start"), 2, ThisProgramName);
                    return false;
                }

                eulaContent = eulaContent.Replace("eula=false", "eula=true");
                File.WriteAllText(eulaPath, eulaContent);
                Output.Log(I18n.Get("anz_eula_agreed"), 1, ThisProgramName);
                return true;
            }
            catch (Exception ex)
            {
                Output.Log(I18n.Get("anz_eula_check_failed", ex.Message), 2, ThisProgramName);
                return true;
            }
        }

        /// <summary>构建 MC 服务端进程对象并挂接 stdout/stderr 处理</summary>
        private static Process BuildServerProcess(InstanceRuntime rt, string workPath, string flags)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = string.IsNullOrWhiteSpace(rt.Entry.JavaPath) ? "java" : rt.Entry.JavaPath,
                    Arguments = flags,
                    WorkingDirectory = workPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                },
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, e) => OnServerStdout(rt, e);
            process.ErrorDataReceived += (_, e) => OnServerStderr(rt, e);
            return process;
        }

        /// <summary>服务端 stdout 处理(含运行中检测到 EULA 未同意时的自动处理)</summary>
        private static void OnServerStdout(InstanceRuntime rt, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                if (rt.Mode == "RUN" || rt.Mode == "RR" || rt.Mode == "RM")
                {
                    using (InstanceContext.Push(rt.Id))
                    {
                        AddToLogBuffer(rt, e.Data);
                        if (!ShouldHideConsole(rt.Id))
                            Output.Log(e.Data, 0, rt.ConnectedServerName);

                        ProcessPlayerEventLine(rt, e.Data);
                        ProcessCrashDetectionLine(rt, e.Data);
                    }
                }

                // 自动检测EULA提示 (首次启动时服务端生成eula.txt后退出)
                if (e.Data.Contains("eula=false", StringComparison.OrdinalIgnoreCase) ||
                    e.Data.Contains("You need to agree to the EULA", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(rt.Entry.WorkPath))
                    {
                        string eulaPath = Path.Combine(rt.Entry.WorkPath, "eula.txt");
                        if (File.Exists(eulaPath))
                        {
                            try
                            {
                                string content = File.ReadAllText(eulaPath);
                                if (content.Contains("eula=false"))
                                {
                                    if (Config.App.AutoAgreeEula)
                                    {
                                        // auto_agree_eula=true: 自动同意并自动重启
                                        content = content.Replace("eula=false", "eula=true");
                                        File.WriteAllText(eulaPath, content);
                                        Output.Log(I18n.Get("anz_eula_auto_agreed_restarting"), 1, "Analyzer");
                                        _ = Task.Run(async () =>
                                        {
                                            await Task.Delay(2000);
                                            StopServer(instanceId: rt.Id);
                                            await Task.Delay(1000);
                                            StartServer(rt.Id);
                                        });
                                    }
                                    else
                                    {
                                        // auto_agree_eula=false: 提示用户阅读并确认
                                        // (经 ConsoleGate 与主输入循环串行化, 避免抢夺控制台输入)
                                        _ = Task.Run(async () =>
                                        {
                                            await Task.Delay(2000); // 等待服务端退出
                                            Output.Log(I18n.Get("anz_eula_refused_closed"), 2, "Analyzer");
                                            Output.Log(I18n.Get("anz_eula_notice"), 1, "Analyzer");
                                            bool agree = ConsoleGate.InteractConfirm(I18n.Get("anz_eula_confirm"), false);
                                            if (agree)
                                            {
                                                try
                                                {
                                                    content = File.ReadAllText(eulaPath);
                                                    content = content.Replace("eula=false", "eula=true");
                                                    File.WriteAllText(eulaPath, content);
                                                    Output.Log(I18n.Get("anz_eula_agreed"), 1, "Analyzer");

                                                    bool restart = ConsoleGate.InteractConfirm(I18n.Get("anz_confirm_restart_server"), true);
                                                    if (restart)
                                                    {
                                                        StartServer(rt.Id);
                                                        Output.Log(I18n.Get("anz_server_restarted"), 1, "Analyzer");
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    Output.Log(I18n.Get("anz_eula_agree_failed", ex.Message), 2, "Analyzer");
                                                }
                                            }
                                            else
                                            {
                                                Output.Log(I18n.Get("anz_eula_refused_hint"), 2, "Analyzer");
                                            }
                                        });
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Output.Log(I18n.Get("anz_eula_handle_failed", ex.Message), 2, "Analyzer");
                            }
                        }
                    }
                }
            }
        }

        /// <summary>服务端 stderr 处理</summary>
        private static void OnServerStderr(InstanceRuntime rt, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                if (rt.Mode == "RUN" || rt.Mode == "RR" || rt.Mode == "RM")
                {
                    using (InstanceContext.Push(rt.Id))
                    {
                        AddToLogBuffer(rt, e.Data);
                        if (!ShouldHideConsole(rt.Id))
                            Output.Log(e.Data, 0, rt.ConnectedServerName);
                        ProcessPlayerEventLine(rt, e.Data);
                        ProcessCrashDetectionLine(rt, e.Data);
                    }
                }
            }
        }

        /// <summary>启动时按配置触发 Little 首次完整备份(延迟 5 秒等待文件稳定)</summary>
        private static void TriggerStartupLittleBackup()
        {
            // Little备份：服务端启动时触发首次完整备份
            if (Config.App.AutoBackupEnabled && Config.App.AutoBackupLittleEnabled)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        System.Threading.Thread.Sleep(5000); // 等待服务端文件稳定
                        Intelligence.TriggerLittleFullBackupIfNeeded();
                    }
                    catch (Exception ex) { Output.Debug(ex, "启动时 Little 完整备份失败", "Analyzer"); }
                });
            }
        }

        /// <summary>启动后的模式相关后续动作: RR 自动连 RCON / RM 连接管理协议 / Run 提示</summary>
        private static void StartModeSpecificFollowUp(InstanceRuntime rt, string workPath, string ThisProgramName)
        {
            // RR模式：启动后尝试RCON连接
            if (rt.Mode == "RR")
            {
                rt.Rcon = new RconClient();
                Output.Log(I18n.Get("anz_rr_auto_connect_rcon"), 1, ThisProgramName);
                _ = Task.Run(() =>
                {
                    // 等待服务端启动完成（最多等待5分钟）
                    for (int i = 0; i < 300; i++)
                    {
                        if (!rt.Running) break;
                        Thread.Sleep(1000);
                        try
                        {
                            if (rt.Rcon.Connect(
                                rt.Entry.RconHost,
                                rt.Entry.RconPort,
                                rt.Entry.RconPassword))
                            {
                                Output.Log(I18n.Get("anz_rcon_connected", rt.Entry.RconHost, rt.Entry.RconPort), 1, ThisProgramName);
                                break;
                            }
                        }
                        catch { }
                    }
                });
                Output.Log(I18n.Get("anz_cmd_via_rcon_hint"), 1, ThisProgramName);
            }
            else if (rt.Mode == "RUN")
            {
                Output.Log(I18n.Get("anz_cmd_hint"), 1, ThisProgramName);
            }
            else if (rt.Mode == "RM")
            {
                // RM模式: 控制台来自子进程 stdout/stderr, 管理协议仅用于事件订阅/玩家管理/全服配置
                StartRmManagement(rt, workPath);
                StartRmManagementWatchdog(rt, workPath);
                Output.Log(I18n.Get("anz_cmd_hint"), 1, ThisProgramName);
            }
        }

        /// <summary>
        /// 管理协议重连看门狗。服务端刚启动时管理端口可能尚未就绪或 server.properties 尚未写出,
        /// 初次连接会失败; 这里定期重新拉起连接循环, 确保后续能自动重连(RM 模式下信息获取随之恢复)。
        /// </summary>
        private static void StartRmManagementWatchdog(InstanceRuntime rt, string workPath)
        {
            _ = Task.Run(async () =>
            {
                while (rt.Running)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
                    catch { break; }
                    if (!rt.Running) break;
                    // 连接循环仍在运行(内部已带指数退避重连)时无需干预
                    if (rt.Management != null && rt.Management.IsRunning) continue;
                    StartRmManagement(rt, workPath, quiet: true);
                }
            });
        }
        /// <summary>StopServer 结果</summary>
        public enum StopResult
        {
            NotRunning,     // 服务端未运行(子进程模式)
            Sent,           // stop 已发送, 后台监测中(asyncReturn=true)
            Stopped,        // 进程已退出
            Unresponsive    // 无响应(进程未退出), 交由用户决定是否强杀
        }

        /// <summary>
        /// 停止服务端(带无响应看门狗)。
        /// prompt: 无响应时在控制台交互确认是否强制关闭(仅 .server stop 交互路径)。
        /// killOnTimeout: 无响应时是否自动强杀兜底(内部自动流程保持 true; 面板/CLI 用户路径为 false, 由用户决定)。
        /// asyncReturn: true 时发送 stop 后立即返回 Sent, 看门狗在后台执行(面板路径, 避免阻塞 gRPC)。
        /// </summary>
        public static StopResult StopServer(bool prompt = false, bool killOnTimeout = true, bool asyncReturn = false, string? instanceId = null)
        {
            const string ThisProgramName = "Analyzer";
            var rt = GetRuntime(instanceId);

            if (!rt.Running)
            {
                Output.Log(I18n.Get("anz_server_not_running"), 1, ThisProgramName);
                return StopResult.NotRunning;
            }

            // 纯远程 RCON: 只断开连接, 不向远程服务器发送 stop
            if (rt.Mode == "RCON")
            {
                Detach(rt.Id);
                Output.Log(I18n.Get("anz_rcon_disconnected"), 1, ThisProgramName);
                return StopResult.Stopped;
            }

            rt.UserInitiatedStop = true;
            long baseline = Interlocked.Read(ref rt.LogActivityCounter);

            try
            {
                if (rt.Stdin != null)
                {
                    rt.Stdin.WriteLine("stop");
                    rt.Stdin.Flush();
                }
            }
            catch (Exception ex)
            {
                Output.Log(I18n.Get("anz_stop_server_error", ex.Message), 2, ThisProgramName);
            }

            if (asyncReturn)
            {
                long taskBaseline = baseline;
                _ = Task.Run(() => WatchStopResponse(rt, taskBaseline, prompt, killOnTimeout));
                return StopResult.Sent;
            }

            return WatchStopResponse(rt, baseline, prompt, killOnTimeout);
        }

        /// <summary>
        /// 等待 stop 响应: 进程退出即成功;
        /// 前 10 秒完全无新日志流 → 提前判定无响应; 日志仍流动但 30 秒未退出 → 判定无响应(慢关闭)。
        /// </summary>
        private static StopResult WatchStopResponse(InstanceRuntime rt, long baseline, bool prompt, bool killOnTimeout)
        {
            const int GraceSeconds = 30;          // 最长等待进程退出
            const int SilentDetectSeconds = 10;   // 完全无日志流时提前判定无响应的阈值

            var sw = Stopwatch.StartNew();
            bool hadLogActivity = false;
            while (sw.Elapsed.TotalSeconds < GraceSeconds)
            {
                var proc = rt.Process;
                if (proc == null || proc.HasExited)
                {
                    CleanupRunMode(rt);
                    Intelligence.StopAutoTips(rt.Id);
                    return StopResult.Stopped;
                }
                if (Interlocked.Read(ref rt.LogActivityCounter) != baseline)
                    hadLogActivity = true;
                else if (sw.Elapsed.TotalSeconds >= SilentDetectSeconds)
                    return HandleStopUnresponsive(rt, hadLogActivity: false, (int)sw.Elapsed.TotalSeconds, prompt, killOnTimeout);
                Thread.Sleep(250);
            }
            return HandleStopUnresponsive(rt, hadLogActivity, (int)sw.Elapsed.TotalSeconds, prompt, killOnTimeout);
        }

        /// <summary>stop 无响应处理: 发布事件 + 控制台警告; 按调用方决定交互确认/自动强杀/交由用户在事件板处理</summary>
        private static StopResult HandleStopUnresponsive(InstanceRuntime rt, bool hadLogActivity, int waitedSeconds, bool prompt, bool killOnTimeout)
        {
            Output.Log(I18n.Get("anz_stop_unresponsive", waitedSeconds,
                hadLogActivity ? I18n.Get("anz_stop_unresponsive_slow") : I18n.Get("anz_stop_unresponsive_silent")), 2, "Analyzer");
            EventBus.Publish(new ServerStopUnresponsiveEvent(rt.Id, hadLogActivity, waitedSeconds));

            if (prompt)
            {
                // CLI 交互路径: 用户自行决定是否强制关闭(拒绝则保留进程)
                bool force = AnsiConsole.Confirm(I18n.Get("prog_stop_confirm_force"), false);
                if (force)
                {
                    ForceKillServer(rt.Id);
                    return StopResult.Stopped;
                }
                Output.Log(I18n.Get("prog_stop_declined_hint"), 1, "Analyzer");
                return StopResult.Unresponsive;
            }

            if (killOnTimeout)
            {
                // 内部自动流程(restart/计划任务/重载等): 保持强杀兜底
                Output.Log(I18n.Get("anz_stop_force_killed"), 2, "Analyzer");
                ForceKillServer(rt.Id);
                return StopResult.Stopped;
            }

            // 面板路径: 不自动杀, 事件板会收到无响应事件, 由用户手动强制关闭
            return StopResult.Unresponsive;
        }

        /// <summary>是否存在本地托管子进程(可强制关闭)</summary>
        public static bool HasManagedProcess => HasManagedProcessOf(null);

        public static bool HasManagedProcessOf(string? instanceId)
        {
            var p = GetRuntime(instanceId).Process;
            return p != null && !p.HasExited;
        }

        /// <summary>强制结束服务端进程树(可能丢失未保存数据, 须经用户确认后调用)</summary>
        public static void ForceKillServer(string? instanceId = null)
        {
            const string ThisProgramName = "Analyzer";
            var rt = GetRuntime(instanceId);
            var proc = rt.Process;
            if (proc == null || proc.HasExited)
            {
                Output.Log(I18n.Get("anz_force_no_process"), 2, ThisProgramName);
                return;
            }

            rt.UserInitiatedStop = true;
            try
            {
                Output.Log(I18n.Get("anz_force_killing", proc.Id), 2, ThisProgramName);
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Output.Log(I18n.Get("anz_stop_server_error", ex.Message), 2, ThisProgramName);
            }

            CleanupRunMode(rt);
            Intelligence.StopAutoTips(rt.Id);
        }

        /// <summary>
        /// 程序退出时停止所有运行中的实例(多实例并发场景)。
        /// 统一发送 stop 后共享一段宽限时间, 仍未退出的进程直接强杀, 避免留下孤儿进程。
        /// </summary>
        public static void StopAllInstances()
        {
            var ids = RunningInstanceIds.ToList();
            if (ids.Count == 0) return;

            foreach (var id in ids)
            {
                try
                {
                    var rt = GetRuntime(id);
                    if (rt.Mode == "RCON")
                    {
                        Detach(id);
                        continue;
                    }
                    rt.UserInitiatedStop = true;
                    if (rt.Stdin != null)
                    {
                        rt.Stdin.WriteLine("stop");
                        rt.Stdin.Flush();
                    }
                }
                catch { }
            }

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 15)
            {
                bool anyAlive = ids.Any(id =>
                {
                    var p = GetRuntime(id).Process;
                    return p != null && !p.HasExited;
                });
                if (!anyAlive) return;
                Thread.Sleep(250);
            }

            foreach (var id in ids)
            {
                try
                {
                    var p = GetRuntime(id).Process;
                    if (p != null && !p.HasExited) p.Kill(entireProcessTree: true);
                }
                catch { }
            }
        }

        private static void MonitorServerProcess(InstanceRuntime rt, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (rt.Process == null || rt.Process.HasExited)
                    {
                        bool hadProcess = rt.Process != null;
                        int exitCode = rt.Process?.ExitCode ?? -1;
                        using (InstanceContext.Push(rt.Id))
                        {
                            Output.Log(I18n.Get("anz_server_exited", exitCode), 0, "Analyzer");
                            CleanupRunMode(rt);
                            Intelligence.StopAutoTips(rt.Id);
                        }

                        if (!hadProcess) break;

                        // 计划内关停(用户 stop / 收到正常停服序列)或退出码为 0 视为正常停止;
                        // 其余非零退出视为崩溃。崩溃事件在同一次运行内只发布一次。
                        bool plannedShutdown = rt.UserInitiatedStop || rt.ShutdownInProgress;
                        bool crashed = !plannedShutdown && exitCode != 0;

                        if (crashed)
                        {
                            NotifyServerCrashOnce(exitCode, null, rt.Id);
                        }
                        else
                        {
                            rt.RestartAttemptCount = 0;
                            EventBus.Publish(new ServerStopEvent(rt.Id));
                            Output.Log(I18n.Get("anz_server_stopped"), 1, "Analyzer");
                        }

                        // 自动重启逻辑(仅真实崩溃)
                        if (crashed && rt.Entry.AutoRestart && (rt.Mode == "RUN" || rt.Mode == "RR" || rt.Mode == "RM"))
                        {
                            // 稳定运行超过阈值后视为新的故障序列, 重置重试计数
                            if (rt.StartTime != DateTime.MinValue &&
                                (DateTime.Now - rt.StartTime).TotalMinutes >= StableRunResetMinutes)
                            {
                                rt.RestartAttemptCount = 0;
                            }

                            int maxRetries = rt.Entry.AutoRestartMaxRetries;
                            if (maxRetries == 0 || rt.RestartAttemptCount < maxRetries)
                            {
                                rt.RestartAttemptCount++;
                                EventBus.Publish(new AutoRestartEvent(rt.Id, rt.RestartAttemptCount, maxRetries));
                                string attemptTotal = maxRetries > 0 ? "/" + maxRetries : "";
                                Output.Log(I18n.Get("anz_auto_restart", rt.RestartAttemptCount, attemptTotal), 1, "Analyzer");
                                Thread.Sleep(5000);
                                if (!rt.UserInitiatedStop)
                                {
                                    StartServer(rt.Id);
                                }
                            }
                            else
                            {
                                Output.Log(I18n.Get("anz_max_retries_reached", maxRetries), 2, "Analyzer");
                                rt.RestartAttemptCount = 0;
                            }
                        }
                        break;
                    }
                    Thread.Sleep(500);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private static void CleanupRunMode(InstanceRuntime rt)
        {
            lock (rt.CommandLock)
            {
                rt.Cts?.Cancel();
                rt.Cts?.Dispose();
                rt.Cts = null;

                rt.Stdin?.Dispose();
                rt.Stdin = null;

                if (rt.Process != null)
                {
                    try
                    {
                        if (!rt.Process.HasExited)
                        {
                            rt.Process.CancelOutputRead();
                            rt.Process.CancelErrorRead();
                        }
                    }
                    catch { }

                    try { rt.Process.Dispose(); } catch { }
                    rt.Process = null;
                }

                rt.Running = false;
                rt.AttachedProcessId = 0;
                rt.AttachedWindowTitle = "";
                rt.ConnectedServerName = "";

                // 断开该实例的管理协议连接(RM模式)
                rt.Management?.Stop();
                rt.Management = null;

                // 标记日志流停止(流与数据保留, .fx get 仍可分析异常关闭前的日志)
                if (!string.IsNullOrEmpty(rt.StreamKey))
                    LogStreamStore.SetLive(rt.StreamKey, false);
            }
        }

        private static string? FindServerPathFromScan()
        {
            var servers = ScanMinecraftServers();
            if (servers.Count > 0)
            {
                string? jarPath = servers[0].JarPath;
                if (!string.IsNullOrEmpty(jarPath))
                {
                    if (Path.IsPathRooted(jarPath))
                        return Path.GetDirectoryName(jarPath);

                    string? workDir = GetProcessWorkingDirectory(servers[0].ProcessId);
                    if (!string.IsNullOrEmpty(workDir))
                        return workDir;
                }
            }
            return null;
        }

        #endregion

        #region RCON Mode

        public static void ScanAndListServers()
        {
            string ThisProgramName = "Analyzer";
            Output.Log(I18n.Get("anz_scanning_servers"), 1, ThisProgramName);

            lock (_scanLock)
            {
                _lastScanResults = ScanMinecraftServers();
            }
            if (_lastScanResults.Count == 0)
            {
                Output.Log(I18n.Get("anz_no_servers_found"), 2, ThisProgramName);
                return;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn(I18n.Get("anz_col_index"), c => c.Alignment(Justify.Center).Width(6))
                .AddColumn(I18n.Get("anz_col_pid"), c => c.Alignment(Justify.Center).Width(10))
                .AddColumn(I18n.Get("anz_col_window_title"), c => c.Width(30))
                .AddColumn(I18n.Get("anz_col_jar_path"), c => c.Width(50));

            int index = 1;
            foreach (var server in _lastScanResults)
            {
                table.AddRow(
                    index.ToString(),
                    server.ProcessId.ToString(),
                    Markup.Escape(server.WindowTitle),
                    Markup.Escape(server.JarPath)
                );
                index++;
            }

            AnsiConsole.Write(table);
            Output.Log(I18n.Get("anz_servers_found_count", _lastScanResults.Count), 1, ThisProgramName);
        }

        public static void ConnectToServer(int index)
        {
            string ThisProgramName = "Analyzer";

            if (!IsRconMode)
            {
                Output.Log(I18n.Get("anz_connect_mode_unsupported"), 2, ThisProgramName);
                return;
            }

            List<MinecraftServerInfo> scanResults;
            lock (_scanLock) { scanResults = _lastScanResults; }

            if (scanResults.Count == 0)
            {
                Output.Log(I18n.Get("anz_no_scan_results"), 2, ThisProgramName);
                return;
            }

            if (index < 1 || index > scanResults.Count)
            {
                Output.Log(I18n.Get("anz_invalid_index", scanResults.Count), 2, ThisProgramName);
                return;
            }

            var selectedServer = scanResults[index - 1];
            AttachToServerRcon(selectedServer, index.ToString(), Rt.Id);
            Intelligence.StartAutoTips(Rt.Id);
        }

        public static void ConnectToServerByPid(int pid)
        {
            string ThisProgramName = "Analyzer";

            if (!IsRconMode)
            {
                Output.Log(I18n.Get("anz_connect_mode_unsupported"), 2, ThisProgramName);
                return;
            }

            MinecraftServerInfo? server;
            lock (_scanLock) { server = _lastScanResults.FirstOrDefault(s => s.ProcessId == pid); }
            if (server != null)
            {
                AttachToServerRcon(server, $"PID:{pid}", Rt.Id);
                return;
            }

            try
            {
                var process = Process.GetProcessById(pid);
                var tempServer = new MinecraftServerInfo
                {
                    ProcessId = pid,
                    ProcessName = process.ProcessName,
                    WindowTitle = process.MainWindowTitle ?? $"java (PID: {pid})",
                    JarPath = $"PID:{pid}",
                    CommandLine = ""
                };
                AttachToServerRcon(tempServer, $"PID:{pid}", Rt.Id);
                Intelligence.StartAutoTips(Rt.Id);
            }
            catch (Exception ex)
            {
                Output.Log(I18n.Get("anz_connect_pid_failed", pid, ex.Message), 3, ThisProgramName);
            }
        }

        private static void AttachToServerRcon(MinecraftServerInfo server, string serverName, string? instanceId = null)
        {
            string ThisProgramName = "Analyzer";
            var rt = GetRuntime(instanceId);
            lock (_attachLock)
            {
                DetachCore(rt);

                try
                {
                    rt.AttachedProcessId = server.ProcessId;
                    rt.AttachedWindowTitle = server.WindowTitle;
                    rt.ConnectedServerName = serverName;
                    rt.Mode = "RCON";

                    // RCON模式: 控制台来源仅为 RCON 命令/响应, 不跟随任何日志文件
                    rt.Rcon = new RconClient();
                    bool rconConnected = false;
                    try
                    {
                        rconConnected = rt.Rcon.Connect(
                            rt.Entry.RconHost,
                            rt.Entry.RconPort,
                            rt.Entry.RconPassword);
                    }
                    catch (Exception ex)
                    {
                        Output.Log(I18n.Get("anz_rcon_connect_failed", ex.Message), 2, ThisProgramName);
                    }

                    rt.Running = rconConnected;
                    rt.StreamKey = rt.Id;
                    LogStreamStore.BeginServerStream(rt.StreamKey, serverName);

                    Output.Log(I18n.Get("anz_attached_server", Path.GetFileName(server.JarPath), server.ProcessId), 1, ThisProgramName);
                    if (rconConnected)
                    {
                        Output.Log(I18n.Get("anz_rcon_connected", rt.Entry.RconHost, rt.Entry.RconPort), 1, ThisProgramName);
                    }
                    else
                    {
                        Output.Log(I18n.Get("anz_rcon_not_connected"), 2, ThisProgramName);
                    }
                    Output.Log(I18n.Get("anz_cmd_hint"), 1, ThisProgramName);
                    Output.Log(I18n.Get("anz_detach_hint"), 1, ThisProgramName);
                }
                catch (Exception ex)
                {
                    rt.AttachedProcessId = 0;
                    rt.AttachedWindowTitle = "";
                    rt.ConnectedServerName = "";
                    rt.Running = false;
                    Output.ReportError(ex, false, I18n.Get("anz_connect_server_failed"));
                }
            }
        }

        /// <summary>
        /// RCON 纯远程连接: 不依赖本机扫描/PID, 直接用 host/port/password 连接任意远程服务器。
        /// 控制台来源为 RCON 命令与响应。
        /// </summary>
        public static bool ConnectRemoteRcon(string host, int port, string password, string? instanceId = null)
        {
            const string ThisProgramName = "Analyzer";
            var rt = GetRuntime(instanceId);
            if (!rt.Running) RefreshEntry(rt);

            if (string.IsNullOrWhiteSpace(host) || port <= 0 || string.IsNullOrWhiteSpace(password))
            {
                Output.Log(I18n.Get("anz_rcon_remote_incomplete"), 2, ThisProgramName);
                return false;
            }

            // 已以本地子进程模式运行时不允许直接改成纯远程连接, 避免丢失进程/stdin 跟踪
            if (rt.Running && (rt.Mode == "RUN" || rt.Mode == "RR" || rt.Mode == "RM"))
            {
                Output.Log(I18n.Get("anz_rcon_remote_busy"), 2, ThisProgramName);
                return false;
            }

            lock (rt.CommandLock)
            {
                try
                {
                    rt.Rcon?.Disconnect();
                    rt.Rcon = new RconClient();
                    Output.Log(I18n.Get("anz_rcon_remote_connecting", host, port), 1, ThisProgramName);
                    if (!rt.Rcon.Connect(host, port, password))
                    {
                        Output.Log(I18n.Get("anz_rcon_remote_failed", host, port), 2, ThisProgramName);
                        rt.Running = false;
                        return false;
                    }

                    rt.Mode = "RCON";
                    rt.Running = true;
                    rt.AttachedProcessId = 0;
                    rt.AttachedWindowTitle = "";
                    rt.ConnectedServerName = rt.Entry.ServerName;
                    rt.StartTime = DateTime.Now;
                    rt.StreamKey = rt.Id;
                    LogStreamStore.BeginServerStream(rt.StreamKey, rt.ConnectedServerName);
                    Output.Log(I18n.Get("anz_rcon_remote_connected", host, port), 1, ThisProgramName);
                    EventBus.Publish(new ServerStartEvent(Config.App.GrpcPort, rt.Id));
                    return true;
                }
                catch (Exception ex)
                {
                    rt.Running = false;
                    Output.Log(I18n.Get("anz_rcon_remote_failed_detail", host, port, ex.Message), 2, ThisProgramName);
                    return false;
                }
            }
        }

        // ===== RM 模式: 服务端管理协议(MC 1.21.9+) 接入 =====

        /// <summary>
        /// 启动服务端管理协议连接(RM模式)。连接参数优先取实例配置, 缺失时回退读取 server.properties。
        /// </summary>
        private static void StartRmManagement(InstanceRuntime rt, string workPath, bool quiet = false)
        {
            try
            {
                if (rt.Management != null && rt.Management.IsConnected) return;

                rt.Management?.Stop();
                rt.Management = new MCManagementConnection();
                string capturedId = rt.Id;
                rt.Management.NotificationReceived += (method, prms) =>
                {
                    using (InstanceContext.Push(capturedId))
                        OnMgmtNotification(capturedId, method, prms);
                };

                var props = ReadServerProperties(workPath);

                bool enabled = string.Equals(GetProp(props, "management-server-enabled", "false"), "true", StringComparison.OrdinalIgnoreCase);
                string host = rt.Entry.ManagementHost;
                if (string.IsNullOrWhiteSpace(host)) host = "localhost";
                string propHost = GetProp(props, "management-server-host", "");
                if (!string.IsNullOrWhiteSpace(propHost) && (host == "localhost" || string.IsNullOrWhiteSpace(host)))
                    host = propHost;

                int port = rt.Entry.ManagementPort;
                if (port <= 0) int.TryParse(GetProp(props, "management-server-port", "0"), out port);

                string secret = rt.Entry.ManagementSecret;
                if (string.IsNullOrWhiteSpace(secret)) secret = GetProp(props, "management-server-secret", "");

                bool tls = rt.Entry.ManagementTlsEnabled;

                if (!enabled && string.IsNullOrWhiteSpace(secret) && port <= 0)
                {
                    if (!quiet) Output.Log(I18n.Get("anz_mgmt_not_enabled"), 1, ThisProgramName);
                    return;
                }
                if (port <= 0)
                {
                    if (!quiet) Output.Log(I18n.Get("anz_mgmt_no_port"), 2, ThisProgramName);
                    return;
                }
                if (string.IsNullOrWhiteSpace(secret))
                {
                    if (!quiet) Output.Log(I18n.Get("anz_mgmt_no_secret"), 2, ThisProgramName);
                    return;
                }

                if (!quiet) Output.Log(I18n.Get("anz_mgmt_connecting", host, port), 1, ThisProgramName);
                rt.Management.Start(host, port, secret, tls);
            }
            catch (Exception ex)
            {
                if (!quiet) Output.Log(I18n.Get("anz_mgmt_start_failed", ex.Message), 2, ThisProgramName);
            }
        }

        private static string ThisProgramName => "Analyzer";

        /// <summary>读取 server.properties 为键值字典(不存在时返回空字典)</summary>
        private static Dictionary<string, string> ReadServerProperties(string workPath)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string file = Path.Combine(workPath, "server.properties");
                if (!File.Exists(file)) return dict;
                foreach (var line in File.ReadAllLines(file))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;
                    int idx = trimmed.IndexOf('=');
                    if (idx <= 0) continue;
                    dict[trimmed.Substring(0, idx).Trim()] = trimmed.Substring(idx + 1).Trim();
                }
            }
            catch { }
            return dict;
        }

        private static string GetProp(Dictionary<string, string> props, string key, string def)
            => props.TryGetValue(key, out var v) ? v : def;

        /// <summary>管理协议通知 → EventBus 事件(RM 事件需面板开关 showRmEvents 开启, 默认开启)</summary>
        private static void OnMgmtNotification(string instanceId, string method, JsonElement prms)
        {
            try
            {
                if (!PanelDataManager.GetBool("showRmEvents", true)) return;
                string m = method ?? "";
                string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                if (m.EndsWith("operators/added", StringComparison.OrdinalIgnoreCase))
                {
                    EventBus.Publish(new PlayerOpEvent(ExtractPlayerName(prms), true, ExtractString(prms, "source"), time));
                }
                else if (m.EndsWith("operators/removed", StringComparison.OrdinalIgnoreCase))
                {
                    EventBus.Publish(new PlayerOpEvent(ExtractPlayerName(prms), false, ExtractString(prms, "source"), time));
                }
                else if (m.EndsWith("bans/added", StringComparison.OrdinalIgnoreCase))
                {
                    EventBus.Publish(new PlayerBanEvent(ExtractPlayerName(prms), "ban", true, ExtractString(prms, "reason"), time));
                }
                else if (m.EndsWith("bans/removed", StringComparison.OrdinalIgnoreCase))
                {
                    EventBus.Publish(new PlayerBanEvent(ExtractPlayerName(prms), "ban", false, "", time));
                }
                else if (m.EndsWith("ip_bans/added", StringComparison.OrdinalIgnoreCase))
                {
                    EventBus.Publish(new PlayerBanEvent(ExtractIp(prms), "ip_ban", true, ExtractString(prms, "reason"), time));
                }
                else if (m.EndsWith("ip_bans/removed", StringComparison.OrdinalIgnoreCase))
                {
                    EventBus.Publish(new PlayerBanEvent(ExtractIp(prms), "ip_ban", false, "", time));
                }
            }
            catch (Exception ex)
            {
                Output.Log($"管理协议事件处理异常: {ex.Message}", 2, "Analyzer");
            }
        }

        /// <summary>递归查找通知参数中的玩家名(优先 player.name)</summary>
        private static string ExtractPlayerName(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    if (el.TryGetProperty("player", out var p) && p.ValueKind == JsonValueKind.Object)
                    {
                        string r = ExtractPlayerName(p);
                        if (!string.IsNullOrEmpty(r)) return r;
                    }
                    if (el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                        return n.GetString() ?? "";
                    foreach (var prop in el.EnumerateObject())
                    {
                        string r = ExtractPlayerName(prop.Value);
                        if (!string.IsNullOrEmpty(r)) return r;
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray())
                    {
                        string r = ExtractPlayerName(item);
                        if (!string.IsNullOrEmpty(r)) return r;
                    }
                    break;
            }
            return "";
        }

        /// <summary>递归查找通知参数中的 IP(优先 ip 字段, 其次裸字符串)</summary>
        private static string ExtractIp(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    if (el.TryGetProperty("ip", out var ip) && ip.ValueKind == JsonValueKind.String)
                        return ip.GetString() ?? "";
                    foreach (var prop in el.EnumerateObject())
                    {
                        string r = ExtractIp(prop.Value);
                        if (!string.IsNullOrEmpty(r)) return r;
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray())
                    {
                        string r = ExtractIp(item);
                        if (!string.IsNullOrEmpty(r)) return r;
                    }
                    break;
                case JsonValueKind.String:
                    return el.GetString() ?? "";
            }
            return "";
        }

        /// <summary>递归查找通知参数中的字符串字段</summary>
        private static string ExtractString(JsonElement el, string field)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    if (el.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString() ?? "";
                    foreach (var prop in el.EnumerateObject())
                    {
                        string r = ExtractString(prop.Value, field);
                        if (!string.IsNullOrEmpty(r)) return r;
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray())
                    {
                        string r = ExtractString(item, field);
                        if (!string.IsNullOrEmpty(r)) return r;
                    }
                    break;
            }
            return "";
        }

        #endregion

        #region Common

        public static bool IsAttached => Rt.Running && (Rt.AttachedProcessId != 0 || Rt.Mode == "RCON");

        /// <summary>
        /// 获取指定实例的 MC 服务端进程。
        /// Run/RR/RM 返回托管子进程; Attach/RCON 模式通过 attached PID 获取(纯远程 RCON 无本地进程)。
        /// </summary>
        public static Process? GetServerProcess(string? instanceId = null)
        {
            var rt = GetRuntime(instanceId);

            // Run/RR/RM 模式：直接返回启动的进程
            if (rt.Process != null && !rt.Process.HasExited)
                return rt.Process;

            // Attach/RCON 模式：通过 PID 获取
            if (rt.AttachedProcessId != 0)
            {
                try
                {
                    var proc = Process.GetProcessById(rt.AttachedProcessId);
                    if (proc != null && !proc.HasExited)
                        return proc;
                }
                catch { }
            }

            return null;
        }

        /// <summary>是否在 CLI 控制台隐藏该实例的服务端输出(按实例配置判断, 多实例互不影响)</summary>
        private static bool ShouldHideConsole(string? instanceId = null)
        {
            var hideList = Config.App.HideConsoleServers;
            if (hideList == null || hideList.Count == 0)
                return false;
            return hideList.Contains(instanceId ?? Config.App.CurrentServer);
        }

        public static void SendCommand(string command, string? instanceId = null)
        {
            var rt = GetRuntime(instanceId);
            using (InstanceContext.Push(rt.Id))
            {
                lock (rt.CommandLock)
                {
                    if (rt.Mode == "RUN" || rt.Mode == "RM")
                    {
                        SendCommandRunMode(rt, command);
                    }
                    else if (rt.Mode == "RCON" || rt.Mode == "RR")
                    {
                        SendCommandRconMode(rt, command);
                    }
                }
            }
        }

        private static void SendCommandRunMode(InstanceRuntime rt, string command)
        {
            string ThisProgramName = "Analyzer";

            if (!rt.Running || rt.Stdin == null)
            {
                Output.Log(I18n.Get("anz_server_not_running_start_first"), 2, ThisProgramName);
                return;
            }

            try
            {
                rt.Stdin.WriteLine(command);
                rt.Stdin.Flush();
                Output.Log($"> {command}", 1, rt.ConnectedServerName);
            }
            catch (Exception ex)
            {
                Output.Log(I18n.Get("anz_send_cmd_failed", ex.Message), 3, ThisProgramName);
            }
        }

        private static void SendCommandRconMode(InstanceRuntime rt, string command)
        {
            string ThisProgramName = "Analyzer";

            if (rt.Rcon == null || !rt.Rcon.IsConnected)
            {
                Output.Log(I18n.Get("anz_rcon_not_connected_send"), 2, ThisProgramName);
                return;
            }

            try
            {
                string response = rt.Rcon.SendCommand(command);
                // RCON 模式控制台来源为命令与响应: 写入实例日志缓冲, 供面板控制台/分析使用
                AddToLogBuffer(rt, $"> {command}");
                if (!string.IsNullOrWhiteSpace(response))
                    AddToLogBuffer(rt, response);

                Output.Log($"> {command}", 1, rt.ConnectedServerName);
                if (!string.IsNullOrWhiteSpace(response))
                {
                    Output.Log(Markup.Escape(response), 1, rt.ConnectedServerName);
                }
            }
            catch (Exception ex)
            {
                Output.Log(I18n.Get("anz_rcon_send_cmd_failed", ex.Message), 3, ThisProgramName);
            }
        }

        // ===== 命令发送 + 响应捕获(用于 tps/list 等需要解析返回结果的命令) =====

        /// <summary>
        /// 发送命令到 MC 服务端并捕获响应行。
        /// RCON 模式直接返回响应；Run 模式从日志缓冲区收集命令执行后产生的新行。
        /// </summary>
        /// <param name="command">要发送的命令(不含前导 /)</param>
        /// <param name="timeoutMs">等待响应的超时时间(毫秒)</param>
        /// <param name="silent">静默模式: 不打印 "> 命令" 与响应行到控制台(用于 tps/list 等内部信息采集)</param>
        /// <returns>捕获到的响应行列表</returns>
        public static List<string> SendCommandWithCapture(string command, int timeoutMs = 3000, bool silent = false, string? instanceId = null)
        {
            var result = new List<string>();
            var rt = GetRuntime(instanceId);
            using (InstanceContext.Push(rt.Id))
            {
                lock (rt.CommandLock)
                {
                    if ((rt.Mode == "RCON" || rt.Mode == "RR") && rt.Rcon != null && rt.Rcon.IsConnected)
                    {
                        // RCON 模式：SendCommand 直接返回响应字符串
                        try
                        {
                            string response = rt.Rcon.SendCommand(command);
                            AddToLogBuffer(rt, $"> {command}");
                            if (!string.IsNullOrWhiteSpace(response))
                                AddToLogBuffer(rt, response);
                            if (!silent)
                            {
                                Output.Log($"> {command}", 1, rt.ConnectedServerName);
                                if (!string.IsNullOrWhiteSpace(response))
                                    Output.Log(Markup.Escape(response), 1, rt.ConnectedServerName);
                            }
                            if (!string.IsNullOrWhiteSpace(response))
                            {
                                // RCON 响应可能是多行的
                                foreach (var line in response.Replace("\r\n", "\n").Split('\n'))
                                {
                                    if (!string.IsNullOrWhiteSpace(line))
                                        result.Add(line);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Output.Log(I18n.Get("anz_rcon_send_cmd_failed", ex.Message), 3, "Analyzer");
                        }
                        return result;
                    }

                    if ((rt.Mode == "RUN" || rt.Mode == "RM") && rt.Running && rt.Stdin != null)
                    {
                        // Run/RM 模式：写入 stdin，然后按绝对序号从该实例日志缓冲区收集新增行
                        long cursor = rt.LogCursor;

                        try
                        {
                            rt.Stdin.WriteLine(command);
                            rt.Stdin.Flush();
                            if (!silent)
                                Output.Log($"> {command}", 1, rt.ConnectedServerName);
                        }
                        catch (Exception ex)
                        {
                            Output.Log(I18n.Get("anz_send_cmd_failed", ex.Message), 3, "Analyzer");
                            return result;
                        }

                        // 轮询日志缓冲区等待响应
                        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                        bool gotAny = false;
                        while (DateTime.UtcNow < deadline)
                        {
                            Thread.Sleep(100);
                            var newLines = rt.ReadLogLinesSince(cursor, out cursor);
                            if (newLines.Count > 0)
                                result.AddRange(newLines);
                            if (result.Count > 0)
                            {
                                if (!gotAny)
                                {
                                    // 第一次收到响应，再等 300ms 收集完整输出
                                    gotAny = true;
                                    Thread.Sleep(300);
                                    continue;
                                }
                                break;
                            }
                        }
                        return result;
                    }
                }
            }
            return result;
        }

        // ===== TPS 查询(发送 tps 命令解析返回) =====

        private static Regex? _tpsCustomRegex;
        private static string? _tpsCustomPattern;
        private static Regex? _listCustomRegex;
        private static string? _listCustomPattern;

        /// <summary>
        /// 编译 regex_settings 中的自定义解析正则(带匹配超时)。
        /// 同一 pattern 只编译/告警一次; 留空或非法返回 null, 由调用方回退内置规则。
        /// </summary>
        private static Regex? GetConfiguredRegex(ref Regex? cache, ref string? cachedPattern, string? pattern, string name)
        {
            if (cachedPattern == pattern) return cache;
            cachedPattern = pattern;
            cache = null;
            if (string.IsNullOrWhiteSpace(pattern)) return null;
            try
            {
                cache = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase,
                    TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException ex)
            {
                Output.Log($"regex_settings 中 {name} 的正则无效，已回退内置规则: {ex.Message}", 2, "Analyzer");
            }
            return cache;
        }

        /// <summary>按配置正则解析 TPS: 3 个捕获组视为 1m/5m/15m, 1 个捕获组视为单值</summary>
        private static bool TryParseTpsByRegex(Regex regex, string line, out double t1m, out double t5m, out double t15m)
        {
            t1m = t5m = t15m = -1;
            if (string.IsNullOrEmpty(line)) return false;

            Match m;
            try { m = regex.Match(line); }
            catch (RegexMatchTimeoutException) { return false; }
            if (!m.Success) return false;

            int groups = m.Groups.Count - 1;
            if (groups >= 3)
            {
                if (!double.TryParse(m.Groups[1].Value, out var a)) return false;
                if (!double.TryParse(m.Groups[2].Value, out var b)) return false;
                if (!double.TryParse(m.Groups[3].Value, out var c)) return false;
                t1m = a; t5m = b; t15m = c;
                return true;
            }
            if (groups == 1)
            {
                if (!double.TryParse(m.Groups[1].Value, out var single)) return false;
                t1m = t5m = t15m = single;
                return true;
            }
            return false;
        }

        /// <summary>按配置正则解析在线人数: 需要 2 个捕获组(count/max)</summary>
        private static bool TryParsePlayerCountByRegex(Regex regex, string line, out int count, out int max)
        {
            count = max = -1;
            if (string.IsNullOrEmpty(line)) return false;

            Match m;
            try { m = regex.Match(line); }
            catch (RegexMatchTimeoutException) { return false; }
            if (!m.Success || m.Groups.Count - 1 < 2) return false;
            if (!int.TryParse(m.Groups[1].Value, out var c)) return false;
            if (!int.TryParse(m.Groups[2].Value, out var mx)) return false;
            count = c; max = mx;
            return true;
        }

        /// <summary>
        /// 发送 tps 命令并解析返回结果。
        /// 指令/正则/开关均由 regex_settings.yml 的 server_query.tps 控制;
        /// 配置正则为空或非法时回退 MCConsole 内置规则(Spark 多值优先, 旧式单值兜底)。
        /// 带节流缓存(间隔由 interval_seconds 配置)，避免频繁发送命令。
        /// </summary>
        public static (double Tps1m, double Tps5m, double Tps15m) QueryTps(int timeoutMs = 3000, string? instanceId = null)
        {
            // 配置关闭时不发送指令, 返回未知值(不读缓存、不更新节流时间)
            var cfg = ContentManager.Regex?.ServerQuery?.Tps;
            if (cfg == null || !cfg.Enabled)
                return (-1, -1, -1);

            var rt = GetRuntime(instanceId);
            if (!rt.Running)
                return (-1, -1, -1);

            // 节流：间隔由 regex_settings.yml 的 server_query.tps.interval_seconds 控制(默认10秒)
            var interval = TimeSpan.FromSeconds(Math.Max(1, cfg.IntervalSeconds));
            if (DateTime.UtcNow - rt.LastTpsQueryTime < interval)
                return rt.CachedTps;

            rt.LastTpsQueryTime = DateTime.UtcNow;

            var command = string.IsNullOrWhiteSpace(cfg.Command) ? "tps" : cfg.Command.Trim().TrimStart('/');
            var customRegex = GetConfiguredRegex(ref _tpsCustomRegex, ref _tpsCustomPattern, cfg.Regex, "TPS");

            try
            {
                var lines = SendCommandWithCapture(command, timeoutMs, silent: true, instanceId: rt.Id);
                foreach (var line in lines)
                {
                    if (customRegex != null && TryParseTpsByRegex(customRegex, line, out var c1, out var c5, out var c15))
                    {
                        rt.CachedTps = (c1, c5, c15);
                        return rt.CachedTps;
                    }
                    // 解析规则统一维护于 MCConsole(Spark 多值优先, 旧式单值兜底)
                    if (MCConsole.TryParseSparkTps(line, out var t1, out var t5, out var t15))
                    {
                        rt.CachedTps = (t1, t5, t15);
                        return rt.CachedTps;
                    }
                    if (MCConsole.TryParseSingleTps(line, out var t))
                    {
                        rt.CachedTps = (t, t, t);
                        return rt.CachedTps;
                    }
                }
            }
            catch { }

            return rt.CachedTps;
        }

        // ===== 玩家数量查询(发送 list 命令解析返回) =====

        /// <summary>
        /// 获取在线玩家数。RM 模式优先使用管理协议 server/status(无需发命令),
        /// 不可用时回退命令通道(由 regex_settings.yml 的 server_query.player_count 控制)。
        /// 节流间隔由 interval_seconds 配置。
        /// </summary>
        public static (int Count, int Max) QueryPlayerCount(int timeoutMs = 3000, string? instanceId = null)
        {
            // 配置关闭时不发送指令, 返回未知值
            var cfg = ContentManager.Regex?.ServerQuery?.PlayerCount;
            if (cfg == null || !cfg.Enabled)
                return (-1, -1);

            var rt = GetRuntime(instanceId);
            if (!rt.Running)
                return (-1, -1);

            // 节流：间隔由 regex_settings.yml 的 server_query.player_count.interval_seconds 控制(默认10秒)
            var interval = TimeSpan.FromSeconds(Math.Max(1, cfg.IntervalSeconds));
            if (DateTime.UtcNow - rt.LastListQueryTime < interval)
                return rt.CachedPlayers;

            rt.LastListQueryTime = DateTime.UtcNow;

            // RM: 管理协议 server/status 直接给出在线人数与上限, 不占用命令通道
            if (rt.Mode == "RM" && rt.Management != null && rt.Management.IsConnected)
            {
                try
                {
                    var (ok, online, max) = rt.Management.TryGetPlayerCount(Math.Max(timeoutMs, 3000));
                    if (ok && online >= 0)
                    {
                        rt.CachedPlayers = (online, max);
                        return rt.CachedPlayers;
                    }
                }
                catch { }
            }

            var command = string.IsNullOrWhiteSpace(cfg.Command) ? "list" : cfg.Command.Trim().TrimStart('/');
            var customRegex = GetConfiguredRegex(ref _listCustomRegex, ref _listCustomPattern, cfg.Regex, "在线人数");

            try
            {
                var lines = SendCommandWithCapture(command, timeoutMs, silent: true, instanceId: rt.Id);
                // 格式: There are 2 of a max of 20 players online: player1, player2 — 规则维护于 MCConsole
                foreach (var line in lines)
                {
                    if (customRegex != null && TryParsePlayerCountByRegex(customRegex, line, out var c, out var mx))
                    {
                        rt.CachedPlayers = (c, mx);
                        return rt.CachedPlayers;
                    }
                    if (MCConsole.TryParsePlayerCount(line, out var c2, out var mx2))
                    {
                        rt.CachedPlayers = (c2, mx2);
                        return rt.CachedPlayers;
                    }
                }
            }
            catch { }

            return rt.CachedPlayers;
        }

        // ===== MC 进程 CPU 使用率(基于 TotalProcessorTime 两次采样) =====

        /// <summary>
        /// 获取 MC 服务端 Java 进程的 CPU 使用率(%)。
        /// 基于 Process.TotalProcessorTime 两次采样计算，首次调用返回 -1。
        /// </summary>
        public static double GetMcCpuUsage(string? instanceId = null)
        {
            var rt = GetRuntime(instanceId);
            var proc = GetServerProcess(rt.Id);
            if (proc == null || proc.HasExited)
            {
                rt.LastMcCpuSampleTime = DateTime.MinValue;
                rt.CachedMcCpuUsage = -1;
                return -1;
            }

            try
            {
                var now = DateTime.UtcNow;
                var cpuTime = proc.TotalProcessorTime;

                if (rt.LastMcCpuSampleTime == DateTime.MinValue)
                {
                    // 首次采样，只记录基准值
                    rt.LastMcCpuSampleTime = now;
                    rt.LastMcCpuTime = cpuTime;
                    return rt.CachedMcCpuUsage < 0 ? -1 : rt.CachedMcCpuUsage;
                }

                var wallElapsed = now - rt.LastMcCpuSampleTime;
                var cpuElapsed = cpuTime - rt.LastMcCpuTime;

                // 至少间隔 0.5 秒才更新，避免抖动
                if (wallElapsed.TotalSeconds >= 0.5)
                {
                    rt.CachedMcCpuUsage = (cpuElapsed.TotalMilliseconds /
                        (wallElapsed.TotalMilliseconds * Environment.ProcessorCount)) * 100;
                    rt.LastMcCpuSampleTime = now;
                    rt.LastMcCpuTime = cpuTime;
                }

                return rt.CachedMcCpuUsage < 0 ? -1 : rt.CachedMcCpuUsage;
            }
            catch
            {
                return -1;
            }
        }

        public static void Detach(string? instanceId = null)
        {
            var rt = GetRuntime(instanceId);
            lock (rt.CommandLock)
            {
                DetachCore(rt);
            }
        }

        /// <summary>
        /// .fx add: 添加外部日志文件为外源数据流,自动识别错误并按来源保存
        /// </summary>
        public static (bool Success, string Message) AddExternalLog(string path)
        {
            string ThisProgramName = "Analyzer";

            if (string.IsNullOrWhiteSpace(path))
                return (false, I18n.Get("prog_fx_add_usage_hint"));

            path = path.Trim().Trim('"');
            if (!File.Exists(path))
                return (false, I18n.Get("anz_file_not_exists", path));

            try
            {
                var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
                string name = Path.GetFileNameWithoutExtension(path);

                // 加入日志流缓存(外部流)并流式识别错误(来源=文件名)
                LogStreamStore.ImportExternal(name, lines);
                int newErrors = ErrorStreamDetector.FeedLines(name, lines, "external");

                Output.Log(I18n.Get("prog_fx_add_ok", name, lines.Count, newErrors), 1, ThisProgramName);
                return (true, I18n.Get("prog_fx_add_ok", name, lines.Count, newErrors));
            }
            catch (Exception ex)
            {
                Output.Log(I18n.Get("anz_fx_add_failed", ex.Message), 3, ThisProgramName);
                return (false, I18n.Get("anz_fx_add_failed", ex.Message));
            }
        }

        /// <summary>
        /// 重扫日志流并识别错误(内部/面板触发):
        /// 服务器启动时错误已由 ErrorStreamDetector 自动识别保存,
        /// 此方法用于强制重扫(如正则调整后),按来源逐流分析。
        /// </summary>
        public static void AnalyzeErrors(string? filePath = null)
        {
            string ThisProgramName = "Analyzer";
            ContentManager.Initialize();

            // 分析批次: (来源标识, 来源类型, 日志行)
            var batches = new List<(string Source, string Type, List<string> Lines)>();

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                if (filePath.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var s in LogStreamStore.GetAll())
                        batches.Add((s.Key, s.Type == LogStreamType.External ? "external" : "server", s.Lines.ToList()));
                    if (batches.Count == 0)
                    {
                        Output.Log(I18n.Get("anz_fx_no_streams"), 2, ThisProgramName);
                        return;
                    }
                }
                else if (File.Exists(filePath))
                {
                    try
                    {
                        var lines = File.ReadAllLines(filePath, Encoding.UTF8).ToList();
                        batches.Add((Path.GetFileNameWithoutExtension(filePath), "external", lines));
                    }
                    catch (Exception ex)
                    {
                        Output.Log(I18n.Get("anz_read_file_failed", ex.Message), 3, ThisProgramName);
                        return;
                    }
                }
                else if (LogStreamStore.Contains(filePath))
                {
                    var s = LogStreamStore.Get(filePath)!;
                    batches.Add((s.Key, s.Type == LogStreamType.External ? "external" : "server", s.Lines.ToList()));
                }
                else
                {
                    string available = string.Join(", ", LogStreamStore.GetAll().Select(s => s.Key));
                    Output.Log(I18n.Get("anz_fx_not_found", filePath, string.IsNullOrEmpty(available) ? "-" : available), 2, ThisProgramName);
                    return;
                }
            }
            else
            {
                // 无参: 重扫全部缓存流(各标识独立分析,错误记录各自带来源)
                foreach (var s in LogStreamStore.GetAll())
                    batches.Add((s.Key, s.Type == LogStreamType.External ? "external" : "server", s.Lines.ToList()));

                // 无缓存流时回退: 实时缓冲 / latest.log(来源=当前服务器标识)
                if (batches.Count == 0)
                {
                    if (!IsRunModeActive && !IsAttached)
                    {
                        Output.Log(I18n.Get("anz_not_attached_analyze_hint"), 2, ThisProgramName);
                        return;
                    }

                    List<string> fallback = new List<string>();
                    lock (_logBufferLock)
                    {
                        fallback = _logBuffer.Snapshot();
                    }

                    if (fallback.Count == 0 && IsRunModeActive && !string.IsNullOrWhiteSpace(Config.CurrentServer.WorkPath))
                    {
                        string logFile = Path.Combine(Config.CurrentServer.WorkPath, "logs", "latest.log");
                        if (File.Exists(logFile))
                        {
                            try
                            {
                                fallback = File.ReadAllLines(logFile, Encoding.UTF8).ToList();
                                Output.Log(I18n.Get("anz_read_logfile_lines", fallback.Count), 1, ThisProgramName);
                            }
                            catch (Exception ex)
                            {
                                Output.Log(I18n.Get("anz_read_logfile_failed", ex.Message), 3, ThisProgramName);
                                return;
                            }
                        }
                    }

                    if (fallback.Count == 0)
                    {
                        Output.Log(I18n.Get("anz_no_log_content"), 2, ThisProgramName);
                        return;
                    }

                    batches.Add((Config.App.CurrentServer, "server", fallback));
                }
            }

            int totalLines = 0;
            int before = ErrorStreamDetector.TotalCount;
            foreach (var b in batches)
            {
                if (b.Lines.Count == 0) continue;
                totalLines += b.Lines.Count;
                ErrorStreamDetector.FeedLines(b.Source, b.Lines, b.Type);
            }
            int newErrors = ErrorStreamDetector.TotalCount - before;

            if (ErrorStreamDetector.TotalCount == 0)
            {
                Output.Log(I18n.Get("anz_no_errors_detected"), 1, ThisProgramName);
                return;
            }

            if (newErrors > 0)
                Output.Log(I18n.Get("anz_errors_recognized", ErrorStreamDetector.TotalCount, newErrors), 1, ThisProgramName);
            else
                Output.Log(I18n.Get("anz_fx_no_new_errors", totalLines, ErrorStreamDetector.TotalCount), 1, ThisProgramName);
        }

        public static void ListErrors(int? index)
        {
            string ThisProgramName = "Analyzer";
            var errors = ContentManager.LoadErrorLog();

            if (errors.Count == 0)
            {
                Output.Log(I18n.Get("anz_no_saved_errors"), 1, ThisProgramName);
                return;
            }

            if (index.HasValue)
            {
                if (errors.TryGetValue(index.Value, out var record))
                {
                    var detail = new StringBuilder();
                    detail.AppendLine(I18n.Get("anz_record_source", record.SourceDisplay));
                    detail.AppendLine(I18n.Get("anz_record_time", string.IsNullOrEmpty(record.Time) ? "-" : record.Time));
                    detail.AppendLine();
                    detail.Append(record.Content);

                    var panel = new Panel(Markup.Escape(detail.ToString()))
                        .Header(I18n.Get("anz_analysis_result_header", index.Value))
                        .Border(BoxBorder.Rounded);
                    AnsiConsole.Write(panel);
                }
                else
                {
                    Output.Log(I18n.Get("anz_no_such_index_result", index.Value), 2, ThisProgramName);
                }
                return;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn(I18n.Get("anz_col_no"), c => c.Alignment(Justify.Center).Width(6))
                .AddColumn(I18n.Get("anz_col_source"), c => c.Width(24))
                .AddColumn(I18n.Get("anz_col_error_summary"), c => c.Width(64));

            foreach (var kv in errors.OrderBy(kv => kv.Key))
            {
                string summary = kv.Value.Content.Split('\n').FirstOrDefault() ?? "";
                if (summary.Length > 64)
                    summary = summary.Substring(0, 61) + "...";

                table.AddRow(kv.Key.ToString(), Markup.Escape(kv.Value.SourceDisplay), Markup.Escape(summary));
            }

            AnsiConsole.Write(table);
            Output.Log(I18n.Get("anz_error_results_count", errors.Count), 1, ThisProgramName);
        }

        public static void DeleteErrors()
        {
            ContentManager.DeleteErrorLog();
            ErrorStreamDetector.Reset();
        }

        public static void ClientGuide(int? selectedIndex)
        {
            string ThisProgramName = "Analyzer";

            if (selectedIndex.HasValue && selectedIndex.Value >= 0)
            {
                lock (_clientGuideLock)
                {
                    if (_lastClientGuideMatches.Count == 0)
                    {
                        Output.Log(I18n.Get("anz_no_clientguide_matches"), 2, ThisProgramName);
                        return;
                    }

                    int idx = selectedIndex.Value - 1;
                    if (idx < 0 || idx >= _lastClientGuideMatches.Count)
                    {
                        Output.Log(I18n.Get("anz_invalid_index", _lastClientGuideMatches.Count), 2, ThisProgramName);
                        return;
                    }

                    var match = _lastClientGuideMatches[idx];
                    ShowClientGuideSolution(match);
                }
                return;
            }

            if (!IsRunModeActive && !IsAttached)
            {
                Output.Log(I18n.Get("anz_clientguide_not_attached"), 2, ThisProgramName);
                return;
            }

            ContentManager.Initialize();

            if (_clientGuideActive)
            {
                Output.Log(I18n.Get("anz_clientguide_running"), 2, ThisProgramName);
                return;
            }

            lock (_clientGuideLock)
            {
                _clientGuideCts?.Cancel();
                _clientGuideCts?.Dispose();
                _clientGuideCts = new CancellationTokenSource();
                _lastClientGuideMatches.Clear();
            }

            _clientGuideActive = true;

            long startSequence = LogCursor;

            int timeout = ContentManager.Regex.ClientGuide.Timeout;
            Output.Log(I18n.Get("anz_clientguide_waiting", timeout), 1, ThisProgramName);
            Output.Log(I18n.Get("anz_clientguide_wait_hint"), 1, ThisProgramName);

            var token = _clientGuideCts.Token;
            _ = Task.Run(() => MonitorClientGuide(startSequence, timeout, token), token);
        }

        private static void MonitorClientGuide(long startSequence, int timeout, CancellationToken cancellationToken)
        {
            try
            {
                string joinPattern = ContentManager.Regex.ClientGuide.PlayerJoin;
                string disconnectPattern = ContentManager.Regex.ClientGuide.PlayerDisconnect;
                string clientErrorPattern = ContentManager.Regex.ClientGuide.ClientError;
                string errorHandlerPattern = ContentManager.Regex.ClientGuide.ErrorHandler;

                Regex joinRegex = new Regex(joinPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, DynamicRegexTimeout);
                Regex disconnectRegex = new Regex(disconnectPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, DynamicRegexTimeout);
                Regex clientErrorRegex = new Regex(clientErrorPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, DynamicRegexTimeout);
                Regex errorHandlerRegex = new Regex(errorHandlerPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, DynamicRegexTimeout);

                DateTime startTime = DateTime.Now;
                List<string> capturedMessages = new List<string>();
                long lastSequence = startSequence;

                while (!cancellationToken.IsCancellationRequested)
                {
                    if ((DateTime.Now - startTime).TotalSeconds > timeout)
                    {
                        Output.Log(I18n.Get("anz_clientguide_timeout"), 2, "ClientGuide");
                        ShowTimeoutTroubleshootTable();
                        return;
                    }

                    List<string> newLines = ReadLogLinesSince(lastSequence, out lastSequence);

                    foreach (var line in newLines)
                    {
                        if (joinRegex.IsMatch(line))
                        {
                            Output.Log(I18n.Get("anz_clientguide_player_join", Markup.Escape(line)), 1, "ClientGuide");
                        }

                        if (disconnectRegex.IsMatch(line) || clientErrorRegex.IsMatch(line) || errorHandlerRegex.IsMatch(line))
                        {
                            capturedMessages.Add(line);
                            Output.Log(I18n.Get("anz_clientguide_disconnect_error", Markup.Escape(line)), 1, "ClientGuide");
                        }
                    }

                    if (capturedMessages.Count > 0)
                    {
                        break;
                    }

                    Thread.Sleep(500);
                }

                if (capturedMessages.Count > 0 && !cancellationToken.IsCancellationRequested)
                {
                    MatchClientErrors(capturedMessages);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Output.ReportError(ex, false, I18n.Get("anz_clientguide_error"));
            }
            finally
            {
                _clientGuideActive = false;
            }
        }

        private static void MatchClientErrors(List<string> messages)
        {
            string combinedMessage = string.Join("\n", messages);

            var matches = new List<ClientGuideMatch>();
            var allErrors = ContentManager.GetAllClientErrors();

            foreach (var entry in allErrors)
            {
                double similarity = CalculateSimilarity(combinedMessage, entry.Keyword);
                if (similarity >= 0.3)
                {
                    matches.Add(new ClientGuideMatch
                    {
                        Index = matches.Count + 1,
                        Entry = entry,
                        Similarity = similarity,
                        MatchedMessage = combinedMessage
                    });
                }
            }

            matches = matches.OrderByDescending(m => m.Similarity).ToList();

            for (int i = 0; i < matches.Count; i++)
            {
                matches[i].Index = i + 1;
            }

            lock (_clientGuideLock)
            {
                _lastClientGuideMatches.Clear();
                _lastClientGuideMatches.AddRange(matches);
            }

            if (matches.Count == 0)
            {
                Output.Log(I18n.Get("anz_no_client_error_matched"), 2, "ClientGuide");
                Output.Log(I18n.Get("anz_raw_message_label"), 1, "ClientGuide");
                foreach (var msg in messages)
                {
                    Output.Log(msg, 0, "ClientGuide");
                }
                return;
            }

            Output.Log(I18n.Get("anz_clientguide_match_count", matches.Count), 1, "ClientGuide");
            foreach (var match in matches)
            {
                Output.Log(I18n.Get("anz_clientguide_match_item", match.Index, Markup.Escape(match.Entry.Keyword), match.Similarity.ToString("P0"), Markup.Escape(match.Entry.Description)), 1, "ClientGuide");
            }
            Output.Log(I18n.Get("anz_clientguide_view_solution"), 1, "ClientGuide");
        }

        private static double CalculateSimilarity(string message, string keyword)
        {
            if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(keyword))
                return 0;

            if (message.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                return 1.0;

            string[] keywordParts = keyword.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int matchedParts = 0;
            foreach (var part in keywordParts)
            {
                if (message.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)
                    matchedParts++;
            }

            if (keywordParts.Length > 0 && matchedParts > 0)
                return (double)matchedParts / keywordParts.Length * 0.7;

            return 0;
        }

        private static void ShowClientGuideSolution(ClientGuideMatch match)
        {
            var rule = new Rule(I18n.Get("anz_clientguide_match_title", match.Index));
            AnsiConsole.Write(rule);

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn(I18n.Get("anz_col_item"), c => c.Width(15))
                .AddColumn(I18n.Get("anz_col_content"), c => c.Width(65));

            table.AddRow(I18n.Get("anz_row_keyword"), Markup.Escape(match.Entry.Keyword));
            table.AddRow(I18n.Get("anz_row_error_desc"), Markup.Escape(match.Entry.Description));
            table.AddRow(I18n.Get("anz_row_solution"), Markup.Escape(match.Entry.Solution));
            table.AddRow(I18n.Get("anz_row_similarity"), $"{match.Similarity:P0}");

            string displayMessage = match.MatchedMessage.Length > 200
                ? match.MatchedMessage.Substring(0, 197) + "..."
                : match.MatchedMessage;
            table.AddRow(I18n.Get("anz_row_raw_message"), Markup.Escape(displayMessage));

            AnsiConsole.Write(table);
        }

        private static void ShowTimeoutTroubleshootTable()
        {
            var root = new Tree(I18n.Get("anz_troubleshoot_tree_title"));

            foreach (var item in ContentManager.GetAllTroubleshoot())
            {
                var node = root.AddNode($"[cyan]{Markup.Escape(item.Title)}[/]");
                node.AddNode(I18n.Get("anz_tree_problem", Markup.Escape(item.Problem)));
                node.AddNode(I18n.Get("anz_tree_solution", Markup.Escape(item.Solution)));
            }

            AnsiConsole.Write(root);
        }

        #region Base

        public static void BaseAnalyze(string? rangeArg)
        {
            string ThisProgramName = "Base";

            var errors = ContentManager.LoadErrorLog();
            if (errors.Count == 0)
            {
                Output.Log(I18n.Get("anz_no_errors_analyze_first"), 2, ThisProgramName);
                return;
            }

            if (string.IsNullOrWhiteSpace(rangeArg))
            {
                Output.Log(I18n.Get("anz_base_usage"), 1, ThisProgramName);
                Output.Log(I18n.Get("anz_base_usage_n"), 1, ThisProgramName);
                Output.Log(I18n.Get("anz_base_usage_nm"), 1, ThisProgramName);
                Output.Log(I18n.Get("anz_error_records_count", errors.Count), 1, ThisProgramName);
                return;
            }

            List<int>? targetIndices = ParseRange(rangeArg, errors.Keys.ToList());
            if (targetIndices == null || targetIndices.Count == 0)
            {
                Output.Log(I18n.Get("anz_invalid_range", rangeArg), 2, ThisProgramName);
                return;
            }

            var sb = new StringBuilder();
            var involvedSources = new List<string>();
            foreach (int idx in targetIndices)
            {
                if (errors.TryGetValue(idx, out var record))
                {
                    // 标注数据来源: 供 base 匹配与结果展示
                    sb.AppendLine($"===== {I18n.Get("anz_record_source", record.SourceDisplay)} =====");
                    sb.AppendLine(record.Content);
                    sb.AppendLine("---");
                    if (!involvedSources.Contains(record.SourceDisplay))
                        involvedSources.Add(record.SourceDisplay);
                }
                else
                {
                    Output.Log(I18n.Get("anz_error_index_missing", idx), 2, ThisProgramName);
                    return;
                }
            }

            string combinedError = sb.ToString();
            Output.Log(I18n.Get("anz_analyzing_records", targetIndices.Count), 1, ThisProgramName);
            Output.Log(I18n.Get("anz_data_sources", string.Join(", ", involvedSources)), 1, ThisProgramName);

            var baseEntries = ContentManager.GetAllBaseEntries();
            var matches = new List<(BaseEntry Entry, Match Match, double Score)>();

            foreach (var entry in baseEntries)
            {
                var regex = GetBaseEntryRegex(entry.Pattern);
                if (regex == null)
                    continue;

                try
                {
                    var match = regex.Match(combinedError);
                    if (match.Success)
                    {
                        double score = (double)match.Value.Length / combinedError.Length;
                        matches.Add((entry, match, score));
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    Output.Log(I18n.Get("anz_regex_invalid_topic", entry.Topic, "匹配超时(疑似灾难性回溯)"), 2, ThisProgramName);
                }
            }

            // 按命中长度与占比排序, 只展示最可能的前若干条, 避免宽泛规则刷屏
            matches = matches.OrderByDescending(m => m.Match.Value.Length).ThenByDescending(m => m.Score)
                             .Take(MaxBaseMatchResults).ToList();

            if (matches.Count == 0)
            {
                Output.Log(I18n.Get("anz_no_base_pattern_matched"), 2, ThisProgramName);
                Output.Log(I18n.Get("anz_raw_error_content"), 1, ThisProgramName);

                var panel = new Panel(Markup.Escape(combinedError.Length > 500 ? combinedError.Substring(0, 497) + "..." : combinedError))
                    .Header(I18n.Get("anz_unrecognized_error"))
                    .Border(BoxBorder.Rounded);
                AnsiConsole.Write(panel);
                return;
            }

            var rule = new Rule(I18n.Get("anz_base_result_title", matches.Count));
            AnsiConsole.Write(rule);

            for (int i = 0; i < matches.Count; i++)
            {
                var (entry, match, score) = matches[i];

                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn(I18n.Get("anz_col_item"), c => c.Width(12))
                    .AddColumn(I18n.Get("anz_col_content"), c => c.Width(68));

                table.AddRow(I18n.Get("anz_row_index"), $"{i + 1}");
                table.AddRow(I18n.Get("anz_row_topic"), $"[cyan]{Markup.Escape(entry.Topic)}[/]");
                table.AddRow(I18n.Get("anz_row_solution"), Markup.Escape(entry.Solution));

                string matchedText = match.Value.Length > 200
                    ? match.Value.Substring(0, 197) + "..."
                    : match.Value;
                table.AddRow(I18n.Get("anz_row_matched_content"), Markup.Escape(matchedText));
                table.AddRow(I18n.Get("anz_row_matched_length"), I18n.Get("anz_length_chars", match.Value.Length));

                if (!string.IsNullOrEmpty(entry.Action))
                {
                    // 安全策略: 基础分析命中后不再自动执行脚本, 仅提示用户手动执行
                    table.AddRow("Action", Markup.Escape(entry.Action) + "（不自动执行，请手动运行）");
                }

                AnsiConsole.Write(table);
                AnsiConsole.WriteLine();
            }
        }

        /// <summary>
        /// 获取基础错误库条目对应的已编译正则(带超时, 结果缓存); 无效表达式缓存为 null 并只记录一次
        /// </summary>
        private static Regex? GetBaseEntryRegex(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return null;

            lock (_baseEntryRegexLock)
            {
                if (_baseEntryRegexCache.TryGetValue(pattern, out var cached))
                    return cached;
            }

            Regex? regex = null;
            try
            {
                regex = new Regex(pattern,
                    RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant,
                    DynamicRegexTimeout);
            }
            catch (Exception ex)
            {
                Output.Log(I18n.Get("anz_regex_invalid_topic", pattern, ex.Message), 2, ThisProgramName);
            }

            lock (_baseEntryRegexLock)
            {
                // 自定义+AI 生成的条目可能无上限, 超过阈值时整体清空重建
                if (_baseEntryRegexCache.Count > 500)
                    _baseEntryRegexCache.Clear();
                _baseEntryRegexCache[pattern] = regex;
            }
            return regex;
        }

        private static List<int>? ParseRange(string rangeArg, List<int> availableKeys)
        {
            availableKeys.Sort();

            if (int.TryParse(rangeArg, out int singleIndex))
            {
                if (availableKeys.Contains(singleIndex))
                    return new List<int> { singleIndex };
                return null;
            }

            if (rangeArg.Contains('-'))
            {
                var parts = rangeArg.Split('-', 2);
                if (parts.Length == 2 && int.TryParse(parts[0], out int start) && int.TryParse(parts[1], out int end))
                {
                    if (start > end)
                        return null;

                    var result = new List<int>();
                    for (int i = start; i <= end; i++)
                    {
                        if (availableKeys.Contains(i))
                            result.Add(i);
                    }
                    return result.Count > 0 ? result : null;
                }
            }

            return null;
        }

        private static volatile bool _aiRunning = false;
        private static readonly object _aiLock = new object();

        public static async Task AiAnalyze(string? rangeArg)
        {
            string ThisProgramName = "AI";

            lock (_aiLock)
            {
                if (_aiRunning)
                {
                    Output.Log(I18n.Get("anz_ai_running"), 2, ThisProgramName);
                    return;
                }
                _aiRunning = true;
            }

            try
            {

            var errors = ContentManager.LoadErrorLog();
            if (errors.Count == 0)
            {
                Output.Log(I18n.Get("anz_no_errors_analyze_first"), 2, ThisProgramName);
                return;
            }

            if (string.IsNullOrWhiteSpace(rangeArg))
            {
                Output.Log(I18n.Get("anz_ai_usage"), 1, ThisProgramName);
                Output.Log(I18n.Get("anz_ai_usage_n"), 1, ThisProgramName);
                Output.Log(I18n.Get("anz_ai_usage_nm"), 1, ThisProgramName);
                Output.Log(I18n.Get("anz_error_records_count", errors.Count), 1, ThisProgramName);
                return;
            }

            List<int>? targetIndices = ParseRange(rangeArg, errors.Keys.ToList());
            if (targetIndices == null || targetIndices.Count == 0)
            {
                Output.Log(I18n.Get("anz_invalid_range", rangeArg), 2, ThisProgramName);
                return;
            }

            var sb = new StringBuilder();
            var involvedSources = new List<string>();
            foreach (int idx in targetIndices)
            {
                if (errors.TryGetValue(idx, out var record))
                {
                    // 标注数据来源: AI 分析可感知错误来自哪个服务器/外部导入
                    sb.AppendLine($"===== {I18n.Get("anz_record_source", record.SourceDisplay)} =====");
                    sb.AppendLine(record.Content);
                    sb.AppendLine("---");
                    if (!involvedSources.Contains(record.SourceDisplay))
                        involvedSources.Add(record.SourceDisplay);
                }
                else
                {
                    Output.Log(I18n.Get("anz_error_index_missing", idx), 2, ThisProgramName);
                    return;
                }
            }

            string combinedError = sb.ToString();
            Output.Log(I18n.Get("anz_ai_sending", targetIndices.Count), 1, ThisProgramName);
            Output.Log(I18n.Get("anz_data_sources", string.Join(", ", involvedSources)), 1, ThisProgramName);

            string? aiResponse = await Intelligence.AnalyzeWithAi(combinedError);

            if (string.IsNullOrWhiteSpace(aiResponse))
            {
                Output.Log(I18n.Get("anz_ai_no_result"), 2, ThisProgramName);
                return;
            }

            var rule = new Rule(I18n.Get("anz_ai_result_title"));
            AnsiConsole.Write(rule);

            var lines = aiResponse.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            foreach (var line in lines)
            {
                Output.Log(line, 0, "AI");
            }

            ContentManager.SaveAiResponse(targetIndices.FirstOrDefault(), combinedError, aiResponse);
            }
            finally
            {
                _aiRunning = false;
            }
        }

        #endregion

        #region Filter

        public static void FilterInfo()
        {
            Output.Log(I18n.Get("anz_filter_intro"), 1, "Filter");
            Output.Log(I18n.Get("anz_filter_intro_hint"), 1, "Filter");
            Output.Log(I18n.Get("anz_filter_subcommands"), 1, "Filter");
            Output.Log(I18n.Get("anz_filter_config_desc"), 1, "Filter");
            Output.Log(I18n.Get("anz_filter_plugin_desc"), 1, "Filter");
        }

        public static void FilterConfig(int? selectedIndex)
        {
            string ThisProgramName = "Filter";

            if (selectedIndex.HasValue && selectedIndex.Value == 0)
            {
                if (Directory.Exists(ConfigBackupDir))
                {
                    Directory.Delete(ConfigBackupDir, true);
                    Output.Log(I18n.Get("anz_config_backup_deleted"), 1, ThisProgramName);
                }
                else
                {
                    Output.Log(I18n.Get("anz_no_config_backup"), 2, ThisProgramName);
                }
                return;
            }

            if (selectedIndex.HasValue && selectedIndex.Value > 0)
            {
                lock (_filterLock)
                {
                    if (_lastConfigDiffs.Count == 0)
                    {
                        Output.Log(I18n.Get("anz_no_config_diffs"), 2, ThisProgramName);
                        return;
                    }

                    int idx = selectedIndex.Value - 1;
                    if (idx >= _lastConfigDiffs.Count)
                    {
                        Output.Log(I18n.Get("anz_invalid_index", _lastConfigDiffs.Count), 2, ThisProgramName);
                        return;
                    }

                    var diff = _lastConfigDiffs[idx];
                    try
                    {
                        string? dir = Path.GetDirectoryName(diff.CurrentPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);

                        File.Copy(diff.BackupPath, diff.CurrentPath, true);
                        Output.Log(I18n.Get("anz_restored", Markup.Escape(diff.RelativePath)), 1, ThisProgramName);
                    }
                    catch (Exception ex)
                    {
                        Output.Log(I18n.Get("anz_restore_failed", ex.Message), 3, ThisProgramName);
                    }
                }
                return;
            }

            string workPath = GetWorkPath();
            if (string.IsNullOrEmpty(workPath) || !Directory.Exists(workPath))
            {
                Output.Log(I18n.Get("anz_no_workdir"), 2, ThisProgramName);
                return;
            }

            if (!Directory.Exists(ConfigBackupDir))
            {
                BackupConfigFiles(workPath);
                return;
            }

            CompareConfigFiles(workPath);
        }

        private static void BackupConfigFiles(string workPath)
        {
            string ThisProgramName = "Filter";
            string[] extensions = { ".yml", ".yaml", ".json", ".properties" };

            try
            {
                if (Directory.Exists(ConfigBackupDir))
                    Directory.Delete(ConfigBackupDir, true);

                Directory.CreateDirectory(ConfigBackupDir);

                int count = 0;

                count += CopyConfigFiles(workPath, ConfigBackupDir, extensions, 2);

                string pluginsDir = Path.Combine(workPath, "plugins");
                if (Directory.Exists(pluginsDir))
                {
                    string backupPluginsDir = Path.Combine(ConfigBackupDir, "plugins");
                    count += CopyConfigFiles(pluginsDir, backupPluginsDir, extensions, 2);
                }

                Output.Log(I18n.Get("anz_config_backup_done", count, ConfigBackupDir), 1, ThisProgramName);
                Output.Log(I18n.Get("anz_config_backup_hint"), 1, ThisProgramName);
            }
            catch (Exception ex)
            {
                Output.Log(I18n.Get("anz_backup_failed", ex.Message), 3, ThisProgramName);
            }
        }

        private static int CopyConfigFiles(string sourceDir, string targetDir, string[] extensions, int maxDepth, int currentDepth = 0)
        {
            int count = 0;

            if (!Directory.Exists(sourceDir))
                return 0;

            if (!Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (extensions.Contains(ext))
                {
                    string fileName = Path.GetFileName(file);
                    string targetPath = Path.Combine(targetDir, fileName);
                    File.Copy(file, targetPath, true);
                    count++;
                }
            }

            if (currentDepth < maxDepth)
            {
                foreach (var dir in Directory.GetDirectories(sourceDir))
                {
                    string dirName = Path.GetFileName(dir);
                    string targetSubDir = Path.Combine(targetDir, dirName);
                    count += CopyConfigFiles(dir, targetSubDir, extensions, maxDepth, currentDepth + 1);
                }
            }

            return count;
        }

        private static void CompareConfigFiles(string workPath)
        {
            string ThisProgramName = "Filter";

            lock (_filterLock)
            {
                _lastConfigDiffs.Clear();
            }

            var diffs = new List<ConfigDiffEntry>();
            int diffIndex = 1;

            CompareDirectory(ConfigBackupDir, workPath, "", ref diffs, ref diffIndex);

            string pluginsBackupDir = Path.Combine(ConfigBackupDir, "plugins");
            string pluginsSourceDir = Path.Combine(workPath, "plugins");
            if (Directory.Exists(pluginsBackupDir) && Directory.Exists(pluginsSourceDir))
            {
                CompareDirectory(pluginsBackupDir, pluginsSourceDir, "plugins", ref diffs, ref diffIndex);
            }

            lock (_filterLock)
            {
                _lastConfigDiffs.AddRange(diffs);
            }

            if (diffs.Count == 0)
            {
                Output.Log(I18n.Get("anz_config_no_diff"), 1, ThisProgramName);
                return;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn(I18n.Get("anz_col_index"), c => c.Alignment(Justify.Center).Width(6))
                .AddColumn(I18n.Get("anz_col_file_path"), c => c.Width(60))
                .AddColumn(I18n.Get("anz_col_status"), c => c.Alignment(Justify.Center).Width(12));

            foreach (var diff in diffs)
            {
                string status = !File.Exists(diff.CurrentPath) ? I18n.Get("anz_status_deleted") : I18n.Get("anz_status_modified");
                table.AddRow(diff.Index.ToString(), Markup.Escape(diff.RelativePath), status);
            }

            AnsiConsole.Write(table);
            Output.Log(I18n.Get("anz_config_diff_count", diffs.Count), 1, ThisProgramName);
        }

        private static void CompareDirectory(string backupDir, string currentDir, string relativePrefix, ref List<ConfigDiffEntry> diffs, ref int diffIndex)
        {
            if (!Directory.Exists(backupDir))
                return;

            foreach (var backupFile in Directory.GetFiles(backupDir))
            {
                string fileName = Path.GetFileName(backupFile);
                string relativePath = string.IsNullOrEmpty(relativePrefix) ? fileName : Path.Combine(relativePrefix, fileName);
                string currentFile = Path.Combine(currentDir, fileName);

                bool isDifferent = false;

                if (!File.Exists(currentFile))
                {
                    isDifferent = true;
                }
                else
                {
                    try
                    {
                        string backupContent = File.ReadAllText(backupFile);
                        string currentContent = File.ReadAllText(currentFile);
                        if (backupContent != currentContent)
                            isDifferent = true;
                    }
                    catch
                    {
                        isDifferent = true;
                    }
                }

                if (isDifferent)
                {
                    diffs.Add(new ConfigDiffEntry
                    {
                        Index = diffIndex++,
                        RelativePath = relativePath,
                        BackupPath = backupFile,
                        CurrentPath = currentFile
                    });
                }
            }

            foreach (var backupSubDir in Directory.GetDirectories(backupDir))
            {
                string dirName = Path.GetFileName(backupSubDir);
                string currentSubDir = Path.Combine(currentDir, dirName);
                string subRelative = string.IsNullOrEmpty(relativePrefix) ? dirName : Path.Combine(relativePrefix, dirName);
                CompareDirectory(backupSubDir, currentSubDir, subRelative, ref diffs, ref diffIndex);
            }
        }

        public static void FilterPlugin(int? selectedIndex)
        {
            string ThisProgramName = "Filter";

            if (selectedIndex.HasValue && selectedIndex.Value == 0)
            {
                Output.Log(I18n.Get("anz_restarting_server"), 1, ThisProgramName);
                StopServer();
                Thread.Sleep(2000);
                StartServer();
                return;
            }

            if (selectedIndex.HasValue && selectedIndex.Value > 0)
            {
                lock (_filterLock)
                {
                    if (_lastPluginList.Count == 0)
                    {
                        Output.Log(I18n.Get("anz_no_plugin_list"), 2, ThisProgramName);
                        return;
                    }

                    int idx = selectedIndex.Value - 1;
                    if (idx >= _lastPluginList.Count)
                    {
                        Output.Log(I18n.Get("anz_invalid_index", _lastPluginList.Count), 2, ThisProgramName);
                        return;
                    }

                    var plugin = _lastPluginList[idx];
                    try
                    {
                        if (plugin.IsDisabled)
                        {
                            string newPath = plugin.FullPath.Substring(0, plugin.FullPath.Length - 7) + ".jar";
                            File.Move(plugin.FullPath, newPath);
                            Output.Log(I18n.Get("anz_plugin_enabled", Markup.Escape(plugin.FileName), Markup.Escape(Path.GetFileName(newPath))), 1, ThisProgramName);
                        }
                        else
                        {
                            string newPath = plugin.FullPath.Substring(0, plugin.FullPath.Length - 4) + ".disjar";
                            File.Move(plugin.FullPath, newPath);
                            Output.Log(I18n.Get("anz_plugin_disabled", Markup.Escape(plugin.FileName), Markup.Escape(Path.GetFileName(newPath))), 1, ThisProgramName);
                        }

                        ListPlugins();
                    }
                    catch (Exception ex)
                    {
                        Output.Log(I18n.Get("anz_operation_failed", ex.Message), 3, ThisProgramName);
                    }
                }
                return;
            }

            ListPlugins();
        }

        private static void ListPlugins()
        {
            string ThisProgramName = "Filter";
            string workPath = GetWorkPath();
            if (string.IsNullOrEmpty(workPath) || !Directory.Exists(workPath))
            {
                Output.Log(I18n.Get("anz_no_workdir"), 2, ThisProgramName);
                return;
            }

            string pluginsDir = Path.Combine(workPath, "plugins");
            if (!Directory.Exists(pluginsDir))
            {
                Output.Log(I18n.Get("anz_plugins_dir_missing"), 2, ThisProgramName);
                return;
            }

            lock (_filterLock)
            {
                _lastPluginList.Clear();
            }

            var jarFiles = new List<FileInfo>();
            try
            {
                var dirInfo = new DirectoryInfo(pluginsDir);
                jarFiles.AddRange(dirInfo.GetFiles("*.jar"));
                jarFiles.AddRange(dirInfo.GetFiles("*.disjar"));
                jarFiles = jarFiles.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex)
            {
                Output.Log(I18n.Get("anz_read_plugins_dir_failed", ex.Message), 3, ThisProgramName);
                return;
            }

            if (jarFiles.Count == 0)
            {
                Output.Log(I18n.Get("anz_no_plugin_files"), 2, ThisProgramName);
                return;
            }

            var plugins = new List<PluginEntry>();
            int index = 1;
            foreach (var file in jarFiles)
            {
                bool isDisabled = file.Extension.Equals(".disjar", StringComparison.OrdinalIgnoreCase);
                plugins.Add(new PluginEntry
                {
                    Index = index++,
                    FileName = file.Name,
                    FullPath = file.FullName,
                    IsDisabled = isDisabled
                });
            }

            lock (_filterLock)
            {
                _lastPluginList.Clear();
                _lastPluginList.AddRange(plugins);
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn(I18n.Get("anz_col_index"), c => c.Alignment(Justify.Center).Width(6))
                .AddColumn(I18n.Get("anz_col_plugin_file"), c => c.Width(50))
                .AddColumn(I18n.Get("anz_col_status"), c => c.Alignment(Justify.Center).Width(12));

            foreach (var plugin in plugins)
            {
                string status = plugin.IsDisabled ? I18n.Get("anz_status_disabled") : I18n.Get("anz_status_normal");
                table.AddRow(plugin.Index.ToString(), Markup.Escape(plugin.FileName), status);
            }

            AnsiConsole.Write(table);
            Output.Log(I18n.Get("anz_plugin_count", plugins.Count), 1, ThisProgramName);
        }

        // ===== fx filter mod (模组启用/禁用, 参考 filter plugin) =====
        private static readonly List<ModEntry> _lastModList = new List<ModEntry>();

        public static void FilterMod(int? selectedIndex)
        {
            string ThisProgramName = "Filter";

            if (selectedIndex.HasValue && selectedIndex.Value == 0)
            {
                Output.Log(I18n.Get("anz_restarting_server"), 1, ThisProgramName);
                StopServer();
                Thread.Sleep(2000);
                StartServer();
                return;
            }

            if (selectedIndex.HasValue && selectedIndex.Value > 0)
            {
                lock (_filterLock)
                {
                    if (_lastModList.Count == 0)
                    {
                        Output.Log(I18n.Get("anz_no_mod_list"), 2, ThisProgramName);
                        return;
                    }

                    int idx = selectedIndex.Value - 1;
                    if (idx >= _lastModList.Count)
                    {
                        Output.Log(I18n.Get("anz_invalid_index", _lastModList.Count), 2, ThisProgramName);
                        return;
                    }

                    var mod = _lastModList[idx];
                    try
                    {
                        // 模组禁用约定: mod.jar -> mod.jar.disabled (Fabric/Forge 通用)
                        if (mod.IsDisabled)
                        {
                            string newPath = mod.FullPath.Substring(0, mod.FullPath.Length - ".disabled".Length);
                            File.Move(mod.FullPath, newPath);
                            Output.Log(I18n.Get("anz_mod_enabled", Markup.Escape(mod.FileName), Markup.Escape(Path.GetFileName(newPath))), 1, ThisProgramName);
                        }
                        else
                        {
                            string newPath = mod.FullPath + ".disabled";
                            File.Move(mod.FullPath, newPath);
                            Output.Log(I18n.Get("anz_mod_disabled", Markup.Escape(mod.FileName), Markup.Escape(Path.GetFileName(newPath))), 1, ThisProgramName);
                        }

                        ListMods();
                    }
                    catch (Exception ex)
                    {
                        Output.Log(I18n.Get("anz_operation_failed", ex.Message), 3, ThisProgramName);
                    }
                }
                return;
            }

            ListMods();
        }

        private static void ListMods()
        {
            string ThisProgramName = "Filter";
            string workPath = GetWorkPath();
            if (string.IsNullOrEmpty(workPath) || !Directory.Exists(workPath))
            {
                Output.Log(I18n.Get("anz_no_workdir"), 2, ThisProgramName);
                return;
            }

            string modsDir = Path.Combine(workPath, "mods");
            if (!Directory.Exists(modsDir))
            {
                Output.Log(I18n.Get("anz_mods_dir_missing"), 2, ThisProgramName);
                return;
            }

            lock (_filterLock)
            {
                _lastModList.Clear();
            }

            var jarFiles = new List<FileInfo>();
            try
            {
                var dirInfo = new DirectoryInfo(modsDir);
                jarFiles.AddRange(dirInfo.GetFiles("*.jar"));
                jarFiles.AddRange(dirInfo.GetFiles("*.disabled"));
                jarFiles = jarFiles.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex)
            {
                Output.Log(I18n.Get("anz_read_mods_dir_failed", ex.Message), 3, ThisProgramName);
                return;
            }

            if (jarFiles.Count == 0)
            {
                Output.Log(I18n.Get("anz_no_mod_files"), 2, ThisProgramName);
                return;
            }

            var mods = new List<ModEntry>();
            int index = 1;
            foreach (var file in jarFiles)
            {
                bool isDisabled = file.Extension.Equals(".disabled", StringComparison.OrdinalIgnoreCase);
                mods.Add(new ModEntry
                {
                    Index = index++,
                    FileName = file.Name,
                    FullPath = file.FullName,
                    IsDisabled = isDisabled
                });
            }

            lock (_filterLock)
            {
                _lastModList.Clear();
                _lastModList.AddRange(mods);
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn(I18n.Get("anz_col_index"), c => c.Alignment(Justify.Center).Width(6))
                .AddColumn(I18n.Get("anz_col_mod_file"), c => c.Width(50))
                .AddColumn(I18n.Get("anz_col_status"), c => c.Alignment(Justify.Center).Width(12));

            foreach (var mod in mods)
            {
                string status = mod.IsDisabled ? I18n.Get("anz_status_disabled") : I18n.Get("anz_status_normal");
                table.AddRow(mod.Index.ToString(), Markup.Escape(mod.FileName), status);
            }

            AnsiConsole.Write(table);
            Output.Log(I18n.Get("anz_mod_count", mods.Count), 1, ThisProgramName);
        }

        private static string GetWorkPath()
        {
            string? workPath = Config.CurrentServer.WorkPath;

            if (!string.IsNullOrWhiteSpace(workPath) && Directory.Exists(workPath))
                return workPath;

            if (IsRunModeActive && !string.IsNullOrWhiteSpace(Config.CurrentServer.WorkPath))
                return Config.CurrentServer.WorkPath;

            workPath = FindServerPathFromScan();
            return workPath ?? "";
        }

        #endregion

        private static void AddToLogBuffer(InstanceRuntime rt, string line)
        {
            Interlocked.Increment(ref rt.LogActivityCounter);
            lock (rt.LogBufferLock)
            {
                rt.LogBuffer.Add(line);
            }

            // 同步写入全局日志流缓存(.fx get 可分析任意历史流,不受服务器停止影响)
            if (!string.IsNullOrEmpty(rt.StreamKey))
                LogStreamStore.Append(rt.StreamKey, line);
        }

        /// <summary>
        /// 实时检测日志行中的服务器崩溃信号(精准关键字单行命中即判定)
        /// 计划内关停不再判定为崩溃; 崩溃事件同一次运行内只发布一次, 并触发 AI 崩溃检测任务
        /// </summary>
        private static void ProcessCrashDetectionLine(InstanceRuntime rt, string line)
        {
            if (string.IsNullOrEmpty(line)) return;

            // 计划内关停: 用户发起 stop, 或日志出现正常停服序列(Stopping server 等)。
            // 一经确认, 后续保存/收尾日志(即使含 FATAL/Exception)都不再判定为崩溃
            if (rt.UserInitiatedStop || MCConsole.IsNormalShutdownLine(line))
            {
                rt.ShutdownInProgress = true;
                return;
            }
            if (rt.ShutdownInProgress) return;

            // 精准崩溃标志(单行命中即判定，避免误报) — 规则统一维护于 MCConsole
            bool isCrashSignal = MCConsole.IsCrashLine(line);

            // 非崩溃行: 累积"错误相关行", 供崩溃时生成错误签名(用于 AI 相似度判重)
            if (!isCrashSignal)
            {
                TrackErrorSignatureLine(rt, line);
                return;
            }

            string errorSignature;
            lock (rt.CrashSignalLock)
            {
                // 本次错误签名 = 崩溃前累积的错误行 + 崩溃行本身; 生成后重置, 避免跨崩溃累积稀释相似度
                errorSignature = rt.RecentErrorLines.Count == 0
                    ? line
                    : string.Join("\n", rt.RecentErrorLines) + "\n" + line;
                rt.RecentErrorLines.Clear();
            }

            // 崩溃事件只发布一次: 日志侧与进程退出侧都可能在检测, 由原子标记去重
            if (!NotifyServerCrashOnce(-1, line, rt.Id)) return;

            // 触发 AI 崩溃检测任务(延迟2秒等待崩溃报告文件写入完成)
            if (Intelligence.AiAutoRunner.IsRunning)
            {
                _ = Task.Run(async () =>
                {
                    using (InstanceContext.Push(rt.Id))
                    {
                        try
                        {
                            await Task.Delay(2000);
                            // 事件触发: 传入错误签名(崩溃前错误行+崩溃行), 由 AiAutoRunner 做最小触发间隔与错误相似度判重
                            await Intelligence.AiAutoRunner.TriggerByEventAsync("crash_detect", errorSignature);
                        }
                        catch (Exception ex)
                        {
                            Output.Log(I18n.Get("anz_crash_detect_task_failed", ex.Message), 2, "Analyzer");
                        }
                    }
                });
            }
        }

        /// <summary>
        /// 发布崩溃事件(同一次运行内只发布一次)。日志侧与进程退出侧都可能检测到崩溃,
        /// 由原子标记保证不重复发布; 返回 true 表示本次是首个检测者。
        /// </summary>
        private static bool NotifyServerCrashOnce(int exitCode, string? detailLine, string? instanceId = null)
        {
            var rt = GetRuntime(instanceId);
            if (Interlocked.Exchange(ref rt.CrashNotified, 1) == 1) return false;

            if (!string.IsNullOrEmpty(detailLine))
                Output.Log(I18n.Get("anz_crash_signal_detected", detailLine), 2, "Analyzer");
            EventBus.Publish(new ServerCrashEvent(rt.Id, exitCode));
            return true;
        }

        /// <summary>
        /// 记录"错误相关"日志行(ERROR/WARN/异常/堆栈等), 崩溃时用于生成错误签名。
        /// 仅保留最近 MaxErrorSignatureLines 行, 崩溃触发后重置。
        /// </summary>
        private static void TrackErrorSignatureLine(InstanceRuntime rt, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            bool worthy =
                line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("WARN", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Caused by", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Crash", StringComparison.OrdinalIgnoreCase) ||
                line.TrimStart().StartsWith("at ", StringComparison.Ordinal);
            if (!worthy) return;

            lock (rt.CrashSignalLock)
            {
                rt.RecentErrorLines.Add(line);
                while (rt.RecentErrorLines.Count > MaxErrorSignatureLines)
                    rt.RecentErrorLines.RemoveAt(0);
            }
        }

        #region 玩家事件监听

        private static void ProcessPlayerEventLine(InstanceRuntime rt, string line)
        {
            // 玩家事件通知给面板/AI/调度器时需要知道归属实例
            using var _ctx = InstanceContext.Push(rt.Id);
            try
            {
                var pe = Config.App.PlayerEvent;
                if (pe == null || !pe.Enabled) return;

                // 预筛: 全部事件 pattern 合并为一条正则, 不命中则直接返回(避免逐事件逐 pattern 扫描)
                var prefilter = GetPlayerEventPrefilter(pe);
                if (prefilter != null)
                {
                    try
                    {
                        if (!prefilter.IsMatch(line)) return;
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        // 预筛超时(异常表达式): 该行直接跳过, 不影响后续日志
                        return;
                    }
                }

                // player_join
                TryMatchAndPublish(line, pe.PlayerJoin, (m) =>
                {
                    EventBus.Publish(new PlayerJoinEvent(
                        GetGroupValue(m, "player_name"),
                        GetGroupValue(m, "player_trigger_time")));
                });

                // connect (额外传递 player_ip)
                TryMatchAndPublish(line, pe.Connect, (m) =>
                {
                    EventBus.Publish(new PlayerConnectEvent(
                        GetGroupValue(m, "player_name"),
                        GetGroupValue(m, "player_trigger_time"),
                        GetGroupValue(m, "player_ip")));
                });

                // lost (额外传递 player_lost_reason)
                TryMatchAndPublish(line, pe.Lost, (m) =>
                {
                    EventBus.Publish(new PlayerLostEvent(
                        GetGroupValue(m, "player_name"),
                        GetGroupValue(m, "player_trigger_time"),
                        GetGroupValue(m, "player_lost_reason").Trim()));
                });

                // leaves
                TryMatchAndPublish(line, pe.Leaves, (m) =>
                {
                    EventBus.Publish(new PlayerLeaveEvent(
                        GetGroupValue(m, "player_name"),
                        GetGroupValue(m, "player_trigger_time")));
                });

                // command (额外传递 command)
                TryMatchAndPublish(line, pe.Command, (m) =>
                {
                    EventBus.Publish(new PlayerCommandEvent(
                        GetGroupValue(m, "player_name"),
                        GetGroupValue(m, "player_trigger_time"),
                        GetGroupValue(m, "command").Trim()));
                });

                // chat (额外传递 message)
                TryMatchAndPublish(line, pe.Chat, (m) =>
                {
                    EventBus.Publish(new PlayerChatEvent(
                        GetGroupValue(m, "player_name"),
                        GetGroupValue(m, "player_trigger_time"),
                        GetGroupValue(m, "message").Trim()));
                });

                // setmode (额外传递 player_mode)
                TryMatchAndPublish(line, pe.Setmode, (m) =>
                {
                    EventBus.Publish(new PlayerSetModeEvent(
                        GetGroupValue(m, "player_name"),
                        GetGroupValue(m, "player_trigger_time"),
                        GetGroupValue(m, "player_mode").Trim()));
                });

                // customs (自定义事件)
                if (pe.Customs != null && pe.Customs.Count > 0)
                {
                    foreach (var kvp in pe.Customs)
                    {
                        string eventName = kvp.Key;
                        var customCfg = kvp.Value;
                        if (customCfg?.Patterns == null || customCfg.Patterns.Count == 0) continue;

                        TryMatchAndPublish(line, customCfg.Patterns, (m) =>
                        {
                            var ev = new CustomPlayerEvent(
                                eventName,
                                GetGroupValue(m, "player_name"),
                                GetGroupValue(m, "player_trigger_time"));

                            if (customCfg.Parameters != null)
                            {
                                foreach (var paramName in customCfg.Parameters)
                                {
                                    string val = GetGroupValue(m, paramName);
                                    if (!string.IsNullOrEmpty(val))
                                        ev.Parameters[paramName] = val.Trim();
                                }
                            }
                            EventBus.Publish(ev);
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Output.Log(I18n.Get("anz_player_event_error", ex.Message), 2, "PlayerEvent");
            }
        }

        private static void TryMatchAndPublish(string line, List<string> patterns, Action<Match> onMatch)
        {
            if (patterns == null || patterns.Count == 0) return;
            foreach (var pattern in patterns)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;

                var regex = GetPlayerEventRegex(pattern);
                if (regex == null) continue;

                try
                {
                    var match = regex.Match(line);
                    if (match.Success)
                    {
                        onMatch(match);
                        return; // 一个行只触发一次
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // 超时的表达式已缓存, 不会在每行日志上反复触发灾难性回溯
                    Output.Log($"玩家事件正则匹配超时, 已跳过: {pattern}", 3, "PlayerEvent");
                }
            }
        }

        /// <summary>
        /// 构建全部玩家事件 pattern 的合并预筛正则(配置对象变化时重建)。
        /// 仅用于"这一行是否可能命中任何事件"的快速判断, 命中后再走逐事件精确匹配。
        /// </summary>
        private static Regex? GetPlayerEventPrefilter(PlayerEventConfig pe)
        {
            lock (_playerEventPrefilterLock)
            {
                if (ReferenceEquals(_playerEventPrefilterConfig, pe))
                    return _playerEventPrefilter;
            }

            var parts = new List<string>();
            void Collect(List<string>? patterns)
            {
                if (patterns == null) return;
                foreach (var p in patterns)
                {
                    if (!string.IsNullOrWhiteSpace(p))
                        parts.Add("(?:" + p + ")");
                }
            }

            Collect(pe.PlayerJoin);
            Collect(pe.Connect);
            Collect(pe.Lost);
            Collect(pe.Leaves);
            Collect(pe.Command);
            Collect(pe.Chat);
            Collect(pe.Setmode);
            if (pe.Customs != null)
            {
                foreach (var kvp in pe.Customs)
                    Collect(kvp.Value?.Patterns);
            }

            Regex? regex = null;
            if (parts.Count > 0)
            {
                try
                {
                    regex = new Regex(string.Join("|", parts),
                        RegexOptions.Compiled | RegexOptions.CultureInvariant,
                        DynamicRegexTimeout);
                }
                catch (Exception ex)
                {
                    // 合并失败(单个表达式非法)时退化为无预筛, 由逐事件匹配各自报错
                    Output.Log($"玩家事件预筛正则构建失败, 已跳过预筛: {ex.Message}", 2, "PlayerEvent");
                }
            }

            lock (_playerEventPrefilterLock)
            {
                _playerEventPrefilterConfig = pe;
                _playerEventPrefilter = regex;
                return regex;
            }
        }

        /// <summary>获取玩家事件正则(预编译 + 超时, 按 pattern 缓存; 无效表达式缓存为 null)</summary>
        private static Regex? GetPlayerEventRegex(string pattern)
        {
            lock (_playerEventRegexLock)
            {
                if (_playerEventRegexCache.TryGetValue(pattern, out var cached))
                    return cached;
            }

            Regex? regex = null;
            try
            {
                regex = new Regex(pattern,
                    RegexOptions.Compiled | RegexOptions.CultureInvariant,
                    DynamicRegexTimeout);
            }
            catch (Exception ex)
            {
                Output.Log($"玩家事件正则无效已忽略: {pattern} ({ex.Message})", 3, "PlayerEvent");
            }

            lock (_playerEventRegexLock)
            {
                if (_playerEventRegexCache.Count > 500)
                    _playerEventRegexCache.Clear();
                _playerEventRegexCache[pattern] = regex;
            }
            return regex;
        }

        private static string GetGroupValue(Match m, string name)
        {
            return m.Groups.TryGetValue(name, out var g) ? g.Value : "";
        }

        #endregion

        private static void DetachCore(InstanceRuntime rt)
        {
            rt.ClientGuideCts?.Cancel();
            Intelligence.StopAutoTips(rt.Id);

            rt.Cts?.Cancel();
            rt.Cts?.Dispose();
            rt.Cts = null;

            if (rt.Rcon != null)
            {
                rt.Rcon.Disconnect();
                rt.Rcon = null;
            }

            rt.Management?.Stop();
            rt.Management = null;

            rt.AttachedProcessId = 0;
            rt.AttachedWindowTitle = "";
            rt.ConnectedServerName = "";
            rt.Running = false;
            if (!string.IsNullOrEmpty(rt.StreamKey))
                LogStreamStore.SetLive(rt.StreamKey, false);

            Output.Log(I18n.Get("anz_detached"), 1, "Analyzer");
        }

        private static string? ResolveServerDirectory(MinecraftServerInfo server)
        {
            if (!string.IsNullOrEmpty(server.JarPath) && server.JarPath != $"PID:{server.ProcessId}")
            {
                if (Path.IsPathRooted(server.JarPath))
                {
                    return Path.GetDirectoryName(server.JarPath);
                }

                string? workDir = GetProcessWorkingDirectory(server.ProcessId);
                if (!string.IsNullOrEmpty(workDir))
                {
                    string fullPath = Path.Combine(workDir, server.JarPath);
                    if (File.Exists(fullPath))
                    {
                        return Path.GetDirectoryName(fullPath);
                    }
                }

                return workDir;
            }

            return GetProcessWorkingDirectory(server.ProcessId);
        }

        #endregion

        #region Process Working Directory

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr ProcessHandle, int ProcessInformationClass, out PROCESS_BASIC_INFORMATION ProcessInformation, int ProcessInformationLength, out int ReturnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr Reserved3;
        }

        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_VM_READ = 0x0010;
        private const int ProcessBasicInformation = 0;

        private static string? GetProcessWorkingDirectory(int pid)
        {
            IntPtr hProcess = IntPtr.Zero;
            try
            {
                hProcess = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, (uint)pid);
                if (hProcess == IntPtr.Zero)
                    return null;

                int status = NtQueryInformationProcess(hProcess, ProcessBasicInformation, out PROCESS_BASIC_INFORMATION pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
                if (status != 0 || pbi.PebBaseAddress == IntPtr.Zero)
                    return null;

                bool is64Bit = Environment.Is64BitProcess;
                int pebParamsOffset = is64Bit ? 0x20 : 0x10;

                byte[] pebBuffer = new byte[pebParamsOffset + IntPtr.Size];
                if (!ReadProcessMemory(hProcess, pbi.PebBaseAddress, pebBuffer, pebBuffer.Length, out _))
                    return null;

                IntPtr processParamsPtr = is64Bit
                    ? (IntPtr)BitConverter.ToInt64(pebBuffer, pebParamsOffset)
                    : (IntPtr)BitConverter.ToInt32(pebBuffer, pebParamsOffset);

                if (processParamsPtr == IntPtr.Zero)
                    return null;

                int curDirOffset = is64Bit ? 0x38 : 0x24;
                int unicodeStringSize = is64Bit ? 16 : 8;
                byte[] paramsBuffer = new byte[curDirOffset + unicodeStringSize];
                if (!ReadProcessMemory(hProcess, processParamsPtr, paramsBuffer, paramsBuffer.Length, out _))
                    return null;

                ushort strLength = BitConverter.ToUInt16(paramsBuffer, curDirOffset);
                IntPtr strBufferPtr = is64Bit
                    ? (IntPtr)BitConverter.ToInt64(paramsBuffer, curDirOffset + 8)
                    : (IntPtr)BitConverter.ToInt32(paramsBuffer, curDirOffset + 4);

                if (strLength == 0 || strBufferPtr == IntPtr.Zero)
                    return null;

                byte[] strBuffer = new byte[strLength];
                if (!ReadProcessMemory(hProcess, strBufferPtr, strBuffer, strBuffer.Length, out _))
                    return null;

                string dir = Encoding.Unicode.GetString(strBuffer);
                dir = dir.TrimEnd('\\', '\0');
                return dir;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hProcess != IntPtr.Zero)
                    CloseHandle(hProcess);
            }
        }

        #endregion

        #region Windows API

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        #endregion

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:验证平台兼容性", Justification = "Windows only")]
        private static List<MinecraftServerInfo> ScanMinecraftServers()
        {
            var results = new List<MinecraftServerInfo>();
            var javaProcesses = new List<(int ProcessId, string ProcessName, string CommandLine, string WindowTitle, string ExecutablePath)>();

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine, Name, ExecutablePath FROM Win32_Process WHERE Name LIKE 'java%'");
                using var collection = searcher.Get();

                foreach (ManagementObject obj in collection)
                {
                    int pid = Convert.ToInt32(obj["ProcessId"]);
                    string? cmdLine = obj["CommandLine"]?.ToString() ?? "";
                    string? exePath = obj["ExecutablePath"]?.ToString() ?? "";

                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        string processName = "";
                        string windowTitle = "";
                        try
                        {
                            processName = process.ProcessName;
                            windowTitle = process.MainWindowTitle ?? "";
                            // WMI 未返回 ExecutablePath 时回退到 MainModule.FileName
                            if (string.IsNullOrEmpty(exePath))
                            {
                                try { exePath = process.MainModule?.FileName ?? ""; } catch { }
                            }
                        }
                        catch { }

                        javaProcesses.Add((pid, processName, cmdLine, windowTitle, exePath ?? ""));
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                Output.Log(I18n.Get("anz_wmi_query_failed", ex.Message), 3, "Analyzer");
            }

            // 计算配置的 Java 路径目录(用于优先级匹配)
            // 仅当配置为完整路径(包含目录且文件名为 java.exe/javaw.exe)时才参与匹配,
            // 否则(例如配置为 "java")无法做目录级匹配, 所有候选视为同等优先级。
            string? configuredJavaDir = null;
            string configuredJavaPath = Config.CurrentServer.JavaPath ?? "";
            if (!string.IsNullOrWhiteSpace(configuredJavaPath) && Path.IsPathRooted(configuredJavaPath))
            {
                try
                {
                    string fn = Path.GetFileName(configuredJavaPath).ToLowerInvariant();
                    if (fn == "java.exe" || fn == "javaw.exe" || fn == "java" || fn == "javaw")
                    {
                        configuredJavaDir = NormalizeDir(Path.GetDirectoryName(configuredJavaPath));
                    }
                }
                catch { }
            }

            foreach (var (processId, processName, cmdLine, windowTitle, exePath) in javaProcesses)
            {
                string? jarPath = ExtractJarPath(cmdLine);
                if (!string.IsNullOrEmpty(jarPath))
                {
                    string jarName = Path.GetFileName(jarPath).ToLowerInvariant();
                    bool isLikelyMcServer = jarName.Contains("server") ||
                                            jarName.Contains("paper") ||
                                            jarName.Contains("sponge") ||
                                            jarName.Contains("folia") ||
                                            jarName.Contains("leaf") ||
                                            jarName.Contains("spigot") ||
                                            jarName.Contains("bukkit") ||
                                            jarName.Contains("forge") ||
                                            jarName.Contains("fabric") ||
                                            jarName.Contains("mohist") ||
                                            jarName.Contains("catserver") ||
                                            jarName.Contains("arclight") ||
                                            jarName.Contains("leaves") ||
                                            jarName.Contains("luminol") ||
                                            jarName.Contains("pufferfish") ||
                                            jarName.Contains("waterfall") ||
                                            jarName.Contains("purpur") ||
                                            jarName.Contains("velocity") ||
                                            jarName.Contains("bungee");

                    bool hasMcClasses = cmdLine.Contains("net.minecraft") ||
                                        cmdLine.Contains("net.fabricmc") ||
                                        cmdLine.Contains("cpw.mods") ||
                                        cmdLine.Contains("bukkit") ||
                                        cmdLine.Contains("paper");

                    if (isLikelyMcServer || hasMcClasses)
                    {
                        int score = ComputeJavaPathMatchScore(exePath, configuredJavaDir);
                        results.Add(new MinecraftServerInfo
                        {
                            ProcessId = processId,
                            ProcessName = processName,
                            WindowTitle = string.IsNullOrEmpty(windowTitle) ? $"java (PID: {processId})" : windowTitle,
                            JarPath = jarPath,
                            CommandLine = cmdLine,
                            JavaExePath = exePath,
                            MatchScore = score
                        });
                    }
                }
            }

            // 按匹配优先级排序(高分在前); 同分保持原顺序(稳定排序)
            // 这一步确保: 当 javapath_target_* 哨兵进程与真实 MC 服务端(jdk-22)同时存在时,
            // 配置了 JavaPath 的真实进程会排在首位, 从而被 FindServerPathFromScan / 自动连接选中。
            if (results.Count > 1)
            {
                results = results.OrderByDescending(r => r.MatchScore).ToList();

                // 诊断日志: 多候选时记录最终选择与各候选的 Java 路径(便于排查 CPU/内存取错进程的问题)
                try
                {
                    var top = results[0];
                    var others = results.Skip(1).Select(r => $"PID={r.ProcessId}({r.JavaExePath},score={r.MatchScore})");
                    Output.Log(
                        I18n.Get("anz_scan_candidates",
                        results.Count,
                        $"PID={top.ProcessId} java={top.JavaExePath} score={top.MatchScore}",
                        string.Join(", ", others)),
                        1, "Analyzer");
                }
                catch { }
            }

            return results;
        }

        /// <summary>规范化目录字符串: 小写 + 去除尾部路径分隔符, 用于路径比较</summary>
        private static string? NormalizeDir(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
                return null;
            return dir.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        }

        /// <summary>
        /// 计算候选进程 Java 可执行路径与配置 JavaPath 的匹配优先级。
        /// 100 = 目录完全一致; 90 = 解析符号链接/短路径后一致; 0 = 不匹配或无法比较。
        /// </summary>
        private static int ComputeJavaPathMatchScore(string exePath, string? configuredJavaDir)
        {
            if (string.IsNullOrEmpty(exePath) || string.IsNullOrEmpty(configuredJavaDir))
                return 0;

            try
            {
                string? exeDir = NormalizeDir(Path.GetDirectoryName(exePath));
                if (string.IsNullOrEmpty(exeDir))
                    return 0;

                if (string.Equals(exeDir, configuredJavaDir, StringComparison.Ordinal))
                    return 100;

                // 解析符号链接/ Junction(例如 javapath_target_XXX 可能是 Junction)
                // 后再比较一次, 兼容 Oracle javapath 哨兵目录指向真实 JDK 的情况
                string? resolvedExeDir = NormalizeDir(ResolveRealDirectory(exePath));
                if (!string.IsNullOrEmpty(resolvedExeDir) &&
                    string.Equals(resolvedExeDir, configuredJavaDir, StringComparison.Ordinal))
                    return 90;

                return 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>尝试解析路径的真实目录(去除符号链接/ Junction), 失败时返回原路径目录</summary>
        private static string? ResolveRealDirectory(string exePath)
        {
            try
            {
                // 优先用 Win32 API 解析最终路径(可识别 Junction/Symbolic Link)
                string? dir = Path.GetDirectoryName(exePath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    return dir;

                // 使用 Alphaleonis 或直接调用 GetFinalPathNameByHandle 较重,
                // 这里采用 DirectoryInfo.ResolveLinkTarget(.NET 6+), 不可用则回退。
                var di = new DirectoryInfo(dir);
                try
                {
                    var resolved = di.ResolveLinkTarget(true);
                    if (resolved != null)
                        return resolved.FullName;
                }
                catch { }
                return dir;
            }
            catch
            {
                return null;
            }
        }

        private static string? ExtractJarPath(string cmdLine)
        {
            if (string.IsNullOrWhiteSpace(cmdLine))
                return null;

            int jarIndex = cmdLine.IndexOf("-jar", StringComparison.OrdinalIgnoreCase);
            if (jarIndex >= 0)
            {
                int start = jarIndex + 4;
                while (start < cmdLine.Length && char.IsWhiteSpace(cmdLine[start]))
                    start++;

                if (start < cmdLine.Length)
                {
                    int end = start;
                    bool inQuotes = false;

                    while (end < cmdLine.Length)
                    {
                        char c = cmdLine[end];
                        if (c == '"')
                        {
                            inQuotes = !inQuotes;
                            end++;
                            continue;
                        }
                        if (!inQuotes && char.IsWhiteSpace(c))
                            break;
                        end++;
                    }

                    string jarPath = cmdLine.Substring(start, end - start).Trim('"');
                    if (!string.IsNullOrWhiteSpace(jarPath))
                    {
                        return jarPath;
                    }
                }
            }

            return null;
        }
    }
}
