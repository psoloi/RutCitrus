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

        public event Action<string>? OnLogReceived;
        public event Action? OnConnected;
        public event Action? OnDisconnected;

        public async Task<bool> ConnectAsync(string host, int port)
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
                var address = $"http://{host}:{port}";
                _channel = GrpcChannel.ForAddress(address);
                _client = new RtCliService.RtCliServiceClient(_channel);

                var info = await _client.GetServerInfoAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(5));
                _connectedServerName = info.ServerName;
                IsConnected = true;

                StartConnectionMonitor();
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

                            await _client.GetStatusAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(5));
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
                return await _client.GetServerInfoAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<ExtensionListResponse?> GetExtensionsAsync()
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.GetExtensionsAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<UnloadExtensionResponse?> UnloadExtensionAsync(string extensionKey)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.UnloadExtensionAsync(new UnloadExtensionRequest { ExtensionKey = extensionKey }, deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<LoadExtensionResponse?> LoadExtensionAsync(string extensionPath)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.LoadExtensionAsync(new LoadExtensionRequest { ExtensionPath = extensionPath }, deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<ExecuteCommandResponse?> ExecuteCommandAsync(string command)
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.ExecuteCommandAsync(new ExecuteCommandRequest { Command = command }, deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<StatusResponse?> GetStatusAsync()
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.GetStatusAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        public async Task<ClientListResponse?> GetClientsAsync()
        {
            if (_client == null || !IsConnected) return null;
            try
            {
                return await _client.GetClientsAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch { MarkDisconnected(); return null; }
        }

        public void StartLogStream()
        {
            if (_client == null || !IsConnected) return;

            StopLogStream();
            _logCts = new CancellationTokenSource();
            _logStream = _client.StreamLogs(new Empty());

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
                        OnLogReceived?.Invoke($"[{log.Timestamp}] |{levelStr}| ({log.Source}) {log.Message}");
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

        public void StopLogStream()
        {
            _logCts?.Cancel();
            _logCts?.Dispose();
            _logCts = null;
        }
    }
}
