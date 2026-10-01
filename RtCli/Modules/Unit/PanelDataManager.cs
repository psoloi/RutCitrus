using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;

namespace RtCli.Modules
{
    /// <summary>
    /// 面板设置管理器: 负责加载/保存 panel_data.json
    /// 存储在 server_data.json 同目录(Config.DataPath)
    /// 同时管理玩家事件日志(按实例ID分组, 保存在 playerEvents 字段下)
    /// </summary>
    internal static class PanelDataManager
    {
        private static readonly string FileName = "panel_data.json";
        private static string FilePath => Path.Combine(Unit.Config.DataPath, FileName);
        private static readonly object _lock = new();

        /// <summary>每个实例最多保留的玩家事件条数</summary>
        private const int MaxEventsPerInstance = 1000;

        /// <summary>加载面板设置(JSON 字符串)</summary>
        public static string Load()
        {
            // 先冲刷未落盘的玩家事件, 保证面板读到完整数据
            FlushPendingPlayerEvents();
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(FilePath))
                        return "{}";
                    return File.ReadAllText(FilePath);
                }
                catch (Exception ex)
                {
                    Output.Log(Unit.I18n.Get("hub_load_panel_data_failed", ex.Message), 3, "Hub");
                    return "{}";
                }
            }
        }

        /// <summary>保存面板设置(JSON 字符串)</summary>
        public static (bool Success, string Message) Save(string jsonData)
        {
            lock (_lock)
            {
                try
                {
                    Unit.AtomicFile.WriteAllText(FilePath, jsonData, keepBackup: true);
                }
                catch (Exception ex)
                {
                    return (false, $"保存失败: {ex.Message}");
                }
            }
            // 面板保存的是其快照(可能不含快照后产生的事件): 写入后立即冲刷排队事件补齐
            FlushPendingPlayerEvents();
            return (true, "保存成功");
        }

        /// <summary>读取面板布尔设置(顶层键); 缺失或非法时返回 defaultValue</summary>
        public static bool GetBool(string key, bool defaultValue)
        {
            if (string.IsNullOrEmpty(key)) return defaultValue;
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(FilePath)) return defaultValue;
                    using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty(key, out var el))
                    {
                        return el.ValueKind switch
                        {
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.String => string.Equals(el.GetString(), "true", StringComparison.OrdinalIgnoreCase),
                            _ => defaultValue
                        };
                    }
                }
                catch { }
                return defaultValue;
            }
        }

        // ===== 玩家事件追加防抖 =====
        // 每个玩家进服/聊天/命令事件都会全量读改写 panel_data.json(活跃服上为秒级热路径),
        // 改为内存排队 + 延迟合并落盘; Load 前与 Save 后冲刷保证面板读写一致。
        private static readonly List<(string InstanceId, PlayerEventEntry Entry)> _pendingPlayerEvents = new();
        private static System.Threading.Timer? _playerEventFlushTimer;
        private const int PlayerEventFlushDelayMs = 2000;
        private static bool _playerEventExitHooked;

        /// <summary>追加一条玩家事件到指定实例的事件列表(防抖, 延迟合并落盘)</summary>
        public static void AppendPlayerEvent(string instanceId, PlayerEventEntry entry)
        {
            if (string.IsNullOrEmpty(instanceId) || entry == null) return;
            lock (_lock)
            {
                _pendingPlayerEvents.Add((instanceId, entry));
                if (_playerEventFlushTimer == null)
                    _playerEventFlushTimer = new System.Threading.Timer(_ => FlushPendingPlayerEvents(), null, Timeout.Infinite, Timeout.Infinite);
                _playerEventFlushTimer.Change(PlayerEventFlushDelayMs, Timeout.Infinite);
            }
            HookPlayerEventExitFlush();
        }

        /// <summary>立即把排队中的玩家事件合并写入文件(公开供退出/读取前调用)</summary>
        public static void FlushPendingPlayerEvents()
        {
            List<(string InstanceId, PlayerEventEntry Entry)> batch;
            lock (_lock)
            {
                if (_pendingPlayerEvents.Count == 0) return;
                batch = new List<(string, PlayerEventEntry)>(_pendingPlayerEvents);
                _pendingPlayerEvents.Clear();
                _playerEventFlushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            foreach (var (id, entry) in batch)
                AppendPlayerEventCore(id, entry);
        }

        private static void HookPlayerEventExitFlush()
        {
            lock (_lock)
            {
                if (_playerEventExitHooked) return;
                _playerEventExitHooked = true;
            }
            AppDomain.CurrentDomain.ProcessExit += (s, e) => FlushPendingPlayerEvents();
        }

        /// <summary>单条事件落盘(原有读改写逻辑; 调用方须已让出 _lock)</summary>
        private static void AppendPlayerEventCore(string instanceId, PlayerEventEntry entry)
        {
            lock (_lock)
            {
                try
                {
                    var json = File.Exists(FilePath) ? File.ReadAllText(FilePath) : "{}";
                    using var doc = JsonDocument.Parse(json);
                    using var ms = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(ms))
                    {
                        writer.WriteStartObject();
                        bool hasPlayerEvents = false;
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Name == "playerEvents")
                            {
                                hasPlayerEvents = true;
                                writer.WritePropertyName("playerEvents");
                                writer.WriteStartObject();
                                // 复制已有实例的事件
                                bool foundInstance = false;
                                foreach (var instProp in prop.Value.EnumerateObject())
                                {
                                    if (instProp.Name == instanceId)
                                    {
                                        foundInstance = true;
                                        writer.WritePropertyName(instanceId);
                                        WriteEventArrayWithAppend(writer, instProp.Value, entry);
                                    }
                                    else
                                    {
                                        instProp.WriteTo(writer);
                                    }
                                }
                                if (!foundInstance)
                                {
                                    writer.WritePropertyName(instanceId);
                                    writer.WriteStartArray();
                                    WriteEventEntry(writer, entry);
                                    writer.WriteEndArray();
                                }
                                writer.WriteEndObject();
                            }
                            else
                            {
                                prop.WriteTo(writer);
                            }
                        }
                        if (!hasPlayerEvents)
                        {
                            writer.WritePropertyName("playerEvents");
                            writer.WriteStartObject();
                            writer.WritePropertyName(instanceId);
                            writer.WriteStartArray();
                            WriteEventEntry(writer, entry);
                            writer.WriteEndArray();
                            writer.WriteEndObject();
                        }
                        writer.WriteEndObject();
                    }
                    Unit.AtomicFile.WriteAllText(FilePath, Encoding.UTF8.GetString(ms.ToArray()), keepBackup: true);
                }
                catch (Exception ex)
                {
                    Output.Log(Unit.I18n.Get("hub_append_player_event_failed", ex.Message), 3, "Hub");
                }
            }
        }

        /// <summary>事件板最大保留条数</summary>
        private const int MaxBoardEvents = 500;

        /// <summary>追加一条事件板事件(自动分配自增 id, 返回分配的 id, 失败返回 0)</summary>
        public static long AppendBoardEvent(BoardEventEntry entry)
        {
            if (entry == null) return 0;
            lock (_lock)
            {
                try
                {
                    var json = File.Exists(FilePath) ? File.ReadAllText(FilePath) : "{}";
                    using var doc = JsonDocument.Parse(json);
                    long nextId = 1;
                    if (doc.RootElement.TryGetProperty("boardNextId", out var nid) && nid.TryGetInt64(out var n))
                        nextId = n;
                    entry.Id = nextId;
                    using var ms = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(ms))
                    {
                        writer.WriteStartObject();
                        bool hasBoard = false;
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Name == "boardEvents")
                            {
                                hasBoard = true;
                                writer.WritePropertyName("boardEvents");
                                WriteBoardEventArrayWithAppend(writer, prop.Value, entry);
                            }
                            else if (prop.Name == "boardNextId")
                            {
                                // 跳过旧值, 末尾统一重新写入
                            }
                            else
                            {
                                prop.WriteTo(writer);
                            }
                        }
                        if (!hasBoard)
                        {
                            writer.WritePropertyName("boardEvents");
                            writer.WriteStartArray();
                            WriteBoardEventEntry(writer, entry);
                            writer.WriteEndArray();
                        }
                        writer.WriteNumber("boardNextId", nextId + 1);
                        writer.WriteEndObject();
                    }
                    Unit.AtomicFile.WriteAllText(FilePath, Encoding.UTF8.GetString(ms.ToArray()), keepBackup: true);
                    return nextId;
                }
                catch (Exception ex)
                {
                    Output.Log(Unit.I18n.Get("hub_append_board_event_failed", ex.Message), 3, "Hub");
                    return 0;
                }
            }
        }

        /// <summary>获取事件板事件列表(按时间正序)与已读水位</summary>
        public static (List<BoardEventEntry> Events, long LastReadId) GetBoardEvents(int limit = 0)
        {
            var result = new List<BoardEventEntry>();
            long lastReadId = 0;
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(FilePath)) return (result, 0);
                    var json = File.ReadAllText(FilePath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("boardLastReadId", out var lr) && lr.TryGetInt64(out var lrid))
                        lastReadId = lrid;
                    if (!doc.RootElement.TryGetProperty("boardEvents", out var arr))
                        return (result, lastReadId);
                    foreach (var item in arr.EnumerateArray())
                    {
                        result.Add(new BoardEventEntry
                        {
                            Id = item.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var idv) ? idv : 0,
                            EventKey = item.TryGetProperty("eventKey", out var ek) ? ek.GetString() ?? "" : "",
                            EventType = item.TryGetProperty("eventType", out var t) ? t.GetString() ?? "" : "",
                            InstanceId = item.TryGetProperty("instanceId", out var iid) ? iid.GetString() ?? "" : "",
                            InstanceName = item.TryGetProperty("instanceName", out var inm) ? inm.GetString() ?? "" : "",
                            Title = item.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "",
                            Detail = item.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "",
                            RecordedAt = item.TryGetProperty("recordedAt", out var r) ? r.GetString() ?? "" : ""
                        });
                    }
                }
                catch (Exception ex)
                {
                    Output.Log(Unit.I18n.Get("hub_read_board_events_failed", ex.Message), 3, "Hub");
                }
            }
            if (limit > 0 && result.Count > limit)
                result = result.Skip(result.Count - limit).ToList();
            return (result, lastReadId);
        }

        /// <summary>标记事件板已读水位(只前进不后退)</summary>
        public static (bool Success, string Message) MarkBoardRead(long id)
        {
            lock (_lock)
            {
                try
                {
                    var json = File.Exists(FilePath) ? File.ReadAllText(FilePath) : "{}";
                    using var doc = JsonDocument.Parse(json);
                    long current = 0;
                    if (doc.RootElement.TryGetProperty("boardLastReadId", out var lr) && lr.TryGetInt64(out var lv))
                        current = lv;
                    if (id <= current) return (true, "无新事件");
                    using var ms = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(ms))
                    {
                        writer.WriteStartObject();
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Name == "boardLastReadId") continue; // 末尾统一写入新水位
                            prop.WriteTo(writer);
                        }
                        writer.WriteNumber("boardLastReadId", id);
                        writer.WriteEndObject();
                    }
                    Unit.AtomicFile.WriteAllText(FilePath, Encoding.UTF8.GetString(ms.ToArray()), keepBackup: true);
                    return (true, "已标记");
                }
                catch (Exception ex)
                {
                    return (false, $"标记失败: {ex.Message}");
                }
            }
        }

        /// <summary>清空事件板(自增 id 水位保留, 避免旧事件复活为未读)</summary>
        public static (bool Success, string Message) ClearBoardEvents()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(FilePath)) return (true, "无数据");
                    var json = File.ReadAllText(FilePath);
                    using var doc = JsonDocument.Parse(json);
                    long nextId = 0;
                    if (doc.RootElement.TryGetProperty("boardNextId", out var nid) && nid.TryGetInt64(out var nv))
                        nextId = nv;
                    using var ms = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(ms))
                    {
                        writer.WriteStartObject();
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Name == "boardEvents") continue;
                            prop.WriteTo(writer);
                        }
                        if (nextId > 0)
                            writer.WriteNumber("boardLastReadId", nextId - 1);
                        writer.WriteEndObject();
                    }
                    Unit.AtomicFile.WriteAllText(FilePath, Encoding.UTF8.GetString(ms.ToArray()), keepBackup: true);
                    return (true, Unit.I18n.Get("hub_board_cleared"));
                }
                catch (Exception ex)
                {
                    return (false, $"清空失败: {ex.Message}");
                }
            }
        }

        private static void WriteBoardEventArrayWithAppend(Utf8JsonWriter writer, JsonElement existing, BoardEventEntry newEntry)
        {
            writer.WriteStartArray();
            var items = existing.EnumerateArray().ToList();
            // 超过上限则丢弃最旧的, 保持不超过 MaxBoardEvents
            int skip = 0;
            if (items.Count >= MaxBoardEvents)
                skip = items.Count - MaxBoardEvents + 1;
            for (int i = skip; i < items.Count; i++)
            {
                items[i].WriteTo(writer);
            }
            WriteBoardEventEntry(writer, newEntry);
            writer.WriteEndArray();
        }

        private static void WriteBoardEventEntry(Utf8JsonWriter writer, BoardEventEntry entry)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", entry.Id);
            if (!string.IsNullOrEmpty(entry.EventKey))
                writer.WriteString("eventKey", entry.EventKey);
            writer.WriteString("eventType", entry.EventType);
            writer.WriteString("instanceId", entry.InstanceId);
            writer.WriteString("instanceName", entry.InstanceName);
            writer.WriteString("title", entry.Title);
            writer.WriteString("detail", entry.Detail);
            writer.WriteString("recordedAt", entry.RecordedAt);
            writer.WriteEndObject();
        }

        /// <summary>解析实例显示名(取 config.yml server_list 的 server_name, 找不到回退为标识)</summary>
        private static string ResolveInstanceName(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return "";
            return Unit.Config.App.ServerList.TryGetValue(instanceId, out var entry) && !string.IsNullOrWhiteSpace(entry.ServerName)
                ? entry.ServerName
                : instanceId;
        }

        /// <summary>
        /// 追加或更新一条事件板事件(面板上报, 如文件上传进度):
        /// event_key 非空且已存在 → 更新该条(保留原 id/位置); 否则追加新事件并分配新 id。
        /// </summary>
        public static (bool Success, string Message) UpsertBoardEvent(string eventKey, string eventType,
            string instanceId, string title, string detail)
        {
            lock (_lock)
            {
                try
                {
                    var json = File.Exists(FilePath) ? File.ReadAllText(FilePath) : "{}";
                    using var doc = JsonDocument.Parse(json);
                    long nextId = 1;
                    if (doc.RootElement.TryGetProperty("boardNextId", out var nid) && nid.TryGetInt64(out var n))
                        nextId = n;

                    var instanceName = ResolveInstanceName(instanceId);
                    var recordedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                    bool updated = false;
                    bool appended = false;

                    using var ms = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(ms))
                    {
                        writer.WriteStartObject();
                        bool hasBoard = false;
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Name == "boardEvents")
                            {
                                hasBoard = true;
                                writer.WritePropertyName("boardEvents");
                                writer.WriteStartArray();
                                var items = prop.Value.EnumerateArray().ToList();
                                int skip = items.Count >= MaxBoardEvents ? items.Count - MaxBoardEvents + 1 : 0;
                                for (int i = skip; i < items.Count; i++)
                                {
                                    var item = items[i];
                                    var key = item.TryGetProperty("eventKey", out var k) ? (k.GetString() ?? "") : "";
                                    if (!updated && !string.IsNullOrEmpty(eventKey) && key == eventKey)
                                    {
                                        // 命中同键: 原地更新内容, 保留原 id(不产生新事件/新未读)
                                        long id = item.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var idv) ? idv : 0;
                                        WriteBoardEventEntry(writer, new BoardEventEntry
                                        {
                                            Id = id,
                                            EventKey = eventKey,
                                            EventType = eventType,
                                            InstanceId = instanceId,
                                            InstanceName = instanceName,
                                            Title = title,
                                            Detail = detail ?? "",
                                            RecordedAt = recordedAt
                                        });
                                        updated = true;
                                    }
                                    else
                                    {
                                        item.WriteTo(writer);
                                    }
                                }
                                if (!updated)
                                {
                                    WriteBoardEventEntry(writer, new BoardEventEntry
                                    {
                                        Id = nextId, EventKey = eventKey, EventType = eventType,
                                        InstanceId = instanceId, InstanceName = instanceName,
                                        Title = title, Detail = detail ?? "", RecordedAt = recordedAt
                                    });
                                    appended = true;
                                }
                                writer.WriteEndArray();
                            }
                            else if (prop.Name == "boardNextId")
                            {
                                // 跳过旧值, 末尾统一重新写入
                            }
                            else
                            {
                                prop.WriteTo(writer);
                            }
                        }
                        if (!hasBoard)
                        {
                            writer.WritePropertyName("boardEvents");
                            writer.WriteStartArray();
                            WriteBoardEventEntry(writer, new BoardEventEntry
                            {
                                Id = nextId, EventKey = eventKey, EventType = eventType,
                                InstanceId = instanceId, InstanceName = instanceName,
                                Title = title, Detail = detail ?? "", RecordedAt = recordedAt
                            });
                            writer.WriteEndArray();
                            appended = true;
                        }
                        // 仅新增时推进自增 id
                        writer.WriteNumber("boardNextId", appended ? nextId + 1 : nextId);
                        writer.WriteEndObject();
                    }
                    Unit.AtomicFile.WriteAllText(FilePath, Encoding.UTF8.GetString(ms.ToArray()), keepBackup: true);
                    return (true, updated ? "已更新" : "已记录");
                }
                catch (Exception ex)
                {
                    Output.Log(Unit.I18n.Get("hub_append_board_event_failed", ex.Message), 3, "Hub");
                    return (false, ex.Message);
                }
            }
        }

        /// <summary>获取指定实例的玩家事件列表</summary>
        public static List<PlayerEventEntry> GetPlayerEvents(string instanceId, int limit = 0)
        {
            var result = new List<PlayerEventEntry>();
            if (string.IsNullOrEmpty(instanceId)) return result;
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(FilePath)) return result;
                    var json = File.ReadAllText(FilePath);
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("playerEvents", out var eventsObj)) return result;
                    if (!eventsObj.TryGetProperty(instanceId, out var arr)) return result;
                    foreach (var item in arr.EnumerateArray())
                    {
                        result.Add(new PlayerEventEntry
                        {
                            EventType = item.TryGetProperty("eventType", out var t) ? t.GetString() ?? "" : "",
                            PlayerName = item.TryGetProperty("playerName", out var n) ? n.GetString() ?? "" : "",
                            TriggerTime = item.TryGetProperty("triggerTime", out var tt) ? tt.GetString() ?? "" : "",
                            RecordedAt = item.TryGetProperty("recordedAt", out var r) ? r.GetString() ?? "" : "",
                            Detail = item.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : ""
                        });
                    }
                }
                catch (Exception ex)
                {
                    Output.Log(Unit.I18n.Get("hub_read_player_events_failed", ex.Message), 3, "Hub");
                }
            }
            if (limit > 0 && result.Count > limit)
                result = result.Skip(result.Count - limit).ToList();
            return result;
        }

        /// <summary>清空指定实例的玩家事件</summary>
        public static (bool Success, string Message) ClearPlayerEvents(string instanceId)
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(FilePath)) return (true, "无数据");
                    var json = File.ReadAllText(FilePath);
                    using var doc = JsonDocument.Parse(json);
                    using var ms = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(ms))
                    {
                        writer.WriteStartObject();
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Name == "playerEvents")
                            {
                                writer.WritePropertyName("playerEvents");
                                writer.WriteStartObject();
                                foreach (var instProp in prop.Value.EnumerateObject())
                                {
                                    if (instProp.Name != instanceId)
                                        instProp.WriteTo(writer);
                                }
                                writer.WriteEndObject();
                            }
                            else
                            {
                                prop.WriteTo(writer);
                            }
                        }
                        writer.WriteEndObject();
                    }
                    Unit.AtomicFile.WriteAllText(FilePath, Encoding.UTF8.GetString(ms.ToArray()), keepBackup: true);
                    return (true, "已清空");
                }
                catch (Exception ex)
                {
                    return (false, $"清空失败: {ex.Message}");
                }
            }
        }

        private static void WriteEventArrayWithAppend(Utf8JsonWriter writer, JsonElement existing, PlayerEventEntry newEntry)
        {
            writer.WriteStartArray();
            var items = existing.EnumerateArray().ToList();
            // 超过上限则丢弃最旧的, 保持不超过 MaxEventsPerInstance
            int skip = 0;
            if (items.Count >= MaxEventsPerInstance)
                skip = items.Count - MaxEventsPerInstance + 1;
            for (int i = skip; i < items.Count; i++)
            {
                items[i].WriteTo(writer);
            }
            WriteEventEntry(writer, newEntry);
            writer.WriteEndArray();
        }

        private static void WriteEventEntry(Utf8JsonWriter writer, PlayerEventEntry entry)
        {
            writer.WriteStartObject();
            writer.WriteString("eventType", entry.EventType);
            writer.WriteString("playerName", entry.PlayerName);
            writer.WriteString("triggerTime", entry.TriggerTime);
            writer.WriteString("recordedAt", entry.RecordedAt);
            writer.WriteString("detail", entry.Detail);
            writer.WriteEndObject();
        }
    }

    /// <summary>玩家事件记录条目</summary>
    public class PlayerEventEntry
    {
        public string EventType { get; set; } = "";
        public string PlayerName { get; set; } = "";
        public string TriggerTime { get; set; } = "";
        public string RecordedAt { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    /// <summary>事件板事件条目(实例生命周期/面板上报)</summary>
    public class BoardEventEntry
    {
        public long Id { get; set; }
        /// <summary>稳定键(非空时同一键更新同一条, 用于上传进度等可刷新事件; 生命周期事件为空)</summary>
        public string EventKey { get; set; } = "";
        public string EventType { get; set; } = "";
        public string InstanceId { get; set; } = "";
        public string InstanceName { get; set; } = "";
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public string RecordedAt { get; set; } = "";
    }
}
