using System.Text;
using System.Text.Json.Serialization;

namespace RtPanel.Services
{
    /// <summary>
    /// 配置文件结构化解析与编辑服务。
    /// 将带注释的 YAML 配置解析为树形节点(供界面编辑)，并支持将界面修改回写为文本。
    /// 支持多配置文件 profile 切换(每种 YAML 文件有自己的已知键集合)。
    /// </summary>
    public class ConfigSchemaService
    {
        // 已注册的 profile 表(文件名 -> profile)
        private static readonly Dictionary<string, SchemaProfile> _profiles = RegisterProfiles();

        private static Dictionary<string, SchemaProfile> RegisterProfiles()
        {
            var profiles = new Dictionary<string, SchemaProfile>(StringComparer.OrdinalIgnoreCase);

            // ===== config.yml =====
            var config = new SchemaProfile("config.yml");
            config.TopLevelKeys.UnionWith(new[]
            {
                "check_java", "check_dot_net", "check_python", "check_os_bit", "check_update",
                "skip_select", "current_server", "grpc_port", "grpc_auth_key", "server_list",
                "enable_auto_tips", "done_patterns", "auto_backup_enabled", "auto_backup_interval_minutes",
                "auto_backup_servers", "auto_backup_path", "auto_backup_file_name_pattern",
                "auto_backup_little_enabled", "auto_backup_little_force_binary",
                "auto_backup_ignore_paths", "auto_backup_ignore_patterns",
                "hide_console_servers", "console_strip_patterns", "popular_versions",
                "auto_agree_eula", "start_run_ai", "player_event", "debug"
            });
            config.DictParents.Add("server_list");
            config.DictParents.Add("player_event.customs");
            config.Sections["server_list.*"] = new SectionSchema(new[]
            {
                "server_name", "analyzer_mode", "work_path", "run_server_flags", "java_path",
                "rcon_host", "rcon_port", "rcon_password", "management_host", "management_port",
                "management_secret", "management_tls_enabled", "auto_restart", "auto_restart_max_retries"
            });
            config.Sections["player_event"] = new SectionSchema(new[]
            {
                "enabled", "ticks", "player_join", "connect", "lost", "leaves",
                "command", "chat", "setmode", "customs"
            });
            config.Sections["player_event.customs.*"] = new SectionSchema(new[] { "patterns", "parameters" });
            foreach (var (k, v) in new Dictionary<string, string>
            {
                ["check_java"] = "bool",
                ["check_dot_net"] = "bool",
                ["check_python"] = "bool",
                ["check_os_bit"] = "bool",
                ["check_update"] = "bool",
                ["skip_select"] = "bool",
                ["current_server"] = "string",
                ["grpc_port"] = "int",
                ["grpc_auth_key"] = "string",
                ["enable_auto_tips"] = "bool",
                ["done_patterns"] = "list",
                ["auto_backup_enabled"] = "bool",
                ["auto_backup_interval_minutes"] = "int",
                ["auto_backup_servers"] = "list",
                ["auto_backup_path"] = "longstring",
                ["auto_backup_file_name_pattern"] = "string",
                ["auto_backup_little_enabled"] = "bool",
                ["auto_backup_little_force_binary"] = "bool",
                ["auto_backup_ignore_paths"] = "list",
                ["auto_backup_ignore_patterns"] = "list",
                ["hide_console_servers"] = "list",
                ["console_strip_patterns"] = "list",
                ["popular_versions"] = "list",
                ["auto_agree_eula"] = "bool",
                ["start_run_ai"] = "bool",
                ["debug"] = "string",
                ["server_list.*.server_name"] = "string",
                ["server_list.*.analyzer_mode"] = "string",
                ["server_list.*.work_path"] = "longstring",
                ["server_list.*.run_server_flags"] = "longstring",
                ["server_list.*.java_path"] = "longstring",
                ["server_list.*.rcon_host"] = "string",
                ["server_list.*.rcon_port"] = "int",
                ["server_list.*.rcon_password"] = "string",
                ["server_list.*.management_host"] = "string",
                ["server_list.*.management_port"] = "int",
                ["server_list.*.management_secret"] = "string",
                ["server_list.*.management_tls_enabled"] = "bool",
                ["server_list.*.auto_restart"] = "bool",
                ["server_list.*.auto_restart_max_retries"] = "int",
                ["player_event.enabled"] = "bool",
                ["player_event.ticks"] = "int",
                ["player_event.player_join"] = "list",
                ["player_event.connect"] = "list",
                ["player_event.lost"] = "list",
                ["player_event.leaves"] = "list",
                ["player_event.command"] = "list",
                ["player_event.chat"] = "list",
                ["player_event.setmode"] = "list",
                ["player_event.customs.*.patterns"] = "list",
                ["player_event.customs.*.parameters"] = "list",
            })
                config.ValueTypes[k] = v;
            profiles["config.yml"] = config;

            // ===== regex_settings.yml =====
            var regex = new SchemaProfile("regex_settings.yml");
            regex.TopLevelKeys.UnionWith(new[]
            {
                "console_error", "client_guide", "custom_client_errors",
                "custom_troubleshoot", "custom_base_entries", "custom_tips"
            });
            regex.Sections["console_error"] = new SectionSchema(new[] { "handler", "limit" });
            regex.Sections["client_guide"] = new SectionSchema(new[]
            {
                "player_join", "player_disconnect", "client_error", "error_handler", "timeout"
            });
            foreach (var (k, v) in new Dictionary<string, string>
            {
                ["console_error.handler"] = "longstring",
                ["console_error.limit"] = "int",
                ["client_guide.player_join"] = "longstring",
                ["client_guide.player_disconnect"] = "longstring",
                ["client_guide.client_error"] = "longstring",
                ["client_guide.error_handler"] = "longstring",
                ["client_guide.timeout"] = "int",
                ["custom_client_errors"] = "list",
                ["custom_troubleshoot"] = "list",
                ["custom_base_entries"] = "list",
                ["custom_tips"] = "list",
            })
                regex.ValueTypes[k] = v;
            profiles["regex_settings.yml"] = regex;

            // ===== ai_settings.yml =====
            var ai = new SchemaProfile("ai_settings.yml");
            ai.TopLevelKeys.UnionWith(new[] { "console_error", "server_auto_ai" });
            ai.Sections["console_error"] = new SectionSchema(new[]
            {
                "prompt", "max_tokens", "api_endpoint", "api_key", "model",
                "temperature", "request_timeout_seconds", "retry_on_error",
                "retry_count", "save_response"
            });
            ai.Sections["server_auto_ai"] = new SectionSchema(new[]
            {
                "prompt", "max_tokens", "api_endpoint", "api_key", "model",
                "temperature", "request_timeout_seconds", "retry_on_error",
                "retry_count", "save_response", "context_cache_size",
                "max_tool_rounds", "tools", "skills", "rules", "tasks"
            });
            ai.Sections["server_auto_ai.tools"] = new SectionSchema(new[]
            {
                "enabled", "allow", "deny", "tools_prompt"
            });
            ai.DictParents.Add("server_auto_ai.tasks");
            ai.DictParents.Add("server_auto_ai.tools.tools_prompt");
            ai.Sections["server_auto_ai.tasks.*"] = new SectionSchema(new[]
            {
                "enabled", "name", "trigger", "input", "limit", "prompt", "interval", "actions"
            });
            foreach (var (k, v) in new Dictionary<string, string>
            {
                ["console_error.prompt"] = "longstring",
                ["console_error.max_tokens"] = "int",
                ["console_error.api_endpoint"] = "string",
                ["console_error.api_key"] = "string",
                ["console_error.model"] = "string",
                ["console_error.temperature"] = "string",
                ["console_error.request_timeout_seconds"] = "int",
                ["console_error.retry_on_error"] = "bool",
                ["console_error.retry_count"] = "int",
                ["console_error.save_response"] = "bool",
                ["server_auto_ai.prompt"] = "longstring",
                ["server_auto_ai.max_tokens"] = "int",
                ["server_auto_ai.api_endpoint"] = "string",
                ["server_auto_ai.api_key"] = "string",
                ["server_auto_ai.model"] = "string",
                ["server_auto_ai.temperature"] = "string",
                ["server_auto_ai.request_timeout_seconds"] = "int",
                ["server_auto_ai.retry_on_error"] = "bool",
                ["server_auto_ai.retry_count"] = "int",
                ["server_auto_ai.save_response"] = "bool",
                ["server_auto_ai.context_cache_size"] = "int",
                ["server_auto_ai.max_tool_rounds"] = "int",
                ["server_auto_ai.tools.enabled"] = "bool",
                ["server_auto_ai.tools.allow"] = "list",
                ["server_auto_ai.tools.deny"] = "list",
                ["server_auto_ai.skills"] = "list",
                ["server_auto_ai.rules"] = "list",
                ["server_auto_ai.tasks.*.enabled"] = "bool",
                ["server_auto_ai.tasks.*.name"] = "string",
                ["server_auto_ai.tasks.*.trigger"] = "string",
                ["server_auto_ai.tasks.*.input"] = "list",
                ["server_auto_ai.tasks.*.limit"] = "int",
                ["server_auto_ai.tasks.*.prompt"] = "longstring",
                ["server_auto_ai.tasks.*.interval"] = "string",
                ["server_auto_ai.tasks.*.actions"] = "list",
            })
                ai.ValueTypes[k] = v;
            profiles["ai_settings.yml"] = ai;

            // ===== scheduler_settings.yml =====
            var sched = new SchemaProfile("scheduler_settings.yml");
            sched.TopLevelKeys.UnionWith(new[] { "tasks" });
            sched.DictParents.Add("tasks");
            sched.Sections["tasks.*"] = new SectionSchema(new[]
            {
                "enabled", "server_keys", "trigger", "condition", "execute", "pass_parameters", "rules"
            });
            sched.Sections["tasks.*.rules"] = new SectionSchema(new[]
            {
                "timeout_ms", "delay_minutes", "delay_until", "continue_tasks",
                "execute_immediately", "max_executions", "expire_at", "queue"
            });
            foreach (var (k, v) in new Dictionary<string, string>
            {
                ["tasks.*.enabled"] = "bool",
                ["tasks.*.server_keys"] = "list",
                ["tasks.*.trigger"] = "longstring",
                ["tasks.*.condition"] = "longstring",
                ["tasks.*.execute"] = "longstring",
                ["tasks.*.pass_parameters"] = "bool",
                ["tasks.*.rules.timeout_ms"] = "int",
                ["tasks.*.rules.delay_minutes"] = "int",
                ["tasks.*.rules.delay_until"] = "string",
                ["tasks.*.rules.continue_tasks"] = "list",
                ["tasks.*.rules.execute_immediately"] = "bool",
                ["tasks.*.rules.max_executions"] = "int",
                ["tasks.*.rules.expire_at"] = "string",
                ["tasks.*.rules.queue"] = "string",
            })
                sched.ValueTypes[k] = v;
            profiles["scheduler_settings.yml"] = sched;

            // ===== scripts_settings.yml =====
            var scripts = new SchemaProfile("scripts_settings.yml");
            scripts.TopLevelKeys.UnionWith(new[] { "scripts" });
            scripts.DictParents.Add("scripts");
            scripts.Sections["scripts.*"] = new SectionSchema(new[]
            {
                "file", "trigger_event", "enabled", "input"
            });
            foreach (var (k, v) in new Dictionary<string, string>
            {
                ["scripts.*.file"] = "string",
                ["scripts.*.trigger_event"] = "string",
                ["scripts.*.enabled"] = "bool",
                ["scripts.*.input"] = "longstring",
            })
                scripts.ValueTypes[k] = v;
            profiles["scripts_settings.yml"] = scripts;

            // ===== support.yml =====
            var support = new SchemaProfile("support.yml");
            support.TopLevelKeys.UnionWith(new[] { "luckperms" });
            support.Sections["luckperms"] = new SectionSchema(new[] { "enabled", "mysql" });
            support.Sections["luckperms.mysql"] = new SectionSchema(new[]
            {
                "address", "database", "username", "password", "table_prefix"
            });
            foreach (var (k, v) in new Dictionary<string, string>
            {
                ["luckperms.enabled"] = "bool",
                ["luckperms.mysql.address"] = "string",
                ["luckperms.mysql.database"] = "string",
                ["luckperms.mysql.username"] = "string",
                ["luckperms.mysql.password"] = "string",
                ["luckperms.mysql.table_prefix"] = "string",
            })
                support.ValueTypes[k] = v;
            profiles["support.yml"] = support;

            // ===== bukkit.yml =====
            var bukkit = new SchemaProfile("bukkit.yml");
            bukkit.TopLevelKeys.UnionWith(new[] { "settings", "spawn-limits", "chunk-gc", "ticks-per" });
            bukkit.Sections["settings"] = new SectionSchema(new[]
            {
                "allow-end", "warn-on-overload", "permissions-file", "update-folder",
                "plugin-profiling", "connection-throttle", "query-plugins", "deprecated-verbose",
                "shutdown-message", "minimum-api"
            });
            bukkit.Sections["spawn-limits"] = new SectionSchema(new[]
            {
                "monsters", "animals", "water-animals", "water-ambient", "ambient",
                "axolotls", "underground_water_creature"
            });
            bukkit.Sections["chunk-gc"] = new SectionSchema(new[] { "period-in-ticks" });
            bukkit.Sections["ticks-per"] = new SectionSchema(new[]
            {
                "animal-spawns", "monster-spawns", "water-spawns", "water-ambient-spawns",
                "ambient-spawns", "axolotl-spawns", "underground_water_creature-spawns", "autosave"
            });
            foreach (var (k, v) in new Dictionary<string, string>
            {
                ["settings.allow-end"] = "bool",
                ["settings.warn-on-overload"] = "bool",
                ["settings.permissions-file"] = "string",
                ["settings.update-folder"] = "string",
                ["settings.plugin-profiling"] = "bool",
                ["settings.connection-throttle"] = "int",
                ["settings.query-plugins"] = "bool",
                ["settings.deprecated-verbose"] = "bool",
                ["settings.shutdown-message"] = "longstring",
                ["settings.minimum-api"] = "string",
                ["spawn-limits.monsters"] = "int",
                ["spawn-limits.animals"] = "int",
                ["spawn-limits.water-animals"] = "int",
                ["spawn-limits.water-ambient"] = "int",
                ["spawn-limits.ambient"] = "int",
                ["spawn-limits.axolotls"] = "int",
                ["spawn-limits.underground_water_creature"] = "int",
                ["chunk-gc.period-in-ticks"] = "int",
                ["ticks-per.animal-spawns"] = "int",
                ["ticks-per.monster-spawns"] = "int",
                ["ticks-per.water-spawns"] = "int",
                ["ticks-per.water-ambient-spawns"] = "int",
                ["ticks-per.ambient-spawns"] = "int",
                ["ticks-per.axolotl-spawns"] = "int",
                ["ticks-per.underground_water_creature-spawns"] = "int",
                ["ticks-per.autosave"] = "int",
            })
                bukkit.ValueTypes[k] = v;
            profiles["bukkit.yml"] = bukkit;
            // bukkit.yml 中文说明已迁移至 RtCli Repository.Doc_root_bukkit_yml()

            // ===== spigot.yml =====
            var spigot = new SchemaProfile("spigot.yml");
            spigot.TopLevelKeys.UnionWith(new[] { "settings", "world-settings", "messages", "advancements", "players", "commands", "stats", "config-version" });
            spigot.DictParents.Add("world-settings");
            spigot.DictParents.Add("settings.attribute");
            spigot.Sections["settings"] = new SectionSchema(new[]
            {
                "debug", "sample-count", "bungeecord", "save-user-cache-on-stop-only",
                "moved-wrongly-threshold", "moved-too-quickly-multiplier",
                "log-villager-deaths", "log-named-deaths", "timeout-time",
                "restart-on-crash", "restart-script", "user-cache-size", "player-shuffle", "netty-threads",
                "attribute"
            });
            spigot.Sections["settings.attribute.*"] = new SectionSchema(new[] { "max" });
            spigot.Sections["world-settings.*"] = new SectionSchema(new[]
            {
                "verbose", "mob-spawn-range", "below-zero-generation-in-existing-chunks",
                "merge-radius", "ticks-per", "hopper-amount", "hopper-can-load-chunks",
                "hunger", "hanging-tick-frequency", "dragon-death-sound-radius",
                "wither-spawn-sound-radius", "end-portal-sound-radius",
                "zombie-aggressive-towards-villager", "enable-zombie-pigmen-portal-spawns",
                "simulation-distance", "view-distance", "entity-tracking-range",
                "entity-activation-range", "thunder-chance", "growth", "max-tnt-per-tick",
                "max-tick-time", "arrow-despawn-rate", "trident-despawn-rate",
                "item-despawn-rate", "nerf-spawner-mobs", "unload-frozen-chunks",
                "seed-village", "seed-desert", "seed-igloo", "seed-jungle", "seed-swamp",
                "seed-monument", "seed-shipwreck", "seed-ocean", "seed-outpost", "seed-endcity",
                "seed-slime", "seed-nether", "seed-mansion", "seed-fossil", "seed-portal",
                "seed-ancientcity", "seed-trailruins", "seed-trialchambers", "seed-buriedtreasure",
                "seed-mineshaft", "seed-stronghold"
            });
            spigot.DictParents.Add("world-settings.*.merge-radius");
            spigot.DictParents.Add("world-settings.*.ticks-per");
            spigot.DictParents.Add("world-settings.*.hunger");
            spigot.DictParents.Add("world-settings.*.entity-tracking-range");
            spigot.DictParents.Add("world-settings.*.entity-activation-range");
            spigot.DictParents.Add("world-settings.*.max-tick-time");
            spigot.DictParents.Add("world-settings.*.growth");
            spigot.Sections["world-settings.*.entity-activation-range"] = new SectionSchema(new[]
            {
                "animals", "monsters", "raiders", "misc", "water", "villagers", "flying-monsters",
                "wake-up-inactive", "villagers-work-immunity-after", "villagers-work-immunity-for",
                "villagers-active-for-panic", "tick-inactive-villagers", "ignore-spectators"
            });
            spigot.Sections["messages"] = new SectionSchema(new[]
            {
                "whitelist", "unknown-command", "server-full", "outdated-client", "outdated-server", "restart"
            });
            spigot.Sections["advancements"] = new SectionSchema(new[] { "disable-saving", "disabled" });
            spigot.Sections["players"] = new SectionSchema(new[] { "disable-saving" });
            spigot.Sections["commands"] = new SectionSchema(new[]
            {
                "silent-commandblock-console", "tab-complete", "send-namespaced",
                "spam-exclusions", "log", "replace-commands", "enable-spam-exclusions"
            });
            spigot.Sections["stats"] = new SectionSchema(new[] { "disable-saving", "forced-stats" });
            foreach (var (k, v) in new Dictionary<string, string>
            {
                ["settings.debug"] = "bool",
                ["settings.sample-count"] = "int",
                ["settings.bungeecord"] = "bool",
                ["settings.save-user-cache-on-stop-only"] = "bool",
                ["settings.moved-wrongly-threshold"] = "float",
                ["settings.moved-too-quickly-multiplier"] = "float",
                ["settings.log-villager-deaths"] = "bool",
                ["settings.log-named-deaths"] = "bool",
                ["settings.timeout-time"] = "int",
                ["settings.restart-on-crash"] = "bool",
                ["settings.restart-script"] = "string",
                ["settings.user-cache-size"] = "int",
                ["settings.player-shuffle"] = "int",
                ["settings.netty-threads"] = "int",
                ["settings.attribute.*.max"] = "float",
                ["world-settings.*.verbose"] = "bool",
                ["world-settings.*.mob-spawn-range"] = "int",
                ["world-settings.*.below-zero-generation-in-existing-chunks"] = "bool",
                ["world-settings.*.merge-radius.item"] = "float",
                ["world-settings.*.merge-radius.exp"] = "float",
                ["world-settings.*.ticks-per.hopper-transfer"] = "int",
                ["world-settings.*.ticks-per.hopper-check"] = "int",
                ["world-settings.*.hopper-amount"] = "int",
                ["world-settings.*.hopper-can-load-chunks"] = "bool",
                ["world-settings.*.hunger.jump-walk-exhaustion"] = "float",
                ["world-settings.*.hunger.jump-sprint-exhaustion"] = "float",
                ["world-settings.*.hunger.combat-exhaustion"] = "float",
                ["world-settings.*.hunger.regen-exhaustion"] = "float",
                ["world-settings.*.hunger.swim-multiplier"] = "float",
                ["world-settings.*.hunger.sprint-multiplier"] = "float",
                ["world-settings.*.hunger.other-multiplier"] = "float",
                ["world-settings.*.hanging-tick-frequency"] = "int",
                ["world-settings.*.dragon-death-sound-radius"] = "int",
                ["world-settings.*.wither-spawn-sound-radius"] = "int",
                ["world-settings.*.end-portal-sound-radius"] = "int",
                ["world-settings.*.zombie-aggressive-towards-villager"] = "bool",
                ["world-settings.*.enable-zombie-pigmen-portal-spawns"] = "bool",
                ["world-settings.*.simulation-distance"] = "string",
                ["world-settings.*.view-distance"] = "string",
                ["world-settings.*.entity-tracking-range.players"] = "int",
                ["world-settings.*.entity-tracking-range.animals"] = "int",
                ["world-settings.*.entity-tracking-range.monsters"] = "int",
                ["world-settings.*.entity-tracking-range.misc"] = "int",
                ["world-settings.*.entity-tracking-range.display"] = "int",
                ["world-settings.*.entity-tracking-range.other"] = "int",
                ["world-settings.*.entity-activation-range.animals"] = "int",
                ["world-settings.*.entity-activation-range.monsters"] = "int",
                ["world-settings.*.entity-activation-range.raiders"] = "int",
                ["world-settings.*.entity-activation-range.misc"] = "int",
                ["world-settings.*.entity-activation-range.water"] = "int",
                ["world-settings.*.entity-activation-range.villagers"] = "int",
                ["world-settings.*.entity-activation-range.flying-monsters"] = "int",
                ["world-settings.*.entity-activation-range.villagers-work-immunity-after"] = "int",
                ["world-settings.*.entity-activation-range.villagers-work-immunity-for"] = "int",
                ["world-settings.*.entity-activation-range.villagers-active-for-panic"] = "bool",
                ["world-settings.*.entity-activation-range.tick-inactive-villagers"] = "bool",
                ["world-settings.*.entity-activation-range.ignore-spectators"] = "bool",
                ["world-settings.*.thunder-chance"] = "int",
                ["world-settings.*.max-tnt-per-tick"] = "int",
                ["world-settings.*.max-tick-time.tile"] = "int",
                ["world-settings.*.max-tick-time.entity"] = "int",
                ["world-settings.*.arrow-despawn-rate"] = "int",
                ["world-settings.*.trident-despawn-rate"] = "int",
                ["world-settings.*.item-despawn-rate"] = "int",
                ["world-settings.*.nerf-spawner-mobs"] = "bool",
                ["world-settings.*.unload-frozen-chunks"] = "bool",
                ["world-settings.*.growth.cactus-modifier"] = "int",
                ["world-settings.*.growth.cane-modifier"] = "int",
                ["world-settings.*.growth.melon-modifier"] = "int",
                ["world-settings.*.growth.mushroom-modifier"] = "int",
                ["world-settings.*.growth.pumpkin-modifier"] = "int",
                ["world-settings.*.growth.sapling-modifier"] = "int",
                ["world-settings.*.growth.beetroot-modifier"] = "int",
                ["world-settings.*.growth.carrot-modifier"] = "int",
                ["world-settings.*.growth.potato-modifier"] = "int",
                ["world-settings.*.growth.wheat-modifier"] = "int",
                ["world-settings.*.growth.netherwart-modifier"] = "int",
                ["world-settings.*.growth.vine-modifier"] = "int",
                ["world-settings.*.growth.cocoa-modifier"] = "int",
                ["world-settings.*.growth.bamboo-modifier"] = "int",
                ["world-settings.*.growth.sweetberry-modifier"] = "int",
                ["world-settings.*.growth.kelp-modifier"] = "int",
                ["world-settings.*.growth.twistingvines-modifier"] = "int",
                ["world-settings.*.growth.weepingvines-modifier"] = "int",
                ["world-settings.*.growth.cavevines-modifier"] = "int",
                ["world-settings.*.growth.glowberry-modifier"] = "int",
                ["world-settings.*.growth.pitcherplant-modifier"] = "int",
                ["messages.whitelist"] = "longstring",
                ["messages.unknown-command"] = "longstring",
                ["messages.server-full"] = "longstring",
                ["messages.outdated-client"] = "string",
                ["messages.outdated-server"] = "string",
                ["messages.restart"] = "longstring",
                ["advancements.disable-saving"] = "bool",
                ["players.disable-saving"] = "bool",
                ["commands.silent-commandblock-console"] = "bool",
                ["commands.tab-complete"] = "int",
                ["commands.send-namespaced"] = "bool",
                ["commands.log"] = "bool",
                ["commands.enable-spam-exclusions"] = "bool",
                ["stats.disable-saving"] = "bool",
                ["config-version"] = "int",
            })
                spigot.ValueTypes[k] = v;
            profiles["spigot.yml"] = spigot;
            // spigot.yml 中文说明已迁移至 RtCli Repository.Doc_root_spigot_yml()

            // ===== paper-global.yml (也用于 config/paper-global.yml) =====
            var paperGlobal = new SchemaProfile("paper-global.yml");
            paperGlobal.TopLevelKeys.UnionWith(new[]
            {
                "_version", "anticheat", "block-updates", "chunk-loading-advanced", "chunk-loading-basic",
                "chunk-system", "collisions", "commands", "console", "item-validation", "logging",
                "messages", "misc", "packet-limiter", "player-auto-save", "proxies", "scoreboards",
                "spam-limiter", "spark", "unsupported-settings", "watchdog"
            });
            paperGlobal.Sections["anticheat"] = new SectionSchema(new[] { "obfuscation" });
            paperGlobal.Sections["block-updates"] = new SectionSchema(new[]
            { "disable-chorus-plant-updates", "disable-mushroom-block-updates", "disable-noteblock-updates", "disable-tripwire-updates" });
            paperGlobal.Sections["chunk-loading-advanced"] = new SectionSchema(new[]
            { "auto-config-send-distance", "player-max-concurrent-chunk-generates", "player-max-concurrent-chunk-loads" });
            paperGlobal.Sections["chunk-loading-basic"] = new SectionSchema(new[]
            { "player-max-chunk-generate-rate", "player-max-chunk-load-rate", "player-max-chunk-send-rate" });
            paperGlobal.Sections["chunk-system"] = new SectionSchema(new[] { "io-threads", "worker-threads" });
            paperGlobal.Sections["collisions"] = new SectionSchema(new[]
            { "enable-player-collisions", "send-full-pos-for-hard-colliding-entities" });
            paperGlobal.Sections["commands"] = new SectionSchema(new[]
            { "ride-command-allow-player-as-vehicle", "suggest-player-names-when-null-tab-completions", "time-command-affects-all-worlds" });
            paperGlobal.Sections["console"] = new SectionSchema(new[]
            { "enable-brigadier-completions", "enable-brigadier-highlighting", "has-all-permissions" });
            paperGlobal.Sections["item-validation"] = new SectionSchema(new[] { "book", "book-size", "display-name", "lore-line", "resolve-selectors-in-books" });
            paperGlobal.Sections["logging"] = new SectionSchema(new[] { "deobfuscate-stacktraces" });
            paperGlobal.Sections["messages"] = new SectionSchema(new[] { "kick", "no-permission", "use-display-name-in-quit-message" });
            paperGlobal.Sections["misc"] = new SectionSchema(new[]
            { "chat-threads", "client-interaction-leniency-distance", "compression-level", "enable-nether",
              "fix-far-end-terrain-generation", "load-permissions-yml-before-plugins", "max-joins-per-tick",
              "prevent-negative-villager-demand", "region-file-cache-size", "send-full-pos-for-item-entities",
              "strict-advancement-dimension-check", "use-alternative-luck-formula",
              "use-dimension-type-for-custom-spawners", "xp-orb-groups-per-area" });
            paperGlobal.Sections["packet-limiter"] = new SectionSchema(new[] { "all-packets", "kick-message", "overrides" });
            paperGlobal.Sections["player-auto-save"] = new SectionSchema(new[] { "max-per-tick", "rate" });
            paperGlobal.Sections["proxies"] = new SectionSchema(new[] { "bungee-cord", "proxy-protocol", "velocity" });
            paperGlobal.Sections["scoreboards"] = new SectionSchema(new[]
            { "save-empty-scoreboard-teams", "track-plugin-scoreboards" });
            paperGlobal.Sections["spam-limiter"] = new SectionSchema(new[]
            { "incoming-packet-threshold", "recipe-spam-increment", "recipe-spam-limit", "tab-spam-increment", "tab-spam-limit" });
            paperGlobal.Sections["spark"] = new SectionSchema(new[] { "enable-immediately", "enabled" });
            paperGlobal.Sections["unsupported-settings"] = new SectionSchema(new[]
            { "allow-headless-pistons", "allow-permanent-block-break-exploits", "allow-piston-duplication",
              "allow-unsafe-end-portal-teleportation", "compression-format", "perform-username-validation",
              "skip-tripwire-hook-placement-validation", "skip-vanilla-damage-tick-when-shield-blocked",
              "update-equipment-on-player-actions" });
            paperGlobal.Sections["watchdog"] = new SectionSchema(new[] { "early-warning-delay", "early-warning-every" });
            foreach (var (k, v) in new Dictionary<string, string>
            {
                ["_version"] = "int",
                ["block-updates.disable-chorus-plant-updates"] = "bool",
                ["block-updates.disable-mushroom-block-updates"] = "bool",
                ["block-updates.disable-noteblock-updates"] = "bool",
                ["block-updates.disable-tripwire-updates"] = "bool",
                ["chunk-loading-advanced.auto-config-send-distance"] = "bool",
                ["chunk-loading-advanced.player-max-concurrent-chunk-generates"] = "int",
                ["chunk-loading-advanced.player-max-concurrent-chunk-loads"] = "int",
                ["chunk-loading-basic.player-max-chunk-generate-rate"] = "float",
                ["chunk-loading-basic.player-max-chunk-load-rate"] = "float",
                ["chunk-loading-basic.player-max-chunk-send-rate"] = "float",
                ["chunk-system.io-threads"] = "int",
                ["chunk-system.worker-threads"] = "int",
                ["collisions.enable-player-collisions"] = "bool",
                ["collisions.send-full-pos-for-hard-colliding-entities"] = "bool",
                ["commands.ride-command-allow-player-as-vehicle"] = "bool",
                ["commands.suggest-player-names-when-null-tab-completions"] = "bool",
                ["commands.time-command-affects-all-worlds"] = "bool",
                ["console.enable-brigadier-completions"] = "bool",
                ["console.enable-brigadier-highlighting"] = "bool",
                ["console.has-all-permissions"] = "bool",
                ["item-validation.book.author"] = "int",
                ["item-validation.book.page"] = "int",
                ["item-validation.book.title"] = "int",
                ["item-validation.book-size.page-max"] = "int",
                ["item-validation.book-size.total-multiplier"] = "float",
                ["item-validation.display-name"] = "int",
                ["item-validation.lore-line"] = "int",
                ["item-validation.resolve-selectors-in-books"] = "bool",
                ["logging.deobfuscate-stacktraces"] = "bool",
                ["messages.use-display-name-in-quit-message"] = "bool",
                ["misc.client-interaction-leniency-distance"] = "string",
                ["misc.compression-level"] = "string",
                ["misc.enable-nether"] = "bool",
                ["misc.fix-far-end-terrain-generation"] = "bool",
                ["misc.load-permissions-yml-before-plugins"] = "bool",
                ["misc.max-joins-per-tick"] = "int",
                ["misc.prevent-negative-villager-demand"] = "bool",
                ["misc.region-file-cache-size"] = "int",
                ["misc.send-full-pos-for-item-entities"] = "bool",
                ["misc.strict-advancement-dimension-check"] = "bool",
                ["misc.use-alternative-luck-formula"] = "bool",
                ["misc.use-dimension-type-for-custom-spawners"] = "bool",
                ["misc.xp-orb-groups-per-area"] = "string",
                ["packet-limiter.all-packets.action"] = "string",
                ["packet-limiter.all-packets.interval"] = "float",
                ["packet-limiter.all-packets.max-packet-rate"] = "float",
                ["player-auto-save.max-per-tick"] = "int",
                ["player-auto-save.rate"] = "int",
                ["proxies.bungee-cord.online-mode"] = "bool",
                ["proxies.proxy-protocol"] = "bool",
                ["proxies.velocity.enabled"] = "bool",
                ["proxies.velocity.online-mode"] = "bool",
                ["proxies.velocity.secret"] = "string",
                ["scoreboards.save-empty-scoreboard-teams"] = "bool",
                ["scoreboards.track-plugin-scoreboards"] = "bool",
                ["spam-limiter.incoming-packet-threshold"] = "int",
                ["spam-limiter.recipe-spam-increment"] = "int",
                ["spam-limiter.recipe-spam-limit"] = "int",
                ["spam-limiter.tab-spam-increment"] = "int",
                ["spam-limiter.tab-spam-limit"] = "int",
                ["spark.enable-immediately"] = "bool",
                ["spark.enabled"] = "bool",
                ["unsupported-settings.allow-headless-pistons"] = "bool",
                ["unsupported-settings.allow-permanent-block-break-exploits"] = "bool",
                ["unsupported-settings.allow-piston-duplication"] = "bool",
                ["unsupported-settings.allow-unsafe-end-portal-teleportation"] = "bool",
                ["unsupported-settings.compression-format"] = "string",
                ["unsupported-settings.perform-username-validation"] = "bool",
                ["unsupported-settings.skip-tripwire-hook-placement-validation"] = "bool",
                ["unsupported-settings.skip-vanilla-damage-tick-when-shield-blocked"] = "bool",
                ["unsupported-settings.update-equipment-on-player-actions"] = "bool",
                ["watchdog.early-warning-delay"] = "int",
                ["watchdog.early-warning-every"] = "int",
            })
                paperGlobal.ValueTypes[k] = v;
            profiles["paper-global.yml"] = paperGlobal;

            // ===== paper-world-defaults.yml (也用于 config/paper-world-defaults.yml) =====
            var paperWorld = new SchemaProfile("paper-world-defaults.yml");
            paperWorld.TopLevelKeys.UnionWith(new[]
            {
                "_version", "chunks", "collisions", "entities", "environment", "fixes",
                "hopper", "lobber", "max-growth-height", "misc", "scoreboards",
                "tick-rates", "unsupported-settings"
            });
            paperWorld.DictParents.Add("chunks");
            paperWorld.DictParents.Add("entities");
            paperWorld.DictParents.Add("environment");
            paperWorld.DictParents.Add("fixes");
            paperWorld.DictParents.Add("hopper");
            paperWorld.DictParents.Add("misc");
            paperWorld.DictParents.Add("tick-rates");
            paperWorld.DictParents.Add("unsupported-settings");
            profiles["paper-world-defaults.yml"] = paperWorld;

            return profiles;
        }

