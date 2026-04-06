using RtCli.Modules;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using TouchSocket.Core;
using TouchSocket.Sockets;

namespace RtCli.Modules.Unit
{
    internal class Connector
    {
        private static TcpService? _server;
        private static readonly ConcurrentDictionary<string, ClientInfo> _authenticatedClients = new();
        private static readonly ConcurrentDictionary<string, DateTime> _pendingAuthClients = new();
        private static readonly int _authTimeoutSeconds = 15;

        public static string ServerName => Config.App.ServerName;
        public static string ServerKey => Config.App.ServerKey;
        public static int ServerPort => Config.App.ServerPort;
        public static bool IsRunning => _server?.ServerState == ServerState.Running;
        public static int AuthenticatedClientCount => _authenticatedClients.Count;

        public static async Task StartServerAsync()
        {
            Thread.CurrentThread.Name = "Net";

            if (_server != null && _server.ServerState == ServerState.Running)
            {
                Output.Log("暂时不支持在同个RtCli上开放多个管理端口", 2, "Connector");
                return;
            }

            try
            {
                _server = new TcpService();
                _server.Received = (client, e) =>
                {
                    try
                    {
                        var message = Encoding.UTF8.GetString(e.Memory.Span);
                        Output.Log($"[DEBUG] Received回调触发: '{message}'", 1, "Connector");
                        HandleReceivedSync(client, message);
                    }
                    catch (Exception ex)
                    {
                        Output.Log($"[DEBUG] Received异常: {ex.Message}", 2, "Connector");
                    }
                    return Task.CompletedTask;
                };

                await _server.SetupAsync(new TouchSocketConfig()
                    .SetListenIPHosts(new IPHost[] 
                    { 
                        new IPHost($"0.0.0.0:{ServerPort}"),
                        new IPHost($"[::]:{ServerPort}")
                    })
                    .SetServerName(ServerName)
                    .SetMaxCount(1000)
                    .ConfigurePlugins(a =>
                    {
                        a.Add<ServerConnectionPlugin>();
                    }));

                await _server.StartAsync();
                Output.Log($"服务器 [[{ServerName}]] 已启动管理端口: {ServerPort} (IPv4/IPv6)", 1, "Connector");
            }
            catch (Exception ex)
            {
                Output.Log($"服务器启动失败: {ex.Message}", 3, "Connector");
            }
        }

        private static void HandleReceivedSync(TcpSessionClient client, string message)
        {
            var clientId = client.Id;

            Output.Log($"[DEBUG] 处理消息: '{message}' 来自客户端: {clientId}", 1, "Connector");

            if (message.StartsWith("AUTH:"))
            {
                var key = message.Substring(5);
                Output.Log($"[DEBUG] AUTH请求, key长度: {key.Length}", 1, "Connector");
                
                if (key == ServerKey)
                {
                    _pendingAuthClients.TryRemove(clientId, out _);
                    var clientIP = GetClientIP(client);
                    _authenticatedClients[clientId] = new ClientInfo
                    {
                        ConnectTime = DateTime.Now,
                        IP = clientIP
                    };
                    Output.Log($"[DEBUG] 验证成功，已添加到authenticatedClients", 1, "Connector");
                    client.SendAsync($"AUTH_SUCCESS:{ServerName}").Wait();
                    Output.Log($"管理面板 [{clientIP}] 密钥验证成功", 1, "Connector");
                }
                else
                {
                    var clientIP = GetClientIP(client);
                    client.SendAsync("AUTH_FAIL").Wait();
                    Output.Log($"管理面板 [{clientIP}] 密钥验证失败，已断开连接", 2, "Connector");
                    client.CloseAsync().Wait();
                }
            }
            else
            {
                var isAuthenticated = IsClientAuthenticated(clientId);
                Output.Log($"[DEBUG] 非AUTH消息, isAuthenticated: {isAuthenticated}", 1, "Connector");
                
                if (isAuthenticated)
                {
                    var clientInfo = _authenticatedClients[clientId];
                    Output.Log($"收到管理面板 [{clientInfo.IP}] 消息: {message}", 1, "Connector");
                }
                else
                {
                    var clientIP = GetClientIP(client);
                    client.SendAsync("NOT_AUTHENTICATED").Wait();
                    Output.Log($"拒绝未验证管理面板 [{clientIP}] 的消息: {message}", 2, "Connector");
                    client.CloseAsync().Wait();
                }
            }
        }

