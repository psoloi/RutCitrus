using RtCli.Modules;
using RtCli.Modules.Extension;
using RtCli.Modules.Function;
using RtCli.Modules.Unit;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RtCli
{
    /// <summary>
    /// CLI 命令处理器表(#4 单一注册来源): 与面板命令表(Backend.PanelCommands.cs)对称。
    /// 每个命令在此登记一行(精确名或"命令名+空格"前缀), 处理器只做状态变更与控制台渲染。
    /// 这些处理器原先是 Continued() 里 890 行的巨型 switch, 后按前缀聚成 9 个命令族,
    /// 现逐命令抽出, 由 CommandRegistry 统一登记与分发。
    /// </summary>
    internal partial class Program
    {
        private static bool _cliCommandsRegistered;
        private static readonly object _cliRegLock = new();

        /// <summary>
        /// 注册全部 CLI 命令(幂等, 首次分发时调用)。
        /// 加锁并在注册完成后才置位, 避免并发首次调用时拿到半填的注册表。
        /// </summary>
        internal static void EnsureCliCommandsRegistered()
        {
            lock (_cliRegLock)
            {
                if (_cliCommandsRegistered) return;
                RegisterCliCommandsCore();
                _cliCommandsRegistered = true;
            }
        }

        /// <summary>CLI 命令注册体(由 EnsureCliCommandsRegistered 在锁内调用)</summary>
        private static void RegisterCliCommandsCore()
        {

            CommandRegistry.RegisterCliCommand("rt", Cli_rt);
            CommandRegistry.RegisterCliCommand("rt help", Cli_rt_help);
            CommandRegistry.RegisterCliCommand("rt about", Cli_rt_about);
            CommandRegistry.RegisterCliCommand("rt end", Cli_rt_end);
            CommandRegistry.RegisterCliCommand("rt reload", Cli_rt_reload);
            CommandRegistry.RegisterCliCommand("rt status", Cli_rt_status);
            CommandRegistry.RegisterCliCommand("rt clients", Cli_rt_clients);
            CommandRegistry.RegisterCliCommand("rt clients kick", Cli_rt_clients_kick);
            CommandRegistry.RegisterCliPrefixCommand("rt clients kick", Cli_rt_clients_kick_2);
            CommandRegistry.RegisterCliCommand("rt extensions", Cli_rt_extensions);
            CommandRegistry.RegisterCliCommand("rt extensions load", Cli_rt_extensions_load);
            CommandRegistry.RegisterCliCommand("rt extensions unload", Cli_rt_extensions_unload);
            CommandRegistry.RegisterCliPrefixCommand("rt extensions load", Cli_rt_extensions_load_2);
            CommandRegistry.RegisterCliPrefixCommand("rt extensions unload", Cli_rt_extensions_unload_2);
            CommandRegistry.RegisterCliCommand("rt start", Cli_rt_start);
            CommandRegistry.RegisterCliCommand("rt stop", Cli_rt_stop);
            CommandRegistry.RegisterCliCommand(".help", Cli_help);
            CommandRegistry.RegisterCliCommand(".guide", Cli_guide);
            CommandRegistry.RegisterCliCommand(".auto", Cli_auto);
            CommandRegistry.RegisterCliPrefixCommand(".auto on", Cli_auto_on);
            CommandRegistry.RegisterCliCommand(".auto on", Cli_auto_on_2);
            CommandRegistry.RegisterCliPrefixCommand(".auto off", Cli_auto_off);
            CommandRegistry.RegisterCliCommand(".auto off", Cli_auto_off_2);
            CommandRegistry.RegisterCliCommand(".auto list", Cli_auto_list);
            CommandRegistry.RegisterCliCommand(".auto start", Cli_auto_start);
            CommandRegistry.RegisterCliCommand(".auto stop", Cli_auto_stop);
            CommandRegistry.RegisterCliCommand(".ai", Cli_ai);
            CommandRegistry.RegisterCliCommand(".ai start", Cli_ai_start);
            CommandRegistry.RegisterCliCommand(".ai stop", Cli_ai_stop);
            CommandRegistry.RegisterCliCommand(".ai list", Cli_ai_list);
            CommandRegistry.RegisterCliCommand(".ai reload", Cli_ai_reload);
            CommandRegistry.RegisterCliPrefixCommand(".ai run", Cli_ai_run);
            CommandRegistry.RegisterCliCommand(".ai run", Cli_ai_run_2);
            CommandRegistry.RegisterCliPrefixCommand(".ai clear", Cli_ai_clear);
            CommandRegistry.RegisterCliCommand(".ai clear", Cli_ai_clear_2);
            CommandRegistry.RegisterCliCommand(".fx", Cli_fx);
            CommandRegistry.RegisterCliCommand(".fx add", Cli_fx_add);
            CommandRegistry.RegisterCliPrefixCommand(".fx add", Cli_fx_add_2);
            CommandRegistry.RegisterCliCommand(".fx list", Cli_fx_list);
            CommandRegistry.RegisterCliCommand(".fx del", Cli_fx_del);
            CommandRegistry.RegisterCliCommand(".fx clientguide", Cli_fx_clientguide);
            CommandRegistry.RegisterCliCommand(".fx base", Cli_fx_base);
            CommandRegistry.RegisterCliCommand(".fx ai", Cli_fx_ai);
            CommandRegistry.RegisterCliCommand(".fx filter", Cli_fx_filter);
            CommandRegistry.RegisterCliCommand(".fx filter config", Cli_fx_filter_config);
            CommandRegistry.RegisterCliCommand(".fx filter plugin", Cli_fx_filter_plugin);
            CommandRegistry.RegisterCliPrefixCommand(".fx list", Cli_fx_list_2);
            CommandRegistry.RegisterCliPrefixCommand(".fx base", Cli_fx_base_2);
            CommandRegistry.RegisterCliPrefixCommand(".fx ai", Cli_fx_ai_2);
            CommandRegistry.RegisterCliPrefixCommand(".fx clientguide", Cli_fx_clientguide_2);
            CommandRegistry.RegisterCliPrefixCommand(".fx filter config", Cli_fx_filter_config_2);
            CommandRegistry.RegisterCliPrefixCommand(".fx filter plugin", Cli_fx_filter_plugin_2);
            CommandRegistry.RegisterCliCommand(".fx filter mod", Cli_fx_filter_mod);
            CommandRegistry.RegisterCliCommand(".fx genpattern", Cli_fx_genpattern);
            CommandRegistry.RegisterCliPrefixCommand(".fx filter mod", Cli_fx_filter_mod_2);
            CommandRegistry.RegisterCliCommand(".cfg", Cli_cfg);
            CommandRegistry.RegisterCliCommand(".lang", Cli_lang);
            CommandRegistry.RegisterCliPrefixCommand(".lang", Cli_lang_2);
            CommandRegistry.RegisterCliCommand(".cfg reload", Cli_cfg_reload);
            CommandRegistry.RegisterCliCommand(".cfg clear", Cli_cfg_clear);
            CommandRegistry.RegisterCliCommand(".status", Cli_status);
            CommandRegistry.RegisterCliCommand(".server", Cli_server);
            CommandRegistry.RegisterCliCommand(".server list", Cli_server_list);
            CommandRegistry.RegisterCliCommand(".server add", Cli_server_add);
            CommandRegistry.RegisterCliCommand(".server del", Cli_server_del);
            CommandRegistry.RegisterCliCommand(".server change", Cli_server_change);
            CommandRegistry.RegisterCliPrefixCommand(".server change", Cli_server_change_2);
            CommandRegistry.RegisterCliCommand(".server get", Cli_server_get);
            CommandRegistry.RegisterCliCommand(".server connect", Cli_server_connect);
            CommandRegistry.RegisterCliPrefixCommand(".server connect", Cli_server_connect_2);
            CommandRegistry.RegisterCliCommand(".server rcon", Cli_server_rcon);
            CommandRegistry.RegisterCliPrefixCommand(".server rcon", Cli_server_rcon_2);
            CommandRegistry.RegisterCliCommand(".server detach", Cli_server_detach);
            CommandRegistry.RegisterCliCommand(".server start", Cli_server_start);
            CommandRegistry.RegisterCliCommand(".server stop", Cli_server_stop);
            CommandRegistry.RegisterCliCommand(".server restart", Cli_server_restart);
            CommandRegistry.RegisterCliCommand(".server kill", Cli_server_kill);
            CommandRegistry.RegisterCliCommand(".server status", Cli_server_status);
            CommandRegistry.RegisterCliCommand(".group", Cli_group);
            CommandRegistry.RegisterCliPrefixCommand(".group add", Cli_group_add);
            CommandRegistry.RegisterCliPrefixCommand(".group del", Cli_group_del);
            CommandRegistry.RegisterCliCommand(".group list", Cli_group_list);
            CommandRegistry.RegisterCliPrefixCommand(".group set", Cli_group_set);
            CommandRegistry.RegisterCliPrefixCommand(".group unset", Cli_group_unset);
            CommandRegistry.RegisterCliCommand(".group build", Cli_group_build);
            CommandRegistry.RegisterCliCommand("ct", Cli_ct);
            CommandRegistry.RegisterCliCommand("ct list", Cli_ct_list);
            CommandRegistry.RegisterCliPrefixCommand("ct unpack", Cli_ct_unpack);
        }

        /// <summary>cmd == 'rt'</summary>
        private static CommandRegistry.CliOutcome Cli_rt(string cmd)
        {
            Output.Log(I18n.Get("prog_version_info", Markup.Escape(RtCliVersion), RtCliInformation), 1, ThisProgramName);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == 'rt help'</summary>
        private static CommandRegistry.CliOutcome Cli_rt_help(string cmd)
        {
            ShowHelp();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == 'rt about'</summary>
        private static CommandRegistry.CliOutcome Cli_rt_about(string cmd)
        {
            ShowAbout();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == 'rt end'</summary>
        private static CommandRegistry.CliOutcome Cli_rt_end(string cmd)
        {
            return CommandRegistry.CliOutcome.Exit;
        }

        /// <summary>cmd == 'rt reload'</summary>
        private static CommandRegistry.CliOutcome Cli_rt_reload(string cmd)
        {
            Output.Log(I18n.Get("main_reloading"), 1, ThisProgramName);
            Reload.Restart();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == 'rt status'</summary>
        private static CommandRegistry.CliOutcome Cli_rt_status(string cmd)
        {
            // 先收集行: 按"显示宽度"统一补齐标签列, 避免中文/动态内容把边框撑歪
            var rows = new List<(string Label, string Value)>
            {
                (I18n.Get("prog_status_manage_port"), Connector.IsRunning ? I18n.Get("prog_state_running") : I18n.Get("prog_state_not_running")),
                (I18n.Get("prog_status_connected_panels"), Connector.ConnectedClientCount.ToString()),
                (I18n.Get("prog_status_current_server"), $"[cyan]{Markup.Escape(Config.App.CurrentServer)}[/] ({Markup.Escape(Config.CurrentServer?.ServerName ?? string.Empty)})"),
                (I18n.Get("prog_status_mc_mode"), $"[cyan]{Analyzer.CurrentMode}[/]")
            };
            if (Analyzer.NeedsRunServer)
                rows.Add((I18n.Get("prog_status_mc_server"), Analyzer.IsRunModeActive ? I18n.Get("prog_state_running") : I18n.Get("prog_state_not_running")));
            else
                rows.Add((I18n.Get("prog_status_mc_connection"), Analyzer.IsAttached ? I18n.Get("prog_state_connected") : I18n.Get("prog_state_disconnected")));
            rows.Add((I18n.Get("prog_status_auto_tips"), Intelligence.IsTipsRunning ? I18n.Get("prog_state_running") : I18n.Get("prog_state_not_running_grey")));

            int labelWidth = rows.Max(r => Output.DisplayWidth(Output.StripMarkup(r.Label))) + 2;
            var rtStatusTable = new Table().Border(TableBorder.Rounded).Title(I18n.Get("prog_status_title"));
            rtStatusTable.AddColumn(I18n.Get("prog_col_item")).AddColumn(I18n.Get("prog_col_status"));
            foreach (var (label, value) in rows)
                rtStatusTable.AddRow(Output.PadDisplay(label, labelWidth), value);
            AnsiConsole.Write(rtStatusTable);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == 'rt clients'</summary>
        private static CommandRegistry.CliOutcome Cli_rt_clients(string cmd)
        {
            Output.Log(I18n.Get("prog_clients_list_header"), 1, ThisProgramName);
            if (Connector.ConnectedClientCount > 0)
            {
                var clientsTable = new Table().Border(TableBorder.Rounded);
                clientsTable.AddColumn(I18n.Get("prog_col_index")).AddColumn("ID").AddColumn("IP").AddColumn(I18n.Get("prog_col_connect_time"));
                int idx = 1;
                foreach (var kvp in Connector.ConnectedClients)
                {
                    clientsTable.AddRow($"[cyan]{idx}[/]", Markup.Escape(kvp.Key), Markup.Escape(kvp.Value.IP), kvp.Value.ConnectTime.ToString("HH:mm:ss"));
                    idx++;
                }
                AnsiConsole.Write(clientsTable);
                Output.Log(I18n.Get("prog_clients_kick_hint"), 1, ThisProgramName);
            }
            else
            {
                Output.Log(I18n.Get("prog_none"), 1, ThisProgramName);
            }
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == 'rt clients kick'</summary>
        private static CommandRegistry.CliOutcome Cli_rt_clients_kick(string cmd)
        {
            if (Connector.ConnectedClientCount == 0)
            {
                Output.Log(I18n.Get("prog_no_connected_panels"), 1, ThisProgramName);
            }
            else
            {
                Output.Log(I18n.Get("prog_kick_usage"), 1, ThisProgramName);
            }
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('rt clients kick ')</summary>
        private static CommandRegistry.CliOutcome Cli_rt_clients_kick_2(string cmd)
        {
            string kickArg = cmd.Substring(16).Trim();
            if (int.TryParse(kickArg, out int kickIndex) && kickIndex >= 1)
            {
                var clientList = Connector.ConnectedClients.ToList();
                if (kickIndex <= clientList.Count)
                {
                    var target = clientList[kickIndex - 1];
                    Connector.UnregisterClient(target.Key);
                    Output.Log(I18n.Get("prog_client_disconnected", target.Key, target.Value.IP), 1, ThisProgramName);
                }
                else
                {
                    Output.Log(I18n.Get("prog_index_out_of_range", clientList.Count), 2, ThisProgramName);
                }
            }
            else
            {
                Output.Log(I18n.Get("prog_invalid_index"), 2, ThisProgramName);
            }
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == 'rt extensions'</summary>
        private static CommandRegistry.CliOutcome Cli_rt_extensions(string cmd)
        {
            Output.Log(I18n.Get("prog_ext_list_header"), 1, ThisProgramName);
            RtExtensionManager.DisplayLoadedExtensions();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == 'rt extensions load'</summary>
        private static CommandRegistry.CliOutcome Cli_rt_extensions_load(string cmd)
        {
            Output.Log(I18n.Get("prog_ext_load_usage"), 1, ThisProgramName);
            Output.Log(I18n.Get("prog_ext_dir", RtExtensionManager.GetExtensionsDirectory()), 1, ThisProgramName);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == 'rt extensions unload'</summary>
        private static CommandRegistry.CliOutcome Cli_rt_extensions_unload(string cmd)
        {
            Output.Log(I18n.Get("prog_ext_unload_usage"), 1, ThisProgramName);
            Output.Log(I18n.Get("prog_ext_view_list"), 1, ThisProgramName);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('rt extensions load ')</summary>
        private static CommandRegistry.CliOutcome Cli_rt_extensions_load_2(string cmd)
        {
            string loadPath = cmd.Substring(18).Trim();
            if (!string.IsNullOrWhiteSpace(loadPath))
            {
                RtExtensionManager.LoadExtensionByKey(loadPath);
            }
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('rt extensions unload ')</summary>
        private static CommandRegistry.CliOutcome Cli_rt_extensions_unload_2(string cmd)
        {
            string unloadKey = cmd.Substring(20).Trim();
            if (!string.IsNullOrWhiteSpace(unloadKey))
            {
                RtExtensionManager.UnloadExtensionByKey(unloadKey);
            }
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == 'rt start'</summary>
        private static CommandRegistry.CliOutcome Cli_rt_start(string cmd)
        {
            Connector.StartServerAsync().GetAwaiter().GetResult();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == 'rt stop'</summary>
        private static CommandRegistry.CliOutcome Cli_rt_stop(string cmd)
        {
            Connector.StopServerAsync().GetAwaiter().GetResult();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.help'</summary>
        private static CommandRegistry.CliOutcome Cli_help(string cmd)
        {
            ShowExtensionCommands();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.guide'</summary>
        private static CommandRegistry.CliOutcome Cli_guide(string cmd)
        {
            Output.Log(I18n.Get("prog_guide_tutorial"), 1, ThisProgramName);
            Intelligence.Guide().GetAwaiter().GetResult();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>"/" 开头的 MC 命令转发(无"命令名+空格"结构, 由 DispatchCliCommand 单独接线)</summary>
        private static CommandRegistry.CliOutcome Cli_mcCommand(string cmd)
        {
            string mcCommand = cmd.Substring(1);
            if (string.IsNullOrWhiteSpace(mcCommand)) return CommandRegistry.CliOutcome.Unhandled;
            Analyzer.SendCommand(mcCommand);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.auto'</summary>
        private static CommandRegistry.CliOutcome Cli_auto(string cmd)
        {
            var autoRoot = new Tree(I18n.Get("prog_auto_tree_title"));
            autoRoot.AddNode(I18n.Get("prog_auto_on_desc"));
            autoRoot.AddNode(I18n.Get("prog_auto_off_desc"));
            autoRoot.AddNode(I18n.Get("prog_auto_list_desc"));
            autoRoot.AddNode(I18n.Get("prog_auto_start_desc"));
            autoRoot.AddNode(I18n.Get("prog_auto_stop_desc"));
            AnsiConsole.Write(autoRoot);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('.auto on ')</summary>
        private static CommandRegistry.CliOutcome Cli_auto_on(string cmd)
        {
            string onTaskName = cmd.Substring(".auto on ".Length).Trim();
            Scheduler.SetTaskEnabled(onTaskName, true);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.auto on'</summary>
        private static CommandRegistry.CliOutcome Cli_auto_on_2(string cmd)
        {
            Output.Log(I18n.Get("prog_auto_on_usage"), 1, ThisProgramName);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('.auto off ')</summary>
        private static CommandRegistry.CliOutcome Cli_auto_off(string cmd)
        {
            string offTaskName = cmd.Substring(".auto off ".Length).Trim();
            Scheduler.SetTaskEnabled(offTaskName, false);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.auto off'</summary>
        private static CommandRegistry.CliOutcome Cli_auto_off_2(string cmd)
        {
            Output.Log(I18n.Get("prog_auto_off_usage"), 1, ThisProgramName);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.auto list'</summary>
        private static CommandRegistry.CliOutcome Cli_auto_list(string cmd)
        {
            Scheduler.ListTasks();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.auto start'</summary>
        private static CommandRegistry.CliOutcome Cli_auto_start(string cmd)
        {
            Scheduler.Start();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.auto stop'</summary>
        private static CommandRegistry.CliOutcome Cli_auto_stop(string cmd)
        {
            Scheduler.Stop();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.ai'</summary>
        private static CommandRegistry.CliOutcome Cli_ai(string cmd)
        {
            var aiRoot = new Tree(I18n.Get("prog_ai_tree_title"));
            aiRoot.AddNode(I18n.Get("prog_ai_start_desc"));
            aiRoot.AddNode(I18n.Get("prog_ai_stop_desc"));
            aiRoot.AddNode(I18n.Get("prog_ai_list_desc"));
            aiRoot.AddNode(I18n.Get("prog_ai_reload_desc"));
            aiRoot.AddNode(I18n.Get("prog_ai_run_desc"));
            aiRoot.AddNode(I18n.Get("prog_ai_clear_task_desc"));
            aiRoot.AddNode(I18n.Get("prog_ai_clear_desc"));
            AnsiConsole.Write(aiRoot);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.ai start'</summary>
        private static CommandRegistry.CliOutcome Cli_ai_start(string cmd)
        {
            Intelligence.AiAutoRunner.Start();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.ai stop'</summary>
        private static CommandRegistry.CliOutcome Cli_ai_stop(string cmd)
        {
            Intelligence.AiAutoRunner.Stop();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.ai list'</summary>
        private static CommandRegistry.CliOutcome Cli_ai_list(string cmd)
        {
            Intelligence.AiAutoRunner.ListTasks();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.ai reload'</summary>
        private static CommandRegistry.CliOutcome Cli_ai_reload(string cmd)
        {
            Intelligence.AiAutoRunner.Reload();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('.ai run ')</summary>
        private static CommandRegistry.CliOutcome Cli_ai_run(string cmd)
        {
            string aiRunTaskName = cmd.Substring(".ai run ".Length).Trim();
            _ = Task.Run(async () => await Intelligence.AiAutoRunner.TriggerTaskAsync(aiRunTaskName));
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.ai run'</summary>
        private static CommandRegistry.CliOutcome Cli_ai_run_2(string cmd)
        {
            Output.Log(I18n.Get("prog_ai_run_usage"), 1, ThisProgramName);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('.ai clear ')</summary>
        private static CommandRegistry.CliOutcome Cli_ai_clear(string cmd)
        {
            string aiClearTaskName = cmd.Substring(".ai clear ".Length).Trim();
            if (string.IsNullOrWhiteSpace(aiClearTaskName) || aiClearTaskName.Equals("all", StringComparison.OrdinalIgnoreCase))
                Intelligence.AiAutoRunner.ClearAllContextCache();
            else
                Intelligence.AiAutoRunner.ClearContextCache(aiClearTaskName);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.ai clear'</summary>
        private static CommandRegistry.CliOutcome Cli_ai_clear_2(string cmd)
        {
            Intelligence.AiAutoRunner.ClearAllContextCache();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.fx'</summary>
        private static CommandRegistry.CliOutcome Cli_fx(string cmd)
        {
            var fxRoot = new Tree(I18n.Get("prog_fx_tree_title"));

            var addNode = fxRoot.AddNode(I18n.Get("prog_fx_add_desc"));
            addNode.AddNode(I18n.Get("prog_fx_add_usage"));
            addNode.AddNode(I18n.Get("prog_fx_add_auto_hint"));

            var listNode = fxRoot.AddNode(I18n.Get("prog_fx_list_desc"));
            listNode.AddNode(I18n.Get("prog_fx_list_all"));
            listNode.AddNode(I18n.Get("prog_fx_list_n"));

            fxRoot.AddNode(I18n.Get("prog_fx_del_desc"));

            var cgNode = fxRoot.AddNode(I18n.Get("prog_fx_cg_desc"));
            cgNode.AddNode(I18n.Get("prog_fx_cg_start"));
            cgNode.AddNode(I18n.Get("prog_fx_cg_n"));

            var baseNode = fxRoot.AddNode(I18n.Get("prog_fx_base_desc"));
            baseNode.AddNode(I18n.Get("prog_fx_base_n"));
            baseNode.AddNode(I18n.Get("prog_fx_base_range"));

            var filterNode = fxRoot.AddNode(I18n.Get("prog_fx_filter_desc"));
            var configNode = filterNode.AddNode(I18n.Get("prog_fx_filter_config_desc"));
            configNode.AddNode(I18n.Get("prog_fx_filter_config"));
            configNode.AddNode(I18n.Get("prog_fx_filter_config_n"));
            var pluginNode = filterNode.AddNode(I18n.Get("prog_fx_filter_plugin_desc"));
            pluginNode.AddNode(I18n.Get("prog_fx_filter_plugin"));
            pluginNode.AddNode(I18n.Get("prog_fx_filter_plugin_n"));
            var modNode = filterNode.AddNode(I18n.Get("prog_fx_filter_mod_desc"));
            modNode.AddNode(I18n.Get("prog_fx_filter_mod"));
            modNode.AddNode(I18n.Get("prog_fx_filter_mod_n"));

            var aiNode = fxRoot.AddNode(I18n.Get("prog_fx_ai_desc"));
            aiNode.AddNode(I18n.Get("prog_fx_ai_n"));
            aiNode.AddNode(I18n.Get("prog_fx_ai_range"));

            AnsiConsole.Write(fxRoot);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.fx add'</summary>
        private static CommandRegistry.CliOutcome Cli_fx_add(string cmd)
        {
            Output.Log(I18n.Get("prog_fx_add_usage_hint"), 1, ThisProgramName);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('.fx add ')</summary>
        private static CommandRegistry.CliOutcome Cli_fx_add_2(string cmd)
        {
            string addArg = cmd.Substring(".fx add ".Length).Trim().Trim('"');
            Analyzer.AddExternalLog(addArg);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.fx list'</summary>
        private static CommandRegistry.CliOutcome Cli_fx_list(string cmd)
        {
            Analyzer.ListErrors(null);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.fx del'</summary>
        private static CommandRegistry.CliOutcome Cli_fx_del(string cmd)
        {
            Analyzer.DeleteErrors();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.fx clientguide'</summary>
        private static CommandRegistry.CliOutcome Cli_fx_clientguide(string cmd)
        {
            Analyzer.ClientGuide(null);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.fx base'</summary>
        private static CommandRegistry.CliOutcome Cli_fx_base(string cmd)
        {
            Analyzer.BaseAnalyze(null);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.fx ai'</summary>
        private static CommandRegistry.CliOutcome Cli_fx_ai(string cmd)
        {
            Task.Run(() => Analyzer.AiAnalyze(null));
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.fx filter'</summary>
        private static CommandRegistry.CliOutcome Cli_fx_filter(string cmd)
        {
            Analyzer.FilterInfo();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.fx filter config'</summary>
        private static CommandRegistry.CliOutcome Cli_fx_filter_config(string cmd)
        {
            Analyzer.FilterConfig(null);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.fx filter plugin'</summary>
        private static CommandRegistry.CliOutcome Cli_fx_filter_plugin(string cmd)
        {
            Analyzer.FilterPlugin(null);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('.fx list ')</summary>
        private static CommandRegistry.CliOutcome Cli_fx_list_2(string cmd)
        {
            string listArg = cmd.Substring(".fx list ".Length).Trim();
            if (int.TryParse(listArg, out int listIndex))
            {
                Analyzer.ListErrors(listIndex);
            }
            else
            {
                Output.Log(I18n.Get("prog_fx_list_invalid"), 2, ThisProgramName);
            }
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('.fx base ')</summary>
        private static CommandRegistry.CliOutcome Cli_fx_base_2(string cmd)
        {
            string baseArg = cmd.Substring(".fx base ".Length).Trim();
            Analyzer.BaseAnalyze(baseArg);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('.fx ai ')</summary>
        private static CommandRegistry.CliOutcome Cli_fx_ai_2(string cmd)
        {
            string aiArg = cmd.Substring(".fx ai ".Length).Trim();
            Task.Run(() => Analyzer.AiAnalyze(aiArg));
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('.fx clientguide ')</summary>
        private static CommandRegistry.CliOutcome Cli_fx_clientguide_2(string cmd)
        {
            string cgArg = cmd.Substring(".fx clientguide ".Length).Trim();
            if (int.TryParse(cgArg, out int cgIndex))
            {
                Analyzer.ClientGuide(cgIndex);
            }
            else
            {
                Output.Log(I18n.Get("prog_fx_cg_invalid"), 2, ThisProgramName);
            }
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('.fx filter config ')</summary>
        private static CommandRegistry.CliOutcome Cli_fx_filter_config_2(string cmd)
        {
            string fcArg = cmd.Substring(".fx filter config ".Length).Trim();
            if (int.TryParse(fcArg, out int fcIndex))
            {
                Analyzer.FilterConfig(fcIndex);
            }
            else
            {
                Output.Log(I18n.Get("prog_fx_fc_invalid"), 2, ThisProgramName);
            }
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('.fx filter plugin ')</summary>
        private static CommandRegistry.CliOutcome Cli_fx_filter_plugin_2(string cmd)
        {
            string fpArg = cmd.Substring(".fx filter plugin ".Length).Trim();
            if (int.TryParse(fpArg, out int fpIndex))
            {
                Analyzer.FilterPlugin(fpIndex);
            }
            else
            {
                Output.Log(I18n.Get("prog_fx_fp_invalid"), 2, ThisProgramName);
            }
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.fx filter mod'</summary>
        private static CommandRegistry.CliOutcome Cli_fx_filter_mod(string cmd)
        {
            Analyzer.FilterMod(null);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('.fx filter mod ')</summary>
        private static CommandRegistry.CliOutcome Cli_fx_filter_mod_2(string cmd)
        {
            string fmArg = cmd.Substring(".fx filter mod ".Length).Trim();
            if (int.TryParse(fmArg, out int fmIndex))
            {
                Analyzer.FilterMod(fmIndex);
            }
            else
            {
                Output.Log(I18n.Get("prog_fx_fm_invalid"), 2, ThisProgramName);
            }
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.cfg'</summary>
        private static CommandRegistry.CliOutcome Cli_cfg(string cmd)
        {
            var cfgRoot = new Tree($"[cyan]{I18n.Get("cfg_title")}[/]");
            cfgRoot.AddNode($"[green]reload[/] - {I18n.Get("cfg_reload_desc")}");
            cfgRoot.AddNode($"[green]clear[/] - {I18n.Get("cfg_clear_desc")}");
            AnsiConsole.Write(cfgRoot);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.lang'</summary>
        private static CommandRegistry.CliOutcome Cli_lang(string cmd)
        {
            var langRoot = new Tree($"[cyan]{I18n.Get("lang_title")}[/]");
            langRoot.AddNode(I18n.Get("lang_current", I18n.CurrentLang));
            langRoot.AddNode(I18n.Get("lang_supported", string.Join(", ", I18n.SupportedLangs)));
            langRoot.AddNode(I18n.Get("lang_usage"));
            AnsiConsole.Write(langRoot);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('.lang ')</summary>
        private static CommandRegistry.CliOutcome Cli_lang_2(string cmd)
        {
            string langArg = cmd.Substring(".lang ".Length).Trim();
            if (I18n.SetLanguageAndSave(langArg))
                Output.Log(I18n.Get("lang_changed", langArg), 1, ThisProgramName);
            else
                Output.Log(I18n.Get("lang_invalid", langArg), 2, ThisProgramName);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.cfg reload'</summary>
        private static CommandRegistry.CliOutcome Cli_cfg_reload(string cmd)
        {
            Config.ReloadAll();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.cfg clear'</summary>
        private static CommandRegistry.CliOutcome Cli_cfg_clear(string cmd)
        {
            Config.ClearContent();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.fx genpattern' — 从 AI 记忆生成错误识别表达式并写入 custom_base_entries</summary>
        private static CommandRegistry.CliOutcome Cli_fx_genpattern(string cmd)
        {
            var (ok, message) = ErrorPatternGenerator.Generate();
            if (!ok) Output.Log(message, 2, ThisProgramName);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.status' — 集中状态总览(接口/服务端/自动化/扩展/版本)</summary>
        private static CommandRegistry.CliOutcome Cli_status(string cmd)
        {
            StatusReport.Print();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.server'</summary>
        private static CommandRegistry.CliOutcome Cli_server(string cmd)
        {
            Output.Log(I18n.Get("prog_server_current", Markup.Escape(Config.App.CurrentServer), Analyzer.CurrentMode), 1, ThisProgramName);
            Output.Log(I18n.Get("prog_server_subcommands"), 1, ThisProgramName);
            if (!Analyzer.NeedsRunServer)
                Output.Log(I18n.Get("prog_server_rcon_extra"), 1, ThisProgramName);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.server list'</summary>
        private static CommandRegistry.CliOutcome Cli_server_list(string cmd)
        {
            var serverListTable = new Table().Border(TableBorder.Rounded).Title(I18n.Get("prog_server_list_title"));
            serverListTable.AddColumn(I18n.Get("prog_col_key")).AddColumn(I18n.Get("prog_col_name")).AddColumn(I18n.Get("prog_col_mode")).AddColumn(I18n.Get("prog_col_workdir")).AddColumn(I18n.Get("prog_col_current"));
            foreach (var kvp in Config.App.ServerList)
            {
                string isCurrent = kvp.Key == Config.App.CurrentServer ? "[green]*[/]" : "";
                string workDir = string.IsNullOrWhiteSpace(kvp.Value.WorkPath) ? I18n.Get("prog_not_configured_grey") : Markup.Escape(kvp.Value.WorkPath);
                serverListTable.AddRow(
                    Markup.Escape(kvp.Key),
                    Markup.Escape(kvp.Value.ServerName),
                    Markup.Escape(kvp.Value.AnalyzerMode),
                    workDir,
                    isCurrent);
            }
            AnsiConsole.Write(serverListTable);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.server add'</summary>
        private static CommandRegistry.CliOutcome Cli_server_add(string cmd)
        {
            Output.Log(I18n.Get("prog_server_add_header"), 1, ThisProgramName);
            string? newKey = AnsiConsole.Ask<string>(I18n.Get("prog_server_add_key_prompt"));
            if (string.IsNullOrWhiteSpace(newKey))
            {
                Output.Log(I18n.Get("prog_server_key_empty"), 2, ThisProgramName);
            }
            else if (Config.App.ServerList.ContainsKey(newKey))
            {
                Output.Log(I18n.Get("prog_server_key_exists", Markup.Escape(newKey)), 2, ThisProgramName);
            }
            else
            {
                var newEntry = new ServerEntry();
                newEntry.ServerName = AnsiConsole.Ask(I18n.Get("prog_server_add_name_prompt"), newKey);
                string inputWorkPath = AnsiConsole.Ask(I18n.Get("prog_server_add_workdir_prompt"), "");
                if (!string.IsNullOrWhiteSpace(inputWorkPath))
                    newEntry.WorkPath = inputWorkPath;

                var modeSelect = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title(I18n.Get("prog_server_add_mode_title"))
                        .AddChoices(I18n.Get("prog_mode_rm_recommended"), "Run", "RR", "Rcon"));
                newEntry.AnalyzerMode = modeSelect.Split(' ')[0];

                if (newEntry.AnalyzerMode == "RM")
                {
                    Output.Log(I18n.Get("prog_server_rm_mode_info"), 1, ThisProgramName);
                }

                Config.App.ServerList[newKey] = newEntry;
                Config.SaveCurrentConfig();
                Output.Log(I18n.Get("prog_server_added", newKey), 1, ThisProgramName);
            }
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.server del'</summary>
        private static CommandRegistry.CliOutcome Cli_server_del(string cmd)
        {
            if (Config.App.ServerList.Count <= 1)
            {
                Output.Log(I18n.Get("prog_server_del_min"), 2, ThisProgramName);
            }
            else
            {
                string delTarget = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title(I18n.Get("prog_server_del_title"))
                        .AddChoices(Config.App.ServerList.Keys.ToList()));
                if (delTarget == Config.App.CurrentServer)
                {
                    Output.Log(I18n.Get("prog_server_del_current"), 2, ThisProgramName);
                }
                else
                {
                    bool confirmDel = AnsiConsole.Confirm(I18n.Get("prog_server_del_confirm", delTarget), false);
                    if (confirmDel)
                    {
                        Config.App.ServerList.Remove(delTarget);
                        Config.SaveCurrentConfig();
                        Output.Log(I18n.Get("prog_server_deleted", delTarget), 1, ThisProgramName);
                    }
                }
            }
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.server change'</summary>
        private static CommandRegistry.CliOutcome Cli_server_change(string cmd)
        {
            Output.Log(I18n.Get("prog_server_change_usage"), 1, ThisProgramName);
            Output.Log(I18n.Get("prog_server_configured"), 1, ThisProgramName);
            foreach (var kvp in Config.App.ServerList)
            {
                string marker = kvp.Key == Config.App.CurrentServer ? I18n.Get("prog_server_current_marker") : "";
                Output.Log(I18n.Get("prog_server_entry_line", Markup.Escape(kvp.Key), Markup.Escape(kvp.Value.ServerName), Markup.Escape(kvp.Value.AnalyzerMode), marker), 1, ThisProgramName);
            }
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('.server change ')</summary>
        private static CommandRegistry.CliOutcome Cli_server_change_2(string cmd)
        {
            string changeArg = cmd.Substring(".server change ".Length).Trim();
            if (string.IsNullOrWhiteSpace(changeArg))
            {
                Output.Log(I18n.Get("prog_server_change_empty"), 2, ThisProgramName);
            }
            else if (Config.SwitchServer(changeArg))
            {
                Analyzer.Initialize();
                Output.Log(I18n.Get("prog_server_switched",
                Markup.Escape(changeArg),
                Markup.Escape(Config.CurrentServer?.ServerName ?? string.Empty),
                Markup.Escape(Analyzer.CurrentMode)), 1, ThisProgramName);
            }
            else
            {
                Output.Log(I18n.Get("prog_server_not_found", Markup.Escape(changeArg)), 2, ThisProgramName);
                Output.Log(I18n.Get("prog_server_configured_keys", string.Join(", ", Config.App.ServerList.Keys)), 1, ThisProgramName);
            }
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.server get'</summary>
        private static CommandRegistry.CliOutcome Cli_server_get(string cmd)
        {
            if (Analyzer.NeedsRunServer)
                Output.Log(I18n.Get("prog_server_get_unsupported"), 2, ThisProgramName);
            else
                Analyzer.ScanAndListServers();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.server connect'</summary>
        private static CommandRegistry.CliOutcome Cli_server_connect(string cmd)
        {
            if (Analyzer.NeedsRunServer)
                Output.Log(I18n.Get("prog_server_connect_unsupported"), 2, ThisProgramName);
            else
                Output.Log(I18n.Get("prog_server_connect_usage"), 1, ThisProgramName);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('.server connect ')</summary>
        private static CommandRegistry.CliOutcome Cli_server_connect_2(string cmd)
        {
            if (Analyzer.NeedsRunServer)
            {
                Output.Log(I18n.Get("prog_server_connect_unsupported"), 2, ThisProgramName);
            }
            else
            {
                string connectArg = cmd.Substring(16).Trim();
                if (!string.IsNullOrWhiteSpace(connectArg))
                {
                    if (connectArg.StartsWith("pid:", StringComparison.OrdinalIgnoreCase))
                    {
                        string pidStr = connectArg.Substring(4);
                        if (int.TryParse(pidStr, out int pid))
                        {
                            Analyzer.ConnectToServerByPid(pid);
                        }
                        else
                        {
                            Output.Log(I18n.Get("prog_invalid_pid"), 2, ThisProgramName);
                        }
                    }
                    else if (int.TryParse(connectArg, out int index))
                    {
                        Analyzer.ConnectToServer(index);
                    }
                    else
                    {
                        Output.Log(I18n.Get("prog_server_connect_invalid_arg"), 2, ThisProgramName);
                    }
                }
                else
                {
                    Output.Log(I18n.Get("prog_server_connect_usage"), 1, ThisProgramName);
                }
            }
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.server rcon'</summary>
        private static CommandRegistry.CliOutcome Cli_server_rcon(string cmd)
        {
            Output.Log(I18n.Get("prog_server_rcon_usage"), 1, ThisProgramName);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('.server rcon ')</summary>
        private static CommandRegistry.CliOutcome Cli_server_rcon_2(string cmd)
        {
            string arg = cmd.Substring(".server rcon".Length).Trim();
            var parts = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                Output.Log(I18n.Get("prog_server_rcon_usage"), 1, ThisProgramName);
                return CommandRegistry.CliOutcome.Handled;
            }

            switch (parts[0].ToLowerInvariant())
            {
                case "connect":
                    if (parts.Length < 3)
                    {
                        Output.Log(I18n.Get("prog_server_rcon_usage"), 1, ThisProgramName);
                        break;
                    }
                    if (!int.TryParse(parts[2], out int port) || port <= 0 || port > 65535)
                    {
                        Output.Log(I18n.Get("prog_server_rcon_port_invalid", parts[2]), 2, ThisProgramName);
                        break;
                    }

                    string password = parts.Length >= 4
                        ? parts[3]
                        : (Config.App.ServerList.TryGetValue(Config.App.CurrentServer, out var entry)
                            ? entry.RconPassword
                            : "");

                    if (Analyzer.ConnectRemoteRcon(parts[1], port, password))
                        Output.Log(I18n.Get("prog_server_rcon_connected", parts[1], port), 1, ThisProgramName);
                    else
                        Output.Log(I18n.Get("prog_server_rcon_failed", parts[1], port), 2, ThisProgramName);
                    break;

                case "disconnect":
                case "stop":
                    Analyzer.Detach();
                    Output.Log(I18n.Get("prog_server_rcon_disconnected"), 1, ThisProgramName);
                    break;

                case "status":
                    if (Analyzer.IsRunning() && Analyzer.CurrentModeOf(null) == "RCON")
                        Output.Log(I18n.Get("prog_server_rcon_status_on",
                            Config.CurrentServer.RconHost, Config.CurrentServer.RconPort), 1, ThisProgramName);
                    else
                        Output.Log(I18n.Get("prog_server_rcon_status_off"), 1, ThisProgramName);
                    break;

                default:
                    Output.Log(I18n.Get("prog_server_rcon_usage"), 1, ThisProgramName);
                    break;
            }

            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.server detach'</summary>
        private static CommandRegistry.CliOutcome Cli_server_detach(string cmd)
        {
            if (Analyzer.NeedsRunServer)
                Output.Log(I18n.Get("prog_server_detach_unsupported"), 2, ThisProgramName);
            else
                Analyzer.Detach();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.server start'</summary>
        private static CommandRegistry.CliOutcome Cli_server_start(string cmd)
        {
            // RUN/RR/RM 启动本地子进程; RCON 模式直接连接实例配置中的远程 RCON
            Analyzer.StartServer();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.server stop'</summary>
        private static CommandRegistry.CliOutcome Cli_server_stop(string cmd)
        {
            // 交互路径: 无响应时在控制台确认是否强制关闭(不自动杀); RCON 模式下仅断开连接
            _ = Analyzer.StopServer(prompt: true, killOnTimeout: false);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.server restart'</summary>
        private static CommandRegistry.CliOutcome Cli_server_restart(string cmd)
        {
            if (!Analyzer.IsRunning())
            {
                Output.Log(I18n.Get("anz_server_not_running"), 2, ThisProgramName);
                return CommandRegistry.CliOutcome.Handled;
            }

            // 先等待进程结束(超时强杀), 再按当前实例模式重新启动
            Analyzer.StopServer(prompt: true, killOnTimeout: true);
            Analyzer.StartServer();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.server kill'</summary>
        private static CommandRegistry.CliOutcome Cli_server_kill(string cmd)
        {
            if (Analyzer.HasManagedProcess)
            {
                if (AnsiConsole.Confirm(I18n.Get("prog_kill_confirm"), false))
                {
                    Analyzer.ForceKillServer();
                    Output.Log(I18n.Get("prog_server_killed"), 1, ThisProgramName);
                }
            }
            else
                Output.Log(I18n.Get("prog_server_no_local_process"), 2, ThisProgramName);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.server status'</summary>
        private static CommandRegistry.CliOutcome Cli_server_status(string cmd)
        {
            var table = new Table();
            table.Border(TableBorder.Rounded);
            table.Title = new TableTitle(I18n.Get("prog_server_status_title"));
            table.AddColumn("");
            table.AddColumn(I18n.Get("prog_col_key"));
            table.AddColumn(I18n.Get("prog_col_name"));
            table.AddColumn(I18n.Get("prog_col_mode"));
            table.AddColumn(I18n.Get("prog_col_run_status"));
            table.AddColumn(I18n.Get("prog_col_auto_restart"));
            table.AddColumn(I18n.Get("prog_col_auto_backup"));
            table.AddColumn(I18n.Get("prog_col_workdir"));

            string currentKey = Config.App.CurrentServer;
            foreach (var kvp in Config.App.ServerList)
            {
                bool isCurrent = kvp.Key == currentKey;
                string mark = isCurrent ? "[green]*[/]" : "";

                string mode = Markup.Escape(kvp.Value.AnalyzerMode);

                // 每个实例独立显示运行状态(多实例可同时运行)
                string runStatus;
                if (Analyzer.IsRunning(kvp.Key))
                {
                    string realMode = Analyzer.CurrentModeOf(kvp.Key);
                    runStatus = (realMode == "RCON" || realMode == "RR")
                        ? I18n.Get("prog_state_connected")
                        : I18n.Get("prog_state_running");
                }
                else
                {
                    runStatus = I18n.Get("prog_state_not_running");
                }

                string autoRestart = kvp.Value.AutoRestart
                    ? I18n.Get("prog_yes_with_count", kvp.Value.AutoRestartMaxRetries)
                    : I18n.Get("prog_no_grey");

                bool inBackupList = Config.App.AutoBackupEnabled &&
                                    Config.App.AutoBackupServers.Contains(kvp.Key);
                string autoBackup = Config.App.AutoBackupEnabled
                    ? (inBackupList ? I18n.Get("prog_backup_added") : I18n.Get("prog_backup_not_added"))
                    : I18n.Get("prog_backup_global_off");

                string workPath = string.IsNullOrWhiteSpace(kvp.Value.WorkPath)
                    ? I18n.Get("prog_not_configured_yellow")
                    : Markup.Escape(kvp.Value.WorkPath);

                table.AddRow(mark, Markup.Escape(kvp.Key), Markup.Escape(kvp.Value.ServerName),
                              mode, runStatus, autoRestart, autoBackup, workPath);
            }

            AnsiConsole.Write(table);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.group'</summary>
        private static CommandRegistry.CliOutcome Cli_group(string cmd)
        {
            var groupRoot = new Tree(I18n.Get("prog_group_tree_title"));
            groupRoot.AddNode(I18n.Get("prog_group_add_desc"));
            groupRoot.AddNode(I18n.Get("prog_group_del_desc"));
            groupRoot.AddNode(I18n.Get("prog_group_list_desc"));
            groupRoot.AddNode(I18n.Get("prog_group_set_desc"));
            groupRoot.AddNode(I18n.Get("prog_group_unset_desc"));
            groupRoot.AddNode(I18n.Get("prog_group_build_desc"));
            AnsiConsole.Write(groupRoot);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('.group add ')</summary>
        private static CommandRegistry.CliOutcome Cli_group_add(string cmd)
        {
            string gName = cmd.Substring(".group add ".Length).Trim();
            var (gOk, gMsg, _) = ServerDataManager.CreateGroup(gName, new List<string>());
            Output.Log(gMsg, gOk ? 1 : 2, ThisProgramName);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('.group del ')</summary>
        private static CommandRegistry.CliOutcome Cli_group_del(string cmd)
        {
            string gId = cmd.Substring(".group del ".Length).Trim();
            var (gOk, gMsg) = ServerDataManager.DeleteGroup(gId);
            Output.Log(gMsg, gOk ? 1 : 2, ThisProgramName);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.group list'</summary>
        private static CommandRegistry.CliOutcome Cli_group_list(string cmd)
        {
            var groups = ServerDataManager.Data.Groups;
            if (groups.Count == 0)
            {
                Output.Log(I18n.Get("prog_group_empty"), 1, ThisProgramName);
                return CommandRegistry.CliOutcome.Handled;
            }
            var gTable = new Table().Border(TableBorder.Rounded).Title(I18n.Get("prog_group_list_title"));
            gTable.AddColumn(I18n.Get("prog_col_group_id")).AddColumn(I18n.Get("prog_col_name")).AddColumn(I18n.Get("prog_col_member_count")).AddColumn(I18n.Get("prog_col_members")).AddColumn(I18n.Get("prog_col_created_at"));
            foreach (var g in groups)
            {
                string members = g.MemberIds.Count > 0 ? string.Join(", ", g.MemberIds) : I18n.Get("prog_none_grey");
                gTable.AddRow(g.Id, Markup.Escape(g.Name), g.MemberIds.Count.ToString(), members, g.CreatedAt);
            }
            AnsiConsole.Write(gTable);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('.group set ')</summary>
        private static CommandRegistry.CliOutcome Cli_group_set(string cmd)
        {
            var parts = cmd.Substring(".group set ".Length).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                Output.Log(I18n.Get("prog_group_set_usage"), 2, ThisProgramName);
                return CommandRegistry.CliOutcome.Handled;
            }
            string sId = parts[0];
            string gId = parts[1];
            if (!Config.App.ServerList.ContainsKey(sId))
            {
                Output.Log(I18n.Get("prog_server_not_exist", sId), 2, ThisProgramName);
                return CommandRegistry.CliOutcome.Handled;
            }
            if (!ServerDataManager.Data.Groups.Any(g => g.Id == gId))
            {
                Output.Log(I18n.Get("prog_group_not_exist", gId), 2, ThisProgramName);
                return CommandRegistry.CliOutcome.Handled;
            }
            var (sOk, sMsg) = ServerDataManager.UpdateGroup("add", gId, null, new List<string> { sId });
            Output.Log(sMsg, sOk ? 1 : 2, ThisProgramName);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('.group unset ')</summary>
        private static CommandRegistry.CliOutcome Cli_group_unset(string cmd)
        {
            string sId = cmd.Substring(".group unset ".Length).Trim();
            if (!Config.App.ServerList.ContainsKey(sId))
            {
                Output.Log(I18n.Get("prog_server_not_exist", sId), 2, ThisProgramName);
                return CommandRegistry.CliOutcome.Handled;
            }
            var inst = ServerDataManager.Data.Instances.FirstOrDefault(i => i.Id == sId);
            if (inst == null || string.IsNullOrEmpty(inst.GroupId))
            {
                Output.Log(I18n.Get("prog_server_no_group", sId), 2, ThisProgramName);
                return CommandRegistry.CliOutcome.Handled;
            }
            string oldGroupId = inst.GroupId;
            var (uOk, uMsg) = ServerDataManager.UpdateGroup("remove", oldGroupId, null, new List<string> { sId });
            Output.Log(uMsg, uOk ? 1 : 2, ThisProgramName);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == '.group build'</summary>
        private static CommandRegistry.CliOutcome Cli_group_build(string cmd)
        {
            Hub.GroupBuildWizard().GetAwaiter().GetResult();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == 'ct'</summary>
        private static CommandRegistry.CliOutcome Cli_ct(string cmd)
        {
            Output.Log(I18n.Get("prog_ct_desc"), 1, ThisProgramName);
            Output.Log(I18n.Get("prog_ct_list_desc"), 1, ThisProgramName);
            Output.Log(I18n.Get("prog_ct_unpack_desc"), 1, ThisProgramName);
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd == 'ct list'</summary>
        private static CommandRegistry.CliOutcome Cli_ct_list(string cmd)
        {
            ContentManager.ListEmbeddedResources();
            return CommandRegistry.CliOutcome.Handled;
        }

        /// <summary>cmd.StartsWith('ct unpack ')</summary>
        private static CommandRegistry.CliOutcome Cli_ct_unpack(string cmd)
        {
            string resourceName = cmd.Substring("ct unpack ".Length).Trim();
            ContentManager.UnpackEmbeddedResource(resourceName);
            return CommandRegistry.CliOutcome.Handled;
        }
    }
}
