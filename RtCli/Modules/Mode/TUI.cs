using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Terminal.Gui;

namespace RtCli.Modules.Mode
{
    class TUI
    {
        private static Process currentProcess;
        private static Label memoryLabel;
        private static ProgressBar memoryProgressBar;
        private static FrameView mainContent;

        public static void Run()
        {
            currentProcess = Process.GetCurrentProcess();

            // 初始化Terminal.Gui应用
            Application.Init();

            // 创建主窗口
            var top = Application.Top;

            // 创建菜单栏
            var menu = CreateMenuBar();
            top.Add(menu);

            // 创建主内容区域
            mainContent = new FrameView("Main")
            {
                X = 0,
                Y = 1, // 位于菜单栏下方
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };

            // 初始化主页内容
            UpdateMainPage();
            top.Add(mainContent);

            // 启动定时更新性能数据的任务
            StartPerformanceMonitoring();

            // 运行应用
            Application.Run();
            Application.Shutdown();
        }

        private static MenuBar CreateMenuBar()
        {
            var menuItems = new MenuBarItem[]
            {
                new MenuBarItem ("_Main", new MenuItem[]
                {
                    new MenuItem ("_View Main", "F1", () => ShowMainPage())
                }),
                new MenuBarItem ("_Menu", new MenuItem[]
                {
                    new MenuItem ("_Item 1", "", () => ShowMenuItem("Menu Item 1")),
                    new MenuItem ("_Item 2", "", () => ShowMenuItem("Menu Item 2")),
                    new MenuItem ("_Exit", "Ctrl+Q", () => Application.RequestStop())
                }),
                new MenuBarItem ("_Plugins", new MenuItem[]
                {
                    new MenuItem ("_Plugin Manager", "", () => ShowMenuItem("Plugin Manager")),
                    new MenuItem ("_Install Plugin", "", () => ShowMenuItem("Install Plugin"))
                }),
                new MenuBarItem ("_Settings", new MenuItem[]
                {
                    new MenuItem ("_Basic Settings", "", () => ShowMenuItem("Basic Settings")),
                    new MenuItem ("_Advanced Settings", "", () => ShowMenuItem("Advanced Settings"))
                }),
                new MenuBarItem ("_About", new MenuItem[]
                {
                    new MenuItem ("_About RtCli", "", () => ShowMenuItem("About RtCli"))
                })
            };

            return new MenuBar(menuItems);
        }

        private static void ShowMainPage()
        {
            mainContent.Title = "Main";
            UpdateMainPage();
        }

        private static void ShowMenuItem(string title)
        {
            mainContent.Title = title;
            // 为About页面添加特殊内容
            mainContent.RemoveAll();

            if (title == "About RtCli")
            {
                var authorLabel = new Label("Author: psoloi")
                {
                    X = Pos.Center(),
                    Y = Pos.Center() - 2
                };
                
                var descriptionLabel = new Label("RtCli is a sub-project of RutCitrus")
                {
                    X = Pos.Center(),
                    Y = Pos.Center()
                };
                
                var descriptionLabel2 = new Label("providing framework for other projects")
                {
                    X = Pos.Center(),
                    Y = Pos.Center() + 1
                };
                
                var versionLabel = new Label($"Ver: {Program.RtCliVersion}")
                {
                    X = Pos.Center(),
                    Y = Pos.Center() + 3
                };
                
                mainContent.Add(authorLabel, descriptionLabel, descriptionLabel2, versionLabel);
            }
            else
            {
                // Add placeholder text for other menu items
                var label = new Label($"You selected: {title}")
                {
                    X = Pos.Center(),
                    Y = Pos.Center()
                };
                mainContent.Add(label);
            }
            
            mainContent.SetNeedsDisplay();
        }

        private static void UpdateMainPage()
        {
            // 清空当前内容
            mainContent.RemoveAll();

            // 创建性能信息标签
            memoryLabel = new Label("Memory Usage: Calculating...") { X = 2, Y = 2 };

            memoryProgressBar = new ProgressBar()
            {
                X = 2,
                Y = 3,
                Width = 40,
                Fraction = 0
            };

            // 添加到主内容区域
            mainContent.Add(memoryLabel, memoryProgressBar);

            // 添加一些额外信息
            var appInfoLabel = new Label($"Application: {currentProcess.ProcessName}") { X = 2, Y = 7 };
            var pidLabel = new Label($"Process ID: {currentProcess.Id}") { X = 2, Y = 8 };
            var startTimeLabel = new Label($"Start Time: {currentProcess.StartTime}") { X = 2, Y = 9 };

            mainContent.Add(appInfoLabel, pidLabel, startTimeLabel);
            mainContent.SetNeedsDisplay();

            // 立即更新一次性能数据
            UpdatePerformanceData();
        }

        private static void StartPerformanceMonitoring()
        {
            Task.Run(async () =>
            {
                while (Application.Top.Running)
                {
                    UpdatePerformanceData();
                    await Task.Delay(1000); // 每秒更新一次
                }
            });
        }

        private static void UpdatePerformanceData()
        {
            try
            {
                currentProcess.Refresh();

                // 获取内存使用情况
                long memoryUsage = currentProcess.WorkingSet64;
                string memoryText = $"Memory Usage: {FormatBytes(memoryUsage)}";
                
                // 估计系统总内存（简化版本，实际应用可能需要更准确的方法）
                long totalMemory = Environment.WorkingSet * 10; // 简化估计
                double memoryPercentage = (double)memoryUsage / totalMemory;

                // 更新UI（需要在主线程中）
                Application.MainLoop.Invoke(() =>
                {
                    memoryLabel.Text = memoryText;
                    memoryProgressBar.Fraction = (float)Math.Min(memoryPercentage, 1.0);
                });

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating performance data: {ex.Message}");
            }
        }

        private static string FormatBytes(long bytes)
        {
            string[] suffix = { "B", "KB", "MB", "GB", "TB" };
            int i;
            double dblBytes = bytes;

            for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024)
            {
                dblBytes = bytes / 1024.0;
            }

            return $"{dblBytes:F2} {suffix[i]}";
        }
    }
}