        internal static string GetClientIP(TcpSessionClient client)
        {
            try
            {
                if (client.IP != null)
                {
                    return client.IP.ToString();
                }
            }
            catch { }
            return "未知";
        }

        public static async Task StopServerAsync()
        {
            Thread.CurrentThread.Name = "Net";
            if (_server != null && _server.ServerState == ServerState.Running)
            {
                await _server.StopAsync();
                _authenticatedClients.Clear();
                _pendingAuthClients.Clear();
                Output.Log($"服务器 [[{ServerName}]] 关闭", 1, "Connector");
            }
        }

        public static async Task BroadcastDataAsync(string data)
        {
            if (_server != null && _server.ServerState == ServerState.Running)
            {
                foreach (var clientId in _authenticatedClients.Keys)
                {
                    if (_server.Clients.TryGetClient(clientId, out var sessionClient))
                    {
                        await sessionClient.SendAsync(data);
                    }
                }
                Output.Log($"已广播数据到 {_authenticatedClients.Count} 个已验证管理面板", 1, "Connector");
            }
        }

        public static bool IsClientAuthenticated(string clientId)
        {
            return _authenticatedClients.ContainsKey(clientId);
        }

        internal static async Task CheckAuthTimeoutAsync(string clientId)
        {
            await Task.Delay(_authTimeoutSeconds * 1000);

            if (IsClientAuthenticated(clientId))
            {
                Output.Log($"[DEBUG] 超时检查: 客户端已验证，跳过", 1, "Connector");
                return;
            }

            if (_pendingAuthClients.TryRemove(clientId, out _))
            {
                if (_server?.Clients.TryGetClient(clientId, out var client) == true)
                {
                    var ip = GetClientIP(client);
                    Output.Log($"管理面板 [{ip}] 验证超时，已断开连接", 2, "Connector");
                    try
                    {
                        await client.CloseAsync();
                    }
                    catch { }
                }
            }
        }

        internal static void OnClientConnected(string clientId, string clientIP)
        {
            _pendingAuthClients[clientId] = DateTime.Now;
            Output.Log($"管理面板 [{clientIP}] 已连接，等待验证... (ID: {clientId})", 1, "Connector");
            _ = CheckAuthTimeoutAsync(clientId);
        }

        internal static void OnClientDisconnected(string clientId)
        {
            Output.Log($"[DEBUG] 客户端断开: {clientId}", 1, "Connector");
            if (_authenticatedClients.TryRemove(clientId, out var clientInfo))
            {
                Output.Log($"管理面板 [{clientInfo.IP}] 已断开连接", 1, "Connector");
            }
            _pendingAuthClients.TryRemove(clientId, out _);
        }
    }

    internal class ClientInfo
    {
        public DateTime ConnectTime { get; set; }
        public string IP { get; set; } = "";
    }

    internal class ServerConnectionPlugin : PluginBase, ITcpConnectedPlugin, ITcpClosedPlugin
    {
        public async Task OnTcpConnected(ITcpSession client, ConnectedEventArgs e)
        {
            Thread.CurrentThread.Name = "Net";
            if (client is IIdClient idClient)
            {
                var clientIP = "未知";
                if (client is TcpSessionClient sessionClient)
                {
                    clientIP = Connector.GetClientIP(sessionClient);
                }
                Connector.OnClientConnected(idClient.Id, clientIP);
            }
            await e.InvokeNext();
        }

        public async Task OnTcpClosed(ITcpSession client, ClosedEventArgs e)
        {
            Thread.CurrentThread.Name = "Net";
            if (client is IIdClient idClient)
            {
                Connector.OnClientDisconnected(idClient.Id);
            }
            await e.InvokeNext();
        }
    }
}
