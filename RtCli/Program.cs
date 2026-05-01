using RtCli.Modules;
using RtCli.Modules.Extension;
using RtCli.Modules.Function;
using RtCli.Modules.Unit;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml.Linq;

namespace RtCli
{
    internal class Program
    {
        // 目前修改方向：部分代码捕获不要阻止加上try，MC控制台分析器，将捕获控制台方式换为/命令通过rcon或management来发送
        // 程序版本号规则：主版本号.日期yy/mm.次版本号.编译号
        public static string RtCliVersion { get; } = "1.2604.29.13";
        public static string ThisProgramName { get; } = "RtCli";

        private static readonly string[] BaseCommands = new string[]
        {
            "rt reload",
            "rt status",
            "rt clients",
            "rt extensions",
            "rt end",
            "rt stop",
            "rt start",
            "rt help",
            "rt",
            ".help",
            ".server",
            ".server get",
            ".server connect",
            ".server start",
            ".server stop",
            ".server detach",
            ".server status",
            "/"
        };

        private static string[] _allCommands = Array.Empty<string>();
        private static readonly List<string> _commandHistory = new List<string>();
        private const int MaxHistorySize = 100;

        /// <summary>
        /// 应用程序主入口点
        /// </summary>
        /// <param name="args"></param>
        [STAThread]
        public static Task Main(string[] args)
        {
            try
            {
                return MainInternal(args);
            }
            catch (Exception ex)
            {
                Output.ReportError(ex);
                //return Task.CompletedTask;
                return Task.FromResult(ex);
            }
            finally
            {
                Reload.End();
            }
        }


        /// <summary>
        /// 捕获未处理的 Task 异常
        /// </summary>
        private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs ex)
        {
            Output.ReportError(ex.Exception);
            ex.SetObserved();
        }

        private static Task MainInternal(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Title = "RtCli";
            Console.Clear();
            Output.TextBlock("启动主线程", 1, "Task#0");

            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            Thread rtmain = new Thread(Continued);
            Thread.CurrentThread.Name = "MainThread";

            Process currentProcess = Process.GetCurrentProcess();
            string currentProcessName = currentProcess.ProcessName;

            Process[] processes = Process.GetProcessesByName(currentProcessName);
            if (processes.Length > 1)
            {
                Output.TextBlock("重复的程序!", 2, "Task#End");
                return Task.CompletedTask;
            }

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            Output.Log("启动中...", 1, currentProcessName);

            #region 启动任务

            RtExtensionManager.RtExtensionManager.LoadAll();

            Modules.Unit.I18n.Init();
            Output.InitializeLogging();
            Analyzer.Initialize();

            Thread.CurrentThread.Name = "MainThread";
            if (Config.App.CheckJava)
            {
                string result = Checker.CheckJava();
                Output.Log(result, 1, "Checker");
            }
            if (Config.App.CheckDotNet)
            {
                string result2 = Checker.CheckDotNet();
                Output.Log(result2, 1, "Checker");
            }
            if (Config.App.CheckOSBit)
            {
                bool is64BitOperatingSystem = Environment.Is64BitOperatingSystem;
                if (!is64BitOperatingSystem)
                {
                    Output.Log($"[yellow]{I18n.Get("checker_osbit")}[/]", 2, "Checker");
                }
            }

            UpdateAllCommands();

            #endregion

            stopwatch.Stop();
            Output.Log($"{Modules.Unit.I18n.Get("main_loadfinsih")}（{stopwatch.ElapsedMilliseconds}ms）", 1, ThisProgramName);
            EventBus.Publish(new ProgramStartupEvent(args));

            Thread.CurrentThread.Name = "MainThread";
            string selloaded;
            if (Config.App.SkipSelect)
            {
                selloaded = I18n.Get("main_selmode_default");
            }
            else
            {
                Output.Log(Modules.Unit.I18n.Get("main_seltip_1"), 1, ThisProgramName);
                selloaded = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                    .Title(Modules.Unit.I18n.Get("mian_seltip_2"))
                    .AddChoices(I18n.Get("main_selmode_default"), I18n.Get("main_selmode_debug"), I18n.Get("main_selmode_reload"), I18n.Get("main_selmode_exit")));
            }