        /// <summary>解析配置文本为结构化节点树(仅顶层节点列表)。</summary>
        /// <param name="externalComments">来自 RtCli Repository 的外部注释字典(优先于 profile 内置 Comments)。</param>
        public List<ConfigNode> Parse(string content, string profileName = "config.yml", Dictionary<string, string>? externalComments = null)
        {
            var profile = GetProfile(profileName);
            var lines = content.Replace("\r\n", "\n").Split('\n');
            var root = new ConfigNode { Key = "", Path = "", ValueType = "object", Depth = -1 };
            var stack = new Stack<ConfigNode>();
            stack.Push(root);
            var commentBuffer = new List<string>();
            bool firstKeySeen = false;

            for (int i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var stripped = raw.TrimStart();
                var indent = raw.Substring(0, raw.Length - stripped.Length);
                int depth = indent.Length;

                if (string.IsNullOrWhiteSpace(stripped))
                {
                    commentBuffer.Clear();
                    continue;
                }

                if (stripped.StartsWith("#"))
                {
                    commentBuffer.Add(stripped);
                    continue;
                }

                if (stripped.StartsWith("- "))
                {
                    var itemRaw = stripped.Substring(2).Trim();
                    if (stack.Count > 0)
                    {
                        var top = stack.Peek();
                        top.ListValue.Add(UnquoteYaml(itemRaw));
                        top.ListEndLine = i;
                        if (top.ListStartLine < 0) top.ListStartLine = i;
                        if (top.ValueType == "object") top.ValueType = "list";
                    }
                    continue;
                }

                var colonIdx = stripped.IndexOf(':');
                if (colonIdx < 0) continue;

                var key = stripped.Substring(0, colonIdx).Trim();
                var valuePart = colonIdx + 1 < stripped.Length ? stripped.Substring(colonIdx + 1).Trim() : "";

                while (stack.Count > 1 && stack.Peek().Depth >= depth)
                    stack.Pop();

                var parent = stack.Peek();
                var parentPath = parent.Path;
                var path = string.IsNullOrEmpty(parentPath) ? key : parentPath + "." + key;

                var comment = string.Join("\n", commentBuffer);
                commentBuffer.Clear();

                if (!firstKeySeen && comment.Split('\n').Length > 6)
                    comment = "";
                firstKeySeen = true;

                // 优先使用外部注释(RtCli Repository)，其次回退到 profile 内置 Comments
                var schemaPath = profile?.ToSchemaPath(path) ?? "";
                var commentsSrc = externalComments ?? profile?.Comments;
                if (commentsSrc != null && commentsSrc.TryGetValue(schemaPath, out var zhComment) && !string.IsNullOrEmpty(zhComment))
                    comment = "# " + zhComment;

                var node = new ConfigNode
                {
                    Key = key,
                    Path = path,
                    Depth = depth,
                    Indent = indent,
                    LineIndex = i,
                    Comment = comment,
                };

                if (!string.IsNullOrEmpty(valuePart))
                {
                    node.Value = UnquoteYaml(valuePart);
                    node.ValueType = ResolveType(profile!, path, valuePart, false, false);
                }
                else
                {
                    node.ValueType = "object";
                    node.ListStartLine = i;
                    node.ListEndLine = i;
                }

                node.IsUnknown = profile!.IsUnknown(parentPath, key);

                parent.Children.Add(node);

                if (string.IsNullOrEmpty(valuePart))
                    stack.Push(node);
            }

            return root.Children;
        }

