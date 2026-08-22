using Grpc.Core;
using RtCli.Grpc;
using RtCli.Modules;
using RtCli.Modules.Extension;
using RtCli.Modules.Function;
using Spectre.Console;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var service = new RtCliServiceImpl();

                    _grpcServer = new Server
                    {
                        Services = { RtCliService.BindService(service) },
                        Ports = { new ServerPort("0.0.0.0", ServerPort, CreateSslServerCredentials()) }
                    };

                    _grpcServer.Start();

                    Output.Log($"gRPC服务器已启动端口: {ServerPort} (TLS)", 1, "Connector");
                    return;
                }
                catch (Exception ex)
                {
                    _grpcServer = null;
                    if (attempt < maxAttempts)
                    {
                        Output.Log($"gRPC服务器启动失败(第 {attempt}/{maxAttempts} 次): {ex.Message}，5秒后重试...", 3, "Connector");
                        await Task.Delay(5000);
                    }
                    else
                    {
                        Output.Log($"gRPC服务器启动失败(已重试 {maxAttempts} 次): {ex.Message}。面板将无法连接，请检查端口 {ServerPort} 是否被占用", 3, "Connector");
                    }
                }
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

        /// <summary>
        /// 加载或生成自签名证书并构造 gRPC TLS 服务端凭据。
        /// 证书持久化到 Config.DataPath (grpc_cert.pem / grpc_key.pem)，重启后复用以保持指纹稳定，
        /// 否则面板端 TOFU 证书固定会在 RtCli 每次重启后因指纹变化而拒绝连接。
        /// 加密传输外的身份认证由 authorization 头中的共享密钥承担。
        /// </summary>
        private static SslServerCredentials CreateSslServerCredentials()
        {
            string certPath = Path.Combine(Config.DataPath, "grpc_cert.pem");
            string keyPath = Path.Combine(Config.DataPath, "grpc_key.pem");

            // 尝试加载已持久化的证书(剩余有效期不足30天时视为过期重新生成)
            try
            {
                if (File.Exists(certPath) && File.Exists(keyPath))
                {
                    string certPem = File.ReadAllText(certPath);
                    string keyPem = File.ReadAllText(keyPath);
                    using var loaded = new X509Certificate2(certPath);
                    if (DateTimeOffset.UtcNow < loaded.NotAfter.AddDays(-30))
                        return new SslServerCredentials(new[] { new KeyCertificatePair(certPem, keyPem) });
                    Output.Log("gRPC 证书即将过期, 已重新生成", 2, "Connector");
                }
            }
            catch (Exception ex)
            {
                Output.Log($"加载 gRPC 证书失败, 将重新生成: {ex.Message}", 2, "Connector");
            }

            // 生成新证书并原子化持久化(失败不影响本次运行)
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=rutcitrus-grpc", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

            string newCertPem = cert.ExportCertificatePem();
            string newKeyPem = rsa.ExportPkcs8PrivateKeyPem();
            try
            {
                Directory.CreateDirectory(Config.DataPath);
                AtomicFile.WriteAllText(certPath, newCertPem);
                AtomicFile.WriteAllText(keyPath, newKeyPem);
            }
            catch (Exception ex)
            {
                Output.Log($"持久化 gRPC 证书失败(不影响本次运行): {ex.Message}", 2, "Connector");
            }

            return new SslServerCredentials(new[] { new KeyCertificatePair(newCertPem, newKeyPem) });
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

        public override Task<ConfigDocsResponse> GetConfigDocs(ConfigFileRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var docs = Repository.GetDocs(request.FileName);
            var response = new ConfigDocsResponse
            {
                Success = true,
                Message = "获取成功",
                FileName = request.FileName
            };
            if (docs != null)
            {
                foreach (var kvp in docs)
                    response.Docs[kvp.Key] = kvp.Value;
            }
            return Task.FromResult(response);
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
                MemoryTotalMb = memTotal
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
                    McMemoryMb = s.McMemMb
                });
            }
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
            var (success, message, content, category) = Backend.GetServerFile(request.ServerKey, request.FileName, request.TailLines);
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

        // ===== 任务列表 =====
        public override Task<TaskListResponse> GetTaskList(GetTaskListRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var taskType = request.TaskType ?? "";
            var tasks = new List<Grpc.TaskItem>();
            bool isRunning = false;

            if (string.Equals(taskType, "ai", StringComparison.OrdinalIgnoreCase))
            {
                isRunning = Intelligence.AiAutoRunner.IsRunning;
                var config = ContentManager.Ai.ServerAutoAi;
                if (config?.Tasks != null)
                {
                    foreach (var kv in config.Tasks)
                    {
                        var lastRun = Intelligence.AiAutoRunner.LastRunTime.TryGetValue(kv.Key, out var t)
                            ? t.ToString("MM-dd HH:mm:ss") : "-";
                        int count = Intelligence.AiAutoRunner.RunCount.TryGetValue(kv.Key, out var c) ? c : 0;
                        tasks.Add(new Grpc.TaskItem
                        {
                            Key = kv.Key,
                            Name = kv.Value.Name ?? kv.Key,
                            Enabled = kv.Value.Enabled,
                            Trigger = kv.Value.Trigger ?? "",
                            Interval = kv.Value.Interval ?? "",
                            Execute = string.Join(", ", kv.Value.Actions ?? new List<string>()),
                            RunCount = count,
                            LastRun = lastRun,
                            Status = kv.Value.Enabled ? "启用" : "禁用"
                        });
                    }
                }
            }
            else if (string.Equals(taskType, "auto", StringComparison.OrdinalIgnoreCase))
            {
                isRunning = Scheduler.IsRunning;
                foreach (var kvp in Scheduler.Settings.Tasks)
                {
                    string status = kvp.Value.Enabled ? "启用" : "禁用";
                    int count = Scheduler.GetExecutionCount(kvp.Key);
                    tasks.Add(new Grpc.TaskItem
                    {
                        Key = kvp.Key,
                        Name = kvp.Key,
                        Enabled = kvp.Value.Enabled,
                        Trigger = kvp.Value.Trigger ?? "",
                        Interval = kvp.Value.Condition ?? "",
                        Execute = kvp.Value.Execute ?? "",
                        RunCount = count,
                        LastRun = "-",
                        Status = status
                    });
                }
            }

            var resp = new TaskListResponse
            {
                Success = true,
                Message = "OK",
                IsRunning = isRunning
            };
            resp.Tasks.Add(tasks);
            return Task.FromResult(resp);
        }

        // ===== RtCli 状态 =====
        public override Task<RtStatusResponse> GetRtStatus(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var resp = new RtStatusResponse
            {
                Success = true,
                Message = "OK",
                ManagementPortRunning = Connector.IsRunning,
                ConnectedClients = Connector.ConnectedClientCount,
                CurrentServerKey = Config.App.CurrentServer ?? "",
                CurrentServerName = Config.CurrentServer?.ServerName ?? "",
                McMode = Function.Analyzer.CurrentMode ?? "",
                McRunning = Function.Analyzer.IsRunModeActive,
                McConnected = Function.Analyzer.IsAttached,
                AutoTipsRunning = Intelligence.IsTipsRunning,
                AiRunning = Intelligence.AiAutoRunner.IsRunning,
                SchedulerRunning = Function.Scheduler.IsRunning
            };
            return Task.FromResult(resp);
        }

        // ===== 面板数据 =====
        public override Task<PanelDataResponse> GetPanelData(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            return Task.FromResult(new PanelDataResponse
            {
                Success = true,
                Message = "OK",
                JsonData = PanelDataManager.Load() ?? ""
            });
        }

        public override Task<PanelDataResponse> SavePanelData(PanelDataRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = PanelDataManager.Save(request.JsonData ?? "");
            return Task.FromResult(new PanelDataResponse
            {
                Success = ok,
                Message = msg,
                JsonData = request.JsonData ?? ""
            });
        }

        // ===== 实例管理 =====
        public override Task<InstanceListResponse> GetInstanceList(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message, instances, groups) = Backend.GetInstanceList();
            var resp = new InstanceListResponse { Success = success, Message = message };
            foreach (var i in instances)
            {
                resp.Instances.Add(new Grpc.InstanceInfo
                {
                    Id = i.Id,
                    Identifier = i.Identifier,
                    Name = i.Name,
                    GroupId = i.GroupId ?? "",
                    GroupName = i.GroupName ?? "",
                    IsRunning = i.IsRunning,
                    Hidden = i.Hidden,
                    AutoRestart = i.AutoRestart,
                    WorkPath = i.WorkPath ?? "",
                    JavaPath = i.JavaPath ?? "",
                    RunFlags = i.RunFlags ?? "",
                    AnalyzerMode = i.AnalyzerMode ?? "",
                    CreatedAt = i.CreatedAt ?? "",
                    LastStartedAt = i.LastStartedAt ?? ""
                });
            }
            foreach (var g in groups)
            {
                resp.Groups.Add(new Grpc.GroupInfo
                {
                    Id = g.Id,
                    Name = g.Name,
                    CreatedAt = g.CreatedAt ?? ""
                });
                // MemberIds
                var gi = resp.Groups[resp.Groups.Count - 1];
                gi.MemberIds.Add(g.MemberIds ?? new List<string>());
            }
            return Task.FromResult(resp);
        }

        public override Task<InstanceActionResponse> CreateInstance(CreateInstanceRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = ServerDataManager.CreateInstance(
                request.Identifier ?? "", request.Name ?? "",
                request.WorkPath ?? "", request.JavaPath ?? "",
                request.RunFlags ?? "", request.AnalyzerMode ?? "",
                request.GroupId ?? "");
            return Task.FromResult(new InstanceActionResponse { Success = ok, Message = msg });
        }

        public override Task<InstanceActionResponse> DeleteInstance(DeleteInstanceRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = ServerDataManager.DeleteInstance(request.Id ?? "", request.DeleteFiles);
            return Task.FromResult(new InstanceActionResponse { Success = ok, Message = msg });
        }

        public override Task<InstanceActionResponse> UpdateInstance(UpdateInstanceRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = ServerDataManager.UpdateInstance(
                request.Id ?? "", request.Name ?? "", request.GroupId ?? "",
                request.Hidden, request.AutoRestart,
                request.WorkPath ?? "", request.JavaPath ?? "",
                request.RunFlags ?? "", request.AnalyzerMode ?? "");
            return Task.FromResult(new InstanceActionResponse { Success = ok, Message = msg });
        }

        public override Task<InstanceActionResponse> InstanceAction(InstanceActionRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = Backend.InstanceAction(request.Action ?? "", request.Ids?.ToList() ?? new List<string>());
            return Task.FromResult(new InstanceActionResponse { Success = ok, Message = msg });
        }

        public override Task<InstanceActionResponse> CreateGroup(CreateGroupRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg, _) = ServerDataManager.CreateGroup(request.Name ?? "", request.MemberIds?.ToList() ?? new List<string>());
            return Task.FromResult(new InstanceActionResponse { Success = ok, Message = msg });
        }

        public override Task<InstanceActionResponse> DeleteGroup(DeleteGroupRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = ServerDataManager.DeleteGroup(request.GroupId ?? "");
            return Task.FromResult(new InstanceActionResponse { Success = ok, Message = msg });
        }

        public override Task<InstanceActionResponse> UpdateGroup(UpdateGroupRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = ServerDataManager.UpdateGroup(
                request.Operation ?? "", request.GroupId ?? "",
                request.Name ?? "", request.MemberIds?.ToList() ?? new List<string>());
            return Task.FromResult(new InstanceActionResponse { Success = ok, Message = msg });
        }

        public override Task<InstanceActionResponse> GroupAction(GroupActionRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = Backend.GroupAction(request.Action ?? "", request.GroupId ?? "");
            return Task.FromResult(new InstanceActionResponse { Success = ok, Message = msg });
        }

        public override Task<InstanceDetailResponse> GetInstanceDetail(InstanceDetailRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message, instance, backups) = Backend.GetInstanceDetail(request.Id ?? "");
            var resp = new InstanceDetailResponse { Success = success, Message = message };
            if (instance != null)
            {
                resp.Instance = new Grpc.InstanceInfo
                {
                    Id = instance.Id,
                    Identifier = instance.Identifier,
                    Name = instance.Name,
                    GroupId = instance.GroupId ?? "",
                    GroupName = instance.GroupName ?? "",
                    IsRunning = instance.IsRunning,
                    Hidden = instance.Hidden,
                    AutoRestart = instance.AutoRestart,
                    WorkPath = instance.WorkPath ?? "",
                    JavaPath = instance.JavaPath ?? "",
                    RunFlags = instance.RunFlags ?? "",
                    AnalyzerMode = instance.AnalyzerMode ?? "",
                    CreatedAt = instance.CreatedAt ?? "",
                    LastStartedAt = instance.LastStartedAt ?? ""
                };
            }
            if (backups != null)
            {
                foreach (var b in backups)
                {
                    resp.Backups.Add(new Grpc.BackupEntry
                    {
                        FileName = b.FileName ?? "",
                        CreatedAt = b.CreatedAt ?? "",
                        SizeBytes = b.SizeBytes
                    });
                }
            }
            return Task.FromResult(resp);
        }

        public override Task<InstanceActionResponse> CreateBackup(CreateBackupRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = ServerDataManager.CreateBackup(request.Id ?? "");
            return Task.FromResult(new InstanceActionResponse { Success = ok, Message = msg });
        }

        public override Task<InstanceActionResponse> RestoreBackup(RestoreBackupRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = ServerDataManager.RestoreBackup(request.Id ?? "", request.FileName ?? "");
            return Task.FromResult(new InstanceActionResponse { Success = ok, Message = msg });
        }

        public override Task<InstanceActionResponse> DeleteBackup(DeleteBackupRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = ServerDataManager.DeleteBackup(request.Id ?? "", request.FileName ?? "");
            return Task.FromResult(new InstanceActionResponse { Success = ok, Message = msg });
        }

        // ===== 错误日志分析 =====
        public override Task<InstanceErrorsResponse> AnalyzeInstanceErrors(InstanceDetailRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg, errors) = Backend.AnalyzeInstanceErrors(request.Id ?? "");
            var resp = new InstanceErrorsResponse { Success = ok, Message = msg };
            foreach (var kvp in errors ?? new Dictionary<int, string>())
                resp.Errors[kvp.Key] = kvp.Value;
            return Task.FromResult(resp);
        }

        public override async Task<AiAnalyzeResponse> AiAnalyzeInstanceErrors(AiAnalyzeRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg, result) = await Backend.AiAnalyzeInstanceErrors(request.Id ?? "", request.Range ?? "");
            return new AiAnalyzeResponse { Success = ok, Message = msg, Result = result ?? "" };
        }

        // ===== 玩家事件 =====
        public override Task<PlayerEventListResponse> GetPlayerEvents(PlayerEventRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var events = PlayerEventRecorder.GetEvents(request.Id ?? "", request.Limit);
            var resp = new PlayerEventListResponse { Success = true, Message = "OK" };
            if (events != null)
            {
                foreach (var e in events)
                {
                    resp.Events.Add(new Grpc.PlayerEventLog
                    {
                        EventType = e.EventType ?? "",
                        PlayerName = e.PlayerName ?? "",
                        TriggerTime = e.TriggerTime ?? "",
                        RecordedAt = e.RecordedAt ?? "",
                        Detail = e.Detail ?? ""
                    });
                }
            }
            return Task.FromResult(resp);
        }

        // ===== 插件/模组 =====
        public override Task<JarFileListResponse> ListPlugins(InstanceDetailRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg, plugins) = Backend.ListPlugins(request.Id ?? "");
            var resp = new JarFileListResponse { Success = ok, Message = msg };
            if (plugins != null)
            {
                foreach (var p in plugins)
                {
                    resp.Files.Add(new Grpc.JarFileInfo
                    {
                        FileName = p.FileName ?? "",
                        SizeBytes = p.SizeBytes,
                        IsDisabled = p.IsDisabled
                    });
                }
            }
            return Task.FromResult(resp);
        }

        public override Task<JarFileListResponse> ListMods(InstanceDetailRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg, mods) = Backend.ListMods(request.Id ?? "");
            var resp = new JarFileListResponse { Success = ok, Message = msg };
            if (mods != null)
            {
                foreach (var m in mods)
                {
                    resp.Files.Add(new Grpc.JarFileInfo
                    {
                        FileName = m.FileName ?? "",
                        SizeBytes = m.SizeBytes,
                        IsDisabled = m.IsDisabled
                    });
                }
            }
            return Task.FromResult(resp);
        }

        public override Task<SimpleResponse> ToggleJarFile(ManageJarFileRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = string.Equals(request.Type, "mod", StringComparison.OrdinalIgnoreCase)
                ? Backend.ToggleMod(request.Id ?? "", request.FileName ?? "")
                : Backend.TogglePlugin(request.Id ?? "", request.FileName ?? "");
            return Task.FromResult(new SimpleResponse { Success = ok, Message = msg });
        }

        public override Task<SimpleResponse> DeleteJarFile(ManageJarFileRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = string.Equals(request.Type, "mod", StringComparison.OrdinalIgnoreCase)
                ? Backend.DeleteMod(request.Id ?? "", request.FileName ?? "")
                : Backend.DeletePlugin(request.Id ?? "", request.FileName ?? "");
            return Task.FromResult(new SimpleResponse { Success = ok, Message = msg });
        }

        public override Task<SimpleResponse> UploadJarFile(UploadJarFileRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = string.Equals(request.Type, "mod", StringComparison.OrdinalIgnoreCase)
                ? Backend.UploadMod(request.Id ?? "", request.FileName ?? "", request.Content?.ToByteArray() ?? Array.Empty<byte>())
                : Backend.UploadPlugin(request.Id ?? "", request.FileName ?? "", request.Content?.ToByteArray() ?? Array.Empty<byte>());
            return Task.FromResult(new SimpleResponse { Success = ok, Message = msg });
        }

        // ===== 玩家管理 =====
        public override Task<SimpleResponse> ManagePlayer(ManagePlayerRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            bool ok = false;
            string msg = "";
            var id = request.Id ?? "";
            var target = request.Target ?? "";
            var reason = request.Reason ?? "";
            switch (request.Action?.ToLowerInvariant() ?? "")
            {
                case "ban":
                    (ok, msg) = Backend.BanPlayer(id, target, reason);
                    break;
                case "pardon":
                    (ok, msg) = Backend.PardonPlayer(id, target);
                    break;
                case "ban_ip":
                    (ok, msg) = Backend.BanIp(id, target, reason);
                    break;
                case "pardon_ip":
                    (ok, msg) = Backend.PardonIp(id, target);
                    break;
                case "kick":
                    (ok, msg) = Backend.KickPlayer(id, target, reason);
                    break;
                case "custom":
                    (ok, msg) = Backend.SendCustomCommand(id, request.Command ?? "");
                    break;
                default:
                    ok = false; msg = $"未知操作: {request.Action}";
                    break;
            }
            return Task.FromResult(new SimpleResponse { Success = ok, Message = msg });
        }

        // ===== 面板向导 RPC (对应 .guide / .group build 向导能力) =====

        public override Task<ServerTypeListResponse> GetServerTypes(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var resp = new ServerTypeListResponse { Success = true, Message = "" };
            foreach (var t in Intelligence.GetServerTypeListForPanel())
            {
                resp.ServerTypes.Add(new ServerTypeInfoMsg
                {
                    Name = t.Name,
                    TypeKey = t.TypeKey,
                    Downloadable = t.Downloadable,
                    Website = t.Website,
                    DefaultJar = t.DefaultJar
                });
            }
            return Task.FromResult(resp);
        }

        public override async Task<ServerVersionListResponse> GetServerVersions(ServerVersionRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (popular, all, error) = await Intelligence.GetServerVersionListForPanel(request.TypeKey ?? "");
            var resp = new ServerVersionListResponse
            {
                Success = string.IsNullOrEmpty(error),
                Message = error
            };
            resp.Versions.Add(popular);
            resp.AllVersions.Add(all);
            return resp;
        }

        public override async Task<DownloadServerJarResponse> DownloadServerJar(DownloadServerJarRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg, jarName) = await Intelligence.DownloadServerJarForPanel(
                request.TypeKey ?? "", request.Version ?? "", request.WorkPath ?? "");
            return new DownloadServerJarResponse { Success = ok, Message = msg, JarName = jarName };
        }

        public override async Task<GroupBuildResponse> GroupBuild(GroupBuildRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg, groupId, proxyId, serverPorts) = await Hub.GroupBuildForPanel(
                request.GroupName ?? "",
                request.MemberIds?.ToList() ?? new List<string>(),
                request.ProxyType ?? "",
                request.ProxyId ?? "",
                request.ProxyPort,
                request.AutoPorts,
                request.PortRangeStart,
                request.PortRangeEnd,
                request.OnlineMode,
                request.LobbyServer ?? "");
            var resp = new GroupBuildResponse
            {
                Success = ok,
                Message = msg,
                GroupId = groupId,
                ProxyInstanceId = proxyId
            };
            foreach (var kv in serverPorts)
                resp.ServerPorts.Add(kv.Key, kv.Value);
            return resp;
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
