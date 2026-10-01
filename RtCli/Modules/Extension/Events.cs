using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RtCli.Modules;
using RtCli.Modules.Unit;

namespace RtCli.Modules.Extension
{
    /// <summary>
    /// 事件名工具: 内置事件清单、事件名校验、合法事件列表生成
    /// 合法事件名 = 内置事件类名 + "CustomPlayerEvent"(通配全部自定义玩家事件) + player_event.customs中定义的键
    /// </summary>
    public static class EventNames
    {
        /// <summary>CustomPlayerEvent字面量，作为通配名订阅全部自定义玩家事件</summary>
        public const string CustomPlayerEventName = "CustomPlayerEvent";

        /// <summary>全部内置事件类名(不含CustomPlayerEvent通配名)</summary>
        public static readonly string[] BuiltInEvents =
        {
            "ModeSelectedEvent", "ProgramStartupEvent", "ProgramShutdownEvent",
            "ExtensionLoadEvent", "ExtensionUnloadEvent",
            "ServerStartEvent", "ServerStopEvent", "ServerDoneEvent", "ServerCrashEvent",
            "ServerStopUnresponsiveEvent", "AutoRestartEvent",
            "BackupStartEvent", "BackupCompleteEvent",
            "TaskExecuteEvent", "SchedulerStartEvent", "SchedulerStopEvent",
            "CommandExecuteEvent", "ConfigReloadEvent",
            "PlayerJoinEvent", "PlayerConnectEvent", "PlayerLostEvent", "PlayerLeaveEvent",
            "PlayerCommandEvent", "PlayerChatEvent", "PlayerSetModeEvent",
            "PlayerOpEvent", "PlayerBanEvent",
            "LuckPermsChangeEvent",
            "PacketInitializeEvent", "PacketStopEvent", "PacketClientEvent", "PacketServerEvent",
            "ServerStatusEvent",
        };

        /// <summary>
        /// 判断事件名是否合法: 内置事件类名 / CustomPlayerEvent / player_event.customs中定义的键
        /// </summary>
        public static bool IsValidEventName(string? eventName)
        {
            if (string.IsNullOrWhiteSpace(eventName))
                return false;
            if (Array.IndexOf(BuiltInEvents, eventName) >= 0)
                return true;
            if (string.Equals(eventName, CustomPlayerEventName, StringComparison.OrdinalIgnoreCase))
                return true;
            var customs = Config.App.PlayerEvent?.Customs;
            return customs != null && customs.ContainsKey(eventName);
        }

        /// <summary>
        /// 判断是否为自定义玩家事件名(CustomPlayerEvent通配名或customs键)，需要订阅CustomPlayerEvent
        /// </summary>
        public static bool IsCustomPlayerEventName(string? eventName)
        {
            if (string.IsNullOrWhiteSpace(eventName))
                return false;
            if (Array.IndexOf(BuiltInEvents, eventName) >= 0)
                return false;
            // CustomPlayerEvent通配名或player_event.customs中定义的键
            return string.Equals(eventName, CustomPlayerEventName, StringComparison.OrdinalIgnoreCase)
                || (Config.App.PlayerEvent?.Customs?.ContainsKey(eventName) ?? false);
        }

        /// <summary>
        /// 合法事件列表描述(内置事件 + 当前配置的自定义事件)，用于错误提示
        /// </summary>
        public static string GetValidEventsDescription()
        {
            var names = new List<string>(BuiltInEvents) { CustomPlayerEventName };
            var customs = Config.App.PlayerEvent?.Customs;
            if (customs != null)
                names.AddRange(customs.Keys);
            return string.Join(" / ", names);
        }
    }

    /// <summary>
    /// 事件变量提取: 把事件对象携带的字段转为 {rt.xxx} 变量字典,
    /// 供调度器/AI 触发条件表达式求值(统一维护, 避免多处重复)。
    /// 通用变量: event_name / event_time; 其余为各事件自身的字段(snake_case)。
    /// </summary>
    public static class EventVariables
    {
        public static Dictionary<string, string> Extract(RtEvent? e)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (e == null) return dict;

            dict["event_name"] = e.EventName;
            dict["event_time"] = e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");

