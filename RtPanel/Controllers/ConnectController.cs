using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RtPanel.Services;

namespace RtPanel.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ConnectController : ControllerBase
    {
        private readonly RtCliClientService _client;

        public ConnectController(RtCliClientService client)
        {
            _client = client;
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
}
