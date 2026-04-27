using RtCli.Modules.Unit;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;

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
                    { "DebugMode", () => {
                        Output.Log("调试模式中...", 1, ThisProgramName);
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
                Output.Log("未知的命令参数!", 2, ThisProgramName);
            }
        }
        public static void MinecraftCommand(string[] args)
        {
            string ThisProgramName = "MinecraftCommand";
            if (args == null || args.Length == 0)
            {
                Output.Log("请提供要执行的 Minecraft 命令!", 2, ThisProgramName);
                return;
            }
            string command = string.Join(' ', args);
            Output.Log($"执行 Minecraft 命令: {command}", 1, ThisProgramName);
            // 在这里添加实际执行 Minecraft 命令的逻辑
        }
    }
}