            switch (e)
            {
                case ModeSelectedEvent v: dict["mode"] = v.Mode; break;
                case ProgramStartupEvent v: dict["args"] = string.Join(" ", v.Args ?? Array.Empty<string>()); break;
                case ProgramShutdownEvent v: dict["reason"] = v.Reason; break;
                case ExtensionLoadEvent v: dict["extension_name"] = v.ExtensionName; dict["version"] = v.Version; break;
                case ExtensionUnloadEvent v: dict["extension_name"] = v.ExtensionName; break;
                case AiActivityEvent v: dict["kind"] = v.Kind; dict["task_key"] = v.TaskKey; dict["detail"] = v.Detail; dict["success"] = v.Success.ToString().ToLowerInvariant(); break;
                case ServerStartEvent v: dict["server_key"] = v.ServerKey; dict["port"] = v.Port.ToString(); break;
                case ServerStopEvent v: dict["server_key"] = v.ServerKey; break;
                case ServerDoneEvent v: dict["server_key"] = v.ServerKey; dict["raw_message"] = v.RawMessage; break;
                case ServerCrashEvent v: dict["server_key"] = v.ServerKey; dict["exit_code"] = v.ExitCode.ToString(); break;
                case ServerStopUnresponsiveEvent v: dict["server_key"] = v.ServerKey; dict["had_log_activity"] = v.HadLogActivity.ToString().ToLowerInvariant(); dict["waited_seconds"] = v.WaitedSeconds.ToString(); break;
                case AutoRestartEvent v: dict["server_key"] = v.ServerKey; dict["attempt_count"] = v.AttemptCount.ToString(); dict["max_retries"] = v.MaxRetries.ToString(); break;
                case BackupStartEvent v: dict["server_key"] = v.ServerKey; break;
                case BackupCompleteEvent v: dict["server_key"] = v.ServerKey; dict["backup_path"] = v.BackupPath; dict["size_bytes"] = v.SizeBytes.ToString(); break;
                case TaskExecuteEvent v: dict["task_name"] = v.TaskName; dict["execute_content"] = v.ExecuteContent; break;
                case CommandExecuteEvent v: dict["command"] = v.Command; dict["args"] = string.Join(" ", v.Args ?? Array.Empty<string>()); break;
                case LuckPermsChangeEvent v:
                    dict["actor_uuid"] = v.ActorUuid; dict["actor_name"] = v.ActorName; dict["type"] = v.Type;
                    dict["acted_uuid"] = v.ActedUuid; dict["acted_name"] = v.ActedName; dict["action"] = v.Action;
                    break;
                case PlayerConnectEvent v: dict["player_name"] = v.PlayerName; dict["player_trigger_time"] = v.PlayerTriggerTime; dict["player_ip"] = v.PlayerIp; break;
                case PlayerLostEvent v: dict["player_name"] = v.PlayerName; dict["player_trigger_time"] = v.PlayerTriggerTime; dict["player_lost_reason"] = v.PlayerLostReason; break;
                case PlayerJoinEvent v: dict["player_name"] = v.PlayerName; dict["player_trigger_time"] = v.PlayerTriggerTime; break;
                case PlayerLeaveEvent v: dict["player_name"] = v.PlayerName; dict["player_trigger_time"] = v.PlayerTriggerTime; break;
                case PlayerCommandEvent v: dict["player_name"] = v.PlayerName; dict["player_trigger_time"] = v.PlayerTriggerTime; dict["command"] = v.Command; break;
                case PlayerChatEvent v: dict["player_name"] = v.PlayerName; dict["player_trigger_time"] = v.PlayerTriggerTime; dict["message"] = v.Message; break;
                case PlayerSetModeEvent v: dict["player_name"] = v.PlayerName; dict["player_trigger_time"] = v.PlayerTriggerTime; dict["player_mode"] = v.PlayerMode; break;
                case PlayerOpEvent v: dict["player_name"] = v.PlayerName; dict["player_trigger_time"] = v.PlayerTriggerTime; dict["granted"] = v.Granted.ToString().ToLowerInvariant(); dict["source"] = v.Source; break;
                case PlayerBanEvent v: dict["target"] = v.Target; dict["ban_type"] = v.BanType; dict["banned"] = v.Banned.ToString().ToLowerInvariant(); dict["reason"] = v.Reason; dict["player_trigger_time"] = v.PlayerTriggerTime; break;
                case CustomPlayerEvent v:
                    dict["player_name"] = v.PlayerName; dict["player_trigger_time"] = v.PlayerTriggerTime;
                    foreach (var kvp in v.Parameters) dict[kvp.Key] = kvp.Value;
                    break;
                case PacketInitializeEvent v: dict["message"] = v.Message; break;
                case PacketStopEvent v: dict["message"] = v.Message; break;
                case PacketClientEvent v: dict["tick_ms"] = v.TickMs.ToString(); dict["packet_count"] = v.PacketCount.ToString(); dict["json"] = v.Json; break;
                case PacketServerEvent v: dict["tick_ms"] = v.TickMs.ToString(); dict["packet_count"] = v.PacketCount.ToString(); dict["json"] = v.Json; break;
                case ServerStatusEvent v:
                    dict["server_count"] = v.Servers.Count.ToString();
                    dict["online_count"] = v.Servers.Count(s => s.Online).ToString();
                    dict["offline_count"] = v.Servers.Count(s => !s.Online).ToString();
                    dict["servers_json"] = v.Json;
                    foreach (var s in v.Servers)
                    {
                        // 每个服务器一组变量: server_<标识>_online / _players / _max_players / _latency_ms / _version / _error
                        var prefix = "server_" + s.Id + "_";
                        dict[prefix + "online"] = s.Online ? "true" : "false";
                        dict[prefix + "players"] = s.OnlinePlayers.ToString();
                        dict[prefix + "max_players"] = s.MaxPlayers.ToString();
                        dict[prefix + "latency_ms"] = s.LatencyMs.ToString();
                        dict[prefix + "version"] = s.Version;
                        dict[prefix + "error"] = s.Error;
                    }
                    break;
            }

