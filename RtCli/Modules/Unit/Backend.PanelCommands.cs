using System.Text;
using RtCli.Modules.Extension;
using RtCli.Modules.Function;
using Spectre.Console;

namespace RtCli.Modules.Unit
{
    /// <summary>
    /// 面板命令处理器表(#4 单一注册来源)。
    /// 每个命令在此登记一行, 处理器返回 (是否成功, 结果摘要, 需广播到面板的纯文本)。
    /// 处理器只做状态变更与控制台渲染, 不自行广播——广播由 DispatchCommand 在锁外统一完成,
    /// 因此全局命令锁不再覆盖渲染与广播。
    /// </summary>
    public static partial class Backend
    {
        private static bool _panelCommandsRegistered;
        private static readonly object _panelRegLock = new();

        /// <summary>
        /// 注册全部面板命令(幂等; 首次分发时调用)。
        /// 必须加锁: 面板分发与 CLI 分发可能并发首次调用, 若无锁会出现
        /// "另一个线程看到已注册标志, 但注册表其实还没填完" —— 表现为随机解析不到命令。
        /// </summary>
        internal static void EnsurePanelCommandsRegistered()
        {
            lock (_panelRegLock)
            {
                if (_panelCommandsRegistered) return;
                RegisterPanelCommandsCore();
                _panelCommandsRegistered = true;
            }
        }

        /// <summary>面板命令注册体(由 EnsurePanelCommandsRegistered 在锁内调用)</summary>
        private static void RegisterPanelCommandsCore()
        {

            // ---- rt 系列 ----
            CommandRegistry.RegisterPanelCommand("rt", RtVersion, needsLock: false);
            CommandRegistry.RegisterPanelCommand("rt help", RtHelp, needsLock: false);
            CommandRegistry.RegisterPanelCommand(".help", RtHelp, needsLock: false);
            CommandRegistry.RegisterPanelCommand("rt status", RtStatus, needsLock: false);
            CommandRegistry.RegisterPanelCommand("rt clients", RtClients, needsLock: false);
            CommandRegistry.RegisterPanelCommand("rt extensions", RtExtensions, needsLock: false);
            CommandRegistry.RegisterPanelCommand("rt reload", RtReload);

            // ---- .cfg 系列 ----
            CommandRegistry.RegisterPanelCommand(".cfg reload", CfgReload);
            CommandRegistry.RegisterPanelCommand(".cfg show", CfgShow, needsLock: false);

            // ---- .server 系列 ----
            CommandRegistry.RegisterPanelCommand(".server list", ServerList, needsLock: false);
            CommandRegistry.RegisterPanelPrefixCommand(".server change", ServerChange);
            CommandRegistry.RegisterPanelCommand(".server status", ServerStatus, needsLock: false);
            CommandRegistry.RegisterPanelCommand(".server start", ServerStart);
            CommandRegistry.RegisterPanelCommand(".server stop", ServerStop);
            CommandRegistry.RegisterPanelCommand(".server restart", ServerRestart);
            CommandRegistry.RegisterPanelCommand(".server detach", ServerDetach);
            CommandRegistry.RegisterPanelCommand(".server rcon", ServerRconUsage, needsLock: false);
            CommandRegistry.RegisterPanelPrefixCommand(".server rcon", ServerRcon);

            // ---- .ai 系列 ----
            CommandRegistry.RegisterPanelCommand(".ai", AiRoot, needsLock: false);
            CommandRegistry.RegisterPanelCommand(".ai start", AiStart);
            CommandRegistry.RegisterPanelCommand(".ai stop", AiStop);
            CommandRegistry.RegisterPanelCommand(".ai list", AiList, needsLock: false);
            CommandRegistry.RegisterPanelCommand(".ai reload", AiReload);
            CommandRegistry.RegisterPanelPrefixCommand(".ai run", AiRun);
            CommandRegistry.RegisterPanelCommand(".ai run", AiRunUsage);
            CommandRegistry.RegisterPanelPrefixCommand(".ai clear", AiClear);
            CommandRegistry.RegisterPanelCommand(".ai clear", AiClearAll);

            // ---- .auto 系列 ----
            CommandRegistry.RegisterPanelCommand(".auto", AutoRoot, needsLock: false);
            CommandRegistry.RegisterPanelCommand(".auto start", AutoStart);
            CommandRegistry.RegisterPanelCommand(".auto stop", AutoStop);
            CommandRegistry.RegisterPanelCommand(".auto list", AutoList, needsLock: false);
            CommandRegistry.RegisterPanelPrefixCommand(".auto on", AutoOn);
            CommandRegistry.RegisterPanelCommand(".auto on", AutoOnUsage);
            CommandRegistry.RegisterPanelPrefixCommand(".auto off", AutoOff);
            CommandRegistry.RegisterPanelCommand(".auto off", AutoOffUsage);

            // ---- .fx 系列 ----
            CommandRegistry.RegisterPanelCommand(".fx", FxRoot, needsLock: false);
            CommandRegistry.RegisterPanelCommand(".fx add", FxAddUsage, needsLock: false);
            CommandRegistry.RegisterPanelPrefixCommand(".fx add", FxAdd);
            CommandRegistry.RegisterPanelCommand(".fx get", FxGet, needsLock: false);
            CommandRegistry.RegisterPanelPrefixCommand(".fx get", FxGet, needsLock: false);
            CommandRegistry.RegisterPanelCommand(".fx list", FxList, needsLock: false);
            CommandRegistry.RegisterPanelPrefixCommand(".fx list", FxListN, needsLock: false);
            CommandRegistry.RegisterPanelCommand(".fx del", FxDel);
            CommandRegistry.RegisterPanelCommand(".fx filter", FxFilter, needsLock: false);
            CommandRegistry.RegisterPanelCommand(".fx filter config", FxFilterConfig);
            CommandRegistry.RegisterPanelCommand(".fx filter plugin", FxFilterPlugin, needsLock: false);
            CommandRegistry.RegisterPanelCommand(".fx filter mod", FxFilterMod, needsLock: false);
            // 从 AI 记忆生成错误识别表达式: 会调用 AI(可能数十秒), 故不进全域命令锁, 由生成器内部串行化落盘
            CommandRegistry.RegisterPanelCommand(".fx genpattern", FxGenPattern, needsLock: false);
        }

