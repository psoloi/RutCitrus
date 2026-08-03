using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RtPanel.Services;
using System.Text.Json;

namespace RtPanel.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SystemController : ControllerBase
    {
        private readonly RtCliClientService _client;

        public SystemController(RtCliClientService client)
        {
            _client = client;
        }

        /// <summary>
        /// 获取主机 CPU/内存 + 各 MC 服务端 TPS 状态。
        /// </summary>
        [HttpGet("stats")]
        public async Task<IActionResult> GetStats()
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var response = await _client.GetSystemStatsAsync();
            if (response == null)
                return Ok(new { success = false, message = "请求失败" });

            var servers = response.Servers.Select(s => new
            {
                serverKey = s.ServerKey,
                serverName = s.ServerName,
                isRunning = s.IsRunning,
                tps = s.Tps,
                tpsStatus = s.TpsStatus,
                mcPid = s.McPid,
                mcMemoryMb = s.McMemoryMb,
                tps1m = s.Tps1M,
                tps5m = s.Tps5M,
                tps15m = s.Tps15M,
                mcCpuUsage = s.McCpuUsage,
                playerCount = s.PlayerCount,
                playerMax = s.PlayerMax
            }).ToList();

            return Ok(new
            {
                success = response.Success,
                message = response.Message,
                cpuUsage = response.CpuUsage,
                memoryUsedMb = response.MemoryUsedMb,
                memoryTotalMb = response.MemoryTotalMb,
                currentServerKey = response.CurrentServerKey,
                aiRunning = response.AiRunning,
                schedulerRunning = response.SchedulerRunning,
                servers
            });
        }

        /// <summary>获取任务列表(AI任务或计划任务)</summary>
        [HttpGet("tasks/{taskType}")]
        public async Task<IActionResult> GetTaskList(string taskType)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.GetTaskListAsync(taskType);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            var tasks = resp.Tasks.Select(t => new
            {
                key = t.Key,
                name = t.Name,
                enabled = t.Enabled,
                trigger = t.Trigger,
                interval = t.Interval,
                execute = t.Execute,
                runCount = t.RunCount,
                lastRun = t.LastRun,
                status = t.Status
            }).ToList();

            return Ok(new
            {
                success = resp.Success,
                message = resp.Message,
                isRunning = resp.IsRunning,
                tasks
            });
        }

        /// <summary>获取 RtCli 运行状态(底层方法, 非命令执行)</summary>
        [HttpGet("rtstatus")]
        [AllowAnonymous]
        public async Task<IActionResult> GetRtStatus()
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.GetRtStatusAsync();
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new
            {
                success = resp.Success,
                message = resp.Message,
                managementPortRunning = resp.ManagementPortRunning,
                connectedClients = resp.ConnectedClients,
                currentServerKey = resp.CurrentServerKey,
                currentServerName = resp.CurrentServerName,
                mcMode = resp.McMode,
                mcRunning = resp.McRunning,
                mcConnected = resp.McConnected,
                autoTipsRunning = resp.AutoTipsRunning,
                aiRunning = resp.AiRunning,
                schedulerRunning = resp.SchedulerRunning
            });
        }

        /// <summary>获取面板设置</summary>
        [HttpGet("paneldata")]
        [AllowAnonymous]
        public async Task<IActionResult> GetPanelData()
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, jsonData = "{}" });

            var resp = await _client.GetPanelDataAsync();
            if (resp == null)
                return Ok(new { success = false, jsonData = "{}" });

            return Ok(new { success = resp.Success, jsonData = resp.JsonData });
        }

        /// <summary>保存面板设置</summary>
        [HttpPost("paneldata")]
        [AllowAnonymous]
        public async Task<IActionResult> SavePanelData()
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            using var reader = new System.IO.StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(body);
            string jsonData = data.TryGetProperty("jsonData", out var jd) ? jd.GetString() ?? "{}" : "{}";

            var resp = await _client.SavePanelDataAsync(jsonData);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new { success = resp.Success, message = resp.Message });
        }
    }
}
