using RtCli.Modules.Unit;

namespace RtCli.Tests;

/// <summary>
/// AI 任务默认配置的护栏(重构 P1):
///   - 所有 cron 必须是 6 段。5 段会被 NormalizeCron 当成"分 时"补秒位,
///     导致 "0 0/5 * * ?" 实际变成"每5小时"而不是"每5分钟"。
///   - 所有任务都必须有最小触发间隔, 否则只要条件持续成立就会每个 tick 调一次 AI。
/// </summary>
public class AiTaskDefaultsTests
{
    private static Dictionary<string, AiTask> Tasks => ContentManager.Ai.ServerAutoAi.Tasks;

    [Fact]
    public void 所有任务cron都是6段()
    {
        var bad = Tasks
            .Where(kv => kv.Value.Interval.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length != 6)
            .Select(kv => kv.Key + "=" + kv.Value.Interval)
            .ToList();

        Assert.True(bad.Count == 0,
            "以下任务的cron不是6段(会被解释成每N小时): " + string.Join(", ", bad));
    }

    [Fact]
    public void 所有任务都有最小触发间隔()
    {
        var bad = Tasks
            .Where(kv => kv.Value.MinTriggerIntervalMinutes <= 0)
            .Select(kv => kv.Key)
            .ToList();

        Assert.True(bad.Count == 0,
            "以下任务缺少最小触发间隔(条件持续成立时会每个tick都调AI): " + string.Join(", ", bad));
    }

    [Fact]
    public void 系统命令默认关闭()
        => Assert.False(ContentManager.Ai.ServerAutoAi.Tools.AllowSystemCommands);

    [Fact]
    public void 系统命令白名单默认只含只读诊断()
    {
        var allow = ContentManager.Ai.ServerAutoAi.Tools.SystemCommandAllowlist;
        Assert.NotEmpty(allow);
        Assert.All(allow, a => Assert.DoesNotContain("del", a, StringComparison.OrdinalIgnoreCase));
        Assert.All(allow, a => Assert.DoesNotContain("format", a, StringComparison.OrdinalIgnoreCase));
        Assert.All(allow, a => Assert.DoesNotContain("shutdown", a, StringComparison.OrdinalIgnoreCase));
    }
}
