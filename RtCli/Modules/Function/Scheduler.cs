using Hangfire;
using RtCli.Modules.Extension;
using RtCli.Modules.Unit;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RtCli.Modules.Function
{
    #region 配置模型

    public enum TaskQueue
    {
        Long,
        Fast,
        NoAsync
    }

    public class TaskRule
    {
        /// <summary>单个任务执行的超时时间(ms)，0=关闭</summary>
        public int TimeoutMs { get; set; } = 0;

        /// <summary>延迟执行：分钟数</summary>
        public int DelayMinutes { get; set; } = 0;

        /// <summary>延迟执行：指定日期时间</summary>
        public string DelayUntil { get; set; } = "";

        /// <summary>任务执行完成后延续开启的其他任务标识</summary>
        public List<string> ContinueTasks { get; set; } = new List<string>();

        /// <summary>任务创建后是否立即异步执行</summary>
        public bool ExecuteImmediately { get; set; } = false;

        /// <summary>执行n次后关闭，0=不限制</summary>
        public int MaxExecutions { get; set; } = 0;

        /// <summary>在指定日期时间关闭</summary>
        public string ExpireAt { get; set; } = "";

        /// <summary>任务队列</summary>
        public string Queue { get; set; } = "Fast";
    }

    public class TaskEntry
    {
        /// <summary>是否启用</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 启用该计划任务的目标实例标识列表，为空则只作用于当前实例。
        /// 命令类执行会按列表逐个实例发送(RUN/RM 走子进程 stdin, RCON/RR 走该实例独立 RCON)。
        /// </summary>
        public List<string> ServerKeys { get; set; } = new List<string>();

        /// <summary>
        /// 触发执行条件
        /// 支持格式:
        ///   时间条件:
        ///     cron: "0 */5 * * * *"          - CRON表达式
        ///     every: 30                       - 每隔30分钟
        ///     daily: "08:00"                  - 每天08:00
        ///     daily: "08:00,20:00"            - 每天08:00和20:00
        ///     date: "2026-07-01 10:00"        - 在指定日期时间执行
        ///   事件条件(仅支持以下事件名，其他值无效并告警，任务不会触发):
        ///     event: ServerStartEvent         - 在指定事件触发时执行
        ///     event: CustomPlayerEvent        - 通配全部自定义玩家事件
        ///     event: PlayerMoveTooFast        - player_event.customs中定义的自定义事件
        ///   组合条件:
        ///     every: 30 and event: ServerStartEvent  - AND条件
        ///     daily: "08:00" or event: ServerStartEvent - OR条件
        /// </summary>
        public string Trigger { get; set; } = "";

        /// <summary>
        /// 执行前置条件（trigger满足后、execute执行前检查）
        /// 支持格式:
        ///   变量比较: {rt.server_running} == "true"
        ///   check script 脚本名称 运算符 值: check script example_cs == "true"
        ///   check file 路径 true/false: check file "E:\path\to\file" true
        ///   多条件用 and / or 连接
        /// </summary>
        public string Condition { get; set; } = "";

        /// <summary>
        /// 执行内容
        /// script:脚本名称 - 执行Scripts中已配置的脚本
        /// backup - 执行当前服务端备份
        /// restart - 重启当前服务端
        /// command:/say hello - 发送命令到MC服务端
        /// cli:.auto list - 执行面板/RtCli 自身命令(不发给MC服务端)
        /// 多行动作以\n分隔, 按顺序依次执行
        /// </summary>
        public string Execute { get; set; } = "";

        /// <summary>是否开启参数传递</summary>
        public bool PassParameters { get; set; } = false;

        /// <summary>任务规则</summary>
        public TaskRule Rules { get; set; } = new TaskRule();
    }

    public class SchedulerSettings
    {
        public Dictionary<string, TaskEntry> Tasks { get; set; } = new Dictionary<string, TaskEntry>();
    }

    #endregion

    internal class Scheduler
    {
        private const string ThisName = "Scheduler";
        private static BackgroundJobServer? _jobServer;
        private static bool _isRunning = false;
        private static SchedulerSettings _settings = new SchedulerSettings();
        private static readonly Dictionary<string, int> _executionCounts = new();
        // 任务名 -> 该任务注册的全部 Hangfire 任务ID(OR 多分支/每日多时间点会产生多个)
        private static readonly Dictionary<string, List<string>> _hangfireJobIds = new();
        private static readonly Dictionary<string, CancellationTokenSource> _delayedTasks = new();
        private static readonly Dictionary<string, List<string>> _eventSubscriptions = new();
        private static readonly object _lock = new();
        // 间隔任务的真实分钟间隔与上次执行时间(以 Hangfire job id 为键, 与 OR 分支一一对应)
        private static readonly Dictionary<string, int> _intervalMinutes = new();
        private static readonly Dictionary<string, DateTime> _intervalLastRun = new();
        private static readonly object _intervalLock = new();
        private static readonly AsyncLocal<Dictionary<string, string>?> _eventVars = new();

        // 调度器生命周期取消源: Stop/Reload 时取消, 让延迟任务与长任务及时退出
        private static CancellationTokenSource _schedulerCts = new();
        // 事件驱动任务的并发上限, 避免高频事件瞬间打满线程池
        private static readonly SemaphoreSlim _eventExecutionLimit = new(4, 4);
        // 事件+时间条件的上次触发时间(键为 任务名+分支+条件)
        private static readonly Dictionary<string, DateTime> _timeCondLastTrigger = new();
        private static readonly object _timeCondLock = new();
        // 延续任务的最大调用链深度, 防止 A->B->A 无限递归
        private const int MaxContinueDepth = 16;
        // 每日时间条件的允许触发窗口(分钟), 事件不必恰好落在整分钟
        private const int DailyWindowMinutes = 5;
        private static readonly HashSet<string> _maxReachedLogged = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _cpuSampleLock = new();
        private static DateTime _lastCpuSampleAt;
        private static TimeSpan _lastCpuSampleTime;
        private static readonly Process _selfProcess = Process.GetCurrentProcess();

        public static bool IsRunning => _isRunning;
        public static SchedulerSettings Settings => _settings;

        private static void Log(string msg, int msgType)
        {
            Output.Log(Markup.Escape(msg), msgType, ThisName);
        }

        #region 条件求值引擎

        /// <summary>
        /// 评估执行前置条件，支持 and/or 组合、{rt.xxx}变量、check script、check file
        /// </summary>
        private static bool EvaluateCondition(string condition)
        {
            try
            {
                // 先按 or 分割（优先级最低）
                var orParts = SplitByOperator(condition, " or ");
                foreach (var orPart in orParts)
                {
                    // 再按 and 分割
                    var andParts = SplitByOperator(orPart.Trim(), " and ");
                    bool allMatch = true;
                    foreach (var andPart in andParts)
                    {
                        if (!EvaluateSingleCondition(andPart.Trim()))
                        {
                            allMatch = false;
                            break;
                        }
                    }
                    if (allMatch)
                        return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Log($"条件求值异常: {ex.Message}", 2);
                return false;
            }
        }

        /// <summary>
        /// 按操作符分割(引号感知、大小写不敏感)，忽略引号内的同名字样(如 "a and b")
        /// </summary>
        private static List<string> SplitByOperator(string input, string op)
        {
            var result = new List<string>();
            var sb = new System.Text.StringBuilder();
            char quote = '\0';
            int i = 0;

            void Flush()
            {
                var trimmed = sb.ToString().Trim();
                if (trimmed.Length > 0)
                    result.Add(trimmed);
                sb.Clear();
            }

            while (i < input.Length)
            {
                char ch = input[i];

                // 引号内原样保留, 不做任何拆分
                if (quote != '\0')
                {
                    sb.Append(ch);
                    if (ch == quote) quote = '\0';
                    i++;
                    continue;
                }
                if (ch == '"' || ch == '\'')
                {
                    quote = ch;
                    sb.Append(ch);
                    i++;
                    continue;
                }

                // 词边界比较: 前后不能是字母/数字/下划线, 避免把 "candy" 当成 and
                if (i + op.Length <= input.Length &&
                    string.Compare(input, i, op, 0, op.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    char prev = i == 0 ? ' ' : input[i - 1];
                    char next = i + op.Length < input.Length ? input[i + op.Length] : ' ';
                    bool prevOk = !char.IsLetterOrDigit(prev) && prev != '_';
                    bool nextOk = !char.IsLetterOrDigit(next) && next != '_';
                    if (prevOk && nextOk)
                    {
                        Flush();
                        i += op.Length;
                        continue;
                    }
                }

                sb.Append(ch);
                i++;
            }
            Flush();
            return result.Count > 0 ? result : new List<string> { input };
        }

        /// <summary>
        /// 评估单个条件
        /// </summary>
        private static bool EvaluateSingleCondition(string condition)
        {
            condition = condition.Trim();
            if (string.IsNullOrEmpty(condition))
                return true;

            // check script 脚本名称 运算符 值
            if (condition.StartsWith("check script ", StringComparison.OrdinalIgnoreCase))
            {
                return EvaluateCheckScript(condition.Substring("check script ".Length).Trim());
            }

            // check file 路径 true/false
            if (condition.StartsWith("check file ", StringComparison.OrdinalIgnoreCase))
            {
                return EvaluateCheckFile(condition.Substring("check file ".Length).Trim());
            }

            // 变量比较: {rt.xxx} 运算符 值
            return EvaluateVariableComparison(condition);
        }

        /// <summary>
        /// 评估变量比较表达式: {rt.xxx} == "value" 或 {rt.xxx} >= 10
        /// </summary>
        private static bool EvaluateVariableComparison(string expr)
        {
            // 提取 {rt.xxx} 变量
            var varMatch = Regex.Match(expr, @"\{rt\.(\w+)\}");
            if (!varMatch.Success)
            {
                // 没有变量，直接尝试解析为 bool
                return bool.TryParse(expr, out var b) && b;
            }

            string varName = varMatch.Groups[1].Value;
            string varValue = GetRuntimeVariable(varName);

            // 提取运算符和右值
            string remaining = expr.Substring(varMatch.Index + varMatch.Length).Trim();
            var (op, rightValue) = ExtractOperatorAndValue(remaining);

            if (string.IsNullOrEmpty(op))
                return !string.IsNullOrEmpty(varValue);

            // 数值比较时未知值(空/查询失败)不成立, 避免 "tps < 18" 在TPS未知时恒为真误触发任务
            if (string.IsNullOrEmpty(varValue) &&
                (op == ">" || op == ">=" || op == "<" || op == "<="))
                return false;

            return CompareValues(varValue, op, rightValue);
        }

        /// <summary>
        /// 从剩余字符串中提取运算符和值
        /// </summary>
        private static (string op, string value) ExtractOperatorAndValue(string remaining)
        {
            // 按优先级匹配运算符（长运算符优先）
            string[] operators = { ">=", "<=", "!=", "==", ">", "<", " contains ", " startsWith ", " endsWith " };
            foreach (var op in operators)
            {
                int idx = remaining.IndexOf(op, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string value = remaining.Substring(idx + op.Length).Trim();
                    // 去除引号
                    value = value.Trim('"', '\'');
                    string cleanOp = op.Trim();
                    return (cleanOp, value);
                }
            }
            return ("", "");
        }

        /// <summary>
        /// 比较两个值
        /// </summary>
        private static bool CompareValues(string left, string op, string right)
        {
            switch (op)
            {
                case "==":
                    return left == right;
                case "!=":
                    return left != right;
                case "contains":
                    return left.Contains(right);
                case "startsWith":
                    return left.StartsWith(right);
                case "endsWith":
                    return left.EndsWith(right);
                case ">":
                case ">=":
                case "<":
                case "<=":
                    if (double.TryParse(left, out var lNum) && double.TryParse(right, out var rNum))
                    {
                        return op switch
                        {
                            ">" => lNum > rNum,
                            ">=" => lNum >= rNum,
                            "<" => lNum < rNum,
                            "<=" => lNum <= rNum,
                            _ => false
                        };
                    }
                    // 非数字按字符串比较
                    return op switch
                    {
                        ">" => string.Compare(left, right, StringComparison.Ordinal) > 0,
                        ">=" => string.Compare(left, right, StringComparison.Ordinal) >= 0,
                        "<" => string.Compare(left, right, StringComparison.Ordinal) < 0,
                        "<=" => string.Compare(left, right, StringComparison.Ordinal) <= 0,
                        _ => false
                    };
                default:
                    return false;
            }
        }

        /// <summary>
        /// check script 脚本名称 运算符 值
        /// </summary>
        private static bool EvaluateCheckScript(string args)
        {
            // 格式: 脚本名称 运算符 "值"
            var parts = SplitScriptArgs(args);
            if (parts.Count < 3)
                return false;

            string scriptName = parts[0];
            string op = parts[1];
            string expectedValue = parts[2];

            // 执行脚本获取返回值
            string scriptResult = ExecuteScriptForResult(scriptName);

            return CompareValues(scriptResult, op, expectedValue);
        }

        /// <summary>
        /// 分割脚本检查参数（支持引号）
        /// </summary>
        private static List<string> SplitScriptArgs(string input)
        {
            var result = new List<string>();
            var matches = Regex.Matches(input, @"(?:""([^""]*)""|'([^']*)'|(\S+))");
            foreach (Match m in matches)
            {
                string val = m.Groups[1].Success ? m.Groups[1].Value :
                             m.Groups[2].Success ? m.Groups[2].Value :
                             m.Groups[3].Value;
                if (!string.IsNullOrEmpty(val))
                    result.Add(val);
            }
            return result;
        }

        /// <summary>
        /// 执行脚本并获取返回值（按异步流捕获 stdout，不做进程级 Console.SetOut 重定向）
        /// </summary>
        private static string ExecuteScriptForResult(string scriptName)
        {
            try
            {
                var sw = new System.IO.StringWriter();
                // AsyncLocal 捕获只在当前执行流生效: 并发的其他 Hangfire worker 仍正常输出到控制台与日志
                Output.AmbientCapture.Value = sw;
                try
                {
                    Scripts.ExecuteScriptByName(scriptName);
                }
                finally
                {
                    Output.AmbientCapture.Value = null;
                }
                return sw.ToString().Trim();
            }
            catch (Exception ex)
            {
                Log($"执行脚本 {scriptName} 失败: {ex.Message}", 2);
                return "";
            }
        }

        /// <summary>
        /// check file 路径 true/false
        /// </summary>
        private static bool EvaluateCheckFile(string args)
        {
            // 格式: "路径" true/false 或 路径 true/false
            var parts = SplitScriptArgs(args);
            if (parts.Count < 2)
                return false;

            string filePath = parts[0];
            if (!bool.TryParse(parts[parts.Count - 1], out var shouldExist))
                return false;

            bool exists = File.Exists(filePath) || Directory.Exists(filePath);
            return shouldExist ? exists : !exists;
        }

        /// <summary>
        /// 获取运行时变量值
        /// </summary>
        private static string GetRuntimeVariable(string varName)
        {
            try
            {
                // 事件变量优先（事件触发时由 SubscribeToEvent 注入，如 actor_uuid/player_name 等）
                var eventVars = _eventVars.Value;
                if (eventVars != null && eventVars.Count > 0)
                {
                    if (eventVars.TryGetValue(varName, out var ev1))
                        return ev1;
                    if (eventVars.TryGetValue(varName.ToLowerInvariant(), out var ev2))
                        return ev2;
                }

                switch (varName.ToLowerInvariant())
                {
                    case "server_running":
                        return (Analyzer.IsRunModeActive || Analyzer.IsAttached).ToString().ToLowerInvariant();
                    case "server_key":
                        return Config.App?.CurrentServer ?? "";
                    case "server_name":
                        return Config.CurrentServer?.ServerName ?? "";
                    case "mode":
                        return Analyzer.CurrentMode ?? "";
                    case "player_count":
                        {
                            // 真实在线人数(内部带10秒节流缓存; 服务端未运行/查询失败返回-1, 求值侧按未知处理)
                            var (count, _) = Analyzer.QueryPlayerCount(1500);
                            return count < 0 ? "" : count.ToString();
                        }
                    case "uptime_minutes":
                        {
                            // MC 服务端运行时长(不是 RtCli 进程启动时间); 服务端未运行返回空, 求值侧按未知处理
                            var mcProc = Analyzer.GetServerProcess();
                            if (mcProc == null)
                                return "";
                            try
                            {
                                return ((int)(DateTime.Now - mcProc.StartTime).TotalMinutes).ToString();
                            }
                            catch { return ""; }
                        }
                    case "tps":
                        {
                            // 真实TPS取1分钟窗口(内部带10秒节流缓存; 查询失败返回-1, 求值侧按未知处理)
                            var (tps1m, _, _) = Analyzer.QueryTps(1500);
                            return tps1m < 0 ? "" : tps1m.ToString("0.##");
                        }
                    case "memory_usage_mb":
                        return (_selfProcess.WorkingSet64 / 1024 / 1024).ToString();
                    case "cpu_usage":
                        return GetProcessCpuUsagePercent().ToString("0.#");
                    case "auto_backup_enabled":
                        return (Config.App?.AutoBackupEnabled ?? false).ToString().ToLowerInvariant();
                    case "scheduler_running":
                        return _isRunning.ToString().ToLowerInvariant();
                    case "time":
                        return DateTime.Now.ToString("HH:mm:ss");
                    case "date":
                        return DateTime.Now.ToString("yyyy-MM-dd");
                    case "timestamp":
                        return ((long)DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds).ToString();
                    default:
                        // 尝试从当前服务器配置中读取同名属性
                        if (Config.CurrentServer != null)
                        {
                            var prop = Config.CurrentServer.GetType().GetProperty(varName);
                            if (prop != null)
                                return prop.GetValue(Config.CurrentServer)?.ToString() ?? "";
                        }
                        return "";
                }
            }
            catch { return ""; }
        }

        /// <summary>
        /// 按采样区间计算进程 CPU 占用百分比(首次调用无基线返回 0)
        /// </summary>
        private static double GetProcessCpuUsagePercent()
        {
            lock (_cpuSampleLock)
            {
                var now = DateTime.UtcNow;
                var cpu = _selfProcess.TotalProcessorTime;
                double percent = 0;

                if (_lastCpuSampleAt != default)
                {
                    var elapsedMs = (now - _lastCpuSampleAt).TotalMilliseconds;
                    if (elapsedMs > 0)
                    {
                        var cpuMs = (cpu - _lastCpuSampleTime).TotalMilliseconds;
                        percent = cpuMs / (elapsedMs * Environment.ProcessorCount) * 100.0;
                    }
                }

                _lastCpuSampleAt = now;
                _lastCpuSampleTime = cpu;
                return Math.Clamp(percent, 0, 100);
            }
        }

        /// <summary>
        /// 将事件对象的字段提取为变量字典，供 condition 中的 {rt.xxx} 求值使用(统一走 EventVariables)。
        /// </summary>
        private static Dictionary<string, string> BuildEventVariables(RtEvent e) => EventVariables.Extract(e);

        #endregion

        public static void Initialize()
        {
            LoadSettings();
        }

        public static void LoadSettings()
        {
            _settings = ContentManager.LoadSchedulerSettings();
            LoadExecutionState();
        }

        /// <summary>执行计数持久化文件(重启后不清零, 保证 MaxExecutions 语义一致)</summary>
        private static string ExecutionStatePath => Path.Combine(Config.DataPath, "scheduler_state.json");

        private static void LoadExecutionState()
        {
            try
            {
                if (!File.Exists(ExecutionStatePath))
                    return;

                var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(ExecutionStatePath));
                if (data == null)
                    return;

                lock (_lock)
                {
                    _executionCounts.Clear();
                    foreach (var kvp in data)
                        _executionCounts[kvp.Key] = kvp.Value;
                }
            }
            catch (Exception ex)
            {
                Output.Debug(ex, "加载调度器执行计数失败", ThisName);
            }
        }

        private static void SaveExecutionState()
        {
            try
            {
                Dictionary<string, int> snapshot;
                lock (_lock)
                    snapshot = new Dictionary<string, int>(_executionCounts);

                AtomicFile.WriteAllText(ExecutionStatePath, System.Text.Json.JsonSerializer.Serialize(snapshot));
            }
            catch (Exception ex)
            {
                Output.Debug(ex, "保存调度器执行计数失败", ThisName);
            }
        }

        public static void Start()
        {
            if (_isRunning) return;

            try
            {
                // Stop/Reload 后重新创建取消源, 旧延迟任务已全部取消不会再执行
                if (_schedulerCts.IsCancellationRequested)
                {
                    var oldCts = _schedulerCts;
                    _schedulerCts = new CancellationTokenSource();
                    try { oldCts.Dispose(); } catch { }
                }

                GlobalConfiguration.Configuration
                    .UseSimpleAssemblyNameTypeSerializer()
                    .UseRecommendedSerializerSettings()
                    .UseInMemoryStorage();

                _jobServer = new BackgroundJobServer(new BackgroundJobServerOptions
                {
                    WorkerCount = 4,
                    // 必须包含 default: RecurringJob 未指定队列时作业入 default 队列, 若 worker 不监听则永不执行。
                    // 顺序即优先级: 靠前的队列分配到更多 worker(4 个 worker 按顺序轮询分配)。
                    Queues = new[] { "fast", "default", "long" }
                });

                _isRunning = true;
                RegisterAllTasks();
                Log("调度器已启动", 1);
                EventBus.Publish(new SchedulerStartEvent());
            }
            catch (Exception ex)
            {
                Log($"调度器启动失败: {ex.Message}", 3);
            }
        }

        public static void Stop()
        {
            if (!_isRunning) return;

            try
            {
                // 取消调度器生命周期令牌(延迟/超时/脚本任务随之退出)
                try { _schedulerCts.Cancel(); }
                catch (Exception ex) { Output.Debug(ex, "取消调度器令牌失败", ThisName); }

                // 取消所有延迟任务
                foreach (var cts in _delayedTasks.Values)
                {
                    try { cts.Cancel(); }
                    catch (Exception ex) { Output.Debug(ex, "取消延迟任务失败", ThisName); }
                }
                _delayedTasks.Clear();

                // 删除所有Hangfire任务(RecurringJob 必须用 RemoveIfExists 才能从存储中移除)
                List<string> jobOwners;
                lock (_lock) { jobOwners = _hangfireJobIds.Keys.ToList(); }
                foreach (var owner in jobOwners)
                    RemoveTrackedJobs(owner);
                _hangfireJobIds.Clear();

                // 取消事件订阅
                foreach (var kvp in _eventSubscriptions)
                {
                    foreach (var subId in kvp.Value)
                    {
                        try { EventBus.UnsubscribeAll(subId); }
                        catch (Exception ex) { Output.Debug(ex, "取消事件订阅失败", ThisName); }
                    }
                }
                _eventSubscriptions.Clear();

                _jobServer?.Dispose();
                _jobServer = null;
                _isRunning = false;
                Log("调度器已停止", 1);
                EventBus.Publish(new SchedulerStopEvent());
            }
            catch (Exception ex)
            {
                Log($"调度器停止失败: {ex.Message}", 3);
            }
        }

        public static void Reload()
        {
            Stop();
            LoadSettings();
            Start();
        }

        private static void RegisterAllTasks()
        {
            int registered = 0;
            foreach (var kvp in _settings.Tasks)
            {
                string taskName = kvp.Key;
                TaskEntry task = kvp.Value;

                if (!task.Enabled)
                {
                    Log($"任务 {taskName} 已禁用，跳过", 1);
                    continue;
                }

                // 检查服务器标识: server_keys 指定目标实例, 只要任一目标实例存在就注册,
                // 执行时再按目标实例逐个发送(不再要求必须是当前实例)
                if (!IsAnyTargetAvailable(task))
                {
                    Log($"任务 {taskName} 的目标实例均不存在，跳过", 1);
                    continue;
                }

                // 检查过期
                if (!string.IsNullOrEmpty(task.Rules.ExpireAt))
                {
                    if (DateTime.TryParse(task.Rules.ExpireAt, out var expireTime) && DateTime.Now > expireTime)
                    {
                        Log($"任务 {taskName} 已过期，跳过", 1);
                        continue;
                    }
                }

                RegisterTask(taskName, task);
                registered++;

                // 立即执行
                if (task.Rules.ExecuteImmediately)
                {
                    var token = _schedulerCts.Token;
                    _ = Task.Run(() => ExecuteTaskSafe(taskName, task, null, token));
                }
            }

            Log($"已注册 {registered} 个计划任务", 1);
        }

        /// <summary>读取事件来源实例: 优先事件自带的 ServerKey, 其次发布时的实例上下文(读不到返回 null)。</summary>
        private static string? EventInstanceOf(RtEvent e)
        {
            var prop = e.GetType().GetProperty("ServerKey");
            if (prop != null && prop.GetValue(e) is string key && !string.IsNullOrWhiteSpace(key))
                return key;
            return InstanceContext.Current;
        }

        /// <summary>
        /// 解析任务的目标实例列表: server_keys 为空时只作用于当前实例。
        /// 每个实例按其自身分析模式发送: RUN/RM 走子进程 stdin, RCON/RR 走该实例独立的 RCON 连接。
        /// </summary>
        private static List<string> TargetInstances(TaskEntry task)
        {
            var keys = task.ServerKeys?
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            if (keys.Count == 0)
                keys.Add(Config.App.CurrentServer);
            return keys;
        }

        /// <summary>server_keys 为空或其中任一实例存在(当前实例或已配置实例)时返回 true。</summary>
        private static bool IsAnyTargetAvailable(TaskEntry task)
        {
            if (task.ServerKeys == null || task.ServerKeys.Count == 0)
                return true;

            foreach (var key in task.ServerKeys)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (string.Equals(key, Config.App.CurrentServer, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (Config.App.ServerList != null && Config.App.ServerList.ContainsKey(key))
                    return true;
            }
            return false;
        }

        /// <summary>按目标实例逐个发送 MC 命令(空列表=当前实例)。</summary>
        private static void SendCommandToTargets(TaskEntry task, string command, string note)
        {
            var targets = TargetInstances(task);
            foreach (var instanceId in targets)
            {
                Analyzer.SendCommand(command, instanceId);
                Log(targets.Count > 1 ? $"{note}: {command} -> {instanceId}" : $"{note}: {command}", 1);
            }
        }

        private static void RegisterTask(string taskName, TaskEntry task)
        {
            string trigger = task.Trigger.Trim();
            if (string.IsNullOrEmpty(trigger))
            {
                Log($"任务 {taskName} 未设置触发条件", 2);
                return;
            }

            // 解析组合条件: 支持引号、括号、3 段以上 AND/OR, 大小写不敏感
            TriggerNode? root;
            List<List<string>> terms;
            try
            {
                root = ParseTriggerExpression(trigger);
                if (root == null)
                {
                    Log($"任务 {taskName} 触发条件格式无效: {trigger}", 2);
                    return;
                }
                // 转为 DNF(OR 连接多个 AND 组), 每个 AND 组等价于旧实现的一个 OR 分支
                terms = ToDnf(root);
            }
            catch (Exception ex)
            {
                Log($"任务 {taskName} 触发条件解析失败: {ex.Message}", 2);
                return;
            }

            if (terms.Count == 0)
            {
                Log($"任务 {taskName} 触发条件为空: {trigger}", 2);
                return;
            }

            int index = 0;
            foreach (var term in terms)
            {
                index++;
                string suffix = terms.Count > 1 ? $"__or{index}" : "";
                RegisterTerm(taskName, task, term, suffix);
            }
        }

        /// <summary>注册一个 AND 条件组(组内条件需同时满足, 组间由 OR 连接)</summary>
        private static void RegisterTerm(string taskName, TaskEntry task, List<string> conditions, string branchSuffix)
        {
            var parsed = new List<ConditionInfo>();
            foreach (var condition in conditions)
            {
                var info = ParseCondition(condition.Trim());
                if (info == null)
                {
                    Log($"任务 {taskName} 条件格式无效: {condition}", 2);
                    return;
                }
                parsed.Add(info);
            }

            var events = parsed.Where(p => p.Type == "event").ToList();
            var times = parsed.Where(p => p.Type != "event").ToList();

            if (events.Count == 0)
            {
                if (times.Count == 1)
                    RegisterSingleCondition(taskName, task, conditions[0], branchSuffix);
                else
                    Log($"任务 {taskName} 多个时间条件无法同时满足(需事件条件配合): {string.Join(" and ", conditions)}", 2);
                return;
            }

            if (events.Count > 1)
            {
                Log($"任务 {taskName} 同一个 AND 组内只能有一个事件条件: {string.Join(" and ", conditions)}", 2);
                return;
            }

            // 事件触发时检查该组内的全部时间条件
            SubscribeToEvent(taskName, task, events[0].Value, times, branchSuffix);
        }

        private static void RegisterSingleCondition(string taskName, TaskEntry task, string condition, string branchSuffix = "")
        {
            var parsed = ParseCondition(condition.Trim());
            if (parsed == null)
            {
                Log($"任务 {taskName} 条件格式无效: {condition}", 2);
                return;
            }

            switch (parsed.Type)
            {
                case "cron":
                    RegisterCronTask(taskName, task, parsed.Value, branchSuffix);
                    break;
                case "every":
                    RegisterIntervalTask(taskName, task, parsed.Value, branchSuffix);
                    break;
                case "daily":
                    RegisterDailyTask(taskName, task, parsed.Value, branchSuffix);
                    break;
                case "date":
                    RegisterDateTask(taskName, task, parsed.Value, branchSuffix);
                    break;
                case "event":
                    RegisterEventTask(taskName, task, parsed.Value, branchSuffix);
                    break;
            }
        }

        #region 触发条件解析(引号/括号/多条件)

        private abstract class TriggerNode { }

        private sealed class TriggerLeaf : TriggerNode
        {
            public string Text = "";
        }

        private sealed class TriggerGroup : TriggerNode
        {
            public bool IsAnd;
            public readonly List<TriggerNode> Items = new();
        }

        private enum TriggerTokenType
        {
            Text,
            And,
            Or,
            Open,
            Close
        }

        private sealed class TriggerToken
        {
            public TriggerTokenType Type;
            public string Text = "";
        }

        /// <summary>词法分析: 识别括号与(大小写不敏感的) and/or, 引号内的内容不拆分</summary>
        private static List<TriggerToken> TokenizeTrigger(string input)
        {
            var tokens = new List<TriggerToken>();
            var sb = new System.Text.StringBuilder();
            char quote = '\0';
            int i = 0;

            void FlushText()
            {
                var text = sb.ToString().Trim();
                if (text.Length > 0)
                    tokens.Add(new TriggerToken { Type = TriggerTokenType.Text, Text = text });
                sb.Clear();
            }

            while (i < input.Length)
            {
                char ch = input[i];

                if (quote != '\0')
                {
                    sb.Append(ch);
                    if (ch == quote) quote = '\0';
                    i++;
                    continue;
                }
                if (ch == '"' || ch == '\'')
                {
                    quote = ch;
                    sb.Append(ch);
                    i++;
                    continue;
                }
                if (ch == '(')
                {
                    FlushText();
                    tokens.Add(new TriggerToken { Type = TriggerTokenType.Open, Text = "(" });
                    i++;
                    continue;
                }
                if (ch == ')')
                {
                    FlushText();
                    tokens.Add(new TriggerToken { Type = TriggerTokenType.Close, Text = ")" });
                    i++;
                    continue;
                }

                // and / or: 词边界匹配, 大小写不敏感
                if (TryMatchKeyword(input, i, "and", out int andLen))
                {
                    FlushText();
                    tokens.Add(new TriggerToken { Type = TriggerTokenType.And, Text = "and" });
                    i += andLen;
                    continue;
                }
                if (TryMatchKeyword(input, i, "or", out int orLen))
                {
                    FlushText();
                    tokens.Add(new TriggerToken { Type = TriggerTokenType.Or, Text = "or" });
                    i += orLen;
                    continue;
                }

                sb.Append(ch);
                i++;
            }

            FlushText();
            return tokens;
        }

        private static bool TryMatchKeyword(string input, int index, string keyword, out int length)
        {
            length = keyword.Length;
            if (index + length > input.Length)
                return false;
            if (string.Compare(input, index, keyword, 0, length, StringComparison.OrdinalIgnoreCase) != 0)
                return false;

            char prev = index == 0 ? ' ' : input[index - 1];
            char next = index + length < input.Length ? input[index + length] : ' ';
            return !char.IsLetterOrDigit(prev) && prev != '_'
                && !char.IsLetterOrDigit(next) && next != '_';
        }

        /// <summary>语法分析: or 优先级最低, and 次之, 括号最高</summary>
        private static TriggerNode? ParseTriggerExpression(string input)
        {
            var tokens = TokenizeTrigger(input);
            if (tokens.Count == 0)
                return null;

            int pos = 0;
            var node = ParseOrNode(tokens, ref pos);
            return pos == tokens.Count ? node : null; // 存在多余 token 视为语法错误
        }

        private static TriggerNode? ParseOrNode(List<TriggerToken> tokens, ref int pos)
        {
            var left = ParseAndNode(tokens, ref pos);
            if (left == null)
                return null;

            var items = new List<TriggerNode> { left };
            while (pos < tokens.Count && tokens[pos].Type == TriggerTokenType.Or)
            {
                pos++;
                var right = ParseAndNode(tokens, ref pos);
                if (right == null)
                    return null;
                items.Add(right);
            }

            if (items.Count == 1)
                return left;

            var group = new TriggerGroup { IsAnd = false };
            group.Items.AddRange(items);
            return group;
        }

        private static TriggerNode? ParseAndNode(List<TriggerToken> tokens, ref int pos)
        {
            var left = ParseFactorNode(tokens, ref pos);
            if (left == null)
                return null;

            var items = new List<TriggerNode> { left };
            while (pos < tokens.Count && tokens[pos].Type == TriggerTokenType.And)
            {
                pos++;
                var right = ParseFactorNode(tokens, ref pos);
                if (right == null)
                    return null;
                items.Add(right);
            }

            if (items.Count == 1)
                return left;

            var group = new TriggerGroup { IsAnd = true };
            group.Items.AddRange(items);
            return group;
        }

        private static TriggerNode? ParseFactorNode(List<TriggerToken> tokens, ref int pos)
        {
            if (pos >= tokens.Count)
                return null;

            var token = tokens[pos];
            if (token.Type == TriggerTokenType.Open)
            {
                pos++;
                var inner = ParseOrNode(tokens, ref pos);
                if (inner == null)
                    return null;
                if (pos >= tokens.Count || tokens[pos].Type != TriggerTokenType.Close)
                    return null; // 括号不匹配
                pos++;
                return inner;
            }

            if (token.Type == TriggerTokenType.Text)
            {
                pos++;
                return new TriggerLeaf { Text = token.Text };
            }

            return null;
        }

        /// <summary>把语法树展开为 DNF: 外层 List 为 OR 分支, 内层为 AND 条件</summary>
        private static List<List<string>> ToDnf(TriggerNode node)
        {
            switch (node)
            {
                case TriggerLeaf leaf:
                    return new List<List<string>> { new List<string> { leaf.Text } };

                case TriggerGroup group when group.IsAnd:
                    {
                        var result = new List<List<string>> { new List<string>() };
                        foreach (var item in group.Items)
                        {
                            var child = ToDnf(item);
                            var next = new List<List<string>>();
                            foreach (var prefix in result)
                            {
                                foreach (var term in child)
                                {
                                    var merged = new List<string>(prefix);
                                    merged.AddRange(term);
                                    next.Add(merged);
                                }
                            }
                            result = next;
                            if (result.Count > 64)
                                throw new InvalidOperationException("条件组合过于复杂");
                        }
                        return result;
                    }

                case TriggerGroup group:
                    {
                        var result = new List<List<string>>();
                        foreach (var item in group.Items)
                            result.AddRange(ToDnf(item));
                        if (result.Count > 64)
                            throw new InvalidOperationException("条件组合过于复杂");
                        return result;
                    }

                default:
                    return new List<List<string>>();
            }
        }

        #endregion

        private static ConditionInfo? ParseCondition(string condition)
        {
            if (condition.StartsWith("cron:", StringComparison.OrdinalIgnoreCase))
                return new ConditionInfo("cron", Unquote(condition.Substring(5)));
            if (condition.StartsWith("every:", StringComparison.OrdinalIgnoreCase))
                return new ConditionInfo("every", Unquote(condition.Substring(6)));
            if (condition.StartsWith("daily:", StringComparison.OrdinalIgnoreCase))
                return new ConditionInfo("daily", Unquote(condition.Substring(6)));
            if (condition.StartsWith("date:", StringComparison.OrdinalIgnoreCase))
                return new ConditionInfo("date", Unquote(condition.Substring(5)));
            if (condition.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                return new ConditionInfo("event", Unquote(condition.Substring(6)));

            return null;
        }

        /// <summary>
        /// 去掉条件值两端成对的引号(词法分析为保证引号内内容不被拆分而保留了引号)。
        /// 否则 daily: "11:46" 这类写法会把引号当成值的一部分, 导致时间/日期/事件名解析失败。
        /// </summary>
        private static string Unquote(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.Length >= 2)
            {
                char first = trimmed[0];
                char last = trimmed[trimmed.Length - 1];
                if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
                    return trimmed.Substring(1, trimmed.Length - 2).Trim();
            }
            return trimmed;
        }

        /// <summary>把面板/配置里的队列名映射到 worker 实际监听的队列(fast/default/long)。</summary>
        private static string NormalizeQueue(string? queue)
        {
            return (queue ?? "").Trim().ToLowerInvariant() switch
            {
                "fast" => "fast",
                "long" => "long",
                "slow" => "long",   // 兼容旧配置里的 Slow
                _ => "default"
            };
        }

        private static void RegisterCronTask(string taskName, TaskEntry task, string cronExpr, string branchSuffix = "")
        {
            string jobId = taskName + branchSuffix + "__cron";
            try
            {
                RecurringJob.AddOrUpdate(
                    jobId,
                    () => HangfireExecute(taskName, jobId),
                    cronExpr,
                    new RecurringJobOptions
                    {
                        TimeZone = TimeZoneInfo.Local,
                        QueueName = NormalizeQueue(task.Rules?.Queue)
                    });
                TrackJobId(taskName, jobId);
                Log($"任务 {taskName} 已注册CRON: {cronExpr}", 1);
            }
            catch (Exception ex)
            {
                Log($"任务 {taskName} CRON表达式无效: {cronExpr} - {ex.Message}", 2);
            }
        }

        private static void RegisterIntervalTask(string taskName, TaskEntry task, string minutesStr, string branchSuffix = "")
        {
            if (!int.TryParse(minutesStr, out int minutes) || minutes <= 0)
            {
                Log($"任务 {taskName} 间隔分钟数无效: {minutesStr}", 2);
                return;
            }
            // 上限 7 天; 超过该值基本等同一次性任务, 提示用户改用 date:
            if (minutes > 7 * 24 * 60)
            {
                Log($"任务 {taskName} 间隔分钟数过大({minutes}), 请改用 date: 指定执行时间", 2);
                return;
            }

            // Hangfire 只支持 crontab; minute 字段无法表达 >59 或无法整除 60 的间隔,
            // 因此注册"每分钟检查一次", 由 ShouldRunInterval 按真实时间间隔放行, 保证间隔均匀。
            string jobId = taskName + branchSuffix + "__interval";
            lock (_intervalLock)
            {
                _intervalMinutes[jobId] = minutes;
                _intervalLastRun[jobId] = DateTime.Now;
            }

            try
            {
                RecurringJob.AddOrUpdate(
                    jobId,
                    () => HangfireExecute(taskName, jobId),
                    "*/1 * * * *",
                    new RecurringJobOptions
                    {
                        TimeZone = TimeZoneInfo.Local,
                        QueueName = NormalizeQueue(task.Rules?.Queue)
                    });
                TrackJobId(taskName, jobId);
                Log($"任务 {taskName} 已注册间隔执行: 每 {minutes} 分钟", 1);
            }
            catch (Exception ex)
            {
                Log($"任务 {taskName} 间隔任务注册失败: {ex.Message}", 2);
            }
        }

        private static void RegisterDailyTask(string taskName, TaskEntry task, string timeStr, string branchSuffix = "")
        {
            var times = timeStr.Split(',', StringSplitOptions.RemoveEmptyEntries);
            int okCount = 0;
            foreach (var time in times)
            {
                var trimmed = time.Trim();
                if (!TimeSpan.TryParse(trimmed, out var ts))
                {
                    Log($"任务 {taskName} 时间格式无效: {trimmed}", 2);
                    continue;
                }

                string jobId = taskName + branchSuffix + "__daily_" + trimmed.Replace(":", "");
                string cronExpr = $"{ts.Minutes} {ts.Hours} * * *";
                try
                {
                    RecurringJob.AddOrUpdate(
                        jobId,
                        () => HangfireExecute(taskName, jobId),
                        cronExpr,
                        new RecurringJobOptions
                        {
                            TimeZone = TimeZoneInfo.Local,
                            QueueName = NormalizeQueue(task.Rules?.Queue)
                        });
                    TrackJobId(taskName, jobId);
                    okCount++;
                }
                catch (Exception ex)
                {
                    Log($"任务 {taskName} 每日任务注册失败: {trimmed} - {ex.Message}", 2);
                }
            }
            if (okCount > 0)
                Log($"任务 {taskName} 已注册每日执行: {timeStr}", 1);
        }

        private static void RegisterDateTask(string taskName, TaskEntry task, string dateStr, string branchSuffix = "")
        {
            if (!DateTime.TryParse(dateStr, out var targetDate))
            {
                Log($"任务 {taskName} 日期格式无效: {dateStr}", 2);
                return;
            }

            var delay = targetDate - DateTime.Now;
            if (delay.TotalSeconds <= 0)
            {
                Log($"任务 {taskName} 指定日期已过: {dateStr}", 2);
                return;
            }

            var cts = new CancellationTokenSource();
            string jobId = taskName + branchSuffix + "__date";
            if (_delayedTasks.TryGetValue(jobId, out var oldCts))
            {
                try { oldCts.Cancel(); } catch { }
                oldCts.Dispose();
            }
            _delayedTasks[jobId] = cts;
            TrackJobId(taskName, jobId);

            Task.Run(async () =>
            {
                try
                {
                    await DelayAsync(delay, cts.Token);
                    await ExecuteTaskSafe(taskName, task, null, _schedulerCts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log($"任务 {taskName} 延迟执行失败: {ex.Message}", 3);
                }
                finally
                {
                    // 释放延迟任务自身的取消源(字典中仍保留引用, Cancel 由 try/catch 保护)
                    try { cts.Dispose(); } catch { }
                }
            }, cts.Token);

            Log($"任务 {taskName} 已注册定时执行: {dateStr}", 1);
        }

        private static void RegisterEventTask(string taskName, TaskEntry task, string eventName, string branchSuffix = "")
        {
            SubscribeToEvent(taskName, task, eventName, new List<ConditionInfo>(), branchSuffix);
        }

        private static void SubscribeToEvent(string taskName, TaskEntry task, string eventName, List<ConditionInfo>? timeConds, string branchSuffix = "")
        {
            timeConds ??= new List<ConditionInfo>();

            // 先校验事件名有效性，避免不存在的事件名被静默当作自定义事件订阅
            if (!EventNames.IsValidEventName(eventName))
            {
                Log($"任务 {taskName} 事件名无效: {eventName}；合法事件: {EventNames.GetValidEventsDescription()}", 3);
                return;
            }

            // OR 多事件分支必须使用不同订阅ID, 否则取消订阅时会漏掉/重复取消
            var subId = $"Scheduler_{taskName}{branchSuffix}";
            if (!_eventSubscriptions.ContainsKey(taskName))
                _eventSubscriptions[taskName] = new List<string>();
            if (!_eventSubscriptions[taskName].Contains(subId))
                _eventSubscriptions[taskName].Add(subId);

            string timeCondKey = taskName + branchSuffix;

            Action<RtEvent> handler = (e) =>
            {
                // 检查服务器标识: 事件型任务按事件来源实例匹配 server_keys
                if (task.ServerKeys.Count > 0)
                {
                    string evtInstance = EventInstanceOf(e) ?? Config.App.CurrentServer;
                    if (!task.ServerKeys.Any(k => string.Equals(k?.Trim(), evtInstance, StringComparison.OrdinalIgnoreCase)))
                        return;
                }

                // AND 组内全部时间条件必须满足(事件不必落在整分钟, 按时间窗与上次触发时间判断)
                if (timeConds.Count > 0 && !CheckTimeConditions(timeCondKey, timeConds))
                    return;

                // 检查过期
                if (!string.IsNullOrEmpty(task.Rules.ExpireAt))
                {
                    if (DateTime.TryParse(task.Rules.ExpireAt, out var expireTime) && DateTime.Now > expireTime)
                        return;
                }

                var token = _schedulerCts.Token;
                if (token.IsCancellationRequested)
                    return;

                string param = task.PassParameters ? e.EventName : "";
                var evtVars = BuildEventVariables(e);
                Task.Run(async () =>
                {
                    // 并发上限: 拿不到信号量直接跳过, 避免事件风暴淹没线程池
                    if (!await _eventExecutionLimit.WaitAsync(0).ConfigureAwait(false))
                    {
                        Log($"事件任务 {taskName} 并发已达上限，本次跳过", 2);
                        return;
                    }

                    _eventVars.Value = evtVars;
                    try
                    {
                        await ExecuteTaskSafe(taskName, task, param, token);
                    }
                    finally
                    {
                        _eventVars.Value = null;
                        _eventExecutionLimit.Release();
                    }
                });
            };

            // 间隔时间条件从订阅时刻起算, 首个间隔窗口结束后才允许事件触发
            foreach (var tc in timeConds)
            {
                if (tc.Type != "every" || !int.TryParse(tc.Value, out _))
                    continue;
                lock (_timeCondLock)
                {
                    var key = timeCondKey + "|every|" + tc.Value;
                    if (!_timeCondLastTrigger.ContainsKey(key))
                        _timeCondLastTrigger[key] = DateTime.Now;
                }
            }

            switch (eventName)
            {
                case "ModeSelectedEvent":
                    EventBus.Subscribe<ModeSelectedEvent>(e => handler(e), subId);
                    break;
                case "ProgramStartupEvent":
                    EventBus.Subscribe<ProgramStartupEvent>(e => handler(e), subId);
                    break;
                case "ProgramShutdownEvent":
                    EventBus.Subscribe<ProgramShutdownEvent>(e => handler(e), subId);
                    break;
                case "ExtensionLoadEvent":
                    EventBus.Subscribe<ExtensionLoadEvent>(e => handler(e), subId);
                    break;
                case "ExtensionUnloadEvent":
                    EventBus.Subscribe<ExtensionUnloadEvent>(e => handler(e), subId);
                    break;
                case "ServerStartEvent":
                    EventBus.Subscribe<ServerStartEvent>(e => handler(e), subId);
                    break;
                case "ServerStopEvent":
                    EventBus.Subscribe<ServerStopEvent>(e => handler(e), subId);
                    break;
                case "ServerDoneEvent":
                    EventBus.Subscribe<ServerDoneEvent>(e => handler(e), subId);
                    break;
                case "ServerCrashEvent":
                    EventBus.Subscribe<ServerCrashEvent>(e => handler(e), subId);
                    break;
                case "ServerStopUnresponsiveEvent":
                    EventBus.Subscribe<ServerStopUnresponsiveEvent>(e => handler(e), subId);
                    break;
                case "AutoRestartEvent":
                    EventBus.Subscribe<AutoRestartEvent>(e => handler(e), subId);
                    break;
                case "BackupStartEvent":
                    EventBus.Subscribe<BackupStartEvent>(e => handler(e), subId);
                    break;
                case "BackupCompleteEvent":
                    EventBus.Subscribe<BackupCompleteEvent>(e => handler(e), subId);
                    break;
                case "TaskExecuteEvent":
                    EventBus.Subscribe<TaskExecuteEvent>(e => handler(e), subId);
                    break;
                case "SchedulerStartEvent":
                    EventBus.Subscribe<SchedulerStartEvent>(e => handler(e), subId);
                    break;
                case "SchedulerStopEvent":
                    EventBus.Subscribe<SchedulerStopEvent>(e => handler(e), subId);
                    break;
                case "CommandExecuteEvent":
                    EventBus.Subscribe<CommandExecuteEvent>(e => handler(e), subId);
                    break;
                case "ConfigReloadEvent":
                    EventBus.Subscribe<ConfigReloadEvent>(e => handler(e), subId);
                    break;
                case "PlayerJoinEvent":
                    EventBus.Subscribe<PlayerJoinEvent>(e => handler(e), subId);
                    break;
                case "PlayerConnectEvent":
                    EventBus.Subscribe<PlayerConnectEvent>(e => handler(e), subId);
                    break;
                case "PlayerLostEvent":
                    EventBus.Subscribe<PlayerLostEvent>(e => handler(e), subId);
                    break;
                case "PlayerLeaveEvent":
                    EventBus.Subscribe<PlayerLeaveEvent>(e => handler(e), subId);
                    break;
                case "PlayerCommandEvent":
                    EventBus.Subscribe<PlayerCommandEvent>(e => handler(e), subId);
                    break;
                case "PlayerChatEvent":
                    EventBus.Subscribe<PlayerChatEvent>(e => handler(e), subId);
                    break;
                case "PlayerSetModeEvent":
                    EventBus.Subscribe<PlayerSetModeEvent>(e => handler(e), subId);
                    break;
                case "LuckPermsChangeEvent":
                    EventBus.Subscribe<LuckPermsChangeEvent>(e => handler(e), subId);
                    break;
                case "PacketInitializeEvent":
                    EventBus.Subscribe<PacketInitializeEvent>(e => handler(e), subId);
                    break;
                case "PacketStopEvent":
                    EventBus.Subscribe<PacketStopEvent>(e => handler(e), subId);
                    break;
                case "PacketClientEvent":
                    EventBus.Subscribe<PacketClientEvent>(e => handler(e), subId);
                    break;
                case "PacketServerEvent":
                    EventBus.Subscribe<PacketServerEvent>(e => handler(e), subId);
                    break;
                case "PlayerOpEvent":
                    EventBus.Subscribe<PlayerOpEvent>(e => handler(e), subId);
                    break;
                case "PlayerBanEvent":
                    EventBus.Subscribe<PlayerBanEvent>(e => handler(e), subId);
                    break;
                case "ServerStatusEvent":
                    EventBus.Subscribe<ServerStatusEvent>(e => handler(e), subId);
                    break;
                case "CustomPlayerEvent":
                    // 字面量CustomPlayerEvent为通配名: 订阅全部自定义玩家事件
                    EventBus.Subscribe<CustomPlayerEvent>(e => handler(e), subId);
                    break;
                default:
                    // 其余合法事件名为player_event.customs中定义的自定义事件键: 按事件名过滤后触发
                    EventBus.Subscribe<CustomPlayerEvent>(e =>
                    {
                        if (!string.Equals(e.EventName, eventName, StringComparison.OrdinalIgnoreCase))
                            return;
                        handler(e);
                    }, subId);
                    break;
            }

            Log($"任务 {taskName} 已订阅事件: {eventName}", 1);
        }

        /// <summary>AND 组内的全部时间条件都必须满足</summary>
        private static bool CheckTimeConditions(string key, List<ConditionInfo> timeConds)
        {
            foreach (var cond in timeConds)
            {
                if (!CheckTimeCondition(key, cond))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 事件到达时的时间条件判断:
        /// daily 允许 [计划时间, 计划时间+窗口] 区间内触发一次; every 按上次触发时间计算间隔; date 只触发一次
        /// </summary>
        private static bool CheckTimeCondition(string key, ConditionInfo timeCond)
        {
            var now = DateTime.Now;
            switch (timeCond.Type)
            {
                case "daily":
                    foreach (var t in timeCond.Value.Split(','))
                    {
                        if (!TimeSpan.TryParse(t.Trim(), out var ts))
                            continue;

                        var start = now.Date + ts;
                        if (now < start || now > start.AddMinutes(DailyWindowMinutes))
                            continue;

                        // 同一天同一时间点只触发一次
                        if (MarkTimeConditionTriggered(key + "|daily|" + ts, now.Date))
                            return true;
                    }
                    return false;

                case "every":
                    if (!int.TryParse(timeCond.Value, out int minutes) || minutes <= 0)
                        return false;

                    var everyKey = key + "|every|" + minutes;
                    lock (_timeCondLock)
                    {
                        // 未记录过(如任务动态注册)则从当前时刻起算, 避免事件到达即触发
                        if (!_timeCondLastTrigger.TryGetValue(everyKey, out var last))
                        {
                            _timeCondLastTrigger[everyKey] = now;
                            return false;
                        }
                        if ((now - last).TotalMinutes < minutes)
                            return false;

                        _timeCondLastTrigger[everyKey] = now;
                        return true;
                    }

                case "date":
                    if (DateTime.TryParse(timeCond.Value, out var targetDate) && now >= targetDate)
                        return MarkTimeConditionTriggered(key + "|date|" + targetDate.Ticks, targetDate.Date);
                    return false;

                default:
                    return true;
            }
        }

        /// <summary>记录时间条件已触发(同一天只允许一次), 原子判断</summary>
        private static bool MarkTimeConditionTriggered(string key, DateTime day)
        {
            lock (_timeCondLock)
            {
                if (_timeCondLastTrigger.TryGetValue(key, out var last) && last.Date == day.Date)
                    return false;
                _timeCondLastTrigger[key] = day;
                return true;
            }
        }

        /// <summary>Hangfire任务执行入口（无可选参数，供表达式树调用）</summary>
        public static async Task HangfireExecute(string taskName, string? jobId = null)
        {
            // 间隔任务由每分钟的检查任务触发, 只有真实间隔已达时才执行
            if (!string.IsNullOrEmpty(jobId) && !ShouldRunInterval(jobId))
                return;

            var token = _schedulerCts.Token;
            if (token.IsCancellationRequested)
                return;

            if (_settings.Tasks.TryGetValue(taskName, out var task))
            {
                await ExecuteTaskSafe(taskName, task, null, token);
            }
        }

        /// <summary>记录任务注册的 Hangfire 任务ID, 便于禁用/停止时精确移除</summary>
        private static void TrackJobId(string taskName, string jobId)
        {
            lock (_lock)
            {
                if (!_hangfireJobIds.TryGetValue(taskName, out var list))
                {
                    list = new List<string>();
                    _hangfireJobIds[taskName] = list;
                }
                if (!list.Contains(jobId))
                    list.Add(jobId);
            }
        }

        /// <summary>移除某任务注册的全部 Hangfire 任务(含间隔任务的状态)</summary>
        private static void RemoveTrackedJobs(string taskName)
        {
            List<string>? ids = null;
            lock (_lock)
            {
                if (_hangfireJobIds.TryGetValue(taskName, out var list))
                {
                    ids = new List<string>(list);
                    _hangfireJobIds.Remove(taskName);
                }
            }
            if (ids == null) return;

            foreach (var id in ids)
            {
                try { RecurringJob.RemoveIfExists(id); }
                catch (Exception ex) { Output.Debug(ex, $"删除Hangfire任务失败: {id}", ThisName); }

                // 同一 ID 也可能是 date: 延迟任务, 一并取消
                if (_delayedTasks.TryGetValue(id, out var dcts))
                {
                    try { dcts.Cancel(); } catch { }
                    dcts.Dispose();
                    _delayedTasks.Remove(id);
                }

                lock (_intervalLock)
                {
                    _intervalMinutes.Remove(id);
                    _intervalLastRun.Remove(id);
                }
            }
        }

        /// <summary>按真实时间间隔判断间隔任务是否应执行(每分钟被触发一次)</summary>
        private static bool ShouldRunInterval(string jobId)
        {
            lock (_intervalLock)
            {
                if (!_intervalMinutes.TryGetValue(jobId, out int minutes))
                    return true; // 非间隔任务

                var now = DateTime.Now;
                // 允许 1 分钟误差: 注册时刻不在整分钟边界上, 首次触发可能略早于完整间隔
                if (_intervalLastRun.TryGetValue(jobId, out var last) &&
                    (now - last).TotalMinutes < minutes - 1)
                    return false;

                _intervalLastRun[jobId] = now;
                return true;
            }
        }

        public static async Task ExecuteTaskSafe(string taskName, TaskEntry task, string? extraParam = null,
            CancellationToken ct = default, HashSet<string>? chain = null)
        {
            try
            {
                // 延迟: 用 TimeSpan 避免分钟数乘 60*1000 的整型溢出, 且可被取消
                if (task.Rules.DelayMinutes > 0)
                {
                    await DelayAsync(TimeSpan.FromMinutes(task.Rules.DelayMinutes), ct);
                }
                else if (!string.IsNullOrEmpty(task.Rules.DelayUntil))
                {
                    if (DateTime.TryParse(task.Rules.DelayUntil, out var delayTime))
                    {
                        var wait = delayTime - DateTime.Now;
                        if (wait > TimeSpan.Zero)
                            await DelayAsync(wait, ct);
                    }
                }

                // 检查执行前置条件
                if (!string.IsNullOrWhiteSpace(task.Condition))
                {
                    if (!EvaluateCondition(task.Condition))
                    {
                        Log($"任务 {taskName} 条件不满足，跳过执行: {task.Condition}", 1);
                        return;
                    }
                }

                // 执行次数原子占位(并发事件/多分支下也不会突破上限; 条件不满足时不消耗次数)
                if (task.Rules.MaxExecutions > 0 && !TryReserveExecution(taskName, task.Rules.MaxExecutions))
                {
                    bool shouldLog;
                    lock (_lock)
                        shouldLog = _maxReachedLogged.Add(taskName);
                    if (shouldLog)
                        Log($"任务 {taskName} 已达到最大执行次数 {task.Rules.MaxExecutions}，后续触发将跳过", 1);
                    return;
                }

                ct.ThrowIfCancellationRequested();

                // 超时处理: 链接调度器令牌, 超时或调度器停止都能中断
                if (task.Rules.TimeoutMs > 0)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(task.Rules.TimeoutMs);
                    await ExecuteTaskCore(taskName, task, extraParam, cts.Token);
                }
                else
                {
                    await ExecuteTaskCore(taskName, task, extraParam, ct);
                }

                // 延续任务(记录调用链, 检测 A->B->A 循环)
                if (task.Rules.ContinueTasks.Count > 0)
                {
                    var nextChain = chain != null
                        ? new HashSet<string>(chain, StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    nextChain.Add(taskName);

                    if (nextChain.Count > MaxContinueDepth)
                    {
                        Log($"任务 {taskName} 延续链深度超过 {MaxContinueDepth}，已停止继续", 3);
                        return;
                    }

                    foreach (var nextTask in task.Rules.ContinueTasks)
                    {
                        if (nextChain.Contains(nextTask))
                        {
                            Log($"任务 {taskName} 的延续任务 {nextTask} 形成循环调用，已忽略", 3);
                            continue;
                        }
                        if (_settings.Tasks.TryGetValue(nextTask, out var nextEntry))
                        {
                            _ = Task.Run(() => ExecuteTaskSafe(nextTask, nextEntry, extraParam, ct, nextChain));
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested && _schedulerCts.IsCancellationRequested)
                    Log($"任务 {taskName} 因调度器停止而取消", 2);
                else
                    Log($"任务 {taskName} 执行超时 ({task.Rules.TimeoutMs}ms)", 2);
            }
            catch (Exception ex)
            {
                Log($"任务 {taskName} 执行失败: {ex.Message}", 3);
            }
        }

        /// <summary>原子占位一次执行机会(并发安全)</summary>
        private static bool TryReserveExecution(string taskName, int maxExecutions)
        {
            bool reserved;
            lock (_lock)
            {
                if (!_executionCounts.TryGetValue(taskName, out int count))
                    count = 0;
                if (count >= maxExecutions)
                    return false;
                _executionCounts[taskName] = count + 1;
                reserved = true;
            }

            // 锁外落盘, 避免持锁做磁盘 IO
            if (reserved)
                SaveExecutionState();
            return reserved;
        }

        /// <summary>可取消的长延迟(按天分片, 避免超长 TimeSpan 超过 Task.Delay 上限)</summary>
        private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            if (delay <= TimeSpan.Zero)
                return;

            var chunkSize = TimeSpan.FromDays(1);
            while (delay > TimeSpan.Zero)
            {
                var chunk = delay > chunkSize ? chunkSize : delay;
                await Task.Delay(chunk, ct);
                delay -= chunk;
            }
        }

        private static async Task ExecuteTaskCore(string taskName, TaskEntry task, string? extraParam, CancellationToken ct)
        {
            string execute = (task.Execute ?? "").Trim();
            if (string.IsNullOrEmpty(execute))
            {
                Log($"任务 {taskName} 未设置执行内容", 2);
                return;
            }

            // 一个流程可配置多个执行动作: 以换行分隔, 按顺序依次执行(空行忽略)
            var steps = execute.Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (steps.Length == 0)
            {
                Log($"任务 {taskName} 未设置执行内容", 2);
                return;
            }

            Log($"执行任务: {taskName}", 1);
            EventBus.Publish(new TaskExecuteEvent(taskName, execute));

            for (int i = 0; i < steps.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (steps.Length > 1)
                    Log($"执行步骤 {i + 1}/{steps.Length}: {steps[i]}", 1);
                await ExecuteStepCore(taskName, task, steps[i], extraParam, ct);
            }

            ct.ThrowIfCancellationRequested();
        }

        /// <summary>执行单个动作(execute 中的一行)。</summary>
        private static async Task ExecuteStepCore(string taskName, TaskEntry task, string execute, string? extraParam, CancellationToken ct)
        {
            if (execute.StartsWith("script:", StringComparison.OrdinalIgnoreCase))
            {
                string scriptName = execute.Substring(7).Trim();
                string input = task.PassParameters && !string.IsNullOrEmpty(extraParam) ? extraParam : "";
                if (!string.IsNullOrEmpty(input))
                {
                    // 临时修改脚本Input参数
                    if (Scripts.GetSettings().Scripts.TryGetValue(scriptName, out var scriptItem))
                    {
                        var originalInput = scriptItem.Input;
                        scriptItem.Input = input;
                        Scripts.ExecuteScriptByName(scriptName, ct);
                        scriptItem.Input = originalInput;
                    }
                    else
                    {
                        Scripts.ExecuteScriptByName(scriptName, ct);
                    }
                }
                else
                {
                    Scripts.ExecuteScriptByName(scriptName, ct);
                }
            }
            else if (execute.Equals("backup", StringComparison.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                Intelligence.BackupCurrentServer();
            }
            else if (execute.Equals("restart", StringComparison.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                Analyzer.StopServer();
                await Task.Delay(3000, ct);
                Analyzer.StartServer();
            }
            else if (execute.Equals("check_online", StringComparison.OrdinalIgnoreCase))
            {
                SendCommandToTargets(task, "list", "已发送 list 命令检查服务端在线情况");
            }
            else if (execute.Equals("check_tps", StringComparison.OrdinalIgnoreCase))
            {
                SendCommandToTargets(task, "tps", "已发送 tps 命令检查服务端TPS（需要Spark等插件支持）");
            }
            else if (execute.Equals("check_memory", StringComparison.OrdinalIgnoreCase))
            {
                var mcProc = System.Diagnostics.Process.GetCurrentProcess();
                long appMemMB = mcProc.WorkingSet64 / (1024 * 1024);
                Log($"程序内存使用: {appMemMB}MB", 1);

                try
                {
                    var mcProcess = Analyzer.GetServerProcess();
                    if (mcProcess != null && !mcProcess.HasExited)
                    {
                        long mcMemMB = mcProcess.WorkingSet64 / (1024 * 1024);
                        Log($"MC服务端内存使用: {mcMemMB}MB (PID: {mcProcess.Id})", 1);
                    }
                    else
                    {
                        Log("MC服务端进程未运行", 2);
                    }
                }
                catch
                {
                    Log("无法获取MC服务端内存信息", 2);
                }
            }
            else if (execute.Equals("clear_drops", StringComparison.OrdinalIgnoreCase))
            {
                SendCommandToTargets(task, "kill @e[type=item]", "已发送清理掉落物命令");
            }
            else if (execute.Equals("server_stop", StringComparison.OrdinalIgnoreCase))
            {
                Analyzer.StopServer();
                Log("服务端已关闭", 1);
            }
            else if (execute.StartsWith("server_restart", StringComparison.OrdinalIgnoreCase))
            {
                // server_restart 或 server_restart:5 (5分钟后重启)
                int delayMinutes = 0;
                if (execute.Contains(':'))
                {
                    string delayStr = execute.Substring(execute.IndexOf(':') + 1).Trim();
                    int.TryParse(delayStr, out delayMinutes);
                }

                Analyzer.StopServer();
                if (delayMinutes > 0)
                {
                    Log($"服务端已关闭，{delayMinutes}分钟后重启...", 1);
                    await DelayAsync(TimeSpan.FromMinutes(delayMinutes), ct);
                }
                else
                {
                    await Task.Delay(3000, ct);
                }
                Analyzer.StartServer();
                Log("服务端已重启", 1);
            }
            else if (execute.StartsWith("send_command:", StringComparison.OrdinalIgnoreCase))
            {
                string command = execute.Substring(13).Trim();
                SendCommandToTargets(task, command, "已发送命令");
            }
            else if (execute.StartsWith("command:", StringComparison.OrdinalIgnoreCase))
            {
                string command = execute.Substring(8).Trim();
                SendCommandToTargets(task, command, "已发送命令");
            }
            else if (execute.StartsWith("cli:", StringComparison.OrdinalIgnoreCase)
                || execute.StartsWith("panel:", StringComparison.OrdinalIgnoreCase))
            {
                // 面板/RtCli 自身命令(如 .auto list、.fx base、rt status), 不发给 MC 服务端
                string command = execute.Substring(execute.IndexOf(':') + 1).Trim();
                if (string.IsNullOrEmpty(command))
                {
                    Log($"任务 {taskName} 面板命令不能为空", 2);
                }
                else
                {
                    foreach (var instanceId in TargetInstances(task))
                    {
                        var result = Backend.DispatchCommand(command, instanceId);
                        string output = result.Result ?? "";
                        if (output.Length > 200) output = output.Substring(0, 200) + "...";
                        Log($"已执行面板命令[{instanceId}]: {command} -> {(result.Success ? "成功" : "失败")} {output}".Trim(), 1);
                    }
                }
            }
            else
            {
                Log($"任务 {taskName} 未知执行类型: {execute}", 2);
            }

            ct.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// 获取任务执行次数
        /// </summary>
        public static int GetExecutionCount(string taskName)
        {
            lock (_lock)
            {
                return _executionCounts.TryGetValue(taskName, out int count) ? count : 0;
            }
        }

        /// <summary>
        /// 设置任务启用/禁用状态
        /// </summary>
        public static void SetTaskEnabled(string taskName, bool enabled)
        {
            if (!_settings.Tasks.TryGetValue(taskName, out var task))
            {
                Log($"未找到任务: {taskName}", 2);
                return;
            }

            task.Enabled = enabled;
            Log($"任务 {taskName} 已{(enabled ? "启用" : "禁用")}", 1);

            if (enabled && _isRunning)
            {
                // 重新注册该任务
                RegisterTask(taskName, task);
            }
            else if (!enabled)
            {
                // 移除该任务注册的全部 Hangfire 任务(含 OR 多分支)、延迟任务与间隔状态
                RemoveTrackedJobs(taskName);
                // 移除事件订阅
                if (_eventSubscriptions.TryGetValue(taskName, out var subIds))
                {
                    foreach (var subId in subIds)
                    {
                        try { EventBus.UnsubscribeAll(subId); }
                        catch (Exception ex) { Output.Debug(ex, $"取消事件订阅失败: {taskName}", ThisName); }
                    }
                    _eventSubscriptions.Remove(taskName);
                }
            }
        }

        /// <summary>
        /// 列出所有任务状态
        /// </summary>
        public static void ListTasks()
        {
            if (_settings.Tasks.Count == 0)
            {
                Output.Log("没有已配置的计划任务。", 1, ThisName);
                return;
            }

            var table = new Table();
            table.Border(TableBorder.Rounded);
            table.Title = new TableTitle("[cyan]计划任务列表[/]");
            table.AddColumn("标识");
            table.AddColumn("状态");
            table.AddColumn("触发条件");
            table.AddColumn("执行内容");
            table.AddColumn("队列");
            table.AddColumn("已执行");

            foreach (var kvp in _settings.Tasks)
            {
                string status = kvp.Value.Enabled ? "[green]启用[/]" : "[grey]禁用[/]";
                string trigger = Markup.Escape(kvp.Value.Trigger);
                string execute = Markup.Escape((kvp.Value.Execute ?? "").Replace("\r", "").Replace("\n", " → "));
                string queue = Markup.Escape(kvp.Value.Rules.Queue ?? "Fast");
                int count = GetExecutionCount(kvp.Key);

                // 检查目标实例: 指定了其他实例时仅标记, 不再视为"不适用"
                if (!IsAnyTargetAvailable(kvp.Value))
                {
                    status = "[yellow]目标实例不存在[/]";
                }
                else if (kvp.Value.ServerKeys.Count > 0 && !kvp.Value.ServerKeys.Contains(Config.App.CurrentServer))
                {
                    status = "[cyan]指定实例[/]";
                }

                // 检查过期
                if (!string.IsNullOrEmpty(kvp.Value.Rules.ExpireAt) &&
                    DateTime.TryParse(kvp.Value.Rules.ExpireAt, out var exp) && DateTime.Now > exp)
                {
                    status = "[red]已过期[/]";
                }

                // 检查执行次数限制
                if (kvp.Value.Rules.MaxExecutions > 0 && count >= kvp.Value.Rules.MaxExecutions)
                {
                    status = "[darkorange]已完成[/]";
                }

                table.AddRow(
                    Markup.Escape(kvp.Key),
                    status,
                    trigger,
                    execute,
                    queue,
                    count.ToString()
                );
            }

            AnsiConsole.Write(table);
        }

        private class ConditionInfo
        {
            public string Type { get; }
            public string Value { get; }
            public ConditionInfo(string type, string value) { Type = type; Value = value; }
        }
    }
}
