using RtCli.Modules;
using RtCli.Modules.Unit;
using RtExtensionManager;
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

            #endregion

            stopwatch.Stop();
            Output.Log($"加载完毕！用时（{stopwatch.ElapsedMilliseconds}ms）", 1, ThisProgramName);

            // 处理命令行参数
            if (Modules.Mode.Commands.Cli(args))
            {
                return Task.CompletedTask;
            }

            // 设置位点的暂停
            Output.Log("请选择接下来需要的加载项...", 1, ThisProgramName);
            var selloaded = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                .Title("使用↑↓来选择按回车确定")
                .AddChoices("命令行", "TUI", "重新加载", "关闭程序", "运行扩展以继续"));


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
                    Output.TextBlock("主线程结束", 2, "Task#0");
                    return Task.CompletedTask;

            }

            RtExtensionManager.RtExtensionManager.UnloadAll();
            Output.TextBlock("主线程结束", 1, "Task#0");
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

        }
    }
}