        // ================= rt 系列 =================

        private static (bool Success, string Result, string? PanelText) RtVersion(string arg)
        {
            Output.Log($"RtCli版本：[cyan]{Markup.Escape(Program.RtCliVersion)}[/] {Program.RtCliInformation}", 1, "Backend");
            return (true, $"RtCli {Program.RtCliVersion}", null);
        }

        private static (bool Success, string Result, string? PanelText) RtHelp(string arg)
        {
            // 命令条目统一维护于 CommandHelp(与 CLI .help 同源), 面板用纯文本渲染
            var plainEntries = Function.CommandHelp.GetPlainTextEntries();

            var table = new Table().Border(TableBorder.Rounded).Title("[cyan]RtCli 命令列表[/]");
            table.AddColumn("命令").AddColumn("说明");
            foreach (var (helpCmd, helpDesc) in plainEntries)
                table.AddRow($"[grey]{helpCmd}[/]", helpDesc);
            AnsiConsole.Write(table);

            var sbHelp = new StringBuilder("=== RtCli 命令列表 ===");
            foreach (var (helpCmd, helpDesc) in plainEntries)
                sbHelp.Append('\n').Append(helpCmd.PadRight(17)).Append(" - ").Append(helpDesc);
            return (true, "命令列表已输出", sbHelp.ToString());
        }

