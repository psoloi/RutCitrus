using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RtPanel.Services;

namespace RtPanel.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ConsoleController : ControllerBase
    {
        private readonly RtCliClientService _client;

        public ConsoleController(RtCliClientService client)
        {
            _client = client;
        }

        /// <summary>
        /// 执行命令
        /// </summary>
        [HttpPost("execute")]
        public async Task<IActionResult> Execute([FromBody] ExecuteRequest request)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var response = await _client.ExecuteCommandAsync(request.Command);
            if (response == null)
                return Ok(new { success = false, message = "请求失败，连接可能已断开" });

            return Ok(new { response.Success, response.Result });
        }

        /// <summary>
        /// 轮询获取最新日志。传入 afterSeq 获取该序号之后的日志。
        /// </summary>
        [HttpGet("logs")]
        public IActionResult GetLogs([FromQuery] int afterSeq = 0)
        {
            var (lines, latestSeq) = _client.GetRecentLogs(afterSeq);
            return Ok(new { lines, latestSeq, connected = _client.IsConnected });
        }

        /// <summary>
        /// 获取可用命令列表（用于前端自动补全）
        /// </summary>
        [HttpGet("commands")]
        public async Task<IActionResult> GetCommands()
        {
            if (!_client.IsConnected)
                return Ok(new List<object>());

            var response = await _client.GetCommandListAsync();
            if (response == null)
                return Ok(new List<object>());

            var result = response.Commands.Select(c => new
            {
                c.Command,
                c.Description
            });

            return Ok(result);
        }

        /// <summary>
        /// 获取已连接的面板客户端列表
        /// </summary>
        [HttpGet("clients")]
        public async Task<IActionResult> GetClients()
        {
            if (!_client.IsConnected)
                return Ok(new List<object>());

            var response = await _client.GetClientsAsync();
            if (response == null)
                return Ok(new List<object>());

            var result = response.Clients.Select(c => new
            {
                c.Id,
                c.Ip,
                c.ConnectTime
            });

            return Ok(result);
        }
    }

    public class ExecuteRequest
    {
        public string Command { get; set; } = "";
    }
}
