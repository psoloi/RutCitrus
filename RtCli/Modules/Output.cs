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
            string info = $"[white on dodgerblue2]{time}[/]" + $"[white on steelblue1][[Main - {Task}]][/]" + "[black on green]信息[/] ";
            string error = $"[white on dodgerblue2]{time}[/]" + $"[white on steelblue1][[Main - {Task}]][/]" + "[black on red]错误[/] ";
            string warn = $"[white on dodgerblue2]{time}[/]" + $"[white on steelblue1][[Main - {Task}]][/]" + "[black on gold1]警告[/] ";
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
                AnsiConsole.Markup($"[white][[{time}]][/] " + c_info + $"[white][[{Thread.CurrentThread.Name}]][/] " + $"[dodgerblue1]({name})[/] " + msg + "\n");
            }
            if (msg_type == 2)
            {
                AnsiConsole.Markup($"[white][[{time}]][/] " + c_warn + $"[white][[{Thread.CurrentThread.Name}]][/] " + $"[dodgerblue1]({name})[/] " + msg + "\n");
            }
            if (msg_type == 3)
            {
                AnsiConsole.Markup($"[white][[{time}]][/] " + c_error + $"[white][[{Thread.CurrentThread.Name}]][/] " + $"[dodgerblue1]({name})[/] " + msg + "\n");
            }
        }


        //用于某些特殊情况的错误输出
        [RequiresDynamicCode("Calls RtCli.Modules.Output.EX(Exception)")]
        public static void EX(Exception ex)
        {
            AnsiConsole.WriteException(ex);
        }
    }
}
