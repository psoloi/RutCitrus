using System.Reflection;
using Newtonsoft.Json.Linq;
using RtCli.Modules.Unit;

namespace RtCli.Tests;

/// <summary>同一集合内串行: I18n 的当前语言是全局状态, 避免与其他测试并发互相影响。</summary>
[CollectionDefinition("I18n", DisableParallelization = true)]
public class I18nCollection { }

/// <summary>
/// 内置文案资源化(#5)后的行为约定: 文案来自嵌入资源 i18n.default.json,
/// 回退链与格式化行为保持不变。
/// </summary>
[Collection("I18n")]
public class I18nTests
{
    private static JObject Defaults()
    {
        var p = typeof(I18n).GetProperty("DefaultLangData", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(p);
        return (JObject)p!.GetValue(null)!;
    }

    [Fact]
    public void 内置资源可加载且结构完整()
    {
        var d = Defaults();
        Assert.Equal("zh_CN", d["selectLang"]?.ToString());
        Assert.IsType<JObject>(d["zh_CN"]);
        Assert.IsType<JObject>(d["en_US"]);
    }

    [Fact]
    public void 中英文键集完全一致()
    {
        var d = Defaults();
        var zh = new HashSet<string>(((JObject)d["zh_CN"]!).Properties().Select(p => p.Name));
        var en = new HashSet<string>(((JObject)d["en_US"]!).Properties().Select(p => p.Name));

        Assert.NotEmpty(zh);
        var missingInEn = zh.Except(en).ToList();
        var missingInZh = en.Except(zh).ToList();
        Assert.True(missingInEn.Count == 0, "en_US 缺少键: " + string.Join(", ", missingInEn.Take(10)));
        Assert.True(missingInZh.Count == 0, "zh_CN 缺少键: " + string.Join(", ", missingInZh.Take(10)));
    }

    [Fact]
    public void Get_默认读取中文文案()
    {
        I18n.SetLanguage("zh_CN");
        Assert.Equal("语言设置", I18n.Get("lang_title"));
        Assert.Equal("重新加载中...", I18n.Get("main_reloading"));
    }

    [Fact]
    public void Get_支持格式化参数()
    {
        I18n.SetLanguage("zh_CN");
        Assert.Equal("当前语言: zh_CN", I18n.Get("lang_current", "zh_CN"));
    }

    [Fact]
    public void Get_切换英文后读取英文文案()
    {
        try
        {
            I18n.SetLanguage("en_US");
            Assert.Equal("Language Settings", I18n.Get("lang_title"));
            Assert.Equal("Usage: .ai run <task name>", I18n.Get("prog_ai_run_usage"));
        }
        finally
        {
            I18n.SetLanguage("zh_CN");
        }
    }

    [Fact]
    public void Get_未知键回退为键名本身()
        => Assert.Equal("__no_such_i18n_key__", I18n.Get("__no_such_i18n_key__"));
}
