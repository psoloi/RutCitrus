using System;
using System.IO;
using System.Text;
using RtCli.Modules;
using RtCli.Modules.Unit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Rt.Common
{
    /// <summary>
    /// Rt扩展配置模型，对应 Content/Data/rt_config.yml
    /// </summary>
    public class RtExtensionConfig
    {
        /// <summary>是否在扩展加载时自动初始化网络监测器(供其他子功能调用)</summary>
        public bool InitNetworkMonitor { get; set; } = false;

        /// <summary>全局网络通信监测器配置(抓包核心)</summary>
        public NetworkMonitorSection NetworkMonitor { get; set; } = new NetworkMonitorSection();

        /// <summary>发包频率限制子功能配置</summary>
        public PacketsLimitSection PacketsLimit { get; set; } = new PacketsLimitSection();

        /// <summary>反机器人流量监测子功能配置</summary>
        public AntibotSection Antibot { get; set; } = new AntibotSection();

        /// <summary>MC服务端外反作弊配置</summary>
        public AntiCheatSection AntiCheat { get; set; } = new AntiCheatSection();

        public StatusQuerySection StatusQuery { get; set; } = new StatusQuerySection();
    }

    public class StatusQuerySection
    {
        public string DefaultHost { get; set; } = "127.0.0.1";
        public int DefaultPort { get; set; } = 25565;
        public int TimeoutSeconds { get; set; } = 5;
    }

    /// <summary>
    /// 全局网络通信监测器配置。
    /// 此监测器为抓包核心，其他子功能(PacketsLimit/Antibot)订阅它获取数据包。
    /// </summary>
    public class NetworkMonitorSection
    {
        /// <summary>网络接口名称(留空则自动选择物理网卡，排除虚拟网卡)</summary>
        public string Interface { get; set; } = "";
        /// <summary>被监测的MC服务器IP。留空/0.0.0.0/any=通配模式(按端口匹配)</summary>
        public string ServerIp { get; set; } = "127.0.0.1";
        /// <summary>被监测的MC服务器端口</summary>
        public int ServerPort { get; set; } = 25565;
    }

    /// <summary>
    /// 发包频率限制子功能配置(原 MonitorSection)。
    /// 订阅 NetworkMonitor.PacketReceived，监测每客户端发包频率，超阈值执行动作。
    /// </summary>
    public class PacketsLimitSection
    {
        public bool Enabled { get; set; } = false;
        /// <summary>触发阈值: 客户端1秒内发包数超过此值则进入持续监测</summary>
        public int TriggerThreshold { get; set; } = 100;
        /// <summary>持续监测时间窗口(秒)</summary>
        public int MonitorWindowSeconds { get; set; } = 60;
        /// <summary>持续阈值: 监测窗口内每秒发包数超过此值记一次"超限"</summary>
        public int SustainedThreshold { get; set; } = 80;
        /// <summary>达成次数: 超限次数达到此值则触发动作</summary>
        public int SustainedCount { get; set; } = 3;
        /// <summary>触发动作({ip}替换为客户端IP，如 minecraft:ban-ip {ip})</summary>
        public string Action { get; set; } = "minecraft:ban-ip {ip}";
        /// <summary>RtCli服务端标识，留空则使用当前服务端。支持逗号分隔多个服务端。</summary>
        public string ServerKey { get; set; } = "";
    }

    public class AntibotSection
    {
        public bool Enabled { get; set; } = false;
        /// <summary>流量统计周期(秒)，每周期统计一次各IP的流量</summary>
        public int StatWindowSeconds { get; set; } = 5;
        /// <summary>触发阈值: 单个IP在一个统计周期内的流量(字节)超过此值则进入监测</summary>
        public long TrafficThreshold { get; set; } = 51200; // 50KB/5s
        /// <summary>持续监测时间窗口(秒)</summary>
        public int MonitorWindowSeconds { get; set; } = 30;
        /// <summary>在监测窗口内流量持续超过阈值的周期数达到此值则封禁</summary>
        public int SustainedCount { get; set; } = 3;
        /// <summary>封禁动作命令(不含/前缀)，{ip}会被替换为客户端IP</summary>
        public string Action { get; set; } = "ban-ip {ip}";
        /// <summary>RtCli服务端标识，留空则使用当前服务端。支持逗号分隔多个服务端。</summary>
        public string ServerKey { get; set; } = "";
        /// <summary>是否启用周期性流量通知(运行时可用 rte antibot notify 切换)</summary>
        public bool NotifyEnabled { get; set; } = true;
        /// <summary>流量通知间隔(秒)，推荐15秒</summary>
        public int NotifyIntervalSeconds { get; set; } = 15;
        /// <summary>是否启用详细数据包日志(运行时可用 rte antibot verbose 切换)</summary>
        public bool VerboseEnabled { get; set; } = false;
    }

    /// <summary>
    /// MC服务端外反作弊配置。
    /// 基于网络数据包大小+时序启发式检测作弊行为(无需协议解析, 加密模式下仍可用)。
    /// </summary>
    public class AntiCheatSection
    {
        /// <summary>是否启动反作弊监测器</summary>
        public bool Enabled { get; set; } = false;
        /// <summary>是否显示警报(alert 行动受此开关控制)</summary>
        public bool Alert { get; set; } = true;
        /// <summary>RtCli服务端标识(ban行动通过此标识发送封禁命令，留空则使用当前服务端)</summary>
        public string ServerKey { get; set; } = "";
        /// <summary>快速放方块检测(FastPlace)</summary>
        public AntiCheatDetectionSection FastPlace { get; set; } = new AntiCheatDetectionSection();
        /// <summary>快速食用检测(FastEat)</summary>
        public AntiCheatDetectionSection FastEat { get; set; } = new AntiCheatDetectionSection();
    }

    /// <summary>
    /// 单个作弊检测项配置。
    /// 每个检测项有独立的启用开关、违规阈值、衰减时间与行动定义。
    /// </summary>
    public class AntiCheatDetectionSection
    {
        /// <summary>是否开启此检测</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>
        /// 检测值，格式 "A:B"。
        /// A = 触发行动所需的违规次数(达到A次执行一次行动)
        /// B = 作弊相似度阈值(%)，当检测到相似度≥B%时计1次违规
        /// 例如 "10:45" 表示相似度≥45%时计1次违规，满10次执行行动
        /// </summary>
        public string Vl { get; set; } = "10:45";
        /// <summary>违规值清除时间(分钟)，超过此时间无新违规则计数清零。0=不清除</summary>
        public int DecayMinutes { get; set; } = 10;
        /// <summary>
        /// 行动定义(多行字符串)。每行格式: [x]action [args]
        /// x = 违规值达到此值时执行该行行动(每个阈值只执行一次)
        /// action 类型:
        ///   alert [自定义文本]  - 显示警报(受全局 alert 开关控制)
        ///                         占位符: {player} {ip} {detection} {vl} {sim} {details}
        ///   save                - 保存违规数据到 RtAC_data/data_玩家_时间.json
        ///   ban                 - 通过 /ban-ip 命令封禁玩家IP
        ///   command &lt;cmd&gt;     - 执行 RtCli 命令(支持占位符替换)
        /// 示例:
        ///   [5]alert
        ///   [10]save
        ///   [15]command say 检测到 {player} 使用 {detection}
        ///   [20]ban
        /// </summary>
        public string Actions { get; set; } = "[5]alert\n[10]save\n[15]ban";
    }

    /// <summary>
    /// Rt扩展配置管理器：负责加载/保存 Content/Data/rt_config.yml
    /// </summary>
    public static class RtConfig
    {
        private const string ConfigFileName = "rt_config.yml";
        private static readonly object _lock = new object();

        public static RtExtensionConfig Current { get; private set; } = new RtExtensionConfig();

        /// <summary>
        /// 确保配置文件存在并加载配置。若文件不存在则生成默认配置。
        /// </summary>
        public static void EnsureAndLoad()
        {
            lock (_lock)
            {
                string filePath = Path.Combine(Config.DataPath, ConfigFileName);

                if (!File.Exists(filePath))
                {
                    SaveDefault(filePath);
                    Output.Log($"已生成Rt扩展配置文件: {filePath}", 1, "Rt");
                }
                else
                {
                    Load(filePath);
                }
            }
        }

        public static void Reload()
        {
            lock (_lock)
            {
                string filePath = Path.Combine(Config.DataPath, ConfigFileName);
                if (File.Exists(filePath))
                {
                    Load(filePath);
                    Output.Log("Rt扩展配置已重新加载", 1, "Rt");
                }
                else
                {
                    SaveDefault(filePath);
                    Output.Log($"配置文件不存在，已生成默认配置: {filePath}", 1, "Rt");
                }
            }
        }

        private static void Load(string filePath)
        {
            try
            {
                var yaml = File.ReadAllText(filePath);
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .Build();
                Current = deserializer.Deserialize<RtExtensionConfig>(yaml) ?? new RtExtensionConfig();
            }
            catch (Exception ex)
            {
                Output.Log($"rt_config.yml 解析失败: {ex.Message}，使用默认配置", 2, "Rt");
                Current = new RtExtensionConfig();
            }
        }

        /// <summary>
        /// 生成带注释的默认配置文件
        /// </summary>
        private static void SaveDefault(string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#                       Rt 扩展配置文件");
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#");
            sb.AppendLine("#  init_network_monitor - 是否在扩展加载时自动初始化网络监测器(抓包核心)");
            sb.AppendLine("#                         true : 扩展加载时自动启动 NetworkMonitor");
            sb.AppendLine("#                                其他子功能(packets_limit/antibot)可立即订阅");
            sb.AppendLine("#                         false: 需手动执行 rte monitor start 启动");
            sb.AppendLine("#");
            sb.AppendLine("#  network_monitor - 全局网络通信监测器配置(抓包核心，供其他子功能调用)");
            sb.AppendLine("#    interface             - 网络接口名称(留空则自动选择物理网卡，排除虚拟网卡)");
            sb.AppendLine("#    server_ip             - 被监测的MC服务器IP");
            sb.AppendLine("#                            留空/0.0.0.0/any = 通配模式(按端口匹配)");
            sb.AppendLine("#                            若MC服务器监听0.0.0.0且对外IP非127.0.0.1，");
            sb.AppendLine("#                            请填服务器对外IP，或留空(通配-按端口匹配)");
            sb.AppendLine("#                            切勿保留默认127.0.0.1，否则无法捕获外部攻击流量");
            sb.AppendLine("#    server_port           - 被监测的MC服务器端口");
            sb.AppendLine("#");
            sb.AppendLine("#  packets_limit - 发包频率限制子功能(订阅 NetworkMonitor 数据)");
            sb.AppendLine("#    enabled               - 是否启用发包频率限制");
            sb.AppendLine("#    trigger_threshold     - 触发阈值: 客户端1秒内发包数超过此值则进入持续监测");
            sb.AppendLine("#    monitor_window_seconds- 持续监测时间窗口(秒)");
            sb.AppendLine("#    sustained_threshold   - 持续阈值: 监测窗口内每秒发包数超过此值记一次\"超限\"");
            sb.AppendLine("#    sustained_count       - 达成次数: 超限次数达到此值则触发动作");
            sb.AppendLine("#    action                - 触发动作({ip}替换为客户端IP，如 minecraft:ban-ip {ip})");
            sb.AppendLine("#    server_key            - RtCli服务端标识(对应config.yml中server_list的键名，");
            sb.AppendLine("#                            留空则使用当前服务端)，支持逗号分隔多个服务端");
            sb.AppendLine("#");
            sb.AppendLine("#  antibot - 反机器人流量监测子功能(订阅 NetworkMonitor 数据)");
            sb.AppendLine("#    enabled               - 是否启用反机器人监测");
            sb.AppendLine("#    stat_window_seconds   - 流量统计周期(秒)");
            sb.AppendLine("#    traffic_threshold     - 单IP单周期流量阈值(字节)，超过则进入监测");
            sb.AppendLine("#    monitor_window_seconds- 持续监测时间窗口(秒)");
            sb.AppendLine("#    sustained_count       - 窗口内超阈值周期数达到此值则封禁");
            sb.AppendLine("#    action                - 封禁命令({ip}替换为客户端IP)");
            sb.AppendLine("#    server_key            - RtCli服务端标识(留空则使用当前服务端，支持逗号分隔多个)");
            sb.AppendLine("#    notify_enabled        - 是否启用周期性流量通知(运行时可用 rte antibot notify 切换)");
            sb.AppendLine("#    notify_interval_seconds- 流量通知间隔(秒)，推荐15秒");
            sb.AppendLine("#    verbose_enabled       - 是否启用详细数据包日志(运行时可用 rte antibot verbose 切换)");
            sb.AppendLine("#");
            sb.AppendLine("#  anti_cheat - MC服务端外反作弊(基于数据包大小+时序启发式检测)");
            sb.AppendLine("#    enabled               - 是否启动反作弊监测器");
            sb.AppendLine("#    alert                 - 是否显示警报(alert行动受此开关控制)");
            sb.AppendLine("#    server_key            - RtCli服务端标识(ban行动发送目标，留空则使用当前服务端)");
            sb.AppendLine("#    fast_place            - 快速放方块检测(FastPlace)");
            sb.AppendLine("#    fast_eat              - 快速食用检测(FastEat)");
            sb.AppendLine("#    每个检测项子配置:");
            sb.AppendLine("#      enabled             - 是否开启此检测");
            sb.AppendLine("#      vl                  - 检测值 A:B (A=违规次数阈值, B=相似度%阈值)");
            sb.AppendLine("#                            例如 10:45 = 相似度≥45%计1次违规, 满10次执行行动");
            sb.AppendLine("#      decay_minutes       - 违规值清除时间(分钟), 0=不清除");
            sb.AppendLine("#      actions             - 行动定义(多行), 格式 [x]action [args]");
            sb.AppendLine("#                            alert [文本]  显示警报(支持 {player} {ip} {detection} {vl} {sim} {details})");
            sb.AppendLine("#                            save           保存到 RtAC_data/data_玩家_时间.json");
            sb.AppendLine("#                            ban            通过 /ban-ip 封禁IP");
            sb.AppendLine("#                            command <cmd>  执行RtCli命令(支持占位符)");
            sb.AppendLine("#");
            sb.AppendLine("#  使用 rte reload 可热重载此配置");
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine();

            var serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            var yaml = serializer.Serialize(Current);

            // 跟踪当前 section，以便为重复字段名插入正确的注释
            string currentSection = "";
            var lines = yaml.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                // 检测 section 切换 (顶层 key: 形式，无缩进)
                if (!line.StartsWith(" ") && trimmed.EndsWith(":"))
                {
                    currentSection = trimmed.TrimEnd(':').ToLowerInvariant();
                }

                // 根据 (section, field) 插入注释
                string? comment = GetComment(currentSection, trimmed);
                if (comment != null)
                {
                    sb.AppendLine(comment);
                }

                sb.AppendLine(line);
            }

            File.WriteAllText(filePath, sb.ToString());
        }

        /// <summary>根据当前 section 与字段名生成对应注释</summary>
        private static string? GetComment(string section, string trimmedLine)
        {
            // 公共字段(每个 section 的第一个字段不插入空行)
            switch (section)
            {
                case "status_query":
                    if (trimmedLine.StartsWith("default_host:")) return "# 默认查询主机";
                    if (trimmedLine.StartsWith("default_port:")) return "# 默认查询端口";
                    if (trimmedLine.StartsWith("timeout_seconds:")) return "# 查询超时时间(秒)";
                    break;

                case "network_monitor":
                    if (trimmedLine.StartsWith("interface:")) return "# 网络接口名称(如果是本地回环则选择有关loopback)(留空则自动选择)";
                    if (trimmedLine.StartsWith("server_ip:")) return "# 被监测的MC服务器IP(留空/0.0.0.0=通配按端口匹配)";
                    if (trimmedLine.StartsWith("server_port:")) return "# 被监测的MC服务器端口";
                    break;

                case "packets_limit":
                    if (trimmedLine.StartsWith("enabled:")) return "# 是否启用发包频率限制";
                    if (trimmedLine.StartsWith("trigger_threshold:")) return "# 触发阈值: 客户端1秒内发包数超过此值则进入持续监测";
                    if (trimmedLine.StartsWith("monitor_window_seconds:")) return "# 持续监测时间窗口(秒)";
                    if (trimmedLine.StartsWith("sustained_threshold:")) return "# 持续阈值: 监测窗口内每秒发包数超过此值记一次\"超限\"";
                    if (trimmedLine.StartsWith("sustained_count:")) return "# 达成次数: 超限次数达到此值则触发动作";
                    if (trimmedLine.StartsWith("action:")) return "# 触发动作 ({ip}替换为客户端IP，如 ban-ip {ip})";
                    if (trimmedLine.StartsWith("server_key:")) return "# RtCli服务端标识(留空则使用当前服务端，支持逗号分隔多个)";
                    break;

                case "antibot":
                    if (trimmedLine.StartsWith("enabled:")) return "# 是否启用反机器人监测";
                    if (trimmedLine.StartsWith("stat_window_seconds:")) return "# 流量统计周期(秒)";
                    if (trimmedLine.StartsWith("traffic_threshold:")) return "# 单IP单周期流量阈值(字节)，超过则进入监测";
                    if (trimmedLine.StartsWith("monitor_window_seconds:")) return "# 持续监测时间窗口(秒)";
                    if (trimmedLine.StartsWith("sustained_count:")) return "# 窗口内超阈值周期数达到此值则封禁";
                    if (trimmedLine.StartsWith("action:")) return "# 封禁命令({ip}替换为客户端IP)";
                    if (trimmedLine.StartsWith("server_key:")) return "# RtCli服务端标识(留空则使用当前服务端，支持逗号分隔多个)";
                    if (trimmedLine.StartsWith("notify_enabled:")) return "# 是否启用周期性流量通知(运行时可用 rte antibot notify 切换)";
                    if (trimmedLine.StartsWith("notify_interval_seconds:")) return "# 流量通知间隔(秒)，推荐15秒";
                    if (trimmedLine.StartsWith("verbose_enabled:")) return "# 是否启用详细数据包日志(运行时可用 rte antibot verbose 切换)";
                    break;

                case "anti_cheat":
                    if (trimmedLine.StartsWith("enabled:")) return "# 是否启动反作弊监测器";
                    if (trimmedLine.StartsWith("alert:")) return "# 是否显示警报(alert行动受此开关控制)";
                    if (trimmedLine.StartsWith("server_key:")) return "# RtCli服务端标识(ban行动发送目标，留空则使用当前服务端)";
                    if (trimmedLine.StartsWith("fast_place:")) return "# 快速放方块检测(FastPlace) - 1秒内放方块类发包数过多";
                    if (trimmedLine.StartsWith("fast_eat:")) return "# 快速食用检测(FastEat) - 使用物品类发包间隔过短";
                    if (trimmedLine.StartsWith("vl:")) return "# 检测值 A:B (A=违规次数阈值, B=相似度%阈值, 例如 10:45)";
                    if (trimmedLine.StartsWith("decay_minutes:")) return "# 违规值清除时间(分钟), 0=不清除";
                    if (trimmedLine.StartsWith("actions:")) return "# 行动定义(多行, [x]action [args]), 可用: alert [文本]/save/ban/command <cmd>";
                    break;
            }

            // 顶层字段
            if (string.IsNullOrEmpty(section))
            {
                if (trimmedLine.StartsWith("init_network_monitor:"))
                    return "# 是否在扩展加载时自动初始化网络监测器(true=自动启动抓包核心，false=手动rte monitor start)";
            }

            return null;
        }
    }
}
