using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RtCli.Modules.Function;
using RtCli.Modules.Unit;

namespace RtCli.Modules.Extension
{
    /// <summary>
    /// REST API 桥接器: 在 RtCli 中提供 HTTP REST API, 使用 gRPC 认证密钥。
    /// 将大部分信息获取接口从 gRPC/面板控制器转移到此处, 简化面板数据获取。
    /// 端口默认为 GrpcPort + 1, OpenAPI 文档位于 /api/openapi.json, Swagger UI 位于 /api/docs
    /// </summary>
    internal class Bridge
    {
        private static HttpListener? _listener;
        private static CancellationTokenSource? _cts;
        private static readonly object _lock = new();

        public static int ApiPort => Config.App.GrpcPort + 1;
        public static bool IsRunning => _listener != null && _listener.IsListening;

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>启动 REST API 服务器</summary>
        public static void Start()
        {
            lock (_lock)
            {
                if (_listener != null) return;
                try
                {
                    _cts = new CancellationTokenSource();
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://+:{ApiPort}/");
                    _listener.Start();
                    _ = Task.Run(() => ListenLoop(_cts.Token));
                    Output.Log($"REST API 服务器已启动端口: {ApiPort} (OpenAPI: /api/openapi.json, 文档: /api/docs)", 1, "Bridge");
                }
                catch (Exception ex)
                {
                    Output.Log($"REST API 启动失败: {ex.Message}", 3, "Bridge");
                    _listener = null;
                }
            }
        }

        /// <summary>停止 REST API 服务器</summary>
        public static void Stop()
        {
            lock (_lock)
            {
                _cts?.Cancel();
                _listener?.Stop();
                _listener = null;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private static async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }
                _ = Task.Run(() => HandleRequestSafe(ctx));
            }
        }

        private static async Task HandleRequestSafe(HttpListenerContext ctx)
        {
            try { await HandleRequest(ctx); }
            catch (Exception ex)
            {
                try { await WriteJson(ctx.Response, new { success = false, message = ex.Message }, 500); }
                catch { }
            }
        }

        // ===== 认证 =====
        private static bool ValidateAuth(HttpListenerRequest req)
        {
            var key = Config.App.GrpcAuthKey;
            if (string.IsNullOrEmpty(key)) return true;
            var auth = req.Headers["Authorization"];
            return auth == key;
        }

        // ===== CORS 与响应 =====
        private static void SetCors(HttpListenerResponse resp)
        {
            resp.Headers["Access-Control-Allow-Origin"] = "*";
            resp.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            resp.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";
        }

