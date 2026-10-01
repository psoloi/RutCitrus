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
                // 全局只有一条 gRPC 连接(由所有面板会话共享)。若本次登录的目标与当前连接完全一致
                // (主机+端口+认证密钥相同), 则视为"登出后重新登录", 直接复用现有连接签发 Cookie,
                // 解决"登出后无法再登录"的死锁。必须认证密钥一致才允许复用, 避免绕过密钥校验。
                var sameTarget =
                    string.Equals(_client.Host, request.Host, StringComparison.OrdinalIgnoreCase) &&
                    _client.Port == request.Port &&
                    string.Equals(_client.AuthKey ?? "", request.AuthKey ?? "", StringComparison.Ordinal);

                if (sameTarget)
                {
                    LoginRateLimiter.Reset(ip);
                    await HttpContext.SignInAsync("RtPanelCookie", BuildPrincipal(request));
                    return Ok(new { success = true, message = "已复用现有连接", serverName = _client.ConnectedServerName });
                }

                // 目标不同: 不能直接顶掉现有连接(会踢掉其他已登录会话),
                // 由前端提供"断开当前连接并登录"入口, 让用户显式确认。
                return Ok(new
                {
                    success = false,
                    message = $"已有活动连接（{_client.Host}:{_client.Port}），请先断开后再连接其他服务器",
                    requiresDisconnect = true
                });
            }

            // 用户确认重新信任: 清除该主机的 TOFU 证书指纹记录后重连
            if (request.Retrust)
                _client.ResetKnownHost(request.Host, request.Port);

            var success = await _client.ConnectAsync(request.Host, request.Port, request.AuthKey, request.TrustNew);
            if (!success)
            {
                // 证书未确认/不匹配时认证密钥尚未被校验, 不计入登录失败次数
                if (!_client.CertMismatch && !_client.CertUntrusted)
                    LoginRateLimiter.RegisterFailure(ip);
                var reason = _client.LastError;
                var message = string.IsNullOrEmpty(reason)
                    ? "连接失败，请检查端口和密钥是否正确，以及服务器是否运行"
                    : $"连接失败：{reason}";
                return Ok(new
                {
                    success = false,
                    message,
                    certMismatch = _client.CertMismatch,
                    certUntrusted = _client.CertUntrusted,
                    fingerprint = _client.PendingFingerprint
                });
            }

            LoginRateLimiter.Reset(ip);

            // 创建认证票据，写入 cookie
            await HttpContext.SignInAsync("RtPanelCookie", BuildPrincipal(request));

            return Ok(new
            {
                success = true,
                message = "登录成功",
                serverName = _client.ConnectedServerName
            });
        }

        /// <summary>按登录目标构建面板认证票据</summary>
        private ClaimsPrincipal BuildPrincipal(LoginRequest request)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, $"panel@{request.Host}:{request.Port}"),
                new("ServerName", _client.ConnectedServerName ?? "unknown"),
            };
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "RtPanelCookie"));
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
        /// <summary>为 true 表示用户已确认首次连接的服务器证书指纹(TOFU 首连确认)</summary>
        public bool TrustNew { get; set; }
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
