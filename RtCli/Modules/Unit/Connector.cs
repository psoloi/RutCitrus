using RtCli.Modules;
using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Tasks;
using TouchSocket.Core;
using TouchSocket.Sockets;

namespace RtCli.Modules.Unit
{
    internal class Connector
    {
        private static TcpService? _server;
        private static TcpClient? _client;
        private static readonly ConcurrentDictionary<string, DateTime> _authenticatedClients = new();

        public static string ServerName { get; set; } = "RutCitrusServer";
        public static string ServerKey { get; set; } = "default_key";

        public static async Task StartServerAsync(int port, string serverName, string key)
        {
            ServerName = serverName;
            ServerKey = key;

            try
            {
                _server = new TcpService();
                _server.Received = async (client, e) =>
                {
                    var message = e.Memory.Span.ToString(Encoding.UTF8);
                    var clientId = client.Id;

                    if (message.StartsWith("AUTH:"))
                    {
                        var key = message.Substring(5);
                        if (key == ServerKey)
                        {
                            _authenticatedClients[clientId] = DateTime.Now;
                            await client.SendAsync($"AUTH_SUCCESS:{ServerName}");
                            Output.Log($"客户端 {clientId} 密钥验证成功", 1, "Connector");
                        }
                        else
                        {
                            await client.SendAsync("AUTH_FAIL");
                            Output.Log($"客户端 {clientId} 密钥验证失败", 2, "Connector");
                        }
                    }
                    else if (IsClientAuthenticated(clientId))
                    {
                        Output.Log($"收到客户端 {clientId} 消息: {message}", 1, "Connector");
                    }
                    else
                    {
                        await client.SendAsync("NOT_AUTHENTICATED");
                        Output.Log($"收到未验证客户端 {clientId} 消息，已拒绝", 2, "Connector");
                    }

                    await e.InvokeNext();
                };

                await _server.SetupAsync(new TouchSocketConfig()
                    .SetListenIPHosts(port)
                    .SetServerName(ServerName)
                    .ConfigurePlugins(a =>
                    {
                        a.Add<TcpServerConnectionPlugin>();
                    }));

                await _server.StartAsync();
                Output.Log($"服务器 [{ServerName}] 已启动，监听端口: {port}", 1, "Connector");
            }
            catch (Exception ex)
            {
                Output.Log($"服务器启动失败: {ex.Message}", 3, "Connector");
            }
        }

        public static void StopServer()
        {
            if (_server != null && _server.ServerState == ServerState.Running)
            {
                _server.StopAsync().Wait();
                _authenticatedClients.Clear();
                Output.Log($"服务器 [{ServerName}] 已停止", 1, "Connector");
            }
        }

        public static async Task ConnectClientAsync(string ip, int port, string key)
        {
            try
            {
                _client = new TcpClient();
                _client.Received = async (client, e) =>
                {
                    var message = e.Memory.Span.ToString(Encoding.UTF8);

                    if (message.StartsWith("AUTH_SUCCESS:"))
                    {
                        var serverName = message.Substring(13);
                        Output.Log($"密钥验证成功，已连接到服务器: {serverName}", 1, "Connector");
                    }
                    else if (message == "AUTH_FAIL")
                    {
                        Output.Log("密钥验证失败，连接将被关闭", 3, "Connector");
                    }
                    else if (message == "NOT_AUTHENTICATED")
                    {
                        Output.Log("尚未通过验证，请先发送密钥", 2, "Connector");
                    }
                    else
                    {
                        Output.Log($"收到服务器消息: {message}", 1, "Connector");
                    }

                    await e.InvokeNext();
                };

                await _client.SetupAsync(new TouchSocketConfig()
                    .SetRemoteIPHost($"tcp://{ip}:{port}")
                    .ConfigurePlugins(a =>
                    {
                        a.Add<TcpClientConnectionPlugin>();
                    }));

                await _client.ConnectAsync();
                Output.Log($"已连接到服务器 {ip}:{port}", 1, "Connector");

                var authMessage = $"AUTH:{key}";
                await _client.SendAsync(authMessage);
                Output.Log("已发送密钥验证请求", 1, "Connector");
            }
            catch (Exception ex)
            {
                Output.Log($"连接服务器失败: {ex.Message}", 3, "Connector");
            }
        }

        public static async Task DisconnectClientAsync()
        {
            if (_client != null && _client.Online)
            {
                await _client.CloseAsync();
                Output.Log("已断开与服务器的连接", 1, "Connector");
            }
        }

        public static async Task SendDataAsync(string data)
        {
            if (_client != null && _client.Online)
            {
                await _client.SendAsync(data);
                Output.Log($"已发送数据: {data}", 1, "Connector");
            }
            else
            {
                Output.Log("客户端未连接", 2, "Connector");
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
                Output.Log($"已广播数据到 {_authenticatedClients.Count} 个已验证客户端", 1, "Connector");
            }
        }

        public static bool IsClientAuthenticated(string clientId)
        {
            return _authenticatedClients.ContainsKey(clientId);
        }
    }

    internal class TcpServerConnectionPlugin : PluginBase, ITcpConnectedPlugin, ITcpClosedPlugin
    {
        public async Task OnTcpConnected(ITcpSession client, ConnectedEventArgs e)
        {
            if (client is IIdClient idClient)
            {
                Output.Log($"客户端 {idClient.Id} 已连接", 1, "Connector");
            }
            await e.InvokeNext();
        }

        public async Task OnTcpClosed(ITcpSession client, ClosedEventArgs e)
        {
            if (client is IIdClient idClient)
            {
                Output.Log($"客户端 {idClient.Id} 已断开连接", 1, "Connector");
            }
            await e.InvokeNext();
        }
    }

    internal class TcpClientConnectionPlugin : PluginBase, ITcpClosedPlugin
    {
        public async Task OnTcpClosed(ITcpSession client, ClosedEventArgs e)
        {
            Output.Log("与服务器的连接已断开", 2, "Connector");
            await e.InvokeNext();
        }
    }
}
