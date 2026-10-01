using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;
using RtCli.Modules.Extension;
using RtCli.Modules.Unit;
using Spectre.Console;

namespace RtCli.Modules.Function
{
    /// <summary>
    /// .status 的集中状态总览。
    /// 分五块: 程序概况 / 外部接口(含并发短超时探测) / 游戏服务端 / 自动化 / 扩展与脚本。
    /// 每项一行只给简略结论; 探测失败不抛异常, 以状态文字呈现。
    ///
    /// 对齐策略: 先收集全部行, 按"显示宽度"(中文/全角/emoji 计 2 单元格)计算统一的
    /// 标签列与值列宽度, 再用空格补齐后统一渲染。这样各分区表格的边框能完全对齐,
    /// 且动态内容(服务端名、路径、错误文本等)不会把某一行的边框撑歪。
    /// </summary>
    internal static class StatusReport
    {
        /// <summary>外部接口探测超时: 状态命令不应长时间卡住</summary>
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

        /// <summary>分区: 标题 + 键值行</summary>
        private sealed class Section
        {
            public string Title { get; }
            public List<(string Label, string Value)> Rows { get; } = new List<(string, string)>();

            public Section(string title) => Title = title;
        }

        public static void Print()
        {
            var sections = new List<Section>
            {
                BuildOverview(),
                BuildExternalInterfaces(),
                BuildGameServer(),
                BuildAutomation(),
                BuildExtensionsAndScripts()
            };

            // 统一列宽: 取所有分区中最宽的标签/值(按终端显示宽度而非字符数)
            int labelWidth = 8;
            int valueWidth = 16;
            foreach (var s in sections)
            {
                foreach (var (label, value) in s.Rows)
                {
                    labelWidth = Math.Max(labelWidth, Output.DisplayWidth(Output.StripMarkup(label)));
                    valueWidth = Math.Max(valueWidth, Output.DisplayWidth(Output.StripMarkup(value)));
                }
            }
            labelWidth += 2; // 留出一点呼吸空间

            foreach (var s in sections)
            {
                var t = new Table().Border(TableBorder.Rounded).Title($"[yellow]{s.Title}[/]");
                t.AddColumn("项目");
                t.AddColumn("状态");
                foreach (var (label, value) in s.Rows)
                    t.AddRow(Output.PadDisplay(label, labelWidth), Output.PadDisplay(value, valueWidth));
                AnsiConsole.Write(t);
            }

            AnsiConsole.Write(new Rule().RuleStyle("dodgerblue1"));
        }

        // ================= 各分区 =================

        private static Section BuildOverview()
        {
            var s = new Section("程序概况");
            s.Rows.Add(("版本", $"[white]{Markup.Escape(Program.RtCliVersion)}[/]"));
            s.Rows.Add(("运行时长", $"[white]{FormatUptime(Uptime())}[/]"));
            s.Rows.Add(("环境", $"[white]{Markup.Escape(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription)}[/]"));
            s.Rows.Add(("系统", $"[white]{Markup.Escape(System.Runtime.InteropServices.RuntimeInformation.OSDescription)}[/]"));
            s.Rows.Add(("当前服务端",
                $"[cyan]{Markup.Escape(Config.App.CurrentServer)}[/] ({Markup.Escape(Config.CurrentServer?.ServerName ?? "-")})"));
            s.Rows.Add(("已配置服务端", $"[white]{Config.App.ServerList.Count}[/] 个"));
            return s;
        }

