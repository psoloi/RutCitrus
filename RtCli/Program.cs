using RtCli.Modules;
using RtCli.Modules.Unit;
using Spectre.Console;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Terminal.Gui;

namespace RtCli
{
    internal class Program
    {
        // 程序版本号规则：主版本号.日期yy/mm.次版本号.编译号
        public static string RtCliVersion { get; } = "1.2602.27.1";
        public static string ThisProgramName { get; } = "RtCli";

        /// <summary>
        /// 应用程序主入口点
        /// </summary>
        /// <param name="args"></param>
        [STAThread]
        public static Task Main(string[] args)
        {

            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Title = "RtCli";
            Console.Clear();
            Output.TextBlock("启动主线程", 1, "Task#0");

            Process currentProcess = Process.GetCurrentProcess();
            string currentProcessName = currentProcess.ProcessName;

            Process[] processes = Process.GetProcessesByName(currentProcessName);
            if (processes.Length > 1)
            {
                Output.TextBlock("重复的线程!", 2, "Task#End");
                return Task.CompletedTask;
            }

            Thread.CurrentThread.Name = "Main";
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            Output.Log("启动中...", 1, currentProcessName);

            #region 启动任务

            RtExtensionManager.RtExtensionManager.LoadAll();

            // i18n 初始化
            Modules.Unit.I18n.Init();

            // 环境检测
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


            #endregion

            stopwatch.Stop();
            Output.Log($"{Modules.Unit.I18n.Get("main_loadfinsih")}（{stopwatch.ElapsedMilliseconds}ms）", 1, ThisProgramName);

            // 处理命令行参数
            if (Modules.Mode.Commands.Cli(args))
            {
                return Task.CompletedTask;
            }

            // 设置位点的暂停
            Output.Log(Modules.Unit.I18n.Get("main_seltip_1"), 1, ThisProgramName);
            var selloaded = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                .Title(Modules.Unit.I18n.Get("mian_seltip_2"))
                .AddChoices("命令行", "服务管理", "重新加载", "关闭程序", "运行扩展以继续"));


            switch (selloaded)
            {
                case "运行扩展以继续":
                    Output.Log("正在运行扩展内容...", 1, ThisProgramName);
                    RtExtensionManager.RtExtensionManager.DisplayLoadedExtensions();
                    RtExtensionManager.RtExtensionManager.Run();
                    Continued();
                    break;

                case "关闭程序":
                    RtExtensionManager.RtExtensionManager.UnloadAll();
                    break;

                case "重新加载":
                    Output.Log("重新加载中...", 1, ThisProgramName);
                    Reload.Restart();
                    break;

                case "TUI":
                    Modules.Mode.TUI.Run();
                    break;

                case "命令行":
                    
                    break;

                default:
                    RtExtensionManager.RtExtensionManager.UnloadAll();
                    Output.TextBlock(Modules.Unit.I18n.Get("main_end"), 2, "Task#0");
                    return Task.CompletedTask;

            }

            RtExtensionManager.RtExtensionManager.UnloadAll();
            Output.TextBlock(Modules.Unit.I18n.Get("main_end"), 1, "Task#0");
            // Environment.Exit(0);
            return Task.CompletedTask;
        }

        // Windows最大化控制台窗口
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetConsoleWindow();

        const int SW_MAXIMIZE = 3;

        public static void Maximize_ConsoleWindows()
        {
            // 获取当前控制台窗口句柄
            IntPtr hWnd = GetConsoleWindow();
            // 最大化窗口
            ShowWindow(hWnd, SW_MAXIMIZE);
            Console.ReadKey();
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = (Exception)e.ExceptionObject;

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("===== [CrashAssistant]发生未处理异常 =====");
            Console.WriteLine($" - 异常类型: {ex.GetType().Name}");
            Console.WriteLine($" - 异常消息: {ex.Message}");
            Console.WriteLine($" - 堆栈跟踪: {ex.StackTrace}");
            Console.ResetColor();
            Console.WriteLine("按任意键退出...");
            Console.ReadKey();

            Environment.Exit(1);
        }



        public static void Continued()
        {
            Output.Log("[yellow]注意：RtService服务仅能在本机或局域网运行，如果在其他网络环境下运行安全目前无法保障，本机要使用该服务你需要在该程序继续配置Config选项后将会帮你安装该服务[/]", 2, ThisProgramName);
            var table = new Table()
              .AddColumn("选项")
              .AddColumn("按键")
              .AddColumn("描述")
              .AddRow("[green]安装RtService服务[/]", "I", "该服务为Minecraft服务器管理分支后端，服务管理用前端通过局域网等连接")
              .AddRow("[red]删除RtService服务[/]", "U", "将服务从当前设备移除并删除服务的配置文件，但会保留数据按需要手动删除")
              .AddRow("[yellow]模块[/]", "E", "no");
            AnsiConsole.Write(table);
            if (Console.ReadKey().Key != ConsoleKey.I)
            {
                Output.Log("正在安装RtService服务...", 1, ThisProgramName);
            }
        }
    }
}
