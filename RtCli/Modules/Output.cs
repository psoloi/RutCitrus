using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RtCli.Modules
{
    public class Output
    {
        static string c_info = "[white]|[/][green]信息[/][white]| [/]";
        static string c_error = "[white]|[/][red]错误[/][white]| [/]";
        static string c_warn = "[white]|[/][yellow]警告[/][white]| [/]";

        // 方块输出
        protected internal static void TextBlock(string msg, int msg_type, string Task)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            string info = $"[white on dodgerblue2]{time}[/]" + $"[white on steelblue1][[MainThread - {Task}]][/]" + "[black on green]信息[/] ";
            string error = $"[white on dodgerblue2]{time}[/]" + $"[white on steelblue1][[MainThread - {Task}]][/]" + "[black on red]错误[/] ";
            string warn = $"[white on dodgerblue2]{time}[/]" + $"[white on steelblue1][[MainThread - {Task}]][/]" + "[black on gold1]警告[/] ";
            if (msg_type == 1)
            {
                AnsiConsole.Markup(info + msg + "\n");
            }
            if (msg_type == 2)
            {
                AnsiConsole.Markup(warn + msg + "\n");
            }
            if (msg_type == 3)
            {
                AnsiConsole.Markup(error + msg + "\n");
            }
        }

        // 为基础输出 格式[时;分;秒] |信息| [线程Main/XXX - Task] (调用程序名称) 消息
        /// <summary>
        /// 该方法用于所有的非错误日志输出
        /// </summary>
        public static void Log(string msg, int msg_type, string name)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            if (msg_type == 1)
            {
                AnsiConsole.Markup($"[white][[{time}]][/] " + c_info + $"[white][[{Thread.CurrentThread.Name}-{Thread.CurrentThread.ManagedThreadId}]][/] " + $"[dodgerblue1]({name})[/] " + msg + "\n");
            }
            if (msg_type == 2)
            {
                AnsiConsole.Markup($"[white][[{time}]][/] " + c_warn + $"[white][[{Thread.CurrentThread.Name}-{Thread.CurrentThread.ManagedThreadId}]][/] " + $"[dodgerblue1]({name})[/] " + msg + "\n");
            }
            if (msg_type == 3)
            {
                AnsiConsole.Markup($"[white][[{time}]][/] " + c_error + $"[white][[{Thread.CurrentThread.Name}-{Thread.CurrentThread.ManagedThreadId}]][/] " + $"[dodgerblue1]({name})[/] " + msg + "\n");
            }
        }


        private static bool _crashAssistantRunning = false;
        private static readonly object _crashLock = new object();

        /// <summary>
        /// 异步CrashAssistant
        /// </summary>
        public static async Task StartCrashAssistantAsync(Exception ex)
        {
            await Task.Run(() => CrashAssistant(ex));
        }

        /// <summary>
        /// CrashAssistant错误处理
        /// </summary>
        public static void CrashAssistant(Exception ex)
        {
            lock (_crashLock)
            {
                if (_crashAssistantRunning)
                    return;
                _crashAssistantRunning = true;
            }

            try
            {
                Log("[red][[CrashAssistant]] 已捕获到一个未被处理异常[/]\n", 3, "CrashAssistant");
                string time = DateTime.Now.ToString("HH:mm:ss");
                AnsiConsole.Markup($"[white on red][[{time}]][/][white on darkred][[CrashAssistant]][/]\n\n");

                var table = new Table()
                  .Border(TableBorder.Heavy)
                  .AddColumn("[yellow]属性[/]")
                  .AddColumn("[yellow]值[/]");

                table.AddRow("异常类型", ex.GetType().Name);
                table.AddRow("异常消息", ex.Message);
                table.AddRow("发生时间", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                table.AddRow("线程", Thread.CurrentThread.Name ?? "Unknown");
                if (ex.InnerException != null)
                {
                    table.AddRow("内部异常类型", ex.InnerException.GetType().Name);
                    table.AddRow("内部异常消息", ex.InnerException.Message);
                }
                AnsiConsole.Write(table);

                if (ex.InnerException != null)
                {
                    AnsiConsole.Markup("[yellow]内部异常:[/]\n");
                    AnsiConsole.Markup($"  [yellow]类型:[/] [white]{ex.InnerException.GetType().Name}[/]\n");
                    AnsiConsole.Markup($"  [yellow]消息:[/] [white]{ex.InnerException.Message}[/]\n\n");
                }

                AnsiConsole.Markup("[yellow]堆栈跟踪:[/]\n");
                AnsiConsole.Markup($"[grey]{Markup.Escape(ex.StackTrace ?? "无堆栈信息")}[/]\n\n");


            }
            catch (Exception innerEx)
            {
                AnsiConsole.Markup($"[red]CrashAssistant 自身发生错误: {innerEx.Message}[/]\n");
            }
            finally
            {
                lock (_crashLock)
                {
                    _crashAssistantRunning = false;
                }
            }
        }

        /// <summary>
        /// 根据条件判断输出错误报告
        /// </summary>
        public static void ReportError(Exception ex, bool critical = false, string? additionalInfo = null)
        {
            Task.Run(() =>
            {
                string time = DateTime.Now.ToString("HH:mm:ss");
                string severity = critical ? "[white on red]严重[/]" : "[white on yellow]一般[/]";

                AnsiConsole.Markup($"[white on dodgerblue2][[{time}]][/][white on steelblue1][[ErrorReport]][/] {severity}\n");

                if (!string.IsNullOrEmpty(additionalInfo))
                {
                    AnsiConsole.Markup($"[yellow]附加信息:[/] [white]{additionalInfo}[/]\n");
                }

                CrashAssistant(ex);
            });
        }
    }
}
