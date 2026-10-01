using System;
using System.Collections.Generic;
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

        /// <summary>数据包事件功能配置</summary>
        public PacketEventsSection PacketEvents { get; set; } = new PacketEventsSection();

        public StatusQuerySection StatusQuery { get; set; } = new StatusQuerySection();
    }

    /// <summary>
    /// MC 服务器状态查询配置。
    /// rte get 使用 DefaultHost/DefaultPort/TimeoutSeconds;
    /// 开启 Enabled 后按 IntervalSeconds 周期查询 Servers 列表并发布 ServerStatusEvent。
    /// </summary>
    public class StatusQuerySection
    {
        public string DefaultHost { get; set; } = "127.0.0.1";
        public int DefaultPort { get; set; } = 25565;
        public int TimeoutSeconds { get; set; } = 5;

        /// <summary>是否启用周期性多服务器状态查询</summary>
        public bool Enabled { get; set; } = false;
        /// <summary>状态查询间隔(秒)</summary>
        public int IntervalSeconds { get; set; } = 60;
        /// <summary>被监测的服务器列表(为空时回退到 DefaultHost/DefaultPort, 标识为 default)</summary>
        public List<StatusQueryServer> Servers { get; set; } = new List<StatusQueryServer>();
    }

    /// <summary>单个被监测的 MC 服务器</summary>
    public class StatusQueryServer
    {
        /// <summary>服务器标识(事件返回, 建议仅使用字母/数字/下划线, 供表达式 server_&lt;id&gt;_online 使用)</summary>
        public string Id { get; set; } = "";
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 25565;
        /// <summary>是否监测该服务器</summary>
        public bool Enabled { get; set; } = true;
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
    /// 数据包事件功能配置。
    /// 订阅 NetworkMonitor.PacketReceived，按 tick 周期聚合客户端/服务端数据包，
    /// 解码后通过 EventBus 发布 PacketClientEvent / PacketServerEvent。
    /// </summary>
    public class PacketEventsSection
    {
        /// <summary>是否启用数据包事件功能</summary>
        public bool Enabled { get; set; } = false;
        /// <summary>客户端包事件检测并触发周期(毫秒)</summary>
        public int ClientEventTick { get; set; } = 1000;
        /// <summary>服务端包事件检测并触发周期(毫秒)</summary>
        public int ServerEventTick { get; set; } = 1000;
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
            File.WriteAllText(filePath, BuildDefaultConfigText());
        }

        /// <summary>
        /// 构建默认配置文件的完整文本(文件头说明 + 序列化结果 + 逐键注释)。
        /// 与文件写入分离, 便于比对生成结果。
        /// </summary>
        internal static string BuildDefaultConfigText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#                       Rt 扩展配置文件");
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#");
            sb.AppendLine("#  本文件由 RtConfig 自动生成: 每个配置项的作用见其上方注释。");
            sb.AppendLine("#  network_monitor 为抓包核心, packets_limit / antibot / packet_events");
            sb.AppendLine("#  均订阅它捕获的数据包, 因此需先保证 network_monitor 的监听参数正确。");
            sb.AppendLine("#  status_query 用于 rte get 的默认地址与周期多服务器状态事件(ServerStatusEvent)。");
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

                // 根据 (section, field) 插入注释(注释文本可含换行, 逐行写出)
                string? comment = GetComment(currentSection, trimmed);
                if (comment != null)
                {
                    foreach (var commentLine in comment.Split('\n'))
                    {
                        sb.AppendLine(commentLine);
                    }
                }

                sb.AppendLine(line);
            }

            return sb.ToString();
        }
        /// <summary>
        /// 逐键注释表(单一来源): 键名 → 注释文本, 文本内可用 \n 表示多行。
        /// 键名格式: 子节字段用 "section.field"(如 "antibot.action");
        ///           section 自身的说明用节名(如 "network_monitor");
        ///           顶层字段直接用字段名(如 "init_network_monitor")。
        /// 新增配置项只需在此登记一行, 不再需要改动注释逻辑。
        /// </summary>
        private static readonly Dictionary<string, string> KeyComments = new Dictionary<string, string>
        {
            // ---- 顶层字段 ----
            ["init_network_monitor"] =
                "# 是否在扩展加载时自动初始化网络监测器(true=自动启动抓包核心，false=手动rte monitor start)\n" +
                "#   true 时其他子功能(packets_limit/antibot)可立即订阅网络监测数据",

            // ---- network_monitor ----
            ["network_monitor"] = "# 全局网络通信监测器配置(抓包核心，供其他子功能调用)",
            ["network_monitor.interface"] =
                "# 网络接口名称(如果是本地回环则选择有关loopback)(留空则自动选择)\n" +
                "#   留空时自动选择物理网卡并排除虚拟网卡",
            ["network_monitor.server_ip"] =
                "# 被监测的MC服务器IP(留空/0.0.0.0=通配按端口匹配)\n" +
                "#   若MC服务器监听0.0.0.0且对外IP非127.0.0.1，请填服务器对外IP，\n" +
                "#   或留空(通配-按端口匹配)\n" +
                "#   切勿保留默认127.0.0.1，否则无法捕获外部攻击流量",
            ["network_monitor.server_port"] = "# 被监测的MC服务器端口",

            // ---- packets_limit ----
            ["packets_limit"] = "# 发包频率限制子功能(订阅 NetworkMonitor 数据)",
            ["packets_limit.enabled"] = "# 是否启用发包频率限制",
            ["packets_limit.trigger_threshold"] = "# 触发阈值: 客户端1秒内发包数超过此值则进入持续监测",
            ["packets_limit.monitor_window_seconds"] = "# 持续监测时间窗口(秒)",
            ["packets_limit.sustained_threshold"] = "# 持续阈值: 监测窗口内每秒发包数超过此值记一次\"超限\"",
            ["packets_limit.sustained_count"] = "# 达成次数: 超限次数达到此值则触发动作",
            ["packets_limit.action"] = "# 触发动作 ({ip}替换为客户端IP，如 ban-ip {ip})",
            ["packets_limit.server_key"] =
                "# RtCli服务端标识(留空则使用当前服务端，支持逗号分隔多个)\n" +
                "#   对应 config.yml 中 server_list 的键名",

            // ---- antibot ----
            ["antibot"] = "# 反机器人流量监测子功能(订阅 NetworkMonitor 数据)",
            ["antibot.enabled"] = "# 是否启用反机器人监测",
            ["antibot.stat_window_seconds"] = "# 流量统计周期(秒)",
            ["antibot.traffic_threshold"] = "# 单IP单周期流量阈值(字节)，超过则进入监测",
            ["antibot.monitor_window_seconds"] = "# 持续监测时间窗口(秒)",
            ["antibot.sustained_count"] = "# 窗口内超阈值周期数达到此值则封禁",
            ["antibot.action"] = "# 封禁命令({ip}替换为客户端IP)",
            ["antibot.server_key"] = "# RtCli服务端标识(留空则使用当前服务端，支持逗号分隔多个)",
            ["antibot.notify_enabled"] = "# 是否启用周期性流量通知(运行时可用 rte antibot notify 切换)",
            ["antibot.notify_interval_seconds"] = "# 流量通知间隔(秒)，推荐15秒",
            ["antibot.verbose_enabled"] = "# 是否启用详细数据包日志(运行时可用 rte antibot verbose 切换)",

            // ---- packet_events ----
            ["packet_events"] = "# 数据包事件功能(订阅 NetworkMonitor 数据并按 tick 周期发布事件)",
            ["packet_events.enabled"] = "# 是否启用数据包事件功能",
            ["packet_events.client_event_tick"] =
                "# 客户端包事件检测并触发周期(毫秒)\n" +
                "#   一个周期内客户端→服务端数据包发生变化时触发 PacketClientEvent",
            ["packet_events.server_event_tick"] =
                "# 服务端包事件检测并触发周期(毫秒)\n" +
                "#   一个周期内服务端→客户端数据包发生变化时触发 PacketServerEvent",

            // ---- status_query ----
            ["status_query"] =
                "# 服务器状态查询(供 rte get 与周期性状态事件使用)\n" +
                "#   enabled=true 时按 interval_seconds 周期查询 servers 列表并发布 ServerStatusEvent",
            ["status_query.default_host"] = "# 默认查询主机(servers 为空时使用)",
            ["status_query.default_port"] = "# 默认查询端口(servers 为空时使用)",
            ["status_query.timeout_seconds"] = "# 单次查询超时时间(秒)",
            ["status_query.enabled"] = "# 是否启用周期性多服务器状态查询(rte status start/stop 可在运行时切换)",
            ["status_query.interval_seconds"] = "# 状态查询间隔(秒), 每次查询全部服务器并发布一次 ServerStatusEvent",
            ["status_query.servers"] =
                "# 被监测服务器列表, 每项:\n" +
                "#   - id: survival        # 服务器标识(事件中返回, 建议仅字母/数字/下划线)\n" +
                "#     host: 127.0.0.1     # 服务器地址\n" +
                "#     port: 25565         # 服务器端口\n" +
                "#     enabled: true       # 是否监测该服务器",
        };

        /// <summary>按 "section.field" 查注释; section 自身与顶层字段按裸键名查</summary>
        private static string? GetComment(string section, string trimmedLine)
        {
            int colon = trimmedLine.IndexOf(':');
            if (colon <= 0) return null;
            string key = trimmedLine.Substring(0, colon);

            if (section.Length > 0 && KeyComments.TryGetValue(section + "." + key, out var scoped))
            {
                return scoped;
            }
            return KeyComments.TryGetValue(key, out var bare) ? bare : null;
        }
    }
}
