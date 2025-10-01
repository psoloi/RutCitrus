using RtCli.Modules;
using RtExtensionManager;
using Spectre.Console;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Terminal.Gui;

namespace RtCli
{
    internal class Program
    {


        /// <summary>
        /// 应用程序主入口点
        /// </summary>
        /// <param name="args"></param>
        static void Main(string[] args)
        {

            Console.Title = "RtCli";
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Output.TextBlock("启动主线程...", 1, "Task#0");

            string ThisProgramName = "RtCli";
            Thread.CurrentThread.Name = "Main";
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            Output.Log("启动中...", 1, ThisProgramName);

            RtExtensionManager.RtExtensionManager.LoadAll();

            stopwatch.Stop();
            Output.Log($"加载完毕！用时（{stopwatch.ElapsedMilliseconds}ms）", 1, ThisProgramName);


            Output.Log("按下（C）打开命令（G）打开TUI（R）重新加载（Esc）退出", 1, ThisProgramName);
            ConsoleKeyInfo keyInfo = Console.ReadKey(true);
            if (keyInfo.Key == ConsoleKey.Enter)
            {
                Console.Clear();
                Output.Log("", 1, ThisProgramName);
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
