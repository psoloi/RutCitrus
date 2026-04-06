using System.Diagnostics.CodeAnalysis;
using System.IO;
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
        public string ServerKey { get; set; } = "default_key";
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
            File.WriteAllText(configPath, yaml);
        }
    }
}
