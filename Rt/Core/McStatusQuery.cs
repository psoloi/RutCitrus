using System;
using System.Linq;
using System.Text.Json;
using RtCli.Modules;
using Spectre.Console;
using Rt.Common;

namespace Rt.Core
{
    /// <summary>
    /// Minecraft服务器状态查询主类
    /// 实现 rte get 命令，遵循MC服务器Ping协议(Server List Ping)
    /// </summary>
    public static class McStatusQuery
    {
        private const string Name = "McStatus";

        /// <summary>
        /// 执行状态查询
        /// </summary>
        /// <param name="args">命令参数，可为空或 host:port / host port</param>
        public static void Execute(string[] args)
        {
            string host;
            int port;

            var cfg = RtConfig.Current.StatusQuery;

            if (args == null || args.Length == 0)
            {
                host = cfg.DefaultHost;
                port = cfg.DefaultPort;
                Output.Log($"使用配置默认地址: {host}:{port}", 1, Name);
            }
            else if (!TryParseAddress(args, out host, out port))
            {
                Output.Log("[red]地址格式无效[/]。用法: rte get [[host:port]] 或 rte get host port", 2, Name);
                return;
            }

            QueryAndDisplay(host, port, cfg.TimeoutSeconds);
        }

        /// <summary>解析地址参数，支持 host:port / host port / host(默认端口)</summary>
        private static bool TryParseAddress(string[] args, out string host, out int port)
        {
            host = "";
            port = RtConfig.Current.StatusQuery.DefaultPort;

            try
            {
                if (args.Length == 1)
                {
                    string arg = args[0].Trim();
                    int colonIdx = arg.LastIndexOf(':');
                    // 处理IPv6 [::1]:25565 形式
                    if (arg.StartsWith("["))
                    {
                        int closeBracket = arg.IndexOf(']');
                        if (closeBracket < 0) return false;
                        host = arg.Substring(1, closeBracket - 1);
                        if (closeBracket + 1 < arg.Length && arg[closeBracket + 1] == ':')
                        {
                            if (!int.TryParse(arg.Substring(closeBracket + 2), out port)) return false;
                        }
                        return true;
                    }
                    if (colonIdx > 0)
                    {
                        host = arg.Substring(0, colonIdx);
                        if (!int.TryParse(arg.Substring(colonIdx + 1), out port)) return false;
                    }
                    else
                    {
                        host = arg;
                    }
                    return true;
                }
                else if (args.Length >= 2)
                {
                    host = args[0].Trim();
                    return int.TryParse(args[1].Trim(), out port);
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        private static void QueryAndDisplay(string host, int port, int timeoutSeconds)
        {
            Output.Log($"[green]正在查询[/] {Markup.Escape(host)}:{port} ...", 1, Name);

            try
            {
                var (status, latency) = McPingProtocol.Query(host, port, timeoutSeconds);
                DisplayResult(host, port, status, latency);
            }
            catch (TimeoutException)
            {
                Output.Log($"[red]查询超时[/]: 服务器在 {timeoutSeconds} 秒内未响应 ({host}:{port})", 3, Name);
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                Output.Log($"[red]连接失败[/]: {Markup.Escape(ex.Message)} ({host}:{port})", 3, Name);
            }
            catch (Exception ex)
            {
                Output.Log($"[red]查询失败[/]: {Markup.Escape(ex.Message)}", 3, Name);
            }
        }

        private static void DisplayResult(string host, int port, McStatusResponse status, int latency)
        {
            var table = new Table()
                .Border(TableBorder.Rounded)
                .Title($"[green]服务器状态: {Markup.Escape(host)}:{port}[/]");
            table.AddColumn("项目");
            table.AddColumn("值");

            // 版本
            string versionName = Markup.Escape(status.Version?.Name ?? "未知");
            int protocol = status.Version?.Protocol ?? 0;
            table.AddRow("[cyan]版本[/]", $"{versionName} [grey](协议 {protocol})[/]");

            // 在线人数
            int online = status.Players?.Online ?? 0;
            int max = status.Players?.Max ?? 0;
            string playerStr = online >= max && max > 0
                ? $"[red]{online}/{max}[/] [grey](已满)[/]"
                : $"[green]{online}/{max}[/]";
            table.AddRow("[cyan]玩家[/]", playerStr);

            // 在线玩家列表
            if (status.Players?.Sample != null && status.Players.Sample.Length > 0)
            {
                var names = status.Players.Sample.Select(p => Markup.Escape(p.Name ?? "?"));
                string list = string.Join(", ", names);
                table.AddRow("[cyan]在线列表[/]", list);
            }

            // MOTD
            string motd = status.GetDescriptionText();
            if (!string.IsNullOrEmpty(motd))
            {
                table.AddRow("[cyan]MOTD[/]", Markup.Escape(motd));
            }

            // 延迟
            string latencyStr = latency < 50
                ? $"[green]{latency} ms[/]"
                : latency < 150
                    ? $"[yellow]{latency} ms[/]"
                    : $"[red]{latency} ms[/]";
            table.AddRow("[cyan]延迟[/]", latencyStr);

            AnsiConsole.Write(table);
        }
    }
}
