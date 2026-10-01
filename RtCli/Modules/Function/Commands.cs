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

        /// <summary>
        /// 面板命令处理器: arg 为命令名之后的参数原文(精确匹配命令为空串)。
        /// 返回 (是否成功, 结果摘要, 需广播到面板的纯文本或 null)。
        /// 处理器只做状态变更与控制台输出, 不自行广播——广播由分发器在锁外统一完成。
        /// </summary>
        public delegate (bool Success, string Result, string? PanelText) PanelCommandHandler(string arg);

        // needsLock: true = 会修改共享状态(配置/调度器/AI运行器/进程), 需在命令锁内串行;
        //            false = 只读查询, 锁只保护共享状态变更, 只读命令不再排队。
        private static readonly Dictionary<string, (PanelCommandHandler Handler, bool NeedsLock)> _panelExact =
            new Dictionary<string, (PanelCommandHandler, bool)>(StringComparer.OrdinalIgnoreCase);

        private static readonly List<(string Command, PanelCommandHandler Handler, bool NeedsLock)> _panelPrefix =
            new List<(string, PanelCommandHandler, bool)>();

        /// <summary>注册面板命令(精确匹配, 如 "rt status")</summary>
        public static void RegisterPanelCommand(string command, PanelCommandHandler handler, bool needsLock = true)
        {
            if (string.IsNullOrWhiteSpace(command) || handler == null) return;
            _panelExact[command.Trim()] = (handler, needsLock);
        }

        /// <summary>注册面板前缀命令(匹配 "命令名 + 空格 + 参数", 如 ".server change")</summary>
        public static void RegisterPanelPrefixCommand(string command, PanelCommandHandler handler, bool needsLock = true)
        {
            if (string.IsNullOrWhiteSpace(command) || handler == null) return;
            string name = command.Trim();
            _panelPrefix.RemoveAll(e => string.Equals(e.Command, name, StringComparison.OrdinalIgnoreCase));
            _panelPrefix.Add((name, handler, needsLock));
            // 长命令优先, 避免 ".fx filter" 抢在 ".fx filter config" 之前
            _panelPrefix.Sort((a, b) => b.Command.Length.CompareTo(a.Command.Length));
        }

        /// <summary>
        /// 解析面板命令: 精确名优先, 其次按最长前缀匹配 "命令名 参数"。
        /// 前缀匹配要求命令名之后紧跟空格, 与旧的 StartsWith("命令名 ") 语义一致。
        /// </summary>
        public static bool TryResolvePanelCommand(string input, out PanelCommandHandler? handler, out string arg, out bool needsLock)
        {
            handler = null;
            arg = "";
            needsLock = true;
            if (string.IsNullOrWhiteSpace(input)) return false;

            string cmd = input.Trim();

            if (_panelExact.TryGetValue(cmd, out var exact))
            {
                handler = exact.Handler;
                needsLock = exact.NeedsLock;
                return true;
            }

            foreach (var (command, h, lockIt) in _panelPrefix)
            {
                if (cmd.Length > command.Length &&
                    cmd.StartsWith(command, StringComparison.OrdinalIgnoreCase) &&
                    cmd[command.Length] == ' ')
                {
                    handler = h;
                    needsLock = lockIt;
                    arg = cmd.Substring(command.Length + 1).Trim();
                    return true;
                }
            }
            return false;
        }

        /// <summary>CLI 命令分发结果</summary>
        public enum CliOutcome
        {
            /// <summary>没有匹配到已注册命令</summary>
            Unhandled,
            /// <summary>命令已处理</summary>
            Handled,
            /// <summary>命令已处理, 且请求退出程序(rt end)</summary>
            Exit
        }

        /// <summary>CLI 命令处理器: 入参为完整命令行原文, 返回处理结果</summary>
        public delegate CliOutcome CliCommandHandler(string cmd);

        // CLI 用序数比较: 与旧 switch 的 cmd == "X" / cmd.StartsWith("X ") 语义严格一致
        private static readonly Dictionary<string, CliCommandHandler> _cliExact =
            new Dictionary<string, CliCommandHandler>(StringComparer.Ordinal);

        private static readonly List<(string Command, CliCommandHandler Handler)> _cliPrefix =
            new List<(string, CliCommandHandler)>();

        /// <summary>注册 CLI 精确命令(如 "rt status" / ".help")</summary>
        public static void RegisterCliCommand(string command, CliCommandHandler handler)
        {
            if (string.IsNullOrWhiteSpace(command) || handler == null) return;
            _cliExact[command.Trim()] = handler;
        }

        /// <summary>注册 CLI 前缀命令(匹配 "命令名 + 空格 + 参数", 如 ".server change")</summary>
        public static void RegisterCliPrefixCommand(string command, CliCommandHandler handler)
        {
            if (string.IsNullOrWhiteSpace(command) || handler == null) return;
            string name = command.Trim();
            _cliPrefix.RemoveAll(x => string.Equals(x.Command, name, StringComparison.Ordinal));
            _cliPrefix.Add((name, handler));
            _cliPrefix.Sort((a, b) => b.Command.Length.CompareTo(a.Command.Length));
        }

        /// <summary>仅解析(不执行), 供测试断言命令归属</summary>
        public static bool TryResolveCliCommand(string input, out CliCommandHandler? handler)
        {
            handler = null;
            if (string.IsNullOrWhiteSpace(input)) return false;
            string cmd = input.Trim();
            if (_cliExact.TryGetValue(cmd, out var exact)) { handler = exact; return true; }
            foreach (var (command, h) in _cliPrefix)
            {
                if (cmd.Length > command.Length &&
                    cmd.StartsWith(command, StringComparison.Ordinal) &&
                    cmd[command.Length] == ' ')
                {
                    handler = h;
                    return true;
                }
            }
            return false;
        }

        /// <summary>解析并执行 CLI 命令(精确优先, 其次最长前缀)</summary>
        public static CliOutcome TryDispatchCli(string input)
            => TryResolveCliCommand(input, out var handler) ? handler!(input.Trim()) : CliOutcome.Unhandled;

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

        /// <summary>
        /// 从完整输入中解析命令与参数并执行。
        /// 采用最长前缀匹配：例如输入 "rte get 127.0.0.1:25565" 时，
        /// 依次尝试 "rte get 127.0.0.1:25565" → "rte get" → "rte"，
        /// 命中 "rte get" 后将 ["127.0.0.1:25565"] 作为参数传入。
        /// </summary>
        /// <param name="input">用户完整输入</param>
        /// <returns>是否找到并执行了命令</returns>
        public static bool TryExecuteWithArgs(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return false;

            var parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return false;

            // 从最长到最短依次尝试匹配
            for (int len = parts.Length; len >= 1; len--)
            {
                string candidate = string.Join(' ', parts.Take(len)).ToLower();
                if (_commands.TryGetValue(candidate, out var handler))
                {
                    string[] args = parts.Skip(len).ToArray();
                    handler.Invoke(args);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 判断完整输入是否能匹配到某个已注册命令(含参数前缀匹配)。
        /// </summary>
        public static bool HasCommandWithArgs(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return false;

            var parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int len = parts.Length; len >= 1; len--)
            {
                string candidate = string.Join(' ', parts.Take(len)).ToLower();
                if (_commands.ContainsKey(candidate))
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
    }
}