        private static (bool Success, string Result, string? PanelText) RtStatus(string arg)
        {
            var table = new Table().Border(TableBorder.Rounded).Title("[cyan]RtCli 状态[/]");
            table.AddColumn("项目").AddColumn("状态");
            table.AddRow("管理端口", Connector.IsRunning ? "[green]运行中[/]" : "[red]未运行[/]");
            table.AddRow("已连接面板", Connector.ConnectedClientCount.ToString());
            table.AddRow("当前服务端", $"[cyan]{Markup.Escape(Config.App.CurrentServer)}[/] ({Markup.Escape(Config.CurrentServer.ServerName)})");
            table.AddRow("MC模式", $"[cyan]{Markup.Escape(Analyzer.CurrentMode)}[/]");
            if (Analyzer.NeedsRunServer)
                table.AddRow("MC服务端", Analyzer.IsRunModeActive ? "[green]运行中[/]" : "[red]未运行[/]");
            else
                table.AddRow("MC连接", Analyzer.IsAttached ? "[green]已连接[/]" : "[red]未连接[/]");
            table.AddRow("自动提示", Intelligence.IsTipsRunning ? "[green]运行中[/]" : "[grey]未运行[/]");
            AnsiConsole.Write(table);

            return (true, "状态已输出",
                $"管理端口: {(Connector.IsRunning ? "运行中" : "未运行")} | " +
                $"已连接面板: {Connector.ConnectedClientCount} | " +
                $"当前服务端: {Config.App.CurrentServer} ({Config.CurrentServer.ServerName}) | " +
                $"MC模式: {Analyzer.CurrentMode} | " +
                $"MC服务端: {(Analyzer.NeedsRunServer ? (Analyzer.IsRunModeActive ? "运行中" : "未运行") : (Analyzer.IsAttached ? "已连接" : "未连接"))} | " +
                $"自动提示: {(Intelligence.IsTipsRunning ? "运行中" : "未运行")}");
        }

        private static (bool Success, string Result, string? PanelText) RtClients(string arg)
        {
            if (Connector.ConnectedClientCount > 0)
            {
                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn("序号").AddColumn("ID").AddColumn("IP").AddColumn("连接时间");
                int idx = 1;
                foreach (var kvp in Connector.ConnectedClients)
                {
                    table.AddRow($"[cyan]{idx}[/]", Markup.Escape(kvp.Key), Markup.Escape(kvp.Value.IP), kvp.Value.ConnectTime.ToString("HH:mm:ss"));
                    idx++;
                }
                AnsiConsole.Write(table);

                var sb = new StringBuilder();
                sb.Append("已连接面板: ");
                idx = 1;
                foreach (var kvp in Connector.ConnectedClients)
                {
                    if (idx > 1) sb.Append(", ");
                    sb.Append($"{kvp.Key}({kvp.Value.IP})");
                    idx++;
                }
                return (true, "面板列表已输出", sb.ToString());
            }

            Output.Log("当前没有已连接的管理面板", 1, "Backend");
            return (true, "面板列表已输出", null);
        }

        private static (bool Success, string Result, string? PanelText) RtExtensions(string arg)
        {
            var json = RtExtensionManager.GetExtensionsJson();
            var extensions = Newtonsoft.Json.JsonConvert.DeserializeObject<List<ExtensionData>>(json);
            if (extensions != null && extensions.Count > 0)
            {
                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn("Key").AddColumn("名称").AddColumn("版本").AddColumn("描述").AddColumn("加载时间");
                foreach (var ext in extensions)
                {
                    table.AddRow(
                        Markup.Escape(ext.Key ?? ""),
                        Markup.Escape(ext.Name ?? ""),
                        Markup.Escape(ext.Version ?? ""),
                        Markup.Escape(ext.Description ?? ""),
                        Markup.Escape(ext.LoadTime ?? ""));
                }
                AnsiConsole.Write(table);

                var sb = new StringBuilder();
                sb.AppendLine("已加载扩展:");
                foreach (var ext in extensions)
                {
                    sb.AppendLine($"  {ext.Key} - {ext.Name} v{ext.Version}");
                }
                return (true, "扩展列表已输出", sb.ToString());
            }

            Output.Log("没有已加载的扩展", 1, "Backend");
            return (true, "扩展列表已输出", null);
        }

        private static (bool Success, string Result, string? PanelText) RtReload(string arg)
        {
            Output.Log("[yellow]重新加载中...[/]", 1, "Backend");
            Reload.Restart();
            return (true, "正在重新加载...", null);
        }

        // ================= .cfg 系列 =================

        private static (bool Success, string Result, string? PanelText) CfgReload(string arg)
        {
            Config.ReloadAll();
            return (true, "配置文件已热重载", null);
        }

