using RtCli.Modules.Function;

namespace RtCli.Tests;

/// <summary>
/// AI 工具的安全边界(重构 P0):
///   - run_command 系统命令 fail-closed(默认拒绝, 白名单前缀放行)
///   - modify_file 受保护文件 / 受限扩展名 / 存档目录 / 目录穿越 / 体积上限
///   - 工具调用审计可写入并按任务归属读回
/// </summary>
// 与 AiAutomationObservabilityTests 共用审计文件, 必须串行(否则 Count 断言会竞态)
[Collection("AiMemory")]
public class AiToolSafetyTests
{
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rt_hardening_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ================= run_command: 系统命令 fail-closed =================

    [Fact]
    public void 白名单为空时拒绝任何系统命令()
        => Assert.False(Intelligence.AiTools.IsSystemCommandAllowed("jps", new List<string>()));

    [Fact]
    public void 白名单为null时拒绝任何系统命令()
        => Assert.False(Intelligence.AiTools.IsSystemCommandAllowed("jps", null));

    [Fact]
    public void 白名单按前缀放行()
    {
        var allow = new List<string> { "jps", "tasklist" };
        Assert.True(Intelligence.AiTools.IsSystemCommandAllowed("jps", allow));
        Assert.True(Intelligence.AiTools.IsSystemCommandAllowed("jps -l", allow));
        Assert.True(Intelligence.AiTools.IsSystemCommandAllowed("TASKLIST /FI X", allow));
    }

    [Theory]
    [InlineData("del /s /q C:")]
    [InlineData("format D:")]
    [InlineData("shutdown -s -t 0")]
    [InlineData("taskkill /f /im java.exe")]
    [InlineData("jpsx")]
    public void 破坏性或非前缀命令被拒绝(string cmd)
        => Assert.False(Intelligence.AiTools.IsSystemCommandAllowed(cmd, new List<string> { "jps", "tasklist" }));

    [Fact]
    public async Task op提权命令被拒绝()
    {
        string r = await Intelligence.AiTools.ExecuteToolAsync("run_command", "{\"command\":\"/op Steve\"}");
        Assert.Contains("禁止执行的命令", r);
    }

    [Fact]
    public async Task 未开启系统命令时拒绝执行破坏性命令()
    {
        string r = await Intelligence.AiTools.ExecuteToolAsync("run_command", "{\"command\":\"del /s /q temp\"}");
        Assert.Contains("拒绝", r);
        Assert.Contains("安全", r);
    }

    // ================= modify_file: 路径与体积校验 =================

    [Theory]
    [InlineData("server.properties")]
    [InlineData("ops.json")]
    [InlineData("whitelist.json")]
    [InlineData("banned-players.json")]
    public void 受保护文件被拒绝(string rel)
    {
        string dir = NewTempDir();
        try
        {
            string? err = Intelligence.AiTools.ValidateModifyPath(dir, rel, 10, out _);
            Assert.NotNull(err);
            Assert.Contains("受保护", err);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("plugins/Foo.jar")]
    [InlineData("data/level.dat")]
    [InlineData("backup/world.zip")]
    public void 受限扩展名被拒绝(string rel)
    {
        string dir = NewTempDir();
        try
        {
            string? err = Intelligence.AiTools.ValidateModifyPath(dir, rel, 10, out _);
            Assert.NotNull(err);
            Assert.Contains("不允许修改该类型", err);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("world/level.dat")]
    [InlineData("libraries/x/y.txt")]
    [InlineData("plugins/.cache/a.txt")]
    public void 存档与依赖目录被拒绝(string rel)
    {
        string dir = NewTempDir();
        try
        {
            string? err = Intelligence.AiTools.ValidateModifyPath(dir, rel, 10, out _);
            Assert.NotNull(err);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 目录穿越被拒绝()
    {
        string dir = NewTempDir();
        try
        {
            string? err = Intelligence.AiTools.ValidateModifyPath(dir, "../escape.txt", 10, out _);
            Assert.NotNull(err);
            Assert.Contains("工作目录外", err);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 超过体积上限被拒绝()
    {
        string dir = NewTempDir();
        try
        {
            string? err = Intelligence.AiTools.ValidateModifyPath(dir, "plugins/Foo/config.yml", 2 * 1024 * 1024, out _);
            Assert.NotNull(err);
            Assert.Contains("内容过大", err);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 普通配置路径被放行()
    {
        string dir = NewTempDir();
        try
        {
            string? err = Intelligence.AiTools.ValidateModifyPath(dir, "plugins/Foo/config.yml", 100, out string full);
            Assert.Null(err);
            Assert.EndsWith("config.yml", full);
            Assert.StartsWith(Path.GetFullPath(dir), full);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ================= 审计 =================

    [Fact]
    public void 工具调用会写入审计并能读回()
    {
        AiToolAudit.CurrentTaskKey = "audit_test_task";
        try
        {
            AiToolAudit.Record("read_file", "{\"path\":\"a.txt\"}", "[成功]", true);
            var recent = AiToolAudit.ReadRecent(5);
            string last = recent[^1];
            Assert.Contains("audit_test_task", last);
            Assert.Contains("read_file", last);
            Assert.Contains("\"ok\":true", last);
        }
        finally
        {
            AiToolAudit.CurrentTaskKey = null;
        }
    }
}
