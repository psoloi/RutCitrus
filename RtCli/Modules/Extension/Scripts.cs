using CSScriptLib;
using RtCli.Modules;
using RtCli.Modules.Extension;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Spectre.Console;

namespace RtCli.Modules.Extension
{
    /// <summary>
    /// 脚本配置项
    /// </summary>
    public class ScriptItem
    {
        /// <summary>
        /// 脚本文件路径（相对于Scripts目录）
        /// </summary>
        public string File { get; set; } = "";

        /// <summary>
        /// 触发加载/执行的事件名称
        /// </summary>
        public string TriggerEvent { get; set; } = "";

        /// <summary>
        /// 是否启用
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 传入脚本的参数
        /// </summary>
        public string Input { get; set; } = "";
    }

    /// <summary>
    /// 脚本配置
    /// </summary>
    public class ScriptsSettings
    {
        /// <summary>
        /// 脚本项字典，键为脚本名称
        /// </summary>
        public Dictionary<string, ScriptItem> Scripts { get; set; } = new();
    }

    /// <summary>
    /// 脚本引擎，支持C#(.cs)和Python(.py)脚本的加载与事件触发执行
    /// </summary>
    internal class Scripts
    {
        private const string ThisName = "Scripts";
        private static readonly string ScriptsDirectory = Path.Combine("Content", "Scripts");
        private static readonly string AbsoluteScriptsPath = Path.GetFullPath(ScriptsDirectory);
        private static ScriptsSettings _settings = new();
        private static readonly Dictionary<string, dynamic> _loadedCsScripts = new();
        private static bool _isInitialized = false;

        private static void Log(string msg, int msgType, string name)
        {
            Output.Log(Markup.Escape(msg), msgType, name);
        }

        /// <summary>
        /// 获取当前脚本配置
        /// </summary>
        public static ScriptsSettings GetSettings() => _settings;

        /// <summary>
        /// 获取脚本目录绝对路径
        /// </summary>
        public static string GetScriptsDirectory() => AbsoluteScriptsPath;