        private static (bool Success, string Result, string? PanelText) CfgShow(string arg)
        {
            var content = GetConfigContent();
            if (content.Success)
            {
                // 配置文件内容可能包含 [ ] 等 YAML 语法，必须转义
                Output.Log(Markup.Escape(content.Content), 1, "Backend");
                return (true, "配置内容已输出到日志", null);
            }
            return (false, content.Message, null);
        }

        // ================= .server 系列 =================

        private static (bool Success, string Result, string? PanelText) ServerList(string arg)
        {
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("标识").AddColumn("名称").AddColumn("模式");
            foreach (var kvp in Config.App.ServerList)
            {
                var marker = kvp.Key == Config.App.CurrentServer ? "[green] *[/]" : "";
                table.AddRow(Markup.Escape(kvp.Key) + marker, Markup.Escape(kvp.Value.ServerName), Markup.Escape(kvp.Value.AnalyzerMode));
            }
            AnsiConsole.Write(table);

            var sb = new StringBuilder();
            foreach (var kvp in Config.App.ServerList)
            {
                var marker = kvp.Key == Config.App.CurrentServer ? " *" : "";
                sb.AppendLine($"  {kvp.Key}{marker} - {kvp.Value.ServerName} ({kvp.Value.AnalyzerMode})");
            }
            return (true, "服务端列表已输出", sb.ToString());
        }

        private static (bool Success, string Result, string? PanelText) ServerChange(string arg)
        {
            var identifier = arg.Trim();
            if (Config.SwitchServer(identifier))
            {
                Config.SaveCurrentConfig();
                // 切换后立即按新实例的分析模式刷新运行时(否则面板切换实例后命令通道仍是旧模式)
                Analyzer.Initialize(identifier);
                Output.Log($"已切换到服务端: [cyan]{Markup.Escape(identifier)}[/]", 1, "Backend");
                return (true, $"已切换到服务端: {identifier}", null);
            }
            return (false, $"服务端标识不存在: {identifier}", null);
        }

        private static (bool Success, string Result, string? PanelText) ServerStatus(string arg)
        {
            string id = InstanceContext.Current ?? "";
            if (Analyzer.IsRunning(id))
            {
                string mode = Analyzer.CurrentModeOf(id);
                Output.Log($"服务端[green]运行中[/]（模式 {mode}）。", 1, "Backend");
                return (true, $"服务端运行中（模式 {mode}）。", null);
            }
            Output.Log("服务端[red]未运行[/]或未连接。", 1, "Backend");
            return (true, "服务端未运行或未连接。", null);
        }

        private static (bool Success, string Result, string? PanelText) ServerStart(string arg)
        {
            string id = InstanceContext.Current ?? "";
            if (Analyzer.IsRunning(id))
                return (true, "服务端已在运行中, 跳过", null);

            // RUN/RR/RM 启动本地子进程; RCON 模式直接连接实例配置中的远程 RCON
            Analyzer.Initialize(id);
            Analyzer.StartServer(string.IsNullOrEmpty(id) ? null : id);
            return (true, "MC服务端启动命令已发送", null);
        }

        private static (bool Success, string Result, string? PanelText) ServerStop(string arg)
        {
            string id = InstanceContext.Current ?? "";
            if (!Analyzer.IsRunning(id))
                return (true, "服务端未运行, 跳过", null);

            Analyzer.StopServer(instanceId: string.IsNullOrEmpty(id) ? null : id);
            // RCON 模式只断开连接; 其余模式向服务端发送 stop
            return (true, Analyzer.CurrentModeOf(id) == "RCON" ? "已断开 RCON 连接" : "MC服务端停止命令已发送", null);
        }

        private static (bool Success, string Result, string? PanelText) ServerRestart(string arg)
        {
            string id = InstanceContext.Current ?? "";
            string? target = string.IsNullOrEmpty(id) ? null : id;
            if (!Analyzer.IsRunning(target))
                return (false, "服务端未运行, 无法重启", null);

            // 重启必须确保进程结束: 保留超时强杀兜底, 再按实例模式重新启动
            Analyzer.StopServer(killOnTimeout: true, instanceId: target);
            Analyzer.StartServer(target);
            return (true, "MC服务端重启命令已发送", null);
        }

