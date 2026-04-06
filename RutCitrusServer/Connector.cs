using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TouchSocket.Core;
using TouchSocket.Sockets;

namespace RutCitrusServer
{
    internal class Connector
    {
        private static TcpClient? _client;
        private static bool _isAuthenticated = false;
        private static CancellationTokenSource? _connectCts;
        private static CancellationTokenSource? _authCts;
        private static int _connectTimeoutMs = 5000;
        private static int _authTimeoutMs = 15000;

        public static bool IsConnected => _client?.Online ?? false;
        public static bool IsAuthenticated => _isAuthenticated;
        public static string? ConnectedServerName { get; private set; }
        public static int ConnectTimeoutMs
        {
            get => _connectTimeoutMs;
            set => _connectTimeoutMs = value > 0 ? value : 5000;
        }
        public static int AuthTimeoutMs
        {
            get => _authTimeoutMs;
            set => _authTimeoutMs = value > 0 ? value : 15000;
        }

        public static event Action<string>? OnMessageReceived;
        public static event Action? OnConnected;
        public static event Action? OnDisconnected;
        public static event Action<string>? OnAuthenticated;
        public static event Action<string>? OnAuthFailed;
        public static event Action? OnConnectTimeout;
        public static event Action? OnAuthTimeout;

        private static void LogDebug(string message)
        {
            OnMessageReceived?.Invoke(message);
        }

        public static async Task<bool> ConnectAsync(string ip, int port, string key)
        {
            try
            {
                _connectCts?.Cancel();
                _connectCts?.Dispose();
                _authCts?.Cancel();
                _authCts?.Dispose();
                _connectCts = new CancellationTokenSource();

                if (_client != null && _client.Online)
                {
                    try { await _client.CloseAsync(); } catch { }
                }

                _client = new TcpClient();
                _isAuthenticated = false;
                ConnectedServerName = null;

                _client.Received = (client, e) =>
                {
                    try
                    {
                        var message = Encoding.UTF8.GetString(e.Memory.Span);
                        LogDebug($"[DEBUG] 收到服务器消息: '{message}'");

                        if (message.StartsWith("AUTH_SUCCESS:"))
                        {
                            _authCts?.Cancel();
                            ConnectedServerName = message.Substring(13);
                            _isAuthenticated = true;
                            LogDebug($"[DEBUG] 验证成功，服务器: {ConnectedServerName}");
                            OnAuthenticated?.Invoke(ConnectedServerName);
                        }
                        else if (message == "AUTH_FAIL")
                        {
                            _authCts?.Cancel();
                            _isAuthenticated = false;
                            OnAuthFailed?.Invoke("密钥验证失败");
                            LogDebug("密钥验证失败，服务器已断开连接");
                        }
                        else if (message == "NOT_AUTHENTICATED")
                        {
                            _authCts?.Cancel();
                            LogDebug("未通过验证，服务器已断开连接");
                        }
                        else if (message.StartsWith("RESULT:"))
                        {
                            LogDebug(message.Substring(7));
                        }
                        else
                        {
                            LogDebug(message);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"[DEBUG] Received回调异常: {ex.Message}");
                    }

                    return Task.CompletedTask;
                };

                await _client.SetupAsync(new TouchSocketConfig()
                    .SetRemoteIPHost($"tcp://{ip}:{port}")
                    .ConfigurePlugins(a =>
                    {
                        a.Add<ClientConnectionPlugin>();
                    }));

                await _client.ConnectAsync();

                OnConnected?.Invoke();
                LogDebug($"[DEBUG] TCP连接成功，准备发送AUTH");

                await _client.SendAsync($"AUTH:{key}");
                LogDebug($"[DEBUG] 已发送AUTH消息");

                _authCts = new CancellationTokenSource();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(_authTimeoutMs, _authCts.Token);
                        if (!_isAuthenticated && _client?.Online == true)
                        {
                            OnAuthTimeout?.Invoke();
                            LogDebug($"验证超时 ({_authTimeoutMs}ms)");
                            await DisconnectAsync();
                        }
                    }
                    catch (OperationCanceledException) { }
                });

                return true;
            }
            catch (OperationCanceledException)
            {
                LogDebug("连接已取消");
                return false;
            }
            catch (Exception ex)
            {
                LogDebug($"连接失败: {ex.Message}");
                OnDisconnected?.Invoke();
                return false;
            }
        }

        public static async Task DisconnectAsync()
        {
            LogDebug($"[DEBUG] DisconnectAsync被调用");
            _connectCts?.Cancel();
            _authCts?.Cancel();
            if (_client != null && _client.Online)
            {
                try { await _client.CloseAsync(); } catch { }
            }
            _isAuthenticated = false;
            ConnectedServerName = null;
            OnDisconnected?.Invoke();
        }

        public static async Task<bool> SendCommandAsync(string command)
        {
            if (_client == null || !_client.Online || !_isAuthenticated)
            {
                return false;
            }

            try
            {
                await _client.SendAsync($"CMD:{command}");
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static async Task<bool> SendDataAsync(string data)
        {
            if (_client == null || !_client.Online || !_isAuthenticated)
            {
                return false;
            }

            try
            {
                await _client.SendAsync(data);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static void OnServerDisconnected()
        {
            LogDebug($"[DEBUG] OnServerDisconnected被调用, Online={_client?.Online}, Auth={_isAuthenticated}");
            _authCts?.Cancel();
            _isAuthenticated = false;
            ConnectedServerName = null;
            OnDisconnected?.Invoke();
        }
    }

    internal class ClientConnectionPlugin : PluginBase, ITcpClosedPlugin
    {
        public Task OnTcpClosed(ITcpSession client, ClosedEventArgs e)
        {
            Connector.OnServerDisconnected();
            return e.InvokeNext();
        }
    }
}