        private static Section BuildExternalInterfaces()
        {
            string? consoleEndpoint = ContentManager.Ai?.Console_Error?.ApiEndpoint;
            string? autoEndpoint = ContentManager.Ai?.ServerAutoAi?.ApiEndpoint;
            bool sameAiEndpoint = string.Equals(autoEndpoint, consoleEndpoint, StringComparison.OrdinalIgnoreCase);
            var lp = Support.Settings.LuckPerms;

            // 所有外部探测并发执行: 全部不可达时总耗时仍约等于单次超时, 而不是逐项累加
            Task<Checker.UpdateStatus> updateTask = Task.Run(() => Checker.QueryUpdateStatus(ProbeTimeout));
            Task<ProbeResult> aiConsoleTask = Task.Run(() => ProbeHttp(consoleEndpoint));
            Task<ProbeResult>? aiAutoTask = sameAiEndpoint ? null : Task.Run(() => ProbeHttp(autoEndpoint));
            Task<ProbeResult>? mysqlTask = lp.Enabled
                ? Task.Run(() => ProbeMysql(Support.BuildConnectionString(lp.Mysql)))
                : null;

            var pending = new List<Task> { updateTask, aiConsoleTask };
            if (aiAutoTask != null) pending.Add(aiAutoTask);
            if (mysqlTask != null) pending.Add(mysqlTask);
            try { Task.WaitAll(pending.ToArray()); } catch (AggregateException) { /* 各探测内部已吞掉异常 */ }

            var s = new Section("外部接口");

            // 版本更新(GitHub releases API)
            var up = updateTask.IsCompletedSuccessfully
                ? updateTask.Result
                : new Checker.UpdateStatus(false, false, null, null, "探测未完成");
            string updateText;
            if (!up.Ok) updateText = Bad("检查失败 ・ " + (up.Error ?? "未知错误"));
            else if (up.HasUpdate) updateText = Warn($"发现新版本 {up.LatestDate} (当前 {Program.RtCliVersion})");
            else updateText = Ok("已是最新");
            s.Rows.Add(("版本更新 (GitHub)", updateText));

            // AI 推断接口: 控制台错误分析 / 服务端自动管理两处端点可能不同
            s.Rows.Add(("AI 接口 ・ 错误分析", DescribeProbe(aiConsoleTask, consoleEndpoint)));
            if (aiAutoTask != null)
                s.Rows.Add(("AI 接口 ・ 自动管理", DescribeProbe(aiAutoTask, autoEndpoint)));

            // LuckPerms MySQL
            if (!lp.Enabled)
                s.Rows.Add(("LuckPerms MySQL", Dim($"未启用 ({lp.Mysql.Address})")));
            else
                s.Rows.Add(("LuckPerms MySQL", DescribeMysql(mysqlTask, lp.Mysql)));

            // 本地 IPC / 对外监听(无网络探测, 直接读状态)
            s.Rows.Add(("面板 gRPC", Connector.IsRunning
                ? Ok($"监听 :{Connector.ServerPort}") + Dim($" ・ 已连接 {Connector.ConnectedClientCount} 个")
                : Bad("未监听")));
            s.Rows.Add(("Bridge REST", Bridge.IsRunning ? Ok($"监听 :{Bridge.ApiPort}") : Dim("未启动")));
            s.Rows.Add(("RM 管理协议", Analyzer.IsManagementConnected ? Ok("已连接") : Dim("未连接")));

            return s;
        }

        private static Section BuildGameServer()
        {
            var s = new Section("游戏服务端");
            s.Rows.Add(("控制模式", $"[cyan]{Markup.Escape(Analyzer.CurrentMode)}[/]"));
            s.Rows.Add(("活动实例", string.IsNullOrEmpty(Analyzer.ActiveInstanceId)
                ? Dim("-")
                : $"[white]{Markup.Escape(Analyzer.ActiveInstanceId)}[/]"));

            if (Analyzer.NeedsRunServer)
                s.Rows.Add(("服务端进程", Analyzer.IsRunModeActive ? Ok("运行中") : Bad("未运行")));
            else
                s.Rows.Add(("服务端进程", Dim("外部进程托管(非 Run/RR/RM 模式)")));

            s.Rows.Add(("服务端连接", Analyzer.IsAttached ? Ok("已连接") : Dim("未连接")));
            return s;
        }

        private static Section BuildAutomation()
        {
            var s = new Section("自动化");

            bool aiRunning = Intelligence.AiAutoRunner.IsRunning;
            int aiTasks = ContentManager.Ai?.ServerAutoAi?.Tasks?.Count ?? 0;
            s.Rows.Add(("AI 自动化", aiRunning ? Ok($"运行中 ・ 任务 {aiTasks}") : Dim($"已停止 ・ 任务 {aiTasks}")));

            int planTasks = Scheduler.Settings.Tasks.Count;
            s.Rows.Add(("计划任务", Scheduler.IsRunning ? Ok($"运行中 ・ 任务 {planTasks}") : Dim($"已停止 ・ 任务 {planTasks}")));

            s.Rows.Add(("LuckPerms 监测", Support.IsRunning ? Ok("运行中") : Dim("已停止")));
            s.Rows.Add(("控制台提示", Intelligence.IsTipsRunning ? Ok("运行中") : Dim("已停止")));

            return s;
        }

