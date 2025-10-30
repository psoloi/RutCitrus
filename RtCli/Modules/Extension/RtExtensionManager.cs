using global::RtExtensionManager;
using System;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using RtCli.Modules;

//namespace RtCli.Modules.Extension
namespace RtExtensionManager
{
    /// <summary>
    /// 运行时扩展管理器
    /// </summary>
    public static class RtExtensionManager
    {
        private static readonly string ExtensionsDirectory = "Extensions";
        private static readonly string absoluteExtensionsPath = Path.GetFullPath(ExtensionsDirectory);
        private static readonly Dictionary<string, ExtensionContext> _loadedExtensions = new Dictionary<string, ExtensionContext>();
        private static bool _isInitialized = false;

        /// <summary>
        /// 已加载的插件信息
        /// </summary>
        public static IReadOnlyDictionary<string, ExtensionInfo> LoadedExtensions =>
            _loadedExtensions.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Info);

        /// <summary>
        /// 初始化扩展管理器
        /// </summary>
        private static void Initialize()
        {
            if (_isInitialized) return;

            // 创建扩展目录（如果不存在）
            if (!Directory.Exists(absoluteExtensionsPath))
            {
                Directory.CreateDirectory(absoluteExtensionsPath);
                Output.Log($"创建扩展目录: {Path.GetFullPath(absoluteExtensionsPath)}", 1, "RtExtensionManager");
            }

            _isInitialized = true;
        }

        /// <summary>
        /// 加载所有扩展
        /// </summary>
        public static void LoadAll()
        {
            Initialize();
            Output.Log("开始加载所有扩展...", 1, "RtExtensionManager");

            // 清空已加载的扩展
            UnloadAll();

            // 查找扩展目录中的所有DLL文件
            var dllFiles = Directory.GetFiles(absoluteExtensionsPath, "*.dll", SearchOption.AllDirectories);

            if (dllFiles.Length == 0)
            {
                Output.Log("未找到任何扩展文件", 1, "RtExtensionManager");
                return;
            }

            int loadedCount = 0;
            foreach (var dllPath in dllFiles)
            {
                try
                {
                    if (LoadExtension(dllPath))
                    {
                        loadedCount++;
                    }
                }
                catch (Exception ex)
                {
                    Output.Log($"加载扩展失败 {Path.GetFileName(dllPath)}: {ex.Message}", 3, "RtExtensionManager");
                }
            }

            Output.Log($"扩展加载完成。成功: {loadedCount}, 总数: {dllFiles.Length}", 1, "RtExtensionManager");
        }