        /// <summary>把界面修改回写到配置文本。changes 中的 path 与解析时的 Path 对应。</summary>
        public string ApplyChanges(string content, List<ConfigChange> changes, string profileName = "config.yml")
        {
            if (changes == null || changes.Count == 0) return content;
            var profile = GetProfile(profileName);

            var lines = new List<string>(content.Replace("\r\n", "\n").Split('\n'));
            var flat = new Dictionary<string, ConfigNode>();
            CollectFlat(Parse(content, profileName), flat);

            var patches = new List<(int startLine, int endLine, List<string> newLines)>();
            foreach (var ch in changes)
            {
                if (string.IsNullOrEmpty(ch.Path)) continue;
                if (!flat.TryGetValue(ch.Path, out var node)) continue;

                if (node.ValueType == "list" || ch.ValueType == "list")
                {
                    var newLines = new List<string>();
                    newLines.Add(node.Indent + node.Key + ":");
                    if (ch.ListValue != null)
                    {
                        foreach (var item in ch.ListValue)
                        {
                            if (!string.IsNullOrEmpty(item))
                                newLines.Add(node.Indent + "- " + QuoteYaml(item));
                        }
                    }
                    int start = node.ListStartLine >= 0 ? node.ListStartLine : node.LineIndex;
                    int end = node.ListEndLine >= 0 ? node.ListEndLine : start;
                    patches.Add((start, end, newLines));
                }
                else
                {
                    var vtype = !string.IsNullOrEmpty(ch.ValueType) ? ch.ValueType : node.ValueType;
                    var newLine = node.Indent + node.Key + ": " + FormatScalar(ch.Value ?? "", vtype);
                    patches.Add((node.LineIndex, node.LineIndex, new List<string> { newLine }));
                }
            }

            foreach (var (start, end, newLines) in patches.OrderByDescending(p => p.startLine))
            {
                int count = end - start + 1;
                if (count > 0) lines.RemoveRange(start, count);
                lines.InsertRange(start, newLines);
            }

            return string.Join("\n", lines);
        }

