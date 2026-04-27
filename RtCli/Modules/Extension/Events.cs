using System;
using System.Collections.Generic;
using System.Linq;
using RtCli.Modules;

namespace RtCli.Modules.Extension
{
    public abstract class RtEvent
    {
        public string EventName => GetType().Name;
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
        public ServerStartEvent(int port = 0) { Port = port; }
    }

    public class ServerStopEvent : RtEvent
    {
        public ServerStopEvent() { }
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
}
