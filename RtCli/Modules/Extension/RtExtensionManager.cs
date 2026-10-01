using System;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using RtCli.Modules;
using RtCli.Modules.Function;

namespace RtCli.Modules.Extension
{
    /// <summary>
    /// 运行时扩展管理器
    /// </summary>
    public static class RtExtensionManager
    {
        private const string ThisName = "RtEM";
        private static readonly string ExtensionsDirectory = "Extensions";
        // 扩展目录锚定程序根目录, 所有扩展及其依赖的加载路径均限制在程序根目录内(安全校验要求)
        private static readonly string absoluteExtensionsPath =
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ExtensionsDirectory));
        private static readonly Dictionary<string, ExtensionContext> _loadedExtensions = new Dictionary<string, ExtensionContext>();
        private static bool _isInitialized = false;
        private static IReadOnlyDictionary<string, ExtensionInfo>? _cachedLoadedExtensions;
        private static readonly Dictionary<string, Task> _runningTasks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _taskLock = new object();
        private static readonly object _extensionsLock = new object();

        public static IReadOnlyDictionary<string, ExtensionInfo> LoadedExtensions
        {
            get
            {
                if (_cachedLoadedExtensions != null) return _cachedLoadedExtensions;
                lock (_extensionsLock)
                {
                    return _cachedLoadedExtensions ??=
                        _loadedExtensions.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Info);
                }
            }
        }

        private static void InvalidateLoadedExtensionsCache()
        {
            _cachedLoadedExtensions = null;
        }

        /// <summary>
        /// 初始化扩展管理器
        /// </summary>
        private static void Initialize()
        {
            if (_isInitialized) return;

            if (!Directory.Exists(absoluteExtensionsPath))
            {
                Directory.CreateDirectory(absoluteExtensionsPath);
                Output.Log($"创建扩展目录: {Path.GetFullPath(absoluteExtensionsPath)}", 1, ThisName);
            }

            _isInitialized = true;
        }

        /// <summary>
        /// 加载所有扩展
        /// </summary>
        public static void LoadAll()
        {
            Initialize();
            Output.Log("开始加载所有扩展...", 1, ThisName);

            // 防止神秘小漏洞的初始化-清空已加载的扩展
            UnloadAll();

            // DLL
            var dllFiles = Directory.GetFiles(absoluteExtensionsPath, "*.dll", SearchOption.AllDirectories);

            if (dllFiles.Length == 0)
            {
                Output.Log("未找到任何扩展文件", 1, ThisName);
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
                    Output.Log($"加载扩展失败 {Path.GetFileName(dllPath)}: {ex.Message}", 3, ThisName);
                }
            }

            Output.Log($"扩展加载完成。成功: {loadedCount}, 总数: {dllFiles.Length}", 1, ThisName);
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
                string normalizedPath = Path.GetFullPath(assemblyPath);

                string? alreadyLoadedName = null;
                lock (_extensionsLock)
                {
                    var dup = _loadedExtensions.FirstOrDefault(kvp =>
                        string.Equals(Path.GetFullPath(kvp.Value.Info.AssemblyPath ?? ""), normalizedPath, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(dup.Key))
                        alreadyLoadedName = $"{dup.Value.Info.Name} Ver:{dup.Value.Info.Version}";
                }

                if (alreadyLoadedName != null)
                {
                    Output.Log($"扩展已加载，取消加载: {alreadyLoadedName}", 2, ThisName);
                    return false;
                }

                // 加载前安全校验: 路径归属程序根目录、签名认证、程序集名称、deps.json 可执行文件拦截
                if (!Validator.ValidateExtension(normalizedPath, out string reason))
                {
                    Output.Log($"扩展安全校验未通过，已取消加载 {Path.GetFileName(normalizedPath)}: {reason}", 3, ThisName);
                    return false;
                }

                var context = new ExtensionLoadContext(normalizedPath);
                Assembly assembly = context.LoadFromAssemblyPath(normalizedPath);

                var extensionTypes = assembly.GetTypes()
                    .Where(t => typeof(IExtension).IsAssignableFrom(t) &&
                               !t.IsInterface && !t.IsAbstract);

                if (!extensionTypes.Any())
                {
                    Output.Log($"程序集 {Path.GetFileName(normalizedPath)} 中未找到实现IExtension接口的类型", 2, ThisName);
                    context.Unload();
                    return false;
                }

                foreach (var type in extensionTypes)
                {
                    try
                    {
                        var extension = (IExtension?)Activator.CreateInstance(type);
                        if (extension == null)
                        {
                            Output.Log($"创建扩展实例失败 {type.FullName}: 返回null", 2, ThisName);
                            continue;
                        }

                        var extensionKey = $"{extension.Name}_{extension.Version}";

                        lock (_extensionsLock)
                        {
                            if (_loadedExtensions.ContainsKey(extensionKey))
                            {
                                Output.Log($"扩展键已存在，取消加载: {extensionKey}", 2, ThisName);
                                continue;
                            }
                        }

                        extension.Load();

                        var info = new ExtensionInfo
                        {
                            Name = extension.Name,
                            Version = extension.Version,
                            Description = extension.Description,
                            AssemblyPath = normalizedPath,
                            TypeName = type.FullName,
                            IsLoaded = true,
                            LoadTime = DateTime.Now
                        };

                        lock (_extensionsLock)
                        {
                            if (!_loadedExtensions.ContainsKey(extensionKey))
                            {
                                _loadedExtensions[extensionKey] = new ExtensionContext
                                {
                                    Context = context,
                                    Extension = extension,
                                    Info = info
                                };
                                InvalidateLoadedExtensionsCache();
                            }
                        }

                        Output.Log($"[green]+[/] 加载扩展成功: {extension.Name} Ver:{extension.Version}", 1, ThisName);
                        Output.Log($"   描述: {extension.Description}", 1, ThisName);

                        EventBus.Publish(new ExtensionLoadEvent(extension.Name, extension.Version));
                    }
                    catch (Exception ex)
                    {
                        Output.Log($"创建扩展实例失败 {type.FullName}: {DescribeException(ex)}", 2, ThisName);
                        Output.Debug(ex, $"创建扩展实例异常堆栈: {type.FullName}", ThisName);
                    }
                }

                lock (_extensionsLock)
                {
                    return _loadedExtensions.Any(kvp => kvp.Value.Context == context);
                }
            }
            catch (Exception ex)
            {
                Output.Log($"加载扩展程序集失败 {Path.GetFileName(assemblyPath)}: {DescribeException(ex)}", 2, ThisName);
                Output.Debug(ex, $"加载扩展程序集异常堆栈: {assemblyPath}", ThisName);
                return false;
            }
        }

        /// <summary>
        /// 运行所有已加载的扩展
        /// </summary>
        public static void Run()
        {
            List<KeyValuePair<string, ExtensionContext>> extensions;
            lock (_extensionsLock)
            {
                extensions = _loadedExtensions.ToList();
            }

            if (extensions.Count == 0)
            {
                Output.Log("没有可运行的扩展", 1, ThisName);
                return;
            }

            Output.Log($"开始运行 {extensions.Count} 个扩展...", 1, ThisName);

            // 内存守护: 扩展占用超过 256MB 且持续 2 秒即强制卸载(阈值与语义见 Validator)
            ExtensionMemoryGuard.Start();

            foreach (var kv in extensions)
            {
                string extensionKey = kv.Key;
                var context = kv.Value;
                // 登记为"已启动扩展": 守护器据此冻结堆基线并持续计它的内存账, 直到卸载。
                // (扩展的 Run() 常常只做初始化就返回, 真正耗内存的是它自起的后台线程与事件回调)
                ExtensionMemoryGuard.RegisterActive(extensionKey);

                var task = Task.Run(() =>
                {
                    // MarkExecuting/MarkIdle 只在多扩展并发时用于归属
                    ExtensionMemoryGuard.MarkExecuting(extensionKey);
                    try
                    {
                        Output.Log($"》 运行扩展: {context.Info.Name}", 0, ThisName);
                        context.Extension.Run();
                    }
                    catch (Exception ex)
                    {
                        Output.Log($"× 运行扩展失败 {context.Info.Name}: {ex.Message}", 0, ThisName);
                    }
                    finally
                    {
                        ExtensionMemoryGuard.MarkIdle(extensionKey);
                    }
                });
                lock (_taskLock)
                {
                    _runningTasks[extensionKey] = task;
                }
            }

            Output.Log("所有扩展已启动", 1, ThisName);
        }

        /// <summary>
        /// 卸载所有扩展
        /// </summary>
        public static void UnloadAll()
        {
            // 卸载期间停止内存守护, 避免把卸载过程当成扩展内存增长
            ExtensionMemoryGuard.Stop();

            lock (_taskLock)
            {
                if (_runningTasks.Count > 0)
                {
                    try
                    {
                        Task.WaitAll(_runningTasks.Values.ToArray(), TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex) { Output.Debug(ex, "等待扩展任务退出超时或失败", "RtEM"); }
                    _runningTasks.Clear();
                }
            }

            List<string> keys;
            int totalCount;
            lock (_extensionsLock)
            {
                keys = _loadedExtensions.Keys.ToList();
                totalCount = _loadedExtensions.Count;
            }

            if (totalCount == 0)
            {
                Output.Log("没有需要卸载的扩展", 1, ThisName);
                return;
            }

            Output.Log($"开始卸载 {totalCount} 个扩展...", 1, ThisName);

            int unloadedCount = 0;

            foreach (var key in keys)
            {
                if (UnloadExtension(key))
                {
                    unloadedCount++;
                }
            }

            Output.Log($"扩展卸载成功: {unloadedCount}, 总数: {keys.Count}", 1, ThisName);

            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        /// <summary>
        /// 卸载单个扩展
        /// </summary>
        /// <param name="extensionKey">扩展键</param>
        /// <returns>是否卸载成功</returns>
        private static bool UnloadExtension(string extensionKey)
        {
            ExtensionContext? context;
            lock (_extensionsLock)
            {
                if (!_loadedExtensions.TryGetValue(extensionKey, out context))
                    return false;
            }

            string extensionName = context.Info.Name ?? "";
            string typeName = context.Info.TypeName ?? "";

            // 可选生命周期: 扩展实现 IStoppableExtension 时先请求停止自建线程/定时器
            if (context.Extension is IStoppableExtension stoppable)
            {
                try { stoppable.RequestStop(); }
                catch (Exception ex) { Output.Debug(ex, $"请求扩展停止失败: {extensionName}", ThisName); }
            }

            // 每一步独立兜底: 某一步抛异常不能导致后续 Unload/Context.Unload 被跳过
            try { EventBus.Publish(new ExtensionUnloadEvent(extensionName)); }
            catch (Exception ex) { Output.Debug(ex, $"发布扩展卸载事件失败: {extensionName}", ThisName); }

            // 清理该扩展的事件订阅(按名称前缀与类型名, 避免静态处理器残留持有程序集)
            try { EventBus.UnsubscribeAll(extensionName); }
            catch (Exception ex) { Output.Debug(ex, $"退订扩展事件失败: {extensionName}", ThisName); }
            if (!string.IsNullOrEmpty(typeName))
            {
                try { EventBus.UnsubscribeAll(typeName); }
                catch (Exception ex) { Output.Debug(ex, $"退订扩展事件失败: {typeName}", ThisName); }
            }

            try
            {
                context.Extension.Unload();
            }
            catch (Exception ex)
            {
                Output.Log($"× 扩展 Unload() 失败 {extensionName}: {DescribeException(ex)}", 3, ThisName);
                Output.Debug(ex, $"扩展 Unload() 异常堆栈: {extensionName}", ThisName);
            }

            // 等待该扩展的 Run() 任务退出(最多 2 秒), 避免卸载程序集时它仍在执行
            Task? runTask = null;
            lock (_taskLock)
            {
                if (_runningTasks.TryGetValue(extensionKey, out var task))
                {
                    runTask = task;
                    _runningTasks.Remove(extensionKey);
                }
            }
            if (runTask != null && !runTask.IsCompleted)
            {
                try
                {
                    if (!runTask.Wait(TimeSpan.FromSeconds(2)))
                        Output.Log($"扩展 {extensionName} 的 Run() 任务未在 2 秒内退出(可能仍持有自建线程)", 2, ThisName);
                }
                catch (Exception ex) { Output.Debug(ex, $"等待扩展任务退出失败: {extensionName}", ThisName); }
            }

            try
            {
                // 卸载加载上下文(对持有静态引用的扩展无效, 但能释放多数程序集)
                context.Context.Unload();

                lock (_extensionsLock)
                {
                    _loadedExtensions.Remove(extensionKey);
                    InvalidateLoadedExtensionsCache();
                }

                Output.Log($"- 卸载扩展成功: {extensionName}", 1, ThisName);
                return true;
            }
            catch (Exception ex)
            {
                Output.Log($"× 卸载扩展失败 {extensionName}: {DescribeException(ex)}", 1, ThisName);
                Output.Debug(ex, $"卸载扩展异常堆栈: {extensionName}", ThisName);
                return false;
            }
        }

        /// <summary>展开 TargetInvocationException 等包装异常, 取到真正的内层异常信息</summary>
        private static string DescribeException(Exception ex)
        {
            while (ex is System.Reflection.TargetInvocationException && ex.InnerException != null)
                ex = ex.InnerException;
            return $"{ex.GetType().Name}: {ex.Message}";
        }

        /// <summary>
        /// 重新加载所有扩展
        /// </summary>
        public static void Reload()
        {
            Output.Log("开始重新加载所有扩展...", 1, ThisName);
            UnloadAll();
            LoadAll();
        }

        /// <summary>
        /// 显示已加载的扩展信息
        /// </summary>
        public static void DisplayLoadedExtensions()
        {
            List<KeyValuePair<string, ExtensionContext>> snapshot;
            lock (_extensionsLock)
            {
                snapshot = _loadedExtensions.ToList();
            }

            if (snapshot.Count == 0)
            {
                Output.Log("没有已加载的扩展", 1, ThisName);
                return;
            }

            Output.Log($"* - 已加载的扩展 ({snapshot.Count} 个):", 1, ThisName);
            Output.Log(new string('=', 60), 1, ThisName);

            foreach (var kvp in snapshot)
            {
                var info = kvp.Value.Info;
                Output.Log($"[[#]] {info.Name} Ver:{info.Version}", 1, ThisName);
                Output.Log($"     Key: {kvp.Key}", 1, ThisName);
                Output.Log($"     描述: {info.Description}", 1, ThisName);
                Output.Log($"     程序集: {Path.GetFileName(info.AssemblyPath)}", 1, ThisName);
                Output.Log($"     加载时间: {info.LoadTime:yyyy-MM-dd HH:mm:ss}", 1, ThisName);

            }
        }

        /// <summary>
        /// 获取扩展列表JSON
        /// </summary>
        public static string GetExtensionsJson()
        {
            List<object> snapshot;
            lock (_extensionsLock)
            {
                snapshot = _loadedExtensions.Select(kvp => new
                {
                    Key = kvp.Key,
                    Name = kvp.Value.Info.Name,
                    Version = kvp.Value.Info.Version,
                    Description = kvp.Value.Info.Description,
                    LoadTime = kvp.Value.Info.LoadTime.ToString("yyyy-MM-dd HH:mm:ss")
                }).ToList<object>();
            }

            return Newtonsoft.Json.JsonConvert.SerializeObject(snapshot);
        }

        /// <summary>
        /// 卸载指定扩展（公开方法）
        /// </summary>
        public static bool UnloadExtensionByKey(string extensionKey)
        {
            if (string.IsNullOrWhiteSpace(extensionKey))
            {
                Output.Log("扩展Key不能为空", 2, ThisName);
                return false;
            }

            bool exists;
            string[] availableKeys;
            lock (_extensionsLock)
            {
                exists = _loadedExtensions.ContainsKey(extensionKey);
                availableKeys = _loadedExtensions.Keys.ToArray();
            }

            if (!exists)
            {
                Output.Log($"未找到扩展: {extensionKey}", 2, ThisName);
                Output.Log("可用的扩展Key:", 1, ThisName);
                foreach (var key in availableKeys)
                {
                    Output.Log($"  - {key}", 1, ThisName);
                }
                return false;
            }

            return UnloadExtension(extensionKey);
        }

        /// <summary>
        /// 加载指定扩展（公开方法）
        /// </summary>
        public static bool LoadExtensionByKey(string extensionPath)
        {
            Initialize();
            
            if (string.IsNullOrWhiteSpace(extensionPath))
            {
                Output.Log("扩展路径不能为空", 2, ThisName);
                return false;
            }

            string fullPath;
            if (Path.IsPathRooted(extensionPath))
            {
                fullPath = extensionPath;
            }
            else
            {
                fullPath = Path.Combine(absoluteExtensionsPath, extensionPath);
            }

            if (!File.Exists(fullPath))
            {
                Output.Log($"扩展文件不存在: {fullPath}", 2, ThisName);
                return false;
            }

            if (!fullPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                Output.Log("扩展文件必须是 .dll 格式", 2, ThisName);
                return false;
            }

            return LoadExtension(fullPath);
        }

        /// <summary>
        /// 获取扩展目录路径
        /// </summary>
        public static string GetExtensionsDirectory() => absoluteExtensionsPath;

        /// <summary>
        /// 获取扩展数量
        /// </summary>
        public static int GetExtensionCount()
        {
            lock (_extensionsLock) return _loadedExtensions.Count;
        }

        /// <summary>
        /// 扩展上下文
        /// </summary>
        private class ExtensionContext
        {
            public required ExtensionLoadContext Context { get; set; }
            public required IExtension Extension { get; set; }
            public required ExtensionInfo Info { get; set; }
        }
    }

    /// <summary>
    /// 可卸载的扩展加载上下文
    /// </summary>
    internal class ExtensionLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _pluginPath;

        public ExtensionLoadContext(string pluginPath) : base(isCollectible: true)
        {
            _pluginPath = pluginPath;
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // 框架程序集(System.*, Microsoft.*, netstandard)优先使用运行时内置版本，
            // 避免从NuGet缓存加载不兼容的高版本(如 .NET9 的 System.Text.Encoding.CodePages 9.0
            // 在 .NET8 宿主上无法加载)
            string? name = assemblyName.Name;
            if (!string.IsNullOrEmpty(name) &&
                (name.StartsWith("System.") || name.StartsWith("Microsoft.") ||
                 name.StartsWith("netstandard") || name == "mscorlib"))
            {
                // 1. 优先复用已加载的框架程序集(任意版本均可)
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == name)
                            return asm;
                    }
                }
                catch (Exception ex) { Output.Debug(ex, $"扫描已加载程序集失败: {name}", "RtEM"); }

                // 2. 从运行时目录加载对应DLL(版本兼容，CLR会统一)
                try
                {
                    string? runtimeDir = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location);
                    if (!string.IsNullOrEmpty(runtimeDir))
                    {
                        string dllPath = System.IO.Path.Combine(runtimeDir, name + ".dll");
                        if (System.IO.File.Exists(dllPath))
                            return LoadFromAssemblyPath(dllPath);
                    }
                }
                catch (Exception ex) { Output.Debug(ex, $"从运行时目录加载程序集失败: {name}", "RtEM"); }

                return null;
            }

            string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath != null)
            {
                // 强制依赖程序集加载路径位于程序根目录内, 阻止从任意路径加载
                if (Validator.IsWithinProgramRoot(assemblyPath))
                    return LoadFromAssemblyPath(assemblyPath);
                Output.Log($"拒绝加载程序根目录外的扩展依赖: {System.IO.Path.GetFileName(assemblyPath)}", 3, "RtEM");
            }

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == assemblyName.Name)
                    {
                        return asm;
                    }
                }
            }
            catch (Exception ex) { Output.Debug(ex, $"匹配已加载程序集失败: {assemblyName.Name}", "RtEM"); }

            return null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (libraryPath != null)
            {
                // 原生库同样限制在程序根目录内
                if (Validator.IsWithinProgramRoot(libraryPath))
                    return LoadUnmanagedDllFromPath(libraryPath);
                Output.Log($"拒绝加载程序根目录外的原生库: {System.IO.Path.GetFileName(libraryPath)}", 3, "RtEM");
            }

            // 可回收上下文中返回 IntPtr.Zero 的默认回退可能无法解析系统目录中的原生库
            // (如 SharpPcap 依赖的 wpcap.dll)，因此显式从系统目录尝试加载
            string fileName = unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? unmanagedDllName : unmanagedDllName + ".dll";

            string[] candidateDirs = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows)
                ? new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.System) }
                : new[] { "/usr/lib", "/usr/lib/x86_64-linux-gnu", "/lib" };

            foreach (string dir in candidateDirs)
            {
                string full = System.IO.Path.Combine(dir, fileName);
                try
                {
                    if (System.IO.File.Exists(full))
                        return LoadUnmanagedDllFromPath(full);
                }
                catch (Exception ex) { Output.Debug(ex, $"从系统目录加载原生库失败: {full}", "RtEM"); }
            }

            return IntPtr.Zero;
        }
    }
}
