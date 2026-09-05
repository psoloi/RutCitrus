using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using RtCli.Modules.Extension;
using RtCli.Modules.Function;
using Spectre.Console;

namespace RtCli.Modules.Unit
{
    /// <summary>
    /// 面板后端适配层：为 gRPC 面板提供命令分发、配置读写、日志广播与命令列表能力。
    /// </summary>
    public static class Backend
    {
        private static readonly object _cmdLock = new();
        private static bool _initialized = false;

        // 面板可用命令列表（用于前端自动补全）
        private static readonly (string Command, string Description)[] _knownCommands =
        {
            ("rt", "显示 RtCli 版本信息"),
            ("rt help", "查看命令列表"),
            ("rt status", "查看运行状态"),
            ("rt clients", "查看已连接面板"),
            ("rt extensions", "查看已加载扩展"),
            ("rt extensions load", "加载扩展"),
            ("rt extensions unload", "卸载扩展"),
            ("rt reload", "重新加载程序"),
            (".cfg reload", "热重载配置文件"),
            (".cfg show", "查看当前配置"),
            (".server list", "查看服务端列表"),
            (".server change", "切换当前服务端"),
            (".server add", "添加服务端"),
            (".server del", "删除服务端"),
            (".server status", "查看MC服务端状态"),
            (".group", "群组服务器管理"),
            (".group add", "创建群组"),
            (".group del", "删除群组"),
            (".group list", "列出所有群组信息"),
            (".group set", "设置服务器到群组"),
            (".group unset", "解除服务器群组设置"),
            (".group build", "自动构建群组向导"),
            (".server start", "启动MC服务端"),
            (".server stop", "停止MC服务端"),
            (".server detach", "断开MC服务端连接(Rcon)"),
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
            (".fx get", "获取并分析MC服务端错误日志"),
            (".fx list", "列出错误分析结果"),
            (".fx del", "删除所有错误分析结果"),
            (".fx filter config", "备份/对照/还原配置文件"),
            (".fx filter plugin", "列出/启用/禁用插件"),
            (".fx filter mod", "列出/启用/禁用模组"),
            (".help", "查看功能命令帮助"),
        };

        /// <summary>
        /// 初始化后端：挂载日志广播钩子，使 Output.Log 的输出同步推送到已连接的面板。
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            // 将日志广播挂载到 Output，面板通过 StreamLogs 即可实时收到日志
            Output.OnLogBroadcast = (timestamp, level, source, message) =>
            {
                try
                {
                    Connector.BroadcastLogAsync(timestamp, level, source, message).GetAwaiter().GetResult();
                }
                catch
                {
                    // 广播失败不影响主流程
                }
            };

            // 启动玩家事件记录器(订阅 EventBus, 持久化到 panel_data.json)
            PlayerEventRecorder.Start();

            Output.Log("面板后端适配层已初始化", 1, "Backend");
        }

        /// <summary>
        /// 分发并执行来自面板的命令，返回执行结果摘要。
        /// 输出策略：
        ///   - 简单文本命令 → Output.Log（Spectre标记语法，控制台彩色 + 面板纯文本）
        ///   - 表格命令 → AnsiConsole.Write(table) 控制台渲染 + BroadcastToPanel 纯文本广播
        ///   - 非标记的方括号一律用 [[ ]] 转义，用户内容用 Markup.Escape()
        /// </summary>
        public static (bool Success, string Result) DispatchCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return (false, "命令不能为空");

