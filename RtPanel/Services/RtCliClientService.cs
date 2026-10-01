using Grpc.Core;
using Grpc.Net.Client;
using RtCli.Grpc;
using System.Security.Cryptography;

namespace RtPanel.Services
{
    public class RtCliClientService
    {
        private GrpcChannel? _channel;
        private RtCliService.RtCliServiceClient? _client;
        private string? _connectedServerName;
        private AsyncServerStreamingCall<LogMessage>? _logStream;
        private CancellationTokenSource? _logCts;
        private CancellationTokenSource? _monitorCts;
        private bool _manuallyDisconnected = false;
        private readonly object _lock = new();
        private readonly object _connectLock = new();
        private volatile bool _connecting = false;
        // 本次连接中 TLS 回调捕获的证书指纹(用于 TOFU 首次记录)
        private string? _pendingCertFingerprint;
        // 首次连接时待用户确认的证书指纹(未确认不放行, 防止首连被中间人劫持)
        private string? _pendingNewFingerprint;
        private bool _certUntrusted;
        // 证书指纹与记录不一致时置位(用于在连接失败时给出明确原因)
        private bool _certMismatch;

        public bool IsConnected { get; private set; }
        public string? ConnectedServerName => _connectedServerName;
        /// <summary>连接成功时从 GetServerInfo 获取的 RtCli 版本号</summary>
        public string? ConnectedRtCliVersion { get; private set; }
        public string? AuthKey { get; private set; }
        public string? Host { get; private set; }
        public int Port { get; private set; }
        public string? LastError { get; private set; }
        /// <summary>上次连接失败是否因证书指纹不匹配(TOFU 校验拒绝)</summary>
        public bool CertMismatch => _certMismatch;
        /// <summary>首次连接且证书未被信任(需用户确认指纹后重试)</summary>
        public bool CertUntrusted => _certUntrusted;
        /// <summary>待用户确认的首次连接证书指纹(SHA256)</summary>
        public string? PendingFingerprint => _pendingNewFingerprint;

        public event Action<string>? OnLogReceived;
        public event Action? OnConnected;
        public event Action? OnDisconnected;

        // 日志环形缓冲：保留最近 500 条日志供面板轮询(Queue 出队 O(1), 避免 List.RemoveAt(0) 整体搬移)
        // 每条记录归属实例(空 = 全局消息, 所有实例控制台都显示)
        private const int LogBufferSize = 500;
        private readonly object _logBufferLock = new();
        private readonly Queue<(int Seq, string InstanceId, string Line)> _logBuffer = new();
        private int _logSequence = 0;

        // 断线自动重连(指数退避): 网络抖动/服务重启后面板日志流可自动恢复
        private CancellationTokenSource? _reconnectCts;

        /// <summary>
        /// 构建携带认证密钥的 gRPC 请求头。
        /// </summary>
        private Metadata? GetAuthHeaders()
        {
            if (string.IsNullOrEmpty(AuthKey)) return null;
            return new Metadata { { "authorization", AuthKey } };
        }

        public async Task<bool> ConnectAsync(string host, int port, string authKey, bool trustNewCertificate = false)
        {
            lock (_connectLock)
            {
                if (_connecting) return false;
                _connecting = true;
            }

            try
            {
                if (IsConnected)
                {
                    await DisconnectAsync();
                }

                _manuallyDisconnected = false;
                Host = host;
                Port = port;
                AuthKey = authKey;
                _certMismatch = false;
                _certUntrusted = false;
                _pendingNewFingerprint = null;
                _pendingCertFingerprint = null;

                var address = $"https://{host}:{port}";

                // TOFU 证书固定(Trust On First Use):
                // 首次连接自动记录 RtCli 自签名证书的 SHA256 指纹，之后指纹不一致则拒绝握手，
                // 防止同网段中间人截获 authorization 头中的认证密钥。
                // 重置方法: 删除面板端 App_Data/known_hosts.json 中对应条目。
                var hostKey = $"{host}:{port}";
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                    {
                        if (cert == null) return false;
                        var fingerprint = Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));
                        var known = KnownHostsStore.Get(hostKey);
                        if (known == null)
                        {
                            // 首次连接: 必须由用户在登录页显式确认指纹(trustNewCertificate)后才放行,
                            // 否则直接拒绝, 避免首连被中间人接管(TOFU 首连确认)
                            if (trustNewCertificate)
                            {
                                _pendingCertFingerprint = fingerprint;
                                return true;
                            }

                            _pendingNewFingerprint = fingerprint;
                            _certUntrusted = true;
                            return false;
                        }
                        return string.Equals(known, fingerprint, StringComparison.OrdinalIgnoreCase);
                    }
                };

