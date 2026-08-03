using RtCli.Modules.Extension;
using RtCli.Modules.Function;
using RtCli.Modules.Unit;
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
        private static readonly object _endLock = new();
        public static bool IsShuttingDown => _isEnd;

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
                Output.Log("[yellow]正在重启程序...[/]", 1, "Reload");
                CleanupBeforeExit();

                string? currentProcessPath = Environment.ProcessPath;
                Program.ReleaseMutex();
                Console.Clear();
                if (currentProcessPath == null)
                {
                    Output.Log("进程路径环境异常无法重新加载！", 3, "Reload");
                    return;
                }

                Process.Start(currentProcessPath);

                // 启动新进程后必须立即终止当前(旧)进程，否则两个进程会同时争抢同一控制台输入
                // 导致：命令输入异常、TAB补全异常、关闭时出现多次关闭消息
                // 置 _isEnd 防止 Environment.Exit 触发的 ProcessExit 事件再次执行 End() 重复清理
                _isEnd = true;
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
                Output.Log("[yellow]正在关闭程序...[/]", 1, "Reload");
                CleanupBeforeExit();
                Output.TextBlock(Modules.Unit.I18n.Get("main_end"), 1, "Task#0");
                Program.ReleaseMutex();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Output.CrashAssistant(ex);
            }
        }

        /// <summary>
        /// 退出前清理所有后台资源：MC服务端、gRPC、调度器、备份定时器、扩展、日志
        /// </summary>
        private static void CleanupBeforeExit()
        {
            try
            {
                EventBus.Publish(new ProgramShutdownEvent());
                Intelligence.StopAutoBackup();
                Scheduler.Stop();
                Analyzer.StopServer();
                Analyzer.Detach();
                var grpcTask = Connector.StopServerAsync();
                if (!grpcTask.Wait(3000)) // 过低可能会卡死
                {
                    Output.Log("gRPC服务器关闭超时，强制继续。", 2, "Reload");
                }
                RtExtensionManager.UnloadAll();
                Output.CloseLogging();
            }
            catch (Exception ex)
            {
                Output.CrashAssistant(ex);
            }
        }
    }
}
