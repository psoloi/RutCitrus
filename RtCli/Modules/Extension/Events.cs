using System;
using System.Collections.Generic;
using System.Linq;
using RtCli.Modules;

namespace RtCli.Modules.Extension
{
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
}
