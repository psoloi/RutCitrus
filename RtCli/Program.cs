using RtCli.Modules;
using RtCli.Modules.Unit;
using Spectre.Console;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

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

            Process currentProcess = Process.GetCurrentProcess();
            string currentProcessName = currentProcess.ProcessName;

            Process[] processes = Process.GetProcessesByName(currentProcessName);
            if (processes.Length > 1)
            {
                Output.TextBlock("重复的线程!", 2, "Task#End");
                return Task.CompletedTask;
            }

            Thread.CurrentThread.Name = "Main";

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            Output.Log("启动中...", 1, currentProcessName);

            #region 启动任务

            RtExtensionManager.RtExtensionManager.LoadAll();

            Modules.Unit.I18n.Init();

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

            if (Modules.Function.Commands.Cli(args))
            {
                return Task.CompletedTask;
            }

            Output.Log(Modules.Unit.I18n.Get("main_seltip_1"), 1, ThisProgramName);
            var selloaded = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                .Title(Modules.Unit.I18n.Get("mian_seltip_2"))
                .AddChoices("运行", "重新加载", "关闭程序", "设置"));


            switch (selloaded)
            {
                case "运行":
                    Output.Log("正在运行扩展内容...", 1, ThisProgramName);
                    RtExtensionManager.RtExtensionManager.DisplayLoadedExtensions();
                    Continued();
                    RtExtensionManager.RtExtensionManager.Run();
                    break;

                case "关闭程序":
                    RtExtensionManager.RtExtensionManager.UnloadAll();
                    break;

                case "重新加载":
                    Output.Log("重新加载中...", 1, ThisProgramName);
                    Reload.Restart();
                    break;

                case "设置":
                    // no
                    break;

                default:
                    RtExtensionManager.RtExtensionManager.UnloadAll();
                    Output.TextBlock(Modules.Unit.I18n.Get("main_end"), 2, "Task#0");
                    return Task.CompletedTask;

            }

            RtExtensionManager.RtExtensionManager.UnloadAll();
            Output.TextBlock(Modules.Unit.I18n.Get("main_end"), 1, "Task#0");
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
            IntPtr hWnd = GetConsoleWindow();
            ShowWindow(hWnd, SW_MAXIMIZE);
            Console.ReadKey();
        }


        public static void Continued()
        {
            Output.Log("[yellow]注意：程序仅能在本机或局域网运行，如果在其他网络环境下运行安全目前无法保障！[/]", 2, ThisProgramName);

        }
    }
}
