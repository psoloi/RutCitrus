using System.IO;

namespace RtCli.Modules.Unit
{
    /// <summary>
    /// 这个是控制Content所有内容的类，主要负责初始化Content目录。
    /// </summary>
    public static class Config
    {
        private static readonly string DataDirectory = "Content/Data";
        private static readonly string absoluteDataPath = Path.GetFullPath(DataDirectory);
        private static bool _isInitialized = false;

        public static string DataPath => absoluteDataPath;

        public static void Initialize()
        {
            if (_isInitialized) return;

            if (!Directory.Exists(absoluteDataPath))
            {
                Directory.CreateDirectory(absoluteDataPath);
                Output.Log($"创建数据目录: {absoluteDataPath}", 1, "Config");
            }

            _isInitialized = true;
        }
    }
}
