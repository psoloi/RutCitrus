using RtCli.Modules.Function;
using RtCli.Modules.Unit;

namespace RtCli.Tests;

/// <summary>
/// .fx genpattern 的「不重复 + 可用」保证:
///   1) AI 输出必须能解析成 BaseEntry 列表;
///   2) 表达式必须能编译为 .NET 正则, 且不能是万能通配;
///   3) 命令在 CLI 与面板两侧都已注册(面板按钮通过 /api/console/execute 调用它)。
/// </summary>
public class ErrorPatternGeneratorTests
{
    public ErrorPatternGeneratorTests()
    {
        Program.EnsureCliCommandsRegistered();
        Backend.EnsurePanelCommandsRegistered();
    }

    [Theory]
    [InlineData(@"java\.lang\.OutOfMemoryError")]
    [InlineData(@"Could not load .*plugin")]
    [InlineData(@"Caused by: com\.example\.[A-Za-z]+Exception")]
    public void 合法表达式可用(string pattern)
        => Assert.True(ErrorPatternGenerator.IsUsablePattern(pattern, out _));

    [Theory]
    [InlineData(@"[unclosed")]
    [InlineData("ab")]
    [InlineData(".")]
    [InlineData(".*")]
    [InlineData("^.*$")]
    [InlineData("(?s).*")]
    public void 不合法或过宽的表达式被拒绝(string pattern)
    {
        Assert.False(ErrorPatternGenerator.IsUsablePattern(pattern, out string reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void 过长的表达式被拒绝()
    {
        Assert.False(ErrorPatternGenerator.IsUsablePattern(new string('a', 301), out string reason));
        Assert.Contains("过长", reason);
    }

    [Fact]
    public void 能从夹带解释的AI输出中解析出条目()
    {
        string ai = "好的, 结果如下:\n" +
            "[\n" +
            "  {\"pattern\":\"NoSuchPluginException\",\"topic\":\"插件缺失\",\"solution\":\"安装插件\",\"action\":\"\"},\n" +
            "  {\"pattern\":\"MemoryLeak\",\"topic\":\"内存泄漏\",\"solution\":\"重启服务端\"}\n" +
            "]\n请核对。";

        var list = ErrorPatternGenerator.ParseCandidates(ai);

        Assert.Equal(2, list.Count);
        Assert.Equal("NoSuchPluginException", list[0].Pattern);
        Assert.Equal("插件缺失", list[0].Topic);
        Assert.Equal("安装插件", list[0].Solution);
        Assert.Equal("内存泄漏", list[1].Topic);
        Assert.Equal("", list[1].Action);
    }

    [Fact]
    public void 缺少pattern或topic的条目被跳过()
    {
        string ai = "[{\"pattern\":\"\",\"topic\":\"x\"}," +
                    "{\"pattern\":\"abc\",\"topic\":\"\"}," +
                    "{\"pattern\":\"ok\",\"topic\":\"y\"}]";
        var list = ErrorPatternGenerator.ParseCandidates(ai);
        Assert.Single(list);
        Assert.Equal("ok", list[0].Pattern);
    }

    [Theory]
    [InlineData("完全不是 JSON")]
    [InlineData("")]
    [InlineData("{\"a\":1}")]
    public void 非数组输出解析为空(string ai)
        => Assert.Empty(ErrorPatternGenerator.ParseCandidates(ai));

    [Fact]
    public void 命令在CLI与面板两侧都已注册()
    {
        Assert.True(CommandRegistry.TryResolveCliCommand(".fx genpattern", out var cliHandler));
        Assert.NotNull(cliHandler);

        Assert.True(CommandRegistry.TryResolvePanelCommand(".fx genpattern", out var panelHandler, out var arg, out var needsLock));
        Assert.NotNull(panelHandler);
        Assert.Equal("", arg);
        // 会调用 AI 且可能耗时数十秒, 因此不占全域命令锁
        Assert.False(needsLock);
    }
}
