using Newtonsoft.Json.Linq;
using System.IO;

namespace RtCli.Modules.Unit
{
    /// <summary>
    /// 国际化支持(仅 RtCli 使用, 支持简体中文/英文)。
    /// lang.json 与内置默认自动合并: 程序更新新增的键自动补全, 用户自定义翻译保留。
    /// </summary>
    public static class I18n
    {
        private static JObject _langData = new JObject();
        private static string _currentLang = "zh_CN";
        private static readonly string LangFileName = "lang.json";

        /// <summary>支持的语言列表(仅中英)</summary>
        public static readonly string[] SupportedLangs = { "zh_CN", "en_US" };

                /// <summary>内置默认文案的嵌入资源名(见 RtCli.csproj 的 EmbeddedResource)</summary>
        private const string DefaultLangResource = "RtCli.Modules.Unit.i18n.default.json";

        private static JObject? _defaultLangData;

        /// <summary>
        /// 内置默认文案。原先以 1077 条内联 new JProperty 链硬编码在本文件里,
        /// 现已抽成嵌入资源 i18n.default.json: 改文案只动 JSON, 代码里只剩 Get()。
        /// 首次访问时加载并缓存。
        /// </summary>
        private static JObject DefaultLangData => _defaultLangData ??= LoadDefaultLangData();

        private static JObject LoadDefaultLangData()
        {
            try
            {
                using var stream = typeof(I18n).Assembly.GetManifestResourceStream(DefaultLangResource);
                if (stream == null)
                {
                    Output.Log($"内置语言资源缺失: {DefaultLangResource}", 3, "I18n");
                    return new JObject();
                }
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                return JObject.Parse(reader.ReadToEnd());
            }
            catch (Exception ex)
            {
                Output.Log($"内置语言资源加载失败: {ex.Message}", 3, "I18n");
                return new JObject();
            }
        }

        public static void Init()
        {
            Config.Initialize();

            string langFilePath = Path.Combine(Config.DataPath, LangFileName);

            if (!File.Exists(langFilePath))
            {
                AtomicFile.WriteAllText(langFilePath, DefaultLangData.ToString());
                Output.Log($"创建语言文件: {langFilePath}", 1, "I18n");
            }

            try
            {
                var userJson = JObject.Parse(File.ReadAllText(langFilePath));
                _langData = MergeWithDefaults(userJson);
            }
            catch (Exception ex)
            {
                Output.Log($"语言文件解析失败，使用默认语言: {ex.Message}", 2, "I18n");
                _langData = DefaultLangData;
            }

            _currentLang = _langData["selectLang"]?.ToString() ?? "zh_CN";
            if (!SupportedLangs.Contains(_currentLang))
                _currentLang = "zh_CN";
        }

        /// <summary>
        /// 将用户语言文件与内置默认合并: 用户已有的翻译保留, 程序更新新增的键自动补全。
        /// </summary>
        private static JObject MergeWithDefaults(JObject userJson)
        {
            var merged = new JObject(userJson);
            bool changed = false;

            foreach (var langProp in DefaultLangData.Properties())
            {
                // selectLang 等标量键: 仅在用户文件缺失时补全
                if (langProp.Value is not JObject langObj)
                {
                    if (merged[langProp.Name] == null)
                    {
                        merged[langProp.Name] = langProp.Value.DeepClone();
                        changed = true;
                    }
                    continue;
                }

                // 语言对象: 逐键补全缺失项(不覆盖用户自定义值)
                var userLang = merged[langProp.Name] as JObject;
                if (userLang == null)
                {
                    merged[langProp.Name] = langObj.DeepClone();
                    changed = true;
                    continue;
                }

                foreach (var keyProp in langObj.Properties())
                {
                    if (userLang[keyProp.Name] == null)
                    {
                        userLang[keyProp.Name] = keyProp.Value.DeepClone();
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                // 合并产生新键时回写文件(原子写入防止损坏)
                try
                {
                    AtomicFile.WriteAllText(Path.Combine(Config.DataPath, LangFileName), merged.ToString());
                }
                catch
                {
                    // 回写失败不影响本次运行
                }
            }

            return merged;
        }

        /// <summary>
        /// 获取翻译文本。支持 string.Format 格式化: Get("key", arg0, arg1)。
        /// 回退链: 当前语言 → zh_CN → 键名本身。
        /// </summary>
        public static string Get(string key, params object[] args)
        {
            string value = Lookup(key);
            return args.Length > 0 ? string.Format(value, args) : value;
        }

        private static string Lookup(string key)
        {
            var token = _langData[_currentLang]?[key];
            if (!string.IsNullOrEmpty(token?.ToString())) return token.ToString();

            // 回退到内置默认(防止用户文件被清空对应键)
            token = DefaultLangData[_currentLang]?[key];
            if (!string.IsNullOrEmpty(token?.ToString())) return token.ToString();

            // 最终回退到中文
            token = _langData["zh_CN"]?[key] ?? DefaultLangData["zh_CN"]?[key];
            return token?.ToString() ?? key;
        }

        public static void SetLanguage(string lang)
        {
            if (SupportedLangs.Contains(lang))
            {
                _currentLang = lang;
            }
        }

        public static bool SetLanguageAndSave(string lang)
        {
            if (!SupportedLangs.Contains(lang))
            {
                return false;
            }

            _currentLang = lang;
            _langData["selectLang"] = lang;

            AtomicFile.WriteAllText(Path.Combine(Config.DataPath, LangFileName), _langData.ToString());

            return true;
        }

        public static string CurrentLang => _currentLang;
    }
}
