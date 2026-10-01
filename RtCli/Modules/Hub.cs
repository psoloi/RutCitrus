using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;

namespace RtCli.Modules
{
    internal class Hub
    {
        /// <summary>
        /// 群组服务器自动构建向导
        /// </summary>
        public static async Task GroupBuildWizard()
        {
            string ThisName = "GroupBuild";

            AnsiConsole.Write(new Rule(Unit.I18n.Get("hub_wizard_title")).RuleStyle("grey").Centered());

            // ===== 步骤1: 创建群组 =====
            Output.Log(Unit.I18n.Get("hub_step1_create_group"), 1, ThisName);
            string groupName = AnsiConsole.Ask<string>(Unit.I18n.Get("hub_ask_group_name"));
            if (string.IsNullOrWhiteSpace(groupName))
            {
                Output.Log(Unit.I18n.Get("hub_group_name_empty"), 2, ThisName);
                return;
            }

            string groupId;
            var (ok, msg, gid) = ServerDataManager.CreateGroup(groupName, new List<string>());
            if (!ok)
            {
                Output.Log(msg, 2, ThisName);
                return;
            }
            groupId = gid;
            Output.Log(Unit.I18n.Get("hub_group_created", groupName, groupId), 1, ThisName);

            // ===== 步骤2: 选择服务器加入群组 =====
            Output.Log(Unit.I18n.Get("hub_step2_select_servers"), 1, ThisName);
            var selectedMembers = new List<string>();
            var availableServers = Unit.Config.App.ServerList.Keys
                .Where(k => ServerDataManager.Data.Instances.FirstOrDefault(i => i.Id == k)?.GroupId != groupId)
                .ToList();

            while (availableServers.Count > 0)
            {
                var choices = new List<string>(availableServers);
                choices.Add(Unit.I18n.Get("hub_choice_done"));
                choices.Add(Unit.I18n.Get("hub_choice_undo"));

                var selected = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title(Unit.I18n.Get("hub_select_members_title", selectedMembers.Count))
                        .AddChoices(choices));

                if (selected == Unit.I18n.Get("hub_choice_done"))
                    break;

                if (selected == Unit.I18n.Get("hub_choice_undo"))
                {
                    if (selectedMembers.Count > 0)
                    {
                        string last = selectedMembers[^1];
                        selectedMembers.RemoveAt(selectedMembers.Count - 1);
                        availableServers.Add(last);
                        Output.Log(Unit.I18n.Get("hub_member_undone", last), 1, ThisName);
                    }
                    continue;
                }

                selectedMembers.Add(selected);
                availableServers.Remove(selected);
                Output.Log(Unit.I18n.Get("hub_member_added", selected), 1, ThisName);
            }

            // 添加选中的服务器到群组
            if (selectedMembers.Count > 0)
            {
                ServerDataManager.UpdateGroup("add", groupId, null, selectedMembers);
                Output.Log(Unit.I18n.Get("hub_members_added_to_group", selectedMembers.Count), 1, ThisName);
            }
            else
            {
                Output.Log(Unit.I18n.Get("hub_no_members_selected"), 1, ThisName);
            }

            // ===== 步骤3: 创建代理服务端实例 =====
            Output.Log(Unit.I18n.Get("hub_step3_create_proxy"), 1, ThisName);

            var proxyChoices = new List<string> { Unit.I18n.Get("hub_proxy_velocity"), "BungeeCord", Unit.I18n.Get("hub_proxy_manual") };
            var proxyChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title(Unit.I18n.Get("hub_select_proxy_type"))
                    .AddChoices(proxyChoices));

            bool isVelocity = proxyChoice.StartsWith("Velocity");
            bool isBungeeCord = proxyChoice.StartsWith("BungeeCord");
            bool manualProxy = proxyChoice.StartsWith("手动");

