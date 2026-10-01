using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;
using RtCli.Modules.Extension;
using RtCli.Modules.Function;
using Spectre.Console;
using YamlDotNet.RepresentationModel;

namespace RtCli.Modules.Unit
{
    /// <summary>
    /// 面板后端适配层：为 gRPC 面板提供命令分发、配置读写、日志广播与命令列表能力。
    /// </summary>
    public static partial class Backend
    {
        /// <summary>
        /// 面板命令锁。锁范围(#6)已收窄为"只保护会修改共享状态的命令":
        ///   - 只读命令(rt status/.fx list/.ai list 等, 见 CommandRegistry 的 needsLock=false)完全不进锁;
        ///   - 命令回显日志、事件板记录、面板广播都已在锁外(DispatchCommand 中);
        ///   - 处理器自身的表格渲染仍随状态变更一起在锁内执行 —— 要彻底移出需让处理器返回
        ///     "渲染描述"而非直接 AnsiConsole.Write, 属表示层改写, 未在本轮做。
        /// </summary>
        private static readonly object _cmdLock = new();
        private static bool _initialized = false;

        // 面板可用命令列表（用于前端自动补全，同时作为命令描述映射来源）
        private static readonly (string Command, string Description)[] _knownCommands =
        {
            ("rt", "显示 RtCli 版本信息"),
            ("rt help", "查看命令列表"),
            ("rt status", "查看运行状态"),
            ("rt clients", "查看已连接面板"),
            ("rt clients kick", "断开指定面板客户端"),
            ("rt extensions", "查看已加载扩展"),
            ("rt extensions load", "加载扩展"),
            ("rt extensions unload", "卸载扩展"),
            ("rt reload", "重新加载程序"),
            ("rt start", "启动命令行交互"),
            ("rt stop", "停止（关闭服务端并退出）"),
            ("rt end", "结束程序"),
            ("rt about", "关于 RtCli"),
            (".cfg", "配置管理"),
            (".cfg reload", "热重载配置文件"),
            (".cfg show", "查看当前配置"),
            (".cfg clear", "清空配置"),
            (".lang", "切换语言"),
            (".lang zh_CN", "切换到简体中文"),
            (".lang en_US", "切换到 English"),
            (".status", "查看当前状态"),
            (".guide", "新手引导"),
            (".server", "服务端管理"),
            (".server list", "查看服务端列表"),
            (".server change", "切换当前服务端"),
            (".server add", "添加服务端"),
            (".server del", "删除服务端"),
            (".server get", "获取服务端信息"),
            (".server connect", "连接服务端(Rcon)"),
            (".server rcon connect", "远程连接RCON服务端(host/port/密码)"),
            (".server rcon disconnect", "断开远程RCON连接"),
            (".server rcon status", "查看远程RCON连接状态"),
            (".server status", "查看MC服务端状态"),
            (".server start", "启动MC服务端"),
            (".server stop", "停止MC服务端"),
            (".server restart", "重启MC服务端"),
            (".server detach", "断开MC服务端连接(Rcon)"),
            (".group", "群组服务器管理"),
            (".group add", "创建群组"),
            (".group del", "删除群组"),
            (".group list", "列出所有群组信息"),
            (".group set", "设置服务器到群组"),
            (".group unset", "解除服务器群组设置"),
            (".group build", "自动构建群组向导"),
            (".ai", "AI自动化管理"),
            (".ai start", "启动AI自动化管理"),
            (".ai stop", "停止AI自动化管理"),
            (".ai list", "列出AI任务及运行状态"),
            (".ai reload", "重载AI配置并重启任务"),
            (".ai run", "手动触发指定AI任务"),
            (".ai clear", "清除AI任务上下文缓存"),
            (".auto", "计划任务调度器"),
            (".auto start", "启动调度器"),
            (".auto stop", "停止调度器"),
            (".auto list", "列出所有计划任务"),
            (".auto on", "启用指定计划任务"),
            (".auto off", "禁用指定计划任务"),
            (".fx", "错误分析/诊断/过滤工具"),
            (".fx add", "添加/标记错误分析"),
            (".fx get", "获取并分析MC服务端错误日志"),
            (".fx list", "列出错误分析结果"),
            (".fx del", "删除所有错误分析结果"),
            (".fx clientguide", "客户端指引"),
            (".fx base", "基础诊断"),
            (".fx ai", "AI 错误分析"),
            (".fx filter", "过滤/管理插件与模组"),
            (".fx filter config", "备份/对照/还原配置文件"),
            (".fx filter plugin", "列出/启用/禁用插件"),
            (".fx filter mod", "列出/启用/禁用模组"),
            (".help", "查看功能命令帮助"),
            ("ct", "客户端工具"),
            ("ct list", "列出客户端"),
            ("ct unpack", "解包"),
            ("/", "发送原版 MC 命令(以 / 开头)"),
        };

        /// <summary>
        /// 初始化后端：挂载日志广播钩子，使 Output.Log 的输出同步推送到已连接的面板。
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            // 将日志广播挂载到 Output，面板通过 StreamLogs 即可实时收到日志
            // 投递仅入队(非阻塞)，日志线程不再等待任何网络写入
            Output.OnLogBroadcast = (timestamp, level, source, message, instanceId) =>
            {
                Connector.BroadcastLog(timestamp, level, source, message, instanceId);
            };

            // 启动玩家事件记录器(订阅 EventBus, 持久化到 panel_data.json)
            PlayerEventRecorder.Start();

            // 启动事件板记录器(订阅实例生命周期事件, 持久化到 panel_data.json)
            BoardEventRecorder.Start();

            Output.Log("面板后端适配层已初始化", 1, "Backend");
        }

        /// <summary>
        /// 分发并执行来自面板的命令，返回执行结果摘要。
        /// 输出策略：
        ///   - 简单文本命令 → Output.Log（Spectre标记语法，控制台彩色 + 面板纯文本）
        ///   - 表格命令 → AnsiConsole.Write(table) 控制台渲染 + BroadcastToPanel 纯文本广播
        ///   - 非标记的方括号一律用 [[ ]] 转义，用户内容用 Markup.Escape()
        /// </summary>
        public static (bool Success, string Result) DispatchCommand(string command, string? instanceId = null)
        {
            if (string.IsNullOrWhiteSpace(command))
                return (false, "命令不能为空");

            // 面板指定实例时, 本次命令产生的日志/事件/命令路由都归属该实例
            using var instanceScope = Function.InstanceContext.Push(
                string.IsNullOrWhiteSpace(instanceId) ? Function.InstanceContext.Current : instanceId);

            var cmd = command.Trim();

            (bool Success, string Result, string? PanelText) result;
            try
            {
                // [[面板]] — Spectre 中 [[ ]] 表示字面量方括号
                Output.Log($"[[面板]] 执行命令: {Markup.Escape(command)}", 1, "Backend");

                // 记录到事件板(标题为指令原文)
                BoardEventRecorder.RecordPanelCommand(command);

                EnsurePanelCommandsRegistered();

                if (CommandRegistry.TryResolvePanelCommand(cmd, out var handler, out string arg, out bool needsLock))
                {
                    // 锁只保护共享状态变更: 只读命令(rt status / .fx list 等)与长耗时命令不再排他,
                    // 会修改配置/调度器/AI运行器状态的命令才进命令锁; 面板广播统一在锁外完成。
                    if (needsLock)
                    {
                        lock (_cmdLock)
                        {
                            result = handler!(arg);
                        }
                    }
                    else
                    {
                        result = handler!(arg);
                    }
                }
                else if (cmd.StartsWith("/"))
                {
                    // MC 命令转发
                    var mcCommand = cmd[1..];
                    if (string.IsNullOrWhiteSpace(mcCommand))
                        return (false, "MC命令不能为空");

                    Analyzer.SendCommand(mcCommand, instanceId);
                    result = (true, $"已发送到MC服务端: /{mcCommand}", null);
                }
                else if (CommandRegistry.TryExecuteWithArgs(cmd))
                {
                    // 扩展注册的命令(支持参数前缀匹配)
                    result = (true, $"扩展命令已执行: {cmd}", null);
                }
                else
                {
                    Output.Log($"未知命令: [yellow]{Markup.Escape(cmd)}[/]，输入 rt help 查看命令列表", 2, "Backend");
                    return (false, $"未知命令: {cmd}");
                }
            }
            catch (Exception ex)
            {
                Output.Log($"命令执行异常: [red]{Markup.Escape(ex.Message)}[/]", 3, "Backend");
                return (false, $"命令执行异常: {ex.Message}");
            }

            // 锁外广播: 不再让所有面板命令因广播而串行
            if (result.PanelText != null)
                BroadcastToPanel(result.PanelText);

            return (result.Success, result.Result);
        }

