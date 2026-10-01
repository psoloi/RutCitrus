using RtCli.Modules.Function;
using RtCli.Modules.Unit;

namespace RtCli.Tests;

/// <summary>
/// AI 自动化观测(面板「AI 自动化」页的数据来源):
///   - 工具调用审计可计数并读回
///   - ai_save.yml 的历史结论可解析, 且最新在前
///   - 任务运行态查询不会误报
/// </summary>
[Collection("AiMemory")]
public class AiAutomationObservabilityTests : IDisposable
{
    private readonly string _savePath = Path.Combine(Config.DataPath, "ai_save.yml");
    private readonly string? _saveBackup;

    public AiAutomationObservabilityTests()
    {
        Directory.CreateDirectory(Config.DataPath);
        _saveBackup = File.Exists(_savePath) ? File.ReadAllText(_savePath) : null;
    }

    public void Dispose()
    {
        try
        {
            if (_saveBackup != null) File.WriteAllText(_savePath, _saveBackup);
            else if (File.Exists(_savePath)) File.Delete(_savePath);
        }
        catch { }
    }

    [Fact]
    public void 审计条数随记录增长()
    {
        int before = AiToolAudit.Count();
        AiToolAudit.Record("read_file", "{\"path\":\"a.txt\"}", "[成功]", true);
        Assert.Equal(before + 1, AiToolAudit.Count());
    }

    [Fact]
    public void 历史结论可解析且最新在前()
    {
        File.WriteAllText(_savePath, string.Join("\n",
            "- task_key: tps_monitor",
            "  task_name: TPS监测",
            "  input_summary: 摘要A",
            "  ai_response: 结论A",
            "  timestamp: 2026-01-01 10:00:00",
            "- task_key: crash_detect",
            "  task_name: 崩溃检测",
            "  input_summary: 摘要B",
            "  ai_response: 结论B",
            "  timestamp: 2026-01-01 11:00:00",
            ""));

        var list = Intelligence.AiAutoRunner.ReadSavedResponses(10, out int total);

        Assert.Equal(2, total);
        Assert.Equal(2, list.Count);
        // 最新在前
        Assert.Equal("crash_detect", list[0].TaskKey);
        Assert.Equal("结论B", list[0].AiResponse);
        Assert.Equal("tps_monitor", list[1].TaskKey);
    }

    [Fact]
    public void 历史结论支持条数限制()
    {
        var lines = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            lines.Add($"- task_key: t{i}");
            lines.Add($"  task_name: 任务{i}");
            lines.Add($"  input_summary: 摘要{i}");
            lines.Add($"  ai_response: 结论{i}");
            lines.Add($"  timestamp: 2026-01-01 1{i}:00:00");
        }
        File.WriteAllText(_savePath, string.Join("\n", lines));

        var list = Intelligence.AiAutoRunner.ReadSavedResponses(3, out int total);

        Assert.Equal(10, total);
        Assert.Equal(3, list.Count);
        Assert.Equal("t9", list[0].TaskKey);
    }

    [Fact]
    public void 文件不存在时返回空且不抛异常()
    {
        if (File.Exists(_savePath)) File.Delete(_savePath);
        var list = Intelligence.AiAutoRunner.ReadSavedResponses(5, out int total);
        Assert.Empty(list);
        Assert.Equal(0, total);
    }

    [Fact]
    public void 未执行的任务不会误报运行中()
        => Assert.False(Intelligence.AiAutoRunner.IsTaskRunning("not_a_real_task"));
}