            return dict;
        }
    }

    public abstract class RtEvent
    {
        public virtual string EventName => GetType().Name;
        public DateTime Timestamp { get; } = DateTime.Now;
        public bool IsCancelled { get; set; }
    }

    public delegate void RtEventHandler<TEvent>(TEvent e) where TEvent : RtEvent;

    public static class EventBus
    {
        private static readonly Dictionary<Type, List<Delegate>> _handlers = new();
        private static readonly Dictionary<string, HashSet<Delegate>> _extensionHandlers = new();
        private static readonly object _lock = new();

        public static void Subscribe<TEvent>(RtEventHandler<TEvent> handler, string? extensionName = null) where TEvent : RtEvent
        {
            if (handler == null) return;

            lock (_lock)
            {
                var eventType = typeof(TEvent);
                if (!_handlers.ContainsKey(eventType))
                {
                    _handlers[eventType] = new List<Delegate>();
                }
                _handlers[eventType].Add(handler);

                if (!string.IsNullOrEmpty(extensionName))
                {
                    if (!_extensionHandlers.ContainsKey(extensionName!))
                    {
                        _extensionHandlers[extensionName!] = new HashSet<Delegate>();
                    }
                    _extensionHandlers[extensionName!].Add(handler);
                }
            }
        }

        public static void Unsubscribe<TEvent>(RtEventHandler<TEvent> handler) where TEvent : RtEvent
        {
            if (handler == null) return;

            lock (_lock)
            {
                var eventType = typeof(TEvent);
                if (_handlers.ContainsKey(eventType))
                {
                    _handlers[eventType].Remove(handler);
                    if (_handlers[eventType].Count == 0)
                    {
                        _handlers.Remove(eventType);
                    }
                }

                foreach (var kvp in _extensionHandlers)
                {
                    kvp.Value.Remove(handler);
                }
            }
        }

        public static void UnsubscribeAll(string extensionName)
        {
            if (string.IsNullOrEmpty(extensionName)) return;

            lock (_lock)
            {
                if (!_extensionHandlers.TryGetValue(extensionName, out var handlers))
                    return;

                foreach (var handler in handlers.ToList())
                {
                    foreach (var handlerList in _handlers.Values)
                    {
                        handlerList.Remove(handler);
                    }
                }

                var emptyKeys = _handlers.Where(kvp => kvp.Value.Count == 0).Select(kvp => kvp.Key).ToList();
                foreach (var key in emptyKeys)
                {
                    _handlers.Remove(key);
                }

                _extensionHandlers.Remove(extensionName);
            }
        }

        public static void Publish<TEvent>(TEvent e) where TEvent : RtEvent
        {
            if (e == null) return;

            List<Delegate> handlersCopy;
            lock (_lock)
            {
                if (!_handlers.TryGetValue(typeof(TEvent), out var delegates))
                    return;
                handlersCopy = new List<Delegate>(delegates);
            }

            foreach (var del in handlersCopy)
            {
                if (del is RtEventHandler<TEvent> handler)
                {
                    try
                    {
                        handler(e);
                    }
                    catch (Exception ex)
                    {
                        Output.Log($"事件处理器执行异常 [{e.EventName}]: {ex.Message}", 3, "EventBus");
                    }
                }
            }
        }

        public static int GetHandlerCount<TEvent>() where TEvent : RtEvent
        {
            lock (_lock)
            {
                return _handlers.TryGetValue(typeof(TEvent), out var list) ? list.Count : 0;
            }
        }

        /// <summary>内置事件 类名→类型 映射(仅取现有具体事件类)</summary>
        private static readonly Dictionary<string, Type> _builtInEventTypes = BuildBuiltInEventTypes();

        private static Dictionary<string, Type> BuildBuiltInEventTypes()
        {
            var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            var asm = typeof(RtEvent).Assembly;
            foreach (var name in EventNames.BuiltInEvents)
            {
                var t = asm.GetType(typeof(RtEvent).Namespace + "." + name);
                if (t != null && typeof(RtEvent).IsAssignableFrom(t))
                    map[name] = t;
            }
            return map;
        }

        /// <summary>
        /// 按事件名订阅(供调度器/AI 触发条件表达式使用)。
        /// 支持内置事件类名、CustomPlayerEvent 通配名、player_event.customs 中定义的自定义事件键;
        /// 自定义事件键会订阅 CustomPlayerEvent 并按 EventName 过滤。返回是否订阅成功。
        /// subId(即扩展名) 可用于 UnsubscribeAll 批量退订。
        /// </summary>
        public static bool SubscribeByName(string eventName, Action<RtEvent> handler, string? subId = null)
        {
            if (handler == null || string.IsNullOrWhiteSpace(eventName)) return false;

            bool filterByName;
            Type? eventType;
            if (EventNames.IsCustomPlayerEventName(eventName))
            {
                eventType = typeof(CustomPlayerEvent);
                filterByName = !string.Equals(eventName, EventNames.CustomPlayerEventName, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                _builtInEventTypes.TryGetValue(eventName, out eventType);
                filterByName = false;
            }

            if (eventType == null) return false;

            Action<RtEvent> wrapped = filterByName
                ? (e => { if (e == null || string.Equals(e.EventName, eventName, StringComparison.OrdinalIgnoreCase)) handler(e); })
                : handler;

            try
            {
                var mi = typeof(EventBus).GetMethod(nameof(SubscribeByNameGeneric), BindingFlags.NonPublic | BindingFlags.Static);
                mi?.MakeGenericMethod(eventType).Invoke(null, new object?[] { wrapped, subId });
                return true;
            }
            catch (Exception ex)
            {
                Output.Log($"按名订阅事件失败 [{eventName}]: {ex.Message}", 3, "EventBus");
                return false;
            }
        }

        private static void SubscribeByNameGeneric<TEvent>(Action<RtEvent> handler, string? subId) where TEvent : RtEvent
            => Subscribe<TEvent>(e => handler(e), subId);
    }

    public class ModeSelectedEvent : RtEvent
    {
        public string Mode { get; }
        public ModeSelectedEvent(string mode) { Mode = mode; }
    }

    public class ProgramStartupEvent : RtEvent
    {
        public string[] Args { get; }
        public ProgramStartupEvent(string[] args) { Args = args; }
    }

    public class ProgramShutdownEvent : RtEvent
    {
        public string Reason { get; }
        public ProgramShutdownEvent(string reason = "") { Reason = reason; }
    }

    public class ExtensionLoadEvent : RtEvent
    {
        public string ExtensionName { get; }
        public string Version { get; }
        public ExtensionLoadEvent(string name, string version)
        {
            ExtensionName = name;
            Version = version;
        }
    }

    /// <summary>
    /// AI 自动化活动: 任务开始/完成/失败、工具调用、运行器启停。
    /// 事件板据此展示"AI 到底做了什么"(否则 AI 的活动在面板里完全不可见)。
    /// </summary>
    public class AiActivityEvent : RtEvent
    {
        /// <summary>类别: runner / task_start / task_done / task_fail / tool_call</summary>
        public string Kind { get; }
        public string TaskKey { get; }
        public string Detail { get; }
        public bool Success { get; }

        public AiActivityEvent(string kind, string taskKey, string detail, bool success = true)
        {
            Kind = kind ?? "";
            TaskKey = taskKey ?? "";
            Detail = detail ?? "";
            Success = success;
        }
    }

    public class ExtensionUnloadEvent : RtEvent
    {
        public string ExtensionName { get; }
        public ExtensionUnloadEvent(string name) { ExtensionName = name; }
    }

    public class ServerStartEvent : RtEvent
    {
        public int Port { get; set; }
        public string ServerKey { get; set; }
        public ServerStartEvent(int port = 0, string serverKey = "") { Port = port; ServerKey = serverKey; }
    }

    public class ServerStopEvent : RtEvent
    {
        public string ServerKey { get; set; }
        public ServerStopEvent(string serverKey = "") { ServerKey = serverKey; }
    }

    public class ServerDoneEvent : RtEvent
    {
        public string ServerKey { get; set; }
        public string RawMessage { get; set; }
        public ServerDoneEvent(string serverKey = "", string rawMessage = "") { ServerKey = serverKey; RawMessage = rawMessage; }
    }

    public class ServerCrashEvent : RtEvent
    {
        public string ServerKey { get; set; }
        public int ExitCode { get; set; }
        public ServerCrashEvent(string serverKey = "", int exitCode = -1) { ServerKey = serverKey; ExitCode = exitCode; }
    }

    /// <summary>stop 指令无响应(无日志流/进程未退出), 提示用户可强制关闭</summary>
    public class ServerStopUnresponsiveEvent : RtEvent
    {
        public string ServerKey { get; set; }
        public bool HadLogActivity { get; set; }
        public int WaitedSeconds { get; set; }
        public ServerStopUnresponsiveEvent(string serverKey = "", bool hadLogActivity = false, int waitedSeconds = 0)
        {
            ServerKey = serverKey;
            HadLogActivity = hadLogActivity;
            WaitedSeconds = waitedSeconds;
        }
    }

    public class AutoRestartEvent : RtEvent
    {
        public string ServerKey { get; set; }
        public int AttemptCount { get; set; }
        public int MaxRetries { get; set; }
        public AutoRestartEvent(string serverKey = "", int attemptCount = 0, int maxRetries = 0) { ServerKey = serverKey; AttemptCount = attemptCount; MaxRetries = maxRetries; }
    }

    public class BackupStartEvent : RtEvent
    {
        public string ServerKey { get; set; }
        public BackupStartEvent(string serverKey = "") { ServerKey = serverKey; }
    }

    public class BackupCompleteEvent : RtEvent
    {
        public string ServerKey { get; set; }
        public string BackupPath { get; set; }
        public long SizeBytes { get; set; }
        public BackupCompleteEvent(string serverKey = "", string backupPath = "", long sizeBytes = 0) { ServerKey = serverKey; BackupPath = backupPath; SizeBytes = sizeBytes; }
    }

    public class TaskExecuteEvent : RtEvent
    {
        public string TaskName { get; set; }
        public string ExecuteContent { get; set; }
        public TaskExecuteEvent(string taskName = "", string executeContent = "") { TaskName = taskName; ExecuteContent = executeContent; }
    }

    public class SchedulerStartEvent : RtEvent
    {
        public SchedulerStartEvent() { }
    }

    public class SchedulerStopEvent : RtEvent
    {
        public SchedulerStopEvent() { }
    }

    public class CommandExecuteEvent : RtEvent
    {
        public string Command { get; }
        public string[] Args { get; }
        public CommandExecuteEvent(string command, string[] args)
        {
            Command = command;
            Args = args;
        }
    }

    public class ConfigReloadEvent : RtEvent
    {
        public ConfigReloadEvent() { }
    }

    #region 玩家事件

    public class PlayerJoinEvent : RtEvent
    {
        public string PlayerName { get; set; } = "";
        public string PlayerTriggerTime { get; set; } = "";
        public PlayerJoinEvent(string playerName = "", string triggerTime = "")
        { PlayerName = playerName; PlayerTriggerTime = triggerTime; }
    }

    public class PlayerConnectEvent : RtEvent
    {
        public string PlayerName { get; set; } = "";
        public string PlayerTriggerTime { get; set; } = "";
        public string PlayerIp { get; set; } = "";
        public PlayerConnectEvent(string playerName = "", string triggerTime = "", string playerIp = "")
        { PlayerName = playerName; PlayerTriggerTime = triggerTime; PlayerIp = playerIp; }
    }

    public class PlayerLostEvent : RtEvent
    {
        public string PlayerName { get; set; } = "";
        public string PlayerTriggerTime { get; set; } = "";
        public string PlayerLostReason { get; set; } = "";
        public PlayerLostEvent(string playerName = "", string triggerTime = "", string lostReason = "")
        { PlayerName = playerName; PlayerTriggerTime = triggerTime; PlayerLostReason = lostReason; }
    }

    public class PlayerLeaveEvent : RtEvent
    {
        public string PlayerName { get; set; } = "";
        public string PlayerTriggerTime { get; set; } = "";
        public PlayerLeaveEvent(string playerName = "", string triggerTime = "")
        { PlayerName = playerName; PlayerTriggerTime = triggerTime; }
    }

    public class PlayerCommandEvent : RtEvent
    {
        public string PlayerName { get; set; } = "";
        public string PlayerTriggerTime { get; set; } = "";
        public string Command { get; set; } = "";
        public PlayerCommandEvent(string playerName = "", string triggerTime = "", string command = "")
        { PlayerName = playerName; PlayerTriggerTime = triggerTime; Command = command; }
    }

    public class PlayerChatEvent : RtEvent
    {
        public string PlayerName { get; set; } = "";
        public string PlayerTriggerTime { get; set; } = "";
        public string Message { get; set; } = "";
        public PlayerChatEvent(string playerName = "", string triggerTime = "", string message = "")
        { PlayerName = playerName; PlayerTriggerTime = triggerTime; Message = message; }
    }

    public class PlayerSetModeEvent : RtEvent
    {
        public string PlayerName { get; set; } = "";
        public string PlayerTriggerTime { get; set; } = "";
        public string PlayerMode { get; set; } = "";
        public PlayerSetModeEvent(string playerName = "", string triggerTime = "", string playerMode = "")
        { PlayerName = playerName; PlayerTriggerTime = triggerTime; PlayerMode = playerMode; }
    }

    /// <summary>
    /// 自定义玩家事件，Parameters包含用户定义的参数键值对
    /// </summary>
    public class CustomPlayerEvent : RtEvent
    {
        private readonly string _customEventName;
        public override string EventName => _customEventName;
        public string PlayerName { get; set; } = "";
        public string PlayerTriggerTime { get; set; } = "";
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
        public CustomPlayerEvent(string eventName = "CustomPlayerEvent", string playerName = "", string triggerTime = "")
        { _customEventName = eventName; PlayerName = playerName; PlayerTriggerTime = triggerTime; }
    }

    /// <summary>
    /// 玩家 OP 变更事件(RM 管理协议 notification:operators/added|removed)
    /// </summary>
    public class PlayerOpEvent : RtEvent
    {
        public string PlayerName { get; set; } = "";
        /// <summary>true=添加OP, false=移除OP</summary>
        public bool Granted { get; set; }
        public string Source { get; set; } = "";
        public string PlayerTriggerTime { get; set; } = "";
        public PlayerOpEvent(string playerName = "", bool granted = true, string source = "", string triggerTime = "")
        { PlayerName = playerName; Granted = granted; Source = source; PlayerTriggerTime = triggerTime; }
    }

    /// <summary>
    /// 玩家/IP 封禁变更事件(RM 管理协议 notification:bans/* 与 notification:ip_bans/*)
    /// </summary>
    public class PlayerBanEvent : RtEvent
    {
        /// <summary>被封禁对象(玩家名或 IP)</summary>
        public string Target { get; set; } = "";
        /// <summary>ban=玩家封禁, ip_ban=IP封禁</summary>
        public string BanType { get; set; } = "ban";
        /// <summary>true=封禁, false=解封</summary>
        public bool Banned { get; set; }
        public string Reason { get; set; } = "";
        public string PlayerTriggerTime { get; set; } = "";
        public PlayerBanEvent(string target = "", string banType = "ban", bool banned = true, string reason = "", string triggerTime = "")
        { Target = target; BanType = banType; Banned = banned; Reason = reason; PlayerTriggerTime = triggerTime; }
    }

    #endregion

    #region LuckPerms 事件

    /// <summary>
    /// LuckPerms 权限变更事件 - 当 LuckPerms actions 表出现新记录时触发
    /// 变量: actor_uuid, actor_name, type, acted_uuid, acted_name, action
    /// </summary>
    public class LuckPermsChangeEvent : RtEvent
    {
        public string ActorUuid { get; set; }
        public string ActorName { get; set; }
        public string Type { get; set; }
        public string ActedUuid { get; set; }
        public string ActedName { get; set; }
        public string Action { get; set; }

        public LuckPermsChangeEvent(string actorUuid, string actorName, string type,
            string actedUuid, string actedName, string action)
        {
            ActorUuid = actorUuid ?? "";
            ActorName = actorName ?? "";
            Type = type ?? "";
            ActedUuid = actedUuid ?? "";
            ActedName = actedName ?? "";
            Action = action ?? "";
        }
    }

    #endregion

    #region 数据包事件(PacketEvent)

    /// <summary>
    /// 数据包事件功能初始化启动后触发。
    /// </summary>
    public class PacketInitializeEvent : RtEvent
    {
        public string Message { get; set; }
        public PacketInitializeEvent(string message = "") { Message = message; }
    }

    /// <summary>
    /// 数据包事件功能关闭时触发。
    /// </summary>
    public class PacketStopEvent : RtEvent
    {
        public string Message { get; set; }
        public PacketStopEvent(string message = "") { Message = message; }
    }

    /// <summary>
    /// 当一个 tick 周期内客户端→服务端数据包发生变化时触发。
    /// Json 为解码后的 MC 数据包 JSON 详细信息(数组)。
    /// </summary>
    public class PacketClientEvent : RtEvent
    {
        /// <summary>本次事件对应的检测触发周期(毫秒)</summary>
        public int TickMs { get; set; }
        /// <summary>本周期内发生变化的客户端数据包数量</summary>
        public int PacketCount { get; set; }
        /// <summary>解码后的 MC 数据包 JSON 详细信息</summary>
        public string Json { get; set; }
        public PacketClientEvent(int tickMs = 0, int packetCount = 0, string json = "")
        { TickMs = tickMs; PacketCount = packetCount; Json = json ?? ""; }
    }

    /// <summary>
    /// 当一个 tick 周期内服务端→客户端数据包发生变化时触发。
    /// Json 为解码后的 MC 数据包 JSON 详细信息(数组)。
    /// </summary>
    public class PacketServerEvent : RtEvent
    {
        /// <summary>本次事件对应的检测触发周期(毫秒)</summary>
        public int TickMs { get; set; }
        /// <summary>本周期内发生变化的服务端数据包数量</summary>
        public int PacketCount { get; set; }
        /// <summary>解码后的 MC 数据包 JSON 详细信息</summary>
        public string Json { get; set; }
        public PacketServerEvent(int tickMs = 0, int packetCount = 0, string json = "")
        { TickMs = tickMs; PacketCount = packetCount; Json = json ?? ""; }
    }

    #endregion

    #region MC 服务器状态事件(ServerStatus)

    /// <summary>
    /// 单个 MC 服务器的状态查询结果。
    /// </summary>
    public class ServerStatusInfo
    {
        /// <summary>服务器标识(来自 rt_config status_query.servers[].id)</summary>
        public string Id { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        /// <summary>是否在线(SLP 查询成功)</summary>
        public bool Online { get; set; }
        /// <summary>查询耗时(毫秒)</summary>
        public int LatencyMs { get; set; }
        /// <summary>服务端版本</summary>
        public string Version { get; set; } = "";
        public int OnlinePlayers { get; set; }
        public int MaxPlayers { get; set; }
        public string Motd { get; set; } = "";
        /// <summary>离线时的错误原因</summary>
        public string Error { get; set; } = "";
    }

    /// <summary>
    /// MC 服务器状态事件 - 按 rt_config 的 status_query.interval_seconds 周期触发。
    /// Servers 返回所有被监测服务器的标识与状态信息列表;
    /// 变量: server_count, online_count, offline_count, servers_json,
    /// server_&lt;id&gt;_online/_players/_max_players/_latency_ms/_version/_error
    /// </summary>
    public class ServerStatusEvent : RtEvent
    {
        public List<ServerStatusInfo> Servers { get; set; } = new();
        /// <summary>全部结果的 JSON(camelCase), 便于 AI/脚本直接使用</summary>
        public string Json { get; set; } = "";

        public ServerStatusEvent(List<ServerStatusInfo> servers, string json = "")
        {
            Servers = servers ?? new List<ServerStatusInfo>();
            Json = json ?? "";
        }
    }

    #endregion
}
