using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Grpc.Core;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RtCli.Grpc;
using RtPanel.Services;

namespace RtPanel.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class InstancesController : ControllerBase
    {
        private readonly RtCliClientService _client;

        public InstancesController(RtCliClientService client)
        {
            _client = client;
        }

        private static readonly HttpClient _httpClient = new HttpClient();
        private static DateTime _lastMcLogsUpload = DateTime.MinValue;

        /// <summary>获取所有实例和群组</summary>
        [HttpGet("list")]
        public async Task<IActionResult> List()
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.GetInstanceListAsync();
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            var instances = resp.Instances.Select(i => new
            {
                id = i.Id,
                identifier = i.Identifier,
                name = string.IsNullOrEmpty(i.Name) ? i.Identifier : i.Name,
                groupId = i.GroupId,
                groupName = i.GroupName,
                isRunning = i.IsRunning,
                hidden = i.Hidden,
                autoRestart = i.AutoRestart,
                workPath = i.WorkPath,
                javaPath = i.JavaPath,
                runFlags = i.RunFlags,
                analyzerMode = i.AnalyzerMode,
                createdAt = i.CreatedAt,
                lastStartedAt = i.LastStartedAt
            }).ToList();

            var groups = resp.Groups.Select(g => new
            {
                id = g.Id,
                name = g.Name,
                memberIds = g.MemberIds.ToList(),
                createdAt = g.CreatedAt
            }).ToList();

            return Ok(new { success = true, instances, groups, currentServerKey = resp.CurrentServerKey ?? "" });
        }

        /// <summary>创建实例</summary>
        [HttpPost("create")]
        public async Task<IActionResult> Create([FromBody] CreateInstancePayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var req = new CreateInstanceRequest
            {
                Identifier = payload.Identifier ?? "",
                Name = payload.Name ?? "",
                WorkPath = payload.WorkPath ?? "",
                JavaPath = payload.JavaPath ?? "",
                RunFlags = payload.RunFlags ?? "",
                AnalyzerMode = payload.AnalyzerMode ?? "RM",
                GroupId = payload.GroupId ?? ""
            };
            var resp = await _client.CreateInstanceAsync(req);
            return Ok(new { success = resp?.Success ?? false, message = resp?.Message ?? "请求失败" });
        }

        /// <summary>删除实例</summary>
        [HttpPost("delete")]
        public async Task<IActionResult> Delete([FromBody] DeleteInstancePayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.DeleteInstanceAsync(payload.Id ?? "", payload.DeleteFiles);
            return Ok(new { success = resp?.Success ?? false, message = resp?.Message ?? "请求失败" });
        }

        /// <summary>更新实例</summary>
        [HttpPost("update")]
        public async Task<IActionResult> Update([FromBody] UpdateInstancePayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var req = new UpdateInstanceRequest
            {
                Id = payload.Id ?? "",
                Name = payload.Name ?? "",
                GroupId = payload.GroupId ?? "",
                Hidden = payload.Hidden,
                AutoRestart = payload.AutoRestart,
                WorkPath = payload.WorkPath ?? "",
                JavaPath = payload.JavaPath ?? "",
                RunFlags = payload.RunFlags ?? "",
                AnalyzerMode = payload.AnalyzerMode ?? ""
            };
            var resp = await _client.UpdateInstanceAsync(req);
            return Ok(new { success = resp?.Success ?? false, message = resp?.Message ?? "请求失败" });
        }

        /// <summary>扫描主机 Java 安装路径(JAVA_HOME/PATH/常见安装目录)</summary>
        [HttpGet("javas")]
        public async Task<IActionResult> ScanJavas()
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.ScanJavaPathsAsync();
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            var javas = resp.Javas.Select(j => new
            {
                path = j.Path,
                version = j.Version,
                source = j.Source
            }).ToList();

            return Ok(new { success = resp.Success, message = resp.Message, javas });
        }

        /// <summary>克隆实例(复制配置, 可选复制工作目录文件)</summary>
        [HttpPost("clone")]
        public async Task<IActionResult> Clone([FromBody] CloneInstancePayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.CloneInstanceAsync(
                payload.Id ?? "", payload.NewIdentifier ?? "", payload.NewName ?? "", payload.CopyFiles);
            return Ok(new { success = resp?.Success ?? false, message = resp?.Message ?? "请求失败" });
        }

        /// <summary>解压实例工作目录中的 zip(导入实例用), 返回第一层启动脚本列表</summary>
        [HttpPost("extract")]
        public async Task<IActionResult> Extract([FromBody] ExtractInstancePayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.ExtractInstanceArchiveAsync(payload.Id ?? "", payload.RelPath ?? "");
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new { success = resp.Success, message = resp.Message, scripts = resp.Scripts.ToList() });
        }

        /// <summary>批量操作实例(start/stop/restart)</summary>
        [HttpPost("action")]
        public async Task<IActionResult> Action([FromBody] InstanceActionPayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.InstanceActionAsync(payload.Action ?? "", payload.Ids ?? new List<string>());
            return Ok(new { success = resp?.Success ?? false, message = resp?.Message ?? "请求失败" });
        }

        /// <summary>创建群组</summary>
        [HttpPost("group/create")]
        public async Task<IActionResult> CreateGroup([FromBody] CreateGroupPayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.CreateGroupAsync(payload.Name ?? "", payload.MemberIds ?? new List<string>());
            return Ok(new { success = resp?.Success ?? false, message = resp?.Message ?? "请求失败" });
        }

        /// <summary>解散群组</summary>
        [HttpPost("group/delete")]
        public async Task<IActionResult> DeleteGroup([FromBody] DeleteGroupPayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.DeleteGroupAsync(payload.GroupId ?? "");
            return Ok(new { success = resp?.Success ?? false, message = resp?.Message ?? "请求失败" });
        }

        /// <summary>更新群组(add/remove/rename)</summary>
        [HttpPost("group/update")]
        public async Task<IActionResult> UpdateGroup([FromBody] UpdateGroupPayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.UpdateGroupAsync(
                payload.Operation ?? "", payload.GroupId ?? "",
                payload.Name ?? "", payload.MemberIds ?? new List<string>());
            return Ok(new { success = resp?.Success ?? false, message = resp?.Message ?? "请求失败" });
        }

        /// <summary>群组批量操作(start_all/stop_all/restart_all)</summary>
        [HttpPost("group/action")]
        public async Task<IActionResult> GroupAction([FromBody] GroupActionPayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.GroupActionAsync(payload.Action ?? "", payload.GroupId ?? "");
            return Ok(new { success = resp?.Success ?? false, message = resp?.Message ?? "请求失败" });
        }

        /// <summary>获取实例详情(含备份列表)</summary>
        [HttpGet("detail")]
        public async Task<IActionResult> Detail([FromQuery] string id)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.GetInstanceDetailAsync(id ?? "");
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            var instance = resp.Instance == null ? null : new
            {
                id = resp.Instance.Id,
                identifier = resp.Instance.Identifier,
                name = resp.Instance.Name,
                groupId = resp.Instance.GroupId,
                groupName = resp.Instance.GroupName,
                isRunning = resp.Instance.IsRunning,
                hidden = resp.Instance.Hidden,
                autoRestart = resp.Instance.AutoRestart,
                workPath = resp.Instance.WorkPath,
                javaPath = resp.Instance.JavaPath,
                runFlags = resp.Instance.RunFlags,
                analyzerMode = resp.Instance.AnalyzerMode,
                createdAt = resp.Instance.CreatedAt,
                lastStartedAt = resp.Instance.LastStartedAt
            };

            var backups = resp.Backups.Select(b => new
            {
                fileName = b.FileName,
                createdAt = b.CreatedAt,
                sizeBytes = b.SizeBytes
            }).ToList();

            return Ok(new { success = true, instance, backups });
        }

        /// <summary>创建备份</summary>
        [HttpPost("backup/create")]
        public async Task<IActionResult> CreateBackup([FromBody] BackupPayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.CreateBackupAsync(payload.Id ?? "");
            return Ok(new { success = resp?.Success ?? false, message = resp?.Message ?? "请求失败" });
        }

        /// <summary>恢复备份</summary>
        [HttpPost("backup/restore")]
        public async Task<IActionResult> RestoreBackup([FromBody] RestoreBackupPayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.RestoreBackupAsync(payload.Id ?? "", payload.FileName ?? "");
            return Ok(new { success = resp?.Success ?? false, message = resp?.Message ?? "请求失败" });
        }

        /// <summary>删除备份</summary>
        [HttpPost("backup/delete")]
        public async Task<IActionResult> DeleteBackup([FromBody] RestoreBackupPayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.DeleteBackupAsync(payload.Id ?? "", payload.FileName ?? "");
            return Ok(new { success = resp?.Success ?? false, message = resp?.Message ?? "请求失败" });
        }

        /// <summary>获取实例日志内容(服务端截取尾部 maxLines 行，避免大日志全量传输)</summary>
        [HttpGet("log")]
        public async Task<IActionResult> Log([FromQuery] string id, [FromQuery] int maxLines = 500)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.GetServerFileAsync(id ?? "", "logs/latest.log", tailLines: maxLines);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            if (!resp.Success)
                return Ok(new { success = resp.Success, message = resp.Message });

            var content = resp.Content ?? "";
            var totalLines = content.Length == 0 ? 0 : content.Split('\n').Length;

            return Ok(new { success = true, message = resp.Message, content, totalLines });
        }

        /// <summary>分析实例错误日志(正则提取)</summary>
        [HttpPost("errors/analyze")]
        public async Task<IActionResult> AnalyzeErrors([FromBody] AnalyzeErrorsPayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.AnalyzeInstanceErrorsAsync(payload?.Id ?? "");
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            var errors = resp.Errors.Select(e => new { index = e.Key, content = e.Value }).ToList();
            return Ok(new { success = resp.Success, message = resp.Message, errors });
        }

        /// <summary>基础分析实例错误(对应控制台 .fx base)</summary>
        [HttpPost("errors/base")]
        public async Task<IActionResult> BaseAnalyzeErrors([FromBody] AiAnalyzeErrorsPayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.BaseAnalyzeInstanceErrorsAsync(payload?.Id ?? "", payload?.Range ?? "");
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new
            {
                success = resp.Success,
                message = resp.Message,
                matches = resp.Matches.Select(m => new
                {
                    topic = m.Topic,
                    solution = m.Solution,
                    action = m.Action,
                    matched = m.Matched
                })
            });
        }

        /// <summary>AI分析实例错误</summary>
        [HttpPost("errors/ai")]
        public async Task<IActionResult> AiAnalyzeErrors([FromBody] AiAnalyzeErrorsPayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.AiAnalyzeInstanceErrorsAsync(payload?.Id ?? "", payload?.Range ?? "");
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new { success = resp.Success, message = resp.Message, result = resp.Result });
        }

        /// <summary>上传日志到 mclo.gs</summary>
        [HttpPost("log/share")]
        public async Task<IActionResult> ShareLog([FromBody] ShareLogPayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var content = payload?.Content ?? "";
            if (string.IsNullOrEmpty(content))
                return Ok(new { success = false, message = "日志内容为空" });

            if (System.Text.Encoding.UTF8.GetByteCount(content) >= 10 * 1024 * 1024)
                return Ok(new { success = false, message = "日志内容过大(超过10MB限制)" });

            var now = DateTime.UtcNow;
            var elapsed = now - _lastMcLogsUpload;
            if (elapsed.TotalSeconds < 15)
            {
                var wait = (int)Math.Ceiling(15 - elapsed.TotalSeconds);
                return Ok(new { success = false, message = $"操作过于频繁，请等待 {wait} 秒后再试" });
            }
            _lastMcLogsUpload = now;

            try
            {
                var body = JsonSerializer.Serialize(new { content, source = "RtPanel" });
                var httpContent = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                var httpResponse = await _httpClient.PostAsync("https://api.mclo.gs/1/log", httpContent);
                var raw = await httpResponse.Content.ReadAsStringAsync();

                string? url = null;
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("url", out var urlEl))
                        url = urlEl.GetString();
                }
                catch { /* 响应非合法 JSON 时 url 保持为 null */ }

                if (string.IsNullOrEmpty(url))
                    return Ok(new { success = false, message = "上传失败", url = (string?)null, raw });

                return Ok(new { success = true, message = "上传成功", url, raw });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "上传异常: " + ex.Message, url = (string?)null, raw = (string?)null });
            }
        }

        /// <summary>获取指定实例的玩家事件日志</summary>
        [HttpGet("player-events")]
        public async Task<IActionResult> GetPlayerEvents([FromQuery] string id, [FromQuery] int limit = 0)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });
            if (string.IsNullOrEmpty(id))
                return Ok(new { success = false, message = "缺少实例ID" });

            var resp = await _client.GetPlayerEventsAsync(id, limit);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            var events = resp.Events.Select(e => new
            {
                eventType = e.EventType,
                playerName = e.PlayerName,
                triggerTime = e.TriggerTime,
                recordedAt = e.RecordedAt,
                detail = e.Detail
            }).ToList();

            return Ok(new { success = resp.Success, message = resp.Message, events });
        }

        // ===== 插件/模组管理 =====

        /// <summary>列出指定实例的插件</summary>
        [HttpGet("plugins")]
        public async Task<IActionResult> ListPlugins([FromQuery] string id)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });
            if (string.IsNullOrEmpty(id))
                return Ok(new { success = false, message = "缺少实例ID" });

            var resp = await _client.ListPluginsAsync(id);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            var files = resp.Files.Select(f => new
            {
                fileName = f.FileName,
                sizeBytes = f.SizeBytes,
                isDisabled = f.IsDisabled,
                name = f.Name,
                version = f.Version,
                description = f.Description,
                metaParsed = f.MetaParsed
            }).ToList();
            return Ok(new { success = resp.Success, message = resp.Message, files });
        }

        /// <summary>列出指定实例的模组</summary>
        [HttpGet("mods")]
        public async Task<IActionResult> ListMods([FromQuery] string id)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });
            if (string.IsNullOrEmpty(id))
                return Ok(new { success = false, message = "缺少实例ID" });

            var resp = await _client.ListModsAsync(id);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            // 模组也解析元数据(fabric.mod.json / mcmod.info / META-INF/mods.toml), 透传给面板显示名称与版本
            var files = resp.Files.Select(f => new
            {
                fileName = f.FileName,
                sizeBytes = f.SizeBytes,
                isDisabled = f.IsDisabled,
                name = f.Name,
                version = f.Version,
                description = f.Description,
                metaParsed = f.MetaParsed
            }).ToList();
            return Ok(new { success = resp.Success, message = resp.Message, files });
        }

        /// <summary>插件更新请求负载</summary>
        public class PluginUpdatePayload
        {
            public string Id { get; set; } = "";
            public string FileName { get; set; } = "";
            /// <summary>false=只检查版本, true=下载并替换</summary>
            public bool Apply { get; set; }
        }

        public class JarFileActionPayload
        {
            public string Id { get; set; } = "";
            public string FileName { get; set; } = "";
            public string Type { get; set; } = ""; // plugin 或 mod
        }

        /// <summary>切换插件/模组启用状态</summary>
        [HttpPost("jar/toggle")]
        public async Task<IActionResult> ToggleJarFile([FromBody] JarFileActionPayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.ToggleJarFileAsync(payload.Id, payload.FileName, payload.Type);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });
            return Ok(new { success = resp.Success, message = resp.Message });
        }

        /// <summary>删除插件/模组文件</summary>
        [HttpPost("jar/delete")]
        public async Task<IActionResult> DeleteJarFile([FromBody] JarFileActionPayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.DeleteJarFileAsync(payload.Id, payload.FileName, payload.Type);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });
            return Ok(new { success = resp.Success, message = resp.Message });
        }

        /// <summary>上传插件/模组文件(multipart)</summary>
        /// <summary>插件更新: 用 plugin.yml 的插件名去 Modrinth 比对版本并下载替换。</summary>
        [HttpPost("jar/update")]
        public async Task<IActionResult> UpdatePlugin([FromBody] PluginUpdatePayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });
            if (payload == null || string.IsNullOrWhiteSpace(payload.FileName))
                return Ok(new { success = false, message = "缺少插件文件名" });

            var resp = await _client.UpdatePluginAsync(payload.Id ?? "", payload.FileName, payload.Apply);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败(可能是网络或超时)" });

            return Ok(new
            {
                resp.Success,
                resp.Message,
                hasUpdate = resp.HasUpdate,
                localVersion = resp.LocalVersion,
                remoteVersion = resp.RemoteVersion,
                projectTitle = resp.ProjectTitle,
                newFileName = resp.NewFileName
            });
        }

        [HttpPost("jar/upload")]
        [RequestSizeLimit(64 * 1024 * 1024)]
        public async Task<IActionResult> UploadJarFile([FromForm] string id, [FromForm] string type, IFormFile file)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });
            if (file == null || file.Length == 0)
                return Ok(new { success = false, message = "未选择文件" });

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var content = ms.ToArray();

            var resp = await _client.UploadJarFileAsync(id, file.FileName, type, content);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });
            return Ok(new { success = resp.Success, message = resp.Message });
        }

        // ===== 实例文件管理 =====

        /// <summary>列出实例工作目录内指定目录的内容(path 为相对工作目录的路径, "" = 根)</summary>
        [HttpGet("file/list")]
        public async Task<IActionResult> FileList([FromQuery] string id, [FromQuery] string path = "")
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });
            var resp = await _client.ListInstanceDirAsync(id ?? "", path ?? "");
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });
            var entries = resp.Entries.Select(e => new
            {
                name = e.Name,
                isDir = e.IsDir,
                sizeBytes = e.SizeBytes,
                modifiedAt = e.ModifiedAt
            }).ToList();
            return Ok(new { success = resp.Success, message = resp.Message, workPath = resp.WorkPath, entries });
        }

        /// <summary>下载实例工作目录内的文件(流式转发, 不落盘)</summary>
        [HttpGet("file/download")]
        public async Task<IActionResult> FileDownload([FromQuery] string id, [FromQuery] string path)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });
            var call = _client.DownloadInstanceFile(id ?? "", path ?? "");
            if (call == null)
                return Ok(new { success = false, message = "请求失败" });

            try
            {
                // 第一次 MoveNext 时服务端完成校验, 业务错误(文件不存在/非法路径)以 RpcException 抛出
                if (!await call.ResponseStream.MoveNext(CancellationToken.None))
                    return Ok(new { success = false, message = "文件为空" });

                var fileName = Path.GetFileName((path ?? "file").Replace('\\', '/'));
                Response.ContentType = "application/octet-stream";
                Response.Headers["Content-Disposition"] = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
                do
                {
                    var chunk = call.ResponseStream.Current;
                    await Response.Body.WriteAsync(chunk.Data.ToByteArray());
                    await Response.Body.FlushAsync();
                }
                while (await call.ResponseStream.MoveNext(CancellationToken.None));
                return new EmptyResult();
            }
            catch (RpcException ex)
            {
                if (!Response.HasStarted)
                    return Ok(new { success = false, message = string.IsNullOrEmpty(ex.Status.Detail) ? "下载失败" : ex.Status.Detail });
                return new EmptyResult();
            }
            finally
            {
                call.Dispose();
            }
        }

        /// <summary>上传文件到实例工作目录(1MB 分块转发 gRPC, 避免单消息大小限制); 过程中向事件板上报进度</summary>
        [HttpPost("file/upload")]
        [RequestSizeLimit(1024L * 1024 * 1024)]
        [RequestFormLimits(MultipartBodyLengthLimit = 1024L * 1024 * 1024)]
        public async Task<IActionResult> FileUpload([FromForm] string id, [FromForm] string? path, IFormFile file)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });
            if (file == null || file.Length == 0)
                return Ok(new { success = false, message = "未选择文件" });

            var fileName = Path.GetFileName((file.FileName ?? "").Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(fileName))
                return Ok(new { success = false, message = "非法文件名" });
            var relPath = string.IsNullOrWhiteSpace(path)
                ? fileName
                : $"{path.Trim().Replace('\\', '/').Trim('/')}/{fileName}";

            // 事件板进度上报: 同一次上传共用一个 event_key, 事件板只占一行并原地刷新进度
            var totalBytes = file.Length;
            var boardKey = $"file_upload:{Guid.NewGuid():N}";
            async Task ReportAsync(string title, string detail)
            {
                try { await _client.ReportBoardEventAsync(boardKey, "file_upload", id ?? "", title, detail); }
                catch { /* 旁路上报失败不应影响上传主流程 */ }
            }

            try
            {
                await ReportAsync($"上传文件: {fileName}", $"0% (0 B / {FormatSize(totalBytes)})");

                const int chunkSize = 1024 * 1024;
                var buffer = new byte[chunkSize];
                long offset = 0;
                int lastPct = 0;
                await using var stream = file.OpenReadStream();
                while (true)
                {
                    int read = 0;
                    while (read < chunkSize)
                    {
                        var n = await stream.ReadAsync(buffer, read, chunkSize - read);
                        if (n == 0) break;
                        read += n;
                    }
                    bool final = read < chunkSize; // 不足一块即文件读完, 本块为最后一块
                    var resp = await _client.UploadInstanceFileChunkAsync(id ?? "", relPath, offset, buffer[..read], final);
                    if (resp == null)
                    {
                        await ReportAsync($"上传失败: {fileName}", "上传请求失败");
                        return Ok(new { success = false, message = "上传请求失败" });
                    }
                    if (!resp.Success)
                    {
                        await ReportAsync($"上传失败: {fileName}", resp.Message ?? "上传失败");
                        return Ok(new { success = resp.Success, message = resp.Message });
                    }
                    offset += read;
                    if (final) break;

                    // 进度每前进 5% 刷新一次事件板(避免每块都上报造成额外开销)
                    var pct = totalBytes > 0 ? (int)(offset * 100 / totalBytes) : 100;
                    if (pct - lastPct >= 5)
                    {
                        lastPct = pct;
                        await ReportAsync($"上传文件: {fileName} ({pct}%)", $"{FormatSize(offset)} / {FormatSize(totalBytes)}");
                    }
                }
                await ReportAsync($"上传完成: {fileName}", $"共 {FormatSize(totalBytes)}");
                return Ok(new { success = true, message = $"已上传: {fileName}" });
            }
            catch (Exception ex)
            {
                await ReportAsync($"上传失败: {fileName}", ex.Message);
                return Ok(new { success = false, message = $"上传失败: {ex.Message}" });
            }
        }

        /// <summary>字节数格式化(用于事件板上传进度显示)</summary>
        private static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return u == 0 ? $"{bytes} B" : $"{v:0.##} {units[u]}";
        }

        public class FileDeletePayload
        {
            public string Id { get; set; } = "";
            public string Path { get; set; } = "";
            public bool Recursive { get; set; }
        }

        /// <summary>删除实例工作目录内的文件或目录</summary>
        [HttpPost("file/delete")]
        public async Task<IActionResult> FileDelete([FromBody] FileDeletePayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });
            var resp = await _client.DeleteInstanceEntryAsync(payload.Id, payload.Path, payload.Recursive);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });
            // 事件由 RtCli 的 Backend 在删除成功后统一记录(含 CLI/桥接通道), 此处不再重复上报
            return Ok(new { success = resp.Success, message = resp.Message });
        }

        public class FileRenamePayload
        {
            public string Id { get; set; } = "";
            public string OldPath { get; set; } = "";
            public string NewPath { get; set; } = "";
        }

        /// <summary>重命名/移动实例工作目录内的文件或目录(移动 = 跨目录重命名)</summary>
        [HttpPost("file/rename")]
        public async Task<IActionResult> FileRename([FromBody] FileRenamePayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });
            var resp = await _client.RenameInstanceEntryAsync(payload.Id, payload.OldPath, payload.NewPath);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });
            return Ok(new { success = resp.Success, message = resp.Message });
        }

        /// <summary>读取实例工作目录内的文本文件内容(供面板编辑, 拒绝二进制/超大文件)</summary>
        [HttpGet("file/read")]
        public async Task<IActionResult> FileRead([FromQuery] string id, [FromQuery] string path)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });
            var resp = await _client.ReadInstanceTextFileAsync(id ?? "", path ?? "");
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });
            return Ok(new { success = resp.Success, message = resp.Message, content = resp.Content, sizeBytes = resp.SizeBytes });
        }

        public class FileWritePayload
        {
            public string Id { get; set; } = "";
            public string Path { get; set; } = "";
            public string Content { get; set; } = "";
        }

        /// <summary>保存实例工作目录内的文本文件内容(服务端自动备份原文件为 .bak)</summary>
        [HttpPost("file/write")]
        public async Task<IActionResult> FileWrite([FromBody] FileWritePayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });
            var resp = await _client.WriteInstanceTextFileAsync(payload.Id, payload.Path, payload.Content);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });
            return Ok(new { success = resp.Success, message = resp.Message });
        }

        // ===== 玩家管理 =====

        public class PlayerManagePayload
        {
            public string Id { get; set; } = "";
            public string Action { get; set; } = ""; // ban/pardon/ban_ip/pardon_ip/kick/custom
            public string Target { get; set; } = "";
            public string Reason { get; set; } = "";
            public string Command { get; set; } = "";
        }

        /// <summary>玩家管理操作</summary>
        [HttpPost("player/manage")]
        public async Task<IActionResult> ManagePlayer([FromBody] PlayerManagePayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.ManagePlayerAsync(payload.Id, payload.Action, payload.Target, payload.Reason, payload.Command);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });
            return Ok(new { success = resp.Success, message = resp.Message });
        }

        // ===== 全服配置 (RM 管理协议 serversettings) =====

        /// <summary>读取全服配置(仅 RM 模式且已连接管理协议的实例可用)</summary>
        [HttpGet("serversettings")]
        public async Task<IActionResult> GetServerSettings([FromQuery] string? serverKey)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.GetServerSettingsAsync(serverKey ?? "");
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });
            var settings = resp.Settings.Select(s => new
            {
                key = s.Key,
                value = s.Value,
                type = s.Type,
                label = s.Label,
                options = s.Options.ToList()
            });
            return Ok(new { success = resp.Success, message = resp.Message, settings });
        }

        public class ServerSettingsSavePayload
        {
            public string ServerKey { get; set; } = "";
            public List<ServerSettingValueItem> Values { get; set; } = new();
        }

        public class ServerSettingValueItem
        {
            public string Key { get; set; } = "";
            public string Value { get; set; } = "";
        }

        /// <summary>保存全服配置(仅 RM 模式且已连接管理协议的实例可用)</summary>
        [HttpPost("serversettings/save")]
        public async Task<IActionResult> SaveServerSettings([FromBody] ServerSettingsSavePayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var values = (payload.Values ?? new()).Select(v => new ServerSettingValue { Key = v.Key ?? "", Value = v.Value ?? "" }).ToList();
            var resp = await _client.SetServerSettingsAsync(payload.ServerKey ?? "", values);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });
            return Ok(new { success = resp.Success, message = resp.Message });
        }

        // ===== RM 管理协议通用操作(游戏规则/名单/服务器控制/能力发现) =====

        public class RmManagePayload
        {
            public string InstanceId { get; set; } = "";
            public string Category { get; set; } = ""; // gamerules/allowlist/operators/bans/ip_bans/server/discover
            public string Action { get; set; } = "";   // get/add/remove/clear/set/save/stop/system_message/status
            public string Target { get; set; } = "";
            public string Value { get; set; } = "";
            public bool Flag { get; set; }
        }

        /// <summary>RM 管理协议通用操作(非 RM 实例部分操作自动降级为命令通道)</summary>
        [HttpPost("rm/manage")]
        public async Task<IActionResult> RmManage([FromBody] RmManagePayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.ManageServerAsync(payload.InstanceId ?? "", payload.Category ?? "",
                payload.Action ?? "", payload.Target ?? "", payload.Value ?? "", payload.Flag);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });
            var entries = resp.Entries.Select(e => new { name = e.Name, value = e.Value, extra = e.Extra }).ToList();
            return Ok(new { success = resp.Success, message = resp.Message, entries, raw = resp.Raw });
        }

        /// <summary>
        /// 查询玩家 UUID(供 ops.json / whitelist.json 界面编辑新增条目使用)。
        /// 依据实例 server.properties 的 online-mode 决定: 正版 → Mojang API; 离线 → 本地离线算法;
        /// 读不到 server.properties 或没有 online-mode 时不自动生成, 由用户手动填写。
        /// </summary>
        [HttpGet("player/uuid")]
        public async Task<IActionResult> LookupPlayerUuid([FromQuery] string? serverKey, [FromQuery] string? name)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, reason = "disconnected", message = "未连接到服务器" });

            var playerName = (name ?? "").Trim();
            if (playerName.Length == 0)
                return Ok(new { success = false, reason = "invalid-name", message = "请输入玩家名称" });
            if (!Regex.IsMatch(playerName, "^[A-Za-z0-9_]{1,16}$"))
                return Ok(new { success = false, reason = "invalid-name", message = "玩家名只能是 1-16 位字母/数字/下划线" });

            var props = await _client.GetServerFileAsync(serverKey ?? "", "server.properties");
            if (props == null || !props.Success || string.IsNullOrWhiteSpace(props.Content))
                return Ok(new { success = false, reason = "no-server-properties", message = "未找到该实例的 server.properties，无法判断正版/离线模式，请手动填写 UUID" });

            var onlineMode = ParseOnlineMode(props.Content);
            if (onlineMode == null)
                return Ok(new { success = false, reason = "no-online-mode", message = "server.properties 中未找到 online-mode，请手动填写 UUID" });

            if (onlineMode == true)
            {
                try
                {
                    using var resp = await _httpClient.GetAsync(
                        $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(playerName)}");
                    if (resp.StatusCode == System.Net.HttpStatusCode.NoContent ||
                        resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                        return Ok(new { success = false, reason = "not-found", onlineMode = true, message = "正版数据库中未找到该玩家，请确认名称拼写或手动填写 UUID" });
                    if (!resp.IsSuccessStatusCode)
                        return Ok(new { success = false, reason = "api-error", onlineMode = true, message = $"正版 API 返回 {(int)resp.StatusCode}，请稍后重试或手动填写 UUID" });

                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var rawId = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    var uuid = FormatMojangUuid(rawId);
                    if (string.IsNullOrEmpty(uuid))
                        return Ok(new { success = false, reason = "api-error", onlineMode = true, message = "正版 API 返回的 UUID 无法解析，请手动填写" });

                    return Ok(new { success = true, onlineMode = true, source = "mojang", uuid, name = playerName });
                }
                catch (Exception ex)
                {
                    return Ok(new { success = false, reason = "api-error", onlineMode = true, message = "请求正版 API 失败: " + ex.Message });
                }
            }

            // 离线模式: 等价于 Java 的 UUID.nameUUIDFromBytes("OfflinePlayer:" + name)
            return Ok(new { success = true, onlineMode = false, source = "offline", uuid = OfflinePlayerUuid(playerName), name = playerName });
        }

        /// <summary>从 server.properties 内容解析 online-mode, 未找到返回 null</summary>
        private static bool? ParseOnlineMode(string content)
        {
            foreach (var raw in content.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var idx = line.IndexOf('=');
                if (idx <= 0) continue;
                if (!line.Substring(0, idx).Trim().Equals("online-mode", StringComparison.OrdinalIgnoreCase)) continue;
                var value = line.Substring(idx + 1).Trim();
                if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return null;
        }

        /// <summary>Mojang API 返回 32 位无连字符 hex, 补成标准 UUID 字符串</summary>
        private static string? FormatMojangUuid(string? raw)
        {
            var hex = (raw ?? "").Replace("-", "").Trim().ToLowerInvariant();
            if (hex.Length != 32) return null;
            return $"{hex.Substring(0, 8)}-{hex.Substring(8, 4)}-{hex.Substring(12, 4)}-{hex.Substring(16, 4)}-{hex.Substring(20, 12)}";
        }

        /// <summary>离线模式 UUID(MD5 v3), 与 Minecraft 服务端离线模式对同一玩家名的计算结果一致</summary>
        private static string OfflinePlayerUuid(string name)
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
            hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // 版本 = 3 (MD5 命名 UUID)
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // 变体 = RFC4122
            var hex = Convert.ToHexString(hash).ToLowerInvariant();
            return $"{hex.Substring(0, 8)}-{hex.Substring(8, 4)}-{hex.Substring(12, 4)}-{hex.Substring(16, 4)}-{hex.Substring(20, 12)}";
        }

        // ===== 自定义命令(保存在 panel_data.json) =====

        // ===== 面板向导(对应 RtCli .guide / .group build) =====

        /// <summary>获取可用的服务端类型列表</summary>
        [HttpGet("wizard/types")]
        public async Task<IActionResult> GetWizardServerTypes()
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.GetServerTypesAsync();
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            var types = resp.ServerTypes.Select(t => new
            {
                name = t.Name,
                typeKey = t.TypeKey,
                downloadable = t.Downloadable,
                website = t.Website,
                defaultJar = t.DefaultJar
            }).ToList();
            return Ok(new { success = resp.Success, message = resp.Message, types });
        }

        /// <summary>获取指定服务端类型的版本列表</summary>
        [HttpGet("wizard/versions")]
        public async Task<IActionResult> GetWizardServerVersions([FromQuery] string typeKey)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });
            if (string.IsNullOrEmpty(typeKey))
                return Ok(new { success = false, message = "缺少服务端类型" });

            var resp = await _client.GetServerVersionsAsync(typeKey);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new
            {
                success = resp.Success,
                message = resp.Message,
                versions = resp.Versions.ToList(),
                allVersions = resp.AllVersions.ToList()
            });
        }

        /// <summary>下载服务端 jar 到指定目录(version 留空 = 最新版)</summary>
        [HttpPost("wizard/download")]
        public async Task<IActionResult> WizardDownloadJar([FromBody] WizardDownloadPayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.DownloadServerJarAsync(payload?.TypeKey ?? "", payload?.Version ?? "", payload?.WorkPath ?? "");
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new { success = resp.Success, message = resp.Message, jarName = resp.JarName });
        }

        /// <summary>群组服务器一键构建(创建群组+代理实例+端口分配+代理配置生成)</summary>
        [HttpPost("wizard/group-build")]
        public async Task<IActionResult> WizardGroupBuild([FromBody] GroupBuildPayload payload)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });
            if (payload == null || string.IsNullOrEmpty(payload.GroupName))
                return Ok(new { success = false, message = "缺少群组名称" });

            var req = new GroupBuildRequest
            {
                GroupName = payload.GroupName ?? "",
                ProxyType = payload.ProxyType ?? "velocity",
                ProxyId = payload.ProxyId ?? "",
                ProxyPort = payload.ProxyPort > 0 ? payload.ProxyPort : 25565,
                AutoPorts = payload.AutoPorts,
                PortRangeStart = payload.PortRangeStart,
                PortRangeEnd = payload.PortRangeEnd,
                OnlineMode = payload.OnlineMode,
                LobbyServer = payload.LobbyServer ?? ""
            };
            if (payload.MemberIds != null)
                req.MemberIds.AddRange(payload.MemberIds);

            var resp = await _client.GroupBuildAsync(req);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            var ports = resp.ServerPorts.Select(kv => new { id = kv.Key, port = kv.Value }).ToList();
            return Ok(new
            {
                success = resp.Success,
                message = resp.Message,
                groupId = resp.GroupId,
                proxyInstanceId = resp.ProxyInstanceId,
                serverPorts = ports
            });
        }

        public class WizardDownloadPayload
        {
            public string? TypeKey { get; set; }
            public string? Version { get; set; }
            public string? WorkPath { get; set; }
        }

        public class GroupBuildPayload
        {
            public string? GroupName { get; set; }
            public List<string>? MemberIds { get; set; }
            public string? ProxyType { get; set; }     // velocity / bungeecord / manual
            public string? ProxyId { get; set; }
            public int ProxyPort { get; set; }
            public bool AutoPorts { get; set; } = true;
            public int PortRangeStart { get; set; }
            public int PortRangeEnd { get; set; }
            public bool OnlineMode { get; set; } = true;
            public string? LobbyServer { get; set; }
        }

        /// <summary>获取指定实例的自定义命令列表</summary>
        [HttpGet("custom-commands")]
        public async Task<IActionResult> GetCustomCommands([FromQuery] string id)
        {
            if (string.IsNullOrEmpty(id))
                return Ok(new { success = false, message = "缺少实例ID" });

            var panelResp = await _client.GetPanelDataAsync();
            var jsonStr = panelResp?.JsonData ?? "{}";
            var commands = new List<object>();
            using var doc = JsonDocument.Parse(jsonStr);
            if (doc.RootElement.TryGetProperty("customCommands", out var cc) &&
                cc.TryGetProperty(id, out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                {
                    commands.Add(new
                    {
                        name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        command = item.TryGetProperty("command", out var c) ? c.GetString() ?? "" : ""
                    });
                }
            }
            return Ok(new { success = true, commands });
        }

        public class CustomCommandsPayload
        {
            public string Id { get; set; } = "";
            public List<CustomCommandItem> Commands { get; set; } = new();
        }

        public class CustomCommandItem
        {
            public string Name { get; set; } = "";
            public string Command { get; set; } = "";
        }

        /// <summary>保存指定实例的自定义命令列表</summary>
        [HttpPost("custom-commands")]
        public async Task<IActionResult> SaveCustomCommands([FromBody] CustomCommandsPayload payload)
        {
            if (string.IsNullOrEmpty(payload.Id))
                return Ok(new { success = false, message = "缺少实例ID" });

            var panelResp = await _client.GetPanelDataAsync();
            var jsonStr = panelResp?.JsonData ?? "{}";
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonStr) ?? new Dictionary<string, JsonElement>();

            // 构建 customCommands(保留其他实例的命令)
            var merged = new Dictionary<string, List<CustomCommandItem>>();
            if (data.TryGetValue("customCommands", out var existingCc))
            {
                foreach (var p in existingCc.EnumerateObject())
                {
                    if (p.Name == payload.Id) continue;
                    var list = new List<CustomCommandItem>();
                    foreach (var item in p.Value.EnumerateArray())
                    {
                        list.Add(new CustomCommandItem
                        {
                            Name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                            Command = item.TryGetProperty("command", out var c) ? c.GetString() ?? "" : ""
                        });
                    }
                    merged[p.Name] = list;
                }
            }
            merged[payload.Id] = payload.Commands;

            // 重建 JSON(保留其他字段)
            var newDict = new Dictionary<string, object>();
            foreach (var kv in data)
            {
                if (kv.Key != "customCommands")
                    newDict[kv.Key] = kv.Value;
            }
            newDict["customCommands"] = merged.ToDictionary(
                kvp => kvp.Key,
                kvp => (object)kvp.Value.Select(c => new { name = c.Name, command = c.Command }).ToList()
            );

            var newJson = JsonSerializer.Serialize(newDict);
            var saveResp = await _client.SavePanelDataAsync(newJson);
            return Ok(new { success = saveResp?.Success ?? false, message = saveResp?.Success == true ? "已保存" : "保存失败" });
        }

        // ===== 请求载荷类 =====
        public class CreateInstancePayload
        {
            public string? Identifier { get; set; }
            public string? Name { get; set; }
            public string? WorkPath { get; set; }
            public string? JavaPath { get; set; }
            public string? RunFlags { get; set; }
            public string? AnalyzerMode { get; set; }
            public string? GroupId { get; set; }
        }

        public class DeleteInstancePayload
        {
            public string? Id { get; set; }
            public bool DeleteFiles { get; set; }
        }

        public class UpdateInstancePayload
        {
            public string? Id { get; set; }
            public string? Name { get; set; }
            public string? GroupId { get; set; }
            public bool Hidden { get; set; }
            public bool AutoRestart { get; set; }
            public string? WorkPath { get; set; }
            public string? JavaPath { get; set; }
            public string? RunFlags { get; set; }
            public string? AnalyzerMode { get; set; }
        }

        public class InstanceActionPayload
        {
            public string? Action { get; set; }
            public List<string>? Ids { get; set; }
        }

        public class CloneInstancePayload
        {
            public string? Id { get; set; }
            public string? NewIdentifier { get; set; }
            public string? NewName { get; set; }
            public bool CopyFiles { get; set; }
        }

        public class ExtractInstancePayload
        {
            public string? Id { get; set; }
            public string? RelPath { get; set; }
        }

        public class CreateGroupPayload
        {
            public string? Name { get; set; }
            public List<string>? MemberIds { get; set; }
        }

        public class DeleteGroupPayload
        {
            public string? GroupId { get; set; }
        }

        public class UpdateGroupPayload
        {
            public string? Operation { get; set; }
            public string? GroupId { get; set; }
            public string? Name { get; set; }
            public List<string>? MemberIds { get; set; }
        }

        public class GroupActionPayload
        {
            public string? Action { get; set; }
            public string? GroupId { get; set; }
        }

        public class BackupPayload
        {
            public string? Id { get; set; }
        }

        public class RestoreBackupPayload
        {
            public string? Id { get; set; }
            public string? FileName { get; set; }
        }

        public class AnalyzeErrorsPayload
        {
            public string? Id { get; set; }
        }

        public class AiAnalyzeErrorsPayload
        {
            public string? Id { get; set; }
            public string? Range { get; set; }
        }

        public class ShareLogPayload
        {
            public string? Id { get; set; }
            public string? Content { get; set; }
        }
    }
}
