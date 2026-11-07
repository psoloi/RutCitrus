using System.Text.RegularExpressions;

namespace RtCli.Tests;

/// <summary>
/// rt about 的依赖库表改为"只登记包名, 版本运行时从程序集自动读取"后的约定:
///   1) 每个登记的库都能解析出版本(否则运行时表格显示"未加载");
///   2) 解析出的版本与 RtCli.csproj 声明的包版本一致 —— 这条证明自动读取拿到的是
///      真实包版本(AssemblyInformationalVersion), 而不是像 13.0.0.0 那样的 AssemblyVersion;
///   3) 登记的库都是 csproj 的直接依赖, 除仅构建期的包外没有遗漏。
/// </summary>
public class AboutDependencyTableTests
{
    /// <summary>csproj 里声明但运行时不会作为独立包加载的条目</summary>
    private static readonly HashSet<string> NonRuntimePackages = new(StringComparer.Ordinal)
    {
        "Grpc.Tools"         // PrivateAssets=all, 仅构建期
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RtCli", "RtCli.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根目录(未找到 RtCli/RtCli.csproj)");
    }

    private static Dictionary<string, string> DeclaredInCsproj()
    {
        string csproj = File.ReadAllText(Path.Combine(RepoRoot(), "RtCli", "RtCli.csproj"));
        return Regex.Matches(csproj, "<PackageReference Include=\"([^\"]+)\" Version=\"([^\"]+)\"")
                    .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    [Fact]
    public void 每个依赖都能解析出版本()
    {
        var unresolved = Program.DependencyLibraries
            .Where(l => Program.ResolveDependencyVersion(l.AssemblyName) == "-")
            .Select(l => l.Name)
            .ToList();
        Assert.True(unresolved.Count == 0, "以下依赖运行时解析不到版本: " + string.Join(", ", unresolved));
    }

    [Fact]
    public void 解析版本与csproj声明一致()
    {
        var declared = DeclaredInCsproj();
        var wrong = new List<string>();

        foreach (var (name, asmName) in Program.DependencyLibraries)
        {
            string resolved = Program.ResolveDependencyVersion(asmName);
            Assert.False(string.IsNullOrWhiteSpace(resolved));
            Assert.DoesNotContain('+', resolved);
            Assert.DoesNotContain('-', resolved);

            if (declared.TryGetValue(name, out var expected) && resolved != expected)
                wrong.Add($"{name}: 解析={resolved} csproj={expected}");
        }
        Assert.True(wrong.Count == 0, "自动解析的版本与 csproj 不一致: " + string.Join(" | ", wrong));
    }

    [Fact]
    public void 登记的库都是csproj直接依赖()
    {
        var declared = DeclaredInCsproj();
        var extra = Program.DependencyLibraries.Select(l => l.Name).Where(n => !declared.ContainsKey(n)).ToList();
        Assert.True(extra.Count == 0, "依赖表列了非直接依赖: " + string.Join(", ", extra));
    }

    [Fact]
    public void 未收录的仅限构建期与框架裁剪包()
    {
        var declared = DeclaredInCsproj().Keys.ToHashSet(StringComparer.Ordinal);
        var listed = Program.DependencyLibraries.Select(l => l.Name).ToHashSet(StringComparer.Ordinal);

        var missing = declared.Except(listed).ToHashSet(StringComparer.Ordinal);
        Assert.True(missing.SetEquals(NonRuntimePackages),
            "未收录的依赖超出预期集合, 实际: " + string.Join(", ", missing));
    }

    [Fact]
    public void 依赖表无重复项()
    {
        var names = Program.DependencyLibraries.Select(l => l.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        var asms = Program.DependencyLibraries.Select(l => l.AssemblyName).ToList();
        Assert.Equal(asms.Count, asms.Distinct().Count());
    }

    [Fact]
    public void 不存在的程序集解析为未加载标记()
        => Assert.Equal("-", Program.ResolveDependencyVersion("这种程序集不存在.RtCli.Tests"));
}
