using Grpc.Core;
using Grpc.Net.Client;
using RtCli.Grpc;

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

        public bool IsConnected { get; private set; }
        public string? ConnectedServerName => _connectedServerName;
        public string? AuthKey { get; private set; }
        public string? Host { get; private set; }
        public int Port { get; private set; }

        public event Action<string>? OnLogReceived;
        public event Action? OnConnected;
        public event Action? OnDisconnected;

        // 日志环形缓冲：保留最近 500 条日志供面板轮询
        private const int LogBufferSize = 500;
        private readonly object _logBufferLock = new();
        private readonly List<string> _logBuffer = new();
        private int _logSequence = 0;

        /// <summary>
        /// 构建携带认证密钥的 gRPC 请求头。
        /// </summary>
        private Metadata? GetAuthHeaders()
        {
            if (string.IsNullOrEmpty(AuthKey)) return null;
            return new Metadata { { "authorization", AuthKey } };
        }

        public async Task<bool> ConnectAsync(string host, int port, string authKey)
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

                var address = $"http://{host}:{port}";
                _channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
                {
                    MaxReceiveMessageSize = 64 * 1024 * 1024,
                    MaxSendMessageSize = 64 * 1024 * 1024
                });
                _client = new RtCliService.RtCliServiceClient(_channel);

                var info = await _client.GetServerInfoAsync(new Empty(),
                    headers: GetAuthHeaders(),
                    deadline: DateTime.UtcNow.AddSeconds(5));
                _connectedServerName = info.ServerName;
                IsConnected = true;

                StartConnectionMonitor();
                StartLogStream();
                OnConnected?.Invoke();
                return true;
            }
            catch
            {
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

        public async Task DisconnectAsync()
        {
            _manuallyDisconnected = true;
            IsConnected = false;
            AuthKey = null;
            Host = null;
            Port = 0;
            CleanupConnection();
            OnDisconnected?.Invoke();
            DisposeChannel();
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

        public async Task<ExecuteCommandResponse?> ExecuteCommandAsync(string command)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.ExecuteCommandAsync(new ExecuteCommandRequest { Command = command },
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

        public async Task<ServerFileListResponse?> ListServerFilesAsync()
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.ListServerFilesAsync(new Empty(),
                    headers: GetAuthHeaders(),
                    deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<ServerFileResponse?> GetServerFileAsync(string serverKey, string fileName)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.GetServerFileAsync(new ServerFileRequest { ServerKey = serverKey ?? "", FileName = fileName },
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

        public async Task<PlayerEventListResponse?> GetPlayerEventsAsync(string id, int limit = 0)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.GetPlayerEventsAsync(new PlayerEventRequest { Id = id ?? "", Limit = limit }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(10)); }
            catch { MarkDisconnected(); return null; }
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

        public async Task<SimpleResponse?> ManagePlayerAsync(string id, string action, string target, string reason, string command)
        {
            if (_client == null || !IsConnected) return null;
            try { return await _client.ManagePlayerAsync(new ManagePlayerRequest { Id = id ?? "", Action = action, Target = target ?? "", Reason = reason ?? "", Command = command ?? "" }, headers: GetAuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(15)); }
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
                        BufferLog(line);
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

        private void BufferLog(string line)
        {
            lock (_logBufferLock)
            {
                _logSequence++;
                _logBuffer.Add($"#{_logSequence} {line}");
                if (_logBuffer.Count > LogBufferSize)
                    _logBuffer.RemoveAt(0);
            }
        }

        /// <summary>
        /// 获取自指定序号之后的日志。返回 (日志行列表, 最新序号)。
        /// </summary>
        public (List<string> lines, int latestSeq) GetRecentLogs(int afterSeq = 0)
        {
            lock (_logBufferLock)
            {
                var lines = new List<string>();
                foreach (var entry in _logBuffer)
                {
                    // 解析序号
                    var spaceIdx = entry.IndexOf(' ');
                    if (spaceIdx > 1 && entry[0] == '#')
                    {
                        if (int.TryParse(entry[1..spaceIdx], out var seq) && seq > afterSeq)
                        {
                            lines.Add(entry[(spaceIdx + 1)..]);
                        }
                    }
                }
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
}