        private static (bool Success, string Result, string? PanelText) ServerDetach(string arg)
        {
            string id = InstanceContext.Current ?? "";
            Analyzer.Detach(string.IsNullOrEmpty(id) ? null : id);
            return (true, "已断开与MC服务端的连接", null);
        }

        private static (bool Success, string Result, string? PanelText) ServerRconUsage(string arg)
        {
            const string usage = "用法: .server rcon connect <host> <port> [password] | disconnect | status";
            Output.Log(usage, 1, "Backend");
            return (false, usage, usage);
        }

        /// <summary>
        /// RCON 纯远程连接: 不依赖本机扫描序号/PID, 可直接连接任意远程服务端。
        /// 未提供密码时回退使用当前实例配置中的 rcon_password。
        /// </summary>
        private static (bool Success, string Result, string? PanelText) ServerRcon(string arg)
        {
            var parts = arg.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return ServerRconUsage(arg);

            string id = InstanceContext.Current ?? "";
            string? target = string.IsNullOrEmpty(id) ? null : id;

            switch (parts[0].ToLowerInvariant())
            {
                case "connect":
                    if (parts.Length < 3)
                        return ServerRconUsage(arg);
                    if (!int.TryParse(parts[2], out int port) || port <= 0 || port > 65535)
                        return (false, $"端口无效: {parts[2]}", null);

                    string password = parts.Length >= 4
                        ? parts[3]
                        : (Config.App.ServerList.TryGetValue(id, out var entry) ? entry.RconPassword ?? "" : "");

                    bool connected = Analyzer.ConnectRemoteRcon(parts[1], port, password, target);
                    return (connected,
                        connected ? $"已连接远程 RCON: {parts[1]}:{port}" : $"连接远程 RCON 失败: {parts[1]}:{port}",
                        null);

                case "disconnect":
                case "stop":
                    Analyzer.Detach(target);
                    return (true, "已断开 RCON 连接", null);

                case "status":
                    bool online = Analyzer.IsRunning(id) && Analyzer.CurrentModeOf(id) == "RCON";
                    return (true, online ? "RCON 已连接" : "RCON 未连接", null);

                default:
                    return ServerRconUsage(arg);
            }
        }

        // ================= .ai 系列 =================

        private static (bool Success, string Result, string? PanelText) AiRoot(string arg)
        {
            const string msg = ".ai 命令: start/stop/list/reload/run <任务名>/clear [任务名|all]";
            Output.Log(msg, 1, "Backend");
            return (true, msg, null);
        }

        private static (bool Success, string Result, string? PanelText) AiStart(string arg)
        {
            Intelligence.AiAutoRunner.Start();
            bool running = Intelligence.AiAutoRunner.IsRunning;
            return (running, running ? "AI自动化管理已启动" : "AI自动化管理启动失败，请查看日志", null);
        }

        private static (bool Success, string Result, string? PanelText) AiStop(string arg)
        {
            Intelligence.AiAutoRunner.Stop();
            return (true, "AI自动化管理已停止", null);
        }

        private static (bool Success, string Result, string? PanelText) AiList(string arg)
        {
            Intelligence.AiAutoRunner.ListTasks();
            var sb = new StringBuilder();
            sb.AppendLine($"AI自动化管理: {(Intelligence.AiAutoRunner.IsRunning ? "运行中" : "已停止")}");
            var aiConfig = ContentManager.Ai?.ServerAutoAi;
            if (aiConfig?.Tasks != null && aiConfig.Tasks.Count > 0)
            {
                foreach (var kv in aiConfig.Tasks)
                {
                    string st = kv.Value.Enabled ? "启用" : "禁用";
                    int cnt = Intelligence.AiAutoRunner.RunCount.TryGetValue(kv.Key, out var c) ? c : 0;
                    string last = Intelligence.AiAutoRunner.LastRunTime.TryGetValue(kv.Key, out var t) ? t.ToString("MM-dd HH:mm") : "-";
                    sb.AppendLine($"  {kv.Key} - {kv.Value.Name} [{st}] 触发:{kv.Value.Trigger} 间隔:{kv.Value.Interval} 已执行:{cnt} 最后:{last}");
                }
            }
            else
            {
                sb.AppendLine("未配置任何AI任务");
            }
            return (true, "AI任务列表已输出", sb.ToString());
        }

