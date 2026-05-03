using RtCli.Modules.Extension;
using RtExtensionManager;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RtCli.Modules
{
    internal class Reload
    {
        private static bool _isEnd = false;
        private static bool _isInitialized = false;
        private static readonly object _endLock = new object();

        public static void Initialize()
        {
            if (_isInitialized) return;
            _isInitialized = true;

            Console.CancelKeyPress += OnCancelKeyPress;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            Output.Log("[yellow]正在关闭程序...[/]", 1, "Reload");
            End();
        }

        private static void OnProcessExit(object? sender, EventArgs e)
        {
            End();
        }

        public static void Restart()
        {
            try
            {
                string? currentProcessPath = Environment.ProcessPath;
                RtExtensionManager.RtExtensionManager.UnloadAll();
                Program.ReleaseMutex();
                Console.Clear();
                if (currentProcessPath != null)
                {
                    Process.Start(currentProcessPath);
                }
                else
                {
                    Output.Log("进程路径环境异常无法重新加载！", 3, "Reload");
                }

                Environment.Exit(0);
            }
            catch (Exception)
            {
                Output.Log("出现错误： ", 3, "Reload");
                throw;
            }
        }

        public static void End()
        {
            lock (_endLock)
            {
                if (_isEnd) return;
                _isEnd = true;
            }

            try
            {
                EventBus.Publish(new ProgramShutdownEvent());
                RtExtensionManager.RtExtensionManager.UnloadAll();
                Output.CloseLogging();
                Output.TextBlock(Modules.Unit.I18n.Get("main_end"), 1, "Task#0");
                Program.ReleaseMutex();
            }
            catch { }
        }
    }
}