        /// <summary>
        /// 初始化脚本系统，加载配置并注册事件订阅
        /// </summary>
        public static void Initialize()
        {
            if (_isInitialized) return;

            if (!Directory.Exists(AbsoluteScriptsPath))
            {
                Directory.CreateDirectory(AbsoluteScriptsPath);
                Log($"创建脚本目录: {AbsoluteScriptsPath}", 1, ThisName);
            }

            // 生成示例脚本文件
            string exampleCsPath = Path.Combine(AbsoluteScriptsPath, "example.cs");
            if (!File.Exists(exampleCsPath))
            {
                File.WriteAllText(exampleCsPath, @"using System;
using System.Text.Json;

public class Script
{
    public void Execute(string input = """", string eventName = """")
    {
        Console.WriteLine($""[Example CS] event={eventName}"");

        // 玩家事件触发时, input为JSON格式的事件参数
        if (!string.IsNullOrEmpty(input) && input.StartsWith(""{""))
        {
            try
            {
                using var doc = JsonDocument.Parse(input);
                if (doc.RootElement.TryGetProperty(""player_name"", out var nameEl))
                    Console.WriteLine($""  玩家: {nameEl.GetString()}"");
                if (doc.RootElement.TryGetProperty(""player_trigger_time"", out var timeEl))
                    Console.WriteLine($""  时间: {timeEl.GetString()}"");
            }
            catch { }
        }
        else if (!string.IsNullOrEmpty(input))
        {
            Console.WriteLine($""  input={input}"");
        }
    }
}
");
                Log($"创建示例C#脚本: {exampleCsPath}", 1, ThisName);
            }

            string examplePyPath = Path.Combine(AbsoluteScriptsPath, "example.py");
            if (!File.Exists(examplePyPath))
            {
                File.WriteAllText(examplePyPath, @"import sys

def main():
    input_arg = """"
    event_name = """"

    # Parse arguments
    args = sys.argv[1:]
    i = 0
    while i < len(args):
        if args[i] == ""--event"" and i + 1 < len(args):
            event_name = args[i + 1]
            i += 2
        else:
            input_arg = args[i]
            i += 1

    print(f""[Example Py] input={input_arg}, event={event_name}"")

if __name__ == ""__main__"":
    main()
");
                Log($"创建示例Python脚本: {examplePyPath}", 1, ThisName);
            }

            _isInitialized = true;
        }

        /// <summary>
        /// 加载脚本配置并注册事件触发器
        /// </summary>
        public static void LoadSettingsAndSubscribe()
        {
            _settings = RtCli.Modules.Unit.ContentManager.LoadScriptsSettings();

            if (_settings.Scripts.Count == 0)
            {
                Log("没有已配置的脚本", 1, ThisName);
                return;
            }

            int subscribedCount = 0;
            foreach (var kvp in _settings.Scripts)
            {
                string scriptName = kvp.Key;
                ScriptItem item = kvp.Value;

                if (!item.Enabled)
                {
                    Log($"已禁用脚本: {scriptName}", 1, ThisName);
                    continue;
                }

                if (string.IsNullOrEmpty(item.File))
                {
                    Log($"无法加载脚本 {scriptName} 其未指定文件路径", 2, ThisName);
                    continue;
                }

                string extension = Path.GetExtension(item.File).ToLower();
                if (extension != ".cs" && extension != ".py")
                {
                    Log($"脚本 {scriptName} 不支持的文件类型: {extension}（仅支持 .cs 和 .py）", 2, ThisName);
                    continue;
                }

                if (string.IsNullOrEmpty(item.TriggerEvent))
                {
                    Log($"脚本 {scriptName} 未指定触发事件，将在启动时加载", 1, ThisName);
                    ExecuteScript(scriptName, item);
                    subscribedCount++;
                    continue;
                }

                SubscribeToEvent(scriptName, item);
                subscribedCount++;
            }

            Log($"脚本加载完成，已注册 {subscribedCount} 个脚本", 1, ThisName);
        }

        /// <summary>
        /// 根据事件名称订阅脚本执行
        /// </summary>
        private static void SubscribeToEvent(string scriptName, ScriptItem item)
        {
            var eventMap = new Dictionary<string, Action<RtEvent>>
            {
                { "ModeSelectedEvent", e => ExecuteScript(scriptName, item, e) },
                { "ProgramStartupEvent", e => ExecuteScript(scriptName, item, e) },
                { "ProgramShutdownEvent", e => ExecuteScript(scriptName, item, e) },
                { "ExtensionLoadEvent", e => ExecuteScript(scriptName, item, e) },
                { "ExtensionUnloadEvent", e => ExecuteScript(scriptName, item, e) },
                { "ServerStartEvent", e => ExecuteScript(scriptName, item, e) },
                { "ServerStopEvent", e => ExecuteScript(scriptName, item, e) },
                { "ServerDoneEvent", e => ExecuteScript(scriptName, item, e) },
                { "ServerCrashEvent", e => ExecuteScript(scriptName, item, e) },
                { "AutoRestartEvent", e => ExecuteScript(scriptName, item, e) },
                { "BackupStartEvent", e => ExecuteScript(scriptName, item, e) },
                { "BackupCompleteEvent", e => ExecuteScript(scriptName, item, e) },
                { "TaskExecuteEvent", e => ExecuteScript(scriptName, item, e) },
                { "SchedulerStartEvent", e => ExecuteScript(scriptName, item, e) },
                { "SchedulerStopEvent", e => ExecuteScript(scriptName, item, e) },
                { "CommandExecuteEvent", e => ExecuteScript(scriptName, item, e) },
                { "ConfigReloadEvent", e => ExecuteScript(scriptName, item, e) },
                { "PlayerJoinEvent", e => ExecuteScript(scriptName, item, e) },
                { "PlayerConnectEvent", e => ExecuteScript(scriptName, item, e) },
                { "PlayerLostEvent", e => ExecuteScript(scriptName, item, e) },
                { "PlayerLeaveEvent", e => ExecuteScript(scriptName, item, e) },
                { "PlayerCommandEvent", e => ExecuteScript(scriptName, item, e) },
                { "PlayerChatEvent", e => ExecuteScript(scriptName, item, e) },
                { "PlayerSetModeEvent", e => ExecuteScript(scriptName, item, e) },
                { "CustomPlayerEvent", e => ExecuteScript(scriptName, item, e) },
            };

            // 支持自定义事件名（player_event.customs中定义的事件名）
            if (!eventMap.ContainsKey(item.TriggerEvent))
            {
                eventMap[item.TriggerEvent] = e => ExecuteScript(scriptName, item, e);
            }

            if (!eventMap.TryGetValue(item.TriggerEvent, out var handler))
            {
                Log($"脚本 {scriptName} 的事件 {item.TriggerEvent} 未找到，可用事件: {string.Join(", ", eventMap.Keys)}", 2, ThisName);
                return;
            }

            switch (item.TriggerEvent)
            {
                case "ModeSelectedEvent":
                    EventBus.Subscribe<ModeSelectedEvent>(e => handler(e), ThisName);
                    break;
                case "ProgramStartupEvent":
                    EventBus.Subscribe<ProgramStartupEvent>(e => handler(e), ThisName);
                    break;
                case "ProgramShutdownEvent":
                    EventBus.Subscribe<ProgramShutdownEvent>(e => handler(e), ThisName);
                    break;
                case "ExtensionLoadEvent":
                    EventBus.Subscribe<ExtensionLoadEvent>(e => handler(e), ThisName);
                    break;
                case "ExtensionUnloadEvent":
                    EventBus.Subscribe<ExtensionUnloadEvent>(e => handler(e), ThisName);
                    break;
                case "ServerStartEvent":
                    EventBus.Subscribe<ServerStartEvent>(e => handler(e), ThisName);
                    break;
                case "ServerStopEvent":
                    EventBus.Subscribe<ServerStopEvent>(e => handler(e), ThisName);
                    break;
                case "ServerDoneEvent":
                    EventBus.Subscribe<ServerDoneEvent>(e => handler(e), ThisName);
                    break;
                case "ServerCrashEvent":
                    EventBus.Subscribe<ServerCrashEvent>(e => handler(e), ThisName);
                    break;
                case "AutoRestartEvent":
                    EventBus.Subscribe<AutoRestartEvent>(e => handler(e), ThisName);
                    break;
                case "BackupStartEvent":
                    EventBus.Subscribe<BackupStartEvent>(e => handler(e), ThisName);
                    break;
                case "BackupCompleteEvent":
                    EventBus.Subscribe<BackupCompleteEvent>(e => handler(e), ThisName);
                    break;
                case "TaskExecuteEvent":
                    EventBus.Subscribe<TaskExecuteEvent>(e => handler(e), ThisName);
                    break;
                case "SchedulerStartEvent":
                    EventBus.Subscribe<SchedulerStartEvent>(e => handler(e), ThisName);
                    break;
                case "SchedulerStopEvent":
                    EventBus.Subscribe<SchedulerStopEvent>(e => handler(e), ThisName);
                    break;
                case "CommandExecuteEvent":
                    EventBus.Subscribe<CommandExecuteEvent>(e => handler(e), ThisName);
                    break;
                case "ConfigReloadEvent":
                    EventBus.Subscribe<ConfigReloadEvent>(e => handler(e), ThisName);
                    break;
                case "PlayerJoinEvent":
                    EventBus.Subscribe<PlayerJoinEvent>(e => handler(e), ThisName);
                    break;
                case "PlayerConnectEvent":
                    EventBus.Subscribe<PlayerConnectEvent>(e => handler(e), ThisName);
                    break;
                case "PlayerLostEvent":
                    EventBus.Subscribe<PlayerLostEvent>(e => handler(e), ThisName);
                    break;
                case "PlayerLeaveEvent":
                    EventBus.Subscribe<PlayerLeaveEvent>(e => handler(e), ThisName);
                    break;
                case "PlayerCommandEvent":
                    EventBus.Subscribe<PlayerCommandEvent>(e => handler(e), ThisName);
                    break;
                case "PlayerChatEvent":
                    EventBus.Subscribe<PlayerChatEvent>(e => handler(e), ThisName);
                    break;
                case "PlayerSetModeEvent":
                    EventBus.Subscribe<PlayerSetModeEvent>(e => handler(e), ThisName);
                    break;
                case "CustomPlayerEvent":
                default:
                    // 自定义事件名和CustomPlayerEvent都通过CustomPlayerEvent订阅
                    EventBus.Subscribe<CustomPlayerEvent>(e => handler(e), ThisName);
                    break;
            }

            Log($"脚本 {scriptName} 已订阅事件: {item.TriggerEvent}", 1, ThisName);
        }

        /// <summary>
        /// 获取事件名称对应的Type
        /// </summary>
        private static Type GetEventType(string eventName)
        {
            return eventName switch
            {
                "ModeSelectedEvent" => typeof(ModeSelectedEvent),
                "ProgramStartupEvent" => typeof(ProgramStartupEvent),
                "ProgramShutdownEvent" => typeof(ProgramShutdownEvent),
                "ExtensionLoadEvent" => typeof(ExtensionLoadEvent),
                "ExtensionUnloadEvent" => typeof(ExtensionUnloadEvent),
                "ServerStartEvent" => typeof(ServerStartEvent),
                "ServerStopEvent" => typeof(ServerStopEvent),
                "ServerDoneEvent" => typeof(ServerDoneEvent),
                "ServerCrashEvent" => typeof(ServerCrashEvent),
                "AutoRestartEvent" => typeof(AutoRestartEvent),
                "BackupStartEvent" => typeof(BackupStartEvent),
                "BackupCompleteEvent" => typeof(BackupCompleteEvent),
                "TaskExecuteEvent" => typeof(TaskExecuteEvent),
                "SchedulerStartEvent" => typeof(SchedulerStartEvent),
                "SchedulerStopEvent" => typeof(SchedulerStopEvent),
                "CommandExecuteEvent" => typeof(CommandExecuteEvent),
                "ConfigReloadEvent" => typeof(ConfigReloadEvent),
                "PlayerJoinEvent" => typeof(PlayerJoinEvent),
                "PlayerConnectEvent" => typeof(PlayerConnectEvent),
                "PlayerLostEvent" => typeof(PlayerLostEvent),
                "PlayerLeaveEvent" => typeof(PlayerLeaveEvent),
                "PlayerCommandEvent" => typeof(PlayerCommandEvent),
                "PlayerChatEvent" => typeof(PlayerChatEvent),
                "PlayerSetModeEvent" => typeof(PlayerSetModeEvent),
                "CustomPlayerEvent" => typeof(CustomPlayerEvent),
                _ => typeof(CustomPlayerEvent) // 自定义事件名也映射为CustomPlayerEvent
            };
        }

        /// <summary>
        /// 执行脚本（无事件触发）
        /// </summary>
        public static void ExecuteScript(string scriptName, ScriptItem item)
        {
            ExecuteScript(scriptName, item, null);
        }

        public static bool ExecuteScriptByName(string scriptName)
        {
            if (_settings.Scripts.TryGetValue(scriptName, out var item))
            {
                if (!item.Enabled)
                {
                    Log($"脚本 {scriptName} 已禁用", 2, ThisName);
                    return false;
                }

                ExecuteScript(scriptName, item);
                return true;
            }
            else
            {
                Log($"未找到脚本: {scriptName}", 2, ThisName);
                return false;
            }
        }

        /// <summary>
        /// 执行脚本
        /// </summary>
        public static void ExecuteScript(string scriptName, ScriptItem item, RtEvent? eventArgs)
        {
            string filePath = ResolveScriptPath(item.File);
            if (!File.Exists(filePath))
            {
                Log($"脚本文件不存在: {filePath}", 2, ThisName);
                return;
            }

            string extension = Path.GetExtension(filePath).ToLower();

            try
            {
                switch (extension)
                {
                    case ".cs":
                        ExecuteCsScript(scriptName, filePath, item, eventArgs);
                        break;
                    case ".py":
                        ExecutePyScript(scriptName, filePath, item, eventArgs);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"脚本执行失败 {scriptName}: {ex.Message}", 3, ThisName);
            }
        }

        /// <summary>
        /// 解析脚本文件路径
        /// </summary>
        private static string ResolveScriptPath(string file)
        {
            if (Path.IsPathRooted(file))
                return file;

            return Path.Combine(AbsoluteScriptsPath, file);
        }

        /// <summary>
        /// 执行C#脚本（使用CS-Script）
        /// </summary>
        private static void ExecuteCsScript(string scriptName, string filePath, ScriptItem item, RtEvent? eventArgs)
        {
            if (!_loadedCsScripts.TryGetValue(scriptName, out var script))
            {
                script = CSScript.Evaluator
                    .ReferenceAssembly(Assembly.GetExecutingAssembly())
                    .ReferenceDomainAssemblies()
                    .LoadFile(filePath);

                _loadedCsScripts[scriptName] = script;
                Log($"C#脚本已加载: {scriptName}", 1, ThisName);
            }

            try
            {
                string inputArg = item.Input ?? "";
                string eventArg = eventArgs?.EventName ?? "";

                // 玩家事件触发时，将事件参数序列化为JSON作为input传入
                if (eventArgs != null && IsPlayerEvent(eventArgs))
                {
                    inputArg = SerializePlayerEvent(eventArgs);
                }

                var type = ((object)script).GetType();
                var method = type.GetMethod("Execute", new[] { typeof(string), typeof(string) });
                if (method != null)
                {
                    method.Invoke(script, new object[] { inputArg, eventArg });
                }
                else
                {
                    method = type.GetMethod("Execute", new[] { typeof(string) });
                    if (method != null)
                    {
                        method.Invoke(script, new object[] { inputArg });
                    }
                    else
                    {
                        method = type.GetMethod("Execute", Type.EmptyTypes);
                        if (method != null)
                        {
                            method.Invoke(script, null);
                        }
                        else
                        {
                            Log($"C#脚本 {scriptName} 未找到 Execute 方法", 3, ThisName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"C#脚本执行异常 {scriptName}: {ex.Message}", 3, ThisName);
            }
        }

        /// <summary>
        /// 执行Python脚本（使用Python解释器）
        /// </summary>
        private static void ExecutePyScript(string scriptName, string filePath, ScriptItem item, RtEvent? eventArgs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = "-X utf8 " + BuildPythonArgs(filePath, item, eventArgs),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            psi.Environment["PYTHONUTF8"] = "1";

            using var process = Process.Start(psi);
            if (process == null)
            {
                Log($"无法启动Python进程: {scriptName}", 3, ThisName);
                return;
            }

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (!string.IsNullOrEmpty(output))
            {
                Log($"[Python:{scriptName}] {output.Trim()}", 1, ThisName);
            }

            if (process.ExitCode != 0 && !string.IsNullOrEmpty(error))
            {
                Log($"Python脚本执行错误 {scriptName}: {error.Trim()}", 3, ThisName);
            }
        }

        /// <summary>
        /// 构建Python命令行参数
        /// </summary>
        private static string BuildPythonArgs(string filePath, ScriptItem item, RtEvent? eventArgs)
        {
            var sb = new StringBuilder();
            sb.Append($"\"{filePath}\"");

            // 玩家事件触发时，将事件参数JSON作为input传入
            string input = item.Input ?? "";
            if (eventArgs != null && IsPlayerEvent(eventArgs))
            {
                input = SerializePlayerEvent(eventArgs);
            }

            if (!string.IsNullOrEmpty(input))
            {
                sb.Append($" \"{input}\"");
            }

            if (eventArgs != null)
            {
                sb.Append($" --event {eventArgs.EventName}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 判断是否为玩家事件
        /// </summary>
        private static bool IsPlayerEvent(RtEvent e)
        {
            return e is PlayerJoinEvent or PlayerConnectEvent or PlayerLostEvent
                or PlayerLeaveEvent or PlayerCommandEvent or PlayerChatEvent
                or PlayerSetModeEvent or CustomPlayerEvent;
        }

        /// <summary>
        /// 将玩家事件参数序列化为JSON
        /// </summary>
        private static string SerializePlayerEvent(RtEvent e)
        {
            var dict = new Dictionary<string, string>();
            dict["event_name"] = e.EventName;

            switch (e)
            {
                case PlayerJoinEvent je:
                    dict["player_name"] = je.PlayerName;
                    dict["player_trigger_time"] = je.PlayerTriggerTime;
                    break;
                case PlayerConnectEvent ce:
                    dict["player_name"] = ce.PlayerName;
                    dict["player_trigger_time"] = ce.PlayerTriggerTime;
                    dict["player_ip"] = ce.PlayerIp;
                    break;
                case PlayerLostEvent le:
                    dict["player_name"] = le.PlayerName;
                    dict["player_trigger_time"] = le.PlayerTriggerTime;
                    dict["player_lost_reason"] = le.PlayerLostReason;
                    break;
                case PlayerLeaveEvent lve:
                    dict["player_name"] = lve.PlayerName;
                    dict["player_trigger_time"] = lve.PlayerTriggerTime;
                    break;
                case PlayerCommandEvent cme:
                    dict["player_name"] = cme.PlayerName;
                    dict["player_trigger_time"] = cme.PlayerTriggerTime;
                    dict["command"] = cme.Command;
                    break;
                case PlayerChatEvent che:
                    dict["player_name"] = che.PlayerName;
                    dict["player_trigger_time"] = che.PlayerTriggerTime;
                    dict["message"] = che.Message;
                    break;
                case PlayerSetModeEvent sme:
                    dict["player_name"] = sme.PlayerName;
                    dict["player_trigger_time"] = sme.PlayerTriggerTime;
                    dict["player_mode"] = sme.PlayerMode;
                    break;
                case CustomPlayerEvent cpe:
                    dict["player_name"] = cpe.PlayerName;
                    dict["player_trigger_time"] = cpe.PlayerTriggerTime;
                    foreach (var kvp in cpe.Parameters)
                        dict[kvp.Key] = kvp.Value;
                    break;
            }

            return System.Text.Json.JsonSerializer.Serialize(dict);
        }

        /// <summary>
        /// 卸载所有已加载的C#脚本
        /// </summary>
        public static void UnloadAll()
        {
            _loadedCsScripts.Clear();
            EventBus.UnsubscribeAll(ThisName);
            Log("所有脚本已卸载", 1, ThisName);
        }

        /// <summary>
        /// 重新加载脚本配置
        /// </summary>
        public static void Reload()
        {
            UnloadAll();
            _loadedCsScripts.Clear();
            LoadSettingsAndSubscribe();
        }
    }
}
