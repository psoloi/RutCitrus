using RtCli.Modules.Extension;
using RtCli.Modules.Function;
using Spectre.Console;
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

        public List<ClientGuideEntry> CustomClientErrors { get; set; } = new List<ClientGuideEntry>();

        public List<ClientGuideTroubleshoot> CustomTroubleshoot { get; set; } = new List<ClientGuideTroubleshoot>();

        public List<BaseEntry> CustomBaseEntries { get; set; } = new List<BaseEntry>();

        public List<TipsEntry> CustomTips { get; set; } = new List<TipsEntry>();
    }

    public class ConsoleErrorSetting
    {
        public string Handler { get; set; } = @"(\[ERROR\]|\[SEVERE\]|\[WARN\]|\[WARNING\]|Exception|Error|FATAL|Caused by|at\s+[\w\.$]+\(|Warning:)";

        public int Limit { get; set; } = 500;
    }

    /// <summary>
    /// 错误分析记录(含数据来源): 服务器标识 / 外部导入(.fx add) / 实时缓冲等
    /// </summary>
    public class ErrorRecord
    {
        [YamlMember(Alias = "content")]
        public string Content { get; set; } = "";

        /// <summary>来源标识: 服务器标识或外部文件名</summary>
        [YamlMember(Alias = "source")]
        public string Source { get; set; } = "";

        /// <summary>来源类型: server / external / buffer / legacy(旧格式记录)</summary>
        [YamlMember(Alias = "source_type")]
        public string SourceType { get; set; } = "";

        [YamlMember(Alias = "time")]
        public string Time { get; set; } = "";

        /// <summary>显示用来源文本: [服务器] key / [外部] name / [历史]</summary>
        [YamlIgnore]
        public string SourceDisplay
        {
            get
            {
                return SourceType switch
                {
                    "server" => $"[服务器] {Source}",
                    "external" => $"[外部] {Source}",
                    "buffer" => $"[缓冲] {Source}",
                    _ => string.IsNullOrEmpty(Source) ? "[历史]" : Source
                };
            }
        }
    }

    public class ClientGuideSetting
    {
        public string PlayerJoin { get; set; } = @"joined the game|logged in with entity id";

        public string PlayerDisconnect { get; set; } = @"lost connection|disconnected|kicked from the server|timed out";

        public string ClientError { get; set; } = @"Outdated server|Outdated client|Invalid player data|Internal Exception|Connection refused|Connection timed out|Not authenticated|Failed to verify username|You are not whitelisted|You are banned|Server is full|Authentication Servers are down";

        public string ErrorHandler { get; set; } = @"(\[ERROR\]|\[SEVERE\]|\[WARN\]|\[WARNING\]|Exception|Error|FATAL|Caused by|lost connection|disconnected|timed out|kicked|Warning:)";

        public int Timeout { get; set; } = 30;
    }

    public static class ContentManager
    {
        private static readonly string RegexSettingsFile = "regex_settings.yml";
        private static readonly string FxSaveErrorFile = "fx_save_error.yml";
        private static readonly string AiSettingsFile = "ai_settings.yml";
        private static readonly string AiSaveFile = "ai_save.yml";
        private static readonly string ScriptsSettingsFile = "scripts_settings.yml";
        private static readonly string SchedulerSettingsFile = "scheduler_settings.yml";
        private static readonly string SupportSettingsFile = "support.yml";
        private static bool _isInitialized = false;

        public static RegexSettings Regex { get; private set; } = new RegexSettings();

        public static AiSetting Ai { get; private set; } = new AiSetting();

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

        private static readonly List<BaseEntry> BuiltInBaseEntries = new List<BaseEntry>
        {
            new BaseEntry(
                @"has been compiled by a more recent version of the Java Runtime \(class file version \d+\.0\), this version of the Java Runtime only recognizes class file versions up to \d+\.0",
                "Java版本与插件不兼容",
                "当前Java版本与插件构建的版本不符，请升级Java版本到与插件要求一致，或在启动参数中指定更高版本的Java路径",
                ""),
            new BaseEntry(
                @"java\.lang\.OutOfMemoryError",
                "内存溢出",
                "服务端内存不足，请增加JVM启动参数中的 -Xmx 值（如 -Xmx4G），检查是否存在内存泄漏插件",
                ""),
            new BaseEntry(
                @"java\.lang\.StackOverflowError",
                "栈溢出",
                "可能存在无限递归调用，检查插件代码或配置中是否有循环引用",
                ""),
            new BaseEntry(
                @"java\.net\.BindException: Address already in use",
                "端口被占用",
                "服务端端口已被其他程序占用，请更换端口或关闭占用端口的程序。使用 netstat -ano | findstr <端口> 查找占用进程",
                ""),
            new BaseEntry(
                @"java\.net\.UnknownHostException",
                "DNS解析失败",
                "无法解析主机名，请检查网络连接和DNS设置",
                ""),
            new BaseEntry(
                @"java\.io\.FileNotFoundException|java\.nio\.file\.NoSuchFileException",
                "文件未找到",
                "缺少必要的文件，可能是插件或服务端文件损坏，尝试重新下载或恢复该文件",
                ""),
            new BaseEntry(
                @"java\.lang\.ClassNotFoundException",
                "类未找到",
                "缺少必要的类库，可能是插件依赖缺失或版本不匹配，检查插件是否需要额外的前置插件",
                ""),
            new BaseEntry(
                @"java\.lang\.NoClassDefFoundError",
                "类定义未找到",
                "运行时找不到类定义，通常是插件与服务端版本不兼容或缺少前置插件，请更新插件或安装前置",
                ""),
            new BaseEntry(
                @"java\.lang\.NoSuchMethodError",
                "方法未找到",
                "调用的方法不存在，通常是插件与服务端API版本不匹配，请更新插件到与服务端兼容的版本",
                ""),
            new BaseEntry(
                @"java\.lang\.IllegalArgumentException",
                "非法参数",
                "传入了不合法的参数，检查相关插件配置文件中的值是否正确",
                ""),
            new BaseEntry(
                @"java\.lang\.NullPointerException",
                "空指针异常",
                "程序尝试访问空对象，通常是插件Bug或配置缺失，检查插件配置并更新到最新版本",
                ""),
            new BaseEntry(
                @"java\.lang\.UnsupportedClassVersionError",
                "类版本不支持",
                "Java版本过低无法加载类文件，请升级Java版本到插件要求的版本，或自行均衡乃至构建",
                ""),
            new BaseEntry(
                @"org\.yaml\.snakeyaml\.error\.YAMLException|org\.yaml\.snakeyaml\.scanner\.ScannerException|while parsing a block mapping|while parsing a block collection",
                "YAML配置文件格式错误",
                "配置文件的YAML格式有误，请检查缩进是否一致（使用空格而非Tab）、冒号后是否有空格、引号是否闭合",
                ""),
            new BaseEntry(
                @"com\.google\.json\.JsonSyntaxException|com\.fasterxml\.jackson\.core\.JsonParseException|JsonParseException|Unexpected character",
                "JSON配置文件格式错误",
                "配置文件的JSON格式有误，请检查括号是否匹配、字符串是否用双引号包裹、是否有多余的逗号",
                ""),
            new BaseEntry(
                @"Plugin .* has not been enabled|Failed to load plugin|Cannot load plugin",
                "插件加载失败",
                "插件未能成功加载，可能是缺少前置插件、版本不兼容或配置错误，查看详细错误信息确认原因",
                ""),
            new BaseEntry(
                @"Timings reset|Timings report|timings",
                "性能分析",
                "这是Timings性能分析相关信息，如非主动操作请忽略，可使用 /timings paste 获取性能报告，不建议使用timings建议使用spark",
                ""),
            new BaseEntry(
                @"World .* is corrupt|Chunk .* is corrupt|Corrupted chunk",
                "世界/区块损坏",
                "世界或区块数据损坏，尝试使用区块修复工具或从备份恢复，也可删除损坏的区块文件让服务端重新生成",
                ""),
            new BaseEntry(
                @"Keeping entity .* that already exists|Duplicate entity|Entity id .* already exists",
                "实体重复",
                "存在重复实体，通常是区块数据异常导致，可使用 /kill @e[type=!player] 清理或使用插件如 LagFixer 清理",
                ""),
            new BaseEntry(
                @"Can.t keep up!.*overloaded",
                "服务器过载/TPS过低",
                "服务端处理速度跟不上，检查是否有高消耗插件、实体过多、红石机械过多，考虑优化或升级硬件",
                ""),
            new BaseEntry(
                @"Moved (too quickly|wrongly)!|moved wrongly|moved too quickly",
                "玩家移动异常",
                "玩家位置更新异常，可能是网络延迟、飞行作弊检测或服务端TPS过低导致误判，可调整 server.properties 中相关阈值",
                ""),
            new BaseEntry(
                @"java\.net\.SocketException: Connection reset|Connection reset by peer",
                "连接被重置",
                "网络连接被异常关闭，可能是网络不稳定、防火墙拦截或客户端异常退出",
                ""),
            new BaseEntry(
                @"The server has stopped responding and has been forcibly shutdown!",
                "Watchdog服务端崩溃",
                "服务端主线程卡死被Watchdog强制关闭，通常是某个插件或红石机械导致主线程阻塞，检查崩溃前的日志定位卡死原因",
                ""),
            new BaseEntry(
                @"Server thread watchdog|overloaded|Running \d+ms behind",
                "服务端主线程过载",
                "服务端主线程处理超时，可能是高消耗插件、大量实体、复杂红石电路或世界生成导致，使用 /timings paste 分析性能瓶颈",
                ""),
            new BaseEntry(
                @"io\.net\.channel\.AbstractChannel\$AnnotatedConnectException|Connection refused: no further information",
                "BungeeCord/Velocity后端连接失败",
                "代理服务端无法连接到后端服务器，检查后端服务器是否在线、IP和端口配置是否正确、后端服务器是否设置了proxy_protocol",
                ""),
            new BaseEntry(
                @"Could not connect to backend server|lost connection before we could connect you to the backend",
                "Velocity代理后端不可达",
                "Velocity无法将玩家转发到后端服务器，检查velocity.toml中后端服务器配置、确保后端服务器已启动且端口可达",
                ""),
            new BaseEntry(
                @"If you wish to use IP forwarding, please enable it in your BungeeCord config as well!",
                "BungeeCord IP转发未配置",
                "后端服务器检测到代理连接但未启用IP转发，在后端服务器的 spigot.yml 中设置 bungeecord: true，或在 Paper 的 paper-global.yml 中配置",
                ""),
            new BaseEntry(
                @"Kicked for floating too long|flying is not enabled on this server",
                "BungeeCord/Velocity悬浮踢出",
                "代理环境下玩家被误判为飞行，在服务端 server.properties 中设置 allow-flight=true，或检查代理的转发模式配置",
                ""),
            new BaseEntry(
                @"Not authenticated with Minecraft|Invalid player data received from proxy",
                "代理转发认证失败",
                "代理转发的玩家信息未通过认证，检查代理和后端服务器的转发模式配置是否一致（BungeeCord/velocity-modern/legacy）",
                ""),
            new BaseEntry(
                @"net\.minecraft\.util\.crash\.CrashReport|Minecraft ran into a problem|Unexpected error",
                "Minecraft崩溃报告",
                "服务端发生崩溃，查看 crash-reports/ 目录下的崩溃报告文件获取详细信息，通常是模组或插件导致",
                ""),
            new BaseEntry(
                @"cpw\.mods\.modlauncher|net\.minecraftforge\.fml|Failed to load mod|Mod loading error|Mod .* has failed to load",
                "Forge模组加载失败",
                "Forge模组加载出错，检查模组版本是否与Forge版本匹配、是否有缺失的前置模组、模组间是否存在冲突",
                ""),
            new BaseEntry(
                @"net\.fabricmc\.loader|FabricLoader|Could not load mod|Mod resolution failed|Unresolved dependency",
                "Fabric模组加载失败",
                "Fabric模组加载出错，检查模组版本是否与Fabric Loader/API版本匹配、依赖模组是否齐全、模组间是否存在版本冲突",
                ""),
            new BaseEntry(
                @"net\.neoforged\.fml|NeoForge|Failed to load mod|Mod loading error",
                "NeoForge模组加载失败",
                "NeoForge模组加载出错，检查模组版本是否与NeoForge版本匹配、是否有缺失的前置模组",
                ""),
            new BaseEntry(
                @"Mixin apply error|Mixin .* failed|could not apply mixin",
                "Mixin注入失败",
                "模组的Mixin补丁应用失败，通常是模组版本与其他模组或服务端版本不兼容，更新相关模组或检查兼容性",
                ""),
            new BaseEntry(
                @"Registry .* already present|Duplicate registry|Could not register",
                "注册表冲突",
                "模组/插件尝试注册已存在的条目，通常是多个模组注册了相同的内容，检查是否有重复安装的模组",
                ""),
            new BaseEntry(
                @"Arclight|Mixed mod and plugin environment|Cannot load plugin in mod environment",
                "Arclight混合环境错误",
                "Arclight混合环境中插件与模组不兼容，检查插件是否支持Forge/Fabric环境，或使用替代插件",
                ""),
            new BaseEntry(
                @"com\.destroystokyo\.paper|io\.papermc\.paper|Paper watchdog",
                "Paper服务端问题",
                "Paper服务端特有问题，检查Paper版本是否与插件兼容，查看详细堆栈信息定位问题插件",
                ""),
            new BaseEntry(
                @"org\.spigotmc|Spigot version|CraftBukkit version mismatch",
                "Spigot版本不匹配",
                "插件与Spigot服务端版本不匹配，更新插件到与服务端API版本兼容的版本",
                ""),
            new BaseEntry(
                @"java\.util\.concurrent\.ExecutionException|java\.util\.concurrent\.CompletableFuture",
                "异步任务异常",
                "异步操作中发生异常，查看Caused by后的具体原因，通常是插件在异步线程中执行了不允许的操作",
                ""),
        };

        private static readonly List<TipsEntry> BuiltInTips = new List<TipsEntry>
        {
            new TipsEntry(
                @"has been compiled by a more recent version of the Java Runtime \(class file version (\d+)\.0\), this version of the Java Runtime only recognizes class file versions up to (\d+)\.0",
                "Java版本不兼容：该插件/模组需要更高版本的Java。class file version对照：52=Java8, 53=Java9, 54=Java10, 55=Java11, 56=Java12, 57=Java13, 58=Java14, 59=Java15, 60=Java16, 61=Java17, 62=Java18, 63=Java19, 64=Java20, 65=Java21, 66=Java22, 67=Java23, 69=Java25。请升级Java到对应版本或更高版本"),
            new TipsEntry(
                @"Setting online-mode to false|Online mode is disabled|server is running in offline mode",
                "已关闭正版验证(online-mode=false)，任何玩家可以使用离线版加入服务器，建议安装登录插件(如AuthMeReload、JPremium)或开启白名单(/whitelist on)防止恶意登录"),
            new TipsEntry(
                @"Can.t keep up!.*overloaded",
                "服务端过载，TPS可能已降低。检查方向：高消耗插件/模组、实体数量、红石机械复杂度、机器性能、配置问题、服务器崩溃"),
            new TipsEntry(
                @"The server has stopped responding and has been forcibly shutdown!",
                "Watchdog检测到服务端无响应并强制关闭，主线程被阻塞。检查崩溃前的日志定位阻塞原因，可能是死循环插件或无限等待的I/O操作"),
            new TipsEntry(
                @"Failed to bind to port|Perhaps a server is already running on that port\?",
                "端口已被占用，请检查：1.是否有其他MC服务端实例在运行 2.使用 netstat -ano | findstr <端口> 查找占用进程 3.更换server.properties中的端口号"),
            new TipsEntry(
                @"You are running an outdated version of (Paper|Spigot|CraftBukkit)",
                "服务端核心的版本可更新"),
            new TipsEntry(
                @"Loading LegacyAPI|This server is running an outdated version of",
                "服务端或插件API版本过旧，部分插件可能无法正常工作，建议更新服务端核心，使用只能工具自行构建或寻找替代方案"),
            new TipsEntry(
                @"java\.lang\.OutOfMemoryError: Java heap space",
                "Java堆内存不足，请在启动参数中增加 -Xmx 值（如 -Xmx4G），同时检查是否存在内存泄漏的插件"),
            new TipsEntry(
                @"java\.lang\.OutOfMemoryError: Metaspace",
                "Metaspace内存不足（类加载过多），请在启动参数中增加 -XX:MaxMetaspaceSize=256M 或更高值，或减少模组/插件数量"),
            new TipsEntry(
                @"Could not load 'plugins[/\\](.+\.jar)' in folder 'plugins'",
                "插件加载失败，可能原因：1.缺少前置插件 2.插件与服务端版本不兼容 3.jar文件损坏，请检查详细错误日志"),
            new TipsEntry(
                @"Could not create FastClassByGuice|Could not create BoostedClassLocator",
                "Guice/依赖注入框架错误，通常是插件初始化失败，检查插件是否与服务端版本兼容"),
            new TipsEntry(
                @"Error occurred while enabling|Disabling plugin",
                "插件启用时出错被自动禁用，查看详细堆栈信息定位问题，通常是配置错误(看有没有JSON/YAML字样)或缺少依赖"),
            new TipsEntry(
                @"Could not pass event (.+) to (.+)",
                "插件事件处理异常，$2在处理$1事件时出错，检查该插件是否为最新版本，或向插件作者反馈"),
            new TipsEntry(
                @"World .* contains corrupt chunk|Chunk .* at \[.*\] is corrupt",
                "世界文件中存在损坏的区块，可使用区块修复工具(如 MCA Selector)修复或删除损坏区块让服务端重新生成"),
            new TipsEntry(
                @"Moved too quickly!|Moved wrongly!",
                "玩家移动异常被服务端修正，频繁出现可能是：1.网络延迟高 2.服务端TPS低 3.玩家可能作弊或插件的自定义属性导致。可在spigot.yml中调整相关阈值"),
        };

        public static void Initialize()
        {
            if (_isInitialized) return;

            LoadRegexSettings();
            LoadAiSettings();
            EnsureScriptsSettings();
            EnsureSchedulerSettings();
            _isInitialized = true;
        }

        public static List<ClientGuideEntry> GetAllClientErrors()
        {
            var custom = Regex.CustomClientErrors ?? new List<ClientGuideEntry>();
            var result = new List<ClientGuideEntry>(BuiltInClientErrors);
            result.AddRange(custom);
            if (custom.Count > 0)
                Output.Log($"客户端错误条目: 内置 {BuiltInClientErrors.Count} + 自定义 {custom.Count} = 共 {result.Count} 条", 1, "ContentManager");
            return result;
        }

        public static List<ClientGuideTroubleshoot> GetAllTroubleshoot()
        {
            var custom = Regex.CustomTroubleshoot ?? new List<ClientGuideTroubleshoot>();
            var result = new List<ClientGuideTroubleshoot>(BuiltInTroubleshoot);
            result.AddRange(custom);
            return result;
        }

        public static List<BaseEntry> GetAllBaseEntries()
        {
            var custom = Regex.CustomBaseEntries ?? new List<BaseEntry>();
            var result = new List<BaseEntry>(BuiltInBaseEntries);
            result.AddRange(custom);
            return result;
        }

        public static List<TipsEntry> GetAllTips()
        {
            var custom = Regex.CustomTips ?? new List<TipsEntry>();
            var result = new List<TipsEntry>(BuiltInTips);
            result.AddRange(custom);
            return result;
        }

        public static void ReloadAll()
        {
            _isInitialized = false;
            Initialize();
            Output.Log("ContentManager 配置已重载", 1, "ContentManager");
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

            Regex = Config.LoadYamlConfig<RegexSettings>(filePath, new RegexSettings(), "regex_settings.yml");
        }

        private static void SaveRegexSettings(string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#                     RtCli 正则表达式配置文件");
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#");
            sb.AppendLine("#  Console_Error:");
            sb.AppendLine("#    handler - 匹配错误/警告日志行的正则表达式");
            sb.AppendLine("#    limit   - 匹配后截取内容的最大字符数");
            sb.AppendLine("#");
            sb.AppendLine("#  ClientGuide:");
            sb.AppendLine("#    player_join       - 匹配玩家加入服务端的消息");
            sb.AppendLine("#    player_disconnect - 匹配玩家断开连接的消息");
            sb.AppendLine("#    client_error      - 匹配客户端导致的错误消息");
            sb.AppendLine("#    error_handler     - 匹配错误/警告日志行的正则表达式(clientguide)");
            sb.AppendLine("#    timeout           - 等待玩家消息的超时时间(秒)");
            sb.AppendLine("#");
            sb.AppendLine("#  CustomClientErrors: 自定义客户端错误条目");
            sb.AppendLine("#    - keyword: 错误关键词");
            sb.AppendLine("#      description: 错误描述");
            sb.AppendLine("#      solution: 解决方案");
            sb.AppendLine("#");
            sb.AppendLine("#  CustomTroubleshoot: 自定义超时排查条目");
            sb.AppendLine("#    - title: 问题标题");
            sb.AppendLine("#      problem: 问题描述");
            sb.AppendLine("#      solution: 解决方案");
            sb.AppendLine("#");
            sb.AppendLine("#  CustomBaseEntries: 自定义基础分析条目");
            sb.AppendLine("#    - pattern: '正则表达式(用单引号包裹)'");
            sb.AppendLine("#      topic: 问题主题");
            sb.AppendLine("#      solution: 解决方案");
            sb.AppendLine("#      action: 脚本名称(留空不执行)");
            sb.AppendLine("#");
            sb.AppendLine("#  CustomTips: 自定义自动提示条目");
            sb.AppendLine("#    - pattern: '正则表达式(用单引号包裹)'");
            sb.AppendLine("#      tips: 提示内容");
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
                {
                    sb.AppendLine("# 匹配错误/警告日志行的正则表达式");
                }
                else if (trimmed.StartsWith("limit:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 匹配后截取内容的最大字符数");
                }
                else if (trimmed.StartsWith("player_join:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 匹配玩家加入服务端的消息");
                }
                else if (trimmed.StartsWith("player_disconnect:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 匹配玩家断开连接的消息");
                }
                else if (trimmed.StartsWith("client_error:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 匹配客户端导致的错误消息");
                }
                else if (trimmed.StartsWith("error_handler:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 匹配错误/警告日志行的正则表达式(clientguide)");
                }
                else if (trimmed.StartsWith("timeout:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 等待玩家消息的超时时间(秒)");
                }
                else if (trimmed.StartsWith("custom_client_errors:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 自定义客户端错误条目(格式: keyword/description/solution)");
                }
                else if (trimmed.StartsWith("custom_troubleshoot:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 自定义超时排查条目(格式: title/problem/solution)");
                }
                else if (trimmed.StartsWith("custom_base_entries:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 自定义基础分析条目(格式: pattern/topic/solution/action)");
                }
                else if (trimmed.StartsWith("custom_tips:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 自定义自动提示条目(格式: pattern/tips)");
                }

                sb.AppendLine(line.TrimEnd('\r'));
            }

            File.WriteAllText(filePath, sb.ToString());
        }

        public static void LoadAiSettings()
        {
            string filePath = Path.Combine(Config.DataPath, AiSettingsFile);
            if (!File.Exists(filePath))
            {
                SaveAiSettings(filePath);
                Output.Log($"创建AI配置文件: {filePath}", 1, "ContentManager");
                return;
            }

            Ai = Config.LoadYamlConfig<AiSetting>(filePath, new AiSetting(), "ai_settings.yml");
        }

        private static void SaveAiSettings(string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#                     RtCli AI 配置文件");
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#");
            sb.AppendLine("#  Console_Error:");
            sb.AppendLine("#    prompt               - 系统提示词");
            sb.AppendLine("#    max_tokens           - AI输出的最大token数");
            sb.AppendLine("#    api_endpoint         - AI API地址(兼容OpenAI格式)");
            sb.AppendLine("#    api_key              - API密钥(本地模型如Ollama留空即可)");
            sb.AppendLine("#    model                - AI模型名称");
            sb.AppendLine("#    temperature          - 生成温度(0.0-2.0，越低越确定)");
            sb.AppendLine("#    request_timeout_seconds - 请求超时时间(秒)");
            sb.AppendLine("#    retry_on_error       - 异常时是否自动重连");
            sb.AppendLine("#    retry_count          - 最大重试次数");
            sb.AppendLine("#    save_response        - 是否保存AI回复到ai_save.yml");
            sb.AppendLine("#");
            sb.AppendLine("#  ServerAutoAi (服务端自动AI管理):");
            sb.AppendLine("#    prompt               - 系统提示词(总)");
            sb.AppendLine("#    max_tokens           - AI输出的最大token数");
            sb.AppendLine("#    api_endpoint         - AI API地址(留空则复用Console_Error的配置)");
            sb.AppendLine("#    api_key              - API密钥(留空则复用Console_Error的配置)");
            sb.AppendLine("#    model                - AI模型名称(留空则复用Console_Error的配置)");
            sb.AppendLine("#    temperature          - 生成温度(建议较低, 0.3-0.6)");
            sb.AppendLine("#    request_timeout_seconds - 请求超时时间(秒)");
            sb.AppendLine("#    retry_on_error       - 异常时是否自动重连");
            sb.AppendLine("#    retry_count          - 最大重试次数");
            sb.AppendLine("#    save_response        - 是否保存AI回复(上下文缓存)到ai_save.yml");
            sb.AppendLine("#    context_cache_size   - 上下文缓存最大条数(每个任务独立)");
            sb.AppendLine("#    max_tool_rounds      - AI允许调用工具的最大轮次(防无限循环)");
            sb.AppendLine("#    tools                - 工具配置(enabled/allow/deny/tools_prompt)");
            sb.AppendLine("#    skills               - 技能列表(name/description/behavior)");
            sb.AppendLine("#    rules                - 规则列表(多行字符串, 使用 - | 格式)");
            sb.AppendLine("#    tasks                - 任务列表(键名为类别, 含name/trigger/input/limit/prompt/interval/actions)");
            sb.AppendLine("#");
            sb.AppendLine("#  内置工具:");
            sb.AppendLine("#    read_file            - 读取MC服务端目录中文件内容");
            sb.AppendLine("#    get_server_plugin_list - 获取MC服务端插件列表");
            sb.AppendLine("#    get_server_log       - 获取MC服务端日志");
            sb.AppendLine("#    run_script           - 运行Scripts中已配置的脚本");
            sb.AppendLine("#    modify_file          - 修改MC服务端目录中文件内容");
            sb.AppendLine("#    toggle_plugin        - 启用/禁用插件(jar<->disjar)");
            sb.AppendLine("#    run_command          - 运行程序命令或向服务器发送命令(禁止/op)");
            sb.AppendLine("#    restart_server       - 重启MC服务端");
            sb.AppendLine("#");
            sb.AppendLine("#  工具调用格式: [rt:tools\"(工具名称{参数JSON})\"]");
            sb.AppendLine("#    示例: [rt:tools\"(get_server_log{\"lines\":100})\"]");
            sb.AppendLine("#    示例: [rt:tools\"(toggle_plugin{\"plugin\":\"Example.jar\",\"disable\":true})\"]");
            sb.AppendLine("#");
            sb.AppendLine("#  tasks 任务字段说明:");
            sb.AppendLine("#    name      - 任务名称/主题");
            sb.AppendLine("#    trigger   - 触发条件(always/server_running/server_stopped/crash_detected)");
            sb.AppendLine("#                条件表达式: tps < 18 / cpu > 85 / memory > 80 / players == 0 / idle_minutes > 360");
            sb.AppendLine("#    input     - 数据源(server_logs/app_logs/server_tps/server_plugin/host_cpu/host_memory)");
            sb.AppendLine("#                扩展源: server_players/server_status/crash_report");
            sb.AppendLine("#    limit     - 提示词+输入内容最大token量");
            sb.AppendLine("#    prompt    - 该分类的提示词");
            sb.AppendLine("#    interval  - Cron表达式(如 0 0 0/1 * * ? 每小时)");
            sb.AppendLine("#    actions   - 执行动作(ai 进行AI分析, tools:工具名1,工具名2 限定可用工具)");
            sb.AppendLine("#");
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine();

            var serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            var yaml = serializer.Serialize(Ai);

            var lines = yaml.Split('\n');
            bool inServerAutoAi = false;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("server_auto_ai:"))
                {
                    inServerAutoAi = true;
                    sb.AppendLine();
                    sb.AppendLine("# ====================== 服务端自动AI管理 ======================");
                    sb.AppendLine("# 启用后可通过 .ai 命令进行无人自动化管理MC服务端");
                    sb.AppendLine(line.TrimEnd('\r'));
                    continue;
                }
                if (inServerAutoAi)
                {
                    if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !trimmed.StartsWith("-") && !trimmed.StartsWith("#"))
                    {
                        inServerAutoAi = false;
                    }
                }
                if (!inServerAutoAi)
                {
                    if (trimmed.StartsWith("prompt:"))
                    {
                        sb.AppendLine("# 系统提示词");
                    }
                    else if (trimmed.StartsWith("max_tokens:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# AI输出的最大token数");
                    }
                    else if (trimmed.StartsWith("api_endpoint:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# AI API地址(兼容OpenAI格式，如 http://localhost:11434/v1/chat/completions 或 https://api.openai.com/v1/chat/completions)");
                    }
                    else if (trimmed.StartsWith("api_key:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# API密钥(本地模型如Ollama留空即可，OpenAI等云服务需要填写)");
                    }
                    else if (trimmed.StartsWith("model:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# AI模型名称(如 qwen2.5:7b、gpt-4o-mini、deepseek-chat、openrouter/free 等)");
                    }
                    else if (trimmed.StartsWith("temperature:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 生成温度(0.0-2.0，越低越确定，越高越随机)");
                    }
                    else if (trimmed.StartsWith("request_timeout_seconds:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 请求超时时间(秒)");
                    }
                    else if (trimmed.StartsWith("retry_on_error:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 异常时是否自动重连");
                    }
                    else if (trimmed.StartsWith("retry_count:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 最大重试次数");
                    }
                    else if (trimmed.StartsWith("save_response:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 是否保存AI回复到ai_save.yml");
                    }
                }
                else
                {
                    if (trimmed.StartsWith("prompt:"))
                    {
                        sb.AppendLine("# 系统提示词(总)");
                    }
                    else if (trimmed.StartsWith("max_tokens:"))
                    {
                        sb.AppendLine("# AI输出的最大token数");
                    }
                    else if (trimmed.StartsWith("api_endpoint:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# AI API地址(留空则复用Console_Error的配置)");
                    }
                    else if (trimmed.StartsWith("api_key:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# API密钥(留空则复用Console_Error的配置)");
                    }
                    else if (trimmed.StartsWith("model:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# AI模型名称(留空则复用Console_Error的配置)");
                    }
                    else if (trimmed.StartsWith("temperature:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 生成温度(建议较低, 0.3-0.6)");
                    }
                    else if (trimmed.StartsWith("request_timeout_seconds:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 请求超时时间(秒)");
                    }
                    else if (trimmed.StartsWith("retry_on_error:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 异常时是否自动重连");
                    }
                    else if (trimmed.StartsWith("retry_count:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 最大重试次数");
                    }
                    else if (trimmed.StartsWith("save_response:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 是否保存AI回复(上下文缓存)到ai_save.yml");
                    }
                    else if (trimmed.StartsWith("context_cache_size:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 上下文缓存最大条数(每个任务独立缓存)");
                    }
                    else if (trimmed.StartsWith("max_tool_rounds:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# AI允许调用工具的最大轮次(防止无限循环)");
                    }
                    else if (trimmed.StartsWith("tools:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 工具配置(enabled/allow/deny/tools_prompt)");
                    }
                    else if (trimmed.StartsWith("enabled:"))
                    {
                        sb.AppendLine("# 是否启用该功能");
                    }
                    else if (trimmed.StartsWith("allow:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 允许使用的工具列表(为空则允许所有未在deny中的工具)");
                    }
                    else if (trimmed.StartsWith("deny:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 禁止使用的工具列表(优先级高于allow)");
                    }
                    else if (trimmed.StartsWith("tools_prompt:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 每个工具调用的提示词(键为工具名)");
                    }
                    else if (trimmed.StartsWith("skills:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 技能列表(每个技能包含name/description/behavior)");
                    }
                    else if (trimmed.StartsWith("rules:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 规则列表(多行字符串, 使用 - | 格式)");
                    }
                    else if (trimmed.StartsWith("tasks:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 任务列表(键名为类别, 含name/trigger/input/limit/prompt/interval/actions)");
                    }
                    else if (trimmed.StartsWith("name:"))
                    {
                        sb.AppendLine("# 任务名称/主题");
                    }
                    else if (trimmed.StartsWith("trigger:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 触发条件(tps < 18 / server_running / always)");
                    }
                    else if (trimmed.StartsWith("input:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 数据源(server_logs/app_logs/server_tps/server_plugin/host_cpu/host_memory)");
                    }
                    else if (trimmed.StartsWith("limit:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 提示词+输入内容最大token量");
                    }
                    else if (trimmed.StartsWith("interval:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# Cron表达式(如 0 0 0/1 * * ? 每小时执行一次)");
                    }
                    else if (trimmed.StartsWith("actions:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 执行动作(- ai 进行AI分析, - tools:工具名1,工具名2 限定可用工具)");
                    }
                }

                sb.AppendLine(line.TrimEnd('\r'));
            }

            File.WriteAllText(filePath, sb.ToString());
        }

        public static void SaveAiResponse(int errorIndex, string errorContent, string aiResponse)
        {
            if (!Ai.Console_Error.SaveResponse) return;

            string filePath = Path.Combine(Config.DataPath, AiSaveFile);
            var existing = new List<Dictionary<string, string>>();

            if (File.Exists(filePath))
            {
                try
                {
                    var yaml = File.ReadAllText(filePath);
                    var deserializer = new DeserializerBuilder()
                        .WithNamingConvention(UnderscoredNamingConvention.Instance)
                        .Build();
                    var loaded = deserializer.Deserialize<List<Dictionary<string, string>>>(yaml);
                    if (loaded != null) existing = loaded;
                }
                catch { }
            }

            existing.Add(new Dictionary<string, string>
            {
                ["error_index"] = errorIndex.ToString(),
                ["error_content"] = errorContent.Length > 500 ? errorContent.Substring(0, 497) + "..." : errorContent,
                ["ai_response"] = aiResponse,
                ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });

            var serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            File.WriteAllText(filePath, serializer.Serialize(existing));
        }

        public static void SaveErrorLog(Dictionary<int, ErrorRecord> errors, bool quiet = false)
        {
            string filePath = Path.Combine(Config.DataPath, FxSaveErrorFile);

            var sb = new StringBuilder();
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#                     RtCli MC错误日志分析结果");
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine();

            foreach (var kv in errors)
            {
                sb.AppendLine($"{kv.Key}:");
                sb.AppendLine($"  source: \"{kv.Value.Source.Replace("\"", "'")}\"");
                sb.AppendLine($"  source_type: \"{kv.Value.SourceType}\"");
                sb.AppendLine($"  time: \"{kv.Value.Time}\"");
                sb.AppendLine("  content: |-");
                var lines = kv.Value.Content.Split('\n');
                foreach (var line in lines)
                {
                    sb.AppendLine($"    {line}");
                }
                sb.AppendLine();
            }

            AtomicFile.WriteAllText(filePath, sb.ToString());
            if (!quiet)
                Output.Log($"错误分析结果已保存: {filePath}", 1, "ContentManager");
        }

        public static Dictionary<int, ErrorRecord> LoadErrorLog()
        {
            string filePath = Path.Combine(Config.DataPath, FxSaveErrorFile);
            if (!File.Exists(filePath))
                return new Dictionary<int, ErrorRecord>();

            try
            {
                var yaml = File.ReadAllText(filePath);
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .Build();

                // 新格式: Dictionary<int, ErrorRecord>(带来源)
                try
                {
                    var records = deserializer.Deserialize<Dictionary<int, ErrorRecord>>(yaml);
                    if (records != null && records.Count > 0) return records;
                }
                catch { }

                // 兼容旧格式(Dictionary<int, string>): 无来源信息,标记为历史记录
                var legacy = deserializer.Deserialize<Dictionary<int, string>>(yaml);
                if (legacy != null)
                {
                    var result = new Dictionary<int, ErrorRecord>();
                    foreach (var kv in legacy)
                    {
                        result[kv.Key] = new ErrorRecord { Content = kv.Value, Source = "", SourceType = "legacy", Time = "" };
                    }
                    return result;
                }
                return new Dictionary<int, ErrorRecord>();
            }
            catch
            {
                return new Dictionary<int, ErrorRecord>();
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

        /// <summary>
        /// 确保脚本配置文件存在，不存在则创建默认配置
        /// </summary>
        private static void EnsureScriptsSettings()
        {
            string filePath = Path.Combine(Config.DataPath, ScriptsSettingsFile);
            if (!File.Exists(filePath))
            {
                SaveDefaultScriptsSettings(filePath);
                Output.Log($"创建脚本配置文件: {filePath}", 1, "ContentManager");
            }
        }

        /// <summary>
        /// 确保调度器配置文件存在，不存在则创建默认配置
        /// </summary>
        private static void EnsureSchedulerSettings()
        {
            string filePath = Path.Combine(Config.DataPath, SchedulerSettingsFile);
            if (!File.Exists(filePath))
            {
                SaveDefaultSchedulerSettings(filePath);
                Output.Log($"创建调度器配置文件: {filePath}", 1, "ContentManager");
            }
        }

        /// <summary>
        /// 加载脚本配置
        /// </summary>
        public static ScriptsSettings LoadScriptsSettings()
        {
            string filePath = Path.Combine(Config.DataPath, ScriptsSettingsFile);

            if (!File.Exists(filePath))
            {
                SaveDefaultScriptsSettings(filePath);
                return new ScriptsSettings();
            }

            return Config.LoadYamlConfig<ScriptsSettings>(filePath, new ScriptsSettings(), "scripts_settings.yml");
        }

        /// <summary>
        /// 保存脚本配置
        /// </summary>
        public static void SaveScriptsSettings(ScriptsSettings settings)
        {
            string filePath = Path.Combine(Config.DataPath, ScriptsSettingsFile);

            var sb = new StringBuilder();
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#                     RtCli 脚本配置文件");
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#");
            sb.AppendLine("#  脚本配置说明:");
            sb.AppendLine("#    每个脚本的名称为项（键），项内包含以下属性:");
            sb.AppendLine("#    file          - 脚本文件路径（相对于 Content/Scripts 目录，或绝对路径）");
            sb.AppendLine("#                    .cs 文件使用 CS-Script 引擎执行");
            sb.AppendLine("#                    .py 文件使用 Python 解释器执行");
            sb.AppendLine("#    trigger_event - 触发脚本执行的事件名称，留空则在启动时执行一次");
            sb.AppendLine("#                    可用事件: ModeSelectedEvent, ProgramStartupEvent,");
            sb.AppendLine("#                    ProgramShutdownEvent, ExtensionLoadEvent, ExtensionUnloadEvent,");
            sb.AppendLine("#                    ServerStartEvent, ServerStopEvent, ServerDoneEvent,");
            sb.AppendLine("#                    ServerCrashEvent, AutoRestartEvent, BackupStartEvent,");
            sb.AppendLine("#                    BackupCompleteEvent, TaskExecuteEvent, SchedulerStartEvent,");
            sb.AppendLine("#                    SchedulerStopEvent, CommandExecuteEvent, ConfigReloadEvent,");
            sb.AppendLine("#                    PlayerJoinEvent, PlayerConnectEvent, PlayerLostEvent,");
            sb.AppendLine("#                    PlayerLeaveEvent, PlayerCommandEvent, PlayerChatEvent,");
            sb.AppendLine("#                    PlayerSetModeEvent, CustomPlayerEvent(需在player_event.customs中定义)");
            sb.AppendLine("#    enabled       - 是否启用 (true/false)");
            sb.AppendLine("#    input         - 传入脚本的参数（字符串）");
            sb.AppendLine("#");
            sb.AppendLine("#  C# 脚本要求:");
            sb.AppendLine("#    必须包含一个 Script 类，并实现 Execute 方法:");
            sb.AppendLine("#    public class Script { public void Execute(string input = \"\", string eventName = \"\") { ... } }");
            sb.AppendLine("#    玩家事件触发时, input传入事件参数JSON(含player_name, player_trigger_time等)");
            sb.AppendLine("#");
            sb.AppendLine("#  Python 脚本要求:");
            sb.AppendLine("#    需要系统安装 Python 解释器，脚本通过命令行参数接收 input 和 --event");
            sb.AppendLine("#");
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine();

            var serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            var yaml = serializer.Serialize(settings);

            var lines = yaml.Split('\n');
            bool inScripts = false;
            string currentScriptName = "";

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("scripts:"))
                {
                    inScripts = true;
                    sb.AppendLine("# 脚本列表");
                    sb.AppendLine(line.TrimEnd('\r'));
                    continue;
                }

                if (inScripts && !trimmed.StartsWith("#"))
                {
                    if (line.Length > 0 && line[0] != ' ' && line[0] != '-' && trimmed.EndsWith(":"))
                    {
                        currentScriptName = trimmed.TrimEnd(':');
                        sb.AppendLine();
                        sb.AppendLine($"# 脚本: {currentScriptName}");
                    }
                    else if (trimmed.StartsWith("file:"))
                    {
                        sb.AppendLine("# 脚本文件路径（.cs 或 .py）");
                    }
                    else if (trimmed.StartsWith("trigger_event:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 触发事件名称（留空则启动时执行一次）");
                    }
                    else if (trimmed.StartsWith("enabled:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 是否启用");
                    }
                    else if (trimmed.StartsWith("input:"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("# 传入脚本的参数");
                    }
                }

                sb.AppendLine(line.TrimEnd('\r'));
            }

            AtomicFile.WriteAllText(filePath, sb.ToString());
        }

        /// <summary>
        /// 保存默认脚本配置文件
        /// </summary>
        private static void SaveDefaultScriptsSettings(string filePath)
        {
            var defaultSettings = new ScriptsSettings
            {
                Scripts = new Dictionary<string, ScriptItem>
                {
                    ["example_cs"] = new ScriptItem
                    {
                        File = "example.cs",
                        TriggerEvent = "ProgramStartupEvent",
                        Enabled = false,
                        Input = ""
                    },
                    ["example_py"] = new ScriptItem
                    {
                        File = "example.py",
                        TriggerEvent = "ModeSelectedEvent",
                        Enabled = false,
                        Input = ""
                    }
                }
            };

            SaveScriptsSettings(defaultSettings);
        }

        /// <summary>
        /// 加载调度器配置
        /// </summary>
        public static SchedulerSettings LoadSchedulerSettings()
        {
            string filePath = Path.Combine(Config.DataPath, SchedulerSettingsFile);

            if (!File.Exists(filePath))
            {
                SaveDefaultSchedulerSettings(filePath);
                return new SchedulerSettings();
            }

            return Config.LoadYamlConfig<SchedulerSettings>(filePath, new SchedulerSettings(), "scheduler_settings.yml");
        }

        /// <summary>
        /// 加载 Support 扩展配置(support.yml)
        /// </summary>
        public static SupportSettings LoadSupportSettings()
        {
            string filePath = Path.Combine(Config.DataPath, SupportSettingsFile);

            if (!File.Exists(filePath))
            {
                SaveDefaultSupportSettings(filePath);
                return new SupportSettings();
            }

            return Config.LoadYamlConfig<SupportSettings>(filePath, new SupportSettings(), "support.yml");
        }

        /// <summary>
        /// 保存默认 Support 配置文件(support.yml)
        /// </summary>
        private static void SaveDefaultSupportSettings(string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#                     RtCli Support 扩展配置文件");
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#");
            sb.AppendLine("#  Support 扩展配置说明:");
            sb.AppendLine("#    luckperms                    - LuckPerms 权限变更监测配置");
            sb.AppendLine("#");
            sb.AppendLine("#  luckperms 配置说明:");
            sb.AppendLine("#    enabled                      - 是否启用 LuckPerms 变更监测 (true/false)");
            sb.AppendLine("#    mysql                        - MySQL 数据库连接配置");
            sb.AppendLine("#      address                    - MySQL 地址(主机:端口)");
            sb.AppendLine("#      database                   - 数据库名");
            sb.AppendLine("#      username                   - 用户名");
            sb.AppendLine("#      password                   - 密码");
            sb.AppendLine("#      table_prefix               - LuckPerms 表前缀(默认 luckperms_)");
            sb.AppendLine("#");
            sb.AppendLine("#  当 enabled=true 时，程序会轮询 LuckPerms 的 actions 表，");
            sb.AppendLine("#  检测到新记录后发布 LuckPermsChangeEvent 事件，");
            sb.AppendLine("#  该事件可用于脚本(scripts_settings.yml)和调度器(scheduler_settings.yml)的触发与条件。");
            sb.AppendLine("#");
            sb.AppendLine("#  事件变量: actor_uuid, actor_name, type, acted_uuid, acted_name, action");
            sb.AppendLine("#");
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine();

            var serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            var yaml = serializer.Serialize(new SupportSettings());

            File.WriteAllText(filePath, sb.ToString() + yaml, Encoding.UTF8);
        }

        /// <summary>
        /// 保存默认调度器配置文件
        /// </summary>
        private static void SaveDefaultSchedulerSettings(string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#                     RtCli 调度器配置文件");
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#");
            sb.AppendLine("#  调度器配置说明:");
            sb.AppendLine("#    每个任务的名称为项（键），项内包含以下属性:");
            sb.AppendLine("#    enabled        - 是否启用该任务 (true/false)");
            sb.AppendLine("#    server_keys    - 适用服务器标识列表(为空则所有服务器)");
            sb.AppendLine("#    trigger        - 触发执行条件，支持以下格式:");
            sb.AppendLine("#                      cron: \"0 */5 * * * *\"       - CRON表达式");
            sb.AppendLine("#                      every: 30                    - 每隔30分钟");
            sb.AppendLine("#                      daily: \"08:00\"               - 每天08:00");
            sb.AppendLine("#                      daily: \"08:00,20:00\"         - 每天08:00和20:00");
            sb.AppendLine("#                      date: \"2026-07-01 10:00\"     - 在指定日期时间执行");
            sb.AppendLine("#                      event: ServerStartEvent      - 在指定事件触发时执行");
            sb.AppendLine("#                      可用事件: ModeSelectedEvent, ProgramStartupEvent,");
            sb.AppendLine("#                      ProgramShutdownEvent, ServerStartEvent, ServerStopEvent,");
            sb.AppendLine("#                      ServerDoneEvent, ServerCrashEvent, AutoRestartEvent,");
            sb.AppendLine("#                      BackupStartEvent, BackupCompleteEvent, TaskExecuteEvent,");
            sb.AppendLine("#                      SchedulerStartEvent, SchedulerStopEvent,");
            sb.AppendLine("#                      CommandExecuteEvent, ConfigReloadEvent,");
            sb.AppendLine("#                      PlayerJoinEvent, PlayerConnectEvent, PlayerLostEvent,");
            sb.AppendLine("#                      PlayerLeaveEvent, PlayerCommandEvent, PlayerChatEvent,");
            sb.AppendLine("#                      PlayerSetModeEvent, CustomPlayerEvent(需在player_event.customs中定义)");
            sb.AppendLine("#                      LuckPermsChangeEvent(需在support.yml中启用LuckPerms监测)");
            sb.AppendLine("#                      组合条件: every: 30 and event: ServerStartEvent");
            sb.AppendLine("#                      组合条件: daily: \"08:00\" or event: ServerStartEvent");
            sb.AppendLine("#    condition     - 执行前置条件(trigger满足后、execute执行前检查，留空则不检查):");
            sb.AppendLine("#                      变量比较: {rt.server_running} == \"true\"");
            sb.AppendLine("#                      变量比较: {rt.player_count} >= 10");
            sb.AppendLine("#                      脚本检查: check script example_cs == \"true\"");
            sb.AppendLine("#                      文件检查: check file \"E:\\path\\file\" true  (true=需存在,false=需不存在)");
            sb.AppendLine("#                      多条件: {rt.server_running} == \"true\" and check file \"data.lock\" false");
            sb.AppendLine("#                      运算符: == != > >= < <= contains startsWith endsWith");
            sb.AppendLine("#                      可用{rt.xxx}变量: server_running, server_key, player_count,");
            sb.AppendLine("#                      uptime_minutes, tps, memory_usage_mb, cpu_usage,");
            sb.AppendLine("#                      auto_backup_enabled, scheduler_running, time, date");
            sb.AppendLine("#                      LuckPermsChangeEvent 事件变量: actor_uuid, actor_name,");
            sb.AppendLine("#                      type, acted_uuid, acted_name, action");
            sb.AppendLine("#    execute        - 执行内容:");
            sb.AppendLine("#                      script:脚本名称  - 执行Scripts中已配置的脚本");
            sb.AppendLine("#                      backup            - 执行当前服务端备份");
            sb.AppendLine("#                      restart           - 重启当前服务端");
            sb.AppendLine("#                      check_online      - 检查服务端在线情况(发送list命令)");
            sb.AppendLine("#                      check_tps         - 检查服务端TPS(需Spark等插件)");
            sb.AppendLine("#                      check_memory      - 检查服务端和程序内存使用");
            sb.AppendLine("#                      clear_drops       - 自动清理掉落物(kill @e[type=item])");
            sb.AppendLine("#                      server_stop       - 关闭服务端");
            sb.AppendLine("#                      server_restart    - 关闭并重启服务端");
            sb.AppendLine("#                      server_restart:5  - 关闭服务端5分钟后重启");
            sb.AppendLine("#                      send_command:list - 向MC服务端发送指令(给玩家发消息用tellraw)");
            sb.AppendLine("#                      command:rt        - 发送控制台命令");
            sb.AppendLine("#    pass_parameters - 是否开启参数传递(将条件产生的参数传递给脚本)");
            sb.AppendLine("#");
            sb.AppendLine("#  rules 规则配置:");
            sb.AppendLine("#    timeout_ms          - 单个任务超时时间(ms)，0=关闭");
            sb.AppendLine("#    delay_minutes       - 延迟执行(分钟)");
            sb.AppendLine("#    delay_until         - 延迟执行(指定日期时间)");
            sb.AppendLine("#    continue_tasks      - 执行完成后延续开启的其他任务标识");
            sb.AppendLine("#    execute_immediately - 任务创建后是否立即异步执行");
            sb.AppendLine("#    max_executions      - 执行n次后关闭，0=不限制");
            sb.AppendLine("#    expire_at           - 在指定日期时间关闭");
            sb.AppendLine("#    queue               - 任务队列 (Long/Fast/NoAsync)");
            sb.AppendLine("#                          Long: 长时间运行的任务");
            sb.AppendLine("#                          Fast: 短时间高调用的任务(默认)");
            sb.AppendLine("#                          NoAsync: 阻塞队列，待一个任务完成后继续下一个");
            sb.AppendLine("#");
            sb.AppendLine("#  使用 .auto on 开启调度器  .auto off 关闭  .auto list 查看任务");
            sb.AppendLine("#");
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine();

            var defaultSettings = new SchedulerSettings
            {
                Tasks = new Dictionary<string, TaskEntry>
                {
                    ["auto_backup"] = new TaskEntry
                    {
                        Enabled = false,
                        ServerKeys = new List<string>(),
                        Trigger = "every: 60",
                        Execute = "backup",
                        PassParameters = false,
                        Rules = new TaskRule
                        {
                            Queue = "Long"
                        }
                    },
                    ["restart_on_crash"] = new TaskEntry
                    {
                        Enabled = false,
                        ServerKeys = new List<string>(),
                        Trigger = "event: ServerStopEvent",
                        Execute = "restart",
                        PassParameters = false,
                        Rules = new TaskRule
                        {
                            DelayMinutes = 1,
                            Queue = "Fast"
                        }
                    },
                    ["check_status"] = new TaskEntry
                    {
                        Enabled = false,
                        ServerKeys = new List<string>(),
                        Trigger = "every: 10",
                        Execute = "check_online",
                        PassParameters = false,
                        Rules = new TaskRule
                        {
                            Queue = "Fast"
                        }
                    },
                    ["clear_items"] = new TaskEntry
                    {
                        Enabled = false,
                        ServerKeys = new List<string>(),
                        Trigger = "every: 30",
                        Execute = "clear_drops",
                        PassParameters = false,
                        Rules = new TaskRule
                        {
                            Queue = "Fast"
                        }
                    },
                    ["scheduled_restart"] = new TaskEntry
                    {
                        Enabled = false,
                        ServerKeys = new List<string>(),
                        Trigger = "daily: \"04:00\"",
                        Execute = "server_restart:1",
                        PassParameters = false,
                        Rules = new TaskRule
                        {
                            Queue = "Long"
                        }
                    }
                }
            };

            var serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            var yaml = serializer.Serialize(defaultSettings);

            var lines = yaml.Split('\n');
            string currentTaskName = "";
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (line.Length > 0 && line[0] != ' ' && line[0] != '-' && trimmed.EndsWith(":"))
                {
                    currentTaskName = trimmed.TrimEnd(':');
                    sb.AppendLine();
                    sb.AppendLine($"# 任务: {currentTaskName}");
                }
                else if (trimmed.StartsWith("enabled:"))
                {
                    sb.AppendLine("# 是否启用");
                }
                else if (trimmed.StartsWith("server_keys:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 适用服务器标识(为空则所有服务器)");
                }
                else if (trimmed.StartsWith("trigger:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 触发条件");
                }
                else if (trimmed.StartsWith("condition:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 执行前置条件(trigger满足后检查，留空则不检查)");
                }
                else if (trimmed.StartsWith("execute:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 执行内容");
                }
                else if (trimmed.StartsWith("pass_parameters:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 是否开启参数传递");
                }
                else if (trimmed.StartsWith("timeout_ms:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 超时时间(ms)，0=关闭");
                }
                else if (trimmed.StartsWith("delay_minutes:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 延迟执行(分钟)");
                }
                else if (trimmed.StartsWith("delay_until:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 延迟执行(指定日期时间)");
                }
                else if (trimmed.StartsWith("continue_tasks:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 执行完成后延续开启的任务");
                }
                else if (trimmed.StartsWith("execute_immediately:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 创建后是否立即执行");
                }
                else if (trimmed.StartsWith("max_executions:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 执行n次后关闭，0=不限制");
                }
                else if (trimmed.StartsWith("expire_at:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 过期时间");
                }
                else if (trimmed.StartsWith("queue:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 任务队列 (Long/Fast/NoAsync)");
                }

                sb.AppendLine(line.TrimEnd('\r'));
            }

            AtomicFile.WriteAllText(filePath, sb.ToString());
        }

        /// <summary>
        /// 列出程序集内嵌的资源（Content文件夹下的文件）
        /// </summary>
        public static void ListEmbeddedResources()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var names = asm.GetManifestResourceNames();

                // 筛选Content文件夹下的资源（嵌入后资源名以程序集名.Content.开头）
                var contentResources = names
                    .Where(n => n.Contains(".Content.", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (contentResources.Count == 0)
                {
                    Output.Log("没有找到内嵌资源。", 1, "ContentManager");
                    return;
                }

                var table = new Table().Border(TableBorder.Rounded).Title("[cyan]内嵌资源列表[/]");
                table.AddColumn("名称");
                table.AddColumn("大小");

                foreach (var name in contentResources)
                {
                    using var stream = asm.GetManifestResourceStream(name);
                    long size = stream?.Length ?? 0;
                    string sizeStr = size >= 1024 ? $"{size / 1024.0:F1} KB" : $"{size} B";
                    // 显示简短名称：去掉前缀，将.替换为路径分隔符
                    string displayName = name;
                    int contentIdx = displayName.IndexOf(".Content.", StringComparison.OrdinalIgnoreCase);
                    if (contentIdx >= 0)
                    {
                        displayName = displayName.Substring(contentIdx + ".Content.".Length);
                    }
                    table.AddRow($"[cyan]{Markup.Escape(displayName)}[/]", sizeStr);
                }

                AnsiConsole.Write(table);
                Output.Log("使用 [green]ct unpack <名称>[/] 释放资源到程序根目录", 1, "ContentManager");
            }
            catch (Exception ex)
            {
                Output.Log($"列出内嵌资源失败: {ex.Message}", 2, "ContentManager");
            }
        }

        /// <summary>
        /// 释放指定内嵌资源到程序根目录
        /// </summary>
        public static void UnpackEmbeddedResource(string resourceName)
        {
            if (string.IsNullOrWhiteSpace(resourceName))
            {
                Output.Log("用法: ct unpack <资源名称>", 1, "ContentManager");
                return;
            }

            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var names = asm.GetManifestResourceNames();

                // 查找匹配的资源：支持简短名称或完整名称
                string? targetName = null;

                // 先尝试精确匹配
                foreach (var name in names)
                {
                    if (string.Equals(name, resourceName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetName = name;
                        break;
                    }
                }

                // 再尝试简短名称匹配（用户输入如 Rt.dll）
                if (targetName == null)
                {
                    foreach (var name in names)
                    {
                        // 将嵌入资源名中的.替换为.但保留文件扩展名
                        // 嵌入格式: RtCli.Content.Rt.dll → 用户输入 Rt.dll
                        int contentIdx = name.IndexOf(".Content.", StringComparison.OrdinalIgnoreCase);
                        if (contentIdx >= 0)
                        {
                            string shortName = name.Substring(contentIdx + ".Content.".Length);
                            // 资源名中的目录分隔符是. 但文件名本身也含.（如Rt.dll）
                            // 所以直接比较
                            if (string.Equals(shortName, resourceName, StringComparison.OrdinalIgnoreCase))
                            {
                                targetName = name;
                                break;
                            }
                        }
                    }
                }

                // 最后尝试模糊匹配（包含）
                if (targetName == null)
                {
                    foreach (var name in names)
                    {
                        if (name.Contains(resourceName, StringComparison.OrdinalIgnoreCase))
                        {
                            targetName = name;
                            break;
                        }
                    }
                }

                if (targetName == null)
                {
                    Output.Log($"未找到资源: {Markup.Escape(resourceName)}", 2, "ContentManager");
                    Output.Log("使用 [green]ct list[/] 查看可用资源", 1, "ContentManager");
                    return;
                }

                // 计算输出文件名：取Content.之后的部分，还原为文件路径
                string outputFileName = targetName;
                int contentIdx2 = outputFileName.IndexOf(".Content.", StringComparison.OrdinalIgnoreCase);
                if (contentIdx2 >= 0)
                {
                    outputFileName = outputFileName.Substring(contentIdx2 + ".Content.".Length);
                }

                // 输出到程序根目录
                string outputPath = Path.Combine(AppContext.BaseDirectory, outputFileName);
                string? outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                using var stream = asm.GetManifestResourceStream(targetName);
                if (stream == null)
                {
                    Output.Log($"无法读取资源流: {Markup.Escape(targetName)}", 2, "ContentManager");
                    return;
                }

                using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                stream.CopyTo(fs);

                long size = stream.Length;
                string sizeStr = size >= 1024 ? $"{size / 1024.0:F1} KB" : $"{size} B";
                Output.Log($"已释放资源: [cyan]{Markup.Escape(outputFileName)}[/] → {Markup.Escape(outputPath)} ({sizeStr})", 1, "ContentManager");
            }
            catch (Exception ex)
            {
                Output.Log($"释放资源失败: {ex.Message}", 2, "ContentManager");
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

    public class BaseEntry
    {
        public string Pattern { get; set; } = "";
        public string Topic { get; set; } = "";
        public string Solution { get; set; } = "";
        public string Action { get; set; } = "";

        public BaseEntry() { }

        public BaseEntry(string pattern, string topic, string solution, string action)
        {
            Pattern = pattern;
            Topic = topic;
            Solution = solution;
            Action = action;
        }
    }

    public class AiSetting
    {
        public AiConsoleError Console_Error { get; set; } = new AiConsoleError();

        public AiServerAuto ServerAutoAi { get; set; } = new AiServerAuto();
    }

    public class AiConsoleError
    {
        public string Prompt { get; set; } = "你是一个Minecraft服务端技术支持专家。请根据提供的错误日志，简洁地分析问题原因并给出解决方案。回答格式：1.问题原因 2.解决步骤。不要输出思考过程，只输出最终回答。";

        public int MaxTokens { get; set; } = 2048;

        public string ApiEndpoint { get; set; } = "http://localhost:11434/v1/chat/completions";

        public string ApiKey { get; set; } = "";

        public string Model { get; set; } = "qwen2.5:7b";

        public double Temperature { get; set; } = 0.7;

        public int RequestTimeoutSeconds { get; set; } = 120;

        public bool RetryOnError { get; set; } = true;

        public int RetryCount { get; set; } = 3;

        public bool SaveResponse { get; set; } = true;
    }

    /// <summary>
    /// 服务端自动AI管理配置(无人自动化管理MC服务端)
    /// </summary>
    public class AiServerAuto
    {
        /// <summary>系统提示词(总)</summary>
        public string Prompt { get; set; } = "你是一个Minecraft服务端智能维护员，负责自动监测和处理服务器异常。请基于提供的日志、TPS、插件列表等信息分析问题，并在需要时调用工具采取行动。回答时严格按照要求格式输出，如需调用工具请使用 [rt:tools\"(工具名称{参数})\"] 格式。";

        /// <summary>AI输出的最大token数</summary>
        public int MaxTokens { get; set; } = 4096;

        /// <summary>AI API地址(兼容OpenAI格式)</summary>
        public string ApiEndpoint { get; set; } = "http://localhost:11434/v1/chat/completions";

        /// <summary>API密钥(本地模型如Ollama留空即可)</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>AI模型名称</summary>
        public string Model { get; set; } = "qwen2.5:7b";

        /// <summary>生成温度(0.0-2.0)</summary>
        public double Temperature { get; set; } = 0.4;

        /// <summary>请求超时时间(秒)</summary>
        public int RequestTimeoutSeconds { get; set; } = 180;

        /// <summary>异常时是否自动重连</summary>
        public bool RetryOnError { get; set; } = true;

        /// <summary>最大重试次数</summary>
        public int RetryCount { get; set; } = 3;

        /// <summary>是否保存AI回复(上下文缓存)到ai_save.yml</summary>
        public bool SaveResponse { get; set; } = true;

        /// <summary>上下文缓存最大条数(每个任务独立缓存)</summary>
        public int ContextCacheSize { get; set; } = 20;

        /// <summary>AI允许调用工具的最大轮次(防止无限循环)</summary>
        public int MaxToolRounds { get; set; } = 5;

        /// <summary>工具配置</summary>
        public AiToolsConfig Tools { get; set; } = new AiToolsConfig();

        /// <summary>技能列表</summary>
        public List<AiSkill> Skills { get; set; } = new List<AiSkill>();

        /// <summary>规则(多行字符串列表)</summary>
        public List<string> Rules { get; set; } = new List<string>
        {
            "禁止向服务器发送 /op 命令，禁止提升任何玩家为管理员。",
            "禁止删除或修改 server.properties、bukkit.yml、spigot.yml 等核心配置文件，除非用户明确授权。",
            "在执行重启服务器、关闭插件等破坏性操作前，必须先在日志中说明原因。",
            "工具调用必须严格使用 [rt:tools\"(工具名称{参数})\"] 格式，参数为JSON对象字符串。",
            "每次分析后必须输出结论和后续建议，不能只调用工具而不给结论。"
        };

        /// <summary>任务列表(键名为任务类别)</summary>
        public Dictionary<string, AiTask> Tasks { get; set; } = new Dictionary<string, AiTask>
        {
            // 1. TPS 异常监测(每小时检查一次，TPS 低于 18 时触发)
            ["tps_monitor"] = new AiTask
            {
                Enabled = true,
                Name = "服务器TPS监测",
                Trigger = "tps < 18",
                Input = new List<string> { "server_logs", "server_tps" },
                Limit = 4000,
                Prompt = "请你作为一个mc服务器智能维护员，保证服务器稳定情况下决策实在不行时再重启服务器，现在服务器的tps似乎有一些异常，请根据日志判断当前服务器是否出现什么问题，然后按照一下格式回答1.服务器发生什么 2.下次应该如何避免该问题再度发生后续还需要服务器管理员做什么 3.在该情况下你做了什么，是否重启了服务器",
                Interval = "0 0 0/1 * * ?",
                Actions = new List<string> { "ai", "tools:restart_server,get_server_log,get_server_plugin_list" }
            },

            // 2. 服务器崩溃检测(每分钟检查日志中是否出现崩溃关键字)
            ["crash_detect"] = new AiTask
            {
                Enabled = true,
                Name = "服务器崩溃检测",
                Trigger = "crash_detected",
                Input = new List<string> { "crash_report", "server_logs", "server_status" },
                Limit = 6000,
                Prompt = "请你作为一个mc服务器智能维护员，检测到服务器可能已经崩溃。请根据崩溃报告和最近的服务器日志分析以下内容：1.服务器崩溃的根本原因是什么(如内存溢出、插件异常、区块损坏等) 2.崩溃前服务器发生了什么 3.是否应该自动重启服务器，如果重启需要在日志中说明原因 4.后续服务器管理员需要做什么来避免再次崩溃。如果确认服务器已崩溃且需要重启，请调用 restart_server 工具。",
                Interval = "0 * * * * ?",
                Actions = new List<string> { "ai", "tools:restart_server,get_server_log,read_file" }
            },

            // 3. 长时间无人自动关闭(每30分钟检查，持续6小时无人时触发)
            ["idle_shutdown"] = new AiTask
            {
                Enabled = false,
                Name = "长时间无人自动关闭",
                Trigger = "idle_minutes > 360",
                Input = new List<string> { "server_players", "server_status", "server_tps" },
                Limit = 2000,
                Prompt = "请你作为一个mc服务器智能维护员，服务器已经持续6小时没有任何玩家在线。请根据当前服务器状态判断：1.当前服务器运行状态是否正常 2.是否应该关闭服务器以节省系统资源 3.如果决定关闭服务器，请使用 run_command 工具发送 stop 命令优雅关闭服务器，并在关闭前说明原因。注意：关闭服务器前请确认确实没有玩家在线，且当前不是服务器热门时间段。",
                Interval = "0 0/30 * * * ?",
                Actions = new List<string> { "ai", "tools:run_command,get_server_log" }
            },

            // 4. 服务器活跃度报告(每分钟检查，玩家全部退出后持续10分钟时触发)
            ["activity_report"] = new AiTask
            {
                Enabled = false,
                Name = "服务器活跃度报告",
                Trigger = "idle_minutes > 10",
                Input = new List<string> { "server_logs", "server_players", "server_status" },
                Limit = 8000,
                Prompt = "请你作为一个mc服务器数据分析员，服务器玩家已全部退出并持续10分钟，或服务器已关闭。请根据提供的服务器日志和状态信息生成一份服务器活跃度报告，报告需包含以下内容：1.本次运行时段的总在线人数峰值 2.玩家活跃时间段分析(从日志中提取玩家加入和离开的时间) 3.服务器热门时间段预估 4.本次运行期间服务器是否出现异常(卡顿、崩溃、警告等) 5.对服务器运营的建议(如建议在哪些时间段开放活动等)。请以清晰的报告格式输出，不要调用任何工具。",
                Interval = "0 * * * * ?",
                Actions = new List<string> { "ai" }
            },

            // 5. 服务器关闭时生成报告(服务器停止时触发)
            ["shutdown_report"] = new AiTask
            {
                Enabled = false,
                Name = "服务器关闭报告",
                Trigger = "server_stopped",
                Input = new List<string> { "server_logs", "server_status" },
                Limit = 6000,
                Prompt = "请你作为一个mc服务器数据分析员，检测到服务器已经停止运行。请根据服务器日志生成本次运行总结报告：1.服务器本次运行时长 2.运行期间是否出现异常或错误 3.运行期间的玩家活跃情况 4.服务器停止的可能原因 5.下次启动前需要检查或准备的事项。请以清晰的报告格式输出，不要调用任何工具。",
                Interval = "0 0/5 * * * ?",
                Actions = new List<string> { "ai" }
            },

            // 6. 主机资源监控(每5分钟检查，CPU或内存过高时触发分析)
            ["host_resource_monitor"] = new AiTask
            {
                Enabled = false,
                Name = "主机资源监控",
                Trigger = "cpu > 85",
                Input = new List<string> { "host_cpu", "host_memory", "server_tps", "server_logs" },
                Limit = 3000,
                Prompt = "请你作为一个mc服务器智能维护员，主机CPU使用率过高。请分析：1.当前主机CPU和内存使用情况 2.服务器TPS是否受到影响 3.是否需要重启服务器或关闭部分插件来释放资源 4.给出优化建议。如果需要关闭插件，请使用 toggle_plugin 工具；如果需要重启，请使用 restart_server 工具。",
                Interval = "0 0/5 * * ?",
                Actions = new List<string> { "ai", "tools:toggle_plugin,restart_server,get_server_plugin_list" }
            }
        };
    }

    /// <summary>
    /// AI工具配置
    /// </summary>
    public class AiToolsConfig
    {
        /// <summary>是否启用工具调用功能</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>允许使用的工具列表(为空则允许所有未在deny中的工具)</summary>
        public List<string> Allow { get; set; } = new List<string>();

        /// <summary>禁止使用的工具列表(优先级高于allow)</summary>
        public List<string> Deny { get; set; } = new List<string>();

        /// <summary>每个工具调用的提示词(键为工具名)</summary>
        public Dictionary<string, string> ToolsPrompt { get; set; } = new Dictionary<string, string>
        {
            ["read_file"] = "读取MC服务端目录中指定文件的内容。参数: {\"path\":\"相对路径\"}",
            ["get_server_plugin_list"] = "获取MC服务端的插件列表(包含启用/禁用状态)。无需参数。",
            ["get_server_log"] = "获取MC服务端最近的日志。参数: {\"lines\":100} 指定行数。",
            ["run_script"] = "运行Scripts中已配置的脚本。参数: {\"name\":\"脚本名称\",\"input\":\"参数\"}",
            ["modify_file"] = "修改MC服务端目录中文件的内容。参数: {\"path\":\"相对路径\",\"content\":\"新内容\"}",
            ["toggle_plugin"] = "启用或禁用插件(通过jar/disjar后缀切换)。参数: {\"plugin\":\"文件名\",\"disable\":true}",
            ["run_command"] = "运行程序命令或向MC服务器发送命令(禁止/op)。参数: {\"command\":\"命令内容\"}",
            ["restart_server"] = "重启MC服务端。无需参数。"
        };
    }

    /// <summary>
    /// AI技能
    /// </summary>
    public class AiSkill
    {
        /// <summary>技能名称</summary>
        public string Name { get; set; } = "";

        /// <summary>技能描述</summary>
        public string Description { get; set; } = "";

        /// <summary>技能行为(详细说明)</summary>
        public string Behavior { get; set; } = "";
    }

    /// <summary>
    /// AI自动监测任务
    /// </summary>
    public class AiTask
    {
        /// <summary>是否启用</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>任务名称/主题</summary>
        public string Name { get; set; } = "";

        /// <summary>触发条件(支持: "always"、"server_running"、"server_stopped"、"crash_detected"、
        /// "tps &lt; 18"、"cpu &gt; 85"、"memory &gt; 80"、"players == 0"、"idle_minutes &gt; 360" 等)</summary>
        public string Trigger { get; set; } = "always";

        /// <summary>输入内容数据源(支持: server_logs, app_logs, server_tps, server_plugin,
        /// host_cpu, host_memory, server_players, server_status, crash_report)</summary>
        public List<string> Input { get; set; } = new List<string> { "server_logs" };

        /// <summary>提示词/输入内容最大token量</summary>
        public int Limit { get; set; } = 4000;

        /// <summary>该分类的提示词</summary>
        public string Prompt { get; set; } = "";

        /// <summary>Cron间隔表达式(如 "0 0 0/1 * * ?" 表示每小时)</summary>
        public string Interval { get; set; } = "0 0 0/1 * * ?";

        /// <summary>执行动作列表(支持 "ai" 进行AI分析、 "tools:工具名1,工具名2" 限定可用工具)</summary>
        public List<string> Actions { get; set; } = new List<string> { "ai" };
    }

    public class TipsEntry
    {
        public string Pattern { get; set; } = "";
        public string Tips { get; set; } = "";

        public TipsEntry() { }

        public TipsEntry(string pattern, string tips)
        {
            Pattern = pattern;
            Tips = tips;
        }
    }
}