            // 输入代理服务端标识
            string proxyId = AnsiConsole.Ask<string>(Unit.I18n.Get("hub_ask_proxy_id"));
            if (string.IsNullOrWhiteSpace(proxyId))
            {
                Output.Log(Unit.I18n.Get("hub_proxy_id_empty"), 2, ThisName);
                return;
            }
            if (Unit.Config.App.ServerList.ContainsKey(proxyId))
            {
                Output.Log(Unit.I18n.Get("hub_proxy_id_exists", proxyId), 2, ThisName);
                return;
            }

            // 输入工作目录
            string proxyWorkPath = AnsiConsole.Ask<string>(Unit.I18n.Get("hub_ask_proxy_workpath"), "");
            if (string.IsNullOrWhiteSpace(proxyWorkPath))
            {
                proxyWorkPath = Path.Combine(Environment.CurrentDirectory, "servers", proxyId);
                Output.Log(Unit.I18n.Get("hub_autocreate_workdir", proxyWorkPath), 1, ThisName);
            }
            if (!Directory.Exists(proxyWorkPath))
                Directory.CreateDirectory(proxyWorkPath);

            // 下载代理端
            string? jarName = "";
            if (isVelocity)
            {
                Output.Log(Unit.I18n.Get("hub_downloading_velocity"), 1, ThisName);
                jarName = await Function.Intelligence.DownloadPaperMC(proxyWorkPath, "velocity", "velocity.jar");
                if (string.IsNullOrEmpty(jarName))
                {
                    Output.Log(Unit.I18n.Get("hub_velocity_download_failed"), 2, ThisName);
                    jarName = "velocity.jar";
                }
            }
            else if (isBungeeCord)
            {
                Output.Log(Unit.I18n.Get("hub_bungeecord_manual"), 1, ThisName);
                jarName = "BungeeCord.jar";
            }
            else
            {
                jarName = AnsiConsole.Ask<string>(Unit.I18n.Get("hub_ask_proxy_jar"), "proxy.jar");
            }

            // 创建代理服务端实例
            string proxyRunFlags = $"-Xms256M -Xmx512M -jar {jarName} --nogui";
            var (pOk, pMsg) = ServerDataManager.CreateInstance(
                proxyId, proxyId + " (代理)", proxyWorkPath, "", proxyRunFlags, "RUN", groupId);
            if (!pOk)
            {
                Output.Log(pMsg, 2, ThisName);
                return;
            }
            Output.Log(Unit.I18n.Get("hub_proxy_created", proxyId), 1, ThisName);

            // ===== 步骤4: 端口配置 =====
            Output.Log(Unit.I18n.Get("hub_step4_ports"), 1, ThisName);

            // 询问代理端口(玩家连接的端口)
            int proxyPort = AnsiConsole.Ask<int>(Unit.I18n.Get("hub_ask_proxy_port"), 25565);

            // 询问是否自动配置子服务器端口
            bool autoPorts = AnsiConsole.Confirm(Unit.I18n.Get("hub_confirm_auto_ports"), true);

            var serverPorts = new Dictionary<string, int>(); // 服务器标识 -> 端口

            if (autoPorts)
            {
                // 用户输入端口范围
                string portRange = AnsiConsole.Ask<string>(Unit.I18n.Get("hub_ask_port_range"), "25566-25570");
                var match = Regex.Match(portRange, @"(\d+)\s*-\s*(\d+)");
                if (!match.Success)
                {
                    Output.Log(Unit.I18n.Get("hub_port_range_invalid"), 2, ThisName);
                    match = Regex.Match("25566-25570", @"(\d+)\s*-\s*(\d+)");
                }
                int rangeStart = int.Parse(match.Groups[1].Value);
                int rangeEnd = int.Parse(match.Groups[2].Value);

                int port = rangeStart;
                foreach (var memberId in selectedMembers)
                {
                    if (port > rangeEnd)
                    {
                        Output.Log(Unit.I18n.Get("hub_port_range_exhausted", memberId, port), 2, ThisName);
                    }
                    serverPorts[memberId] = port;
                    port++;
                }
            }
            else
            {
                // 从各服务器的 server.properties 读取端口
                var usedPorts = new HashSet<int>();
                foreach (var memberId in selectedMembers)
                {
                    if (!Unit.Config.App.ServerList.TryGetValue(memberId, out var entry))
                    {
                        serverPorts[memberId] = 25565;
                        continue;
                    }
                    int port = ReadServerProperty(entry.WorkPath, "server-port", 25565);
                    if (usedPorts.Contains(port))
                    {
                        port = AnsiConsole.Ask<int>(Unit.I18n.Get("hub_port_conflict", memberId, port), port + 1);
                    }
                    usedPorts.Add(port);
                    serverPorts[memberId] = port;
                }
            }

