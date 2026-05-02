using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RtCli.Modules.Unit
{
    public class AppConfig
    {

        public bool CheckJava { get; set; } = true;
        public bool CheckDotNet { get; set; } = true;
        public bool CheckOSBit { get; set; } = true;
        public bool SkipSelect { get; set; } = true;

        public string ServerName { get; set; } = "myserver";
        public int ServerPort { get; set; } = 7789;

        public string AnalyzerMode { get; set; } = "RCON";
        public string WorkPath { get; set; } = "";
        public string RunServerFlags { get; set; } = "-Xms1024M -Xmx1024M -XX:+AlwaysPreTouch -XX:+DisableExplicitGC -XX:+ParallelRefProcEnabled -XX:+PerfDisableSharedMem -XX:+UnlockExperimentalVMOptions -XX:+UseG1GC -XX:G1HeapRegionSize=8M -XX:G1HeapWastePercent=5 -XX:G1MaxNewSizePercent=40 -XX:G1MixedGCCountTarget=4 -XX:G1MixedGCLiveThresholdPercent=90 -XX:G1NewSizePercent=30 -XX:G1RSetUpdatingPauseTimePercent=5 -XX:G1ReservePercent=20 -XX:InitiatingHeapOccupancyPercent=15 -XX:MaxGCPauseMillis=200 -XX:MaxTenuringThreshold=1 -XX:SurvivorRatio=32 -jar server.jar --nogui";
        public string RconHost { get; set; } = "127.0.0.1";
        public int RconPort { get; set; } = 25575;
        public string RconPassword { get; set; } = "";

        public string Debug { get; set; } = "no";

    }

    public static class Config
    {
        private static readonly string DataDirectory = "Content/Data";
        private static readonly string LogsDirectory = "Content/Logs";
        private static readonly string absoluteDataPath = Path.GetFullPath(DataDirectory);
        private static readonly string absoluteLogsPath = Path.GetFullPath(LogsDirectory);
        private static readonly string ConfigFileName = "config.yml";
        private static bool _isInitialized = false;

        public static string DataPath => absoluteDataPath;
        public static string LogsPath => absoluteLogsPath;
        public static AppConfig App { get; private set; } = new AppConfig();

        public static void Initialize()
        {
            if (_isInitialized) return;

            if (!Directory.Exists(absoluteDataPath))
            {
                Directory.CreateDirectory(absoluteDataPath);
                Output.Log($"创建数据目录: {absoluteDataPath}", 1, "Config");
            }

            if (!Directory.Exists(absoluteLogsPath))
            {
                Directory.CreateDirectory(absoluteLogsPath);
                Output.Log($"创建日志目录: {absoluteLogsPath}", 1, "Config");
            }

            LoadConfig();
            _isInitialized = true;
        }

        private static void LoadConfig()
        {
            string configPath = Path.Combine(absoluteDataPath, ConfigFileName);

            if (!File.Exists(configPath))
            {
                SaveConfig(configPath);
                Output.Log($"创建配置文件: {configPath}", 1, "Config");
                return;
            }

            try
            {
                var yaml = File.ReadAllText(configPath);
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .Build();
                App = deserializer.Deserialize<AppConfig>(yaml) ?? new AppConfig();
            }
            catch
            {
                Output.Log("配置文件解析失败，使用默认配置", 2, "Config");
                App = new AppConfig();
            }
        }


        private static void SaveConfig(string configPath)
        {
            var serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            var yaml = serializer.Serialize(App);

            var sb = new StringBuilder();
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#                          RtCli 配置文件 - " + $"{Program.RtCliVersion}");
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#");
            sb.AppendLine("#  说明:");
            sb.AppendLine("#    该程序配置文件如果不清晰，未来将会考虑Wiki制作");
            sb.AppendLine("#    如果你不知道那些项有什么作用，请考虑使用RutCitrusServer来设置");
            sb.AppendLine("#    目前修改配置后需要重启应用程序才能生效");
            sb.AppendLine("#");
            sb.AppendLine("#  配置项说明:");
            sb.AppendLine("#    check_java      - 是否在启动时检查 Java 运行时环境 (true/false)");
            sb.AppendLine("#    check_dot_net   - 是否在启动时检查 .NET 运行时环境 (true/false)");
            sb.AppendLine("#    check_os_bit    - 是否检查操作系统位数 (true/false)");
            sb.AppendLine("#    skip_select     - 是否跳过模式选择界面直接进入默认模式 (true/false)");
            sb.AppendLine("#    server_name     - 服务器名称，用于标识本服务器");
            sb.AppendLine("#    server_port     - 服务器监听端口号 (1-65535)");
            sb.AppendLine("#    analyzer_mode   - MC控制台模式 (RUN/RCON)");
            sb.AppendLine("#                      RUN: 程序直接启动MC服务端作为子进程，可完全控制输入输出");
            sb.AppendLine("#                      RCON: 通过日志文件读取输出 + RCON协议发送命令");
            sb.AppendLine("#    work_path       - RUN模式下MC服务端的工作目录路径");
            sb.AppendLine("#    run_server_flags- RUN模式下启动MC服务端的JVM参数");
            sb.AppendLine("#    rcon_host       - RCON模式下RCON服务地址");
            sb.AppendLine("#    rcon_port       - RCON模式下RCON服务端口");
            sb.AppendLine("#    rcon_password   - RCON模式下RCON密码");
            sb.AppendLine("#");
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine();

            var lines = yaml.Split('\n');
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (trimmedLine.StartsWith("check_java:"))
                    sb.AppendLine("# 是否检查 Java 运行时环境");
                else if (trimmedLine.StartsWith("check_dot_net:"))
                    sb.AppendLine("# 是否检查 .NET 运行时环境");
                else if (trimmedLine.StartsWith("check_o_s_bit:"))
                    sb.AppendLine("# 是否检查操作系统位数 (32位/64位)");
                else if (trimmedLine.StartsWith("server_name:"))
                    sb.AppendLine("# 服务器名称");
                else if (trimmedLine.StartsWith("server_port:"))
                    sb.AppendLine("# 服务器端口号");
                else if (trimmedLine.StartsWith("skip_select"))
                    sb.AppendLine("# 跳过模式选择");
                else if (trimmedLine.StartsWith("analyzer_mode:"))
                    sb.AppendLine("# MC控制台模式 (RUN/RCON)");
                else if (trimmedLine.StartsWith("work_path:"))
                    sb.AppendLine("# RUN模式工作目录");
                else if (trimmedLine.StartsWith("run_server_flags:"))
                    sb.AppendLine("# RUN模式JVM启动参数");
                else if (trimmedLine.StartsWith("rcon_host:"))
                    sb.AppendLine("# RCON服务地址");
                else if (trimmedLine.StartsWith("rcon_port:"))
                    sb.AppendLine("# RCON服务端口");
                else if (trimmedLine.StartsWith("rcon_password:"))
                    sb.AppendLine("# RCON密码");
                else if (trimmedLine.StartsWith("debug:"))
                    sb.AppendLine("# 调试模式 (No)");

                sb.Append(line);
            }

            File.WriteAllText(configPath, sb.ToString());
        }
    }
}
