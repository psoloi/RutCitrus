using RtCli.Modules;
using RtCli.Modules.Extension;
using RtCli.Modules.Function;
using RtCli.Modules.Unit;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml.Linq;

namespace RtCli
{
    internal class Program
    {
        // 目前修改方向：部分代码捕获不要阻止加上try，MC控制台分析器，将捕获控制台方式换为/命令通过rcon或management来发送，未来使用Base64和密钥加密通信
        // 版本号在 RtCli.csproj 的 VersionPrefix 中修改
        public static string RtCliVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
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
            ".guide",
            ".auto",
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
        private static Mutex? _appMutex;

        public static void ReleaseMutex()
        {
            try
            {
                _appMutex?.ReleaseMutex();
                _appMutex?.Dispose();
                _appMutex = null;
            }
            catch { }
        }

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

        private static async Task MainInternal(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Title = "RtCli";
            Console.Clear();
            Output.TextBlock("启动主线程", 1, "Task#0");

            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            Thread.CurrentThread.Name = "MainThread";

            bool createdNew;
            _appMutex = new Mutex(true, "RtCli_SingleInstance", out createdNew);

            if (!createdNew)
            {
                Output.TextBlock("重复的程序可能会导致异常请等待程序结束...", 2, "Mutex");
                try
                {
                    _appMutex.WaitOne();
                    Output.TextBlock("等待结束...", 1, "Mutex");
                }
                catch (AbandonedMutexException)
                {
                    Output.TextBlock("继续启动...", 1, "Mutex");
                }
            }

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            Output.Log("启动中...", 1, ThisProgramName);

            #region 启动任务

            Reload.Initialize();
            Config.Initialize();
            Modules.Unit.I18n.Init();
            Output.InitializeLogging();
            Analyzer.Initialize();

            if (Config.App.Debug.ToLower() != "No")
            {
                Commands.Execute(Config.App.Debug);
            }

            RtExtensionManager.RtExtensionManager.LoadAll();

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

            var modeChoices = new (string Key, string DisplayName)[]
            {
                ("default", I18n.Get("main_selmode_default")),
                ("debug", I18n.Get("main_selmode_debug")),
                ("reload", I18n.Get("main_selmode_reload")),
                ("exit", I18n.Get("main_selmode_exit"))
            };

            string selectedMode;
            if (Config.App.SkipSelect)
            {
                selectedMode = "default";
            }
            else
            {
                Output.Log(I18n.Get("main_seltip_1"), 1, ThisProgramName);
                var selectedDisplay = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                    .Title(I18n.Get("mian_seltip_2"))
                    .AddChoices(modeChoices.Select(c => c.DisplayName)));
                selectedMode = modeChoices.First(c => c.DisplayName == selectedDisplay).Key;
            }

            switch (selectedMode)
            {
                case "default":
                    EventBus.Publish(new ModeSelectedEvent(I18n.Get("main_selmode_default")));
                    Output.Log("正在运行扩展内容...", 1, ThisProgramName);
                    RtExtensionManager.RtExtensionManager.DisplayLoadedExtensions();
                    RtExtensionManager.RtExtensionManager.Run();
                    await Continued();
                    break;

                case "debug":
                    EventBus.Publish(new ModeSelectedEvent(I18n.Get("main_selmode_debug")));
                    Output.ReportError(new Exception("神秘错误"), true, "这只是一个彩蛋");
                    Task.Run(() =>
                    {
                        Output.Log("测试模式尚未实现！", 2, ThisProgramName);
                        throw new IOException("无测试");
                    }).Wait();
                    break;

                case "exit":
                    Reload.End();
                    break;

                case "reload":
                    Output.Log("重新加载中...", 1, ThisProgramName);
                    Reload.Restart();
                    break;

                default:
                    Output.Log(I18n.Get("main_selmode_no"), 3, ThisProgramName);
                    return;
            }

            EventBus.Publish(new ProgramShutdownEvent("正常退出"));

            Reload.End();
        }