        // ===== server.properties 字段 schema(版本感知) =====
        // 参考：https://zh.minecraft.wiki/w/服务端配置文件格式
        // SinceVer: 字段引入的版本(空字符串表示一直存在)；Category 用于分组
        private static readonly PropertiesFieldSchema[] _serverPropertiesFields =
        {
            // === 服务器基本设置 ===
            new("server-port", "服务端端口", "服务器监听的 TCP 端口。\n若端口为 0，则随机选择可用端口；范围 0-65535。", "int", "25565", "basic", ""),
            new("server-ip", "监听地址", "服务器绑定的本机 IP 地址。\n留空表示监听所有网卡(0.0.0.0)。", "string", "", "basic", ""),
            new("max-players", "最大玩家数", "服务器同时允许在线的最大玩家数量。\nOP 可在达到上限时继续加入。", "int", "20", "basic", ""),
            new("motd", "服务器 MOTD", "在多人服务器列表中显示的服务器描述文本。\n支持旧版格式化代码(§)和 JSON 文本。", "longstring", "A Minecraft Server", "basic", ""),
            new("level-name", "世界名称", "世界存档文件夹的名称。\n修改后会在服务器目录下生成新的世界存档。", "string", "world", "basic", ""),
            new("level-seed", "世界种子", "世界生成种子。留空则随机生成。\n不同种子会生成不同的地形。", "string", "", "basic", ""),
            new("level-type", "世界类型", "世界生成类型。\nminecraft\\:default=默认；minecraft\\:flat=超平坦；minecraft\\:large_biomes=巨型生物群系；minecraft\\:amplified=放大化(1.13 前)。", "string", "minecraft\\:default", "basic", ""),
            new("generator-settings", "生成器设置", "自定义世界生成参数。JSON 格式。\n例如超平坦层的方块配置。", "longstring", "", "basic", ""),
            new("max-world-size", "最大世界尺寸", "世界边界的最大半径(方块)。\n范围 1-29999984。", "int", "29999984", "basic", "1.14"),

            // === 游戏模式与规则 ===
            new("gamemode", "游戏模式", "新玩家默认进入的游戏模式。\nsurvival=生存；creative=创造；adventure=冒险；spectator=旁观。", "string", "survival", "gameplay", ""),
            new("force-gamemode", "强制游戏模式", "为 true 时玩家加入时强制重置为默认游戏模式，覆盖其离线前的模式。", "bool", "false", "gameplay", ""),
            new("difficulty", "难度", "游戏难度。\npeaceful=和平；easy=简单；normal=普通；hard=困难。", "string", "easy", "gameplay", ""),
            new("hardcore", "极限模式", "为 true 时启用极限模式：难度锁定为困难，玩家死亡后被永久封禁。", "bool", "false", "gameplay", ""),
            new("pvp", "PVP", "是否允许玩家互相攻击。\n26.2+ 已移至 gamerule pvp。", "bool", "true", "gameplay", "", "26.2"),
            new("allow-flight", "允许飞行", "是否允许玩家在生存模式下使用飞行(如 Mod)。开启后可避免反作弊误判。", "bool", "false", "gameplay", ""),
            new("allow-nether", "允许下界", "是否生成下界传送门并加载下界维度。\n26.2+ 已移至 gamerule allowNether。", "bool", "true", "gameplay", "", "26.2"),
            new("allow-end", "允许末地", "是否加载末地维度。", "bool", "true", "gameplay", "1.19.4"),
            new("generate-structures", "生成结构", "是否生成村庄、要塞、神殿等结构。", "bool", "true", "gameplay", ""),
            new("view-distance", "视距", "服务器向客户端发送的区块半径。范围 3-32。值越大网络/内存占用越高。", "int", "10", "gameplay", ""),
            new("simulation-distance", "模拟距离", "实体/方块激活的区块半径。范围 0-32；0 表示使用视距。", "int", "10", "gameplay", "1.18"),
            new("max-build-height", "最大建筑高度", "玩家可放置方块的最大 Y 坐标。建议为 16 的倍数。", "int", "256", "gameplay", ""),
            new("enable-command-block", "命令方块", "是否启用命令方块。\n26.2+ 已移至 gamerule commandBlockEnabled。", "bool", "false", "gameplay", "", "26.2"),
            new("spawn-protection", "出生点保护", "出生点保护半径(方块)。0 表示禁用；只有 OP 可在该范围建造。", "int", "16", "gameplay", ""),
            new("announce-player-achievements", "成就广播", "是否在玩家获得成就时向全服广播。", "bool", "true", "gameplay", ""),
            new("spawn-animals", "生成动物", "是否生成动物。关闭后动物仍会存在但不会自然生成。\n26.2+ 已移至 gamerule doMobSpawning。", "bool", "true", "gameplay", "", "26.2"),
            new("spawn-monsters", "生成怪物", "是否生成怪物。关闭后怪物仍会存在但不会自然生成。\n26.2+ 已移至 gamerule doMobSpawning。", "bool", "true", "gameplay", "", "26.2"),
            new("spawn-npcs", "生成 NPC", "是否生成村民等 NPC。\n26.2+ 已移至 gamerule doMobSpawning。", "bool", "true", "gameplay", "", "26.2"),

            // === 玩家管理 ===
            new("white-list", "白名单", "为 true 时仅白名单内玩家可加入服务器。", "bool", "false", "players", ""),
            new("enforce-whitelist", "强制白名单", "为 true 时玩家修改白名单后会被立即踢出，重连后才会重新校验。", "bool", "false", "players", "1.14.4"),
            new("enforce-secure-profile", "强制安全档案", "为 true 时仅允许具有有效 Mojang 签名档案的玩家加入(1.19 聊天签名特性)。", "bool", "true", "players", "1.19"),
            new("online-mode", "正版验证", "为 true 时进行 Mojang 账号正版验证；关闭可允许离线模式/盗版进入，但失去皮肤和签名聊天。", "bool", "true", "players", ""),
            new("prevent-proxy-connections", "禁止代理连接", "为 true 时服务器会拒绝从 VPN/代理来的连接(基于 IP 地理位置校验)。", "bool", "false", "players", "1.14.4"),
            new("op-permission-level", "OP 权限等级", "新 OP 的默认权限等级。范围 0-4；4 为最高。", "int", "4", "players", ""),
            new("function-permission-level", "函数权限等级", "执行数据包中函数时的默认权限等级。范围 0-4。", "int", "2", "players", "1.13"),
            new("player-idle-timeout", "挂机踢出", "玩家无操作超过该分钟数后自动踢出。0 表示禁用。", "int", "0", "players", ""),
            new("require-resource-pack", "强制资源包", "为 true 时玩家必须接受服务器资源包才能加入。", "bool", "false", "players", "1.18"),
            new("resource-pack", "资源包 URL", "服务器资源包的下载 URL(直链 zip)。", "longstring", "", "players", ""),
            new("resource-pack-sha1", "资源包校验值", "资源包 SHA-1 哈希(40 位十六进制)，用于校验完整性。", "string", "", "players", "1.10"),
            new("resource-pack-prompt", "资源包提示", "玩家接受资源包前显示的提示文本(可空)。", "longstring", "", "players", "1.17"),
            new("max-chained-neighbor-updates", "最大连锁更新", "限制单次方块状态变更引发的连锁更新深度，防止无限循环。\n-1 表示无限制。", "int", "10000", "players", "1.19.3"),

            // === 网络与协议 ===
            new("network-compression-threshold", "网络压缩阈值", "数据包字节数超过该阈值时启用压缩。\n-1 表示禁用压缩；0 表示全部压缩。", "int", "256", "network", ""),
            new("use-native-transport", "原生传输", "为 true 时在 Linux 上使用 epoll 优化网络性能。", "bool", "true", "network", "1.12"),
            new("enable-status", "状态响应", "为 true 时服务器响应 server list ping 请求。\n关闭可隐藏服务器 MOTD 显示。", "bool", "true", "network", "1.15.2"),
            new("enable-jmx-monitoring", "JMX 监控", "为 true 时启用 JMX 监控，可用于 Prometheus 等监控平台。", "bool", "false", "network", "1.16"),

            // === 远程管理 ===
            new("enable-rcon", "RCON", "是否启用远程控制台协议(RCON)。", "bool", "false", "remote", ""),
            new("rcon.port", "RCON 端口", "RCON 服务监听端口。", "int", "25575", "remote", ""),
            new("rcon.password", "RCON 密码", "RCON 认证密码。建议使用强密码。", "string", "", "remote", ""),
            new("broadcast-rcon-to-ops", "广播 RCON 给 OP", "为 true 时通过 RCON 执行的命令会向所有 OP 显示。", "bool", "true", "remote", ""),
            new("enable-query", "GameSpy 查询", "是否启用 GameSpy4 协议查询(用于第三方工具获取服务器信息)。", "bool", "false", "remote", ""),
            new("query.port", "查询端口", "GameSpy 查询服务端口。", "int", "25565", "remote", ""),

            // === 性能与日志 ===
            new("sync-chunk-writes", "同步区块写入", "为 true 时区块写入磁盘采用同步模式(更稳定，性能略低)。", "bool", "true", "performance", "1.16"),
            new("log-ips", "记录玩家 IP", "为 true 时在玩家加入时将其 IP 写入日志。", "bool", "true", "performance", "1.20.5"),

            // === 快照/测试 ===
            new("snooper-enabled", "Snooper 统计", "为 true 时服务器向 Mojang 匿名发送统计数据(已弃用)。", "bool", "true", "deprecated", "1.3", "1.14.4"),
            new("max-tick-time", "最大 tick 时间", "单 tick 超过该毫秒数(且服务器卡顿时)会触发看门狗警告与崩溃报告。\n-1 表示禁用。", "int", "60000", "performance", ""),
            new("rate-limit", "速率限制", "单个玩家每秒可发送的数据包数量上限。0 表示无限制。", "int", "0", "network", "1.14.4"),
            new("region-file-compression", "区块文件压缩", "区域文件(.mca)的压缩算法。\ndeflate=GZip；zlib=原始 zlib；none=不压缩。", "string", "deflate", "performance", "1.20.5"),
            new("pause-when-empty-seconds", "无玩家暂停", "服务器无玩家时多少秒后暂停 tick 以节省资源。\n-1 表示禁用，0 表示立即暂停。", "int", "60", "performance", "1.21.2"),
            new("text-filtering-config", "文本过滤配置", "聊天文本过滤的配置 JSON(企业版特性)。", "longstring", "", "players", "1.17"),
            new("text-filtering-version", "文本过滤版本", "文本过滤协议版本。", "int", "0", "players", "1.17"),
            new("initial-zeroed-particle-level", "粒子初始化", "内部调试选项。一般保持默认。", "int", "0", "deprecated", "1.19", "1.19"),
            new("hide-online-players", "隐藏在线玩家", "为 true 时不在 status 响应中显示玩家列表。", "bool", "false", "players", "1.16"),
            new("broadcast-console-to-ops", "广播控制台给 OP", "为 true 时控制台执行的命令会向所有 OP 显示。", "bool", "true", "remote", "1.16.2"),

            // === 资源包增强 ===
            new("resource-pack-hash", "资源包哈希(旧版)", "资源包 SHA-1 哈希(旧字段名)。\n1.10+ 改用 resource-pack-sha1。", "string", "", "players", "", "1.10"),
            new("resource-pack-id", "资源包 ID", "资源包的唯一 UUID，用于客户端缓存。\n格式为 8-4-4-4-12 的 UUID。", "string", "", "players", "1.20.2"),

            // === 数据包与调试 ===
            new("initial-enabled-packs", "初始启用数据包", "服务器启动时默认启用的数据包列表(逗号分隔)。\n默认仅 vanilla。", "string", "vanilla", "gameplay", "1.20.2"),
            new("initial-disabled-packs", "初始禁用数据包", "服务器启动时默认禁用的数据包列表(逗号分隔)。", "string", "", "gameplay", "1.20.2"),
            new("entity-broadcast-range-percentage", "实体广播范围", "实体广播范围的百分比。范围 10-1000。", "int", "100", "performance", "1.18.2"),
            new("debug", "调试模式", "为 true 时启用服务端调试模式(仅开发/测试用)。", "bool", "false", "deprecated", "1.20.5"),
            new("bug-report-link", "崩溃报告链接", "服务器崩溃时在日志中显示的错误报告链接。", "longstring", "", "basic", "1.20.5"),
            new("accepts-transfers", "接受转移", "为 true 时允许从其他服务器转移玩家。", "bool", "false", "network", "1.20.5"),
            new("enable-code-of-conduct", "启用行为准则", "为 true 时在首次加入时显示 Mojang 行为准则。", "bool", "false", "players", "1.20.5"),
            new("status-heartbeat-interval", "状态心跳间隔", "服务器向 Mojang 状态服务器发送心跳的间隔(秒)。0 表示禁用。", "int", "0", "network", "1.21"),

            // === 管理服务器 ===
            new("management-server-enabled", "管理服务器", "是否启用原版管理服务器(Management Server)。\n用于多服务器集群管理。", "bool", "false", "remote", "1.21.4"),
            new("management-server-host", "管理服务器地址", "管理服务器监听地址。", "string", "localhost", "remote", "1.21.4"),
            new("management-server-port", "管理服务器端口", "管理服务器监听端口。0 表示自动选择。", "int", "0", "remote", "1.21.4"),
            new("management-server-secret", "管理服务器密钥", "管理服务器的认证密钥(40 位字母数字)。留空则自动生成。", "string", "", "remote", "1.21.4"),
            new("management-server-allowed-origins", "管理服务器允许源", "管理服务器允许的 CORS 源列表(逗号分隔)。", "string", "", "remote", "1.21.4"),
            new("management-server-tls-enabled", "管理服务器 TLS", "是否为管理服务器启用 TLS 加密。\n默认启用，需提供密钥库文件。", "bool", "true", "remote", "1.21.9"),
            new("management-server-tls-keystore", "TLS 密钥库路径", "管理服务器 TLS 密钥库文件路径(PKCS12 格式)。", "string", "", "remote", "1.21.9"),
            new("management-server-tls-keystore-password", "TLS 密钥库密码", "管理服务器 TLS 密钥库密码。\n也可通过环境变量 MINECRAFT_MANAGEMENT_TLS_KEYSTORE_PASSWORD 设置。", "string", "", "remote", "1.21.9"),
        };

