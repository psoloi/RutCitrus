using Newtonsoft.Json.Linq;
using System.IO;

namespace RtCli.Modules.Unit
{
    public static class I18n
    {
        private static JObject _langData = new JObject();
        private static string _currentLang = "zh_CN";
        private static readonly string LangFileName = "lang.json";

        private static readonly JObject DefaultLangData = new JObject(
            new JProperty("selectLang", "zh_CN"),
            new JProperty("zh_CN", new JObject(
                new JProperty("checker_nojava", "当前设备环境没有检测到Java"),
                new JProperty("checker_java", "检测到的Java版本："),
                new JProperty("checker_nodotnet", "当前设备环境没有检测到.NET，推荐安装.NET8.0及更高版本"),
                new JProperty("checker_dotnet", "检测到的.NET版本："),
                new JProperty("checker_osbit", "当前操作系统环境为32位可能多数功能不支持!"),
                new JProperty("main_loadfinsih", "加载完毕！用时"),
                new JProperty("main_seltip_1", "请选择接下来需要的加载项..."),
                new JProperty("mian_seltip_2", "使用↑↓来选择按回车确定"),
                new JProperty("main_selmode_default", "默认模式"),
                new JProperty("main_selmode_debug", "测试模式"),
                new JProperty("main_selmode_reload", "重新加载"),
                new JProperty("main_selmode_exit", "关闭程序"),
                new JProperty("main_selmode_no", "不存在的选项，请尝试检查语言配置文件！"),
                new JProperty("main_end", "主线程结束")
            )),
            new JProperty("en_US", new JObject(
                new JProperty("checker_nojava", "Java is not detected in the current device environment."),
                new JProperty("checker_java", "Detected Java version:"),
                new JProperty("checker_nodotnet", "The current device environment does not detect .NET. It is recommended to install .NET 8.0 or higher."),
                new JProperty("checker_dotnet", "Detected .NET version:"),
                new JProperty("checker_osbit", "The current operating system environment is 32-bit, which may not support most functions!"),
                new JProperty("main_loadfinsih", "Load finished!It took"),
                new JProperty("main_seltip_1", "Please select the next loading item..."),
                new JProperty("mian_seltip_2", "Use ↑↓ to select and press Enter to confirm"),
                new JProperty("main_selmode_default", "默认模式"),
                new JProperty("main_selmode_debug", "测试模式"),
                new JProperty("main_selmode_reload", "重新加载"),
                new JProperty("main_selmode_exit", "关闭程序"),
                new JProperty("main_end", "Main thread ends")
            ))
        );

        public static void Init()
        {
            Config.Initialize();

            string langFilePath = Path.Combine(Config.DataPath, LangFileName);

            if (!File.Exists(langFilePath))
            {
                File.WriteAllText(langFilePath, DefaultLangData.ToString());
                Output.Log($"创建语言文件: {langFilePath}", 1, "I18n");
            }

            var json = File.ReadAllText(langFilePath);
            try
            {
                _langData = JObject.Parse(json);
            }
            catch (Exception ex)
            {
                Output.Log($"语言文件解析失败，使用默认语言: {ex.Message}", 2, "I18n");
                _langData = DefaultLangData;
            }
            _currentLang = _langData["selectLang"]?.ToString() ?? "zh_CN";
        }

        public static string Get(string key)
        {
            var token = _langData[_currentLang]?[key];
            return token?.ToString() ?? key;
        }

        public static void SetLanguage(string lang)
        {
            if (_langData[lang] != null)
            {
                _currentLang = lang;
            }
        }

        public static bool SetLanguageAndSave(string lang)
        {
            if (_langData[lang] == null)
            {
                return false;
            }

            _currentLang = lang;
            _langData["selectLang"] = lang;

            string langFilePath = Path.Combine(Config.DataPath, LangFileName);
            File.WriteAllText(langFilePath, _langData.ToString());

            return true;
        }

        public static string CurrentLang => _currentLang;
    }
}
