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
                new JProperty("main_loadfinsih", "加载完毕！用时"),
                new JProperty("main_seltip_1", "请选择接下来需要的加载项..."),
                new JProperty("mian_seltip_2", "使用↑↓来选择按回车确定"),
                new JProperty("main_end", "主线程结束")
            )),
            new JProperty("en_US", new JObject(
                new JProperty("main_loadfinsih", "Load finished!It took"),
                new JProperty("main_seltip_1", "Please select the next loading item..."),
                new JProperty("mian_seltip_2", "Use ↑↓ to select and press Enter to confirm"),
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
            _langData = JObject.Parse(json);
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

        public static string CurrentLang => _currentLang;
    }
}
