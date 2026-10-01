using RtCli.Modules.Function;

namespace RtCli.Tests;

/// <summary>
/// CLI 命令注册表(#4): 原先 Continued() 内 85 条 switch 分支必须逐条仍能解析到处理器。
/// 用 TryResolveCliCommand 只做解析不执行, 避免测试触发真实命令副作用。
/// </summary>
public class CliCommandRegistryTests
{
    public CliCommandRegistryTests() => Program.EnsureCliCommandsRegistered();

    private static readonly string[] Samples =
    {
        "rt",
        "rt help",
        "rt about",
        "rt end",
        "rt reload",
        "rt status",
        "rt clients",
        "rt clients kick",
        "rt clients kick 样例",
        "rt extensions",
        "rt extensions load",
        "rt extensions unload",
        "rt extensions load 样例",
        "rt extensions unload 样例",
        "rt start",
        "rt stop",
        ".help",
        ".guide",
        ".auto",
        ".auto on 样例",
        ".auto on",
        ".auto off 样例",
        ".auto off",
        ".auto list",
        ".auto start",
        ".auto stop",
        ".ai",
        ".ai start",
        ".ai stop",
        ".ai list",
        ".ai reload",
        ".ai run 样例",
        ".ai run",
        ".ai clear 样例",
        ".ai clear",
        ".fx",
        ".fx add",
        ".fx add 样例",
        ".fx list",
        ".fx del",
        ".fx clientguide",
        ".fx base",
        ".fx ai",
        ".fx filter",
        ".fx filter config",
        ".fx filter plugin",
        ".fx list 样例",
        ".fx base 样例",
        ".fx ai 样例",
        ".fx clientguide 样例",
        ".fx filter config 样例",
        ".fx filter plugin 样例",
        ".fx filter mod",
        ".fx filter mod 样例",
        ".cfg",
        ".lang",
        ".lang 样例",
        ".cfg reload",
        ".cfg clear",
        ".status",
        ".server",
        ".server list",
        ".server add",
        ".server del",
        ".server change",
        ".server change 样例",
        ".server get",
        ".server connect",
        ".server connect 样例",
        ".server detach",
        ".server start",
        ".server stop",
        ".server kill",
        ".server status",
        ".group",
        ".group add 样例",
        ".group del 样例",
        ".group list",
        ".group set 样例",
        ".group unset 样例",
        ".group build",
        "ct",
        "ct list",
        "ct unpack 样例",
    };

    [Fact]
    public void 全部CLI命令都能解析到处理器()
    {
        var unresolved = new List<string>();
        foreach (var cmd in Samples)
        {
            if (!CommandRegistry.TryResolveCliCommand(cmd, out var handler) || handler == null)
                unresolved.Add(cmd);
        }
        Assert.True(unresolved.Count == 0, "未解析到处理器: " + string.Join(" | ", unresolved));
    }

    [Fact]
    public void 精确匹配优先于前缀匹配()
    {
        Assert.True(CommandRegistry.TryResolveCliCommand(".fx list", out var exact));
        Assert.True(CommandRegistry.TryResolveCliCommand(".fx list 3", out var prefix));
        Assert.NotSame(exact, prefix);
        Assert.True(CommandRegistry.TryResolveCliCommand(".ai run", out var aiExact));
        Assert.True(CommandRegistry.TryResolveCliCommand(".ai run 任务", out var aiPrefix));
        Assert.NotSame(aiExact, aiPrefix);
    }

    [Fact]
    public void 前缀命令缺少分隔空格时不命中()
    {
        Assert.False(CommandRegistry.TryResolveCliCommand(".server changeX", out _));
        Assert.False(CommandRegistry.TryResolveCliCommand(".fx listX", out _));
        Assert.False(CommandRegistry.TryResolveCliCommand(".ai runX", out _));
    }

    [Fact]
    public void 未注册命令不命中()
    {
        Assert.False(CommandRegistry.TryResolveCliCommand("不存在的命令", out _));
        Assert.False(CommandRegistry.TryResolveCliCommand("", out _));
    }

    [Fact]
    public void rt_end解析为退出分支()
    {
        // rt end 的处理器无副作用, 可直接执行以确认 Exit 结果
        Assert.Equal(CommandRegistry.CliOutcome.Exit, CommandRegistry.TryDispatchCli("rt end"));
    }

    [Fact]
    public void 斜杠命令由分发器单独接线()
    {
        // "/" 开头没有"命令名+空格"结构, 注册表解析不到, 由 Program.DispatchCliCommand 兜底
        Assert.False(CommandRegistry.TryResolveCliCommand("/say hello", out _));
    }
}
