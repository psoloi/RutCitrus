namespace RtCli.Tests;

/// <summary>
/// 日志输出的健壮性: Output.Log 绝不能抛异常。
///
/// 原实现只兜住 Spectre 的 "Could not find color or style", 遇到未闭合 '['、
/// 孤立 '[/]' 这类 "Encountered malformed markup tag" 会把异常抛给调用方。
/// 而 AI 任务与工具回调正好会输出模型原文、工具返回值(满是方括号), 一旦抛出会中断整次执行。
/// </summary>
public class OutputLogRobustnessTests
{
    [Theory]
    [InlineData("未闭合 [ 方括号")]
    [InlineData("[ERROR] 日志行")]
    [InlineData("[/] 只有闭合标签")]
    [InlineData("[Server thread/INFO]: hi")]
    [InlineData("正则 (?:are|'re) 与 [abc")]
    [InlineData("[red]ok[/red]")]
    [InlineData("{}")]
    [InlineData("")]
    public void 任意文本都不会让日志抛异常(string msg)
    {
        var ex = Record.Exception(() => RtCli.Modules.Output.Log(msg, 1, "Probe"));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void 各日志级别都不会抛异常(int level)
    {
        var ex = Record.Exception(() => RtCli.Modules.Output.Log("含 [ 未闭合的消息", level, "Probe"));
        Assert.Null(ex);
    }
}
