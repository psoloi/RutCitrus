using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using RtCli.Modules.Extension;
using RtCli.Modules.Function;
using Spectre.Console;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RtCli.Modules.Unit
{
    public class ServerEntry
    {
        public string ServerName { get; set; } = "myserver";
        public string AnalyzerMode { get; set; } = "RM";
        public string WorkPath { get; set; } = "";
        public string RunServerFlags { get; set; } = "-Xms1024M -Xmx1024M -XX:+AlwaysPreTouch -XX:+DisableExplicitGC -XX:+ParallelRefProcEnabled -XX:+PerfDisableSharedMem -XX:+UnlockExperimentalVMOptions -XX:+UseG1GC -XX:G1HeapRegionSize=8M -XX:G1HeapWastePercent=5 -XX:G1MaxNewSizePercent=40 -XX:G1MixedGCCountTarget=4 -XX:G1MixedGCLiveThresholdPercent=90 -XX:G1NewSizePercent=30 -XX:G1RSetUpdatingPauseTimePercent=5 -XX:G1ReservePercent=20 -XX:InitiatingHeapOccupancyPercent=15 -XX:MaxGCPauseMillis=200 -XX:MaxTenuringThreshold=1 -XX:SurvivorRatio=32 -jar server.jar --nogui";
        public string JavaPath { get; set; } = "";
        public string RconHost { get; set; } = "127.0.0.1";
        public int RconPort { get; set; } = 25575;
        public string RconPassword { get; set; } = "";
        // RM模式配置 (需要MC 1.21.9+，服务端需开启management-server-enabled)
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
        // Bridge REST API 监听地址（默认仅本机回环；需外部访问时改为 "+" 或 "0.0.0.0"）
        public string BridgeBind { get; set; } = "localhost";

        public Dictionary<string, ServerEntry> ServerList { get; set; } = new Dictionary<string, ServerEntry>
        {
            ["default"] = new ServerEntry()
        };

        public bool EnableAutoTips { get; set; } = true;
        public List<string> DonePatterns { get; set; } = new List<string>
        {
            // s 可选: 新版(1.20+)为 Done (3.456s)!, 旧版为 Done (3.456)! — 一条即可覆盖
            @"Done \([\d.]+s?\)!",
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

        // 控制台显示过滤正则表达式列表(匹配的内容将被移除，仅影响控制台显示，不影响日志文件记录和gRPC广播)
        public List<string> ConsoleStripPatterns { get; set; } = new List<string>
        {
            @"^\[\d{2}:\d{2}:\d{2}\]\s*"
        };

        public List<string> PopularVersions { get; set; } = new List<string>
        {
            "26.1", "26.2", "26.3",
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
        // 日志前缀锚定行首，兼容三种主流格式与毫秒时间戳——
        //   vanilla/Spigot: [12:34:56] [Server thread/INFO]: ...
        //   Paper:          [12:34:56 INFO]: ...
        //   Forge/NeoForge: [12:34:56] [Server thread/INFO] [net.minecraft.../]: ...
        // 锚定行首可避免玩家在聊天里伪造 "Steve joined the game" 触发事件
        private const string LogPrefix = @"^\s*\[\s*(?<player_trigger_time>\d{1,2}:\d{2}:\d{2}(?:[.,]\d{1,3})?)[^\]]*\](?:\s*\[[^\]]+\])*\s*:";

        // 玩家名: 允许基岩版(Floodgate 的 .Name)、点号与特殊字符; 排除 <>[]" 等聊天/命令包裹符
        private const string PlayerName = @"(?<player_name>[^\s<>\[\]""']+)";

        public List<string> PlayerJoin { get; set; } = new List<string>
        {
            LogPrefix + @"\s+" + PlayerName + @"\s+joined the game\s*$"
        };

        public List<string> Connect { get; set; } = new List<string>
        {
            // player_ip 同时兼容 IPv4 与 IPv6(vanilla 将 IPv6 完整展开, 端口在末个冒号后, 由回溯取端口)
            LogPrefix + @"\s+" + PlayerName + @"\[/(?<player_ip>[\da-fA-F\.:]+):\d+\]\s+logged in\s*$"
        };

        public List<string> Lost { get; set; } = new List<string>
        {
            LogPrefix + @"\s+" + PlayerName + @"\s+lost connection(?<player_lost_reason>.*)$"
        };

        public List<string> Leaves { get; set; } = new List<string>
        {
            LogPrefix + @"\s+" + PlayerName + @"\s+left the game\s*$"
        };

        public List<string> Command { get; set; } = new List<string>
        {
            LogPrefix + @"\s+" + PlayerName + @"\s+issued server command:\s*(?<command>.*)$"
        };

        public List<string> Chat { get; set; } = new List<string>
        {
            LogPrefix + @"\s+<(?<player_name>[^>]{1,64})>\s?(?<message>.*)$"
        };

        public List<string> Setmode { get; set; } = new List<string>
        {
            // 自身改模式(消息里没有玩家名)
            LogPrefix + @"\s+Set own game mode to\s+(?<player_mode>[^.]+?)\.?\s*$",
            // 修改他人模式(如 /gamemode creative Steve)
            LogPrefix + @"\s+Set\s+" + PlayerName + @"'s\s+game mode to\s+(?<player_mode>[^.]+?)\.?\s*$"
        };

        // 自定义事件: 键名=事件名, 值=配置(patterns+parameters)
        public Dictionary<string, CustomPlayerEventConfig> Customs { get; set; } = new Dictionary<string, CustomPlayerEventConfig>
        {
            ["PlayerMoveTooFast"] = new CustomPlayerEventConfig
            {
                Patterns = new List<string>
                {
                    LogPrefix + @"\s+" + PlayerName + @"\s+moved too quickly!\s+(?<speed>-?[\d.,]+)",
                    LogPrefix + @"\s+" + PlayerName + @"\s+moved wrongly!"
                },
                Parameters = new List<string> { "speed" }
            },
            ["PlayerKick"] = new CustomPlayerEventConfig
            {
                Patterns = new List<string>
                {
                    LogPrefix + @"\s+" + PlayerName + @"\s+was kicked by\s+(?<actor>.+?):\s*(?<reason>.*)$",
                    LogPrefix + @"\s+" + PlayerName + @"\s+left the game \((?<reason>[^)]*)\)\s*$"
                },
                Parameters = new List<string> { "reason", "actor" }
            },
            ["PlayerDeath"] = new CustomPlayerEventConfig
            {
                Patterns = new List<string>
                {
                    LogPrefix + @"\s+" + PlayerName + @"\s+was (?:slain|shot|killed|blown up) by\s+(?<killer>.+?)\s*$",
                    LogPrefix + @"\s+" + PlayerName + @"\s+(?:drowned|blew up|burned to death|fell from a high place|died)\s*$"
                },
                Parameters = new List<string> { "killer" }
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
                Output.Log(I18n.Get("conf_create_data_dir", absoluteDataPath), 1, "Config");
            }

            if (!Directory.Exists(absoluteLogsPath))
            {
                Directory.CreateDirectory(absoluteLogsPath);
                Output.Log(I18n.Get("conf_create_logs_dir", absoluteLogsPath), 1, "Config");
            }

            LoadConfig();

            // 确保gRPC认证密钥有效（12字符以上）
            if (string.IsNullOrWhiteSpace(App.GrpcAuthKey) || App.GrpcAuthKey.Length < 12)
            {
                App.GrpcAuthKey = GenerateAuthKey();
                Output.Log(I18n.Get("conf_grpc_key_generated", App.GrpcAuthKey), 1, "Config");
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
                Output.Log(I18n.Get("conf_create_config_file", configPath), 1, "Config");
                return;
            }

            App = LoadYamlConfig<AppConfig>(configPath, new AppConfig(), "config.yml");

            // YAML 中「键:」这类空值会被反序列化为 null, 统一兜底, 避免后续 .Contains/.Any 触发 NullReferenceException
            App.HideConsoleServers ??= new List<string>();
            App.ConsoleStripPatterns ??= new List<string>();
            App.DonePatterns ??= new List<string>();
            App.PopularVersions ??= new List<string>();
            App.AutoBackupServers ??= new List<string>();
            App.AutoBackupIgnorePaths ??= new List<string>();
            App.AutoBackupIgnorePatterns ??= new List<string>();
            App.ServerList ??= new Dictionary<string, ServerEntry> { ["default"] = new ServerEntry() };

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


        // config.yml 逐键注释表(单一来源): 由 YamlCommentInjector 注入序列化结果,
        // 新增配置项只需在此登记一行注释, 不再写 if 分支; 键名与 YAML 序列化名一致(下划线风格)
        private static readonly YamlCommentInjector.Comment[] ConfigKeyComments =
        {
            new("check_java", "# 是否检查 Java 运行时环境", BlankLineBefore: false),
            new("check_dot_net", "# 是否检查 .NET 运行时环境"),
            new("check_python", "# 是否检查 Python 运行时环境(推荐Python 3及以上)"),
            new("check_os_bit", "# 是否检查操作系统位数 (32位/64位)"),
            new("check_update", "# 是否在启动时检查版本更新"),
            new("current_server", "# 当前选中的服务端标识(对应server_list中的键名)"),
            new("grpc_port", "# gRPC管理端口（RtPanel面板连接此端口，全局共享）"),
            new("grpc_auth_key", "# gRPC认证密钥（RtPanel登录时需输入此密钥，留空则自动生成）"),
            new("bridge_bind", "# Bridge REST API 监听地址（默认 localhost 仅本机，\"+\" 为所有网卡）"),
            new("server_list", "# 服务端列表(每个键为服务端标识，值为服务端配置)"),
            new("server_name", "# 服务器名称", BlankLineBefore: false),
            new("analyzer_mode", "# MC控制台模式 (Run/Rcon/RR/RM)"),
            new("work_path", "# MC服务端工作目录"),
            new("run_server_flags", "# JVM启动参数"),
            new("java_path", "# Java可执行文件路径(留空则使用系统默认java)"),
            new("auto_agree_eula", "# 自动同意Minecraft EULA(全局设置, 请确认你已经阅读并同意 https://www.minecraft.net/eula)"),
            new("start_run_ai", "# 启动程序后自动开启MC服务端并启动AI自动化管理(.ai命令功能)"),
            new("enable_auto_tips", "# 是否开启自动提示功能(监控日志自动显示问题提示)"),
            new("done_patterns", "# 服务端启动完成(Done)消息的正则表达式列表(留空则不提示)"),
            new("rcon_host", "# RCON服务地址"),
            new("rcon_port", "# RCON服务端口"),
            new("rcon_password", "# RCON密码"),
            new("management_host", "# 服务端管理协议地址(RM模式, 需MC 1.21.9+)"),
            new("management_port", "# 服务端管理协议端口(0=自动分配)"),
            new("management_secret", "# 服务端管理协议认证令牌(40位字母数字)"),
            new("management_tls_enabled", "# 是否启用TLS连接管理协议"),
            new("auto_restart_max_retries", "# 自动重启最大尝试次数(0=无限次)"),
            new("auto_restart", "# 服务端崩溃后是否自动重启"),
            new("auto_backup_enabled", "# 是否开启自动备份"),
            new("auto_backup_interval_minutes", "# 备份间隔(分钟)"),
            new("auto_backup_servers", "# 需要备份的服务端标识列表(为空则备份所有)"),
            new("auto_backup_path", "# 备份保存路径(留空则使用Content/Data/backup)"),
            new("auto_backup_file_name_pattern", "# 备份文件名模式({time}=时间, {server}=服务端标识)"),
            new("auto_backup_little_enabled", "# 是否开启Little增量备份(首次完整备份，后续仅备份差异文件)"),
            new("auto_backup_little_force_binary", "# 是否强制备份大小不同的二进制文件(jar/dat等，否则仅备份文本差异)"),
            new("auto_backup_ignore_paths", "# 忽略备份的目录路径(相对于工作目录，如 logs、crash-reports)"),
            new("auto_backup_ignore_patterns", "# 忽略备份的文件正则表达式列表(如 \\.log$、\\.log\\.gz$)"),
            new("hide_console_servers", "# 隐藏指定服务端标识的控制台消息(为空则不隐藏)"),
            new("console_strip_patterns", "# 控制台显示过滤正则表达式列表(匹配的内容将从控制台显示中移除，不影响日志和广播)"),
            new("popular_versions", "# 引导中显示的流行版本列表，这些版本基本支持"),
            new("skip_select", "# 跳过模式选择"),
            new("player_event", "# 玩家事件监听设置(监听控制台消息并发布事件, 正则中使用命名组提取参数)"),
            new("debug", "# 调试模式 (No)"),
        };

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
            sb.AppendLine("#    修改配置后可使用 .cfg reload 热重载生效(各配置项说明见对应键上方注释)");
            sb.AppendLine("#");
            sb.AppendLine("#  玩家事件监听 (player_event) 子项说明:");
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

            sb.Append(YamlCommentInjector.Inject(yaml, YamlCommentInjector.Section.Default(ConfigKeyComments)));

            AtomicFile.WriteAllText(configPath, sb.ToString());
        }

        public static void ReloadAll()
        {
            Output.Log(I18n.Get("conf_reloading_all"), 1, "Config");

            Intelligence.StopAutoBackup();
            Scheduler.Reload();
            LoadConfig();
            ContentManager.ReloadAll();
            Scripts.Reload();
            Support.Reload();
            Intelligence.StartAutoBackup();

            // 若AI自动化管理正在运行，则重载任务配置
            if (Intelligence.AiAutoRunner.IsRunning)
            {
                try { Intelligence.AiAutoRunner.Reload(); }
                catch (Exception ex) { Output.Debug(ex, "重载AI任务配置失败", "Config"); }
            }

            Output.Log(I18n.Get("conf_reload_done"), 1, "Config");
            EventBus.Publish(new ConfigReloadEvent());
        }

        public static void SaveCurrentConfig()
        {
            string configPath = Path.Combine(absoluteDataPath, ConfigFileName);
            SaveConfig(configPath);
            Output.Log(I18n.Get("conf_saved"), 1, "Config");
        }

        /// <summary>
        /// 通用YAML配置文件加载，解析失败时输出详细错误位置(行号/列号)并自动备份错误文件
        /// </summary>
        /// <typeparam name="T">配置类型</typeparam>
        /// <param name="filePath">配置文件完整路径</param>
        /// <param name="defaultValue">解析失败时的默认值</param>
        /// <param name="configName">配置名称(用于日志显示)</param>
        /// <returns>解析结果或默认值</returns>
        public static T LoadYamlConfig<T>(string filePath, T defaultValue, string configName) where T : new()
        {
            if (!File.Exists(filePath))
                return defaultValue;

            try
            {
                var yaml = File.ReadAllText(filePath);
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .Build();
                return deserializer.Deserialize<T>(yaml) ?? defaultValue;
            }
            catch (YamlException yex)
            {
                string location = I18n.Get("conf_yaml_error_location", yex.Start.Line, yex.Start.Column);
                string detail = yex.InnerException?.Message ?? yex.Message;
                Output.Log(I18n.Get("conf_yaml_parse_failed_at", configName, location, detail), 3, "Config");
                Output.Log(I18n.Get("conf_error_file_backed_up"), 2, "Config");
                BackupConfigFile(filePath);
                return defaultValue;
            }
            catch (Exception ex)
            {
                Output.Log(I18n.Get("conf_yaml_parse_failed", configName, ex.Message), 3, "Config");
                Output.Log(I18n.Get("conf_error_file_backed_up"), 2, "Config");
                BackupConfigFile(filePath);
                return defaultValue;
            }
        }

        /// <summary>
        /// 备份配置文件(格式: 原文件名.back (n)，自动递增编号避免覆盖)
        /// </summary>
        public static void BackupConfigFile(string filePath)
        {
            try
            {
                string dir = Path.GetDirectoryName(filePath) ?? "";
                string fileName = Path.GetFileName(filePath);

                int n = 1;
                string backupPath;
                do
                {
                    backupPath = Path.Combine(dir, $"{fileName}.back ({n})");
                    n++;
                } while (File.Exists(backupPath));

                File.Copy(filePath, backupPath, true);
                Output.Log(I18n.Get("conf_error_config_backed_up", backupPath), 2, "Config");
            }
            catch (Exception ex)
            {
                Output.Log(I18n.Get("conf_backup_config_failed", ex.Message), 3, "Config");
            }
        }

        public static void ClearContent()
        {
            string contentPath = Path.GetFullPath("Content");

            if (!Directory.Exists(contentPath))
            {
                Output.Log(I18n.Get("conf_content_not_exist"), 2, "Config");
                return;
            }

            bool confirm = Spectre.Console.AnsiConsole.Confirm(I18n.Get("conf_confirm_delete_content"), false);
            if (!confirm)
            {
                Output.Log(I18n.Get("conf_delete_cancelled"), 1, "Config");
                return;
            }

            bool confirmAgain = Spectre.Console.AnsiConsole.Confirm(I18n.Get("conf_confirm_delete_content_again"), false);
            if (!confirmAgain)
            {
                Output.Log(I18n.Get("conf_delete_cancelled"), 1, "Config");
                return;
            }

            try
            {
                int failedCount = 0;
                DeleteDirectoryRecursive(contentPath, ref failedCount);

                if (failedCount > 0)
                    Output.Log(I18n.Get("conf_content_deleted_with_failures", failedCount), 2, "Config");
                else
                    Output.Log(I18n.Get("conf_content_deleted"), 1, "Config");

                Reload.Restart();
            }
            catch (Exception ex)
            {
                Output.Log(I18n.Get("conf_delete_content_failed", ex.Message), 3, "Config");
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
            catch (Exception ex) { Output.Debug(ex, $"删除目录失败: {path}", "Config"); }
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

    /// <summary>
    /// 原子文件写入辅助：先写临时文件再原子替换目标文件，
    /// 避免写入中途崩溃/断电导致配置与数据文件(如 server_data.json、config.yml)损坏。
    /// </summary>
    public static class AtomicFile
    {
        /// <summary>统一使用 UTF-8 无 BOM: MC/Java 侧 Properties.load(InputStream) 与 Gson 对 BOM 敏感。</summary>
        public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <summary>
        /// 原子写入文本文件。目标已存在时使用 File.Replace 原子替换(保留原文件内容直到替换成功)；
        /// 平台不支持 Replace 时回退为覆盖式 Move。
        /// keepBackup=true 时把替换前的旧文件保留为 &lt;path&gt;.bak，用于文件损坏后恢复。
        /// </summary>
        public static void WriteAllText(string path, string content, Encoding? encoding = null, bool keepBackup = false)
        {
            // 显式传入 Encoding.UTF8 时其 Preamble 带 BOM, 这里统一归一化为无 BOM
            encoding ??= Utf8NoBom;
            if (encoding is UTF8Encoding)
                encoding = Utf8NoBom;

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tmpPath = path + ".tmp";
            // 用 WriteThrough + Flush(true) 强制刷盘, 确保数据真正写入磁盘后再原子替换;
            // 否则断电/崩溃(尤其 NTFS 延迟写)可能留下全 0 或半截文件, 导致下次读取时 JSON 解析失败。
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var sw = new StreamWriter(fs, encoding))
            {
                sw.Write(content);
                sw.Flush();
                fs.Flush(true);
            }

            try
            {
                if (File.Exists(path))
                {
                    // 保留旧文件为 .bak(原子替换同时产生备份), 便于损坏时恢复
                    File.Replace(tmpPath, path, keepBackup ? path + ".bak" : null);
                }
                else
                {
                    File.Move(tmpPath, path);
                }
            }
            catch (PlatformNotSupportedException)
            {
                // 部分平台不支持 Replace，回退为覆盖式移动
                File.Move(tmpPath, path, overwrite: true);
            }
        }
    }
}
