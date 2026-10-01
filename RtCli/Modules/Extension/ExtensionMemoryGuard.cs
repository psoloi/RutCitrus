using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RtCli.Modules.Function;

namespace RtCli.Modules.Extension
{
    /// <summary>
    /// 扩展内存守护: 扩展占用超过 256MB 且持续 2 秒, 直接强制卸载。
    ///
    /// 为什么是"已启动扩展 + 堆增长"这种度量方式(.NET 无法按 AssemblyLoadContext 统计堆占用):
    ///   - 扩展的 Run() 往往只做初始化就返回, 真正耗内存的是它自己起的后台线程和事件回调,
    ///     因此不能只在 Run() 执行期间计量;
    ///   - 改为: 扩展"启动"时冻结堆基线, 卸载/全部退出时重新对齐,
    ///     这期间托管堆的增长即视为扩展占用近似。宿主自身增长(日志缓冲有上限)不会长期累积;
    ///   - 归属: 只有一个已启动扩展时直接归它; 多个时归"当前正在执行扩展代码"的那个,
    ///     仍无法区分就跳过(宁可漏判也不误杀)。
    ///
    /// 阈值与"持续 2 秒"的语义由 Validator.ExtensionMemoryWatch 实现:
    /// 超限只开始计时, 连续超限满时长才卸载, 中途回落即清零, 避免解析大文件这类瞬时峰值误杀。
    /// </summary>
    internal static class ExtensionMemoryGuard
    {
        private const string ThisName = "RtEM";

        private static readonly object _lock = new();
        private static readonly Dictionary<string, Validator.ExtensionMemoryWatch> _watches =
            new(StringComparer.OrdinalIgnoreCase);
        /// <summary>已启动的扩展(启动后一直存在, 直到卸载)</summary>
        private static readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>此刻正在执行扩展代码的扩展(用于多扩展时归属)</summary>
        private static readonly HashSet<string> _executing = new(StringComparer.OrdinalIgnoreCase);

        private static Timer? _timer;
        private static long _baseline;

        /// <summary>是否启用内存守护(默认启用)</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>单个扩展的内存上限(字节), 默认 256MB</summary>
        public static long LimitBytes { get; set; } = Validator.MaxExtensionMemoryBytes;

        public static bool IsRunning
        {
            get { lock (_lock) return _timer != null; }
        }

        /// <summary>启动守护(幂等)</summary>
        public static void Start()
        {
            lock (_lock)
            {
                if (_timer != null) return;
                _baseline = GC.GetTotalMemory(false);
                _watches.Clear();
                _active.Clear();
                _executing.Clear();
                _timer = new Timer(_ => SafeSample(), null,
                    Validator.ExtensionMemorySampleInterval, Validator.ExtensionMemorySampleInterval);
            }
            Output.Log($"扩展内存守护已启动(上限 {LimitBytes / 1024 / 1024}MB, 持续 " +
                       $"{Validator.ExtensionMemorySustainDuration.TotalSeconds:0.#}s 触发强制卸载)", 1, ThisName);
        }

        /// <summary>停止守护</summary>
        public static void Stop()
        {
            lock (_lock)
            {
                _timer?.Dispose();
                _timer = null;
                _active.Clear();
                _executing.Clear();
                _watches.Clear();
            }
        }

        /// <summary>登记一个已启动的扩展(之后一直计它的账, 直到卸载)</summary>
        public static void RegisterActive(string extensionKey)
        {
            if (string.IsNullOrEmpty(extensionKey)) return;
            lock (_lock)
            {
                bool wasNone = _active.Count == 0;
                if (!_active.Add(extensionKey)) return;
                // 首批扩展开始运行: 在此刻冻结基线
                if (wasNone) _baseline = GC.GetTotalMemory(false);
            }
        }

        /// <summary>标记扩展正在执行扩展代码(用于多扩展时的归属)</summary>
        public static void MarkExecuting(string extensionKey)
        {
            if (string.IsNullOrEmpty(extensionKey)) return;
            lock (_lock) { _executing.Add(extensionKey); }
        }

        /// <summary>标记扩展代码执行结束</summary>
        public static void MarkIdle(string extensionKey)
        {
            if (string.IsNullOrEmpty(extensionKey)) return;
            lock (_lock) { _executing.Remove(extensionKey); }
        }

        /// <summary>各扩展的观测峰值(字节), 供状态展示</summary>
        public static Dictionary<string, long> Snapshot()
        {
            lock (_lock) { return _watches.ToDictionary(kv => kv.Key, kv => kv.Value.PeakBytes); }
        }

        private static void SafeSample()
        {
            try { Sample(); }
            catch (Exception ex) { Output.Debug($"扩展内存采样失败: {ex.Message}", ThisName); }
        }

        private static void Sample()
        {
            if (!Enabled) return;

            long heap = GC.GetTotalMemory(false);
            string? target;
            long baseline;

            lock (_lock)
            {
                if (_timer == null) return;

                // 与当前真实加载情况对齐(被强制卸载/手动卸载的扩展要移出计账)
                try
                {
                    var loaded = RtExtensionManager.LoadedExtensions;
                    if (loaded.Count == 0)
                    {
                        _active.Clear();
                        _executing.Clear();
                    }
                    else
                    {
                        var keys = new HashSet<string>(loaded.Keys, StringComparer.OrdinalIgnoreCase);
                        _active.IntersectWith(keys);
                        _executing.IntersectWith(keys);
                    }
                }
                catch (Exception ex) { Output.Debug($"扩展列表对齐失败: {ex.Message}", ThisName); }

                if (_active.Count == 0)
                {
                    // 没有已启动的扩展: 基线跟随宿主
                    _baseline = heap;
                    return;
                }

                target = _active.Count == 1
                    ? _active.First()
                    : (_executing.Count == 1 ? _executing.First() : null);

                if (target == null) return; // 多扩展并发, 无法可靠归属 -> 跳过
                baseline = _baseline;
            }

            long growth = heap - baseline;

            Validator.ExtensionMemoryWatch watch;
            lock (_lock)
            {
                if (!_watches.TryGetValue(target, out watch!))
                {
                    watch = new Validator.ExtensionMemoryWatch(LimitBytes);
                    _watches[target] = watch;
                }
            }

            if (!watch.Sample(growth, DateTime.Now, out TimeSpan exceededFor)) return;

            long peak = watch.PeakBytes;
            Output.Log($"[内存守护] 扩展 {target} 托管堆占用约 {growth / 1024 / 1024}MB" +
                       $"(峰值 {peak / 1024 / 1024}MB), 已连续 {exceededFor.TotalSeconds:0.#}s " +
                       $"超过上限 {LimitBytes / 1024 / 1024}MB, 强制卸载。", 3, ThisName);

            lock (_lock)
            {
                _active.Remove(target);
                _executing.Remove(target);
                _watches.Remove(target);
                _baseline = GC.GetTotalMemory(false);
            }

            // 卸载可能阻塞(等待扩展任务退出), 放后台避免卡住守护定时器
            _ = Task.Run(() =>
            {
                try
                {
                    if (!RtExtensionManager.UnloadExtensionByKey(target))
                        Output.Log($"× 强制卸载扩展失败: {target}", 3, ThisName);
                }
                catch (Exception ex)
                {
                    Output.Log($"× 强制卸载扩展异常 {target}: {ex.Message}", 3, ThisName);
                }
                finally
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            });
        }
    }
}
