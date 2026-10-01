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
    internal static class ErrorStreamDetector
    {
        private class StreamState
        {
            public StringBuilder Buffer = new StringBuilder();
            public bool InError;
        }

        private static readonly object _lock = new();
        private static readonly Dictionary<string, StreamState> _states = new(StringComparer.OrdinalIgnoreCase);
        private static Regex? _handlerRegex;
        private static int _limit = 500;
        private static Dictionary<int, ErrorRecord> _records = new();
        // 内容去重索引(替代 O(n) 的 Values.Any 扫描)与自增记录号(替代 Keys.Max())
        private static readonly HashSet<string> _contentIndex = new(StringComparer.Ordinal);
        private static int _nextRecordIndex = 1;
        private static bool _loaded;

        /// <summary>内存中保留的错误记录上限, 超出后按编号淘汰最旧记录, 防止长期运行内存无限增长</summary>
        private const int MaxRecords = 2000;

        private static void EnsureInitialized()
        {
            if (_loaded) return;
            ContentManager.Initialize();
            _limit = ContentManager.Regex.Console_Error.Limit;
            // 正则编译规则统一维护于 ErrorBlockParser
            _handlerRegex = ErrorBlockParser.CreateHandlerRegex(ContentManager.Regex.Console_Error.Handler);
            _records = ContentManager.LoadErrorLog();

            _contentIndex.Clear();
            foreach (var rec in _records.Values)
            {
                if (!string.IsNullOrEmpty(rec.Content))
                    _contentIndex.Add(rec.Content);
            }
            _nextRecordIndex = _records.Count == 0 ? 1 : _records.Keys.Max() + 1;
            _loaded = true;
        }

        /// <summary>流式喂入一行日志,自动识别错误块并保存(带来源)</summary>
        public static void Feed(string key, string line, string sourceType)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(line)) return;

            // 过滤命令噪音: 监控发送的 tps/list 等命令在部分服务端(如Folia无tps)不存在,
            // "Unknown or incomplete command...error" 会命中 Error 正则造成误记录
            if (line.Contains("Unknown or incomplete command", StringComparison.OrdinalIgnoreCase))
                return;

            List<(int Index, string Key)>? saved = null;
            lock (_lock)
            {
                EnsureInitialized();
                var handlerRegex = _handlerRegex;
                if (handlerRegex == null) return;

                if (!_states.TryGetValue(key, out var state))
                {
                    state = new StreamState();
                    _states[key] = state;
                }

                if (handlerRegex.IsMatch(line))
                {
                    // 新错误行: 先收尾上一块
                    if (state.InError && state.Buffer.Length > 0)
                        SaveBlock(key, sourceType, state, ref saved);
                    state.InError = true;
                    state.Buffer.AppendLine(line);
                }
                else if (state.InError)
                {
                    // 延续判定规则统一维护于 ErrorBlockParser
                    if (ErrorBlockParser.IsContinuation(line, handlerRegex))
                    {
                        state.Buffer.AppendLine(line);
                    }
                    else
                    {
                        SaveBlock(key, sourceType, state, ref saved);
                        state.InError = false;
                    }
                }
            }

            // 落盘与日志输出移出全局锁, 避免错误风暴时阻塞日志线程
            Persist(saved);
        }

        /// <summary>批量喂入并收尾(供 .fx add 外部导入)</summary>
        public static int FeedLines(string key, IEnumerable<string> lines, string sourceType)
        {
            int before = TotalCount;
            foreach (var line in lines)
                Feed(key, line, sourceType);
            Flush(key, sourceType);
            return TotalCount - before;
        }

        /// <summary>收尾未闭合的错误块(流结束/导入完成时)</summary>
        public static void Flush(string key, string sourceType)
        {
            List<(int Index, string Key)>? saved = null;
            lock (_lock)
            {
                EnsureInitialized();
                if (_states.TryGetValue(key, out var state) && state.InError && state.Buffer.Length > 0)
                {
                    SaveBlock(key, sourceType, state, ref saved);
                    state.InError = false;
                }
            }

            Persist(saved);
        }

        /// <summary>持久化新增错误记录并输出日志(锁外执行, 快照避免并发读写字典)</summary>
        private static void Persist(List<(int Index, string Key)>? saved)
        {
            if (saved == null || saved.Count == 0) return;

            Dictionary<int, ErrorRecord> snapshot;
            lock (_lock)
            {
                snapshot = new Dictionary<int, ErrorRecord>(_records);
            }

            ContentManager.SaveErrorLog(snapshot, quiet: true);
            foreach (var item in saved)
                Output.Log(I18n.Get("anz_fx_auto_saved", item.Index, item.Key), 1, "Analyzer");
        }

        public static int TotalCount
        {
            get { lock (_lock) { EnsureInitialized(); return _records.Count; } }
        }

        /// <summary>重置内存副本(.fx del 删除记录后调用,防止旧记录复活)</summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _records = new Dictionary<int, ErrorRecord>();
                _contentIndex.Clear();
                _nextRecordIndex = 1;
                _loaded = false;
            }
        }

        private static void SaveBlock(string key, string sourceType, StreamState state,
            ref List<(int Index, string Key)>? saved)
        {
            string text = state.Buffer.ToString().Trim();
            state.Buffer.Clear();
            if (text.Length == 0) return;
            if (text.Length > _limit) text = text.Substring(0, _limit) + "...";

            // O(1) 内容去重(与手动分析行为一致)
            if (!_contentIndex.Add(text)) return;

            int idx = _nextRecordIndex++;
            _records[idx] = new ErrorRecord
            {
                Content = text,
                Source = key,
                SourceType = sourceType,
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            EvictOverflow();
            (saved ??= new List<(int Index, string Key)>()).Add((idx, key));
        }

        /// <summary>超出内存上限时按编号淘汰最旧记录(同时从去重索引移除)</summary>
        private static void EvictOverflow()
        {
            while (_records.Count > MaxRecords)
            {
                int oldest = int.MaxValue;
                foreach (var k in _records.Keys)
                {
                    if (k < oldest) oldest = k;
                }
                if (oldest == int.MaxValue) break;

                if (_records.TryGetValue(oldest, out var removed))
                    _contentIndex.Remove(removed.Content);
                _records.Remove(oldest);
            }
        }
    }

}
