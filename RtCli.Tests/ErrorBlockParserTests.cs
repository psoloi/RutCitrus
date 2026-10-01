using RtCli.Modules.Unit;

namespace RtCli.Tests;

/// <summary>错误块切分规则(实时流识别与面板日志分析共用的单一来源)。</summary>
public class ErrorBlockParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[")]          // 无效正则
    public void CreateHandlerRegex_空或无效返回null(string? pattern)
        => Assert.Null(ErrorBlockParser.CreateHandlerRegex(pattern));

    [Fact]
    public void CreateHandlerRegex_有效模式返回可用正则()
    {
        var re = ErrorBlockParser.CreateHandlerRegex(@"ERROR");
        Assert.NotNull(re);
        Assert.Matches(re!, "[12:00:00] [Server thread/ERROR]: boom");
    }

    [Fact]
    public void IsContinuation_识别堆栈与异常链与空行()
    {
        var re = ErrorBlockParser.CreateHandlerRegex(@"ERROR")!;
        Assert.True(ErrorBlockParser.IsContinuation("    at com.x.Y.z(Y.java:1)", re));
        Assert.True(ErrorBlockParser.IsContinuation("Caused by: java.lang.NullPointerException", re));
        Assert.True(ErrorBlockParser.IsContinuation("... 10 more", re));
        Assert.True(ErrorBlockParser.IsContinuation("   ", re));
        Assert.True(ErrorBlockParser.IsContinuation("java.lang.Error: Suppressed: x", re));
        Assert.False(ErrorBlockParser.IsContinuation("[12:00:00] [Server thread/INFO]: 普通输出", re));
    }

    [Fact]
    public void ParseBlocks_按错误行切块并并入堆栈行()
    {
        var re = ErrorBlockParser.CreateHandlerRegex(@"ERROR")!;
        var lines = new[]
        {
            "[12:00:00] [Server thread/INFO]: starting",
            "[12:00:01] [Server thread/ERROR]: boom",
            "    at com.x.Y.z(Y.java:1)",
            "    at com.x.Y.w(Y.java:2)",
            "[12:00:02] [Server thread/INFO]: recovered",
            "[12:00:03] [Server thread/ERROR]: bang"
        };

        var blocks = ErrorBlockParser.ParseBlocks(lines, re);

        Assert.Equal(2, blocks.Count);
        Assert.Contains("boom", blocks[0]);
        Assert.Contains("at com.x.Y.z", blocks[0]);
        Assert.Contains("at com.x.Y.w", blocks[0]);
        Assert.DoesNotContain("recovered", blocks[0]);
        Assert.Contains("bang", blocks[1]);
    }

    [Fact]
    public void ParseBlocks_无错误行时返回空()
    {
        var re = ErrorBlockParser.CreateHandlerRegex(@"ERROR")!;
        var blocks = ErrorBlockParser.ParseBlocks(new[] { "[x] [Server thread/INFO]: ok" }, re);
        Assert.Empty(blocks);
    }
}
