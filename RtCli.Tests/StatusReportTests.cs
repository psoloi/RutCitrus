using RtCli.Modules.Function;

namespace RtCli.Tests;

/// <summary>
/// .status 全局状态总览: 命令必须已注册, 且渲染过程不抛异常
/// (各分区依赖大量全局静态状态, 缺一个未初始化的模块就会炸)。
/// </summary>
public class StatusReportTests
{
    public StatusReportTests() => Program.EnsureCliCommandsRegistered();

    [Fact]
    public void status命令已注册到CLI注册表()
    {
        Assert.True(CommandRegistry.TryResolveCliCommand(".status", out var handler));
        Assert.NotNull(handler);
    }

    [Fact]
    public void 状态总览渲染不抛异常()
    {
        // 会做短超时的外部探测(GitHub / AI 端点), 失败也应以状态文字呈现而非抛出
        var ex = Record.Exception(StatusReport.Print);
        Assert.Null(ex);
    }

    [Fact]
    public void 版本检查结果形状自洽()
    {
        var r = RtCli.Modules.Unit.Checker.QueryUpdateStatus(TimeSpan.FromSeconds(2));
        // 成功时不应带错误信息; 失败时必须有错误原因
        if (r.Ok) Assert.Null(r.Error);
        else Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }
}
