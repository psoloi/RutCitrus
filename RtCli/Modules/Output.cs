using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
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
            
            string plainMsg = StripMarkup(msg);
            
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

        /// <summary>
        /// 该方法用于所有的非错误日志输出
        /// 基础输出的格式[时;分;秒] |信息| [线程Main/XXX - Task] (调用程序名称) 消息
        /// </summary>
        public static void Log(string msg, int msg_type, string name)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            string plainMsg = StripMarkup(msg);
            string threadName = Thread.CurrentThread.Name ?? "Unknown";
            
            switch (msg_type)
            {
                case 1:
                    AnsiConsole.Markup($"[white][[{time}]][/] " + c_info + $"[white][[{threadName}-{Thread.CurrentThread.ManagedThreadId}]][/] " + $"[dodgerblue1]({Markup.Escape(name)})[/] " + msg + "\n");
                    _logger?.Information("[{Thread}-{ThreadId}] ({Name}) {Message}", threadName, Thread.CurrentThread.ManagedThreadId, name, plainMsg);
                    break;
                case 2:
                    AnsiConsole.Markup($"[white][[{time}]][/] " + c_warn + $"[white][[{threadName}-{Thread.CurrentThread.ManagedThreadId}]][/] " + $"[dodgerblue1]({Markup.Escape(name)})[/] " + msg + "\n");
                    _logger?.Warning("[{Thread}-{ThreadId}] ({Name}) {Message}", threadName, Thread.CurrentThread.ManagedThreadId, name, plainMsg);
                    break;
                case 3:
                    AnsiConsole.Markup($"[white][[{time}]][/] " + c_error + $"[white][[{threadName}-{Thread.CurrentThread.ManagedThreadId}]][/] " + $"[dodgerblue1]({Markup.Escape(name)})[/] " + msg + "\n");
                    _logger?.Error("[{Thread}-{ThreadId}] ({Name}) {Message}", threadName, Thread.CurrentThread.ManagedThreadId, name, plainMsg);
                    break;
                default:
                    AnsiConsole.Markup($"[white][[{time}]][/] " + "[white]|[/][yellow]调试[/][white]| [/]" + $"[white][[{threadName}-{Thread.CurrentThread.ManagedThreadId}]][/] " + $"[dodgerblue1]({Markup.Escape(name)})[/] " + Markup.Escape(msg) + "\n");
                    _logger?.Debug("[{Thread}-{ThreadId}] ({Name}) {Message}", threadName, Thread.CurrentThread.ManagedThreadId, name, plainMsg);
                    break;
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
                _logger.Debug("{Message}", value);
            }
        }

        public override void WriteLine(string? value)
        {
            _originalOut.WriteLine(value);
            if (!string.IsNullOrEmpty(value))
            {
                _logger.Debug("{Message}", value);
            }
        }

        public override void WriteLine()
        {
            _originalOut.WriteLine();
        }
    }
}