        /// <summary>支持的 MC 版本列表(用于版本下拉框)。</summary>
        public static readonly string[] SupportedVersions =
        {
            "1.8", "1.9", "1.10", "1.11", "1.12", "1.13", "1.14", "1.14.4",
            "1.15", "1.16", "1.17", "1.18", "1.19", "1.19.3", "1.19.4",
            "1.20", "1.20.5", "1.21", "1.21.2", "1.21.4", "1.21.5",
            "1.21.6", "1.21.7", "1.21.8", "1.21.9", "1.21.10", "1.21.11",
            "26.1", "26.2"
        };

        /// <summary>版本比较：返回 true 表示 actual >= target(忽略预发布)。</summary>
        private static bool VersionGe(string actual, string target)
        {
            if (string.IsNullOrEmpty(target)) return true;
            if (string.IsNullOrEmpty(actual)) return false;
            // 简单按 . 分段比较整数
            var aSegs = actual.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
            var tSegs = target.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
            int len = Math.Max(aSegs.Length, tSegs.Length);
            for (int i = 0; i < len; i++)
            {
                int a = i < aSegs.Length ? aSegs[i] : 0;
                int t = i < tSegs.Length ? tSegs[i] : 0;
                if (a > t) return true;
                if (a < t) return false;
            }
            return true; // equal
        }

