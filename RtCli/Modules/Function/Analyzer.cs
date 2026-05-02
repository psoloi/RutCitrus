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
using System.Threading;
using System.Threading.Tasks;

namespace RtCli.Modules.Function
{
    internal class Analyzer
    {
        private static Process? _serverProcess;
        private static StreamWriter? _serverInput;
        private static CancellationTokenSource? _outputCts;
        private static readonly object _attachLock = new object();
        private static int _attachedProcessId = 0;
        private static string _attachedWindowTitle = "";
        private static string _connectedServerName = "";
        private static List<MinecraftServerInfo> _lastScanResults = new List<MinecraftServerInfo>();
        private static long _logFilePosition = 0;
        private static string _currentMode = "RCON";
        private static bool _isRunModeActive = false;

        private static RconClient? _rconClient;

        public static IReadOnlyList<MinecraftServerInfo> LastScanResults => _lastScanResults;
        public static string CurrentMode => _currentMode;
        public static bool IsRunMode => _currentMode == "RUN";
        public static bool IsRconMode => _currentMode == "RCON";

        public static void Initialize()
        {
            _currentMode = Config.App.AnalyzerMode.ToUpperInvariant();
            if (_currentMode != "RUN" && _currentMode != "RCON")
            {
                _currentMode = "RCON";
            }
        }

        #region RUN Mode

        public static bool IsRunModeActive => _isRunModeActive;

        public static void StartServer()
        {
            string ThisProgramName = "Analyzer";

            if (_currentMode != "RUN")
            {
                Output.Log("当前模式为 RCON，无法使用 RUN 模式启动服务端。请在配置文件中设置 analyzer_mode 为 RUN。", 2, ThisProgramName);
                return;
            }

            if (_isRunModeActive)
            {
                Output.Log("服务端已在运行中。", 2, ThisProgramName);
                return;
            }

            string workPath = Config.App.WorkPath;
            string flags = Config.App.RunServerFlags;

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

            try
            {
                _serverProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "java",
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

                _serverProcess.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Output.Log(Markup.Escape(e.Data), 1, _connectedServerName);
                    }
                };

