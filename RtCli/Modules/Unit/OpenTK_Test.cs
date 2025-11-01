using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RtCli.Modules.Unit
{
    /// <summary>
    /// 这是一个Photoshop风格的安装界面实现类，用于显示安装进度和任务列表
    /// 可以从外部被引用，提供了完整的安装UI控制功能
    /// </summary>
    public class PhotoshopStyleInstaller
    {
        private InstallerWindow? _window = null;
        private Task? _windowTask = null;

        /// <summary>
        /// 启动安装界面（从外部引用的入口方法）
        /// 注意：此方法会阻塞调用线程直到窗口关闭
        /// </summary>
        public void Start()
        {
            if (_window != null)
            {
                throw new InvalidOperationException("安装界面已经启动");
            }
            
            try
            {
                // 在同一个线程（调用线程）上创建并运行窗口
                // 这确保OpenGL上下文和窗口操作在同一个线程
                using (_window = new InstallerWindow())
                {
                    _window.Run(); // 这会阻塞直到窗口关闭
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"窗口运行出错: {ex.Message}");
                throw; // 重新抛出异常以便调用者知道发生了错误
            }
            finally
            {
                _window = null;
            }
        }
        
        /// <summary>
        /// 异步启动安装界面
        /// 注意：这个方法会创建一个新的UI线程来运行窗口
        /// 这是推荐的方法，避免阻塞调用线程
        /// </summary>
        public Task StartAsync()
        {
            if (_window != null)
            {
                throw new InvalidOperationException("安装界面已经启动");
            }
            
            // 创建一个新的任务来运行窗口，但这次确保在同一个线程上创建和运行
            _windowTask = Task.Run(() =>
            {
                try
                {
                    // 在同一个后台线程上创建并运行窗口
                    using (_window = new InstallerWindow())
                    {
                        _window.Run();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"窗口运行出错: {ex.Message}");
                    throw;
                }
                finally
                {
                    _window = null;
                }
            });
            
            // 返回实际的窗口任务，而不是Task.CompletedTask
            // 这样调用者可以正确等待窗口初始化完成
            return Task.Run(async () =>
            {
                // 等待窗口初始化完成（最多等待2秒）
                int attempts = 0;
                while (_window == null && attempts < 20)
                {
                    await Task.Delay(100);
                    attempts++;
                }
                
                if (_window == null)
                {
                    throw new InvalidOperationException("窗口初始化超时");
                }
            });
        }

        public void UpdateProgress(string taskName, float progress)
        {
            _window?.UpdateTaskProgress(taskName, progress);
        }

        public void CompleteInstallation()
        {
            _window?.CompleteInstallation();
        }

        public async Task StopAsync()
        {
            _window?.Close();
            if (_windowTask != null)
            {
                await _windowTask;
            }
        }

        public bool IsRunning => _window != null && !_window.IsExited;
    }

    internal class InstallerWindow : GameWindow
    {
        private List<InstallTask> _tasks;
        private float _overallProgress = 0.0f;
        private bool _installationComplete = false;
        private System.Diagnostics.Stopwatch _timer;
        private Random _random;
        private bool _isExited = false;

        // UI状态 - 移除未使用的字段

        public bool IsExited => _isExited;

        public InstallerWindow() : base(GameWindowSettings.Default, new NativeWindowSettings()
        {
            ClientSize = (800, 600),
            Title = "Adobe Photoshop 2023 - Installation",
            // 明确指定OpenGL 2.1版本，完全支持立即模式渲染
            APIVersion = new Version(2, 1),
            Profile = ContextProfile.Compatability
        })
        {
            _tasks = new List<InstallTask>();
            _timer = new System.Diagnostics.Stopwatch();
            _random = new Random();

            InitializeTasks();
        }

        protected override void OnLoad()
        {
            base.OnLoad();

            // 设置OpenGL状态
            GL.ClearColor(0.1f, 0.1f, 0.1f, 1.0f);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            _timer.Start();
        }

        private void InitializeTasks()
        {
            _tasks.Add(new InstallTask("Extracting installation files", 10.0f));
            _tasks.Add(new InstallTask("Installing core components", 25.0f));
            _tasks.Add(new InstallTask("Configuring preferences", 15.0f));
            _tasks.Add(new InstallTask("Installing plugins", 20.0f));
            _tasks.Add(new InstallTask("Registering application", 10.0f));
            _tasks.Add(new InstallTask("Creating shortcuts", 10.0f));
            _tasks.Add(new InstallTask("Finalizing installation", 10.0f));
        }

        public void UpdateTaskProgress(string taskName, float progress)
        {
            var task = _tasks.Find(t => t.Name == taskName);
            if (task != null)
            {
                task.Progress = Math.Min(progress, 100.0f);
                if (task.Progress >= 100.0f)
                {
                    task.IsComplete = true;
                }
                UpdateOverallProgress();
            }
        }

        public void CompleteInstallation()
        {
            foreach (var task in _tasks)
            {
                task.Progress = 100.0f;
                task.IsComplete = true;
            }
            _installationComplete = true;
            _overallProgress = 100.0f;
        }

        private void UpdateOverallProgress()
        {
            float totalProgress = 0.0f;
            foreach (var task in _tasks)
            {
                totalProgress += task.Progress;
            }
            _overallProgress = totalProgress / _tasks.Count;

            if (_overallProgress >= 100.0f)
            {
                _installationComplete = true;
                _overallProgress = 100.0f;
            }
        }

        protected override void OnResize(ResizeEventArgs e)
        {
            base.OnResize(e);
            GL.Viewport(0, 0, ClientSize.X, ClientSize.Y);
        }

        protected override void OnUpdateFrame(FrameEventArgs args)
        {
            base.OnUpdateFrame(args);

            // 可以在这里添加自动进度更新逻辑（如果需要）
            // 或者完全通过外部控制进度
        }

        protected override void OnRenderFrame(FrameEventArgs args)
        {
            base.OnRenderFrame(args);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            // 设置正交投影
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();
            GL.Ortho(0, ClientSize.X, ClientSize.Y, 0, -1, 1);
            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadIdentity();

            // 确保启用立即模式渲染所需的状态
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Texture2D);
            GL.Color4(1.0f, 1.0f, 1.0f, 1.0f);

            // 绘制UI
            DrawBackground();
            DrawHeader();
            DrawProgressSection();
            DrawTaskList();
            DrawFooter();

            SwapBuffers();
            // 确保帧刷新
            Context.SwapInterval = 1; // 启用垂直同步，防止撕裂
        }

        private void DrawBackground()
        {
            // 绘制深色渐变背景
            DrawRect(0, 0, ClientSize.X, ClientSize.Y, new Color(30, 30, 35));
        }

        private void DrawHeader()
        {
            // 标题栏
            DrawRect(0, 0, ClientSize.X, 40, new Color(45, 45, 50));

            // 标题
            DrawText("Adobe Photoshop 2023", 20, 10, Color.White, 16, false);

            // 关闭按钮
            DrawRect(ClientSize.X - 40, 5, 30, 30, new Color(70, 70, 75));
            DrawText("x", ClientSize.X - 25, 5, Color.White, 20, true);
        }

        private void DrawProgressSection()
        {
            int sectionY = 60;

            // 总体进度标题
            DrawText("Installing Adobe Photoshop 2023", ClientSize.X / 2, sectionY, Color.White, 14, true);

            // 进度条背景
            int progressBarWidth = ClientSize.X - 100;
            DrawRect(50, sectionY + 40, progressBarWidth, 20, new Color(60, 60, 65));

            // 进度条前景
            int progressWidth = (int)(progressBarWidth * _overallProgress / 100.0f);
            DrawRect(50, sectionY + 40, progressWidth, 20, new Color(0, 150, 255));

            // 进度文本
            DrawText($"{_overallProgress:F1}% Complete", ClientSize.X / 2, sectionY + 70, Color.White, 12, true);

            // 状态文本
            string status = _installationComplete ? "Installation Complete!" : "Installing...";
            DrawText(status, ClientSize.X / 2, sectionY + 100, _installationComplete ? Color.LightGreen : Color.White, 12, true);
        }

        private void DrawTaskList()
        {
            int startY = 180;
            int taskHeight = 30;

            // 任务列表标题
            DrawText("Installation Tasks", 20, startY - 25, new Color(200, 200, 200), 12, false);

            // 绘制任务列表
            for (int i = 0; i < _tasks.Count; i++)
            {
                var task = _tasks[i];
                int y = startY + i * taskHeight;

                // 任务背景
                DrawRect(20, y, ClientSize.X - 40, 25, new Color(50, 50, 55));

                // 任务名称
                DrawText(task.Name, 30, y + 5, Color.White, 10, false);

                // 任务进度文本
                string progressText = task.IsComplete ? "Complete" : $"{task.Progress:F1}%";
                DrawText(progressText, ClientSize.X - 80, y + 5,
                    task.IsComplete ? Color.LightGreen : new Color(180, 180, 180), 10, false);

                // 任务进度条背景
                DrawRect(30, y + 18, ClientSize.X - 100, 4, new Color(70, 70, 75));

                // 任务进度条前景
                int taskProgressWidth = (int)((ClientSize.X - 100) * task.Progress / 100.0f);
                DrawRect(30, y + 18, taskProgressWidth, 4,
                    task.IsComplete ? Color.LightGreen : new Color(0, 150, 255));
            }
        }

        private void DrawFooter()
        {
            // 底部区域
            DrawRect(0, ClientSize.Y - 60, ClientSize.X, 60, new Color(45, 45, 50));

            // 按钮
            if (_installationComplete)
            {
                DrawRect(ClientSize.X - 120, ClientSize.Y - 45, 100, 30, new Color(0, 120, 215));
                DrawText("Finish", ClientSize.X - 70, ClientSize.Y - 40, Color.White, 12, true);
            }
            else
            {
                DrawRect(ClientSize.X - 120, ClientSize.Y - 45, 100, 30, new Color(70, 70, 75));
                DrawText("Cancel", ClientSize.X - 70, ClientSize.Y - 40, Color.White, 12, true);
            }

            // 版权信息
            DrawText("© 2023 Adobe Inc. All rights reserved.", ClientSize.X / 2, ClientSize.Y - 20, new Color(150, 150, 150), 10, true);
        }

        // 绘制矩形
        private void DrawRect(float x, float y, float width, float height, Color color)
        {
            GL.Color4(color.R / 255.0f, color.G / 255.0f, color.B / 255.0f, color.A / 255.0f);
            GL.Begin(PrimitiveType.Quads);
            GL.Vertex2(x, y);
            GL.Vertex2(x + width, y);
            GL.Vertex2(x + width, y + height);
            GL.Vertex2(x, y + height);
            GL.End();
        }

        // 绘制文本（优化版，更明显的显示效果）
        private void DrawText(string text, float x, float y, Color color, int size, bool centered)
        {
            // 在实际应用中，这里应该使用纹理字体渲染
            // 这里我们使用更明显的矩形模拟文本
            if (text.Length > 0)
            {
                float textWidth = text.Length * size * 0.6f;
                if (centered) x -= textWidth / 2;

                // 模拟文本绘制 - 绘制更宽更明显的矩形代表字符
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] != ' ')
                    {
                        // 增加矩形的高度和宽度，使文本更明显
                        float charWidth = size * 0.5f;
                        float charHeight = size * 0.8f; // 增加高度使字符更明显
                        DrawRect(x + i * size * 0.6f, y, charWidth, charHeight, color);
                    }
                }
            }
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);

            if (e.Button == MouseButton.Left)
            {
                // 获取鼠标位置
                var mouseState = MouseState;
                float mouseX = mouseState.X;
                float mouseY = mouseState.Y;

                // 检查是否点击了关闭按钮
                if (mouseX >= ClientSize.X - 40 && mouseX <= ClientSize.X - 10 &&
                    mouseY >= 5 && mouseY <= 35)
                {
                    Close();
                }

                // 检查是否点击了完成/取消按钮
                if (mouseX >= ClientSize.X - 120 && mouseX <= ClientSize.X - 20 &&
                    mouseY >= ClientSize.Y - 45 && mouseY <= ClientSize.Y - 15)
                {
                    Close();
                }
            }
        }

        // 移除重写的OnClosed方法，改用事件处理

        // 直接使用基类的Close方法，不需要重写
        // public new void Close()
        // {
        //     base.Close();
        // }
    }

    internal class InstallTask
    {
        public string Name { get; set; }
        public float Progress { get; set; }
        public bool IsComplete { get; set; }
        public float Weight { get; set; }

        public InstallTask(string name, float weight)
        {
            Name = name;
            Weight = weight;
            Progress = 0.0f;
            IsComplete = false;
        }
    }

    // 简化的Color结构，避免依赖System.Drawing
    public struct Color
    {
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
        public byte A { get; set; }

        public Color(byte r, byte g, byte b, byte a = 255)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public static Color White => new Color(255, 255, 255);
        public static Color LightGreen => new Color(144, 238, 144);
    }

    // 简化的Point结构
    public struct Point
    {
        public int X { get; set; }
        public int Y { get; set; }

        public Point(int x, int y)
        {
            X = x;
            Y = y;
        }
    }
}
