using RtCli.Modules.Extension;
using RtCli.Modules.Unit;
using Spectre.Console;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RtCli.Modules.Function
{
    public static class MCManagementStatus
    {
        public static volatile bool Connected = false;
        public static volatile string LastError = "";
        public static string Endpoint = "";
    }

    /// <summary>全服配置(serversettings)项定义</summary>
    public sealed class ServerSettingDef
    {
        public string Key { get; init; } = "";
        public string Type { get; init; } = "string";   // bool / int / enum / string
        public string Label { get; init; } = "";
        public string[] Options { get; init; } = Array.Empty<string>();
    }

    /// <summary>全服配置项读取结果</summary>
    public sealed class ServerSettingValueResult
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
        public string Type { get; set; } = "string";
        public string Label { get; set; } = "";
        public string[] Options { get; set; } = Array.Empty<string>();
        public bool Supported { get; set; } = true;
        public string Error { get; set; } = "";
    }

    /// <summary>名单/游戏规则条目(管理协议通用结果)</summary>
    public sealed class RmEntry
    {
        /// <summary>名称(玩家名 / IP / 规则名)</summary>
        public string Name { get; set; } = "";
        /// <summary>值(封禁原因 / 规则值)</summary>
        public string Value { get; set; } = "";
        /// <summary>附加信息(IP / UUID / 来源等)</summary>
        public string Extra { get; set; } = "";
    }

    /// <summary>
    /// MC 服务端管理协议(Server Management Protocol, MC 1.21.9+) 客户端。
    /// JSON-RPC 2.0 over WebSocket, Authorization: Bearer &lt;secret&gt;。
    /// 每个实例持有一个连接对象, 按 id 匹配响应、自动重连(指数退避)。
    /// </summary>
    public sealed class MCManagementConnection
    {
        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private readonly object _lock = new();
        private int _nextId = 0;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private volatile bool _running;
        private volatile bool _connected;
        private volatile string _lastError = "";
        private volatile string _endpoint = "";

        /// <summary>收到管理协议通知(method, params)。params 可能为 Undefined。</summary>
        public event Action<string, JsonElement>? NotificationReceived;

        public bool IsConnected => _connected;
        /// <summary>后台连接循环是否仍在运行(用于看门狗判断是否需要重新拉起)</summary>
        public bool IsRunning => _running;
        public string LastError => _lastError;
        public string Endpoint => _endpoint;

        /// <summary>启动连接(后台自动重连)。重复调用会先停止旧连接。</summary>
        public void Start(string host, int port, string secret, bool tls)
        {
            Stop();
            if (string.IsNullOrWhiteSpace(host) || port <= 0 || string.IsNullOrWhiteSpace(secret))
            {
                _lastError = "管理协议参数不完整(host/port/secret)";
                return;
            }
            lock (_lock)
            {
                _running = true;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _loop = Task.Run(() => RunAsync(host, port, secret, tls, token));
            }
        }

        /// <summary>停止连接并中断所有等待中的请求。</summary>
        public void Stop()
        {
            CancellationTokenSource? cts;
            ClientWebSocket? ws;
            lock (_lock)
            {
                _running = false;
                cts = _cts;
                _cts = null;
                ws = _ws;
            }
            try { cts?.Cancel(); } catch { }
            try
            {
                if (ws != null && ws.State == WebSocketState.Open)
                    ws.Abort();
            }
            catch { }
            _connected = false;
            FailPending("管理协议连接已关闭");
        }

        private async Task RunAsync(string host, int port, string secret, bool tls, CancellationToken ct)
        {
            int backoff = 3000;
            while (!ct.IsCancellationRequested && _running)
            {
                ClientWebSocket? ws = null;
                try
                {
                    string url = (tls ? "wss" : "ws") + $"://{host}:{port}/";
                    ws = new ClientWebSocket();
                    // 认证方式: Authorization: Bearer <secret>(MC 1.21.9 起的标准方式, 不受 Origin 校验约束)。
                    // 不要改用 WebSocket 子协议方式(minecraft-v1,<secret>) —— 该方式受 Origin 校验约束,
                    // 需服务端配置 management-server-allowed-origins 才可用(默认空 = 该方式被禁用),
                    // 否则握手被拒返回 401(表现为 "returned status code '401' when '101' was expected")。
                    ws.Options.SetRequestHeader("Authorization", "Bearer " + secret);
                    if (tls)
                        ws.Options.RemoteCertificateValidationCallback =
                            (_, certificate, _, _) => ValidateTofuCertificate(host, port, certificate);
                    lock (_lock) { _ws = ws; }
                    _endpoint = url;
                    _pendingCertError = "";

                    await ws.ConnectAsync(new Uri(url), ct);
                    if (ct.IsCancellationRequested) { ws.Dispose(); return; }
                    _connected = true;
                    _lastError = "";
                    backoff = 3000;
                    Output.Log($"管理协议已连接: {url}", 1, "McManagement");

                    await ReceiveLoopAsync(ws, ct);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    // 证书校验拒绝时使用更明确的原因(TOFU 不匹配等), 而不是笼统的 SSL 握手失败
                    _lastError = string.IsNullOrEmpty(_pendingCertError) ? ex.Message : _pendingCertError;
                    Output.Log($"管理协议连接失败: {_lastError}", 2, "McManagement");
                }
                finally
                {
                    _connected = false;
                    lock (_lock) { if (ReferenceEquals(_ws, ws)) _ws = null; }
                    try { ws?.Dispose(); } catch { }
                    FailPending("管理协议连接已断开");
                }

                if (ct.IsCancellationRequested || !_running) break;
                try { await Task.Delay(backoff, ct); } catch { break; }
                backoff = Math.Min(backoff * 2, 30000);
            }
        }

        // ===== 管理协议 TLS: TOFU(首次信任)证书指纹固定, 与面板侧策略保持一致 =====

        private static readonly object _knownHostsLock = new();
        private static Dictionary<string, string>? _knownHosts;
        private static string KnownHostsPath => Path.Combine(Config.DataPath, "rm_known_hosts.json");
        // 本次 TLS 校验失败原因(用于覆盖笼统的握手异常信息)
        private volatile string _pendingCertError = "";

        /// <summary>
        /// 校验管理协议服务端证书: 首次连接记录 SHA256 指纹(TOFU), 之后必须与记录一致。
        /// 指纹变化直接拒绝连接, 防止中间人劫持管理通道。
        /// </summary>
        private bool ValidateTofuCertificate(string host, int port, X509Certificate? certificate)
        {
            if (certificate == null)
            {
                _pendingCertError = "管理协议 TLS 握手未提供服务端证书";
                return false;
            }

            string fingerprint;
            try
            {
                using var cert = new X509Certificate2(certificate);
                fingerprint = cert.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
            }
            catch (Exception ex)
            {
                _pendingCertError = $"管理协议证书解析失败: {ex.Message}";
                return false;
            }

            string key = $"{host}:{port}";
            string? known;
            lock (_knownHostsLock)
            {
                EnsureKnownHostsLoaded();
                _knownHosts!.TryGetValue(key, out known);
                if (known == null)
                {
                    // 首次连接: 记录指纹(TOFU)
                    _knownHosts[key] = fingerprint;
                    SaveKnownHosts();
                    Output.Log($"管理协议首次连接已固定证书指纹: {key} (SHA256 {fingerprint})", 1, "McManagement");
                    return true;
                }
            }

            if (!string.Equals(known, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                _pendingCertError =
                    $"管理协议证书指纹不匹配({key}): 已固定 {known}, 当前 {fingerprint}。" +
                    $"如确认服务端更换了证书, 请删除 {KnownHostsPath} 中该条目后重连";
                Output.Log(_pendingCertError, 3, "McManagement");
                return false;
            }

            return true;
        }

        private static void EnsureKnownHostsLoaded()
        {
            if (_knownHosts != null) return;
            _knownHosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(KnownHostsPath))
                {
                    var json = File.ReadAllText(KnownHostsPath);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (loaded != null)
                        foreach (var kvp in loaded)
                            _knownHosts[kvp.Key] = kvp.Value;
                }
            }
            catch (Exception ex)
            {
                Output.Log($"管理协议已知主机指纹读取失败(将按首次信任处理): {ex.Message}", 2, "McManagement");
            }
        }

        private static void SaveKnownHosts()
        {
            try
            {
                AtomicFile.WriteAllText(KnownHostsPath, JsonSerializer.Serialize(_knownHosts));
            }
            catch (Exception ex)
            {
                Output.Log($"管理协议证书指纹保存失败: {ex.Message}", 2, "McManagement");
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var ms = new MemoryStream();
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult res;
                do
                {
                    res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (res.MessageType == WebSocketMessageType.Close)
                        return;
                    ms.Write(buffer, 0, res.Count);
                } while (!res.EndOfMessage);

                string text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                Dispatch(text);
            }
        }

        private void Dispatch(string text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;

                if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                {
                    int id = idEl.GetInt32();
                    if (_pending.TryRemove(id, out var tcs))
                    {
                        if (root.TryGetProperty("error", out var errEl))
                            tcs.TrySetException(new Exception(DescribeError(errEl)));
                        else if (root.TryGetProperty("result", out var resEl))
                            tcs.TrySetResult(resEl.Clone());
                        else
                            tcs.TrySetResult(default);
                        return;
                    }
                }

                if (root.TryGetProperty("method", out var mEl) && mEl.ValueKind == JsonValueKind.String)
                {
                    string method = mEl.GetString() ?? "";
                    JsonElement prms = default;
                    if (root.TryGetProperty("params", out var pEl)) prms = pEl.Clone();
                    try { NotificationReceived?.Invoke(method, prms); }
                    catch (Exception ex) { Output.Log($"管理协议通知处理异常: {ex.Message}", 2, "McManagement"); }
                }
            }
            catch { }
        }

        private static string DescribeError(JsonElement err)
        {
            try
            {
                if (err.ValueKind == JsonValueKind.Object)
                {
                    string msg = err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString() ?? "" : "";
                    string data = err.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString() ?? "" : "";
                    return string.IsNullOrEmpty(data) ? (string.IsNullOrEmpty(msg) ? err.GetRawText() : msg) : data;
                }
                return err.GetRawText();
            }
            catch { return "未知错误"; }
        }

        private void FailPending(string reason)
        {
            foreach (var key in _pending.Keys.ToList())
            {
                if (_pending.TryRemove(key, out var tcs))
                    tcs.TrySetException(new Exception(reason));
            }
        }

        /// <summary>发送 JSON-RPC 请求并等待结果。异常: 未连接/超时/服务端错误。</summary>
        public async Task<JsonElement> CallAsync(string method, JsonArray? prms, int timeoutMs = 5000)
        {
            var ws = _ws;
            if (ws == null || ws.State != WebSocketState.Open || !_connected)
                throw new InvalidOperationException("管理协议未连接");

            int id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            var obj = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method
            };
            if (prms != null) obj["params"] = prms;
            byte[] bytes = Encoding.UTF8.GetBytes(obj.ToJsonString());

            try
            {
                await _sendLock.WaitAsync();
                try
                {
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                finally { _sendLock.Release(); }
            }
            catch
            {
                _pending.TryRemove(id, out _);
                throw;
            }

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completed != tcs.Task)
            {
                _pending.TryRemove(id, out _);
                throw new TimeoutException($"管理协议请求超时: {method}");
            }
            return await tcs.Task;
        }

        /// <summary>同步包装: 返回 (成功, 消息)。成功时 Message 为结果原文(可能为空)。</summary>
        public (bool Success, string Message) Invoke(string method, JsonArray? prms, int timeoutMs = 5000)
        {
            try
            {
                var r = CallAsync(method, prms, timeoutMs).GetAwaiter().GetResult();
                return (true, r.ValueKind == JsonValueKind.Undefined ? "" : r.GetRawText());
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // ===== 玩家管理(管理协议) =====

        private static JsonObject PlayerRef(string name) => new JsonObject { ["name"] = name };

        public (bool Success, string Message) OpPlayer(string playerName, bool op)
        {
            var prms = new JsonArray { new JsonArray { new JsonObject { ["player"] = PlayerRef(playerName) } } };
            return Invoke(op ? "minecraft:operators/add" : "minecraft:operators/remove", prms);
        }

        public (bool Success, string Message) BanPlayer(string playerName, string reason)
        {
            var entry = new JsonObject { ["player"] = PlayerRef(playerName) };
            if (!string.IsNullOrWhiteSpace(reason)) entry["reason"] = reason;
            var prms = new JsonArray { new JsonArray { entry } };
            return Invoke("minecraft:bans/add", prms);
        }

        public (bool Success, string Message) UnbanPlayer(string playerName)
        {
            var prms = new JsonArray { new JsonArray { PlayerRef(playerName) } };
            return Invoke("minecraft:bans/remove", prms);
        }

        public (bool Success, string Message) BanIp(string ipOrPlayer, string reason)
        {
            var entry = new JsonObject();
            if (LooksLikeIp(ipOrPlayer)) entry["ip"] = ipOrPlayer;
            else entry["player"] = PlayerRef(ipOrPlayer);
            if (!string.IsNullOrWhiteSpace(reason)) entry["reason"] = reason;
            var prms = new JsonArray { new JsonArray { entry } };
            return Invoke("minecraft:ip_bans/add", prms);
        }

        public (bool Success, string Message) UnbanIp(string ip)
        {
            var prms = new JsonArray { new JsonArray { (JsonNode)ip } };
            return Invoke("minecraft:ip_bans/remove", prms);
        }

        public (bool Success, string Message) KickPlayer(string playerName, string reason)
        {
            var kick = new JsonObject { ["player"] = PlayerRef(playerName) };
            if (!string.IsNullOrWhiteSpace(reason)) kick["message"] = new JsonObject { ["text"] = reason };
            var prms = new JsonArray { new JsonArray { kick } };
            return Invoke("minecraft:players/kick", prms);
        }

        private static bool LooksLikeIp(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            return s.Contains('.') || s.Contains(':');
        }

        // ===== 全服配置(serversettings) =====

        /// <summary>支持的全服配置项清单(管理协议 serversettings 命名空间)</summary>
        public static readonly ServerSettingDef[] SettingDefs =
        {
            new() { Key = "difficulty", Type = "enum", Label = "难度", Options = new[] { "peaceful", "easy", "normal", "hard" } },
            new() { Key = "game_mode", Type = "enum", Label = "默认游戏模式", Options = new[] { "survival", "creative", "adventure", "spectator" } },
            new() { Key = "force_game_mode", Type = "bool", Label = "强制默认游戏模式" },
            new() { Key = "max_players", Type = "int", Label = "最大玩家数" },
            new() { Key = "motd", Type = "string", Label = "服务器描述(MOTD)" },
            new() { Key = "view_distance", Type = "int", Label = "视距(区块)" },
            new() { Key = "simulation_distance", Type = "int", Label = "模拟距离(区块)" },
            new() { Key = "allow_flight", Type = "bool", Label = "允许飞行" },
            new() { Key = "spawn_protection_radius", Type = "int", Label = "出生点保护半径" },
            new() { Key = "pause_when_empty_seconds", Type = "int", Label = "空服暂停延迟(秒)" },
            new() { Key = "player_idle_timeout", Type = "int", Label = "挂机踢出(秒)" },
            new() { Key = "use_allowlist", Type = "bool", Label = "启用白名单" },
            new() { Key = "enforce_allowlist", Type = "bool", Label = "立即执行白名单" },
            new() { Key = "autosave", Type = "bool", Label = "自动保存" },
            new() { Key = "accept_transfers", Type = "bool", Label = "接受玩家转移" },
            new() { Key = "hide_online_players", Type = "bool", Label = "隐藏在线玩家" },
            new() { Key = "status_replies", Type = "bool", Label = "响应状态查询" },
            new() { Key = "status_heartbeat_interval", Type = "int", Label = "状态心跳间隔(秒)" },
            new() { Key = "operator_user_permission_level", Type = "int", Label = "OP 权限等级" },
            new() { Key = "entity_broadcast_range", Type = "int", Label = "实体广播范围(%)" },
        };

        /// <summary>读取全部全服配置(并发请求)。</summary>
        public async Task<List<ServerSettingValueResult>> ReadAllSettingsAsync(int timeoutMs = 5000)
        {
            var tasks = SettingDefs.Select(d => ReadOneAsync(d, timeoutMs)).ToArray();
            var results = await Task.WhenAll(tasks);
            return results.ToList();
        }

        private async Task<ServerSettingValueResult> ReadOneAsync(ServerSettingDef d, int timeoutMs)
        {
            var res = new ServerSettingValueResult { Key = d.Key, Type = d.Type, Label = d.Label, Options = d.Options };
            try
            {
                var el = await CallAsync("minecraft:serversettings/" + d.Key, null, timeoutMs);
                res.Value = FirstScalarToString(el);
            }
            catch (Exception ex)
            {
                res.Supported = false;
                res.Error = ex.Message;
            }
            return res;
        }

        /// <summary>应用单个全服配置(serversettings/&lt;key&gt;/set)。</summary>
        public (bool Success, string Message) ApplySetting(string key, string type, string value, int timeoutMs = 5000)
        {
            try
            {
                JsonNode? v = type switch
                {
                    "bool" => JsonValue.Create(string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)),
                    "int" => JsonValue.Create(int.TryParse(value, out var i) ? i : 0),
                    _ => JsonValue.Create(value)
                };
                var prms = new JsonArray { v };
                CallAsync("minecraft:serversettings/" + key + "/set", prms, timeoutMs).GetAwaiter().GetResult();
                return (true, "已应用");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private static string FirstScalarToString(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in el.EnumerateObject())
                        return ScalarToString(prop.Value);
                    return "";
                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray())
                        return ScalarToString(item);
                    return "";
                default:
                    return ScalarToString(el);
            }
        }

        private static string ScalarToString(JsonElement el) => el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "",
            _ => ""
        };

        // ===== 游戏规则(gamerules) =====

        /// <summary>读取全部游戏规则(minecraft:gamerules)。</summary>
        public async Task<List<RmEntry>> ReadGamerulesAsync(int timeoutMs = 8000)
        {
            var el = await CallAsync("minecraft:gamerules", null, timeoutMs);
            return ExtractGamerules(el);
        }

        /// <summary>设置单个游戏规则(minecraft:gamerules/update, 参数 UntypedGameRule {key, value})。</summary>
        public (bool Success, string Message) SetGamerule(string rule, string value, int timeoutMs = 5000)
        {
            try
            {
                JsonNode? v;
                if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
                    v = JsonValue.Create(string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
                else if (long.TryParse(value, out var l))
                    v = JsonValue.Create(l);
                else
                    v = JsonValue.Create(value);
                var prms = new JsonArray { new JsonObject { ["key"] = rule, ["value"] = v } };
                CallAsync("minecraft:gamerules/update", prms, timeoutMs).GetAwaiter().GetResult();
                return (true, "已应用");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // ===== 名单(allowlist / operators / bans / ip_bans) =====

        /// <summary>读取名单条目。category: allowlist / operators / bans / ip_bans</summary>
        public async Task<List<RmEntry>> ReadListAsync(string category, int timeoutMs = 8000)
        {
            string method = category switch
            {
                "allowlist" => "minecraft:allowlist",
                "operators" => "minecraft:operators",
                "bans" => "minecraft:bans",
                "ip_bans" => "minecraft:ip_bans",
                _ => throw new ArgumentException("未知名单类型: " + category)
            };
            var el = await CallAsync(method, null, timeoutMs);
            return ExtractEntries(el);
        }

        /// <summary>名单增删清(allowlist / operators / bans / ip_bans)。action: add / remove / clear</summary>
        public (bool Success, string Message) ListModify(string category, string action, string target, string reason = "")
        {
            try
            {
                string t = target ?? "";
                string method;
                JsonArray prms;
                switch (category)
                {
                    case "allowlist":
                        if (action == "clear") { method = "minecraft:allowlist/clear"; prms = new JsonArray(); }
                        else
                        {
                            method = action == "remove" ? "minecraft:allowlist/remove" : "minecraft:allowlist/add";
                            prms = new JsonArray { new JsonArray { new JsonObject { ["name"] = t } } };
                        }
                        break;
                    case "operators":
                        if (action == "add")
                        {
                            method = "minecraft:operators/add";
                            prms = new JsonArray { new JsonArray { new JsonObject { ["player"] = PlayerRef(t) } } };
                        }
                        else
                        {
                            method = "minecraft:operators/remove";
                            prms = new JsonArray { new JsonArray { new JsonObject { ["name"] = t } } };
                        }
                        break;
                    case "bans":
                        if (action == "add")
                        {
                            method = "minecraft:bans/add";
                            var entry = new JsonObject { ["player"] = PlayerRef(t) };
                            if (!string.IsNullOrWhiteSpace(reason)) entry["reason"] = reason;
                            prms = new JsonArray { new JsonArray { entry } };
                        }
                        else
                        {
                            method = "minecraft:bans/remove";
                            prms = new JsonArray { new JsonArray { new JsonObject { ["name"] = t } } };
                        }
                        break;
                    case "ip_bans":
                        if (action == "add")
                        {
                            method = "minecraft:ip_bans/add";
                            var entry = new JsonObject();
                            if (LooksLikeIp(t)) entry["ip"] = t;
                            else entry["player"] = PlayerRef(t);
                            if (!string.IsNullOrWhiteSpace(reason)) entry["reason"] = reason;
                            prms = new JsonArray { new JsonArray { entry } };
                        }
                        else
                        {
                            method = "minecraft:ip_bans/remove";
                            prms = new JsonArray { new JsonArray { (JsonNode)t } };
                        }
                        break;
                    default:
                        return (false, "未知名单类型: " + category);
                }
                return Invoke(method, prms);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // ===== 服务器控制(server/*) =====

        /// <summary>保存世界(minecraft:server/save, flush: boolean)。</summary>
        public (bool Success, string Message) ServerSave(int timeoutMs = 15000)
            => Invoke("minecraft:server/save", new JsonArray { true }, timeoutMs);

        /// <summary>停止服务端(minecraft:server/stop)。</summary>
        public (bool Success, string Message) ServerStop(int timeoutMs = 15000)
            => Invoke("minecraft:server/stop", null, timeoutMs);

        /// <summary>发送系统消息(minecraft:server/system_message)。</summary>
        public (bool Success, string Message) SystemMessage(string text, bool overlay, int timeoutMs = 5000)
        {
            var entry = new JsonObject { ["text"] = text ?? "" };
            if (overlay) entry["overlay"] = true;
            // 参数为单个 message: System Message 对象, 按位置传递即 [ {text, overlay?} ]
            var prms = new JsonArray { entry };
            return Invoke("minecraft:server/system_message", prms, timeoutMs);
        }

        /// <summary>查询服务端状态(minecraft:server/status), 返回 (成功, 消息, 原始JSON)。</summary>
        public (bool Success, string Message, string Raw) ServerStatus(int timeoutMs = 8000)
        {
            try
            {
                var el = CallAsync("minecraft:server/status", null, timeoutMs).GetAwaiter().GetResult();
                return (true, "OK", el.ValueKind == JsonValueKind.Undefined ? "" : el.GetRawText());
            }
            catch (Exception ex)
            {
                return (false, ex.Message, "");
            }
        }

        /// <summary>
        /// 从 minecraft:server/status 读取在线人数(协议无任意命令执行方法, 但状态接口包含玩家数)。
        /// 返回 (成功, 在线人数, 上限)。失败或字段缺失时返回 -1。
        /// </summary>
        public (bool Success, int Online, int Max) TryGetPlayerCount(int timeoutMs = 5000)
        {
            var (ok, _, raw) = ServerStatus(timeoutMs);
            if (!ok || string.IsNullOrWhiteSpace(raw)) return (false, -1, -1);
            try
            {
                using var doc = JsonDocument.Parse(raw);
                int online = FindIntByKeys(doc.RootElement, "online", "online_players", "onlineplayers", "player_count", "playercount");
                int max = FindIntByKeys(doc.RootElement, "max", "max_players", "maxplayers", "player_limit");
                if (online < 0)
                {
                    // 部分版本返回 players 数组, 用数组长度作为在线人数
                    int len = FindArrayLength(doc.RootElement, "players");
                    if (len >= 0) online = len;
                }
                if (online < 0) return (false, -1, -1);
                return (true, online, max < 0 ? -1 : max);
            }
            catch
            {
                return (false, -1, -1);
            }
        }

        private static int FindIntByKeys(JsonElement el, params string[] keys)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    string name = prop.Name.ToLowerInvariant().Replace("_", "").Replace("-", "");
                    foreach (var key in keys)
                    {
                        if (name == key.ToLowerInvariant().Replace("_", "").Replace("-", ""))
                        {
                            if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var n)) return n;
                            if (prop.Value.ValueKind == JsonValueKind.Object)
                            {
                                int nested = FindIntByKeys(prop.Value, "online", "count", "current");
                                if (nested >= 0) return nested;
                            }
                        }
                    }
                    int deep = FindIntByKeys(prop.Value, keys);
                    if (deep >= 0) return deep;
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    int deep = FindIntByKeys(item, keys);
                    if (deep >= 0) return deep;
                }
            }
            return -1;
        }

        private static int FindArrayLength(JsonElement el, string name)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) &&
                        prop.Value.ValueKind == JsonValueKind.Array)
                        return prop.Value.GetArrayLength();
                    int deep = FindArrayLength(prop.Value, name);
                    if (deep >= 0) return deep;
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    int deep = FindArrayLength(item, name);
                    if (deep >= 0) return deep;
                }
            }
            return -1;
        }

        // ===== 能力发现(rpc.discover) =====

        /// <summary>发现服务端支持的方法/通知(rpc.discover), 返回 (成功, 消息, 原始JSON)。</summary>
        public (bool Success, string Message, string Raw) Discover(int timeoutMs = 10000)
        {
            try
            {
                var el = CallAsync("rpc.discover", null, timeoutMs).GetAwaiter().GetResult();
                return (true, "OK", el.ValueKind == JsonValueKind.Undefined ? "" : el.GetRawText());
            }
            catch (Exception ex)
            {
                return (false, ex.Message, "");
            }
        }

        // ===== 结果解析helper =====

        /// <summary>容错解析名单结果(数组/对象包裹/单条目均可)。</summary>
        private static List<RmEntry> ExtractEntries(JsonElement el)
        {
            var list = new List<RmEntry>();
            var arr = FindEntryArray(el);
            if (arr.HasValue)
            {
                foreach (var item in arr.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var sub in item.EnumerateArray())
                        {
                            var e = ToEntry(sub);
                            if (!string.IsNullOrEmpty(e.Name)) list.Add(e);
                        }
                    }
                    else
                    {
                        var e = ToEntry(item);
                        if (!string.IsNullOrEmpty(e.Name)) list.Add(e);
                    }
                }
                return list;
            }
            // 无数组: 尝试把根对象当单个条目
            var single = ToEntry(el);
            if (!string.IsNullOrEmpty(single.Name)) list.Add(single);
            return list;
        }

        private static JsonElement? FindEntryArray(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Array) return el;
            if (el.ValueKind != JsonValueKind.Object) return null;
            // 仅当对象本身不像条目时才向下查找数组属性
            if (HasName(el)) return null;
            foreach (var prop in el.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.Array)
                    return prop.Value;
            // 再尝试对象属性(如 { "0": {...} })
            foreach (var prop in el.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var nested = FindEntryArray(prop.Value);
                    if (nested.HasValue) return nested;
                }
            return null;
        }

        private static bool HasName(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Object) return false;
            return el.TryGetProperty("name", out _) || el.TryGetProperty("player", out _)
                || el.TryGetProperty("username", out _) || el.TryGetProperty("ip", out _);
        }

        private static RmEntry ToEntry(JsonElement e)
        {
            var entry = new RmEntry();
            if (e.ValueKind == JsonValueKind.String) { entry.Name = e.GetString() ?? ""; return entry; }
            if (e.ValueKind != JsonValueKind.Object) return entry;

            entry.Name = FirstString(e, "name", "username", "target");
            if (string.IsNullOrEmpty(entry.Name) && e.TryGetProperty("player", out var p))
                entry.Name = p.ValueKind == JsonValueKind.Object ? FirstString(p, "name") : (p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "");
            if (string.IsNullOrEmpty(entry.Name)) entry.Name = FirstString(e, "ip");
            entry.Value = FirstString(e, "reason", "value");
            entry.Extra = FirstString(e, "ip", "uuid", "id", "source", "created", "expires");
            return entry;
        }

        private static string FirstString(JsonElement obj, params string[] keys)
        {
            if (obj.ValueKind != JsonValueKind.Object) return "";
            foreach (var k in keys)
            {
                if (obj.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString() ?? "";
            }
            return "";
        }

        /// <summary>
        /// 容错解析游戏规则。服务端返回 TypedGameRule 数组, 每项形如 { key, type, value }，
        /// 其中 key 为新版命名空间形式(如 minecraft:keep_inventory), value 可能是 bool/int/string。
        /// </summary>
        private static List<RmEntry> ExtractGamerules(JsonElement el)
        {
            var list = new List<RmEntry>();
            // 定位规则数组: 结果本身是数组, 或被对象包裹(如 { "gamerules": [...] })
            JsonElement arr = default;
            bool hasArr = false;
            if (el.ValueKind == JsonValueKind.Array) { arr = el; hasArr = true; }
            else if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                    if (prop.Value.ValueKind == JsonValueKind.Array) { arr = prop.Value; hasArr = true; break; }
            }
            if (hasArr)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string key = FirstString(item, "key", "name");
                    if (string.IsNullOrEmpty(key)) continue;
                    var entry = new RmEntry { Name = key };
                    if (item.TryGetProperty("value", out var v))
                        entry.Value = GameruleValueToString(v);
                    if (item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                        entry.Extra = t.GetString() ?? "";
                    list.Add(entry);
                }
                if (list.Count > 0) return list;
            }
            // 回退: { rule: value } 映射形式
            JsonElement obj = el;
            int guard = 0;
            while (obj.ValueKind == JsonValueKind.Object && guard++ < 5)
            {
                JsonElement? inner = null;
                foreach (var prop in obj.EnumerateObject())
                    if (prop.Value.ValueKind == JsonValueKind.Object) { inner = prop.Value; break; }
                if (inner == null) break;
                obj = inner.Value;
            }
            if (obj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in obj.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object || prop.Value.ValueKind == JsonValueKind.Array) continue;
                    list.Add(new RmEntry { Name = prop.Name, Value = ScalarToString(prop.Value) });
                }
            }
            return list;
        }

        /// <summary>提取游戏规则值(优先取 value 字段, 兼容被对象/数组包裹的类型化值)。</summary>
        private static string GameruleValueToString(JsonElement v)
        {
            if (v.ValueKind == JsonValueKind.Object)
            {
                if (v.TryGetProperty("value", out var inner)) return GameruleValueToString(inner);
                foreach (var p in v.EnumerateObject()) return GameruleValueToString(p.Value);
                return "";
            }
            if (v.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in v.EnumerateArray()) return GameruleValueToString(it);
                return "";
            }
            return ScalarToString(v);
        }
    }
}