        /// <summary>
        /// 加载单个扩展
        /// </summary>
        /// <param name="assemblyPath">程序集路径</param>
        /// <returns>是否加载成功</returns>
        private static bool LoadExtension(string assemblyPath)
        {
            try
            {
                // 创建可卸载的加载上下文
                var context = new ExtensionLoadContext(assemblyPath);
                Assembly assembly = context.LoadFromAssemblyPath(assemblyPath);

                // 查找实现IExtension接口的类型
                var extensionTypes = assembly.GetTypes()
                    .Where(t => typeof(IExtension).IsAssignableFrom(t) &&
                               !t.IsInterface && !t.IsAbstract);

                if (!extensionTypes.Any())
                {
                    Output.Log($"程序集 {Path.GetFileName(assemblyPath)} 中未找到实现IExtension接口的类型", 2, "RtExtensionManager");
                    context.Unload();
                    return false;
                }

                foreach (var type in extensionTypes)
                {
                    try
                    {
                        var extension = (IExtension)Activator.CreateInstance(type);

                        // 调用加载方法
                        extension.Load();

                        var extensionKey = $"{extension.Name}_{extension.Version}";
                        var info = new ExtensionInfo
                        {
                            Name = extension.Name,
                            Version = extension.Version,
                            Description = extension.Description,
                            AssemblyPath = assemblyPath,
                            TypeName = type.FullName,
                            IsLoaded = true,
                            LoadTime = DateTime.Now
                        };

                        _loadedExtensions[extensionKey] = new ExtensionContext
                        {
                            Context = context,
                            Extension = extension,
                            Info = info
                        };

                        Output.Log($"[green]√[/] 加载扩展成功: {extension.Name} Ver:{extension.Version}", 1, "RtExtensionManager");
                        Output.Log($"   描述: {extension.Description}", 1, "RtExtensionManager");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Output.Log($"创建扩展实例失败 {type.FullName}: {ex.Message}", 1, "RtExtensionManager");
                        context.Unload();
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Output.Log($"加载扩展程序集失败 {Path.GetFileName(assemblyPath)}: {ex.Message}", 1, "RtExtensionManager");
                return false;
            }

            return false;
        }

        /// <summary>
        /// 运行所有已加载的扩展
        /// </summary>
        public static void Run()
        {
            if (_loadedExtensions.Count == 0)
            {
                Output.Log("没有可运行的扩展", 1, "RtExtensionManager");
                return;
            }

            Output.Log($"开始运行 {_loadedExtensions.Count} 个扩展...", 1, "RtExtensionManager");

            foreach (var kvp in _loadedExtensions)
            {
                var context = kvp.Value;
                try
                {
                    Output.Log($"》 运行扩展: {context.Info.Name}", 1, "RtExtensionManager");
                    context.Extension.Run();
                }
                catch (Exception ex)
                {
                    Output.Log($"× 运行扩展失败 {context.Info.Name}: {ex.Message}", 1, "RtExtensionManager");
                }
            }

            Output.Log("扩展运行完成", 1, "RtExtensionManager");
        }

        /// <summary>
        /// 卸载所有扩展
        /// </summary>
        public static void UnloadAll()
        {
            if (_loadedExtensions.Count == 0)
            {
                Output.Log("没有需要卸载的扩展", 1, "RtExtensionManager");
                return;
            }

            Output.Log($"开始卸载 {_loadedExtensions.Count} 个扩展...", 1, "RtExtensionManager");

            var keys = _loadedExtensions.Keys.ToList();
            int unloadedCount = 0;

            foreach (var key in keys)
            {
                if (UnloadExtension(key))
                {
                    unloadedCount++;
                }
            }

            Output.Log($"扩展卸载完成。成功: {unloadedCount}, 总数: {keys.Count}", 1, "RtExtensionManager");
        }

        /// <summary>
        /// 卸载单个扩展
        /// </summary>
        /// <param name="extensionKey">扩展键</param>
        /// <returns>是否卸载成功</returns>
        private static bool UnloadExtension(string extensionKey)
        {
            if (_loadedExtensions.TryGetValue(extensionKey, out var context))
            {
                try
                {
                    // 调用扩展的卸载方法
                    context.Extension.Unload();

                    // 卸载加载上下文
                    context.Context.Unload();

                    _loadedExtensions.Remove(extensionKey);

                    // 强制垃圾回收以完成卸载
                    GC.Collect();
                    GC.WaitForPendingFinalizers();

                    Output.Log($"√ 卸载扩展成功: {context.Info.Name}", 1, "RtExtensionManager");
                    return true;
                }
                catch (Exception ex)
                {
                    Output.Log($"× 卸载扩展失败 {context.Info.Name}: {ex.Message}", 1, "RtExtensionManager");
                    return false;
                }
            }
            return false;
        }

        /// <summary>
        /// 重新加载所有扩展
        /// </summary>
        public static void Reload()
        {
            Output.Log("开始重新加载所有扩展...", 1, "RtExtensionManager");
            UnloadAll();
            LoadAll();
        }

        /// <summary>
        /// 显示已加载的扩展信息
        /// </summary>
        public static void DisplayLoadedExtensions()
        {
            if (_loadedExtensions.Count == 0)
            {
                Output.Log("没有已加载的扩展", 1, "RtExtensionManager");
                return;
            }

            Output.Log($"\n* - 已加载的扩展 ({_loadedExtensions.Count} 个):", 1, "RtExtensionManager");
            Output.Log(new string('=', 60), 1, "RtExtensionManager");

            foreach (var kvp in _loadedExtensions)
            {
                var info = kvp.Value.Info;
                Output.Log($"[[]] {info.Name} Ver:{info.Version}", 1, "RtExtensionManager");
                Output.Log($"   描述: {info.Description}", 1, "RtExtensionManager");
                Output.Log($"   程序集: {Path.GetFileName(info.AssemblyPath)}", 1, "RtExtensionManager");
                Output.Log($"   加载时间: {info.LoadTime:yyyy-MM-dd HH:mm:ss}", 1, "RtExtensionManager");
                Console.WriteLine();
            }
        }

        /// <summary>
        /// 扩展上下文
        /// </summary>
        private class ExtensionContext
        {
            public ExtensionLoadContext? Context { get; set; }
            public IExtension Extension { get; set; }
            public ExtensionInfo Info { get; set; }
        }
    }

    /// <summary>
    /// 可卸载的扩展加载上下文
    /// </summary>
    internal class ExtensionLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public ExtensionLoadContext(string pluginPath) : base(isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            string assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath != null)
            {
                return LoadFromAssemblyPath(assemblyPath);
            }

            return null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (libraryPath != null)
            {
                return LoadUnmanagedDllFromPath(libraryPath);
            }

            return IntPtr.Zero;
        }
    }
}