        private static void UpdateAllCommands()
        {
            var registeredCommands = CommandRegistry.GetAutoCompleteCommands();
            _allCommands = BaseCommands.Union(registeredCommands).Distinct().ToArray();
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
            Console.Write(text);
            int clearLength = Math.Max(0, Console.WindowWidth - text.Length - 1);
            if (clearLength > 0)
            {
                Console.Write(new string(' ', clearLength));
            }
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

        public static async Task Continued()
        {
            try
            {
                Output.Log("[yellow]注意：目前已将通信验证删除！程序仅能在本机或局域网运行否则安全无法保障！[/]", 2, ThisProgramName);
                Output.Log("[yellow]注意：该分支为测试分支，可能包含未测试的功能！[/]", 2, ThisProgramName);
                await Connector.StartServerAsync();

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
                            case var cmd when cmd == "rt":
                                Output.Log($"RtCli版本：{Markup.Escape(RtCliVersion)} 输入rt help查看命令列表，按 TAB 键自动补全命令", 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == "rt help":
                                ShowHelp();
                                handled = true;
                                break;
                            case var cmd when cmd == "rt end":
                                goto endpage;
                            case var cmd when cmd == "rt reload":
                                Output.Log("重新加载中...", 1, ThisProgramName);
                                Reload.Restart();
                                handled = true;
                                break;
                            case var cmd when cmd == "rt status":
                                Output.Log($"管理端口状态：{(Connector.IsRunning ? "运行中" : "未运行")}，已连接客户端数量：{Connector.ConnectedClientCount}", 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == "rt clients":
                                Output.Log("已连接面板列表：", 1, ThisProgramName);
                                if (Connector.ConnectedClientCount > 0)
                                {
                                    foreach (var client in Connector.ConnectedClients.Values)
                                    {
                                        Output.Log($"- {client.IP} (连接时间: {client.ConnectTime:HH:mm:ss})", 1, ThisProgramName);
                                    }
                                }
                                else
                                {
                                    Output.Log("无", 1, ThisProgramName);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd == "rt extensions":
                                Output.Log("已加载的扩展列表：", 1, ThisProgramName);
                                RtExtensionManager.RtExtensionManager.DisplayLoadedExtensions();
                                handled = true;
                                break;
                            case var cmd when cmd == "rt extension load":
                                Output.Log("用法: rt extension load <扩展文件路径或文件名>", 1, ThisProgramName);
                                Output.Log($"扩展目录: {RtExtensionManager.RtExtensionManager.GetExtensionsDirectory()}", 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == "rt extension unload":
                                Output.Log("用法: rt extension unload <扩展Key>", 1, ThisProgramName);
                                Output.Log("使用 rt extensions 查看已加载的扩展列表", 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith("rt extension load "):
                                string loadPath = cmd.Substring(18).Trim();
                                if (!string.IsNullOrWhiteSpace(loadPath))
                                {
                                    RtExtensionManager.RtExtensionManager.LoadExtensionByKey(loadPath);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith("rt extension unload "):
                                string unloadKey = cmd.Substring(20).Trim();
                                if (!string.IsNullOrWhiteSpace(unloadKey))
                                {
                                    RtExtensionManager.RtExtensionManager.UnloadExtensionByKey(unloadKey);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd == "rt start":
                                await Connector.StartServerAsync();
                                handled = true;
                                break;
                            case var cmd when cmd == "rt stop":
                                await Connector.StopServerAsync();
                                handled = true;
                                break;
                            case var cmd when cmd == ".help":
                                ShowExtensionCommands();
                                handled = true;
                                break;
                            case var cmd when cmd == ".guide":
                                Intelligence.Guide();
                                handled = true;
                                break;
                            case var cmd when cmd == ".auto":
                                Intelligence.Auto();
                                handled = true;
                                break;
                            case var cmd when cmd == ".server":
                                if (Analyzer.IsRunMode)
                                    Output.Log("当前为 RUN 模式，子命令：start、stop、status", 1, ThisProgramName);
                                else
                                    Output.Log("当前为 RCON 模式，子命令：get、connect、detach、status", 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == ".server get":
                                if (Analyzer.IsRunMode)
                                    Output.Log("RUN 模式下不支持 .server get，请使用 .server start 启动服务端。", 2, ThisProgramName);
                                else
                                    Analyzer.ScanAndListServers();
                                handled = true;
                                break;
                            case var cmd when cmd == ".server connect":
                                if (Analyzer.IsRunMode)
                                    Output.Log("RUN 模式下不支持 .server connect，请使用 .server start 启动服务端。", 2, ThisProgramName);
                                else
                                    Output.Log("用法: .server connect <序号|pid:进程ID>", 1, ThisProgramName);
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
                            case var cmd when cmd == ".server detach":
                                if (Analyzer.IsRunMode)
                                    Output.Log("RUN 模式下不支持 .server detach，请使用 .server stop 停止服务端。", 2, ThisProgramName);
                                else
                                    Analyzer.Detach();
                                handled = true;
                                break;
                            case var cmd when cmd == ".server start":
                                if (Analyzer.IsRunMode)
                                    Analyzer.StartServer();
                                else
                                    Output.Log("RCON 模式下不支持 .server start，请使用 .server get + .server connect 连接。", 2, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == ".server stop":
                                if (Analyzer.IsRunMode)
                                    Analyzer.StopServer();
                                else
                                    Output.Log("RCON 模式下不支持 .server stop。", 2, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == ".server status":
                                if (Analyzer.IsRunMode)
                                    Output.Log(Analyzer.IsRunModeActive ? "服务端运行中。" : "服务端未运行。", 1, ThisProgramName);
                                else
                                    Output.Log(Analyzer.IsAttached ? "已连接到 Minecraft 服务端。" : "未连接到 Minecraft 服务端。", 1, ThisProgramName);
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

        private static void ShowHelp()
        {
            Output.Log("RtCli程序命令列表：", 1, ThisProgramName);
            var table = new Table()
                .AddColumn("命令")
                .AddColumn("描述")
                .AddRow("[green]rt[/]", "显示版本信息")
                .AddRow("[green]rt help[/]", "显示此列表")
                .AddRow("[white]rt end[/]", "关闭程序")
                .AddRow("[white]rt reload[/]", "重新加载")
                .AddRow("[white]rt status[/]", "管理端口状态")
                .AddRow("[white]rt clients[/]", "已连接面板列表")
                .AddRow("[white]rt extensions[/]", "已加载的扩展列表")
                .AddRow("[white]rt extension load <路径>[/]", "加载指定扩展")
                .AddRow("[white]rt extension unload <Key>[/]", "卸载指定扩展")
                .AddRow("[white]rt start[/]", "启动管理端口")
                .AddRow("[white]rt stop[/]", "关闭管理端口")
                .AddRow("[green].help[/]", "显示扩展命令列表")
                .AddRow("[white].guide[/]", "新手引导")
                .AddRow("[white].auto[/]", "自动化")
                .AddRow("[white].server[/]", "MC控制台相关命令 (模式取决于配置)")
                .AddRow("[white].server get[/]", "[[[DarkOrange]RCON[/]]] 扫描并列出运行中的MC服务端")
                .AddRow("[white].server connect <序号|pid:进程ID>[/]", "[[[DarkOrange]RCON[/]]] 连接到指定的MC服务端")
                .AddRow("[white].server detach[/]", "[[[DarkOrange]RCON[/]]] 断开与MC服务端的连接")
                .AddRow("[white].server start[/]", "[[[green]RUN[/]]] 启动MC服务端作为子进程")
                .AddRow("[white].server stop[/]", "[[[green]RUN[/]]] 停止MC服务端")
                .AddRow("[white].server status[/]", "查看MC服务端连接/运行状态")
                .AddRow("[green]/<命令>[/]", "发送命令到MC服务端执行");
            AnsiConsole.Write(table);
        }

        private static void ShowExtensionCommands()
        {
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
        }
    }
}
