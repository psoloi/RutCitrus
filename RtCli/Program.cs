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

        public static string RtCliVersion { get; } = "1.2510.26.11";
        public static string ThisProgramName { get; } = "RtCli";

        /// <summary>
        /// 应用程序主入口点
        /// </summary>
        /// <param name="args"></param>
        [STAThread]
        static async Task Main(string[] args)
        {

            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Title = "RtCli";
            Console.Clear();
            Output.TextBlock("启动主线程...", 1, "Task#0");

            Process currentProcess = Process.GetCurrentProcess();
            string currentProcessName = currentProcess.ProcessName;

            // 查找同名的进程
            Process[] processes = Process.GetProcessesByName(currentProcessName);
            if (processes.Length > 1)
            {
                Output.TextBlock("重复的线程!", 2, "Task#End");
                return;
            }


            Thread.CurrentThread.Name = "Main";
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            Output.Log("启动中...", 1, currentProcessName);

            #region 启动线程

            RtExtensionManager.RtExtensionManager.LoadAll();

            #endregion

            stopwatch.Stop();
            Output.Log($"加载完毕！用时（{stopwatch.ElapsedMilliseconds}ms）", 1, ThisProgramName);

            // 处理命令行参数
            if (Modules.Mode.Commands.Cli(args))
            {
                return; // 如果参数已处理，直接返回
            }

            Output.Log("按下  （C）打开命令  （G）打开TUI  （R）重新加载  （Esc）退出", 1, ThisProgramName);
            ConsoleKeyInfo keyInfo = Console.ReadKey(true);
            if (keyInfo.Key == ConsoleKey.Enter)
            {
                Console.Clear();
                Output.Log("", 1, ThisProgramName);
                var installer = new PhotoshopStyleInstaller();

                try
                {
                    Console.WriteLine("启动安装界面...");

                    // 方法1: 使用异步StartAsync方法（推荐）
                    await installer.StartAsync();

                    // 方法2: 使用同步Start方法（会阻塞当前线程）
                    // installer.Start();

                    // 步骤2: 等待UI初始化（给用户一些时间看到界面）
                    await Task.Delay(1000);

                    // 步骤3: 检查UI是否正在运行
                    if (installer.IsRunning)
                    {
                        Console.WriteLine("开始安装过程...");

                        // 步骤4: 更新各个安装任务的进度
                        // 注意：任务名称必须与InstallerWindow.InitializeTasks()中定义的任务名称完全匹配

                        // 更新第一个任务进度
                        await Task.Delay(500); // 模拟安装过程
                        installer.UpdateProgress("Extracting installation files", 50f);
                        await Task.Delay(500);
                        installer.UpdateProgress("Extracting installation files", 100f); // 完成第一个任务
                        await Task.Delay(500);

                        // 更新第二个任务进度
                        await Task.Delay(500);
                        installer.UpdateProgress("Installing core components", 30f);
                        await Task.Delay(500);
                        installer.UpdateProgress("Installing core components", 70f);
                        await Task.Delay(500);
                        installer.UpdateProgress("Installing core components", 100f); // 完成第二个任务
                        await Task.Delay(500);

                        // 更新第三个任务进度
                        await Task.Delay(500);
                        installer.UpdateProgress("Configuring preferences", 50f);
                        await Task.Delay(500);
                        installer.UpdateProgress("Configuring preferences", 100f);
                        await Task.Delay(500);

                        // 继续更新其他任务...
                        await Task.Delay(500);
                        installer.UpdateProgress("Installing plugins", 100f);
                        await Task.Delay(500);
                        installer.UpdateProgress("Registering application", 100f);
                        await Task.Delay(500);
                        installer.UpdateProgress("Creating shortcuts", 100f);
                        await Task.Delay(500);
                        installer.UpdateProgress("Finalizing installation", 100f);

                        // 步骤5: 完成所有安装任务
                        installer.CompleteInstallation();

                        Console.WriteLine("安装完成！");
                        await Task.Delay(2000); // 给用户时间看到完成界面

                        // 步骤6: 关闭安装界面
                        await installer.StopAsync();
                    }
                    else
                    {
                        Console.WriteLine("安装界面未能成功启动");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"发生错误: {ex.Message}");
                    // 确保在出错时关闭窗口
                    if (installer.IsRunning)
                    {
                        await installer.StopAsync();
                    }
                }
            }
            if (keyInfo.Key == ConsoleKey.Escape)
            {
                RtExtensionManager.RtExtensionManager.UnloadAll();
                Output.Log("关闭！", 1, ThisProgramName);
                Environment.Exit(0);
            }
            if (keyInfo.Key == ConsoleKey.R)
            {
                Output.Log("重新加载中...", 1, ThisProgramName);
                Reload.Restart();
            }
            if (keyInfo.Key == ConsoleKey.G)
            {
                Modules.Mode.TUI.Run();
            }
            else
            {
                return;
            }
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
            Console.WriteLine("===== [CrashReport]发生未处理异常 =====");
            Console.WriteLine($" - 异常类型: {ex.GetType().Name}");
            Console.WriteLine($" - 异常消息: {ex.Message}");
            Console.WriteLine($" - 堆栈跟踪: {ex.StackTrace}");
            Console.ResetColor();
            Console.WriteLine("按任意键退出...");
            Console.ReadKey();

            Environment.Exit(1);
        }
    }
}
