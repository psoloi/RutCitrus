using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace RtCli.Modules.Extension
{
    /// <summary>
    /// 配置文件注释仓库：每个方法对应一个配置文件，返回 键路径→中文说明 的字典。
    /// 方法命名规则：Doc_{location}_{filename}
    ///   location: root=服务器根目录, config=config目录, plugin=插件目录, mod=模组目录
    ///   filename: 将文件名中的 . - / 替换为 _
    /// 例如：
    ///   bukkit.yml              → Doc_root_bukkit_yml
    ///   config/paper-global.yml → Doc_config_paper_global_yml
    ///   server.properties       → Doc_root_server_properties
    /// 插件目录支持两级回退：
    ///   plugins/Luckperms/config.yml
    ///     1) Doc_plugin_Luckperms_config_yml  (文件特定，优先)
    ///     2) Doc_plugin_Luckperms             (插件通用，回退)
    /// 开发者可直接在此类内新增方法以扩展配置文件注释，前端会通过 GetDocs 自动获取。
    /// 对于 Repository 未支持的配置文件，前端会自动回退到配置文件内联注释(# 开头)。
    /// </summary>
    internal class Repository
    {
        /// <summary>
        /// 根据配置文件名获取对应的注释字典。
        /// 自动将文件名派生为方法名并通过反射调用对应的 Doc_xxx 方法。
        /// 查找顺序：
        ///   1. basename → Doc_root_xxx_yml          (bukkit.yml → Doc_root_bukkit_yml)
        ///   2. 完整路径 → Doc_config_xxx_yml         (config/paper-global.yml → Doc_config_paper_global_yml)
        ///   3. 插件回退 → Doc_plugin_插件名称        (plugins/Luckperms/config.yml → Doc_plugin_Luckperms)
        /// </summary>
        public static Dictionary<string, string> GetDocs(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return new Dictionary<string, string>();

            // 1. 取 basename 进行匹配(支持 config/paper-global.yml 等)
            var basename = System.IO.Path.GetFileName(fileName);
            var method = FindMethod(ToMethodName(basename));

            // 2. basename 没找到则用完整路径尝试
            if (method == null && fileName.Contains('/'))
                method = FindMethod(ToMethodName(fileName));

            // 3. 插件目录回退：plugins/Luckperms/config.yml → Doc_plugin_Luckperms
            if (method == null && fileName.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase))
            {
                var afterPlugins = fileName.Substring("plugins/".Length);
                var slashIdx = afterPlugins.IndexOf('/');
                if (slashIdx > 0)
                {
                    var pluginName = afterPlugins.Substring(0, slashIdx);
                    method = FindMethod($"Doc_plugin_{pluginName}");
                }
            }

            if (method == null)
                return new Dictionary<string, string>();

            var instance = new Repository();
            var result = method.Invoke(instance, null);
            return result as Dictionary<string, string> ?? new Dictionary<string, string>();
        }

        private static MethodInfo? FindMethod(string methodName)
        {
            return typeof(Repository).GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }

        /// <summary>
        /// 将文件名转换为方法名。
        /// 例如: bukkit.yml → Doc_root_bukkit_yml
        ///       paper-global.yml → Doc_root_paper_global_yml
        ///       config/paper-global.yml → Doc_config_paper_global_yml
        /// </summary>
        private static string ToMethodName(string fileName)
        {
            string prefix;
            string rest;

            if (fileName.StartsWith("config/", StringComparison.OrdinalIgnoreCase))
            {
                prefix = "config";
                rest = fileName.Substring("config/".Length);
            }
            else if (fileName.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase))
            {
                prefix = "plugin";
                rest = fileName.Substring("plugins/".Length);
            }
            else if (fileName.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
            {
                prefix = "mod";
                rest = fileName.Substring("mods/".Length);
            }
            else
            {
                prefix = "root";
                rest = fileName;
            }

            var sb = new StringBuilder();
            foreach (var c in rest)
            {
                if (c == '.' || c == '-' || c == '/')
                    sb.Append('_');
                else
                    sb.Append(c);
            }
            return $"Doc_{prefix}_{sb}";
        }

        // ===================================================================
        //  服务器根目录配置文件 (root)
        // ===================================================================
        // 换行操作输入“\n”

        /// <summary>bukkit.yml 配置项注释</summary>
        internal Dictionary<string, string> Doc_root_bukkit_yml()
        {
            return new Dictionary<string, string>
            {
                ["settings.allow-end"] = "是否开启末地，适用小游戏服务器关闭后可删除末地世界节约一定资源",
                ["settings.warn-on-overload"] = "服务器过载时是否在控制台显示警告",
                ["settings.permissions-file"] = "权限文件路径",
                ["settings.update-folder"] = "插件更新文件夹(放在此文件夹的插件jar会在重启后替换旧版)",
                ["settings.plugin-profiling"] = "是否启用插件性能分析(使用 /timings 命令)",
                ["settings.connection-throttle"] = "玩家重连冷却时间(毫秒)，防止频繁重连",
                ["settings.query-plugins"] = "Query 查询是否返回插件列表",
                ["settings.deprecated-verbose"] = "已弃用的详细日志设置",
                ["settings.shutdown-message"] = "服务器关闭时显示给玩家的消息",
                ["settings.minimum-api"] = "最低 API 版本要求，低于此版本的插件不会加载",
                ["spawn-limits.monsters"] = "怪物生成上限(每玩家)",
                ["spawn-limits.animals"] = "动物生成上限(每玩家)",
                ["spawn-limits.water-animals"] = "水生动物生成上限(每玩家)",
                ["spawn-limits.water-ambient"] = "水生环境生物生成上限(每玩家)",
                ["spawn-limits.ambient"] = "环境生物(蝙蝠等)生成上限(每玩家)",
                ["spawn-limits.axolotls"] = "美西螈生成上限(每玩家)",
                ["spawn-limits.underground_water_creature"] = "地下水生生物生成上限(每玩家)",
                ["chunk-gc.period-in-ticks"] = "区块垃圾回收间隔(tick)，600=30秒",
                ["ticks-per.animal-spawns"] = "动物生成间隔(tick)，400=20秒",
                ["ticks-per.monster-spawns"] = "怪物生成间隔(tick)，1=每tick",
                ["ticks-per.water-spawns"] = "水生动物生成间隔(tick)",
                ["ticks-per.water-ambient-spawns"] = "水生环境生物生成间隔(tick)",
                ["ticks-per.ambient-spawns"] = "环境生物生成间隔(tick)",
                ["ticks-per.axolotl-spawns"] = "美西螈生成间隔(tick)",
                ["ticks-per.underground_water_creature-spawns"] = "地下水生生物生成间隔(tick)",
                ["ticks-per.autosave"] = "自动保存间隔(tick)，6000=5分钟",
            };
        }

        /// <summary>spigot.yml 配置项注释(参考 SpigotMC 官方文档)</summary>
        internal Dictionary<string, string> Doc_root_spigot_yml()
        {
            return new Dictionary<string, string>
            {
                ["settings.debug"] = "切换低于 INFO 级别的日志是否记录到控制台",
                ["settings.sample-count"] = "控制悬停在服务器列表玩家数量上时显示的样本玩家数量",
                ["settings.bungeecord"] = "启用 BungeeCord 专用功能(IP 白名单、直通 IP)",
                ["settings.save-user-cache-on-stop-only"] = "仅在服务器停止时保存用户缓存，而非持续保存",
                ["settings.moved-wrongly-threshold"] = "「错误移动」检查阈值，增大可减少回弹但可能被滥用",
                ["settings.moved-too-quickly-multiplier"] = "「移动过快」检查倍数，即服务器允许的最大移动速度",
                ["settings.log-villager-deaths"] = "是否记录村民的死亡信息",
                ["settings.log-named-deaths"] = "是否记录命名实体的死亡信息",
                ["settings.timeout-time"] = "服务器无响应多少秒后执行线程转储并尝试重启",
                ["settings.restart-on-crash"] = "服务器崩溃时是否自动尝试重启",
                ["settings.restart-script"] = "服务器启动脚本路径，用于 /restart 命令和崩溃重启",
                ["settings.user-cache-size"] = "usercache.json 中存储的最大玩家数量",
                ["settings.player-shuffle"] = "防止玩家通过重连获取优先处理位置，对 PVP 服务器有用(tick)",
                ["settings.netty-threads"] = "Netty 网络线程数量",
                ["settings.attribute.*.max"] = "对应属性的最大值上限",
                ["world-settings.*.verbose"] = "是否在启动时显示每个世界的详细配置报告",
                ["world-settings.*.mob-spawn-range"] = "在玩家周围生成怪物的半径(区块)",
                ["world-settings.*.below-zero-generation-in-existing-chunks"] = "升级到 1.18+ 时是否生成 y=0 以下的新区块",
                ["world-settings.*.simulation-distance"] = "实体/区块/流体的模拟距离(区块)，不能低于 5",
                ["world-settings.*.view-distance"] = "玩家周围加载区块数量，降低可减轻服务器负载",
                ["world-settings.*.merge-radius.item"] = "地面物品合并范围(区块)",
                ["world-settings.*.merge-radius.exp"] = "经验球合并范围(区块)",
                ["world-settings.*.ticks-per.hopper-transfer"] = "漏斗推送/拉取物品间隔(tick)，8=原版",
                ["world-settings.*.ticks-per.hopper-check"] = "漏斗检查物品间隔(tick)，0或1=原版",
                ["world-settings.*.hopper-amount"] = "漏斗单次传输最大物品数量",
                ["world-settings.*.hopper-can-load-chunks"] = "漏斗是否可以加载区块",
                ["world-settings.*.hunger.jump-walk-exhaustion"] = "行走跳跃消耗的饥饿值",
                ["world-settings.*.hunger.jump-sprint-exhaustion"] = "冲刺跳跃消耗的饥饿值",
                ["world-settings.*.hunger.combat-exhaustion"] = "战斗消耗的饥饿值",
                ["world-settings.*.hunger.regen-exhaustion"] = "生命恢复消耗的饥饿值",
                ["world-settings.*.hunger.swim-multiplier"] = "游泳饥饿消耗倍数",
                ["world-settings.*.hunger.sprint-multiplier"] = "冲刺饥饿消耗倍数",
                ["world-settings.*.hunger.other-multiplier"] = "其他饥饿消耗倍数",
                ["world-settings.*.hanging-tick-frequency"] = "悬挂实体(画、物品展示框等)的tick更新间隔",
                ["world-settings.*.dragon-death-sound-radius"] = "末影龙死亡声音传播范围，0=无限",
                ["world-settings.*.wither-spawn-sound-radius"] = "凋灵生成声音传播范围，0=无限",
                ["world-settings.*.end-portal-sound-radius"] = "末地传送门声音传播范围",
                ["world-settings.*.zombie-aggressive-towards-villager"] = "僵尸是否会攻击村民",
                ["world-settings.*.enable-zombie-pigmen-portal-spawns"] = "下界传送门是否随机产生僵尸猪人",
                ["world-settings.*.entity-tracking-range.players"] = "玩家实体可见/跟踪范围(区块)",
                ["world-settings.*.entity-tracking-range.animals"] = "动物实体可见/跟踪范围(区块)",
                ["world-settings.*.entity-tracking-range.monsters"] = "怪物实体可见/跟踪范围(区块)",
                ["world-settings.*.entity-tracking-range.misc"] = "杂项实体可见/跟踪范围(区块)",
                ["world-settings.*.entity-tracking-range.other"] = "其他实体可见/跟踪范围(区块)",
                ["world-settings.*.entity-activation-range.animals"] = "动物激活范围(区块)，超出此范围实体以较低速度tick",
                ["world-settings.*.entity-activation-range.monsters"] = "怪物激活范围(区块)",
                ["world-settings.*.entity-activation-range.raiders"] = "袭击者激活范围(区块)",
                ["world-settings.*.entity-activation-range.misc"] = "杂项实体激活范围(区块)",
                ["world-settings.*.entity-activation-range.water"] = "水生实体激活范围(区块)",
                ["world-settings.*.entity-activation-range.villagers"] = "村民激活范围(区块)",
                ["world-settings.*.entity-activation-range.flying-monsters"] = "飞行怪物激活范围(区块)",
                ["world-settings.*.entity-activation-range.tick-inactive-villagers"] = "村民在激活范围外是否仍然tick",
                ["world-settings.*.entity-activation-range.ignore-spectators"] = "计算激活范围时是否忽略旁观者",
                ["world-settings.*.thunder-chance"] = "雷暴触发概率",
                ["world-settings.*.max-tnt-per-tick"] = "每tick最大 TNT 实体处理数量",
                ["world-settings.*.max-tick-time.tile"] = "方块实体操作最大计算时间(ms)，超过则跳过",
                ["world-settings.*.max-tick-time.entity"] = "实体操作最大计算时间(ms)，超过则跳过",
                ["world-settings.*.arrow-despawn-rate"] = "箭矢消失间隔(tick)",
                ["world-settings.*.trident-despawn-rate"] = "三叉戟消失间隔(tick)",
                ["world-settings.*.item-despawn-rate"] = "地面物品消失间隔(tick)，6000=5分钟",
                ["world-settings.*.nerf-spawner-mobs"] = "刷怪笼生成的怪物是否没有 AI",
                ["world-settings.*.growth.cactus-modifier"] = "仙人掌生长速度百分比(100=原版)",
                ["world-settings.*.growth.cane-modifier"] = "甘蔗生长速度百分比",
                ["world-settings.*.growth.melon-modifier"] = "西瓜生长速度百分比",
                ["world-settings.*.growth.mushroom-modifier"] = "蘑菇生长速度百分比",
                ["world-settings.*.growth.pumpkin-modifier"] = "南瓜生长速度百分比",
                ["world-settings.*.growth.sapling-modifier"] = "树苗生长速度百分比",
                ["world-settings.*.growth.wheat-modifier"] = "小麦生长速度百分比",
                ["world-settings.*.growth.cocoa-modifier"] = "可可豆生长速度百分比",
                ["world-settings.*.growth.bamboo-modifier"] = "竹子生长速度百分比",
                ["world-settings.*.growth.sweetberry-modifier"] = "甜浆果生长速度百分比",
                ["world-settings.*.growth.kelp-modifier"] = "海带生长速度百分比",
                ["world-settings.*.growth.vine-modifier"] = "藤蔓生长速度百分比",
                ["world-settings.*.seed-village"] = "村庄生成种子",
                ["world-settings.*.seed-monument"] = "海底神殿生成种子",
                ["world-settings.*.seed-slime"] = "史莱姆生成种子",
                ["world-settings.*.seed-nether"] = "下界要塞生成种子",
                ["world-settings.*.seed-mansion"] = "林地府邸生成种子",
                ["world-settings.*.seed-endcity"] = "末地城生成种子",
                ["world-settings.*.seed-shipwreck"] = "沉船生成种子",
                ["world-settings.*.seed-outpost"] = "掠夺者前哨站生成种子",
                ["world-settings.*.seed-portal"] = "废弃传送门生成种子",
                ["world-settings.*.seed-ancientcity"] = "远古城市生成种子",
                ["world-settings.*.seed-trailruins"] = "古迹废墟生成种子",
                ["messages.whitelist"] = "未列入白名单的玩家连接时显示的消息",
                ["messages.unknown-command"] = "玩家输入未知命令时显示的消息",
                ["messages.server-full"] = "服务器满员时显示的消息",
                ["messages.outdated-client"] = "客户端版本过低时显示的消息，{0}替换为服务器版本",
                ["messages.outdated-server"] = "客户端版本过高时显示的消息，{0}替换为服务器版本",
                ["messages.restart"] = "服务器重启时显示给在线玩家的消息",
                ["advancements.disable-saving"] = "是否禁用成就/进度保存",
                ["players.disable-saving"] = "是否禁用玩家数据保存",
                ["commands.silent-commandblock-console"] = "是否将命令方块输出静音(不发送到控制台)",
                ["commands.tab-complete"] = "需要输入多少个字母才开始 Tab 补全，防止玩家探测命令",
                ["commands.send-namespaced"] = "Tab 补全时是否显示 命名空间:命令 格式(如 minecraft:tp)",
                ["commands.log"] = "是否将玩家命令打印到控制台/日志",
                ["commands.replace-commands"] = "禁用 Bukkit 实现并启用原版行为的命令列表",
                ["commands.spam-exclusions"] = "从垃圾邮件过滤器排除的命令列表",
                ["stats.disable-saving"] = "是否禁用统计/成就保存",
                ["config-version"] = "配置文件版本号(请勿手动修改)",
            };
        }

        /// <summary>server.properties 配置项注释(参考 Minecraft Wiki)</summary>
        internal Dictionary<string, string> Doc_root_server_properties()
        {
            return new Dictionary<string, string>
            {
                // === 服务器基本设置 ===
                ["server-port"] = "服务器监听的 TCP 端口。\n若端口为 0，则随机选择可用端口；范围 0-65535。",
                ["server-ip"] = "服务器绑定的本机 IP 地址。\n留空表示监听所有网卡(0.0.0.0)。",
                ["max-players"] = "服务器同时允许在线的最大玩家数量。\nOP 可在达到上限时继续加入。",
                ["motd"] = "在多人服务器列表中显示的服务器描述文本。\n支持旧版格式化代码(§)和 JSON 文本。",
                ["level-name"] = "世界存档文件夹的名称。\n修改后会在服务器目录下生成新的世界存档。",
                ["level-seed"] = "世界生成种子。留空则随机生成。\n不同种子会生成不同的地形。",
                ["level-type"] = "世界生成类型。\nminecraft\\:default=默认；minecraft\\:flat=超平坦；minecraft\\:large_biomes=巨型生物群系；minecraft\\:amplified=放大化(1.13 前)。",
                ["generator-settings"] = "自定义世界生成参数。JSON 格式。\n例如超平坦层的方块配置。",
                ["max-world-size"] = "世界边界的最大半径(方块)。\n范围 1-29999984。",

                // === 游戏模式与规则 ===
                ["gamemode"] = "新玩家默认进入的游戏模式。\nsurvival=生存；creative=创造；adventure=冒险；spectator=旁观。",
                ["force-gamemode"] = "为 true 时玩家加入时强制重置为默认游戏模式，覆盖其离线前的模式。",
                ["difficulty"] = "游戏难度。\npeaceful=和平；easy=简单；normal=普通；hard=困难。",
                ["hardcore"] = "为 true 时启用极限模式：难度锁定为困难，玩家死亡后被永久封禁。",
                ["pvp"] = "是否允许玩家互相攻击。\n26.2+ 已移至 gamerule pvp。",
                ["allow-flight"] = "是否允许玩家在生存模式下使用飞行(如 Mod)。开启后可避免反作弊误判。",
                ["allow-nether"] = "是否生成下界传送门并加载下界维度。\n26.2+ 已移至 gamerule allowNether。",
                ["allow-end"] = "是否加载末地维度。",
                ["generate-structures"] = "是否生成村庄、要塞、神殿等结构。",
                ["view-distance"] = "服务器向客户端发送的区块半径。范围 3-32。值越大网络/内存占用越高。",
                ["simulation-distance"] = "实体/方块激活的区块半径。范围 0-32；0 表示使用视距。",
                ["max-build-height"] = "玩家可放置方块的最大 Y 坐标。建议为 16 的倍数。",
                ["enable-command-block"] = "是否启用命令方块。\n26.2+ 已移至 gamerule commandBlockEnabled。",
                ["spawn-protection"] = "出生点保护半径(方块)。0 表示禁用；只有 OP 可在该范围建造。",
                ["announce-player-achievements"] = "是否在玩家获得成就时向全服广播。",
                ["spawn-animals"] = "是否生成动物。关闭后动物仍会存在但不会自然生成。\n26.2+ 已移至 gamerule doMobSpawning。",
                ["spawn-monsters"] = "是否生成怪物。关闭后怪物仍会存在但不会自然生成。\n26.2+ 已移至 gamerule doMobSpawning。",
                ["spawn-npcs"] = "是否生成村民等 NPC。\n26.2+ 已移至 gamerule doMobSpawning。",

                // === 玩家管理 ===
                ["white-list"] = "为 true 时仅白名单内玩家可加入服务器。",
                ["enforce-whitelist"] = "为 true 时玩家修改白名单后会被立即踢出，重连后才会重新校验。",
                ["enforce-secure-profile"] = "为 true 时仅允许具有有效 Mojang 签名档案的玩家加入(1.19 聊天签名特性)。",
                ["online-mode"] = "为 true 时进行 Mojang 账号正版验证；关闭可允许离线模式/盗版进入，但失去皮肤和签名聊天。",
                ["prevent-proxy-connections"] = "为 true 时服务器会拒绝从 VPN/代理来的连接(基于 IP 地理位置校验)。",
                ["op-permission-level"] = "新 OP 的默认权限等级。范围 0-4；4 为最高。",
                ["function-permission-level"] = "执行数据包中函数时的默认权限等级。范围 0-4。",
                ["player-idle-timeout"] = "玩家无操作超过该分钟数后自动踢出。0 表示禁用。",
                ["require-resource-pack"] = "为 true 时玩家必须接受服务器资源包才能加入。",
                ["resource-pack"] = "服务器资源包的下载 URL(直链 zip)。",
                ["resource-pack-sha1"] = "资源包 SHA-1 哈希(40 位十六进制)，用于校验完整性。",
                ["resource-pack-prompt"] = "玩家接受资源包前显示的提示文本(可空)。",
                ["max-chained-neighbor-updates"] = "限制单次方块状态变更引发的连锁更新深度，防止无限循环。\n-1 表示无限制。",

                // === 网络与协议 ===
                ["network-compression-threshold"] = "数据包字节数超过该阈值时启用压缩。\n-1 表示禁用压缩；0 表示全部压缩。",
                ["use-native-transport"] = "为 true 时在 Linux 上使用 epoll 优化网络性能。",
                ["enable-status"] = "为 true 时服务器响应 server list ping 请求。\n关闭可隐藏服务器 MOTD 显示。",
                ["enable-jmx-monitoring"] = "为 true 时启用 JMX 监控，可用于 Prometheus 等监控平台。",

                // === 远程管理 ===
                ["enable-rcon"] = "是否启用远程控制台协议(RCON)。",
                ["rcon.port"] = "RCON 服务监听端口。",
                ["rcon.password"] = "RCON 认证密码。建议使用强密码。",
                ["broadcast-rcon-to-ops"] = "为 true 时通过 RCON 执行的命令会向所有 OP 显示。",
                ["enable-query"] = "是否启用 GameSpy4 协议查询(用于第三方工具获取服务器信息)。",
                ["query.port"] = "GameSpy 查询服务端口。",

                // === 性能与日志 ===
                ["sync-chunk-writes"] = "为 true 时区块写入磁盘采用同步模式(更稳定，性能略低)。",
                ["log-ips"] = "为 true 时在玩家加入时将其 IP 写入日志。",

                // === 快照/测试 ===
                ["snooper-enabled"] = "为 true 时服务器向 Mojang 匿名发送统计数据(已弃用)。",
                ["max-tick-time"] = "单 tick 超过该毫秒数(且服务器卡顿时)会触发看门狗警告与崩溃报告。\n-1 表示禁用。",
                ["rate-limit"] = "单个玩家每秒可发送的数据包数量上限。0 表示无限制。",
                ["region-file-compression"] = "区域文件(.mca)的压缩算法。\ndeflate=GZip；zlib=原始 zlib；none=不压缩。",
                ["pause-when-empty-seconds"] = "服务器无玩家时多少秒后暂停 tick 以节省资源。\n-1 表示禁用，0 表示立即暂停。",
                ["text-filtering-config"] = "聊天文本过滤的配置 JSON(企业版特性)。",
                ["text-filtering-version"] = "文本过滤协议版本。",
                ["initial-zeroed-particle-level"] = "内部调试选项。一般保持默认。",
                ["hide-online-players"] = "为 true 时不在 status 响应中显示玩家列表。",
                ["broadcast-console-to-ops"] = "为 true 时控制台执行的命令会向所有 OP 显示。",

                // === 资源包增强 ===
                ["resource-pack-hash"] = "资源包 SHA-1 哈希(旧字段名)。\n1.10+ 改用 resource-pack-sha1。",
                ["resource-pack-id"] = "资源包的唯一 UUID，用于客户端缓存。\n格式为 8-4-4-4-12 的 UUID。",

                // === 数据包与调试 ===
                ["initial-enabled-packs"] = "服务器启动时默认启用的数据包列表(逗号分隔)。\n默认仅 vanilla。",
                ["initial-disabled-packs"] = "服务器启动时默认禁用的数据包列表(逗号分隔)。",
                ["entity-broadcast-range-percentage"] = "实体广播范围的百分比。范围 10-1000。",
                ["debug"] = "为 true 时启用服务端调试模式(仅开发/测试用)。",
                ["bug-report-link"] = "服务器崩溃时在日志中显示的错误报告链接。",
                ["accepts-transfers"] = "为 true 时允许从其他服务器转移玩家。",
                ["enable-code-of-conduct"] = "为 true 时在首次加入时显示 Mojang 行为准则。",
                ["status-heartbeat-interval"] = "服务器向 Mojang 状态服务器发送心跳的间隔(秒)。0 表示禁用。",

                // === 管理服务器 ===
                ["management-server-enabled"] = "是否启用原版管理服务器(Management Server)。\n用于多服务器集群管理。",
                ["management-server-host"] = "管理服务器监听地址。",
                ["management-server-port"] = "管理服务器监听端口。0 表示自动选择。",
                ["management-server-secret"] = "管理服务器的认证密钥(40 位字母数字)。留空则自动生成。",
                ["management-server-allowed-origins"] = "管理服务器允许的 CORS 源列表(逗号分隔)。",
                ["management-server-tls-enabled"] = "是否为管理服务器启用 TLS 加密。\n默认启用，需提供密钥库文件。",
                ["management-server-tls-keystore"] = "管理服务器 TLS 密钥库文件路径(PKCS12 格式)。",
                ["management-server-tls-keystore-password"] = "管理服务器 TLS 密钥库密码。\n也可通过环境变量 MINECRAFT_MANAGEMENT_TLS_KEYSTORE_PASSWORD 设置。",
            };
        }

        /// <summary>support.yml(RtCli Support 扩展)配置项注释</summary>
        internal Dictionary<string, string> Doc_root_support_yml()
        {
            return new Dictionary<string, string>
            {
                ["luckperms.enabled"] = "是否启用 LuckPerms 权限变更监测。\n启用后程序会轮询 LuckPerms 的 actions 表，检测到新记录时发布 LuckPermsChangeEvent 事件。",
                ["luckperms.mysql.address"] = "LuckPerms 使用的 MySQL 地址(主机:端口)。",
                ["luckperms.mysql.database"] = "LuckPerms 使用的数据库名。",
                ["luckperms.mysql.username"] = "MySQL 连接用户名。",
                ["luckperms.mysql.password"] = "MySQL 连接密码。",
                ["luckperms.mysql.table_prefix"] = "LuckPerms 数据表前缀(默认 luckperms_)。\n实际轮询表为 {table_prefix}actions。",
            };
        }
    }
}
