using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    }
}
