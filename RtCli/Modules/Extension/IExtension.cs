using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

//namespace RtCli.Modules.Extension
namespace RtExtensionManager
{
    /// <summary>
    /// 插件接口定义
    /// </summary>
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
}
