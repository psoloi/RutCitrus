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
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            var (allowed, retryAfter) = LoginRateLimiter.Check(ip);
            if (!allowed)
            {
                return Ok(new { success = false, message = $"尝试次数过多，请在 {retryAfter} 秒后再试" });
            }

            if (_client.IsConnected)
            {
                return Ok(new { success = false, message = "已有活动连接，请先登出" });
            }

            // 用户确认重新信任: 清除该主机的 TOFU 证书指纹记录后重连
            if (request.Retrust)
                _client.ResetKnownHost(request.Host, request.Port);

            var success = await _client.ConnectAsync(request.Host, request.Port, request.AuthKey);
            if (!success)
            {
                // 证书不匹配时认证密钥尚未被校验, 不计入登录失败次数
                if (!_client.CertMismatch)
                    LoginRateLimiter.RegisterFailure(ip);
                var reason = _client.LastError;
                var message = string.IsNullOrEmpty(reason)
                    ? "连接失败，请检查端口和密钥是否正确，以及服务器是否运行"
                    : $"连接失败：{reason}";
                return Ok(new { success = false, message, certMismatch = _client.CertMismatch });
            }

            LoginRateLimiter.Reset(ip);

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

        /// <summary>
        /// 登出: 仅清除本浏览器的登录 Cookie，不断开 gRPC 连接
        /// (避免一个会话登出把其他已登录会话全部踢下线)。
        /// </summary>
        [HttpPost("logout")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync("RtPanelCookie");
            return Ok(new { success = true });
        }

        /// <summary>登出并断开与服务器的 gRPC 连接</summary>
        [HttpPost("disconnect")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Disconnect()
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
        /// <summary>为 true 时清除该主机的 TOFU 证书指纹后重连(用户确认重新信任新证书)</summary>
        public bool Retrust { get; set; }
    }

    /// <summary>
    /// 基于客户端 IP 的登录失败限流器：窗口内连续失败达到阈值后锁定一段时间，防止暴力破解认证密钥。
    /// </summary>
    public static class LoginRateLimiter
    {
        private static readonly object Lock = new();
        private static readonly Dictionary<string, AttemptEntry> Attempts = new();
        private const int MaxAttempts = 5;
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5);

        private struct AttemptEntry
        {
            public int Count;
            public DateTime First;
            public DateTime LockUntil;
        }

        /// <summary>检查该 IP 当前是否被锁定。返回 (是否允许尝试, 剩余锁定秒数)。</summary>
        public static (bool allowed, int retryAfterSeconds) Check(string key)
        {
            lock (Lock)
            {
                if (Attempts.TryGetValue(key, out var e) && DateTime.UtcNow < e.LockUntil)
                    return (false, (int)Math.Ceiling((e.LockUntil - DateTime.UtcNow).TotalSeconds));
                return (true, 0);
            }
        }

        /// <summary>记录一次登录失败。</summary>
        public static void RegisterFailure(string key)
        {
            lock (Lock)
            {
                var now = DateTime.UtcNow;
                Attempts.TryGetValue(key, out var e);

                if (now - e.First > Window)
                {
                    e.Count = 0;
                    e.First = now;
                }

                e.Count++;
                e.LockUntil = e.Count >= MaxAttempts ? now.Add(Lockout) : DateTime.MinValue;
                Attempts[key] = e;
            }
        }

        /// <summary>登录成功后清除该 IP 的失败记录。</summary>
        public static void Reset(string key)
        {
            lock (Lock) { Attempts.Remove(key); }
        }
    }
}
