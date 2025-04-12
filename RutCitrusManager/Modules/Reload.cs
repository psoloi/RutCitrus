using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RutCitrusManager.Modules
{
    internal class Reload
    {
        public static void Restart()
        {
            try
            {
                // 获取当前进程的完整路径
                string ?currentProcessPath = Environment.ProcessPath;

                // 清空控制台
                Console.Clear();
                if(currentProcessPath != null)
                {
                    // 启动当前程序
                    Process.Start(currentProcessPath);
                }
                else
                {
                    Output.Text_Time("当前进程路径为空，无法重启程序。", 3);
                }

                // 退出当前程序
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Output.Text_Time("出现错误： ", 3);
                AnsiConsole.WriteException(ex);
            }
        }
    }
}
