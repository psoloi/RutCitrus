using RtCli.Modules.Function;

namespace RtCli.Tests;

/// <summary>触发条件比较运算(采样判定与瞬时值判定共用)</summary>
public class TriggerCompareTests
{
    [Theory]
    [InlineData(17.0, "<", 18.0, true)]
    [InlineData(18.0, "<", 18.0, false)]
    [InlineData(19.0, ">=", 18.0, true)]
    [InlineData(18.0, "==", 18.0, true)]
    [InlineData(18.01, "==", 18.0, false)]
    [InlineData(18.0, "<=", 18.0, true)]
    [InlineData(1.0, "??", 2.0, false)]
    public void 比较运算符合预期(double value, string op, double threshold, bool expected)
        => Assert.Equal(expected, Intelligence.AiAutoRunner.CompareOp(value, op, threshold));
}

[CollectionDefinition("AiMemory", DisableParallelization = true)]
public class AiMemoryCollection { }

/// <summary>
/// AI 记忆(重构 P2):
///   - 同主题+同内容不再重复写入(否则每次任务执行都把同一批错误再写一遍)
///   - Recall 能按"最近的"召回, 供注入系统提示
/// </summary>
[Collection("AiMemory")]
public class AiMemoryTests : IDisposable
{
    private readonly string _path = AiMemoryManager.FilePath;
    private readonly string? _backup;

    public AiMemoryTests()
    {
        _backup = File.Exists(_path) ? File.ReadAllText(_path) : null;
        AiMemoryManager.Save(new List<AiMemoryManager.MemoryEntry>());
    }

    public void Dispose()
    {
        try
        {
            if (_backup != null) File.WriteAllText(_path, _backup);
            else if (File.Exists(_path)) File.Delete(_path);
        }
        catch { }
    }

    private static void Add(string topic, string content)
        => AiMemoryManager.AddRange(
            new[] { AiMemoryManager.Create(topic, content, 500) }, 100, 500);

    [Fact]
    public void 相同主题与内容不会重复写入()
    {
        Add("服务器常见错误", "java.lang.OutOfMemoryError");
        Add("服务器常见错误", "java.lang.OutOfMemoryError");

        Assert.Single(AiMemoryManager.Load());
    }

    [Fact]
    public void 空白差异也视为同一条()
    {
        Add("服务器常见错误", "Caused by:  Foo");
        Add("服务器常见错误", "Caused by:    Foo  ");

        Assert.Single(AiMemoryManager.Load());
    }

    [Fact]
    public void 不同内容会新增()
    {
        Add("服务器常见错误", "OutOfMemoryError");
        Add("服务器常见错误", "NoSuchPluginException");

        Assert.Equal(2, AiMemoryManager.Load().Count);
    }

    [Fact]
    public void Recall返回最近的若干条()
    {
        for (int i = 0; i < 6; i++) Add($"主题{i}", $"内容{i}");

        var recalled = AiMemoryManager.Recall(3, 100000);

        Assert.Equal(3, recalled.Count);
        Assert.Equal("主题5", recalled[^1].Topic);
        Assert.Equal("主题3", recalled[0].Topic);
    }

    [Fact]
    public void Recall受字符上限约束()
    {
        for (int i = 0; i < 6; i++) Add($"主题{i}", new string('x', 400));

        var recalled = AiMemoryManager.Recall(10, 1500);

        Assert.True(recalled.Count < 6, $"实际召回 {recalled.Count} 条");
    }
}

/// <summary>运行态快照: idle 计算不再依赖"恰好有人更新"</summary>
public class ServerRuntimeSnapshotTests
{
    [Fact]
    public void 重置后空闲时长接近零()
    {
        ServerRuntimeSnapshot.Reset();
        Assert.True(ServerRuntimeSnapshot.IdleMinutes >= 0 && ServerRuntimeSnapshot.IdleMinutes < 1);
        Assert.Null(ServerRuntimeSnapshot.LastCrashTime);
    }

    [Fact]
    public void 可写入与读取崩溃时间()
    {
        var t = DateTime.Now.AddMinutes(-5);
        ServerRuntimeSnapshot.LastCrashTime = t;
        Assert.Equal(t, ServerRuntimeSnapshot.LastCrashTime);
        ServerRuntimeSnapshot.Reset();
    }
}
