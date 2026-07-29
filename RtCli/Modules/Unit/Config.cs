using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using RtCli.Modules.Extension;
using RtCli.Modules.Function;
using Spectre.Console;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RtCli.Modules.Unit
{
    public class ServerEntry
    {
        public string ServerName { get; set; } = "myserver";
        public string AnalyzerMode { get; set; } = "Management";
        public string WorkPath { get; set; } = "";
        public string RunServerFlags { get; set; } = "-Xms1024M -Xmx1024M -XX:+AlwaysPreTouch -XX:+DisableExplicitGC -XX:+ParallelRefProcEnabled -XX:+PerfDisableSharedMem -XX:+UnlockExperimentalVMOptions -XX:+UseG1GC -XX:G1HeapRegionSize=8M -XX:G1HeapWastePercent=5 -XX:G1MaxNewSizePercent=40 -XX:G1MixedGCCountTarget=4 -XX:G1MixedGCLiveThresholdPercent=90 -XX:G1NewSizePercent=30 -XX:G1RSetUpdatingPauseTimePercent=5 -XX:G1ReservePercent=20 -XX:InitiatingHeapOccupancyPercent=15 -XX:MaxGCPauseMillis=200 -XX:MaxTenuringThreshold=1 -XX:SurvivorRatio=32 -jar server.jar --nogui";
        public string JavaPath { get; set; } = "";
        public string RconHost { get; set; } = "127.0.0.1";
        public int RconPort { get; set; } = 25575;
        public string RconPassword { get; set; } = "";
        // Management模式配置 (需要MC 1.21.9+，服务端需开启management-server-enabled)
        public string ManagementHost { get; set; } = "localhost";
        public int ManagementPort { get; set; } = 0;
        public string ManagementSecret { get; set; } = "";
        public bool ManagementTlsEnabled { get; set; } = false;
        // 崩溃自动重启
        public bool AutoRestart { get; set; } = false;
        public int AutoRestartMaxRetries { get; set; } = 3;
    }

    public class AppConfig
    {
        public bool CheckJava { get; set; } = true;
        public bool CheckDotNet { get; set; } = true;
        public bool CheckPython { get; set; } = true;
        [YamlMember(Alias = "check_os_bit")]
        public bool CheckOSBit { get; set; } = true;
        public bool CheckUpdate { get; set; } = true;
        public bool SkipSelect { get; set; } = true;

        public string CurrentServer { get; set; } = "default";

        // gRPC管理端口（RtPanel面板连接此端口，全局共享）
        public int GrpcPort { get; set; } = 7789;
        // gRPC认证密钥（RtPanel登录时需输入此密钥，留空则自动生成）
        public string GrpcAuthKey { get; set; } = "";

        public Dictionary<string, ServerEntry> ServerList { get; set; } = new Dictionary<string, ServerEntry>
        {
            ["default"] = new ServerEntry()
        };

        public bool EnableAutoTips { get; set; } = true;
        public List<string> DonePatterns { get; set; } = new List<string>
        {
            @"Done \([\d.]+s?\)!",
            @"Done \([\d.]+\)! For help",
        };

        // 自动备份
        public bool AutoBackupEnabled { get; set; } = false;
        public int AutoBackupIntervalMinutes { get; set; } = 60;
        public List<string> AutoBackupServers { get; set; } = new List<string>();
        public string AutoBackupPath { get; set; } = "";
        public string AutoBackupFileNamePattern { get; set; } = "{time}-{server}";
        // Little备份（增量差异备份）
        public bool AutoBackupLittleEnabled { get; set; } = false;
        public bool AutoBackupLittleForceBinary { get; set; } = false;
        public List<string> AutoBackupIgnorePaths { get; set; } = new List<string> { "logs", "crash-reports", "cache" };
        public List<string> AutoBackupIgnorePatterns { get; set; } = new List<string> { @"\.log\.gz$", @"\.log$", @"session\.lock$" };

        // 隐藏MC服务端控制台消息
        public List<string> HideConsoleServers { get; set; } = new List<string>();

        public List<string> PopularVersions { get; set; } = new List<string>
        {
            "26.1", "26.2",
            "1.21.11", "1.21.8", "1.21.4", "1.21.3", "1.21.1", "1.21",
            "1.20.6", "1.20.4", "1.20.2", "1.20.1",
            "1.19.4", "1.19.2",
            "1.18.2",
            "1.16.5",
            "1.12.2",
            "1.8.8",
        };

        // 自动同意EULA（全局设置，适用于所有服务端）
        public bool AutoAgreeEula { get; set; } = false;

        // 启动程序后自动开启服务端并启动AI自动化管理
        public bool StartRunAi { get; set; } = false;

        // 玩家事件监听
        public PlayerEventConfig PlayerEvent { get; set; } = new PlayerEventConfig();

        public string Debug { get; set; } = "No";
    }

    /// <summary>
    /// 玩家事件监听配置
    /// </summary>
    public class PlayerEventConfig
    {
        public bool Enabled { get; set; } = false;
        public int Ticks { get; set; } = 10;

        // 各事件的正则表达式列表（支持多个，匹配任意一个）
        // 使用命名组提取参数: player_name, player_trigger_time, player_ip, player_lost_reason, player_mode
        public List<string> PlayerJoin { get; set; } = new List<string>
        {
            @"\[(?<player_trigger_time>[\d:]+)\][^\]]*:\s+(?<player_name>\w+)\s+joined the game"
        };

        public List<string> Connect { get; set; } = new List<string>
        {
            @"\[(?<player_trigger_time>[\d:]+)\][^\]]*:\s+(?<player_name>\w+)\[/(?<player_ip>[\d\.]+):\d+\]\s+logged in"
        };

        public List<string> Lost { get; set; } = new List<string>
        {
            @"\[(?<player_trigger_time>[\d:]+)\][^\]]*:\s+(?<player_name>\w+)\s+lost connection(?<player_lost_reason>.*)"
        };

        public List<string> Leaves { get; set; } = new List<string>
        {
            @"\[(?<player_trigger_time>[\d:]+)\][^\]]*:\s+(?<player_name>\w+)\s+left the game"
        };

        public List<string> Command { get; set; } = new List<string>
        {
            @"\[(?<player_trigger_time>[\d:]+)\][^\]]*:\s+(?<player_name>\w+)\s+issued server command:\s*(?<command>.*)"
        };

        public List<string> Chat { get; set; } = new List<string>
        {
            @"\[(?<player_trigger_time>[\d:]+)\][^\]]*:\s+<(?<player_name>\w+)>\s*(?<message>.*)"
        };

        public List<string> Setmode { get; set; } = new List<string>
        {
            @"\[(?<player_trigger_time>[\d:]+)\][^\]]*:\s+(?<player_name>\w+)\s+Set own game mode to\s+(?<player_mode>\w+ Mode)"
        };

        // 自定义事件: 键名=事件名, 值=配置(patterns+parameters)
        public Dictionary<string, CustomPlayerEventConfig> Customs { get; set; } = new Dictionary<string, CustomPlayerEventConfig>
        {
            ["PlayerMoveTooFast"] = new CustomPlayerEventConfig
            {
                Patterns = new List<string>
                {
                    @"\[(?<player_trigger_time>[\d:]+)\][^\]]*:\s+(?<player_name>\w+)\s+moved too quickly!\s+(?<speed>[\d.,]+)",
                    @"\[(?<player_trigger_time>[\d:]+)\][^\]]*:\s+(?<player_name>\w+)\s+moved wrongly!"
                },
                Parameters = new List<string> { "speed" }
            }
        };
    }

    /// <summary>
    /// 自定义玩家事件配置
    /// </summary>
    public class CustomPlayerEventConfig
    {
        public List<string> Patterns { get; set; } = new List<string>();
        // 参数名列表，对应正则表达式中的命名组名
        public List<string> Parameters { get; set; } = new List<string>();
    }

    public static class Config
    {
        private static readonly string DataDirectory = "Content/Data";
        private static readonly string LogsDirectory = "Content/Logs";
        private static readonly string absoluteDataPath = Path.GetFullPath(DataDirectory);
        private static readonly string absoluteLogsPath = Path.GetFullPath(LogsDirectory);
        private static readonly string ConfigFileName = "config.yml";
        private static bool _isInitialized = false;

        public static string DataPath => absoluteDataPath;
        public static string LogsPath => absoluteLogsPath;
        public static AppConfig App { get; private set; } = new AppConfig();

        /// <summary>
        /// 获取当前选中的服务端配置项
        /// </summary>
        public static ServerEntry CurrentServer
        {
            get
            {
                var key = App.CurrentServer;
                if (!string.IsNullOrEmpty(key) && App.ServerList.TryGetValue(key, out var entry))
                    return entry;
                // 回退到第一个可用项
                if (App.ServerList.Count > 0)
                    return App.ServerList.First().Value;
                return new ServerEntry();
            }
        }

        /// <summary>
        /// 切换当前服务端
        /// </summary>
        public static bool SwitchServer(string identifier)
        {
            if (!App.ServerList.ContainsKey(identifier))
                return false;
            App.CurrentServer = identifier;
            return true;
        }

        /// <summary>
        /// 获取或创建服务端配置项
        /// </summary>
        public static ServerEntry GetOrCreateServer(string identifier)
        {
            if (!App.ServerList.ContainsKey(identifier))
            {
                App.ServerList[identifier] = new ServerEntry();
            }
            return App.ServerList[identifier];
        }

        public static void Initialize()
        {
            if (_isInitialized) return;

            if (!Directory.Exists(absoluteDataPath))
            {
                Directory.CreateDirectory(absoluteDataPath);
                Output.Log($"创建数据目录: {absoluteDataPath}", 1, "Config");
            }

            if (!Directory.Exists(absoluteLogsPath))
            {
                Directory.CreateDirectory(absoluteLogsPath);
                Output.Log($"创建日志目录: {absoluteLogsPath}", 1, "Config");
            }

            LoadConfig();

            // 确保gRPC认证密钥有效（12字符以上）
            if (string.IsNullOrWhiteSpace(App.GrpcAuthKey) || App.GrpcAuthKey.Length < 12)
            {
                App.GrpcAuthKey = GenerateAuthKey();
                Output.Log($"已生成gRPC认证密钥: {App.GrpcAuthKey}", 1, "Config");
                SaveCurrentConfig();
            }

            _isInitialized = true;
        }

        private static void LoadConfig()
        {
            string configPath = Path.Combine(absoluteDataPath, ConfigFileName);

            if (!File.Exists(configPath))
            {
                SaveConfig(configPath);
                Output.Log($"创建配置文件: {configPath}", 1, "Config");
                return;
            }

            try
            {
                var yaml = File.ReadAllText(configPath);
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .Build();
                App = deserializer.Deserialize<AppConfig>(yaml) ?? new AppConfig();
            }
            catch
            {
                Output.Log("配置文件解析失败，使用默认配置", 2, "Config");
                App = new AppConfig();
            }

            // 确保当前服务端标识有效
            if (!App.ServerList.ContainsKey(App.CurrentServer))
            {
                if (App.ServerList.Count > 0)
                    App.CurrentServer = App.ServerList.First().Key;
                else
                {
                    App.ServerList["default"] = new ServerEntry();
                    App.CurrentServer = "default";
                }
            }
        }


        private static void SaveConfig(string configPath)
        {
            var serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            var yaml = serializer.Serialize(App);

            var sb = new StringBuilder();
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#                          RtCli 配置文件 - " + $"{Program.RtCliVersion}");
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#");
            sb.AppendLine("#  说明:");
            sb.AppendLine("#    该程序配置文件如果不清晰，未来将会考虑Wiki制作");
            sb.AppendLine("#    如果你不知道那些项有什么作用，请考虑使用RtPanel来设置");
            sb.AppendLine("#    修改配置后可使用 .cfg reload 热重载生效");
            sb.AppendLine("#");
            sb.AppendLine("#  配置项说明:");
            sb.AppendLine("#    check_java      - 是否在启动时检查 Java 运行时环境 (true/false)");
            sb.AppendLine("#    check_dot_net   - 是否在启动时检查 .NET 运行时环境 (true/false)");
            sb.AppendLine("#    check_python    - 是否在启动时检查 Python 运行时环境 (true/false)");
            sb.AppendLine("#    check_os_bit    - 是否检查操作系统位数 (true/false)");
            sb.AppendLine("#    check_update    - 是否在启动时检查版本更新 (true/false)");
            sb.AppendLine("#    skip_select     - 是否跳过模式选择界面直接进入默认模式 (true/false)");
            sb.AppendLine("#    current_server  - 当前选中的服务端标识(对应server_list中的键名)");
            sb.AppendLine("#    grpc_port       - gRPC管理端口（RtPanel面板连接此端口，全局共享）");
            sb.AppendLine("#    grpc_auth_key   - gRPC认证密钥（RtPanel登录时需输入此密钥，留空则自动生成）");
            sb.AppendLine("#");
            sb.AppendLine("#  server_list 中的每个服务端配置:");
            sb.AppendLine("#    server_name       - 服务器名称，用于标识");
            sb.AppendLine("#    analyzer_mode     - MC控制台模式:");
            sb.AppendLine("#                          Run: 启动MC服务端作为子进程，通过stdin发送命令");
            sb.AppendLine("#                          Rcon: 启动MC服务端作为子进程读取日志 + RCON发送命令(推荐)");
            sb.AppendLine("#                          OnlyRcon: 连接已运行的MC服务端(日志文件+RCON)");
            sb.AppendLine("#                          Management: 启动MC服务端读取日志 + 服务端管理协议(推荐)");
            sb.AppendLine("#                                      需要MC 1.21.9+，服务端需开启management-server-enabled");
            sb.AppendLine("#    work_path         - MC服务端的工作目录路径");
            sb.AppendLine("#    run_server_flags  - 启动MC服务端的JVM参数");
            sb.AppendLine("#    java_path         - Java可执行文件路径(留空则使用系统默认java)");
            sb.AppendLine("#    rcon_host         - RCON服务地址");
            sb.AppendLine("#    rcon_port         - RCON服务端口");
            sb.AppendLine("#    rcon_password     - RCON密码");
            sb.AppendLine("#    management_host   - 服务端管理协议地址(Management模式, 需MC 1.21.9+)");
            sb.AppendLine("#    management_port   - 服务端管理协议端口(0=自动, Management模式)");
            sb.AppendLine("#    management_secret - 服务端管理协议认证令牌(40位字母数字)");
            sb.AppendLine("#    management_tls_enabled - 是否启用TLS连接管理协议");
            sb.AppendLine("#    auto_restart      - 服务端崩溃后是否自动重启 (true/false)");
            sb.AppendLine("#    auto_restart_max_retries - 自动重启最大尝试次数 (0=无限次)");
            sb.AppendLine("#");
            sb.AppendLine("#  自动备份配置:");
            sb.AppendLine("#    auto_backup_enabled - 是否开启自动备份 (true/false)");
            sb.AppendLine("#    auto_backup_interval_minutes - 备份间隔(分钟)");
            sb.AppendLine("#    auto_backup_servers - 需要备份的服务端标识列表(为空则备份所有)");
            sb.AppendLine("#    auto_backup_path - 备份保存路径(留空则使用Content/Data/backup)");
            sb.AppendLine("#    auto_backup_file_name_pattern - 备份文件名模式({time}=时间, {server}=服务端标识)");
            sb.AppendLine("#    auto_backup_little_enabled - 是否开启Little增量备份 (true/false)");
            sb.AppendLine("#    auto_backup_little_force_binary - 是否强制备份大小不同的二进制文件 (true/false)");
            sb.AppendLine("#    auto_backup_ignore_paths - 忽略备份的目录路径(相对于工作目录)");
            sb.AppendLine("#    auto_backup_ignore_patterns - 忽略备份的文件正则表达式列表");
            sb.AppendLine("#");
            sb.AppendLine("#  隐藏控制台消息:");
            sb.AppendLine("#    hide_console_servers - 隐藏指定服务端标识的控制台消息(为空则不隐藏)");
            sb.AppendLine("#");
            sb.AppendLine("#  EULA设置:");
            sb.AppendLine("#    auto_agree_eula - 自动同意Minecraft EULA (true/false, 全局设置)");
            sb.AppendLine("#    start_run_ai    - 启动程序后自动开启MC服务端并启动AI自动化管理 (true/false)");
            sb.AppendLine("#");
            sb.AppendLine("#  玩家事件监听 (player_event):");
            sb.AppendLine("#    enabled  - 是否启用控制台消息监听并发布玩家事件 (true/false)");
            sb.AppendLine("#    ticks    - 每次监听的延迟(ms, 推荐10, 人数越多建议越小)");
            sb.AppendLine("#    player_join  - 玩家加入事件正则列表(传递: player_name, player_trigger_time)");
            sb.AppendLine("#    connect      - 玩家连接事件正则列表(额外传递: player_ip)");
            sb.AppendLine("#    lost         - 玩家断开事件正则列表(额外传递: player_lost_reason)");
            sb.AppendLine("#    leaves       - 玩家离开事件正则列表(传递: player_name, player_trigger_time)");
            sb.AppendLine("#    command      - 玩家命令事件正则列表(额外传递: command)");
            sb.AppendLine("#    chat         - 玩家聊天事件正则列表(额外传递: message)");
            sb.AppendLine("#    setmode      - 玩家切换模式事件正则列表(额外传递: player_mode)");
            sb.AppendLine("#    customs      - 自定义事件(键名=事件名, 值含patterns正则列表和parameters参数名列表)");
            sb.AppendLine("#    正则表达式中使用命名组提取参数, 如 (?<player_name>\\w+) 等");
            sb.AppendLine("#");
            sb.AppendLine("#  使用 .server change <标识> 切换当前服务端");
            sb.AppendLine("#  使用 .server list 查看所有服务端  .server add 添加  .server del 删除");
            sb.AppendLine("#");
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine();

            var lines = yaml.Split('\n');
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (trimmedLine.StartsWith("check_java:"))
                {
                    sb.AppendLine("# 是否检查 Java 运行时环境");
                }
                else if (trimmedLine.StartsWith("check_dot_net:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 是否检查 .NET 运行时环境");
                }
                else if (trimmedLine.StartsWith("check_python:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 是否检查 Python 运行时环境(推荐Python 3及以上)");
                }
                else if (trimmedLine.StartsWith("check_os_bit:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 是否检查操作系统位数 (32位/64位)");
                }
                else if (trimmedLine.StartsWith("check_update:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 是否在启动时检查版本更新");
                }
                else if (trimmedLine.StartsWith("current_server:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 当前选中的服务端标识(对应server_list中的键名)");
                }
                else if (trimmedLine.StartsWith("grpc_port:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# gRPC管理端口（RtPanel面板连接此端口，全局共享）");
                }
                else if (trimmedLine.StartsWith("grpc_auth_key:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# gRPC认证密钥（RtPanel登录时需输入此密钥，留空则自动生成）");
                }
                else if (trimmedLine.StartsWith("server_list:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 服务端列表(每个键为服务端标识，值为服务端配置)");
                }
                else if (trimmedLine.StartsWith("server_name:"))
                {
                    sb.AppendLine("# 服务器名称");
                }
                else if (trimmedLine.StartsWith("analyzer_mode:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# MC控制台模式 (Run/Rcon/OnlyRcon/Management)");
                }
                else if (trimmedLine.StartsWith("work_path:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# MC服务端工作目录");
                }
                else if (trimmedLine.StartsWith("run_server_flags:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# JVM启动参数");
                }
                else if (trimmedLine.StartsWith("java_path:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# Java可执行文件路径(留空则使用系统默认java)");
                }
                else if (trimmedLine.StartsWith("auto_agree_eula:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 自动同意Minecraft EULA(全局设置, 请确认你已经阅读并同意 https://www.minecraft.net/eula)");
                }
                else if (trimmedLine.StartsWith("start_run_ai:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 启动程序后自动开启MC服务端并启动AI自动化管理(.ai命令功能)");
                }
                else if (trimmedLine.StartsWith("enable_auto_tips:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 是否开启自动提示功能(监控日志自动显示问题提示)");
                }
                else if (trimmedLine.StartsWith("done_patterns:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 服务端启动完成(Done)消息的正则表达式列表(留空则不提示)");
                }
                else if (trimmedLine.StartsWith("rcon_host:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# RCON服务地址");
                }
                else if (trimmedLine.StartsWith("rcon_port:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# RCON服务端口");
                }
                else if (trimmedLine.StartsWith("rcon_password:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# RCON密码");
                }
                else if (trimmedLine.StartsWith("management_host:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 服务端管理协议地址(Management模式, 需MC 1.21.9+)");
                }
                else if (trimmedLine.StartsWith("management_port:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 服务端管理协议端口(0=自动分配)");
                }
                else if (trimmedLine.StartsWith("management_secret:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 服务端管理协议认证令牌(40位字母数字)");
                }
                else if (trimmedLine.StartsWith("management_tls_enabled:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 是否启用TLS连接管理协议");
                }
                else if (trimmedLine.StartsWith("auto_restart_max_retries:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 自动重启最大尝试次数(0=无限次)");
                }
                else if (trimmedLine.StartsWith("auto_restart:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 服务端崩溃后是否自动重启");
                }
                else if (trimmedLine.StartsWith("auto_backup_enabled:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 是否开启自动备份");
                }
                else if (trimmedLine.StartsWith("auto_backup_interval_minutes:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 备份间隔(分钟)");
                }
                else if (trimmedLine.StartsWith("auto_backup_servers:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 需要备份的服务端标识列表(为空则备份所有)");
                }
                else if (trimmedLine.StartsWith("auto_backup_path:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 备份保存路径(留空则使用Content/Data/backup)");
                }
                else if (trimmedLine.StartsWith("auto_backup_file_name_pattern:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 备份文件名模式({time}=时间, {server}=服务端标识)");
                }
                else if (trimmedLine.StartsWith("auto_backup_little_enabled:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 是否开启Little增量备份(首次完整备份，后续仅备份差异文件)");
                }
                else if (trimmedLine.StartsWith("auto_backup_little_force_binary:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 是否强制备份大小不同的二进制文件(jar/dat等，否则仅备份文本差异)");
                }
                else if (trimmedLine.StartsWith("auto_backup_ignore_paths:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 忽略备份的目录路径(相对于工作目录，如 logs、crash-reports)");
                }
                else if (trimmedLine.StartsWith("auto_backup_ignore_patterns:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 忽略备份的文件正则表达式列表(如 \\.log$、\\.log\\.gz$)");
                }
                else if (trimmedLine.StartsWith("hide_console_servers:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 隐藏指定服务端标识的控制台消息(为空则不隐藏)");
                }
                else if (trimmedLine.StartsWith("popular_versions:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 引导中显示的流行版本列表，这些版本基本支持");
                }
                else if (trimmedLine.StartsWith("skip_select"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 跳过模式选择");
                }
                else if (trimmedLine.StartsWith("player_event:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 玩家事件监听设置(监听控制台消息并发布事件, 正则中使用命名组提取参数)");
                }
                else if (trimmedLine.StartsWith("debug:"))
                {
                    sb.AppendLine();
                    sb.AppendLine("# 调试模式 (No)");
                }

                sb.AppendLine(line.TrimEnd('\r'));
            }

            File.WriteAllText(configPath, sb.ToString());
        }

        public static void ReloadAll()
        {
            Output.Log("正在热重载所有配置文件...", 1, "Config");

            Intelligence.StopAutoBackup();
            Scheduler.Reload();
            LoadConfig();
            ContentManager.ReloadAll();
            Scripts.Reload();
            Intelligence.StartAutoBackup();

            // 若AI自动化管理正在运行，则重载任务配置
            if (Intelligence.AiAutoRunner.IsRunning)
            {
                try { Intelligence.AiAutoRunner.Reload(); } catch { }
            }

            Output.Log("所有配置文件已热重载完成。", 1, "Config");
            EventBus.Publish(new ConfigReloadEvent());
        }

        public static void SaveCurrentConfig()
        {
            string configPath = Path.Combine(absoluteDataPath, ConfigFileName);
            SaveConfig(configPath);
            Output.Log("配置文件已保存。", 1, "Config");
        }

        public static void ClearContent()
        {
            string contentPath = Path.GetFullPath("Content");

            if (!Directory.Exists(contentPath))
            {
                Output.Log("Content 文件夹不存在。", 2, "Config");
                return;
            }

            bool confirm = Spectre.Console.AnsiConsole.Confirm("[red]确定要删除 Content 文件夹吗？这将清除所有配置、数据和脚本。[/]", false);
            if (!confirm)
            {
                Output.Log("已取消删除。", 1, "Config");
                return;
            }

            bool confirmAgain = Spectre.Console.AnsiConsole.Confirm("[red]再次确认：删除 Content 文件夹后程序将冷重载，是否继续？[/]", false);
            if (!confirmAgain)
            {
                Output.Log("已取消删除。", 1, "Config");
                return;
            }

            try
            {
                int failedCount = 0;
                DeleteDirectoryRecursive(contentPath, ref failedCount);

                if (failedCount > 0)
                    Output.Log($"Content 文件夹已删除（{failedCount} 个文件被占用无法删除），正在冷重载...", 2, "Config");
                else
                    Output.Log("Content 文件夹已删除，正在冷重载...", 1, "Config");

                Reload.Restart();
            }
            catch (Exception ex)
            {
                Output.Log($"删除 Content 文件夹失败: {ex.Message}，正在冷重载...", 3, "Config");
                Reload.Restart();
            }
        }

        private static void DeleteDirectoryRecursive(string path, ref int failedCount)
        {
            foreach (var file in Directory.GetFiles(path))
            {
                try { File.Delete(file); }
                catch { failedCount++; }
            }

            foreach (var dir in Directory.GetDirectories(path))
            {
                DeleteDirectoryRecursive(dir, ref failedCount);
            }

            try { Directory.Delete(path, false); }
            catch { }
        }

        private static string GenerateAuthKey()
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var random = new Random();
            var key = new char[24];
            for (int i = 0; i < key.Length; i++)
            {
                key[i] = chars[random.Next(chars.Length)];
            }
            return new string(key);
        }
    }
}
