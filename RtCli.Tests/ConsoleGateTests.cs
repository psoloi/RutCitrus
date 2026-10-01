using System.Diagnostics;
using RtCli.Modules.Unit;

namespace RtCli.Tests;

/// <summary>控制台交互门闩: 后台线程与主输入循环的让出/归还握手。</summary>
public class ConsoleGateTests
{
    [Fact]
    public async Task Interact_主循环让出后执行并归还()
    {
        Assert.False(ConsoleGate.YieldRequested);

        int? result = null;
        var worker = Task.Run(() => { result = ConsoleGate.Interact(() => 7); });

        // 模拟主输入循环: 轮询到请求即让出控制台
        var sw = Stopwatch.StartNew();
        while (!ConsoleGate.YieldRequested && sw.ElapsedMilliseconds < 1500)
            Thread.Sleep(5);

        Assert.True(ConsoleGate.YieldRequested);
        ConsoleGate.YieldAndWait();

        await worker.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(7, result);
        Assert.False(ConsoleGate.YieldRequested);
    }

    [Fact]
    public void Interact_交互抛异常时也会归还控制台()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ConsoleGate.Interact<int>(() => throw new InvalidOperationException("x")));
        Assert.False(ConsoleGate.YieldRequested);
    }
}