        private static (bool Success, string Result, string? PanelText) AiReload(string arg)
        {
            Intelligence.AiAutoRunner.Reload();
            return (true, "AI自动化管理已重载", null);
        }

        private static (bool Success, string Result, string? PanelText) AiRun(string arg)
        {
            var taskName = arg.Trim();
            if (string.IsNullOrWhiteSpace(taskName))
                return (false, "用法: .ai run <任务名>", null);
            bool ok = Intelligence.AiAutoRunner.TriggerTaskAsync(taskName).GetAwaiter().GetResult();
            return (ok, ok ? $"已手动触发任务: {taskName}" : $"触发任务失败: {taskName}（任务不存在或已禁用）", null);
        }

        private static (bool Success, string Result, string? PanelText) AiRunUsage(string arg)
            => (true, "用法: .ai run <任务名>", null);

        private static (bool Success, string Result, string? PanelText) AiClear(string arg)
        {
            var taskName = arg.Trim();
            if (string.IsNullOrWhiteSpace(taskName) || taskName.Equals("all", StringComparison.OrdinalIgnoreCase))
                Intelligence.AiAutoRunner.ClearAllContextCache();
            else
                Intelligence.AiAutoRunner.ClearContextCache(taskName);
            return (true, $"已清除任务上下文缓存: {taskName}", null);
        }

        private static (bool Success, string Result, string? PanelText) AiClearAll(string arg)
        {
            Intelligence.AiAutoRunner.ClearAllContextCache();
            return (true, "已清除所有AI任务上下文缓存", null);
        }

        // ================= .auto 系列 =================

        private static (bool Success, string Result, string? PanelText) AutoRoot(string arg)
        {
            const string msg = ".auto 命令: on/off <任务名>/list/start/stop";
            Output.Log(msg, 1, "Backend");
            return (true, msg, null);
        }

        private static (bool Success, string Result, string? PanelText) AutoStart(string arg)
        {
            Scheduler.Start();
            return (Scheduler.IsRunning, Scheduler.IsRunning ? "调度器已启动" : "调度器启动失败", null);
        }

        private static (bool Success, string Result, string? PanelText) AutoStop(string arg)
        {
            Scheduler.Stop();
            return (true, "调度器已停止", null);
        }

        private static (bool Success, string Result, string? PanelText) AutoList(string arg)
        {
            Scheduler.ListTasks();
            var sb = new StringBuilder();
            sb.AppendLine($"调度器: {(Scheduler.IsRunning ? "运行中" : "已停止")}");
            if (Scheduler.Settings.Tasks.Count > 0)
            {
                foreach (var kvp in Scheduler.Settings.Tasks)
                {
                    string status = kvp.Value.Enabled ? "启用" : "禁用";
                    if (kvp.Value.ServerKeys.Count > 0 && !kvp.Value.ServerKeys.Contains(Config.App.CurrentServer))
                        status = "不适用";
                    if (!string.IsNullOrEmpty(kvp.Value.Rules.ExpireAt) &&
                        DateTime.TryParse(kvp.Value.Rules.ExpireAt, out var exp) && DateTime.Now > exp)
                        status = "已过期";
                    string queue = kvp.Value.Rules.Queue ?? "Fast";
                    sb.AppendLine($"  {kvp.Key} [{status}] 触发:{kvp.Value.Trigger} 执行:{kvp.Value.Execute} 队列:{queue}");
                }
            }
            else
            {
                sb.AppendLine("未配置任何计划任务");
            }
            return (true, "计划任务列表已输出", sb.ToString());
        }

        private static (bool Success, string Result, string? PanelText) AutoOn(string arg)
        {
            var taskName = arg.Trim();
            Scheduler.SetTaskEnabled(taskName, true);
            return (true, $"已启用任务: {taskName}", null);
        }

