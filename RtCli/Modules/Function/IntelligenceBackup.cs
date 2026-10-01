using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Polly;
using Polly.Retry;
using RtCli.Modules.Extension;
using RtCli.Modules.Unit;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.Extensions.AI;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RtCli.Modules.Function
{
    /// <summary>Intelligence 分部实现(自 Intelligence.cs 按职责拆分, 类成员同源)。</summary>
    internal partial class Intelligence
    {
        #region 自动备份

        private static System.Threading.Timer? _backupTimer;
        private static volatile bool _backupRunning = false;
        public static bool IsBackupRunning => _backupRunning;

        public static void StartAutoBackup()
        {
            if (_backupRunning) return;
            if (!Config.App.AutoBackupEnabled) return;

            _backupRunning = true;
            int intervalMs = Config.App.AutoBackupIntervalMinutes * 60 * 1000;
            if (intervalMs < 60000) intervalMs = 60000;

            _backupTimer = new System.Threading.Timer(OnBackupTick, null, intervalMs, intervalMs);
            Output.Log($"自动备份已启动，间隔 {Config.App.AutoBackupIntervalMinutes} 分钟", 1, ThisProgramName);
        }

        public static void StopAutoBackup()
        {
            if (!_backupRunning) return;

            _backupTimer?.Dispose();
            _backupTimer = null;
            _backupRunning = false;
            Output.Log("自动备份已停止", 1, ThisProgramName);
        }

        private static void OnBackupTick(object? state)
        {
            try
            {
                PerformBackup();
            }
            catch (Exception ex)
            {
                Output.Log($"自动备份执行出错: {ex.Message}", 2, ThisProgramName);
            }
        }

        public static void PerformBackup()
        {
            var servers = Config.App.AutoBackupServers;
            if (servers == null || servers.Count == 0)
            {
                servers = Config.App.ServerList.Keys.ToList();
            }

            foreach (var serverKey in servers)
            {
                if (!Config.App.ServerList.TryGetValue(serverKey, out var entry))
                {
                    Output.Log($"跳过不存在的服务端标识: {serverKey}", 2, ThisProgramName);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(entry.WorkPath) || !Directory.Exists(entry.WorkPath))
                {
                    Output.Log($"跳过服务端 {serverKey}：工作目录未配置或不存在", 2, ThisProgramName);
                    continue;
                }

                BackupServer(serverKey, entry);
            }
        }

        private static void BackupServer(string serverKey, ServerEntry entry)
        {
            // 标识会参与路径拼接(little/{serverKey}), 拒绝分隔符与 .. 防止路径穿越
            if (string.IsNullOrWhiteSpace(serverKey)
                || serverKey.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0
                || serverKey.Contains(".."))
            {
                Output.Log($"备份标识非法，已跳过: {serverKey}", 2, ThisProgramName);
                return;
            }

            string backupBasePath = Config.App.AutoBackupPath;
            if (string.IsNullOrWhiteSpace(backupBasePath))
            {
                backupBasePath = Path.Combine(Config.DataPath, "backup");
            }

            if (!Directory.Exists(backupBasePath))
            {
                Directory.CreateDirectory(backupBasePath);
            }

            string pattern = Config.App.AutoBackupFileNamePattern;
            if (string.IsNullOrWhiteSpace(pattern))
                pattern = "{time}-{server}";

            string fileName = pattern
                .Replace("{time}", DateTime.Now.ToString("yyyyMMdd-HHmmss"))
                .Replace("{server}", serverKey);
            fileName = SanitizeBackupFileName(fileName);

            string zipPath = Path.Combine(backupBasePath, $"{fileName}.zip");
            string workPath = entry.WorkPath;

            if (string.IsNullOrWhiteSpace(workPath) || !Directory.Exists(workPath))
            {
                Output.Log($"备份服务端 {serverKey} 失败: 工作目录不存在", 2, ThisProgramName);
                return;
            }

            // 文件名中的 {server} 等占位符可能包含 ../ 或分隔符, 必须确保最终路径仍落在备份目录内
            string backupRootFull = Path.GetFullPath(backupBasePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(zipPath).StartsWith(backupRootFull, StringComparison.OrdinalIgnoreCase))
            {
                Output.Log($"备份服务端 {serverKey} 失败: 备份文件名非法或越出备份目录", 2, ThisProgramName);
                return;
            }

            try
            {
                EventBus.Publish(new BackupStartEvent(serverKey));

                // 仅当前实例可下发命令: 先刷盘再暂停自动保存, 避免打包到半写入的 region 文件
                bool canQuiesce = Config.App.CurrentServer == serverKey && Analyzer.IsAttached;
                try
                {
                    if (canQuiesce)
                    {
                        Output.Log($"备份前刷新存档并暂停保存: {serverKey}", 1, ThisProgramName);
                        Analyzer.SendCommand("save-all flush");
                        // 控制台/Rcon 无法同步确认 flush 完成, 留出落盘时间
                        Thread.Sleep(2000);
                        Analyzer.SendCommand("save-off");
                    }

                    if (Config.App.AutoBackupLittleEnabled)
                    {
                        BackupServerLittle(serverKey, workPath, backupBasePath, zipPath);
                    }
                    else
                    {
                        BackupServerFull(serverKey, workPath, zipPath);
                    }
                }
                finally
                {
                    if (canQuiesce)
                        Analyzer.SendCommand("save-on");
                }
            }
            catch (Exception ex)
            {
                Output.Log($"备份服务端 {serverKey} 失败: {ex.Message}", 2, ThisProgramName);
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            }
        }

        /// <summary>清理备份文件名中的非法字符, 保证只生成单层文件名</summary>
        private static string SanitizeBackupFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

            string result = sb.ToString().Trim().Trim('.');
            return string.IsNullOrWhiteSpace(result) ? "backup" : result;
        }

        /// <summary>
        /// 安全枚举备份文件: 手动遍历并跳过符号链接/联接目录, 避免无限递归或越出工作目录
        /// </summary>
        private static IEnumerable<string> EnumerateBackupFiles(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                string dir = stack.Pop();

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir); }
                catch { continue; }

                foreach (var file in files)
                {
                    // 文件级符号链接同样可能指向工作目录外, 跳过
                    try
                    {
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        {
                            Output.Log($"跳过符号链接文件: {file}", 1, ThisProgramName);
                            continue;
                        }
                    }
                    catch { continue; }

                    yield return file;
                }

                IEnumerable<string> subDirs;
                try { subDirs = Directory.EnumerateDirectories(dir); }
                catch { continue; }

                foreach (var subDir in subDirs)
                {
                    try
                    {
                        if ((new DirectoryInfo(subDir).Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            Output.Log($"跳过符号链接/联接目录: {subDir}", 1, ThisProgramName);
                            continue;
                        }
                    }
                    catch { continue; }

                    stack.Push(subDir);
                }
            }
        }

        /// <summary>
        /// 完整备份（原有逻辑）
        /// </summary>
        private static void BackupServerFull(string serverKey, string workPath, string zipPath)
        {
            Output.Log($"正在完整备份服务端 {serverKey}...", 1, ThisProgramName);

            int skippedCount = 0;
            int addedCount = 0;

            using (var zipArchive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (var file in EnumerateBackupFiles(workPath))
                {
                    string relativePath = file.Substring(workPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    if (ShouldIgnoreBackupFile(relativePath)) continue;

                    try
                    {
                        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var zipEntry = zipArchive.CreateEntry(relativePath, System.IO.Compression.CompressionLevel.Fastest);
                        using var entryStream = zipEntry.Open();
                        fs.CopyTo(entryStream);
                        addedCount++;
                    }
                    catch (IOException)
                    {
                        skippedCount++;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        skippedCount++;
                    }
                }
            }

            long sizeBytes = new FileInfo(zipPath).Length;
            long sizeMB = sizeBytes / (1024 * 1024);
            string skipMsg = skippedCount > 0 ? $"（跳过 {skippedCount} 个被占用文件）" : "";
            Output.Log($"完整备份完成: {zipPath} ({sizeMB}MB) {addedCount} 个文件{skipMsg}", 1, ThisProgramName);
            EventBus.Publish(new BackupCompleteEvent(serverKey, zipPath, sizeBytes));
        }

        /// <summary>
        /// Little增量备份：首次完整备份，后续仅备份与完整备份有差异的文件
        /// </summary>
        private static void BackupServerLittle(string serverKey, string workPath, string backupBasePath, string zipPath)
        {
            string littleBaseDir = Path.Combine(backupBasePath, "little", serverKey);
            string fullBackupDir = Path.Combine(littleBaseDir, "full");

            // 检查是否存在完整备份
            bool hasFullBackup = Directory.Exists(fullBackupDir) && Directory.GetFiles(fullBackupDir, "*", SearchOption.AllDirectories).Length > 0;

            if (!hasFullBackup)
            {
                // 首次：执行完整备份（同时保存一份到little/full目录作为基准）
                Output.Log($"正在创建Little完整基准备份: {serverKey}...", 1, ThisProgramName);

                int addedCount = 0;
                int skippedCount = 0;

                // 创建完整基准目录
                Directory.CreateDirectory(fullBackupDir);

                using (var zipArchive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
                {
                    foreach (var file in EnumerateBackupFiles(workPath))
                    {
                        string relativePath = file.Substring(workPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                        if (ShouldIgnoreBackupFile(relativePath)) continue;

                        try
                        {
                            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                            // 写入zip
                            var zipEntry = zipArchive.CreateEntry(relativePath, System.IO.Compression.CompressionLevel.Fastest);
                            using var entryStream = zipEntry.Open();
                            fs.CopyTo(entryStream);
                            addedCount++;

                            // 复制到基准目录
                            string destFile = Path.Combine(fullBackupDir, relativePath);
                            string? destDir = Path.GetDirectoryName(destFile);
                            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                                Directory.CreateDirectory(destDir);

                            fs.Position = 0;
                            using var destFs = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None);
                            fs.CopyTo(destFs);
                        }
                        catch (IOException)
                        {
                            skippedCount++;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            skippedCount++;
                        }
                    }
                }

                long sizeBytes = new FileInfo(zipPath).Length;
                long sizeMB = sizeBytes / (1024 * 1024);
                string skipMsg = skippedCount > 0 ? $"（跳过 {skippedCount} 个被占用文件）" : "";
                Output.Log($"Little完整基准备份完成: {zipPath} ({sizeMB}MB) {addedCount} 个文件{skipMsg}", 1, ThisProgramName);
                EventBus.Publish(new BackupCompleteEvent(serverKey, zipPath, sizeBytes));
            }
            else
            {
                // 后续：差异备份，仅备份与基准不同的文件
                Output.Log($"正在创建Little差异备份: {serverKey}...", 1, ThisProgramName);

                int addedCount = 0;
                int skippedCount = 0;
                int unchangedCount = 0;

                using (var zipArchive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
                {
                    foreach (var file in EnumerateBackupFiles(workPath))
                    {
                        string relativePath = file.Substring(workPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                        if (ShouldIgnoreBackupFile(relativePath)) continue;

                        string baseFile = Path.Combine(fullBackupDir, relativePath);

                        try
                        {
                            if (!File.Exists(baseFile))
                            {
                                // 基准中不存在，新增文件
                                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                var zipEntry = zipArchive.CreateEntry(relativePath, System.IO.Compression.CompressionLevel.Fastest);
                                using var entryStream = zipEntry.Open();
                                fs.CopyTo(entryStream);
                                addedCount++;
                            }
                            else if (IsFileChanged(file, baseFile, relativePath))
                            {
                                // 文件有差异
                                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                var zipEntry = zipArchive.CreateEntry(relativePath, System.IO.Compression.CompressionLevel.Fastest);
                                using var entryStream = zipEntry.Open();
                                fs.CopyTo(entryStream);
                                addedCount++;
                            }
                            else
                            {
                                unchangedCount++;
                            }
                        }
                        catch (IOException)
                        {
                            skippedCount++;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            skippedCount++;
                        }
                    }
                }

                long sizeBytes = new FileInfo(zipPath).Length;
                long sizeMB = sizeBytes / (1024 * 1024);
                string skipMsg = skippedCount > 0 ? $"（跳过 {skippedCount} 个被占用文件）" : "";
                Output.Log($"Little差异备份完成: {zipPath} ({sizeMB}MB) 差异 {addedCount} 个文件，{unchangedCount} 个未变更{skipMsg}", 1, ThisProgramName);
                EventBus.Publish(new BackupCompleteEvent(serverKey, zipPath, sizeBytes));
            }
        }

        /// <summary>
        /// 判断文件是否与基准文件有差异
        /// </summary>
        private static bool IsFileChanged(string currentFile, string baseFile, string relativePath)
        {
            string ext = Path.GetExtension(relativePath).ToLowerInvariant();

            // 文本类文件：对比内容
            if (IsTextFile(ext))
            {
                try
                {
                    string currentContent = File.ReadAllText(currentFile);
                    string baseContent = File.ReadAllText(baseFile);
                    return currentContent != baseContent;
                }
                catch
                {
                    return true;
                }
            }

            // 二进制文件：对比大小
            try
            {
                var currentInfo = new FileInfo(currentFile);
                var baseInfo = new FileInfo(baseFile);

                if (currentInfo.Length != baseInfo.Length)
                    return true;

                // 大小相同且未开启强制备份二进制文件，视为未变更
                if (!Config.App.AutoBackupLittleForceBinary)
                    return false;

                // 强制备份模式：大小相同也对比内容（用于检测同大小但内容不同的二进制文件）
                return IsBinaryContentDifferent(currentFile, baseFile);
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// 判断文件扩展名是否为文本类型
        /// </summary>
        private static bool IsTextFile(string ext)
        {
            return ext == ".json" || ext == ".yml" || ext == ".yaml" ||
                   ext == ".properties" || ext == ".bat" || ext == ".sh" ||
                   ext == ".txt" || ext == ".cfg" || ext == ".conf" ||
                   ext == ".toml" || ext == ".ini" || ext == ".xml" ||
                   ext == ".md" || ext == ".log" || ext == ".csv";
        }

        /// <summary>
        /// 对比两个二进制文件内容是否不同
        /// </summary>
        private static bool IsBinaryContentDifferent(string file1, string file2)
        {
            try
            {
                const int bufferSize = 8192;
                using var fs1 = new FileStream(file1, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var fs2 = new FileStream(file2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                if (fs1.Length != fs2.Length) return true;

                var buffer1 = new byte[bufferSize];
                var buffer2 = new byte[bufferSize];

                while (true)
                {
                    int read1 = fs1.Read(buffer1, 0, bufferSize);
                    int read2 = fs2.Read(buffer2, 0, bufferSize);

                    if (read1 != read2) return true;
                    if (read1 == 0) return false;

                    for (int i = 0; i < read1; i++)
                    {
                        if (buffer1[i] != buffer2[i]) return true;
                    }
                }
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// 判断文件是否应被忽略备份
        /// </summary>
        private static bool ShouldIgnoreBackupFile(string relativePath)
        {
            // 检查忽略路径（匹配任意层级的目录名，包括其所有子目录和文件）
            var ignorePaths = Config.App.AutoBackupIgnorePaths;
            if (ignorePaths != null && ignorePaths.Count > 0)
            {
                string normalizedPath = relativePath.Replace('\\', '/');
                string[] pathSegments = normalizedPath.Split('/');
                foreach (var ignorePath in ignorePaths)
                {
                    if (string.IsNullOrWhiteSpace(ignorePath)) continue;
                    string normalizedIgnore = ignorePath.Replace('\\', '/').Trim('/');
                    // 检查路径中任意一级目录名是否匹配
                    foreach (var segment in pathSegments)
                    {
                        if (string.Equals(segment, normalizedIgnore, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }

            // 检查忽略正则
            var ignorePatterns = Config.App.AutoBackupIgnorePatterns;
            if (ignorePatterns != null && ignorePatterns.Count > 0)
            {
                string fileName = Path.GetFileName(relativePath);
                foreach (var pattern in ignorePatterns)
                {
                    if (string.IsNullOrWhiteSpace(pattern)) continue;
                    try
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(fileName, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch { }
                }
            }

            return false;
        }

        /// <summary>
        /// 服务端启动时触发Little首次完整备份（如果尚无基准）
        /// </summary>
        public static void TriggerLittleFullBackupIfNeeded()
        {
            if (!Config.App.AutoBackupEnabled || !Config.App.AutoBackupLittleEnabled)
                return;

            var currentKey = Config.App.CurrentServer;
            if (!Config.App.ServerList.TryGetValue(currentKey, out var entry))
                return;

            string backupBasePath = Config.App.AutoBackupPath;
            if (string.IsNullOrWhiteSpace(backupBasePath))
                backupBasePath = Path.Combine(Config.DataPath, "backup");

            string fullBackupDir = Path.Combine(backupBasePath, "little", currentKey, "full");
            bool hasFullBackup = Directory.Exists(fullBackupDir) && Directory.GetFiles(fullBackupDir, "*", SearchOption.AllDirectories).Length > 0;

            if (!hasFullBackup)
            {
                Output.Log("检测到Little备份无完整基准，正在创建首次完整备份...", 1, ThisProgramName);
                BackupServer(currentKey, entry);
            }
        }

        public static void BackupCurrentServer()
        {
            var currentKey = Config.App.CurrentServer;
            if (!Config.App.ServerList.TryGetValue(currentKey, out var entry))
            {
                Output.Log("当前服务端配置无效。", 2, ThisProgramName);
                return;
            }

            if (string.IsNullOrWhiteSpace(entry.WorkPath) || !Directory.Exists(entry.WorkPath))
            {
                Output.Log("当前服务端工作目录未配置或不存在。", 2, ThisProgramName);
                return;
            }

            BackupServer(currentKey, entry);
        }

        #endregion

        public static void StartAutoTips(string? instanceId = null)
        {
            var state = GetTipsState(instanceId);
            if (state.Running) return;

            if (!Config.App.EnableAutoTips)
                return;

            string id = string.IsNullOrWhiteSpace(instanceId) ? Config.App.CurrentServer : instanceId!;
            state.Running = true;
            state.Cts = new CancellationTokenSource();
            var token = state.Cts.Token;

            _ = Task.Run(() => MonitorTips(id, state, token), token);
            Output.Log($"自动提示已启动({id})", 1, "Tips");
        }

        public static void StopAutoTips(string? instanceId = null)
        {
            var state = GetTipsState(instanceId);
            if (!state.Running) return;

            state.Cts?.Cancel();
            state.Running = false;
            Output.Log("自动提示已停止", 1, "Tips");
        }

        private static void MonitorTips(string instanceId, TipsState state, CancellationToken token)
        {
            var compiledTips = new List<(Regex Regex, string Tips)>();

            foreach (var tip in ContentManager.GetAllTips())
            {
                try
                {
                    var regex = new Regex(tip.Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                    compiledTips.Add((regex, tip.Tips));
                }
                catch (Exception ex)
                {
                    Output.Log($"提示正则无效: {ex.Message}", 2, "Tips");
                }
            }

            var donePatterns = new List<Regex>();
            foreach (var pattern in Config.App.DonePatterns)
            {
                try
                {
                    donePatterns.Add(new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase));
                }
                catch (Exception ex)
                {
                    Output.Log($"Done正则无效: {ex.Message}", 2, "Tips");
                }
            }

            if (compiledTips.Count == 0 && donePatterns.Count == 0)
            {
                state.Running = false;
                return;
            }

            Output.Log($"已加载 {compiledTips.Count} 条自动提示规则, {donePatterns.Count} 条Done消息规则", 1, "Tips");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    List<string> newLines = Analyzer.ReadLogLinesSince(state.LastSequence, out long nextSeq, instanceId);
                    state.LastSequence = nextSeq;

                    foreach (var line in newLines)
                    {
                        // Done消息检测
                        if (donePatterns.Count > 0)
                        {
                            foreach (var doneRegex in donePatterns)
                            {
                                if (doneRegex.IsMatch(line))
                                {
                                    Output.Log("[green][[Done]][/] MC服务端已启动完成!", 1, "Tips");
                                    EventBus.Publish(new ServerDoneEvent(instanceId, line));
                                    break;
                                }
                            }
                        }

                        // 常规Tips检测
                        foreach (var (regex, tips) in compiledTips)
                        {
                            var m = regex.Match(line);
                            if (m.Success)
                            {
                                Output.Log("[yellow][[Tips]][/]", 1, "Tips");
                                Output.Log($"  {ExpandTipPlaceholders(tips, m)}", 1, "Tips");
                                break;
                            }
                        }
                    }

                    Thread.Sleep(1000);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }

            state.Running = false;
        }

        /// <summary>将提示文案中的 $N 占位符替换为正则匹配到的第 N 个分组(N 不存在或越界时替换为空串)</summary>
        private static string ExpandTipPlaceholders(string tips, Match m)
        {
            if (string.IsNullOrEmpty(tips) || !tips.Contains('$') || m == null || !m.Success)
                return tips;
            return Regex.Replace(tips, @"\$(\d+)", g =>
            {
                int idx = int.TryParse(g.Groups[1].Value, out var i) ? i : 0;
                return idx > 0 && idx < m.Groups.Count ? m.Groups[idx].Value : "";
            });
        }

        private enum ServerApi { Vanilla, VanillaSnapshot, PaperMC, Leaf, Purpur, Fabric, Manual }

        private class ServerTypeInfo
        {
            public string Name { get; }
            public ServerApi Api { get; }
            public string DefaultFileName { get; }
            public string? ProjectId { get; }
            public string? Website { get; }

            public ServerTypeInfo(string name, ServerApi api, string defaultFileName, string? projectId = null, string? website = null)
            {
                Name = name;
                Api = api;
                DefaultFileName = defaultFileName;
                ProjectId = projectId;
                Website = website;
            }
        }

    }
}
