using RtCli.Modules.Unit;

namespace RtCli.Tests;

/// <summary>
/// 插件版本号比较: 用于判断 Modrinth 上的版本是否比本地 jar 新。
/// 必须按"数字段"比较(1.0.10 &gt; 1.0.9), 而不是字典序。
/// </summary>
public class PluginVersionTests
{
    [Theory]
    [InlineData("2.22.0", "2.21.2", 1)]
    [InlineData("1.0.10", "1.0.9", 1)]          // 数字比较, 非字典序
    [InlineData("2.21.2", "2.22.0", -1)]
    [InlineData("1.0.9", "1.0.10", -1)]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("v1.2.3", "1.2.3", 0)]          // 前缀 v 忽略
    [InlineData("V2.0.0", "2.0.0", 0)]
    [InlineData("1.0.0", "1.0.0.0", 0)]         // 尾随 .0 视为相同
    [InlineData("2.0", "2.0.0", 0)]
    [InlineData("1.0.0", "1.0.0-beta", 1)]      // 正式版比预发布新
    [InlineData("1.0.0-beta", "1.0.0-alpha", 1)]
    [InlineData("1.20.1-R0.1-SNAPSHOT", "1.20.1-R0.1", -1)]  // SNAPSHOT 是同版本的开发快照, 比正式版旧
    public void 版本比较符合预期(string a, string b, int expectedSign)
        => Assert.Equal(expectedSign, Math.Sign(PluginVersion.Compare(a, b)));

    [Fact]
    public void 空版本视为最旧()
    {
        Assert.Equal(-1, Math.Sign(PluginVersion.Compare(null, "1.0.0")));
        Assert.Equal(-1, Math.Sign(PluginVersion.Compare("", "1.0.0")));
        Assert.Equal(1, Math.Sign(PluginVersion.Compare("1.0.0", null)));
        Assert.Equal(0, Math.Sign(PluginVersion.Compare(null, "")));
    }

    [Fact]
    public void IsNewer语义()
    {
        Assert.True(PluginVersion.IsNewer("2.0.0", "1.9.9"));
        Assert.False(PluginVersion.IsNewer("1.9.9", "2.0.0"));
        Assert.False(PluginVersion.IsNewer("1.0.0", "1.0.0"));
    }

    [Fact]
    public void 常见插件版本串可比较()
    {
        Assert.True(PluginVersion.IsNewer("5.2.1", "5.1.0"));
        Assert.True(PluginVersion.IsNewer("3.0.0", "2.18.3"));
        Assert.False(PluginVersion.IsNewer("2.19.0", "2.22.0"));
    }
}
