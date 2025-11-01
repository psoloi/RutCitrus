using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Spectre.Console.Cli;
using Spectre.Console;

namespace RtCli.Modules.Mode
{
    internal class Commands
    {
        public static void Execute(string type)
        {
            string ThisProgramName = "Commands";
            if (string.IsNullOrEmpty(type))
            {
                //Output.Log("异常的程序启动参数?", 2, ThisProgramName);
                return;
            }
            var actions = new Dictionary<string, Action>
                {
                    { "SetMode", () => { 
                        Output.Log("设置模式中...", 1, ThisProgramName);
                    } },
                    { "2", () => { /* Add functionality for option 2 */ } },
                    { "3", () => { /* Add functionality for option 3 */ } },
                    { "4", () => { /* Add functionality for option 4 */ } },
                    { "5", () => { /* Add functionality for option 5 */ } },
                    { "6", () => { } },
                    { "7", () => { } }
                };
            if (actions.ContainsKey(type))
            {
                actions[type].Invoke();
            }
            else
            {
                Output.Log("未知的程序启动参数!", 2, ThisProgramName);
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }

        /// <summary>
        /// 使用Spectre.Console.Cli实现命令行参数处理
        /// </summary>
        /// <param name="args">命令行参数</param>
        /// <returns>是否处理了参数（true表示已处理参数，false表示需要继续执行默认逻辑）</returns>
        public static bool Cli(string[] args)
        {
            // 如果没有参数，不处理
            if (args.Length == 0)
            {
                return false;
            }

            // 简单的参数处理方式
            // 检查是否包含--TUI参数
            if (args.Contains("--TUI", StringComparer.OrdinalIgnoreCase))
            {
                Output.Log("启动TUI模式...", 1, "RtCli");
                Modules.Mode.TUI.Run();
                return true;
            }

            // 检查是否包含--installer参数
            if (args.Contains("--installer", StringComparer.OrdinalIgnoreCase))
            {
                Output.Log("启动安装界面...", 1, "RtCli");
                RunInstaller();
                return true;
            }

            // 检查是否包含--reload参数
            if (args.Contains("--reload", StringComparer.OrdinalIgnoreCase))
            {
                Output.Log("重启程序...", 1, "RtCli");
                Reload.Restart();
                return true;
            }

            // 检查是否包含--help或-h参数
            if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || 
                args.Contains("-h", StringComparer.OrdinalIgnoreCase))
            {
                ShowHelp();
                return true;
            }

            return false;
        }

        /// <summary>
        /// 运行安装界面
        /// </summary>
        private static void RunInstaller()
        {
            var installer = new Modules.Unit.PhotoshopStyleInstaller();
            try
            {
                installer.Start(); // 使用同步方法以阻塞当前线程
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteLine($"安装界面启动失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 显示帮助信息
        /// </summary>
        private static void ShowHelp()
        {
            var table = new Table();
            table.AddColumn(new TableColumn("参数").Centered());
            table.AddColumn(new TableColumn("描述"));

            table.AddRow("--TUI", "启动文本用户界面模式");
            table.AddRow("--installer", "启动安装界面演示");
            table.AddRow("--reload", "重新加载应用");
            table.AddRow("--help, -h", "显示此帮助信息");

            AnsiConsole.WriteLine("RtCli 命令行参数:");
            AnsiConsole.Write(table);
        }
    }
}
