using RtCli.Modules.Extension;
using RtCli.Modules.Unit;
using Spectre.Console;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RtCli.Modules.Function
{
    internal class LogStreamEntry
    {
        public string Key { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public LogStreamType Type { get; set; } = LogStreamType.Server;
        public List<string> Lines { get; } = new List<string>();
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime LastAppendedAt { get; set; } = DateTime.Now;
        public bool IsLive { get; set; }
        /// <summary>因数据量超限被清空的次数(重置计数)</summary>
        public int ResetCount { get; set; }
    }

    /// <summary>
    /// 全局日志流缓存仓库：按标识(服务器ID/外部流名)缓存各服务器的实时日志数据流。
    /// 第一个服务器启动时开始缓存；程序重启自然清空(内存缓存)。
    /// </summary>
    internal static class LogStreamStore
    {
        /// <summary>单流最大行数，超出后清空该流(数据流过多时清空缓存)</summary>
        private const int MaxLinesPerStream = 5000;
        /// <summary>全部流合计最大行数，超出后按最久未追加顺序清空</summary>
        private const int MaxTotalLines = 50000;

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, LogStreamEntry> _streams = new Dictionary<string, LogStreamEntry>(StringComparer.OrdinalIgnoreCase);
        // 全部流行数合计(增量维护, 避免每次追加都 O(n) 求和)
        private static int _totalLines;

        /// <summary>确保流存在(已存在则复用并标记运行中)；重启场景在流中插入分段标记</summary>
        public static LogStreamEntry BeginServerStream(string key, string displayName)
        {
            lock (_lock)
            {
                if (!_streams.TryGetValue(key, out var entry))
                {
                    entry = new LogStreamEntry
                    {
                        Key = key,
                        DisplayName = string.IsNullOrWhiteSpace(displayName) ? key : displayName,
                        Type = LogStreamType.Server,
                        IsLive = true
                    };
                    _streams[key] = entry;
                }
                else
                {
                    // 服务器重启: 流保留,插入分段标记便于区分多次启动的日志
                    if (entry.Lines.Count > 0)
                    {
                        entry.Lines.Add($"===== [{key}] {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
                        _totalLines++;
                    }
                    entry.IsLive = true;
                    if (!string.IsNullOrWhiteSpace(displayName))
                        entry.DisplayName = displayName;
                }
                return entry;
            }
        }

        /// <summary>向指定流追加一行(容量控制: 单流超限清空该流; 全局超限清空最旧的流)</summary>
        public static void Append(string key, string line)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(line)) return;

            string sourceType;
            lock (_lock)
            {
                if (!_streams.TryGetValue(key, out var entry)) return;
                entry.Lines.Add(line);
                entry.LastAppendedAt = DateTime.Now;
                _totalLines++;
                sourceType = entry.Type == LogStreamType.External ? "external" : "server";

                // 单流数据量过多: 清空该流并记录重置
                if (entry.Lines.Count > MaxLinesPerStream)
                {
                    _totalLines -= entry.Lines.Count;
                    entry.Lines.Clear();
                    entry.ResetCount++;
                    entry.Lines.Add($"===== [{key}] {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
                    _totalLines++;
                }
                else
                {
                    // 全局数据量过多: 从最久未追加的流开始清空
                    TrimTotal();
                }
            }

            // 流式错误识别移出本锁: 其内部有独立锁且包含磁盘写入, 避免拖住日志追加路径
            ErrorStreamDetector.Feed(key, line, sourceType);
        }

        private static void TrimTotal()
        {
            while (_totalLines > MaxTotalLines && _streams.Count > 1)
            {
                var oldest = _streams.Values
                    .Where(s => !s.IsLive && s.Lines.Count > 0)
                    .OrderBy(s => s.LastAppendedAt)
                    .FirstOrDefault();
                if (oldest == null) break;
                _totalLines -= oldest.Lines.Count;
                oldest.Lines.Clear();
                oldest.ResetCount++;
            }
        }

        /// <summary>标记服务器流停止(流保留,数据仍可分析)</summary>
        public static void SetLive(string key, bool isLive)
        {
            lock (_lock)
            {
                if (_streams.TryGetValue(key, out var entry))
                    entry.IsLive = isLive;
            }
        }

        /// <summary>导入外部日志文件为流(同名覆盖旧导入),同时流式识别错误并按来源保存</summary>
        public static LogStreamEntry ImportExternal(string name, List<string> lines)
        {
            lock (_lock)
            {
                if (_streams.TryGetValue(name, out var existing) && existing.Type == LogStreamType.External)
                {
                    _totalLines -= existing.Lines.Count;
                    existing.Lines.Clear();
                    foreach (var l in lines) existing.Lines.Add(l);
                    _totalLines += existing.Lines.Count;
                    existing.LastAppendedAt = DateTime.Now;
                    return existing;
                }
                var entry = new LogStreamEntry
                {
                    Key = name,
                    DisplayName = name,
                    Type = LogStreamType.External,
                    IsLive = false
                };
                foreach (var l in lines) entry.Lines.Add(l);
                _totalLines += entry.Lines.Count;
                _streams[name] = entry;
                return entry;
            }
        }

        public static bool Contains(string key)
        {
            lock (_lock) { return _streams.ContainsKey(key); }
        }

        public static LogStreamEntry? Get(string key)
        {
            lock (_lock)
            {
                return _streams.TryGetValue(key, out var e) ? e : null;
            }
        }

        /// <summary>获取全部流的快照(按创建时间排序)</summary>
        public static List<LogStreamEntry> GetAll()
        {
            lock (_lock)
            {
                return _streams.Values.OrderBy(s => s.CreatedAt).ToList();
            }
        }

        /// <summary>合并全部流的日志行(流之间以空行分隔,供"全部同时分析")</summary>
        public static List<string> GetMergedLines()
        {
            lock (_lock)
            {
                var result = new List<string>();
                foreach (var s in _streams.Values.OrderBy(s => s.CreatedAt))
                {
                    if (result.Count > 0) result.Add("");
                    result.AddRange(s.Lines);
                }
                return result;
            }
        }
    }

    /// <summary>
    /// 流式错误识别器: 日志行流入时按错误正则自动识别错误块,
    /// 记录数据来源(服务器标识/外部导入)并保存到错误分析结果。
    /// 服务器启动后自动工作,无需手动触发;服务器异常关闭也不丢失已识别的错误。
    /// </summary>
}
