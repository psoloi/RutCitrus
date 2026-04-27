using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RtCli.Modules.Extension;

namespace RtExtensionManager
{
    public interface IExtension
    {
        /// <summary>
        /// 插件名称
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 插件版本
        /// </summary>
        string Version { get; }

        /// <summary>
        /// 插件描述
        /// </summary>
        string Description { get; }

        /// <summary>
        /// 加载插件
        /// </summary>
        void Load();

        /// <summary>
        /// 运行插件
        /// </summary>
        void Run();

        /// <summary>
        /// 卸载插件
        /// </summary>
        void Unload();
    }

    /// <summary>
    /// 插件信息
    /// </summary>
    public class ExtensionInfo
    {
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        public string? AssemblyPath { get; set; }
        public string? TypeName { get; set; }
        public bool IsLoaded { get; set; }
        public DateTime LoadTime { get; set; }
    }

    public abstract class ExtensionBase : IExtension
    {
        public abstract string Name { get; }
        public abstract string Version { get; }
        public abstract string Description { get; }

        private readonly List<Delegate> _registeredHandlers = new();

        protected void SubscribeEvent<TEvent>(RtEventHandler<TEvent> handler) where TEvent : RtEvent
        {
            _registeredHandlers.Add(handler);
            EventBus.Subscribe(handler, Name);
        }

        protected void UnsubscribeEvent<TEvent>(RtEventHandler<TEvent> handler) where TEvent : RtEvent
        {
            _registeredHandlers.Remove(handler);
            EventBus.Unsubscribe(handler);
        }

        public virtual void Load() { }
        public virtual void Run() { }

        public virtual void Unload()
        {
            _registeredHandlers.Clear();
            EventBus.UnsubscribeAll(Name);
        }
    }
}