        /// <summary>
        /// 向指定服务端发送MC命令(不带/前缀)。
        /// serverKey 为空或匹配当前服务端时使用已有连接；否则通过临时RCON连接发送。
        /// 支持逗号分隔的多个serverKey。
        /// </summary>
        public static (bool Success, string Result) SendMcCommand(string serverKey, string mcCommand)
        {
            if (string.IsNullOrWhiteSpace(mcCommand))
                return (false, "MC命令不能为空");

            var keys = (serverKey ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (keys.Length == 0)
                keys = new[] { Config.App.CurrentServer };

            var results = new System.Collections.Generic.List<string>();
            bool allSuccess = true;

            foreach (var key in keys)
            {
                var (ok, msg) = SendMcCommandSingle(key, mcCommand);
                if (!ok) allSuccess = false;
                results.Add($"[{key}] {(ok ? "OK" : "FAIL")}: {msg}");
            }

            return (allSuccess, string.Join("\n", results));
        }

        private static (bool Success, string Result) SendMcCommandSingle(string serverKey, string mcCommand)
        {
            // 目标实例已由 RtCli 托管(子进程 stdin / 常驻 RCON / 远程 RCON)：按其模式直接路由
            var target = string.IsNullOrEmpty(serverKey) ? Config.App.CurrentServer : serverKey;
            if (Analyzer.IsRunning(target))
            {
                Analyzer.SendCommand(mcCommand, target);
                return (true, $"已通过 {(Analyzer.UsesRconCommandsOf(target) ? "RCON" : "stdin")} 发送到 [{target}]: /{mcCommand}");
            }

            // 未托管：通过临时RCON连接发送
            if (!Config.App.ServerList.TryGetValue(target, out var server))
                return (false, $"未找到服务端标识: {target}");

            try
            {
                var rcon = new RconClient();
                if (!rcon.Connect(server.RconHost, server.RconPort, server.RconPassword))
                    return (false, $"RCON连接失败: {server.RconHost}:{server.RconPort}");

                string response = rcon.SendCommand(mcCommand);
                rcon.Disconnect();
                return (true, string.IsNullOrEmpty(response) ? $"已发送: /{mcCommand}" : response);
            }
            catch (Exception ex)
            {
                return (false, $"发送失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 直接向面板广播纯文本日志，不经过 Output.Log（避免控制台重复输出）。
        /// 用于表格类命令：控制台用 AnsiConsole.Write(table) 渲染，面板用此方法广播纯文本摘要。
        /// </summary>
        private static void BroadcastToPanel(string message, int level = 1)
        {
            try
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");
                Connector.BroadcastLog(timestamp, level, "Backend", message, Function.InstanceContext.Current);
            }
            catch
            {
                // 广播失败不影响主流程
            }
        }

        /// <summary>
        /// 读取 config.yml 文件内容。
        /// </summary>
        public static (bool Success, string Content, string Message) GetConfigContent()
        {
            return GetConfigFileContent("config.yml");
        }

        /// <summary>
        /// 保存 config.yml 文件内容并热重载。
        /// </summary>
        public static (bool Success, string Message) SaveConfigContent(string content)
        {
            return SaveConfigFileContent("config.yml", content);
        }

        // 可被面板编辑的配置文件白名单(文件名 -> 显示信息)
        private static readonly (string FileName, string DisplayName, string Description)[] _editableConfigFiles =
        {
            ("config.yml", "主配置", "RtCli 主配置文件：环境检查、服务端列表、备份、玩家事件等"),
            ("regex_settings.yml", "正则配置", "正则表达式配置：错误匹配、客户端引导、自定义条目"),
            ("ai_settings.yml", "AI配置", "AI 自动化管理配置：模型、提示词、自动监测任务"),
            ("scheduler_settings.yml", "调度器配置", "计划任务调度器配置：定时与事件触发的自动化任务"),
            ("scripts_settings.yml", "脚本配置", "脚本引擎配置：C#/Python 脚本项与触发事件"),
            ("support.yml", "Support配置", "Support扩展配置：LuckPerms权限变更监测(MySQL轮询)"),
        };

        /// <summary>
        /// 列出可编辑的配置文件清单。
        /// </summary>
        public static IEnumerable<(string FileName, string DisplayName, string Description)> ListConfigFiles()
        {
            return _editableConfigFiles;
        }

        /// <summary>
        /// 读取指定配置文件内容(白名单限制)。
        /// </summary>
        public static (bool Success, string Content, string Message) GetConfigFileContent(string fileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileName) || !_editableConfigFiles.Any(f => f.FileName == fileName))
                    return (false, "", $"不支持的配置文件: {fileName}");

                var filePath = Path.Combine(Config.DataPath, fileName);
                if (!File.Exists(filePath))
                    return (false, "", $"配置文件不存在: {fileName}");
                var content = File.ReadAllText(filePath);
                return (true, content, "读取成功");
            }
            catch (Exception ex)
            {
                return (false, "", $"读取失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 保存指定配置文件内容并热重载所有配置。
        /// </summary>
        public static (bool Success, string Message) SaveConfigFileContent(string fileName, string content)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileName) || !_editableConfigFiles.Any(f => f.FileName == fileName))
                    return (false, $"不支持的配置文件: {fileName}");
                if (string.IsNullOrEmpty(content))
                    return (false, "配置内容不能为空");

                var filePath = Path.Combine(Config.DataPath, fileName);

                // 备份当前配置
                if (File.Exists(filePath))
                {
                    var backupPath = filePath + ".bak";
                    File.Copy(filePath, backupPath, true);
                }

                AtomicFile.WriteAllText(filePath, content);
                Output.Log($"配置文件 [{Markup.Escape(fileName)}] 已由面板保存，正在热重载...", 1, "Backend");

                Config.ReloadAll();
                return (true, $"{fileName} 已保存并热重载");
            }
            catch (Exception ex)
            {
                Output.Log($"保存配置失败: [red]{Markup.Escape(ex.Message)}[/]", 3, "Backend");
                return (false, $"保存失败: {ex.Message}");
            }
        }

        // ===== AI 技能管理(读写 ai_settings.yml 的 ServerAutoAi.skills) =====

        /// <summary>
        /// 获取 AI 技能列表。
        /// </summary>
        public static List<AiSkill> GetAiSkills()
        {
            ContentManager.Initialize();
            return (ContentManager.Ai.ServerAutoAi.Skills ?? new List<AiSkill>()).ToList();
        }

        /// <summary>
        /// 新增或编辑一个 AI 技能并写回 ai_settings.yml。
        /// originalName 为空表示新增; 非空表示编辑(用于在改名时定位原技能)。
        /// </summary>
        public static (bool Success, string Message) SaveAiSkill(string originalName, string name, string description, string behavior)
        {
            ContentManager.Initialize();
            name = (name ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                return (false, "技能名称不能为空");

            var skills = ContentManager.Ai.ServerAutoAi.Skills;
            if (skills == null)
                skills = ContentManager.Ai.ServerAutoAi.Skills = new List<AiSkill>();

            AiSkill? target = null;
            if (!string.IsNullOrWhiteSpace(originalName))
                target = skills.FirstOrDefault(s => string.Equals(s.Name, originalName.Trim(), StringComparison.Ordinal));

            if (skills.Any(s => !ReferenceEquals(s, target) && string.Equals(s.Name, name, StringComparison.Ordinal)))
                return (false, $"已存在同名技能: {name}");

            if (target == null)
            {
                target = new AiSkill();
                skills.Add(target);
            }
            target.Name = name;
            target.Description = description ?? "";
            target.Behavior = behavior ?? "";

            return PersistAiSettings();
        }

        /// <summary>
        /// 删除指定 AI 技能。
        /// </summary>
        public static (bool Success, string Message) DeleteAiSkill(string name)
        {
            ContentManager.Initialize();
            name = (name ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                return (false, "技能名称不能为空");

            var skills = ContentManager.Ai.ServerAutoAi.Skills;
            if (skills == null || skills.RemoveAll(s => string.Equals(s.Name, name, StringComparison.Ordinal)) == 0)
                return (false, $"技能不存在: {name}");

            return PersistAiSettings();
        }

        /// <summary>
        /// 导入技能(JSON/YAML 文本)。overwrite=true 时同名覆盖, 否则跳过同名。
        /// </summary>
        public static (bool Success, string Message, List<AiSkill> Skills) ImportAiSkills(string content, bool overwrite)
        {
            ContentManager.Initialize();
            var (ok, message, parsed) = ParseSkillsContent(content);
            if (!ok)
                return (false, message, GetAiSkills());

            var skills = ContentManager.Ai.ServerAutoAi.Skills;
            if (skills == null)
                skills = ContentManager.Ai.ServerAutoAi.Skills = new List<AiSkill>();

            int added = 0, updated = 0, skipped = 0;
            foreach (var skill in parsed)
            {
                var existing = skills.FirstOrDefault(s => string.Equals(s.Name, skill.Name, StringComparison.Ordinal));
                if (existing == null)
                {
                    skills.Add(skill);
                    added++;
                }
                else if (overwrite)
                {
                    existing.Description = skill.Description;
                    existing.Behavior = skill.Behavior;
                    updated++;
                }
                else
                {
                    skipped++;
                }
            }

            if (added == 0 && updated == 0)
                return (true, $"未导入任何技能(跳过同名 {skipped} 个)", GetAiSkills());

            var result = PersistAiSettings();
            if (!result.Success)
                return (false, result.Message, GetAiSkills());
            return (true, $"导入完成: 新增 {added} 个, 覆盖 {updated} 个, 跳过 {skipped} 个", GetAiSkills());
        }

        // ===== AI 工作流(读写 ai_settings.yml 中 ServerAutoAi 的任务列表与运行参数) =====

        /// <summary>合法触发条件示例(供编辑器下拉; 引擎支持 and/or 组合、{rt.变量}、event: 事件)</summary>
        public static readonly string[] AiTriggerTemplates =
        {
            "always", "server_running", "server_stopped", "crash_detected",
            "{rt.tps} < 18", "{rt.cpu} > 85", "{rt.memory} > 80", "{rt.players} == 0", "{rt.idle_minutes} > 360",
            "event: PlayerJoinEvent", "event: PlayerLeaveEvent", "event: ServerStartEvent", "event: ServerCrashEvent",
            "event: PlayerJoinEvent and {rt.player_name} == \"Steve\"",
            "{rt.tps} < 18 or {rt.cpu} > 85"
        };

        /// <summary>
        /// 校验 AI 任务触发条件是否合法(与 IntelligenceAiRunner.EvaluateTriggerExpression 的语法一致):
        /// 条件 (and|or 条件)*; 单个条件为关键字 / event:&lt;事件名&gt; / {rt.变量}|裸变量名 运算符 值。
        /// </summary>
        private static bool IsValidAiTrigger(string trigger)
        {
            var raw = (trigger ?? "").Trim();
            if (raw.Length == 0) return false;

            var leaves = new List<string>();
            foreach (var orPart in Regex.Split(raw, @"\s+or\s+", RegexOptions.IgnoreCase))
                foreach (var andPart in Regex.Split(orPart, @"\s+and\s+", RegexOptions.IgnoreCase))
                {
                    var leaf = andPart.Trim();
                    if (leaf.Length > 0) leaves.Add(leaf);
                }
            if (leaves.Count == 0) return false;

            bool hasCrash = false;
            foreach (var leaf in leaves)
            {
                if (!IsValidAiTriggerLeaf(leaf, out bool isCrash)) return false;
                hasCrash |= isCrash;
            }
            // crash_detected 依赖 Analyzer 事件推送, 组合其它条件后事件推送不会求值整个表达式, 故禁止组合
            if (hasCrash && leaves.Count > 1) return false;
            return true;
        }

        private static bool IsValidAiTriggerLeaf(string leaf, out bool isCrash)
        {
            isCrash = false;
            var lower = leaf.ToLowerInvariant();

            if (lower is "always" or "true" or "1" or "server_running" or "server_stopped") return true;
            if (lower == "crash_detected") { isCrash = true; return true; }

            // 事件条件: event: <事件名>
            if (lower.StartsWith("event:"))
            {
                var name = leaf.Substring(6).Trim().Trim('"', '\'');
                return EventNames.IsValidEventName(name);
            }

            // 比较表达式: {rt.变量} 或 裸变量名 运算符 值
            var m = Regex.Match(leaf,
                @"^(?:\{rt\.(?<v1>[A-Za-z_]\w*)\}|(?<v2>[A-Za-z_]\w*))\s*(?<op>==|!=|>=|<=|>|<|=|contains|startsWith|endsWith)\s*.+$",
                RegexOptions.IgnoreCase);
            if (!m.Success) return false;

            // {rt.变量} 中的变量名可为运行时变量或事件参数, 不做白名单; 裸变量名仅限已知运行时变量
            if (m.Groups["v1"].Success) return true;
            return IsKnownTriggerVariable(m.Groups["v2"].Value);
        }

        private static bool IsKnownTriggerVariable(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "tps":
                case "cpu":
                case "memory":
                case "players":
                case "player_count":
                case "idle_minutes":
                case "uptime_minutes":
                case "server_running":
                case "server_key":
                case "server_name":
                case "mode":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 读取 AI 工作流配置: 运行参数 + 任务列表。密钥只回传掩码, 不回传明文。
        /// </summary>
        public static (bool Success, string Message, AiRuntimeView Runtime, List<KeyValuePair<string, AiTask>> Tasks,
            List<string> AvailableTools, string FilePath, bool RunnerRunning, List<string> AvailableEvents) GetAiWorkflow()
        {
            try
            {
                ContentManager.Initialize();
                var auto = ContentManager.Ai.ServerAutoAi ?? new AiServerAuto();

                var runtime = new AiRuntimeView
                {
                    Prompt = auto.Prompt ?? "",
                    MaxTokens = auto.MaxTokens,
                    ApiEndpoint = auto.ApiEndpoint ?? "",
                    ApiKey = auto.ApiKey ?? "",
                    Model = auto.Model ?? "",
                    Temperature = auto.Temperature,
                    RequestTimeoutSeconds = auto.RequestTimeoutSeconds,
                    RetryOnError = auto.RetryOnError,
                    RetryCount = auto.RetryCount,
                    SaveResponse = auto.SaveResponse,
                    ContextCacheSize = auto.ContextCacheSize,
                    MaxToolRounds = auto.MaxToolRounds
                };

                var tasks = (auto.Tasks ?? new Dictionary<string, AiTask>())
                    .Select(kv => new KeyValuePair<string, AiTask>(kv.Key, kv.Value))
                    .ToList();

                var tools = Intelligence.AiTools.BuiltInTools.ToList();

                // 合法事件清单: 内置事件 + CustomPlayerEvent 通配名 + player_event.customs 自定义事件
                var events = new List<string>(EventNames.BuiltInEvents) { EventNames.CustomPlayerEventName };
                var customs = Config.App?.PlayerEvent?.Customs;
                if (customs != null) events.AddRange(customs.Keys);

                return (true, "OK", runtime, tasks, tools,
                    Path.Combine(Config.DataPath, "ai_settings.yml"),
                    Intelligence.AiAutoRunner.IsRunning,
                    events);
            }
            catch (Exception ex)
            {
                return (false, $"读取失败: {ex.Message}", new AiRuntimeView(), new List<KeyValuePair<string, AiTask>>(),
                    new List<string>(), "", false, new List<string>());
            }
        }

        /// <summary>生成密钥掩码串(仅保留首尾各 4 位)</summary>
        public static string MaskApiKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            return key.Length <= 8 ? "****" : key.Substring(0, 4) + "****" + key.Substring(key.Length - 4);
        }

        /// <summary>
        /// 新增或编辑一个 AI 自动化任务并写回 ai_settings.yml。
        /// originalKey 非空且与 key 不同时视为改名, 会移除旧键。
        /// </summary>
        public static (bool Success, string Message) SaveAiTask(string originalKey, string key, AiTask task)
        {
            if (task == null)
                return (false, "任务配置不能为空");

            ContentManager.Initialize();

            key = (key ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(key))
                return (false, "任务标识不能为空");
            if (!Regex.IsMatch(key, @"^[a-z0-9_]+$"))
                return (false, "任务标识只能包含小写字母、数字与下划线");

            if (!IsValidAiTrigger(task.Trigger))
                return (false, $"触发条件无效: {task.Trigger}；合法值: {string.Join(" / ", AiTriggerTemplates)}");

            var interval = (task.Interval ?? "").Trim();
            if (interval.Length == 0)
                return (false, "Cron 表达式不能为空");
            if (interval.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length != 6)
                return (false, $"Cron 表达式必须为 6 段(秒 分 时 日 月 周): {interval}");

            var tasks = ContentManager.Ai.ServerAutoAi.Tasks;
            if (tasks == null)
                tasks = ContentManager.Ai.ServerAutoAi.Tasks = new Dictionary<string, AiTask>();

            var normalizedOriginal = (originalKey ?? "").Trim().ToLowerInvariant();
            if (normalizedOriginal.Length > 0 && normalizedOriginal != key)
                tasks.Remove(normalizedOriginal);

            tasks[key] = task;
            return PersistAiSettings();
        }

        /// <summary>
        /// 删除指定 AI 自动化任务。
        /// </summary>
        public static (bool Success, string Message) DeleteAiTask(string key)
        {
            ContentManager.Initialize();

            key = (key ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(key))
                return (false, "任务标识不能为空");

            var tasks = ContentManager.Ai.ServerAutoAi.Tasks;
            if (tasks == null || !tasks.Remove(key))
                return (false, $"任务不存在: {key}");

            return PersistAiSettings();
        }

        /// <summary>
        /// 保存 AI 工作流运行参数(AI 模型/接口/超时/重试等)。
        /// ApiKey 为空或为掩码串时保持原值, 避免回显掩码覆盖真实密钥。
        /// </summary>
        public static (bool Success, string Message) SaveAiRuntime(AiRuntimeView runtime)
        {
            if (runtime == null)
                return (false, "运行参数不能为空");

            ContentManager.Initialize();
            var auto = ContentManager.Ai.ServerAutoAi ?? (ContentManager.Ai.ServerAutoAi = new AiServerAuto());

            auto.Prompt = runtime.Prompt ?? "";
            auto.MaxTokens = runtime.MaxTokens;
            auto.ApiEndpoint = runtime.ApiEndpoint ?? "";

            var newKey = (runtime.ApiKey ?? "").Trim();
            if (newKey.Length > 0 && !newKey.Contains("****"))
                auto.ApiKey = newKey;

            auto.Model = runtime.Model ?? "";
            auto.Temperature = runtime.Temperature;
            auto.RequestTimeoutSeconds = runtime.RequestTimeoutSeconds;
            auto.RetryOnError = runtime.RetryOnError;
            auto.RetryCount = runtime.RetryCount;
            auto.SaveResponse = runtime.SaveResponse;
            auto.ContextCacheSize = runtime.ContextCacheSize;
            auto.MaxToolRounds = runtime.MaxToolRounds;

            return PersistAiSettings();
        }

        // ===== AI 记忆管理(读取 memory.json 条目, 读写记忆设置) =====

        /// <summary>
        /// 读取 memory.json 中的全部记忆(时间/主题/内容)。
        /// </summary>
        public static List<RtCli.Modules.Function.AiMemoryManager.MemoryEntry> GetAiMemories()
        {
            return RtCli.Modules.Function.AiMemoryManager.Load();
        }

        /// <summary>
        /// 获取 AI 记忆设置。
        /// </summary>
        public static AiMemorySetting GetAiMemorySettings()
        {
            ContentManager.Initialize();
            return ContentManager.Ai.ServerAutoAi.Memory ?? new AiMemorySetting();
        }

        /// <summary>
        /// 保存 AI 记忆设置到 ai_settings.yml 并热重载。
        /// </summary>
        public static (bool Success, string Message) SaveAiMemorySettings(AiMemorySetting settings)
        {
            if (settings == null)
                return (false, "记忆设置不能为空");

            ContentManager.Initialize();
            ContentManager.Ai.ServerAutoAi.Memory = settings;
            return PersistAiSettings();
        }

        /// <summary>
        /// 清空 memory.json 中的所有记忆。
        /// </summary>
        public static (bool Success, string Message) ClearAiMemories()
        {
            try
            {
                RtCli.Modules.Function.AiMemoryManager.Save(new List<RtCli.Modules.Function.AiMemoryManager.MemoryEntry>());
                return (true, "已清空记忆");
            }
            catch (Exception ex)
            {
                return (false, $"清空失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 将内存中的 AI 配置写回 ai_settings.yml 并热重载。
        /// </summary>
        private static (bool Success, string Message) PersistAiSettings()
        {
            try
            {
                if (!ContentManager.SaveAiSettings())
                    return (false, "写入 ai_settings.yml 失败");
                Config.ReloadAll();
                return (true, "已写入 ai_settings.yml");
            }
            catch (Exception ex)
            {
                return (false, $"保存失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析技能导入内容: 支持 JSON(数组 / 含 skills 的对象 / 单对象)与 YAML(列表 / 含 skills 的对象)。
        /// </summary>
        private static (bool Success, string Message, List<AiSkill> Skills) ParseSkillsContent(string content)
        {
            var result = new List<AiSkill>();
            if (string.IsNullOrWhiteSpace(content))
                return (false, "导入内容为空", result);

            // 1) JSON
            try
            {
                CollectSkillsFromJson(JToken.Parse(content), result);
                if (result.Count > 0)
                    return (true, "", result);
            }
            catch { }

            // 2) YAML
            try
            {
                var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
                    .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                var list = deserializer.Deserialize<List<AiSkill>>(content);
                if (list != null)
                {
                    foreach (var s in list)
                    {
                        if (s != null && !string.IsNullOrWhiteSpace(s.Name))
                            result.Add(new AiSkill { Name = s.Name.Trim(), Description = s.Description ?? "", Behavior = s.Behavior ?? "" });
                    }
                }
                if (result.Count > 0)
                    return (true, "", result);

                var wrapper = deserializer.Deserialize<AiSkillImportWrapper>(content);
                if (wrapper?.Skills != null)
                {
                    foreach (var s in wrapper.Skills)
                    {
                        if (s != null && !string.IsNullOrWhiteSpace(s.Name))
                            result.Add(new AiSkill { Name = s.Name.Trim(), Description = s.Description ?? "", Behavior = s.Behavior ?? "" });
                    }
                }
                if (result.Count > 0)
                    return (true, "", result);
            }
            catch { }

            return (false, "无法解析技能内容(支持 JSON 或 YAML 的技能数组/对象)", result);
        }

        private static void CollectSkillsFromJson(JToken token, List<AiSkill> result)
        {
            if (token is JArray arr)
            {
                foreach (var item in arr)
                    AddSkillFromJson(item, result);
                return;
            }
            if (token is JObject obj)
            {
                var skillsToken = obj["skills"] ?? obj["Skills"];
                if (skillsToken is JArray skillsArr)
                {
                    foreach (var item in skillsArr)
                        AddSkillFromJson(item, result);
                    return;
                }
                AddSkillFromJson(obj, result);
            }
        }

        private static void AddSkillFromJson(JToken token, List<AiSkill> result)
        {
            if (token is not JObject obj) return;
            var name = (obj["name"] ?? obj["Name"])?.ToString()?.Trim() ?? "";
            if (string.IsNullOrEmpty(name)) return;
            result.Add(new AiSkill
            {
                Name = name,
                Description = (obj["description"] ?? obj["Description"])?.ToString() ?? "",
                Behavior = (obj["behavior"] ?? obj["Behavior"] ?? obj["behaviour"] ?? obj["Behaviour"])?.ToString() ?? ""
            });
        }

        // 系统状态缓存(单飞采集): 面板轮询间隔通常 1-2 秒, 而单次采集可能耗时数秒
        private static readonly SemaphoreSlim _systemStatsGate = new(1, 1);
        private static readonly object _systemStatsCacheLock = new();
        private static readonly TimeSpan SystemStatsCacheTtl = TimeSpan.FromSeconds(3);
        private static (bool Success, string Message, double CpuUsage, double MemUsedMb, double MemTotalMb,
                       List<(string Key, string Name, bool Running, double Tps, string TpsStatus, int Pid, double McMemMb,
                             double Tps1m, double Tps5m, double Tps15m, double McCpuUsage, int PlayerCount, int PlayerMax)> Servers)?
            _systemStatsCache;
        private static DateTime _systemStatsCacheAt = DateTime.MinValue;

        /// <summary>
        /// 采集系统状态：主机 CPU/内存 + 各 MC 服务端 TPS、玩家数、进程 CPU/内存。
        /// TPS 与玩家数通过向 MC 服务端发送 tps/list 命令获取(带 10 秒缓存节流)。
        /// 结果做短期缓存并单飞采集: 面板/桥接高频轮询时不会重复执行 WMI 与最长 6 秒的 TPS/玩家查询。
        /// </summary>
        public static (bool Success, string Message, double CpuUsage, double MemUsedMb, double MemTotalMb,
                       List<(string Key, string Name, bool Running, double Tps, string TpsStatus, int Pid, double McMemMb,
                             double Tps1m, double Tps5m, double Tps15m, double McCpuUsage, int PlayerCount, int PlayerMax)> Servers)
            GetSystemStats()
        {
            if (TryGetCachedSystemStats(out var cached)) return cached;

            // 单飞: 并发轮询只触发一次采集, 其余请求等待复用结果, 避免线程池被阻塞式采集占满
            _systemStatsGate.Wait();
            try
            {
                if (TryGetCachedSystemStats(out cached)) return cached;

                var result = CollectSystemStats();
                lock (_systemStatsCacheLock)
                {
                    _systemStatsCache = result;
                    _systemStatsCacheAt = DateTime.UtcNow;
                }
                return result;
            }
            finally
            {
                _systemStatsGate.Release();
            }
        }

        private static bool TryGetCachedSystemStats(out (bool Success, string Message, double CpuUsage, double MemUsedMb, double MemTotalMb,
                       List<(string Key, string Name, bool Running, double Tps, string TpsStatus, int Pid, double McMemMb,
                             double Tps1m, double Tps5m, double Tps15m, double McCpuUsage, int PlayerCount, int PlayerMax)> Servers) result)
        {
            lock (_systemStatsCacheLock)
            {
                if (_systemStatsCache.HasValue &&
                    DateTime.UtcNow - _systemStatsCacheAt < SystemStatsCacheTtl)
                {
                    result = _systemStatsCache.Value;
                    return true;
                }
            }
            result = default;
            return false;
        }

        private static (bool Success, string Message, double CpuUsage, double MemUsedMb, double MemTotalMb,
                       List<(string Key, string Name, bool Running, double Tps, string TpsStatus, int Pid, double McMemMb,
                             double Tps1m, double Tps5m, double Tps15m, double McCpuUsage, int PlayerCount, int PlayerMax)> Servers)
            CollectSystemStats()
        {
            try
            {
                double cpu = Intelligence.AiTools.GetCpuUsage();
                var (usedMb, totalMb) = Intelligence.AiTools.GetMemoryUsage();

                var servers = new List<(string, string, bool, double, string, int, double, double, double, double, double, int, int)>();
                var serverGate = new object();
                var keys = Config.App.ServerList.Keys.ToList();

                // 多实例并发采集: 每个实例独立查询 TPS/玩家数, 并行执行避免串行累加超时(单实例最长约6秒)
                Parallel.ForEach(keys, new ParallelOptions { MaxDegreeOfParallelism = 4 }, key =>
                {
                    var entry = Config.App.ServerList[key];
                    var name = string.IsNullOrEmpty(entry.ServerName) ? key : entry.ServerName;
                    // 运行状态按实例自身运行时分判断, 而不是按"当前选中"的实例
                    bool running = Analyzer.IsRunning(key);
                    double tps = -1;
                    string tpsStatus = "未运行";
                    int pid = 0;
                    double mcMemMb = 0;
                    double tps1m = -1, tps5m = -1, tps15m = -1;
                    double mcCpu = -1;
                    int playerCount = -1, playerMax = -1;

                    if (running)
                    {
                        tpsStatus = "运行中";

                        // TPS：发送 tps 命令解析(带缓存节流, 按实例独立)
                        try
                        {
                            var (t1, t5, t15) = Analyzer.QueryTps(3000, key);
                            tps1m = t1; tps5m = t5; tps15m = t15;
                            tps = t1; // 兼容字段
                            if (t1 >= 0)
                                tpsStatus = $"{t1:0.0}" + (t5 >= 0 ? $" / {t5:0.0} / {t15:0.0}" : "");
                            else
                                tpsStatus = "运行中(TPS未获取,需Spark等插件)";
                        }
                        catch { }

                        // 玩家数：RM 优先管理协议 server/status, 其余走 list 命令(带缓存节流)
                        try
                        {
                            var (count, max) = Analyzer.QueryPlayerCount(3000, key);
                            playerCount = count;
                            playerMax = max;
                        }
                        catch { }

                        // MC 进程 CPU 与内存
                        try
                        {
                            var proc = Analyzer.GetServerProcess(key);
                            if (proc != null && !proc.HasExited)
                            {
                                pid = proc.Id;
                                mcMemMb = proc.WorkingSet64 / (1024.0 * 1024.0);
                            }
                            mcCpu = Analyzer.GetMcCpuUsage(key);
                        }
                        catch { }
                    }

                    lock (serverGate)
                    {
                        servers.Add((key, name, running, tps, tpsStatus, pid, mcMemMb,
                                     tps1m, tps5m, tps15m, mcCpu, playerCount, playerMax));
                    }
                });

                // 并行采集后恢复 server_list 原始顺序
                servers = servers.OrderBy(s => keys.IndexOf(s.Item1)).ToList();

                return (true, "OK", cpu, usedMb, totalMb, servers);
            }
            catch (Exception ex)
            {
                return (false, $"采集失败: {ex.Message}", -1, -1, -1,
                    new List<(string, string, bool, double, string, int, double, double, double, double, double, int, int)>());
            }
        }

        // MC 服务端已知配置文件白名单(文件名, 显示名, 描述, 分类, 是否可写)
        private static readonly (string FileName, string DisplayName, string Description, string Category, bool Editable)[] _knownServerFiles =
        {
            ("server.properties", "服务端属性", "MC 服务端核心配置：端口、模式、世界、白名单等", "properties", true),
            ("ops.json", "管理员列表", "OP 玩家列表及权限等级", "json", true),
            ("whitelist.json", "白名单", "白名单玩家列表(需 white-list=true 才生效)", "whitelist", true),
            ("banned-players.json", "封禁玩家", "被封禁的玩家列表", "ban", true),
            ("banned-ips.json", "封禁IP", "被封禁的 IP 地址列表", "ban", true),
            ("eula.txt", "EULA协议", "Mojang EULA 同意标志(eula=true 才能启动)", "text", true),
            ("commands.yml", "命令配置", "Bukkit/Spigot 命令相关配置", "yaml", true),
            ("bukkit.yml", "Bukkit配置", "Bukkit 服务端配置", "yaml", true),
            ("spigot.yml", "Spigot配置", "Spigot/Paper 服务端配置", "yaml", true),
            ("paper-global.yml", "Paper全局配置", "Paper 全局配置(1.19+，旧版在根目录)", "yaml", true),
            ("paper-world-defaults.yml", "Paper世界默认", "Paper 世界默认配置(1.19+，旧版在根目录)", "yaml", true),
            ("purpur.yml", "Purpur配置", "Purpur 服务端配置", "yaml", true),
            ("pufferfish.yml", "Pufferfish配置", "Pufferfish 服务端配置", "yaml", true),
            ("usercache.json", "用户缓存", "玩家用户名缓存(可清理,运行时自动重建)", "json", true),
            ("server-icon.png", "服务器图标", "服务器图标 PNG(64x64,二进制)", "binary", false),
            // config/ 目录下的配置文件(1.20+ Paper 系服务端)
            ("config/paper-global.yml", "Paper全局配置", "Paper 全局配置(config 目录)", "yaml", true),
            ("config/paper-world-defaults.yml", "Paper世界默认", "Paper 世界默认配置(config 目录)", "yaml", true),
            ("config/gale-global.yml", "Gale全局配置", "Gale 服务端全局配置", "yaml", true),
            ("config/gale-world-defaults.yml", "Gale世界默认", "Gale 服务端世界默认配置", "yaml", true),
            ("config/leaf-global.yml", "Leaf全局配置", "Leaf 服务端全局配置", "yaml", true),
            ("config/leaf-world-defaults.yml", "Leaf世界默认", "Leaf 服务端世界默认配置", "yaml", true),
        };

        /// <summary>允许编辑的文本配置文件扩展名</summary>
        private static readonly string[] _editableExtensions = { ".yml", ".yaml", ".json", ".properties", ".txt", ".toml", ".log" };

        /// <summary>扫描时应跳过的目录名(世界数据、缓存等)</summary>
        private static readonly string[] _skipDirs = { "world", "world_nether", "world_the_end", "region", "playerdata", "stats", "data", "entities", "poi", "cache", ".cache", "session.lock", "backups", "logs", ".mixin", "libraries", "versions", "assets", "downloads" };

        /// <summary>判断文件名是否允许编辑(已知白名单 或服务端目录内的文本配置文件)。</summary>
        private static bool IsAllowedServerFile(string fileName, out string category, out bool editable, out string displayName, out string description)
        {
            // 检查白名单
            var meta = _knownServerFiles.FirstOrDefault(f => f.FileName == fileName);
            if (meta != default)
            {
                category = meta.Category;
                editable = meta.Editable;
                displayName = meta.DisplayName;
                description = meta.Description;
                return true;
            }
            // 允许任意文本配置文件
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (_editableExtensions.Contains(ext))
            {
                category = ext == ".json" ? "json" : (ext == ".properties" ? "properties" : (ext == ".toml" ? "toml" : (ext == ".log" ? "log" : "yaml")));
                editable = true;
                displayName = Path.GetFileName(fileName);
                description = "";
                return true;
            }
            category = "";
            editable = false;
            displayName = "";
            description = "";
            return false;
        }

        /// <summary>递归扫描目录下的文本配置文件并添加到列表。</summary>
        /// <param name="workPath">服务端工作目录</param>
        /// <param name="subDir">子目录名(相对路径)</param>
        /// <param name="files">文件列表</param>
        /// <param name="seen">已见文件集合(防重复)</param>
        /// <param name="maxDepth">最大递归深度</param>
        private static void ScanDirectory(string workPath, string subDir,
            List<(string FileName, string DisplayName, string Description, string Category, bool Editable, long SizeBytes)> files,
            HashSet<string> seen, int maxDepth)
        {
            var fullDir = Path.Combine(workPath, subDir);
            if (!Directory.Exists(fullDir)) return;

            void Scan(string dirPath, string relPrefix, int depth)
            {
                if (depth > maxDepth) return;
                try
                {
                    foreach (var file in Directory.GetFiles(dirPath, "*", SearchOption.TopDirectoryOnly))
                    {
                        var relPath = relPrefix + Path.GetFileName(file);
                        if (seen.Contains(relPath)) continue;
                        var ext = Path.GetExtension(file).ToLowerInvariant();
                        if (!_editableExtensions.Contains(ext)) continue;
                        var info = new FileInfo(file);
                        var cat = ext == ".json" ? "json" : (ext == ".properties" ? "properties" : (ext == ".toml" ? "toml" : (ext == ".log" ? "log" : "yaml")));
                        files.Add((relPath, relPath, "", cat, true, info.Length));
                        seen.Add(relPath);
                    }
                }
                catch { }

                if (depth < maxDepth)
                {
                    try
                    {
                        foreach (var sub in Directory.GetDirectories(dirPath, "*", SearchOption.TopDirectoryOnly))
                        {
                            var subName = Path.GetFileName(sub);
                            if (_skipDirs.Contains(subName, StringComparer.OrdinalIgnoreCase)) continue;
                            Scan(sub, relPrefix + subName + "/", depth + 1);
                        }
                    }
                    catch { }
                }
            }

            Scan(fullDir, subDir + "/", 1);
        }

        /// <summary>尝试获取指定 serverKey 的 WorkPath，失败返回 null。</summary>
        private static string? ResolveWorkPath(string serverKey)
        {
            var entry = string.IsNullOrEmpty(serverKey)
                ? Config.CurrentServer
                : (Config.App.ServerList.TryGetValue(serverKey, out var e) ? e : null);
            if (entry == null || string.IsNullOrWhiteSpace(entry.WorkPath)) return null;
            if (!Directory.Exists(entry.WorkPath)) return null;
            return Path.GetFullPath(entry.WorkPath);
        }

        /// <summary>判断 fullPath 是否位于 root 目录内(带目录边界校验，防止 ../server2 之类前缀误匹配)。</summary>
        private static bool IsWithinRoot(string root, string fullPath)
        {
            var rootFull = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(fullPath, rootFull, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 列出指定 MC 服务端工作目录下已知的配置文件(只列出实际存在的)。
        /// </summary>
        public static (bool Success, string Message, string ServerKey, string ServerName, string WorkPath,
                       List<(string FileName, string DisplayName, string Description, string Category, bool Editable, long SizeBytes)> Files)
            ListServerFiles(string serverKey)
        {
            try
            {
                var workPath = ResolveWorkPath(serverKey);
                if (workPath == null)
                {
                    var name = string.IsNullOrEmpty(serverKey) ? Config.CurrentServer.ServerName : serverKey;
                    return (false, $"服务端 {name} 的工作目录未配置或不存在", serverKey ?? "", name, "", new List<(string, string, string, string, bool, long)>());
                }

                var key = string.IsNullOrEmpty(serverKey) ? Config.App.CurrentServer : serverKey;
                var entry = Config.App.ServerList.TryGetValue(key, out var e) ? e : Config.CurrentServer;
                var displayName = string.IsNullOrEmpty(entry.ServerName) ? key : entry.ServerName;

                var files = new List<(string, string, string, string, bool, long)>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 1. 扫描已知白名单文件(根目录 + config/ 目录)
                foreach (var (fileName, fileDisplay, desc, category, editable) in _knownServerFiles)
                {
                    var fullPath = Path.Combine(workPath, fileName);
                    if (File.Exists(fullPath))
                    {
                        var info = new FileInfo(fullPath);
                        files.Add((fileName, fileDisplay, desc, category, editable, info.Length));
                        seen.Add(fileName);
                    }
                }

                // 2. 动态扫描 config/ 目录下额外的配置文件
                ScanDirectory(workPath, "config", files, seen, 1);

                // 3. 扫描 plugins/ 目录(递归，插件配置)
                ScanDirectory(workPath, "plugins", files, seen, 3);

                // 4. 扫描 mods/ 目录下的配置文件(非 .jar)
                ScanDirectory(workPath, "mods", files, seen, 1);

                // 5. 扫描根目录下剩余的文本配置文件
                foreach (var file in Directory.GetFiles(workPath, "*", SearchOption.TopDirectoryOnly))
                {
                    var relPath = Path.GetFileName(file);
                    if (seen.Contains(relPath)) continue;
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (!_editableExtensions.Contains(ext)) continue;
                    var info = new FileInfo(file);
                    var cat = ext == ".json" ? "json" : (ext == ".properties" ? "properties" : (ext == ".toml" ? "toml" : (ext == ".log" ? "log" : "yaml")));
                    files.Add((relPath, relPath, "", cat, true, info.Length));
                    seen.Add(relPath);
                }

                // 6. 扫描其他子目录(排除世界数据等)
                foreach (var dir in Directory.GetDirectories(workPath, "*", SearchOption.TopDirectoryOnly))
                {
                    var dirName = Path.GetFileName(dir);
                    if (_skipDirs.Contains(dirName, StringComparer.OrdinalIgnoreCase)) continue;
                    if (dirName.Equals("config", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("plugins", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("mods", StringComparison.OrdinalIgnoreCase)) continue;
                    ScanDirectory(workPath, dirName, files, seen, 2);
                }

                return (true, "OK", key, displayName, workPath, files);
            }
            catch (Exception ex)
            {
                return (false, $"列文件失败: {ex.Message}", "", "", "", new List<(string, string, string, string, bool, long)>());
            }
        }

        /// <summary>
        /// 读取 MC 服务端的配置文件内容。
        /// </summary>
        public static (bool Success, string Message, string Content, string Category) GetServerFile(string serverKey, string fileName, int tailLines = 0)
        {
            try
            {
                var workPath = ResolveWorkPath(serverKey);
                if (workPath == null) return (false, "服务端工作目录未配置或不存在", "", "");

                if (string.IsNullOrWhiteSpace(fileName) || !IsAllowedServerFile(fileName, out var category, out var editable, out _, out _))
                    return (false, $"不支持的文件: {fileName}", "", "");
                if (category == "binary")
                    return (false, $"{fileName} 是二进制文件，无法在面板编辑", "", category);

                var fullPath = Path.GetFullPath(Path.Combine(workPath, fileName));
                if (!IsWithinRoot(workPath, fullPath))
                    return (false, "非法路径", "", category);
                if (!File.Exists(fullPath))
                    return (false, $"文件不存在: {fileName}", "", category);

                // 大日志文件仅在服务端截取尾部 N 行，避免整个文件经 gRPC 传输
                string content;
                if (tailLines > 0)
                {
                    content = ReadTailLines(fullPath, tailLines);
                }
                else
                {
                    // 全量读取必须有上限: 数 GB 的日志会直接 OOM, 也超过 gRPC 单条消息上限
                    long size = new FileInfo(fullPath).Length;
                    if (size > MaxDirectReadBytes)
                    {
                        return (false,
                            $"文件过大({size / 1024 / 1024}MB), 上限 {MaxDirectReadBytes / 1024 / 1024}MB; " +
                            "请使用 tail_lines 读取尾部或分块下载",
                            "", category);
                    }
                    content = File.ReadAllText(fullPath);
                }
                return (true, "OK", content, category);
            }
            catch (Exception ex)
            {
                return (false, $"读取失败: {ex.Message}", "", "");
            }
        }

        /// <summary>
        /// 从文件末尾倒读块定位第 N 个换行符，仅读取文件尾部 N 行(用于大日志文件)。
        /// 使用 FileShare.ReadWrite 以兼容正被 MC 服务端写入的日志文件。
        /// </summary>
        private static string ReadTailLines(string fullPath, int tailLines)
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length == 0) return "";

            const int blockSize = 8192;
            var buffer = new byte[blockSize];
            long position = fs.Length;
            int newlines = 0;

            while (position > 0)
            {
                int read = (int)Math.Min(blockSize, position);
                position -= read;
                fs.Position = position;
                fs.ReadExactly(buffer, 0, read);

                for (int i = read - 1; i >= 0; i--)
                {
                    if (buffer[i] != '\n') continue;
                    newlines++;
                    // 文件末尾换行不占一行；找到第 N+1 个换行符时其后即为目标内容起点
                    if (newlines > tailLines)
                    {
                        var tailStart = position + i + 1;
                        fs.Position = tailStart;
                        using var reader = new StreamReader(fs, System.Text.Encoding.UTF8,
                            detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                        return reader.ReadToEnd();
                    }
                }
            }

            // 整个文件不足 N 行，返回全部内容
            fs.Position = 0;
            using var fullReader = new StreamReader(fs, System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            return fullReader.ReadToEnd();
        }

        /// <summary>
        /// 保存 MC 服务端的配置文件内容(自动备份原文件)。不热重载 - MC 配置通常需要服务端重启。
        /// </summary>
        public static (bool Success, string Message) SaveServerFile(string serverKey, string fileName, string content)
        {
            try
            {
                var workPath = ResolveWorkPath(serverKey);
                if (workPath == null) return (false, "服务端工作目录未配置或不存在");

                if (string.IsNullOrWhiteSpace(fileName) || !IsAllowedServerFile(fileName, out var category, out var editable, out _, out _))
                    return (false, $"不支持的文件: {fileName}");
                if (!editable || category == "binary")
                    return (false, $"{fileName} 不可编辑");

                var fullPath = Path.GetFullPath(Path.Combine(workPath, fileName));
                if (!IsWithinRoot(workPath, fullPath))
                    return (false, "非法路径");

                // 去除 BOM 并按原文件换行风格回写(避免 CRLF 文件被改成 LF)
                string newline = DetectNewlineStyle(fullPath);
                content = NormalizeTextContent(content, newline);

                if (category == "properties")
                {
                    // Java 的 Properties.load(InputStream) 按 ISO-8859-1 解码, 中文必须转义成 \uXXXX,
                    // 否则 MC 读取后 motd 等字段乱码; 同时做一次可解析性校验。
                    content = EscapePropertiesNonAscii(content);
                    var propError = ValidatePropertiesContent(content);
                    if (propError != null)
                        return (false, $"{fileName} 格式校验失败, 未保存: {propError}");
                }
                else if (category == "json")
                {
                    try
                    {
                        JToken.Parse(content);
                    }
                    catch (Exception ex)
                    {
                        return (false, $"{fileName} 不是合法 JSON, 未保存: {ex.Message}");
                    }
                }

                // 备份
                if (File.Exists(fullPath))
                {
                    var backupPath = fullPath + ".bak";
                    File.Copy(fullPath, backupPath, true);
                }

                // AtomicFile 默认使用 UTF-8 无 BOM(Gson/Properties 对 BOM 敏感)
                AtomicFile.WriteAllText(fullPath, content);
                Output.Log($"MC 配置文件 [{Markup.Escape(fileName)}] 已由面板保存", 1, "Backend");
                return (true, $"{fileName} 已保存(部分配置需服务端重启生效)");
            }
            catch (Exception ex)
            {
                Output.Log($"保存 MC 配置失败: [red]{Markup.Escape(ex.Message)}[/]", 3, "Backend");
                return (false, $"保存失败: {ex.Message}");
            }
        }

        /// <summary>检测文件原有换行风格: 存在 CRLF 则按 CRLF 回写, 否则用 LF。</summary>
        private static string DetectNewlineStyle(string fullPath)
        {
            try
            {
                if (!File.Exists(fullPath)) return "\n";
                using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buffer = new byte[8192];
                int read = fs.Read(buffer, 0, buffer.Length);
                for (int i = 0; i < read - 1; i++)
                {
                    if (buffer[i] == 13 && buffer[i + 1] == 10) return "\r\n";
                }
            }
            catch { }
            return "\n";
        }

        /// <summary>去掉文件头 BOM, 并把换行统一为指定风格。</summary>
        private static string NormalizeTextContent(string content, string newline)
        {
            content = content.TrimStart('\uFEFF');
            var unified = content.Replace("\r\n", "\n").Replace('\r', '\n');
            return newline == "\r\n" ? unified.Replace("\n", "\r\n") : unified;
        }

        /// <summary>按 Java Properties(ISO-8859-1) 读取规则转义非 ASCII 字符为 \uXXXX。</summary>
        private static string EscapePropertiesNonAscii(string content)
        {
            var sb = new StringBuilder(content.Length + 16);
            foreach (char c in content)
            {
                if (c > 0x7E)
                    sb.Append("\\u").Append(((int)c).ToString("x4"));
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>校验 properties 文本可解析: 非注释/非空行必须有合法键名。返回 null 表示通过。</summary>
        private static string? ValidatePropertiesContent(string content)
        {
            var lines = content.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                if (t.Length == 0 || t.StartsWith("#") || t.StartsWith("!")) continue;

                int sep = t.IndexOf('=');
                if (sep < 0) sep = t.IndexOf(':');
                if (sep < 0) sep = t.IndexOfAny(new[] { ' ', '\t' });
                if (sep <= 0)
                    return $"第 {i + 1} 行缺少键名或分隔符: {lines[i]}";
            }
            return null;
        }

        /// <summary>
        /// 获取面板可用命令列表（含描述），用于前端自动补全。
        /// </summary>
        public static (string Command, string Description)[] GetAvailableCommands()
        {
            // 命令全集 = 权威命令表(BaseCommands) ∪ 已注册命令 ∪ 面板已知命令
            var all = new List<string>();
            all.AddRange(Program.BaseCommandNames);
            all.AddRange(CommandRegistry.GetAutoCompleteCommands());
            foreach (var (cmd, _) in _knownCommands)
                all.Add(cmd);

            // 描述映射（大小写不敏感，来源为 _knownCommands，查不到给空串）
            var descMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (cmd, desc) in _knownCommands)
            {
                if (string.IsNullOrWhiteSpace(cmd)) continue;
                descMap[cmd.Trim()] = desc ?? "";
            }

            return all
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .Select(c => (Command: c, Description: descMap.TryGetValue(c, out var d) ? d : ""))
                .ToArray();
        }

        // ===== 实例管理 API =====

        /// <summary>获取所有实例和群组(合并 config.yml 与 server_data.json 数据)</summary>
        public static (bool Success, string Message, List<InstanceListItem> Instances, List<GroupListItem> Groups)
            GetInstanceList()
        {
            try
            {
                ServerDataManager.Load();
                var data = ServerDataManager.Data;

                var instances = new List<InstanceListItem>();
                foreach (var inst in data.Instances)
                {
                    if (!Config.App.ServerList.TryGetValue(inst.Id, out var entry))
                        continue;

                    var groupName = "";
                    if (!string.IsNullOrEmpty(inst.GroupId))
                    {
                        var g = data.Groups.FirstOrDefault(x => x.Id == inst.GroupId);
                        groupName = g?.Name ?? "";
                    }

                    instances.Add(new InstanceListItem
                    {
                        Id = inst.Id,
                        Identifier = inst.Id,
                        Name = entry.ServerName,
                        GroupId = inst.GroupId,
                        GroupName = groupName,
                        IsRunning = Function.Analyzer.IsRunning(inst.Id),
                        Hidden = Config.App.HideConsoleServers.Contains(inst.Id),
                        AutoRestart = entry.AutoRestart,
                        WorkPath = entry.WorkPath,
                        JavaPath = entry.JavaPath,
                        RunFlags = entry.RunServerFlags,
                        AnalyzerMode = entry.AnalyzerMode,
                        CreatedAt = inst.CreatedAt,
                        LastStartedAt = inst.LastStartedAt
                    });
                }

                var groups = data.Groups.Select(g => new GroupListItem
                {
                    Id = g.Id,
                    Name = g.Name,
                    MemberIds = g.MemberIds.ToList(),
                    CreatedAt = g.CreatedAt
                }).ToList();

                return (true, "OK", instances, groups);
            }
            catch (Exception ex)
            {
                return (false, ex.Message, new List<InstanceListItem>(), new List<GroupListItem>());
            }
        }

        /// <summary>批量操作实例(start/stop/restart), 直接调用 Analyzer</summary>
        public static (bool Success, string Message) InstanceAction(string action, List<string> ids)
        {
            if (ids == null || ids.Count == 0)
                return (false, "未选择任何实例");

            var results = new List<string>();

            foreach (var id in ids)
            {
                if (!Config.App.ServerList.ContainsKey(id))
                {
                    results.Add($"[{id}] 不存在");
                    continue;
                }

                try
                {
                    // 直接按实例 ID 操作, 不切换 CurrentServer, 多实例并发时不会互相覆盖
                    switch (action?.ToLowerInvariant())
                    {
                        case "start":
                            if (Function.Analyzer.IsRunning(id))
                            {
                                results.Add($"[{id}] 已在运行中, 跳过");
                            }
                            else
                            {
                                Function.Analyzer.Initialize(id);
                                Function.Analyzer.StartServer(id);
                                // 更新最后启动时间
                                var inst = ServerDataManager.Data.Instances.FirstOrDefault(i => i.Id == id);
                                if (inst != null)
                                {
                                    inst.LastStartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                                    ServerDataManager.Save();
                                }
                                results.Add($"[{id}] 启动指令已发送");
                            }
                            break;

                        case "stop":
                            if (Function.Analyzer.IsRunning(id))
                            {
                                // 面板路径: 立即返回, 后台看门狗监测无响应(不自动强杀)
                                var stopRes = Function.Analyzer.StopServer(killOnTimeout: false, asyncReturn: true, instanceId: id);
                                results.Add(stopRes == Function.Analyzer.StopResult.NotRunning
                                    ? $"[{id}] 未运行, 跳过"
                                    : $"[{id}] 停止指令已发送, 若服务端无响应可在事件板强制关闭(可能丢失数据)");
                            }
                            else
                            {
                                results.Add($"[{id}] 未运行, 跳过");
                            }
                            break;

                        case "force_stop":
                            if (Function.Analyzer.HasManagedProcessOf(id))
                            {
                                Function.Analyzer.ForceKillServer(id);
                                results.Add($"[{id}] 已强制关闭");
                            }
                            else
                            {
                                results.Add($"[{id}] 无本地托管进程(远程模式?), 请在服务器控制台操作");
                            }
                            break;

                        case "restart":
                            if (Function.Analyzer.IsRunning(id))
                            {
                                // 重启前必须确保进程结束: 保留超时强杀兜底
                                Function.Analyzer.StopServer(killOnTimeout: true, instanceId: id);
                                System.Threading.Thread.Sleep(2000);
                            }
                            Function.Analyzer.Initialize(id);
                            Function.Analyzer.StartServer(id);
                            results.Add($"[{id}] 重启指令已发送");
                            break;

                        default:
                            results.Add($"[{id}] 未知操作: {action}");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    results.Add($"[{id}] 操作失败: {ex.Message}");
                }
            }

            return (true, string.Join("\n", results));
        }

        /// <summary>群组批量操作</summary>
        public static (bool Success, string Message) GroupAction(string action, string groupId)
        {
            var data = ServerDataManager.Data;
            var group = data.Groups.FirstOrDefault(g => g.Id == groupId);
            if (group == null)
                return (false, $"群组 '{groupId}' 不存在");

            return InstanceAction(action, group.MemberIds.ToList());
        }

        /// <summary>获取实例详情(含备份列表)</summary>
        public static (bool Success, string Message, InstanceListItem? Instance, List<BackupItem> Backups)
            GetInstanceDetail(string id)
        {
            try
            {
                if (!Config.App.ServerList.TryGetValue(id, out var entry))
                    return (false, $"实例 '{id}' 不存在", null, new List<BackupItem>());

                var data = ServerDataManager.Data;
                var inst = data.Instances.FirstOrDefault(i => i.Id == id);
                var groupName = "";
                if (inst != null && !string.IsNullOrEmpty(inst.GroupId))
                {
                    var g = data.Groups.FirstOrDefault(x => x.Id == inst.GroupId);
                    groupName = g?.Name ?? "";
                }

                var instance = new InstanceListItem
                {
                    Id = id,
                    Identifier = id,
                    Name = entry.ServerName,
                    GroupId = inst?.GroupId ?? "",
                    GroupName = groupName,
                    IsRunning = Function.Analyzer.IsRunning(id),
                    Hidden = Config.App.HideConsoleServers.Contains(id),
                    AutoRestart = entry.AutoRestart,
                    WorkPath = entry.WorkPath,
                    JavaPath = entry.JavaPath,
                    RunFlags = entry.RunServerFlags,
                    AnalyzerMode = entry.AnalyzerMode,
                    CreatedAt = inst?.CreatedAt ?? "",
                    LastStartedAt = inst?.LastStartedAt ?? ""
                };

                var backups = ServerDataManager.ListBackups(id)
                    .Select(b => new BackupItem { FileName = b.FileName, CreatedAt = b.CreatedAt, SizeBytes = b.SizeBytes })
                    .ToList();

                return (true, "OK", instance, backups);
            }
            catch (Exception ex)
            {
                return (false, ex.Message, null, new List<BackupItem>());
            }
        }

        // ===== 插件/模组管理 API =====

        /// <summary>插件/模组条目</summary>
        public class JarFileItem
        {
            public string FileName { get; set; } = "";
            public long SizeBytes { get; set; }
            public bool IsDisabled { get; set; }
            public string Name { get; set; } = "";
            public string Version { get; set; } = "";
            public string Description { get; set; } = "";
            public bool MetaParsed { get; set; }
        }

        /// <summary>从插件 jar 内解析出的元数据(名称/版本/介绍)</summary>
        internal class PluginJarMeta
        {
            public string Name = "";
            public string Version = "";
            public string Description = "";
        }

        /// <summary>
        /// 尝试从插件 jar 内读取元数据。优先级: plugin.yml(Bukkit/Spigot/Paper/Nukkit)
        /// → paper-plugin.yml(Paper 原生插件) → bungee.yml(BungeeCord/Waterfall 代理)
        /// → velocity-plugin.json(Velocity 代理) → sponge_plugins.json(Sponge)。
        /// 均失败返回 null(面板将标黄提示可能无法加载, 名称回退为文件名)。
        /// </summary>
        private static PluginJarMeta? ReadPluginJarMeta(string jarPath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(jarPath);
                foreach (var candidate in new[] { "plugin.yml", "paper-plugin.yml", "bungee.yml" })
                {
                    var text = ReadZipRootEntryText(zip, candidate);
                    if (text == null) continue;
                    var meta = ParseYamlMeta(text);
                    if (meta != null) return meta;
                }
                foreach (var candidate in new[] { "velocity-plugin.json", "sponge_plugins.json", "fabric.mod.json", "mcmod.info" })
                {
                    var text = ReadZipRootEntryText(zip, candidate);
                    if (text == null) continue;
                    var meta = ParseJsonMeta(text);
                    if (meta != null) return meta;
                }
                // Forge / NeoForge 的元数据在 META-INF 下且为 TOML
                foreach (var candidate in new[] { "META-INF/mods.toml", "META-INF/neoforge.mods.toml" })
                {
                    var text = ReadZipEntryText(zip, candidate);
                    if (text == null) continue;
                    var meta = ParseTomlMeta(text);
                    if (meta != null) return meta;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>读取 zip 根目录下指定名称的文本条目(忽略大小写, 限制 1MB 防御异常大文件)</summary>
        private static string? ReadZipRootEntryText(ZipArchive zip, string entryName)
        {
            foreach (var e in zip.Entries)
            {
                var n = e.FullName;
                if (n.StartsWith("./")) n = n[2..];
                n = n.TrimStart('/');
                if (n.Contains('/') || n.Contains('\\')) continue; // 仅根目录
                if (!string.Equals(n, entryName, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Length > 1024 * 1024) return null;
                using var sr = new StreamReader(e.Open(), Encoding.UTF8);
                return sr.ReadToEnd();
            }
            return null;
        }

        /// <summary>读取 zip 内指定相对路径的文本条目(忽略大小写与分隔符, 限制 1MB)</summary>
        private static string? ReadZipEntryText(ZipArchive zip, string relativePath)
        {
            string want = relativePath.Replace('\\', '/');
            foreach (var e in zip.Entries)
            {
                var n = e.FullName.Replace('\\', '/').TrimStart('/');
                if (n.StartsWith("./")) n = n[2..];
                if (!string.Equals(n, want, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Length > 1024 * 1024) return null;
                using var sr = new StreamReader(e.Open(), Encoding.UTF8);
                return sr.ReadToEnd();
            }
            return null;
        }

        /// <summary>
        /// 从 Forge/NeoForge 的 META-INF/mods.toml 解析 modId/version/displayName/description。
        /// 只做键值行匹配: mods.toml 是 TOML, 但所需字段都在 [[mods]] 段内且为简单字符串键。
        /// </summary>
        internal static PluginJarMeta? ParseTomlMeta(string text)
        {
            try
            {
                string? Pick(string key)
                {
                    var m = Regex.Match(text,
                        @"^\s*" + Regex.Escape(key) + @"\s*=\s*(?:""([^""]*)""|'([^']*)')",
                        RegexOptions.Multiline | RegexOptions.IgnoreCase);
                    if (!m.Success) return null;
                    string v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    // 版本为占位符时会带 $ 记号, 视为无版本信息
                    if (string.IsNullOrWhiteSpace(v) || v.Contains('$')) return null;
                    return v.Trim();
                }

                string? modId = Pick("modId");
                string? name = Pick("displayName") ?? modId;
                if (string.IsNullOrWhiteSpace(name)) return null;

                return new PluginJarMeta
                {
                    Name = name!,
                    Version = Pick("version") ?? "",
                    Description = Pick("description") ?? ""
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>从 plugin.yml/bungee.yml 等 YAML 元数据中解析 name/version/description</summary>
        private static PluginJarMeta? ParseYamlMeta(string text)
        {
            try
            {
                var yaml = new YamlStream();
                yaml.Load(new StringReader(text));
                if (yaml.Documents.Count == 0) return null;
                if (yaml.Documents[0].RootNode is not YamlMappingNode root) return null;
                string? Get(string key)
                {
                    foreach (var kv in root.Children)
                    {
                        if (kv.Key is YamlScalarNode k &&
                            string.Equals(k.Value, key, StringComparison.OrdinalIgnoreCase) &&
                            kv.Value is YamlScalarNode v)
                            return v.Value;
                    }
                    return null;
                }
                var name = Get("name") ?? Get("id");
                if (string.IsNullOrWhiteSpace(name)) return null;
                return new PluginJarMeta
                {
                    Name = name.Trim(),
                    Version = (Get("version") ?? "").Trim(),
                    Description = (Get("description") ?? "").Trim()
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>从 velocity-plugin.json/sponge_plugins.json 等 JSON 元数据中解析 name/version/description</summary>
        internal static PluginJarMeta? ParseJsonMeta(string json)
        {
            try
            {
                var token = JToken.Parse(json);
                // sponge_plugins.json 为 {"plugins": [...]}; 也有顶层即数组/对象的情况
                if (token is JObject obj)
                {
                    if (obj["plugins"] is JArray arr && arr.Count > 0)
                        token = arr[0];
                }
                else if (token is JArray topArr && topArr.Count > 0)
                {
                    token = topArr[0];
                }
                if (token is not JObject o) return null;
                string? GetStr(params string[] keys)
                {
                    foreach (var k in keys)
                    {
                        if (o[k] is JValue v && v.Type == JTokenType.String && !string.IsNullOrWhiteSpace(v.Value<string>()))
                            return v.Value<string>();
                    }
                    return null;
                }
                var name = GetStr("name", "id", "modid");
                if (string.IsNullOrWhiteSpace(name)) return null;
                return new PluginJarMeta
                {
                    Name = name.Trim(),
                    Version = GetStr("version") ?? "",
                    Description = GetStr("description") ?? ""
                };
            }
            catch
            {
                return null;
            }
        }

        private static List<JarFileItem> ListJarFiles(string serverKey, string subDir, string disabledExt, bool readPluginMeta = false)
        {
            var result = new List<JarFileItem>();
            var workPath = ResolveWorkPath(serverKey);
            if (workPath == null) return result;
            var dir = Path.Combine(workPath, subDir);
            if (!Directory.Exists(dir)) return result;
            var files = new List<FileInfo>();
            var di = new DirectoryInfo(dir);
            files.AddRange(di.GetFiles("*.jar"));
            if (disabledExt == ".disjar")
                files.AddRange(di.GetFiles("*.disjar"));
            else
                files.AddRange(di.GetFiles("*.disabled"));
            files = files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var f in files)
            {
                var item = new JarFileItem
                {
                    FileName = f.Name,
                    SizeBytes = f.Length,
                    IsDisabled = f.Extension.Equals(disabledExt, StringComparison.OrdinalIgnoreCase)
                };
                if (readPluginMeta)
                {
                    var meta = ReadPluginJarMeta(f.FullName);
                    item.MetaParsed = meta != null;
                    if (meta != null)
                    {
                        item.Name = meta.Name;
                        item.Version = meta.Version;
                        item.Description = meta.Description;
                    }
                }
                result.Add(item);
            }
            return result;
        }

        /// <summary>列出指定实例的插件</summary>
        public static (bool Success, string Message, List<JarFileItem> Plugins)
            ListPlugins(string serverKey)
        {
            try
            {
                var workPath = ResolveWorkPath(serverKey);
                if (workPath == null) return (false, "服务端工作目录未配置或不存在", new List<JarFileItem>());
                return (true, "OK", ListJarFiles(serverKey, "plugins", ".disjar", readPluginMeta: true));
            }
            catch (Exception ex)
            {
                return (false, ex.Message, new List<JarFileItem>());
            }
        }

        /// <summary>列出指定实例的模组</summary>
        public static (bool Success, string Message, List<JarFileItem> Mods)
            ListMods(string serverKey)
        {
            try
            {
                var workPath = ResolveWorkPath(serverKey);
                if (workPath == null) return (false, "服务端工作目录未配置或不存在", new List<JarFileItem>());
                // 模组同样解析元数据(fabric.mod.json / mcmod.info / META-INF/mods.toml), 便于面板显示名称与版本
                return (true, "OK", ListJarFiles(serverKey, "mods", ".disabled", readPluginMeta: true));
            }
            catch (Exception ex)
            {
                return (false, ex.Message, new List<JarFileItem>());
            }
        }

        /// <summary>切换插件/模组启用状态</summary>
        /// <param name="disabledExt">.disjar(插件) 或 .disabled(模组)</param>
        private static (bool Success, string Message) ToggleJarFile(string serverKey, string subDir, string fileName, string disabledExt)
        {
            try
            {
                var workPath = ResolveWorkPath(serverKey);
                if (workPath == null) return (false, "服务端工作目录未配置或不存在");
                var dir = Path.Combine(workPath, subDir);
                var fullPath = Path.Combine(dir, fileName);
                if (!File.Exists(fullPath)) return (false, $"文件不存在: {fileName}");

                // 防止路径穿越
                var resolvedPath = Path.GetFullPath(fullPath);
                if (!resolvedPath.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return (false, "非法路径");

                string newPath;
                string kind = subDir == "mods" ? I18n.Get("board_event_kind_mod") : I18n.Get("board_event_kind_plugin");
                if (fileName.EndsWith(disabledExt, StringComparison.OrdinalIgnoreCase))
                {
                    // 启用: 去掉禁用后缀
                    newPath = resolvedPath.Substring(0, resolvedPath.Length - disabledExt.Length);
                    File.Move(resolvedPath, newPath);
                    BoardEventRecorder.Record("jar_toggle", serverKey,
                        I18n.Get("board_event_jar_toggle", kind), $"已启用: {Path.GetFileName(newPath)}");
                    return (true, $"已启用: {Path.GetFileName(newPath)}");
                }
                else if (fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                {
                    // 禁用: 添加禁用后缀
                    newPath = resolvedPath + disabledExt;
                    File.Move(resolvedPath, newPath);
                    BoardEventRecorder.Record("jar_toggle", serverKey,
                        I18n.Get("board_event_jar_toggle", kind), $"已禁用: {Path.GetFileName(newPath)}");
                    return (true, $"已禁用: {Path.GetFileName(newPath)}");
                }
                return (false, "不支持的文件类型");
            }
            catch (Exception ex)
            {
                return (false, $"操作失败: {ex.Message}");
            }
        }

        /// <summary>切换插件启用/禁用</summary>
        public static (bool Success, string Message) TogglePlugin(string serverKey, string fileName)
            => ToggleJarFile(serverKey, "plugins", fileName, ".disjar");

        /// <summary>切换模组启用/禁用</summary>
        public static (bool Success, string Message) ToggleMod(string serverKey, string fileName)
            => ToggleJarFile(serverKey, "mods", fileName, ".disabled");

        /// <summary>删除插件/模组文件</summary>
        private static (bool Success, string Message) DeleteJarFile(string serverKey, string subDir, string fileName)
        {
            try
            {
                var workPath = ResolveWorkPath(serverKey);
                if (workPath == null) return (false, "服务端工作目录未配置或不存在");
                var dir = Path.Combine(workPath, subDir);
                var fullPath = Path.Combine(dir, fileName);
                if (!File.Exists(fullPath)) return (false, $"文件不存在: {fileName}");
                var resolvedPath = Path.GetFullPath(fullPath);
                if (!resolvedPath.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return (false, "非法路径");
                File.Delete(resolvedPath);
                string kind = subDir == "mods" ? I18n.Get("board_event_kind_mod") : I18n.Get("board_event_kind_plugin");
                BoardEventRecorder.Record("jar_delete", serverKey,
                    I18n.Get("board_event_jar_delete", kind), fileName);
                return (true, $"已删除: {fileName}");
            }
            catch (Exception ex)
            {
                return (false, $"删除失败: {ex.Message}");
            }
        }

        public static (bool Success, string Message) DeletePlugin(string serverKey, string fileName)
            => DeleteJarFile(serverKey, "plugins", fileName);

        public static (bool Success, string Message) DeleteMod(string serverKey, string fileName)
            => DeleteJarFile(serverKey, "mods", fileName);

        /// <summary>上传插件/模组文件(Base64内容)</summary>
        private static (bool Success, string Message) UploadJarFile(string serverKey, string subDir, string fileName, byte[] content)
        {
            try
            {
                var workPath = ResolveWorkPath(serverKey);
                if (workPath == null) return (false, "服务端工作目录未配置或不存在");
                var dir = Path.Combine(workPath, subDir);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // 仅允许 .jar 后缀
                if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                    return (false, "仅支持 .jar 文件");
                var safeName = Path.GetFileName(fileName);
                var fullPath = Path.Combine(dir, safeName);
                File.WriteAllBytes(fullPath, content);
                string kind = subDir == "mods" ? I18n.Get("board_event_kind_mod") : I18n.Get("board_event_kind_plugin");
                BoardEventRecorder.Record("jar_upload", serverKey,
                    I18n.Get("board_event_jar_upload", kind), safeName);
                return (true, $"已上传: {safeName}");
            }
            catch (Exception ex)
            {
                return (false, $"上传失败: {ex.Message}");
            }
        }

        public static (bool Success, string Message) UploadPlugin(string serverKey, string fileName, byte[] content)
            => UploadJarFile(serverKey, "plugins", fileName, content);

        public static (bool Success, string Message) UploadMod(string serverKey, string fileName, byte[] content)
            => UploadJarFile(serverKey, "mods", fileName, content);

        // ===== 实例文件管理 API (工作目录为根, 禁止越界) =====

        /// <summary>
        /// 解析工作目录内的相对路径并校验越界。
        /// 拒绝 ".."/盘符等片段, 返回 GetFullPath 后的绝对路径; 非法时返回 null。
        /// </summary>
        private static string? ResolveInstancePath(string serverKey, string? relPath, out string workPath, out string error)
        {
            error = "";
            workPath = "";
            var root = ResolveWorkPath(serverKey);
            if (root == null)
            {
                error = "服务端工作目录未配置或不存在";
                return null;
            }
            workPath = root;

            var rel = (relPath ?? "").Trim().Replace('\\', '/').Trim('/');
            if (rel.Length == 0) return root;

            var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || parts.Any(p => p == "." || p == ".." || p.EndsWith(":", StringComparison.Ordinal)))
            {
                error = "非法路径";
                return null;
            }
            var full = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
            if (!IsWithinRoot(root, full))
            {
                error = "非法路径";
                return null;
            }
            return full;
        }

        /// <summary>列出工作目录内指定目录的条目(目录在前, 各按名称排序)。</summary>
        public static (bool Success, string Message, string WorkPath, List<(string Name, bool IsDir, long SizeBytes, string ModifiedAt)> Entries)
            ListInstanceDirectory(string serverKey, string relPath)
        {
            try
            {
                var dirPath = ResolveInstancePath(serverKey, relPath, out var workPath, out var err);
                if (dirPath == null) return (false, err, "", new List<(string, bool, long, string)>());
                if (!Directory.Exists(dirPath)) return (false, $"目录不存在: {relPath}", workPath, new List<(string, bool, long, string)>());

                var entries = new List<(string Name, bool IsDir, long SizeBytes, string ModifiedAt)>();
                foreach (var d in Directory.GetDirectories(dirPath))
                    entries.Add((Path.GetFileName(d), true, 0, Directory.GetLastWriteTime(d).ToString("yyyy-MM-dd HH:mm:ss")));
                foreach (var f in Directory.GetFiles(dirPath))
                {
                    if (f.EndsWith(".rtupload.tmp", StringComparison.OrdinalIgnoreCase)) continue; // 上传临时文件不展示
                    var fi = new FileInfo(f);
                    entries.Add((fi.Name, false, fi.Length, fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")));
                }
                var sorted = entries.OrderBy(e => !e.IsDir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
                return (true, "OK", workPath, sorted);
            }
            catch (Exception ex)
            {
                return (false, $"列目录失败: {ex.Message}", "", new List<(string, bool, long, string)>());
            }
        }

        /// <summary>校验并打开工作目录内文件用于流式下载(共享读写, 兼容服务端正在写日志)。调用方负责释放流。</summary>
        public static (bool Success, string Message, FileStream? Stream, string FileName)
            OpenInstanceFileForRead(string serverKey, string relPath)
        {
            try
            {
                var p = ResolveInstancePath(serverKey, relPath, out _, out var err);
                if (p == null) return (false, err, null, "");
                if (Directory.Exists(p)) return (false, "不能下载目录", null, "");
                if (!File.Exists(p)) return (false, $"文件不存在: {relPath}", null, "");
                var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return (true, "OK", fs, Path.GetFileName(p));
            }
            catch (Exception ex)
            {
                return (false, $"打开文件失败: {ex.Message}", null, "");
            }
        }

        /// <summary>
        /// 分块写入工作目录内文件(面板上传)。
        /// offset=0 时创建/截断临时文件; 否则校验临时文件长度与 offset 连续后追加;
        /// final=true 时将临时文件提交为正式文件。
        /// </summary>
        public static (bool Success, string Message) WriteInstanceFileChunk(string serverKey, string relPath, long offset, byte[] data, bool final)
        {
            try
            {
                var p = ResolveInstancePath(serverKey, relPath, out _, out var err);
                if (p == null) return (false, err);
                if (Directory.Exists(p)) return (false, "目标路径是目录");

                var tempPath = p + ".rtupload.tmp";
                if (offset == 0)
                {
                    var dir = Path.GetDirectoryName(p);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        fs.Write(data, 0, data.Length);
                }
                else
                {
                    if (!File.Exists(tempPath)) return (false, "上传会话丢失, 请重新上传");
                    if (new FileInfo(tempPath).Length != offset) return (false, "上传偏移不连续, 请重新上传");
                    using var fs = new FileStream(tempPath, FileMode.Append, FileAccess.Write, FileShare.None);
                    fs.Write(data, 0, data.Length);
                }

                if (final)
                {
                    File.Move(tempPath, p, overwrite: true);
                    Output.Log($"实例文件 [{Markup.Escape(relPath ?? "")}] 已由面板上传", 1, "Backend");
                }
                return (true, "OK");
            }
            catch (Exception ex)
            {
                return (false, $"写入失败: {ex.Message}");
            }
        }

        /// <summary>删除工作目录内的文件或目录(拒绝删除根目录)。</summary>
        public static (bool Success, string Message) DeleteInstanceEntry(string serverKey, string relPath, bool recursive)
        {
            try
            {
                var p = ResolveInstancePath(serverKey, relPath, out var workPath, out var err);
                if (p == null) return (false, err);

                var rootFull = Path.GetFullPath(workPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(p, rootFull, StringComparison.OrdinalIgnoreCase))
                    return (false, "不能删除工作目录根目录");

                if (Directory.Exists(p))
                {
                    if (!recursive && Directory.EnumerateFileSystemEntries(p).Any())
                        return (false, "目录非空, 请确认递归删除");
                    Directory.Delete(p, recursive);
                    Output.Log($"实例目录 [{Markup.Escape(relPath ?? "")}] 已由面板删除", 1, "Backend");
                    BoardEventRecorder.Record("file_delete", serverKey,
                        I18n.Get("board_event_file_delete_dir"), relPath ?? "");
                    return (true, $"已删除目录: {relPath}");
                }
                if (File.Exists(p))
                {
                    File.Delete(p);
                    Output.Log($"实例文件 [{Markup.Escape(relPath ?? "")}] 已由面板删除", 1, "Backend");
                    BoardEventRecorder.Record("file_delete", serverKey,
                        I18n.Get("board_event_file_delete_file"), relPath ?? "");
                    return (true, $"已删除: {relPath}");
                }
                return (false, $"不存在: {relPath}");
            }
            catch (Exception ex)
            {
                return (false, $"删除失败: {ex.Message}");
            }
        }

        // ===== 实例增强: Java 扫描 / 克隆 / zip 解压导入 =====

        /// <summary>
        /// 扫描本机 Java 安装: JAVA_HOME / PATH / 常见安装目录(Java、Eclipse Adoptium、Zulu、Microsoft、BellSoft、Amazon Corretto)。
        /// 版本号优先从目录名解析, 解析不到时运行 java -version 获取。
        /// </summary>
        public static (bool Success, string Message, List<(string Path, string Version, string Source)> Javas)
            ScanJavaInstallations()
        {
            var results = new List<(string Path, string Version, string Source)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddJava(string exePath, string source)
            {
                try
                {
                    exePath = Path.GetFullPath(exePath);
                    if (!File.Exists(exePath)) return;
                }
                catch { return; }
                if (!seen.Add(exePath)) return;

                // 从路径目录名解析版本(jdk-17.0.2 / zulu17 / jre1.8.0_301 / corretto11 等)
                var version = "";
                var binDir = Path.GetDirectoryName(exePath) ?? "";
                var installDir = Path.GetDirectoryName(binDir) ?? "";
                foreach (var cand in new[] { Path.GetFileName(installDir), Path.GetFileName(binDir) })
                {
                    if (string.IsNullOrEmpty(cand)) continue;
                    var m = Regex.Match(cand, @"(?:jdk|jre|zulu|corretto|liberica)[-_\.]?(\d+(?:[._]\d+)*)", RegexOptions.IgnoreCase);
                    if (m.Success) { version = m.Groups[1].Value.Replace('_', '.'); break; }
                    m = Regex.Match(cand, @"^v?(\d+(?:\.\d+)+)$", RegexOptions.IgnoreCase); // 纯版本号目录名(如 "17.0.2")
                    if (m.Success) { version = m.Groups[1].Value; break; }
                }

                // 目录名解析失败且数量有限时, 运行 java -version 获取精确版本(2秒超时)
                if (version.Length == 0)
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = exePath,
                            Arguments = "-version",
                            RedirectStandardError = true,
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var proc = System.Diagnostics.Process.Start(psi);
                        if (proc != null && proc.WaitForExit(2000))
                        {
                            var output = proc.StandardError.ReadToEnd() + proc.StandardOutput.ReadToEnd();
                            var vm = Regex.Match(output, @"version\s+""([^""]+)""");
                            if (vm.Success) version = vm.Groups[1].Value;
                        }
                        proc?.Kill(entireProcessTree: true);
                    }
                    catch { /* 版本获取失败不影响路径返回 */ }
                }

                results.Add((exePath, version.Length == 0 ? "未知" : version, source));
            }

            // 1. JAVA_HOME
            try
            {
                var jhome = Environment.GetEnvironmentVariable("JAVA_HOME");
                if (!string.IsNullOrWhiteSpace(jhome))
                {
                    AddJava(Path.Combine(jhome, "bin", "java.exe"), "JAVA_HOME");

                    // 1b. JAVA_HOME 同级目录(纯 zip 解压的 Java 常与当前 JDK 集中放在同一父目录)
                    var jhomeParent = Path.GetDirectoryName(jhome.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(jhomeParent) && Directory.Exists(jhomeParent))
                    {
                        foreach (var sub in Directory.GetDirectories(jhomeParent))
                            AddJava(Path.Combine(sub, "bin", "java.exe"), "解压目录(JAVA_HOME 同级)");
                    }
                }
            }
            catch { }

            // 2. PATH 中的 java.exe
            try
            {
                var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var seg in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var candidate = seg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? seg : Path.Combine(seg, "java.exe");
                    if (File.Exists(candidate)) AddJava(candidate, "PATH");

                    // 2b. PATH 段的父目录兄弟版本(如 PATH 含 D:\Java\jdk-21\bin, 扫描 D:\Java 下其他解压版本)
                    try
                    {
                        var segFull = Path.GetFullPath(seg.TrimEnd('\\', '/'));
                        var segParent = Path.GetDirectoryName(segFull); // bin 的上级 = 安装目录
                        var segGrand = string.IsNullOrEmpty(segParent) ? null : Path.GetDirectoryName(segParent); // 安装目录的上级
                        if (!string.IsNullOrEmpty(segGrand) && Directory.Exists(segGrand))
                        {
                            foreach (var sub in Directory.GetDirectories(segGrand))
                                AddJava(Path.Combine(sub, "bin", "java.exe"), "解压目录(PATH 同级)");
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // 3. 常见安装根目录
            try
            {
                var roots = new List<string>();
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (!string.IsNullOrEmpty(pf))
                {
                    roots.Add(Path.Combine(pf, "Java"));
                    roots.Add(Path.Combine(pf, "Eclipse Adoptium"));
                    roots.Add(Path.Combine(pf, "Zulu"));
                    roots.Add(Path.Combine(pf, "Microsoft"));
                    roots.Add(Path.Combine(pf, "BellSoft"));
                    roots.Add(Path.Combine(pf, "Amazon Corretto"));
                }
                if (!string.IsNullOrEmpty(pf86))
                {
                    roots.Add(Path.Combine(pf86, "Java"));
                    roots.Add(Path.Combine(pf86, "Eclipse Adoptium"));
                    roots.Add(Path.Combine(pf86, "Zulu"));
                }
                foreach (var root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var sub in Directory.GetDirectories(root))
                        AddJava(Path.Combine(sub, "bin", "java.exe"), $"安装目录({Path.GetFileName(root)})");
                }
            }
            catch { }

            // 4. 各驱动器根目录下的 Java(纯 zip 解压未安装、未配置环境变量的场景, 仅扫根目录一层)
            try
            {
                foreach (var drive in Environment.GetLogicalDrives())
                {
                    try
                    {
                        var di = new DirectoryInfo(drive);
                        if (!di.Exists) continue;

                        foreach (var sub in di.GetDirectories())
                        {
                            var name = sub.Name;
                            // 根下集中存放目录(如 D:\Java) → 再深入一层
                            if (name.Equals("Java", StringComparison.OrdinalIgnoreCase)
                                || name.Equals("JDK", StringComparison.OrdinalIgnoreCase))
                            {
                                foreach (var inner in sub.GetDirectories())
                                    AddJava(Path.Combine(inner.FullName, "bin", "java.exe"), $"解压目录({drive}{name})");
                                continue;
                            }
                            // 根下直接解压的 JDK/JRE 目录(如 D:\jdk-17.0.2、E:\zulu21)
                            if (Regex.IsMatch(name, @"^(jdk|jre|zulu|corretto|liberica|graalvm|temurin)[-_\.]?\d", RegexOptions.IgnoreCase))
                                AddJava(Path.Combine(sub.FullName, "bin", "java.exe"), "解压目录(驱动器根)");
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            }
            catch { }

            return (true, $"共找到 {results.Count} 个 Java", results);
        }

        /// <summary>递归复制目录(不复制 .rtupload.tmp 临时文件与运行中的日志锁)。</summary>
        private static void CopyDirectoryRecursive(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.GetFiles(src))
            {
                if (file.EndsWith(".rtupload.tmp", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
            }
            foreach (var dir in Directory.GetDirectories(src))
                CopyDirectoryRecursive(dir, Path.Combine(dst, Path.GetFileName(dir)));
        }

        /// <summary>实例的默认工作目录(Content/Server/{id}, 相对 RtCli 运行目录, 与 Intelligence 自动路径一致)。</summary>
        private static string GetDefaultWorkPath(string serverKey)
            => Path.GetFullPath(Path.Combine("Content", "Server", serverKey));

        /// <summary>
        /// 克隆实例: 复制配置创建新实例, 可选复制工作目录全部文件。
        /// 新实例使用默认布局 Content/Server/{newIdentifier} 作为工作目录。
        /// </summary>
        public static (bool Success, string Message, string NewId) CloneInstance(
            string srcId, string newIdentifier, string newName, bool copyFiles)
        {
            try
            {
                if (!Config.App.ServerList.TryGetValue(srcId ?? "", out var src))
                    return (false, $"源实例 '{srcId}' 不存在", "");

                newIdentifier = (newIdentifier ?? "").Trim();
                if (newIdentifier.Length == 0)
                {
                    newIdentifier = $"{srcId}-copy";
                    int n = 2;
                    while (Config.App.ServerList.ContainsKey(newIdentifier))
                        newIdentifier = $"{srcId}-copy{n++}";
                }
                if (Config.App.ServerList.ContainsKey(newIdentifier))
                    return (false, $"标识 '{newIdentifier}' 已存在", "");
                newName = string.IsNullOrWhiteSpace(newName) ? $"{src.ServerName} 副本" : newName.Trim();

                var dstWork = GetDefaultWorkPath(newIdentifier);
                var srcWork = string.IsNullOrWhiteSpace(src.WorkPath)
                    ? GetDefaultWorkPath(srcId)
                    : Path.GetFullPath(src.WorkPath);

                var (ok, msg) = ServerDataManager.CreateInstance(
                    newIdentifier, newName, dstWork, src.JavaPath ?? "",
                    src.RunServerFlags ?? "", src.AnalyzerMode ?? "", "");
                if (!ok) return (false, msg, "");

                if (copyFiles && Directory.Exists(srcWork))
                {
                    try
                    {
                        CopyDirectoryRecursive(srcWork, dstWork);
                        Output.Log($"实例 [{Markup.Escape(srcId)}] 已克隆为 [{Markup.Escape(newIdentifier)}] (含文件)", 1, "Backend");
                        return (true, $"克隆成功: {newIdentifier} (已复制服务端文件)", newIdentifier);
                    }
                    catch (Exception ex)
                    {
                        return (true, $"实例已创建, 但复制文件失败: {ex.Message}", newIdentifier);
                    }
                }

                Output.Log($"实例 [{Markup.Escape(srcId)}] 已克隆为 [{Markup.Escape(newIdentifier)}] (仅配置)", 1, "Backend");
                return (true, $"克隆成功: {newIdentifier} (仅配置)", newIdentifier);
            }
            catch (Exception ex)
            {
                return (false, $"克隆失败: {ex.Message}", "");
            }
        }

        /// <summary>
        /// 解压实例工作目录中的 zip 到工作目录根:
        /// - zip-slip 路径穿越校验
        /// - 单根目录自动提升(zip 内所有内容都在一个顶层目录时, 将该目录内容直接解压到根)
        /// - 解压完成后删除 zip
        /// - 返回工作目录第一层的 bat/cmd/sh 启动脚本列表
        /// </summary>
        public static (bool Success, string Message, List<string> Scripts) ExtractInstanceZip(
            string serverKey, string relPath)
        {
            try
            {
                var zipPath = ResolveInstancePath(serverKey, relPath, out var workPath, out var err);
                if (zipPath == null) return (false, err, new List<string>());
                if (!File.Exists(zipPath)) return (false, $"zip 文件不存在: {relPath}", new List<string>());
                if (!zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    return (false, "仅支持 .zip 格式", new List<string>());

                using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath))
                {
                    if (archive.Entries.Count == 0)
                        return (false, "zip 压缩包为空", new List<string>());

                    // zip-slip 校验: 每个条目的目标路径必须位于工作目录内
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue; // 目录条目
                        var dest = Path.GetFullPath(Path.Combine(workPath, entry.FullName.Replace('\\', '/')));
                        if (!IsWithinRoot(workPath, dest))
                            return (false, $"zip 包含非法路径条目: {entry.FullName}, 已拒绝解压", new List<string>());
                    }

                    // 单根目录检测: 所有条目第一段相同且存在更深层内容
                    var firstSegs = archive.Entries
                        .Where(e => !string.IsNullOrEmpty(e.FullName))
                        .Select(e => e.FullName.Replace('\\', '/').Split('/')[0])
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    bool singleRoot = firstSegs.Count == 1
                        && archive.Entries.Any(e => e.FullName.Replace('\\', '/').Contains('/'));

                    var tmpDir = Path.Combine(workPath, "__extract_tmp__");
                    if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);

                    try
                    {
                        archive.ExtractToDirectory(tmpDir, overwriteFiles: true);

                        // 提升到工作目录根
                        var moveSrc = singleRoot
                            ? Path.Combine(tmpDir, firstSegs[0])
                            : tmpDir;
                        foreach (var entry in Directory.GetFileSystemEntries(moveSrc))
                        {
                            var target = Path.Combine(workPath, Path.GetFileName(entry));
                            if (Directory.Exists(entry))
                            {
                                if (Directory.Exists(target))
                                    CopyDirectoryRecursive(entry, target);
                                else
                                    Directory.Move(entry, target);
                            }
                            else
                            {
                                File.Copy(entry, target, overwrite: true);
                            }
                        }
                    }
                    finally
                    {
                        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
                    }
                }

                File.Delete(zipPath);

                // 扫描第一层启动脚本
                var scripts = Directory.GetFiles(workPath)
                    .Where(f => { var ext = Path.GetExtension(f).ToLowerInvariant(); return ext == ".bat" || ext == ".cmd" || ext == ".sh"; })
                    .Select(Path.GetFileName)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToList()!;

                Output.Log($"实例 [{Markup.Escape(serverKey ?? "")}] 已导入 zip [{Markup.Escape(relPath ?? "")}]", 1, "Backend");
                return (true, "解压完成", scripts);
            }
            catch (Exception ex)
            {
                return (false, $"解压失败: {ex.Message}", new List<string>());
            }
        }

        /// <summary>重命名/移动工作目录内的文件或目录(拒绝操作根目录, 目标已存在时拒绝)。</summary>
        public static (bool Success, string Message) RenameInstanceEntry(string serverKey, string oldRel, string newRel)
        {
            try
            {
                var oldPath = ResolveInstancePath(serverKey, oldRel, out var workPath, out var err);
                if (oldPath == null) return (false, err);
                var newPath = ResolveInstancePath(serverKey, newRel, out _, out err);
                if (newPath == null) return (false, err);

                var rootFull = Path.GetFullPath(workPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(oldPath, rootFull, StringComparison.OrdinalIgnoreCase))
                    return (false, "不能重命名工作目录根目录");
                if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                    return (false, "路径未变化");

                bool isDir = Directory.Exists(oldPath);
                if (!isDir && !File.Exists(oldPath)) return (false, $"不存在: {oldRel}");
                if (Directory.Exists(newPath) || File.Exists(newPath)) return (false, $"目标已存在: {newRel}");

                var targetDir = Path.GetDirectoryName(newPath);
                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir)) return (false, "目标目录不存在");

                if (isDir) Directory.Move(oldPath, newPath);
                else File.Move(oldPath, newPath);
                Output.Log($"实例条目 [{Markup.Escape(oldRel ?? "")}] 已由面板重命名/移动为 [{Markup.Escape(newRel ?? "")}]", 1, "Backend");
                BoardEventRecorder.Record("file_rename", serverKey,
                    I18n.Get("board_event_file_rename"), $"{oldRel} → {newRel}");
                return (true, "OK");
            }
            catch (Exception ex)
            {
                return (false, $"重命名失败: {ex.Message}");
            }
        }

        /// <summary>可编辑文本文件大小上限(2MB)。</summary>
        private const int MaxEditableFileBytes = 2 * 1024 * 1024;
        /// <summary>GetServerFile 全量读取上限(超过需走 tail_lines)</summary>
        private const long MaxDirectReadBytes = 8 * 1024 * 1024;

        /// <summary>读取工作目录内文本文件内容(供面板编辑)。拒绝目录与二进制内容(检测 0x00 字节)。</summary>
        public static (bool Success, string Message, string Content, long SizeBytes)
            ReadInstanceTextFile(string serverKey, string relPath)
        {
            try
            {
                var p = ResolveInstancePath(serverKey, relPath, out _, out var err);
                if (p == null) return (false, err, "", 0);
                if (Directory.Exists(p)) return (false, "目标路径是目录", "", 0);
                if (!File.Exists(p)) return (false, $"文件不存在: {relPath}", "", 0);

                var fi = new FileInfo(p);
                if (fi.Length > MaxEditableFileBytes)
                    return (false, $"文件过大({fi.Length} 字节), 仅支持编辑不超过 {MaxEditableFileBytes / 1024 / 1024} MB 的文本文件", "", fi.Length);
                var bytes = File.ReadAllBytes(p);
                if (bytes.Length > 0 && Array.IndexOf(bytes, (byte)0) >= 0)
                    return (false, "检测到二进制内容, 不支持在面板编辑", "", fi.Length);
                return (true, "OK", Encoding.UTF8.GetString(bytes), fi.Length);
            }
            catch (Exception ex)
            {
                return (false, $"读取失败: {ex.Message}", "", 0);
            }
        }

        /// <summary>保存工作目录内文本文件内容(自动备份原文件为 .bak)。</summary>
        public static (bool Success, string Message) WriteInstanceTextFile(string serverKey, string relPath, string content)
        {
            try
            {
                var p = ResolveInstancePath(serverKey, relPath, out _, out var err);
                if (p == null) return (false, err);
                if (Directory.Exists(p)) return (false, "目标路径是目录");

                var size = Encoding.UTF8.GetByteCount(content);
                if (size > MaxEditableFileBytes)
                    return (false, $"内容过大({size} 字节), 超出可编辑上限");

                if (File.Exists(p)) File.Copy(p, p + ".bak", true);
                AtomicFile.WriteAllText(p, content);
                Output.Log($"实例文件 [{Markup.Escape(relPath ?? "")}] 已由面板编辑保存", 1, "Backend");
                BoardEventRecorder.Record("file_write", serverKey,
                    I18n.Get("board_event_file_write"), relPath ?? "");
                return (true, $"{Path.GetFileName(relPath)} 已保存(部分配置需服务端重启生效)");
            }
            catch (Exception ex)
            {
                return (false, $"保存失败: {ex.Message}");
            }
        }

        // ===== 玩家管理 API =====

        /// <summary>指定实例是否可走服务端管理协议(RM 模式且该实例的管理协议已连接)</summary>
        private static Function.MCManagementConnection? GetManagement(string serverKey)
        {
            if (!Function.Analyzer.UsesManagementProtocolOf(serverKey)) return null;
            var mgmt = Function.Analyzer.GetManagementConnection(serverKey);
            return mgmt != null && mgmt.IsConnected ? mgmt : null;
        }

        private static bool CanUseManagement(string serverKey) => GetManagement(serverKey) != null;

        /// <summary>封禁玩家</summary>
        public static (bool Success, string Message) BanPlayer(string serverKey, string playerName, string reason)
        {
            var mgmt = GetManagement(serverKey);
            if (mgmt != null)
                return mgmt.BanPlayer(playerName, reason);

            var cmd = string.IsNullOrWhiteSpace(reason)
                ? $"ban {playerName}"
                : $"ban {playerName} {reason}";
            var (ok, msg) = SendMcCommand(serverKey, cmd);
            return (ok, msg);
        }

        /// <summary>解封玩家</summary>
        public static (bool Success, string Message) PardonPlayer(string serverKey, string playerName)
        {
            var mgmt = GetManagement(serverKey);
            if (mgmt != null)
                return mgmt.UnbanPlayer(playerName);

            var (ok, msg) = SendMcCommand(serverKey, $"pardon {playerName}");
            return (ok, msg);
        }

        /// <summary>添加/移除 OP</summary>
        public static (bool Success, string Message) OpPlayer(string serverKey, string playerName, bool op)
        {
            if (string.IsNullOrWhiteSpace(playerName))
                return (false, "玩家名不能为空");

            var mgmt = GetManagement(serverKey);
            if (mgmt != null)
                return mgmt.OpPlayer(playerName, op);

            return SendMcCommand(serverKey, op ? $"op {playerName}" : $"deop {playerName}");
        }

        /// <summary>封禁IP</summary>
        public static (bool Success, string Message) BanIp(string serverKey, string ipOrPlayer, string reason)
        {
            var mgmt = GetManagement(serverKey);
            if (mgmt != null)
                return mgmt.BanIp(ipOrPlayer, reason);

            var cmd = string.IsNullOrWhiteSpace(reason)
                ? $"ban-ip {ipOrPlayer}"
                : $"ban-ip {ipOrPlayer} {reason}";
            var (ok, msg) = SendMcCommand(serverKey, cmd);
            return (ok, msg);
        }

        /// <summary>解封IP</summary>
        public static (bool Success, string Message) PardonIp(string serverKey, string ip)
        {
            var mgmt = GetManagement(serverKey);
            if (mgmt != null)
                return mgmt.UnbanIp(ip);

            var (ok, msg) = SendMcCommand(serverKey, $"pardon-ip {ip}");
            return (ok, msg);
        }

        /// <summary>踢出玩家</summary>
        public static (bool Success, string Message) KickPlayer(string serverKey, string playerName, string reason)
        {
            var mgmt = GetManagement(serverKey);
            if (mgmt != null)
                return mgmt.KickPlayer(playerName, reason);

            var cmd = string.IsNullOrWhiteSpace(reason)
                ? $"kick {playerName}"
                : $"kick {playerName} {reason}";
            var (ok, msg) = SendMcCommand(serverKey, cmd);
            return (ok, msg);
        }

        /// <summary>发送自定义MC命令到指定实例</summary>
        public static (bool Success, string Message) SendCustomCommand(string serverKey, string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return (false, "命令不能为空");
            // 去掉可能的 / 前缀
            command = command.TrimStart('/');
            return SendMcCommand(serverKey, command);
        }

        // ===== 全服配置 API (RM 管理协议 serversettings) =====

        /// <summary>读取全服配置(RM 模式管理协议)</summary>
        public static (bool Success, string Message, List<Function.ServerSettingValueResult> Settings)
            GetServerSettings(string serverKey)
        {
            var mgmt = GetManagement(serverKey);
            if (mgmt == null)
                return (false, "该实例未启用 RM 模式管理协议支持(需 MC 1.21.9+ 且已连接管理协议)",
                    new List<Function.ServerSettingValueResult>());
            try
            {
                var list = mgmt.ReadAllSettingsAsync().GetAwaiter().GetResult();
                return (true, "OK", list);
            }
            catch (Exception ex)
            {
                return (false, $"读取全服配置失败: {ex.Message}", new List<Function.ServerSettingValueResult>());
            }
        }

        /// <summary>应用全服配置(键值对, 类型由键定义推导)</summary>
        public static (bool Success, string Message) SetServerSettings(
            string serverKey, List<KeyValuePair<string, string>> values)
        {
            var mgmt = GetManagement(serverKey);
            if (mgmt == null)
                return (false, "该实例未启用 RM 模式管理协议支持");
            if (values == null || values.Count == 0)
                return (false, "没有需要保存的配置项");

            var errors = new List<string>();
            int applied = 0;
            foreach (var kv in values)
            {
                var def = Function.MCManagementConnection.SettingDefs.FirstOrDefault(d => d.Key == kv.Key);
                if (def == null)
                {
                    errors.Add($"{kv.Key}: 不支持的配置项");
                    continue;
                }
                var (ok, msg) = mgmt.ApplySetting(def.Key, def.Type, kv.Value);
                if (ok) applied++;
                else errors.Add($"{kv.Key}: {msg}");
            }

            if (errors.Count == 0)
                return (true, $"已应用 {applied} 项");
            return (false, $"部分失败: {string.Join("; ", errors)}");
        }

        // ===== RM 管理协议通用操作(游戏规则/名单/服务器控制/能力发现) =====

        /// <summary>
        /// RM 管理协议通用操作入口。
        /// category: gamerules / allowlist / operators / bans / ip_bans / server / discover
        /// action:   get / add / remove / clear / set / save / stop / system_message / status / discover
        /// 非 RM 实例时对可映射的操作回退命令通道, 读取类操作返回不支持提示。
        /// </summary>
        public static (bool Success, string Message, List<Function.RmEntry> Entries, string Raw) RmManage(
            string serverKey, string category, string action, string target, string value, bool flag)
        {
            category = (category ?? "").Trim().ToLowerInvariant();
            action = (action ?? "").Trim().ToLowerInvariant();
            target = target ?? "";
            value = value ?? "";

            var mgmt = GetManagement(serverKey);
            if (mgmt != null)
            {
                try
                {
                    switch (category)
                    {
                        case "gamerules":
                            if (action == "get")
                            {
                                var rules = mgmt.ReadGamerulesAsync().GetAwaiter().GetResult();
                                return (true, "OK", rules, "");
                            }
                            if (string.IsNullOrWhiteSpace(target))
                                return (false, "缺少规则名", new List<Function.RmEntry>(), "");
                            var (gok, gmsg) = mgmt.SetGamerule(target, value);
                            return (gok, gmsg, new List<Function.RmEntry>(), "");

                        case "allowlist":
                        case "operators":
                        case "bans":
                        case "ip_bans":
                            if (action == "get")
                            {
                                var list = mgmt.ReadListAsync(category).GetAwaiter().GetResult();
                                return (true, "OK", list, "");
                            }
                            if (action == "clear" && category != "allowlist")
                                return (false, "仅白名单支持一键清空", new List<Function.RmEntry>(), "");
                            if (action != "clear" && string.IsNullOrWhiteSpace(target))
                                return (false, "请输入名称/IP", new List<Function.RmEntry>(), "");
                            var (lok, lmsg) = mgmt.ListModify(category, action, target, value);
                            return (lok, lmsg, new List<Function.RmEntry>(), "");

                        case "server":
                            switch (action)
                            {
                                case "save":
                                    var sv = mgmt.ServerSave();
                                    return (sv.Success, sv.Message, new List<Function.RmEntry>(), "");
                                case "stop":
                                    var st = mgmt.ServerStop();
                                    return (st.Success, st.Message, new List<Function.RmEntry>(), "");
                                case "system_message":
                                    if (string.IsNullOrWhiteSpace(value))
                                        return (false, "请输入系统消息内容", new List<Function.RmEntry>(), "");
                                    var sm = mgmt.SystemMessage(value, flag);
                                    return (sm.Success, sm.Message, new List<Function.RmEntry>(), "");
                                case "status":
                                    var (stOk, stMsg, stRaw) = mgmt.ServerStatus();
                                    return (stOk, stMsg, new List<Function.RmEntry>(), stRaw);
                                default:
                                    return (false, "未知服务器操作: " + action, new List<Function.RmEntry>(), "");
                            }

                        case "discover":
                            var (dOk, dMsg, dRaw) = mgmt.Discover();
                            return (dOk, dMsg, new List<Function.RmEntry>(), dRaw);

                        default:
                            return (false, "未知操作类别: " + category, new List<Function.RmEntry>(), "");
                    }
                }
                catch (Exception ex)
                {
                    return (false, "管理协议操作失败: " + ex.Message, new List<Function.RmEntry>(), "");
                }
            }

            // ===== 非 RM: 命令通道回退(读取类不支持) =====
            if (action == "get" || action == "status" || category == "discover")
                return (false, "该实例未启用 RM 模式管理协议支持(需 MC 1.21.9+ 且已连接管理协议)", new List<Function.RmEntry>(), "");

            string cmd;
            switch (category)
            {
                case "gamerules":
                    if (string.IsNullOrWhiteSpace(target)) return (false, "缺少规则名", new List<Function.RmEntry>(), "");
                    cmd = $"gamerule {target} {value}";
                    break;
                case "allowlist":
                    if (action == "add") cmd = $"whitelist add {target}";
                    else if (action == "remove") cmd = $"whitelist remove {target}";
                    else return (false, "非 RM 实例不支持清空白名单", new List<Function.RmEntry>(), "");
                    break;
                case "operators":
                    if (action == "add") cmd = $"op {target}";
                    else if (action == "remove") cmd = $"deop {target}";
                    else return (false, "非 RM 实例不支持清空 OP 名单", new List<Function.RmEntry>(), "");
                    break;
                case "bans":
                    if (action == "add") cmd = string.IsNullOrWhiteSpace(value) ? $"ban {target}" : $"ban {target} {value}";
                    else if (action == "remove") cmd = $"pardon {target}";
                    else return (false, "非 RM 实例不支持清空封禁名单", new List<Function.RmEntry>(), "");
                    break;
                case "ip_bans":
                    if (action == "add") cmd = string.IsNullOrWhiteSpace(value) ? $"ban-ip {target}" : $"ban-ip {target} {value}";
                    else if (action == "remove") cmd = $"pardon-ip {target}";
                    else return (false, "非 RM 实例不支持清空 IP 封禁名单", new List<Function.RmEntry>(), "");
                    break;
                case "server":
                    if (action == "save") cmd = "save-all";
                    else if (action == "stop") cmd = "stop";
                    else if (action == "system_message") { cmd = $"say {value}"; }
                    else return (false, "非 RM 实例不支持该服务器操作", new List<Function.RmEntry>(), "");
                    break;
                default:
                    return (false, "未知操作类别: " + category, new List<Function.RmEntry>(), "");
            }

            var (ok, msg) = SendMcCommand(serverKey, cmd);
            return (ok, msg, new List<Function.RmEntry>(), "");
        }

        // ===== 实例日志 API =====

        /// <summary>
        /// 读取实例的 MC 服务端日志(latest.log)。
        /// </summary>
        public static (bool Success, string Message, string Content, int TotalLines)
            GetInstanceLog(string serverKey, int maxLines = 0)
        {
            try
            {
                var workPath = ResolveWorkPath(serverKey);
                if (workPath == null)
                    return (false, "服务端工作目录未配置或不存在", "", 0);

                var logFile = Path.Combine(workPath, "logs", "latest.log");
                if (!File.Exists(logFile))
                    return (false, "日志文件不存在(logs/latest.log)", "", 0);

                var allLines = File.ReadAllLines(logFile, Encoding.UTF8);
                var totalLines = allLines.Length;

                string content;
                if (maxLines > 0 && totalLines > maxLines)
                {
                    content = string.Join('\n', allLines.Skip(totalLines - maxLines));
                    content = $"...(已截断前 {totalLines - maxLines} 行，仅显示最后 {maxLines} 行)\n" + content;
                }
                else
                {
                    content = string.Join('\n', allLines);
                }

                return (true, "OK", content, totalLines);
            }
            catch (Exception ex)
            {
                return (false, $"读取日志失败: {ex.Message}", "", 0);
            }
        }

        /// <summary>
        /// 分析实例错误日志(使用 regex_settings 中的正则提取错误)。
        /// 错误块切分规则统一维护于 ErrorBlockParser(与 ErrorStreamDetector 实时识别同源)。
        /// </summary>
        public static (bool Success, string Message, Dictionary<int, string> Errors)
            AnalyzeInstanceErrors(string serverKey)
        {
            try
            {
                var workPath = ResolveWorkPath(serverKey);
                if (workPath == null)
                    return (false, "服务端工作目录未配置或不存在", new Dictionary<int, string>());

                var logFile = Path.Combine(workPath, "logs", "latest.log");
                if (!File.Exists(logFile))
                    return (false, "日志文件不存在(logs/latest.log)", new Dictionary<int, string>());

                ContentManager.Initialize();

                var handlerRegex = ErrorBlockParser.CreateHandlerRegex(ContentManager.Regex.Console_Error.Handler);
                if (handlerRegex == null)
                    return (false, "正则表达式配置为空或无效", new Dictionary<int, string>());

                var logLines = File.ReadAllLines(logFile, Encoding.UTF8);
                var blocks = ErrorBlockParser.ParseBlocks(logLines, handlerRegex);

                var errors = new Dictionary<int, string>();
                int limit = ContentManager.Regex.Console_Error.Limit;
                for (int i = 0; i < blocks.Count && errors.Count < limit; i++)
                    errors[i + 1] = blocks[i];

                Output.Log($"实例 {serverKey} 日志分析完成: 共 {errors.Count} 条错误", 1, "Backend");
                return (true, $"分析完成，共 {errors.Count} 条错误", errors);
            }
            catch (Exception ex)
            {
                return (false, $"分析失败: {ex.Message}", new Dictionary<int, string>());
            }
        }

        /// <summary>
        /// AI分析实例错误(将错误内容发送给AI)。
        /// </summary>
        public static async Task<(bool Success, string Message, string Result)>
            AiAnalyzeInstanceErrors(string serverKey, string range)
        {
            try
            {
                // 先分析错误
                var (analyzeOk, analyzeMsg, errors) = AnalyzeInstanceErrors(serverKey);
                if (!analyzeOk)
                    return (false, analyzeMsg, "");

                if (errors.Count == 0)
                    return (false, "未检测到错误，无需AI分析", "");

                // 解析范围
                List<int> targetIndices;
                if (string.IsNullOrWhiteSpace(range) || range == "all")
                {
                    targetIndices = errors.Keys.ToList();
                }
                else
                {
                    var parsed = ParseErrorRange(range, errors.Keys.ToList());
                    if (parsed == null || parsed.Count == 0)
                        return (false, $"无效的范围参数: {range}", "");
                    targetIndices = parsed;
                }

                var sb = new StringBuilder();
                foreach (int idx in targetIndices)
                {
                    if (errors.TryGetValue(idx, out var errorText))
                    {
                        sb.AppendLine($"[错误 #{idx}]");
                        sb.AppendLine(errorText);
                        sb.AppendLine("---");
                    }
                }

                Output.Log($"正在将 {targetIndices.Count} 条错误发送给AI分析...(实例: {serverKey})", 1, "Backend");

                var aiResponse = await Intelligence.AnalyzeWithAi(sb.ToString());

                if (string.IsNullOrWhiteSpace(aiResponse))
                    return (false, "AI分析未返回结果", "");

                // 结果同时回显到 RtCli 控制台(来源 AiAnalyze), 并写入 ai_save.yml 供面板"自动化结果"展示
                Output.Log($"AI分析结果(实例: {serverKey}):", 1, "AiAnalyze");
                foreach (string lineText in aiResponse.Replace("\r", "").Split('\n'))
                    Output.Log(lineText.TrimEnd(), 1, "AiAnalyze");
                Intelligence.AiAutoRunner.SaveManualResponse(serverKey, sb.ToString(), aiResponse);

                return (true, "AI分析完成", aiResponse);
            }
            catch (Exception ex)
            {
                return (false, $"AI分析失败: {ex.Message}", "");
            }
        }

        /// <summary>
        /// 基础分析实例错误(对应控制台 .fx base): 将选定错误与 base 规则库正则匹配，返回主题/方案/修复脚本。
        /// </summary>
        public static (bool Success, string Message, List<(string Topic, string Solution, string Action, string Matched)> Matches)
            BaseAnalyzeInstanceErrors(string serverKey, string range)
        {
            try
            {
                var (analyzeOk, analyzeMsg, errors) = AnalyzeInstanceErrors(serverKey);
                if (!analyzeOk)
                    return (false, analyzeMsg, new List<(string, string, string, string)>());

                if (errors.Count == 0)
                    return (false, "未检测到错误，无需基础分析", new List<(string, string, string, string)>());

                List<int> targetIndices;
                if (string.IsNullOrWhiteSpace(range) || range == "all")
                    targetIndices = errors.Keys.ToList();
                else
                {
                    var parsed = ParseErrorRange(range, errors.Keys.ToList());
                    if (parsed == null || parsed.Count == 0)
                        return (false, $"无效的范围参数: {range}", new List<(string, string, string, string)>());
                    targetIndices = parsed;
                }

                var sb = new StringBuilder();
                foreach (int idx in targetIndices)
                {
                    if (errors.TryGetValue(idx, out var errorText))
                    {
                        sb.AppendLine($"[错误 #{idx}]");
                        sb.AppendLine(errorText);
                        sb.AppendLine("---");
                    }
                }
                string combined = sb.ToString();

                var baseEntries = ContentManager.GetAllBaseEntries();
                var matches = new List<(string Topic, string Solution, string Action, string Matched, int Length)>();

                foreach (var entry in baseEntries)
                {
                    try
                    {
                        var regex = new Regex(entry.Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
                        var match = regex.Match(combined);
                        if (match.Success)
                        {
                            string matchedText = match.Value.Length > 200
                                ? match.Value.Substring(0, 197) + "..."
                                : match.Value;
                            matches.Add((entry.Topic, entry.Solution, entry.Action, matchedText, match.Value.Length));
                        }
                    }
                    catch (Exception ex)
                    {
                        Output.Log($"基础规则 {entry.Topic} 的正则无效: {ex.Message}", 2, "Backend");
                    }
                }

                var ordered = matches.OrderByDescending(m => m.Length)
                    .Select(m => (m.Topic, m.Solution, m.Action, m.Matched))
                    .ToList();

                Output.Log($"实例 {serverKey} 基础分析完成: 匹配 {ordered.Count} 条规则", 1, "Backend");
                return (true, ordered.Count == 0 ? "未匹配到基础规则" : $"基础分析完成，匹配 {ordered.Count} 条规则", ordered);
            }
            catch (Exception ex)
            {
                return (false, $"基础分析失败: {ex.Message}", new List<(string, string, string, string)>());
            }
        }

        private static List<int>? ParseErrorRange(string range, List<int> availableKeys)
        {
            if (string.IsNullOrWhiteSpace(range))
                return availableKeys;

            var result = new List<int>();
            var parts = range.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Contains('-'))
                {
                    var rangeParts = trimmed.Split('-');
                    if (rangeParts.Length == 2 && int.TryParse(rangeParts[0].Trim(), out int start) && int.TryParse(rangeParts[1].Trim(), out int end))
                    {
                        for (int i = start; i <= end; i++)
                            if (availableKeys.Contains(i))
                                result.Add(i);
                    }
                }
                else if (int.TryParse(trimmed, out int idx))
                {
                    if (availableKeys.Contains(idx))
                        result.Add(idx);
                }
            }
            return result;
        }
    }

    // ===== 实例管理数据传输类(供 Backend 与 Connector 共用) =====
    public class InstanceListItem
    {
        public string Id { get; set; } = "";
        public string Identifier { get; set; } = "";
        public string Name { get; set; } = "";
        public string GroupId { get; set; } = "";
        public string GroupName { get; set; } = "";
        public bool IsRunning { get; set; }
        public bool Hidden { get; set; }
        public bool AutoRestart { get; set; }
        public string WorkPath { get; set; } = "";
        public string JavaPath { get; set; } = "";
        public string RunFlags { get; set; } = "";
        public string AnalyzerMode { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string LastStartedAt { get; set; } = "";
    }

    public class GroupListItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> MemberIds { get; set; } = new List<string>();
        public string CreatedAt { get; set; } = "";
    }

    public class BackupItem
    {
        public string FileName { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public long SizeBytes { get; set; }
    }

    /// <summary>
    /// 玩家事件记录器: 订阅 EventBus 的玩家事件, 持久化到 panel_data.json
    /// 事件归属当前服务器(Config.App.CurrentServer)
    /// </summary>
    public static class PlayerEventRecorder
    {
        private static bool _started = false;
        private static readonly object _lock = new();
        // 内存缓冲: 按实例ID分组, 减少磁盘写入频率
        private static readonly Dictionary<string, List<PlayerEventEntry>> _buffer = new();
        private static Timer? _flushTimer;
        private const int FlushIntervalMs = 5000; // 5秒批量写入一次

        public static void Start()
        {
            lock (_lock)
            {
                if (_started) return;
                _started = true;

                EventBus.Subscribe<PlayerJoinEvent>(OnPlayerJoin);
                EventBus.Subscribe<PlayerConnectEvent>(OnPlayerConnect);
                EventBus.Subscribe<PlayerLostEvent>(OnPlayerLost);
                EventBus.Subscribe<PlayerLeaveEvent>(OnPlayerLeave);
                EventBus.Subscribe<PlayerCommandEvent>(OnPlayerCommand);
                EventBus.Subscribe<PlayerChatEvent>(OnPlayerChat);
                EventBus.Subscribe<PlayerSetModeEvent>(OnPlayerSetMode);
                EventBus.Subscribe<CustomPlayerEvent>(OnCustomPlayer);
                EventBus.Subscribe<PlayerOpEvent>(OnPlayerOp);
                EventBus.Subscribe<PlayerBanEvent>(OnPlayerBan);

                _flushTimer = new Timer(_ => FlushAll(), null, FlushIntervalMs, FlushIntervalMs);
                Output.Log("玩家事件记录器已启动", 1, "PlayerEventRecorder");
            }
        }

        /// <summary>事件归属实例: 优先取当前日志流的实例上下文, 否则回退到当前选中实例</summary>
        private static string CurrentServer =>
            Function.InstanceContext.Current ?? Config.App.CurrentServer ?? "";

        private static void Buffer(string eventType, string playerName, string triggerTime, string detail)
        {
            var entry = new PlayerEventEntry
            {
                EventType = eventType,
                PlayerName = playerName ?? "",
                TriggerTime = triggerTime ?? "",
                RecordedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                Detail = detail ?? ""
            };
            var key = CurrentServer;
            if (string.IsNullOrEmpty(key)) return;
            lock (_buffer)
            {
                if (!_buffer.TryGetValue(key, out var list))
                {
                    list = new List<PlayerEventEntry>();
                    _buffer[key] = list;
                }
                list.Add(entry);
                // 内存缓冲上限, 防止突发流量内存溢出
                if (list.Count > 200)
                    list.RemoveRange(0, list.Count - 200);
            }
        }

        private static void OnPlayerJoin(PlayerJoinEvent e) =>
            Buffer("join", e.PlayerName, e.PlayerTriggerTime, "");
        private static void OnPlayerConnect(PlayerConnectEvent e) =>
            Buffer("connect", e.PlayerName, e.PlayerTriggerTime, e.PlayerIp ?? "");
        private static void OnPlayerLost(PlayerLostEvent e) =>
            Buffer("lost", e.PlayerName, e.PlayerTriggerTime, e.PlayerLostReason ?? "");
        private static void OnPlayerLeave(PlayerLeaveEvent e) =>
            Buffer("leave", e.PlayerName, e.PlayerTriggerTime, "");
        private static void OnPlayerCommand(PlayerCommandEvent e) =>
            Buffer("command", e.PlayerName, e.PlayerTriggerTime, e.Command ?? "");
        private static void OnPlayerChat(PlayerChatEvent e) =>
            Buffer("chat", e.PlayerName, e.PlayerTriggerTime, e.Message ?? "");
        private static void OnPlayerSetMode(PlayerSetModeEvent e) =>
            Buffer("setmode", e.PlayerName, e.PlayerTriggerTime, e.PlayerMode ?? "");
        private static void OnCustomPlayer(CustomPlayerEvent e)
        {
            var detail = string.Join("; ", e.Parameters.Select(p => $"{p.Key}={p.Value}"));
            Buffer(e.EventName, e.PlayerName, e.PlayerTriggerTime, detail);
        }

        private static void OnPlayerOp(PlayerOpEvent e) =>
            Buffer(e.Granted ? "op" : "deop", e.PlayerName, e.PlayerTriggerTime, e.Source ?? "");

        private static void OnPlayerBan(PlayerBanEvent e)
        {
            bool ip = e.BanType == "ip_ban";
            string type = e.Banned ? (ip ? "ip_ban" : "ban") : (ip ? "ip_unban" : "unban");
            Buffer(type, e.Target, e.PlayerTriggerTime, e.Reason ?? "");
        }

        /// <summary>将内存缓冲的事件批量写入磁盘</summary>
        private static void FlushAll()
        {
            List<KeyValuePair<string, List<PlayerEventEntry>>> snapshot;
            lock (_buffer)
            {
                if (_buffer.Count == 0) return;
                snapshot = _buffer.Select(kvp =>
                    new KeyValuePair<string, List<PlayerEventEntry>>(kvp.Key, kvp.Value.ToList())).ToList();
                _buffer.Clear();
            }
            foreach (var kvp in snapshot)
            {
                foreach (var entry in kvp.Value)
                {
                    try { PanelDataManager.AppendPlayerEvent(kvp.Key, entry); }
                    catch (Exception ex) { Output.Log($"写入玩家事件失败: {ex.Message}", 3, "PlayerEventRecorder"); }
                }
            }
        }

        /// <summary>获取指定实例的玩家事件(先刷新缓冲, 再读取磁盘)</summary>
        public static List<PlayerEventEntry> GetEvents(string instanceId, int limit = 0)
        {
            FlushAll();
            return PanelDataManager.GetPlayerEvents(instanceId, limit);
        }

        /// <summary>清空指定实例的玩家事件</summary>
        public static (bool Success, string Message) ClearEvents(string instanceId)
        {
            lock (_buffer)
            {
                _buffer.Remove(instanceId);
            }
            return PanelDataManager.ClearPlayerEvents(instanceId);
        }
    }

    /// <summary>
    /// 事件板记录器: 订阅实例生命周期事件(开启/关闭/崩溃/自动重启/stop无响应),
    /// 缓冲后批量写入 panel_data.json 的 boardEvents 节点, 供面板事件板展示。
    /// </summary>
    public static class BoardEventRecorder
    {
        private static bool _started = false;
        private static readonly object _lock = new();
        // 内存缓冲: 减少磁盘写入频率
        private static readonly List<BoardEventEntry> _buffer = new();
        private static Timer? _flushTimer;
        private const int FlushIntervalMs = 5000; // 5秒批量写入一次

        public static void Start()
        {
            lock (_lock)
            {
                if (_started) return;
                _started = true;

                EventBus.Subscribe<ServerStartEvent>(OnServerStart);
                EventBus.Subscribe<ServerStopEvent>(OnServerStop);
                EventBus.Subscribe<ServerCrashEvent>(OnServerCrash);
                EventBus.Subscribe<AutoRestartEvent>(OnAutoRestart);
                EventBus.Subscribe<ServerStopUnresponsiveEvent>(OnStopUnresponsive);
                EventBus.Subscribe<PlayerOpEvent>(OnPlayerOp);
                EventBus.Subscribe<PlayerBanEvent>(OnPlayerBan);
                // 扩展加载/卸载与配置重载(全局事件, 无实例标识时回退到当前实例/系统)
                EventBus.Subscribe<ExtensionLoadEvent>(OnExtensionLoad);
                EventBus.Subscribe<ExtensionUnloadEvent>(OnExtensionUnload);
                EventBus.Subscribe<ConfigReloadEvent>(OnConfigReload);
                // AI 自动化活动(全局事件): 任务开始/完成/失败、工具调用
                EventBus.Subscribe<AiActivityEvent>(OnAiActivity);

                _flushTimer = new Timer(_ => FlushAll(), null, FlushIntervalMs, FlushIntervalMs);
                Output.Log("事件板记录器已启动", 1, "BoardEventRecorder");
            }
        }

        private static string ResolveInstanceName(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            return Config.App.ServerList.TryGetValue(key, out var entry) && !string.IsNullOrWhiteSpace(entry.ServerName)
                ? entry.ServerName
                : key;
        }

        /// <summary>
        /// 全局事件的实例标识: 扩展加载/卸载、配置重载、面板指令等不属于任何单个实例。
        /// 面板据此把这类事件单列成「全局」分组, 而不是并到当前实例名下。
        /// </summary>
        public const string GlobalInstanceId = "*";

        /// <summary>
        /// 事件归属判定。
        /// 全局事件(GlobalInstanceId)原样保留, 绝不回退到"当前实例" —— 否则扩展加载这类
        /// 与实例无关的事件会被错记到当前实例名下(此前事件板把全局事件显示成实例事件就是此因)。
        /// 只有空标识才回退: 先当前实例, 再 system。
        /// </summary>
        internal static string ResolveEventInstanceId(string? instanceId)
        {
            if (instanceId == GlobalInstanceId) return GlobalInstanceId;
            if (string.IsNullOrEmpty(instanceId)) instanceId = Config.App.CurrentServer;
            if (string.IsNullOrEmpty(instanceId)) instanceId = "system";
            return instanceId;
        }

        /// <summary>记录一条全局事件(不属于任何实例)</summary>
        private static void BufferGlobal(string eventType, string title, string detail)
            => Buffer(eventType, GlobalInstanceId, title, detail);

        /// <summary>
        /// 记录一条普通操作事件(归属于指定实例)。
        /// 供文件/备份/插件模组等面板与命令通道操作调用; 标题与详情由调用方按 i18n 组装。
        /// </summary>
        public static void Record(string eventType, string instanceId, string title, string detail = "")
            => Buffer(eventType, instanceId, title, detail);

        private static void Buffer(string eventType, string? instanceId, string title, string detail)
        {
            instanceId = ResolveEventInstanceId(instanceId);

            string instanceName = instanceId == GlobalInstanceId
                ? I18n.Get("board_event_global")
                : ResolveInstanceName(instanceId);

            var entry = new BoardEventEntry
            {
                EventType = eventType,
                InstanceId = instanceId,
                InstanceName = instanceName,
                Title = title,
                Detail = detail ?? "",
                RecordedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")
            };
            lock (_buffer)
            {
                _buffer.Add(entry);
                // 内存缓冲上限, 防止突发流量内存溢出
                if (_buffer.Count > 100)
                    _buffer.RemoveRange(0, _buffer.Count - 100);
            }
        }

        private static void OnServerStart(ServerStartEvent e) =>
            Buffer("started", e.ServerKey, I18n.Get("board_event_started"), $"模式 {Function.Analyzer.CurrentMode}");

        private static void OnServerStop(ServerStopEvent e) =>
            Buffer("stopped", e.ServerKey, I18n.Get("board_event_stopped"), "");

        private static void OnServerCrash(ServerCrashEvent e) =>
            Buffer("crashed", e.ServerKey, I18n.Get("board_event_crashed"),
                I18n.Get("board_event_crashed_detail", e.ExitCode));

        private static void OnAutoRestart(AutoRestartEvent e) =>
            Buffer("auto_restarted", e.ServerKey, I18n.Get("board_event_auto_restarted"),
                I18n.Get("board_event_auto_restarted_detail", e.AttemptCount,
                    e.MaxRetries > 0 ? e.MaxRetries.ToString() : I18n.Get("board_event_retry_unlimited")));

        private static void OnStopUnresponsive(ServerStopUnresponsiveEvent e) =>
            Buffer("stop_unresponsive", e.ServerKey, I18n.Get("board_event_stop_unresponsive"),
                I18n.Get("board_event_stop_unresponsive_detail", e.WaitedSeconds,
                    e.HadLogActivity
                        ? I18n.Get("board_event_unresponsive_slow")
                        : I18n.Get("board_event_unresponsive_silent")));

        private static void OnPlayerOp(PlayerOpEvent e)
        {
            string by = string.IsNullOrWhiteSpace(e.Source) ? "" : I18n.Get("board_event_op_by", e.Source);
            Buffer(e.Granted ? "op" : "deop", Function.InstanceContext.Current,
                I18n.Get(e.Granted ? "board_event_op_added" : "board_event_op_removed"),
                I18n.Get("board_event_op_detail", e.PlayerName, by));
        }

        private static void OnPlayerBan(PlayerBanEvent e)
        {
            bool ip = e.BanType == "ip_ban";
            string type = e.Banned ? (ip ? "ip_ban" : "ban") : (ip ? "ip_unban" : "unban");
            string titleKey = e.Banned
                ? (ip ? "board_event_ip_ban_added" : "board_event_ban_added")
                : (ip ? "board_event_ip_ban_removed" : "board_event_ban_removed");
            string reason = string.IsNullOrWhiteSpace(e.Reason) ? "" : I18n.Get("board_event_ban_reason", e.Reason);
            Buffer(type, Function.InstanceContext.Current, I18n.Get(titleKey),
                I18n.Get("board_event_ban_detail", e.Target, reason));
        }

        private static void OnExtensionLoad(ExtensionLoadEvent e) =>
            BufferGlobal("plugin_load",
                I18n.Get("board_event_plugin_load"),
                I18n.Get("board_event_plugin_load_detail", e.ExtensionName,
                    string.IsNullOrWhiteSpace(e.Version) ? I18n.Get("board_event_plugin_no_version") : e.Version));

        private static void OnExtensionUnload(ExtensionUnloadEvent e) =>
            BufferGlobal("plugin_unload",
                I18n.Get("board_event_plugin_unload"),
                I18n.Get("board_event_plugin_unload_detail", e.ExtensionName));

        /// <summary>AI 自动化活动 -> 事件板(全局事件, 不属于任何实例)。</summary>
        private static void OnAiActivity(AiActivityEvent e)
        {
            string type = (e.Kind ?? "") switch
            {
                "task_start" => "ai_task_start",
                "task_done" => "ai_task_done",
                "task_fail" => "ai_task_fail",
                "tool_call" => "ai_tool_call",
                "runner" => "ai_runner",
                _ => "ai_activity"
            };
            string title = (e.Kind ?? "") switch
            {
                "task_start" => "AI 任务开始",
                "task_done" => "AI 任务完成",
                "task_fail" => "AI 任务失败",
                "tool_call" => "AI 调用工具",
                "runner" => "AI 自动化状态",
                _ => "AI 活动"
            };
            string detail = e.TaskKey.Length > 0 ? $"[{e.TaskKey}] {e.Detail}" : e.Detail;
            BufferGlobal(type, title, detail);
        }

        private static void OnConfigReload(ConfigReloadEvent e) =>
            BufferGlobal("config_reload",
                I18n.Get("board_event_config_reload"),
                I18n.Get("board_event_config_reload_detail"));

        /// <summary>
        /// 记录一条面板下发的指令(供 Backend.DispatchCommand 调用)。
        /// 标题为指令原文, 详情标注来源为面板; 事件板读取类调用不经过命令分发, 不会在此刷屏。
        /// </summary>
        public static void RecordPanelCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return;
            BufferGlobal("panel_command",
                command.Trim(),
                I18n.Get("board_event_panel_command_detail"));
        }

        /// <summary>将内存缓冲的事件批量写入磁盘</summary>
        private static void FlushAll()
        {
            List<BoardEventEntry> snapshot;
            lock (_buffer)
            {
                if (_buffer.Count == 0) return;
                snapshot = _buffer.ToList();
                _buffer.Clear();
            }
            foreach (var entry in snapshot)
            {
                try { PanelDataManager.AppendBoardEvent(entry); }
                catch (Exception ex) { Output.Log($"写入事件板失败: {ex.Message}", 3, "BoardEventRecorder"); }
            }
        }
    }

    /// <summary>AI 技能导入包装(YAML 对象形式: skills: [...])</summary>
    public class AiSkillImportWrapper
    {
        public List<AiSkill> Skills { get; set; } = new List<AiSkill>();
    }

    /// <summary>AI 工作流运行参数(对应 ai_settings.yml 中 ServerAutoAi 的连接/运行字段)</summary>
    public class AiRuntimeView
    {
        /// <summary>系统提示词(总)</summary>
        public string Prompt { get; set; } = "";

        /// <summary>AI输出最大token数</summary>
        public int MaxTokens { get; set; }

        /// <summary>API地址(留空复用 Console_Error 配置)</summary>
        public string ApiEndpoint { get; set; } = "";

        /// <summary>API密钥(仅提交时使用; 回显时由面板掩码展示)</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>模型名称</summary>
        public string Model { get; set; } = "";

        /// <summary>生成温度</summary>
        public double Temperature { get; set; }

        /// <summary>请求超时(秒)</summary>
        public int RequestTimeoutSeconds { get; set; }

        /// <summary>异常时是否自动重连</summary>
        public bool RetryOnError { get; set; }

        /// <summary>最大重试次数</summary>
        public int RetryCount { get; set; }

        /// <summary>是否保存AI回复(上下文缓存)</summary>
        public bool SaveResponse { get; set; }

        /// <summary>上下文缓存最大条数</summary>
        public int ContextCacheSize { get; set; }

        /// <summary>工具调用最大轮次</summary>
        public int MaxToolRounds { get; set; }
    }
}
