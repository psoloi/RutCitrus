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
        private static readonly ConcurrentDictionary<string, ClientInfo> _connectedClients = new();

        public static string ServerName => Config.App.ServerName;
        public static int ServerPort => Config.App.ServerPort;
        public static bool IsRunning => _server?.ServerState == ServerState.Running;
        public static int ConnectedClientCount => _connectedClients.Count;

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
                    ProcessMessage(client, e.Memory.Span);
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

        private static void ProcessMessage(TcpSessionClient client, ReadOnlySpan<byte> data)
        {
            try
            {
                var message = Encoding.UTF8.GetString(data);
                var clientId = client.Id;

                if (_connectedClients.TryGetValue(clientId, out var clientInfo))
                {
                    if (message.StartsWith("CMD:"))
                    {
                        var command = message.Substring(4);
                        Output.Log($"收到管理面板 [{clientInfo.IP}] 命令: {command}", 1, "Connector");
                        client.SendAsync($"RESULT:命令 '{command}' 执行完成").Wait();
                    }
                    else
                    {
                        Output.Log($"收到管理面板 [{clientInfo.IP}] 消息: {message}", 1, "Connector");
                    }
                }
            }
            catch (Exception ex)
            {
                Output.Log($"处理消息异常: {ex.Message}", 2, "Connector");
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
                _connectedClients.Clear();
                Output.Log($"服务器 [[{ServerName}]] 关闭", 1, "Connector");
            }
        }

        public static async Task BroadcastDataAsync(string data)
        {
            if (_server != null && _server.ServerState == ServerState.Running)
            {
                foreach (var clientId in _connectedClients.Keys)
                {
                    if (_server.Clients.TryGetClient(clientId, out var sessionClient))
                    {
                        await sessionClient.SendAsync(data);
                    }
                }
                Output.Log($"已广播数据到 {_connectedClients.Count} 个管理面板", 1, "Connector");
            }
        }

        internal static void OnClientConnected(string clientId, string clientIP)
        {
            _connectedClients[clientId] = new ClientInfo
            {
                ConnectTime = DateTime.Now,
                IP = clientIP
            };
            Output.Log($"管理面板 [{clientIP}] 已连接 (ID: {clientId})", 1, "Connector");
        }

        internal static void OnClientDisconnected(string clientId)
        {
            if (_connectedClients.TryRemove(clientId, out var clientInfo))
            {
                Output.Log($"管理面板 [{clientInfo.IP}] 已断开连接", 1, "Connector");
            }
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