            switch (selloaded)
            {
                case "默认模式":
                    EventBus.Publish(new ModeSelectedEvent("默认模式"));
                    Output.Log("正在运行扩展内容...", 1, ThisProgramName);
                    RtExtensionManager.RtExtensionManager.DisplayLoadedExtensions();
                    RtExtensionManager.RtExtensionManager.Run();
                    rtmain.Start();
                    rtmain.Join();
                    break;

                case "测试模式":
                    EventBus.Publish(new ModeSelectedEvent("测试模式"));
                    // no
                    Output.ReportError(new Exception("神秘错误"), true, "这只是一个彩蛋");
                    Task.Run(() =>
                    {
                        Output.Log("测试模式尚未实现！", 2, ThisProgramName);
                        throw new IOException("无测试");
                    }).Wait();
                    break;

                case "关闭程序":
                    Reload.End();
                    break;

                case "重新加载":
                    Output.Log("重新加载中...", 1, ThisProgramName);
                    Reload.Restart();
                    break;

                default:
                    Output.Log(I18n.Get("main_selmode_no"), 3, ThisProgramName);
                    return Task.CompletedTask;

            }

            EventBus.Publish(new ProgramShutdownEvent("正常退出"));

            Reload.End();
            return Task.CompletedTask;
        }

        private static void UpdateAllCommands()
        {
            var registeredCommands = CommandRegistry.GetAutoCompleteCommands();
            _allCommands = BaseCommands.Union(registeredCommands).Distinct().ToArray();
        }

        // Windows的最大化控制台窗口
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetConsoleWindow();

        const int SW_MAXIMIZE = 3;
        public static void Maximize_ConsoleWindows()
        {
            IntPtr hWnd = GetConsoleWindow();
            ShowWindow(hWnd, SW_MAXIMIZE);
            Console.ReadKey();
        }

        #region 命令附加功能

        private static string? ReadLineWithTabCompletion()
        {
            StringBuilder input = new StringBuilder();
            int cursorPosition = 0;
            int tabIndex = -1;
            List<string> currentMatches = new List<string>();
            bool hasSuggestions = false;
            int suggestionLine = -1;
            int historyIndex = -1;
            string originalInput = "";

            while (true)
            {
                ConsoleKeyInfo keyInfo = Console.ReadKey(true);

                switch (keyInfo.Key)
                {
                    case ConsoleKey.Enter:
                        if (hasSuggestions)
                        {
                            ClearSuggestions(suggestionLine, _lastSuggestionLines);
                            hasSuggestions = false;
                        }
                        Console.WriteLine();
                        return input.ToString();

                    case ConsoleKey.UpArrow:
                        if (_commandHistory.Count > 0)
                        {
                            if (historyIndex == -1)
                            {
                                originalInput = input.ToString();
                                historyIndex = _commandHistory.Count - 1;
                            }
                            else if (historyIndex > 0)
                            {
                                historyIndex--;
                            }
                            
                            input.Clear();
                            input.Append(_commandHistory[historyIndex]);
                            cursorPosition = input.Length;
                            tabIndex = -1;
                            RedrawLine(input.ToString(), cursorPosition);
                            if (hasSuggestions)
                            {
                                ClearSuggestions(suggestionLine, _lastSuggestionLines);
                                hasSuggestions = false;
                            }
                        }
                        break;

                    case ConsoleKey.DownArrow:
                        if (historyIndex != -1)
                        {
                            if (historyIndex < _commandHistory.Count - 1)
                            {
                                historyIndex++;
                                input.Clear();
                                input.Append(_commandHistory[historyIndex]);
                            }
                            else
                            {
                                historyIndex = -1;
                                input.Clear();
                                input.Append(originalInput);
                            }
                            cursorPosition = input.Length;
                            tabIndex = -1;
                            RedrawLine(input.ToString(), cursorPosition);
                            if (hasSuggestions)
                            {
                                ClearSuggestions(suggestionLine, _lastSuggestionLines);
                                hasSuggestions = false;
                            }
                        }
                        break;

                    case ConsoleKey.Backspace:
                        if (cursorPosition > 0)
                        {
                            input.Remove(cursorPosition - 1, 1);
                            cursorPosition--;
                            tabIndex = -1;
                            historyIndex = -1;
                            RedrawLine(input.ToString(), cursorPosition);
                            if (hasSuggestions)
                            {
                                ClearSuggestions(suggestionLine, _lastSuggestionLines);
                                hasSuggestions = false;
                            }
                        }
                        break;

                    case ConsoleKey.Delete:
                        if (cursorPosition < input.Length)
                        {
                            input.Remove(cursorPosition, 1);
                            tabIndex = -1;
                            historyIndex = -1;
                            RedrawLine(input.ToString(), cursorPosition);
                            if (hasSuggestions)
                            {
                                ClearSuggestions(suggestionLine, _lastSuggestionLines);
                                hasSuggestions = false;
                            }
                        }
                        break;

                    case ConsoleKey.LeftArrow:
                        if (cursorPosition > 0)
                        {
                            cursorPosition--;
                            Console.SetCursorPosition(cursorPosition, Console.CursorTop);
                        }
                        break;

                    case ConsoleKey.RightArrow:
                        if (cursorPosition < input.Length)
                        {
                            cursorPosition++;
                            Console.SetCursorPosition(cursorPosition, Console.CursorTop);
                        }
                        break;

                    case ConsoleKey.Home:
                        cursorPosition = 0;
                        Console.SetCursorPosition(0, Console.CursorTop);
                        break;

                    case ConsoleKey.End:
                        cursorPosition = input.Length;
                        Console.SetCursorPosition(cursorPosition, Console.CursorTop);
                        break;

                    case ConsoleKey.Tab:
                        string currentInput = input.ToString();
                        
                        if (tabIndex == -1 || currentMatches.Count == 0)
                        {
                            currentMatches = _allCommands
                                .Where(cmd => cmd.StartsWith(currentInput, StringComparison.OrdinalIgnoreCase))
                                .ToList();
                            tabIndex = 0;
                        }
                        else
                        {
                            tabIndex = (tabIndex + 1) % currentMatches.Count;
                        }

                        if (currentMatches.Count > 0)
                        {
                            input.Clear();
                            input.Append(currentMatches[tabIndex]);
                            cursorPosition = input.Length;
                            RedrawLine(input.ToString(), cursorPosition);
                            
                            if (currentMatches.Count > 1)
                            {
                                suggestionLine = ShowSuggestions(currentMatches, tabIndex, hasSuggestions ? suggestionLine : -1, hasSuggestions ? _lastSuggestionLines : 1);
                                hasSuggestions = true;
                            }
                            else if (hasSuggestions)
                            {
                                ClearSuggestions(suggestionLine, _lastSuggestionLines);
                                hasSuggestions = false;
                            }
                        }
                        break;

                    default:
                        if (!char.IsControl(keyInfo.KeyChar))
                        {
                            input.Insert(cursorPosition, keyInfo.KeyChar);
                            cursorPosition++;
                            tabIndex = -1;
                            historyIndex = -1;
                            RedrawLine(input.ToString(), cursorPosition);
                            if (hasSuggestions)
                            {
                                ClearSuggestions(suggestionLine, _lastSuggestionLines);
                                hasSuggestions = false;
                            }
                        }
                        break;
                }
            }
        }

        private static void ClearSuggestions(int suggestionLine, int linesUsed)
        {
            if (suggestionLine >= 0 && suggestionLine < Console.BufferHeight)
            {
                int currentTop = Console.CursorTop;
                for (int i = 0; i < linesUsed && (suggestionLine + i) < Console.BufferHeight; i++)
                {
                    Console.SetCursorPosition(0, suggestionLine + i);
                    Console.Write(new string(' ', Console.WindowWidth));
                }
                Console.SetCursorPosition(0, currentTop);
            }
        }

        private static void RedrawLine(string text, int cursorPosition)
        {
            int currentLine = Console.CursorTop;
            Console.SetCursorPosition(0, currentLine);
            Console.Write(text + new string(' ', Math.Max(0, Console.WindowWidth - text.Length - 1)));
            Console.SetCursorPosition(cursorPosition, currentLine);
        }

        private static int ShowSuggestions(List<string> matches, int selectedIndex, int previousLine, int previousLinesUsed)
        {
            int originalTop = Console.CursorTop;
            
            if (previousLine >= 0 && previousLine < Console.BufferHeight)
            {
                for (int i = 0; i < previousLinesUsed && (previousLine + i) < Console.BufferHeight; i++)
                {
                    Console.SetCursorPosition(0, previousLine + i);
                    Console.Write(new string(' ', Console.WindowWidth));
                }
            }
            
            int suggestionLine = originalTop + 1;
            int linesUsed = 1;
            
            if (suggestionLine < Console.BufferHeight)
            {
                Console.SetCursorPosition(0, suggestionLine);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                
                int currentLineStart = suggestionLine;
                
                for (int i = 0; i < matches.Count; i++)
                {
                    string displayText;
                    if (i == selectedIndex)
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        displayText = $"[{matches[i]}] ";
                        Console.Write(displayText);
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                    }
                    else
                    {
                        displayText = $"{matches[i]} ";
                        Console.Write(displayText);
                    }
                    
                    if (Console.CursorTop > currentLineStart)
                    {
                        linesUsed++;
                        currentLineStart = Console.CursorTop;
                    }
                }
                
                int remainingWidth = Console.WindowWidth - Console.CursorLeft;
                if (remainingWidth > 0)
                {
                    Console.Write(new string(' ', remainingWidth));
                }
                
                Console.ResetColor();
            }
            
            Console.SetCursorPosition(0, originalTop);
            _lastSuggestionLines = linesUsed;
            return suggestionLine;
        }
        
        private static int _lastSuggestionLines = 1;

        #endregion

        public static async void Continued()
        {
            try
            {
                Thread.CurrentThread.Name = "Main";
                Output.Log("[yellow]注意：目前已将通信验证删除！程序仅能在本机或局域网运行，如果在其他网络环境下运行安全目前无法保障！[/]", 2, ThisProgramName);
                Output.Log("[yellow]注意：该分支为测试分支，可能包含未测试的功能！[/]", 2, ThisProgramName);
                await Connector.StartServerAsync();
                Thread.CurrentThread.Name = "Main";

                string? cmd_input = ReadLineWithTabCompletion();
                while (true)
                {
                    bool handled = false;

                    try
                    {
                        if (!string.IsNullOrEmpty(cmd_input))
                        {
                            EventBus.Publish(new CommandExecuteEvent(cmd_input, BaseCommands));
                        }
                        switch (cmd_input)
                        {
                            case var cmd when cmd == BaseCommands[0]:
                                Output.Log("重新加载中...", 1, ThisProgramName);
                                Reload.Restart();
                                handled = true;
                                break;
                            case var cmd when cmd == BaseCommands[1]:
                                Output.Log($"管理端口状态：{(Connector.IsRunning ? "运行中" : "未运行")}，已连接客户端数量：{Connector.ConnectedClientCount}", 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == BaseCommands[2]:
                                Output.Log($"已连接面板列表：", 1, ThisProgramName);
                                foreach (var client in Connector.ConnectedClientCount > 0 ? Connector.ConnectedClientCount.ToString() : "无")
                                {
                                    Output.Log($"- {client}", 1, ThisProgramName);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd == BaseCommands[3]:
                                Output.Log("已加载的扩展列表：", 1, ThisProgramName);
                                RtExtensionManager.RtExtensionManager.DisplayLoadedExtensions();
                                handled = true;
                                break;
                            case var cmd when cmd == BaseCommands[4]:
                                goto endpage;
                            case var cmd when cmd == BaseCommands[5]:
                                await Connector.StopServerAsync();
                                handled = true;
                                break;
                            case var cmd when cmd == BaseCommands[6]:
                                await Connector.StartServerAsync();
                                handled = true;
                                break;
                            case var cmd when cmd == BaseCommands[7]:
                                Output.Log("RtCli程序命令列表：", 1, ThisProgramName);
                                var table = new Table()
                                    .AddColumn("命令")
                                    .AddColumn("描述")
                                    .AddRow("[green]rt help[/]", "显示此列表")
                                    .AddRow("[white]rt end[/]", "关闭程序")
                                    .AddRow("[white]rt stop[/]", "关闭管理端口")
                                    .AddRow("[white]rt start[/]", "启动管理端口")
                                    .AddRow("[white]rt reload[/]", "重新加载")
                                    .AddRow("[white]rt status[/]", "管理端口状态")
                                    .AddRow("[white]rt clients[/]", "已连接面板列表")
                                    .AddRow("[white]rt extensions[/]", "已加载的扩展列表")
                                    .AddRow("[green].help[/]", "显示其他命令或扩展命令列表")
                                    .AddRow("[white].server[/]", "MC控制台相关命令 (模式取决于配置)")
                                    .AddRow("[white].server get[/]", "[[[DarkOrange]RCON[/]]] 扫描并列出运行中的MC服务端")
                                    .AddRow("[white].server connect <序号|pid:进程ID>[/]", "[[[DarkOrange]RCON[/]]] 连接到指定的MC服务端")
                                    .AddRow("[white].server detach[/]", "[[[DarkOrange]RCON[/]]] 断开与MC服务端的连接")
                                    .AddRow("[white].server start[/]", "[[[green]RUN[/]]] 启动MC服务端作为子进程")
                                    .AddRow("[white].server stop[/]", "[[[green]RUN[/]]] 停止MC服务端")
                                    .AddRow("[white].server status[/]", "查看MC服务端连接/运行状态")
                                    .AddRow("[green]/[/]", "输入/开头的命令将直接发送到MC服务端执行");
                                AnsiConsole.Write(table);
                                handled = true;
                                break;
                            case var cmd when cmd == BaseCommands[8]:
                                Output.Log($"RtCli版本：{RtCliVersion} 输入rt help查看命令列表，按 TAB 键自动补全命令", 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == BaseCommands[9]:
                                var extensionCommands = CommandRegistry.Commands;
                                var extensionDescriptions = CommandRegistry.Descriptions;
                                if (extensionCommands.Count > 0)
                                {
                                    Output.Log("扩展注册的命令列表：", 1, ThisProgramName);
                                    var extTable = new Table()
                                        .AddColumn("命令")
                                        .AddColumn("描述");
                                    foreach (var kvp in extensionCommands)
                                    {
                                        string description = extensionDescriptions.TryGetValue(kvp.Key, out var desc) ? desc : "";
                                        extTable.AddRow($"[cyan]{kvp.Key}[/]", string.IsNullOrEmpty(description) ? "[grey]-[/]" : description);
                                    }
                                    AnsiConsole.Write(extTable);
                                }
                                else
                                {
                                    Output.Log("没有扩展注册的命令", 1, ThisProgramName);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd == BaseCommands[10]:
                                if (Analyzer.IsRunMode)
                                    Output.Log("当前为 RUN 模式，子命令：start、stop、status", 1, ThisProgramName);
                                else
                                    Output.Log("当前为 RCON 模式，子命令：get、connect、detach、status", 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == BaseCommands[11]:
                                if (Analyzer.IsRunMode)
                                    Output.Log("RUN 模式下不支持 .server get，请使用 .server start 启动服务端。", 2, ThisProgramName);
                                else
                                    Analyzer.ScanAndListServers();
                                handled = true;
                                break;
                            case var cmd when cmd == BaseCommands[12]:
                                if (Analyzer.IsRunMode)
                                    Output.Log("RUN 模式下不支持 .server connect，请使用 .server start 启动服务端。", 2, ThisProgramName);
                                else
                                    Output.Log("用法: .server connect <序号|pid:进程ID>", 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == BaseCommands[13]:
                                if (Analyzer.IsRunMode)
                                    Analyzer.StartServer();
                                else
                                    Output.Log("RCON 模式下不支持 .server start，请使用 .server get + .server connect 连接。", 2, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == BaseCommands[14]:
                                if (Analyzer.IsRunMode)
                                    Analyzer.StopServer();
                                else
                                    Output.Log("RCON 模式下不支持 .server stop。", 2, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == BaseCommands[15]:
                                if (Analyzer.IsRunMode)
                                    Output.Log("RUN 模式下不支持 .server detach，请使用 .server stop 停止服务端。", 2, ThisProgramName);
                                else
                                    Analyzer.Detach();
                                handled = true;
                                break;
                            case var cmd when cmd == BaseCommands[16]:
                                if (Analyzer.IsRunMode)
                                    Output.Log(Analyzer.IsRunModeActive ? "服务端运行中。" : "服务端未运行。", 1, ThisProgramName);
                                else
                                    Output.Log(Analyzer.IsAttached ? "已连接到 Minecraft 服务端。" : "未连接到 Minecraft 服务端。", 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith(".server connect "):
                                if (Analyzer.IsRunMode)
                                {
                                    Output.Log("RUN 模式下不支持 .server connect，请使用 .server start 启动服务端。", 2, ThisProgramName);
                                }
                                else
                                {
                                    string connectArg = cmd.Substring(16).Trim();
                                    if (!string.IsNullOrWhiteSpace(connectArg))
                                    {
                                        if (connectArg.StartsWith("pid:", StringComparison.OrdinalIgnoreCase))
                                        {
                                            string pidStr = connectArg.Substring(4);
                                            if (int.TryParse(pidStr, out int pid))
                                            {
                                                Analyzer.ConnectToServerByPid(pid);
                                            }
                                            else
                                            {
                                                Output.Log("无效的进程ID格式。", 2, ThisProgramName);
                                            }
                                        }
                                        else if (int.TryParse(connectArg, out int index))
                                        {
                                            Analyzer.ConnectToServer(index);
                                        }
                                        else
                                        {
                                            Output.Log("无效的参数。请使用序号或 pid:进程ID。", 2, ThisProgramName);
                                        }
                                    }
                                    else
                                    {
                                        Output.Log("用法: .server connect <序号|pid:进程ID>", 1, ThisProgramName);
                                    }
                                }
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith("/"):
                                string mcCommand = cmd.Substring(1);
                                if (!string.IsNullOrWhiteSpace(mcCommand))
                                {
                                    Analyzer.SendCommand(mcCommand);
                                    handled = true;
                                }
                                break;
                        }

                        if (!handled && !string.IsNullOrWhiteSpace(cmd_input))
                        {
                            if (CommandRegistry.HasCommand(cmd_input))
                            {
                                CommandRegistry.TryExecute(cmd_input, Array.Empty<string>());
                            }
                            else
                            {
                                Output.Log("未知命令！输入rt help查看命令列表", 1, ThisProgramName);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Output.ReportError(ex);
                        Output.Log($"命令执行错误请检查是否有扩展出现问题", 2, ThisProgramName);
                    }

                    if (!string.IsNullOrWhiteSpace(cmd_input))
                    {
                        if (_commandHistory.Count == 0 || _commandHistory[_commandHistory.Count - 1] != cmd_input)
                        {
                            _commandHistory.Add(cmd_input);
                            if (_commandHistory.Count > MaxHistorySize)
                            {
                                _commandHistory.RemoveAt(0);
                            }
                        }
                    }

                    Thread.CurrentThread.Name = "Main";
                    cmd_input = ReadLineWithTabCompletion();
                }
                endpage:
                    Output.Log("正在关闭...", 1, ThisProgramName);
                    Reload.End();
                return;
            }
            catch (Exception ex)
            {
                Output.ReportError(ex);
            }
        }
    }
}
