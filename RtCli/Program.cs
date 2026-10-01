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
    internal partial class Program
    {
        // 功能测试稳定优化完善

        // 支持Linux系统

        // 优化文字和整理及I18n完整支持
        // 服务端信息获取模式（Run、Rcon、RR、RM）"四架构 理功能"
        // 程序和MC添加测试更多错误识别并分类

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
            ".fx add",
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
            ".server rcon connect",
            ".server rcon disconnect",
            ".server rcon status",
            ".server start",
            ".server stop",
            ".server restart",
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

        /// <summary>
        /// 权威命令表（只读），供同程序集其他模块（如 Backend 的命令列表补全）读取。
        /// </summary>
        internal static IReadOnlyList<string> BaseCommandNames => BaseCommands;

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
            catch (Exception ex) { Output.Debug(ex, "释放单实例互斥体失败", ThisProgramName); }
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
            Output.Log($"{Modules.Unit.I18n.Get("main_loadfinish")}（{stopwatch.ElapsedMilliseconds}ms）", 1, ThisProgramName);
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
                    .Title(I18n.Get("main_seltip_2"))
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
                // 轮询读键(不阻塞式 ReadKey): 无按键且后台线程(EULA确认等)请求控制台时让出,
                // 等其交互完成后继续; 有按键待读时先处理按键(避免被后台交互抢走), 下轮再让出
                ConsoleKeyInfo keyInfo;
                while (!Console.KeyAvailable)
                {
                    if (ConsoleGate.YieldRequested)
                        ConsoleGate.YieldAndWait();
                    Thread.Sleep(15);
                }
                keyInfo = Console.ReadKey(true);

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

                        // cmd_input 为 null 时原 switch 不会命中任何 case, 这里保持同样结果
                        var dispatch = cmd_input == null ? CliDispatch.Unhandled : DispatchCliCommand(cmd_input);
                        if (dispatch == CliDispatch.Exit)
                            goto endpage;
                        handled = dispatch == CliDispatch.Handled;

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

        /// <summary>CLI 命令分发结果</summary>
        private enum CliDispatch { Unhandled, Handled, Exit }

        /// <summary>
        /// CLI 命令分发: 交给注册表(精确名优先, 其次最长前缀)。
        /// 命令与处理器登记在 Program.CliCommands.cs, 与面板命令表对称。
        /// </summary>
        private static CliDispatch DispatchCliCommand(string cmd)
        {
            EnsureCliCommandsRegistered();

            var outcome = CommandRegistry.TryDispatchCli(cmd);

            // "/" 开头的 MC 命令转发没有"命令名+空格"结构, 无法用前缀表登记, 单独接线
            if (outcome == CommandRegistry.CliOutcome.Unhandled && cmd.StartsWith("/", StringComparison.Ordinal))
                outcome = Cli_mcCommand(cmd);

            return outcome switch
            {
                CommandRegistry.CliOutcome.Handled => CliDispatch.Handled,
                CommandRegistry.CliOutcome.Exit => CliDispatch.Exit,
                _ => CliDispatch.Unhandled
            };
        }



        /// <summary>
        /// rt about 展示的依赖库: 只登记"包名 → 程序集名", 版本在运行时从程序集自动读取,
        /// 因此升级包后表格自动跟随, 不会再出现"表里写的是旧版本"。
        /// 只收录运行时真实加载的包; Grpc.Tools(仅构建期)与 System.Private.Uri(随框架统一/裁剪)不在此列。
        /// </summary>
        internal static readonly (string Name, string AssemblyName)[] DependencyLibraries =
        {
            ("CS-Script", "CSScriptLib"),
            ("CSnakes.Runtime", "CSnakes.Runtime"),
            ("Google.Protobuf", "Google.Protobuf"),
            ("Grpc.AspNetCore", "Grpc.AspNetCore.Server"),
            ("Grpc.Core", "Grpc.Core"),
            ("Hangfire.Core", "Hangfire.Core"),
            ("Hangfire.InMemory", "Hangfire.InMemory"),
            ("Microsoft.Extensions.AI", "Microsoft.Extensions.AI"),
            ("MySqlConnector", "MySqlConnector"),
            ("Newtonsoft.Json", "Newtonsoft.Json"),
            ("Polly", "Polly"),
            ("Serilog", "Serilog"),
            ("Serilog.Sinks.File", "Serilog.Sinks.File"),
            ("Spectre.Console", "Spectre.Console"),
            ("System.Management", "System.Management"),
            ("YamlDotNet", "YamlDotNet"),
        };

        /// <summary>
        /// 读取依赖库的实际运行时版本。
        /// 优先 AssemblyInformationalVersion(绝大多数 NuGet 包在此写真实包版本,
        /// 如 13.0.4+commit / 4.4.0-main-hash+hash), 其次 AssemblyFileVersion, 最后 AssemblyVersion。
        /// </summary>
        internal static string ResolveDependencyVersion(string assemblyName)
        {
            try
            {
                var asm = Assembly.Load(assemblyName);
                string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                string? file = asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
                string? asmVer = asm.GetName().Version?.ToString();

                return NormalizeVersion(info) ?? NormalizeVersion(file) ?? NormalizeVersion(asmVer) ?? "未知";
            }
            catch
            {
                // 程序集未随包提供或加载失败: 表格显示"未加载", 而不是印一个可能过时的版本号
                return "-";
            }
        }

        /// <summary>
        /// 归一化版本串: 去掉 SemVer 的 +构建元数据 与 -预发布 后缀,
        /// 以及 4 段式版本末尾的 .0 (如 CSScriptLib 的 4.14.11.0+hash → 4.14.11)。
        /// </summary>
        private static string? NormalizeVersion(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string v = raw.Trim();
            int plus = v.IndexOf('+');
            if (plus >= 0) v = v[..plus];
            int dash = v.IndexOf('-');
            if (dash >= 0) v = v[..dash];
            var parts = v.Split('.');
            if (parts.Length == 4 && parts[3] == "0") v = string.Join('.', parts, 0, 3);
            return v.Length > 0 && char.IsDigit(v[0]) ? v : null;
        }

        private static void ShowAbout()
        {
            string runtimeVersion = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
            string osDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            string architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLower();

            AnsiConsole.Write(new Rule("[dodgerblue1]RutCitrus RtCli[/]").RuleStyle("dodgerblue1").Centered());

            // 使用带边框的表格(而非 NoBorder): 之前只显示成文字, 与其他命令的表格观感不一致
            var infoTable = new Table().Border(TableBorder.Rounded).Title("[yellow]程序信息[/]");
            infoTable.AddColumn("项目").AddColumn("值");
            infoTable.AddRow("[yellow]版本[/]", $"[white]{Markup.Escape(RtCliVersion)}[/]");
            infoTable.AddRow("[yellow]作者[/]", "[white]psoloi[/]");
            infoTable.AddRow("[yellow]项目[/]", "[link]https://github.com/psoloi/RutCitrus[/]");
            infoTable.AddRow("[yellow]环境[/]", $"[white]{Markup.Escape(runtimeVersion)}[/]");
            infoTable.AddRow("[yellow]系统[/]", $"[white]{Markup.Escape(osDescription)}[/]");
            infoTable.AddRow("[yellow]架构[/]", $"[white]{architecture}[/]");
            infoTable.AddRow("[yellow]Java[/]", $"[white]{Markup.Escape(Checker.CheckJava() ?? string.Empty)}[/]");
            infoTable.AddRow("[yellow].NET[/]", $"[white]{Markup.Escape(Checker.CheckDotNet() ?? string.Empty)}[/]");
            infoTable.AddRow("[yellow]Python[/]", $"[white]{Markup.Escape(Checker.CheckPython() ?? string.Empty)}[/]");
            infoTable.AddRow("[yellow]系统位数[/]", $"[white]{(Environment.Is64BitOperatingSystem ? "64位" : "32位")}[/]");
            AnsiConsole.Write(infoTable);

            AnsiConsole.WriteLine();

            var libTable = new Table().Border(TableBorder.Rounded).Title("[yellow]部分依赖库[/]");
            libTable.AddColumn("库");
            libTable.AddColumn("版本");

            foreach (var lib in DependencyLibraries)
            {
                string version = ResolveDependencyVersion(lib.AssemblyName);
                string cell = version == "-" ? "[red]未加载[/]" : $"[grey]{Markup.Escape(version)}[/]";
                libTable.AddRow($"[cyan]{lib.Name}[/]", cell);
            }

            AnsiConsole.Write(libTable);
            AnsiConsole.Write(new Rule().RuleStyle("dodgerblue1"));
        }

        private static void ShowHelp()
        {
            // 命令条目统一维护于 CommandHelp(与面板帮助同源)
            Output.Log("RtCli程序命令列表：", 1, ThisProgramName);
            var table = new Table()
                .AddColumn("命令")
                .AddColumn("描述");
            foreach (var e in CommandHelp.Entries)
                table.AddRow(e.Command, e.Description);
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