        private static async Task WriteJson(HttpListenerResponse resp, object data, int status = 200)
        {
            resp.StatusCode = status;
            resp.ContentType = "application/json; charset=utf-8";
            SetCors(resp);
            var json = JsonSerializer.Serialize(data, _jsonOpts);
            var bytes = Encoding.UTF8.GetBytes(json);
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes);
            resp.OutputStream.Close();
        }

        private static async Task WriteText(HttpListenerResponse resp, string text, string contentType = "text/html; charset=utf-8", int status = 200)
        {
            resp.StatusCode = status;
            resp.ContentType = contentType;
            SetCors(resp);
            var bytes = Encoding.UTF8.GetBytes(text);
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes);
            resp.OutputStream.Close();
        }

        private static async Task<string> ReadBody(HttpListenerRequest req)
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            return await reader.ReadToEndAsync();
        }

        // ===== 主路由 =====
        private static async Task HandleRequest(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var resp = ctx.Response;
            var method = req.HttpMethod;
            var path = req.Url?.AbsolutePath?.TrimEnd('/') ?? "";

            // OPTIONS 预检
            if (method == "OPTIONS") { SetCors(resp); resp.StatusCode = 204; resp.Close(); return; }

            // Swagger UI 和 OpenAPI 文档不需要认证
            if (path == "/api/docs" && method == "GET") { await WriteText(resp, GetSwaggerUiHtml()); return; }
            if (path == "/api/openapi.json" && method == "GET") { await WriteJson(resp, BuildOpenApiSpec()); return; }

            // 所有 /api/* 路径需要认证
            if (path.StartsWith("/api/"))
            {
                if (!ValidateAuth(req))
                {
                    await WriteJson(resp, new { success = false, message = "认证失败: 缺少或错误的 Authorization 头" }, 401);
                    return;
                }
            }
            else
            {
                await WriteJson(resp, new { success = false, message = "未知路径" }, 404);
                return;
            }

            try
            {
                // GET 端点
                if (method == "GET")
                {
                    switch (path)
                    {
                        case "/api/info": await ApiGetInfo(resp); return;
                        case "/api/stats": await ApiGetStats(resp); return;
                        case "/api/status": await ApiGetStatus(resp); return;
                        case "/api/clients": await ApiGetClients(resp); return;
                        case "/api/extensions": await ApiGetExtensions(resp); return;
                        case "/api/commands": await ApiGetCommands(resp); return;
                        case "/api/tasks": await ApiGetTasks(req, resp); return;
                        case "/api/config": await ApiGetConfig(resp); return;
                        case "/api/config-files": await ApiGetConfigFiles(resp); return;
                        case "/api/config-file": await ApiGetConfigFile(req, resp); return;
                        case "/api/config-docs": await ApiGetConfigDocs(req, resp); return;
                        case "/api/panel-data": await ApiGetPanelData(resp); return;
                        case "/api/instances": await ApiGetInstances(resp); return;
                        case "/api/server-files": await ApiGetServerFiles(req, resp); return;
                        case "/api/server-file": await ApiGetServerFile(req, resp); return;
                        case "/api/plugins": await ApiGetPlugins(req, resp); return;
                        case "/api/mods": await ApiGetMods(req, resp); return;
                        case "/api/player-events": await ApiGetPlayerEvents(req, resp); return;
                        case "/api/errors": await ApiGetErrors(req, resp); return;
                    }
                    // /api/instances/{id}
                    if (path.StartsWith("/api/instances/"))
                    {
                        var id = path["/api/instances/".Length..];
                        await ApiGetInstanceDetail(id, resp);
                        return;
                    }
                }
                // POST 端点
                else if (method == "POST")
                {
                    switch (path)
                    {
                        case "/api/execute": await ApiExecute(req, resp); return;
                        case "/api/panel-data": await ApiSavePanelData(req, resp); return;
                        case "/api/jar/toggle": await ApiJarToggle(req, resp); return;
                        case "/api/jar/delete": await ApiJarDelete(req, resp); return;
                        case "/api/player/manage": await ApiPlayerManage(req, resp); return;
                    }
                }

                await WriteJson(resp, new { success = false, message = $"未找到端点: {method} {path}" }, 404);
            }
            catch (Exception ex)
            {
                await WriteJson(resp, new { success = false, message = ex.Message }, 500);
            }
        }

        // ===== 辅助: 查询参数 =====
        private static string Q(HttpListenerRequest req, string key) => req.QueryString[key] ?? "";

        // ===== GET 端点实现 =====

        private static async Task ApiGetInfo(HttpListenerResponse resp)
        {
            await WriteJson(resp, new
            {
                serverName = Config.CurrentServer.ServerName,
                version = Program.RtCliVersion,
                grpcPort = Config.App.GrpcPort,
                apiPort = ApiPort,
                isRunning = Connector.IsRunning,
                currentServer = Config.App.CurrentServer ?? ""
            });
        }

        private static async Task ApiGetStats(HttpListenerResponse resp)
        {
            var (success, message, cpu, memUsed, memTotal, servers) = Backend.GetSystemStats();
            await WriteJson(resp, new
            {
                success, message,
                cpuUsage = cpu,
                memoryUsedMb = memUsed,
                memoryTotalMb = memTotal,
                currentServerKey = Config.App.CurrentServer ?? "",
                aiRunning = Intelligence.AiAutoRunner.IsRunning,
                schedulerRunning = Scheduler.IsRunning,
                servers = servers.Select(s => new
                {
                    serverKey = s.Key,
                    serverName = s.Name,
                    isRunning = s.Running,
                    tps = s.Tps,
                    tpsStatus = s.TpsStatus,
                    mcPid = s.Pid,
                    mcMemoryMb = s.McMemMb,
                    tps1m = s.Tps1m,
                    tps5m = s.Tps5m,
                    tps15m = s.Tps15m,
                    mcCpuUsage = s.McCpuUsage,
                    playerCount = s.PlayerCount,
                    playerMax = s.PlayerMax
                })
            });
        }

        private static async Task ApiGetStatus(HttpListenerResponse resp)
        {
            await WriteJson(resp, new
            {
                success = true,
                managementPortRunning = Connector.IsRunning,
                apiRunning = IsRunning,
                connectedClients = Connector.ConnectedClientCount,
                currentServerKey = Config.App.CurrentServer ?? "",
                currentServerName = Config.CurrentServer?.ServerName ?? "",
                mcMode = Analyzer.CurrentMode ?? "",
                mcRunning = Analyzer.IsRunModeActive,
                mcConnected = Analyzer.IsAttached,
                autoTipsRunning = Intelligence.IsTipsRunning,
                aiRunning = Intelligence.AiAutoRunner.IsRunning,
                schedulerRunning = Scheduler.IsRunning
            });
        }

        private static async Task ApiGetClients(HttpListenerResponse resp)
        {
            var clients = Connector.ConnectedClients.Select(kvp => new
            {
                id = kvp.Key,
                ip = kvp.Value.IP,
                connectTime = kvp.Value.ConnectTime.ToString("yyyy-MM-dd HH:mm:ss")
            });
            await WriteJson(resp, new { success = true, clients });
        }

        private static async Task ApiGetExtensions(HttpListenerResponse resp)
        {
            var json = RtExtensionManager.GetExtensionsJson();
            var extensions = Newtonsoft.Json.JsonConvert.DeserializeObject<List<ExtensionData>>(json);
            var list = extensions?.Select(e => new
            {
                key = e.Key ?? "",
                name = e.Name ?? "",
                version = e.Version ?? "",
                description = e.Description ?? "",
                loadTime = e.LoadTime ?? ""
            })?.Cast<object>().ToList() ?? new List<object>();
            await WriteJson(resp, new { success = true, extensions = list });
        }

        private static async Task ApiGetCommands(HttpListenerResponse resp)
        {
            var commands = Backend.GetAvailableCommands().Select(c => new { command = c.Command, description = c.Description });
            await WriteJson(resp, new { success = true, commands });
        }

        private static async Task ApiGetTasks(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var taskType = Q(req, "type");
            var tasks = new List<object>();
            bool isRunning = false;

            if (string.Equals(taskType, "ai", StringComparison.OrdinalIgnoreCase))
            {
                isRunning = Intelligence.AiAutoRunner.IsRunning;
                var config = ContentManager.Ai.ServerAutoAi;
                if (config?.Tasks != null)
                {
                    foreach (var kv in config.Tasks)
                    {
                        var lastRun = Intelligence.AiAutoRunner.LastRunTime.TryGetValue(kv.Key, out var t) ? t.ToString("MM-dd HH:mm:ss") : "-";
                        int count = Intelligence.AiAutoRunner.RunCount.TryGetValue(kv.Key, out var c) ? c : 0;
                        tasks.Add(new
                        {
                            key = kv.Key, name = kv.Value.Name, enabled = kv.Value.Enabled,
                            trigger = kv.Value.Trigger, interval = kv.Value.Interval,
                            execute = string.Join(", ", kv.Value.Actions),
                            runCount = count, lastRun, status = kv.Value.Enabled ? "启用" : "禁用"
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
                    tasks.Add(new
                    {
                        key = kvp.Key, name = kvp.Key, enabled = kvp.Value.Enabled,
                        trigger = kvp.Value.Trigger, interval = kvp.Value.Condition,
                        execute = kvp.Value.Execute, runCount = count, lastRun = "-", status
                    });
                }
            }

            await WriteJson(resp, new { success = true, isRunning, tasks });
        }

        private static async Task ApiGetConfig(HttpListenerResponse resp)
        {
            var (success, content, message) = Backend.GetConfigContent();
            await WriteJson(resp, new { success, content, message });
        }

        private static async Task ApiGetConfigFiles(HttpListenerResponse resp)
        {
            var files = Backend.ListConfigFiles().Select(f => new { fileName = f.FileName, displayName = f.DisplayName, description = f.Description });
            await WriteJson(resp, new { success = true, files });
        }

        private static async Task ApiGetConfigFile(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var name = Q(req, "name");
            var (success, content, message) = Backend.GetConfigFileContent(name);
            await WriteJson(resp, new { success, content, message });
        }

        private static async Task ApiGetConfigDocs(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var name = Q(req, "name");
            var docs = Repository.GetDocs(name);
            await WriteJson(resp, new { success = true, fileName = name, docs });
        }

        private static async Task ApiGetPanelData(HttpListenerResponse resp)
        {
            await WriteJson(resp, new { success = true, jsonData = PanelDataManager.Load() });
        }

        private static async Task ApiGetInstances(HttpListenerResponse resp)
        {
            var (success, message, instances, groups) = Backend.GetInstanceList();
            await WriteJson(resp, new
            {
                success, message,
                instances = instances.Select(i => new
                {
                    id = i.Id, identifier = i.Identifier, name = i.Name,
                    groupId = i.GroupId, groupName = i.GroupName, isRunning = i.IsRunning,
                    createdAt = i.CreatedAt, hidden = i.Hidden, autoRestart = i.AutoRestart,
                    analyzerMode = i.AnalyzerMode, workPath = i.WorkPath
                }),
                groups = groups.Select(g => new
                {
                    id = g.Id, name = g.Name, memberIds = g.MemberIds, createdAt = g.CreatedAt
                })
            });
        }

        private static async Task ApiGetInstanceDetail(string id, HttpListenerResponse resp)
        {
            var (success, message, instance, backups) = Backend.GetInstanceDetail(id);
            if (instance == null)
            {
                await WriteJson(resp, new { success = false, message });
                return;
            }
            await WriteJson(resp, new
            {
                success, message,
                instance = new
                {
                    id = instance.Id, identifier = instance.Identifier, name = instance.Name,
                    groupId = instance.GroupId, groupName = instance.GroupName,
                    isRunning = instance.IsRunning, analyzerMode = instance.AnalyzerMode,
                    workPath = instance.WorkPath, javaPath = instance.JavaPath,
                    runFlags = instance.RunFlags, hidden = instance.Hidden,
                    autoRestart = instance.AutoRestart,
                    createdAt = instance.CreatedAt, lastStartedAt = instance.LastStartedAt
                },
                backups = backups.Select(b => new { fileName = b.FileName, createdAt = b.CreatedAt, sizeBytes = b.SizeBytes })
            });
        }

        private static async Task ApiGetServerFiles(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var serverKey = Q(req, "id");
            var (success, message, _, serverName, workPath, files) = Backend.ListServerFiles(serverKey);
            await WriteJson(resp, new
            {
                success, message, serverName, workPath,
                files = files.Select(f => new
                {
                    fileName = f.FileName, displayName = f.DisplayName,
                    description = f.Description, category = f.Category,
                    editable = f.Editable, sizeBytes = f.SizeBytes
                })
            });
        }

        private static async Task ApiGetServerFile(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var serverKey = Q(req, "id");
            var fileName = Q(req, "name");
            var (success, message, content, category) = Backend.GetServerFile(serverKey, fileName);
            await WriteJson(resp, new { success, message, content, category });
        }

        private static async Task ApiGetPlugins(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var id = Q(req, "id");
            var (success, message, plugins) = Backend.ListPlugins(id);
            await WriteJson(resp, new
            {
                success, message,
                files = plugins.Select(p => new { fileName = p.FileName, sizeBytes = p.SizeBytes, isDisabled = p.IsDisabled })
            });
        }

        private static async Task ApiGetMods(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var id = Q(req, "id");
            var (success, message, mods) = Backend.ListMods(id);
            await WriteJson(resp, new
            {
                success, message,
                files = mods.Select(m => new { fileName = m.FileName, sizeBytes = m.SizeBytes, isDisabled = m.IsDisabled })
            });
        }

        private static async Task ApiGetPlayerEvents(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var id = Q(req, "id");
            var limit = int.TryParse(Q(req, "limit"), out var l) ? l : 0;
            var events = PlayerEventRecorder.GetEvents(id, limit);
            await WriteJson(resp, new
            {
                success = true, message = "OK",
                events = events.Select(e => new
                {
                    eventType = e.EventType, playerName = e.PlayerName,
                    triggerTime = e.TriggerTime, recordedAt = e.RecordedAt, detail = e.Detail
                })
            });
        }

        private static async Task ApiGetErrors(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var id = Q(req, "id");
            var (success, message, errors) = Backend.AnalyzeInstanceErrors(id);
            await WriteJson(resp, new
            {
                success, message,
                errors = errors.Select(kvp => new { index = kvp.Key, content = kvp.Value })
            });
        }

        // ===== POST 端点实现 =====

        private static async Task ApiExecute(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var body = await ReadBody(req);
            var payload = JsonSerializer.Deserialize<JsonElement>(body);
            var command = payload.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
            var (success, result) = Backend.DispatchCommand(command);
            await WriteJson(resp, new { success, result });
        }

        private static async Task ApiSavePanelData(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var body = await ReadBody(req);
            var payload = JsonSerializer.Deserialize<JsonElement>(body);
            var jsonData = payload.TryGetProperty("jsonData", out var j) ? j.GetString() ?? "{}" : "{}";
            var result = PanelDataManager.Save(jsonData);
            await WriteJson(resp, new { success = result.Success, message = result.Message, jsonData });
        }

        private static async Task ApiJarToggle(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var body = await ReadBody(req);
            var p = JsonSerializer.Deserialize<JsonElement>(body);
            var id = p.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            var fileName = p.TryGetProperty("fileName", out var f) ? f.GetString() ?? "" : "";
            var type = p.TryGetProperty("type", out var t) ? t.GetString() ?? "plugin" : "plugin";
            var result = type == "plugin"
                ? Backend.TogglePlugin(id, fileName)
                : Backend.ToggleMod(id, fileName);
            await WriteJson(resp, new { success = result.Success, message = result.Message });
        }

        private static async Task ApiJarDelete(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var body = await ReadBody(req);
            var p = JsonSerializer.Deserialize<JsonElement>(body);
            var id = p.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            var fileName = p.TryGetProperty("fileName", out var f) ? f.GetString() ?? "" : "";
            var type = p.TryGetProperty("type", out var t) ? t.GetString() ?? "plugin" : "plugin";
            var result = type == "plugin"
                ? Backend.DeletePlugin(id, fileName)
                : Backend.DeleteMod(id, fileName);
            await WriteJson(resp, new { success = result.Success, message = result.Message });
        }

        private static async Task ApiPlayerManage(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var body = await ReadBody(req);
            var p = JsonSerializer.Deserialize<JsonElement>(body);
            var id = p.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            var action = p.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
            var target = p.TryGetProperty("target", out var tg) ? tg.GetString() ?? "" : "";
            var reason = p.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            var command = p.TryGetProperty("command", out var cm) ? cm.GetString() ?? "" : "";

            (bool Success, string Message) result = action switch
            {
                "ban" => Backend.BanPlayer(id, target, reason),
                "pardon" => Backend.PardonPlayer(id, target),
                "ban_ip" => Backend.BanIp(id, target, reason),
                "pardon_ip" => Backend.PardonIp(id, target),
                "kick" => Backend.KickPlayer(id, target, reason),
                "custom" => Backend.SendCustomCommand(id, command),
                _ => (false, $"未知操作: {action}")
            };
            await WriteJson(resp, new { success = result.Success, message = result.Message });
        }

        // ===== OpenAPI 文档生成 =====
        private static object BuildOpenApiSpec()
        {
            var endpoints = new[]
            {
                ("get", "/api/info", "获取服务器信息"),
                ("get", "/api/stats", "获取系统状态(CPU/内存/TPS)"),
                ("get", "/api/status", "获取RtCli运行状态"),
                ("get", "/api/clients", "获取已连接客户端列表"),
                ("get", "/api/extensions", "获取扩展列表"),
                ("get", "/api/commands", "获取可用命令列表"),
                ("get", "/api/tasks", "获取任务列表(AI/计划任务)"),
                ("get", "/api/config", "获取RtCli配置"),
                ("get", "/api/config-files", "获取配置文件列表"),
                ("get", "/api/config-file", "获取配置文件内容"),
                ("get", "/api/config-docs", "获取配置项注释"),
                ("get", "/api/panel-data", "获取面板数据JSON"),
                ("get", "/api/instances", "获取实例列表"),
                ("get", "/api/instances/{id}", "获取实例详情"),
                ("get", "/api/server-files", "获取服务端文件列表"),
                ("get", "/api/server-file", "获取服务端文件内容"),
                ("get", "/api/plugins", "获取插件列表"),
                ("get", "/api/mods", "获取模组列表"),
                ("get", "/api/player-events", "获取玩家事件日志"),
                ("get", "/api/errors", "分析实例错误日志"),
                ("post", "/api/execute", "执行命令"),
                ("post", "/api/panel-data", "保存面板数据"),
                ("post", "/api/jar/toggle", "切换插件/模组启用状态"),
                ("post", "/api/jar/delete", "删除插件/模组文件"),
                ("post", "/api/player/manage", "玩家管理操作")
            };

            var paths = new Dictionary<string, object>();
            foreach (var (m, p, desc) in endpoints)
            {
                var pathKey = p;
                var methodObj = new
                {
                    summary = desc,
                    security = new[] { new Dictionary<string, string[]> { ["ApiKey"] = Array.Empty<string>() } },
                    responses = new Dictionary<string, object>
                    {
                        ["200"] = new { description = "成功" },
                        ["401"] = new { description = "认证失败" }
                    }
                };
                if (!paths.ContainsKey(pathKey))
                    paths[pathKey] = new Dictionary<string, object>();
                ((Dictionary<string, object>)paths[pathKey])[m] = methodObj;
            }

            return new
            {
                openapi = "3.0.1",
                info = new
                {
                    title = "RtCli REST API",
                    version = Program.RtCliVersion ?? "1.0",
                    description = "RutCitrus 管理面板 REST API, 使用 gRPC 认证密钥进行 Authorization 头认证"
                },
                servers = new[] { new { url = $"http://localhost:{ApiPort}" } },
                components = new
                {
                    securitySchemes = new Dictionary<string, object>
                    {
                        ["ApiKey"] = new Dictionary<string, object> { ["type"] = "apiKey", ["in"] = "header", ["name"] = "Authorization" }
                    }
                },
                security = new[] { new Dictionary<string, string[]> { ["ApiKey"] = Array.Empty<string>() } },
                paths
            };
        }

        private static string GetSwaggerUiHtml()
        {
            return @"<!DOCTYPE html>
<html lang=""zh"">
<head>
<meta charset=""utf-8""><title>RtCli API 文档</title>
<link rel=""stylesheet"" href=""https://unpkg.com/swagger-ui-dist@5/swagger-ui.css"">
<style>body{margin:0}</style>
</head>
<body>
<div id=""swagger-ui""></div>
<script src=""https://unpkg.com/swagger-ui-dist@5/swagger-ui-bundle.js""></script>
<script>
window.onload=function(){
SwaggerUIBundle({url:'/api/openapi.json',dom_id:'#swagger-ui',
requestInterceptor:function(req){
req.headers['Authorization']='" + Config.App.GrpcAuthKey + @"';
return req;
}});
};
</script>
</body></html>";
        }
    }
}
