using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RtCli.Modules.Unit
{
    public class RegexSettings
    {
        public ConsoleErrorSetting Console_Error { get; set; } = new ConsoleErrorSetting();

        public ClientGuideSetting ClientGuide { get; set; } = new ClientGuideSetting();
    }

    public class ConsoleErrorSetting
    {
        public string Handler { get; set; } = @"(\[ERROR\]|\[SEVERE\]|Exception|Error|FATAL|Caused by|at\s+[\w\.$]+\()";

        public int Limit { get; set; } = 500;
    }

    public class ClientGuideSetting
    {
        public string PlayerJoin { get; set; } = @"joined the game|logged in with entity id";

        public string PlayerDisconnect { get; set; } = @"lost connection|disconnected|kicked from the server|timed out";

        public string ClientError { get; set; } = @"Outdated server|Outdated client|Invalid player data|Internal Exception|Connection refused|Connection timed out|Not authenticated|Failed to verify username|You are not whitelisted|You are banned|Server is full|Authentication Servers are down";

        public string ErrorHandler { get; set; } = @"(\[ERROR\]|\[SEVERE\]|Exception|Error|FATAL|Caused by|lost connection|disconnected|timed out|kicked)";

        public int Timeout { get; set; } = 30;
    }

    public static class ContentManager
    {
        private static readonly string RegexSettingsFile = "regex_settings.yml";
        private static readonly string FxSaveErrorFile = "fx_save_error.yml";
        private static readonly string CustomClientErrorsFile = "custom_client_errors.yml";
        private static readonly string CustomTroubleshootFile = "custom_troubleshoot.yml";
        private static bool _isInitialized = false;

        public static RegexSettings Regex { get; private set; } = new RegexSettings();

        private static readonly List<ClientGuideEntry> BuiltInClientErrors = new List<ClientGuideEntry>
        {
            new ClientGuideEntry("Outdated server", "客户端版本高于服务端版本", "安装 ViaVersion 或 ViaBackwards 插件以支持跨版本连接，或更新服务端到与客户端相同的版本"),
            new ClientGuideEntry("Outdated client", "客户端版本低于服务端版本", "更新客户端到与服务端相同的版本，或安装 ViaVersion 插件以支持跨版本连接"),
            new ClientGuideEntry("Invalid player data", "玩家数据损坏或格式不兼容", "删除服务端 world/playerdata/ 下对应玩家的 .dat 文件，让服务端重新生成玩家数据"),
            new ClientGuideEntry("Internal Exception", "服务端内部异常导致连接中断", "检查服务端日志中的异常堆栈信息，使用 .fx get 分析错误"),
            new ClientGuideEntry("Connection refused", "客户端无法连接到服务端", "检查服务端是否已启动、端口是否正确、防火墙是否放行端口"),
            new ClientGuideEntry("Connection timed out", "连接超时", "检查网络连通性、服务端是否卡死(TPS过低)、防火墙或NAT配置"),
            new ClientGuideEntry("Not authenticated", "正版验证失败", "检查 server.properties 中 online-mode 设置，离线服设为 false，正版服确认账号有效"),
            new ClientGuideEntry("Failed to verify username", "用户名验证失败", "检查 Mojang/AuthServer 是否可访问，或设置 online-mode=false 为离线模式"),
            new ClientGuideEntry("You are not whitelisted", "玩家不在白名单中", "在白名单中添加该玩家: /whitelist add <玩家名>，或关闭白名单: /whitelist off"),
            new ClientGuideEntry("You are banned", "玩家被封禁", "解封玩家: /pardon <玩家名>，或检查封禁列表: /banlist"),
            new ClientGuideEntry("Server is full", "服务器已满", "增加 server.properties 中的 max-players 值，或安装可控制人数的插件"),
            new ClientGuideEntry("Authentication Servers are down", "Mojang认证服务器不可用", "等待 Mojang 服务器恢复，或临时设置 online-mode=false (注意安全风险)"),
            new ClientGuideEntry("lost connection", "玩家意外断开连接", "检查网络稳定性、服务端日志中是否有踢出原因、是否被插件踢出"),
            new ClientGuideEntry("kicked from the server", "玩家被踢出", "检查踢出原因，可能是插件规则、AFK踢出、或管理员操作"),
            new ClientGuideEntry("timed out", "玩家连接超时", "检查服务端 TPS 是否正常、网络延迟、server.properties 中 timeout 设置"),
        };

        private static readonly List<ClientGuideTroubleshoot> BuiltInTroubleshoot = new List<ClientGuideTroubleshoot>
        {
            new ClientGuideTroubleshoot("HAProxy/反向代理配置", "是否在插件或映射软件或服务端上配置了 HAProxy，可能导致玩家无法正确连接", "检查 HAProxy 配置，确保 proxy_protocol 正确设置，或在服务端安装 HAProxy 支持插件"),
            new ClientGuideTroubleshoot("防火墙/端口转发", "服务端端口可能未在防火墙中放行或未正确配置端口转发", "检查服务器防火墙规则，确认端口转发配置正确，使用 canyouseeme.org 测试端口可达性"),
            new ClientGuideTroubleshoot("server.properties 配置", "server-ip 或 server-port 配置错误", "确认 server-ip 留空或设为正确 IP，server-port 设为期望端口"),
            new ClientGuideTroubleshoot("在线模式不匹配", "online-mode 设置与客户端登录方式不匹配", "确认 online-mode 与实际使用方式一致，正版服设 true，离线服设 false"),
            new ClientGuideTroubleshoot("服务端卡死", "服务端 TPS 过低导致无法处理连接", "使用 /tps 检查 TPS，查看服务端日志是否有卡顿信息，减少插件或优化性能"),
            new ClientGuideTroubleshoot("BungeeCord/Velocity 转发", "使用了 BungeeCord/Velocity 但配置不正确", "确认子服的 spigot.yml 中 settings.bungeecord 设为 true，Velocity 使用 velocity-modern 转发模式"),
        };

        public static void Initialize()
        {
            if (_isInitialized) return;

            LoadRegexSettings();
            EnsureCustomFile<ClientGuideEntry>(CustomClientErrorsFile, "自定义客户端错误条目");
            EnsureCustomFile<ClientGuideTroubleshoot>(CustomTroubleshootFile, "自定义超时排查条目");
            _isInitialized = true;
        }

        private static void EnsureCustomFile<T>(string fileName, string description)
        {
            string filePath = Path.Combine(Config.DataPath, fileName);
            if (!File.Exists(filePath))
            {
                SaveCustomTemplate<T>(filePath, description);
                Output.Log($"创建自定义配置文件: {filePath}", 1, "ContentManager");
            }
        }

        public static List<ClientGuideEntry> GetAllClientErrors()
        {
            var custom = LoadCustomList<ClientGuideEntry>(CustomClientErrorsFile, "自定义客户端错误条目");
            var result = new List<ClientGuideEntry>(BuiltInClientErrors);
            result.AddRange(custom);
            Output.Log($"已加载的匹配项: 内置 {BuiltInClientErrors.Count} 自定义 {custom.Count} 共 {result.Count} 条", 1, "ContentManager");
            return result;
        }

        public static List<ClientGuideTroubleshoot> GetAllTroubleshoot()
        {
            var custom = LoadCustomList<ClientGuideTroubleshoot>(CustomTroubleshootFile, "自定义超时排查条目");
            var result = new List<ClientGuideTroubleshoot>(BuiltInTroubleshoot);
            result.AddRange(custom);
            return result;
        }

        private static void LoadRegexSettings()
        {
            string filePath = Path.Combine(Config.DataPath, RegexSettingsFile);

            if (!File.Exists(filePath))
            {
                SaveRegexSettings(filePath);
                Output.Log($"创建正则配置文件: {filePath}", 1, "ContentManager");
                return;
            }

            try
            {
                var yaml = File.ReadAllText(filePath);
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .Build();
                Regex = deserializer.Deserialize<RegexSettings>(yaml) ?? new RegexSettings();
            }
            catch
            {
                Output.Log("正则配置文件解析失败，使用默认配置", 2, "ContentManager");
                Regex = new RegexSettings();
            }
        }

        private static void SaveRegexSettings(string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#                     RtCli 正则表达式配置文件");
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#");
            sb.AppendLine("#  Console_Error:");
            sb.AppendLine("#    handler - 匹配错误日志行的正则表达式");
            sb.AppendLine("#    limit   - 匹配后截取内容的最大字符数");
            sb.AppendLine("#");
            sb.AppendLine("#  ClientGuide:");
            sb.AppendLine("#    player_join       - 匹配玩家加入服务端的消息");
            sb.AppendLine("#    player_disconnect - 匹配玩家断开连接的消息");
            sb.AppendLine("#    client_error      - 匹配客户端导致的错误消息");
            sb.AppendLine("#    error_handler     - 匹配错误日志行的正则表达式(clientguide)");
            sb.AppendLine("#    timeout           - 等待玩家消息的超时时间(秒)");
            sb.AppendLine("#");
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine();

            var serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            var yaml = serializer.Serialize(Regex);

            var lines = yaml.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("handler:"))
                    sb.AppendLine("# 匹配错误日志行的正则表达式");
                else if (trimmed.StartsWith("limit:"))
                    sb.AppendLine("# 匹配后截取内容的最大字符数");
                else if (trimmed.StartsWith("player_join:"))
                    sb.AppendLine("# 匹配玩家加入服务端的消息");
                else if (trimmed.StartsWith("player_disconnect:"))
                    sb.AppendLine("# 匹配玩家断开连接的消息");
                else if (trimmed.StartsWith("client_error:"))
                    sb.AppendLine("# 匹配客户端导致的错误消息");
                else if (trimmed.StartsWith("error_handler:"))
                    sb.AppendLine("# 匹配错误日志行的正则表达式(clientguide)");
                else if (trimmed.StartsWith("timeout:"))
                    sb.AppendLine("# 等待玩家消息的超时时间(秒)");

                sb.Append(line);
            }

            File.WriteAllText(filePath, sb.ToString());
        }

        private static List<T> LoadCustomList<T>(string fileName, string description)
        {
            string filePath = Path.Combine(Config.DataPath, fileName);

            if (!File.Exists(filePath))
            {
                SaveCustomTemplate<T>(filePath, description);
                return new List<T>();
            }

            try
            {
                var yaml = File.ReadAllText(filePath);
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .Build();
                var result = deserializer.Deserialize<List<T>>(yaml) ?? new List<T>();
                if (result.Count > 0)
                {
                    Output.Log($"已加载 {result.Count} 条{description} ({fileName})", 1, "ContentManager");
                }
                else
                {
                    bool hasContent = yaml.Split('\n').Any(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#"));
                    if (hasContent)
                    {
                        Output.Log($"{fileName} 解析成功但结果为空，请检查 YAML 格式是否正确", 2, "ContentManager");
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                Output.Log($"自定义配置文件 {fileName} 解析失败: {ex.Message}", 3, "ContentManager");
                return new List<T>();
            }
        }

        private static void SaveCustomTemplate<T>(string filePath, string description)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {description}");
            sb.AppendLine($"# 格式:");
            if (typeof(T) == typeof(ClientGuideEntry))
            {
                sb.AppendLine("# - keyword: 错误关键词");
                sb.AppendLine("#   description: 错误描述");
                sb.AppendLine("#   solution: 解决方案");
                sb.AppendLine("#");
                sb.AppendLine();
                sb.AppendLine("- keyword: Protocol error");
                sb.AppendLine("  description: 协议错误，可能是客户端与服务端协议版本不兼容");
                sb.AppendLine("  solution: 检查客户端和服务端的协议版本，安装 ProtocolSupport 或 ViaVersion 插件");
            }
            else if (typeof(T) == typeof(ClientGuideTroubleshoot))
            {
                sb.AppendLine("# - title: 问题标题");
                sb.AppendLine("#   problem: 问题描述");
                sb.AppendLine("#   solution: 解决方案");
                sb.AppendLine("#");
                sb.AppendLine();
                sb.AppendLine("- title: 内存不足");
                sb.AppendLine("  problem: 服务端分配的内存不足，可能导致玩家无法加入或频繁卡顿");
                sb.AppendLine("  solution: 增加 JVM 启动参数中的 -Xmx 值，建议至少 2GB 以上");
            }
            sb.AppendLine();

            File.WriteAllText(filePath, sb.ToString());
        }

        public static void SaveErrorLog(Dictionary<int, string> errors)
        {
            string filePath = Path.Combine(Config.DataPath, FxSaveErrorFile);

            var sb = new StringBuilder();
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#                     RtCli MC错误日志分析结果");
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine();

            foreach (var kv in errors)
            {
                sb.AppendLine($"{kv.Key}: |-");
                var lines = kv.Value.Split('\n');
                foreach (var line in lines)
                {
                    sb.AppendLine($"  {line}");
                }
                sb.AppendLine();
            }

            File.WriteAllText(filePath, sb.ToString());
            Output.Log($"错误分析结果已保存: {filePath}", 1, "ContentManager");
        }

        public static Dictionary<int, string> LoadErrorLog()
        {
            string filePath = Path.Combine(Config.DataPath, FxSaveErrorFile);
            if (!File.Exists(filePath))
                return new Dictionary<int, string>();

            try
            {
                var yaml = File.ReadAllText(filePath);
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .Build();
                return deserializer.Deserialize<Dictionary<int, string>>(yaml) ?? new Dictionary<int, string>();
            }
            catch
            {
                return new Dictionary<int, string>();
            }
        }

        public static void DeleteErrorLog()
        {
            string filePath = Path.Combine(Config.DataPath, FxSaveErrorFile);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                Output.Log("已删除错误分析结果。", 1, "ContentManager");
            }
        }
    }

    public class ClientGuideEntry
    {
        public string Keyword { get; set; } = "";
        public string Description { get; set; } = "";
        public string Solution { get; set; } = "";

        public ClientGuideEntry() { }

        public ClientGuideEntry(string keyword, string description, string solution)
        {
            Keyword = keyword;
            Description = description;
            Solution = solution;
        }
    }

    public class ClientGuideTroubleshoot
    {
        public string Title { get; set; } = "";
        public string Problem { get; set; } = "";
        public string Solution { get; set; } = "";

        public ClientGuideTroubleshoot() { }

        public ClientGuideTroubleshoot(string title, string problem, string solution)
        {
            Title = title;
            Problem = problem;
            Solution = solution;
        }
    }
}
