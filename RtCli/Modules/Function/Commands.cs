using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Spectre.Console.Cli;
using Spectre.Console;

namespace RtCli.Modules.Function
{
    public delegate void CommandHandler(string[] args);

    public static class CommandRegistry
    {
        private static readonly Dictionary<string, CommandHandler> _commands = new();
        private static readonly Dictionary<string, string> _commandDescriptions = new();

        public static IReadOnlyDictionary<string, CommandHandler> Commands => _commands;
        public static IReadOnlyDictionary<string, string> Descriptions => _commandDescriptions;

        public static void RegisterCommand(string command, CommandHandler handler, string description = "")
        {
            if (string.IsNullOrWhiteSpace(command))
                return;

            _commands[command.ToLower()] = handler;
            if (!string.IsNullOrEmpty(description))
            {
                _commandDescriptions[command.ToLower()] = description;
            }
        }

        public static void UnregisterCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return;

            _commands.Remove(command.ToLower());
            _commandDescriptions.Remove(command.ToLower());
        }

        public static bool TryExecute(string command, string[] args)
        {
            if (string.IsNullOrWhiteSpace(command))
                return false;

            if (_commands.TryGetValue(command.ToLower(), out var handler))
            {
                handler.Invoke(args);
                return true;
            }
            return false;
        }

        public static bool HasCommand(string command)
        {
            return !string.IsNullOrWhiteSpace(command) && _commands.ContainsKey(command.ToLower());
        }

        public static string[] GetAutoCompleteCommands()
        {
            return _commands.Keys.ToArray();
        }
    }

    internal class Commands
    {
        public static void Execute(string type)
        {
            string ThisProgramName = "Commands";
            if (string.IsNullOrEmpty(type))
            {
                return;
            }
            var actions = new Dictionary<string, Action>
                {
                    { "SetMode", () => { 
                        Output.Log("设置模式中...", 1, ThisProgramName);
                    } },
                    { "2", () => { /* F 2 */ } },
                    { "3", () => { /* F 3 */ } },
                    { "4", () => { /* F 4 */ } },
                    { "5", () => { /* F 5 */ } },
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

        public static bool Cli(string[] args)
        {
            if (args.Length == 0)
            {
                return false;
            }

            if (args.Contains("--reload", StringComparer.OrdinalIgnoreCase))
            {
                Output.Log("重启程序...", 1, "RtCli");
                Reload.Restart();
                return true;
            }

            if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || 
                args.Contains("-h", StringComparer.OrdinalIgnoreCase))
            {
                ShowHelp();
                return true;
            }

            return false;
        }

        private static void ShowHelp()
        {
            var table = new Table();
            table.AddColumn(new TableColumn("参数").Centered());
            table.AddColumn(new TableColumn("描述"));

            table.AddRow("--installer", "启动安装界面演示");
            table.AddRow("--reload", "重新加载应用");
            table.AddRow("--help, -h", "显示此帮助信息");

            AnsiConsole.WriteLine("RtCli 命令行参数:");
            AnsiConsole.Write(table);
        }
    }
}
