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
    // ===== 实例管理数据模型 =====
    // server_data.json 存储实例扩展元数据(分组、顺序、备份历史、创建时间等),
    // 实际服务器配置仍由 config.yml 的 server_list 管理。
    // 创建/删除实例时由 ServerDataManager 同步修改 config.yml。

    /// <summary>实例扩展元数据(存储在 server_data.json)</summary>
    public class ServerInstance
    {
        /// <summary>实例 ID, 等同于 config.yml server_list 的 key 和 identifier</summary>
        public string Id { get; set; } = "";
        /// <summary>所属群组 ID(空 = 独立实例)</summary>
        public string GroupId { get; set; } = "";
        /// <summary>显示顺序(独立实例间排序, 群组内按加入顺序)</summary>
        public int SortOrder { get; set; } = 0;
        /// <summary>创建时间(ISO 8601)</summary>
        public string CreatedAt { get; set; } = "";
        /// <summary>最后启动时间</summary>
        public string LastStartedAt { get; set; } = "";
        /// <summary>备注/描述</summary>
        public string Description { get; set; } = "";
    }

    /// <summary>服务器群组</summary>
    public class ServerGroup
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>群组成员实例 ID 列表(顺序即显示顺序)</summary>
        public List<string> MemberIds { get; set; } = new List<string>();
        public string CreatedAt { get; set; } = "";
    }

    /// <summary>server_data.json 根结构</summary>
    public class ServerData
    {
        public List<ServerInstance> Instances { get; set; } = new List<ServerInstance>();
        public List<ServerGroup> Groups { get; set; } = new List<ServerGroup>();
        /// <summary>下一个群组序号(用于生成群组 ID)</summary>
        public int NextGroupSeq { get; set; } = 1;
    }

    /// <summary>
    /// 实例数据管理器: 负责加载/保存 server_data.json,
    /// 并在创建/删除实例时同步 config.yml 的 server_list。
    /// 所有操作线程安全。
    /// </summary>
    public static class ServerDataManager
    {
        private static readonly object _lock = new object();
        private static ServerData? _data;
        private static readonly string DataFileName = "server_data.json";

        private static string DataFilePath => Path.Combine(Unit.Config.DataPath, DataFileName);

        /// <summary>获取当前数据(懒加载, 首次调用时从文件读取)</summary>
        public static ServerData Data
        {
            get
            {
                lock (_lock)
                {
                    if (_data == null) Load();
                    return _data!;
                }
            }
        }

        private static string BackupFilePath => DataFilePath + ".bak";

        /// <summary>从磁盘加载 server_data.json(容错: 损坏时尝试从 .bak/.tmp 恢复, 并保留损坏现场)</summary>
        public static void Load()
        {
            lock (_lock)
            {
                ServerData? loaded = null;
                bool parseFailed = false;

                if (File.Exists(DataFilePath))
                {
                    try
                    {
                        loaded = DeserializeFile(DataFilePath);
                    }
                    catch (Exception ex)
                    {
                        parseFailed = true;
                        Output.Log(Unit.I18n.Get("hub_load_server_data_failed", ex.Message), 3, "Hub");
                    }
                }

                // 主文件损坏: 依次尝试从备份文件、上次写入的临时文件恢复
                if (loaded == null && parseFailed)
                {
                    foreach (var candidate in new[] { BackupFilePath, DataFilePath + ".tmp" })
                    {
                        if (!File.Exists(candidate)) continue;
                        try
                        {
                            loaded = DeserializeFile(candidate);
                            if (loaded != null)
                            {
                                Output.Log(Unit.I18n.Get("hub_load_server_data_recovered", candidate), 2, "Hub");
                                break;
                            }
                        }
                        catch { /* 备份同样损坏, 继续尝试下一个 */ }
                    }
                }

                // 主文件无法解析: 改名保留现场(便于人工排查/恢复), 避免被新数据直接覆盖
                if (parseFailed)
                {
                    try
                    {
                        var corruptPath = DataFilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                        File.Move(DataFilePath, corruptPath, overwrite: true);
                        Output.Log(Unit.I18n.Get("hub_load_server_data_corrupt_backup", corruptPath), 2, "Hub");
                    }
                    catch { /* 移动失败不阻断, 后续写入会用 .bak 兜底保留旧内容 */ }
                }

                _data = loaded ?? new ServerData();

                // 同步: 确保每个 server_list 条目都有对应实例元数据。
                // 文件损坏/丢失时实例列表会自动依据 config.yml 的 server_list 重建, 避免"实例丢失"。
                SyncWithServerList(forceSave: parseFailed || !File.Exists(DataFilePath));
            }
        }

        /// <summary>读取并反序列化指定文件; 结果为空视为失败(抛出异常)</summary>
        private static ServerData DeserializeFile(string path)
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<ServerData>(json);
            if (data == null)
                throw new InvalidDataException("反序列化结果为空");
            return data;
        }

        /// <summary>保存到磁盘(原子写 + 保留 .bak 备份)</summary>
        public static void Save()
        {
            lock (_lock)
            {
                try
                {
                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                    };
                    var json = JsonSerializer.Serialize(_data, options);
                    Unit.AtomicFile.WriteAllText(DataFilePath, json, Encoding.UTF8, keepBackup: true);
                }
                catch (Exception ex)
                {
                    Output.Log(Unit.I18n.Get("hub_save_server_data_failed", ex.Message), 3, "Hub");
                }
            }
        }

        /// <summary>
        /// 同步实例元数据与 config.yml server_list:
        /// - server_list 中有但 server_data.json 没有的 → 创建元数据
        /// - server_data.json 中有但 server_list 没有的 → 删除元数据(及群组成员引用)
        /// </summary>
        private static void SyncWithServerList(bool forceSave = false)
        {
            if (_data == null) return;

            var serverKeys = Unit.Config.App.ServerList.Keys.ToHashSet();
            bool changed = false;

            // 添加缺失的实例元数据
            foreach (var key in serverKeys)
            {
                if (!_data.Instances.Any(i => i.Id == key))
                {
                    _data.Instances.Add(new ServerInstance
                    {
                        Id = key,
                        CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        SortOrder = _data.Instances.Count
                    });
                    changed = true;
                }
            }

            // 移除多余的实例元数据 + 清理群组引用
            var toRemove = _data.Instances.Where(i => !serverKeys.Contains(i.Id)).ToList();
            foreach (var inst in toRemove)
            {
                _data.Instances.Remove(inst);
                foreach (var g in _data.Groups)
                    g.MemberIds.Remove(inst.Id);
                changed = true;
            }

            // 清理空群组(可选: 保留空群组让用户手动解散)
            // 不自动清理, 由用户显式解散

            // 仅在确有变更(或文件缺失/损坏重建)时写盘, 避免每次读取都产生磁盘写入
            if (changed || forceSave)
                Save();
        }

        // ===== 实例 CRUD =====

        /// <summary>创建实例: 同步在 config.yml server_list 和 server_data.json 中添加</summary>
        public static (bool Success, string Message) CreateInstance(string identifier, string name,
            string workPath, string javaPath, string runFlags, string analyzerMode, string groupId)
        {
            lock (_lock)
            {
                Load();

                // 校验: identifier 不能为空且不能重复
                identifier = identifier?.Trim() ?? "";
                if (string.IsNullOrEmpty(identifier))
                    return (false, "实例标识不能为空");
                if (Unit.Config.App.ServerList.ContainsKey(identifier))
                    return (false, $"标识 '{identifier}' 已存在");
                if (_data!.Instances.Any(i => i.Id == identifier))
                    return (false, $"实例 '{identifier}' 已存在");

                // 校验群组
                if (!string.IsNullOrEmpty(groupId) && !_data.Groups.Any(g => g.Id == groupId))
                    return (false, $"群组 '{groupId}' 不存在");

                // 添加到 config.yml server_list
                var entry = new Unit.ServerEntry
                {
                    ServerName = string.IsNullOrEmpty(name) ? identifier : name,
                    WorkPath = workPath ?? "",
                    JavaPath = javaPath ?? "",
                    RunServerFlags = string.IsNullOrEmpty(runFlags)
                        ? "-Xms1024M -Xmx1024M -jar server.jar --nogui"
                        : runFlags,
                    AnalyzerMode = string.IsNullOrEmpty(analyzerMode) ? "RM" : analyzerMode,
                    AutoRestart = false
                };
                Unit.Config.App.ServerList[identifier] = entry;

                // 工作目录不存在时自动创建(相对路径基于 RtCli 运行目录)
                if (!string.IsNullOrEmpty(workPath))
                {
                    try
                    {
                        var fullWorkPath = Path.GetFullPath(workPath);
                        if (!Directory.Exists(fullWorkPath))
                            Directory.CreateDirectory(fullWorkPath);
                    }
                    catch
                    {
                        // 路径非法时不阻断实例创建, 后续操作会给出明确错误
                    }
                }

                // 添加到 server_data.json
                var instance = new ServerInstance
                {
                    Id = identifier,
                    GroupId = groupId ?? "",
                    SortOrder = _data.Instances.Count,
                    CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };
                _data.Instances.Add(instance);

                // 添加到群组成员
                if (!string.IsNullOrEmpty(groupId))
                {
                    var group = _data.Groups.First(g => g.Id == groupId);
                    if (!group.MemberIds.Contains(identifier))
                        group.MemberIds.Add(identifier);
                }

                Unit.Config.SaveCurrentConfig();
                Save();

                Output.Log(Unit.I18n.Get("hub_instance_created", identifier, entry.ServerName), 1, "Hub");
                return (true, $"实例 '{identifier}' 创建成功");
            }
        }

        /// <summary>删除实例: 同步从 config.yml 和 server_data.json 中移除</summary>
        public static (bool Success, string Message) DeleteInstance(string id, bool deleteFiles)
        {
            lock (_lock)
            {
                Load();

                if (!Unit.Config.App.ServerList.ContainsKey(id))
                    return (false, $"实例 '{id}' 不存在");

                // 从 config.yml 移除
                var entry = Unit.Config.App.ServerList[id];
                var workPath = entry.WorkPath;
                Unit.Config.App.ServerList.Remove(id);

                // 从 hide_console_servers 移除
                Unit.Config.App.HideConsoleServers.Remove(id);

                // 如果是当前服务器, 切换到第一个可用的
                if (Unit.Config.App.CurrentServer == id)
                {
                    Unit.Config.App.CurrentServer = Unit.Config.App.ServerList.Keys.FirstOrDefault() ?? "";
                }

                // 从 server_data.json 移除
                _data!.Instances.RemoveAll(i => i.Id == id);
                foreach (var g in _data.Groups)
                    g.MemberIds.Remove(id);

                Unit.Config.SaveCurrentConfig();
                Save();

                // 可选: 删除服务器文件
                if (deleteFiles && !string.IsNullOrEmpty(workPath) && Directory.Exists(workPath))
                {
                    try
                    {
                        Directory.Delete(workPath, true);
                        Output.Log(Unit.I18n.Get("hub_instance_files_deleted", workPath), 1, "Hub");
                    }
                    catch (Exception ex)
                    {
                        Output.Log(Unit.I18n.Get("hub_delete_instance_files_failed", ex.Message), 2, "Hub");
                    }
                }

                Output.Log(Unit.I18n.Get("hub_instance_deleted", id), 1, "Hub");
                return (true, $"实例 '{id}' 已删除");
            }
        }

        /// <summary>更新实例配置(同步到 config.yml)</summary>
        public static (bool Success, string Message) UpdateInstance(string id, string name,
            string groupId, bool hidden, bool autoRestart,
            string workPath, string javaPath, string runFlags, string analyzerMode)
        {
            lock (_lock)
            {
                Load();

                if (!Unit.Config.App.ServerList.ContainsKey(id))
                    return (false, $"实例 '{id}' 不存在");

                var entry = Unit.Config.App.ServerList[id];

                // 更新 config.yml 字段
                if (name != null) entry.ServerName = name;
                if (workPath != null) entry.WorkPath = workPath;
                if (javaPath != null) entry.JavaPath = javaPath;
                if (runFlags != null) entry.RunServerFlags = runFlags;
                if (analyzerMode != null) entry.AnalyzerMode = analyzerMode;
                entry.AutoRestart = autoRestart;
                Unit.Config.App.ServerList[id] = entry;

                // 同步 hidden 到 hide_console_servers
                if (hidden)
                {
                    if (!Unit.Config.App.HideConsoleServers.Contains(id))
                        Unit.Config.App.HideConsoleServers.Add(id);
                }
                else
                {
                    Unit.Config.App.HideConsoleServers.Remove(id);
                }

                // 更新群组归属
                var instance = _data!.Instances.FirstOrDefault(i => i.Id == id);
                if (instance != null)
                {
                    // 从旧群组移除
                    if (!string.IsNullOrEmpty(instance.GroupId))
                    {
                        var oldGroup = _data.Groups.FirstOrDefault(g => g.Id == instance.GroupId);
                        oldGroup?.MemberIds.Remove(id);
                    }

                    // 添加到新群组
                    instance.GroupId = groupId ?? "";
                    if (!string.IsNullOrEmpty(groupId))
                    {
                        var newGroup = _data.Groups.FirstOrDefault(g => g.Id == groupId);
                        if (newGroup == null)
                            return (false, $"群组 '{groupId}' 不存在");
                        if (!newGroup.MemberIds.Contains(id))
                            newGroup.MemberIds.Add(id);
                    }
                }

                Unit.Config.SaveCurrentConfig();
                Save();

                Output.Log(Unit.I18n.Get("hub_instance_updated", id), 1, "Hub");
                return (true, $"实例 '{id}' 更新成功");
            }
        }

        // ===== 群组管理 =====

        public static (bool Success, string Message, string GroupId) CreateGroup(string name, List<string> memberIds)
        {
            lock (_lock)
            {
                Load();

                name = name?.Trim() ?? "";
                if (string.IsNullOrEmpty(name))
                    return (false, "群组名称不能为空", "");

                if (_data!.Groups.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    return (false, $"群组 '{name}' 已存在", "");

                var group = new ServerGroup
                {
                    Id = $"g{_data.NextGroupSeq++}",
                    Name = name,
                    CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };

                // 校验并添加成员
                foreach (var memberId in memberIds)
                {
                    if (_data.Instances.Any(i => i.Id == memberId))
                    {
                        // 从旧群组移除
                        var inst = _data.Instances.First(i => i.Id == memberId);
                        if (!string.IsNullOrEmpty(inst.GroupId))
                        {
                            var oldGroup = _data.Groups.FirstOrDefault(g => g.Id == inst.GroupId);
                            oldGroup?.MemberIds.Remove(memberId);
                        }
                        inst.GroupId = group.Id;
                        group.MemberIds.Add(memberId);
                    }
                }

                _data.Groups.Add(group);
                Save();

                Output.Log(Unit.I18n.Get("hub_group_created_log", name, group.Id, group.MemberIds.Count), 1, "Hub");
                return (true, $"群组 '{name}' 创建成功", group.Id);
            }
        }

        public static (bool Success, string Message) DeleteGroup(string groupId)
        {
            lock (_lock)
            {
                Load();

                var group = _data!.Groups.FirstOrDefault(g => g.Id == groupId);
                if (group == null)
                    return (false, $"群组 '{groupId}' 不存在");

                // 解除成员的群组归属
                foreach (var memberId in group.MemberIds)
                {
                    var inst = _data.Instances.FirstOrDefault(i => i.Id == memberId);
                    if (inst != null) inst.GroupId = "";
                }

                _data.Groups.Remove(group);
                Save();

                Output.Log(Unit.I18n.Get("hub_group_disbanded", group.Name, groupId), 1, "Hub");
                return (true, $"群组 '{group.Name}' 已解散");
            }
        }

        public static (bool Success, string Message) UpdateGroup(string operation, string groupId,
            string? name, List<string> memberIds)
        {
            lock (_lock)
            {
                Load();

                var group = _data!.Groups.FirstOrDefault(g => g.Id == groupId);
                if (group == null)
                    return (false, $"群组 '{groupId}' 不存在");

                switch (operation?.ToLowerInvariant())
                {
                    case "rename":
                        name = name?.Trim() ?? "";
                        if (string.IsNullOrEmpty(name))
                            return (false, "群组名称不能为空");
                        group.Name = name;
                        break;

                    case "add":
                        foreach (var memberId in memberIds)
                        {
                            if (!group.MemberIds.Contains(memberId) &&
                                _data.Instances.Any(i => i.Id == memberId))
                            {
                                // 从旧群组移除
                                var inst = _data.Instances.First(i => i.Id == memberId);
                                if (!string.IsNullOrEmpty(inst.GroupId))
                                {
                                    var oldGroup = _data.Groups.FirstOrDefault(g => g.Id == inst.GroupId);
                                    oldGroup?.MemberIds.Remove(memberId);
                                }
                                inst.GroupId = group.Id;
                                group.MemberIds.Add(memberId);
                            }
                        }
                        break;

                    case "remove":
                        foreach (var memberId in memberIds)
                        {
                            if (group.MemberIds.Remove(memberId))
                            {
                                var inst = _data.Instances.FirstOrDefault(i => i.Id == memberId);
                                if (inst != null) inst.GroupId = "";
                            }
                        }
                        break;

                    default:
                        return (false, $"未知操作: {operation}");
                }

                Save();
                Output.Log(Unit.I18n.Get("hub_group_updated", group.Name, operation), 1, "Hub");
                return (true, $"群组 '{group.Name}' 更新成功");
            }
        }

        // ===== 备份管理 =====

        /// <summary>获取实例备份目录</summary>
        public static string GetBackupDir(string instanceId)
        {
            var dir = Path.Combine(Unit.Config.DataPath, "instance_backup", instanceId);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>列出实例的所有备份</summary>
        public static List<(string FileName, string CreatedAt, long SizeBytes)> ListBackups(string instanceId)
        {
            var result = new List<(string, string, long)>();
            try
            {
                var dir = GetBackupDir(instanceId);
                foreach (var file in Directory.GetFiles(dir, "*.zip").OrderByDescending(f => f))
                {
                    var fi = new FileInfo(file);
                    result.Add((fi.Name, fi.CreationTime.ToString("yyyy-MM-dd HH:mm:ss"), fi.Length));
                }
            }
            catch (Exception ex) { Output.Debug(ex, $"枚举备份目录失败: {instanceId}", "Hub"); }
            return result;
        }

        /// <summary>创建备份(将 WorkPath 打包为 zip)</summary>
        public static (bool Success, string Message) CreateBackup(string instanceId)
        {
            try
            {
                if (!Unit.Config.App.ServerList.TryGetValue(instanceId, out var entry))
                    return (false, $"实例 '{instanceId}' 不存在");

                if (string.IsNullOrEmpty(entry.WorkPath) || !Directory.Exists(entry.WorkPath))
                    return (false, "服务器工作目录不存在");

                var backupDir = GetBackupDir(instanceId);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var backupFile = Path.Combine(backupDir, $"{timestamp}.zip");

                // 使用 System.IO.Compression 打包
                System.IO.Compression.ZipFile.CreateFromDirectory(entry.WorkPath, backupFile,
                    System.IO.Compression.CompressionLevel.Optimal, false);

                Output.Log(Unit.I18n.Get("hub_backup_created", instanceId, backupFile), 1, "Hub");
                Unit.BoardEventRecorder.Record("backup_create", instanceId,
                    Unit.I18n.Get("board_event_backup_create"), Path.GetFileName(backupFile));
                return (true, $"备份成功: {Path.GetFileName(backupFile)}");
            }
            catch (Exception ex)
            {
                return (false, $"备份失败: {ex.Message}");
            }
        }

        /// <summary>从备份恢复</summary>
        public static (bool Success, string Message) RestoreBackup(string instanceId, string fileName)
        {
            try
            {
                if (!Unit.Config.App.ServerList.TryGetValue(instanceId, out var entry))
                    return (false, $"实例 '{instanceId}' 不存在");

                var backupFile = Path.Combine(GetBackupDir(instanceId), fileName);
                if (!File.Exists(backupFile))
                    return (false, $"备份文件不存在: {fileName}");

                if (string.IsNullOrEmpty(entry.WorkPath))
                    return (false, "工作目录未配置");

                // 恢复前先备份当前状态(如果目录存在且非空)
                if (Directory.Exists(entry.WorkPath) && Directory.EnumerateFileSystemEntries(entry.WorkPath).Any())
                {
                    var preRestoreDir = Path.Combine(GetBackupDir(instanceId), "pre_restore");
                    if (!Directory.Exists(preRestoreDir)) Directory.CreateDirectory(preRestoreDir);
                    var preRestoreFile = Path.Combine(preRestoreDir,
                        $"pre_restore_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
                    System.IO.Compression.ZipFile.CreateFromDirectory(entry.WorkPath, preRestoreFile,
                        System.IO.Compression.CompressionLevel.Optimal, false);
                }

                // 清空工作目录并解压备份
                if (Directory.Exists(entry.WorkPath))
                    Directory.Delete(entry.WorkPath, true);
                Directory.CreateDirectory(entry.WorkPath);
                System.IO.Compression.ZipFile.ExtractToDirectory(backupFile, entry.WorkPath);

                Output.Log(Unit.I18n.Get("hub_backup_restored", instanceId, fileName), 1, "Hub");
                Unit.BoardEventRecorder.Record("backup_restore", instanceId,
                    Unit.I18n.Get("board_event_backup_restore"), fileName);
                return (true, $"恢复成功: {fileName}");
            }
            catch (Exception ex)
            {
                return (false, $"恢复失败: {ex.Message}");
            }
        }

        /// <summary>删除备份文件</summary>
        public static (bool Success, string Message) DeleteBackup(string instanceId, string fileName)
        {
            try
            {
                var backupFile = Path.Combine(GetBackupDir(instanceId), fileName);
                if (!File.Exists(backupFile))
                    return (false, $"备份文件不存在: {fileName}");

                File.Delete(backupFile);
                Output.Log(Unit.I18n.Get("hub_backup_deleted", instanceId, fileName), 1, "Hub");
                Unit.BoardEventRecorder.Record("backup_delete", instanceId,
                    Unit.I18n.Get("board_event_backup_delete"), fileName);
                return (true, $"备份 {fileName} 已删除");
            }
            catch (Exception ex)
            {
                return (false, $"删除备份失败: {ex.Message}");
            }
        }
    }

}
