using Grpc.Core;
using RtCli.Grpc;
using RtCli.Modules;
using RtCli.Modules.Extension;
using RtCli.Modules.Function;
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

        public static string ServerName => "RtCli";
        public static int ServerPort => Config.App.GrpcPort;
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

                Output.Log($"gRPC服务器已启动端口: {ServerPort}", 1, "Connector");
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
                Output.Log("gRPC服务器已关闭", 1, "Connector");
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

            List<IServerStreamWriter<LogMessage>> subscribersCopy;
            lock (_subscribersLock)
            {
                subscribersCopy = new List<IServerStreamWriter<LogMessage>>(_logSubscribers);
            }

            var brokenSubscribers = new List<IServerStreamWriter<LogMessage>>();
            foreach (var subscriber in subscribersCopy)
            {
                try
                {
                    await subscriber.WriteAsync(logMsg);
                }
                catch
                {
                    brokenSubscribers.Add(subscriber);
                }
            }

            if (brokenSubscribers.Count > 0)
            {
                lock (_subscribersLock)
                {
                    foreach (var broken in brokenSubscribers)
                    {
                        _logSubscribers.Remove(broken);
                    }
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
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var response = new ServerInfoResponse
            {
                ServerName = Config.CurrentServer.ServerName,
                Version = Program.RtCliVersion,
                Port = Config.App.GrpcPort,
                IsRunning = Connector.IsRunning
            };
            return Task.FromResult(response);
        }

        public override Task<ExtensionListResponse> GetExtensions(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var json = RtExtensionManager.GetExtensionsJson();
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
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            bool success = RtExtensionManager.UnloadExtensionByKey(request.ExtensionKey);
            return Task.FromResult(new UnloadExtensionResponse
            {
                Success = success,
                Message = success ? $"扩展 {request.ExtensionKey} 卸载成功" : $"扩展 {request.ExtensionKey} 卸载失败"
            });
        }

        public override Task<LoadExtensionResponse> LoadExtension(LoadExtensionRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            bool success = RtExtensionManager.LoadExtensionByKey(request.ExtensionPath);
            return Task.FromResult(new LoadExtensionResponse
            {
                Success = success,
                Message = success ? "扩展加载成功" : "扩展加载失败"
            });
        }

        public override Task<ExecuteCommandResponse> ExecuteCommand(ExecuteCommandRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, result) = Backend.DispatchCommand(request.Command);
            return Task.FromResult(new ExecuteCommandResponse
            {
                Success = success,
                Result = result
            });
        }

        public override async Task StreamLogs(Empty request, IServerStreamWriter<LogMessage> responseStream, ServerCallContext context)
        {
            ValidateAuthKey(context);
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
            ValidateAuthKey(context);
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
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            return Task.FromResult(new StatusResponse
            {
                IsRunning = Connector.IsRunning,
                ConnectedClientCount = Connector.ConnectedClientCount,
                ExtensionCount = RtExtensionManager.GetExtensionCount(),
                ServerName = Config.CurrentServer.ServerName
            });
        }

        public override Task<ConfigResponse> GetConfig(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, content, message) = Backend.GetConfigContent();
            return Task.FromResult(new ConfigResponse
            {
                Success = success,
                Content = content ?? "",
                Message = message
            });
        }

        public override Task<SaveConfigResponse> SaveConfig(SaveConfigRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message) = Backend.SaveConfigContent(request.Content);
            return Task.FromResult(new SaveConfigResponse
            {
                Success = success,
                Message = message
            });
        }

        public override Task<ConfigFileListResponse> ListConfigFiles(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var response = new ConfigFileListResponse();
            foreach (var (fileName, displayName, description) in Backend.ListConfigFiles())
            {
                response.Files.Add(new Grpc.ConfigFileInfo
                {
                    FileName = fileName,
                    DisplayName = displayName,
                    Description = description
                });
            }
            return Task.FromResult(response);
        }

        public override Task<ConfigResponse> GetConfigFile(ConfigFileRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, content, message) = Backend.GetConfigFileContent(request.FileName);
            return Task.FromResult(new ConfigResponse
            {
                Success = success,
                Content = content ?? "",
                Message = message
            });
        }

        public override Task<SaveConfigResponse> SaveConfigFile(SaveConfigFileRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message) = Backend.SaveConfigFileContent(request.FileName, request.Content);
            return Task.FromResult(new SaveConfigResponse
            {
                Success = success,
                Message = message
            });
        }

        public override Task<SystemStatsResponse> GetSystemStats(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message, cpu, memUsed, memTotal, servers) = Backend.GetSystemStats();
            var response = new SystemStatsResponse
            {
                Success = success,
                Message = message,
                CpuUsage = cpu,
                MemoryUsedMb = memUsed,
                MemoryTotalMb = memTotal,
                CurrentServerKey = Config.App.CurrentServer ?? "",
                AiRunning = Intelligence.AiAutoRunner.IsRunning,
                SchedulerRunning = Scheduler.IsRunning
            };
            foreach (var s in servers)
            {
                response.Servers.Add(new Grpc.ServerTpsInfo
                {
                    ServerKey = s.Key,
                    ServerName = s.Name,
                    IsRunning = s.Running,
                    Tps = s.Tps,
                    TpsStatus = s.TpsStatus,
                    McPid = s.Pid,
                    McMemoryMb = s.McMemMb,
                    Tps1M = s.Tps1m,
                    Tps5M = s.Tps5m,
                    Tps15M = s.Tps15m,
                    McCpuUsage = s.McCpuUsage,
                    PlayerCount = s.PlayerCount,
                    PlayerMax = s.PlayerMax
                });
            }
            return Task.FromResult(response);
        }

        public override Task<TaskListResponse> GetTaskList(GetTaskListRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var response = new TaskListResponse { Success = true };

            if (string.Equals(request.TaskType, "ai", StringComparison.OrdinalIgnoreCase))
            {
                response.IsRunning = Intelligence.AiAutoRunner.IsRunning;
                var config = ContentManager.Ai.ServerAutoAi;
                if (config?.Tasks != null)
                {
                    foreach (var kv in config.Tasks)
                    {
                        var lastRun = Intelligence.AiAutoRunner.LastRunTime.TryGetValue(kv.Key, out var t) ? t.ToString("MM-dd HH:mm:ss") : "-";
                        int count = Intelligence.AiAutoRunner.RunCount.TryGetValue(kv.Key, out var c) ? c : 0;
                        response.Tasks.Add(new Grpc.TaskItem
                        {
                            Key = kv.Key,
                            Name = kv.Value.Name,
                            Enabled = kv.Value.Enabled,
                            Trigger = kv.Value.Trigger,
                            Interval = kv.Value.Interval,
                            Execute = string.Join(", ", kv.Value.Actions),
                            RunCount = count,
                            LastRun = lastRun,
                            Status = kv.Value.Enabled ? "启用" : "禁用"
                        });
                    }
                }
            }
            else if (string.Equals(request.TaskType, "auto", StringComparison.OrdinalIgnoreCase))
            {
                response.IsRunning = Scheduler.IsRunning;
                foreach (var kvp in Scheduler.Settings.Tasks)
                {
                    string status = kvp.Value.Enabled ? "启用" : "禁用";
                    if (kvp.Value.ServerKeys.Count > 0 && !kvp.Value.ServerKeys.Contains(Config.App.CurrentServer))
                        status = "不适用";
                    if (!string.IsNullOrEmpty(kvp.Value.Rules.ExpireAt) &&
                        DateTime.TryParse(kvp.Value.Rules.ExpireAt, out var exp) && DateTime.Now > exp)
                        status = "已过期";
                    int count = Scheduler.GetExecutionCount(kvp.Key);
                    if (kvp.Value.Rules.MaxExecutions > 0 && count >= kvp.Value.Rules.MaxExecutions)
                        status = "已完成";

                    response.Tasks.Add(new Grpc.TaskItem
                    {
                        Key = kvp.Key,
                        Name = kvp.Key,
                        Enabled = kvp.Value.Enabled,
                        Trigger = kvp.Value.Trigger,
                        Interval = kvp.Value.Condition,
                        Execute = kvp.Value.Execute,
                        RunCount = count,
                        LastRun = "-",
                        Status = status
                    });
                }
            }
            else
            {
                response.Success = false;
                response.Message = $"未知任务类型: {request.TaskType}";
            }

            return Task.FromResult(response);
        }

        public override Task<RtStatusResponse> GetRtStatus(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            return Task.FromResult(new RtStatusResponse
            {
                Success = true,
                ManagementPortRunning = Connector.IsRunning,
                ConnectedClients = Connector.ConnectedClientCount,
                CurrentServerKey = Config.App.CurrentServer ?? "",
                CurrentServerName = Config.CurrentServer?.ServerName ?? "",
                McMode = Analyzer.CurrentMode ?? "",
                McRunning = Analyzer.IsRunModeActive,
                McConnected = Analyzer.IsAttached,
                AutoTipsRunning = Intelligence.IsTipsRunning,
                AiRunning = Intelligence.AiAutoRunner.IsRunning,
                SchedulerRunning = Scheduler.IsRunning
            });
        }

        public override Task<PanelDataResponse> GetPanelData(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            return Task.FromResult(new PanelDataResponse
            {
                Success = true,
                JsonData = PanelDataManager.Load()
            });
        }

        public override Task<PanelDataResponse> SavePanelData(PanelDataRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var result = PanelDataManager.Save(request.JsonData);
            return Task.FromResult(new PanelDataResponse
            {
                Success = result.Success,
                Message = result.Message,
                JsonData = request.JsonData
            });
        }

        public override Task<ConfigDocsResponse> GetConfigDocs(ConfigFileRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var docs = Repository.GetDocs(request.FileName);
            var response = new ConfigDocsResponse
            {
                Success = true,
                FileName = request.FileName
            };
            foreach (var kvp in docs)
                response.Docs[kvp.Key] = kvp.Value;
            return Task.FromResult(response);
        }

        public override Task<ServerFileListResponse> ListServerFiles(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message, serverKey, serverName, workPath, files) = Backend.ListServerFiles("");
            var response = new ServerFileListResponse
            {
                Success = success,
                Message = message,
                ServerKey = serverKey,
                ServerName = serverName,
                WorkPath = workPath
            };
            foreach (var f in files)
            {
                response.Files.Add(new Grpc.ServerFileInfo
                {
                    FileName = f.FileName,
                    DisplayName = f.DisplayName,
                    Description = f.Description,
                    Category = f.Category,
                    Editable = f.Editable,
                    SizeBytes = f.SizeBytes
                });
            }
            return Task.FromResult(response);
        }

        public override Task<ServerFileResponse> GetServerFile(ServerFileRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message, content, category) = Backend.GetServerFile(request.ServerKey, request.FileName);
            return Task.FromResult(new ServerFileResponse
            {
                Success = success,
                Message = message,
                Content = content ?? "",
                FileName = request.FileName,
                Category = category
            });
        }

        public override Task<SaveConfigResponse> SaveServerFile(SaveServerFileRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message) = Backend.SaveServerFile(request.ServerKey, request.FileName, request.Content);
            return Task.FromResult(new SaveConfigResponse
            {
                Success = success,
                Message = message
            });
        }

        public override Task<CommandListResponse> GetCommandList(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var response = new CommandListResponse();
            foreach (var (command, description) in Backend.GetAvailableCommands())
            {
                response.Commands.Add(new Grpc.CommandInfo
                {
                    Command = command,
                    Description = description
                });
            }
            return Task.FromResult(response);
        }

        // ===== 实例管理 gRPC 实现 =====

        public override Task<InstanceListResponse> GetInstanceList(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message, instances, groups) = Backend.GetInstanceList();
            var response = new InstanceListResponse { Success = success, Message = message };
            foreach (var inst in instances)
            {
                response.Instances.Add(new Grpc.InstanceInfo
                {
                    Id = inst.Id, Identifier = inst.Identifier, Name = inst.Name,
                    GroupId = inst.GroupId, GroupName = inst.GroupName, IsRunning = inst.IsRunning,
                    Hidden = inst.Hidden, AutoRestart = inst.AutoRestart,
                    WorkPath = inst.WorkPath, JavaPath = inst.JavaPath, RunFlags = inst.RunFlags,
                    AnalyzerMode = inst.AnalyzerMode, CreatedAt = inst.CreatedAt, LastStartedAt = inst.LastStartedAt
                });
            }
            foreach (var g in groups)
            {
                response.Groups.Add(new Grpc.GroupInfo
                {
                    Id = g.Id, Name = g.Name, CreatedAt = g.CreatedAt
                });
                foreach (var mid in g.MemberIds) response.Groups.Last().MemberIds.Add(mid);
            }
            return Task.FromResult(response);
        }

        public override Task<InstanceActionResponse> CreateInstance(CreateInstanceRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message) = ServerDataManager.CreateInstance(
                request.Identifier, request.Name, request.WorkPath, request.JavaPath,
                request.RunFlags, request.AnalyzerMode, request.GroupId);
            return Task.FromResult(new InstanceActionResponse { Success = success, Message = message });
        }

        public override Task<InstanceActionResponse> DeleteInstance(DeleteInstanceRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message) = ServerDataManager.DeleteInstance(request.Id, request.DeleteFiles);
            return Task.FromResult(new InstanceActionResponse { Success = success, Message = message });
        }

        public override Task<InstanceActionResponse> UpdateInstance(UpdateInstanceRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message) = ServerDataManager.UpdateInstance(
                request.Id, request.Name, request.GroupId, request.Hidden, request.AutoRestart,
                request.WorkPath, request.JavaPath, request.RunFlags, request.AnalyzerMode);
            return Task.FromResult(new InstanceActionResponse { Success = success, Message = message });
        }

        public override Task<InstanceActionResponse> InstanceAction(InstanceActionRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message) = Backend.InstanceAction(request.Action, request.Ids.ToList());
            return Task.FromResult(new InstanceActionResponse { Success = success, Message = message });
        }

        public override Task<InstanceActionResponse> CreateGroup(CreateGroupRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message, _) = ServerDataManager.CreateGroup(request.Name, request.MemberIds.ToList());
            return Task.FromResult(new InstanceActionResponse { Success = success, Message = message });
        }

        public override Task<InstanceActionResponse> DeleteGroup(DeleteGroupRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message) = ServerDataManager.DeleteGroup(request.GroupId);
            return Task.FromResult(new InstanceActionResponse { Success = success, Message = message });
        }

        public override Task<InstanceActionResponse> UpdateGroup(UpdateGroupRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message) = ServerDataManager.UpdateGroup(
                request.Operation, request.GroupId, request.Name, request.MemberIds.ToList());
            return Task.FromResult(new InstanceActionResponse { Success = success, Message = message });
        }

        public override Task<InstanceActionResponse> GroupAction(GroupActionRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message) = Backend.GroupAction(request.Action, request.GroupId);
            return Task.FromResult(new InstanceActionResponse { Success = success, Message = message });
        }

        public override Task<InstanceDetailResponse> GetInstanceDetail(InstanceDetailRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message, instance, backups) = Backend.GetInstanceDetail(request.Id);
            var response = new InstanceDetailResponse { Success = success, Message = message };
            if (instance != null)
            {
                response.Instance = new Grpc.InstanceInfo
                {
                    Id = instance.Id, Identifier = instance.Identifier, Name = instance.Name,
                    GroupId = instance.GroupId, GroupName = instance.GroupName, IsRunning = instance.IsRunning,
                    Hidden = instance.Hidden, AutoRestart = instance.AutoRestart,
                    WorkPath = instance.WorkPath, JavaPath = instance.JavaPath, RunFlags = instance.RunFlags,
                    AnalyzerMode = instance.AnalyzerMode, CreatedAt = instance.CreatedAt, LastStartedAt = instance.LastStartedAt
                };
            }
            foreach (var b in backups)
            {
                response.Backups.Add(new Grpc.BackupEntry
                { FileName = b.FileName, CreatedAt = b.CreatedAt, SizeBytes = b.SizeBytes });
            }
            return Task.FromResult(response);
        }

        public override Task<InstanceActionResponse> CreateBackup(CreateBackupRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message) = ServerDataManager.CreateBackup(request.Id);
            return Task.FromResult(new InstanceActionResponse { Success = success, Message = message });
        }

        public override Task<InstanceActionResponse> RestoreBackup(RestoreBackupRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message) = ServerDataManager.RestoreBackup(request.Id, request.FileName);
            return Task.FromResult(new InstanceActionResponse { Success = success, Message = message });
        }

        public override Task<InstanceActionResponse> DeleteBackup(DeleteBackupRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message) = ServerDataManager.DeleteBackup(request.Id, request.FileName);
            return Task.FromResult(new InstanceActionResponse { Success = success, Message = message });
        }

        private static readonly ConcurrentDictionary<string, string> _peerToClientId = new();

        private static void ValidateAuthKey(ServerCallContext context)
        {
            var authKey = Config.App.GrpcAuthKey;
            if (string.IsNullOrEmpty(authKey)) return;

            var header = context.RequestHeaders.FirstOrDefault(h => h.Key == "authorization");
            if (header == null || header.Value != authKey)
            {
                throw new RpcException(new global::Grpc.Core.Status(StatusCode.Unauthenticated, "Invalid or missing authentication key"));
            }
        }

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
