using RtCli.Modules.Function;
using RtCli.Modules.Unit;

namespace RtCli.Tests;

/// <summary>
/// 面板命令注册表的解析规则。这组用例锁定 #4 重构前后的行为等价:
/// 原 Backend.DispatchCommand 的 if 链命中集合必须与新注册表逐条一致。
/// </summary>
public class CommandRegistryTests
{
    private static readonly string[] ExactCommands =
    {
        "rt", "rt help", ".help", "rt status", "rt clients", "rt extensions", "rt reload",
        ".cfg reload", ".cfg show", ".server list", ".server status", ".server start", ".server stop", ".server detach",
        ".ai", ".ai start", ".ai stop", ".ai list", ".ai reload", ".ai run", ".ai clear",
        ".auto", ".auto start", ".auto stop", ".auto list", ".auto on", ".auto off",
        ".fx", ".fx add", ".fx get", ".fx list", ".fx del", ".fx filter", ".fx filter config", ".fx filter plugin", ".fx filter mod"
    };

    private static readonly string[] PrefixCommands =
    {
        ".server change", ".ai run", ".ai clear", ".auto on", ".auto off", ".fx add", ".fx get", ".fx list"
    };

    public CommandRegistryTests() => Backend.EnsurePanelCommandsRegistered();

    [Fact]
    public void 全部精确命令都能解析且无参数()
    {
        foreach (var cmd in ExactCommands)
        {
            Assert.True(CommandRegistry.TryResolvePanelCommand(cmd, out var handler, out var arg, out _), cmd);
            Assert.NotNull(handler);
            Assert.Equal("", arg);
        }
    }

    [Fact]
    public void 全部前缀命令都能解析并取出参数()
    {
        foreach (var cmd in PrefixCommands)
        {
            Assert.True(CommandRegistry.TryResolvePanelCommand(cmd + " 样例值", out var handler, out var arg, out _), cmd);
            Assert.NotNull(handler);
            Assert.Equal("样例值", arg);
        }
    }

    [Theory]
    [InlineData(".server changeX")]
    [InlineData("rt statusX")]
    [InlineData(".fx listX")]
    [InlineData("nonexistent")]
    [InlineData(".ai runX")]
    [InlineData("")]
    public void 未注册或缺少分隔空格的命令不应命中(string cmd)
        => Assert.False(CommandRegistry.TryResolvePanelCommand(cmd, out _, out _, out _));

    [Fact]
    public void 精确匹配优先于前缀匹配()
    {
        Assert.True(CommandRegistry.TryResolvePanelCommand(".fx list", out var exact, out var a1, out _));
        Assert.True(CommandRegistry.TryResolvePanelCommand(".fx list 3", out var prefix, out var a2, out _));
        Assert.Equal("", a1);
        Assert.Equal("3", a2);
        Assert.NotSame(exact, prefix);
    }

    [Fact]
    public void 更长的精确键不会被短前缀抢走()
    {
        Assert.True(CommandRegistry.TryResolvePanelCommand(".fx filter config", out var config, out _, out _));
        Assert.True(CommandRegistry.TryResolvePanelCommand(".fx filter", out var filter, out _, out _));
        Assert.NotSame(config, filter);
    }

    [Theory]
    [InlineData("rt status", false)]
    [InlineData(".fx list", false)]
    [InlineData(".ai list", false)]
    [InlineData("rt reload", true)]
    [InlineData(".auto on", true)]
    [InlineData(".server start", true)]
    public void needsLock_只读命令免锁_改状态命令进锁(string cmd, bool expectedLock)
    {
        Assert.True(CommandRegistry.TryResolvePanelCommand(cmd, out _, out _, out var needsLock));
        Assert.Equal(expectedLock, needsLock);
    }
}
