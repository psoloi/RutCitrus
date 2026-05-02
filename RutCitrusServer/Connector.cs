using System;
using System.Text;
using System.Threading.Tasks;
using TouchSocket.Core;
using TouchSocket.Sockets;
using Newtonsoft.Json.Linq;

namespace RutCitrusServer
{
    internal class Connector
    {
        private static TcpClient? _client;

        public static bool IsConnected => _client?.Online ?? false;
        public static string? ConnectedServerName { get; private set; }

        public static event Action<string>? OnMessageReceived;
        public static event Action? OnConnected;
        public static event Action? OnDisconnected;
        public static event Action<string>? OnExtensionListReceived;
        public static event Action<bool, string>? OnExtensionUnloadResult;

        private static void Log(string message)
        {
            OnMessageReceived?.Invoke(message);
        }

        public static async Task<bool> ConnectAsync(string ip, int port)
        {
            try
            {
                if (_client != null && _client.Online)
                {
                    try { await _client.CloseAsync(); } catch { }
                }

                _client = new TcpClient();
                ConnectedServerName = null;

                _client.Received = (client, e) =>
                {
                    try
                    {
                        var message = Encoding.UTF8.GetString(e.Memory.Span);
                        ProcessReceivedMessage(message);
                    }
                    catch (Exception ex)
                    {
                        Log($"[DEBUG] Received异常: {ex.Message}");
                    }
                    return Task.CompletedTask;
                };

                await _client.SetupAsync(new TouchSocketConfig()
                    .SetRemoteIPHost($"tcp://{ip}:{port}")
                    .ConfigurePlugins(a =>
                    {
                        a.Add<ClientConnectionPlugin>();
                    }));

                await _client.ConnectAsync();

                Log("[DEBUG] TCP连接成功");
                OnConnected?.Invoke();

                return true;
            }
            catch (Exception ex)
            {
                Log($"连接失败: {ex.Message}");
                OnDisconnected?.Invoke();
                return false;
            }
        }

        private static void ProcessReceivedMessage(string message)
        {
            if (message.StartsWith("RESULT:"))
            {
                Log(message.Substring(7));
            }
            else if (message.StartsWith("EXT_LIST:"))
            {
                var json = message.Substring(9);
                OnExtensionListReceived?.Invoke(json);
            }
            else if (message.StartsWith("EXT_UNLOAD_RESULT:"))
            {
                var parts = message.Substring(18).Split(':');
                if (parts.Length >= 2)
                {
                    bool success = parts[0] == "SUCCESS";
                    string extensionKey = parts[1];
                    OnExtensionUnloadResult?.Invoke(success, extensionKey);
                    Log($"扩展 {extensionKey} 卸载{(success ? "成功" : "失败")}");
                }
            }
            else
            {
                Log(message);
            }
        }

        public static async Task DisconnectAsync()
        {
            Log("[DEBUG] DisconnectAsync被调用");
            if (_client != null && _client.Online)
            {
                try { await _client.CloseAsync(); } catch { }
            }
            ConnectedServerName = null;
            OnDisconnected?.Invoke();
        }

        public static async Task<bool> SendCommandAsync(string command)
        {
            if (_client == null || !_client.Online)
            {
                return false;
            }

            try
            {
                await _client.SendAsync($"CMD:{command}");
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static async Task<bool> SendDataAsync(string data)
        {
            if (_client == null || !_client.Online)
            {
                return false;
            }

            try
            {
                await _client.SendAsync(data);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static async Task RequestExtensionsAsync()
        {
            if (_client == null || !_client.Online)
            {
                Log("未连接到服务器");
                return;
            }

            try
            {
                await _client.SendAsync("EXT_GET:");
            }
            catch (Exception ex)
            {
                Log($"请求扩展列表失败: {ex.Message}");
            }
        }

        public static async Task UnloadExtensionAsync(string extensionKey)
        {
            if (_client == null || !_client.Online)
            {
                Log("未连接到服务器");
                return;
            }

            try
            {
                await _client.SendAsync($"EXT_UNLOAD:{extensionKey}");
            }
            catch (Exception ex)
            {
                Log($"发送卸载请求失败: {ex.Message}");
            }
        }

        internal static void OnServerDisconnected()
        {
            Log($"[DEBUG] OnServerDisconnected被调用, Online={_client?.Online}");
            ConnectedServerName = null;
            OnDisconnected?.Invoke();
        }
    }

    internal class ClientConnectionPlugin : PluginBase, ITcpClosedPlugin
    {
        public Task OnTcpClosed(ITcpSession client, ClosedEventArgs e)
        {
            Connector.OnServerDisconnected();
            return e.InvokeNext();
        }
    }
}