                _serverProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Output.Log(Markup.Escape(e.Data), 3, _connectedServerName);
                    }
                };

                _serverProcess.Start();
                _serverProcess.BeginOutputReadLine();
                _serverProcess.BeginErrorReadLine();
                _serverInput = _serverProcess.StandardInput;
                _serverInput.AutoFlush = true;

                _isRunModeActive = true;
                _attachedProcessId = _serverProcess.Id;
                _connectedServerName = Config.App.ServerName;

                _outputCts = new CancellationTokenSource();
                _ = Task.Run(() => MonitorServerProcess(_outputCts.Token), _outputCts.Token);

                Output.Log($"已启动服务端 (PID: {_serverProcess.Id})", 1, ThisProgramName);
                Output.Log($"工作目录: {workPath}", 1, ThisProgramName);
                Output.Log($"启动参数: java {flags}", 1, ThisProgramName);
                Output.Log("使用 / 开头的命令发送到服务端。", 1, ThisProgramName);
                Output.Log("输入 .server stop 停止服务端。", 1, ThisProgramName);
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
                Output.Log("服务端未在运行。", 2, ThisProgramName);
                return;
            }

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
            Output.Log("服务端已停止。", 1, ThisProgramName);
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
                        Output.Log($"服务端进程已退出 (退出码: {exitCode})", 2, "Analyzer");
                        CleanupRunMode();
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

            _lastScanResults = ScanMinecraftServers();
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

            if (_currentMode == "RUN")
            {
                Output.Log("当前为 RUN 模式，不支持 .server connect。请使用 .server start 启动服务端。", 2, ThisProgramName);
                return;
            }

            if (_lastScanResults.Count == 0)
            {
                Output.Log("没有可用的服务端列表，请先使用 .server get 扫描。", 2, ThisProgramName);
                return;
            }

            if (index < 1 || index > _lastScanResults.Count)
            {
                Output.Log($"无效的序号，请输入 1 到 {_lastScanResults.Count} 之间的数字。", 2, ThisProgramName);
                return;
            }

            var selectedServer = _lastScanResults[index - 1];
            AttachToServerRcon(selectedServer, index.ToString());
        }

        public static void ConnectToServerByPid(int pid)
        {
            string ThisProgramName = "Analyzer";

            if (_currentMode == "RUN")
            {
                Output.Log("当前为 RUN 模式，不支持 .server connect。请使用 .server start 启动服务端。", 2, ThisProgramName);
                return;
            }

            var server = _lastScanResults.FirstOrDefault(s => s.ProcessId == pid);
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
                Detach();

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
                        _logFilePosition = fs.Length;
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
                            Config.App.RconHost,
                            Config.App.RconPort,
                            Config.App.RconPassword);
                    }
                    catch (Exception ex)
                    {
                        Output.Log($"RCON 连接失败: {ex.Message}，命令发送将不可用。", 2, ThisProgramName);
                    }

                    Output.Log($"已连接服务端: {Path.GetFileName(server.JarPath)} (PID: {server.ProcessId})", 1, ThisProgramName);
                    Output.Log($"日志文件: {logFile}", 1, ThisProgramName);
                    if (rconConnected)
                    {
                        Output.Log($"RCON 已连接 ({Config.App.RconHost}:{Config.App.RconPort})", 1, ThisProgramName);
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
                    _logFilePosition = 0;
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
                if (fs.Length <= _logFilePosition)
                    return;

                fs.Seek(_logFilePosition, SeekOrigin.Begin);
                using var reader = new StreamReader(fs, Encoding.UTF8);

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        Output.Log(Markup.Escape(line), 1, serverName);
                    }
                }

                _logFilePosition = fs.Position;
            }
            catch (IOException) { }
            catch { }
        }

        #endregion

        #region Common

        public static bool IsAttached => _attachedProcessId != 0 || _isRunModeActive;

        public static void SendCommand(string command)
        {
            lock (_attachLock)
            {
                if (_currentMode == "RUN")
                {
                    SendCommandRunMode(command);
                }
                else
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

        public static void Detach()
        {
            lock (_attachLock)
            {
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
                _logFilePosition = 0;

                if (!_isRunModeActive && _currentMode != "RUN")
                {
                    Output.Log("断开与 Minecraft 服务端的连接。", 1, "Analyzer");
                }
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
            var javaProcesses = new List<(Process Process, string CommandLine, string WindowTitle)>();

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine, Name FROM Win32_Process WHERE Name LIKE 'java%'");
                using var collection = searcher.Get();

                foreach (ManagementObject obj in collection)
                {
                    int pid = Convert.ToInt32(obj["ProcessId"]);
                    string? cmdLine = obj["CommandLine"]?.ToString() ?? "";

                    try
                    {
                        var process = Process.GetProcessById(pid);
                        string windowTitle = "";
                        try
                        {
                            windowTitle = process.MainWindowTitle ?? "";
                        }
                        catch { }

                        javaProcesses.Add((process, cmdLine, windowTitle));
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

            foreach (var (process, cmdLine, windowTitle) in javaProcesses)
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
                        results.Add(new MinecraftServerInfo
                        {
                            ProcessId = process.Id,
                            ProcessName = process.ProcessName,
                            WindowTitle = string.IsNullOrEmpty(windowTitle) ? $"java (PID: {process.Id})" : windowTitle,
                            JarPath = jarPath,
                            CommandLine = cmdLine
                        });
                    }
                }
            }

            return results;
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

    internal class MinecraftServerInfo
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = "";
        public string WindowTitle { get; set; } = "";
        public string JarPath { get; set; } = "";
        public string CommandLine { get; set; } = "";
    }

    internal class RconClient
    {
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private int _requestId = 0;

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
            if (!IsConnected || _stream == null)
                throw new InvalidOperationException("RCON 未连接");

            _requestId++;
            SendPacket(PacketTypeCommand, command);

            var response = ReadPacket();
            return response?.Body ?? "";
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
                if (length <= 0 || length > 4096) return null;

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
}
