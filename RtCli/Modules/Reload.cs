using RtExtensionManager;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Terminal.Gui;
using Color = Terminal.Gui.Color;

namespace RtCli.Modules
{
    internal class Reload
    {
        public static void Restart()
        {
            try
            {
                string? currentProcessPath = Environment.ProcessPath;
                RtExtensionManager.RtExtensionManager.UnloadAll();
                Console.Clear();
                if (currentProcessPath != null)
                {
                    // 启动当前程序
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
    }
}