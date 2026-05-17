using System.Collections.Concurrent;
using System.IO;
using System.Text;
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
            (".auto", "查看计划任务"),
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
            try
            {
                var configPath = Path.Combine(Config.DataPath, "config.yml");
                if (!File.Exists(configPath))
                    return (false, "", "配置文件不存在");
                var content = File.ReadAllText(configPath);
                return (true, content, "读取成功");
            }
            catch (Exception ex)
            {
                return (false, "", $"读取失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 保存 config.yml 文件内容并热重载。
        /// </summary>
        public static (bool Success, string Message) SaveConfigContent(string content)
        {
            try
            {
                if (string.IsNullOrEmpty(content))
                    return (false, "配置内容不能为空");

                var configPath = Path.Combine(Config.DataPath, "config.yml");

                // 备份当前配置
                if (File.Exists(configPath))
                {
                    var backupPath = configPath + ".bak";
                    File.Copy(configPath, backupPath, true);
                }

                File.WriteAllText(configPath, content);
                Output.Log("配置文件已由面板保存，正在热重载...", 1, "Backend");

                Config.ReloadAll();
                return (true, "配置文件已保存并热重载");
            }
            catch (Exception ex)
            {
                Output.Log($"保存配置失败: [red]{Markup.Escape(ex.Message)}[/]", 3, "Backend");
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
    }
}