            lock (_cmdLock)
            {
                try
                {
                    // [[面板]] — Spectre 中 [[ ]] 表示字面量方括号
                    Output.Log($"[[面板]] 执行命令: {Markup.Escape(command)}", 1, "Backend");

                    var cmd = command.Trim();

                    // rt 系列命令
                    if (cmd == "rt")
                    {
                        Output.Log($"RtCli版本：[cyan]{Markup.Escape(Program.RtCliVersion)}[/] {Program.RtCliInformation}", 1, "Backend");
                        return (true, $"RtCli {Program.RtCliVersion}");
                    }

                    if (cmd == "rt help" || cmd == ".help")
                    {
                        var table = new Table().Border(TableBorder.Rounded).Title("[cyan]RtCli 命令列表[/]");
                        table.AddColumn("命令").AddColumn("说明");
                        table.AddRow("[grey]rt status[/]", "查看运行状态");
                        table.AddRow("[grey]rt clients[/]", "查看已连接面板");
                        table.AddRow("[grey]rt extensions[/]", "查看已加载扩展");
                        table.AddRow("[grey]rt reload[/]", "重新加载程序");
                        table.AddRow("[grey].cfg reload[/]", "热重载配置文件");
                        table.AddRow("[grey].cfg show[/]", "查看当前配置");
                        table.AddRow("[grey].server list[/]", "查看服务端列表");
                        table.AddRow("[grey].server change X[/]", "切换当前服务端");
                        table.AddRow("[grey].server status[/]", "查看MC服务端状态");
                        table.AddRow("[grey].group[/]", "群组服务器管理");
                        table.AddRow("[grey].auto[/]", "查看计划任务");
                        table.AddRow("[grey]/<命令>[/]", "发送命令到MC服务端");
                        AnsiConsole.Write(table);

                        BroadcastToPanel(
                            "=== RtCli 命令列表 ===\n" +
                            "rt status        - 查看运行状态\n" +
                            "rt clients       - 查看已连接面板\n" +
                            "rt extensions    - 查看已加载扩展\n" +
                            "rt reload        - 重新加载程序\n" +
                            ".cfg reload      - 热重载配置文件\n" +
                            ".cfg show        - 查看当前配置\n" +
                            ".server list     - 查看服务端列表\n" +
                            ".server change X - 切换当前服务端\n" +
                            ".server status   - 查看MC服务端状态\n" +
                            ".group            - 群组服务器管理(add/del/list/set/unset/build)\n" +
                            ".auto            - 查看计划任务\n" +
                            "/<命令>          - 发送命令到MC服务端");
                        return (true, "命令列表已输出");
                    }

                    if (cmd == "rt status")
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

                        BroadcastToPanel(
                            $"管理端口: {(Connector.IsRunning ? "运行中" : "未运行")} | " +
                            $"已连接面板: {Connector.ConnectedClientCount} | " +
                            $"当前服务端: {Config.App.CurrentServer} ({Config.CurrentServer.ServerName}) | " +
                            $"MC模式: {Analyzer.CurrentMode} | " +
                            $"MC服务端: {(Analyzer.NeedsRunServer ? (Analyzer.IsRunModeActive ? "运行中" : "未运行") : (Analyzer.IsAttached ? "已连接" : "未连接"))} | " +
                            $"自动提示: {(Intelligence.IsTipsRunning ? "运行中" : "未运行")}");
                        return (true, "状态已输出");
                    }

                    if (cmd == "rt clients")
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
                            BroadcastToPanel(sb.ToString());
                        }
                        else
                        {
                            Output.Log("当前没有已连接的管理面板", 1, "Backend");
                        }
                        return (true, "面板列表已输出");
                    }

                    if (cmd == "rt extensions")
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
                            BroadcastToPanel(sb.ToString());
                        }
                        else
                        {
                            Output.Log("没有已加载的扩展", 1, "Backend");
                        }
                        return (true, "扩展列表已输出");
                    }

                    if (cmd == "rt reload")
                    {
                        Output.Log("[yellow]重新加载中...[/]", 1, "Backend");
                        Reload.Restart();
                        return (true, "正在重新加载...");
                    }

                    // .cfg 系列命令
                    if (cmd == ".cfg reload")
                    {
                        Config.ReloadAll();
                        return (true, "配置文件已热重载");
                    }

                    if (cmd == ".cfg show")
                    {
                        var content = GetConfigContent();
                        if (content.Success)
                        {
                            // 配置文件内容可能包含 [ ] 等 YAML 语法，必须转义
                            Output.Log(Markup.Escape(content.Content), 1, "Backend");
                            return (true, "配置内容已输出到日志");
                        }
                        return (false, content.Message);
                    }

                    // .server 系列命令
                    if (cmd == ".server list")
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
                        BroadcastToPanel(sb.ToString());
                        return (true, "服务端列表已输出");
                    }

                    if (cmd.StartsWith(".server change "))
                    {
                        var identifier = cmd[".server change ".Length..].Trim();
                        if (Config.SwitchServer(identifier))
                        {
                            Config.SaveCurrentConfig();
                            Output.Log($"已切换到服务端: [cyan]{Markup.Escape(identifier)}[/]", 1, "Backend");
                            return (true, $"已切换到服务端: {identifier}");
                        }
                        return (false, $"服务端标识不存在: {identifier}");
                    }

                    if (cmd == ".server status")
                    {
                        if (Analyzer.NeedsRunServer)
                        {
                            var running = Analyzer.IsRunModeActive;
                            Output.Log(running ? "服务端[green]运行中[/]。" : "服务端[red]未运行[/]。", 1, "Backend");
                            return (true, running ? "服务端运行中。" : "服务端未运行。");
                        }
                        else
                        {
                            var attached = Analyzer.IsAttached;
                            Output.Log(attached ? "[green]已连接[/]到 Minecraft 服务端。" : "[red]未连接[/]到 Minecraft 服务端。", 1, "Backend");
                            return (true, attached ? "已连接到 Minecraft 服务端。" : "未连接到 Minecraft 服务端。");
                        }
                    }

                    // .server start/stop/detach - MC服务端生命周期
                    if (cmd == ".server start")
                    {
                        if (Analyzer.NeedsRunServer)
                        {
                            Analyzer.StartServer();
                            return (true, "MC服务端启动命令已发送");
                        }
                        return (false, "Rcon模式下不支持 .server start，请使用 .server get + .server connect 连接");
                    }

                    if (cmd == ".server stop")
                    {
                        if (Analyzer.NeedsRunServer)
                        {
                            Analyzer.StopServer();
                            return (true, "MC服务端停止命令已发送");
                        }
                        return (false, "Rcon模式下不支持 .server stop");
                    }

                    if (cmd == ".server detach")
                    {
                        if (!Analyzer.NeedsRunServer)
                        {
                            Analyzer.Detach();
                            return (true, "已断开与MC服务端的连接");
                        }
                        return (false, "当前模式下不支持 .server detach，请使用 .server stop 停止服务端");
                    }

                    // .ai 系列命令 - AI自动化管理
                    if (cmd == ".ai")
                    {
                        const string msg = ".ai 命令: start/stop/list/reload/run <任务名>/clear [任务名|all]";
                        Output.Log(msg, 1, "Backend");
                        return (true, msg);
                    }

                    if (cmd == ".ai start")
                    {
                        Intelligence.AiAutoRunner.Start();
                        bool running = Intelligence.AiAutoRunner.IsRunning;
                        return (running, running ? "AI自动化管理已启动" : "AI自动化管理启动失败，请查看日志");
                    }

                    if (cmd == ".ai stop")
                    {
                        Intelligence.AiAutoRunner.Stop();
                        return (true, "AI自动化管理已停止");
                    }

                    if (cmd == ".ai list")
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
                        BroadcastToPanel(sb.ToString());
                        return (true, "AI任务列表已输出");
                    }

                    if (cmd == ".ai reload")
                    {
                        Intelligence.AiAutoRunner.Reload();
                        return (true, "AI自动化管理已重载");
                    }

                    if (cmd.StartsWith(".ai run "))
                    {
                        var taskName = cmd[".ai run ".Length..].Trim();
                        if (string.IsNullOrWhiteSpace(taskName))
                            return (false, "用法: .ai run <任务名>");
                        bool ok = Intelligence.AiAutoRunner.TriggerTaskAsync(taskName).GetAwaiter().GetResult();
                        return (ok, ok ? $"已手动触发任务: {taskName}" : $"触发任务失败: {taskName}（任务不存在或已禁用）");
                    }

                    if (cmd == ".ai run")
                    {
                        return (true, "用法: .ai run <任务名>");
                    }

                    if (cmd.StartsWith(".ai clear "))
                    {
                        var taskName = cmd[".ai clear ".Length..].Trim();
                        if (string.IsNullOrWhiteSpace(taskName) || taskName.Equals("all", StringComparison.OrdinalIgnoreCase))
                            Intelligence.AiAutoRunner.ClearAllContextCache();
                        else
                            Intelligence.AiAutoRunner.ClearContextCache(taskName);
                        return (true, $"已清除任务上下文缓存: {taskName}");
                    }

                    if (cmd == ".ai clear")
                    {
                        Intelligence.AiAutoRunner.ClearAllContextCache();
                        return (true, "已清除所有AI任务上下文缓存");
                    }

                    // .auto 系列命令 - 计划任务调度器
                    if (cmd == ".auto")
                    {
                        const string msg = ".auto 命令: on/off <任务名>/list/start/stop";
                        Output.Log(msg, 1, "Backend");
                        return (true, msg);
                    }

                    if (cmd == ".auto start")
                    {
                        Scheduler.Start();
                        return (Scheduler.IsRunning, Scheduler.IsRunning ? "调度器已启动" : "调度器启动失败");
                    }

                    if (cmd == ".auto stop")
                    {
                        Scheduler.Stop();
                        return (true, "调度器已停止");
                    }

                    if (cmd == ".auto list")
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
                        BroadcastToPanel(sb.ToString());
                        return (true, "计划任务列表已输出");
                    }

                    if (cmd.StartsWith(".auto on "))
                    {
                        var taskName = cmd[".auto on ".Length..].Trim();
                        Scheduler.SetTaskEnabled(taskName, true);
                        return (true, $"已启用任务: {taskName}");
                    }

                    if (cmd == ".auto on")
                    {
                        return (true, "用法: .auto on <任务名>");
                    }

                    if (cmd.StartsWith(".auto off "))
                    {
                        var taskName = cmd[".auto off ".Length..].Trim();
                        Scheduler.SetTaskEnabled(taskName, false);
                        return (true, $"已禁用任务: {taskName}");
                    }

                    if (cmd == ".auto off")
                    {
                        return (true, "用法: .auto off <任务名>");
                    }

                    // .fx 系列命令 - 错误分析/客户端诊断/过滤工具
                    if (cmd == ".fx")
                    {
                        const string msg = ".fx 命令: add/list/del/filter config/filter plugin/clientguide/base/ai";
                        Output.Log(msg, 1, "Backend");
                        return (true, msg);
                    }

                    if (cmd == ".fx add")
                    {
                        const string msg = "用法: .fx add <日志文件路径> (添加外部日志并自动识别错误)";
                        Output.Log(msg, 1, "Backend");
                        return (true, msg);
                    }

                    if (cmd.StartsWith(".fx add "))
                    {
                        var path = cmd[".fx add ".Length..].Trim().Trim('"');
                        var (ok, message) = Analyzer.AddExternalLog(path);
                        return (ok, message);
                    }

                    // 兼容旧命令: .fx get 已改为 .fx add(服务器日志自动识别,无需手动获取)
                    if (cmd == ".fx get" || cmd.StartsWith(".fx get "))
                    {
                        const string msg = ".fx get 已移除: 服务器日志已自动识别保存, 使用 .fx list 查看结果, 外部日志请用 .fx add <路径>";
                        Output.Log(msg, 1, "Backend");
                        return (true, msg);
                    }

                    if (cmd == ".fx list")
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
                        BroadcastToPanel(sb.ToString());
                        return (true, "错误分析结果列表已输出");
                    }

                    if (cmd.StartsWith(".fx list "))
                    {
                        var arg = cmd[".fx list ".Length..].Trim();
                        if (int.TryParse(arg, out int idx))
                        {
                            Analyzer.ListErrors(idx);
                            return (true, $"已查看第 {idx} 条分析结果");
                        }
                        return (false, $"无效的参数: {arg}，用法: .fx list <n>");
                    }

                    if (cmd == ".fx del")
                    {
                        Analyzer.DeleteErrors();
                        return (true, "已删除所有错误分析结果");
                    }

                    if (cmd == ".fx filter")
                    {
                        Analyzer.FilterInfo();
                        return (true, "过滤工具说明已输出");
                    }

                    if (cmd == ".fx filter config")
                    {
                        Analyzer.FilterConfig(null);
                        return (true, "配置文件备份/对照已执行，详情见日志");
                    }

                    if (cmd == ".fx filter plugin")
                    {
                        Analyzer.FilterPlugin(null);
                        return (true, "插件列表已输出，详情见日志");
                    }

                    if (cmd == ".fx filter mod")
                    {
                        Analyzer.FilterMod(null);
                        return (true, "模组列表已输出，详情见日志");
                    }

                    // MC 命令转发
                    if (cmd.StartsWith("/"))
                    {
                        var mcCommand = cmd[1..];
                        if (!string.IsNullOrWhiteSpace(mcCommand))
                        {
                            Analyzer.SendCommand(mcCommand);
                            return (true, $"已发送到MC服务端: /{mcCommand}");
                        }
                        return (false, "MC命令不能为空");
                    }

                    // 扩展注册的命令(支持参数前缀匹配)
                    if (CommandRegistry.TryExecuteWithArgs(cmd))
                    {
                        return (true, $"扩展命令已执行: {cmd}");
                    }

                    Output.Log($"未知命令: [yellow]{Markup.Escape(cmd)}[/]，输入 rt help 查看命令列表", 2, "Backend");
                    return (false, $"未知命令: {cmd}");
                }
                catch (Exception ex)
                {
                    Output.Log($"命令执行异常: [red]{Markup.Escape(ex.Message)}[/]", 3, "Backend");
                    return (false, $"命令执行异常: {ex.Message}");
                }
            }
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
            // 当前服务端：使用已有的Analyzer连接(stdin / RCON / Management)
            if (string.IsNullOrEmpty(serverKey) || serverKey == Config.App.CurrentServer)
            {
                if (Analyzer.IsAttached || Analyzer.IsRunModeActive)
                {
                    Analyzer.SendCommand(mcCommand);
                    return (true, $"已通过当前连接发送: /{mcCommand}");
                }
                return (false, "当前服务端未连接，请先在RtCli中连接服务端");
            }

            // 其他服务端：通过临时RCON连接发送
            if (!Config.App.ServerList.TryGetValue(serverKey, out var server))
                return (false, $"未找到服务端标识: {serverKey}");

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
                Connector.BroadcastLogAsync(timestamp, level, "Backend", message).GetAwaiter().GetResult();
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

        /// <summary>
        /// 采集系统状态：主机 CPU/内存 + 各 MC 服务端 TPS、玩家数、进程 CPU/内存。
        /// TPS 与玩家数通过向 MC 服务端发送 tps/list 命令获取(带 10 秒缓存节流)。
        /// </summary>
        public static (bool Success, string Message, double CpuUsage, double MemUsedMb, double MemTotalMb,
                       List<(string Key, string Name, bool Running, double Tps, string TpsStatus, int Pid, double McMemMb,
                             double Tps1m, double Tps5m, double Tps15m, double McCpuUsage, int PlayerCount, int PlayerMax)> Servers)
            GetSystemStats()
        {
            try
            {
                double cpu = Intelligence.AiTools.GetCpuUsage();
                var (usedMb, totalMb) = Intelligence.AiTools.GetMemoryUsage();

                var servers = new List<(string, string, bool, double, string, int, double, double, double, double, double, int, int)>();
                var currentKey = Config.App.CurrentServer;

                foreach (var kvp in Config.App.ServerList)
                {
                    var key = kvp.Key;
                    var entry = kvp.Value;
                    var name = string.IsNullOrEmpty(entry.ServerName) ? key : entry.ServerName;
                    bool isCurrent = key == currentKey;
                    bool running = isCurrent && (Analyzer.IsRunModeActive || Analyzer.IsAttached);
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

                        // TPS：发送 tps 命令解析(带缓存节流)
                        try
                        {
                            var (t1, t5, t15) = Analyzer.QueryTps(3000);
                            tps1m = t1; tps5m = t5; tps15m = t15;
                            tps = t1; // 兼容字段
                            if (t1 >= 0)
                                tpsStatus = $"{t1:0.0}" + (t5 >= 0 ? $" / {t5:0.0} / {t15:0.0}" : "");
                            else
                                tpsStatus = "运行中(TPS未获取,需Spark等插件)";
                        }
                        catch { }

                        // 玩家数：发送 list 命令解析(带缓存节流)
                        try
                        {
                            var (count, max) = Analyzer.QueryPlayerCount(3000);
                            playerCount = count;
                            playerMax = max;
                        }
                        catch { }

                        // MC 进程 CPU 与内存
                        try
                        {
                            var proc = Analyzer.GetServerProcess();
                            if (proc != null && !proc.HasExited)
                            {
                                pid = proc.Id;
                                mcMemMb = proc.WorkingSet64 / (1024.0 * 1024.0);
                            }
                            mcCpu = Analyzer.GetMcCpuUsage();
                        }
                        catch { }
                    }

                    servers.Add((key, name, running, tps, tpsStatus, pid, mcMemMb,
                                 tps1m, tps5m, tps15m, mcCpu, playerCount, playerMax));
                }

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
                var content = tailLines > 0
                    ? ReadTailLines(fullPath, tailLines)
                    : File.ReadAllText(fullPath);
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

                // 备份
                if (File.Exists(fullPath))
                {
                    var backupPath = fullPath + ".bak";
                    File.Copy(fullPath, backupPath, true);
                }

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

        /// <summary>
        /// 获取面板可用命令列表（含描述），用于前端自动补全。
        /// </summary>
        public static (string Command, string Description)[] GetAvailableCommands()
        {
            var list = new List<(string, string)>(_knownCommands);

            // 合并扩展注册的命令
            foreach (var cmd in CommandRegistry.GetAutoCompleteCommands())
            {
                list.Add((cmd, "扩展命令"));
            }

            return list.ToArray();
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
                        IsRunning = inst.Id == Config.App.CurrentServer && (Function.Analyzer.IsRunModeActive || Function.Analyzer.IsAttached),
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
            var originalServer = Config.App.CurrentServer;

            foreach (var id in ids)
            {
                if (!Config.App.ServerList.ContainsKey(id))
                {
                    results.Add($"[{id}] 不存在");
                    continue;
                }

                try
                {
                    Config.SwitchServer(id);

                    switch (action?.ToLowerInvariant())
                    {
                        case "start":
                            if (Function.Analyzer.IsRunModeActive || Function.Analyzer.IsAttached)
                            {
                                results.Add($"[{id}] 已在运行中, 跳过");
                            }
                            else
                            {
                                Function.Analyzer.StartServer();
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
                            if (Function.Analyzer.IsRunModeActive || Function.Analyzer.IsAttached)
                            {
                                Function.Analyzer.StopServer();
                                results.Add($"[{id}] 已停止");
                            }
                            else
                            {
                                results.Add($"[{id}] 未运行, 跳过");
                            }
                            break;

                        case "restart":
                            if (Function.Analyzer.IsRunModeActive || Function.Analyzer.IsAttached)
                            {
                                Function.Analyzer.StopServer();
                                System.Threading.Thread.Sleep(2000);
                            }
                            Function.Analyzer.StartServer();
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

            // 恢复原 current_server 选择
            Config.SwitchServer(originalServer);

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
                    IsRunning = id == Config.App.CurrentServer && (Function.Analyzer.IsRunModeActive || Function.Analyzer.IsAttached),
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
        }

        private static List<JarFileItem> ListJarFiles(string serverKey, string subDir, string disabledExt)
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
                result.Add(new JarFileItem
                {
                    FileName = f.Name,
                    SizeBytes = f.Length,
                    IsDisabled = f.Extension.Equals(disabledExt, StringComparison.OrdinalIgnoreCase)
                });
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
                return (true, "OK", ListJarFiles(serverKey, "plugins", ".disjar"));
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
                return (true, "OK", ListJarFiles(serverKey, "mods", ".disabled"));
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
                if (fileName.EndsWith(disabledExt, StringComparison.OrdinalIgnoreCase))
                {
                    // 启用: 去掉禁用后缀
                    newPath = resolvedPath.Substring(0, resolvedPath.Length - disabledExt.Length);
                    File.Move(resolvedPath, newPath);
                    return (true, $"已启用: {Path.GetFileName(newPath)}");
                }
                else if (fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                {
                    // 禁用: 添加禁用后缀
                    newPath = resolvedPath + disabledExt;
                    File.Move(resolvedPath, newPath);
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

        // ===== 玩家管理 API =====

        /// <summary>封禁玩家</summary>
        public static (bool Success, string Message) BanPlayer(string serverKey, string playerName, string reason)
        {
            var cmd = string.IsNullOrWhiteSpace(reason)
                ? $"ban {playerName}"
                : $"ban {playerName} {reason}";
            var (ok, msg) = SendMcCommand(serverKey, cmd);
            return (ok, msg);
        }

        /// <summary>解封玩家</summary>
        public static (bool Success, string Message) PardonPlayer(string serverKey, string playerName)
        {
            var (ok, msg) = SendMcCommand(serverKey, $"pardon {playerName}");
            return (ok, msg);
        }

        /// <summary>封禁IP</summary>
        public static (bool Success, string Message) BanIp(string serverKey, string ipOrPlayer, string reason)
        {
            var cmd = string.IsNullOrWhiteSpace(reason)
                ? $"ban-ip {ipOrPlayer}"
                : $"ban-ip {ipOrPlayer} {reason}";
            var (ok, msg) = SendMcCommand(serverKey, cmd);
            return (ok, msg);
        }

        /// <summary>解封IP</summary>
        public static (bool Success, string Message) PardonIp(string serverKey, string ip)
        {
            var (ok, msg) = SendMcCommand(serverKey, $"pardon-ip {ip}");
            return (ok, msg);
        }

        /// <summary>踢出玩家</summary>
        public static (bool Success, string Message) KickPlayer(string serverKey, string playerName, string reason)
        {
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

                var allLines = File.ReadAllLines(logFile, Encoding.GetEncoding(0));
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

                var handlerPattern = ContentManager.Regex.Console_Error.Handler;
                var limit = ContentManager.Regex.Console_Error.Limit;

                if (string.IsNullOrWhiteSpace(handlerPattern))
                    return (false, "正则表达式配置为空", new Dictionary<int, string>());

                Regex handlerRegex;
                try
                {
                    handlerRegex = new Regex(handlerPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                }
                catch (Exception ex)
                {
                    return (false, $"正则表达式无效: {ex.Message}", new Dictionary<int, string>());
                }

                var logLines = File.ReadAllLines(logFile, Encoding.GetEncoding(0));
                var errors = new Dictionary<int, string>();
                int errorIdx = 0;
                int matchCount = 0;
                int startIdx = -1;

                for (int i = 0; i < logLines.Length; i++)
                {
                    if (handlerRegex.IsMatch(logLines[i]))
                    {
                        if (startIdx < 0)
                            startIdx = i;
                        matchCount++;

                        // 每个错误块: 从匹配行开始，直到下一个匹配行或非连续行
                        var sb = new StringBuilder();
                        sb.AppendLine(logLines[i]);

                        // 向后收集堆栈跟踪(以空白、at、Caused by 等开头的行)
                        for (int j = i + 1; j < logLines.Length && j < i + 50; j++)
                        {
                            var line = logLines[j];
                            if (handlerRegex.IsMatch(line))
                                break;
                            if (string.IsNullOrWhiteSpace(line) && sb.Length > 0)
                            {
                                // 空行可能表示错误块结束，但也可能是格式间隔
                                // 检查下一行是否也是非匹配行
                                if (j + 1 < logLines.Length && !handlerRegex.IsMatch(logLines[j + 1]) &&
                                    !IsStackTraceLine(logLines[j + 1]))
                                    break;
                                continue;
                            }
                            if (IsStackTraceLine(line) || line.TrimStart().StartsWith("at "))
                                sb.AppendLine(line);
                            else
                                break;
                        }

                        errorIdx++;
                        errors[errorIdx] = sb.ToString().TrimEnd();
                        startIdx = i;

                        if (errors.Count >= limit)
                            break;
                    }
                }

                Output.Log($"实例 {serverKey} 日志分析完成: 共 {errors.Count} 条错误", 1, "Backend");
                return (true, $"分析完成，共 {errors.Count} 条错误", errors);
            }
            catch (Exception ex)
            {
                return (false, $"分析失败: {ex.Message}", new Dictionary<int, string>());
            }
        }

        private static bool IsStackTraceLine(string line)
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("at ") ||
                   trimmed.StartsWith("Caused by:") ||
                   trimmed.StartsWith("... ") ||
                   trimmed.StartsWith("java.") ||
                   trimmed.StartsWith("org.") ||
                   trimmed.StartsWith("com.") ||
                   trimmed.StartsWith("net.") ||
                   trimmed.StartsWith("Suppressed:");
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

                return (true, "AI分析完成", aiResponse);
            }
            catch (Exception ex)
            {
                return (false, $"AI分析失败: {ex.Message}", "");
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

                _flushTimer = new Timer(_ => FlushAll(), null, FlushIntervalMs, FlushIntervalMs);
                Output.Log("玩家事件记录器已启动", 1, "PlayerEventRecorder");
            }
        }

        private static string CurrentServer => Config.App.CurrentServer ?? "";

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
}