                _channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
                {
                    HttpHandler = handler,
                    MaxReceiveMessageSize = 64 * 1024 * 1024,
                    MaxSendMessageSize = 64 * 1024 * 1024
                });
                _client = new RtCliService.RtCliServiceClient(_channel);

                ServerInfoResponse info;
                info = await _client.GetServerInfoAsync(new Empty(),
                    headers: GetAuthHeaders(),
                    deadline: DateTime.UtcNow.AddSeconds(5));

                _connectedServerName = info.ServerName;
                ConnectedRtCliVersion = info.Version;
                IsConnected = true;
                LastError = null;

                // 认证通过后持久化首次记录的证书指纹(TOFU)
                if (_pendingCertFingerprint != null)
                {
                    KnownHostsStore.Save(hostKey, _pendingCertFingerprint);
                    _pendingCertFingerprint = null;
                }

                StartConnectionMonitor();
                StartLogStream();
                OnConnected?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                // 证书指纹不匹配会在 TLS 握手阶段被拒绝(通常表现为 Unavailable,
                // 但不同 gRPC 版本/平台的异常形态不一, 故按消息内容识别)
                var hostKey = $"{host}:{port}";
                if (!_certMismatch && KnownHostsStore.Get(hostKey) != null && LooksLikeCertRejection(ex))
                    _certMismatch = true;
                LastError = _certUntrusted
                    ? $"首次连接该服务器, 请确认其证书指纹后重试: {_pendingNewFingerprint}"
                    : _certMismatch
                        ? "服务器证书指纹与首次连接时不一致(可能是 RtCli 重新生成了证书)。若确认安全, 可点击\"重新信任服务器证书\"重连"
                        : ex.GetBaseException().Message;
                IsConnected = false;
                CleanupConnection();
                DisposeChannel();
                OnDisconnected?.Invoke();
                return false;
            }
            finally
            {
                _connecting = false;
            }
        }

        /// <summary>判断异常链中是否包含 TLS 证书被拒绝的错误</summary>
        private static bool LooksLikeCertRejection(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var msg = e.Message;
                if (string.IsNullOrEmpty(msg)) continue;
                if (msg.Contains("RemoteCertificateValidationCallback", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("remote certificate", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>清除指定主机的 TOFU 证书指纹记录(用于服务器证书更换后的重新信任)</summary>
        public void ResetKnownHost(string host, int port)
        {
            KnownHostsStore.Remove($"{host}:{port}");
        }

        public async Task DisconnectAsync()
        {
            StopAutoReconnect();
            _manuallyDisconnected = true;
            IsConnected = false;
            AuthKey = null;
            Host = null;
            Port = 0;
            CleanupConnection();
            OnDisconnected?.Invoke();
            DisposeChannel();
        }

        /// <summary>启动断线自动重连(指数退避 2s→30s), 仅用于非主动断开场景</summary>
        private void StartAutoReconnect()
        {
            if (_manuallyDisconnected || _reconnectCts != null) return;

            var host = Host;
            var port = Port;
            var key = AuthKey;
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(key) || port <= 0) return;

            var cts = new CancellationTokenSource();
            _reconnectCts = cts;
            var token = cts.Token;

            _ = Task.Run(async () =>
            {
                int delayMs = 2000;
                try
                {
                    while (!token.IsCancellationRequested && !_manuallyDisconnected)
                    {
                        try { await Task.Delay(delayMs, token); }
                        catch (OperationCanceledException) { break; }

                        if (_manuallyDisconnected || token.IsCancellationRequested) break;

                        bool ok = false;
                        try { ok = await ConnectAsync(host, port, key); }
                        catch { }
                        if (ok) break;

                        delayMs = Math.Min(delayMs * 2, 30000);
                    }
                }
                finally
                {
                    if (ReferenceEquals(_reconnectCts, cts))
                        _reconnectCts = null;
                    cts.Dispose();
                }
            });
        }

        private void StopAutoReconnect()
        {
            var cts = _reconnectCts;
            _reconnectCts = null;
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
        }

        private void DisposeChannel()
        {
            if (_channel != null)
            {
                var ch = _channel;
                _channel = null;
                _client = null;
                _ = Task.Run(async () =>
                {
                    try { await ch.ShutdownAsync(); } catch { }
                    try { ch.Dispose(); } catch { }
                });
            }
        }

        private void MarkDisconnected()
        {
            lock (_lock)
            {
                if (!IsConnected) return;
                IsConnected = false;
                CleanupConnection();
            }
            DisposeChannel();
            OnDisconnected?.Invoke();

            // 非主动断开(网络抖动/服务重启)时后台指数退避重连
            if (!_manuallyDisconnected)
                StartAutoReconnect();
        }

        private void CleanupConnection()
        {
            StopLogStream();
            StopConnectionMonitor();

            if (_logStream != null)
            {
                _logStream.Dispose();
                _logStream = null;
            }

            _connectedServerName = null;
        }

        private void StartConnectionMonitor()
        {
            StopConnectionMonitor();
            _monitorCts = new CancellationTokenSource();
            var token = _monitorCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(3000, token);

                        if (_manuallyDisconnected) return;

                        try
                        {
                            if (_client == null)
                            {
                                MarkDisconnected();
                                return;
                            }

                            await _client.GetStatusAsync(new Empty(),
                                headers: GetAuthHeaders(),
                                deadline: DateTime.UtcNow.AddSeconds(5));
                        }
                        catch
                        {
                            MarkDisconnected();
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
            });
        }

        private void StopConnectionMonitor()
        {
            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
            _monitorCts = null;
        }

        public async Task<ServerInfoResponse?> GetServerInfoAsync()
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.GetServerInfoAsync(new Empty(),
                    headers: GetAuthHeaders(),
                    deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<ExtensionListResponse?> GetExtensionsAsync()
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.GetExtensionsAsync(new Empty(),
                    headers: GetAuthHeaders(),
                    deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<UnloadExtensionResponse?> UnloadExtensionAsync(string extensionKey)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.UnloadExtensionAsync(new UnloadExtensionRequest { ExtensionKey = extensionKey },
                    headers: GetAuthHeaders(),
                    deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<LoadExtensionResponse?> LoadExtensionAsync(string extensionPath)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.LoadExtensionAsync(new LoadExtensionRequest { ExtensionPath = extensionPath },
                    headers: GetAuthHeaders(),
                    deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<ExecuteCommandResponse?> ExecuteCommandAsync(string command, string? instanceId = null)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.ExecuteCommandAsync(
                    new ExecuteCommandRequest { Command = command, InstanceId = instanceId ?? "" },
                    headers: GetAuthHeaders(),
                    deadline: DateTime.UtcNow.AddSeconds(30));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<StatusResponse?> GetStatusAsync()
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.GetStatusAsync(new Empty(),
                    headers: GetAuthHeaders(),
                    deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<ClientListResponse?> GetClientsAsync()
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.GetClientsAsync(new Empty(),
                    headers: GetAuthHeaders(),
                    deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<ConfigResponse?> GetConfigAsync()
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.GetConfigAsync(new Empty(),
                    headers: GetAuthHeaders(),
                    deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SaveConfigResponse?> SaveConfigAsync(string content)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.SaveConfigAsync(new SaveConfigRequest { Content = content },
                    headers: GetAuthHeaders(),
                    deadline: DateTime.UtcNow.AddSeconds(10));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<ConfigFileListResponse?> ListConfigFilesAsync()
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.ListConfigFilesAsync(new Empty(),
                    headers: GetAuthHeaders(),
                    deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<ConfigResponse?> GetConfigFileAsync(string fileName)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.GetConfigFileAsync(new ConfigFileRequest { FileName = fileName },
                    headers: GetAuthHeaders(),
                    deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SaveConfigResponse?> SaveConfigFileAsync(string fileName, string content)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.SaveConfigFileAsync(new SaveConfigFileRequest { FileName = fileName, Content = content },
                    headers: GetAuthHeaders(),
                    deadline: DateTime.UtcNow.AddSeconds(10));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SystemStatsResponse?> GetSystemStatsAsync()
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.GetSystemStatsAsync(new Empty(),
                    headers: GetAuthHeaders(),
                    deadline: DateTime.UtcNow.AddSeconds(10));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<ServerFileListResponse?> ListServerFilesAsync(string serverKey = "")
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.ListServerFilesAsync(new ServerFileListRequest { ServerKey = serverKey ?? "" },
                    headers: GetAuthHeaders(),
                    deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<ServerFileResponse?> GetServerFileAsync(string serverKey, string fileName, int tailLines = 0)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.GetServerFileAsync(new ServerFileRequest { ServerKey = serverKey ?? "", FileName = fileName, TailLines = tailLines },
                    headers: GetAuthHeaders(),
                    deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SaveConfigResponse?> SaveServerFileAsync(string serverKey, string fileName, string content)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.SaveServerFileAsync(new SaveServerFileRequest { ServerKey = serverKey ?? "", FileName = fileName, Content = content },
                    headers: GetAuthHeaders(),
                    deadline: DateTime.UtcNow.AddSeconds(10));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<CommandListResponse?> GetCommandListAsync()
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.GetCommandListAsync(new Empty(),
                    headers: GetAuthHeaders(),
                    deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        // ===== 实例管理 =====

        public async Task<InstanceListResponse?> GetInstanceListAsync()
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.GetInstanceListAsync(new Empty(), headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<InstanceActionResponse?> CreateInstanceAsync(CreateInstanceRequest req)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.CreateInstanceAsync(req, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<InstanceActionResponse?> DeleteInstanceAsync(string id, bool deleteFiles)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.DeleteInstanceAsync(new DeleteInstanceRequest { Id = id, DeleteFiles = deleteFiles }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<InstanceActionResponse?> UpdateInstanceAsync(UpdateInstanceRequest req)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.UpdateInstanceAsync(req, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10)); }
            catch { MarkDisconnected(); return null; }
        }

        /// <summary>扫描主机 Java 安装(JAVA_HOME/PATH/常见安装目录)</summary>
        public async Task<JavaScanResponse?> ScanJavaPathsAsync()
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.ScanJavaPathsAsync(new Empty(), headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(30)); }
            catch { return null; }
        }

        /// <summary>克隆实例(复制配置, 可选复制工作目录文件)</summary>
        public async Task<InstanceActionResponse?> CloneInstanceAsync(string id, string newIdentifier, string newName, bool copyFiles)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                var req = new CloneInstanceRequest { Id = id ?? "", NewIdentifier = newIdentifier ?? "", NewName = newName ?? "", CopyFiles = copyFiles };
                return await _client.CloneInstanceAsync(req, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddMinutes(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        /// <summary>解压实例工作目录中的 zip 并返回第一层启动脚本列表</summary>
        public async Task<ExtractArchiveResponse?> ExtractInstanceArchiveAsync(string id, string relPath)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                var req = new ExtractArchiveRequest { Id = id ?? "", RelPath = relPath ?? "" };
                return await _client.ExtractInstanceArchiveAsync(req, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddMinutes(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<InstanceActionResponse?> InstanceActionAsync(string action, List<string> ids)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                var req = new InstanceActionRequest { Action = action };
                req.Ids.AddRange(ids);
                return await _client.InstanceActionAsync(req, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(30));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<InstanceActionResponse?> CreateGroupAsync(string name, List<string> memberIds)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                var req = new CreateGroupRequest { Name = name };
                req.MemberIds.AddRange(memberIds);
                return await _client.CreateGroupAsync(req, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<InstanceActionResponse?> DeleteGroupAsync(string groupId)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.DeleteGroupAsync(new DeleteGroupRequest { GroupId = groupId }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<InstanceActionResponse?> UpdateGroupAsync(string operation, string groupId, string name, List<string> memberIds)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                var req = new UpdateGroupRequest { Operation = operation, GroupId = groupId, Name = name ?? "" };
                req.MemberIds.AddRange(memberIds);
                return await _client.UpdateGroupAsync(req, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<InstanceActionResponse?> GroupActionAsync(string action, string groupId)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.GroupActionAsync(new GroupActionRequest { Action = action, GroupId = groupId }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(30)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<InstanceDetailResponse?> GetInstanceDetailAsync(string id)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.GetInstanceDetailAsync(new InstanceDetailRequest { Id = id }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<InstanceActionResponse?> CreateBackupAsync(string id)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.CreateBackupAsync(new CreateBackupRequest { Id = id }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(120)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<InstanceActionResponse?> RestoreBackupAsync(string id, string fileName)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.RestoreBackupAsync(new RestoreBackupRequest { Id = id, FileName = fileName }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(120)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<InstanceActionResponse?> DeleteBackupAsync(string id, string fileName)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.DeleteBackupAsync(new DeleteBackupRequest { Id = id, FileName = fileName }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(30)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<TaskListResponse?> GetTaskListAsync(string taskType)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.GetTaskListAsync(new GetTaskListRequest { TaskType = taskType }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<RtStatusResponse?> GetRtStatusAsync()
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.GetRtStatusAsync(new Empty(), headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<PanelDataResponse?> GetPanelDataAsync()
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.GetPanelDataAsync(new Empty(), headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<PanelDataResponse?> SavePanelDataAsync(string jsonData)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.SavePanelDataAsync(new PanelDataRequest { JsonData = jsonData }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<ConfigDocsResponse?> GetConfigDocsAsync(string fileName)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.GetConfigDocsAsync(new ConfigFileRequest { FileName = fileName ?? "" }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(5)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<InstanceErrorsResponse?> AnalyzeInstanceErrorsAsync(string id)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.AnalyzeInstanceErrorsAsync(new InstanceDetailRequest { Id = id ?? "" }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(30)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<AiAnalyzeResponse?> AiAnalyzeInstanceErrorsAsync(string id, string range)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.AiAnalyzeInstanceErrorsAsync(new AiAnalyzeRequest { Id = id ?? "", Range = range ?? "" }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(300)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<BaseAnalyzeResponse?> BaseAnalyzeInstanceErrorsAsync(string id, string range)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.BaseAnalyzeInstanceErrorsAsync(new AiAnalyzeRequest { Id = id ?? "", Range = range ?? "" }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(60)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<PlayerEventListResponse?> GetPlayerEventsAsync(string id, int limit = 0)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.GetPlayerEventsAsync(new PlayerEventRequest { Id = id ?? "", Limit = limit }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10)); }
            catch { MarkDisconnected(); return null; }
        }

        // ===== AI 技能管理 =====
        public async Task<AiSkillListResponse?> GetAiSkillsAsync()
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.GetAiSkillsAsync(new Empty(), headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SimpleResponse?> SaveAiSkillAsync(string originalName, string name, string description, string behavior)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.SaveAiSkillAsync(new SaveAiSkillRequest
                {
                    OriginalName = originalName ?? "",
                    Name = name ?? "",
                    Description = description ?? "",
                    Behavior = behavior ?? ""
                }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(15));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SimpleResponse?> DeleteAiSkillAsync(string name)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.DeleteAiSkillAsync(new DeleteAiSkillRequest { Name = name ?? "" }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(15)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<AiSkillListResponse?> ImportAiSkillsAsync(string content, bool overwrite)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.ImportAiSkillsAsync(new ImportAiSkillsRequest { Content = content ?? "", Overwrite = overwrite },
                    headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(30));
            }
            catch { MarkDisconnected(); return null; }
        }

        // ===== AI 记忆管理 =====
        public async Task<AiMemoryListResponse?> GetAiMemoriesAsync()
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.GetAiMemoriesAsync(new Empty(), headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(15)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SimpleResponse?> SaveAiMemorySettingsAsync(AiMemorySettings settings)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.SaveAiMemorySettingsAsync(new AiMemorySettingsRequest { Settings = settings },
                    headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(15));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SimpleResponse?> ClearAiMemoriesAsync()
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.ClearAiMemoriesAsync(new Empty(), headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(15)); }
            catch { MarkDisconnected(); return null; }
        }

        // ===== AI 自动化观测 =====
        public async Task<AiAutomationStatusResponse?> GetAiAutomationStatusAsync()
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.GetAiAutomationStatusAsync(new Empty(), headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(15)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<AiToolAuditResponse?> GetAiToolAuditAsync(int limit)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.GetAiToolAuditAsync(new AiToolAuditRequest { Limit = limit },
                    headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(15));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<AiResponseListResponse?> GetAiResponsesAsync(int limit)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.GetAiResponsesAsync(new AiResponseListRequest { Limit = limit },
                    headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(20));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SimpleResponse?> DeleteAiResponseAsync(string timestamp, string taskKey)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.DeleteAiResponseAsync(
                    new DeleteAiResponseRequest { Timestamp = timestamp ?? "", TaskKey = taskKey ?? "" },
                    headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(15));
            }
            catch { MarkDisconnected(); return null; }
        }

        // ===== AI 自动化规则 =====
        public async Task<AiRuleListResponse?> GetAiRulesAsync()
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.GetAiRulesAsync(new Empty(), headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(15)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SimpleResponse?> SaveAiRulesAsync(IEnumerable<string> rules)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                var req = new AiRuleListRequest();
                req.Rules.AddRange(rules ?? Enumerable.Empty<string>());
                return await _client.SaveAiRulesAsync(req, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(15));
            }
            catch { MarkDisconnected(); return null; }
        }

        // ===== AI 工作流(任务列表 + 运行参数) =====
        public async Task<AiWorkflowResponse?> GetAiWorkflowAsync()
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.GetAiWorkflowAsync(new Empty(), headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(20)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SimpleResponse?> SaveAiTaskAsync(AiTaskConfig task, string originalKey)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                var req = new AiTaskRequest { Task = task, OriginalKey = originalKey ?? "" };
                return await _client.SaveAiTaskAsync(req, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(20));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SimpleResponse?> DeleteAiTaskAsync(string key)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.DeleteAiTaskAsync(new AiTaskKeyRequest { Key = key ?? "" },
                    headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(20));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SimpleResponse?> SaveAiRuntimeAsync(AiRuntimeConfig runtime)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.SaveAiRuntimeAsync(new AiRuntimeRequest { Runtime = runtime },
                    headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(20));
            }
            catch { MarkDisconnected(); return null; }
        }

        // ===== 调度器任务(蓝图编辑器) =====
        public async Task<SchedulerTaskListResponse?> GetSchedulerTasksAsync()
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.GetSchedulerTasksAsync(new Empty(), headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(15)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SimpleResponse?> SaveSchedulerTaskAsync(SchedulerTaskInfo task)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.SaveSchedulerTaskAsync(new SchedulerTaskRequest { Task = task },
                    headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(20));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SimpleResponse?> DeleteSchedulerTaskAsync(string key)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.DeleteSchedulerTaskAsync(new SchedulerTaskKeyRequest { Key = key },
                    headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(15));
            }
            catch { MarkDisconnected(); return null; }
        }

        // ===== 事件板 =====

        public async Task<EventBoardResponse?> GetEventBoardAsync(int limit = 0)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.GetEventBoardAsync(new EventBoardRequest { Limit = limit }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SimpleResponse?> MarkEventBoardReadAsync()
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.MarkEventBoardReadAsync(new Empty(), headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SimpleResponse?> ClearEventBoardAsync()
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.ClearEventBoardAsync(new Empty(), headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10)); }
            catch { MarkDisconnected(); return null; }
        }

        /// <summary>
        /// 向事件板上报/刷新一条事件(如文件上传进度)。
        /// eventKey 非空且已存在时更新同一条(不产生新事件); 失败返回 false。
        /// 属尽力而为的旁路上报, 失败不标记断连, 避免影响主流程。
        /// </summary>
        public async Task<bool> ReportBoardEventAsync(string eventKey, string eventType, string instanceId, string title, string detail)
        {
            if (_client == null || !IsConnected) return false;
            try
            {
                var r = await _client.ReportBoardEventAsync(new ReportBoardEventRequest
                {
                    EventKey = eventKey ?? "",
                    EventType = eventType ?? "",
                    InstanceId = instanceId ?? "",
                    Title = title ?? "",
                    Detail = detail ?? ""
                }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(5));
                return r?.Success == true;
            }
            catch { return false; }
        }

        public async Task<JarFileListResponse?> ListPluginsAsync(string id)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.ListPluginsAsync(new InstanceDetailRequest { Id = id ?? "" }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<JarFileListResponse?> ListModsAsync(string id)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.ListModsAsync(new InstanceDetailRequest { Id = id ?? "" }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SimpleResponse?> ToggleJarFileAsync(string id, string fileName, string type)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.ToggleJarFileAsync(new ManageJarFileRequest { Id = id ?? "", FileName = fileName, Type = type }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10)); }
            catch { MarkDisconnected(); return null; }
        }

        /// <summary>插件更新(Modrinth): apply=false 只检查, apply=true 下载并替换。</summary>
        public async Task<UpdatePluginResponse?> UpdatePluginAsync(string id, string fileName, bool apply)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.UpdatePluginAsync(
                    new UpdatePluginRequest { Id = id ?? "", FileName = fileName ?? "", Apply = apply },
                    headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(90));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SimpleResponse?> DeleteJarFileAsync(string id, string fileName, string type)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.DeleteJarFileAsync(new ManageJarFileRequest { Id = id ?? "", FileName = fileName, Type = type }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SimpleResponse?> UploadJarFileAsync(string id, string fileName, string type, byte[] content)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.UploadJarFileAsync(new UploadJarFileRequest { Id = id ?? "", FileName = fileName, Type = type, Content = Google.Protobuf.ByteString.CopyFrom(content) }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(60)); }
            catch { MarkDisconnected(); return null; }
        }

        // ===== 实例文件管理 =====

        public async Task<InstanceDirResponse?> ListInstanceDirAsync(string id, string relPath)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.ListInstanceDirAsync(new InstanceDirRequest { Id = id ?? "", RelPath = relPath ?? "" }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(30)); }
            catch { MarkDisconnected(); return null; }
        }

        /// <summary>打开实例文件下载流(服务器流式)。业务错误(文件不存在等)在遍历流时以 RpcException 抛出, 由调用方处理。</summary>
        public AsyncServerStreamingCall<FileDataChunk>? DownloadInstanceFile(string id, string relPath)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return _client.DownloadInstanceFile(new InstanceFileRequest { Id = id ?? "", RelPath = relPath ?? "" }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddMinutes(30));
            }
            catch { return null; }
        }

        public async Task<SimpleResponse?> UploadInstanceFileChunkAsync(string id, string relPath, long offset, byte[] data, bool final)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.UploadInstanceFileChunkAsync(new InstanceFileChunkRequest
                {
                    Id = id ?? "",
                    RelPath = relPath ?? "",
                    Offset = offset,
                    Data = Google.Protobuf.ByteString.CopyFrom(data),
                    Final = final
                }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(60));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SimpleResponse?> DeleteInstanceEntryAsync(string id, string relPath, bool recursive)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.DeleteInstanceEntryAsync(new InstanceEntryRequest { Id = id ?? "", RelPath = relPath ?? "", Recursive = recursive }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(120)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SimpleResponse?> RenameInstanceEntryAsync(string id, string oldPath, string newPath)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.RenameInstanceEntryAsync(new InstanceRenameRequest { Id = id ?? "", OldPath = oldPath ?? "", NewPath = newPath ?? "" }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(30)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<InstanceTextFileResponse?> ReadInstanceTextFileAsync(string id, string relPath)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.ReadInstanceTextFileAsync(new InstanceFileRequest { Id = id ?? "", RelPath = relPath ?? "" }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(30)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SimpleResponse?> WriteInstanceTextFileAsync(string id, string relPath, string content)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.WriteInstanceTextFileAsync(new InstanceTextFileWriteRequest { Id = id ?? "", RelPath = relPath ?? "", Content = content ?? "" }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(60)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SimpleResponse?> ManagePlayerAsync(string id, string action, string target, string reason, string command)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.ManagePlayerAsync(new ManagePlayerRequest { Id = id ?? "", Action = action, Target = target ?? "", Reason = reason ?? "", Command = command ?? "" }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(15)); }
            catch { MarkDisconnected(); return null; }
        }

        // ===== 全服配置 (RM 管理协议 serversettings) =====

        public async Task<ServerSettingsResponse?> GetServerSettingsAsync(string id)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.GetServerSettingsAsync(new InstanceDetailRequest { Id = id ?? "" }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(30)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<SimpleResponse?> SetServerSettingsAsync(string id, List<ServerSettingValue> values)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                var req = new SetServerSettingsRequest { InstanceId = id ?? "" };
                if (values != null) req.Values.AddRange(values);
                return await _client.SetServerSettingsAsync(req, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(30));
            }
            catch { MarkDisconnected(); return null; }
        }

        // ===== RM 管理协议通用操作(游戏规则/名单/服务器控制/能力发现) =====

        public async Task<ManageServerResponse?> ManageServerAsync(string id, string category, string action, string target, string value, bool flag)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                var req = new ManageServerRequest
                {
                    InstanceId = id ?? "",
                    Category = category ?? "",
                    Action = action ?? "",
                    Target = target ?? "",
                    Value = value ?? "",
                    Flag = flag
                };
                return await _client.ManageServerAsync(req, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(60));
            }
            catch { MarkDisconnected(); return null; }
        }

        // ===== 面板向导 =====

        public async Task<ServerTypeListResponse?> GetServerTypesAsync()
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.GetServerTypesAsync(new Empty(), headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<ServerVersionListResponse?> GetServerVersionsAsync(string typeKey)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.GetServerVersionsAsync(new ServerVersionRequest { TypeKey = typeKey ?? "" }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(30)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<DownloadServerJarResponse?> DownloadServerJarAsync(string typeKey, string version, string workPath)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.DownloadServerJarAsync(new DownloadServerJarRequest { TypeKey = typeKey ?? "", Version = version ?? "", WorkPath = workPath ?? "" }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddMinutes(10)); }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<GroupBuildResponse?> GroupBuildAsync(GroupBuildRequest req)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.GroupBuildAsync(req, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddMinutes(10)); }
            catch { MarkDisconnected(); return null; }
        }

        public void StartLogStream()
        {
            if (_client == null || !IsConnected) return;

            StopLogStream();
            _logCts = new CancellationTokenSource();
            _logStream = _client.StreamLogs(new Empty(), headers: GetAuthHeaders());

            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var log in _logStream.ResponseStream.ReadAllAsync(_logCts.Token))
                    {
                        var levelStr = log.Level switch
                        {
                            1 => "信息",
                            2 => "警告",
                            3 => "错误",
                            _ => "调试"
                        };
                        var line = $"[{log.Timestamp}] |{levelStr}| ({log.Source}) {log.Message}";
                        BufferLog(line, log.InstanceId);
                        OnLogReceived?.Invoke(line);
                    }
                }
                catch (OperationCanceledException) { }
                catch
                {
                    if (!_manuallyDisconnected)
                    {
                        MarkDisconnected();
                    }
                }
            });
        }

        private void BufferLog(string line, string instanceId)
        {
            lock (_logBufferLock)
            {
                _logSequence++;
                _logBuffer.Enqueue((_logSequence, instanceId ?? "", line));
                while (_logBuffer.Count > LogBufferSize)
                    _logBuffer.Dequeue();
            }
        }

        /// <summary>
        /// 获取自指定序号之后的日志。返回 (日志行列表, 最新序号)。
        /// instanceId 为空时返回全部; 非空时只返回该实例日志 + 全局消息。
        /// maxLines &gt; 0 时只返回末尾 maxLines 行(首屏/切换实例时避免全量重放)。
        /// </summary>
        public (List<string> lines, int latestSeq) GetRecentLogs(int afterSeq = 0, string? instanceId = null,
            int maxLines = 0)
        {
            lock (_logBufferLock)
            {
                var lines = new List<string>();
                foreach (var (seq, instId, line) in _logBuffer)
                {
                    if (seq <= afterSeq) continue;
                    if (!string.IsNullOrEmpty(instanceId) &&
                        !string.IsNullOrEmpty(instId) &&
                        !string.Equals(instId, instanceId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    lines.Add(line);
                }
                if (maxLines > 0 && lines.Count > maxLines)
                    lines.RemoveRange(0, lines.Count - maxLines);
                return (lines, _logSequence);
            }
        }

        public void StopLogStream()
        {
            _logCts?.Cancel();
            _logCts?.Dispose();
            _logCts = null;
        }
    }

    /// <summary>
    /// TOFU 已知主机证书指纹存储(App_Data/known_hosts.json)。
    /// 记录每个 host:port 首次成功认证时 RtCli 服务端证书的 SHA256 指纹，
    /// 之后连接指纹不一致则拒绝握手。删除对应条目即可重置信任。
    /// </summary>
    internal static class KnownHostsStore
    {
        private static readonly object _lock = new();
        private static Dictionary<string, string>? _cache;

        private static string StorePath
        {
            get
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "App_Data");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "known_hosts.json");
            }
        }

        public static string? Get(string hostKey)
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _cache!.TryGetValue(hostKey, out var fp) ? fp : null;
            }
        }

        public static void Save(string hostKey, string fingerprint)
        {
            lock (_lock)
            {
                EnsureLoaded();
                _cache![hostKey] = fingerprint;
                try
                {
                    File.WriteAllText(StorePath, System.Text.Json.JsonSerializer.Serialize(_cache,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
                catch
                {
                    // 持久化失败仅影响下次启动的指纹校验，内存中的指纹仍生效
                }
            }
        }

        /// <summary>删除指定主机的指纹记录(重新信任), 返回是否确实删除了条目</summary>
        public static bool Remove(string hostKey)
        {
            lock (_lock)
            {
                EnsureLoaded();
                if (!_cache!.Remove(hostKey)) return false;
                try
                {
                    File.WriteAllText(StorePath, System.Text.Json.JsonSerializer.Serialize(_cache,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
                catch
                {
                    // 持久化失败仅影响下次启动, 内存中已删除
                }
                return true;
            }
        }

        private static void EnsureLoaded()
        {
            if (_cache != null) return;
            try
            {
                if (File.Exists(StorePath))
                {
                    var json = File.ReadAllText(StorePath);
                    _cache = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                             ?? new Dictionary<string, string>();
                    return;
                }
            }
            catch
            {
                // 解析失败视为无记录(重新走 TOFU 首次记录流程)
            }
            _cache = new Dictionary<string, string>();
        }
    }
}
