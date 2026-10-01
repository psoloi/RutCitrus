using System.IO.Compression;
using RtCli.Modules.Unit;

namespace RtCli.Tests;

/// <summary>
/// MC 服务端版本探测: 优先读服务端核心 jar 内 version.json 的 id,
/// 读不到再回退 logs/latest.log 的启动行。
/// </summary>
public class ServerVersionTests
{
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rt_srvver_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteJar(string path, string versionJson)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("version.json");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(versionJson);
    }

    private static void WriteLog(string dir, string version)
    {
        string logs = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "latest.log"),
            $"[12:00:00] [Server thread/INFO]: Starting minecraft server version {version}\r\n" +
            "[12:00:01] [Server thread/INFO]: Done (2.5s)!\r\n");
    }

    [Fact]
    public void 从jar内version_json读取id()
    {
        string dir = NewTempDir();
        try
        {
            WriteJar(Path.Combine(dir, "paper-1.21.4.jar"), "{\"id\":\"1.21.4\",\"name\":\"1.21.4\"}");
            var (version, jar) = ServerVersion.Detect(dir);
            Assert.Equal("1.21.4", version);
            Assert.EndsWith("paper-1.21.4.jar", jar);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void version_json只有name时也能取到()
    {
        string dir = NewTempDir();
        try
        {
            WriteJar(Path.Combine(dir, "server.jar"), "{\"name\":\"1.20.6\"}");
            var (version, _) = ServerVersion.Detect(dir);
            Assert.Equal("1.20.6", version);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 没有version_json时回退日志()
    {
        string dir = NewTempDir();
        try
        {
            // 一个不含 version.json 的 jar, 迫使走日志回退
            using (var zip = ZipFile.Open(Path.Combine(dir, "forge.jar"), ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("META-INF/MANIFEST.MF");
                using var w = new StreamWriter(e.Open());
                w.Write("Manifest-Version: 1.0\r\n");
            }
            WriteLog(dir, "1.20.1");

            var (version, jar) = ServerVersion.Detect(dir);
            Assert.Equal("1.20.1", version);
            Assert.Null(jar);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 都取不到时返回null()
    {
        string dir = NewTempDir();
        try
        {
            var (version, jar) = ServerVersion.Detect(dir);
            Assert.Null(version);
            Assert.Null(jar);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 空路径与不存在的目录返回null()
    {
        Assert.Null(ServerVersion.Detect(null).Version);
        Assert.Null(ServerVersion.Detect("").Version);
        Assert.Null(ServerVersion.Detect(Path.Combine(Path.GetTempPath(), "rt_not_exist_" + Guid.NewGuid().ToString("N"))).Version);
    }
}