        /// <summary>返回指定版本下可见的 server.properties 字段 schema 列表。</summary>
        public List<PropertiesFieldSchema> GetVisiblePropertiesFields(string version)
        {
            var result = new List<PropertiesFieldSchema>();
            foreach (var f in _serverPropertiesFields)
            {
                if (!string.IsNullOrEmpty(f.SinceVer) && !VersionGe(version ?? "26.2", f.SinceVer))
                    continue;
                if (!string.IsNullOrEmpty(f.RemovedVer) && VersionGe(version ?? "26.2", f.RemovedVer))
                    continue;
                result.Add(f);
            }
            return result;
        }

        /// <summary>解析 server.properties 文本为结构化节点。</summary>
        /// <param name="version">MC 服务端版本，用于隐藏不适用的字段。</param>
        /// <param name="externalDescriptions">来自 RtCli Repository 的外部描述字典(优先于 schema 内置 Description)。</param>
        public List<ConfigNode> ParseProperties(string content, string version, Dictionary<string, string>? externalDescriptions = null)
        {
            var visibleFields = GetVisiblePropertiesFields(version);
            var fieldMap = visibleFields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
            var result = new List<ConfigNode>();
            var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var lines = content.Replace("\r\n", "\n").Split('\n');
            var pendingComment = new List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var trimmed = raw.Trim();

                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    pendingComment.Clear();
                    continue;
                }
                if (trimmed.StartsWith("#"))
                {
                    pendingComment.Add(trimmed);
                    continue;
                }

