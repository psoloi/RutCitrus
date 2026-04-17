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

        public string ServerName { get; set; } = "myserver";
        public int ServerPort { get; set; } = 7789;

    }

    public static class Config
    {
        private static readonly string DataDirectory = "Content/Data";
        private static readonly string absoluteDataPath = Path.GetFullPath(DataDirectory);
        private static readonly string ConfigFileName = "config.yml";
        private static bool _isInitialized = false;

        public static string DataPath => absoluteDataPath;
        public static AppConfig App { get; private set; } = new AppConfig();

        public static void Initialize()
        {
            if (_isInitialized) return;

            if (!Directory.Exists(absoluteDataPath))
            {
                Directory.CreateDirectory(absoluteDataPath);
                Output.Log($"创建数据目录: {absoluteDataPath}", 1, "Config");
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
            sb.AppendLine("#");
            sb.AppendLine("#  配置项说明:");
            sb.AppendLine("#    check_java    - 是否在启动时检查 Java 运行时环境 (true/false)");
            sb.AppendLine("#    check_dot_net - 是否在启动时检查 .NET 运行时环境 (true/false)");
            sb.AppendLine("#    check_os_bit  - 是否检查操作系统位数 (true/false)");
            sb.AppendLine("#    server_name   - 服务器名称，用于标识本服务器");
            sb.AppendLine("#    server_port   - 服务器监听端口号 (1-65535)");
            sb.AppendLine("#");
            sb.AppendLine("#  注意事项:");
            sb.AppendLine("#    1. 修改配置后需要重启应用程序才能生效");
            sb.AppendLine("#    2. server_port 请确保未被其他程序占用");
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

                sb.Append(line);
            }

            File.WriteAllText(configPath, sb.ToString());
        }
    }
}
