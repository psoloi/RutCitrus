using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RtPanel.Services;
using System.Security.Claims;

namespace RtPanel.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly RtCliClientService _client;

        public AuthController(RtCliClientService client)
        {
            _client = client;
        }

        [HttpPost("login")]
        [AllowAnonymous]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            if (_client.IsConnected)
            {
                return Ok(new { success = false, message = "已有活动连接，请先登出" });
            }

            var success = await _client.ConnectAsync(request.Host, request.Port, request.AuthKey);
            if (!success)
            {
                return Ok(new { success = false, message = "连接失败，请检查端口和密钥是否正确，以及服务器是否运行" });
            }

            // 创建认证票据，写入 cookie
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, $"panel@{request.Host}:{request.Port}"),
                new("ServerName", _client.ConnectedServerName ?? "unknown"),
            };
            var identity = new ClaimsIdentity(claims, "RtPanelCookie");
            var principal = new ClaimsPrincipal(identity);
            await HttpContext.SignInAsync("RtPanelCookie", principal);

            return Ok(new
            {
                success = true,
                message = "登录成功",
                serverName = _client.ConnectedServerName
            });
        }

        [HttpPost("logout")]
        public async Task<IActionResult> Logout()
        {
            await _client.DisconnectAsync();
            await HttpContext.SignOutAsync("RtPanelCookie");
            return Ok(new { success = true });
        }

        [HttpGet("status")]
        [AllowAnonymous]
        public async Task<IActionResult> GetStatus()
        {
            var authenticated = User.Identity?.IsAuthenticated == true;
            var connected = _client.IsConnected;

            string? serverName = null;
            string? version = null;
            bool isRunning = false;
            int clientCount = 0;
            int extCount = 0;

            if (connected)
            {
                var status = await _client.GetStatusAsync();
                var info = await _client.GetServerInfoAsync();
                serverName = _client.ConnectedServerName ?? info?.ServerName;
                version = info?.Version;
                isRunning = status?.IsRunning ?? false;
                clientCount = status?.ConnectedClientCount ?? 0;
                extCount = status?.ExtensionCount ?? 0;
            }

            return Ok(new
            {
                authenticated,
                connected,
                serverName,
                version,
                isRunning,
                connectedClientCount = clientCount,
                extensionCount = extCount,
                host = _client.Host,
                port = _client.Port
            });
        }
    }

    public class LoginRequest
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 7789;
        public string AuthKey { get; set; } = "";
    }
}