                var eqIdx = trimmed.IndexOf('=');
                if (eqIdx <= 0) continue;

                var key = trimmed.Substring(0, eqIdx).Trim();
                var value = eqIdx + 1 < trimmed.Length ? trimmed.Substring(eqIdx + 1).Trim() : "";
                existingKeys.Add(key);

                var comment = string.Join("\n", pendingComment);
                pendingComment.Clear();

                if (fieldMap.TryGetValue(key, out var schema))
                {
                    // 优先使用外部描述(RtCli Repository)，其次回退到 schema.Description
                    var desc = schema.Description ?? "";
                    if (externalDescriptions != null && externalDescriptions.TryGetValue(schema.Key, out var extDesc) && !string.IsNullOrEmpty(extDesc))
                        desc = extDesc;

                    var node = new ConfigNode
                    {
                        Key = schema.DisplayName,
                        Path = schema.Key,
                        Depth = 0,
                        Indent = "",
                        LineIndex = i,
                        Comment = !string.IsNullOrEmpty(comment)
                            ? comment
                            : "#" + desc,
                        Value = value,
                        ValueType = schema.Type,
                        IsUnknown = false,
                    };
                    result.Add(node);
                }
                else
                {
                    // 未知字段(可能是当前版本不存在或第三方插件添加的)
                    // 尝试从 Repository 外部描述获取说明；否则使用文件内联注释
                    var unknownComment = comment;
                    if (externalDescriptions != null
                        && externalDescriptions.TryGetValue(key, out var extDesc)
                        && !string.IsNullOrEmpty(extDesc))
                    {
                        unknownComment = string.IsNullOrEmpty(comment)
                            ? "# " + extDesc
                            : comment + " | " + extDesc;
                    }

                    var node = new ConfigNode
                    {
                        Key = key,
                        Path = key,
                        Depth = 0,
                        Indent = "",
                        LineIndex = i,
                        Comment = unknownComment,
                        Value = value,
                        ValueType = GuessPropertiesType(value),
                        IsUnknown = true,
                    };
                    result.Add(node);
                }
            }

            // 为 schema 中存在但文件中缺失的字段补一个默认节点(便于在界面中新增)
            foreach (var schema in visibleFields)
            {
                if (!existingKeys.Contains(schema.Key))
                {
                    var desc = schema.Description ?? "";
                    if (externalDescriptions != null && externalDescriptions.TryGetValue(schema.Key, out var extDesc) && !string.IsNullOrEmpty(extDesc))
                        desc = extDesc;

                    result.Add(new ConfigNode
                    {
                        Key = schema.DisplayName,
                        Path = schema.Key,
                        Depth = 0,
                        Indent = "",
                        LineIndex = -1, // 不存在
                        Comment = "#" + desc,
                        Value = schema.Default,
                        ValueType = schema.Type,
                        IsUnknown = false,
                        // 用 Children 列表为空标记"缺失"便于前端区分
                    });
                }
            }

