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

        /// <summary>启用该计划任务的服务器标识列表，为空则所有服务器</summary>
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
        ///   事件条件:
        ///     event: ServerStartEvent         - 在指定事件触发时执行
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
        private static readonly Dictionary<string, string> _hangfireJobIds = new();
        private static readonly Dictionary<string, CancellationTokenSource> _delayedTasks = new();
        private static readonly Dictionary<string, List<string>> _eventSubscriptions = new();
        private static readonly object _lock = new();
        private static readonly AsyncLocal<Dictionary<string, string>?> _eventVars = new();

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
        /// 按操作符分割，忽略引号内的内容
        /// </summary>
        private static List<string> SplitByOperator(string input, string op)
        {
            var result = new List<string>();
            var parts = input.Split(new[] { op }, StringSplitOptions.None);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    result.Add(trimmed);
            }
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
        /// 执行脚本并获取返回值（通过stdout捕获）
        /// </summary>
        private static string ExecuteScriptForResult(string scriptName)
        {
            try
            {
                // 捕获脚本输出作为返回值
                var sw = new System.IO.StringWriter();
                var originalOut = Console.Out;
                try
                {
                    Console.SetOut(sw);
                    Scripts.ExecuteScriptByName(scriptName);
                }
                finally
                {
                    Console.SetOut(originalOut);
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
                        return "0"; // TODO: 从Analyzer获取在线人数
                    case "uptime_minutes":
                        return ((int)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMinutes).ToString();
                    case "tps":
                        return "0"; // TODO: 从Analyzer获取TPS
                    case "memory_usage_mb":
                        return ((int)Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024).ToString();
                    case "cpu_usage":
                        return Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds.ToString("F0");
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
        /// 将事件对象的字段提取为变量字典，供 condition 中的 {rt.xxx} 求值使用。
        /// </summary>
        private static Dictionary<string, string> BuildEventVariables(RtEvent e)
        {
            var dict = new Dictionary<string, string>();
            if (e == null) return dict;

            switch (e)
            {
                case LuckPermsChangeEvent lpe:
                    dict["actor_uuid"] = lpe.ActorUuid;
                    dict["actor_name"] = lpe.ActorName;
                    dict["type"] = lpe.Type;
                    dict["acted_uuid"] = lpe.ActedUuid;
                    dict["acted_name"] = lpe.ActedName;
                    dict["action"] = lpe.Action;
                    break;
                case PlayerJoinEvent je:
                    dict["player_name"] = je.PlayerName;
                    dict["player_trigger_time"] = je.PlayerTriggerTime;
                    break;
                case PlayerConnectEvent ce:
                    dict["player_name"] = ce.PlayerName;
                    dict["player_trigger_time"] = ce.PlayerTriggerTime;
                    dict["player_ip"] = ce.PlayerIp;
                    break;
                case PlayerLostEvent le:
                    dict["player_name"] = le.PlayerName;
                    dict["player_trigger_time"] = le.PlayerTriggerTime;
                    dict["player_lost_reason"] = le.PlayerLostReason;
                    break;
                case PlayerLeaveEvent lve:
                    dict["player_name"] = lve.PlayerName;
                    dict["player_trigger_time"] = lve.PlayerTriggerTime;
                    break;
                case PlayerCommandEvent cme:
                    dict["player_name"] = cme.PlayerName;
                    dict["player_trigger_time"] = cme.PlayerTriggerTime;
                    dict["command"] = cme.Command;
                    break;
                case PlayerChatEvent che:
                    dict["player_name"] = che.PlayerName;
                    dict["player_trigger_time"] = che.PlayerTriggerTime;
                    dict["message"] = che.Message;
                    break;
                case PlayerSetModeEvent sme:
                    dict["player_name"] = sme.PlayerName;
                    dict["player_trigger_time"] = sme.PlayerTriggerTime;
                    dict["player_mode"] = sme.PlayerMode;
                    break;
                case CustomPlayerEvent cpe:
                    dict["player_name"] = cpe.PlayerName;
                    dict["player_trigger_time"] = cpe.PlayerTriggerTime;
                    foreach (var kvp in cpe.Parameters)
                        dict[kvp.Key] = kvp.Value;
                    break;
            }

            return dict;
        }

        #endregion

        public static void Initialize()
        {
            LoadSettings();
        }

        public static void LoadSettings()
        {
            _settings = ContentManager.LoadSchedulerSettings();
        }

        public static void Start()
        {
            if (_isRunning) return;

            try
            {
                GlobalConfiguration.Configuration
                    .UseSimpleAssemblyNameTypeSerializer()
                    .UseRecommendedSerializerSettings()
                    .UseInMemoryStorage();

                _jobServer = new BackgroundJobServer(new BackgroundJobServerOptions
                {
                    WorkerCount = 4,
                    Queues = new[] { "long", "fast", "noasync" }
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
                // 取消所有延迟任务
                foreach (var cts in _delayedTasks.Values)
                {
                    try { cts.Cancel(); } catch { }
                }
                _delayedTasks.Clear();

                // 删除所有Hangfire任务
                foreach (var jobId in _hangfireJobIds.Values)
                {
                    try { BackgroundJob.Delete(jobId); } catch { }
                }
                _hangfireJobIds.Clear();

                // 取消事件订阅
                foreach (var kvp in _eventSubscriptions)
                {
                    foreach (var subId in kvp.Value)
                    {
                        try { EventBus.UnsubscribeAll(subId); } catch { }
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

                // 检查服务器标识
                if (task.ServerKeys.Count > 0 && !task.ServerKeys.Contains(Config.App.CurrentServer))
                {
                    Log($"任务 {taskName} 不适用于当前服务器，跳过", 1);
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
                    Task.Run(() => ExecuteTaskSafe(taskName, task));
                }
            }

            Log($"已注册 {registered} 个计划任务", 1);
        }

        private static void RegisterTask(string taskName, TaskEntry task)
        {
            string trigger = task.Trigger.Trim();
            if (string.IsNullOrEmpty(trigger))
            {
                Log($"任务 {taskName} 未设置触发条件", 2);
                return;
            }

            // 解析组合条件
            if (trigger.Contains(" and ", StringComparison.OrdinalIgnoreCase))
            {
                RegisterAndCondition(taskName, task, trigger);
            }
            else if (trigger.Contains(" or ", StringComparison.OrdinalIgnoreCase))
            {
                RegisterOrCondition(taskName, task, trigger);
            }
            else
            {
                RegisterSingleCondition(taskName, task, trigger);
            }
        }

        private static void RegisterSingleCondition(string taskName, TaskEntry task, string condition)
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
                    RegisterCronTask(taskName, task, parsed.Value);
                    break;
                case "every":
                    RegisterIntervalTask(taskName, task, parsed.Value);
                    break;
                case "daily":
                    RegisterDailyTask(taskName, task, parsed.Value);
                    break;
                case "date":
                    RegisterDateTask(taskName, task, parsed.Value);
                    break;
                case "event":
                    RegisterEventTask(taskName, task, parsed.Value);
                    break;
            }
        }

        private static void RegisterAndCondition(string taskName, TaskEntry task, string trigger)
        {
            var parts = trigger.Split(" and ", StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                Log($"任务 {taskName} AND条件格式无效，需要两个条件", 2);
                return;
            }

            var cond1 = ParseCondition(parts[0].Trim());
            var cond2 = ParseCondition(parts[1].Trim());
            if (cond1 == null || cond2 == null)
            {
                Log($"任务 {taskName} AND条件解析失败", 2);
                return;
            }

            // AND条件：时间条件 + 事件条件
            // 当事件触发时，检查时间条件是否满足
            var timeCond = cond1.Type != "event" ? cond1 : cond2;
            var eventCond = cond1.Type == "event" ? cond1 : cond2;

            if (eventCond.Type != "event")
            {
                Log($"任务 {taskName} AND条件需要至少一个事件条件", 2);
                return;
            }

            // 注册事件触发，但执行时检查时间条件
            SubscribeToEvent(taskName, task, eventCond.Value, timeCond);
        }

        private static void RegisterOrCondition(string taskName, TaskEntry task, string trigger)
        {
            var parts = trigger.Split(" or ", StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                RegisterSingleCondition(taskName, task, part.Trim());
            }
        }

        private static ConditionInfo? ParseCondition(string condition)
        {
            if (condition.StartsWith("cron:", StringComparison.OrdinalIgnoreCase))
                return new ConditionInfo("cron", condition.Substring(5).Trim());
            if (condition.StartsWith("every:", StringComparison.OrdinalIgnoreCase))
                return new ConditionInfo("every", condition.Substring(6).Trim());
            if (condition.StartsWith("daily:", StringComparison.OrdinalIgnoreCase))
                return new ConditionInfo("daily", condition.Substring(6).Trim());
            if (condition.StartsWith("date:", StringComparison.OrdinalIgnoreCase))
                return new ConditionInfo("date", condition.Substring(5).Trim());
            if (condition.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                return new ConditionInfo("event", condition.Substring(6).Trim());

            return null;
        }

        private static void RegisterCronTask(string taskName, TaskEntry task, string cronExpr)
        {
            try
            {
                RecurringJob.AddOrUpdate(
                    taskName,
                    () => HangfireExecute(taskName),
                    cronExpr);
                _hangfireJobIds[taskName] = taskName;
                Log($"任务 {taskName} 已注册CRON: {cronExpr}", 1);
            }
            catch (Exception ex)
            {
                Log($"任务 {taskName} CRON表达式无效: {cronExpr} - {ex.Message}", 2);
            }
        }

        private static void RegisterIntervalTask(string taskName, TaskEntry task, string minutesStr)
        {
            if (!int.TryParse(minutesStr, out int minutes) || minutes <= 0)
            {
                Log($"任务 {taskName} 间隔分钟数无效: {minutesStr}", 2);
                return;
            }

            string cronExpr = $"0 */{minutes} * * * *";
            RegisterCronTask(taskName, task, cronExpr);
        }

        private static void RegisterDailyTask(string taskName, TaskEntry task, string timeStr)
        {
            var times = timeStr.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var time in times)
            {
                var trimmed = time.Trim();
                if (!TimeSpan.TryParse(trimmed, out var ts))
                {
                    Log($"任务 {taskName} 时间格式无效: {trimmed}", 2);
                    continue;
                }

                string suffix = times.Length > 1 ? $"_{trimmed.Replace(":", "")}" : "";
                string name = taskName + suffix;
                string cronExpr = $"{ts.Minutes} {ts.Hours} * * *";
                RecurringJob.AddOrUpdate(
                    name,
                    () => HangfireExecute(taskName),
                    cronExpr);
                _hangfireJobIds[name] = name;
            }
            Log($"任务 {taskName} 已注册每日执行: {timeStr}", 1);
        }

        private static void RegisterDateTask(string taskName, TaskEntry task, string dateStr)
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
            _delayedTasks[taskName] = cts;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, cts.Token);
                    await ExecuteTaskSafe(taskName, task);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log($"任务 {taskName} 延迟执行失败: {ex.Message}", 3);
                }
            }, cts.Token);

            Log($"任务 {taskName} 已注册定时执行: {dateStr}", 1);
        }

        private static void RegisterEventTask(string taskName, TaskEntry task, string eventName)
        {
            SubscribeToEvent(taskName, task, eventName, null);
        }

        private static void SubscribeToEvent(string taskName, TaskEntry task, string eventName, ConditionInfo? timeCond)
        {
            var subId = $"Scheduler_{taskName}";
            if (!_eventSubscriptions.ContainsKey(taskName))
                _eventSubscriptions[taskName] = new List<string>();
            _eventSubscriptions[taskName].Add(subId);

            Action<RtEvent> handler = (e) =>
            {
                // 检查服务器标识
                if (task.ServerKeys.Count > 0 && !task.ServerKeys.Contains(Config.App.CurrentServer))
                    return;

                // 如果有AND时间条件，检查是否满足
                if (timeCond != null && !CheckTimeCondition(timeCond))
                    return;

                // 检查执行次数
                if (task.Rules.MaxExecutions > 0)
                {
                    lock (_lock)
                    {
                        if (!_executionCounts.TryGetValue(taskName, out int count))
                            count = 0;
                        if (count >= task.Rules.MaxExecutions)
                            return;
                    }
                }

                // 检查过期
                if (!string.IsNullOrEmpty(task.Rules.ExpireAt))
                {
                    if (DateTime.TryParse(task.Rules.ExpireAt, out var expireTime) && DateTime.Now > expireTime)
                        return;
                }

                string param = task.PassParameters ? e.EventName : "";
                var evtVars = BuildEventVariables(e);
                Task.Run(async () =>
                {
                    _eventVars.Value = evtVars;
                    try
                    {
                        await ExecuteTaskSafe(taskName, task, param);
                    }
                    finally
                    {
                        _eventVars.Value = null;
                    }
                });
            };

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
                case "CustomPlayerEvent":
                default:
                    // 自定义事件名和CustomPlayerEvent都通过CustomPlayerEvent订阅
                    EventBus.Subscribe<CustomPlayerEvent>(e => handler(e), subId);
                    break;
            }

            Log($"任务 {taskName} 已订阅事件: {eventName}", 1);
        }

        private static bool CheckTimeCondition(ConditionInfo timeCond)
        {
            switch (timeCond.Type)
            {
                case "daily":
                    var times = timeCond.Value.Split(',');
                    foreach (var t in times)
                    {
                        if (TimeSpan.TryParse(t.Trim(), out var ts))
                        {
                            var now = DateTime.Now.TimeOfDay;
                            if (Math.Abs((now - ts).TotalMinutes) < 5)
                                return true;
                        }
                    }
                    return false;
                case "every":
                    if (int.TryParse(timeCond.Value, out int minutes) && minutes > 0)
                    {
                        return DateTime.Now.Minute % minutes == 0;
                    }
                    return false;
                case "date":
                    if (DateTime.TryParse(timeCond.Value, out var targetDate))
                    {
                        return DateTime.Now >= targetDate;
                    }
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Hangfire任务执行入口（无可选参数，供表达式树调用）
        /// </summary>
        public static async Task HangfireExecute(string taskName)
        {
            if (_settings.Tasks.TryGetValue(taskName, out var task))
            {
                await ExecuteTaskSafe(taskName, task, null);
            }
        }

        public static async Task ExecuteTaskSafe(string taskName, TaskEntry task, string? extraParam = null)
        {
            try
            {
                // 检查延迟
                if (task.Rules.DelayMinutes > 0)
                {
                    await Task.Delay(task.Rules.DelayMinutes * 60 * 1000);
                }
                else if (!string.IsNullOrEmpty(task.Rules.DelayUntil))
                {
                    if (DateTime.TryParse(task.Rules.DelayUntil, out var delayTime))
                    {
                        var wait = delayTime - DateTime.Now;
                        if (wait.TotalSeconds > 0)
                            await Task.Delay((int)wait.TotalMilliseconds);
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

                // 超时处理
                if (task.Rules.TimeoutMs > 0)
                {
                    var cts = new CancellationTokenSource(task.Rules.TimeoutMs);
                    await ExecuteTaskCore(taskName, task, extraParam, cts.Token);
                }
                else
                {
                    await ExecuteTaskCore(taskName, task, extraParam, CancellationToken.None);
                }

                // 更新执行次数
                lock (_lock)
                {
                    if (!_executionCounts.ContainsKey(taskName))
                        _executionCounts[taskName] = 0;
                    _executionCounts[taskName]++;
                }

                // 延续任务
                if (task.Rules.ContinueTasks.Count > 0)
                {
                    foreach (var nextTask in task.Rules.ContinueTasks)
                    {
                        if (_settings.Tasks.TryGetValue(nextTask, out var nextEntry))
                        {
                            _ = Task.Run(() => ExecuteTaskSafe(nextTask, nextEntry, extraParam));
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log($"任务 {taskName} 执行超时 ({task.Rules.TimeoutMs}ms)", 2);
            }
            catch (Exception ex)
            {
                Log($"任务 {taskName} 执行失败: {ex.Message}", 3);
            }
        }

        private static async Task ExecuteTaskCore(string taskName, TaskEntry task, string? extraParam, CancellationToken ct)
        {
            string execute = task.Execute.Trim();
            if (string.IsNullOrEmpty(execute))
            {
                Log($"任务 {taskName} 未设置执行内容", 2);
                return;
            }

            Log($"执行任务: {taskName}", 1);
            EventBus.Publish(new TaskExecuteEvent(taskName, execute));

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
                        Scripts.ExecuteScriptByName(scriptName);
                        scriptItem.Input = originalInput;
                    }
                    else
                    {
                        Scripts.ExecuteScriptByName(scriptName);
                    }
                }
                else
                {
                    Scripts.ExecuteScriptByName(scriptName);
                }
            }
            else if (execute.Equals("backup", StringComparison.OrdinalIgnoreCase))
            {
                Intelligence.BackupCurrentServer();
            }
            else if (execute.Equals("restart", StringComparison.OrdinalIgnoreCase))
            {
                Analyzer.StopServer();
                await Task.Delay(3000);
                Analyzer.StartServer();
            }
            else if (execute.Equals("check_online", StringComparison.OrdinalIgnoreCase))
            {
                Analyzer.SendCommand("list");
                Log("已发送 list 命令检查服务端在线情况", 1);
            }
            else if (execute.Equals("check_tps", StringComparison.OrdinalIgnoreCase))
            {
                Analyzer.SendCommand("tps");
                Log("已发送 tps 命令检查服务端TPS（需要Spark等插件支持）", 1);
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
                Analyzer.SendCommand("kill @e[type=item]");
                Log("已发送清理掉落物命令", 1);
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
                    await Task.Delay(delayMinutes * 60 * 1000);
                }
                else
                {
                    await Task.Delay(3000);
                }
                Analyzer.StartServer();
                Log("服务端已重启", 1);
            }
            else if (execute.StartsWith("send_command:", StringComparison.OrdinalIgnoreCase))
            {
                string command = execute.Substring(13).Trim();
                Analyzer.SendCommand(command);
                Log($"已发送命令: {command}", 1);
            }
            else if (execute.StartsWith("command:", StringComparison.OrdinalIgnoreCase))
            {
                string command = execute.Substring(8).Trim();
                Analyzer.SendCommand(command);
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
                // 移除Hangfire任务
                if (_hangfireJobIds.TryGetValue(taskName, out var jobId))
                {
                    try { RecurringJob.RemoveIfExists(taskName); } catch { }
                }
                // 移除延迟任务
                if (_delayedTasks.TryGetValue(taskName, out var cts))
                {
                    try { cts.Cancel(); } catch { }
                    _delayedTasks.Remove(taskName);
                }
                // 移除事件订阅
                if (_eventSubscriptions.TryGetValue(taskName, out var subIds))
                {
                    foreach (var subId in subIds)
                    {
                        try { EventBus.UnsubscribeAll(subId); } catch { }
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
                string execute = Markup.Escape(kvp.Value.Execute);
                string queue = Markup.Escape(kvp.Value.Rules.Queue ?? "Fast");
                int count = GetExecutionCount(kvp.Key);

                // 检查是否适用于当前服务器
                if (kvp.Value.ServerKeys.Count > 0 && !kvp.Value.ServerKeys.Contains(Config.App.CurrentServer))
                {
                    status = "[yellow]不适用[/]";
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