            // 写入子服务器 server.properties 端口
            foreach (var kv in serverPorts)
            {
                if (Unit.Config.App.ServerList.TryGetValue(kv.Key, out var entry) && !string.IsNullOrEmpty(entry.WorkPath))
                {
                    WriteServerProperty(entry.WorkPath, "server-port", kv.Value.ToString());
                    Output.Log(Unit.I18n.Get("hub_port_set", kv.Key, kv.Value), 1, ThisName);
                }
            }

            // ===== 步骤5: 在线/离线模式 =====
            Output.Log(Unit.I18n.Get("hub_step5_mode"), 1, ThisName);

            var modeChoices = new List<string> { Unit.I18n.Get("hub_mode_online"), Unit.I18n.Get("hub_mode_offline") };
            var modeChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title(Unit.I18n.Get("hub_select_mode_title"))
                    .AddChoices(modeChoices));

            bool onlineMode = modeChoice == Unit.I18n.Get("hub_mode_online");

            // 设置子服务器的 online-mode=false (无论代理是在线还是离线)
            foreach (var memberId in selectedMembers)
            {
                if (Unit.Config.App.ServerList.TryGetValue(memberId, out var entry) && !string.IsNullOrEmpty(entry.WorkPath))
                {
                    WriteServerProperty(entry.WorkPath, "online-mode", "false");
                }
            }
            Output.Log(Unit.I18n.Get("hub_sub_online_mode_false"), 1, ThisName);

            if (!onlineMode)
            {
                Output.Log(Unit.I18n.Get("hub_offline_notes_title"), 1, ThisName);
                Output.Log(Unit.I18n.Get("hub_offline_note_1"), 1, ThisName);
                Output.Log(Unit.I18n.Get("hub_offline_note_2"), 1, ThisName);
                Output.Log(Unit.I18n.Get("hub_offline_note_3"), 1, ThisName);
            }

            // ===== 步骤6: 选择首入服务器 + 生成代理配置 =====
            Output.Log(Unit.I18n.Get("hub_step6_gen_config"), 1, ThisName);

            string lobbyServer = "";
            if (selectedMembers.Count > 0)
            {
                lobbyServer = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title(Unit.I18n.Get("hub_select_lobby_title"))
                        .AddChoices(selectedMembers));
                Output.Log(Unit.I18n.Get("hub_lobby_server", lobbyServer), 1, ThisName);
            }

            // 生成代理配置文件
            if (isVelocity)
            {
                GenerateVelocityConfig(proxyWorkPath, proxyPort, serverPorts, lobbyServer, onlineMode);
                Output.Log(Unit.I18n.Get("hub_velocity_config_generated"), 1, ThisName);
            }
            else
            {
                GenerateBungeeCordConfig(proxyWorkPath, proxyPort, serverPorts, lobbyServer, onlineMode);
                Output.Log(Unit.I18n.Get("hub_bungeecord_config_generated"), 1, ThisName);
            }

            // 询问是否启动代理服务端
            if (AnsiConsole.Confirm(Unit.I18n.Get("hub_confirm_start_proxy"), false))
            {
                Unit.Config.App.CurrentServer = proxyId;
                Unit.Config.SaveCurrentConfig();
                Function.Analyzer.Initialize();
                Function.Analyzer.StartServer();
            }

            AnsiConsole.Write(new Rule(Unit.I18n.Get("hub_build_complete")).RuleStyle("green").Centered());

            // 显示群组摘要
            var summary = new Table().Border(TableBorder.Rounded).Title(Unit.I18n.Get("hub_summary_title"));
            summary.AddColumn(Unit.I18n.Get("hub_summary_item")).AddColumn(Unit.I18n.Get("hub_summary_value"));
            summary.AddRow(Unit.I18n.Get("hub_summary_group_name"), groupName);
            summary.AddRow(Unit.I18n.Get("hub_summary_group_id"), groupId);
            summary.AddRow(Unit.I18n.Get("hub_summary_proxy"), proxyId);
            summary.AddRow(Unit.I18n.Get("hub_summary_proxy_port"), proxyPort.ToString());
            summary.AddRow(Unit.I18n.Get("hub_summary_auth_mode"), onlineMode ? Unit.I18n.Get("hub_online") : Unit.I18n.Get("hub_offline"));
            summary.AddRow(Unit.I18n.Get("hub_summary_sub_count"), selectedMembers.Count.ToString());
            summary.AddRow(Unit.I18n.Get("hub_summary_lobby"), lobbyServer);
            AnsiConsole.Write(summary);

            foreach (var kv in serverPorts)
                Output.Log($"  {kv.Key} -> 127.0.0.1:{kv.Value}", 1, ThisName);
        }

        /// <summary>
        /// 群组服务器一键构建(非交互, 供面板 gRPC 调用)
        /// 对应 .group build 向导能力的程序化版本
        /// </summary>
        public static async Task<(bool Success, string Message, string GroupId, string ProxyInstanceId, Dictionary<string, int> ServerPorts)> GroupBuildForPanel(
            string groupName, List<string> memberIds, string proxyType, string proxyId, int proxyPort,
            bool autoPorts, int portRangeStart, int portRangeEnd, bool onlineMode, string lobbyServer)
        {
            string ThisName = "GroupBuild";

            try
            {
                // ===== 步骤1: 校验并创建群组 =====
                if (string.IsNullOrWhiteSpace(groupName))
                    return (false, "群组名称不能为空", "", "", new Dictionary<string, int>());

                // 过滤有效成员(必须存在于 server_list)
                var members = (memberIds ?? new List<string>())
                    .Where(m => !string.IsNullOrWhiteSpace(m) && Unit.Config.App.ServerList.ContainsKey(m.Trim()))
                    .Select(m => m.Trim())
                    .Distinct()
                    .ToList();

                // 已存在同名群组时复用(支持为已有群组补建代理), 否则新建
                string groupId;
                var existingGroup = ServerDataManager.Data.Groups
                    .FirstOrDefault(g => g.Name.Equals(groupName.Trim(), StringComparison.OrdinalIgnoreCase));
                if (existingGroup != null)
                {
                    groupId = existingGroup.Id;
                    var missing = members.Where(m => !existingGroup.MemberIds.Contains(m)).ToList();
                    if (missing.Count > 0)
                        ServerDataManager.UpdateGroup("add", groupId, null, missing);
                    Output.Log(Unit.I18n.Get("hub_group_exists_reuse", groupName, groupId), 1, ThisName);
                }
                else
                {
                    var (ok, msg, gid) = ServerDataManager.CreateGroup(groupName.Trim(), members);
                    if (!ok)
                        return (false, msg, "", "", new Dictionary<string, int>());
                    groupId = gid;
                    Output.Log(Unit.I18n.Get("hub_group_created_with_members", groupName, groupId, members.Count), 1, ThisName);
                }

                // ===== 步骤2: 创建代理服务端实例 =====
                proxyType = (proxyType ?? "velocity").Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(proxyId))
                    return (false, "代理服务端标识不能为空", groupId, "", new Dictionary<string, int>());
                proxyId = proxyId.Trim();
                if (Unit.Config.App.ServerList.ContainsKey(proxyId))
                    return (false, $"标识 '{proxyId}' 已存在", groupId, "", new Dictionary<string, int>());

                string proxyWorkPath = Path.Combine(Environment.CurrentDirectory, "servers", proxyId);
                if (!Directory.Exists(proxyWorkPath))
                {
                    Directory.CreateDirectory(proxyWorkPath);
                    Output.Log(Unit.I18n.Get("hub_autocreate_proxy_workdir", proxyWorkPath), 1, ThisName);
                }

                string jarName;
                if (proxyType == "velocity")
                {
                    Output.Log(Unit.I18n.Get("hub_downloading_velocity"), 1, ThisName);
                    var (dOk, dMsg, dJar) = await Function.Intelligence.DownloadServerJarForPanel("velocity", "", proxyWorkPath);
                    if (dOk)
                    {
                        jarName = dJar;
                    }
                    else
                    {
                        Output.Log(Unit.I18n.Get("hub_velocity_download_failed_detail", dMsg), 2, ThisName);
                        jarName = "velocity.jar";
                    }
                }
                else if (proxyType == "bungeecord")
                {
                    Output.Log(Unit.I18n.Get("hub_bungeecord_manual"), 1, ThisName);
                    jarName = "BungeeCord.jar";
                }
                else
                {
                    // manual: 尝试使用代理目录中已有的 jar
                    var jars = Directory.GetFiles(proxyWorkPath, "*.jar");
                    jarName = jars.Length > 0 ? Path.GetFileName(jars[0]) : "proxy.jar";
                    Output.Log(Unit.I18n.Get("hub_proxy_use_existing_jar", jarName), 1, ThisName);
                }

                if (proxyPort <= 0 || proxyPort > 65535) proxyPort = 25565;
                string proxyRunFlags = $"-Xms256M -Xmx512M -jar {jarName} --nogui";
                var (pOk, pMsg) = ServerDataManager.CreateInstance(proxyId, proxyId + " (代理)", proxyWorkPath, "", proxyRunFlags, "RUN", groupId);
                if (!pOk)
                    return (false, pMsg, groupId, "", new Dictionary<string, int>());
                Output.Log(Unit.I18n.Get("hub_proxy_created", proxyId), 1, ThisName);

                // ===== 步骤3: 端口配置 =====
                var serverPorts = new Dictionary<string, int>();
                if (autoPorts)
                {
                    int rangeStart = portRangeStart > 0 && portRangeStart <= 65535 ? portRangeStart : 25566;
                    int rangeEnd = portRangeEnd >= rangeStart && portRangeEnd <= 65535 ? portRangeEnd : 25570;
                    int port = rangeStart;
                    foreach (var memberId in members)
                    {
                        if (port > rangeEnd)
                            Output.Log(Unit.I18n.Get("hub_port_range_exhausted", memberId, port), 2, ThisName);
                        serverPorts[memberId] = port++;
                    }
                }
                else
                {
                    // 从各服务器的 server.properties 读取端口, 冲突时自动递增
                    var usedPorts = new HashSet<int>();
                    foreach (var memberId in members)
                    {
                        if (!Unit.Config.App.ServerList.TryGetValue(memberId, out var entry))
                        {
                            serverPorts[memberId] = 25565;
                            continue;
                        }
                        int port = ReadServerProperty(entry.WorkPath, "server-port", 25565);
                        while (usedPorts.Contains(port))
                            port++;
                        usedPorts.Add(port);
                        serverPorts[memberId] = port;
                    }
                }

                // 写入子服务器 server.properties 端口
                foreach (var kv in serverPorts)
                {
                    if (Unit.Config.App.ServerList.TryGetValue(kv.Key, out var entry) && !string.IsNullOrEmpty(entry.WorkPath))
                    {
                        WriteServerProperty(entry.WorkPath, "server-port", kv.Value.ToString());
                        Output.Log(Unit.I18n.Get("hub_port_set", kv.Key, kv.Value), 1, ThisName);
                    }
                }

                // ===== 步骤4: 在线/离线模式 =====
                foreach (var memberId in members)
                {
                    if (Unit.Config.App.ServerList.TryGetValue(memberId, out var entry) && !string.IsNullOrEmpty(entry.WorkPath))
                        WriteServerProperty(entry.WorkPath, "online-mode", "false");
                }
                Output.Log(Unit.I18n.Get("hub_sub_online_mode_false"), 1, ThisName);

                // ===== 步骤5: 生成代理配置 =====
                string lobby = string.IsNullOrWhiteSpace(lobbyServer) ? members.FirstOrDefault() ?? "" : lobbyServer.Trim();
                if (proxyType == "velocity")
                {
                    GenerateVelocityConfig(proxyWorkPath, proxyPort, serverPorts, lobby, onlineMode);
                    Output.Log(Unit.I18n.Get("hub_velocity_config_generated"), 1, ThisName);
                }
                else
                {
                    GenerateBungeeCordConfig(proxyWorkPath, proxyPort, serverPorts, lobby, onlineMode);
                    Output.Log(Unit.I18n.Get("hub_bungeecord_config_generated"), 1, ThisName);
                }

                Output.Log(Unit.I18n.Get("hub_group_build_complete", groupName, proxyId, proxyPort, onlineMode ? Unit.I18n.Get("hub_online") : Unit.I18n.Get("hub_offline")), 1, ThisName);
                return (true, $"群组 '{groupName}' 构建完成, 代理实例 '{proxyId}' 已创建 (玩家连接端口 {proxyPort})", groupId, proxyId, serverPorts);
            }
            catch (Exception ex)
            {
                Output.Log(Unit.I18n.Get("hub_group_build_failed", ex.Message), 3, ThisName);
                return (false, $"群组构建失败: {ex.Message}", "", "", new Dictionary<string, int>());
            }
        }

        /// <summary>读取 server.properties 中的属性值</summary>
        private static int ReadServerProperty(string workPath, string key, int defaultValue)
        {
            try
            {
                string filePath = Path.Combine(workPath, "server.properties");
                if (!File.Exists(filePath)) return defaultValue;

                foreach (var line in File.ReadAllLines(filePath, Encoding.UTF8))
                {
                    if (line.StartsWith($"{key}=", StringComparison.OrdinalIgnoreCase))
                        return int.TryParse(line.Substring(key.Length + 1).Trim(), out int v) ? v : defaultValue;
                }
            }
            catch { }
            return defaultValue;
        }

        /// <summary>写入 server.properties 中的属性值(不存在则追加)</summary>
        private static void WriteServerProperty(string workPath, string key, string value)
        {
            try
            {
                string filePath = Path.Combine(workPath, "server.properties");
                var lines = new List<string>();
                string newline = "\n";
                bool found = false;

                if (File.Exists(filePath))
                {
                    string raw = File.ReadAllText(filePath, Encoding.UTF8);
                    newline = raw.Contains("\r\n") ? "\r\n" : "\n";
                    lines = raw.TrimStart('\uFEFF')
                        .Replace("\r\n", "\n").Replace('\r', '\n')
                        .Split('\n').ToList();
                    while (lines.Count > 0 && lines[^1].Length == 0)
                        lines.RemoveAt(lines.Count - 1);
                }

                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].StartsWith($"{key}=", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"{key}={value}";
                        found = true;
                        break;
                    }
                }

                if (!found)
                    lines.Add($"{key}={value}");

                // AtomicFile 使用 UTF-8 无 BOM, 避免 MC 解析异常
                Unit.AtomicFile.WriteAllText(filePath, string.Join(newline, lines));
            }
            catch (Exception ex)
            {
                Output.Log(Unit.I18n.Get("hub_write_server_properties_failed", ex.Message), 2, "GroupBuild");
            }
        }

        /// <summary>生成 Velocity 的 velocity.toml 配置文件(官方模板, UTF-8 无 BOM)</summary>
        private static void GenerateVelocityConfig(string workPath, int proxyPort,
            Dictionary<string, int> serverPorts, string lobbyServer, bool onlineMode)
        {
            string configPath = Path.Combine(workPath, "velocity.toml");
            string secretPath = Path.Combine(workPath, "forwarding.secret");

            // modern 转发要求代理与子服共享同一密钥: 首次生成并复用, 便于后续在子服填同一值
            ReadOrCreateForwardingSecret(secretPath);

            var sb = new StringBuilder();
            sb.AppendLine("# Velocity 配置文件 (由 RtCli 群组构建向导生成)");
            sb.AppendLine();
            sb.AppendLine("[config]");
            sb.AppendLine($"bind = \"0.0.0.0:{proxyPort}\"");
            sb.AppendLine($"motd = {TomlQuote("RtCli 群组服务器")}");
            sb.AppendLine("show-max-players = 500");
            sb.AppendLine($"online-mode = {onlineMode.ToString().ToLowerInvariant()}");
            sb.AppendLine("player-info-forwarding-mode = \"modern\"");
            sb.AppendLine("forwarding-secret-file = \"forwarding.secret\"");
            sb.AppendLine();
            sb.AppendLine("[servers]");
            foreach (var kv in serverPorts)
                sb.AppendLine($"{TomlQuote(kv.Key)} = \"127.0.0.1:{kv.Value}\"");
            if (!string.IsNullOrEmpty(lobbyServer))
                sb.AppendLine($"try = [{TomlQuote(lobbyServer)}]");
            sb.AppendLine();
            sb.AppendLine("[forced-hosts]");
            sb.AppendLine();
            sb.AppendLine("[advanced]");
            sb.AppendLine("compression-threshold = 256");
            sb.AppendLine("connection-timeout = 5000");
            sb.AppendLine("read-timeout = 30000");

            Unit.AtomicFile.WriteAllText(configPath, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>读取或生成 Velocity modern 转发的共享密钥文件</summary>
        private static string ReadOrCreateForwardingSecret(string secretPath)
        {
            try
            {
                if (File.Exists(secretPath))
                {
                    string existing = File.ReadAllText(secretPath).Trim();
                    if (existing.Length >= 12) return existing;
                }
            }
            catch { }

            string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
            Unit.AtomicFile.WriteAllText(secretPath, secret, new UTF8Encoding(false));
            return secret;
        }

        /// <summary>生成 BungeeCord 的 config.yml 配置文件(官方模板, UTF-8 无 BOM)</summary>
        private static void GenerateBungeeCordConfig(string workPath, int proxyPort,
            Dictionary<string, int> serverPorts, string lobbyServer, bool onlineMode)
        {
            string configPath = Path.Combine(workPath, "config.yml");
            var sb = new StringBuilder();

            sb.AppendLine("# BungeeCord 配置文件 (由 RtCli 群组构建向导生成)");
            sb.AppendLine($"online_mode: {onlineMode.ToString().ToLowerInvariant()}");
            sb.AppendLine("ip_forward: true");
            sb.AppendLine("server_connect_timeout: 5000");
            sb.AppendLine("listeners:");
            sb.AppendLine($"- host: 0.0.0.0:{proxyPort}");
            sb.AppendLine($"  motd: {YamlQuote("RtCli 群组服务器")}");
            sb.AppendLine("  max_players: 500");
            sb.AppendLine("  priorities:");
            if (!string.IsNullOrEmpty(lobbyServer))
                sb.AppendLine($"  - {YamlQuote(lobbyServer)}");
            sb.AppendLine($"  query_port: {proxyPort}");
            sb.AppendLine("  ping_passthrough: false");
            sb.AppendLine("servers:");
            foreach (var kv in serverPorts)
            {
                sb.AppendLine($"  {YamlQuote(kv.Key)}:");
                sb.AppendLine($"    motd: {YamlQuote(kv.Key)}");
                sb.AppendLine($"    address: 127.0.0.1:{kv.Value}");
                sb.AppendLine("    restricted: false");
            }

            Unit.AtomicFile.WriteAllText(configPath, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>TOML 字符串转义</summary>
        private static string TomlQuote(string value)
            => "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        /// <summary>YAML 单引号字符串转义(单引号自身用两个单引号表示)</summary>
        private static string YamlQuote(string value)
            => "'" + (value ?? "").Replace("'", "''") + "'";
    }

}
