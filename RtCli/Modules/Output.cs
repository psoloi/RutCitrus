using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace RtCli.Modules
{
    public class Output
    {
        static string c_info = "[white]|[/][green]信息[/][white]| [/]";
        static string c_error = "[white]|[/][red]错误[/][white]| [/]";
        static string c_warn = "[white]|[/][yellow]警告[/][white]| [/]";

        private static ILogger? _logger;
        private static TextWriter? _originalConsoleOut;
        private static ConsoleInterceptor? _interceptor;
        private static bool _isLoggingInitialized = false;
        private static readonly object _logLock = new object();

        // 控制台显示过滤正则缓存(配置变化时自动重编译)
        private static List<Regex> _stripRegexes = new List<Regex>();
        private static int _stripPatternsHash = 0;
        private static readonly object _stripCacheLock = new object();

        // 敏感信息脱敏正则: 匹配 key: value / key=value 结构中的敏感键值(值替换为 ***)
        // 仅匹配键值结构，避免误伤正文中出现的敏感词本身
        private static readonly Regex _sensitivePattern = new Regex(
            @"\b(rcon_password|management_secret|grpc_auth_key|api_key|auth_key|password|secret|token)\b(\s*[:=]\s*)(""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;\r\n]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 日志广播钩子：参数依次为 timestamp, level, source, message。
        /// 由 Backend.Initialize 挂载，将日志推送到已连接的 gRPC 面板。
        /// </summary>
        public static Action<string, int, string, string>? OnLogBroadcast;

        public static void InitializeLogging()
        {
            if (_isLoggingInitialized) return;

            lock (_logLock)
            {
                if (_isLoggingInitialized) return;

                try
                {
                    string logsPath = Unit.Config.LogsPath;
                    if (!Directory.Exists(logsPath))
                    {
                        Directory.CreateDirectory(logsPath);
                    }

                    string logFileName = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log";
                    string logFilePath = Path.Combine(logsPath, logFileName);

                    _logger = new LoggerConfiguration()
                        .MinimumLevel.Debug()
                        .WriteTo.File(logFilePath,
                            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                        .CreateLogger();

                    _originalConsoleOut = Console.Out;
                    _interceptor = new ConsoleInterceptor(_originalConsoleOut, _logger);
                    Console.SetOut(_interceptor);

                    _isLoggingInitialized = true;
                }
                catch (Exception ex)
                {
                    AnsiConsole.Markup($"[red]初始化日志系统失败: {Markup.Escape(ex.Message)}[/]\n");
                }
            }
        }

        public static void CloseLogging()
        {
            lock (_logLock)
            {
                if (!_isLoggingInitialized) return;

                try
                {
                    if (_originalConsoleOut != null)
                    {
                        Console.SetOut(_originalConsoleOut);
                    }
                    (_logger as IDisposable)?.Dispose();
                    _logger = null;
                    _interceptor = null;
                    _isLoggingInitialized = false;
                }
                catch { }
            }
        }

        protected internal static void TextBlock(string msg, int msg_type, string Task)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            string info = $"[white on dodgerblue2][[{time}]][/]" + $"[white on steelblue1][[MainThread - {Task}]][/]" + " [black on green]信息[/] ";
            string error = $"[white on dodgerblue2][[{time}]][/]" + $"[white on steelblue1][[MainThread - {Task}]][/]" + " [black on red]错误[/] ";
            string warn = $"[white on dodgerblue2][[{time}]][/]" + $"[white on steelblue1][[MainThread - {Task}]][/]" + " [black on gold1]警告[/] ";

            msg = RedactSensitive(msg);
            string plainMsg = StripMarkup(msg);

            try
            {
                switch (msg_type)
                {
                    case 1:
                        AnsiConsole.Markup(info + msg + "\n");
                        _logger?.Information("[MainThread - {Task}] {Message}", Task, plainMsg);
                        break;
                    case 2:
                        AnsiConsole.Markup(warn + msg + "\n");
                        _logger?.Warning("[MainThread - {Task}] {Message}", Task, plainMsg);
                        break;
                    case 3:
                        AnsiConsole.Markup(error + msg + "\n");
                        _logger?.Error("[MainThread - {Task}] {Message}", Task, plainMsg);
                        break;
                    default:
                        AnsiConsole.Markup($"[white on dodgerblue2]{time}[/]" + $"[white on steelblue1][[MainThread - {Task}]][/]" + "[black on white]调试[/] " + msg + "\n");
                        _logger?.Debug("[MainThread - {Task}] {Message}", Task, plainMsg);
                        break;
                }
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Could not find color or style"))
            {
                // 消息包含无效的Spectre标记，回退到安全转义输出
                string safeMsg = Markup.Escape(plainMsg);
                switch (msg_type)
                {
                    case 1: AnsiConsole.Markup(info + safeMsg + "\n"); break;
                    case 2: AnsiConsole.Markup(warn + safeMsg + "\n"); break;
                    case 3: AnsiConsole.Markup(error + safeMsg + "\n"); break;
                    default: AnsiConsole.Markup($"[white on dodgerblue2]{time}[/]" + $"[white on steelblue1][[MainThread - {Task}]][/]" + "[black on white]调试[/] " + safeMsg + "\n"); break;
                }
                _logger?.Warning("[MainThread - {Task}] 输出消息包含无效Spectre标记，已回退转义: {Message}", Task, plainMsg);
            }
        }

        /// <summary>
        /// 敏感信息脱敏: 将消息中 key: value / key=value 形式的敏感值替换为 ***。
        /// 应用于控制台显示、日志文件记录与 gRPC 面板广播之前的统一入口。
        /// </summary>
        internal static string RedactSensitive(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            // 快速跳过: 不含 ':' 与 '=' 的行不可能命中键值结构
            if (text.IndexOf(':') < 0 && text.IndexOf('=') < 0) return text;
            return _sensitivePattern.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + "***");
        }

        /// <summary>
        /// 该方法用于所有的非错误日志输出,普通输出类型为1,警告类型为2,错误类型为3,兼容类型为0
        /// 基础输出的格式[时;分;秒] |信息| [线程Main/XXX - Task] (调用程序名称) 消息
        /// </summary>
        public static void Log(string msg, int msg_type, string? names)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            msg = RedactSensitive(msg);
            string plainMsg = StripMarkup(msg);
            // 仅用于控制台显示的过滤(如移除MC日志行首时间戳)，不影响日志记录和gRPC广播
            string displayMsg = StripForConsole(msg);
            string name = string.IsNullOrEmpty(names) ? "Null" : names;
            string threadName = Thread.CurrentThread.Name ?? "Null";

            string prefix = $"[white][[{time}]][/] " + (msg_type switch
            {
                2 => c_warn,
                3 => c_error,
                _ => c_info
            }) + $"[white][[{threadName}-{Thread.CurrentThread.ManagedThreadId}]][/] " + $"[dodgerblue1]({Markup.Escape(name)})[/] ";

            try
            {
                switch (msg_type)
                {
                    case 0:
                        AnsiConsole.Markup(prefix + Markup.Escape(displayMsg) + "\n");
                        _logger?.Debug("[{Thread}-{ThreadId}] ({Name}) {Message}", threadName, Thread.CurrentThread.ManagedThreadId, name, plainMsg);
                        break;
                    case 1:
                        AnsiConsole.Markup(prefix + displayMsg + "\n");
                        _logger?.Information("[{Thread}-{ThreadId}] ({Name}) {Message}", threadName, Thread.CurrentThread.ManagedThreadId, name, plainMsg);
                        break;
                    case 2:
                        AnsiConsole.Markup(prefix + displayMsg + "\n");
                        _logger?.Warning("[{Thread}-{ThreadId}] ({Name}) {Message}", threadName, Thread.CurrentThread.ManagedThreadId, name, plainMsg);
                        break;
                    case 3:
                        AnsiConsole.Markup(prefix + displayMsg + "\n");
                        _logger?.Error("[{Thread}-{ThreadId}] ({Name}) {Message}", threadName, Thread.CurrentThread.ManagedThreadId, name, plainMsg);
                        break;
                    default:
                        AnsiConsole.Markup($"[white][[{time}]][/] " + "[white]|[/][yellow]调试[/][white]| [/]" + $"[white][[{threadName}-{Thread.CurrentThread.ManagedThreadId}]][/] " + $"[dodgerblue1]({Markup.Escape(name)})[/] " + Markup.Escape(displayMsg) + "\n");
                        _logger?.Debug("[{Thread}-{ThreadId}] ({Name}) {Message}", threadName, Thread.CurrentThread.ManagedThreadId, name, plainMsg);
                        break;
                }
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Could not find color or style"))
            {
                // 消息包含无效的Spectre标记，回退到安全转义输出
                string safeMsg = Markup.Escape(StripMarkup(displayMsg));
                try
                {
                    switch (msg_type)
                    {
                        case 0: AnsiConsole.Markup(prefix + safeMsg + "\n"); break;
                        case 1: AnsiConsole.Markup(prefix + safeMsg + "\n"); break;
                        case 2: AnsiConsole.Markup(prefix + safeMsg + "\n"); break;
                        case 3: AnsiConsole.Markup(prefix + safeMsg + "\n"); break;
                        default: AnsiConsole.Markup($"[white][[{time}]][/] " + "[white]|[/][yellow]调试[/][white]| [/]" + $"[white][[{threadName}-{Thread.CurrentThread.ManagedThreadId}]][/] " + $"[dodgerblue1]({Markup.Escape(name)})[/] " + safeMsg + "\n"); break;
                    }
                }
                catch { Console.WriteLine($"[{time}] ({name}) {plainMsg}"); }
                _logger?.Warning("[{Thread}-{ThreadId}] ({Name}) 输出消息包含无效Spectre标记，已回退转义: {Message}", threadName, Thread.CurrentThread.ManagedThreadId, name, plainMsg);
            }

            // 广播日志到已连接的 gRPC 面板(使用原始plainMsg，不受控制台过滤影响)
            if (OnLogBroadcast != null)
            {
                try { OnLogBroadcast(time, msg_type, name, plainMsg); }
                catch { }
            }
        }

        private static string StripMarkup(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            
            var result = new StringBuilder();
            int i = 0;
            
            while (i < text.Length)
            {
                if (text[i] == '[')
                {
                    if (i + 1 < text.Length && text[i + 1] == '[')
                    {
                        result.Append('[');
                        i += 2;
                        continue;
                    }
                    
                    int end = text.IndexOf(']', i);
                    if (end > i)
                    {
                        i = end + 1;
                        continue;
                    }
                }
                else if (text[i] == ']')
                {
                    if (i + 1 < text.Length && text[i + 1] == ']')
                    {
                        result.Append(']');
                        i += 2;
                        continue;
                    }
                }
                
                result.Append(text[i]);
                i++;
            }
            
            return result.ToString();
        }

        /// <summary>
        /// 获取控制台显示过滤正则缓存(配置变化时自动重编译)
        /// </summary>
        private static List<Regex> GetStripRegexes()
        {
            var patterns = Unit.Config.App?.ConsoleStripPatterns;
            if (patterns == null || patterns.Count == 0)
                return _stripRegexes.Count == 0 ? _stripRegexes : new List<Regex>();

            // 用模式字符串的哈希作为缓存键，配置变化时重新编译
            int hash = 0;
            for (int i = 0; i < patterns.Count; i++)
            {
                if (!string.IsNullOrEmpty(patterns[i]))
                    hash ^= patterns[i].GetHashCode(StringComparison.Ordinal);
            }

            lock (_stripCacheLock)
            {
                if (hash == _stripPatternsHash && _stripRegexes.Count > 0)
                    return _stripRegexes;

                var compiled = new List<Regex>(patterns.Count);
                foreach (var p in patterns)
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    try
                    {
                        compiled.Add(new Regex(p, RegexOptions.Compiled, TimeSpan.FromMilliseconds(200)));
                    }
                    catch
                    {
                        // 忽略无效正则
                    }
                }

                _stripRegexes = compiled;
                _stripPatternsHash = hash;
                return compiled;
            }
        }

        /// <summary>
        /// 仅用于控制台显示的消息过滤(移除MC服务器日志行首时间戳等)。
        /// 不影响日志文件记录、gRPC广播以及Analyzer中的消息流处理。
        /// </summary>
        private static string StripForConsole(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return msg;
            var regexes = GetStripRegexes();
            if (regexes.Count == 0) return msg;

            string result = msg;
            for (int i = 0; i < regexes.Count; i++)
            {
                result = regexes[i].Replace(result, "");
            }
            return result;
        }

        private static bool _crashAssistantRunning = false;
        private static readonly object _crashLock = new object();
        
        public static async Task StartCrashAssistantAsync(Exception ex)
        {
            await Task.Run(() => CrashAssistant(ex));
        }

        /// <summary>
        /// CrashAssistant错误处理
        /// </summary>
        public static void CrashAssistant(Exception ex)
        {
            lock (_crashLock)
            {
                if (_crashAssistantRunning)
                    return;
                _crashAssistantRunning = true;
            }

            try
            {
                Log("[red][[CrashAssistant]] 已捕获到一个未被处理异常[/]\n", 3, "CrashAssistant");
                string time = DateTime.Now.ToString("HH:mm:ss");
                AnsiConsole.Markup($"[white on red][[{time}]][/][white on darkred][[CrashAssistant]][/]\n\n");

                var table = new Table()
                  .Border(TableBorder.Heavy)
                  .AddColumn("[yellow]属性[/]")
                  .AddColumn("[yellow]值[/]");

                table.AddRow("异常类型", Markup.Escape(ex.GetType().Name));
                table.AddRow("异常消息", Markup.Escape(ex.Message));
                table.AddRow("发生时间", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                table.AddRow("线程", Markup.Escape(Thread.CurrentThread.Name ?? "Unknown"));
                if (ex.InnerException != null)
                {
                    table.AddRow("内部异常类型", Markup.Escape(ex.InnerException.GetType().Name));
                    table.AddRow("内部异常消息", Markup.Escape(ex.InnerException.Message));
                }
                AnsiConsole.Write(table);

                _logger?.Error(ex, "[CrashAssistant] 未处理的异常: {ExceptionType} - {Message}", ex.GetType().Name, ex.Message);

                if (ex.InnerException != null)
                {
                    AnsiConsole.Markup("[yellow]内部异常:[/]\n");
                    AnsiConsole.Markup($"  [yellow]类型:[/] [white]{Markup.Escape(ex.InnerException.GetType().Name)}[/]\n");
                    AnsiConsole.Markup($"  [yellow]消息:[/] [white]{Markup.Escape(ex.InnerException.Message)}[/]\n\n");
                }

                AnsiConsole.Markup("[yellow]堆栈跟踪:[/]\n");
                AnsiConsole.Markup($"[grey]{Markup.Escape(ex.StackTrace ?? "无堆栈信息")}[/]\n\n");


            }
            catch (Exception innerEx)
            {
                AnsiConsole.Markup($"[red]CrashAssistant 自身发生错误: {Markup.Escape(innerEx.Message)}[/]\n");
                _logger?.Error(innerEx, "[CrashAssistant] CrashAssistant 自身发生错误");
            }
            finally
            {
                lock (_crashLock)
                {
                    _crashAssistantRunning = false;
                }
            }
        }

        /// <summary>
        /// 根据条件判断输出错误报告
        /// </summary>
        public static void ReportError(Exception ex, bool critical = false, string? additionalInfo = null)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            string severity = critical ? "[white on red]严重错误[/]" : "[white on cyan]一般错误[/]";

            string? suggestion = null;

            switch (ex)
            {
                case UnauthorizedAccessException:
                    severity = "[white on darkorange]系统IO错误[/]";
                    suggestion = "请检查文件或目录的访问权限，尝试以管理员身份运行程序，或确认当前用户是否有读写权限。";
                    break;
                case DirectoryNotFoundException:
                    severity = "[white on darkorange]系统IO错误[/]";
                    suggestion = "请检查目录路径是否正确，确认目录是否存在，或尝试手动创建所需目录。";
                    break;
                case FileNotFoundException:
                    severity = "[white on darkorange]系统IO错误[/]";
                    suggestion = "请检查文件路径是否正确，确认文件是否存在，或尝试重新安装/恢复缺失的文件。";
                    break;
                case PathTooLongException:
                    severity = "[white on darkorange]系统IO错误[/]";
                    suggestion = "文件或目录路径过长，请将程序移动到更短的路径下运行，或缩短目录/文件名称。";
                    break;
                case DriveNotFoundException:
                    severity = "[white on darkorange]系统IO错误[/]";
                    suggestion = "请检查驱动器是否已连接，确认磁盘/USB设备是否正常挂载。";
                    break;
                case IOException:
                    severity = "[white on darkorange]系统IO错误[/]";
                    suggestion = "请检查磁盘空间是否充足，文件是否被其他程序占用，或磁盘是否存在损坏。";
                    break;
                case System.Net.Sockets.SocketException:
                    severity = "[white on darkorange]网络错误[/]";
                    suggestion = "请检查网络连接是否正常，确认端口是否被占用，或检查防火墙设置是否阻止了连接。";
                    break;
                case OperationCanceledException:
                    severity = "[white on yellow]操作取消[/]";
                    suggestion = "操作已被取消，可能是由于超时或用户主动中止。";
                    break;
                case OutOfMemoryException:
                    severity = "[white on red]内存不足[/]";
                    suggestion = "程序内存不足，请关闭其他占用内存的程序，或增加系统可用内存。";
                    break;
            case InvalidOperationException invalidOpEx when invalidOpEx.Message.Contains("Could not find color or style"):
                    severity = "[white on magenta]格式兼容错误[/]";
                    suggestion = "此错误通常由扩展或脚本输出的消息包含不兼容的Spectre.Console颜色标记导致。请检查最近使用的扩展是否正确转义了输出文本（使用 Markup.Escape），或在 github.com/psoloi/RutCitrus/issues 提交反馈。";
                    break;
                default:
                    suggestion = "错误可能未知，可通过github.com/psoloi/RutCitrus/issues提供反馈";
                    break;
            }

            AnsiConsole.Markup($"[white on red][[{time}]][/][white on steelblue1][[ErrorReport]][/] {severity}\n");

            if (!string.IsNullOrEmpty(additionalInfo))
            {
                AnsiConsole.Markup($"[yellow]附加信息:[/] [white]{Markup.Escape(additionalInfo)}[/]\n");
            }

            if (suggestion != null)
            {
                AnsiConsole.Markup($"[cyan]解决建议:[/] [white]{suggestion}[/]\n");
            }

            _logger?.Error(ex, "[ErrorReport] {Severity} - {ExceptionType}: {Message}", StripMarkup(severity), ex.GetType().Name, ex.Message);

            CrashAssistant(ex);
        }
    }

    internal class ConsoleInterceptor : TextWriter
    {
        private readonly TextWriter _originalOut;
        private readonly ILogger _logger;

        public override Encoding Encoding => _originalOut.Encoding;

        public ConsoleInterceptor(TextWriter originalOut, ILogger logger)
        {
            _originalOut = originalOut;
            _logger = logger;
        }

        public override void Write(char value)
        {
            _originalOut.Write(value);
        }

        public override void Write(string? value)
        {
            _originalOut.Write(value);
            if (!string.IsNullOrEmpty(value))
            {
                _logger.Debug("{Message}", Output.RedactSensitive(value));
            }
        }

        public override void WriteLine(string? value)
        {
            _originalOut.WriteLine(value);
            if (!string.IsNullOrEmpty(value))
            {
                _logger.Debug("{Message}", Output.RedactSensitive(value));
            }
        }

        public override void WriteLine()
        {
            _originalOut.WriteLine();
        }
    }
}
