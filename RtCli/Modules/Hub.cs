using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
                bool found = false;

                if (File.Exists(filePath))
                    lines = File.ReadAllLines(filePath, Encoding.UTF8).ToList();

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

                Unit.AtomicFile.WriteAllText(filePath, string.Join("\n", lines), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Output.Log(Unit.I18n.Get("hub_write_server_properties_failed", ex.Message), 2, "GroupBuild");
            }
        }

        /// <summary>生成 Velocity 的 velocity.toml 配置文件</summary>
        private static void GenerateVelocityConfig(string workPath, int proxyPort,
            Dictionary<string, int> serverPorts, string lobbyServer, bool onlineMode)
        {
            string configPath = Path.Combine(workPath, "velocity.toml");
            var sb = new StringBuilder();

            sb.AppendLine("# Velocity 配置文件 (由 RtCli 群组构建向导生成)");
            sb.AppendLine();

            sb.AppendLine("[config]");
            sb.AppendLine($"bind = \"0.0.0.0:{proxyPort}\"");
            sb.AppendLine($"motd = \"<#09add3>RtCli 群组服务器\"");
            sb.AppendLine();

            sb.AppendLine("[servers]");
            foreach (var kv in serverPorts)
            {
                sb.AppendLine($"\"{kv.Key}\" = \"127.0.0.1:{kv.Value}\"");
            }
            sb.AppendLine();

            if (!string.IsNullOrEmpty(lobbyServer))
            {
                sb.AppendLine("[try]");
                sb.AppendLine($"\"{lobbyServer}\"");
            }

            sb.AppendLine();
            sb.AppendLine("[advanced]");
            sb.AppendLine($"online-mode = {onlineMode.ToString().ToLowerInvariant()}");

            File.WriteAllText(configPath, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>生成 BungeeCord 的 config.yml 配置文件</summary>
        private static void GenerateBungeeCordConfig(string workPath, int proxyPort,
            Dictionary<string, int> serverPorts, string lobbyServer, bool onlineMode)
        {
            string configPath = Path.Combine(workPath, "config.yml");
            var sb = new StringBuilder();

            sb.AppendLine("# BungeeCord 配置文件 (由 RtCli 群组构建向导生成)");
            sb.AppendLine($"online_mode: {onlineMode.ToString().ToLowerInvariant()}");
            sb.AppendLine($"listeners:");
            sb.AppendLine($"- query_port: {proxyPort}");
            sb.AppendLine($"  priorities:");
            if (!string.IsNullOrEmpty(lobbyServer))
                sb.AppendLine($"  - {lobbyServer}");
            sb.AppendLine();
            sb.AppendLine($"servers:");

            foreach (var kv in serverPorts)
            {
                sb.AppendLine($"  {kv.Key}:");
                sb.AppendLine($"    motd: '{kv.Key}'");
                sb.AppendLine($"    address: 127.0.0.1:{kv.Value}");
                sb.AppendLine($"    restricted: false");
            }

            File.WriteAllText(configPath, sb.ToString(), Encoding.UTF8);
        }
    }

    // ===== 实例管理数据模型 =====
    // server_data.json 存储实例扩展元数据(分组、顺序、备份历史、创建时间等),
    // 实际服务器配置仍由 config.yml 的 server_list 管理。
    // 创建/删除实例时由 ServerDataManager 同步修改 config.yml。

    /// <summary>实例扩展元数据(存储在 server_data.json)</summary>
    public class ServerInstance
    {
        /// <summary>实例 ID, 等同于 config.yml server_list 的 key 和 identifier</summary>
        public string Id { get; set; } = "";
        /// <summary>所属群组 ID(空 = 独立实例)</summary>
        public string GroupId { get; set; } = "";
        /// <summary>显示顺序(独立实例间排序, 群组内按加入顺序)</summary>
        public int SortOrder { get; set; } = 0;
        /// <summary>创建时间(ISO 8601)</summary>
        public string CreatedAt { get; set; } = "";
        /// <summary>最后启动时间</summary>
        public string LastStartedAt { get; set; } = "";
        /// <summary>备注/描述</summary>
        public string Description { get; set; } = "";
    }

    /// <summary>服务器群组</summary>
    public class ServerGroup
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>群组成员实例 ID 列表(顺序即显示顺序)</summary>
        public List<string> MemberIds { get; set; } = new List<string>();
        public string CreatedAt { get; set; } = "";
    }

    /// <summary>server_data.json 根结构</summary>
    public class ServerData
    {
        public List<ServerInstance> Instances { get; set; } = new List<ServerInstance>();
        public List<ServerGroup> Groups { get; set; } = new List<ServerGroup>();
        /// <summary>下一个群组序号(用于生成群组 ID)</summary>
        public int NextGroupSeq { get; set; } = 1;
    }

    /// <summary>
    /// 实例数据管理器: 负责加载/保存 server_data.json,
    /// 并在创建/删除实例时同步 config.yml 的 server_list。
    /// 所有操作线程安全。
    /// </summary>
    public static class ServerDataManager
    {
        private static readonly object _lock = new object();
        private static ServerData? _data;
        private static readonly string DataFileName = "server_data.json";

        private static string DataFilePath => Path.Combine(Unit.Config.DataPath, DataFileName);

        /// <summary>获取当前数据(懒加载, 首次调用时从文件读取)</summary>
        public static ServerData Data
        {
            get
            {
                lock (_lock)
                {
                    if (_data == null) Load();
                    return _data!;
                }
            }
        }

        /// <summary>从磁盘加载 server_data.json</summary>
        public static void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(DataFilePath))
                    {
                        var json = File.ReadAllText(DataFilePath, Encoding.UTF8);
                        _data = JsonSerializer.Deserialize<ServerData>(json) ?? new ServerData();
                    }
                    else
                    {
                        _data = new ServerData();
                        Save();
                    }

                    // 同步: 确保每个 server_list 条目都有对应的实例元数据
                    SyncWithServerList();
                }
                catch (Exception ex)
                {
                    Output.Log(Unit.I18n.Get("hub_load_server_data_failed", ex.Message), 3, "Hub");
                    _data = new ServerData();
                }
            }
        }

        /// <summary>保存到磁盘</summary>
        public static void Save()
        {
            lock (_lock)
            {
                try
                {
                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                    };
                    var json = JsonSerializer.Serialize(_data, options);
                    Unit.AtomicFile.WriteAllText(DataFilePath, json, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Output.Log(Unit.I18n.Get("hub_save_server_data_failed", ex.Message), 3, "Hub");
                }
            }
        }

        /// <summary>
        /// 同步实例元数据与 config.yml server_list:
        /// - server_list 中有但 server_data.json 没有的 → 创建元数据
        /// - server_data.json 中有但 server_list 没有的 → 删除元数据(及群组成员引用)
        /// </summary>
        private static void SyncWithServerList()
        {
            if (_data == null) return;

            var serverKeys = Unit.Config.App.ServerList.Keys.ToHashSet();

            // 添加缺失的实例元数据
            foreach (var key in serverKeys)
            {
                if (!_data.Instances.Any(i => i.Id == key))
                {
                    _data.Instances.Add(new ServerInstance
                    {
                        Id = key,
                        CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        SortOrder = _data.Instances.Count
                    });
                }
            }

            // 移除多余的实例元数据 + 清理群组引用
            var toRemove = _data.Instances.Where(i => !serverKeys.Contains(i.Id)).ToList();
            foreach (var inst in toRemove)
            {
                _data.Instances.Remove(inst);
                foreach (var g in _data.Groups)
                    g.MemberIds.Remove(inst.Id);
            }

            // 清理空群组(可选: 保留空群组让用户手动解散)
            // 不自动清理, 由用户显式解散

            Save();
        }

        // ===== 实例 CRUD =====

        /// <summary>创建实例: 同步在 config.yml server_list 和 server_data.json 中添加</summary>
        public static (bool Success, string Message) CreateInstance(string identifier, string name,
            string workPath, string javaPath, string runFlags, string analyzerMode, string groupId)
        {
            lock (_lock)
            {
                Load();

                // 校验: identifier 不能为空且不能重复
                identifier = identifier?.Trim() ?? "";
                if (string.IsNullOrEmpty(identifier))
                    return (false, "实例标识不能为空");
                if (Unit.Config.App.ServerList.ContainsKey(identifier))
                    return (false, $"标识 '{identifier}' 已存在");
                if (_data!.Instances.Any(i => i.Id == identifier))
                    return (false, $"实例 '{identifier}' 已存在");

                // 校验群组
                if (!string.IsNullOrEmpty(groupId) && !_data.Groups.Any(g => g.Id == groupId))
                    return (false, $"群组 '{groupId}' 不存在");

                // 添加到 config.yml server_list
                var entry = new Unit.ServerEntry
                {
                    ServerName = string.IsNullOrEmpty(name) ? identifier : name,
                    WorkPath = workPath ?? "",
                    JavaPath = javaPath ?? "",
                    RunServerFlags = string.IsNullOrEmpty(runFlags)
                        ? "-Xms1024M -Xmx1024M -jar server.jar --nogui"
                        : runFlags,
                    AnalyzerMode = string.IsNullOrEmpty(analyzerMode) ? "RM" : analyzerMode,
                    AutoRestart = false
                };
                Unit.Config.App.ServerList[identifier] = entry;

                // 添加到 server_data.json
                var instance = new ServerInstance
                {
                    Id = identifier,
                    GroupId = groupId ?? "",
                    SortOrder = _data.Instances.Count,
                    CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };
                _data.Instances.Add(instance);

                // 添加到群组成员
                if (!string.IsNullOrEmpty(groupId))
                {
                    var group = _data.Groups.First(g => g.Id == groupId);
                    if (!group.MemberIds.Contains(identifier))
                        group.MemberIds.Add(identifier);
                }

                Unit.Config.SaveCurrentConfig();
                Save();

                Output.Log(Unit.I18n.Get("hub_instance_created", identifier, entry.ServerName), 1, "Hub");
                return (true, $"实例 '{identifier}' 创建成功");
            }
        }

        /// <summary>删除实例: 同步从 config.yml 和 server_data.json 中移除</summary>
        public static (bool Success, string Message) DeleteInstance(string id, bool deleteFiles)
        {
            lock (_lock)
            {
                Load();

                if (!Unit.Config.App.ServerList.ContainsKey(id))
                    return (false, $"实例 '{id}' 不存在");

                // 从 config.yml 移除
                var entry = Unit.Config.App.ServerList[id];
                var workPath = entry.WorkPath;
                Unit.Config.App.ServerList.Remove(id);

                // 从 hide_console_servers 移除
                Unit.Config.App.HideConsoleServers.Remove(id);

                // 如果是当前服务器, 切换到第一个可用的
                if (Unit.Config.App.CurrentServer == id)
                {
                    Unit.Config.App.CurrentServer = Unit.Config.App.ServerList.Keys.FirstOrDefault() ?? "";
                }

                // 从 server_data.json 移除
                _data!.Instances.RemoveAll(i => i.Id == id);
                foreach (var g in _data.Groups)
                    g.MemberIds.Remove(id);

                Unit.Config.SaveCurrentConfig();
                Save();

                // 可选: 删除服务器文件
                if (deleteFiles && !string.IsNullOrEmpty(workPath) && Directory.Exists(workPath))
                {
                    try
                    {
                        Directory.Delete(workPath, true);
                        Output.Log(Unit.I18n.Get("hub_instance_files_deleted", workPath), 1, "Hub");
                    }
                    catch (Exception ex)
                    {
                        Output.Log(Unit.I18n.Get("hub_delete_instance_files_failed", ex.Message), 2, "Hub");
                    }
                }

                Output.Log(Unit.I18n.Get("hub_instance_deleted", id), 1, "Hub");
                return (true, $"实例 '{id}' 已删除");
            }
        }

        /// <summary>更新实例配置(同步到 config.yml)</summary>
        public static (bool Success, string Message) UpdateInstance(string id, string name,
            string groupId, bool hidden, bool autoRestart,
            string workPath, string javaPath, string runFlags, string analyzerMode)
        {
            lock (_lock)
            {
                Load();

                if (!Unit.Config.App.ServerList.ContainsKey(id))
                    return (false, $"实例 '{id}' 不存在");

                var entry = Unit.Config.App.ServerList[id];

                // 更新 config.yml 字段
                if (name != null) entry.ServerName = name;
                if (workPath != null) entry.WorkPath = workPath;
                if (javaPath != null) entry.JavaPath = javaPath;
                if (runFlags != null) entry.RunServerFlags = runFlags;
                if (analyzerMode != null) entry.AnalyzerMode = analyzerMode;
                entry.AutoRestart = autoRestart;
                Unit.Config.App.ServerList[id] = entry;

                // 同步 hidden 到 hide_console_servers
                if (hidden)
                {
                    if (!Unit.Config.App.HideConsoleServers.Contains(id))
                        Unit.Config.App.HideConsoleServers.Add(id);
                }
                else
                {
                    Unit.Config.App.HideConsoleServers.Remove(id);
                }

                // 更新群组归属
                var instance = _data!.Instances.FirstOrDefault(i => i.Id == id);
                if (instance != null)
                {
                    // 从旧群组移除
                    if (!string.IsNullOrEmpty(instance.GroupId))
                    {
                        var oldGroup = _data.Groups.FirstOrDefault(g => g.Id == instance.GroupId);
                        oldGroup?.MemberIds.Remove(id);
                    }

                    // 添加到新群组
                    instance.GroupId = groupId ?? "";
                    if (!string.IsNullOrEmpty(groupId))
                    {
                        var newGroup = _data.Groups.FirstOrDefault(g => g.Id == groupId);
                        if (newGroup == null)
                            return (false, $"群组 '{groupId}' 不存在");
                        if (!newGroup.MemberIds.Contains(id))
                            newGroup.MemberIds.Add(id);
                    }
                }

                Unit.Config.SaveCurrentConfig();
                Save();

                Output.Log(Unit.I18n.Get("hub_instance_updated", id), 1, "Hub");
                return (true, $"实例 '{id}' 更新成功");
            }
        }

        // ===== 群组管理 =====

        public static (bool Success, string Message, string GroupId) CreateGroup(string name, List<string> memberIds)
        {
            lock (_lock)
            {
                Load();

                name = name?.Trim() ?? "";
                if (string.IsNullOrEmpty(name))
                    return (false, "群组名称不能为空", "");

                if (_data!.Groups.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    return (false, $"群组 '{name}' 已存在", "");

                var group = new ServerGroup
                {
                    Id = $"g{_data.NextGroupSeq++}",
                    Name = name,
                    CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };

                // 校验并添加成员
                foreach (var memberId in memberIds)
                {
                    if (_data.Instances.Any(i => i.Id == memberId))
                    {
                        // 从旧群组移除
                        var inst = _data.Instances.First(i => i.Id == memberId);
                        if (!string.IsNullOrEmpty(inst.GroupId))
                        {
                            var oldGroup = _data.Groups.FirstOrDefault(g => g.Id == inst.GroupId);
                            oldGroup?.MemberIds.Remove(memberId);
                        }
                        inst.GroupId = group.Id;
                        group.MemberIds.Add(memberId);
                    }
                }

                _data.Groups.Add(group);
                Save();

                Output.Log(Unit.I18n.Get("hub_group_created_log", name, group.Id, group.MemberIds.Count), 1, "Hub");
                return (true, $"群组 '{name}' 创建成功", group.Id);
            }
        }

        public static (bool Success, string Message) DeleteGroup(string groupId)
        {
            lock (_lock)
            {
                Load();

                var group = _data!.Groups.FirstOrDefault(g => g.Id == groupId);
                if (group == null)
                    return (false, $"群组 '{groupId}' 不存在");

                // 解除成员的群组归属
                foreach (var memberId in group.MemberIds)
                {
                    var inst = _data.Instances.FirstOrDefault(i => i.Id == memberId);
                    if (inst != null) inst.GroupId = "";
                }

                _data.Groups.Remove(group);
                Save();

                Output.Log(Unit.I18n.Get("hub_group_disbanded", group.Name, groupId), 1, "Hub");
                return (true, $"群组 '{group.Name}' 已解散");
            }
        }

        public static (bool Success, string Message) UpdateGroup(string operation, string groupId,
            string? name, List<string> memberIds)
        {
            lock (_lock)
            {
                Load();

                var group = _data!.Groups.FirstOrDefault(g => g.Id == groupId);
                if (group == null)
                    return (false, $"群组 '{groupId}' 不存在");

                switch (operation?.ToLowerInvariant())
                {
                    case "rename":
                        name = name?.Trim() ?? "";
                        if (string.IsNullOrEmpty(name))
                            return (false, "群组名称不能为空");
                        group.Name = name;
                        break;

                    case "add":
                        foreach (var memberId in memberIds)
                        {
                            if (!group.MemberIds.Contains(memberId) &&
                                _data.Instances.Any(i => i.Id == memberId))
                            {
                                // 从旧群组移除
                                var inst = _data.Instances.First(i => i.Id == memberId);
                                if (!string.IsNullOrEmpty(inst.GroupId))
                                {
                                    var oldGroup = _data.Groups.FirstOrDefault(g => g.Id == inst.GroupId);
                                    oldGroup?.MemberIds.Remove(memberId);
                                }
                                inst.GroupId = group.Id;
                                group.MemberIds.Add(memberId);
                            }
                        }
                        break;

                    case "remove":
                        foreach (var memberId in memberIds)
                        {
                            if (group.MemberIds.Remove(memberId))
                            {
                                var inst = _data.Instances.FirstOrDefault(i => i.Id == memberId);
                                if (inst != null) inst.GroupId = "";
                            }
                        }
                        break;

                    default:
                        return (false, $"未知操作: {operation}");
                }

                Save();
                Output.Log(Unit.I18n.Get("hub_group_updated", group.Name, operation), 1, "Hub");
                return (true, $"群组 '{group.Name}' 更新成功");
            }
        }

        // ===== 备份管理 =====

        /// <summary>获取实例备份目录</summary>
        public static string GetBackupDir(string instanceId)
        {
            var dir = Path.Combine(Unit.Config.DataPath, "instance_backup", instanceId);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>列出实例的所有备份</summary>
        public static List<(string FileName, string CreatedAt, long SizeBytes)> ListBackups(string instanceId)
        {
            var result = new List<(string, string, long)>();
            try
            {
                var dir = GetBackupDir(instanceId);
                foreach (var file in Directory.GetFiles(dir, "*.zip").OrderByDescending(f => f))
                {
                    var fi = new FileInfo(file);
                    result.Add((fi.Name, fi.CreationTime.ToString("yyyy-MM-dd HH:mm:ss"), fi.Length));
                }
            }
            catch { }
            return result;
        }

        /// <summary>创建备份(将 WorkPath 打包为 zip)</summary>
        public static (bool Success, string Message) CreateBackup(string instanceId)
        {
            try
            {
                if (!Unit.Config.App.ServerList.TryGetValue(instanceId, out var entry))
                    return (false, $"实例 '{instanceId}' 不存在");

                if (string.IsNullOrEmpty(entry.WorkPath) || !Directory.Exists(entry.WorkPath))
                    return (false, "服务器工作目录不存在");

                var backupDir = GetBackupDir(instanceId);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var backupFile = Path.Combine(backupDir, $"{timestamp}.zip");

                // 使用 System.IO.Compression 打包
                System.IO.Compression.ZipFile.CreateFromDirectory(entry.WorkPath, backupFile,
                    System.IO.Compression.CompressionLevel.Optimal, false);

                Output.Log(Unit.I18n.Get("hub_backup_created", instanceId, backupFile), 1, "Hub");
                return (true, $"备份成功: {Path.GetFileName(backupFile)}");
            }
            catch (Exception ex)
            {
                return (false, $"备份失败: {ex.Message}");
            }
        }

        /// <summary>从备份恢复</summary>
        public static (bool Success, string Message) RestoreBackup(string instanceId, string fileName)
        {
            try
            {
                if (!Unit.Config.App.ServerList.TryGetValue(instanceId, out var entry))
                    return (false, $"实例 '{instanceId}' 不存在");

                var backupFile = Path.Combine(GetBackupDir(instanceId), fileName);
                if (!File.Exists(backupFile))
                    return (false, $"备份文件不存在: {fileName}");

                if (string.IsNullOrEmpty(entry.WorkPath))
                    return (false, "工作目录未配置");

                // 恢复前先备份当前状态(如果目录存在且非空)
                if (Directory.Exists(entry.WorkPath) && Directory.EnumerateFileSystemEntries(entry.WorkPath).Any())
                {
                    var preRestoreDir = Path.Combine(GetBackupDir(instanceId), "pre_restore");
                    if (!Directory.Exists(preRestoreDir)) Directory.CreateDirectory(preRestoreDir);
                    var preRestoreFile = Path.Combine(preRestoreDir,
                        $"pre_restore_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
                    System.IO.Compression.ZipFile.CreateFromDirectory(entry.WorkPath, preRestoreFile,
                        System.IO.Compression.CompressionLevel.Optimal, false);
                }

                // 清空工作目录并解压备份
                if (Directory.Exists(entry.WorkPath))
                    Directory.Delete(entry.WorkPath, true);
                Directory.CreateDirectory(entry.WorkPath);
                System.IO.Compression.ZipFile.ExtractToDirectory(backupFile, entry.WorkPath);

                Output.Log(Unit.I18n.Get("hub_backup_restored", instanceId, fileName), 1, "Hub");
                return (true, $"恢复成功: {fileName}");
            }
            catch (Exception ex)
            {
                return (false, $"恢复失败: {ex.Message}");
            }
        }

        /// <summary>删除备份文件</summary>
        public static (bool Success, string Message) DeleteBackup(string instanceId, string fileName)
        {
            try
            {
                var backupFile = Path.Combine(GetBackupDir(instanceId), fileName);
                if (!File.Exists(backupFile))
                    return (false, $"备份文件不存在: {fileName}");

                File.Delete(backupFile);
                Output.Log(Unit.I18n.Get("hub_backup_deleted", instanceId, fileName), 1, "Hub");
                return (true, $"备份 {fileName} 已删除");
            }
            catch (Exception ex)
            {
                return (false, $"删除备份失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 面板设置管理器: 负责加载/保存 panel_data.json
    /// 存储在 server_data.json 同目录(Config.DataPath)
    /// 同时管理玩家事件日志(按实例ID分组, 保存在 playerEvents 字段下)
    /// </summary>
    internal static class PanelDataManager
    {
        private static readonly string FileName = "panel_data.json";
        private static string FilePath => Path.Combine(Unit.Config.DataPath, FileName);
        private static readonly object _lock = new();

        /// <summary>每个实例最多保留的玩家事件条数</summary>
        private const int MaxEventsPerInstance = 1000;

        /// <summary>加载面板设置(JSON 字符串)</summary>
        public static string Load()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(FilePath))
                        return "{}";
                    return File.ReadAllText(FilePath);
                }
                catch (Exception ex)
                {
                    Output.Log(Unit.I18n.Get("hub_load_panel_data_failed", ex.Message), 3, "Hub");
                    return "{}";
                }
            }
        }

        /// <summary>保存面板设置(JSON 字符串)</summary>
        public static (bool Success, string Message) Save(string jsonData)
        {
            lock (_lock)
            {
                try
                {
                    Unit.AtomicFile.WriteAllText(FilePath, jsonData);
                    return (true, "保存成功");
                }
                catch (Exception ex)
                {
                    return (false, $"保存失败: {ex.Message}");
                }
            }
        }

        /// <summary>追加一条玩家事件到指定实例的事件列表</summary>
        public static void AppendPlayerEvent(string instanceId, PlayerEventEntry entry)
        {
            if (string.IsNullOrEmpty(instanceId) || entry == null) return;
            lock (_lock)
            {
                try
                {
                    var json = File.Exists(FilePath) ? File.ReadAllText(FilePath) : "{}";
                    using var doc = JsonDocument.Parse(json);
                    using var ms = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(ms))
                    {
                        writer.WriteStartObject();
                        bool hasPlayerEvents = false;
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Name == "playerEvents")
                            {
                                hasPlayerEvents = true;
                                writer.WritePropertyName("playerEvents");
                                writer.WriteStartObject();
                                // 复制已有实例的事件
                                bool foundInstance = false;
                                foreach (var instProp in prop.Value.EnumerateObject())
                                {
                                    if (instProp.Name == instanceId)
                                    {
                                        foundInstance = true;
                                        writer.WritePropertyName(instanceId);
                                        WriteEventArrayWithAppend(writer, instProp.Value, entry);
                                    }
                                    else
                                    {
                                        instProp.WriteTo(writer);
                                    }
                                }
                                if (!foundInstance)
                                {
                                    writer.WritePropertyName(instanceId);
                                    writer.WriteStartArray();
                                    WriteEventEntry(writer, entry);
                                    writer.WriteEndArray();
                                }
                                writer.WriteEndObject();
                            }
                            else
                            {
                                prop.WriteTo(writer);
                            }
                        }
                        if (!hasPlayerEvents)
                        {
                            writer.WritePropertyName("playerEvents");
                            writer.WriteStartObject();
                            writer.WritePropertyName(instanceId);
                            writer.WriteStartArray();
                            WriteEventEntry(writer, entry);
                            writer.WriteEndArray();
                            writer.WriteEndObject();
                        }
                        writer.WriteEndObject();
                    }
                    File.WriteAllText(FilePath, Encoding.UTF8.GetString(ms.ToArray()));
                }
                catch (Exception ex)
                {
                    Output.Log(Unit.I18n.Get("hub_append_player_event_failed", ex.Message), 3, "Hub");
                }
            }
        }

        /// <summary>获取指定实例的玩家事件列表</summary>
        public static List<PlayerEventEntry> GetPlayerEvents(string instanceId, int limit = 0)
        {
            var result = new List<PlayerEventEntry>();
            if (string.IsNullOrEmpty(instanceId)) return result;
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(FilePath)) return result;
                    var json = File.ReadAllText(FilePath);
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("playerEvents", out var eventsObj)) return result;
                    if (!eventsObj.TryGetProperty(instanceId, out var arr)) return result;
                    foreach (var item in arr.EnumerateArray())
                    {
                        result.Add(new PlayerEventEntry
                        {
                            EventType = item.TryGetProperty("eventType", out var t) ? t.GetString() ?? "" : "",
                            PlayerName = item.TryGetProperty("playerName", out var n) ? n.GetString() ?? "" : "",
                            TriggerTime = item.TryGetProperty("triggerTime", out var tt) ? tt.GetString() ?? "" : "",
                            RecordedAt = item.TryGetProperty("recordedAt", out var r) ? r.GetString() ?? "" : "",
                            Detail = item.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : ""
                        });
                    }
                }
                catch (Exception ex)
                {
                    Output.Log(Unit.I18n.Get("hub_read_player_events_failed", ex.Message), 3, "Hub");
                }
            }
            if (limit > 0 && result.Count > limit)
                result = result.Skip(result.Count - limit).ToList();
            return result;
        }

        /// <summary>清空指定实例的玩家事件</summary>
        public static (bool Success, string Message) ClearPlayerEvents(string instanceId)
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(FilePath)) return (true, "无数据");
                    var json = File.ReadAllText(FilePath);
                    using var doc = JsonDocument.Parse(json);
                    using var ms = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(ms))
                    {
                        writer.WriteStartObject();
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Name == "playerEvents")
                            {
                                writer.WritePropertyName("playerEvents");
                                writer.WriteStartObject();
                                foreach (var instProp in prop.Value.EnumerateObject())
                                {
                                    if (instProp.Name != instanceId)
                                        instProp.WriteTo(writer);
                                }
                                writer.WriteEndObject();
                            }
                            else
                            {
                                prop.WriteTo(writer);
                            }
                        }
                        writer.WriteEndObject();
                    }
                    Unit.AtomicFile.WriteAllText(FilePath, Encoding.UTF8.GetString(ms.ToArray()));
                    return (true, "已清空");
                }
                catch (Exception ex)
                {
                    return (false, $"清空失败: {ex.Message}");
                }
            }
        }

        private static void WriteEventArrayWithAppend(Utf8JsonWriter writer, JsonElement existing, PlayerEventEntry newEntry)
        {
            writer.WriteStartArray();
            var items = existing.EnumerateArray().ToList();
            // 超过上限则丢弃最旧的, 保持不超过 MaxEventsPerInstance
            int skip = 0;
            if (items.Count >= MaxEventsPerInstance)
                skip = items.Count - MaxEventsPerInstance + 1;
            for (int i = skip; i < items.Count; i++)
            {
                items[i].WriteTo(writer);
            }
            WriteEventEntry(writer, newEntry);
            writer.WriteEndArray();
        }

        private static void WriteEventEntry(Utf8JsonWriter writer, PlayerEventEntry entry)
        {
            writer.WriteStartObject();
            writer.WriteString("eventType", entry.EventType);
            writer.WriteString("playerName", entry.PlayerName);
            writer.WriteString("triggerTime", entry.TriggerTime);
            writer.WriteString("recordedAt", entry.RecordedAt);
            writer.WriteString("detail", entry.Detail);
            writer.WriteEndObject();
        }
    }

    /// <summary>玩家事件记录条目</summary>
    public class PlayerEventEntry
    {
        public string EventType { get; set; } = "";
        public string PlayerName { get; set; } = "";
        public string TriggerTime { get; set; } = "";
        public string RecordedAt { get; set; } = "";
        public string Detail { get; set; } = "";
    }
}