        private static Section BuildExtensionsAndScripts()
        {
            var s = new Section("扩展与脚本");

            var extensions = RtExtensionManager.LoadedExtensions;
            string names = extensions.Count == 0
                ? Dim("-")
                : Dim(" ・ " + string.Join(", ", extensions.Keys.Take(6)) + (extensions.Count > 6 ? " ..." : ""));
            s.Rows.Add(("已加载扩展", $"[white]{extensions.Count}[/] 个{names}"));

            s.Rows.Add(("脚本", $"[white]{Scripts.GetSettings().Scripts.Count}[/] 个"));
            s.Rows.Add(("扩展目录", Dim(ShortenPath(RtExtensionManager.GetExtensionsDirectory()))));

            return s;
        }

        // ================= 探测结果呈现 =================

        private readonly record struct ProbeResult(bool Ok, string Detail);

        private static string DescribeProbe(Task<ProbeResult> task, string? endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return Dim("未配置");
            if (!task.IsCompletedSuccessfully) return Bad("不可达 ・ 探测未完成");
            var r = task.Result;
            return r.Ok ? Ok("可达 ・ " + r.Detail) : Bad("不可达 ・ " + r.Detail);
        }

        private static string DescribeMysql(Task<ProbeResult>? task, LuckPermsMysqlConfig mysql)
        {
            if (task == null || !task.IsCompletedSuccessfully) return Bad($"不可达 ・ {mysql.Address}");
            var r = task.Result;
            return r.Ok
                ? Ok($"已连接 ・ {mysql.Address}/{mysql.Database}")
                : Bad($"不可达 ・ {r.Detail}");
        }

        // ================= 工具 =================

        private static string Ok(string s) => $"[green]{Markup.Escape(s)}[/]";
        private static string Warn(string s) => $"[yellow]{Markup.Escape(s)}[/]";
        private static string Bad(string s) => $"[red]{Markup.Escape(s)}[/]";
        private static string Dim(string s) => $"[grey]{Markup.Escape(s)}[/]";

        private static TimeSpan Uptime()
        {
            try { return DateTime.Now - Process.GetCurrentProcess().StartTime; }
            catch { return TimeSpan.Zero; }
        }

        private static string FormatUptime(TimeSpan span)
        {
            if (span <= TimeSpan.Zero) return "未知";
            if (span.TotalMinutes < 1) return $"{span.Seconds} 秒";
            if (span.TotalHours < 1) return $"{span.Minutes} 分 {span.Seconds} 秒";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
            return $"{(int)span.TotalDays} 天 {span.Hours} 小时";
        }

        /// <summary>路径过长会撑宽表格: 只保留末两段, 前面用 … 省略</summary>
        private static string ShortenPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "-";
            var segments = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length <= 2) return path;
            string sep = System.IO.Path.DirectorySeparatorChar.ToString();
            return "..." + sep + segments[^2] + sep + segments[^1];
        }

        /// <summary>
        /// 探测 HTTP 端点可达性: 任意 HTTP 响应(含 401/404)都说明端点在线,
        /// 因此只发 HEAD, 不调用业务接口, 不消耗额度。
        /// </summary>
        private static ProbeResult ProbeHttp(string? endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return new ProbeResult(false, "未配置");
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return new ProbeResult(false, "地址无效");

            try
            {
                using var http = new HttpClient { Timeout = ProbeTimeout };
                using var request = new HttpRequestMessage(HttpMethod.Head, uri);
                using var response = http.Send(request);
                return new ProbeResult(true, $"HTTP {(int)response.StatusCode}");
            }
            catch (TaskCanceledException)
            {
                return new ProbeResult(false, $"超时 ({ProbeTimeout.TotalSeconds:0}s)");
            }
            catch (Exception ex)
            {
                return new ProbeResult(false, Trim(ex.Message));
            }
        }

        /// <summary>探测 MySQL 连通性(仅建立连接, 不执行查询)</summary>
        private static ProbeResult ProbeMysql(string connectionString)
        {
            try
            {
                using var conn = new MySqlConnection(connectionString);
                using var cts = new CancellationTokenSource(ProbeTimeout);
                conn.OpenAsync(cts.Token).GetAwaiter().GetResult();
                return new ProbeResult(true, "OK");
            }
            catch (OperationCanceledException)
            {
                return new ProbeResult(false, $"超时 ({ProbeTimeout.TotalSeconds:0}s)");
            }
            catch (Exception ex)
            {
                return new ProbeResult(false, Trim(ex.Message));
            }
        }

        private static string Trim(string? s, int max = 60)
        {
            if (string.IsNullOrEmpty(s)) return "未知错误";
            s = s.Replace('\r', ' ').Replace('\n', ' ');
            return s.Length <= max ? s : s[..max] + "...";
        }
    }
}
