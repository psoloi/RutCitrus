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
        private static string _currentMode = "RCON";
        private static bool _isRunModeActive = false;

        internal static readonly object _logBufferLock = new object();
        internal static readonly List<string> _logBuffer = new List<string>();
        private const int MaxLogBufferSize = 5000;

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
        /// <summary>Run模式 - 启动MC服务端作为子进程，通过stdin发送命令</summary>
        public static bool IsRunMode => _currentMode == "RUN";
        /// <summary>Rcon模式 - 启动MC服务端作为子进程读取日志 + RCON发送命令</summary>
        public static bool IsRconMode => _currentMode == "RCON";
        /// <summary>OnlyRcon模式 - 连接已运行的MC服务端(日志文件+RCON)</summary>
        public static bool IsOnlyRconMode => _currentMode == "ONLYRCON";
        /// <summary>Management模式 - 启动MC服务端读取日志，命令由管理模式处理</summary>
        public static bool IsManagementMode => _currentMode == "MANAGEMENT";
        /// <summary>是否需要启动MC服务端作为子进程(Run/Rcon/Management)</summary>
        public static bool NeedsRunServer => _currentMode == "RUN" || _currentMode == "RCON" || _currentMode == "MANAGEMENT";
        /// <summary>是否使用RCON发送命令(Rcon/OnlyRcon)</summary>
        public static bool UsesRconCommands => _currentMode == "RCON" || _currentMode == "ONLYRCON";

        public static void Initialize()
        {
            _currentMode = Config.CurrentServer.AnalyzerMode.ToUpperInvariant();
            if (_currentMode != "RUN" && _currentMode != "RCON" && _currentMode != "ONLYRCON" && _currentMode != "MANAGEMENT")
            {
                _currentMode = "MANAGEMENT";
            }
        }

        #region RUN Mode

        public static bool IsRunModeActive => _isRunModeActive;

        public static void StartServer()
        {
            string ThisProgramName = "Analyzer";

            if (!NeedsRunServer)
            {
                Output.Log("当前模式为 OnlyRcon，无法启动服务端。请在配置文件中设置 analyzer_mode 为 Run/Rcon/Management。", 2, ThisProgramName);
                return;
            }

            if (_isRunModeActive)
            {
                Output.Log("服务端已在运行中。", 2, ThisProgramName);
                return;
            }

            string? workPath = Config.CurrentServer.WorkPath;
            string flags = Config.CurrentServer.RunServerFlags;

            if (string.IsNullOrWhiteSpace(workPath))
            {
                workPath = FindServerPathFromScan();
                if (string.IsNullOrEmpty(workPath))
                {
                    Output.Log("未配置工作目录且无法自动检测。请在配置文件中设置 work_path。", 2, ThisProgramName);
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(flags))
            {
                Output.Log("未配置启动参数。请在配置文件中设置 run_server_flags。", 2, ThisProgramName);
                return;
            }

            if (!Directory.Exists(workPath))
            {
                Output.Log($"工作目录不存在: {workPath}", 3, ThisProgramName);
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
                            Output.Log("已自动同意 EULA（配置: auto_agree_eula = true）。", 1, ThisProgramName);
                        }
                        else
                        {
                            Output.Log("检测到 EULA 未同意。", 2, ThisProgramName);
                            Output.Log("Minecraft EULA 说明: https://www.minecraft.net/eula", 1, ThisProgramName);
                            bool agree = AnsiConsole.Confirm("是否同意 Minecraft EULA？(阅读 https://www.minecraft.net/eula)", false);
                            if (agree)
                            {
                                eulaContent = eulaContent.Replace("eula=false", "eula=true");
                                File.WriteAllText(eulaPath, eulaContent);
                                Output.Log("已同意 EULA。", 1, ThisProgramName);
                            }
                            else
                            {
                                Output.Log("未同意 EULA，服务端无法启动。", 2, ThisProgramName);
                                return;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Output.Log($"EULA 检查失败: {ex.Message}", 2, ThisProgramName);
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
                        AddToLogBuffer(e.Data);
                        if (!ShouldHideConsole())
                            Output.Log(e.Data, 0, _connectedServerName);

                        ProcessPlayerEventLine(e.Data);
                        ProcessCrashDetectionLine(e.Data);

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
                                                Output.Log("已自动同意 EULA，正在重启服务端...", 1, "Analyzer");
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
                                                    Output.Log("服务端因 EULA 未同意而关闭。", 2, "Analyzer");
                                                    Output.Log("Minecraft EULA 说明: https://www.minecraft.net/eula", 1, "Analyzer");
                                                    bool agree = AnsiConsole.Confirm("是否同意 Minecraft EULA？(阅读 https://www.minecraft.net/eula)", false);
                                                    if (agree)
                                                    {
                                                        try
                                                        {
                                                            content = File.ReadAllText(eulaPath);
                                                            content = content.Replace("eula=false", "eula=true");
                                                            File.WriteAllText(eulaPath, content);
                                                            Output.Log("已同意 EULA。", 1, "Analyzer");

                                                            bool restart = AnsiConsole.Confirm("是否重新启动服务端？", true);
                                                            if (restart)
                                                            {
                                                                StartServer();
                                                                Output.Log("服务端已重新启动。", 1, "Analyzer");
                                                            }
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            Output.Log($"同意EULA失败: {ex.Message}", 2, "Analyzer");
                                                        }
                                                    }
                                                    else
                                                    {
                                                        Output.Log("未同意 EULA，服务端无法运行。稍后可手动修改 eula.txt 后使用 .server start 启动。", 2, "Analyzer");
                                                    }
                                                });
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Output.Log($"EULA处理失败: {ex.Message}", 2, "Analyzer");
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
                        AddToLogBuffer(e.Data);
                        if (!ShouldHideConsole())
                            Output.Log(e.Data, 0, _connectedServerName);
                        ProcessPlayerEventLine(e.Data);
                        ProcessCrashDetectionLine(e.Data);
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

                _outputCts = new CancellationTokenSource();
                _ = Task.Run(() => MonitorServerProcess(_outputCts.Token), _outputCts.Token);

                Output.Log($"已启动服务端 (PID: {_serverProcess.Id}) 模式: {_currentMode}", 1, ThisProgramName);
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
                Output.Log($"工作目录: {workPath}", 1, ThisProgramName);
                Output.Log($"启动参数: java {flags}", 1, ThisProgramName);

                // Rcon模式：启动后尝试RCON连接
                if (UsesRconCommands)
                {
                    _rconClient = new RconClient();
                    Output.Log("Rcon模式：将在服务端启动完成后自动连接RCON...", 1, ThisProgramName);
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
                                    Output.Log($"RCON 已连接 ({Config.CurrentServer.RconHost}:{Config.CurrentServer.RconPort})", 1, ThisProgramName);
                                    break;
                                }
                            }
                            catch { }
                        }
                    });
                    Output.Log("使用 / 开头的命令通过RCON发送到服务端。", 1, ThisProgramName);
                }
                else if (IsRunMode)
                {
                    Output.Log("使用 / 开头的命令发送到服务端。", 1, ThisProgramName);
                }
                else if (IsManagementMode)
                {
                    Output.Log("Management模式：日志读取已启动，使用stdin发送命令。", 1, ThisProgramName);
                }

                Output.Log("输入 .server stop 停止服务端。", 1, ThisProgramName);

                Intelligence.StartAutoTips();
            }
            catch (Exception ex)
            {
                Output.ReportError(ex, false, "启动服务端失败");
                CleanupRunMode();
            }
        }

        public static void StopServer()
        {
            string ThisProgramName = "Analyzer";

            if (!_isRunModeActive)
            {
                Output.Log("服务端未在运行。", 1, ThisProgramName);
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
                Output.Log($"停止服务端时出错: {ex.Message}", 2, ThisProgramName);
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
                        Output.Log($"服务端进程已退出 (退出码: {exitCode})", 0, "Analyzer");
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
                                Output.Log($"[yellow]自动重启[/] 第 {_restartAttemptCount} 次{(maxRetries > 0 ? $"/{maxRetries}" : "")}，5秒后重启...", 1, "Analyzer");
                                Thread.Sleep(5000);
                                if (!_userInitiatedStop)
                                {
                                    StartServer();
                                }
                            }
                            else
                            {
                                Output.Log($"已达到最大自动重启次数 ({maxRetries})，不再尝试。", 2, "Analyzer");
                                _restartAttemptCount = 0;
                            }
                        }
                        else
                        {
                            _restartAttemptCount = 0;
                            EventBus.Publish(new ServerStopEvent(Config.App.CurrentServer));
                            Output.Log("服务端已停止。", 1, "Analyzer");
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
            Output.Log("正在扫描运行中的 Minecraft 服务端...", 1, ThisProgramName);

            lock (_scanLock)
            {
                _lastScanResults = ScanMinecraftServers();
            }
            if (_lastScanResults.Count == 0)
            {
                Output.Log("未找到运行中的 Minecraft 服务端。", 2, ThisProgramName);
                return;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("序号", c => c.Alignment(Justify.Center).Width(6))
                .AddColumn("进程ID", c => c.Alignment(Justify.Center).Width(10))
                .AddColumn("窗口标题", c => c.Width(30))
                .AddColumn("Jar文件路径", c => c.Width(50));

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
            Output.Log($"共找到 {_lastScanResults.Count} 个服务端。使用 .server connect <序号> 连接。", 1, ThisProgramName);
        }

        public static void ConnectToServer(int index)
        {
            string ThisProgramName = "Analyzer";

            if (!IsOnlyRconMode)
            {
                Output.Log("当前模式不支持 .server connect。OnlyRcon模式下才可连接已运行的服务端。", 2, ThisProgramName);
                return;
            }

            List<MinecraftServerInfo> scanResults;
            lock (_scanLock) { scanResults = _lastScanResults; }

            if (scanResults.Count == 0)
            {
                Output.Log("没有可用的服务端列表，请先使用 .server get 扫描。", 2, ThisProgramName);
                return;
            }

            if (index < 1 || index > scanResults.Count)
            {
                Output.Log($"无效的序号，请输入 1 到 {scanResults.Count} 之间的数字。", 2, ThisProgramName);
                return;
            }

            var selectedServer = scanResults[index - 1];
            AttachToServerRcon(selectedServer, index.ToString());
            Intelligence.StartAutoTips();
        }

        public static void ConnectToServerByPid(int pid)
        {
            string ThisProgramName = "Analyzer";

            if (!IsOnlyRconMode)
            {
                Output.Log("当前模式不支持 .server connect。OnlyRcon模式下才可连接已运行的服务端。", 2, ThisProgramName);
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
                Output.Log($"无法连接到进程 {pid}: {ex.Message}", 3, ThisProgramName);
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
                    string? serverDir = ResolveServerDirectory(server);
                    if (string.IsNullOrEmpty(serverDir))
                    {
                        Output.Log("无法确定服务端工作目录，连接失败。", 2, ThisProgramName);
                        return;
                    }

                    string logFile = Path.Combine(serverDir, "logs", "latest.log");
                    if (!File.Exists(logFile))
                    {
                        Output.Log($"找不到日志文件: {logFile}", 2, ThisProgramName);
                        return;
                    }

                    _attachedProcessId = server.ProcessId;
                    _attachedWindowTitle = server.WindowTitle;
                    _connectedServerName = serverName;

                    using (var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        Interlocked.Exchange(ref _logFilePosition, fs.Length);
                    }

                    _outputCts = new CancellationTokenSource();
                    var token = _outputCts.Token;
                    var capturedServerName = _connectedServerName;
                    var capturedLogFile = logFile;

                    _ = Task.Run(() => WatchLogFile(capturedLogFile, capturedServerName, token), token);

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
                        Output.Log($"RCON 连接失败: {ex.Message}，命令发送将不可用。", 2, ThisProgramName);
                    }

                    Output.Log($"已连接服务端: {Path.GetFileName(server.JarPath)} (PID: {server.ProcessId})", 1, ThisProgramName);
                    Output.Log($"日志文件: {logFile}", 1, ThisProgramName);
                    if (rconConnected)
                    {
                        Output.Log($"RCON 已连接 ({Config.CurrentServer.RconHost}:{Config.CurrentServer.RconPort})", 1, ThisProgramName);
                    }
                    else
                    {
                        Output.Log("RCON 未连接，命令发送不可用。请检查 RCON 配置。", 2, ThisProgramName);
                    }
                    Output.Log("使用 / 开头的命令发送到服务端。", 1, ThisProgramName);
                    Output.Log("输入 .server detach 可断开连接。", 1, ThisProgramName);
                }
                catch (Exception ex)
                {
                    _attachedProcessId = 0;
                    _attachedWindowTitle = "";
                    _connectedServerName = "";
                    Interlocked.Exchange(ref _logFilePosition, 0);
                    Output.ReportError(ex, false, "连接服务端失败");
                }
            }
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
                if (fs.Length <= currentPos)
                    return;

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
                if (IsRunMode || IsManagementMode)
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
                Output.Log("服务端未运行，请先使用 .server start 启动。", 2, ThisProgramName);
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
                Output.Log($"发送命令失败: {ex.Message}", 3, ThisProgramName);
            }
        }

        private static void SendCommandRconMode(string command)
        {
            string ThisProgramName = "Analyzer";

            if (_attachedProcessId == 0)
            {
                Output.Log("未连接到 Minecraft 服务端，请先使用 .server get 扫描并连接。", 2, ThisProgramName);
                return;
            }

            if (_rconClient == null || !_rconClient.IsConnected)
            {
                Output.Log("RCON 未连接，无法发送命令。请检查 RCON 配置后重新连接。", 2, ThisProgramName);
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
                Output.Log($"RCON 发送命令失败: {ex.Message}", 3, ThisProgramName);
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
                        Output.Log($"RCON 发送命令失败: {ex.Message}", 3, "Analyzer");
                    }
                    return result;
                }

                if ((IsRunMode || IsManagementMode) && _isRunModeActive && _serverInput != null)
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
                        Output.Log($"发送命令失败: {ex.Message}", 3, "Analyzer");
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

        public static void AnalyzeErrors(string? filePath = null)
        {
            string ThisProgramName = "Analyzer";

            ContentManager.Initialize();

            string handlerPattern = ContentManager.Regex.Console_Error.Handler;
            int limit = ContentManager.Regex.Console_Error.Limit;

            if (string.IsNullOrWhiteSpace(handlerPattern))
            {
                Output.Log("正则表达式配置为空，无法分析。", 2, ThisProgramName);
                return;
            }

            Regex handlerRegex;
            try
            {
                handlerRegex = new Regex(handlerPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            }
            catch (Exception ex)
            {
                Output.Log($"正则表达式无效: {ex.Message}", 3, ThisProgramName);
                return;
            }

            List<string> logLines;

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                if (!File.Exists(filePath))
                {
                    Output.Log($"文件不存在: {filePath}", 2, ThisProgramName);
                    return;
                }

                try
                {
                    logLines = File.ReadAllLines(filePath, Encoding.GetEncoding(0)).ToList();
                    Output.Log($"从外部文件读取了 {logLines.Count} 行: {filePath}", 1, ThisProgramName);
                }
                catch (Exception ex)
                {
                    Output.Log($"读取文件失败: {ex.Message}", 3, ThisProgramName);
                    return;
                }
            }
            else
            {
                if (!IsRunModeActive && !IsAttached)
                {
                    Output.Log("未连接到 MC 服务端，无法分析错误日志。使用 .fx get <路径> 分析外部日志文件。", 2, ThisProgramName);
                    return;
                }

                lock (_logBufferLock)
                {
                    logLines = _logBuffer.ToList();
                }

                if (logLines.Count == 0)
                {
                    if (IsRunModeActive && !string.IsNullOrWhiteSpace(Config.CurrentServer.WorkPath))
                    {
                        string logFile = Path.Combine(Config.CurrentServer.WorkPath, "logs", "latest.log");
                        if (File.Exists(logFile))
                        {
                            try
                            {
                                logLines = File.ReadAllLines(logFile, Encoding.GetEncoding(0)).ToList();
                                Output.Log($"从日志文件读取了 {logLines.Count} 行。", 1, ThisProgramName);
                            }
                            catch (Exception ex)
                            {
                                Output.Log($"读取日志文件失败: {ex.Message}", 3, ThisProgramName);
                                return;
                            }
                        }
                    }

                    if (logLines.Count == 0)
                    {
                        Output.Log("没有可分析的日志内容。", 2, ThisProgramName);
                        return;
                    }
                }
            }

            var errors = new Dictionary<int, string>();
            int errorIndex = 1;
            var currentError = new StringBuilder();
            bool inError = false;

            var existingErrors = ContentManager.LoadErrorLog();
            if (existingErrors.Count > 0)
            {
                errorIndex = existingErrors.Keys.Max() + 1;
                foreach (var kv in existingErrors)
                {
                    errors[kv.Key] = kv.Value;
                }
            }

            for (int i = 0; i < logLines.Count; i++)
            {
                string line = logLines[i];

                if (handlerRegex.IsMatch(line))
                {
                    if (inError && currentError.Length > 0)
                    {
                        string errorText = currentError.ToString().Trim();
                        if (errorText.Length > limit)
                            errorText = errorText.Substring(0, limit) + "...";

                        if (!errors.Values.Contains(errorText))
                        {
                            errors[errorIndex++] = errorText;
                        }
                        currentError.Clear();
                    }

                    inError = true;
                    currentError.AppendLine(line);
                }
                else if (inError)
                {
                    bool isContinuation = line.TrimStart().StartsWith("at ")
                        || line.TrimStart().StartsWith("Caused by")
                        || line.TrimStart().StartsWith("...")
                        || string.IsNullOrWhiteSpace(line)
                        || line.Contains("Suppressed")
                        || handlerRegex.IsMatch(line);

                    if (isContinuation)
                    {
                        currentError.AppendLine(line);
                    }
                    else
                    {
                        string errorText = currentError.ToString().Trim();
                        if (errorText.Length > limit)
                            errorText = errorText.Substring(0, limit) + "...";

                        if (!errors.Values.Contains(errorText))
                        {
                            errors[errorIndex++] = errorText;
                        }
                        currentError.Clear();
                        inError = false;
                    }
                }
            }

            if (inError && currentError.Length > 0)
            {
                string errorText = currentError.ToString().Trim();
                if (errorText.Length > limit)
                    errorText = errorText.Substring(0, limit) + "...";

                if (!errors.Values.Contains(errorText))
                {
                    errors[errorIndex++] = errorText;
                }
            }

            int newCount = errors.Count - existingErrors.Count;

            if (errors.Count == 0)
            {
                Output.Log("未检测到错误日志。", 1, ThisProgramName);
                return;
            }

            ContentManager.SaveErrorLog(errors);

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("编号", c => c.Alignment(Justify.Center).Width(8))
                .AddColumn("错误摘要", c => c.Width(80));

            foreach (var kv in errors)
            {
                string summary = kv.Value.Split('\n').FirstOrDefault() ?? "";
                if (summary.Length > 80)
                    summary = summary.Substring(0, 77) + "...";

                table.AddRow(kv.Key.ToString(), Markup.Escape(summary));
            }

            AnsiConsole.Write(table);
            Output.Log($"共识别 {errors.Count} 个错误（新增 {newCount} 个），结果已保存到 fx_save_error.yml。", 1, ThisProgramName);
        }

        public static void ListErrors(int? index)
        {
            string ThisProgramName = "Analyzer";
            var errors = ContentManager.LoadErrorLog();

            if (errors.Count == 0)
            {
                Output.Log("没有已保存的错误分析结果。", 1, ThisProgramName);
                return;
            }

            if (index.HasValue)
            {
                if (errors.TryGetValue(index.Value, out string? errorText))
                {
                    var panel = new Panel(Markup.Escape(errorText))
                        .Header($"[[第 {index.Value} 次分析结果]]")
                        .Border(BoxBorder.Rounded);
                    AnsiConsole.Write(panel);
                }
                else
                {
                    Output.Log($"没有编号为 {index.Value} 的分析结果。", 2, ThisProgramName);
                }
                return;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("编号", c => c.Alignment(Justify.Center).Width(8))
                .AddColumn("错误摘要", c => c.Width(80));

            foreach (var kv in errors.OrderBy(kv => kv.Key))
            {
                string summary = kv.Value.Split('\n').FirstOrDefault() ?? "";
                if (summary.Length > 80)
                    summary = summary.Substring(0, 77) + "...";

                table.AddRow(kv.Key.ToString(), Markup.Escape(summary));
            }

            AnsiConsole.Write(table);
            Output.Log($"共 {errors.Count} 条错误分析结果。", 1, ThisProgramName);
        }

        public static void DeleteErrors()
        {
            ContentManager.DeleteErrorLog();
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
                        Output.Log("没有已匹配的客户端错误结果，请先使用 .fx clientguide 开始诊断。", 2, ThisProgramName);
                        return;
                    }

                    int idx = selectedIndex.Value - 1;
                    if (idx < 0 || idx >= _lastClientGuideMatches.Count)
                    {
                        Output.Log($"无效的序号，请输入 1 到 {_lastClientGuideMatches.Count} 之间的数字。", 2, ThisProgramName);
                        return;
                    }

                    var match = _lastClientGuideMatches[idx];
                    ShowClientGuideSolution(match);
                }
                return;
            }

            if (!IsRunModeActive && !IsAttached)
            {
                Output.Log("未连接到 MC 服务端，无法使用客户端诊断功能。", 2, ThisProgramName);
                return;
            }

            ContentManager.Initialize();

            if (_clientGuideActive)
            {
                Output.Log("客户端诊断已在运行中，请等待结果或使用 .fx clientguide <序号> 查看已匹配的结果。", 2, ThisProgramName);
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
            Output.Log($"正在等待玩家加入服务端... (超时: {timeout}秒)", 1, ThisProgramName);
            Output.Log("当玩家加入或断开连接时，将自动匹配客户端错误并给出解决方案。", 1, ThisProgramName);

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
                        Output.Log("似乎玩家未加入已经超时，请看下表检查服务端方面问题：", 2, "ClientGuide");
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
                            Output.Log($"[[检测到玩家加入]] {Markup.Escape(line)}", 1, "ClientGuide");
                        }

                        if (disconnectRegex.IsMatch(line) || clientErrorRegex.IsMatch(line) || errorHandlerRegex.IsMatch(line))
                        {
                            capturedMessages.Add(line);
                            Output.Log($"[[检测到断开/错误]] {Markup.Escape(line)}", 1, "ClientGuide");
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
                Output.ReportError(ex, false, "客户端诊断出错");
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
                Output.Log("未匹配到已知的客户端错误类型。", 2, "ClientGuide");
                Output.Log("原始消息:", 1, "ClientGuide");
                foreach (var msg in messages)
                {
                    Output.Log(msg, 0, "ClientGuide");
                }
                return;
            }

            Output.Log($"共匹配 {matches.Count} 个可能的错误：", 1, "ClientGuide");
            foreach (var match in matches)
            {
                Output.Log($"  {match.Index}. {Markup.Escape(match.Entry.Keyword)} (匹配度: {match.Similarity:P0}) - {Markup.Escape(match.Entry.Description)}", 1, "ClientGuide");
            }
            Output.Log("使用 .fx clientguide <序号> 查看详细解决方案。", 1, "ClientGuide");
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
            var rule = new Rule($"[cyan]客户端错误诊断 - 匹配项 {match.Index}[/]");
            AnsiConsole.Write(rule);

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("项目", c => c.Width(15))
                .AddColumn("内容", c => c.Width(65));

            table.AddRow("错误关键词", Markup.Escape(match.Entry.Keyword));
            table.AddRow("错误描述", Markup.Escape(match.Entry.Description));
            table.AddRow("解决方案", Markup.Escape(match.Entry.Solution));
            table.AddRow("匹配度", $"{match.Similarity:P0}");

            string displayMessage = match.MatchedMessage.Length > 200
                ? match.MatchedMessage.Substring(0, 197) + "..."
                : match.MatchedMessage;
            table.AddRow("原始消息", Markup.Escape(displayMessage));

            AnsiConsole.Write(table);
        }

        private static void ShowTimeoutTroubleshootTable()
        {
            var root = new Tree("[yellow]服务端方面问题检查[/]");

            foreach (var item in ContentManager.GetAllTroubleshoot())
            {
                var node = root.AddNode($"[cyan]{Markup.Escape(item.Title)}[/]");
                node.AddNode($"[white]问题:[/] {Markup.Escape(item.Problem)}");
                node.AddNode($"[green]解决:[/] {Markup.Escape(item.Solution)}");
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
                Output.Log("没有错误分析结果，请先使用 .fx get 分析错误日志。", 2, ThisProgramName);
                return;
            }

            if (string.IsNullOrWhiteSpace(rangeArg))
            {
                Output.Log("用法: .fx base <n> 或 .fx base <n-m>", 1, ThisProgramName);
                Output.Log("  n   - 分析第n个错误", 1, ThisProgramName);
                Output.Log("  n-m - 合并第n到m个错误后分析", 1, ThisProgramName);
                Output.Log($"当前共有 {errors.Count} 条错误记录，使用 .fx list 查看。", 1, ThisProgramName);
                return;
            }

            List<int>? targetIndices = ParseRange(rangeArg, errors.Keys.ToList());
            if (targetIndices == null || targetIndices.Count == 0)
            {
                Output.Log($"无效的范围参数: {rangeArg}", 2, ThisProgramName);
                return;
            }

            var sb = new StringBuilder();
            foreach (int idx in targetIndices)
            {
                if (errors.TryGetValue(idx, out string? errorText))
                {
                    sb.AppendLine(errorText);
                    sb.AppendLine("---");
                }
                else
                {
                    Output.Log($"编号 {idx} 的错误记录不存在。", 2, ThisProgramName);
                    return;
                }
            }

            string combinedError = sb.ToString();
            Output.Log($"正在分析 {targetIndices.Count} 条错误记录...", 1, ThisProgramName);

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
                    Output.Log($"正则表达式无效 [{entry.Topic}]: {ex.Message}", 2, ThisProgramName);
                }
            }

            matches = matches.OrderByDescending(m => m.Match.Value.Length).ThenByDescending(m => m.Score).ToList();

            if (matches.Count == 0)
            {
                Output.Log("未匹配到已知的基础错误模式。", 2, ThisProgramName);
                Output.Log("原始错误内容:", 1, ThisProgramName);

                var panel = new Panel(Markup.Escape(combinedError.Length > 500 ? combinedError.Substring(0, 497) + "..." : combinedError))
                    .Header("[yellow]未识别的错误[/]")
                    .Border(BoxBorder.Rounded);
                AnsiConsole.Write(panel);
                return;
            }

            var rule = new Rule($"[cyan]基础分析结果 - 匹配 {matches.Count} 个模式[/]");
            AnsiConsole.Write(rule);

            for (int i = 0; i < matches.Count; i++)
            {
                var (entry, match, score) = matches[i];

                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn("项目", c => c.Width(12))
                    .AddColumn("内容", c => c.Width(68));

                table.AddRow("序号", $"{i + 1}");
                table.AddRow("问题主题", $"[cyan]{Markup.Escape(entry.Topic)}[/]");
                table.AddRow("解决方案", Markup.Escape(entry.Solution));

                string matchedText = match.Value.Length > 200
                    ? match.Value.Substring(0, 197) + "..."
                    : match.Value;
                table.AddRow("匹配内容", Markup.Escape(matchedText));
                table.AddRow("匹配长度", $"{match.Value.Length} 字符");

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
                    Output.Log("AI分析正在执行中，请等待当前分析完成。", 2, ThisProgramName);
                    return;
                }
                _aiRunning = true;
            }

            try
            {

            var errors = ContentManager.LoadErrorLog();
            if (errors.Count == 0)
            {
                Output.Log("没有错误分析结果，请先使用 .fx get 分析错误日志。", 2, ThisProgramName);
                return;
            }

            if (string.IsNullOrWhiteSpace(rangeArg))
            {
                Output.Log("用法: .fx ai <n> 或 .fx ai <n-m>", 1, ThisProgramName);
                Output.Log("  n   - 将第n个错误发送给AI分析", 1, ThisProgramName);
                Output.Log("  n-m - 合并第n到m个错误后发送给AI分析", 1, ThisProgramName);
                Output.Log($"当前共有 {errors.Count} 条错误记录，使用 .fx list 查看。", 1, ThisProgramName);
                return;
            }

            List<int>? targetIndices = ParseRange(rangeArg, errors.Keys.ToList());
            if (targetIndices == null || targetIndices.Count == 0)
            {
                Output.Log($"无效的范围参数: {rangeArg}", 2, ThisProgramName);
                return;
            }

            var sb = new StringBuilder();
            foreach (int idx in targetIndices)
            {
                if (errors.TryGetValue(idx, out string? errorText))
                {
                    sb.AppendLine(errorText);
                    sb.AppendLine("---");
                }
                else
                {
                    Output.Log($"编号 {idx} 的错误记录不存在。", 2, ThisProgramName);
                    return;
                }
            }

            string combinedError = sb.ToString();
            Output.Log($"正在将 {targetIndices.Count} 条错误发送给AI分析...", 1, ThisProgramName);

            string? aiResponse = await Intelligence.AnalyzeWithAi(combinedError);

            if (string.IsNullOrWhiteSpace(aiResponse))
            {
                Output.Log("AI分析未返回结果。", 2, ThisProgramName);
                return;
            }

            var rule = new Rule("[cyan]AI 分析结果[/]");
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
            Output.Log("[yellow].fx filter 是帮助过滤问题的备份工具[/]", 1, "Filter");
            Output.Log("推荐在问题发生前使用，可备份配置文件并在修改后对照差异。", 1, "Filter");
            Output.Log("子命令：", 1, "Filter");
            Output.Log("  config  - 备份/对照/还原配置文件", 1, "Filter");
            Output.Log("  plugin  - 列出/禁用/启用插件", 1, "Filter");
        }

        public static void FilterConfig(int? selectedIndex)
        {
            string ThisProgramName = "Filter";

            if (selectedIndex.HasValue && selectedIndex.Value == 0)
            {
                if (Directory.Exists(ConfigBackupDir))
                {
                    Directory.Delete(ConfigBackupDir, true);
                    Output.Log("已删除配置备份。", 1, ThisProgramName);
                }
                else
                {
                    Output.Log("没有配置备份可删除。", 2, ThisProgramName);
                }
                return;
            }

            if (selectedIndex.HasValue && selectedIndex.Value > 0)
            {
                lock (_filterLock)
                {
                    if (_lastConfigDiffs.Count == 0)
                    {
                        Output.Log("没有配置差异记录，请先使用 .fx filter config 对照。", 2, ThisProgramName);
                        return;
                    }

                    int idx = selectedIndex.Value - 1;
                    if (idx >= _lastConfigDiffs.Count)
                    {
                        Output.Log($"无效的序号，请输入 1 到 {_lastConfigDiffs.Count} 之间的数字。", 2, ThisProgramName);
                        return;
                    }

                    var diff = _lastConfigDiffs[idx];
                    try
                    {
                        string? dir = Path.GetDirectoryName(diff.CurrentPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);

                        File.Copy(diff.BackupPath, diff.CurrentPath, true);
                        Output.Log($"已还原: {Markup.Escape(diff.RelativePath)}", 1, ThisProgramName);
                    }
                    catch (Exception ex)
                    {
                        Output.Log($"还原失败: {ex.Message}", 3, ThisProgramName);
                    }
                }
                return;
            }

            string workPath = GetWorkPath();
            if (string.IsNullOrEmpty(workPath) || !Directory.Exists(workPath))
            {
                Output.Log("无法获取 MC 服务端工作目录。", 2, ThisProgramName);
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

                Output.Log($"配置文件备份完成，共备份 {count} 个文件到 {ConfigBackupDir}", 1, ThisProgramName);
                Output.Log("请修改配置文件后再次输入 .fx filter config 来对照差异。", 1, ThisProgramName);
            }
            catch (Exception ex)
            {
                Output.Log($"备份失败: {ex.Message}", 3, ThisProgramName);
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
                Output.Log("配置文件与备份一致，没有发现差异。", 1, ThisProgramName);
                return;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("序号", c => c.Alignment(Justify.Center).Width(6))
                .AddColumn("文件路径", c => c.Width(60))
                .AddColumn("状态", c => c.Alignment(Justify.Center).Width(12));

            foreach (var diff in diffs)
            {
                string status = !File.Exists(diff.CurrentPath) ? "[red]已删除[/]" : "[yellow]已修改[/]";
                table.AddRow(diff.Index.ToString(), Markup.Escape(diff.RelativePath), status);
            }

            AnsiConsole.Write(table);
            Output.Log($"共发现 {diffs.Count} 个差异。使用 .fx filter config <序号> 还原，输入.fx filter config 0 删除备份。", 1, ThisProgramName);
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
                Output.Log("正在重启 MC 服务端...", 1, ThisProgramName);
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
                        Output.Log("没有插件列表，请先使用 .fx filter plugin 查看。", 2, ThisProgramName);
                        return;
                    }

                    int idx = selectedIndex.Value - 1;
                    if (idx >= _lastPluginList.Count)
                    {
                        Output.Log($"无效的序号，请输入 1 到 {_lastPluginList.Count} 之间的数字。", 2, ThisProgramName);
                        return;
                    }

                    var plugin = _lastPluginList[idx];
                    try
                    {
                        if (plugin.IsDisabled)
                        {
                            string newPath = plugin.FullPath.Substring(0, plugin.FullPath.Length - 7) + ".jar";
                            File.Move(plugin.FullPath, newPath);
                            Output.Log($"已启用插件: {Markup.Escape(plugin.FileName)} -> {Markup.Escape(Path.GetFileName(newPath))}", 1, ThisProgramName);
                        }
                        else
                        {
                            string newPath = plugin.FullPath.Substring(0, plugin.FullPath.Length - 4) + ".disjar";
                            File.Move(plugin.FullPath, newPath);
                            Output.Log($"已禁用插件: {Markup.Escape(plugin.FileName)} -> {Markup.Escape(Path.GetFileName(newPath))}", 1, ThisProgramName);
                        }

                        ListPlugins();
                    }
                    catch (Exception ex)
                    {
                        Output.Log($"操作失败: {ex.Message}", 3, ThisProgramName);
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
                Output.Log("无法获取 MC 服务端工作目录。", 2, ThisProgramName);
                return;
            }

            string pluginsDir = Path.Combine(workPath, "plugins");
            if (!Directory.Exists(pluginsDir))
            {
                Output.Log("plugins 目录不存在。", 2, ThisProgramName);
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
                Output.Log($"读取插件目录失败: {ex.Message}", 3, ThisProgramName);
                return;
            }

            if (jarFiles.Count == 0)
            {
                Output.Log("plugins 目录中没有找到插件文件。", 2, ThisProgramName);
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
                .AddColumn("序号", c => c.Alignment(Justify.Center).Width(6))
                .AddColumn("插件文件名", c => c.Width(50))
                .AddColumn("状态", c => c.Alignment(Justify.Center).Width(12));

            foreach (var plugin in plugins)
            {
                string status = plugin.IsDisabled ? "[red]已禁用[/]" : "[green]正常[/]";
                table.AddRow(plugin.Index.ToString(), Markup.Escape(plugin.FileName), status);
            }

            AnsiConsole.Write(table);
            Output.Log($"共 {plugins.Count} 个插件。使用 .fx filter plugin <序号> 切换启用/禁用，使用.fx filter plugin 0 重启服务端。", 1, ThisProgramName);
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

            Output.Log($"检测到服务器崩溃信号: {line}", 2, "Analyzer");
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
                        Output.Log($"触发崩溃检测AI任务失败: {ex.Message}", 2, "Analyzer");
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
                Output.Log($"玩家事件监听异常: {ex.Message}", 2, "PlayerEvent");
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
                Output.Log("断开与 Minecraft 服务端的连接。", 1, "Analyzer");
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
                Output.Log($"WMI 查询失败: {ex.Message}", 3, "Analyzer");
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
                        $"扫描到 {results.Count} 个候选 Java 进程, 已按 JavaPath 优先级排序. " +
                        $"首选: PID={top.ProcessId} java={top.JavaExePath} score={top.MatchScore}; " +
                        $"其他: {string.Join(", ", others)}",
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
}
