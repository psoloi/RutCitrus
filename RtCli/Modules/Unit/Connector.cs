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
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace RtCli.Modules.Unit
{
    internal class Connector
    {
        private static Server? _grpcServer;
        private static readonly ConcurrentDictionary<string, ClientInfo> _connectedClients = new();
        private static readonly List<LogSubscriber> _logSubscribers = new();
        private static readonly object _subscribersLock = new();

        // 每个面板订阅者的独立日志队列容量与单条写入超时(毫秒)
        private const int LogQueueCapacity = 1024;
        private const int LogWriteTimeoutMs = 5000;

        /// <summary>
        /// 单个日志订阅者: 有界队列 + 独立写循环。
        /// 队列满时丢弃最旧日志(DropOldest), 写入超时则断开该订阅, 避免慢客户端拖住日志线程。
        /// </summary>
        internal sealed class LogSubscriber
        {
            public IServerStreamWriter<LogMessage> Writer { get; }
            public System.Threading.Channels.Channel<LogMessage> Queue { get; }
            public CancellationTokenSource Cts { get; }
            public Task? Pump { get; set; }

            public LogSubscriber(IServerStreamWriter<LogMessage> writer, CancellationToken callToken)
            {
                Writer = writer;
                Queue = System.Threading.Channels.Channel.CreateBounded<LogMessage>(new BoundedChannelOptions(LogQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });
                // 与 RPC 调用的取消令牌联动: 客户端断开时写循环立即退出
                Cts = CancellationTokenSource.CreateLinkedTokenSource(callToken);
            }
        }

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
                    using var loaded = X509CertificateLoader.LoadCertificateFromFile(certPath);
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

        /// <summary>
        /// 向所有已连接面板投递日志(非阻塞)。仅做队列入队, 不做网络写入,
        /// 因此日志线程不会被任何慢客户端或网络抖动阻塞。
        /// </summary>
        public static void BroadcastLog(string timestamp, int level, string source, string message, string? instanceId = null)
        {
            List<LogSubscriber>? subscribers;
            lock (_subscribersLock)
            {
                if (_logSubscribers.Count == 0) return;
                subscribers = new List<LogSubscriber>(_logSubscribers);
            }

            var logMsg = new LogMessage
            {
                Timestamp = timestamp,
                Level = level,
                Source = source,
                Message = message,
                InstanceId = instanceId ?? ""
            };

            foreach (var subscriber in subscribers)
            {
                // 有界队列满时按 DropOldest 丢弃最旧日志, TryWrite 始终立即返回
                subscriber.Queue.Writer.TryWrite(logMsg);
            }
        }

        /// <summary>独立的单写循环: 每个订阅者一条, 写入超时即断开该订阅</summary>
        private static async Task PumpLogsAsync(LogSubscriber subscriber)
        {
            try
            {
                var reader = subscriber.Queue.Reader;
                while (await reader.WaitToReadAsync(subscriber.Cts.Token).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var message))
                    {
                        var writeTask = subscriber.Writer.WriteAsync(message);
                        if (writeTask.IsCompleted)
                        {
                            // 正常路径: 缓冲区可写, 不创建超时计时器
                            await writeTask.ConfigureAwait(false);
                            continue;
                        }

                        var completed = await Task.WhenAny(
                            writeTask,
                            Task.Delay(LogWriteTimeoutMs, subscriber.Cts.Token)).ConfigureAwait(false);

                        if (completed != writeTask)
                        {
                            if (subscriber.Cts.IsCancellationRequested) return;
                            // 放弃挂起的写入(其异常由续延吞掉), 断开该慢客户端, 由面板重连
                            _ = writeTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                            Output.Log("面板日志订阅写入超时, 已断开该订阅(慢客户端)", 2, "Connector");
                            return;
                        }

                        await writeTask.ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 客户端断开或服务停止
            }
            catch (Exception)
            {
                // 流异常: 退出写循环, 由 finally 清理订阅
            }
            finally
            {
                RemoveLogSubscriber(subscriber);
            }
        }

        internal static LogSubscriber AddLogSubscriber(IServerStreamWriter<LogMessage> subscriber, CancellationToken callToken)
        {
            var entry = new LogSubscriber(subscriber, callToken);
            lock (_subscribersLock)
            {
                _logSubscribers.Add(entry);
            }
            entry.Pump = Task.Run(() => PumpLogsAsync(entry));
            return entry;
        }

        internal static void RemoveLogSubscriber(LogSubscriber subscriber)
        {
            lock (_subscribersLock)
            {
                _logSubscribers.Remove(subscriber);
            }
            subscriber.Queue.Writer.TryComplete();
            try
            {
                subscriber.Cts.Cancel();
                subscriber.Cts.Dispose();
            }
            catch
            {
                // 已释放/已取消
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

        internal static string ParseIPFromPeer(string peer)
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
            catch (Exception ex) { Output.Debug(ex, "解析对端IP失败", "Connector"); }
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
            var (success, result) = Backend.DispatchCommand(request.Command, request.InstanceId);
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
            var subscriber = Connector.AddLogSubscriber(responseStream, context.CancellationToken);

            try
            {
                // 写循环结束(客户端断开/写超时/服务停止)即结束本次 RPC
                await subscriber.Pump!;
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                Connector.RemoveLogSubscriber(subscriber);
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

        public override async Task<SystemStatsResponse> GetSystemStats(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            // 采集含 WMI 与最长数秒的命令查询, 放到线程池执行避免占用 gRPC 处理线程
            var (success, message, cpu, memUsed, memTotal, servers) =
                await Task.Run(() => Backend.GetSystemStats(), context.CancellationToken).ConfigureAwait(false);
            var response = new SystemStatsResponse
            {
                Success = success,
                Message = message,
                CpuUsage = cpu,
                MemoryUsedMb = memUsed,
                MemoryTotalMb = memTotal,
                CurrentServerKey = Config.App.CurrentServer,
                AiRunning = Intelligence.AiAutoRunner.IsRunning,
                SchedulerRunning = Function.Scheduler.IsRunning
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
            return response;
        }

        public override Task<ServerFileListResponse> ListServerFiles(ServerFileListRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (success, message, serverKey, serverName, workPath, files) = Backend.ListServerFiles(request.ServerKey ?? "");
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

            // MC 版本: 从当前实例服务端 jar 的 version.json 取(取不到回退日志)
            var (mcVersion, jarPath) = ServerVersion.Detect(Config.CurrentServer?.WorkPath);

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
                SchedulerRunning = Function.Scheduler.IsRunning,
                McVersion = mcVersion ?? "",
                ServerJar = string.IsNullOrEmpty(jarPath) ? "" : System.IO.Path.GetFileName(jarPath)
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
            var resp = new InstanceListResponse { Success = success, Message = message, CurrentServerKey = Config.App.CurrentServer ?? "" };
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
            // gRPC proto3 的 string 默认为空串, 无法与"未提供"区分;
            // 将可选字段的空串转为 null 以保留 Hub 层"null=不更新"语义,
            // 避免面板部分更新请求(如仅改分析模式)清空其他配置。
            // groupId 例外: 空串是合法值(表示移出群组)。
            var (ok, msg) = ServerDataManager.UpdateInstance(
                request.Id ?? "",
                string.IsNullOrEmpty(request.Name) ? null : request.Name,
                request.GroupId ?? "",
                request.Hidden, request.AutoRestart,
                string.IsNullOrEmpty(request.WorkPath) ? null : request.WorkPath,
                string.IsNullOrEmpty(request.JavaPath) ? null : request.JavaPath,
                string.IsNullOrEmpty(request.RunFlags) ? null : request.RunFlags,
                string.IsNullOrEmpty(request.AnalyzerMode) ? null : request.AnalyzerMode);
            return Task.FromResult(new InstanceActionResponse { Success = ok, Message = msg });
        }

        public override Task<JavaScanResponse> ScanJavaPaths(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg, javas) = Backend.ScanJavaInstallations();
            var resp = new JavaScanResponse { Success = ok, Message = msg };
            foreach (var j in javas)
                resp.Javas.Add(new Grpc.JavaInfo { Path = j.Path, Version = j.Version, Source = j.Source });
            return Task.FromResult(resp);
        }

        public override Task<InstanceActionResponse> CloneInstance(CloneInstanceRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg, _) = Backend.CloneInstance(
                request.Id ?? "", request.NewIdentifier ?? "", request.NewName ?? "", request.CopyFiles);
            return Task.FromResult(new InstanceActionResponse { Success = ok, Message = msg });
        }

        public override Task<ExtractArchiveResponse> ExtractInstanceArchive(ExtractArchiveRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg, scripts) = Backend.ExtractInstanceZip(request.Id ?? "", request.RelPath ?? "");
            var resp = new ExtractArchiveResponse { Success = ok, Message = msg };
            foreach (var s in scripts)
                resp.Scripts.Add(s);
            return Task.FromResult(resp);
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

        public override Task<BaseAnalyzeResponse> BaseAnalyzeInstanceErrors(AiAnalyzeRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg, matches) = Backend.BaseAnalyzeInstanceErrors(request.Id ?? "", request.Range ?? "");
            var resp = new BaseAnalyzeResponse { Success = ok, Message = msg };
            foreach (var m in matches ?? new List<(string, string, string, string)>())
            {
                resp.Matches.Add(new BaseMatch
                {
                    Topic = m.Item1 ?? "",
                    Solution = m.Item2 ?? "",
                    Action = m.Item3 ?? "",
                    Matched = m.Item4 ?? ""
                });
            }
            return Task.FromResult(resp);
        }

        // ===== AI 技能管理 =====
        public override Task<AiSkillListResponse> GetAiSkills(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var resp = new AiSkillListResponse { Success = true, Message = "OK" };
            resp.Skills.AddRange(Backend.GetAiSkills().Select(BuildAiSkillInfo));
            return Task.FromResult(resp);
        }

        public override Task<SimpleResponse> SaveAiSkill(SaveAiSkillRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = Backend.SaveAiSkill(request.OriginalName ?? "", request.Name ?? "", request.Description ?? "", request.Behavior ?? "");
            return Task.FromResult(new SimpleResponse { Success = ok, Message = msg });
        }

        public override Task<SimpleResponse> DeleteAiSkill(DeleteAiSkillRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = Backend.DeleteAiSkill(request.Name ?? "");
            return Task.FromResult(new SimpleResponse { Success = ok, Message = msg });
        }

        public override Task<AiSkillListResponse> ImportAiSkills(ImportAiSkillsRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg, skills) = Backend.ImportAiSkills(request.Content ?? "", request.Overwrite);
            var resp = new AiSkillListResponse { Success = ok, Message = msg };
            resp.Skills.AddRange(skills.Select(BuildAiSkillInfo));
            return Task.FromResult(resp);
        }

        private static AiSkillInfo BuildAiSkillInfo(AiSkill s) => new AiSkillInfo
        {
            Name = s.Name ?? "",
            Description = s.Description ?? "",
            Behavior = s.Behavior ?? ""
        };

        // ===== AI 记忆管理 =====
        public override Task<AiMemoryListResponse> GetAiMemories(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var resp = new AiMemoryListResponse
            {
                Success = true,
                Message = "OK",
                Settings = BuildAiMemorySettings(Backend.GetAiMemorySettings())
            };
            resp.Memories.AddRange(Backend.GetAiMemories().Select(m => new AiMemoryInfo
            {
                Time = m.Time ?? "",
                Topic = m.Topic ?? "",
                Content = m.Content ?? ""
            }));
            return Task.FromResult(resp);
        }

        public override Task<SimpleResponse> SaveAiMemorySettings(AiMemorySettingsRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var s = request.Settings ?? new AiMemorySettings();
            var setting = new AiMemorySetting
            {
                Enabled = s.Enabled,
                UseAi = s.UseAi,
                MaxTokens = s.MaxTokens,
                MaxContentLength = s.MaxContentLength,
                MaxEntries = s.MaxEntries,
                CaptureServerInfo = s.CaptureServerInfo,
                CapturePlayerCount = s.CapturePlayerCount,
                CaptureCommonErrors = s.CaptureCommonErrors,
                Prompt = s.Prompt ?? ""
            };
            var (ok, msg) = Backend.SaveAiMemorySettings(setting);
            return Task.FromResult(new SimpleResponse { Success = ok, Message = msg });
        }

        public override Task<SimpleResponse> ClearAiMemories(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = Backend.ClearAiMemories();
            return Task.FromResult(new SimpleResponse { Success = ok, Message = msg });
        }
        // ===== AI 自动化观测(任务状态 / 工具调用审计 / 历史结论) =====
        public override Task<AiAutomationStatusResponse> GetAiAutomationStatus(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);

            var resp = new AiAutomationStatusResponse
            {
                Success = true,
                Message = "OK",
                RunnerRunning = Intelligence.AiAutoRunner.IsRunning,
                MemoryCount = AiMemoryManager.Load().Count,
                MemoryBytes = SafeFileSize(AiMemoryManager.FilePath),
                AuditCount = AiToolAudit.Count()
            };

            var config = ContentManager.Ai?.ServerAutoAi;
            if (config?.Tasks != null)
            {
                foreach (var kv in config.Tasks)
                {
                    var t = kv.Value;
                    resp.Tasks.Add(new AiTaskStatus
                    {
                        Key = kv.Key,
                        Name = t.Name ?? "",
                        Enabled = t.Enabled,
                        Trigger = t.Trigger ?? "",
                        Interval = t.Interval ?? "",
                        MinTriggerIntervalMinutes = t.MinTriggerIntervalMinutes,
                        Actions = string.Join(", ", t.Actions ?? new System.Collections.Generic.List<string>()),
                        LastRun = Intelligence.AiAutoRunner.LastRunTime.TryGetValue(kv.Key, out var lr)
                            ? lr.ToString("MM-dd HH:mm:ss") : "",
                        RunCount = Intelligence.AiAutoRunner.RunCount.TryGetValue(kv.Key, out var rc) ? rc : 0,
                        Running = Intelligence.AiAutoRunner.IsTaskRunning(kv.Key)
                    });
                }
            }
            return Task.FromResult(resp);
        }

        public override Task<AiToolAuditResponse> GetAiToolAudit(AiToolAuditRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);

            int limit = request?.Limit ?? 0;
            if (limit <= 0) limit = 100;
            if (limit > 1000) limit = 1000;

            var resp = new AiToolAuditResponse { Success = true, Message = "OK", FilePath = AiToolAudit.FilePath ?? "" };
            var parsed = new System.Collections.Generic.List<AiToolAuditEntry>();
            foreach (var line in AiToolAudit.ReadRecent(limit))
            {
                try
                {
                    var o = Newtonsoft.Json.Linq.JObject.Parse(line);
                    parsed.Add(new AiToolAuditEntry
                    {
                        Time = o["time"]?.ToString() ?? "",
                        Task = o["task"]?.ToString() ?? "",
                        Tool = o["tool"]?.ToString() ?? "",
                        Args = o["args"]?.ToString() ?? "",
                        Result = o["result"]?.ToString() ?? "",
                        Ok = o["ok"]?.ToObject<bool>() ?? true
                    });
                }
                catch { /* 单行损坏不影响整体 */ }
            }
            // ReadRecent 返回时间正序, 面板按最新在前展示
            parsed.Reverse();
            resp.Entries.AddRange(parsed);
            return Task.FromResult(resp);
        }

        public override Task<AiResponseListResponse> GetAiResponses(AiResponseListRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);

            int limit = request?.Limit ?? 0;
            if (limit <= 0) limit = 50;
            if (limit > 500) limit = 500;

            var resp = new AiResponseListResponse { Success = true, Message = "OK" };
            var items = Intelligence.AiAutoRunner.ReadSavedResponses(limit, out int total);
            resp.Total = total;
            foreach (var it in items)
            {
                resp.Responses.Add(new AiResponseItem
                {
                    TaskKey = it.TaskKey,
                    TaskName = it.TaskName,
                    InputSummary = it.InputSummary,
                    AiResponse = it.AiResponse,
                    Timestamp = it.Timestamp
                });
            }
            return Task.FromResult(resp);
        }

        public override Task<SimpleResponse> DeleteAiResponse(DeleteAiResponseRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);

            var (ok, msg) = Intelligence.AiAutoRunner.DeleteSavedResponse(
                request?.Timestamp ?? "", request?.TaskKey ?? "");
            return Task.FromResult(new SimpleResponse { Success = ok, Message = msg });
        }

        // ===== AI 自动化规则 =====
        public override Task<AiRuleListResponse> GetAiRules(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);

            var cfg = ContentManager.Ai?.ServerAutoAi;
            var resp = new AiRuleListResponse
            {
                Success = true,
                Message = "OK",
                SystemPrompt = cfg?.Prompt ?? "",
                RunnerRunning = Intelligence.AiAutoRunner.IsRunning
            };
            if (cfg?.Rules != null) resp.Rules.AddRange(cfg.Rules);
            return Task.FromResult(resp);
        }

        public override Task<SimpleResponse> SaveAiRules(AiRuleListRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);

            var cfg = ContentManager.Ai?.ServerAutoAi;
            if (cfg == null)
                return Task.FromResult(new SimpleResponse { Success = false, Message = "未加载 AI 配置" });

            var rules = (request?.Rules ?? Enumerable.Empty<string>())
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.Trim())
                .ToList();

            string? backup = cfg.Rules == null ? null : string.Join("\u0001", cfg.Rules);
            cfg.Rules = rules;

            if (!ContentManager.SaveAiSettings())
            {
                if (backup != null) cfg.Rules = backup.Split('\u0001').ToList();
                return Task.FromResult(new SimpleResponse { Success = false, Message = "写入 ai_settings.yml 失败" });
            }

            return Task.FromResult(new SimpleResponse
            {
                Success = true,
                Message = $"已保存 {rules.Count} 条规则(下次 AI 分析即生效)"
            });
        }

        // ===== AI 工作流(任务列表 + 运行参数) =====
        public override Task<AiWorkflowResponse> GetAiWorkflow(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);

            var (ok, msg, runtime, tasks, tools, filePath, running, events) = Backend.GetAiWorkflow();

            var resp = new AiWorkflowResponse
            {
                Success = ok,
                Message = msg,
                FilePath = filePath ?? "",
                RunnerRunning = running,
                Runtime = new AiRuntimeConfig
                {
                    Prompt = runtime.Prompt ?? "",
                    MaxTokens = runtime.MaxTokens,
                    ApiEndpoint = runtime.ApiEndpoint ?? "",
                    ApiKeySet = !string.IsNullOrEmpty(runtime.ApiKey),
                    ApiKeyMasked = Backend.MaskApiKey(runtime.ApiKey),
                    Model = runtime.Model ?? "",
                    Temperature = runtime.Temperature,
                    RequestTimeoutSeconds = runtime.RequestTimeoutSeconds,
                    RetryOnError = runtime.RetryOnError,
                    RetryCount = runtime.RetryCount,
                    SaveResponse = runtime.SaveResponse,
                    ContextCacheSize = runtime.ContextCacheSize,
                    MaxToolRounds = runtime.MaxToolRounds
                }
            };

            if (tasks != null)
            {
                foreach (var kv in tasks)
                {
                    var t = kv.Value;
                    var info = new AiTaskConfig
                    {
                        Key = kv.Key ?? "",
                        Enabled = t?.Enabled ?? false,
                        Name = t?.Name ?? "",
                        Trigger = t?.Trigger ?? "",
                        Limit = t?.Limit ?? 0,
                        Prompt = t?.Prompt ?? "",
                        Interval = t?.Interval ?? "",
                        MinTriggerIntervalMinutes = t?.MinTriggerIntervalMinutes ?? 0,
                        ErrorSimilarityThreshold = t?.ErrorSimilarityThreshold ?? 0,
                        ErrorCacheMinutes = t?.ErrorCacheMinutes ?? 0
                    };
                    if (t?.Input != null) info.Input.AddRange(t.Input);
                    if (t?.Actions != null) info.Actions.AddRange(t.Actions);
                    if (t?.ServerKeys != null) info.ServerKeys.AddRange(t.ServerKeys);
                    resp.Tasks.Add(info);
                }
            }

            if (tools != null) resp.AvailableTools.AddRange(tools);
            resp.DefaultTriggers.AddRange(Backend.AiTriggerTemplates);
            if (events != null) resp.AvailableEvents.AddRange(events);

            return Task.FromResult(resp);
        }

        public override Task<SimpleResponse> SaveAiTask(AiTaskRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);

            var cfg = request?.Task;
            if (cfg == null)
                return Task.FromResult(new SimpleResponse { Success = false, Message = "任务配置不能为空" });

            var task = new AiTask
            {
                Enabled = cfg.Enabled,
                Name = cfg.Name ?? "",
                Trigger = cfg.Trigger ?? "",
                Limit = cfg.Limit,
                Prompt = cfg.Prompt ?? "",
                Interval = cfg.Interval ?? "",
                MinTriggerIntervalMinutes = cfg.MinTriggerIntervalMinutes,
                ErrorSimilarityThreshold = cfg.ErrorSimilarityThreshold,
                ErrorCacheMinutes = cfg.ErrorCacheMinutes
            };
            task.Input.AddRange(cfg.Input);
            task.Actions.AddRange(cfg.Actions);
            task.ServerKeys.AddRange(cfg.ServerKeys);

            var (ok, msg) = Backend.SaveAiTask(request.OriginalKey ?? "", cfg.Key ?? "", task);
            return Task.FromResult(new SimpleResponse { Success = ok, Message = msg });
        }

        public override Task<SimpleResponse> DeleteAiTask(AiTaskKeyRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);

            var (ok, msg) = Backend.DeleteAiTask(request?.Key ?? "");
            return Task.FromResult(new SimpleResponse { Success = ok, Message = msg });
        }

        public override Task<SimpleResponse> SaveAiRuntime(AiRuntimeRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);

            var r = request?.Runtime;
            if (r == null)
                return Task.FromResult(new SimpleResponse { Success = false, Message = "运行参数不能为空" });

            var view = new AiRuntimeView
            {
                Prompt = r.Prompt ?? "",
                MaxTokens = r.MaxTokens,
                ApiEndpoint = r.ApiEndpoint ?? "",
                ApiKey = r.ApiKey ?? "",   // 空串=保持原值(避免掩码回显覆盖真实密钥)
                Model = r.Model ?? "",
                Temperature = r.Temperature,
                RequestTimeoutSeconds = r.RequestTimeoutSeconds,
                RetryOnError = r.RetryOnError,
                RetryCount = r.RetryCount,
                SaveResponse = r.SaveResponse,
                ContextCacheSize = r.ContextCacheSize,
                MaxToolRounds = r.MaxToolRounds
            };

            var (ok, msg) = Backend.SaveAiRuntime(view);
            return Task.FromResult(new SimpleResponse { Success = ok, Message = msg });
        }

        // ===== 调度器任务(蓝图编辑器) =====
        public override Task<SchedulerTaskListResponse> GetSchedulerTasks(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);

            var resp = new SchedulerTaskListResponse
            {
                Success = true,
                Message = "OK",
                IsRunning = Scheduler.IsRunning
            };

            foreach (var kv in Scheduler.Settings.Tasks)
            {
                var t = kv.Value;
                var info = new SchedulerTaskInfo
                {
                    Key = kv.Key,
                    Enabled = t.Enabled,
                    Trigger = t.Trigger ?? "",
                    Condition = t.Condition ?? "",
                    Execute = t.Execute ?? "",
                    PassParameters = t.PassParameters,
                    TimeoutMs = t.Rules?.TimeoutMs ?? 0,
                    DelayMinutes = t.Rules?.DelayMinutes ?? 0,
                    DelayUntil = t.Rules?.DelayUntil ?? "",
                    ExecuteImmediately = t.Rules?.ExecuteImmediately ?? false,
                    MaxExecutions = t.Rules?.MaxExecutions ?? 0,
                    ExpireAt = t.Rules?.ExpireAt ?? "",
                    Queue = t.Rules?.Queue ?? "Fast",
                    RunCount = Scheduler.GetExecutionCount(kv.Key)
                };
                if (t.ServerKeys != null) info.ServerKeys.AddRange(t.ServerKeys);
                resp.Tasks.Add(info);
            }

            // 蓝图编辑器的可选数据源: 让用户从下拉里选, 而不是手填字符串
            try
            {
                foreach (var k in Scripts.GetSettings().Scripts.Keys) resp.Scripts.Add(k);
            }
            catch (Exception ex) { Output.Debug($"读取脚本列表失败: {ex.Message}", "Connector"); }

            try
            {
                foreach (var n in EventNames.BuiltInEvents) resp.Events.Add(n);
                resp.Events.Add(EventNames.CustomPlayerEventName);
                if (Config.App.PlayerEvent?.Customs != null)
                    foreach (var k in Config.App.PlayerEvent.Customs.Keys) resp.Events.Add(k);
            }
            catch (Exception ex) { Output.Debug($"读取事件列表失败: {ex.Message}", "Connector"); }

            try
            {
                foreach (var k in Config.App.ServerList.Keys) resp.Servers.Add(k);
            }
            catch (Exception ex) { Output.Debug($"读取实例列表失败: {ex.Message}", "Connector"); }

            return Task.FromResult(resp);
        }

        public override Task<SimpleResponse> SaveSchedulerTask(SchedulerTaskRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);

            var info = request?.Task;
            if (info == null || string.IsNullOrWhiteSpace(info.Key))
                return Task.FromResult(new SimpleResponse { Success = false, Message = "任务标识不能为空" });

            string key = info.Key.Trim();
            // 面板不编辑 continue_tasks, 保存时必须保留原值, 否则一次保存就会清空流程串接
            var oldRules = Scheduler.Settings.Tasks.TryGetValue(key, out var oldEntry) ? oldEntry.Rules : null;
            var entry = new TaskEntry
            {
                Enabled = info.Enabled,
                Trigger = info.Trigger ?? "",
                Condition = info.Condition ?? "",
                Execute = info.Execute ?? "",
                PassParameters = info.PassParameters,
                ServerKeys = info.ServerKeys?.ToList() ?? new System.Collections.Generic.List<string>(),
                Rules = new TaskRule
                {
                    TimeoutMs = info.TimeoutMs,
                    DelayMinutes = info.DelayMinutes,
                    DelayUntil = info.DelayUntil ?? "",
                    ExecuteImmediately = info.ExecuteImmediately,
                    MaxExecutions = info.MaxExecutions,
                    ExpireAt = info.ExpireAt ?? "",
                    Queue = string.IsNullOrWhiteSpace(info.Queue) ? "Fast" : info.Queue,
                    ContinueTasks = oldRules?.ContinueTasks != null
                        ? new System.Collections.Generic.List<string>(oldRules.ContinueTasks)
                        : new System.Collections.Generic.List<string>()
                }
            };

            Scheduler.Settings.Tasks[key] = entry;
            if (!ContentManager.SaveSchedulerSettings(Scheduler.Settings))
                return Task.FromResult(new SimpleResponse { Success = false, Message = "写入 scheduler_settings.yml 失败" });

            // 仅在调度器已在运行时热重载(Reload 会强制 Start, 不应对未启用的调度器产生副作用)
            try { if (Scheduler.IsRunning) Scheduler.Reload(); }
            catch (Exception ex) { Output.Debug($"调度器热重载失败: {ex.Message}", "Connector"); }

            return Task.FromResult(new SimpleResponse { Success = true, Message = $"已保存任务 {key}" });
        }

        public override Task<SimpleResponse> DeleteSchedulerTask(SchedulerTaskKeyRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);

            string key = request?.Key?.Trim() ?? "";
            if (string.IsNullOrEmpty(key))
                return Task.FromResult(new SimpleResponse { Success = false, Message = "任务标识不能为空" });
            if (!Scheduler.Settings.Tasks.Remove(key))
                return Task.FromResult(new SimpleResponse { Success = false, Message = $"任务不存在: {key}" });
            if (!ContentManager.SaveSchedulerSettings(Scheduler.Settings))
                return Task.FromResult(new SimpleResponse { Success = false, Message = "写入 scheduler_settings.yml 失败" });

            try { if (Scheduler.IsRunning) Scheduler.Reload(); }
            catch (Exception ex) { Output.Debug($"调度器热重载失败: {ex.Message}", "Connector"); }

            return Task.FromResult(new SimpleResponse { Success = true, Message = $"已删除任务 {key}" });
        }

        /// <summary>文件大小(不存在返回 0)</summary>
        private static long SafeFileSize(string path)
        {
            try { return System.IO.File.Exists(path) ? new System.IO.FileInfo(path).Length : 0; }
            catch { return 0; }
        }


        private static AiMemorySettings BuildAiMemorySettings(AiMemorySetting m) => new AiMemorySettings
        {
            Enabled = m.Enabled,
            UseAi = m.UseAi,
            MaxTokens = m.MaxTokens,
            MaxContentLength = m.MaxContentLength,
            MaxEntries = m.MaxEntries,
            CaptureServerInfo = m.CaptureServerInfo,
            CapturePlayerCount = m.CapturePlayerCount,
            CaptureCommonErrors = m.CaptureCommonErrors,
            Prompt = m.Prompt ?? ""
        };

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

        // ===== 事件板(实例生命周期事件) =====
        public override Task<EventBoardResponse> GetEventBoard(EventBoardRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (events, lastReadId) = PanelDataManager.GetBoardEvents(0);
            if (!string.IsNullOrEmpty(request.InstanceId))
                events = events.Where(e => e.InstanceId == request.InstanceId).ToList();
            if (request.Limit > 0 && events.Count > request.Limit)
                events = events.Skip(events.Count - request.Limit).ToList();
            var resp = new EventBoardResponse { Success = true, Message = "OK", LastReadId = lastReadId };
            foreach (var e in events)
            {
                resp.Events.Add(new Grpc.BoardEvent
                {
                    Id = e.Id,
                    EventType = e.EventType ?? "",
                    InstanceId = e.InstanceId ?? "",
                    InstanceName = e.InstanceName ?? "",
                    Title = e.Title ?? "",
                    Detail = e.Detail ?? "",
                    RecordedAt = e.RecordedAt ?? ""
                });
            }
            return Task.FromResult(resp);
        }

        public override Task<SimpleResponse> MarkEventBoardRead(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            // 标记到当前最新事件: 取最大已知 id(读全部后取末尾), 空板用 0
            var (events, _) = PanelDataManager.GetBoardEvents(0);
            long latestId = events.Count > 0 ? events[^1].Id : 0;
            var (ok, msg) = PanelDataManager.MarkBoardRead(latestId);
            return Task.FromResult(new SimpleResponse { Success = ok, Message = msg });
        }

        public override Task<SimpleResponse> ClearEventBoard(Empty request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = PanelDataManager.ClearBoardEvents();
            return Task.FromResult(new SimpleResponse { Success = ok, Message = msg });
        }

        public override Task<SimpleResponse> ReportBoardEvent(ReportBoardEventRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            // 面板上报事件(如文件上传进度): event_key 非空且已存在则更新同一条, 否则追加
            var (ok, msg) = PanelDataManager.UpsertBoardEvent(
                request.EventKey ?? "",
                string.IsNullOrWhiteSpace(request.EventType) ? "custom" : request.EventType,
                request.InstanceId ?? "",
                request.Title ?? "",
                request.Detail ?? "");
            return Task.FromResult(new SimpleResponse { Success = ok, Message = msg });
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
                        IsDisabled = p.IsDisabled,
                        Name = p.Name ?? "",
                        Version = p.Version ?? "",
                        Description = p.Description ?? "",
                        MetaParsed = p.MetaParsed
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

        /// <summary>插件更新: 用 plugin.yml 的插件名去 Modrinth 比对版本, 可下载替换。</summary>
        public override async Task<UpdatePluginResponse> UpdatePlugin(UpdatePluginRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);

            var r = await Backend.UpdatePluginAsync(request?.Id ?? "", request?.FileName ?? "", request?.Apply ?? false);
            return new UpdatePluginResponse
            {
                Success = r.Success,
                Message = r.Message,
                HasUpdate = r.HasUpdate,
                LocalVersion = r.LocalVersion,
                RemoteVersion = r.RemoteVersion,
                ProjectTitle = r.ProjectTitle,
                DownloadUrl = r.DownloadUrl,
                NewFileName = r.NewFileName
            };
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

        // ===== 实例文件管理 =====

        public override Task<InstanceDirResponse> ListInstanceDir(InstanceDirRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg, workPath, entries) = Backend.ListInstanceDirectory(request.Id ?? "", request.RelPath ?? "");
            var resp = new InstanceDirResponse { Success = ok, Message = msg, WorkPath = workPath };
            foreach (var e in entries)
                resp.Entries.Add(new DirEntry { Name = e.Name, IsDir = e.IsDir, SizeBytes = e.SizeBytes, ModifiedAt = e.ModifiedAt });
            return Task.FromResult(resp);
        }

        public override async Task DownloadInstanceFile(InstanceFileRequest request, IServerStreamWriter<FileDataChunk> responseStream, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);

            var (ok, msg, stream, _) = Backend.OpenInstanceFileForRead(request.Id ?? "", request.RelPath ?? "");
            if (!ok || stream == null)
                throw new RpcException(new global::Grpc.Core.Status(StatusCode.NotFound, msg));

            try
            {
                var buffer = new byte[256 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, context.CancellationToken)) > 0)
                {
                    await responseStream.WriteAsync(new FileDataChunk
                    {
                        Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, read)
                    });
                }
            }
            finally
            {
                stream.Dispose();
            }
        }

        public override Task<SimpleResponse> UploadInstanceFileChunk(InstanceFileChunkRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = Backend.WriteInstanceFileChunk(
                request.Id ?? "", request.RelPath ?? "", request.Offset,
                request.Data?.ToByteArray() ?? Array.Empty<byte>(), request.Final);
            return Task.FromResult(new SimpleResponse { Success = ok, Message = msg });
        }

        public override Task<SimpleResponse> DeleteInstanceEntry(InstanceEntryRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = Backend.DeleteInstanceEntry(request.Id ?? "", request.RelPath ?? "", request.Recursive);
            return Task.FromResult(new SimpleResponse { Success = ok, Message = msg });
        }

        public override Task<SimpleResponse> RenameInstanceEntry(InstanceRenameRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = Backend.RenameInstanceEntry(request.Id ?? "", request.OldPath ?? "", request.NewPath ?? "");
            return Task.FromResult(new SimpleResponse { Success = ok, Message = msg });
        }

        public override Task<InstanceTextFileResponse> ReadInstanceTextFile(InstanceFileRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg, content, size) = Backend.ReadInstanceTextFile(request.Id ?? "", request.RelPath ?? "");
            return Task.FromResult(new InstanceTextFileResponse { Success = ok, Message = msg, Content = content, SizeBytes = size });
        }

        public override Task<SimpleResponse> WriteInstanceTextFile(InstanceTextFileWriteRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg) = Backend.WriteInstanceTextFile(request.Id ?? "", request.RelPath ?? "", request.Content ?? "");
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
                case "op":
                    (ok, msg) = Backend.OpPlayer(id, target, true);
                    break;
                case "deop":
                    (ok, msg) = Backend.OpPlayer(id, target, false);
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

        // ===== 全服配置 (RM 管理协议 serversettings) =====
        public override async Task<ServerSettingsResponse> GetServerSettings(InstanceDetailRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg, settings) = await Task.Run(
                () => Backend.GetServerSettings(request.Id ?? ""), context.CancellationToken).ConfigureAwait(false);
            var resp = new ServerSettingsResponse { Success = ok, Message = msg };
            foreach (var s in settings)
            {
                var entry = new ServerSettingEntry
                {
                    Key = s.Key ?? "",
                    Value = s.Value ?? "",
                    Type = s.Type ?? "string",
                    Label = s.Label ?? ""
                };
                if (s.Options != null)
                    entry.Options.AddRange(s.Options);
                resp.Settings.Add(entry);
            }
            return resp;
        }

        public override Task<SimpleResponse> SetServerSettings(SetServerSettingsRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var values = new List<KeyValuePair<string, string>>();
            foreach (var v in request.Values)
                values.Add(new KeyValuePair<string, string>(v.Key ?? "", v.Value ?? ""));
            var (ok, msg) = Backend.SetServerSettings(request.InstanceId ?? "", values);
            return Task.FromResult(new SimpleResponse { Success = ok, Message = msg });
        }

        // ===== RM 管理协议通用操作(游戏规则/名单/服务器控制/能力发现) =====
        public override async Task<ManageServerResponse> ManageServer(ManageServerRequest request, ServerCallContext context)
        {
            ValidateAuthKey(context);
            EnsureClientTracked(context);
            var (ok, msg, entries, raw) = await Task.Run(
                () => Backend.RmManage(
                    request.InstanceId ?? "", request.Category ?? "", request.Action ?? "",
                    request.Target ?? "", request.Value ?? "", request.Flag),
                context.CancellationToken).ConfigureAwait(false);
            var resp = new ManageServerResponse { Success = ok, Message = msg, Raw = raw ?? "" };
            foreach (var e in entries)
                resp.Entries.Add(new RmServerEntry { Name = e.Name ?? "", Value = e.Value ?? "", Extra = e.Extra ?? "" });
            return resp;
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

        // 认证失败限流: 同一 IP 在窗口内失败 MaxAuthFailures 次后锁定一段时间
        private const int MaxAuthFailures = 5;
        private static readonly TimeSpan AuthFailureWindow = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan AuthLockoutDuration = TimeSpan.FromMinutes(5);
        private static readonly ConcurrentDictionary<string, AuthFailureState> _authFailures = new();

        private sealed class AuthFailureState
        {
            public int Count;
            public DateTime WindowStart = DateTime.UtcNow;
            public DateTime LockedUntil = DateTime.MinValue;
        }

        private static void ValidateAuthKey(ServerCallContext context)
        {
            var authKey = Config.App.GrpcAuthKey;
            // fail-close: 服务端未配置密钥时一律拒绝, 绝不因空密钥放行
            if (string.IsNullOrEmpty(authKey))
                throw new RpcException(new global::Grpc.Core.Status(StatusCode.Unauthenticated, "服务端未配置认证密钥, 已拒绝访问"));

            string peerIp = Connector.ParseIPFromPeer(context.Peer);
            var now = DateTime.UtcNow;
            var state = _authFailures.GetOrAdd(peerIp, _ => new AuthFailureState());

            lock (state)
            {
                if (state.LockedUntil > now)
                {
                    int remain = (int)Math.Ceiling((state.LockedUntil - now).TotalSeconds);
                    throw new RpcException(new global::Grpc.Core.Status(StatusCode.ResourceExhausted,
                        $"认证失败次数过多, 请 {remain} 秒后重试"));
                }
                if (now - state.WindowStart > AuthFailureWindow)
                {
                    state.Count = 0;
                    state.WindowStart = now;
                }
            }

            var header = context.RequestHeaders.FirstOrDefault(h => h.Key == "authorization");
            if (header != null && FixedTimeEquals(header.Value, authKey))
            {
                lock (state)
                {
                    state.Count = 0;
                    state.LockedUntil = DateTime.MinValue;
                }
                return;
            }

            lock (state)
            {
                state.Count++;
                if (state.Count >= MaxAuthFailures)
                {
                    state.LockedUntil = now + AuthLockoutDuration;
                    state.Count = 0;
                    state.WindowStart = now;
                }
            }
            throw new RpcException(new global::Grpc.Core.Status(StatusCode.Unauthenticated, "Invalid or missing authentication key"));
        }

        /// <summary>常量时间字符串比较, 避免通过响应时间侧信道推测密钥</summary>
        private static bool FixedTimeEquals(string? provided, string expected)
        {
            if (provided == null) return false;
            var a = Encoding.UTF8.GetBytes(provided);
            var b = Encoding.UTF8.GetBytes(expected);
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
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
