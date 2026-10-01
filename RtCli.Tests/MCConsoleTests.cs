using RtCli.Modules.Function;

namespace RtCli.Tests;

/// <summary>MCConsole 的纯解析规则(崩溃判定/TPS/玩家数/进出服)。</summary>
public class MCConsoleTests
{
    [Theory]
    [InlineData("---- Minecraft Crash Report ----", true)]
    [InlineData("This crash report has been saved to: /srv/crash", true)]
    [InlineData("Shutting down the server", true)]
    [InlineData("[12:00:00] [Server thread/FATAL]: ticking entity", true)]
    [InlineData("[12:00:00] [Server thread/ERROR]: A Crash occurred while ticking", true)]
    [InlineData("[12:00:00] [Server thread/ERROR]: 普通错误不应判为崩溃", false)]
    [InlineData("[12:00:00] [Server thread/INFO]: Done (3.456s)!", false)]
    [InlineData("", false)]
    public void IsCrashLine_只按精准信号判定(string line, bool expected)
        => Assert.Equal(expected, MCConsole.IsCrashLine(line));

    [Fact]
    public void TryParseSparkTps_解析三窗口并容忍星号()
    {
        Assert.True(MCConsole.TryParseSparkTps("TPS from last 1m, 5m, 15m: 20.0*, 19.9, 19.8", out var t1, out var t5, out var t15));
        Assert.Equal(20.0, t1);
        Assert.Equal(19.9, t5);
        Assert.Equal(19.8, t15);
    }

    [Fact]
    public void TryParseSparkTps_不匹配时输出保持为负一()
    {
        Assert.False(MCConsole.TryParseSparkTps("没有任何TPS信息", out var t1, out var t5, out var t15));
        Assert.Equal(-1, t1);
        Assert.Equal(-1, t5);
        Assert.Equal(-1, t15);
    }

    [Theory]
    [InlineData("TPS: 20.0", 20.0)]
    [InlineData("TPS from last 1m: 19.5", 19.5)]
    public void TryParseSingleTps_解析单值格式(string line, double expected)
    {
        Assert.True(MCConsole.TryParseSingleTps(line, out var tps));
        Assert.Equal(expected, tps);
    }

    [Fact]
    public void TryParseSingleTps_不会误吞Spark三窗口行()
    {
        // Spark 行在 1m 之后是逗号, 单值正则要求紧跟冒号, 因此不应命中
        Assert.False(MCConsole.TryParseSingleTps("TPS from last 1m, 5m, 15m: 20.0, 19.9, 19.8", out var tps));
        Assert.Equal(-1, tps);
    }

    [Fact]
    public void TryParseJsonTps_解析JSON字段()
    {
        Assert.True(MCConsole.TryParseJsonTps("{\"tps\": 19.87}", out var tps));
        Assert.Equal(19.87, tps);
        Assert.False(MCConsole.TryParseJsonTps("{\"mspt\": 1.2}", out _));
    }

    [Theory]
    [InlineData("There are 2 of a max of 20 players online: Steve, Alex", 2, 20)]
    [InlineData("There're 3 of a max of 10 players online", 3, 10)]
    public void TryParsePlayerCount_解析人数与上限(string line, int count, int max)
    {
        Assert.True(MCConsole.TryParsePlayerCount(line, out var c, out var m));
        Assert.Equal(count, c);
        Assert.Equal(max, m);
    }

    [Fact]
    public void TryParsePlayerCount_不匹配时输出为负一()
    {
        Assert.False(MCConsole.TryParsePlayerCount("There are players online", out var c, out var m));
        Assert.Equal(-1, c);
        Assert.Equal(-1, m);
    }

    [Fact]
    public void IsPlayerJoinLeftLine_大小写不敏感()
    {
        Assert.True(MCConsole.IsPlayerJoinLine("Steve joined the game"));
        Assert.True(MCConsole.IsPlayerLeftLine("Steve left the game"));
        Assert.False(MCConsole.IsPlayerJoinLine("Steve left the game"));
        Assert.False(MCConsole.IsPlayerJoinLine(null!));
    }
}
