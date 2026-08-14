using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http;
using System.Text.Json;
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

            return Ok(new { success = true, instances, groups });
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
                AnalyzerMode = payload.AnalyzerMode ?? "Management",
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

        /// <summary>获取实例日志内容</summary>
        [HttpGet("log")]
        public async Task<IActionResult> Log([FromQuery] string id, [FromQuery] int maxLines = 500)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.GetServerFileAsync(id ?? "", "logs/latest.log");
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            if (!resp.Success)
                return Ok(new { success = resp.Success, message = resp.Message });

            var content = resp.Content ?? "";
            var lines = content.Split('\n');
            var totalLines = lines.Length;
            if (totalLines > maxLines)
                content = string.Join('\n', lines.Skip(totalLines - maxLines));

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
                isDisabled = f.IsDisabled
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

            var files = resp.Files.Select(f => new
            {
                fileName = f.FileName,
                sizeBytes = f.SizeBytes,
                isDisabled = f.IsDisabled
            }).ToList();
            return Ok(new { success = resp.Success, message = resp.Message, files });
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

        // ===== 自定义命令(保存在 panel_data.json) =====

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
