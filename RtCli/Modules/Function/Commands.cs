using RtCli.Modules.Unit;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
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
        private static readonly Dictionary<string, Action> DebugActions = new Dictionary<string, Action>
        {
            { "1", () => {
                Output.Log("测试", 1, "Debug");
                Maximize_ConsoleWindows();
            } },
            { "2", () => { } },
            { "3", () => { } },
            { "4", () => { } },
            { "5", () => { } },
            { "6", () => { } },
            { "7", () => { } }
        };

        public static void Execute(string type)
        {
            string ThisProgramName = "Debug";
            try 
            {
                if (string.IsNullOrEmpty(type))
                {
                    return;
                }
                if (DebugActions.ContainsKey(type))
                {
                    DebugActions[type].Invoke();
                }
                else
                {
                    Output.Log("调试关闭", 1, ThisProgramName);
                }
            }
            catch (Exception ex)
            {
                Output.Log($"调试失败: {ex.Message}", 1, ThisProgramName);
            }
        }
        // Windows的最大化控制台窗口
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetConsoleWindow();

        const int SW_MAXIMIZE = 3;
        public static void Maximize_ConsoleWindows()
        {
            IntPtr hWnd = GetConsoleWindow();
            ShowWindow(hWnd, SW_MAXIMIZE);
            Console.ReadKey();
        }
    }
}