        private static (bool Success, string Result, string? PanelText) AutoOnUsage(string arg)
            => (true, "用法: .auto on <任务名>", null);

        private static (bool Success, string Result, string? PanelText) AutoOff(string arg)
        {
            var taskName = arg.Trim();
            Scheduler.SetTaskEnabled(taskName, false);
            return (true, $"已禁用任务: {taskName}", null);
        }

        private static (bool Success, string Result, string? PanelText) AutoOffUsage(string arg)
            => (true, "用法: .auto off <任务名>", null);

        // ================= .fx 系列 =================

        private static (bool Success, string Result, string? PanelText) FxRoot(string arg)
        {
            const string msg = ".fx 命令: add/list/del/filter config/filter plugin/clientguide/base/ai/genpattern";
            Output.Log(msg, 1, "Backend");
            return (true, msg, null);
        }

        private static (bool Success, string Result, string? PanelText) FxAddUsage(string arg)
        {
            const string msg = "用法: .fx add <日志文件路径> (添加外部日志并自动识别错误)";
            Output.Log(msg, 1, "Backend");
            return (true, msg, null);
        }

        private static (bool Success, string Result, string? PanelText) FxAdd(string arg)
        {
            var path = arg.Trim().Trim('"');
            var (ok, message) = Analyzer.AddExternalLog(path);
            return (ok, message, null);
        }

        // 兼容旧命令: .fx get 已改为 .fx add(服务器日志自动识别,无需手动获取)
        private static (bool Success, string Result, string? PanelText) FxGet(string arg)
        {
            const string msg = ".fx get 已移除: 服务器日志已自动识别保存, 使用 .fx list 查看结果, 外部日志请用 .fx add <路径>";
            Output.Log(msg, 1, "Backend");
            return (true, msg, null);
        }

        private static (bool Success, string Result, string? PanelText) FxList(string arg)
        {
            Analyzer.ListErrors(null);
            var errors = ContentManager.LoadErrorLog();
            var sb = new StringBuilder();
            sb.AppendLine($"共 {errors.Count} 条错误分析结果");
            foreach (var kv in errors.OrderBy(kv => kv.Key))
            {
                string summary = (kv.Value.Content.Split('\n').FirstOrDefault() ?? "");
                if (summary.Length > 80) summary = summary.Substring(0, 77) + "...";
                sb.AppendLine($"  [{kv.Key}] ({kv.Value.SourceDisplay}) {summary}");
            }
            return (true, "错误分析结果列表已输出", sb.ToString());
        }

        private static (bool Success, string Result, string? PanelText) FxListN(string arg)
        {
            if (int.TryParse(arg, out int idx))
            {
                Analyzer.ListErrors(idx);
                return (true, $"已查看第 {idx} 条分析结果", null);
            }
            return (false, $"无效的参数: {arg}，用法: .fx list <n>", null);
        }

        private static (bool Success, string Result, string? PanelText) FxGenPattern(string arg)
        {
            // 结果通过命令返回值回给面板; 生成器同时会写控制台日志
            var (ok, message) = ErrorPatternGenerator.Generate();
            return (ok, message, null);
        }

        private static (bool Success, string Result, string? PanelText) FxDel(string arg)
        {
            Analyzer.DeleteErrors();
            return (true, "已删除所有错误分析结果", null);
        }

        private static (bool Success, string Result, string? PanelText) FxFilter(string arg)
        {
            Analyzer.FilterInfo();
            return (true, "过滤工具说明已输出", null);
        }

        private static (bool Success, string Result, string? PanelText) FxFilterConfig(string arg)
        {
            Analyzer.FilterConfig(null);
            return (true, "配置文件备份/对照已执行，详情见日志", null);
        }

        private static (bool Success, string Result, string? PanelText) FxFilterPlugin(string arg)
        {
            Analyzer.FilterPlugin(null);
            return (true, "插件列表已输出，详情见日志", null);
        }

        private static (bool Success, string Result, string? PanelText) FxFilterMod(string arg)
        {
            Analyzer.FilterMod(null);
            return (true, "模组列表已输出，详情见日志", null);
        }
    }
}
