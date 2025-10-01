using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Terminal.Gui;


namespace RtCli.Modules.Mode
{
    class TUI
    {
        public static void Run()
        {
            // 初始化全局异常处理
            BiosExceptionHandler.Initialize();

            // 启动Terminal.Gui应用
            Application.Init();

            // 设置BIOS风格主题
            SetBiosTheme();

            // 创建主窗口
            var mainWindow = CreateMainWindow();

            // 运行应用
            Application.Run(mainWindow);

            Application.Shutdown();
        }

        static void SetBiosTheme()
        {
            // BIOS经典蓝底白字配色
            var biosScheme = new ColorScheme()
            {
                Normal = Application.Driver.MakeAttribute(Color.White, Color.Blue),
                Focus = Application.Driver.MakeAttribute(Color.BrightYellow, Color.DarkGray),
                HotNormal = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Blue),
                HotFocus = Application.Driver.MakeAttribute(Color.BrightYellow, Color.DarkGray),
                Disabled = Application.Driver.MakeAttribute(Color.Gray, Color.Blue)
            };

            // 应用到各种控件
            Colors.Base = biosScheme;
            Colors.Dialog = biosScheme;
            Colors.Menu = biosScheme;
            Colors.Error = biosScheme;

            // 对话框颜色
            Colors.Dialog.Normal = Application.Driver.MakeAttribute(Color.White, Color.Blue);
            Colors.TopLevel.Normal = Application.Driver.MakeAttribute(Color.White, Color.Blue);
        }

        static Window CreateMainWindow()
        {
            var window = new Window("BIOS风格异常处理器 v1.0")
            {
                X = 0,
                Y = 1,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                ColorScheme = Colors.Base
            };

            // 标题栏
            var title = new Label("┌────────────────────────────────────────────────────────┐")
            {
                X = 0,
                Y = 0
            };
            var title2 = new Label("│             全局异常监控系统 - 运行中                │")
            {
                X = 0,
                Y = 1
            };
            var title3 = new Label("└────────────────────────────────────────────────────────┘")
            {
                X = 0,
                Y = 2
            };

            // 状态信息
            var status = new Label("系统状态: 正常监控中...")
            {
                X = 2,
                Y = 4
            };

            // 测试按钮
            var btnAppDomainException = new Button("触发 AppDomain 异常")
            {
                X = 2,
                Y = 6
            };

            var btnTaskException = new Button("触发 Task 异常")
            {
                X = 25,
                Y = 6
            };

            var btnUnobservedTaskException = new Button("触发未观察Task异常")
            {
                X = 2,
                Y = 8
            };

            var btnExit = new Button("退出系统")
            {
                X = 25,
                Y = 8
            };

            // 日志区域
            var logFrame = new FrameView("异常日志")
            {
                X = 2,
                Y = 10,
                Width = Dim.Fill() - 2,
                Height = Dim.Fill() - 2
            };

            var logView = new TextView()
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                ReadOnly = true
            };

            logFrame.Add(logView);

            // 按钮事件
            btnAppDomainException.Clicked += () =>
            {
                BiosExceptionHandler.TestAppDomainException();
            };

            btnTaskException.Clicked += () =>
            {
                BiosExceptionHandler.TestTaskException();
            };

            btnUnobservedTaskException.Clicked += () =>
            {
                BiosExceptionHandler.TestUnobservedTaskException();
            };

            btnExit.Clicked += () =>
            {
                Application.RequestStop();
            };

            // 添加到窗口
            window.Add(title, title2, title3, status,
                      btnAppDomainException, btnTaskException,
                      btnUnobservedTaskException, btnExit, logFrame);

            // 设置日志回调
            BiosExceptionHandler.SetLogCallback((msg) =>
            {
                Application.MainLoop.Invoke(() =>
                {
                    logView.Text += $"{DateTime.Now:HH:mm:ss} {msg}\n";
                    logView.ScrollTo(1);
                });
            });

            return window;
        }
    }

    public static class BiosExceptionHandler
    {
        private static Action<string> _logCallback;

        public static void Initialize()
        {
            // 注册全局异常处理
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                HandleException("AppDomain异常", ex, e.IsTerminating);
            };

            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                HandleException("未观察Task异常", e.Exception, false);
                e.SetObserved(); // 阻止应用崩溃
            };

            Log("全局异常处理器已初始化 - BIOS风格界面");
        }

        public static void SetLogCallback(Action<string> callback)
        {
            _logCallback = callback;
        }

        private static void HandleException(string source, Exception exception, bool isTerminating)
        {
            Log($"[{source}] 检测到异常: {exception?.GetType().Name} - {exception?.Message}");

            // 在UI线程中显示异常对话框
            Application.MainLoop.Invoke(() =>
            {
                ShowExceptionDialog(source, exception, isTerminating);
            });
        }

        private static void ShowExceptionDialog(string source, Exception exception, bool isTerminating)
        {
            var dialog = new Dialog($"异常报告 - {source}", 60, 20)
            {
                ColorScheme = Colors.Dialog
            };

            // 创建BIOS风格的边框
            var frame = new FrameView("")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                Border = new Terminal.Gui.Border()
                {
                    BorderStyle = Terminal.Gui.BorderStyle.Double
                }
            };

            var exceptionType = new Label($"异常类型: {exception?.GetType().Name}")
            {
                X = 1,
                Y = 1
            };

            var exceptionMessage = new Label($"异常信息: {exception?.Message}")
            {
                X = 1,
                Y = 2
            };

            var stackTrace = new TextView()
            {
                X = 1,
                Y = 4,
                Width = Dim.Fill() - 2,
                Height = 8,
                Text = exception?.StackTrace ?? "无堆栈跟踪信息",
                ReadOnly = true
            };

            var status = new Label(isTerminating ? "状态: 严重 - 应用将终止" : "状态: 已处理 - 应用继续运行")
            {
                X = 1,
                Y = 13
            };

            var okButton = new Button("确定")
            {
                X = Pos.Center(),
                Y = 15
            };

            okButton.Clicked += () =>
            {
                Application.RequestStop(dialog);

                if (isTerminating)
                {
                    Application.Shutdown();
                    Environment.Exit(1);
                }
            };

            frame.Add(exceptionType, exceptionMessage, stackTrace, status);
            dialog.Add(frame, okButton);

            Application.Run(dialog);
        }

        // 测试方法
        public static void TestAppDomainException()
        {
            Log("触发AppDomain异常测试...");
            throw new InvalidOperationException("这是测试的AppDomain异常");
        }

        public static void TestTaskException()
        {
            Log("触发Task异常测试...");
            Task.Run(() =>
            {
                throw new ArgumentException("这是测试的Task异常");
            });
        }

        public static void TestUnobservedTaskException()
        {
            Log("触发未观察Task异常测试...");

            // 创建一个会抛出异常但不会被等待的Task
            var task = Task.Run(() =>
            {
                throw new NotImplementedException("这是未观察的Task异常");
            });

            // 不等待task，让其变成未观察状态
            task = null;

            // 强制垃圾回收来触发UnobservedTaskException
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        private static void Log(string message)
        {
            _logCallback?.Invoke(message);
        }
    }
}
