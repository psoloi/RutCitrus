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

        /// <summary>获取 RtPanel 与 RtCli 版本号</summary>
        [HttpGet("version")]
        public IActionResult GetVersion()
        {
            var panelVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";
            return Ok(new
            {
                success = true,
                connected = _client.IsConnected,
                panelVersion,
                rtCliVersion = _client.ConnectedRtCliVersion
            });
        }

        /// <summary>
        /// 检查更新: 查询 GitHub 最新 Release, 按版本号中的 8 位日期比较(与 RtCli Checker 相同规则)。
        /// </summary>
        [HttpGet("checkupdate")]
        public async Task<IActionResult> CheckUpdate()
        {
            const string repoApi = "https://api.github.com/repos/psoloi/RutCitrus/releases/latest";
            var currentVersion = _client.ConnectedRtCliVersion ?? "";

            // 本地版本日期(与 RtCli Checker.ExtractVersionDate 一致)
            var localMatch = System.Text.RegularExpressions.Regex.Match(currentVersion, @"(\d{8})");
            if (localMatch.Success == false || !long.TryParse(localMatch.Groups[1].Value, out var localDate))
                return Ok(new { success = false, message = "无法解析当前 RtCli 版本号" + (currentVersion.Length == 0 ? "(未连接服务器)" : ": " + currentVersion) });

            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Add("User-Agent", "RtPanel-UpdateCheck");
                http.Timeout = TimeSpan.FromSeconds(10);

                var json = await http.GetStringAsync(repoApi);
                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                var tagName = obj["tag_name"]?.ToString() ?? "";
                var htmlUrl = obj["html_url"]?.ToString() ?? "";
                var releaseName = obj["name"]?.ToString() ?? "";

                var remoteMatch = System.Text.RegularExpressions.Regex.Match(tagName, @"(\d{8})");
                if (!remoteMatch.Success || !long.TryParse(remoteMatch.Groups[1].Value, out var remoteDate))
                    return Ok(new { success = false, message = $"无法解析远程版本标签: {tagName}" });

                var updateAvailable = remoteDate > localDate;
                return Ok(new
                {
                    success = true,
                    updateAvailable,
                    currentVersion,
                    latestVersion = tagName,
                    releaseUrl = htmlUrl,
                    releaseName,
                    message = updateAvailable ? $"发现新版本 {tagName}" : "当前已是最新版本"
                });
            }
            catch (TaskCanceledException)
            {
                return Ok(new { success = false, message = "检查更新超时(无法访问 GitHub)" });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = $"检查更新失败: {ex.Message}" });
            }
        }
    }
}
