using RtCli.Modules.Extension;
using RtCli.Modules.Unit;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RtCli.Modules.Function
{
    public class Analyzer
    {
        private static Process? _serverProcess;
        private static StreamWriter? _serverInput;
        private static CancellationTokenSource? _outputCts;
        private static readonly object _attachLock = new object();
        private static int _attachedProcessId = 0;
        private static string _attachedWindowTitle = "";
        private static string _connectedServerName = "";
        private static readonly object _scanLock = new object();
        private static List<MinecraftServerInfo> _lastScanResults = new List<MinecraftServerInfo>();
        private static long _logFilePosition = 0;
        private static string _currentMode = "RM";
        private static bool _isRunModeActive = false;

        internal static readonly object _logBufferLock = new object();
        internal static readonly List<string> _logBuffer = new List<string>();
        private const int MaxLogBufferSize = 5000;

        // 当前活跃的日志流标识(服务器启动时记录,停止后保留用于 .fx get 分析)
        private static string _activeStreamKey = "";

        // 实时崩溃检测状态(去重用)
        private static DateTime _lastCrashSignalTime = DateTime.MinValue;
        private static readonly object _crashSignalLock = new object();

        private static CancellationTokenSource? _clientGuideCts;
        private static volatile bool _clientGuideActive = false;
        private static readonly List<ClientGuideMatch> _lastClientGuideMatches = new List<ClientGuideMatch>();
        private static readonly object _clientGuideLock = new object();

        private static readonly List<ConfigDiffEntry> _lastConfigDiffs = new List<ConfigDiffEntry>();
        private static readonly object _filterLock = new object();
        private static readonly List<PluginEntry> _lastPluginList = new List<PluginEntry>();
        private static readonly string ConfigBackupDir = Path.Combine(Path.GetDirectoryName(Config.DataPath)!, "config_backup");

        private static RconClient? _rconClient;
        private static int _restartAttemptCount = 0;
        private static bool _userInitiatedStop = false;

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
        /// <summary>RM模式(Run+Management) - 启动服务端通过日志文件读取控制台，stdin发送命令</summary>
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
        /// <summary>是否从stdout读取控制台(Run/RR)</summary>
        public static bool ReadsFromStdout => _currentMode == "RUN" || _currentMode == "RR";
        /// <summary>是否从日志文件读取控制台(RM)</summary>
        public static bool ReadsFromLogFile => _currentMode == "RM";
        /// <summary>是否使用服务端管理协议(RM)</summary>
        public static bool UsesManagementProtocol => _currentMode == "RM";

        public static void Initialize()
        {
            string raw = Config.CurrentServer.AnalyzerMode.ToUpperInvariant();
            // 向后兼容: 旧模式名映射到新模式名
            _currentMode = raw switch
            {
                "RUN" => "RUN",
                "RCON" => "RR",        // 旧Rcon = 新RR (Run+Rcon)
                "ONLYRCON" => "RCON",  // 旧OnlyRcon = 新Rcon (纯RCON)
                "MANAGEMENT" => "RM",  // 旧Management = 新RM (Run+Management)
                "RR" => "RR",
                "RM" => "RM",
                _ => "RM"
            };
        }

        #region RUN Mode

        public static bool IsRunModeActive => _isRunModeActive;

        public static void StartServer()
        {
            string ThisProgramName = "Analyzer";

            if (!NeedsRunServer)
            {
                Output.Log(I18n.Get("anz_rcon_mode_cannot_start"), 2, ThisProgramName);
                return;
            }

            if (_isRunModeActive)
            {
                Output.Log(I18n.Get("anz_server_already_running"), 2, ThisProgramName);
                return;
            }

            string? workPath = Config.CurrentServer.WorkPath;
            string flags = Config.CurrentServer.RunServerFlags;

            if (string.IsNullOrWhiteSpace(workPath))
            {
                workPath = FindServerPathFromScan();
                if (string.IsNullOrEmpty(workPath))
                {
                    Output.Log(I18n.Get("anz_workpath_not_configured"), 2, ThisProgramName);
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(flags))
            {
                Output.Log(I18n.Get("anz_flags_not_configured"), 2, ThisProgramName);
                return;
            }

            if (!Directory.Exists(workPath))
            {
                Output.Log(I18n.Get("anz_workpath_not_exists", workPath), 3, ThisProgramName);
                return;
            }

            // EULA 预检查: 启动前检查 eula.txt
            string eulaPath = Path.Combine(workPath, "eula.txt");
            if (File.Exists(eulaPath))
            {
                try
                {
                    string eulaContent = File.ReadAllText(eulaPath);
                    if (eulaContent.Contains("eula=false"))
                    {
                        if (Config.App.AutoAgreeEula)
                        {
                            eulaContent = eulaContent.Replace("eula=false", "eula=true");
                            File.WriteAllText(eulaPath, eulaContent);
                            Output.Log(I18n.Get("anz_eula_auto_agreed_config"), 1, ThisProgramName);
                        }
                        else
                        {
                            Output.Log(I18n.Get("anz_eula_not_agreed_detected"), 2, ThisProgramName);
                            Output.Log(I18n.Get("anz_eula_notice"), 1, ThisProgramName);
                            bool agree = AnsiConsole.Confirm(I18n.Get("anz_eula_confirm"), false);
                            if (agree)
                            {
                                eulaContent = eulaContent.Replace("eula=false", "eula=true");
                                File.WriteAllText(eulaPath, eulaContent);
                                Output.Log(I18n.Get("anz_eula_agreed"), 1, ThisProgramName);
                            }
                            else
                            {
                                Output.Log(I18n.Get("anz_eula_refused_cannot_start"), 2, ThisProgramName);
                                return;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Output.Log(I18n.Get("anz_eula_check_failed", ex.Message), 2, ThisProgramName);
                }
            }

            try
            {
                _serverProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = string.IsNullOrWhiteSpace(Config.CurrentServer.JavaPath) ? "java" : Config.CurrentServer.JavaPath,
                        Arguments = flags,
                        WorkingDirectory = workPath,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.GetEncoding(0),
                        StandardErrorEncoding = Encoding.GetEncoding(0)
                    },
                    EnableRaisingEvents = true
                };

                _serverProcess.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        // RM模式: 不从stdout读取控制台(改用日志文件)，但仍检测EULA
                        if (ReadsFromStdout)
                        {
                            AddToLogBuffer(e.Data);
                            if (!ShouldHideConsole())
                                Output.Log(e.Data, 0, _connectedServerName);

                            ProcessPlayerEventLine(e.Data);
                            ProcessCrashDetectionLine(e.Data);
                        }

                        // 自动检测EULA提示 (首次启动时服务端生成eula.txt后退出)
                        if (e.Data.Contains("eula=false", StringComparison.OrdinalIgnoreCase) ||
                            e.Data.Contains("You need to agree to the EULA", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(Config.CurrentServer.WorkPath))
                            {
                                string eulaPath = Path.Combine(Config.CurrentServer.WorkPath, "eula.txt");
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
                                                    StopServer();
                                                    await Task.Delay(1000);
                                                    StartServer();
                                                });
                                            }
                                            else
                                            {
                                                // auto_agree_eula=false: 提示用户阅读并确认
                                                _ = Task.Run(async () =>
                                                {
                                                    await Task.Delay(2000); // 等待服务端退出
                                                    Output.Log(I18n.Get("anz_eula_refused_closed"), 2, "Analyzer");
                                                    Output.Log(I18n.Get("anz_eula_notice"), 1, "Analyzer");
                                                    bool agree = AnsiConsole.Confirm(I18n.Get("anz_eula_confirm"), false);
                                                    if (agree)
                                                    {
                                                        try
                                                        {
                                                            content = File.ReadAllText(eulaPath);
                                                            content = content.Replace("eula=false", "eula=true");
                                                            File.WriteAllText(eulaPath, content);
                                                            Output.Log(I18n.Get("anz_eula_agreed"), 1, "Analyzer");

                                                            bool restart = AnsiConsole.Confirm(I18n.Get("anz_confirm_restart_server"), true);
                                                            if (restart)
                                                            {
                                                                StartServer();
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
                };

                _serverProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        // RM模式: 不从stderr读取控制台(改用日志文件)
                        if (ReadsFromStdout)
                        {
                            AddToLogBuffer(e.Data);
                            if (!ShouldHideConsole())
                                Output.Log(e.Data, 0, _connectedServerName);
                            ProcessPlayerEventLine(e.Data);
                            ProcessCrashDetectionLine(e.Data);
                        }
                    }
                };

                _serverProcess.Start();
                _serverProcess.BeginOutputReadLine();
                _serverProcess.BeginErrorReadLine();
                _serverInput = _serverProcess.StandardInput;
                _serverInput.AutoFlush = true;

                _isRunModeActive = true;
                _attachedProcessId = _serverProcess.Id;
                _connectedServerName = Config.CurrentServer.ServerName;
                _restartAttemptCount = 0;
                _userInitiatedStop = false;

                // 注册日志数据流(服务器启动即开始缓存,停止后保留供 .fx get 分析)
                _activeStreamKey = Config.App.CurrentServer;
                LogStreamStore.BeginServerStream(_activeStreamKey, _connectedServerName);

                _outputCts = new CancellationTokenSource();
                _ = Task.Run(() => MonitorServerProcess(_outputCts.Token), _outputCts.Token);

                Output.Log(I18n.Get("anz_server_started", _serverProcess.Id, _currentMode), 1, ThisProgramName);
                EventBus.Publish(new ServerStartEvent(Config.App.GrpcPort, Config.App.CurrentServer));

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
                        catch { }
                    });
                }
                Output.Log(I18n.Get("anz_workdir_label", workPath), 1, ThisProgramName);
                Output.Log(I18n.Get("anz_flags_label", flags), 1, ThisProgramName);

                // RR模式：启动后尝试RCON连接
                if (UsesRconCommands)
                {
                    _rconClient = new RconClient();
                    Output.Log(I18n.Get("anz_rr_auto_connect_rcon"), 1, ThisProgramName);
                    _ = Task.Run(() =>
                    {
                        // 等待服务端启动完成（最多等待5分钟）
                        for (int i = 0; i < 300; i++)
                        {
                            if (!_isRunModeActive) break;
                            Thread.Sleep(1000);
                            try
                            {
                                if (_rconClient.Connect(
                                    Config.CurrentServer.RconHost,
                                    Config.CurrentServer.RconPort,
                                    Config.CurrentServer.RconPassword))
                                {
                                    Output.Log(I18n.Get("anz_rcon_connected", Config.CurrentServer.RconHost, Config.CurrentServer.RconPort), 1, ThisProgramName);
                                    break;
                                }
                            }
                            catch { }
                        }
                    });
                    Output.Log(I18n.Get("anz_cmd_via_rcon_hint"), 1, ThisProgramName);
                }
                else if (IsRunMode)
                {
                    Output.Log(I18n.Get("anz_cmd_hint"), 1, ThisProgramName);
                }
                else if (IsRMMode)
                {
                    // RM模式: 通过日志文件获取控制台信息流
                    Output.Log(I18n.Get("anz_rm_log_monitor_starting"), 1, ThisProgramName);
                    // 注册日志数据流(RM模式由日志文件监控写入缓存)
                    _activeStreamKey = Config.App.CurrentServer;
                    LogStreamStore.BeginServerStream(_activeStreamKey, Config.CurrentServer.ServerName);
                    _ = Task.Run(() => StartRMLogFileMonitor(workPath, _outputCts.Token));
                    Output.Log(I18n.Get("anz_cmd_hint"), 1, ThisProgramName);
                }

                Output.Log(I18n.Get("anz_input_stop_hint"), 1, ThisProgramName);

                Intelligence.StartAutoTips();
            }
            catch (Exception ex)
            {
                Output.ReportError(ex, false, I18n.Get("anz_start_server_failed"));
                CleanupRunMode();
            }
        }

        public static void StopServer()
        {
            string ThisProgramName = "Analyzer";

            if (!_isRunModeActive)
            {
                Output.Log(I18n.Get("anz_server_not_running"), 1, ThisProgramName);
                return;
            }

            _userInitiatedStop = true;

            try
            {
                if (_serverInput != null)
                {
                    _serverInput.WriteLine("stop");
                    _serverInput.Flush();
                }

                if (_serverProcess != null && !_serverProcess.HasExited)
                {
                    if (!_serverProcess.WaitForExit(10000))
                    {
                        _serverProcess.Kill();
                    }
                }
            }
            catch (Exception ex)
            {
                Output.Log(I18n.Get("anz_stop_server_error", ex.Message), 2, ThisProgramName);
            }

            CleanupRunMode();
            Intelligence.StopAutoTips();
        }

        private static void MonitorServerProcess(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (_serverProcess == null || _serverProcess.HasExited)
                    {
                        int exitCode = _serverProcess?.ExitCode ?? -1;
                        Output.Log(I18n.Get("anz_server_exited", exitCode), 0, "Analyzer");
                        CleanupRunMode();
                        Intelligence.StopAutoTips();

                        // 自动重启逻辑
                        if (!_userInitiatedStop && Config.CurrentServer.AutoRestart && NeedsRunServer)
                        {
                            int maxRetries = Config.CurrentServer.AutoRestartMaxRetries;
                            if (maxRetries == 0 || _restartAttemptCount < maxRetries)
                            {
                                _restartAttemptCount++;
                                EventBus.Publish(new ServerCrashEvent(Config.App.CurrentServer, exitCode));
                                EventBus.Publish(new AutoRestartEvent(Config.App.CurrentServer, _restartAttemptCount, maxRetries));
                                string attemptTotal = maxRetries > 0 ? "/" + maxRetries : "";
                                Output.Log(I18n.Get("anz_auto_restart", _restartAttemptCount, attemptTotal), 1, "Analyzer");
                                Thread.Sleep(5000);
                                if (!_userInitiatedStop)
                                {
                                    StartServer();
                                }
                            }
                            else
                            {
                                Output.Log(I18n.Get("anz_max_retries_reached", maxRetries), 2, "Analyzer");
                                _restartAttemptCount = 0;
                            }
                        }
                        else
                        {
                            _restartAttemptCount = 0;
                            EventBus.Publish(new ServerStopEvent(Config.App.CurrentServer));
                            Output.Log(I18n.Get("anz_server_stopped"), 1, "Analyzer");
                        }
                        break;
                    }
                    Thread.Sleep(500);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private static void CleanupRunMode()
        {
            lock (_attachLock)
            {
                _outputCts?.Cancel();
                _outputCts?.Dispose();
                _outputCts = null;

                _serverInput?.Dispose();
                _serverInput = null;

                if (_serverProcess != null)
                {
                    try
                    {
                        if (!_serverProcess.HasExited)
                        {
                            _serverProcess.CancelOutputRead();
                            _serverProcess.CancelErrorRead();
                        }
                    }
                    catch { }

                    try { _serverProcess.Dispose(); } catch { }
                    _serverProcess = null;
                }

                _isRunModeActive = false;
                _attachedProcessId = 0;
                _attachedWindowTitle = "";
                _connectedServerName = "";

                // 标记日志流停止(流与数据保留, .fx get 仍可分析异常关闭前的日志)
                if (!string.IsNullOrEmpty(_activeStreamKey))
                    LogStreamStore.SetLive(_activeStreamKey, false);
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
            AttachToServerRcon(selectedServer, index.ToString());
            Intelligence.StartAutoTips();
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
                AttachToServerRcon(server, $"PID:{pid}");
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
                AttachToServerRcon(tempServer, $"PID:{pid}");
                Intelligence.StartAutoTips();
            }
            catch (Exception ex)
            {
                Output.Log(I18n.Get("anz_connect_pid_failed", pid, ex.Message), 3, ThisProgramName);
            }
        }

        private static void AttachToServerRcon(MinecraftServerInfo server, string serverName)
        {
            string ThisProgramName = "Analyzer";
            lock (_attachLock)
            {
                DetachCore();

                try
                {
                    _attachedProcessId = server.ProcessId;
                    _attachedWindowTitle = server.WindowTitle;
                    _connectedServerName = serverName;

                    _outputCts = new CancellationTokenSource();
                    var token = _outputCts.Token;

                    // Rcon模式: 不读取日志文件，仅通过RCON通信
                    string? serverDir = ResolveServerDirectory(server);
                    if (!string.IsNullOrEmpty(serverDir))
                    {
                        string logFile = Path.Combine(serverDir, "logs", "latest.log");
                        if (File.Exists(logFile))
                        {
                            using (var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                Interlocked.Exchange(ref _logFilePosition, fs.Length);
                            }
                            var capturedServerName = _connectedServerName;
                            var capturedLogFile = logFile;
                            _ = Task.Run(() => WatchLogFile(capturedLogFile, capturedServerName, token), token);
                            Output.Log(I18n.Get("anz_logfile_label", logFile), 1, ThisProgramName);
                        }
                    }

                    _rconClient = new RconClient();
                    bool rconConnected = false;
                    try
                    {
                        rconConnected = _rconClient.Connect(
                            Config.CurrentServer.RconHost,
                            Config.CurrentServer.RconPort,
                            Config.CurrentServer.RconPassword);
                    }
                    catch (Exception ex)
                    {
                        Output.Log(I18n.Get("anz_rcon_connect_failed", ex.Message), 2, ThisProgramName);
                    }

                    Output.Log(I18n.Get("anz_attached_server", Path.GetFileName(server.JarPath), server.ProcessId), 1, ThisProgramName);
                    if (rconConnected)
                    {
                        Output.Log(I18n.Get("anz_rcon_connected", Config.CurrentServer.RconHost, Config.CurrentServer.RconPort), 1, ThisProgramName);
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
                    _attachedProcessId = 0;
                    _attachedWindowTitle = "";
                    _connectedServerName = "";
                    Interlocked.Exchange(ref _logFilePosition, 0);
                    Output.ReportError(ex, false, I18n.Get("anz_connect_server_failed"));
                }
            }
        }

        /// <summary>
        /// RM模式日志文件监控: 等待日志文件创建后开始监控
        /// </summary>
        private static void StartRMLogFileMonitor(string workPath, CancellationToken cancellationToken)
        {
            string ThisProgramName = "Analyzer";
            string logFile = Path.Combine(workPath, "logs", "latest.log");

            // 记录监控开始时已存在的旧日志长度(用于检测轮转,防止重放上一会话的日志)
            long initialLength = -1;
            try
            {
                if (File.Exists(logFile))
                    initialLength = new FileInfo(logFile).Length;
            }
            catch { }

            // 等待日志文件创建/轮转(最多90秒)。
            // MC服务端进程启动后需经JVM引导(可达数十秒)才轮转latest.log:
            // 旧文件归档为日期.gz并新建latest.log。若在轮转前从0读取,会把上一会话的
            // 完整日志(含其关闭序列)重放到控制台,造成"服务器启动后立即被关闭"的假象。
            bool waitLogged = false;
            for (int i = 0; i < 180; i++)
            {
                if (cancellationToken.IsCancellationRequested) return;
                try
                {
                    if (!File.Exists(logFile))
                    {
                        initialLength = -1; // 文件尚不存在,等待创建
                    }
                    else
                    {
                        long len = new FileInfo(logFile).Length;
                        if (initialLength < 0)
                            break; // 全新创建的文件: 从头读取
                        if (len < initialLength)
                        {
                            // 文件比初始长度短: 已轮转,新文件从头读取
                            initialLength = 0;
                            break;
                        }
                    }
                }
                catch { }

                if (i == 20 && !waitLogged && initialLength >= 0)
                {
                    // JVM引导中,等待服务端轮转日志文件(旧日志不会重放)
                    waitLogged = true;
                    Output.Log(I18n.Get("anz_rm_wait_rotation"), 1, ThisProgramName);
                }
                Thread.Sleep(500);
            }

            if (!File.Exists(logFile))
            {
                Output.Log(I18n.Get("anz_rm_logfile_missing"), 2, ThisProgramName);
                return;
            }

            if (initialLength > 0)
            {
                // 超时未检测到轮转: 从旧文件长度处读取(仅采集新增行,避免重放旧会话日志)
                long pos = initialLength;
                try
                {
                    using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (fs.Length < pos) pos = fs.Length;
                }
                catch { }
                Interlocked.Exchange(ref _logFilePosition, pos);
                Output.Log(I18n.Get("anz_rm_append_mode", pos), 1, ThisProgramName);
            }
            else
            {
                Interlocked.Exchange(ref _logFilePosition, 0);
            }

            string serverName = _connectedServerName;
            Output.Log(I18n.Get("anz_rm_log_monitor_started", logFile), 1, ThisProgramName);

            WatchLogFile(logFile, serverName, cancellationToken);
        }

        private static void WatchLogFile(string logFile, string serverName, CancellationToken cancellationToken)
        {
            try
            {
                using var watcher = new FileSystemWatcher(Path.GetDirectoryName(logFile)!, Path.GetFileName(logFile))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                };

                var changedEvent = new AutoResetEvent(false);
                watcher.Changed += (s, e) => changedEvent.Set();
                watcher.EnableRaisingEvents = true;

                while (!cancellationToken.IsCancellationRequested)
                {
                    WaitHandle.WaitAny(new WaitHandle[] { changedEvent, cancellationToken.WaitHandle }, 2000);

                    if (cancellationToken.IsCancellationRequested)
                        break;

                    ReadNewLogLines(logFile, serverName);

                    // 玩家事件监听延迟(ticks)，人数越多建议越小
                    int ticks = Config.App.PlayerEvent?.Enabled == true
                        ? Math.Max(1, Config.App.PlayerEvent.Ticks)
                        : 50;
                    Thread.Sleep(ticks);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private static void ReadNewLogLines(string logFile, string serverName)
        {
            try
            {
                using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long currentPos = Interlocked.Read(ref _logFilePosition);
                if (fs.Length < currentPos)
                {
                    // 文件比上次读取位置短: 日志已轮转(旧文件归档、新文件从0开始),从头读取
                    currentPos = 0;
                    Interlocked.Exchange(ref _logFilePosition, 0);
                }
                else if (fs.Length == currentPos)
                {
                    return; // 无新内容
                }

                fs.Seek(currentPos, SeekOrigin.Begin);
                using var reader = new StreamReader(fs, Encoding.GetEncoding(0));

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        if (!ShouldHideConsole())
                            Output.Log(line, 0, serverName);
                        ProcessPlayerEventLine(line);

                        // RM模式: 日志文件行同步写入全局日志流缓存
                        if (!string.IsNullOrEmpty(_activeStreamKey))
                            LogStreamStore.Append(_activeStreamKey, line);
                    }
                }

                Interlocked.Exchange(ref _logFilePosition, fs.Position);
            }
            catch (IOException) { }
            catch { }
        }

        #endregion

        #region Common

        public static bool IsAttached => _attachedProcessId != 0 || _isRunModeActive;

        /// <summary>
        /// 获取 MC 服务端进程。
        /// Run 模式返回 _serverProcess；Attach/RCON 模式通过 _attachedProcessId 获取。
        /// </summary>
        public static Process? GetServerProcess()
        {
            // Run 模式：直接返回启动的进程
            if (_serverProcess != null && !_serverProcess.HasExited)
                return _serverProcess;

            // Attach/RCON 模式：通过 PID 获取
            if (_attachedProcessId != 0)
            {
                try
                {
                    var proc = Process.GetProcessById(_attachedProcessId);
                    if (proc != null && !proc.HasExited)
                        return proc;
                }
                catch { }
            }

            return null;
        }

        private static bool ShouldHideConsole()
        {
            var hideList = Config.App.HideConsoleServers;
            if (hideList == null || hideList.Count == 0)
                return false;
            return hideList.Contains(Config.App.CurrentServer);
        }

        public static void SendCommand(string command)
        {
            lock (_attachLock)
            {
                if (UsesStdinCommands)
                {
                    SendCommandRunMode(command);
                }
                else if (UsesRconCommands)
                {
                    SendCommandRconMode(command);
                }
            }
        }

        private static void SendCommandRunMode(string command)
        {
            string ThisProgramName = "Analyzer";

            if (!_isRunModeActive || _serverInput == null)
            {
                Output.Log(I18n.Get("anz_server_not_running_start_first"), 2, ThisProgramName);
                return;
            }

            try
            {
                _serverInput.WriteLine(command);
                _serverInput.Flush();
                Output.Log($"> {command}", 1, _connectedServerName);
            }
            catch (Exception ex)
            {
                Output.Log(I18n.Get("anz_send_cmd_failed", ex.Message), 3, ThisProgramName);
            }
        }

        private static void SendCommandRconMode(string command)
        {
            string ThisProgramName = "Analyzer";

            if (_attachedProcessId == 0)
            {
                Output.Log(I18n.Get("anz_not_attached_hint"), 2, ThisProgramName);
                return;
            }

            if (_rconClient == null || !_rconClient.IsConnected)
            {
                Output.Log(I18n.Get("anz_rcon_not_connected_send"), 2, ThisProgramName);
                return;
            }

            try
            {
                string response = _rconClient.SendCommand(command);
                Output.Log($"> {command}", 1, _connectedServerName);
                if (!string.IsNullOrWhiteSpace(response))
                {
                    Output.Log(Markup.Escape(response), 1, _connectedServerName);
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
        public static List<string> SendCommandWithCapture(string command, int timeoutMs = 3000, bool silent = false)
        {
            var result = new List<string>();
            lock (_attachLock)
            {
                if (UsesRconCommands && _rconClient != null && _rconClient.IsConnected)
                {
                    // RCON 模式：SendCommand 直接返回响应字符串
                    try
                    {
                        string response = _rconClient.SendCommand(command);
                        if (!silent)
                        {
                            Output.Log($"> {command}", 1, _connectedServerName);
                            if (!string.IsNullOrWhiteSpace(response))
                                Output.Log(Markup.Escape(response), 1, _connectedServerName);
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

                if (UsesStdinCommands && _isRunModeActive && _serverInput != null)
                {
                    // Run 模式：写入 stdin，然后从日志缓冲区收集新行
                    int startCount;
                    lock (_logBufferLock)
                    {
                        startCount = _logBuffer.Count;
                    }

                    try
                    {
                        _serverInput.WriteLine(command);
                        _serverInput.Flush();
                        if (!silent)
                            Output.Log($"> {command}", 1, _connectedServerName);
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
                        lock (_logBufferLock)
                        {
                            result.Clear();
                            for (int i = startCount; i < _logBuffer.Count; i++)
                                result.Add(_logBuffer[i]);
                        }
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
            return result;
        }

        // ===== TPS 查询(发送 tps 命令解析返回) =====

        private static DateTime _lastTpsQueryTime = DateTime.MinValue;
        private static (double T1m, double T5m, double T15m) _cachedTps = (-1, -1, -1);
        private static readonly TimeSpan _tpsQueryInterval = TimeSpan.FromSeconds(10);

        /// <summary>
        /// 发送 tps 命令并解析返回结果。
        /// 支持 Spark 插件格式："TPS from last 1m, 5m, 15m: 20.0*, 20.0, 19.9"
        /// 也兼容旧式单值格式。
        /// 带 10 秒节流缓存，避免频繁发送命令。
        /// </summary>
        public static (double Tps1m, double Tps5m, double Tps15m) QueryTps(int timeoutMs = 3000)
        {
            if (!IsRunModeActive && !IsAttached)
                return (-1, -1, -1);

            // 节流：10 秒内不重复查询
            if (DateTime.UtcNow - _lastTpsQueryTime < _tpsQueryInterval)
                return _cachedTps;

            _lastTpsQueryTime = DateTime.UtcNow;

            try
            {
                var lines = SendCommandWithCapture("tps", timeoutMs, silent: true);
                // Spark 格式: TPS from last 1m, 5m, 15m: 20.0*, 20.0, 19.9
                var sparkPattern = new Regex(
                    @"TPS\s+from\s+last\s+\d+m\s*,\s*\d+m\s*,\s*\d+m\s*:\s*([\d.]+)\s*\*?\s*,\s*([\d.]+)\s*\*?\s*,\s*([\d.]+)\s*\*?",
                    RegexOptions.IgnoreCase);
                // 旧式单值格式: TPS: 20.0  或  TPS from last 1m: 20.0
                var singlePattern = new Regex(
                    @"TPS\s*(?:from\s+last\s+\d+m)?\s*:\s*([\d.]+)",
                    RegexOptions.IgnoreCase);

                foreach (var line in lines)
                {
                    var m = sparkPattern.Match(line);
                    if (m.Success)
                    {
                        _cachedTps = (
                            double.TryParse(m.Groups[1].Value, out var t1) ? t1 : -1,
                            double.TryParse(m.Groups[2].Value, out var t5) ? t5 : -1,
                            double.TryParse(m.Groups[3].Value, out var t15) ? t15 : -1
                        );
                        return _cachedTps;
                    }
                    m = singlePattern.Match(line);
                    if (m.Success && double.TryParse(m.Groups[1].Value, out var t))
                    {
                        _cachedTps = (t, t, t);
                        return _cachedTps;
                    }
                }
            }
            catch { }

            return _cachedTps;
        }

        // ===== 玩家数量查询(发送 list 命令解析返回) =====

        private static DateTime _lastListQueryTime = DateTime.MinValue;
        private static (int Count, int Max) _cachedPlayers = (-1, -1);
        private static readonly TimeSpan _listQueryInterval = TimeSpan.FromSeconds(10);

        /// <summary>
        /// 发送 list 命令并解析在线玩家数。
        /// 解析格式："There are 2 of a max of 20 players online: ..."
        /// 带 10 秒节流缓存。
        /// </summary>
        public static (int Count, int Max) QueryPlayerCount(int timeoutMs = 3000)
        {
            if (!IsRunModeActive && !IsAttached)
                return (-1, -1);

            if (DateTime.UtcNow - _lastListQueryTime < _listQueryInterval)
                return _cachedPlayers;

            _lastListQueryTime = DateTime.UtcNow;

            try
            {
                var lines = SendCommandWithCapture("list", timeoutMs, silent: true);
                // 格式: There are 2 of a max of 20 players online: player1, player2
                // 也兼容中文端: 当前有 2 名玩家在线 / 共 20 个 slot
                var pattern = new Regex(
                    @"(?:There\s+(?:are|'re)\s+)(\d+)\s+of\s+a\s+max\s+of\s+(\d+)\s+players?\s+online",
                    RegexOptions.IgnoreCase);

                foreach (var line in lines)
                {
                    var m = pattern.Match(line);
                    if (m.Success)
                    {
                        _cachedPlayers = (
                            int.TryParse(m.Groups[1].Value, out var c) ? c : -1,
                            int.TryParse(m.Groups[2].Value, out var mx) ? mx : -1
                        );
                        return _cachedPlayers;
                    }
                }
            }
            catch { }

            return _cachedPlayers;
        }

        // ===== MC 进程 CPU 使用率(基于 TotalProcessorTime 两次采样) =====

        private static DateTime _lastMcCpuSampleTime = DateTime.MinValue;
        private static TimeSpan _lastMcCpuTime = TimeSpan.Zero;
        private static double _cachedMcCpuUsage = -1;

        /// <summary>
        /// 获取 MC 服务端 Java 进程的 CPU 使用率(%)。
        /// 基于 Process.TotalProcessorTime 两次采样计算，首次调用返回 -1。
        /// </summary>
        public static double GetMcCpuUsage()
        {
            var proc = GetServerProcess();
            if (proc == null || proc.HasExited)
            {
                _lastMcCpuSampleTime = DateTime.MinValue;
                _cachedMcCpuUsage = -1;
                return -1;
            }

            try
            {
                var now = DateTime.UtcNow;
                var cpuTime = proc.TotalProcessorTime;

                if (_lastMcCpuSampleTime == DateTime.MinValue)
                {
                    // 首次采样，只记录基准值
                    _lastMcCpuSampleTime = now;
                    _lastMcCpuTime = cpuTime;
                    return _cachedMcCpuUsage < 0 ? -1 : _cachedMcCpuUsage;
                }

                var wallElapsed = now - _lastMcCpuSampleTime;
                var cpuElapsed = cpuTime - _lastMcCpuTime;

                // 至少间隔 0.5 秒才更新，避免抖动
                if (wallElapsed.TotalSeconds >= 0.5)
                {
                    _cachedMcCpuUsage = (cpuElapsed.TotalMilliseconds /
                        (wallElapsed.TotalMilliseconds * Environment.ProcessorCount)) * 100;
                    _lastMcCpuSampleTime = now;
                    _lastMcCpuTime = cpuTime;
                }

                return _cachedMcCpuUsage < 0 ? -1 : _cachedMcCpuUsage;
            }
            catch
            {
                return -1;
            }
        }

        public static void Detach()
        {
            lock (_attachLock)
            {
                DetachCore();
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
                var lines = File.ReadAllLines(path, Encoding.GetEncoding(0)).ToList();
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
                        var lines = File.ReadAllLines(filePath, Encoding.GetEncoding(0)).ToList();
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
                        fallback = _logBuffer.ToList();
                    }

                    if (fallback.Count == 0 && IsRunModeActive && !string.IsNullOrWhiteSpace(Config.CurrentServer.WorkPath))
                    {
                        string logFile = Path.Combine(Config.CurrentServer.WorkPath, "logs", "latest.log");
                        if (File.Exists(logFile))
                        {
                            try
                            {
                                fallback = File.ReadAllLines(logFile, Encoding.GetEncoding(0)).ToList();
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

            int startBufferCount;
            lock (_logBufferLock)
            {
                startBufferCount = _logBuffer.Count;
            }

            int timeout = ContentManager.Regex.ClientGuide.Timeout;
            Output.Log(I18n.Get("anz_clientguide_waiting", timeout), 1, ThisProgramName);
            Output.Log(I18n.Get("anz_clientguide_wait_hint"), 1, ThisProgramName);

            var token = _clientGuideCts.Token;
            _ = Task.Run(() => MonitorClientGuide(startBufferCount, timeout, token), token);
        }

        private static void MonitorClientGuide(int startBufferCount, int timeout, CancellationToken cancellationToken)
        {
            try
            {
                string joinPattern = ContentManager.Regex.ClientGuide.PlayerJoin;
                string disconnectPattern = ContentManager.Regex.ClientGuide.PlayerDisconnect;
                string clientErrorPattern = ContentManager.Regex.ClientGuide.ClientError;
                string errorHandlerPattern = ContentManager.Regex.ClientGuide.ErrorHandler;

                Regex joinRegex = new Regex(joinPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                Regex disconnectRegex = new Regex(disconnectPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                Regex clientErrorRegex = new Regex(clientErrorPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                Regex errorHandlerRegex = new Regex(errorHandlerPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

                DateTime startTime = DateTime.Now;
                List<string> capturedMessages = new List<string>();
                int lastCheckedIndex = startBufferCount;

                while (!cancellationToken.IsCancellationRequested)
                {
                    if ((DateTime.Now - startTime).TotalSeconds > timeout)
                    {
                        Output.Log(I18n.Get("anz_clientguide_timeout"), 2, "ClientGuide");
                        ShowTimeoutTroubleshootTable();
                        return;
                    }

                    List<string> newLines = new List<string>();
                    lock (_logBufferLock)
                    {
                        int currentCount = _logBuffer.Count;
                        if (lastCheckedIndex > currentCount)
                            lastCheckedIndex = 0;

                        for (int i = lastCheckedIndex; i < currentCount; i++)
                        {
                            newLines.Add(_logBuffer[i]);
                        }
                        lastCheckedIndex = currentCount;
                    }

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
                try
                {
                    var regex = new Regex(entry.Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
                    var match = regex.Match(combinedError);
                    if (match.Success)
                    {
                        double score = (double)match.Value.Length / combinedError.Length;
                        matches.Add((entry, match, score));
                    }
                }
                catch (Exception ex)
                {
                    Output.Log(I18n.Get("anz_regex_invalid_topic", entry.Topic, ex.Message), 2, ThisProgramName);
                }
            }

            matches = matches.OrderByDescending(m => m.Match.Value.Length).ThenByDescending(m => m.Score).ToList();

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
                    table.AddRow("Action", Markup.Escape(entry.Action));
                    Scripts.ExecuteScriptByName(entry.Action);
                }

                AnsiConsole.Write(table);
                AnsiConsole.WriteLine();
            }
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

        private static void AddToLogBuffer(string line)
        {
            lock (_logBufferLock)
            {
                _logBuffer.Add(line);
                while (_logBuffer.Count > MaxLogBufferSize)
                {
                    _logBuffer.RemoveAt(0);
                }
            }

            // 同步写入全局日志流缓存(.fx get 可分析任意历史流,不受服务器停止影响)
            if (!string.IsNullOrEmpty(_activeStreamKey))
                LogStreamStore.Append(_activeStreamKey, line);
        }

        /// <summary>
        /// 实时检测日志行中的服务器崩溃信号(精准关键字单行命中即判定)
        /// 命中后通过 EventBus 发布事件，并触发 AI 崩溃检测任务(60秒去重)
        /// </summary>
        private static void ProcessCrashDetectionLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;

            // 精准崩溃标志(单行命中即判定，避免误报)
            bool isCrashSignal =
                line.Contains("---- Minecraft Crash Report ----", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("This crash report has been saved to", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Shutting down the server", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Server thread/FATAL", StringComparison.OrdinalIgnoreCase) ||
                (line.Contains("Server thread/ERROR", StringComparison.OrdinalIgnoreCase) &&
                 line.Contains("Crash", StringComparison.OrdinalIgnoreCase));

            if (!isCrashSignal) return;

            lock (_crashSignalLock)
            {
                // 去重：60 秒内不重复触发
                if ((DateTime.Now - _lastCrashSignalTime).TotalSeconds < 60) return;
                _lastCrashSignalTime = DateTime.Now;
            }

            Output.Log(I18n.Get("anz_crash_signal_detected", line), 2, "Analyzer");
            EventBus.Publish(new ServerCrashEvent(Config.App.CurrentServer, -1));

            // 触发 AI 崩溃检测任务(延迟2秒等待崩溃报告文件写入完成)
            if (Intelligence.AiAutoRunner.IsRunning)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(2000);
                        await Intelligence.AiAutoRunner.TriggerTaskAsync("crash_detect");
                    }
                    catch (Exception ex)
                    {
                        Output.Log(I18n.Get("anz_crash_detect_task_failed", ex.Message), 2, "Analyzer");
                    }
                });
            }
        }

        #region 玩家事件监听

        private static void ProcessPlayerEventLine(string line)
        {
            try
            {
                var pe = Config.App.PlayerEvent;
                if (pe == null || !pe.Enabled) return;

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
                try
                {
                    var match = Regex.Match(line, pattern);
                    if (match.Success)
                    {
                        onMatch(match);
                        return; // 一个行只触发一次
                    }
                }
                catch { } // 忽略无效正则
            }
        }

        private static string GetGroupValue(Match m, string name)
        {
            return m.Groups.TryGetValue(name, out var g) ? g.Value : "";
        }

        #endregion

        private static void DetachCore()
        {
            _clientGuideCts?.Cancel();
            Intelligence.StopAutoTips();

            _outputCts?.Cancel();
            _outputCts?.Dispose();
            _outputCts = null;

            if (_rconClient != null)
            {
                _rconClient.Disconnect();
                _rconClient = null;
            }

            _attachedProcessId = 0;
            _attachedWindowTitle = "";
            _connectedServerName = "";
            Interlocked.Exchange(ref _logFilePosition, 0);

            if (!_isRunModeActive && NeedsRunServer)
            {
                Output.Log(I18n.Get("anz_detached"), 1, "Analyzer");
            }
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

    public class MinecraftServerInfo
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = "";
        public string WindowTitle { get; set; } = "";
        public string JarPath { get; set; } = "";
        public string CommandLine { get; set; } = "";
        /// <summary>Java 可执行文件实际路径(来自 WMI ExecutablePath 或 MainModule.FileName)</summary>
        public string JavaExePath { get; set; } = "";
        /// <summary>与配置 JavaPath 的匹配优先级(越高越优先, 100=完全匹配)</summary>
        public int MatchScore { get; set; }
    }

    /// <summary>日志流类型</summary>
    internal enum LogStreamType
    {
        /// <summary>MC 服务端实时数据流(服务器启动时开始缓存)</summary>
        Server,
        /// <summary>外部导入的日志文件流</summary>
        External
    }

    /// <summary>
    /// 单条日志流缓存条目。
    /// 生命周期：服务器启动时创建，服务器停止后保留(供 .fx get 分析)，
    /// 仅当数据流过多(单流/全局超限)或程序重启时清空。
    /// </summary>
    internal class LogStreamEntry
    {
        public string Key { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public LogStreamType Type { get; set; } = LogStreamType.Server;
        public List<string> Lines { get; } = new List<string>();
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime LastAppendedAt { get; set; } = DateTime.Now;
        public bool IsLive { get; set; }
        /// <summary>因数据量超限被清空的次数(重置计数)</summary>
        public int ResetCount { get; set; }
    }

    /// <summary>
    /// 全局日志流缓存仓库：按标识(服务器ID/外部流名)缓存各服务器的实时日志数据流。
    /// 第一个服务器启动时开始缓存；程序重启自然清空(内存缓存)。
    /// </summary>
    internal static class LogStreamStore
    {
        /// <summary>单流最大行数，超出后清空该流(数据流过多时清空缓存)</summary>
        private const int MaxLinesPerStream = 5000;
        /// <summary>全部流合计最大行数，超出后按最久未追加顺序清空</summary>
        private const int MaxTotalLines = 50000;

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, LogStreamEntry> _streams = new Dictionary<string, LogStreamEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>确保流存在(已存在则复用并标记运行中)；重启场景在流中插入分段标记</summary>
        public static LogStreamEntry BeginServerStream(string key, string displayName)
        {
            lock (_lock)
            {
                if (!_streams.TryGetValue(key, out var entry))
                {
                    entry = new LogStreamEntry
                    {
                        Key = key,
                        DisplayName = string.IsNullOrWhiteSpace(displayName) ? key : displayName,
                        Type = LogStreamType.Server,
                        IsLive = true
                    };
                    _streams[key] = entry;
                }
                else
                {
                    // 服务器重启: 流保留,插入分段标记便于区分多次启动的日志
                    if (entry.Lines.Count > 0)
                        entry.Lines.Add($"===== [{key}] {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
                    entry.IsLive = true;
                    if (!string.IsNullOrWhiteSpace(displayName))
                        entry.DisplayName = displayName;
                }
                return entry;
            }
        }

        /// <summary>向指定流追加一行(容量控制: 单流超限清空该流; 全局超限清空最旧的流)</summary>
        public static void Append(string key, string line)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(line)) return;

            lock (_lock)
            {
                if (!_streams.TryGetValue(key, out var entry)) return;
                entry.Lines.Add(line);
                entry.LastAppendedAt = DateTime.Now;

                // 同步流式错误识别: 自动检测错误块并按来源保存(服务器启动即自动记录)
                ErrorStreamDetector.Feed(key, line, entry.Type == LogStreamType.External ? "external" : "server");

                // 单流数据量过多: 清空该流并记录重置
                if (entry.Lines.Count > MaxLinesPerStream)
                {
                    entry.Lines.Clear();
                    entry.ResetCount++;
                    entry.Lines.Add($"===== [{key}] {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
                    return;
                }

                // 全局数据量过多: 从最久未追加的流开始清空
                TrimTotal();
            }
        }

        private static void TrimTotal()
        {
            int total = _streams.Values.Sum(s => s.Lines.Count);
            while (total > MaxTotalLines && _streams.Count > 1)
            {
                var oldest = _streams.Values
                    .Where(s => !s.IsLive && s.Lines.Count > 0)
                    .OrderBy(s => s.LastAppendedAt)
                    .FirstOrDefault();
                if (oldest == null) break;
                total -= oldest.Lines.Count;
                oldest.Lines.Clear();
                oldest.ResetCount++;
            }
        }

        /// <summary>标记服务器流停止(流保留,数据仍可分析)</summary>
        public static void SetLive(string key, bool isLive)
        {
            lock (_lock)
            {
                if (_streams.TryGetValue(key, out var entry))
                    entry.IsLive = isLive;
            }
        }

        /// <summary>导入外部日志文件为流(同名覆盖旧导入),同时流式识别错误并按来源保存</summary>
        public static LogStreamEntry ImportExternal(string name, List<string> lines)
        {
            lock (_lock)
            {
                if (_streams.TryGetValue(name, out var existing) && existing.Type == LogStreamType.External)
                {
                    existing.Lines.Clear();
                    foreach (var l in lines) existing.Lines.Add(l);
                    existing.LastAppendedAt = DateTime.Now;
                    return existing;
                }
                var entry = new LogStreamEntry
                {
                    Key = name,
                    DisplayName = name,
                    Type = LogStreamType.External,
                    IsLive = false
                };
                foreach (var l in lines) entry.Lines.Add(l);
                _streams[name] = entry;
                return entry;
            }
        }

        public static bool Contains(string key)
        {
            lock (_lock) { return _streams.ContainsKey(key); }
        }

        public static LogStreamEntry? Get(string key)
        {
            lock (_lock)
            {
                return _streams.TryGetValue(key, out var e) ? e : null;
            }
        }

        /// <summary>获取全部流的快照(按创建时间排序)</summary>
        public static List<LogStreamEntry> GetAll()
        {
            lock (_lock)
            {
                return _streams.Values.OrderBy(s => s.CreatedAt).ToList();
            }
        }

        /// <summary>合并全部流的日志行(流之间以空行分隔,供"全部同时分析")</summary>
        public static List<string> GetMergedLines()
        {
            lock (_lock)
            {
                var result = new List<string>();
                foreach (var s in _streams.Values.OrderBy(s => s.CreatedAt))
                {
                    if (result.Count > 0) result.Add("");
                    result.AddRange(s.Lines);
                }
                return result;
            }
        }
    }

    /// <summary>
    /// 流式错误识别器: 日志行流入时按错误正则自动识别错误块,
    /// 记录数据来源(服务器标识/外部导入)并保存到错误分析结果。
    /// 服务器启动后自动工作,无需手动触发;服务器异常关闭也不丢失已识别的错误。
    /// </summary>
    internal static class ErrorStreamDetector
    {
        private class StreamState
        {
            public StringBuilder Buffer = new StringBuilder();
            public bool InError;
        }

        private static readonly object _lock = new();
        private static readonly Dictionary<string, StreamState> _states = new(StringComparer.OrdinalIgnoreCase);
        private static Regex? _handlerRegex;
        private static int _limit = 500;
        private static Dictionary<int, ErrorRecord> _records = new();
        private static bool _loaded;

        private static void EnsureInitialized()
        {
            if (_loaded) return;
            ContentManager.Initialize();
            string pattern = ContentManager.Regex.Console_Error.Handler;
            _limit = ContentManager.Regex.Console_Error.Limit;
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                try { _handlerRegex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase); }
                catch { _handlerRegex = null; }
            }
            _records = ContentManager.LoadErrorLog();
            _loaded = true;
        }

        /// <summary>流式喂入一行日志,自动识别错误块并保存(带来源)</summary>
        public static void Feed(string key, string line, string sourceType)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(line)) return;

            // 过滤命令噪音: 监控发送的 tps/list 等命令在部分服务端(如Folia无tps)不存在,
            // "Unknown or incomplete command...error" 会命中 Error 正则造成误记录
            if (line.Contains("Unknown or incomplete command", StringComparison.OrdinalIgnoreCase))
                return;

            lock (_lock)
            {
                EnsureInitialized();
                if (_handlerRegex == null) return;

                if (!_states.TryGetValue(key, out var state))
                {
                    state = new StreamState();
                    _states[key] = state;
                }

                if (_handlerRegex.IsMatch(line))
                {
                    // 新错误行: 先收尾上一块
                    if (state.InError && state.Buffer.Length > 0)
                        SaveBlock(key, sourceType, state);
                    state.InError = true;
                    state.Buffer.AppendLine(line);
                }
                else if (state.InError)
                {
                    bool isContinuation = line.TrimStart().StartsWith("at ")
                        || line.TrimStart().StartsWith("Caused by")
                        || line.TrimStart().StartsWith("...")
                        || string.IsNullOrWhiteSpace(line)
                        || line.Contains("Suppressed")
                        || _handlerRegex.IsMatch(line);

                    if (isContinuation)
                    {
                        state.Buffer.AppendLine(line);
                    }
                    else
                    {
                        SaveBlock(key, sourceType, state);
                        state.InError = false;
                    }
                }
            }
        }

        /// <summary>批量喂入并收尾(供 .fx add 外部导入)</summary>
        public static int FeedLines(string key, IEnumerable<string> lines, string sourceType)
        {
            int before = TotalCount;
            foreach (var line in lines)
                Feed(key, line, sourceType);
            Flush(key, sourceType);
            return TotalCount - before;
        }

        /// <summary>收尾未闭合的错误块(流结束/导入完成时)</summary>
        public static void Flush(string key, string sourceType)
        {
            lock (_lock)
            {
                EnsureInitialized();
                if (_states.TryGetValue(key, out var state) && state.InError && state.Buffer.Length > 0)
                {
                    SaveBlock(key, sourceType, state);
                    state.InError = false;
                }
            }
        }

        public static int TotalCount
        {
            get { lock (_lock) { EnsureInitialized(); return _records.Count; } }
        }

        /// <summary>重置内存副本(.fx del 删除记录后调用,防止旧记录复活)</summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _records = new Dictionary<int, ErrorRecord>();
                _loaded = false;
            }
        }

        private static void SaveBlock(string key, string sourceType, StreamState state)
        {
            string text = state.Buffer.ToString().Trim();
            state.Buffer.Clear();
            if (text.Length == 0) return;
            if (text.Length > _limit) text = text.Substring(0, _limit) + "...";

            // 全局内容去重(与手动分析行为一致)
            if (_records.Values.Any(r => r.Content == text)) return;

            int idx = _records.Count == 0 ? 1 : _records.Keys.Max() + 1;
            _records[idx] = new ErrorRecord
            {
                Content = text,
                Source = key,
                SourceType = sourceType,
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            ContentManager.SaveErrorLog(_records, quiet: true);
            Output.Log(I18n.Get("anz_fx_auto_saved", idx, key), 1, "Analyzer");
        }
    }

    internal class RconClient
    {
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private int _requestId = 0;
        private readonly object _sendLock = new object();

        public bool IsConnected => _tcpClient?.Connected == true;

        private const int PacketTypeLogin = 3;
        private const int PacketTypeCommand = 2;

        public bool Connect(string host, int port, string password)
        {
            try
            {
                _tcpClient = new TcpClient();
                var result = _tcpClient.BeginConnect(host, port, null, null);
                bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5));

                if (!success)
                {
                    _tcpClient.Dispose();
                    _tcpClient = null;
                    return false;
                }

                _tcpClient.EndConnect(result);
                _stream = _tcpClient.GetStream();

                SendPacket(PacketTypeLogin, password);
                var response = ReadPacket();

                if (response == null || response.Type == -1)
                {
                    Disconnect();
                    return false;
                }

                return true;
            }
            catch
            {
                Disconnect();
                return false;
            }
        }

        public string SendCommand(string command)
        {
            lock (_sendLock)
            {
                if (!IsConnected || _stream == null)
                    throw new InvalidOperationException("RCON 未连接");

                _requestId++;
                SendPacket(PacketTypeCommand, command);

                var response = ReadPacket();
                return response?.Body ?? "";
            }
        }

        public void Disconnect()
        {
            try
            {
                _stream?.Close();
                _stream = null;
                _tcpClient?.Close();
                _tcpClient = null;
            }
            catch { }
        }

        private void SendPacket(int type, string body)
        {
            if (_stream == null) return;

            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            int length = 4 + 4 + bodyBytes.Length + 2;

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(length);
            writer.Write(_requestId);
            writer.Write(type);
            writer.Write(bodyBytes);
            writer.Write((byte)0);
            writer.Write((byte)0);

            _stream.Write(ms.ToArray(), 0, (int)ms.Length);
            _stream.Flush();
        }

        private RconPacket? ReadPacket()
        {
            if (_stream == null) return null;

            try
            {
                var lengthBuffer = new byte[4];
                int read = _stream.Read(lengthBuffer, 0, 4);
                if (read < 4) return null;

                int length = BitConverter.ToInt32(lengthBuffer, 0);
                if (length <= 0 || length > 65536) return null;

                var dataBuffer = new byte[length];
                int totalRead = 0;
                while (totalRead < length)
                {
                    int bytesRead = _stream.Read(dataBuffer, totalRead, length - totalRead);
                    if (bytesRead == 0) return null;
                    totalRead += bytesRead;
                }

                int requestId = BitConverter.ToInt32(dataBuffer, 0);
                int type = BitConverter.ToInt32(dataBuffer, 4);

                string body = "";
                if (length > 8)
                {
                    int bodyLength = length - 8 - 2;
                    if (bodyLength > 0)
                    {
                        body = Encoding.UTF8.GetString(dataBuffer, 8, bodyLength);
                    }
                }

                return new RconPacket { RequestId = requestId, Type = type, Body = body };
            }
            catch
            {
                return null;
            }
        }

        private class RconPacket
        {
            public int RequestId { get; set; }
            public int Type { get; set; }
            public string Body { get; set; } = "";
        }
    }

    internal class ClientGuideMatch
    {
        public int Index { get; set; }
        public ClientGuideEntry Entry { get; set; } = null!;
        public double Similarity { get; set; }
        public string MatchedMessage { get; set; } = "";
    }

    internal class ConfigDiffEntry
    {
        public int Index { get; set; }
        public string RelativePath { get; set; } = "";
        public string BackupPath { get; set; } = "";
        public string CurrentPath { get; set; } = "";
    }

    internal class PluginEntry
    {
        public int Index { get; set; }
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsDisabled { get; set; }
    }

    internal class ModEntry
    {
        public int Index { get; set; }
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsDisabled { get; set; }
    }
}
