using System.Collections.Generic;

namespace RtCli.Modules.Function
{
    /// <summary>
    /// 命令帮助目录单一来源: CLI(.help 表格)与面板命令(Backend 帮助表格/纯文本广播)共用一份条目,
    /// 新增命令只需在此登记。Description 含 Spectre markup, 纯文本场景用 GetPlainTextEntries()。
    /// </summary>
    internal static class CommandHelp
    {
        public record Entry(string Command, string Description, bool Emphasized = false);

        private static List<Entry>? _entries;

        /// <summary>全部命令条目(含 markup)</summary>
        public static IReadOnlyList<Entry> Entries
        {
            get
            {
                if (_entries == null) Build();
                return _entries!;
            }
        }

        private static void Build()
        {
            _entries = new List<Entry>
            {
                new("[green]rt[/]", "显示版本信息", true),
                new("[green]rt help[/]", "显示此列表", true),
                new("[green]rt about[/]", "显示程序信息", true),
                new("[white]rt end[/]", "关闭程序"),
                new("[white]rt reload[/]", "重新加载"),
                new("[white]rt status[/]", "查看RtCli运行状态"),
                new("[white]rt clients[/]", "已连接面板列表"),
                new("[white]rt clients kick <序号>[/]", "断开指定客户端连接"),
                new("[white]rt extensions[/]", "已加载的扩展列表"),
                new("[white]rt extensions load <路径>[/]", "加载指定扩展"),
                new("[white]rt extensions unload <Key>[/]", "卸载指定扩展"),
                new("[white]rt start[/]", "启动管理端口"),
                new("[white]rt stop[/]", "关闭管理端口"),
                new("[green].help[/]", "显示扩展命令列表", true),
                new("[white].guide[/]", "MC服务端安装引导"),
                new("[white].auto[/]", "调度器 (输入 .auto 查看子命令)"),
                new("[white].auto on <任务名>[/]", "启用指定计划任务"),
                new("[white].auto off <任务名>[/]", "禁用指定计划任务"),
                new("[white].auto list[/]", "列出所有计划任务"),
                new("[white].auto start[/]", "启动调度器"),
                new("[white].auto stop[/]", "停止调度器"),
                new("[white].ai[/]", "AI自动化管理 (输入 .ai 查看子命令)"),
                new("[white].ai start[/]", "启动AI自动化(自动启动MC服务端)"),
                new("[white].ai stop[/]", "停止AI自动化"),
                new("[white].ai list[/]", "列出所有AI任务及运行状态"),
                new("[white].ai reload[/]", "重载AI配置并重启任务"),
                new("[white].ai run <任务名>[/]", "手动触发指定任务"),
                new("[white].ai clear[/]", "清除所有任务上下文缓存"),
                new("[white].fx[/]", "错误分析/客户端诊断 (输入 .fx 查看子命令)"),
                new("[white].fx get[/]", "获取MC服务端错误日志"),
                new("[white].fx list[/]", "列出所有错误分析结果"),
                new("[white].fx list <n>[/]", "列出第n次的分析结果"),
                new("[white].fx del[/]", "删除所有错误分析结果"),
                new("[white].fx clientguide[/]", "客户端连接问题诊断"),
                new("[white].fx clientguide <n>[/]", "查看第n个匹配项的详细解决方案"),
                new("[white].fx base[/]", "基础错误分析"),
                new("[white].fx base <n>[/]", "分析第n次的基础错误"),
                new("[white].fx ai[/]", "AI 错误分析"),
                new("[white].fx ai <n>[/]", "AI 分析第n次的错误"),
                new("[white].fx filter[/]", "过滤问题备份工具"),
                new("[white].fx filter config[/]", "备份/对照/还原配置文件"),
                new("[white].fx filter config <n>[/]", "还原第n个差异文件(0删除备份)"),
                new("[white].fx filter plugin[/]", "列出/禁用/启用插件"),
                new("[white].fx filter plugin <n>[/]", "切换第n个插件启用/禁用(0重启)"),
                new("[white].fx filter mod[/]", "列出/禁用/启用模组"),
                new("[white].fx filter mod <n>[/]", "切换第n个模组启用/禁用(0重启)"),
                new("[white].fx genpattern[/]", "从AI记忆生成错误识别表达式, 去重后写入 custom_base_entries"),
                new("[white].cfg[/]", "配置管理 (输入 .cfg 查看子命令)"),
                new("[white].cfg reload[/]", "热重载所有配置文件"),
                new("[white].cfg clear[/]", "删除Content文件夹并冷重载"),
                new("[white].lang[/]", Unit.I18n.Get("lang_usage")),
                new("[white].status[/]", "全局状态总览(外部接口/服务端/自动化/扩展/版本更新)"),
                new("[white].server[/]", "MC控制台相关命令 (输入 .server 查看子命令)"),
                new("[white].server list[/]", "列出所有已配置的MC服务端"),
                new("[white].server add[/]", "添加新的MC服务端配置"),
                new("[white].server del[/]", "删除MC服务端配置"),
                new("[white].server change <标识>[/]", "切换当前MC服务端"),
                new("[white].server get[/]", "[[[DarkOrange]Rcon[/]]] 扫描并列出运行中的MC服务端"),
                new("[white].server connect <序号|pid:进程ID>[/]", "[[[DarkOrange]Rcon[/]]] 连接到指定的MC服务端"),
                new("[white].server rcon connect <host> <port> <密码>[/]", "[[[DarkOrange]Rcon[/]]] 远程连接RCON服务端(无需本机进程)"),
                new("[white].server rcon disconnect[/]", "[[[DarkOrange]Rcon[/]]] 断开远程RCON连接"),
                new("[white].server rcon status[/]", "[[[DarkOrange]Rcon[/]]] 查看远程RCON连接状态"),
                new("[white].server detach[/]", "[[[DarkOrange]Rcon[/]]] 断开与MC服务端的连接"),
                new("[white].server start[/]", "[[[green]Run/RR/RM[/]]] 启动MC服务端作为子进程(RCON模式则连接远程RCON)"),
                new("[white].server stop[/]", "[[[green]Run/RR/RM[/]]] 停止MC服务端(RCON模式仅断开连接)"),
                new("[white].server restart[/]", "[[[green]Run/RR/RM[/]]] 重启MC服务端"),
                new("[white].server status[/]", "查看MC服务端连接/运行状态"),
                new("[white].group[/]", "群组服务器管理 (输入 .group 查看子命令)"),
                new("[white].group add <名称>[/]", "创建群组"),
                new("[white].group del <群组ID>[/]", "删除群组"),
                new("[white].group list[/]", "列出所有群组信息"),
                new("[white].group set <服务器标识> <群组ID>[/]", "设置服务器到某群组"),
                new("[white].group unset <服务器标识>[/]", "解除服务器群组设置"),
                new("[white].group build[/]", "自动构建群组向导"),
                new("[green]/<命令>[/]", "发送命令到MC服务端", true),
            };
        }

        /// <summary>纯文本条目(去 markup), 供面板广播等无渲染场景</summary>
        public static IReadOnlyList<(string Command, string Description)> GetPlainTextEntries()
        {
            var list = new List<(string, string)>(Entries.Count);
            foreach (var e in Entries)
                list.Add((Output.StripMarkup(e.Command), Output.StripMarkup(e.Description)));
            return list;
        }
    }
}
