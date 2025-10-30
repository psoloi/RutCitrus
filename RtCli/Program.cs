using RtCli.Modules;
using RtCli.Modules.Unit;
using RtExtensionManager;
using Spectre.Console;
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

        /// <summary>
        /// 应用程序主入口点
        /// </summary>
        /// <param name="args"></param>
        static async Task Main(string[] args)
        {

            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Title = "RtCli";
            Console.Clear();
            Output.TextBlock("启动主线程...", 1, "Task#0");

            string ThisProgramName = "RtCli";
            Thread.CurrentThread.Name = "Main";
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            Output.Log("启动中...", 1, ThisProgramName);

            #region 启动线程

            RtExtensionManager.RtExtensionManager.LoadAll();


            #endregion

            stopwatch.Stop();
            Output.Log($"加载完毕！用时（{stopwatch.ElapsedMilliseconds}ms）", 1, ThisProgramName);

            Output.Log("按下（C）打开命令（G）打开TUI（R）重新加载（Esc）退出", 1, ThisProgramName);
            ConsoleKeyInfo keyInfo = Console.ReadKey(true);
            if (keyInfo.Key == ConsoleKey.Enter)
            {
                Console.Clear();
                Output.Log("", 1, ThisProgramName);
                var installer = new PhotoshopStyleInstaller();

                try
                {
                    // 步骤1: 启动安装界面
                    // 现在有两种选择：
                    Console.WriteLine("启动安装界面...");

                    // 方法1: 使用异步StartAsync方法（推荐）
                    // 这会在一个新的线程上创建并运行窗口，不会阻塞当前线程
                    // await installer.StartAsync();

                    // 方法2: 使用同步Start方法
                    // 这会阻塞当前线程直到窗口关闭
                    installer.Start();

                    // 步骤2: 等待UI初始化（给用户一些时间看到界面）
                    await Task.Delay(1000);

                    // 步骤3: 检查UI是否正在运行
                    if (installer.IsRunning)
                    {
                        Console.WriteLine("开始安装过程...");

                        // 步骤4: 更新各个安装任务的进度
                        // 注意：任务名称必须与InstallerWindow.InitializeTasks()中定义的任务名称完全匹配

                        // 更新第一个任务进度
                        installer.UpdateProgress("Extracting installation files", 25f);
                        await Task.Delay(500); // 模拟安装过程

                        installer.UpdateProgress("Extracting installation files", 100f); // 完成第一个任务
                        await Task.Delay(500);

                        // 更新第二个任务进度
                        installer.UpdateProgress("Installing core components", 30f);
                        await Task.Delay(500);

                        installer.UpdateProgress("Installing core components", 70f);
                        await Task.Delay(500);

                        installer.UpdateProgress("Installing core components", 100f); // 完成第二个任务
                        await Task.Delay(500);

                        // 可以继续更新其他任务...
                        installer.UpdateProgress("Configuring preferences", 50f);
                        await Task.Delay(500);

                        installer.UpdateProgress("Configuring preferences", 100f);
                        await Task.Delay(500);

                        // 步骤5: 完成所有安装任务
                        // 或者直接调用CompleteInstallation()一次性完成所有任务
                        installer.CompleteInstallation();

                        Console.WriteLine("安装完成！");
                        await Task.Delay(2000); // 给用户时间看到完成界面

                        // 步骤6: 关闭安装界面
                        // 调用StopAsync()确保窗口正确关闭并等待窗口线程结束
                        await installer.StopAsync();
                        Console.WriteLine("安装界面已关闭");
                    }
                }
                catch (OpenTK.Windowing.GraphicsLibraryFramework.GLFWException ex)
                {
                    // 特别捕获GLFW异常，通常是因为在非主线程上调用
                    Console.WriteLine($"GLFW错误: {ex.Message}");
                    Console.WriteLine("请确保在主线程上调用InstallerExample.RunInstallerExample()方法");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"安装过程中发生错误: {ex.Message}");
                    // 确保在错误情况下也关闭窗口
                    if (installer != null)
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