            return result;
        }

        /// <summary>把界面修改回写到 server.properties 文本。</summary>
        public string ApplyProperties(string content, List<ConfigChange> changes, string version)
        {
            if (changes == null || changes.Count == 0) return content;

            var lines = new List<string>(content.Replace("\r\n", "\n").Split('\n'));
            var appliedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) 修改已有行
            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;
                var eqIdx = trimmed.IndexOf('=');
                if (eqIdx <= 0) continue;

                var key = trimmed.Substring(0, eqIdx).Trim();
                var ch = changes.FirstOrDefault(c =>
                    string.Equals(c.Path, key, StringComparison.OrdinalIgnoreCase));
                if (ch == null) continue;

                appliedPaths.Add(ch.Path);
                var formatted = FormatPropertiesValue(ch.Value ?? "", ch.ValueType);
                lines[i] = $"{ch.Path}={formatted}";
            }

            // 2) 追加缺失行(原本不存在的字段)
            var sb = new StringBuilder();
            foreach (var ch in changes)
            {
                if (appliedPaths.Contains(ch.Path)) continue;
                if (string.IsNullOrEmpty(ch.Path)) continue;
                var formatted = FormatPropertiesValue(ch.Value ?? "", ch.ValueType);
                sb.AppendLine($"{ch.Path}={formatted}");
            }
            if (sb.Length > 0)
            {
                if (lines.Count > 0 && !string.IsNullOrEmpty(lines[^1]))
                    lines.Add("");
                var appended = sb.ToString().Replace("\r\n", "\n").TrimEnd('\n');
                lines.AddRange(appended.Split('\n'));
            }

            return string.Join("\n", lines);
        }

        private static string FormatPropertiesValue(string value, string type)
        {
            if (type == "bool")
            {
                return value?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true ? "true" : "false";
            }
            return value ?? "";
        }

        private static string GuessPropertiesType(string value)
        {
            if (string.IsNullOrEmpty(value)) return "string";
            if (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("false", StringComparison.OrdinalIgnoreCase))
                return "bool";
            if (int.TryParse(value, out _)) return "int";
            if (value.Length > 80) return "longstring";
            return "string";
        }

        private static SchemaProfile GetProfile(string profileName)
        {
            if (string.IsNullOrEmpty(profileName))
                return _profiles["config.yml"];
            // 先精确匹配完整文件名
            if (_profiles.TryGetValue(profileName, out var p))
                return p;
            // 再尝试用 basename(去掉目录前缀)匹配，如 config/paper-global.yml → paper-global.yml
            var basename = System.IO.Path.GetFileName(profileName);
            if (_profiles.TryGetValue(basename, out p))
                return p;
            return _profiles["config.yml"];
        }

        private void CollectFlat(List<ConfigNode> nodes, Dictionary<string, ConfigNode> flat)
        {
            foreach (var n in nodes)
            {
                flat[n.Path] = n;
                if (n.Children.Count > 0) CollectFlat(n.Children, flat);
            }
        }

        private string ResolveType(SchemaProfile profile, string path, string rawValue, bool hasChildren, bool hasListItems)
        {
            var schemaPath = profile.ToSchemaPath(path);
            if (profile.ValueTypes.TryGetValue(schemaPath, out var t)) return t;

            if (hasListItems) return "list";
            if (hasChildren) return "object";
            var v = rawValue.Trim();
            if (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("false", StringComparison.OrdinalIgnoreCase))
                return "bool";
            if (int.TryParse(v, out _)) return "int";
            return v.Length > 80 ? "longstring" : "string";
        }

        /// <summary>YAML 双引号字符串反转义；裸值原样返回。</summary>
        private string UnquoteYaml(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            {
                var inner = raw.Substring(1, raw.Length - 2);
                var sb = new StringBuilder();
                for (int i = 0; i < inner.Length; i++)
                {
                    if (inner[i] == '\\' && i + 1 < inner.Length)
                    {
                        var next = inner[i + 1];
                        sb.Append(next switch
                        {
                            'n' => "\n",
                            't' => "\t",
                            'r' => "\r",
                            _ => next.ToString(),
                        });
                        i++;
                    }
                    else sb.Append(inner[i]);
                }
                return sb.ToString();
            }
            if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
                return raw.Substring(1, raw.Length - 2).Replace("''", "'");
            return raw;
        }

        /// <summary>把字符串转义并以双引号包裹。</summary>
        private string QuoteYaml(string s)
        {
            if (s == null) s = "";
            var sb = new StringBuilder();
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private string FormatScalar(string value, string type)
        {
            return type switch
            {
                "bool" => (value?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true).ToString().ToLowerInvariant(),
                "int" => int.TryParse(value, out var n) ? n.ToString() : value,
                _ => QuoteYaml(value ?? ""),
            };
        }
    }

    /// <summary>单个配置文件 profile：已知键、动态字典父路径、值类型表。</summary>
    public class SchemaProfile
    {
        public string FileName { get; }
        public HashSet<string> TopLevelKeys { get; } = new();
        // 父路径 -> 节 schema；键为 schema path(动态段已用 * 替换)
        public Dictionary<string, SectionSchema> Sections { get; } = new(StringComparer.Ordinal);
        // 动态字典父路径(直接子键为动态名)
        public HashSet<string> DictParents { get; } = new();
        public Dictionary<string, string> ValueTypes { get; } = new(StringComparer.Ordinal);
        // 键路径 -> 中文说明(界面编辑时显示)
        public Dictionary<string, string> Comments { get; } = new(StringComparer.Ordinal);

        public SchemaProfile(string fileName) { FileName = fileName; }

        /// <summary>把真实路径中的动态段替换为 *。</summary>
        public string ToSchemaPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var segs = path.Split('.');
            for (int i = 1; i < segs.Length; i++)
            {
                var parent = string.Join('.', segs.Take(i));
                if (DictParents.Contains(parent)) segs[i] = "*";
            }
            return string.Join('.', segs);
        }

        /// <summary>判断指定父路径下的键是否未知。</summary>
        public bool IsUnknown(string parentPath, string key)
        {
            if (string.IsNullOrEmpty(parentPath))
                return !TopLevelKeys.Contains(key);
            // 动态字典父：直接子键为动态名(不视为未知)
            if (DictParents.Contains(parentPath))
                return false;
            var schemaParent = ToSchemaPath(parentPath);
            if (Sections.TryGetValue(schemaParent, out var section))
                return !section.Keys.Contains(key);
            return false; // 未注册的父路径：不强制标记
        }
    }

    /// <summary>节 schema：已知直接子键集合。</summary>
    public class SectionSchema
    {
        public HashSet<string> Keys { get; }

        public SectionSchema(IEnumerable<string> keys)
        {
            Keys = new HashSet<string>(keys, StringComparer.Ordinal);
        }
    }

    public class ConfigNode
    {
        public string Key { get; set; } = "";
        public string Path { get; set; } = "";
        public string ValueType { get; set; } = "string";
        public string? Value { get; set; }
        public List<string> ListValue { get; set; } = new();
        public string Comment { get; set; } = "";
        public int Depth { get; set; }
        public int LineIndex { get; set; } = -1;
        public int ListStartLine { get; set; } = -1;
        public int ListEndLine { get; set; } = -1;
        public bool IsUnknown { get; set; }

        [JsonIgnore]
        public string Indent { get; set; } = "";
        public List<ConfigNode> Children { get; set; } = new();
    }

    public class ConfigChange
    {
        public string Path { get; set; } = "";
        public string ValueType { get; set; } = "";
        public string? Value { get; set; }
        public List<string>? ListValue { get; set; }
    }

    public class ConfigSaveRequest
    {
        public string FileName { get; set; } = "config.yml";
        public List<ConfigChange> Changes { get; set; } = new();
    }

    /// <summary>
    /// server.properties 字段 schema 定义。
    /// 参考: https://zh.minecraft.wiki/w/服务端配置文件格式
    /// </summary>
    public class PropertiesFieldSchema
    {
        public string Key { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public string Type { get; }
        public string Default { get; }
        public string Category { get; }
        public string SinceVer { get; }
        public string RemovedVer { get; }

        public PropertiesFieldSchema(string key, string displayName, string description,
            string type, string defaultValue, string category, string sinceVer, string removedVer = "")
        {
            Key = key;
            DisplayName = displayName;
            Description = description;
            Type = type;
            Default = defaultValue;
            Category = category;
            SinceVer = sinceVer;
            RemovedVer = removedVer;
        }
    }

    /// <summary>MC server.properties 类别显示名映射。</summary>
    public static class PropertiesCategoryNames
    {
        public static readonly Dictionary<string, string> Map = new()
        {
            ["basic"] = "服务器基本设置",
            ["gameplay"] = "游戏模式与规则",
            ["players"] = "玩家管理",
            ["network"] = "网络与协议",
            ["remote"] = "远程管理",
            ["performance"] = "性能与日志",
            ["deprecated"] = "已弃用/内部",
        };
    }
}
