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
        // 目前修改方向：MC控制台日志分析器完善，MC服务端信息获取
        // AI扩展更多功能

        // 优化文字和命令整理及I18n

        // BlueMap和Litebans支持、配置文件翻译、玩家监控、自动监控服务器性能并提供优化建议

        // .auto

        // readme.md参考 github-readme-stats-master 并且中英文分开

        // 版本号在 RtCli.csproj 的 VersionPrefix 中修改
        public static string RtCliVersion { get; } =
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";
        public static string ThisProgramName { get; } = "RtCli";
        public static readonly string RtCliInformation = "Author by psoloi on https://github.com/psoloi/RutCitrus";

        private static readonly string[] BaseCommands = new string[]
        {
            "rt reload",
            "rt status",
            "rt clients",
            "rt clients kick",
            "rt extensions",
            "rt extensions load",
            "rt extensions unload",
            "rt end",
            "rt stop",
            "rt start",
            "rt help",
            "rt about",
            "rt",
            ".help",
            ".guide",
            ".auto",
            ".auto on",
            ".auto off",
            ".auto list",
            ".auto start",
            ".auto stop",
            ".fx",
            ".fx get",
            ".fx list",
            ".fx del",
            ".fx clientguide",
            ".fx base",
            ".fx ai",
            ".fx filter",
            ".fx filter config",
            ".fx filter plugin",
            ".cfg",
            ".cfg reload",
            ".cfg clear",
            ".status",
            ".server",
            ".server list",
            ".server add",
            ".server del",
            ".server change",
            ".server get",
            ".server connect",
            ".server start",
            ".server stop",
            ".server detach",
            ".server status",
            "ct",
            "ct list",
            "ct unpack",
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
                return Task.CompletedTask;
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
            Thread.CurrentThread.Name = "Main";

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
            ContentManager.Initialize();
            Scheduler.Initialize();
            Intelligence.StartAutoBackup();
            Backend.Initialize();

            if (Config.App.Debug.ToLower() != "No")
            {
                Commands.Execute(Config.App.Debug);
            }

            RtExtensionManager.LoadAll();

            Scripts.Initialize();
            Scripts.LoadSettingsAndSubscribe();

            Checker.CheckAll();

            UpdateAllCommands();

            #endregion

            stopwatch.Stop();
            Output.Log($"{Modules.Unit.I18n.Get("main_loadfinsih")}（{stopwatch.ElapsedMilliseconds}ms）", 1, ThisProgramName);
            EventBus.Publish(new ProgramStartupEvent(args));

            #region 模式选择

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
                    RtExtensionManager.DisplayLoadedExtensions();
                    RtExtensionManager.Run();
                    Continued();
                    break;

                case "debug":
                    EventBus.Publish(new ModeSelectedEvent(I18n.Get("main_selmode_debug")));
                    Output.ReportError(new Exception("神秘错误"), true, "这只是一个彩蛋");
                    Task.Run(() =>
                    {
                        Output.Log("测试模式尚未实现！", 2, ThisProgramName);
                        throw new IOException("无测试");
                    }).Wait();
                    Commands.Execute("1");
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
                    return Task.CompletedTask;
            }

            #endregion

            EventBus.Publish(new ProgramShutdownEvent("正常退出"));

            Reload.End();
            return Task.CompletedTask;
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

        public static void Continued()
        {
            try
            {
                Output.Log("[yellow]注意：功能还未完善！程序仅能在本机或局域网运行否则安全无法保障！[/]", 2, ThisProgramName);
                try
                {
                    Connector.StartServerAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Output.ReportError(ex, false, "启动管理端口失败");
                }

                string? cmd_input = ReadLineWithTabCompletion();
                while (true)
                {
                    if (Reload.IsShuttingDown) goto endpage;

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
                                Output.Log($"RtCli版本：{Markup.Escape(RtCliVersion)} {RtCliInformation} 输入rt help查看命令列表，按 TAB 键自动补全命令", 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == "rt help":
                                ShowHelp();
                                handled = true;
                                break;
                            case var cmd when cmd == "rt about":
                                ShowAbout();
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
                                var rtStatusTable = new Table().Border(TableBorder.Rounded).Title("[cyan]RtCli 状态[/]");
                                rtStatusTable.AddColumn("项目").AddColumn("状态");
                                rtStatusTable.AddRow("管理端口", Connector.IsRunning ? "[green]运行中[/]" : "[red]未运行[/]");
                                rtStatusTable.AddRow("已连接面板", Connector.ConnectedClientCount.ToString());
                                rtStatusTable.AddRow("当前服务端", $"[cyan]{Markup.Escape(Config.App.CurrentServer)}[/] ({Markup.Escape(Config.CurrentServer.ServerName)})");
                                rtStatusTable.AddRow("MC模式", $"[cyan]{Analyzer.CurrentMode}[/]");
                                if (Analyzer.NeedsRunServer)
                                {
                                    rtStatusTable.AddRow("MC服务端", Analyzer.IsRunModeActive ? "[green]运行中[/]" : "[red]未运行[/]");
                                }
                                else
                                {
                                    rtStatusTable.AddRow("MC连接", Analyzer.IsAttached ? "[green]已连接[/]" : "[red]未连接[/]");
                                }
                                rtStatusTable.AddRow("自动提示", Intelligence.IsTipsRunning ? "[green]运行中[/]" : "[grey]未运行[/]");
                                AnsiConsole.Write(rtStatusTable);
                                handled = true;
                                break;
                            case var cmd when cmd == "rt clients":
                                Output.Log("已连接面板列表：", 1, ThisProgramName);
                                if (Connector.ConnectedClientCount > 0)
                                {
                                    var clientsTable = new Table().Border(TableBorder.Rounded);
                                    clientsTable.AddColumn("序号").AddColumn("ID").AddColumn("IP").AddColumn("连接时间");
                                    int idx = 1;
                                    foreach (var kvp in Connector.ConnectedClients)
                                    {
                                        clientsTable.AddRow($"[cyan]{idx}[/]", Markup.Escape(kvp.Key), Markup.Escape(kvp.Value.IP), kvp.Value.ConnectTime.ToString("HH:mm:ss"));
                                        idx++;
                                    }
                                    AnsiConsole.Write(clientsTable);
                                    Output.Log("输入 rt clients kick <序号> 断开指定客户端", 1, ThisProgramName);
                                }
                                else
                                {
                                    Output.Log("无", 1, ThisProgramName);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd == "rt clients kick":
                                if (Connector.ConnectedClientCount == 0)
                                {
                                    Output.Log("当前没有已连接的管理面板", 1, ThisProgramName);
                                }
                                else
                                {
                                    Output.Log("用法: rt clients kick <序号>，使用 rt clients 查看列表", 1, ThisProgramName);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith("rt clients kick "):
                                string kickArg = cmd.Substring(16).Trim();
                                if (int.TryParse(kickArg, out int kickIndex) && kickIndex >= 1)
                                {
                                    var clientList = Connector.ConnectedClients.ToList();
                                    if (kickIndex <= clientList.Count)
                                    {
                                        var target = clientList[kickIndex - 1];
                                        Connector.UnregisterClient(target.Key);
                                        Output.Log($"已断开面板 {target.Key} ({target.Value.IP})", 1, ThisProgramName);
                                    }
                                    else
                                    {
                                        Output.Log($"序号超出范围，当前共 {clientList.Count} 个面板。", 2, ThisProgramName);
                                    }
                                }
                                else
                                {
                                    Output.Log("无效的序号，请输入正整数。用法: rt clients kick <序号>", 2, ThisProgramName);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd == "rt extensions":
                                Output.Log("已加载的扩展列表：", 1, ThisProgramName);
                                RtExtensionManager.DisplayLoadedExtensions();
                                handled = true;
                                break;
                            case var cmd when cmd == "rt extensions load":
                                Output.Log("用法: rt extensions load <扩展文件路径或文件名>", 1, ThisProgramName);
                                Output.Log($"扩展目录: {RtExtensionManager.GetExtensionsDirectory()}", 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == "rt extensions unload":
                                Output.Log("用法: rt extensions unload <扩展Key>", 1, ThisProgramName);
                                Output.Log("使用 rt extensions 查看已加载的扩展列表", 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith("rt extensions load "):
                                string loadPath = cmd.Substring(18).Trim();
                                if (!string.IsNullOrWhiteSpace(loadPath))
                                {
                                    RtExtensionManager.LoadExtensionByKey(loadPath);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith("rt extensions unload "):
                                string unloadKey = cmd.Substring(20).Trim();
                                if (!string.IsNullOrWhiteSpace(unloadKey))
                                {
                                    RtExtensionManager.UnloadExtensionByKey(unloadKey);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd == "rt start":
                                Connector.StartServerAsync().GetAwaiter().GetResult();
                                handled = true;
                                break;
                            case var cmd when cmd == "rt stop":
                                Connector.StopServerAsync().GetAwaiter().GetResult();
                                handled = true;
                                break;
                            case var cmd when cmd == ".help":
                                ShowExtensionCommands();
                                handled = true;
                                break;
                            case var cmd when cmd == ".guide":
                                Output.Log("服务端搭建教程：https://zh.minecraft.wiki/w/%E6%95%99%E7%A8%8B", 1, ThisProgramName);
                                Intelligence.Guide().GetAwaiter().GetResult();
                                handled = true;
                                break;
                            case var cmd when cmd == ".auto":
                                var autoRoot = new Tree("[cyan].auto 命令[/]");
                                autoRoot.AddNode("[green]on <任务名>[/] - 启用指定任务");
                                autoRoot.AddNode("[green]off <任务名>[/] - 禁用指定任务");
                                autoRoot.AddNode("[green]list[/] - 列出所有计划任务");
                                autoRoot.AddNode("[green]start[/] - 启动调度器");
                                autoRoot.AddNode("[green]stop[/] - 停止调度器");
                                AnsiConsole.Write(autoRoot);
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith(".auto on "):
                                string onTaskName = cmd.Substring(".auto on ".Length).Trim();
                                Scheduler.SetTaskEnabled(onTaskName, true);
                                handled = true;
                                break;
                            case var cmd when cmd == ".auto on":
                                Output.Log("用法: .auto on <任务名>", 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith(".auto off "):
                                string offTaskName = cmd.Substring(".auto off ".Length).Trim();
                                Scheduler.SetTaskEnabled(offTaskName, false);
                                handled = true;
                                break;
                            case var cmd when cmd == ".auto off":
                                Output.Log("用法: .auto off <任务名>", 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == ".auto list":
                                Scheduler.ListTasks();
                                handled = true;
                                break;
                            case var cmd when cmd == ".auto start":
                                Scheduler.Start();
                                handled = true;
                                break;
                            case var cmd when cmd == ".auto stop":
                                Scheduler.Stop();
                                handled = true;
                                break;
                            case var cmd when cmd == ".fx":
                                var fxRoot = new Tree("[cyan].fx 命令[/]");

                                var getNode = fxRoot.AddNode("[green]get[/] - 错误日志分析");
                                getNode.AddNode(".fx get - 获取MC服务端错误日志");
                                getNode.AddNode(".fx get <路径> - 获取外部日志文件");

                                var listNode = fxRoot.AddNode("[green]list[/] - 查看分析结果");
                                listNode.AddNode(".fx list - 列出所有错误分析结果");
                                listNode.AddNode(".fx list <n> - 列出第n次的分析结果");

                                fxRoot.AddNode("[green]del[/] - 删除所有错误分析结果");

                                var cgNode = fxRoot.AddNode("[green]clientguide[/] - 客户端连接问题诊断");
                                cgNode.AddNode(".fx clientguide - 开始诊断");
                                cgNode.AddNode(".fx clientguide <n> - 查看第n个匹配项的详细解决方案");

                                var baseNode = fxRoot.AddNode("[green]base[/] - 基础错误分析");
                                baseNode.AddNode(".fx base <n> - 分析第n个错误");
                                baseNode.AddNode(".fx base <n-m> - 合并第n到m个错误后分析");

                                var filterNode = fxRoot.AddNode("[green]filter[/] - 过滤问题备份工具");
                                var configNode = filterNode.AddNode("[yellow]config[/] - 配置文件管理");
                                configNode.AddNode(".fx filter config - 备份/对照配置文件");
                                configNode.AddNode(".fx filter config <n> - 还原第n个差异文件(0删除备份)");
                                var pluginNode = filterNode.AddNode("[yellow]plugin[/] - 插件管理");
                                pluginNode.AddNode(".fx filter plugin - 列出插件及状态");
                                pluginNode.AddNode(".fx filter plugin <n> - 切换启用/禁用(0重启)");

                                var aiNode = fxRoot.AddNode("[green]ai[/] - AI智能分析");
                                aiNode.AddNode(".fx ai <n> - 将第n个错误发送给AI分析");
                                aiNode.AddNode(".fx ai <n-m> - 合并第n到m个错误后发送给AI分析");

                                AnsiConsole.Write(fxRoot);
                                handled = true;
                                break;
                            case var cmd when cmd == ".fx get":
                                Analyzer.AnalyzeErrors();
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith(".fx get "):
                                string getArg = cmd.Substring(".fx get ".Length).Trim().Trim('"');
                                Analyzer.AnalyzeErrors(getArg);
                                handled = true;
                                break;
                            case var cmd when cmd == ".fx list":
                                Analyzer.ListErrors(null);
                                handled = true;
                                break;
                            case var cmd when cmd == ".fx del":
                                Analyzer.DeleteErrors();
                                handled = true;
                                break;
                            case var cmd when cmd == ".fx clientguide":
                                Analyzer.ClientGuide(null);
                                handled = true;
                                break;
                            case var cmd when cmd == ".fx base":
                                Analyzer.BaseAnalyze(null);
                                handled = true;
                                break;
                            case var cmd when cmd == ".fx ai":
                                Task.Run(() => Analyzer.AiAnalyze(null));
                                handled = true;
                                break;
                            case var cmd when cmd == ".fx filter":
                                Analyzer.FilterInfo();
                                handled = true;
                                break;
                            case var cmd when cmd == ".fx filter config":
                                Analyzer.FilterConfig(null);
                                handled = true;
                                break;
                            case var cmd when cmd == ".fx filter plugin":
                                Analyzer.FilterPlugin(null);
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith(".fx list "):
                                string listArg = cmd.Substring(".fx list ".Length).Trim();
                                if (int.TryParse(listArg, out int listIndex))
                                {
                                    Analyzer.ListErrors(listIndex);
                                }
                                else
                                {
                                    Output.Log("无效的参数，请输入数字。用法: .fx list <n>", 2, ThisProgramName);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith(".fx base "):
                                string baseArg = cmd.Substring(".fx base ".Length).Trim();
                                Analyzer.BaseAnalyze(baseArg);
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith(".fx ai "):
                                string aiArg = cmd.Substring(".fx ai ".Length).Trim();
                                Task.Run(() => Analyzer.AiAnalyze(aiArg));
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith(".fx clientguide "):
                                string cgArg = cmd.Substring(".fx clientguide ".Length).Trim();
                                if (int.TryParse(cgArg, out int cgIndex))
                                {
                                    Analyzer.ClientGuide(cgIndex);
                                }
                                else
                                {
                                    Output.Log("无效的参数，请输入数字。用法: .fx clientguide <n>", 2, ThisProgramName);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith(".fx filter config "):
                                string fcArg = cmd.Substring(".fx filter config ".Length).Trim();
                                if (int.TryParse(fcArg, out int fcIndex))
                                {
                                    Analyzer.FilterConfig(fcIndex);
                                }
                                else
                                {
                                    Output.Log("无效的参数，请输入数字。用法: .fx filter config <n>", 2, ThisProgramName);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith(".fx filter plugin "):
                                string fpArg = cmd.Substring(".fx filter plugin ".Length).Trim();
                                if (int.TryParse(fpArg, out int fpIndex))
                                {
                                    Analyzer.FilterPlugin(fpIndex);
                                }
                                else
                                {
                                    Output.Log("无效的参数，请输入数字。用法: .fx filter plugin <n>", 2, ThisProgramName);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd == ".cfg":
                                var cfgRoot = new Tree("[cyan].cfg 命令[/]");
                                cfgRoot.AddNode("[green]reload[/] - 热重载所有配置文件(包括语言脚本等)");
                                cfgRoot.AddNode("[green]clear[/] - 删除Content文件夹并冷重载");
                                AnsiConsole.Write(cfgRoot);
                                handled = true;
                                break;
                            case var cmd when cmd == ".cfg reload":
                                Config.ReloadAll();
                                handled = true;
                                break;
                            case var cmd when cmd == ".cfg clear":
                                Config.ClearContent();
                                handled = true;
                                break;
                            case var cmd when cmd == ".status":
                                Output.Log(".status 命令暂未实现。", 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == ".server":
                                Output.Log($"当前服务端: [cyan]{Markup.Escape(Config.App.CurrentServer)}[/] (模式: {Analyzer.CurrentMode})", 1, ThisProgramName);
                                Output.Log("子命令：list、add、del、change、start、stop、status", 1, ThisProgramName);
                                if (!Analyzer.NeedsRunServer)
                                    Output.Log("OnlyRcon模式额外子命令：get、connect、detach", 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == ".server list":
                                var serverListTable = new Table().Border(TableBorder.Rounded).Title("[cyan]服务端列表[/]");
                                serverListTable.AddColumn("标识").AddColumn("名称").AddColumn("模式").AddColumn("工作目录").AddColumn("当前");
                                foreach (var kvp in Config.App.ServerList)
                                {
                                    string isCurrent = kvp.Key == Config.App.CurrentServer ? "[green]*[/]" : "";
                                    string workDir = string.IsNullOrWhiteSpace(kvp.Value.WorkPath) ? "[grey]未配置[/]" : Markup.Escape(kvp.Value.WorkPath);
                                    serverListTable.AddRow(
                                        Markup.Escape(kvp.Key),
                                        Markup.Escape(kvp.Value.ServerName),
                                        Markup.Escape(kvp.Value.AnalyzerMode),
                                        workDir,
                                        isCurrent);
                                }
                                AnsiConsole.Write(serverListTable);
                                handled = true;
                                break;
                            case var cmd when cmd == ".server add":
                                Output.Log("添加新的MC服务端配置：", 1, ThisProgramName);
                                string? newKey = AnsiConsole.Ask<string>("请输入服务端 [cyan]标识[/]（英文）：");
                                if (string.IsNullOrWhiteSpace(newKey))
                                {
                                    Output.Log("标识不能为空。", 2, ThisProgramName);
                                }
                                else if (Config.App.ServerList.ContainsKey(newKey))
                                {
                                    Output.Log($"标识 '{Markup.Escape(newKey)}' 已存在。", 2, ThisProgramName);
                                }
                                else
                                {
                                    var newEntry = new ServerEntry();
                                    newEntry.ServerName = AnsiConsole.Ask("服务端 [cyan]名称[/]（显示名称）：", newKey);
                                    string inputWorkPath = AnsiConsole.Ask("服务端 [cyan]工作目录[/]（留空稍后配置）：", "");
                                    if (!string.IsNullOrWhiteSpace(inputWorkPath))
                                        newEntry.WorkPath = inputWorkPath;

                                    var modeSelect = AnsiConsole.Prompt(
                                        new SelectionPrompt<string>()
                                            .Title("选择 [cyan]控制台模式[/]：")
                                            .AddChoices("Management (推荐)", "Run", "Rcon", "OnlyRcon"));
                                    newEntry.AnalyzerMode = modeSelect.Split(' ')[0];

                                    if (newEntry.AnalyzerMode == "Management")
                                    {
                                        Output.Log("[yellow]Management模式需要MC 1.21.9+，服务端需开启management-server-enabled[/]", 1, ThisProgramName);
                                    }

                                    Config.App.ServerList[newKey] = newEntry;
                                    Config.SaveCurrentConfig();
                                    Output.Log($"已添加服务端 '{newKey}'，使用 .server change {newKey} 切换。", 1, ThisProgramName);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd == ".server del":
                                if (Config.App.ServerList.Count <= 1)
                                {
                                    Output.Log("至少需要保留一个服务端配置。", 2, ThisProgramName);
                                }
                                else
                                {
                                    string delTarget = AnsiConsole.Prompt(
                                        new SelectionPrompt<string>()
                                            .Title("选择要 [red]删除[/] 的服务端：")
                                            .AddChoices(Config.App.ServerList.Keys.ToList()));
                                    if (delTarget == Config.App.CurrentServer)
                                    {
                                        Output.Log("不能删除当前正在使用的服务端，请先 .server change 切换。", 2, ThisProgramName);
                                    }
                                    else
                                    {
                                        bool confirmDel = AnsiConsole.Confirm($"确定删除服务端 '{delTarget}'？", false);
                                        if (confirmDel)
                                        {
                                            Config.App.ServerList.Remove(delTarget);
                                            Config.SaveCurrentConfig();
                                            Output.Log($"已删除服务端 '{delTarget}'。", 1, ThisProgramName);
                                        }
                                    }
                                }
                                handled = true;
                                break;
                            case var cmd when cmd == ".server change":
                                Output.Log("用法: .server change <服务端标识>", 1, ThisProgramName);
                                Output.Log("已配置的服务端:", 1, ThisProgramName);
                                foreach (var kvp in Config.App.ServerList)
                                {
                                    string marker = kvp.Key == Config.App.CurrentServer ? " [green]<- 当前[/]" : "";
                                    Output.Log($"  {Markup.Escape(kvp.Key)}: {Markup.Escape(kvp.Value.ServerName)} (模式: {Markup.Escape(kvp.Value.AnalyzerMode)}){marker}", 1, ThisProgramName);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith(".server change "):
                                string changeArg = cmd.Substring(".server change ".Length).Trim();
                                if (string.IsNullOrWhiteSpace(changeArg))
                                {
                                    Output.Log("请指定服务端标识。", 2, ThisProgramName);
                                }
                                else if (Config.SwitchServer(changeArg))
                                {
                                    Analyzer.Initialize();
                                    Output.Log($"已切换到服务端: {Markup.Escape(changeArg)} ({Markup.Escape(Config.CurrentServer.ServerName)}) 模式: {Markup.Escape(Analyzer.CurrentMode)}", 1, ThisProgramName);
                                }
                                else
                                {
                                    Output.Log($"未找到服务端标识: {Markup.Escape(changeArg)}", 2, ThisProgramName);
                                    Output.Log("已配置的标识: " + string.Join(", ", Config.App.ServerList.Keys), 1, ThisProgramName);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd == ".server get":
                                if (Analyzer.NeedsRunServer)
                                    Output.Log("当前模式下不支持 .server get，请使用 .server start 启动服务端。", 2, ThisProgramName);
                                else
                                    Analyzer.ScanAndListServers();
                                handled = true;
                                break;
                            case var cmd when cmd == ".server connect":
                                if (Analyzer.NeedsRunServer)
                                    Output.Log("当前模式下不支持 .server connect，请使用 .server start 启动服务端。", 2, ThisProgramName);
                                else
                                    Output.Log("用法: .server connect <序号|pid:进程ID>", 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith(".server connect "):
                                if (Analyzer.NeedsRunServer)
                                {
                                    Output.Log("当前模式下不支持 .server connect，请使用 .server start 启动服务端。", 2, ThisProgramName);
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
                                if (Analyzer.NeedsRunServer)
                                    Output.Log("当前模式下不支持 .server detach，请使用 .server stop 停止服务端。", 2, ThisProgramName);
                                else
                                    Analyzer.Detach();
                                handled = true;
                                break;
                            case var cmd when cmd == ".server start":
                                if (Analyzer.NeedsRunServer)
                                    Analyzer.StartServer();
                                else
                                    Output.Log("OnlyRcon模式下不支持 .server start，请使用 .server get + .server connect 连接。", 2, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == ".server stop":
                                if (Analyzer.NeedsRunServer)
                                    Analyzer.StopServer();
                                else
                                    Output.Log("OnlyRcon模式下不支持 .server stop。", 2, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == ".server status":
                                if (Analyzer.NeedsRunServer)
                                    Output.Log(Analyzer.IsRunModeActive ? "服务端运行中。" : "服务端未运行。", 1, ThisProgramName);
                                else
                                    Output.Log(Analyzer.IsAttached ? "已连接到 Minecraft 服务端。" : "未连接到 Minecraft 服务端。", 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == "ct":
                                Output.Log("[cyan]ct[/] - 内嵌资源管理", 1, ThisProgramName);
                                Output.Log("  [green]ct list[/] - 列出所有内嵌资源", 1, ThisProgramName);
                                Output.Log("  [green]ct unpack <名称>[/] - 释放指定资源到程序根目录", 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == "ct list":
                                ContentManager.ListEmbeddedResources();
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith("ct unpack "):
                                string resourceName = cmd.Substring("ct unpack ".Length).Trim();
                                ContentManager.UnpackEmbeddedResource(resourceName);
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
                            if (CommandRegistry.TryExecuteWithArgs(cmd_input))
                            {
                                // 扩展命令(含参数)已执行
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

        private static void ShowAbout()
        {
            string runtimeVersion = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
            string osDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            string architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLower();

            AnsiConsole.Write(new Rule("[dodgerblue1]RutCitrus RtCli[/]").RuleStyle("dodgerblue1").Centered());

            var infoTable = new Table().NoBorder().HideHeaders().AddColumn("").AddColumn("");
            infoTable.AddRow("[yellow]版本[/]", $"[white]{Markup.Escape(RtCliVersion)}[/]");
            infoTable.AddRow("[yellow]作者[/]", "[white]psoloi[/]");
            infoTable.AddRow("[yellow]项目[/]", "[link]https://github.com/psoloi/RutCitrus[/]");
            infoTable.AddRow("[yellow]环境[/]", $"[white]{Markup.Escape(runtimeVersion)}[/]");
            infoTable.AddRow("[yellow]系统[/]", $"[white]{Markup.Escape(osDescription)}[/]");
            infoTable.AddRow("[yellow]架构[/]", $"[white]{architecture}[/]");
            infoTable.AddRow("[yellow]Java[/]", $"[white]{Markup.Escape(Checker.CheckJava())}[/]");
            infoTable.AddRow("[yellow].NET[/]", $"[white]{Markup.Escape(Checker.CheckDotNet())}[/]");
            infoTable.AddRow("[yellow]Python[/]", $"[white]{Markup.Escape(Checker.CheckPython())}[/]");
            infoTable.AddRow("[yellow]系统位数[/]", $"[white]{(Environment.Is64BitOperatingSystem ? "64位" : "32位")}[/]");
            AnsiConsole.Write(infoTable);

            AnsiConsole.WriteLine();

            var libTable = new Table().Border(TableBorder.Rounded).Title("[yellow]依赖库[/]");
            libTable.AddColumn("库");
            libTable.AddColumn("版本");

            var libs = new (string Name, string Version)[]
            {
                ("Spectre.Console", "0.55.2"),
                ("Newtonsoft.Json", "13.0.4"),
                ("Serilog", "4.3.1"),
                ("Serilog.Sinks.File", "7.0.0"),
                ("YamlDotNet", "18.0.0"),
                ("Grpc.AspNetCore", "2.80.0"),
                ("Grpc.Core", "2.46.6"),
                ("Google.Protobuf", "3.35.0"),
                ("RestSharp", "114.0.0"),
                ("System.Management", "10.0.8"),
                ("TouchSocket", "2.3.6"),
                ("Polly", "8.6.6"),
                ("Mono.Cecil", "0.11.6"),
                ("Microsoft.Extensions.AI", "10.6.0"),
                ("Hangfire.Core", "1.8.23"),
                ("CS-Script", "4.14.9"),
                ("CSnakes.Runtime", "1.2.1"),
            };

            foreach (var lib in libs)
            {
                libTable.AddRow($"[cyan]{lib.Name}[/]", $"[grey]{lib.Version}[/]");
            }

            AnsiConsole.Write(libTable);
            AnsiConsole.Write(new Rule().RuleStyle("dodgerblue1"));
        }

        private static void ShowHelp()
        {
            Output.Log("RtCli程序命令列表：", 1, ThisProgramName);
            var table = new Table()
                .AddColumn("命令")
                .AddColumn("描述")
                .AddRow("[green]rt[/]", "显示版本信息")
                .AddRow("[green]rt help[/]", "显示此列表")
                .AddRow("[green]rt about[/]", "显示程序信息")
                .AddRow("[white]rt end[/]", "关闭程序")
                .AddRow("[white]rt reload[/]", "重新加载")
                .AddRow("[white]rt status[/]", "查看RtCli运行状态")
                .AddRow("[white]rt clients[/]", "已连接面板列表")
                .AddRow("[white]rt clients kick <序号>[/]", "断开指定客户端连接")
                .AddRow("[white]rt extensions[/]", "已加载的扩展列表")
                .AddRow("[white]rt extensions load <路径>[/]", "加载指定扩展")
                .AddRow("[white]rt extensions unload <Key>[/]", "卸载指定扩展")
                .AddRow("[white]rt start[/]", "启动管理端口")
                .AddRow("[white]rt stop[/]", "关闭管理端口")
                .AddRow("[green].help[/]", "显示扩展命令列表")
                .AddRow("[white].guide[/]", "MC服务端安装引导")
                .AddRow("[white].auto[/]", "调度器 (输入 .auto 查看子命令)")
                .AddRow("[white].auto on <任务名>[/]", "启用指定计划任务")
                .AddRow("[white].auto off <任务名>[/]", "禁用指定计划任务")
                .AddRow("[white].auto list[/]", "列出所有计划任务")
                .AddRow("[white].auto start[/]", "启动调度器")
                .AddRow("[white].auto stop[/]", "停止调度器")
                .AddRow("[white].fx[/]", "错误分析/客户端诊断 (输入 .fx 查看子命令)")
                .AddRow("[white].fx get[/]", "获取MC服务端错误日志")
                .AddRow("[white].fx list[/]", "列出所有错误分析结果")
                .AddRow("[white].fx list <n>[/]", "列出第n次的分析结果")
                .AddRow("[white].fx del[/]", "删除所有错误分析结果")
                .AddRow("[white].fx clientguide[/]", "客户端连接问题诊断")
                .AddRow("[white].fx clientguide <n>[/]", "查看第n个匹配项的详细解决方案")
                .AddRow("[white].fx base[/]", "基础错误分析")
                .AddRow("[white].fx base <n>[/]", "分析第n次的基础错误")
                .AddRow("[white].fx ai[/]", "AI 错误分析")
                .AddRow("[white].fx ai <n>[/]", "AI 分析第n次的错误")
                .AddRow("[white].fx filter[/]", "过滤问题备份工具")
                .AddRow("[white].fx filter config[/]", "备份/对照/还原配置文件")
                .AddRow("[white].fx filter config <n>[/]", "还原第n个差异文件(0删除备份)")
                .AddRow("[white].fx filter plugin[/]", "列出/禁用/启用插件")
                .AddRow("[white].fx filter plugin <n>[/]", "切换第n个插件启用/禁用(0重启)")
                .AddRow("[white].cfg[/]", "配置管理 (输入 .cfg 查看子命令)")
                .AddRow("[white].cfg reload[/]", "热重载所有配置文件")
                .AddRow("[white].cfg clear[/]", "删除Content文件夹并冷重载")
                .AddRow("[white].status[/]", "查看RtCli运行状态")
                .AddRow("[white].server[/]", "MC控制台相关命令 (输入 .server 查看子命令)")
                .AddRow("[white].server list[/]", "列出所有已配置的MC服务端")
                .AddRow("[white].server add[/]", "添加新的MC服务端配置")
                .AddRow("[white].server del[/]", "删除MC服务端配置")
                .AddRow("[white].server change <标识>[/]", "切换当前MC服务端")
                .AddRow("[white].server get[/]", "[[[DarkOrange]OnlyRcon[/]]] 扫描并列出运行中的MC服务端")
                .AddRow("[white].server connect <序号|pid:进程ID>[/]", "[[[DarkOrange]OnlyRcon[/]]] 连接到指定的MC服务端")
                .AddRow("[white].server detach[/]", "[[[DarkOrange]OnlyRcon[/]]] 断开与MC服务端的连接")
                .AddRow("[white].server start[/]", "[[[green]Run/Rcon/Mgmt[/]]] 启动MC服务端作为子进程")
                .AddRow("[white].server stop[/]", "[[[green]Run/Rcon/Mgmt[/]]] 停止MC服务端")
                .AddRow("[white].server status[/]", "查看MC服务端连接/运行状态")
                .AddRow("[green]/<命令>[/]", "发送命令到MC服务端");
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
