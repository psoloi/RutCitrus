using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Polly;
using Polly.Retry;
using RtCli.Modules.Extension;
using RtCli.Modules.Unit;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.Extensions.AI;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RtCli.Modules.Function
{
    /// <summary>Intelligence 分部实现(自 Intelligence.cs 按职责拆分, 类成员同源)。</summary>
    internal partial class Intelligence
    {
        #region AI自动化管理 - 内置工具集

        /// <summary>
        /// AI内置工具集 - 实现MC服务端自动化管理工具
        /// </summary>
        internal static class AiTools
        {
            private const string ToolsName = "AiTools";

            /// <summary>重启操作的最小间隔(防止故障循环中反复重启)</summary>
            private static readonly TimeSpan RestartMinInterval = TimeSpan.FromMinutes(10);
            private static readonly object _restartLock = new();
            private static DateTime _lastRestartTime = DateTime.MinValue;

            /// <summary>所有内置工具名称列表</summary>
            public static readonly string[] BuiltInTools = new[]
            {
                "read_file",
                "get_server_plugin_list",
                "get_server_log",
                "run_script",
                "modify_file",
                "toggle_plugin",
                "run_command",
                "restart_server"
            };

            private static void Log(string msg, int level = 1)
            {
                Output.Log(msg, level, ToolsName);
            }

            /// <summary>单次工具结果回灌模型的最大字符数</summary>
            private const int MaxToolResultChars = 8000;

            /// <summary>按字符上限截断工具结果, 避免上下文/token 失控</summary>
            private static string ClipToolResult(string? result)
            {
                if (string.IsNullOrEmpty(result))
                    return result ?? "";
                if (result.Length <= MaxToolResultChars)
                    return result;
                return result.Substring(0, MaxToolResultChars) + $"\n…[结果已截断, 原长度 {result.Length} 字符]";
            }

            /// <summary>危险工具二次确认: modify_file/run_command/run_script 需显式 confirm=true</summary>
            private static string? CheckDangerousToolConfirm(string toolName, JObject args)
            {
                if (toolName != "modify_file" && toolName != "run_command" && toolName != "run_script")
                    return null;

                if (args["confirm"]?.ToObject<bool>() == true)
                    return null;

                return $"[拒绝] {toolName} 属于危险操作, 需在参数中提供 \"confirm\": true 二次确认后重试。";
            }

            /// <summary>
            /// 检查工具是否被允许调用
            /// </summary>
            public static bool IsToolAllowed(string toolName, AiToolsConfig toolsConfig, List<string>? taskAllowedTools = null)
            {
                if (toolsConfig == null || !toolsConfig.Enabled) return false;

                // deny 优先
                if (toolsConfig.Deny != null && toolsConfig.Deny.Contains(toolName))
                    return false;

                // 任务级别限制
                if (taskAllowedTools != null && taskAllowedTools.Count > 0)
                {
                    if (!taskAllowedTools.Contains(toolName))
                        return false;
                }

                // allow 为空则允许所有未在 deny 中的
                if (toolsConfig.Allow == null || toolsConfig.Allow.Count == 0)
                    return true;

                return toolsConfig.Allow.Contains(toolName);
            }

            /// <summary>
            /// 执行工具调用
            /// </summary>
            public static async Task<string> ExecuteToolAsync(string toolName, string argsJson)
            {
                string result;
                bool ok = true;
                try
                {
                    JObject args = string.IsNullOrWhiteSpace(argsJson) ? new JObject() : (JObject.Parse(argsJson));

                    // 危险工具二次确认: 未显式 confirm=true 时直接拒绝, 不进入实际执行
                    result = CheckDangerousToolConfirm(toolName, args)
                        ?? (toolName switch
                        {
                            "read_file" => Tool_ReadFile(args),
                            "get_server_plugin_list" => Tool_GetServerPluginList(args),
                            "get_server_log" => Tool_GetServerLog(args),
                            "run_script" => Tool_RunScript(args),
                            "modify_file" => Tool_ModifyFile(args),
                            "toggle_plugin" => Tool_TogglePlugin(args),
                            "run_command" => Tool_RunCommand(args),
                            "restart_server" => await Tool_RestartServerAsync(args),
                            _ => $"[错误] 未知工具: {toolName}"
                        } ?? "[工具执行完毕，无返回内容]");
                }
                catch (Newtonsoft.Json.JsonException je)
                {
                    ok = false;
                    result = $"[参数解析失败] {je.Message}，原始参数: {argsJson}";
                }
                catch (Exception ex)
                {
                    ok = false;
                    result = $"[工具执行异常] {ex.Message}";
                }

                // 工具结果截断: 文件内容/日志行数不可控, 回灌模型前必须限制长度
                result = ClipToolResult(result);

                // 审计: 无论成功失败都留痕, 便于事后复盘 AI 的每一步动作
                AiToolAudit.Record(toolName, argsJson, result, ok);
                return result;
            }

            /// <summary>
            /// 获取工具描述信息(供AI提示词使用)
            /// </summary>
            public static string GetToolsDescription(AiToolsConfig toolsConfig)
            {
                var sb = new StringBuilder();
                sb.AppendLine("可用工具列表(通过函数调用(function calling)方式直接调用，无需任何特殊文本格式):");

                if (toolsConfig?.ToolsPrompt != null)
                {
                    foreach (var kv in toolsConfig.ToolsPrompt)
                    {
                        sb.AppendLine($"- {kv.Key}: {kv.Value}");
                    }
                }
                else
                {
                    foreach (var t in BuiltInTools)
                        sb.AppendLine($"- {t}");
                }

                return sb.ToString();
            }

            /// <summary>
            /// 获取指定工具的JSON Schema(OpenAI函数调用格式)
            /// </summary>
            public static JObject GetToolSchema(string toolName)
            {
                var schema = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject()
                };
                var required = new JArray();

                void AddParam(string name, string type, string desc, bool isRequired = false)
                {
                    ((JObject)schema["properties"]!).Add(name, new JObject
                    {
                        ["type"] = type,
                        ["description"] = desc
                    });
                    if (isRequired) required.Add(name);
                }

                switch (toolName)
                {
                    case "read_file":
                        AddParam("path", "string", "MC服务端目录中的文件相对路径", true);
                        break;
                    case "get_server_plugin_list":
                        break;
                    case "get_server_log":
                        AddParam("lines", "integer", "获取的日志行数(默认100)");
                        break;
                    case "run_script":
                        AddParam("name", "string", "Scripts中已配置的脚本名称", true);
                        AddParam("input", "string", "传递给脚本的输入参数");
                        AddParam("confirm", "boolean", "危险操作二次确认(必须为 true)");
                        break;
                    case "modify_file":
                        AddParam("path", "string", "MC服务端目录中的文件相对路径", true);
                        AddParam("content", "string", "文件的新完整内容", true);
                        AddParam("confirm", "boolean", "危险操作二次确认(必须为 true)");
                        break;
                    case "toggle_plugin":
                        AddParam("plugin", "string", "插件文件名(如 Example.jar)", true);
                        AddParam("disable", "boolean", "true=禁用插件, false=启用插件");
                        break;
                    case "run_command":
                        AddParam("command", "string", "要执行的命令(禁止/op等提权命令)", true);
                        AddParam("confirm", "boolean", "危险操作二次确认(必须为 true)");
                        break;
                    case "restart_server":
                        AddParam("confirm", "boolean", "显式确认重启(true=已确认必须重启; 未提供时需存在崩溃证据)");
                        break;
                }

                if (required.Count > 0)
                    schema["required"] = required;

                return schema;
            }

            /// <summary>
            /// 获取工具的中文描述(优先使用配置中的ToolsPrompt)
            /// </summary>
            public static string GetToolDescription(string toolName, AiToolsConfig? toolsConfig)
            {
                if (toolsConfig?.ToolsPrompt != null &&
                    toolsConfig.ToolsPrompt.TryGetValue(toolName, out var desc) &&
                    !string.IsNullOrWhiteSpace(desc))
                    return desc;

                return toolName switch
                {
                    "read_file" => "读取MC服务端目录中文件内容",
                    "get_server_plugin_list" => "获取MC服务端插件列表",
                    "get_server_log" => "获取MC服务端日志",
                    "run_script" => "运行Scripts中已配置的脚本",
                    "modify_file" => "修改MC服务端目录中文件内容",
                    "toggle_plugin" => "启用/禁用插件(jar<->disjar)",
                    "run_command" => "运行程序命令或向服务器发送命令(禁止/op)",
                    "restart_server" => "重启MC服务端",
                    _ => toolName
                };
            }

            /// <summary>
            /// 构建AI函数调用工具列表(基于Microsoft.Extensions.AI的AIFunction)
            /// </summary>
            public static List<AIFunction> BuildAiFunctions(AiToolsConfig? toolsConfig, List<string>? taskAllowedTools = null)
            {
                var functions = new List<AIFunction>();
                foreach (var toolName in BuiltInTools)
                {
                    if (!IsToolAllowed(toolName, toolsConfig, taskAllowedTools))
                        continue;

                    string description = GetToolDescription(toolName, toolsConfig);
                    JObject schema = GetToolSchema(toolName);
                    functions.Add(new RtAiToolFunction(toolName, description, schema));
                }
                return functions;
            }

            #region 工具实现

            /// <summary>读取MC服务端目录中文件内容</summary>
            private static string Tool_ReadFile(JObject args)
            {
                string? relPath = args["path"]?.ToString();
                if (string.IsNullOrWhiteSpace(relPath))
                    return "[错误] 缺少参数: path";

                string workPath = GetServerWorkPath();
                if (string.IsNullOrEmpty(workPath))
                    return "[错误] 未配置MC服务端工作目录";

                // 防止路径穿越: 解析真实路径(含符号链接/联接点)并校验目录边界
                string fullPath = Path.GetFullPath(Path.Combine(workPath, relPath));
                if (!IsPathWithinRoot(workPath, fullPath, out string resolvedPath))
                {
                    Log($"拒绝读取工作目录外的文件: {relPath}", 2);
                    return "[错误] 禁止访问工作目录外的文件";
                }
                fullPath = resolvedPath;

                if (!File.Exists(fullPath))
                    return $"[错误] 文件不存在: {relPath}";

                var info = new FileInfo(fullPath);
                // 限制读取大小(避免超大文件)
                long maxSize = 512 * 1024; // 512KB
                if (info.Length > maxSize)
                    return $"[错误] 文件过大({info.Length} 字节)，最大支持 {maxSize} 字节";

                try
                {
                    string content = File.ReadAllText(fullPath, Encoding.UTF8);
                    Log($"读取文件: {relPath} ({content.Length} 字符)", 1);
                    return content;
                }
                catch (Exception ex)
                {
                    return $"[读取失败] {ex.Message}";
                }
            }

            /// <summary>获取MC服务端插件列表</summary>
            private static string Tool_GetServerPluginList(JObject args)
            {
                string workPath = GetServerWorkPath();
                if (string.IsNullOrEmpty(workPath))
                    return "[错误] 未配置MC服务端工作目录";

                string pluginsDir = Path.Combine(workPath, "plugins");
                if (!Directory.Exists(pluginsDir))
                    return "[信息] plugins 目录不存在";

                var result = new JArray();
                try
                {
                    var files = new DirectoryInfo(pluginsDir)
                        .GetFiles("*.jar")
                        .Concat(new DirectoryInfo(pluginsDir).GetFiles("*.disjar"))
                        .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    foreach (var file in files)
                    {
                        bool isDisabled = file.Extension.Equals(".disjar", StringComparison.OrdinalIgnoreCase);
                        result.Add(new JObject
                        {
                            ["file"] = file.Name,
                            ["enabled"] = !isDisabled,
                            ["size"] = file.Length
                        });
                    }

                    Log($"获取插件列表: 共 {result.Count} 个插件", 1);
                    return result.ToString(Formatting.Indented);
                }
                catch (Exception ex)
                {
                    return $"[获取失败] {ex.Message}";
                }
            }

            /// <summary>获取MC服务端日志</summary>
            private static string Tool_GetServerLog(JObject args)
            {
                int lines = args["lines"]?.ToObject<int>() ?? 100;
                if (lines <= 0) lines = 100;
                if (lines > 2000) lines = 2000;

                List<string> logLines = new List<string>();

                // 优先使用内存缓冲区
                lock (Analyzer._logBufferLock)
                {
                    if (Analyzer._logBuffer.Count > 0)
                    {
                        int start = Math.Max(0, Analyzer._logBuffer.Count - lines);
                        for (int i = start; i < Analyzer._logBuffer.Count; i++)
                            logLines.Add(Analyzer._logBuffer[i]);
                    }
                }

                // 缓冲区为空则尝试从日志文件读取
                if (logLines.Count == 0)
                {
                    string workPath = GetServerWorkPath();
                    if (!string.IsNullOrEmpty(workPath))
                    {
                        string logFile = Path.Combine(workPath, "logs", "latest.log");
                        if (File.Exists(logFile))
                        {
                            try
                            {
                                var allLines = File.ReadAllLines(logFile, Encoding.UTF8);
                                int start = Math.Max(0, allLines.Length - lines);
                                for (int i = start; i < allLines.Length; i++)
                                    logLines.Add(allLines[i]);
                            }
                            catch (Exception ex)
                            {
                                return $"[读取日志文件失败] {ex.Message}";
                            }
                        }
                    }
                }

                if (logLines.Count == 0)
                    return "[信息] 暂无日志数据(服务端可能未启动或未连接)";

                Log($"获取日志: 最近 {logLines.Count} 行", 1);
                return string.Join("\n", logLines);
            }

            /// <summary>运行Scripts中已配置的脚本</summary>
            private static string Tool_RunScript(JObject args)
            {
                string? scriptName = args["name"]?.ToString();
                string? input = args["input"]?.ToString();

                if (string.IsNullOrWhiteSpace(scriptName))
                    return "[错误] 缺少参数: name";

                try
                {
                    var settings = Scripts.GetSettings();
                    if (settings.Scripts.TryGetValue(scriptName, out var item))
                    {
                        if (!string.IsNullOrWhiteSpace(input))
                            item.Input = input;

                        Scripts.ExecuteScript(scriptName, item);
                        Log($"运行脚本: {scriptName}", 1);
                        return $"[成功] 脚本 {scriptName} 已触发执行";
                    }
                    return $"[错误] 未找到脚本: {scriptName}";
                }
                catch (Exception ex)
                {
                    return $"[脚本执行失败] {ex.Message}";
                }
            }


            /// <summary>受保护的核心配置/权限文件(一律拒绝修改)</summary>
            private static readonly HashSet<string> ProtectedFiles = new(StringComparer.OrdinalIgnoreCase)
            {
                "server.properties", "bukkit.yml", "spigot.yml", "paper.yml", "eula.txt",
                "ops.json", "whitelist.json", "banned-players.json", "banned-ips.json",
                "usercache.json", "permissions.yml"
            };

            /// <summary>不允许以文本方式改写的扩展名</summary>
            private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
            {
                ".jar", ".dat", ".mca", ".mcr", ".zip", ".gz", ".lock", ".db"
            };

            /// <summary>
            /// 校验 modify_file 的目标: 目录穿越 / 体积 / 受保护文件 / 受限扩展名 / 存档目录。
            /// 返回 null 表示允许, 否则返回给模型的拒绝原因。
            /// </summary>
            internal static string? ValidateModifyPath(string? workPath, string? relPath, int contentLength, out string fullPath)
            {
                fullPath = "";
                if (string.IsNullOrWhiteSpace(workPath)) return "[错误] 未配置MC服务端工作目录";
                if (string.IsNullOrWhiteSpace(relPath)) return "[错误] 缺少参数: path";

                const int maxContentChars = 1024 * 1024;
                if (contentLength > maxContentChars)
                    return $"[错误] 内容过大({contentLength} 字符), 上限 {maxContentChars}";

                fullPath = Path.GetFullPath(Path.Combine(workPath, relPath));
                // 目录边界比较补分隔符, 并解析真实路径(符号链接/联接点)防止越权
                if (!IsPathWithinRoot(workPath, fullPath, out string resolvedPath))
                {
                    fullPath = "";
                    return "[错误] 禁止修改工作目录外的文件";
                }
                fullPath = resolvedPath;

                string fileName = Path.GetFileName(fullPath).ToLowerInvariant();
                if (ProtectedFiles.Contains(fileName))
                {
                    fullPath = "";
                    return $"[错误] 受保护的核心文件 {fileName} 禁止修改";
                }

                string ext = Path.GetExtension(fullPath).ToLowerInvariant();
                if (BlockedExtensions.Contains(ext))
                {
                    fullPath = "";
                    return $"[错误] 不允许修改该类型的文件: {ext}";
                }

                // 按路径段判断: 世界存档 / 依赖库 / 任意层级的缓存目录(含隐藏的 .cache)
                var segments = relPath.Replace('\\', '/')
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.ToLowerInvariant())
                    .ToArray();
                if (segments.Length > 0 && (segments[0] == "world" || segments[0] == "libraries"))
                {
                    fullPath = "";
                    return "[错误] 不允许修改世界存档/依赖库目录下的文件";
                }
                if (segments.Any(s => s == "cache" || s == ".cache"))
                {
                    fullPath = "";
                    return "[错误] 不允许修改缓存目录下的文件";
                }

                return null;
            }

            /// <summary>修改MC服务端目录中文件内容</summary>
            private static string Tool_ModifyFile(JObject args)
            {
                string? relPath = args["path"]?.ToString();
                string? content = args["content"]?.ToString();

                if (string.IsNullOrWhiteSpace(relPath))
                    return "[错误] 缺少参数: path";
                if (content == null)
                    return "[错误] 缺少参数: content";

                // 校验目标路径与内容体积(纯函数, 便于单测)
                string? pathError = ValidateModifyPath(GetServerWorkPath(), relPath, content.Length, out string fullPath);
                if (pathError != null)
                {
                    Log($"拒绝修改文件: {relPath} ({pathError})", 2);
                    return pathError;
                }

                try
                {
                    string? dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    // 覆盖前留一份 .rtbak 备份, 便于人工回滚
                    if (File.Exists(fullPath))
                    {
                        try { File.Copy(fullPath, fullPath + ".rtbak", overwrite: true); }
                        catch (Exception bex) { Log($"备份失败(继续写入): {bex.Message}", 2); }
                    }

                    File.WriteAllText(fullPath, content, Encoding.UTF8);
                    Log($"修改文件: {relPath} ({content.Length} 字符)", 1);
                    return $"[成功] 已修改文件: {relPath} (原文件已备份为 {Path.GetFileName(fullPath)}.rtbak)";
                }
                catch (Exception ex)
                {
                    return $"[修改失败] {ex.Message}";
                }
            }

            /// <summary>启用或禁用插件(jar&lt;-&gt;disjar)</summary>
            private static string Tool_TogglePlugin(JObject args)
            {
                string? pluginName = args["plugin"]?.ToString();
                bool disable = args["disable"]?.ToObject<bool>() ?? true;

                if (string.IsNullOrWhiteSpace(pluginName))
                    return "[错误] 缺少参数: plugin";

                string workPath = GetServerWorkPath();
                if (string.IsNullOrEmpty(workPath))
                    return "[错误] 未配置MC服务端工作目录";

                string pluginsDir = Path.Combine(workPath, "plugins");
                if (!Directory.Exists(pluginsDir))
                    return "[错误] plugins 目录不存在";

                // 插件名只能是 plugins 目录下的单个文件名, 禁止路径分隔符/.. 与非法字符
                string rawName = pluginName.Trim();
                if (rawName == "." || rawName == ".."
                    || rawName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || rawName.Contains('/') || rawName.Contains('\\')
                    || Path.GetFileName(rawName) != rawName)
                {
                    Log($"拒绝插件名(疑似路径穿越): {pluginName}", 2);
                    return "[错误] 插件名必须是 plugins 目录下的文件名, 不能包含路径";
                }

                // 规范化文件名
                string fileName = rawName;
                if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                    !fileName.EndsWith(".disjar", StringComparison.OrdinalIgnoreCase))
                {
                    fileName += ".jar";
                }

                string sourcePath = Path.Combine(pluginsDir, fileName);
                string targetName;
                string targetPath;

                if (disable)
                {
                    // 将 .jar 改为 .disjar
                    if (fileName.EndsWith(".disjar", StringComparison.OrdinalIgnoreCase))
                        return $"[信息] 插件 {fileName} 已是禁用状态";

                    targetName = Path.GetFileNameWithoutExtension(fileName) + ".disjar";
                    targetPath = Path.Combine(pluginsDir, targetName);

                    if (!File.Exists(sourcePath))
                        return $"[错误] 插件文件不存在: {fileName}";

                    try
                    {
                        File.Move(sourcePath, targetPath);
                        Log($"禁用插件: {fileName} -> {targetName}", 1);
                        return $"[成功] 已禁用插件: {targetName}";
                    }
                    catch (Exception ex)
                    {
                        return $"[禁用失败] {ex.Message}";
                    }
                }
                else
                {
                    // 将 .disjar 改为 .jar
                    if (fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                        return $"[信息] 插件 {fileName} 已是启用状态";

                    // 确保 .disjar 后缀
                    if (!fileName.EndsWith(".disjar", StringComparison.OrdinalIgnoreCase))
                        fileName += ".disjar";

                    sourcePath = Path.Combine(pluginsDir, fileName);
                    targetName = Path.GetFileNameWithoutExtension(fileName) + ".jar";
                    targetPath = Path.Combine(pluginsDir, targetName);

                    if (!File.Exists(sourcePath))
                        return $"[错误] 插件文件不存在: {fileName}";

                    try
                    {
                        File.Move(sourcePath, targetPath);
                        Log($"启用插件: {fileName} -> {targetName}", 1);
                        return $"[成功] 已启用插件: {targetName} (需重启服务端生效)";
                    }
                    catch (Exception ex)
                    {
                        return $"[启用失败] {ex.Message}";
                    }
                }
            }

            /// <summary>运行程序命令或向MC服务器发送命令(禁止/op)</summary>
            private static string Tool_RunCommand(JObject args)
            {
                string? command = args["command"]?.ToString();
                if (string.IsNullOrWhiteSpace(command))
                    return "[错误] 缺少参数: command";

                // 安全检查: 禁止 /op 等提权命令(含 execute 链与命名空间写法)
                string trimmedCmd = command.Trim();
                if (IsForbiddenCommand(trimmedCmd))
                {
                    Log($"拒绝执行禁止的MC命令: {trimmedCmd}", 2);
                    return $"[错误] 禁止执行的命令: {trimmedCmd}";
                }

                // 判断是发送到MC服务器还是运行系统命令
                // 以 / 开头或匹配MC命令特征则发送到服务器
                bool isMcCommand = trimmedCmd.StartsWith("/") || IsMcServerCommand(trimmedCmd);

                if (isMcCommand)
                {
                    string mcCmd = trimmedCmd.TrimStart('/');
                    try
                    {
                        Analyzer.SendCommand(mcCmd);
                        Log($"向服务器发送命令: {mcCmd}", 1);
                        return $"[成功] 已发送命令到服务器: {mcCmd}";
                    }
                    catch (Exception ex)
                    {
                        return $"[发送命令失败] {ex.Message}";
                    }
                }
                // 非MC命令: 默认拒绝(fail-closed)。
                // 只有显式开启 allow_system_commands 且命中白名单前缀时才允许执行,
                // 避免模型在日志里看到可疑内容后执行 del/format/taskkill 之类的破坏性系统命令。
                var toolsConfig = ContentManager.Ai.ServerAutoAi?.Tools;
                if (toolsConfig == null || !toolsConfig.AllowSystemCommands)
                {
                    Log($"拒绝执行系统命令(未开启 allow_system_commands): {trimmedCmd}", 2);
                    return "[拒绝] 出于安全考虑, 系统命令执行默认关闭(仅允许发送MC服务器命令)。"
                         + "如确需诊断, 请先在 ai_settings.yml 的 tools 中设置 allow_system_commands: true 并配置 system_command_allowlist。";
                }

                // 先做参数拆分与元字符校验, 再匹配白名单; 执行时不再经过 shell
                if (!TryTokenizeCommand(trimmedCmd, out var argv, out string? tokenError))
                {
                    Log($"拒绝执行系统命令: {trimmedCmd} ({tokenError})", 2);
                    return tokenError!;
                }

                string normalizedCmd = string.Join(" ", argv);
                if (!IsSystemCommandAllowed(normalizedCmd, toolsConfig.SystemCommandAllowlist))
                {
                    Log($"拒绝执行白名单外的系统命令: {trimmedCmd}", 2);
                    return "[拒绝] 该命令不在 system_command_allowlist 白名单内, 已阻止执行。";
                }

                try
                {
                    // 直接以参数数组启动, 不经过 cmd.exe /bin/sh, 从根源上避免命令链与重定向注入
                    var psi = new ProcessStartInfo
                    {
                        FileName = argv[0],
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    for (int i = 1; i < argv.Count; i++)
                        psi.ArgumentList.Add(argv[i]);

                    using var proc = Process.Start(psi);
                    if (proc == null)
                        return "[错误] 无法启动进程";

                    if (!proc.WaitForExit(30000))
                    {
                        try { proc.Kill(); } catch { }
                        return "[错误] 命令执行超时(30秒)";
                    }

                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();

                    var sb = new StringBuilder();
                    if (!string.IsNullOrWhiteSpace(stdout))
                        sb.Append("STDOUT:\n").Append(stdout).Append("\n");
                    if (!string.IsNullOrWhiteSpace(stderr))
                        sb.Append("STDERR:\n").Append(stderr);

                    Log($"运行系统命令: {trimmedCmd} (退出码: {proc.ExitCode})", 1);
                    return sb.Length > 0 ? sb.ToString() : $"[成功] 命令执行完毕 (退出码: {proc.ExitCode})";
                }
                catch (Exception ex)
                {
                    return $"[系统命令执行失败] {ex.Message}";
                }
            }

            /// <summary>重启MC服务端</summary>
            private static async Task<string> Tool_RestartServerAsync(JObject args)
            {
                try
                {
                    if (!Analyzer.IsRunModeActive)
                        return "[错误] MC服务端未运行(仅支持Run/RR/RM模式的重启)";

                    bool explicitConfirm = args["confirm"]?.ToObject<bool>() ?? false;
                    bool crashEvidence = HasRecentCrashEvidence(TimeSpan.FromMinutes(15));

                    // 硬校验: 必须"有崩溃证据"或"模型显式确认", 二者缺一即拒绝。
                    // 提示词里的"确认崩溃后再重启"只是软约束, 这里才是真正的闸门。
                    if (!explicitConfirm && !crashEvidence)
                    {
                        Log("拒绝重启: 未检测到崩溃证据且未显式确认", 2);
                        return "[拒绝] 未检测到崩溃证据(最近15分钟无崩溃信号与崩溃报告), 已阻止重启。"
                             + "如确认服务端必须重启, 请先说明原因, 然后用 {\"confirm\": true} 再次调用。";
                    }

                    // 频率限制: 防止模型在故障循环里反复重启把服务器打摆子
                    lock (_restartLock)
                    {
                        if (DateTime.Now - _lastRestartTime < RestartMinInterval)
                            return $"[拒绝] 距上次重启不足 {RestartMinInterval.TotalMinutes:F0} 分钟"
                                 + $"({_lastRestartTime:HH:mm:ss}), 为防止反复重启已阻止。";
                        _lastRestartTime = DateTime.Now;
                    }

                    Log($"AI 触发重启服务器(证据: {(crashEvidence ? "崩溃信号" : "无")}, 显式确认: {explicitConfirm})...", 2);

                    Analyzer.StopServer();
                    await Task.Delay(2000);
                    Analyzer.StartServer();

                    return "[成功] 已触发服务器重启";
                }
                catch (Exception ex)
                {
                    return $"[重启失败] {ex.Message}";
                }
            }

            /// <summary>最近窗口内是否存在崩溃证据(内存崩溃信号 / 日志崩溃关键字 / crash-reports 新文件)</summary>
            private static bool HasRecentCrashEvidence(TimeSpan window)
            {
                try
                {
                    if (ServerRuntimeSnapshot.LastCrashTime.HasValue &&
                        DateTime.Now - ServerRuntimeSnapshot.LastCrashTime.Value <= window)
                        return true;

                    lock (Analyzer._logBufferLock)
                    {
                        int start = Math.Max(0, Analyzer._logBuffer.Count - 200);
                        for (int i = start; i < Analyzer._logBuffer.Count; i++)
                        {
                            string line = Analyzer._logBuffer[i];
                            // 只认窗口内的崩溃行, 避免缓冲区里旧崩溃日志反复触发"有崩溃证据"
                            if (!IsLineWithinWindow(line, window)) continue;
                            if (MCConsole.IsCrashLine(line)) return true;
                        }
                    }

                    string? workPath = GetServerWorkPath();
                    if (!string.IsNullOrEmpty(workPath))
                    {
                        string crashDir = Path.Combine(workPath, "crash-reports");
                        if (Directory.Exists(crashDir))
                        {
                            var newest = new DirectoryInfo(crashDir).GetFiles("*.txt")
                                .OrderByDescending(f => f.LastWriteTime)
                                .FirstOrDefault();
                            if (newest != null && DateTime.Now - newest.LastWriteTime <= window) return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"崩溃证据检测失败: {ex.Message}", 2);
                }
                return false;
            }

            /// <summary>日志行首时间戳(兼容 [HH:mm:ss]、[HH:mm:ss INFO]、毫秒)</summary>
            private static readonly Regex LogTimeRegex = new Regex(
                @"^\[(\d{1,2}):(\d{2}):(\d{2})(?:[.,]\d{1,3})?",
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(200));

            /// <summary>判断日志行是否落在时间窗口内(无时间戳的行不视为近期证据)</summary>
            private static bool IsLineWithinWindow(string line, TimeSpan window)
            {
                if (string.IsNullOrEmpty(line)) return false;

                var m = LogTimeRegex.Match(line);
                if (!m.Success) return false;

                if (!int.TryParse(m.Groups[1].Value, out int h) ||
                    !int.TryParse(m.Groups[2].Value, out int mi) ||
                    !int.TryParse(m.Groups[3].Value, out int s))
                    return false;

                var now = DateTime.Now;
                var time = now.Date.AddHours(h).AddMinutes(mi).AddSeconds(s);

                // 跨天: 解析出的时间明显晚于当前时刻, 说明是昨天的日志
                if (time - now > TimeSpan.FromHours(1))
                    time = time.AddDays(-1);

                return now - time <= window;
            }

            /// <summary>系统命令白名单判定(前缀匹配; 白名单为空时全禁)</summary>
            internal static bool IsSystemCommandAllowed(string command, List<string>? allowlist)
            {
                if (allowlist == null || allowlist.Count == 0) return false;
                string cmd = command.Trim().ToLowerInvariant();
                foreach (var raw in allowlist)
                {
                    string prefix = (raw ?? "").Trim().ToLowerInvariant();
                    if (prefix.Length == 0) continue;
                    if (cmd == prefix || cmd.StartsWith(prefix + " ", StringComparison.Ordinal)) return true;
                }
                return false;
            }

            #endregion

            #region 辅助方法

            /// <summary>
            /// 安全路径校验: 解析真实路径(含符号链接/联接点)后判断是否位于根目录内。
            /// 边界比较会补目录分隔符, 避免 E:\mc 放行 E:\mc-backup;
            /// 目标可尚不存在(写入新文件), 此时解析最近存在的祖先目录再拼回剩余路径。
            /// </summary>
            internal static bool IsPathWithinRoot(string rootPath, string candidatePath, out string resolvedPath)
            {
                resolvedPath = "";
                try
                {
                    string realRoot = ResolveRealPath(rootPath);
                    string realCandidate = ResolveRealPath(candidatePath);

                    string rootWithSep = realRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                       + Path.DirectorySeparatorChar;

                    // Windows 文件系统大小写不敏感, Linux/macOS 需大小写敏感比较
                    var comparison = OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal;

                    if (!realCandidate.Equals(realRoot, comparison)
                        && !realCandidate.StartsWith(rootWithSep, comparison))
                        return false;

                    resolvedPath = realCandidate;
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            /// <summary>解析路径的真实位置: 找到最近的已存在祖先解析链接目标, 再拼回不存在的尾部路径</summary>
            private static string ResolveRealPath(string path)
            {
                string full = Path.GetFullPath(path);
                string? parent = Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(parent)) return full;

                var tail = new Stack<string>();
                tail.Push(Path.GetFileName(full));

                string current = parent;
                while (!string.IsNullOrEmpty(current)
                       && !Directory.Exists(current) && !File.Exists(current))
                {
                    tail.Push(Path.GetFileName(current));
                    current = Path.GetDirectoryName(current) ?? "";
                }
                if (string.IsNullOrEmpty(current)) return full;

                string resolved = current;
                try
                {
                    FileSystemInfo info = Directory.Exists(current)
                        ? new DirectoryInfo(current)
                        : new FileInfo(current);
                    resolved = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName;
                }
                catch
                {
                    resolved = current;
                }

                while (tail.Count > 0)
                    resolved = Path.Combine(resolved, tail.Pop());
                return Path.GetFullPath(resolved);
            }

            /// <summary>危险 shell 元字符: 参数数组执行前必须拒绝, 防止命令链/重定向/变量展开注入</summary>
            private static readonly char[] ShellMetaChars = { '&', '|', ';', '<', '>', '`', '\n', '\r', '%', '$', '(', ')', '{', '}', '[', ']' };

            /// <summary>
            /// 将命令行拆分为参数数组(支持双/单引号包裹)。含 shell 元字符或引号不闭合时拒绝,
            /// 执行阶段不再经过 shell, 因此不存在二次解析与管道/重定向注入。
            /// </summary>
            internal static bool TryTokenizeCommand(string command, out List<string> tokens, out string? error)
            {
                tokens = new List<string>();
                error = null;

                if (string.IsNullOrWhiteSpace(command))
                {
                    error = "[拒绝] 命令为空";
                    return false;
                }
                if (command.IndexOfAny(ShellMetaChars) >= 0)
                {
                    error = "[拒绝] 命令包含 shell 元字符(& | ; < > ` $ % () [] {})，已阻止执行";
                    return false;
                }

                var current = new StringBuilder();
                char quote = '\0';
                bool tokenStarted = false;

                for (int i = 0; i < command.Length; i++)
                {
                    char c = command[i];
                    if (quote != '\0')
                    {
                        if (c == quote) { quote = '\0'; continue; }
                        if (c == '\\' && i + 1 < command.Length) { current.Append(command[++i]); continue; }
                        current.Append(c);
                        continue;
                    }
                    if (c == '"' || c == '\'')
                    {
                        quote = c;
                        tokenStarted = true;
                        continue;
                    }
                    if (char.IsWhiteSpace(c))
                    {
                        if (tokenStarted || current.Length > 0)
                        {
                            tokens.Add(current.ToString());
                            current.Clear();
                            tokenStarted = false;
                        }
                        continue;
                    }
                    current.Append(c);
                    tokenStarted = true;
                }

                if (quote != '\0')
                {
                    error = "[拒绝] 命令包含未闭合的引号";
                    return false;
                }
                if (tokenStarted || current.Length > 0)
                    tokens.Add(current.ToString());

                if (tokens.Count == 0 || string.IsNullOrWhiteSpace(tokens[0]))
                {
                    error = "[拒绝] 无法解析出可执行程序";
                    return false;
                }
                return true;
            }

            private static string GetServerWorkPath()
            {
                // 绑定 AI 任务目标实例(任务外回退当前实例), 避免多实例/执行中切换实例时误操作
                return GetAiTargetWorkPath();
            }

            /// <summary>
            /// 判断是否为禁止的 MC 命令(如 /op)。
            /// 会规范化 minecraft: 命名空间并解析 execute 链, 防止 /execute as @a run op @s 绕过。
            /// </summary>
            private static bool IsForbiddenCommand(string command)
            {
                string normalized = command.Trim().TrimStart('/').Trim();
                if (normalized.Length == 0) return false;

                var tokens = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) return false;

                // execute 链: 逐层寻找 run 之后真正执行的命令(支持嵌套 execute)
                if (NormalizeMcToken(tokens[0]).Equals("execute", StringComparison.OrdinalIgnoreCase))
                {
                    for (int i = 1; i < tokens.Length; i++)
                    {
                        if (!NormalizeMcToken(tokens[i]).Equals("run", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (i + 1 >= tokens.Length) break;
                        if (NormalizeMcToken(tokens[i + 1]).Equals("execute", StringComparison.OrdinalIgnoreCase))
                            continue; // 嵌套 execute, 继续向后找 run
                        return IsForbiddenVerb(tokens, i + 1);
                    }
                    return false;
                }

                return IsForbiddenVerb(tokens, 0);
            }

            /// <summary>去掉 minecraft: 命名空间前缀, 便于统一比较</summary>
            private static string NormalizeMcToken(string token)
                => token.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase) ? token[10..] : token;

            /// <summary>判断指定位置的命令动词是否属于禁止操作(op/deop/ban/pardon/whitelist add|remove)</summary>
            private static bool IsForbiddenVerb(string[] tokens, int start)
            {
                if (start < 0 || start >= tokens.Length) return false;

                string verb = NormalizeMcToken(tokens[start]).ToLowerInvariant();
                switch (verb)
                {
                    case "op":
                    case "deop":
                    case "ban":
                    case "pardon":
                        return true;
                    case "whitelist":
                        string sub = start + 1 < tokens.Length
                            ? NormalizeMcToken(tokens[start + 1]).ToLowerInvariant()
                            : "";
                        return sub == "add" || sub == "remove";
                    default:
                        return false;
                }
            }

            /// <summary>判断是否为MC服务器命令(而非系统命令)</summary>
            private static bool IsMcServerCommand(string command)
            {
                string lower = command.ToLowerInvariant().TrimStart('/');
                string[] mcCommands = {
                    "say", "list", "stop", "save-all", "save-on", "save-off", "save-flush",
                    "whitelist", "ban", "banlist", "pardon", "kick", "tp", "teleport",
                    "gamemode", "gamerule", "give", "clear", "effect", "enchant",
                    "setblock", "fill", "clone", "execute", "function", "particle",
                    "playsound", "title", "tellraw", "bossbar", "scoreboard", "tag",
                    "team", "advancement", "recipe", "xp", "experience", "spawnpoint",
                    "setworldspawn", "weather", "time", "difficulty", "defaultgamemode",
                    "seed", "reload", "perms", "permission", "plugins", "version", "tps",
                    "gc", "restart", "timings"
                };

                foreach (var mc in mcCommands)
                {
                    if (lower == mc || lower.StartsWith(mc + " "))
                        return true;
                }

                return false;
            }

            // 主机状态缓存: WMI 查询开销大, 面板高频轮询时复用短期结果(2 秒)
            private static readonly TimeSpan HostStatsTtl = TimeSpan.FromSeconds(2);
            private static readonly object _hostStatsLock = new();
            private static double _cachedHostCpu = -1;
            private static DateTime _hostCpuAt = DateTime.MinValue;
            private static (double usedMb, double totalMb) _cachedHostMem = (-1, -1);
            private static DateTime _hostMemAt = DateTime.MinValue;

            /// <summary>获取主机CPU使用率(%)</summary>
            public static double GetCpuUsage()
            {
                lock (_hostStatsLock)
                {
                    if (DateTime.UtcNow - _hostCpuAt < HostStatsTtl)
                        return _cachedHostCpu;
                }

                double value = -1;
                try
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        using var searcher = new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_Processor");
                        foreach (var obj in searcher.Get())
                        {
                            value = Convert.ToDouble(obj["LoadPercentage"]);
                            break;
                        }
                    }
                }
                catch { }

                lock (_hostStatsLock)
                {
                    _cachedHostCpu = value;
                    _hostCpuAt = DateTime.UtcNow;
                }
                return value;
            }

            /// <summary>获取主机内存使用情况(已用MB, 总MB)</summary>
            public static (double usedMb, double totalMb) GetMemoryUsage()
            {
                lock (_hostStatsLock)
                {
                    if (DateTime.UtcNow - _hostMemAt < HostStatsTtl)
                        return _cachedHostMem;
                }

                var value = (-1.0, -1.0);
                try
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
                        foreach (var obj in searcher.Get())
                        {
                            double totalKb = Convert.ToDouble(obj["TotalVisibleMemorySize"]);
                            double freeKb = Convert.ToDouble(obj["FreePhysicalMemory"]);
                            double usedKb = totalKb - freeKb;
                            value = (usedKb / 1024.0, totalKb / 1024.0);
                            break;
                        }
                    }
                }
                catch { }

                lock (_hostStatsLock)
                {
                    _cachedHostMem = value;
                    _hostMemAt = DateTime.UtcNow;
                }
                return value;
            }

            /// <summary>获取MC服务器TPS(通过发送 /tps 命令并解析, 此处返回占位说明)</summary>
            public static string GetServerTpsInfo()
            {
                try
                {
                    if (!Analyzer.IsRunModeActive && !Analyzer.IsAttached)
                        return "[服务器未运行，无法获取TPS]";

                    // 尝试从最近日志中查找 TPS 信息
                    List<string> recentLines = new List<string>();
                    lock (Analyzer._logBufferLock)
                    {
                        int start = Math.Max(0, Analyzer._logBuffer.Count - 100);
                        for (int i = start; i < Analyzer._logBuffer.Count; i++)
                            recentLines.Add(Analyzer._logBuffer[i]);
                    }

                    // 查找 TPS 相关行: 解析规则统一维护于 MCConsole(Spark 多值优先, 旧式单值兜底)
                    foreach (var line in recentLines)
                    {
                        if (MCConsole.TryParseSparkTps(line, out var t1, out var t5, out var t15))
                            return $"TPS(1m/5m/15m): {t1}, {t5}, {t15} (来源: 日志)";
                        if (MCConsole.TryParseSingleTps(line, out var tSingle))
                            return $"TPS: {tSingle} (来源: 日志)";
                    }

                    // 没找到则发送命令请求
                    Analyzer.SendCommand("tps");
                    return "[已发送 /tps 命令到服务器，请稍后查看日志获取TPS数据]";
                }
                catch (Exception ex)
                {
                    return $"[获取TPS失败] {ex.Message}";
                }
            }

            #endregion
        }

        #endregion
    }
}
