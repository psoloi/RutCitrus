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
        // 多数测试及功能完善，AI无人化优化(skill.md文件夹)

        // 优化文字和整理及I18n（超微终止了）
        // 服务端信息获取模式（Run、Rcon、RR、RM）"四架构 理功能"
        // 添加更多错误识别并分类（mc和程序自身的）

        // BlueMap和Litebans他们的MySQL支持

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
            ".ai",
            ".ai start",
            ".ai stop",
            ".ai list",
            ".ai reload",
            ".ai run",
            ".ai clear",
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
            ".lang",
            ".lang zh_CN",
            ".lang en_US",
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
            ".group",
            ".group add",
            ".group del",
            ".group list",
            ".group set",
            ".group unset",
            ".group build",
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
            Support.Initialize();
            Intelligence.StartAutoBackup();
            Backend.Initialize();

            // 若启用 start_run_ai，则在配置加载完成后自动启动AI自动化管理
            if (Config.App.StartRunAi)
            {
                Output.Log(I18n.Get("main_ai_autostart"), 1, ThisProgramName);
            }

            if (Config.App.Debug.ToLower() != "No")
            {
                Commands.Execute(Config.App.Debug);
            }

            RtExtensionManager.LoadAll();

            Scripts.Initialize();
            Scripts.LoadSettingsAndSubscribe();
            Support.Start();

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
                    Output.Log(I18n.Get("main_runext"), 1, ThisProgramName);
                    RtExtensionManager.DisplayLoadedExtensions();
                    RtExtensionManager.Run();

                    // 若启用 start_run_ai，则自动启动AI自动化管理
                    if (Config.App.StartRunAi)
                    {
                        try
                        {
                            Output.Log(I18n.Get("main_ai_starting"), 1, ThisProgramName);
                            Intelligence.AiAutoRunner.Start();
                        }
                        catch (Exception ex)
                        {
                            Output.Log(I18n.Get("main_ai_fail", ex.Message), 2, ThisProgramName);
                        }
                    }

                    Continued();
                    break;

                case "debug":
                    EventBus.Publish(new ModeSelectedEvent(I18n.Get("main_selmode_debug")));
                    Output.ReportError(new Exception("神秘错误"), true, "这只是一个彩蛋");
                    Task.Run(() =>
                    {
                        Output.Log(I18n.Get("main_debug_unimpl"), 2, ThisProgramName);
                        throw new IOException("无测试");
                    }).Wait();
                    Commands.Execute("1");
                    break;

                case "exit":
                    Reload.End();
                    break;

                case "reload":
                    Output.Log(I18n.Get("main_reloading"), 1, ThisProgramName);
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
            string completionPrefix = "";
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
                            var paramMatches = GetParameterCompletions(currentInput, out completionPrefix);
                            if (paramMatches != null)
                            {
                                currentMatches = paramMatches;
                            }
                            else
                            {
                                completionPrefix = "";
                                currentMatches = _allCommands
                                    .Where(cmd => cmd.StartsWith(currentInput, StringComparison.OrdinalIgnoreCase))
                                    .ToList();
                            }
                            tabIndex = 0;
                        }
                        else
                        {
                            tabIndex = (tabIndex + 1) % currentMatches.Count;
                        }

                        if (currentMatches.Count > 0)
                        {
                            input.Clear();
                            input.Append(completionPrefix + currentMatches[tabIndex]);
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

        /// <summary>
        /// 根据当前输入返回参数级补全候选。返回 null 表示不处于已知的参数补全场景，
        /// 此时调用方应退回默认的命令名补全。prefix 为补全时需保留的命令前缀。
        /// </summary>
        private static List<string>? GetParameterCompletions(string input, out string prefix)
        {
            prefix = "";

            // ===== 单参数：服务端标识 =====
            if (TryExtractArgPrefix(input, ".server change", out string arg, out prefix))
                return CompletePrefix(arg, Config.App.ServerList.Keys);
            if (TryExtractArgPrefix(input, ".group unset", out arg, out prefix))
                return CompletePrefix(arg, Config.App.ServerList.Keys);

            // ===== 单参数：群组ID =====
            if (TryExtractArgPrefix(input, ".group del", out arg, out prefix))
                return CompletePrefix(arg, GetGroupIds());

            // ===== 单参数：扩展Key =====
            if (TryExtractArgPrefix(input, "rt extensions unload", out arg, out prefix))
                return CompletePrefix(arg, RtExtensionManager.LoadedExtensions.Keys);

            // ===== 单参数：客户端序号 =====
            if (TryExtractArgPrefix(input, "rt clients kick", out arg, out prefix))
                return CompletePrefix(arg, GetClientIndices());

            // ===== 单参数：AI 任务名 =====
            if (TryExtractArgPrefix(input, ".ai run", out arg, out prefix))
                return CompletePrefix(arg, GetAiTaskIds());
            if (TryExtractArgPrefix(input, ".ai clear", out arg, out prefix))
                return CompletePrefix(arg, GetAiTaskIds().Concat(new[] { "all" }));

            // ===== 单参数：调度任务名 =====
            if (TryExtractArgPrefix(input, ".auto on", out arg, out prefix))
                return CompletePrefix(arg, Scheduler.Settings.Tasks.Keys);
            if (TryExtractArgPrefix(input, ".auto off", out arg, out prefix))
                return CompletePrefix(arg, Scheduler.Settings.Tasks.Keys);

            // ===== 双参数：.group set <服务器标识> <群组ID> =====
            if (TryExtractArgPrefix(input, ".group set", out arg, out prefix))
            {
                bool trailing = arg.EndsWith(' ');
                var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int completed = trailing ? parts.Length : Math.Max(0, parts.Length - 1);

                if (completed == 0)
                {
                    // 正在输入第一个参数：服务器标识
                    prefix = ".group set ";
                    string partial = parts.Length > 0 ? parts[0] : "";
                    return CompletePrefix(partial, Config.App.ServerList.Keys);
                }
                if (completed == 1)
                {
                    // 正在输入第二个参数：群组ID
                    prefix = ".group set " + parts[0] + " ";
                    string partial = parts.Length > 1 ? parts[1] : "";
                    return CompletePrefix(partial, GetGroupIds());
                }
                return new List<string>();
            }

            return null;
        }

        /// <summary>按前缀(忽略大小写)过滤候选并排序。</summary>
        private static List<string> CompletePrefix(string partial, IEnumerable<string> all)
        {
            return all
                .Where(k => k.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>获取已配置的群组ID列表。</summary>
        private static IEnumerable<string> GetGroupIds()
            => ServerDataManager.Data.Groups.Select(g => g.Id);

        /// <summary>获取已连接面板的序号列表(从 1 开始)。</summary>
        private static IEnumerable<string> GetClientIndices()
            => Enumerable.Range(1, Connector.ConnectedClientCount).Select(n => n.ToString());

        /// <summary>获取AI自动化任务标识列表。</summary>
        private static IEnumerable<string> GetAiTaskIds()
        {
            var tasks = ContentManager.Ai.ServerAutoAi.Tasks;
            return tasks == null ? Enumerable.Empty<string>() : tasks.Keys;
        }

        /// <summary>
        /// 判断输入是否位于指定命令的参数位置，并拆分出“已键入参数片段”与“命令前缀”。
        /// 仅当输入为“命令 + 空格(+ 部分参数)”时视为参数位置。
        /// </summary>
        private static bool TryExtractArgPrefix(string input, string command, out string arg, out string cmdPrefix)
        {
            arg = "";
            cmdPrefix = "";

            if (input.Length > command.Length
                && input.StartsWith(command, StringComparison.OrdinalIgnoreCase)
                && input[command.Length] == ' ')
            {
                cmdPrefix = command + " ";
                arg = input.Substring(command.Length + 1);
                return true;
            }

            return false;
        }

        #endregion

        public static void Continued()
        {
            try
            {
                Output.Log(I18n.Get("prog_security_notice"), 2, ThisProgramName);
                try
                {
                    Connector.StartServerAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Output.ReportError(ex, false, I18n.Get("prog_manage_port_fail"));
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
                                Output.Log(I18n.Get("prog_version_info", Markup.Escape(RtCliVersion), RtCliInformation), 1, ThisProgramName);
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
                                Output.Log(I18n.Get("main_reloading"), 1, ThisProgramName);
                                Reload.Restart();
                                handled = true;
                                break;
                            case var cmd when cmd == "rt status":
                                var rtStatusTable = new Table().Border(TableBorder.Rounded).Title(I18n.Get("prog_status_title"));
                                rtStatusTable.AddColumn(I18n.Get("prog_col_item")).AddColumn(I18n.Get("prog_col_status"));
                                rtStatusTable.AddRow(I18n.Get("prog_status_manage_port"), Connector.IsRunning ? I18n.Get("prog_state_running") : I18n.Get("prog_state_not_running"));
                                rtStatusTable.AddRow(I18n.Get("prog_status_connected_panels"), Connector.ConnectedClientCount.ToString());
                                rtStatusTable.AddRow(I18n.Get("prog_status_current_server"), $"[cyan]{Markup.Escape(Config.App.CurrentServer)}[/] ({Markup.Escape(Config.CurrentServer.ServerName)})");
                                rtStatusTable.AddRow(I18n.Get("prog_status_mc_mode"), $"[cyan]{Analyzer.CurrentMode}[/]");
                                if (Analyzer.NeedsRunServer)
                                {
                                    rtStatusTable.AddRow(I18n.Get("prog_status_mc_server"), Analyzer.IsRunModeActive ? I18n.Get("prog_state_running") : I18n.Get("prog_state_not_running"));
                                }
                                else
                                {
                                    rtStatusTable.AddRow(I18n.Get("prog_status_mc_connection"), Analyzer.IsAttached ? I18n.Get("prog_state_connected") : I18n.Get("prog_state_disconnected"));
                                }
                                rtStatusTable.AddRow(I18n.Get("prog_status_auto_tips"), Intelligence.IsTipsRunning ? I18n.Get("prog_state_running") : I18n.Get("prog_state_not_running_grey"));
                                AnsiConsole.Write(rtStatusTable);
                                handled = true;
                                break;
                            case var cmd when cmd == "rt clients":
                                Output.Log(I18n.Get("prog_clients_list_header"), 1, ThisProgramName);
                                if (Connector.ConnectedClientCount > 0)
                                {
                                    var clientsTable = new Table().Border(TableBorder.Rounded);
                                    clientsTable.AddColumn(I18n.Get("prog_col_index")).AddColumn("ID").AddColumn("IP").AddColumn(I18n.Get("prog_col_connect_time"));
                                    int idx = 1;
                                    foreach (var kvp in Connector.ConnectedClients)
                                    {
                                        clientsTable.AddRow($"[cyan]{idx}[/]", Markup.Escape(kvp.Key), Markup.Escape(kvp.Value.IP), kvp.Value.ConnectTime.ToString("HH:mm:ss"));
                                        idx++;
                                    }
                                    AnsiConsole.Write(clientsTable);
                                    Output.Log(I18n.Get("prog_clients_kick_hint"), 1, ThisProgramName);
                                }
                                else
                                {
                                    Output.Log(I18n.Get("prog_none"), 1, ThisProgramName);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd == "rt clients kick":
                                if (Connector.ConnectedClientCount == 0)
                                {
                                    Output.Log(I18n.Get("prog_no_connected_panels"), 1, ThisProgramName);
                                }
                                else
                                {
                                    Output.Log(I18n.Get("prog_kick_usage"), 1, ThisProgramName);
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
                                        Output.Log(I18n.Get("prog_client_disconnected", target.Key, target.Value.IP), 1, ThisProgramName);
                                    }
                                    else
                                    {
                                        Output.Log(I18n.Get("prog_index_out_of_range", clientList.Count), 2, ThisProgramName);
                                    }
                                }
                                else
                                {
                                    Output.Log(I18n.Get("prog_invalid_index"), 2, ThisProgramName);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd == "rt extensions":
                                Output.Log(I18n.Get("prog_ext_list_header"), 1, ThisProgramName);
                                RtExtensionManager.DisplayLoadedExtensions();
                                handled = true;
                                break;
                            case var cmd when cmd == "rt extensions load":
                                Output.Log(I18n.Get("prog_ext_load_usage"), 1, ThisProgramName);
                                Output.Log(I18n.Get("prog_ext_dir", RtExtensionManager.GetExtensionsDirectory()), 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == "rt extensions unload":
                                Output.Log(I18n.Get("prog_ext_unload_usage"), 1, ThisProgramName);
                                Output.Log(I18n.Get("prog_ext_view_list"), 1, ThisProgramName);
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
                                Output.Log(I18n.Get("prog_guide_tutorial"), 1, ThisProgramName);
                                Intelligence.Guide().GetAwaiter().GetResult();
                                handled = true;
                                break;
                            case var cmd when cmd == ".auto":
                                var autoRoot = new Tree(I18n.Get("prog_auto_tree_title"));
                                autoRoot.AddNode(I18n.Get("prog_auto_on_desc"));
                                autoRoot.AddNode(I18n.Get("prog_auto_off_desc"));
                                autoRoot.AddNode(I18n.Get("prog_auto_list_desc"));
                                autoRoot.AddNode(I18n.Get("prog_auto_start_desc"));
                                autoRoot.AddNode(I18n.Get("prog_auto_stop_desc"));
                                AnsiConsole.Write(autoRoot);
                                handled = true;
                                break;
                            case var cmd when cmd == ".ai":
                                var aiRoot = new Tree(I18n.Get("prog_ai_tree_title"));
                                aiRoot.AddNode(I18n.Get("prog_ai_start_desc"));
                                aiRoot.AddNode(I18n.Get("prog_ai_stop_desc"));
                                aiRoot.AddNode(I18n.Get("prog_ai_list_desc"));
                                aiRoot.AddNode(I18n.Get("prog_ai_reload_desc"));
                                aiRoot.AddNode(I18n.Get("prog_ai_run_desc"));
                                aiRoot.AddNode(I18n.Get("prog_ai_clear_task_desc"));
                                aiRoot.AddNode(I18n.Get("prog_ai_clear_desc"));
                                AnsiConsole.Write(aiRoot);
                                handled = true;
                                break;
                            case var cmd when cmd == ".ai start":
                                Intelligence.AiAutoRunner.Start();
                                handled = true;
                                break;
                            case var cmd when cmd == ".ai stop":
                                Intelligence.AiAutoRunner.Stop();
                                handled = true;
                                break;
                            case var cmd when cmd == ".ai list":
                                Intelligence.AiAutoRunner.ListTasks();
                                handled = true;
                                break;
                            case var cmd when cmd == ".ai reload":
                                Intelligence.AiAutoRunner.Reload();
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith(".ai run "):
                                string aiRunTaskName = cmd.Substring(".ai run ".Length).Trim();
                                _ = Task.Run(async () => await Intelligence.AiAutoRunner.TriggerTaskAsync(aiRunTaskName));
                                handled = true;
                                break;
                            case var cmd when cmd == ".ai run":
                                Output.Log(I18n.Get("prog_ai_run_usage"), 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith(".ai clear "):
                                string aiClearTaskName = cmd.Substring(".ai clear ".Length).Trim();
                                if (string.IsNullOrWhiteSpace(aiClearTaskName) || aiClearTaskName.Equals("all", StringComparison.OrdinalIgnoreCase))
                                    Intelligence.AiAutoRunner.ClearAllContextCache();
                                else
                                    Intelligence.AiAutoRunner.ClearContextCache(aiClearTaskName);
                                handled = true;
                                break;
                            case var cmd when cmd == ".ai clear":
                                Intelligence.AiAutoRunner.ClearAllContextCache();
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith(".auto on "):
                                string onTaskName = cmd.Substring(".auto on ".Length).Trim();
                                Scheduler.SetTaskEnabled(onTaskName, true);
                                handled = true;
                                break;
                            case var cmd when cmd == ".auto on":
                                Output.Log(I18n.Get("prog_auto_on_usage"), 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith(".auto off "):
                                string offTaskName = cmd.Substring(".auto off ".Length).Trim();
                                Scheduler.SetTaskEnabled(offTaskName, false);
                                handled = true;
                                break;
                            case var cmd when cmd == ".auto off":
                                Output.Log(I18n.Get("prog_auto_off_usage"), 1, ThisProgramName);
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
                                var fxRoot = new Tree(I18n.Get("prog_fx_tree_title"));

                                var getNode = fxRoot.AddNode(I18n.Get("prog_fx_get_desc"));
                                getNode.AddNode(I18n.Get("prog_fx_get_server_log"));
                                getNode.AddNode(I18n.Get("prog_fx_get_external_log"));

                                var listNode = fxRoot.AddNode(I18n.Get("prog_fx_list_desc"));
                                listNode.AddNode(I18n.Get("prog_fx_list_all"));
                                listNode.AddNode(I18n.Get("prog_fx_list_n"));

                                fxRoot.AddNode(I18n.Get("prog_fx_del_desc"));

                                var cgNode = fxRoot.AddNode(I18n.Get("prog_fx_cg_desc"));
                                cgNode.AddNode(I18n.Get("prog_fx_cg_start"));
                                cgNode.AddNode(I18n.Get("prog_fx_cg_n"));

                                var baseNode = fxRoot.AddNode(I18n.Get("prog_fx_base_desc"));
                                baseNode.AddNode(I18n.Get("prog_fx_base_n"));
                                baseNode.AddNode(I18n.Get("prog_fx_base_range"));

                                var filterNode = fxRoot.AddNode(I18n.Get("prog_fx_filter_desc"));
                                var configNode = filterNode.AddNode(I18n.Get("prog_fx_filter_config_desc"));
                                configNode.AddNode(I18n.Get("prog_fx_filter_config"));
                                configNode.AddNode(I18n.Get("prog_fx_filter_config_n"));
                                var pluginNode = filterNode.AddNode(I18n.Get("prog_fx_filter_plugin_desc"));
                                pluginNode.AddNode(I18n.Get("prog_fx_filter_plugin"));
                                pluginNode.AddNode(I18n.Get("prog_fx_filter_plugin_n"));
                                var modNode = filterNode.AddNode(I18n.Get("prog_fx_filter_mod_desc"));
                                modNode.AddNode(I18n.Get("prog_fx_filter_mod"));
                                modNode.AddNode(I18n.Get("prog_fx_filter_mod_n"));

                                var aiNode = fxRoot.AddNode(I18n.Get("prog_fx_ai_desc"));
                                aiNode.AddNode(I18n.Get("prog_fx_ai_n"));
                                aiNode.AddNode(I18n.Get("prog_fx_ai_range"));

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
                                    Output.Log(I18n.Get("prog_fx_list_invalid"), 2, ThisProgramName);
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
                                    Output.Log(I18n.Get("prog_fx_cg_invalid"), 2, ThisProgramName);
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
                                    Output.Log(I18n.Get("prog_fx_fc_invalid"), 2, ThisProgramName);
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
                                    Output.Log(I18n.Get("prog_fx_fp_invalid"), 2, ThisProgramName);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd == ".fx filter mod":
                                Analyzer.FilterMod(null);
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith(".fx filter mod "):
                                string fmArg = cmd.Substring(".fx filter mod ".Length).Trim();
                                if (int.TryParse(fmArg, out int fmIndex))
                                {
                                    Analyzer.FilterMod(fmIndex);
                                }
                                else
                                {
                                    Output.Log(I18n.Get("prog_fx_fm_invalid"), 2, ThisProgramName);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd == ".cfg":
                                var cfgRoot = new Tree($"[cyan]{I18n.Get("cfg_title")}[/]");
                                cfgRoot.AddNode($"[green]reload[/] - {I18n.Get("cfg_reload_desc")}");
                                cfgRoot.AddNode($"[green]clear[/] - {I18n.Get("cfg_clear_desc")}");
                                AnsiConsole.Write(cfgRoot);
                                handled = true;
                                break;
                            case var cmd when cmd == ".lang":
                                {
                                    var langRoot = new Tree($"[cyan]{I18n.Get("lang_title")}[/]");
                                    langRoot.AddNode(I18n.Get("lang_current", I18n.CurrentLang));
                                    langRoot.AddNode(I18n.Get("lang_supported", string.Join(", ", I18n.SupportedLangs)));
                                    langRoot.AddNode(I18n.Get("lang_usage"));
                                    AnsiConsole.Write(langRoot);
                                    handled = true;
                                    break;
                                }
                            case var cmd when cmd.StartsWith(".lang "):
                                {
                                    string langArg = cmd.Substring(".lang ".Length).Trim();
                                    if (I18n.SetLanguageAndSave(langArg))
                                        Output.Log(I18n.Get("lang_changed", langArg), 1, ThisProgramName);
                                    else
                                        Output.Log(I18n.Get("lang_invalid", langArg), 2, ThisProgramName);
                                    handled = true;
                                    break;
                                }
                            case var cmd when cmd == ".cfg reload":
                                Config.ReloadAll();
                                handled = true;
                                break;
                            case var cmd when cmd == ".cfg clear":
                                Config.ClearContent();
                                handled = true;
                                break;
                            case var cmd when cmd == ".status":
                                Output.Log(I18n.Get("prog_status_not_impl"), 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == ".server":
                                Output.Log(I18n.Get("prog_server_current", Markup.Escape(Config.App.CurrentServer), Analyzer.CurrentMode), 1, ThisProgramName);
                                Output.Log(I18n.Get("prog_server_subcommands"), 1, ThisProgramName);
                                if (!Analyzer.NeedsRunServer)
                                    Output.Log(I18n.Get("prog_server_rcon_extra"), 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == ".server list":
                                var serverListTable = new Table().Border(TableBorder.Rounded).Title(I18n.Get("prog_server_list_title"));
                                serverListTable.AddColumn(I18n.Get("prog_col_key")).AddColumn(I18n.Get("prog_col_name")).AddColumn(I18n.Get("prog_col_mode")).AddColumn(I18n.Get("prog_col_workdir")).AddColumn(I18n.Get("prog_col_current"));
                                foreach (var kvp in Config.App.ServerList)
                                {
                                    string isCurrent = kvp.Key == Config.App.CurrentServer ? "[green]*[/]" : "";
                                    string workDir = string.IsNullOrWhiteSpace(kvp.Value.WorkPath) ? I18n.Get("prog_not_configured_grey") : Markup.Escape(kvp.Value.WorkPath);
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
                                Output.Log(I18n.Get("prog_server_add_header"), 1, ThisProgramName);
                                string? newKey = AnsiConsole.Ask<string>(I18n.Get("prog_server_add_key_prompt"));
                                if (string.IsNullOrWhiteSpace(newKey))
                                {
                                    Output.Log(I18n.Get("prog_server_key_empty"), 2, ThisProgramName);
                                }
                                else if (Config.App.ServerList.ContainsKey(newKey))
                                {
                                    Output.Log(I18n.Get("prog_server_key_exists", Markup.Escape(newKey)), 2, ThisProgramName);
                                }
                                else
                                {
                                    var newEntry = new ServerEntry();
                                    newEntry.ServerName = AnsiConsole.Ask(I18n.Get("prog_server_add_name_prompt"), newKey);
                                    string inputWorkPath = AnsiConsole.Ask(I18n.Get("prog_server_add_workdir_prompt"), "");
                                    if (!string.IsNullOrWhiteSpace(inputWorkPath))
                                        newEntry.WorkPath = inputWorkPath;

                                    var modeSelect = AnsiConsole.Prompt(
                                        new SelectionPrompt<string>()
                                            .Title(I18n.Get("prog_server_add_mode_title"))
                                            .AddChoices(I18n.Get("prog_mode_rm_recommended"), "Run", "RR", "Rcon"));
                                    newEntry.AnalyzerMode = modeSelect.Split(' ')[0];

                                    if (newEntry.AnalyzerMode == "RM")
                                    {
                                        Output.Log(I18n.Get("prog_server_rm_mode_info"), 1, ThisProgramName);
                                    }

                                    Config.App.ServerList[newKey] = newEntry;
                                    Config.SaveCurrentConfig();
                                    Output.Log(I18n.Get("prog_server_added", newKey), 1, ThisProgramName);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd == ".server del":
                                if (Config.App.ServerList.Count <= 1)
                                {
                                    Output.Log(I18n.Get("prog_server_del_min"), 2, ThisProgramName);
                                }
                                else
                                {
                                    string delTarget = AnsiConsole.Prompt(
                                        new SelectionPrompt<string>()
                                            .Title(I18n.Get("prog_server_del_title"))
                                            .AddChoices(Config.App.ServerList.Keys.ToList()));
                                    if (delTarget == Config.App.CurrentServer)
                                    {
                                        Output.Log(I18n.Get("prog_server_del_current"), 2, ThisProgramName);
                                    }
                                    else
                                    {
                                        bool confirmDel = AnsiConsole.Confirm(I18n.Get("prog_server_del_confirm", delTarget), false);
                                        if (confirmDel)
                                        {
                                            Config.App.ServerList.Remove(delTarget);
                                            Config.SaveCurrentConfig();
                                            Output.Log(I18n.Get("prog_server_deleted", delTarget), 1, ThisProgramName);
                                        }
                                    }
                                }
                                handled = true;
                                break;
                            case var cmd when cmd == ".server change":
                                Output.Log(I18n.Get("prog_server_change_usage"), 1, ThisProgramName);
                                Output.Log(I18n.Get("prog_server_configured"), 1, ThisProgramName);
                                foreach (var kvp in Config.App.ServerList)
                                {
                                    string marker = kvp.Key == Config.App.CurrentServer ? I18n.Get("prog_server_current_marker") : "";
                                    Output.Log(I18n.Get("prog_server_entry_line", Markup.Escape(kvp.Key), Markup.Escape(kvp.Value.ServerName), Markup.Escape(kvp.Value.AnalyzerMode), marker), 1, ThisProgramName);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith(".server change "):
                                string changeArg = cmd.Substring(".server change ".Length).Trim();
                                if (string.IsNullOrWhiteSpace(changeArg))
                                {
                                    Output.Log(I18n.Get("prog_server_change_empty"), 2, ThisProgramName);
                                }
                                else if (Config.SwitchServer(changeArg))
                                {
                                    Analyzer.Initialize();
                                    Output.Log(I18n.Get("prog_server_switched", Markup.Escape(changeArg), Markup.Escape(Config.CurrentServer.ServerName), Markup.Escape(Analyzer.CurrentMode)), 1, ThisProgramName);
                                }
                                else
                                {
                                    Output.Log(I18n.Get("prog_server_not_found", Markup.Escape(changeArg)), 2, ThisProgramName);
                                    Output.Log(I18n.Get("prog_server_configured_keys", string.Join(", ", Config.App.ServerList.Keys)), 1, ThisProgramName);
                                }
                                handled = true;
                                break;
                            case var cmd when cmd == ".server get":
                                if (Analyzer.NeedsRunServer)
                                    Output.Log(I18n.Get("prog_server_get_unsupported"), 2, ThisProgramName);
                                else
                                    Analyzer.ScanAndListServers();
                                handled = true;
                                break;
                            case var cmd when cmd == ".server connect":
                                if (Analyzer.NeedsRunServer)
                                    Output.Log(I18n.Get("prog_server_connect_unsupported"), 2, ThisProgramName);
                                else
                                    Output.Log(I18n.Get("prog_server_connect_usage"), 1, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd != null && cmd.StartsWith(".server connect "):
                                if (Analyzer.NeedsRunServer)
                                {
                                    Output.Log(I18n.Get("prog_server_connect_unsupported"), 2, ThisProgramName);
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
                                                Output.Log(I18n.Get("prog_invalid_pid"), 2, ThisProgramName);
                                            }
                                        }
                                        else if (int.TryParse(connectArg, out int index))
                                        {
                                            Analyzer.ConnectToServer(index);
                                        }
                                        else
                                        {
                                            Output.Log(I18n.Get("prog_server_connect_invalid_arg"), 2, ThisProgramName);
                                        }
                                    }
                                    else
                                    {
                                        Output.Log(I18n.Get("prog_server_connect_usage"), 1, ThisProgramName);
                                    }
                                }
                                handled = true;
                                break;
                            case var cmd when cmd == ".server detach":
                                if (Analyzer.NeedsRunServer)
                                    Output.Log(I18n.Get("prog_server_detach_unsupported"), 2, ThisProgramName);
                                else
                                    Analyzer.Detach();
                                handled = true;
                                break;
                            case var cmd when cmd == ".server start":
                                if (Analyzer.NeedsRunServer)
                                    Analyzer.StartServer();
                                else
                                    Output.Log(I18n.Get("prog_server_start_rcon"), 2, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == ".server stop":
                                if (Analyzer.NeedsRunServer)
                                    Analyzer.StopServer();
                                else
                                    Output.Log(I18n.Get("prog_server_stop_rcon"), 2, ThisProgramName);
                                handled = true;
                                break;
                            case var cmd when cmd == ".server status":
                                {
                                    var table = new Table();
                                    table.Border(TableBorder.Rounded);
                                    table.Title = new TableTitle(I18n.Get("prog_server_status_title"));
                                    table.AddColumn("");
                                    table.AddColumn(I18n.Get("prog_col_key"));
                                    table.AddColumn(I18n.Get("prog_col_name"));
                                    table.AddColumn(I18n.Get("prog_col_mode"));
                                    table.AddColumn(I18n.Get("prog_col_run_status"));
                                    table.AddColumn(I18n.Get("prog_col_auto_restart"));
                                    table.AddColumn(I18n.Get("prog_col_auto_backup"));
                                    table.AddColumn(I18n.Get("prog_col_workdir"));

                                    string currentKey = Config.App.CurrentServer;
                                    foreach (var kvp in Config.App.ServerList)
                                    {
                                        bool isCurrent = kvp.Key == currentKey;
                                        string mark = isCurrent ? "[green]*[/]" : "";

                                        string mode = Markup.Escape(kvp.Value.AnalyzerMode);

                                        // 仅当前服务器显示实际运行状态
                                        string runStatus;
                                        if (isCurrent)
                                        {
                                            if (Analyzer.NeedsRunServer)
                                                runStatus = Analyzer.IsRunModeActive ? I18n.Get("prog_state_running") : I18n.Get("prog_state_not_running");
                                            else
                                                runStatus = Analyzer.IsAttached ? I18n.Get("prog_state_connected") : I18n.Get("prog_state_disconnected");
                                        }
                                        else
                                        {
                                            runStatus = "[grey]-[/]";
                                        }

                                        string autoRestart = kvp.Value.AutoRestart
                                            ? I18n.Get("prog_yes_with_count", kvp.Value.AutoRestartMaxRetries)
                                            : I18n.Get("prog_no_grey");

                                        bool inBackupList = Config.App.AutoBackupEnabled &&
                                                            Config.App.AutoBackupServers.Contains(kvp.Key);
                                        string autoBackup = Config.App.AutoBackupEnabled
                                            ? (inBackupList ? I18n.Get("prog_backup_added") : I18n.Get("prog_backup_not_added"))
                                            : I18n.Get("prog_backup_global_off");

                                        string workPath = string.IsNullOrWhiteSpace(kvp.Value.WorkPath)
                                            ? I18n.Get("prog_not_configured_yellow")
                                            : Markup.Escape(kvp.Value.WorkPath);

                                        table.AddRow(mark, Markup.Escape(kvp.Key), Markup.Escape(kvp.Value.ServerName),
                                                      mode, runStatus, autoRestart, autoBackup, workPath);
                                    }

                                    AnsiConsole.Write(table);
                                    handled = true;
                                    break;
                                }
                            #region .group 群组服务器管理
                            case var cmd when cmd == ".group":
                                {
                                    var groupRoot = new Tree(I18n.Get("prog_group_tree_title"));
                                    groupRoot.AddNode(I18n.Get("prog_group_add_desc"));
                                    groupRoot.AddNode(I18n.Get("prog_group_del_desc"));
                                    groupRoot.AddNode(I18n.Get("prog_group_list_desc"));
                                    groupRoot.AddNode(I18n.Get("prog_group_set_desc"));
                                    groupRoot.AddNode(I18n.Get("prog_group_unset_desc"));
                                    groupRoot.AddNode(I18n.Get("prog_group_build_desc"));
                                    AnsiConsole.Write(groupRoot);
                                    handled = true;
                                    break;
                                }
                            case var cmd when cmd != null && cmd.StartsWith(".group add "):
                                {
                                    string gName = cmd.Substring(".group add ".Length).Trim();
                                    var (gOk, gMsg, _) = ServerDataManager.CreateGroup(gName, new List<string>());
                                    Output.Log(gMsg, gOk ? 1 : 2, ThisProgramName);
                                    handled = true;
                                    break;
                                }
                            case var cmd when cmd != null && cmd.StartsWith(".group del "):
                                {
                                    string gId = cmd.Substring(".group del ".Length).Trim();
                                    var (gOk, gMsg) = ServerDataManager.DeleteGroup(gId);
                                    Output.Log(gMsg, gOk ? 1 : 2, ThisProgramName);
                                    handled = true;
                                    break;
                                }
                            case var cmd when cmd == ".group list":
                                {
                                    var groups = ServerDataManager.Data.Groups;
                                    if (groups.Count == 0)
                                    {
                                        Output.Log(I18n.Get("prog_group_empty"), 1, ThisProgramName);
                                        handled = true;
                                        break;
                                    }
                                    var gTable = new Table().Border(TableBorder.Rounded).Title(I18n.Get("prog_group_list_title"));
                                    gTable.AddColumn(I18n.Get("prog_col_group_id")).AddColumn(I18n.Get("prog_col_name")).AddColumn(I18n.Get("prog_col_member_count")).AddColumn(I18n.Get("prog_col_members")).AddColumn(I18n.Get("prog_col_created_at"));
                                    foreach (var g in groups)
                                    {
                                        string members = g.MemberIds.Count > 0 ? string.Join(", ", g.MemberIds) : I18n.Get("prog_none_grey");
                                        gTable.AddRow(g.Id, Markup.Escape(g.Name), g.MemberIds.Count.ToString(), members, g.CreatedAt);
                                    }
                                    AnsiConsole.Write(gTable);
                                    handled = true;
                                    break;
                                }
                            case var cmd when cmd != null && cmd.StartsWith(".group set "):
                                {
                                    var parts = cmd.Substring(".group set ".Length).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                    if (parts.Length < 2)
                                    {
                                        Output.Log(I18n.Get("prog_group_set_usage"), 2, ThisProgramName);
                                        handled = true;
                                        break;
                                    }
                                    string sId = parts[0];
                                    string gId = parts[1];
                                    if (!Config.App.ServerList.ContainsKey(sId))
                                    {
                                        Output.Log(I18n.Get("prog_server_not_exist", sId), 2, ThisProgramName);
                                        handled = true;
                                        break;
                                    }
                                    if (!ServerDataManager.Data.Groups.Any(g => g.Id == gId))
                                    {
                                        Output.Log(I18n.Get("prog_group_not_exist", gId), 2, ThisProgramName);
                                        handled = true;
                                        break;
                                    }
                                    var (sOk, sMsg) = ServerDataManager.UpdateGroup("add", gId, null, new List<string> { sId });
                                    Output.Log(sMsg, sOk ? 1 : 2, ThisProgramName);
                                    handled = true;
                                    break;
                                }
                            case var cmd when cmd != null && cmd.StartsWith(".group unset "):
                                {
                                    string sId = cmd.Substring(".group unset ".Length).Trim();
                                    if (!Config.App.ServerList.ContainsKey(sId))
                                    {
                                        Output.Log(I18n.Get("prog_server_not_exist", sId), 2, ThisProgramName);
                                        handled = true;
                                        break;
                                    }
                                    var inst = ServerDataManager.Data.Instances.FirstOrDefault(i => i.Id == sId);
                                    if (inst == null || string.IsNullOrEmpty(inst.GroupId))
                                    {
                                        Output.Log(I18n.Get("prog_server_no_group", sId), 2, ThisProgramName);
                                        handled = true;
                                        break;
                                    }
                                    string oldGroupId = inst.GroupId;
                                    var (uOk, uMsg) = ServerDataManager.UpdateGroup("remove", oldGroupId, null, new List<string> { sId });
                                    Output.Log(uMsg, uOk ? 1 : 2, ThisProgramName);
                                    handled = true;
                                    break;
                                }
                            case var cmd when cmd == ".group build":
                                Hub.GroupBuildWizard().GetAwaiter().GetResult();
                                handled = true;
                                break;
                            #endregion
                            case var cmd when cmd == "ct":
                                Output.Log(I18n.Get("prog_ct_desc"), 1, ThisProgramName);
                                Output.Log(I18n.Get("prog_ct_list_desc"), 1, ThisProgramName);
                                Output.Log(I18n.Get("prog_ct_unpack_desc"), 1, ThisProgramName);
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
                                Output.Log(I18n.Get("prog_unknown_cmd"), 1, ThisProgramName);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Output.ReportError(ex);
                        Output.Log(I18n.Get("prog_cmd_exec_error"), 2, ThisProgramName);
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
                    Output.Log(I18n.Get("prog_shutting_down"), 1, ThisProgramName);
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

            var libTable = new Table().Border(TableBorder.Rounded).Title("[yellow]部分依赖库[/]");
            libTable.AddColumn("库");
            libTable.AddColumn("版本");

            var libs = new (string Name, string Version)[]
            {
                ("Spectre.Console", "0.57.2"),
                ("Newtonsoft.Json", "13.0.4"),
                ("Serilog", "4.4.0"),
                ("Serilog.Sinks.File", "7.0.0"),
                ("YamlDotNet", "18.1.0"),
                ("Grpc.AspNetCore", "2.80.0"),
                ("Grpc.Core", "2.46.6"),
                ("Google.Protobuf", "3.35.1"),
                ("Grpc.Tools", "2.82.0"),
                ("System.Management", "10.0.10"),
                ("TouchSocket", "2.3.6"),
                ("Polly", "8.7.0"),
                ("Mono.Cecil", "0.11.6"),
                ("MySqlConnector", "2.6.1"),
                ("Microsoft.Extensions.AI", "10.8.3"),
                ("Hangfire.Core", "1.8.24"),
                ("CS-Script", "4.14.11"),
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
                .AddRow("[white].ai[/]", "AI自动化管理 (输入 .ai 查看子命令)")
                .AddRow("[white].ai start[/]", "启动AI自动化(自动启动MC服务端)")
                .AddRow("[white].ai stop[/]", "停止AI自动化")
                .AddRow("[white].ai list[/]", "列出所有AI任务及运行状态")
                .AddRow("[white].ai reload[/]", "重载AI配置并重启任务")
                .AddRow("[white].ai run <任务名>[/]", "手动触发指定任务")
                .AddRow("[white].ai clear[/]", "清除所有任务上下文缓存")
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
                .AddRow("[white].fx filter mod[/]", "列出/禁用/启用模组")
                .AddRow("[white].fx filter mod <n>[/]", "切换第n个模组启用/禁用(0重启)")
                .AddRow("[white].cfg[/]", "配置管理 (输入 .cfg 查看子命令)")
                .AddRow("[white].cfg reload[/]", "热重载所有配置文件")
                .AddRow("[white].cfg clear[/]", "删除Content文件夹并冷重载")
                .AddRow("[white].lang[/]", I18n.Get("lang_usage"))
                .AddRow("[white].status[/]", "查看RtCli运行状态")
                .AddRow("[white].server[/]", "MC控制台相关命令 (输入 .server 查看子命令)")
                .AddRow("[white].server list[/]", "列出所有已配置的MC服务端")
                .AddRow("[white].server add[/]", "添加新的MC服务端配置")
                .AddRow("[white].server del[/]", "删除MC服务端配置")
                .AddRow("[white].server change <标识>[/]", "切换当前MC服务端")
                .AddRow("[white].server get[/]", "[[[DarkOrange]Rcon[/]]] 扫描并列出运行中的MC服务端")
                .AddRow("[white].server connect <序号|pid:进程ID>[/]", "[[[DarkOrange]Rcon[/]]] 连接到指定的MC服务端")
                .AddRow("[white].server detach[/]", "[[[DarkOrange]Rcon[/]]] 断开与MC服务端的连接")
                .AddRow("[white].server start[/]", "[[[green]Run/RR/RM[/]]] 启动MC服务端作为子进程")
                .AddRow("[white].server stop[/]", "[[[green]Run/RR/RM[/]]] 停止MC服务端")
                .AddRow("[white].server status[/]", "查看MC服务端连接/运行状态")
                .AddRow("[white].group[/]", "群组服务器管理 (输入 .group 查看子命令)")
                .AddRow("[white].group add <名称>[/]", "创建群组")
                .AddRow("[white].group del <群组ID>[/]", "删除群组")
                .AddRow("[white].group list[/]", "列出所有群组信息")
                .AddRow("[white].group set <服务器标识> <群组ID>[/]", "设置服务器到某群组")
                .AddRow("[white].group unset <服务器标识>[/]", "解除服务器群组设置")
                .AddRow("[white].group build[/]", "自动构建群组向导")
                .AddRow("[green]/<命令>[/]", "发送命令到MC服务端");
            AnsiConsole.Write(table);
        }

        private static void ShowExtensionCommands()
        {
            var extensionCommands = CommandRegistry.Commands;
            var extensionDescriptions = CommandRegistry.Descriptions;
            if (extensionCommands.Count > 0)
            {
                Output.Log(I18n.Get("prog_ext_cmd_list_header"), 1, ThisProgramName);
                var extTable = new Table()
                    .AddColumn(I18n.Get("prog_col_command"))
                    .AddColumn(I18n.Get("prog_col_desc"));
                foreach (var kvp in extensionCommands)
                {
                    string description = extensionDescriptions.TryGetValue(kvp.Key, out var desc) ? desc : "";
                    extTable.AddRow($"[cyan]{kvp.Key}[/]", string.IsNullOrEmpty(description) ? "[grey]-[/]" : description);
                }
                AnsiConsole.Write(extTable);
            }
            else
            {
                Output.Log(I18n.Get("prog_no_ext_cmds"), 1, ThisProgramName);
            }
        }
    }
}
