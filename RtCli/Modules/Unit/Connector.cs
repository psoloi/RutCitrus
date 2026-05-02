using RtCli.Modules;
using RtExtensionManager;
using Spectre.Console;
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
                    var safeIP = Markup.Escape(clientInfo.IP);
                    
                    if (message.StartsWith("CMD:"))
                    {
                        var command = message.Substring(4);
                        Output.Log($"收到管理面板 ({safeIP}) 命令: {command}", 1, "Connector");
                        HandleCommand(client, command);
                    }
                    else if (message.StartsWith("EXT_GET:"))
                    {
                        Output.Log($"收到管理面板 ({safeIP}) 请求扩展列表", 1, "Connector");
                        var json = RtExtensionManager.RtExtensionManager.GetExtensionsJson();
                        client.SendAsync($"EXT_LIST:{json}").Wait();
                    }
                    else if (message.StartsWith("EXT_UNLOAD:"))
                    {
                        var extensionKey = message.Substring(11);
                        Output.Log($"收到管理面板 ({safeIP}) 卸载扩展请求: {extensionKey}", 1, "Connector");
                        bool success = RtExtensionManager.RtExtensionManager.UnloadExtensionByKey(extensionKey);
                        client.SendAsync($"EXT_UNLOAD_RESULT:{(success ? "SUCCESS" : "FAILED")}:{extensionKey}").Wait();
                    }
                    else
                    {
                        Output.Log($"收到管理面板 ({safeIP}) 消息: {message}", 1, "Connector");
                    }
                }
            }
            catch (Exception ex)
            {
                Output.Log($"处理消息异常: {ex.Message}", 2, "Connector");
            }
        }

        private static void HandleCommand(TcpSessionClient client, string command)
        {
            try
            {
                var parts = command.Split(' ', 2);
                var cmd = parts[0].ToLower();
                var args = parts.Length > 1 ? parts[1] : "";

                switch (cmd)
                {
                    case "extensions":
                        var json = RtExtensionManager.RtExtensionManager.GetExtensionsJson();
                        client.SendAsync($"RESULT:{json}").Wait();
                        break;
                    case "unload":
                        if (!string.IsNullOrEmpty(args))
                        {
                            bool success = RtExtensionManager.RtExtensionManager.UnloadExtensionByKey(args);
                            client.SendAsync($"RESULT:{(success ? $"扩展 {args} 卸载成功" : $"扩展 {args} 卸载失败")}").Wait();
                        }
                        else
                        {
                            client.SendAsync("RESULT:请指定要卸载的扩展Key").Wait();
                        }
                        break;
                    default:
                        client.SendAsync($"RESULT:命令 '{command}' 执行完成").Wait();
                        break;
                }
            }
            catch (Exception ex)
            {
                client.SendAsync($"RESULT:命令执行错误: {ex.Message}").Wait();
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
            var safeIP = Markup.Escape(clientIP);
            _connectedClients[clientId] = new ClientInfo
            {
                ConnectTime = DateTime.Now,
                IP = clientIP
            };
            Output.Log($"管理面板 ({safeIP}) 已连接 (ID: {clientId})", 1, "Connector");
        }

        internal static void OnClientDisconnected(string clientId)
        {
            if (_connectedClients.TryRemove(clientId, out var clientInfo))
            {
                var safeIP = Markup.Escape(clientInfo.IP);
                Output.Log($"管理面板 ({safeIP}) 已断开连接", 1, "Connector");
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
