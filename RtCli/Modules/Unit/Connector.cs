using Grpc.Core;
using RtCli.Grpc;
using RtCli.Modules;
using RtExtensionManager;
using Spectre.Console;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace RtCli.Modules.Unit
{
    internal class Connector
    {
        private static Server? _grpcServer;
        private static readonly ConcurrentDictionary<string, ClientInfo> _connectedClients = new();
        private static readonly List<IServerStreamWriter<LogMessage>> _logSubscribers = new();
        private static readonly object _subscribersLock = new();

        public static string ServerName => Config.App.ServerName;
        public static int ServerPort => Config.App.ServerPort;
        public static bool IsRunning => _grpcServer != null;
        public static int ConnectedClientCount => _connectedClients.Count;
        public static IReadOnlyDictionary<string, ClientInfo> ConnectedClients => _connectedClients;

        public static async Task StartServerAsync()
        {
            if (_grpcServer != null)
            {
                Output.Log("gRPC服务器已在运行中", 2, "Connector");
                return;
            }

            try
            {
                var service = new RtCliServiceImpl();

                _grpcServer = new Server
                {
                    Services = { RtCliService.BindService(service) },
                    Ports = { new ServerPort("0.0.0.0", ServerPort, ServerCredentials.Insecure) }
                };

                _grpcServer.Start();

                Output.Log($"gRPC服务器 [[{ServerName}]] 已启动端口: {ServerPort}", 1, "Connector");
            }
            catch (Exception ex)
            {
                Output.Log($"gRPC服务器启动失败: {ex.Message}", 3, "Connector");
            }
        }

        public static async Task StopServerAsync()
        {
            if (_grpcServer != null)
            {
                await _grpcServer.ShutdownAsync();
                _grpcServer = null;
                _connectedClients.Clear();
                Output.Log($"gRPC服务器 [[{ServerName}]] 已关闭", 1, "Connector");
            }
        }

        public static async Task BroadcastLogAsync(string timestamp, int level, string source, string message)
        {
            var logMsg = new LogMessage
            {
                Timestamp = timestamp,
                Level = level,
                Source = source,
                Message = message
            };

            lock (_subscribersLock)
            {
                var brokenSubscribers = new List<IServerStreamWriter<LogMessage>>();
                foreach (var subscriber in _logSubscribers)
                {
                    try
                    {
                        subscriber.WriteAsync(logMsg).Wait();
                    }
                    catch
                    {
                        brokenSubscribers.Add(subscriber);
                    }
                }
                foreach (var broken in brokenSubscribers)
                {
                    _logSubscribers.Remove(broken);
                }
            }
        }

        internal static void AddLogSubscriber(IServerStreamWriter<LogMessage> subscriber)
        {
            lock (_subscribersLock)
            {
                _logSubscribers.Add(subscriber);
            }
        }

        internal static void RemoveLogSubscriber(IServerStreamWriter<LogMessage> subscriber)
        {
            lock (_subscribersLock)
            {
                _logSubscribers.Remove(subscriber);
            }
        }

        internal static string RegisterClient(string peer)
        {
            var clientId = Guid.NewGuid().ToString("N")[..8];
            var ip = ParseIPFromPeer(peer);
            var safeIP = Markup.Escape(ip);
            _connectedClients[clientId] = new ClientInfo
            {
                ConnectTime = DateTime.Now,
                IP = ip,
                Peer = peer
            };
            Output.Log($"管理面板 ({safeIP}) 已连接 (ID: {clientId})", 1, "Connector");
            return clientId;
        }

        internal static void UnregisterClient(string clientId)
        {
            if (_connectedClients.TryRemove(clientId, out var clientInfo))
            {
                var safeIP = Markup.Escape(clientInfo.IP);
                Output.Log($"管理面板 ({safeIP}) 已断开连接 (ID: {clientId})", 1, "Connector");
            }
        }

        private static string ParseIPFromPeer(string peer)
        {
            try
            {
                var parts = peer.Split(':');
                if (parts.Length >= 2)
                {
                    var ipPart = string.Join(":", parts[..^1]);
                    if (ipPart.StartsWith("//")) ipPart = ipPart[2..];
                    return ipPart;
                }
            }
            catch { }
            return "未知";
        }
    }

    internal class ClientInfo
    {
        public DateTime ConnectTime { get; set; }
        public string IP { get; set; } = "";
        public string Peer { get; set; } = "";
    }

    internal class RtCliServiceImpl : RtCliService.RtCliServiceBase
    {
        public override Task<ServerInfoResponse> GetServerInfo(Empty request, ServerCallContext context)
        {
            EnsureClientTracked(context);
            var response = new ServerInfoResponse
            {
                ServerName = Config.App.ServerName,
                Version = Program.RtCliVersion,
                Port = Config.App.ServerPort,
                IsRunning = Connector.IsRunning
            };
            return Task.FromResult(response);
        }

        public override Task<ExtensionListResponse> GetExtensions(Empty request, ServerCallContext context)
        {
            EnsureClientTracked(context);
            var json = RtExtensionManager.RtExtensionManager.GetExtensionsJson();
            var extensions = Newtonsoft.Json.JsonConvert.DeserializeObject<List<ExtensionData>>(json);

            var response = new ExtensionListResponse();
            if (extensions != null)
            {
                foreach (var ext in extensions)
                {
                    response.Extensions.Add(new Grpc.ExtensionInfo
                    {
                        Key = ext.Key ?? "",
                        Name = ext.Name ?? "",
                        Version = ext.Version ?? "",
                        Description = ext.Description ?? "",
                        LoadTime = ext.LoadTime ?? ""
                    });
                }
            }

            return Task.FromResult(response);
        }

        public override Task<UnloadExtensionResponse> UnloadExtension(UnloadExtensionRequest request, ServerCallContext context)
        {
            EnsureClientTracked(context);
            bool success = RtExtensionManager.RtExtensionManager.UnloadExtensionByKey(request.ExtensionKey);
            return Task.FromResult(new UnloadExtensionResponse
            {
                Success = success,
                Message = success ? $"扩展 {request.ExtensionKey} 卸载成功" : $"扩展 {request.ExtensionKey} 卸载失败"
            });
        }

        public override Task<LoadExtensionResponse> LoadExtension(LoadExtensionRequest request, ServerCallContext context)
        {
            EnsureClientTracked(context);
            bool success = RtExtensionManager.RtExtensionManager.LoadExtensionByKey(request.ExtensionPath);
            return Task.FromResult(new LoadExtensionResponse
            {
                Success = success,
                Message = success ? "扩展加载成功" : "扩展加载失败"
            });
        }

        public override Task<ExecuteCommandResponse> ExecuteCommand(ExecuteCommandRequest request, ServerCallContext context)
        {
            EnsureClientTracked(context);
            return Task.FromResult(new ExecuteCommandResponse
            {
                Success = true,
                Result = $"命令 '{request.Command}' 已接收"
            });
        }

        public override async Task StreamLogs(Empty request, IServerStreamWriter<LogMessage> responseStream, ServerCallContext context)
        {
            var clientId = EnsureClientTracked(context);
            Connector.AddLogSubscriber(responseStream);

            try
            {
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                Connector.RemoveLogSubscriber(responseStream);
                Connector.UnregisterClient(clientId);
            }
        }

        public override Task<ClientListResponse> GetClients(Empty request, ServerCallContext context)
        {
            EnsureClientTracked(context);
            var response = new ClientListResponse();
            foreach (var kvp in Connector.ConnectedClients)
            {
                response.Clients.Add(new Grpc.ClientInfo
                {
                    Id = kvp.Key,
                    Ip = kvp.Value.IP,
                    ConnectTime = kvp.Value.ConnectTime.ToString("yyyy-MM-dd HH:mm:ss")
                });
            }
            return Task.FromResult(response);
        }

        public override Task<StatusResponse> GetStatus(Empty request, ServerCallContext context)
        {
            EnsureClientTracked(context);
            return Task.FromResult(new StatusResponse
            {
                IsRunning = Connector.IsRunning,
                ConnectedClientCount = Connector.ConnectedClientCount,
                ExtensionCount = RtExtensionManager.RtExtensionManager.GetExtensionCount(),
                ServerName = Config.App.ServerName
            });
        }

        private static readonly ConcurrentDictionary<string, string> _peerToClientId = new();

        private static string EnsureClientTracked(ServerCallContext context)
        {
            var peer = context.Peer;
            if (_peerToClientId.TryGetValue(peer, out var existingId))
            {
                if (Connector.ConnectedClients.ContainsKey(existingId))
                {
                    return existingId;
                }
                _peerToClientId.TryRemove(peer, out _);
            }

            var clientId = Connector.RegisterClient(peer);
            _peerToClientId[peer] = clientId;

            context.CancellationToken.Register(() =>
            {
                _peerToClientId.TryRemove(peer, out _);
                Connector.UnregisterClient(clientId);
            });

            return clientId;
        }
    }

    internal class ExtensionData
    {
        public string? Key { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        public string? LoadTime { get; set; }
    }
}
