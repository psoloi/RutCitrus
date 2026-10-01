using RtCli.Modules.Unit;
using C = RtCli.Modules.Unit.YamlCommentInjector.Comment;

namespace RtCli.Tests;

/// <summary>注释注入器: 键表 → YAML 的注入规则(配置文件"键说明"的单一来源)。</summary>
public class YamlCommentInjectorTests
{
    private static string Norm(string s) => s.Replace("\r\n", "\n");

    [Fact]
    public void 按键名精确注入且注释紧贴键上方()
    {
        var yaml = "auto_restart: false\nauto_restart_max_retries: 3\n";
        var result = YamlCommentInjector.Inject(yaml,
            YamlCommentInjector.Section.Default(
                new C("auto_restart", "# 重启"),
                new C("auto_restart_max_retries", "# 重试次数")));

        var norm = Norm(result);
        Assert.Contains("# 重启\nauto_restart: false", norm);
        Assert.Contains("# 重试次数\nauto_restart_max_retries: 3", norm);
    }

    [Fact]
    public void 未登记的键不注入注释()
    {
        var result = YamlCommentInjector.Inject("unknown_key: 1\n",
            YamlCommentInjector.Section.Default(new C("other", "# X")));
        Assert.DoesNotContain("# X", Norm(result));
        Assert.Contains("unknown_key: 1", Norm(result));
    }

    [Fact]
    public void 多行注释逐行展开()
    {
        var result = YamlCommentInjector.Inject("a: 1\n",
            YamlCommentInjector.Section.Default(new C("a", "# 一行\n# 二行")));
        Assert.Contains("# 一行\n# 二行\na: 1", Norm(result));
    }

    [Fact]
    public void 命名区块内同名键使用区块专属注释()
    {
        var yaml = "enabled: true\nai_settings:\n  enabled: false\n";
        var result = YamlCommentInjector.Inject(yaml,
            YamlCommentInjector.Section.Default(new C("enabled", "# 顶层")),
            YamlCommentInjector.Section.From("ai_settings", new C("enabled", "# AI段")));

        var norm = Norm(result);
        Assert.Contains("# 顶层\nenabled: true", norm);
        Assert.Contains("# AI段\n  enabled: false", norm);
    }

    [Fact]
    public void BlankLineBefore为false时不插入空行()
    {
        var yaml = "a: 1\nb: 2\n";
        var result = YamlCommentInjector.Inject(yaml,
            YamlCommentInjector.Section.Default(
                new C("a", "# A"),
                new C("b", "# B", BlankLineBefore: false)));

        var norm = Norm(result);
        Assert.Contains("# A\na: 1", norm);
        Assert.Contains("# B\nb: 2", norm);
        Assert.DoesNotContain("\n\n# B", norm);
    }

    [Fact]
    public void 列表项与无冒号行不参与匹配()
    {
        var yaml = "list:\n  - item\nplainvalue\n";
        var result = YamlCommentInjector.Inject(yaml,
            YamlCommentInjector.Section.Default(new C("item", "# 不应注入"), new C("plainvalue", "# 也不应")));
        var norm = Norm(result);
        Assert.DoesNotContain("# 不应注入", norm);
        Assert.DoesNotContain("# 也不应", norm);
    }
}
