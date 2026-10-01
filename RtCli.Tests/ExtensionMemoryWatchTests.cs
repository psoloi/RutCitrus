using RtCli.Modules.Function;

namespace RtCli.Tests;

/// <summary>
/// 扩展内存限制(Validator.ExtensionMemoryWatch):
///   超限只开始计时, 连续超限达到时长才判"内存失控"; 中途回落即清零,
///   因此大文件解析这类瞬时峰值不会误杀扩展。
/// </summary>
public class ExtensionMemoryWatchTests
{
    private const long MB = 1024 * 1024;
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);

    [Fact]
    public void 默认策略是256MB持续2秒()
    {
        Assert.Equal(256L * MB, Validator.MaxExtensionMemoryBytes);
        Assert.Equal(TimeSpan.FromSeconds(2), Validator.ExtensionMemorySustainDuration);
    }

    [Fact]
    public void 低于上限不触发()
    {
        var w = new Validator.ExtensionMemoryWatch();
        Assert.False(w.Sample(200 * MB, T0, out var forTime));
        Assert.Equal(TimeSpan.Zero, forTime);
        Assert.Null(w.ExceededSince);
    }

    [Fact]
    public void 超限但未达持续时长不触发()
    {
        var w = new Validator.ExtensionMemoryWatch();
        Assert.False(w.Sample(300 * MB, T0, out _));
        Assert.False(w.Sample(300 * MB, T0.AddSeconds(1), out var forTime));
        Assert.Equal(TimeSpan.FromSeconds(1), forTime);
    }

    [Fact]
    public void 连续超限满2秒触发卸载()
    {
        var w = new Validator.ExtensionMemoryWatch();
        Assert.False(w.Sample(300 * MB, T0, out _));
        Assert.False(w.Sample(300 * MB, T0.AddSeconds(1.9), out _));
        Assert.True(w.Sample(300 * MB, T0.AddSeconds(2), out var forTime));
        Assert.True(forTime >= TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void 中途回落后计时清零()
    {
        var w = new Validator.ExtensionMemoryWatch();
        Assert.False(w.Sample(300 * MB, T0, out _));
        Assert.False(w.Sample(300 * MB, T0.AddSeconds(1.5), out _));

        // 回落到上限内 -> 清零
        Assert.False(w.Sample(100 * MB, T0.AddSeconds(1.6), out _));
        Assert.Null(w.ExceededSince);

        // 重新超限后必须重新计满 2 秒
        Assert.False(w.Sample(300 * MB, T0.AddSeconds(1.7), out _));
        Assert.False(w.Sample(300 * MB, T0.AddSeconds(3.6), out _));
        Assert.True(w.Sample(300 * MB, T0.AddSeconds(3.7), out _));
    }

    [Fact]
    public void Peak记录最高占用()
    {
        var w = new Validator.ExtensionMemoryWatch();
        w.Sample(100 * MB, T0, out _);
        w.Sample(400 * MB, T0.AddSeconds(1), out _);
        w.Sample(50 * MB, T0.AddSeconds(2), out _);
        Assert.Equal(400 * MB, w.PeakBytes);
    }

    [Fact]
    public void 支持自定义上限与时长()
    {
        var w = new Validator.ExtensionMemoryWatch(10 * MB, TimeSpan.FromMilliseconds(500));
        Assert.False(w.Sample(20 * MB, T0, out _));
        Assert.True(w.Sample(20 * MB, T0.AddMilliseconds(500), out _));
    }

    [Fact]
    public void Reset清空状态()
    {
        var w = new Validator.ExtensionMemoryWatch();
        w.Sample(400 * MB, T0, out _);
        Assert.NotNull(w.ExceededSince);

        w.Reset();
        Assert.Null(w.ExceededSince);
        Assert.Equal(0, w.PeakBytes);
    }

    [Fact]
    public void 恰好等于上限不算超限()
    {
        var w = new Validator.ExtensionMemoryWatch();
        Assert.False(w.Sample(256 * MB, T0, out _));
        Assert.Null(w.ExceededSince);
    }
}
