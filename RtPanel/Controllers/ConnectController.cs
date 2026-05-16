using Microsoft.AspNetCore.Mvc;
using RtPanel.Services;

namespace RtPanel.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ConnectController : ControllerBase
    {
        private readonly RtCliClientService _client;

        public ConnectController(RtCliClientService client)
        {
            _client = client;
        }

        [HttpPost]
        public async Task<IActionResult> Connect([FromBody] ConnectRequest request)
        {
            if (_client.IsConnected)
            {
                return Ok(new { success = false, message = "已有活动连接，请先断开" });
            }

            var success = await _client.ConnectAsync(request.Host, request.Port);
            return Ok(new { success, message = success ? "连接成功" : "连接失败，请检查服务器是否运行" });
        }

        [HttpPost("disconnect")]
        public async Task<IActionResult> Disconnect()
        {
            await _client.DisconnectAsync();
            return Ok(new { success = true });
        }

        [HttpGet("status")]
        public async Task<IActionResult> GetStatus()
        {
            if (!_client.IsConnected)
            {
                return Ok(new
                {
                    connected = false,
                    serverName = (string?)null,
                    version = (string?)null,
                    isRunning = false,
                    connectedClientCount = 0,
                    extensionCount = 0
                });
            }

            var status = await _client.GetStatusAsync();
            var info = await _client.GetServerInfoAsync();

            if (!_client.IsConnected)
            {
                return Ok(new
                {
                    connected = false,
                    serverName = (string?)null,
                    version = (string?)null,
                    isRunning = false,
                    connectedClientCount = 0,
                    extensionCount = 0
                });
            }

            return Ok(new
            {
                connected = true,
                serverName = _client.ConnectedServerName ?? info?.ServerName,
                version = info?.Version,
                isRunning = status?.IsRunning ?? false,
                connectedClientCount = status?.ConnectedClientCount ?? 0,
                extensionCount = status?.ExtensionCount ?? 0
            });
        }
    }

    public class ConnectRequest
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 7789;
    }
}
