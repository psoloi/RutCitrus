using RtCli.Modules;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RtCli.Modules.Function
{
    internal class Analyzer
    {
        private static Process? _attachedProcess;
        private static CancellationTokenSource? _outputCts;
        private static readonly object _attachLock = new object();
        private static int _attachedProcessId = 0;
        private static string _attachedWindowTitle = "";
        private static List<MinecraftServerInfo> _lastScanResults = new List<MinecraftServerInfo>();

        public static IReadOnlyList<MinecraftServerInfo> LastScanResults => _lastScanResults;

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
            AttachToServer(selectedServer);
        }

        public static void ConnectToServerByPid(int pid)
        {
            string ThisProgramName = "Analyzer";

            var server = _lastScanResults.FirstOrDefault(s => s.ProcessId == pid);
            if (server != null)
            {
                AttachToServer(server);
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
                AttachToServer(tempServer);
            }
            catch (Exception ex)
            {
                Output.Log($"无法连接到进程 {pid}: {ex.Message}", 3, ThisProgramName);
            }
        }

        public static bool IsAttached => _attachedProcessId != 0;

        public static void SendCommand(string command)
        {
            lock (_attachLock)
            {
                if (_attachedProcessId == 0)
                {
                    Output.Log("未连接到 Minecraft 服务端，请先使用 .server get 扫描并连接。", 2, "Analyzer");
                    return;
                }

                try
                {
                    IntPtr hWnd = FindWindowByTitleOrPid(_attachedWindowTitle, _attachedProcessId);
                    if (hWnd == IntPtr.Zero)
                    {
                        Output.Log("无法找到目标控制台窗口，可能窗口已关闭。", 2, "Analyzer");
                        return;
                    }

                    foreach (char c in command)
                    {
                        PostMessage(hWnd, WM_CHAR, (IntPtr)c, IntPtr.Zero);
                    }

                    PostMessage(hWnd, WM_KEYDOWN, (IntPtr)VK_RETURN, IntPtr.Zero);
                    PostMessage(hWnd, WM_KEYUP, (IntPtr)VK_RETURN, IntPtr.Zero);

                    Output.Log($"> {command}", 1, "MC");
                }
                catch (Exception ex)
                {
                    Output.Log($"发送命令失败: {ex.Message}", 3, "Analyzer");
                }
            }
        }

        public static void Detach()
        {
            lock (_attachLock)
            {
                _outputCts?.Cancel();
                _outputCts?.Dispose();
                _outputCts = null;

                if (_attachedProcess != null)
                {
                    try
                    {
                        if (!_attachedProcess.HasExited)
                        {
                            _attachedProcess.CancelOutputRead();
                            _attachedProcess.CancelErrorRead();
                        }
                    }
                    catch { }

                    _attachedProcess.Dispose();
                    _attachedProcess = null;
                }

                _attachedProcessId = 0;
                _attachedWindowTitle = "";
                Output.Log("已断开与 Minecraft 服务端的连接。", 1, "Analyzer");
            }
        }

        private static void AttachToServer(MinecraftServerInfo server)
        {
            string ThisProgramName = "Analyzer";
            lock (_attachLock)
            {
                Detach();

                try
                {
                    _attachedProcessId = server.ProcessId;
                    _attachedWindowTitle = server.WindowTitle;
                    _attachedProcess = Process.GetProcessById(server.ProcessId);

                    _outputCts = new CancellationTokenSource();
                    _ = Task.Run(() => CaptureConsoleOutput(server.ProcessId, _outputCts.Token), _outputCts.Token);

                    Output.Log($"已连接服务端: {Path.GetFileName(server.JarPath)} (PID: {server.ProcessId})", 1, ThisProgramName);
                    Output.Log("现在可以使用 / 开头的命令发送到服务端。", 1, ThisProgramName);
                    Output.Log("输入 .server detach 可断开连接。", 1, ThisProgramName);
                }
                catch (Exception ex)
                {
                    _attachedProcessId = 0;
                    _attachedWindowTitle = "";
                    Output.ReportError(ex, false, "连接服务端失败");
                }
            }
        }

        private static void CaptureConsoleOutput(int processId, CancellationToken cancellationToken)
        {
            string ThisProgramName = "Analyzer";
            IntPtr hConsole = IntPtr.Zero;

            try
            {
                if (!FreeConsole()) { }

                if (!AttachConsole((uint)processId))
                {
                    AllocConsole();
                    Output.Log($"无法附加到进程 {processId} 的控制台，输出捕获不可用。", 2, ThisProgramName);
                    return;
                }

                hConsole = CreateFile(
                    "CONOUT$",
                    GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (hConsole == IntPtr.Zero || hConsole == new IntPtr(-1))
                {
                    FreeConsole();
                    AllocConsole();
                    Output.Log("无法打开控制台输出缓冲区。", 2, ThisProgramName);
                    return;
                }

                if (!GetConsoleScreenBufferInfo(hConsole, out CONSOLE_SCREEN_BUFFER_INFO sbInfo))
                {
                    CloseHandle(hConsole);
                    FreeConsole();
                    AllocConsole();
                    Output.Log("无法获取控制台缓冲区信息。", 2, ThisProgramName);
                    return;
                }

                string previousContent = "";

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        if (!GetConsoleScreenBufferInfo(hConsole, out sbInfo))
                            break;

                        short width = sbInfo.dwSize.X;
                        short height = sbInfo.dwSize.Y;

                        if (width <= 0 || height <= 0)
                        {
                            Thread.Sleep(500);
                            continue;
                        }

                        int bufSize = width * height;
                        if (bufSize <= 0 || bufSize > 65536)
                        {
                            Thread.Sleep(500);
                            continue;
                        }

                        var buffer = new CHAR_INFO[bufSize];
                        var readRegion = new SMALL_RECT
                        {
                            Left = 0,
                            Top = 0,
                            Right = (short)(width - 1),
                            Bottom = (short)(height - 1)
                        };

                        if (ReadConsoleOutput(hConsole, buffer, new COORD(width, height), new COORD(0, 0), ref readRegion))
                        {
                            var sb = new StringBuilder();
                            for (int row = 0; row < height; row++)
                            {
                                var lineBuilder = new StringBuilder();
                                for (int col = 0; col < width; col++)
                                {
                                    int idx = row * width + col;
                                    if (idx < buffer.Length)
                                    {
                                        char c = (char)buffer[idx].UnicodeChar;
                                        if (c != '\0')
                                            lineBuilder.Append(c);
                                    }
                                }
                                string line = lineBuilder.ToString().TrimEnd();
                                if (!string.IsNullOrWhiteSpace(line))
                                {
                                    sb.AppendLine(line);
                                }
                            }

                            string currentContent = sb.ToString();
                            if (currentContent != previousContent && !string.IsNullOrWhiteSpace(currentContent))
                            {
                                string diff = GetDiff(previousContent, currentContent);
                                if (!string.IsNullOrWhiteSpace(diff))
                                {
                                    foreach (var line in diff.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                                    {
                                        if (!string.IsNullOrWhiteSpace(line))
                                        {
                                            Output.Log(line, 1, "MC-Out");
                                        }
                                    }
                                }
                                previousContent = currentContent;
                            }
                        }

                        Thread.Sleep(500);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Output.Log($"输出捕获异常: {ex.Message}", 3, ThisProgramName);
            }
            finally
            {
                if (hConsole != IntPtr.Zero && hConsole != new IntPtr(-1))
                    CloseHandle(hConsole);

                try
                {
                    FreeConsole();
                    AllocConsole();
                }
                catch { }
            }
        }

        private static string GetDiff(string previous, string current)
        {
            if (string.IsNullOrEmpty(previous))
                return current;

            if (current.StartsWith(previous, StringComparison.Ordinal))
            {
                return current.Substring(previous.Length);
            }

            var prevLines = previous.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var currLines = current.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            var newLines = new List<string>();
            int startIndex = 0;
            for (int i = Math.Max(0, currLines.Length - prevLines.Length - 5); i < currLines.Length; i++)
            {
                bool found = false;
                for (int j = startIndex; j < prevLines.Length; j++)
                {
                    if (currLines[i] == prevLines[j])
                    {
                        startIndex = j + 1;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    newLines.Add(currLines[i]);
                }
            }

            return string.Join(Environment.NewLine, newLines);
        }

        #region Window Helpers

        private static IntPtr FindWindowByTitleOrPid(string title, int pid)
        {
            if (!string.IsNullOrEmpty(title))
            {
                IntPtr hWnd = FindWindow(null, title);
                if (hWnd != IntPtr.Zero)
                    return hWnd;
            }

            IntPtr result = IntPtr.Zero;
            EnumWindows((hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out uint windowPid);
                if (windowPid == pid)
                {
                    result = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        #endregion

        #region Windows API

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadConsoleOutput(IntPtr hConsoleOutput, [Out] CHAR_INFO[] lpBuffer, COORD dwBufferSize, COORD dwBufferCoord, ref SMALL_RECT lpReadRegion);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint WM_CHAR = 0x0102;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const int VK_RETURN = 0x0D;

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;
            public COORD(short x, short y)
            {
                X = x;
                Y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SMALL_RECT
        {
            public short Left;
            public short Top;
            public short Right;
            public short Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CONSOLE_SCREEN_BUFFER_INFO
        {
            public COORD dwSize;
            public COORD dwCursorPosition;
            public short wAttributes;
            public SMALL_RECT srWindow;
            public COORD dwMaximumWindowSize;
        }

        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
        private struct CHAR_INFO
        {
            [FieldOffset(0)] public char UnicodeChar;
            [FieldOffset(0)] public byte AsciiChar;
            [FieldOffset(2)] public short Attributes;
        }

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
                        // 进程可能已退出
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
}
