using RtCli.Modules;
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
        // 程序版本号规则：主版本号.日期yy/mm.次版本号.编译号
        public static string RtCliVersion { get; } = "1.2604.28.3";
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
            "rt"
        };

        private static string[] _allCommands;

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
                Output.CrashAssistant(ex);
                return Task.CompletedTask;
            }
            finally
            {
                // 上报错误
                // RtExtensionManager.RtExtensionManager.UnloadAll();
            }
        }

        private static Task MainInternal(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Title = "RtCli";
            Console.Clear();
            Output.TextBlock("启动主线程", 1, "Task#0");

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
                    Output.Log("[yellow]当前操作系统环境为32位可能多数功能不支持![/]", 2, "Checker");
                }
            }

            RegisterBaseCommands();
            UpdateAllCommands();

            #endregion

            stopwatch.Stop();
            Output.Log($"{Modules.Unit.I18n.Get("main_loadfinsih")}（{stopwatch.ElapsedMilliseconds}ms）", 1, ThisProgramName);

            if (Modules.Function.Commands.Cli(args))
            {
                return Task.CompletedTask;
            }

            Thread.CurrentThread.Name = "MainThread";
            Output.Log(Modules.Unit.I18n.Get("main_seltip_1"), 1, ThisProgramName);
            var selloaded = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                .Title(Modules.Unit.I18n.Get("mian_seltip_2"))
                .AddChoices("默认模式", "测试模式", "重新加载", "关闭程序"));


            switch (selloaded)
            {
                case "默认模式":
                    Output.Log("正在运行扩展内容...", 1, ThisProgramName);
                    RtExtensionManager.RtExtensionManager.DisplayLoadedExtensions();
                    RtExtensionManager.RtExtensionManager.Run();
                    rtmain.Start();
                    rtmain.Join();
                    break;

                case "测试模式":
                    // no
                    break;

                case "关闭程序":
                    RtExtensionManager.RtExtensionManager.UnloadAll();
                    break;

                case "重新加载":
                    Output.Log("重新加载中...", 1, ThisProgramName);
                    Reload.Restart();
                    break;

                default:
                    return Task.CompletedTask;

            }

            RtExtensionManager.RtExtensionManager.UnloadAll();
            Output.TextBlock(Modules.Unit.I18n.Get("main_end"), 1, "Task#0");
            return Task.CompletedTask;
        }

        private static void RegisterBaseCommands()
        {
            CommandRegistry.RegisterCommand("rt reload", args => Reload.Restart(), "重新加载程序");
            CommandRegistry.RegisterCommand("rt status", args => 
                Output.Log($"管理端口状态：{(Connector.IsRunning ? "运行中" : "未运行")}，已连接客户端数量：{Connector.ConnectedClientCount}", 1, ThisProgramName), 
                "管理端口状态");
            CommandRegistry.RegisterCommand("rt clients", args => 
            {
                Output.Log($"已连接面板列表：", 1, ThisProgramName);
                foreach (var client in Connector.ConnectedClientCount > 0 ? Connector.ConnectedClientCount.ToString() : "无")
                {
                    Output.Log($"- {client}", 1, ThisProgramName);
                }
            }, "已连接面板列表");
            CommandRegistry.RegisterCommand("rt extensions", args => 
                RtExtensionManager.RtExtensionManager.DisplayLoadedExtensions(), 
                "已加载的扩展列表");
            CommandRegistry.RegisterCommand("rt end", args => { }, "关闭程序");
            CommandRegistry.RegisterCommand("rt stop", args => Connector.StopServerAsync().Wait(), "关闭管理端口");
            CommandRegistry.RegisterCommand("rt start", args => Connector.StartServerAsync().Wait(), "启动管理端口");
            CommandRegistry.RegisterCommand("rt help", args => 
                Output.Log("RT命令列表：rt -" +
                    "\n end - 关闭程序 " +
                    "\n stop - 关闭管理端口 " +
                    "\n start - 启动管理端口" +
                    "\n reload - 重新加载 " +
                    "\n status - 管理端口状态 " +
                    "\n clients - 已面板列表 " +
                    "\n extensions - 已加载的扩展列表", 1, ThisProgramName), 
                "显示帮助");
            CommandRegistry.RegisterCommand("rt", args => 
                Output.Log($"RtCli版本：{RtCliVersion} 输入rt help查看命令列表，按 TAB 键自动补全命令", 1, ThisProgramName), 
                "显示版本");
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

        private static string? ReadLineWithTabCompletion()
        {
            StringBuilder input = new StringBuilder();
            int cursorPosition = 0;
            int tabIndex = -1;
            List<string> currentMatches = new List<string>();

            while (true)
            {
                ConsoleKeyInfo keyInfo = Console.ReadKey(true);

                switch (keyInfo.Key)
                {
                    case ConsoleKey.Enter:
                        Console.WriteLine();
                        return input.ToString();

                    case ConsoleKey.Backspace:
                        if (cursorPosition > 0)
                        {
                            input.Remove(cursorPosition - 1, 1);
                            cursorPosition--;
                            tabIndex = -1;
                            RedrawLine(input.ToString(), cursorPosition);
                        }
                        break;

                    case ConsoleKey.Delete:
                        if (cursorPosition < input.Length)
                        {
                            input.Remove(cursorPosition, 1);
                            tabIndex = -1;
                            RedrawLine(input.ToString(), cursorPosition);
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
                                ShowSuggestions(currentMatches, tabIndex);
                            }
                        }
                        break;

                    default:
                        if (!char.IsControl(keyInfo.KeyChar))
                        {
                            input.Insert(cursorPosition, keyInfo.KeyChar);
                            cursorPosition++;
                            tabIndex = -1;
                            RedrawLine(input.ToString(), cursorPosition);
                        }
                        break;
                }
            }
        }

        private static void RedrawLine(string text, int cursorPosition)
        {
            int currentLine = Console.CursorTop;
            Console.SetCursorPosition(0, currentLine);
            Console.Write(text + " ");
            Console.SetCursorPosition(cursorPosition, currentLine);
        }

        private static void ShowSuggestions(List<string> matches, int selectedIndex)
        {
            int originalTop = Console.CursorTop;
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            
            for (int i = 0; i < matches.Count; i++)
            {
                if (i == selectedIndex)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write($"[{matches[i]}] ");
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                }
                else
                {
                    Console.Write($"{matches[i]} ");
                }
            }
            
            Console.ResetColor();
            Console.SetCursorPosition(0, originalTop);
        }

        public static async void Continued()
        {
            Thread.CurrentThread.Name = "Main";
            Output.Log("[yellow]注意：程序仅能在本机或局域网运行，如果在其他网络环境下运行安全目前无法保障！[/]", 2, ThisProgramName);
            Output.Log("[yellow]注意：目前已将通信验证删除！[/]", 2, ThisProgramName);
            Output.Log("[yellow]注意：该分支为测试分支，可能包含未测试的功能！[/]", 2, ThisProgramName);
            await Connector.StartServerAsync();
            Thread.CurrentThread.Name = "Main";

            string? cmd_input = ReadLineWithTabCompletion();
            while (true)
            {
                bool handled = false;

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
                        Output.Log("RT命令列表：rt -" +
                            "\n end - 关闭程序 " +
                            "\n stop - 关闭管理端口 " +
                            "\n start - 启动管理端口" +
                            "\n reload - 重新加载 " +
                            "\n status - 管理端口状态 " +
                            "\n clients - 已面板列表 " +
                            "\n extensions - 已加载的扩展列表", 1, ThisProgramName);
                        handled = true;
                        break;
                    case var cmd when cmd == BaseCommands[8]:
                        Output.Log($"RtCli版本：{RtCliVersion} 输入rt help查看命令列表，按 TAB 键自动补全命令", 1, ThisProgramName);
                        handled = true;
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

                Thread.CurrentThread.Name = "Main";
                cmd_input = ReadLineWithTabCompletion();
            }
            endpage:
                Output.Log("正在关闭...", 1, ThisProgramName);
                return;
        }
    }
}
