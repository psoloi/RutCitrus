using RtCli.Modules.Unit;

namespace RtCli.Tests;

/// <summary>
/// 插件/模组 jar 元数据解析: 面板"插件管理"显示版本、"模组管理"显示名称与版本都依赖它。
/// 覆盖模组格式: fabric.mod.json、mcmod.info、Forge/NeoForge 的 META-INF/mods.toml。
/// </summary>
public class JarMetaParserTests
{
    // ===== mods.toml (Forge / NeoForge) =====

    [Fact]
    public void mods_toml解析标准条目()
    {
        string toml = string.Join("\n",
            "modLoader=\"javafml\"",
            "loaderVersion=\"[47,)\"",
            "[[mods]]",
            "modId=\"examplemod\"",
            "version=\"1.2.3\"",
            "displayName=\"Example Mod\"",
            "description=\"A test mod\"");

        var meta = Backend.ParseTomlMeta(toml);

        Assert.NotNull(meta);
        Assert.Equal("Example Mod", meta!.Name);
        Assert.Equal("1.2.3", meta.Version);
        Assert.Equal("A test mod", meta.Description);
    }

    [Fact]
    public void mods_toml版本占位符视为无版本()
    {
        string toml = string.Join("\n",
            "[[mods]]",
            "modId=\"placeholder\"",
            "version=\"${file.jarVersion}\"",
            "displayName=\"P\"");

        var meta = Backend.ParseTomlMeta(toml);

        Assert.NotNull(meta);
        Assert.Equal("P", meta!.Name);
        Assert.Equal("", meta.Version);
    }

    [Fact]
    public void mods_toml缺少displayName时回退modId()
    {
        var meta = Backend.ParseTomlMeta("[[mods]]\nmodId=\"onlyid\"\nversion=\"1.0\"");
        Assert.NotNull(meta);
        Assert.Equal("onlyid", meta!.Name);
        Assert.Equal("1.0", meta.Version);
    }

    [Fact]
    public void mods_toml支持单引号值()
    {
        var meta = Backend.ParseTomlMeta("[[mods]]\nmodId=\"q\"\ndisplayName='Quoted Mod'\ndescription='d'");
        Assert.NotNull(meta);
        Assert.Equal("Quoted Mod", meta!.Name);
        Assert.Equal("d", meta.Description);
    }

    [Fact]
    public void mods_toml无标识时返回null()
    {
        Assert.Null(Backend.ParseTomlMeta("[[mods]]\nversion=\"1.0\""));
        Assert.Null(Backend.ParseTomlMeta(""));
    }

    // ===== JSON 元数据 (fabric.mod.json / mcmod.info / velocity-plugin.json) =====

    [Fact]
    public void fabric格式优先使用name()
    {
        var meta = Backend.ParseJsonMeta("{\"id\":\"mymod\",\"name\":\"My Mod\",\"version\":\"2.0.1\",\"description\":\"desc\"}");
        Assert.NotNull(meta);
        Assert.Equal("My Mod", meta!.Name);
        Assert.Equal("2.0.1", meta.Version);
        Assert.Equal("desc", meta.Description);
    }

    [Fact]
    public void fabric格式无name时回退id()
    {
        var meta = Backend.ParseJsonMeta("{\"id\":\"mymod\",\"version\":\"2.0.1\"}");
        Assert.NotNull(meta);
        Assert.Equal("mymod", meta!.Name);
    }

    [Fact]
    public void mcmod_info数组使用modid()
    {
        var meta = Backend.ParseJsonMeta("[{\"modid\":\"legacymod\",\"version\":\"1.7.10\"}]");
        Assert.NotNull(meta);
        Assert.Equal("legacymod", meta!.Name);
        Assert.Equal("1.7.10", meta.Version);
    }

    [Fact]
    public void velocity格式取plugins首项()
    {
        var meta = Backend.ParseJsonMeta("{\"plugins\":[{\"id\":\"velo\",\"version\":\"3.0\"}]}");
        Assert.NotNull(meta);
        Assert.Equal("velo", meta!.Name);
    }

    [Fact]
    public void 非法JSON或无标识返回null()
    {
        Assert.Null(Backend.ParseJsonMeta("not json"));
        Assert.Null(Backend.ParseJsonMeta("{\"version\":\"1.0\"}"));
    }
}
