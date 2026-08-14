using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Polly;
using Polly.Retry;
using RtCli.Modules.Extension;
using RtCli.Modules.Unit;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RtCli.Modules.Function
{
    internal class Intelligence
    {
        private static readonly string ThisProgramName = "Auto";

        private static readonly string[] DefaultPopularVersions = new[]
        {
            "26.1", "26.2",
            "1.21.11", "1.21.4", "1.21.3", "1.21.1", "1.21",
            "1.20.6", "1.20.4", "1.20.2", "1.20.1",
            "1.19.4", "1.19.2",
            "1.18.2",
            "1.16.5",
            "1.12.2",
            "1.8.8",
        };

        private static string[] GetPopularVersions()
        {
            var configVersions = Config.App.PopularVersions;
            if (configVersions != null && configVersions.Count > 0)
                return configVersions.ToArray();
            return DefaultPopularVersions;
        }

        private static readonly ServerTypeInfo[] ServerList = new[]
        {
            new ServerTypeInfo("Vanilla (原版)", ServerApi.Vanilla, "server.jar"),
            new ServerTypeInfo("Vanilla-Snapshot (快照)", ServerApi.VanillaSnapshot, "server-snapshot.jar"),
            new ServerTypeInfo("Paper (高性能)", ServerApi.PaperMC, "paper.jar", "paper"),
            new ServerTypeInfo("Purpur (Paper分支)", ServerApi.Purpur, "purpur.jar"),
            new ServerTypeInfo("Folia (多线程)", ServerApi.PaperMC, "folia.jar", "folia"),
            new ServerTypeInfo("Velocity (代理)", ServerApi.PaperMC, "velocity.jar", "velocity"),
            new ServerTypeInfo("Fabric (模组加载器)", ServerApi.Fabric, "fabric-server-launch.jar"),
            new ServerTypeInfo("Spigot (普通)", ServerApi.Manual, "spigot.jar", website: "https://www.spigotmc.org/wiki/spigot-installation/"),
            new ServerTypeInfo("Forge (模组加载器)", ServerApi.Manual, "forge.jar", website: "https://files.minecraftforge.net/net/minecraftforge/forge/"),
            new ServerTypeInfo("NeoForge (模组加载器)", ServerApi.Manual, "neoforge.jar", website: "https://neoforged.net/"),
            new ServerTypeInfo("Arclight-Forge (Forge混合端)", ServerApi.Manual, "arclight-forge.jar", website: "https://github.com/IzzelAliz/Arclight/releases"),
            new ServerTypeInfo("Arclight-Fabric (Fabric混合端)", ServerApi.Manual, "arclight-fabric.jar", website: "https://github.com/IzzelAliz/Arclight/releases"),
            new ServerTypeInfo("Arclight-NeoForge (NeoForge混合端)", ServerApi.Manual, "arclight-neoforge.jar", website: "https://github.com/IzzelAliz/Arclight/releases"),
            new ServerTypeInfo("Sponge (Forge混合端)", ServerApi.Manual, "sponge.jar", website: "https://spongepowered.org/downloads"),
            new ServerTypeInfo("Mohist (Forge混合端)", ServerApi.Manual, "mohist.jar", website: "https://mohistmc.com/downloads"),
            new ServerTypeInfo("CatServer (Forge混合端)", ServerApi.Manual, "catserver.jar", website: "https://github.com/Luohuayu/CatServer/"),
            new ServerTypeInfo("Silkard (Fabric混合端)", ServerApi.Manual, "silkard.jar", website: "https://mohistmc.cn/downloads"),
            new ServerTypeInfo("Leaf (Paper分支)", ServerApi.Leaf, "leaf.jar", "leaf"),
            new ServerTypeInfo("Leaves (Paper分支)", ServerApi.Manual, "leaves.jar", website: "https://github.com/LeavesMC/Leaves/releases"),
            new ServerTypeInfo("Luminol (Folia分支)", ServerApi.Manual, "luminol.jar", website: "https://github.com/LuminolMC/Luminol/releases"),
            new ServerTypeInfo("Pufferfish (Paper分支)", ServerApi.Manual, "pufferfish.jar", website: "https://ci.pufferfish.host/"),
            new ServerTypeInfo("BungeeCord (代理)", ServerApi.Manual, "BungeeCord.jar", website: "https://www.spigotmc.org/wiki/bungeecord/"),
        };

        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        public static async Task Guide()
        {
            string ThisProgramName = "Guide";

            AnsiConsole.Write(new Rule("[yellow]Minecraft 服务端架设引导[/]").RuleStyle("grey").Centered());

            // 多服务器选择
            if (Config.App.ServerList.Count > 1)
            {
                const string addNewMarker = "+ 添加新服务端";
                var serverChoices = Config.App.ServerList.Keys.ToList();
                serverChoices.Add(addNewMarker);
                var selected = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("检测到多个服务端配置，请选择要配置的服务端：")
                        .AddChoices(serverChoices));

                if (selected == addNewMarker)
                {
                    string newKey = AnsiConsole.Ask<string>("请输入新服务端标识（英文）：");
                    if (!string.IsNullOrWhiteSpace(newKey) && !Config.App.ServerList.ContainsKey(newKey))
                    {
                        Config.App.ServerList[newKey] = new ServerEntry { ServerName = newKey };
                        Config.App.CurrentServer = newKey;
                    }
                }
                else
                {
                    Config.App.CurrentServer = selected;
                }
            }

            if (!await Step1_CheckJava()) return;
            if (!await Step2_DownloadServer()) return;
            if (!await Step3_ConfigureAndStart()) return;

            AnsiConsole.Write(new Rule("[green]引导完成！[/]").RuleStyle("green").Centered());
            Output.Log("服务端架设引导完成！之后可以使用 .server start 启动服务端。", 1, ThisProgramName);
        }

        private static Task<bool> Step1_CheckJava()
        {
            string ThisProgramName = "Guide";
            Output.Log("[[步骤 1/3]] 检查 Java 环境...", 1, ThisProgramName);

            string javaResult = Checker.CheckJava();
            if (javaResult.StartsWith(I18n.Get("checker_nojava")))
            {
                Output.Log("未检测到 Java 环境，请先安装 JDK 17 或更高版本。如果非安装则请设置Java环境变量", 3, ThisProgramName);

                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn("JDK 类型")
                    .AddColumn("下载地址")
                    .AddColumn("备注");
                table.AddRow("<非商业> OracleJDK", "https://www.oracle.com/java/technologies/downloads/", "商业用途需授权");
                table.AddRow("<非商业> GraalVM", "https://www.graalvm.org/downloads/", "高性能，商业用途需授权");
                table.AddRow("[green]<开源> OpenJDK[/]", "https://jdk.java.net/", "可自由使用");
                table.AddRow("[green]<开源> Adoptium[/]", "https://adoptium.net/", "推荐，社区维护");
                AnsiConsole.Write(table);

                Output.Log("安装 JDK 后请重新运行引导。", 2, ThisProgramName);
                return Task.FromResult(false);
            }

            string versionStr = javaResult.Substring(I18n.Get("checker_java").Length).Trim();
            Output.Log($"Java 环境检测通过: {versionStr}", 1, ThisProgramName);

            if (!IsJavaVersionAtLeast(versionStr, 17))
            {
                Output.Log($"Java 版本过低 (需要 JDK 17+)，当前: {versionStr}", 3, ThisProgramName);
                Output.Log("MC 1.20.5+ 需要 JDK 21，MC 1.17+ 需要 JDK 17。", 2, ThisProgramName);

                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn("JDK 类型")
                    .AddColumn("下载地址")
                    .AddColumn("备注");
                table.AddRow("[green]<开源> Adoptium JDK 21[/]", "https://adoptium.net/", "推荐，MC 1.20.5+");
                table.AddRow("<开源> Adoptium JDK 17", "https://adoptium.net/", "MC 1.17~1.20.4");
                AnsiConsole.Write(table);

                Output.Log("升级 JDK 后请重新运行引导。", 2, ThisProgramName);
                return Task.FromResult(false);
            }

            Output.Log("Java 版本满足要求 (JDK 17+)。", 1, ThisProgramName);
            return Task.FromResult(true);
        }

        private static async Task<bool> Step2_DownloadServer()
        {
            string ThisProgramName = "Guide";
            Output.Log("[[步骤 2/3]] 选择并下载 Minecraft 服务端...", 1, ThisProgramName);

            var choices = ServerList.Select(s => s.Name).ToList();
            choices.Add("跳过 (我已有服务端文件)");

            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("请选择要使用的服务端类型：")
                    .AddChoices(choices));

            if (selected == "跳过 (我已有服务端文件)")
            {
                return await Step2b_UseExistingServer();
            }

            var serverInfo = ServerList.First(s => s.Name == selected);

            string workPath = GetOrAskWorkPath();
            if (string.IsNullOrEmpty(workPath)) return false;

            if (!Directory.Exists(workPath))
            {
                Directory.CreateDirectory(workPath);
                Output.Log($"已创建工作目录: {workPath}", 1, ThisProgramName);
            }

            string? downloadedJar = null;

            switch (serverInfo.Api)
            {
                case ServerApi.Vanilla:
                    downloadedJar = await DownloadVanilla(workPath, false);
                    break;

                case ServerApi.VanillaSnapshot:
                    downloadedJar = await DownloadVanilla(workPath, true);
                    break;

                case ServerApi.PaperMC:
                    downloadedJar = await DownloadPaperMC(workPath, serverInfo.ProjectId!, serverInfo.DefaultFileName);
                    break;

                case ServerApi.Leaf:
                    downloadedJar = await DownloadLeaf(workPath, serverInfo.ProjectId!);
                    break;

                case ServerApi.Purpur:
                    downloadedJar = await DownloadPurpur(workPath, serverInfo.DefaultFileName);
                    break;

                case ServerApi.Fabric:
                    downloadedJar = await DownloadFabric(workPath);
                    break;

                case ServerApi.Manual:
                    downloadedJar = await HandleManualDownload(workPath, serverInfo);
                    break;
            }

            if (downloadedJar == null)
            {
                Output.Log("服务端下载失败，请重试或手动下载。", 2, ThisProgramName);
                return false;
            }

            UpdateConfig(workPath, downloadedJar);
            return true;
        }

        #region Download Methods

        private static async Task<string?> DownloadVanilla(string workPath, bool includeSnapshots)
        {
            string ThisProgramName = "Guide";
            string label = includeSnapshots ? "Vanilla/Snapshot" : "Vanilla";
            try
            {
                Output.Log($"正在获取 {label} 版本列表...", 1, ThisProgramName);

                string manifestJson = await _httpClient.GetStringAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
                var manifest = JObject.Parse(manifestJson);
                var allVersions = manifest["versions"]!
                    .Select(v => new { Id = v["id"]!.ToString(), Type = v["type"]!.ToString(), Url = v["url"]!.ToString() })
                    .ToList();

                var filteredVersions = includeSnapshots
                    ? allVersions.Where(v => v.Type == "release" || v.Type == "snapshot").ToList()
                    : allVersions.Where(v => v.Type == "release").ToList();

                var availablePopular = filteredVersions.Where(v => GetPopularVersions().Contains(v.Id)).ToList();
                if (!availablePopular.Any())
                {
                    availablePopular = filteredVersions.Take(10).ToList();
                }

                var versionChoices = new List<string>();
                versionChoices.Add($"[[最新版]] {filteredVersions.First().Id}");
                foreach (var v in availablePopular)
                {
                    string suffix = v.Type == "snapshot" ? " (快照)" : "";
                    versionChoices.Add(v.Id + suffix);
                }

                var selectedVersion = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title($"选择 {label} 版本：")
                        .AddChoices(versionChoices));

                string versionId;
                if (selectedVersion.StartsWith("[[最新版]] "))
                {
                    versionId = filteredVersions.First().Id;
                }
                else
                {
                    versionId = selectedVersion.Replace(" (快照)", "");
                }

                var versionInfo = filteredVersions.First(v => v.Id == versionId);
                string versionDetailJson = await _httpClient.GetStringAsync(versionInfo.Url);
                var versionDetail = JObject.Parse(versionDetailJson);

                string? serverUrl = versionDetail["downloads"]?["server"]?["url"]?.ToString();
                if (string.IsNullOrEmpty(serverUrl))
                {
                    Output.Log($"版本 {versionId} 没有服务端下载。", 2, ThisProgramName);
                    return null;
                }

                string jarName = includeSnapshots ? $"server-{versionId}.jar" : $"server-{versionId}.jar";
                string jarPath = Path.Combine(workPath, jarName);

                if (File.Exists(jarPath))
                {
                    Output.Log($"文件已存在: {jarPath}", 1, ThisProgramName);
                    return jarName;
                }

                bool downloaded = await DownloadServerJar(serverUrl, jarPath, $"{label} {versionId}");
                return downloaded ? jarName : null;
            }
            catch (Exception ex)
            {
                Output.Log($"获取 {label} 版本列表失败: {ex.Message}", 3, ThisProgramName);
                return null;
            }
        }

        private static async Task<string?> DownloadPaperMC(string workPath, string projectId, string defaultFileName)
        {
            string ThisProgramName = "Guide";
            try
            {
                // PaperMC Fill v3 API 要求设置 User-Agent
                if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
                {
                    _httpClient.DefaultRequestHeaders.Add("User-Agent",
                        $"RtCli/{Program.RtCliVersion} (https://github.com/RutCitrus/RtCli)");
                }

                const string apiBase = "https://fill.papermc.io/v3";

                // 1. 获取版本列表 (v3: versions 是字典，键=主版本号，值=子版本数组)
                Output.Log($"正在获取 {projectId} 版本列表...", 1, ThisProgramName);

                string projectJson = await _httpClient.GetStringAsync($"{apiBase}/projects/{projectId}");
                var project = JObject.Parse(projectJson);
                var versionsObj = project["versions"] as JObject;

                if (versionsObj == null)
                {
                    Output.Log($"未找到 {projectId} 的版本列表。", 2, ThisProgramName);
                    return null;
                }

                var allVersions = new List<string>();
                foreach (var prop in versionsObj.Properties())
                {
                    if (prop.Value is JArray arr)
                    {
                        foreach (var v in arr)
                            allVersions.Add(v.ToString());
                    }
                }

                if (!allVersions.Any())
                {
                    Output.Log($"未找到 {projectId} 的可用版本。", 2, ThisProgramName);
                    return null;
                }

                var availablePopular = allVersions.Where(v => GetPopularVersions().Contains(v)).ToList();
                if (!availablePopular.Any())
                {
                    availablePopular = allVersions.Take(10).ToList();
                }

                var versionChoices = new List<string>();
                versionChoices.Add($"[[最新版]] {allVersions[0]}");
                foreach (var v in availablePopular)
                {
                    versionChoices.Add(v);
                }

                var selectedVersion = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title($"选择 {projectId} 版本：")
                        .AddChoices(versionChoices));

                string version;
                if (selectedVersion.StartsWith("[[最新版]] "))
                {
                    version = allVersions[0];
                }
                else
                {
                    version = selectedVersion;
                }

                // 2. 获取构建列表 (v3: 直接返回数组，含 channel 和 downloads)
                Output.Log($"正在获取 {projectId} {version} 构建列表...", 1, ThisProgramName);
                string buildsJson = await _httpClient.GetStringAsync($"{apiBase}/projects/{projectId}/versions/{version}/builds");
                var buildsArray = JArray.Parse(buildsJson);

                if (buildsArray.Count == 0)
                {
                    Output.Log($"版本 {version} 没有可用的构建。", 2, ThisProgramName);
                    return null;
                }

                // 优先选择 STABLE 渠道构建 (Velocity 使用 RECOMMENDED)
                var stableBuilds = buildsArray
                    .Where(b => b["channel"]?.ToString() == "STABLE")
                    .ToList();
                var recommendedBuilds = buildsArray
                    .Where(b => b["channel"]?.ToString() == "RECOMMENDED")
                    .ToList();

                JToken? selectedBuild;
                if (stableBuilds.Any())
                    selectedBuild = stableBuilds.Last();
                else if (recommendedBuilds.Any())
                    selectedBuild = recommendedBuilds.Last();
                else
                    selectedBuild = buildsArray.Last();

                int buildId = selectedBuild["id"]?.ToObject<int>() ?? 0;
                string channel = selectedBuild["channel"]?.ToString() ?? "UNKNOWN";

                // v3: 下载链接直接在构建信息中，无需手动构造
                string? downloadUrl = selectedBuild["downloads"]?["server:default"]?["url"]?.ToString();
                string? fileName = selectedBuild["downloads"]?["server:default"]?["name"]?.ToString();

                if (string.IsNullOrEmpty(downloadUrl))
                {
                    Output.Log($"构建 {buildId} 没有可用的下载链接。", 2, ThisProgramName);
                    return null;
                }

                if (string.IsNullOrEmpty(fileName))
                {
                    fileName = $"{projectId}-{version}-{buildId}.jar";
                }

                string jarName = $"{projectId}-{version}.jar";
                string jarPath = Path.Combine(workPath, jarName);

                if (File.Exists(jarPath))
                {
                    Output.Log($"文件已存在: {jarPath}", 1, ThisProgramName);
                    return jarName;
                }

                Output.Log($"选择构建: {buildId} (渠道: {channel})", 1, ThisProgramName);
                bool downloaded = await DownloadServerJar(downloadUrl, jarPath, $"{projectId} {version} (build {buildId})");
                return downloaded ? jarName : null;
            }
            catch (Exception ex)
            {
                Output.Log($"获取 {projectId} 下载信息失败: {ex.Message}", 3, ThisProgramName);
                return null;
            }
        }

        private static async Task<string?> DownloadLeaf(string workPath, string projectId)
        {
            string ThisProgramName = "Guide";
            try
            {
                Output.Log($"正在获取 {projectId} 版本列表...", 1, ThisProgramName);

                string projectJson = await _httpClient.GetStringAsync($"https://api.leafmc.one/v2/projects/{projectId}");
                var project = JObject.Parse(projectJson);
                var allVersions = project["versions"]!.Select(v => v.ToString()).ToList();

                var availablePopular = allVersions.Where(v => GetPopularVersions().Contains(v)).ToList();
                if (!availablePopular.Any())
                {
                    availablePopular = allVersions.Take(10).ToList();
                }

                var versionChoices = new List<string>();
                versionChoices.Add($"[[最新版]] {allVersions.Last()}");
                foreach (var v in availablePopular)
                {
                    versionChoices.Add(v);
                }

                var selectedVersion = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title($"选择 {projectId} 版本：")
                        .AddChoices(versionChoices));

                string version;
                if (selectedVersion.StartsWith("[[最新版]] "))
                {
                    version = allVersions.Last();
                }
                else
                {
                    version = selectedVersion;
                }

                string buildsJson = await _httpClient.GetStringAsync($"https://api.leafmc.one/v2/projects/{projectId}/versions/{version}");
                var buildsData = JObject.Parse(buildsJson);
                var builds = buildsData["builds"]!.Select(b => b.ToString()).ToList();

                if (!builds.Any())
                {
                    Output.Log($"版本 {version} 没有可用的构建。", 2, ThisProgramName);
                    return null;
                }

                string latestBuild = builds.Last();

                string buildDetailJson = await _httpClient.GetStringAsync($"https://api.leafmc.one/v2/projects/{projectId}/versions/{version}/builds/{latestBuild}");
                var buildDetail = JObject.Parse(buildDetailJson);
                string? fileName = buildDetail["downloads"]?["application"]?["name"]?.ToString();

                if (string.IsNullOrEmpty(fileName))
                {
                    fileName = $"{projectId}-{version}-{latestBuild}.jar";
                }

                string downloadUrl = $"https://api.leafmc.one/v2/projects/{projectId}/versions/{version}/builds/{latestBuild}/downloads/{fileName}";
                string jarName = $"{projectId}-{version}.jar";
                string jarPath = Path.Combine(workPath, jarName);

                if (File.Exists(jarPath))
                {
                    Output.Log($"文件已存在: {jarPath}", 1, ThisProgramName);
                    return jarName;
                }

                bool downloaded = await DownloadServerJar(downloadUrl, jarPath, $"{projectId} {version} (build {latestBuild})");
                return downloaded ? jarName : null;
            }
            catch (Exception ex)
            {
                Output.Log($"获取 {projectId} 版本列表失败: {ex.Message}", 3, ThisProgramName);
                return null;
            }
        }

        private static async Task<string?> DownloadPurpur(string workPath, string defaultFileName)
        {
            string ThisProgramName = "Guide";
            try
            {
                Output.Log("正在获取 Purpur 版本列表...", 1, ThisProgramName);

                string versionsJson = await _httpClient.GetStringAsync("https://api.purpurmc.org/v2/purpur");
                var versionsData = JObject.Parse(versionsJson);
                var allVersions = versionsData["versions"]!.Select(v => v.ToString()).ToList();

                var availablePopular = allVersions.Where(v => GetPopularVersions().Contains(v)).ToList();
                if (!availablePopular.Any())
                {
                    availablePopular = allVersions.Take(10).ToList();
                }

                var versionChoices = new List<string>();
                versionChoices.Add($"[[最新版]] {allVersions.Last()}");
                foreach (var v in availablePopular)
                {
                    versionChoices.Add(v);
                }

                var selectedVersion = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("选择 Purpur 版本：")
                        .AddChoices(versionChoices));

                string version;
                if (selectedVersion.StartsWith("[[最新版]] "))
                {
                    version = allVersions.Last();
                }
                else
                {
                    version = selectedVersion;
                }

                string jarName = $"purpur-{version}.jar";
                string jarPath = Path.Combine(workPath, jarName);

                if (File.Exists(jarPath))
                {
                    Output.Log($"文件已存在: {jarPath}", 1, ThisProgramName);
                    return jarName;
                }

                string downloadUrl = $"https://api.purpurmc.org/v2/purpur/{version}/latest/download";
                bool downloaded = await DownloadServerJar(downloadUrl, jarPath, $"Purpur {version}");
                return downloaded ? jarName : null;
            }
            catch (Exception ex)
            {
                Output.Log($"获取 Purpur 版本列表失败: {ex.Message}", 3, ThisProgramName);
                return null;
            }
        }

        private static async Task<string?> DownloadFabric(string workPath)
        {
            string ThisProgramName = "Guide";
            try
            {
                Output.Log("正在获取 Fabric 版本列表...", 1, ThisProgramName);

                string gameVersionsJson = await _httpClient.GetStringAsync("https://meta.fabricmc.net/v2/versions/game");
                var gameVersions = JArray.Parse(gameVersionsJson);
                var stableVersions = gameVersions.Where(v => v["stable"]?.Value<bool>() == true).ToList();

                var allVersionIds = stableVersions.Select(v => v["version"]!.ToString()).ToList();
                var availablePopular = allVersionIds.Where(v => GetPopularVersions().Contains(v)).ToList();
                if (!availablePopular.Any())
                {
                    availablePopular = allVersionIds.Take(10).ToList();
                }

                var versionChoices = new List<string>();
                versionChoices.Add($"[[最新版]] {allVersionIds.FirstOrDefault() ?? "unknown"}");
                foreach (var v in availablePopular)
                {
                    versionChoices.Add(v);
                }

                var selectedVersion = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("选择 Fabric 游戏版本：")
                        .AddChoices(versionChoices));

                string gameVersion;
                if (selectedVersion.StartsWith("[[最新版]] "))
                {
                    gameVersion = allVersionIds.FirstOrDefault() ?? "";
                }
                else
                {
                    gameVersion = selectedVersion;
                }

                if (string.IsNullOrEmpty(gameVersion))
                {
                    Output.Log("无法确定游戏版本。", 2, ThisProgramName);
                    return null;
                }

                string loaderVersionsJson = await _httpClient.GetStringAsync("https://meta.fabricmc.net/v2/versions/loader");
                var loaderVersions = JArray.Parse(loaderVersionsJson);
                string? latestLoader = loaderVersions.FirstOrDefault()?["version"]?.ToString();

                if (string.IsNullOrEmpty(latestLoader))
                {
                    Output.Log("无法获取 Fabric Loader 版本。", 2, ThisProgramName);
                    return null;
                }

                string jarName = $"fabric-{gameVersion}.jar";
                string jarPath = Path.Combine(workPath, jarName);

                if (File.Exists(jarPath))
                {
                    Output.Log($"文件已存在: {jarPath}", 1, ThisProgramName);
                    return jarName;
                }

                string downloadUrl = $"https://meta.fabricmc.net/v2/versions/loader/{gameVersion}/{latestLoader}/server/jar";
                bool downloaded = await DownloadServerJar(downloadUrl, jarPath, $"Fabric {gameVersion} (loader {latestLoader})");
                return downloaded ? jarName : null;
            }
            catch (Exception ex)
            {
                Output.Log($"获取 Fabric 版本列表失败: {ex.Message}", 3, ThisProgramName);
                return null;
            }
        }

        private static Task<string?> HandleManualDownload(string workPath, ServerTypeInfo serverInfo)
        {
            string ThisProgramName = "Guide";
            string website = serverInfo.Website ?? "";
            AnsiConsole.Write(new Markup($"[yellow]请手动下载 {Markup.Escape(serverInfo.Name)}：[/]\n"));
            if (!string.IsNullOrEmpty(website))
            {
                AnsiConsole.Write(new Markup($"  下载页面: [link]{Markup.Escape(website)}[/]\n\n"));
            }
            AnsiConsole.Write(new Markup($"下载后将 jar 文件放入: [white]{Markup.Escape(workPath)}[/]\n\n"));

            var jarFiles = Directory.GetFiles(workPath, "*.jar");
            if (jarFiles.Length == 0)
            {
                Output.Log("等待你将 jar 文件放入工作目录后按回车继续...", 1, ThisProgramName);
                Console.ReadLine();

                jarFiles = Directory.GetFiles(workPath, "*.jar");
            }

            if (jarFiles.Length > 0)
            {
                string jarName = Path.GetFileName(jarFiles[0]);
                Output.Log($"已检测到服务端文件: {jarName}", 1, ThisProgramName);
                return Task.FromResult<string?>(jarName);
            }

            Output.Log("未检测到 jar 文件，请确认后重新运行引导。", 2, ThisProgramName);
            return Task.FromResult<string?>(null);
        }

        #endregion

        private static Task<bool> Step2b_UseExistingServer()
        {
            string ThisProgramName = "Guide";
            string workPath = GetOrAskWorkPath();
            if (string.IsNullOrEmpty(workPath)) return Task.FromResult(false);

            if (!Directory.Exists(workPath))
            {
                Output.Log($"工作目录不存在: {workPath}", 3, ThisProgramName);
                return Task.FromResult(false);
            }

            var jarFiles = Directory.GetFiles(workPath, "*.jar");
            if (jarFiles.Length == 0)
            {
                Output.Log($"工作目录中没有找到 jar 文件: {workPath}", 2, ThisProgramName);
                Output.Log("请将服务端 jar 文件放入该目录后重新运行引导。", 2, ThisProgramName);
                return Task.FromResult(false);
            }

            string selectedJar;
            if (jarFiles.Length == 1)
            {
                selectedJar = Path.GetFileName(jarFiles[0]);
            }
            else
            {
                var jarChoices = jarFiles.Select(f => Path.GetFileName(f)).ToList();
                selectedJar = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("检测到多个 jar 文件，请选择服务端：")
                        .AddChoices(jarChoices));
            }

            UpdateConfig(workPath, selectedJar);
            Output.Log($"已选择服务端: {selectedJar}", 1, ThisProgramName);
            return Task.FromResult(true);
        }

        private static async Task<bool> Step3_ConfigureAndStart()
        {
            string ThisProgramName = "Guide";
            Output.Log("[[步骤 3/3]] 配置并启动服务端...", 1, ThisProgramName);

            if (string.IsNullOrWhiteSpace(Config.CurrentServer.WorkPath) || string.IsNullOrWhiteSpace(Config.CurrentServer.RunServerFlags))
            {
                Output.Log("配置未完成，请检查 work_path 和 run_server_flags。", 3, ThisProgramName);
                return false;
            }

            Config.CurrentServer.AnalyzerMode = "Management";
            SaveConfig();
            Analyzer.Initialize();
            Output.Log("已将模式设置为 Management。", 1, ThisProgramName);

            bool shouldStart = AnsiConsole.Confirm("是否现在启动服务端？", false);
            if (!shouldStart)
            {
                Output.Log("稍后可使用 .server start 启动服务端。", 1, ThisProgramName);
                return true;
            }

            string workPath = Config.CurrentServer.WorkPath;
            var jarFiles = Directory.GetFiles(workPath, "*.jar");
            var otherFiles = Directory.GetFiles(workPath).Where(f => !f.EndsWith(".jar")).ToList();
            var otherDirs = Directory.GetDirectories(workPath).ToList();

            bool isEmptyDir = jarFiles.Length > 0 && otherFiles.Count == 0 && otherDirs.Count == 0;

            Analyzer.StartServer();

            if (isEmptyDir)
            {
                Output.Log("检测到服务端目录为空（首次运行），等待 EULA 确认...", 1, ThisProgramName);

                // 轮询等待 eula.txt 生成（最多等待30秒）
                string eulaPath = Path.Combine(workPath, "eula.txt");
                bool eulaFound = false;
                for (int i = 0; i < 30; i++)
                {
                    await Task.Delay(1000);
                    if (File.Exists(eulaPath))
                    {
                        eulaFound = true;
                        break;
                    }
                    if (!Analyzer.IsRunModeActive)
                        break;
                }

                if (eulaFound)
                {
                    string eulaContent = File.ReadAllText(eulaPath);
                    if (eulaContent.Contains("eula=false"))
                    {
                        if (Config.App.AutoAgreeEula)
                        {
                            // auto_agree_eula=true: 自动同意并自动重启
                            eulaContent = eulaContent.Replace("eula=false", "eula=true");
                            File.WriteAllText(eulaPath, eulaContent);
                            Output.Log("已自动同意 EULA（配置: auto_agree_eula = true），正在重启服务端...", 1, ThisProgramName);
                            Analyzer.StartServer();
                            Output.Log("服务端已重新启动。", 1, ThisProgramName);
                        }
                        else
                        {
                            // auto_agree_eula=false: 提示用户阅读并确认
                            Output.Log("服务端因 EULA 未同意而自动关闭。", 2, ThisProgramName);
                            Output.Log("Minecraft EULA 说明: https://www.minecraft.net/eula", 1, ThisProgramName);

                            bool agree = AnsiConsole.Confirm("是否同意 Minecraft EULA？(阅读 https://www.minecraft.net/eula)", false);
                            if (agree)
                            {
                                eulaContent = eulaContent.Replace("eula=false", "eula=true");
                                File.WriteAllText(eulaPath, eulaContent);
                                Output.Log("已同意 EULA。", 1, ThisProgramName);

                                bool restart = AnsiConsole.Confirm("是否重新启动服务端？", true);
                                if (restart)
                                {
                                    Analyzer.StartServer();
                                    Output.Log("服务端已重新启动。", 1, ThisProgramName);
                                }
                            }
                            else
                            {
                                Output.Log("未同意 EULA，服务端无法运行。稍后可手动修改 eula.txt 后使用 .server start 启动。", 2, ThisProgramName);
                            }
                        }
                    }
                    else
                    {
                        Output.Log("eula.txt 已存在且已同意 EULA。", 1, ThisProgramName);
                    }
                }
                else
                {
                    if (!Analyzer.IsRunModeActive)
                    {
                        Output.Log("服务端启动后意外退出，未检测到 eula.txt。", 2, ThisProgramName);
                        Output.Log("请检查服务端日志确认问题。", 2, ThisProgramName);
                    }
                    else
                    {
                        Output.Log("服务端正在运行中。", 1, ThisProgramName);
                    }
                }
            }
            else
            {
                Output.Log("服务端正在启动中...", 1, ThisProgramName);
            }

            return true;
        }

        #region Helpers

        private static string GetOrAskWorkPath()
        {
            string workPath = Config.CurrentServer.WorkPath;

            if (!string.IsNullOrWhiteSpace(workPath) && Directory.Exists(workPath))
            {
                bool useExisting = AnsiConsole.Confirm($"当前工作目录为 {workPath}，是否使用？", true);
                if (useExisting) return workPath;
            }

            Output.Log("请输入 MC 服务端的工作目录路径（jar 文件所在目录），直接回车则自动创建：", 1, ThisProgramName);
            workPath = Console.ReadLine()?.Trim().Trim('"') ?? "";

            if (string.IsNullOrWhiteSpace(workPath))
            {
                // 自动在Content文件夹中创建Server/{serverKey}作为工作目录
                string serverKey = Config.App.CurrentServer;
                string autoPath = Path.GetFullPath(Path.Combine("Content", "Server", serverKey));
                if (!Directory.Exists(autoPath))
                {
                    Directory.CreateDirectory(autoPath);
                }
                workPath = autoPath;
                Output.Log($"已自动创建工作目录: {workPath}", 1, ThisProgramName);
            }

            return workPath;
        }

        private static void UpdateConfig(string workPath, string jarFileName)
        {
            Config.CurrentServer.WorkPath = workPath;

            string flags = Config.CurrentServer.RunServerFlags;
            if (flags.Contains("-jar"))
            {
                flags = Regex.Replace(flags, @"-jar\s+\S+", $"-jar {jarFileName}");
            }
            else
            {
                flags += $" -jar {jarFileName}";
            }
            Config.CurrentServer.RunServerFlags = flags;

            SaveConfig();

            Output.Log($"已更新配置: work_path={workPath}", 1, ThisProgramName);
            Output.Log($"已更新配置: run_server_flags 包含 -jar {jarFileName}", 1, ThisProgramName);
        }

        private static void SaveConfig()
        {
            Config.SaveCurrentConfig();
        }

        private static bool IsJavaVersionAtLeast(string versionStr, int minMajor)
        {
            try
            {
                string version = versionStr.Trim('"').Trim();

                if (version.StartsWith("1."))
                {
                    var parts = version.Split('.');
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int minor))
                    {
                        return minor >= minMajor;
                    }
                }
                else
                {
                    var parts = version.Split('.', '+', '-');
                    if (parts.Length >= 1 && int.TryParse(parts[0], out int major))
                    {
                        return major >= minMajor;
                    }
                }
            }
            catch { }

            return true;
        }

        private static async Task<bool> DownloadServerJar(string url, string savePath, string serverName)
        {
            try
            {
                Output.Log($"正在下载 {serverName}...", 1, ThisProgramName);

                using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

                await AnsiConsole.Progress()
                    .Columns(new ProgressColumn[]
                    {
                        new TaskDescriptionColumn(),
                        new ProgressBarColumn(),
                        new PercentageColumn(),
                        new DownloadedColumn(),
                        new RemainingTimeColumn(),
                    })
                    .StartAsync(async ctx =>
                    {
                        var task = ctx.AddTask($"[green]下载 {Markup.Escape(serverName)}[/]");

                        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                        response.EnsureSuccessStatusCode();

                        long? totalBytes = response.Content.Headers.ContentLength;
                        if (totalBytes.HasValue && totalBytes.Value > 0)
                        {
                            task.MaxValue = totalBytes.Value;
                        }
                        else
                        {
                            task.IsIndeterminate = true;
                        }

                        using var stream = await response.Content.ReadAsStreamAsync();
                        using var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None);

                        var buffer = new byte[8192];
                        long totalRead = 0;
                        int bytesRead;
                        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            totalRead += bytesRead;
                            task.Increment(bytesRead);

                            if (task.IsIndeterminate)
                            {
                                task.Description = $"[green]下载 {Markup.Escape(serverName)}[/] {totalRead / 1024.0 / 1024.0:F1} MB";
                            }
                        }
                    });

                Output.Log($"下载完成: {savePath}", 1, ThisProgramName);
                return true;
            }
            catch (Exception ex)
            {
                Output.Log($"下载失败: {ex.Message}", 3, ThisProgramName);
                Output.Log($"请手动下载: {url}", 2, ThisProgramName);

                if (File.Exists(savePath))
                {
                    try { File.Delete(savePath); } catch { }
                }

                return false;
            }
        }

        #endregion

        public static async Task<string?> AnalyzeWithAi(string errorContent)
        {
            string ThisProgramName = "AI";
            var aiConfig = ContentManager.Ai.Console_Error;

            if (string.IsNullOrWhiteSpace(aiConfig.ApiEndpoint))
            {
                Output.Log("AI API地址未配置，请在 ai_settings.yml 中设置。", 2, ThisProgramName);
                return null;
            }

            Output.Log($"正在请求AI分析 (模型: {aiConfig.Model})...", 1, ThisProgramName);

            try
            {
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(aiConfig.RequestTimeoutSeconds) };

                if (!string.IsNullOrWhiteSpace(aiConfig.ApiKey))
                {
                    httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {aiConfig.ApiKey}");
                }

                var requestBody = new
                {
                    model = aiConfig.Model,
                    messages = new[]
                    {
                        new { role = "system", content = aiConfig.Prompt },
                        new { role = "user", content = $"以下是Minecraft服务端的错误日志，请分析：\n\n{errorContent}" }
                    },
                    max_tokens = aiConfig.MaxTokens,
                    temperature = aiConfig.Temperature,
                    stream = false
                };

                var json = Newtonsoft.Json.JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                HttpResponseMessage response;

                if (aiConfig.RetryOnError)
                {
                    var retryPipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
                        .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
                        {
                            MaxRetryAttempts = aiConfig.RetryCount,
                            Delay = TimeSpan.FromSeconds(3),
                            BackoffType = DelayBackoffType.Exponential,
                            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                                .Handle<HttpRequestException>()
                                .Handle<TaskCanceledException>()
                                .HandleResult(r => !r.IsSuccessStatusCode),
                            OnRetry = args =>
                            {
                                Output.Log($"AI请求失败，正在重试 ({args.AttemptNumber + 1}/{aiConfig.RetryCount})...", 2, ThisProgramName);
                                return default;
                            }
                        })
                        .Build();

                    response = await retryPipeline.ExecuteAsync(async token =>
                    {
                        return await httpClient.PostAsync(aiConfig.ApiEndpoint, content, token);
                    });
                }
                else
                {
                    response = await httpClient.PostAsync(aiConfig.ApiEndpoint, content);
                }

                if (!response.IsSuccessStatusCode)
                {
                    string errorBody = await response.Content.ReadAsStringAsync();
                    Output.Log($"AI请求失败 (HTTP {(int)response.StatusCode}): {errorBody}", 3, ThisProgramName);
                    return null;
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                var jsonResponse = Newtonsoft.Json.Linq.JObject.Parse(responseBody);

                string? aiMessage = jsonResponse["choices"]?[0]?["message"]?["content"]?.ToString();

                if (string.IsNullOrWhiteSpace(aiMessage))
                {
                    Output.Log("AI返回了空内容。", 2, ThisProgramName);
                    return null;
                }

                return aiMessage;
            }
            catch (TaskCanceledException)
            {
                Output.Log("AI请求超时。", 3, ThisProgramName);
                return null;
            }
            catch (Exception ex)
            {
                Output.Log($"AI分析出错: {ex.Message}", 3, ThisProgramName);
                return null;
            }
        }

        public static void Auto()
        {
            Output.Log("用法: .auto on | .auto off | .auto list", 1, ThisProgramName);
        }

        private static CancellationTokenSource? _tipsCts;
        private static volatile bool _tipsRunning = false;
        public static bool IsTipsRunning => _tipsRunning;
        private static int _tipsLastIndex = 0;

        #region 自动备份

        private static System.Threading.Timer? _backupTimer;
        private static volatile bool _backupRunning = false;
        public static bool IsBackupRunning => _backupRunning;

        public static void StartAutoBackup()
        {
            if (_backupRunning) return;
            if (!Config.App.AutoBackupEnabled) return;

            _backupRunning = true;
            int intervalMs = Config.App.AutoBackupIntervalMinutes * 60 * 1000;
            if (intervalMs < 60000) intervalMs = 60000;

            _backupTimer = new System.Threading.Timer(OnBackupTick, null, intervalMs, intervalMs);
            Output.Log($"自动备份已启动，间隔 {Config.App.AutoBackupIntervalMinutes} 分钟", 1, ThisProgramName);
        }

        public static void StopAutoBackup()
        {
            if (!_backupRunning) return;

            _backupTimer?.Dispose();
            _backupTimer = null;
            _backupRunning = false;
            Output.Log("自动备份已停止", 1, ThisProgramName);
        }

        private static void OnBackupTick(object? state)
        {
            try
            {
                PerformBackup();
            }
            catch (Exception ex)
            {
                Output.Log($"自动备份执行出错: {ex.Message}", 2, ThisProgramName);
            }
        }

        public static void PerformBackup()
        {
            var servers = Config.App.AutoBackupServers;
            if (servers == null || servers.Count == 0)
            {
                servers = Config.App.ServerList.Keys.ToList();
            }

            foreach (var serverKey in servers)
            {
                if (!Config.App.ServerList.TryGetValue(serverKey, out var entry))
                {
                    Output.Log($"跳过不存在的服务端标识: {serverKey}", 2, ThisProgramName);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(entry.WorkPath) || !Directory.Exists(entry.WorkPath))
                {
                    Output.Log($"跳过服务端 {serverKey}：工作目录未配置或不存在", 2, ThisProgramName);
                    continue;
                }

                BackupServer(serverKey, entry);
            }
        }

        private static void BackupServer(string serverKey, ServerEntry entry)
        {
            string backupBasePath = Config.App.AutoBackupPath;
            if (string.IsNullOrWhiteSpace(backupBasePath))
            {
                backupBasePath = Path.Combine(Config.DataPath, "backup");
            }

            if (!Directory.Exists(backupBasePath))
            {
                Directory.CreateDirectory(backupBasePath);
            }

            string pattern = Config.App.AutoBackupFileNamePattern;
            if (string.IsNullOrWhiteSpace(pattern))
                pattern = "{time}-{server}";

            string fileName = pattern
                .Replace("{time}", DateTime.Now.ToString("yyyyMMdd-HHmmss"))
                .Replace("{server}", serverKey);

            string zipPath = Path.Combine(backupBasePath, $"{fileName}.zip");
            string workPath = entry.WorkPath;

            if (string.IsNullOrWhiteSpace(workPath) || !Directory.Exists(workPath))
            {
                Output.Log($"备份服务端 {serverKey} 失败: 工作目录不存在", 2, ThisProgramName);
                return;
            }

            try
            {
                EventBus.Publish(new BackupStartEvent(serverKey));

                if (Config.App.AutoBackupLittleEnabled)
                {
                    BackupServerLittle(serverKey, workPath, backupBasePath, zipPath);
                }
                else
                {
                    BackupServerFull(serverKey, workPath, zipPath);
                }
            }
            catch (Exception ex)
            {
                Output.Log($"备份服务端 {serverKey} 失败: {ex.Message}", 2, ThisProgramName);
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            }
        }

        /// <summary>
        /// 完整备份（原有逻辑）
        /// </summary>
        private static void BackupServerFull(string serverKey, string workPath, string zipPath)
        {
            Output.Log($"正在完整备份服务端 {serverKey}...", 1, ThisProgramName);

            int skippedCount = 0;
            int addedCount = 0;

            using (var zipArchive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (var file in Directory.GetFiles(workPath, "*", SearchOption.AllDirectories))
                {
                    string relativePath = file.Substring(workPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    if (ShouldIgnoreBackupFile(relativePath)) continue;

                    try
                    {
                        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var zipEntry = zipArchive.CreateEntry(relativePath, System.IO.Compression.CompressionLevel.Fastest);
                        using var entryStream = zipEntry.Open();
                        fs.CopyTo(entryStream);
                        addedCount++;
                    }
                    catch (IOException)
                    {
                        skippedCount++;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        skippedCount++;
                    }
                }
            }

            long sizeBytes = new FileInfo(zipPath).Length;
            long sizeMB = sizeBytes / (1024 * 1024);
            string skipMsg = skippedCount > 0 ? $"（跳过 {skippedCount} 个被占用文件）" : "";
            Output.Log($"完整备份完成: {zipPath} ({sizeMB}MB) {addedCount} 个文件{skipMsg}", 1, ThisProgramName);
            EventBus.Publish(new BackupCompleteEvent(serverKey, zipPath, sizeBytes));
        }

        /// <summary>
        /// Little增量备份：首次完整备份，后续仅备份与完整备份有差异的文件
        /// </summary>
        private static void BackupServerLittle(string serverKey, string workPath, string backupBasePath, string zipPath)
        {
            string littleBaseDir = Path.Combine(backupBasePath, "little", serverKey);
            string fullBackupDir = Path.Combine(littleBaseDir, "full");

            // 检查是否存在完整备份
            bool hasFullBackup = Directory.Exists(fullBackupDir) && Directory.GetFiles(fullBackupDir, "*", SearchOption.AllDirectories).Length > 0;

            if (!hasFullBackup)
            {
                // 首次：执行完整备份（同时保存一份到little/full目录作为基准）
                Output.Log($"正在创建Little完整基准备份: {serverKey}...", 1, ThisProgramName);

                int addedCount = 0;
                int skippedCount = 0;

                // 创建完整基准目录
                Directory.CreateDirectory(fullBackupDir);

                using (var zipArchive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
                {
                    foreach (var file in Directory.GetFiles(workPath, "*", SearchOption.AllDirectories))
                    {
                        string relativePath = file.Substring(workPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                        if (ShouldIgnoreBackupFile(relativePath)) continue;

                        try
                        {
                            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                            // 写入zip
                            var zipEntry = zipArchive.CreateEntry(relativePath, System.IO.Compression.CompressionLevel.Fastest);
                            using var entryStream = zipEntry.Open();
                            fs.CopyTo(entryStream);
                            addedCount++;

                            // 复制到基准目录
                            string destFile = Path.Combine(fullBackupDir, relativePath);
                            string? destDir = Path.GetDirectoryName(destFile);
                            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                                Directory.CreateDirectory(destDir);

                            fs.Position = 0;
                            using var destFs = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None);
                            fs.CopyTo(destFs);
                        }
                        catch (IOException)
                        {
                            skippedCount++;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            skippedCount++;
                        }
                    }
                }

                long sizeBytes = new FileInfo(zipPath).Length;
                long sizeMB = sizeBytes / (1024 * 1024);
                string skipMsg = skippedCount > 0 ? $"（跳过 {skippedCount} 个被占用文件）" : "";
                Output.Log($"Little完整基准备份完成: {zipPath} ({sizeMB}MB) {addedCount} 个文件{skipMsg}", 1, ThisProgramName);
                EventBus.Publish(new BackupCompleteEvent(serverKey, zipPath, sizeBytes));
            }
            else
            {
                // 后续：差异备份，仅备份与基准不同的文件
                Output.Log($"正在创建Little差异备份: {serverKey}...", 1, ThisProgramName);

                int addedCount = 0;
                int skippedCount = 0;
                int unchangedCount = 0;

                using (var zipArchive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
                {
                    foreach (var file in Directory.GetFiles(workPath, "*", SearchOption.AllDirectories))
                    {
                        string relativePath = file.Substring(workPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                        if (ShouldIgnoreBackupFile(relativePath)) continue;

                        string baseFile = Path.Combine(fullBackupDir, relativePath);

                        try
                        {
                            if (!File.Exists(baseFile))
                            {
                                // 基准中不存在，新增文件
                                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                var zipEntry = zipArchive.CreateEntry(relativePath, System.IO.Compression.CompressionLevel.Fastest);
                                using var entryStream = zipEntry.Open();
                                fs.CopyTo(entryStream);
                                addedCount++;
                            }
                            else if (IsFileChanged(file, baseFile, relativePath))
                            {
                                // 文件有差异
                                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                var zipEntry = zipArchive.CreateEntry(relativePath, System.IO.Compression.CompressionLevel.Fastest);
                                using var entryStream = zipEntry.Open();
                                fs.CopyTo(entryStream);
                                addedCount++;
                            }
                            else
                            {
                                unchangedCount++;
                            }
                        }
                        catch (IOException)
                        {
                            skippedCount++;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            skippedCount++;
                        }
                    }
                }

                long sizeBytes = new FileInfo(zipPath).Length;
                long sizeMB = sizeBytes / (1024 * 1024);
                string skipMsg = skippedCount > 0 ? $"（跳过 {skippedCount} 个被占用文件）" : "";
                Output.Log($"Little差异备份完成: {zipPath} ({sizeMB}MB) 差异 {addedCount} 个文件，{unchangedCount} 个未变更{skipMsg}", 1, ThisProgramName);
                EventBus.Publish(new BackupCompleteEvent(serverKey, zipPath, sizeBytes));
            }
        }

        /// <summary>
        /// 判断文件是否与基准文件有差异
        /// </summary>
        private static bool IsFileChanged(string currentFile, string baseFile, string relativePath)
        {
            string ext = Path.GetExtension(relativePath).ToLowerInvariant();

            // 文本类文件：对比内容
            if (IsTextFile(ext))
            {
                try
                {
                    string currentContent = File.ReadAllText(currentFile);
                    string baseContent = File.ReadAllText(baseFile);
                    return currentContent != baseContent;
                }
                catch
                {
                    return true;
                }
            }

            // 二进制文件：对比大小
            try
            {
                var currentInfo = new FileInfo(currentFile);
                var baseInfo = new FileInfo(baseFile);

                if (currentInfo.Length != baseInfo.Length)
                    return true;

                // 大小相同且未开启强制备份二进制文件，视为未变更
                if (!Config.App.AutoBackupLittleForceBinary)
                    return false;

                // 强制备份模式：大小相同也对比内容（用于检测同大小但内容不同的二进制文件）
                return IsBinaryContentDifferent(currentFile, baseFile);
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// 判断文件扩展名是否为文本类型
        /// </summary>
        private static bool IsTextFile(string ext)
        {
            return ext == ".json" || ext == ".yml" || ext == ".yaml" ||
                   ext == ".properties" || ext == ".bat" || ext == ".sh" ||
                   ext == ".txt" || ext == ".cfg" || ext == ".conf" ||
                   ext == ".toml" || ext == ".ini" || ext == ".xml" ||
                   ext == ".md" || ext == ".log" || ext == ".csv";
        }

        /// <summary>
        /// 对比两个二进制文件内容是否不同
        /// </summary>
        private static bool IsBinaryContentDifferent(string file1, string file2)
        {
            try
            {
                const int bufferSize = 8192;
                using var fs1 = new FileStream(file1, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var fs2 = new FileStream(file2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                if (fs1.Length != fs2.Length) return true;

                var buffer1 = new byte[bufferSize];
                var buffer2 = new byte[bufferSize];

                while (true)
                {
                    int read1 = fs1.Read(buffer1, 0, bufferSize);
                    int read2 = fs2.Read(buffer2, 0, bufferSize);

                    if (read1 != read2) return true;
                    if (read1 == 0) return false;

                    for (int i = 0; i < read1; i++)
                    {
                        if (buffer1[i] != buffer2[i]) return true;
                    }
                }
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// 判断文件是否应被忽略备份
        /// </summary>
        private static bool ShouldIgnoreBackupFile(string relativePath)
        {
            // 检查忽略路径（匹配任意层级的目录名，包括其所有子目录和文件）
            var ignorePaths = Config.App.AutoBackupIgnorePaths;
            if (ignorePaths != null && ignorePaths.Count > 0)
            {
                string normalizedPath = relativePath.Replace('\\', '/');
                string[] pathSegments = normalizedPath.Split('/');
                foreach (var ignorePath in ignorePaths)
                {
                    if (string.IsNullOrWhiteSpace(ignorePath)) continue;
                    string normalizedIgnore = ignorePath.Replace('\\', '/').Trim('/');
                    // 检查路径中任意一级目录名是否匹配
                    foreach (var segment in pathSegments)
                    {
                        if (string.Equals(segment, normalizedIgnore, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }

            // 检查忽略正则
            var ignorePatterns = Config.App.AutoBackupIgnorePatterns;
            if (ignorePatterns != null && ignorePatterns.Count > 0)
            {
                string fileName = Path.GetFileName(relativePath);
                foreach (var pattern in ignorePatterns)
                {
                    if (string.IsNullOrWhiteSpace(pattern)) continue;
                    try
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(fileName, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch { }
                }
            }

            return false;
        }

        /// <summary>
        /// 服务端启动时触发Little首次完整备份（如果尚无基准）
        /// </summary>
        public static void TriggerLittleFullBackupIfNeeded()
        {
            if (!Config.App.AutoBackupEnabled || !Config.App.AutoBackupLittleEnabled)
                return;

            var currentKey = Config.App.CurrentServer;
            if (!Config.App.ServerList.TryGetValue(currentKey, out var entry))
                return;

            string backupBasePath = Config.App.AutoBackupPath;
            if (string.IsNullOrWhiteSpace(backupBasePath))
                backupBasePath = Path.Combine(Config.DataPath, "backup");

            string fullBackupDir = Path.Combine(backupBasePath, "little", currentKey, "full");
            bool hasFullBackup = Directory.Exists(fullBackupDir) && Directory.GetFiles(fullBackupDir, "*", SearchOption.AllDirectories).Length > 0;

            if (!hasFullBackup)
            {
                Output.Log("检测到Little备份无完整基准，正在创建首次完整备份...", 1, ThisProgramName);
                BackupServer(currentKey, entry);
            }
        }

        public static void BackupCurrentServer()
        {
            var currentKey = Config.App.CurrentServer;
            if (!Config.App.ServerList.TryGetValue(currentKey, out var entry))
            {
                Output.Log("当前服务端配置无效。", 2, ThisProgramName);
                return;
            }

            if (string.IsNullOrWhiteSpace(entry.WorkPath) || !Directory.Exists(entry.WorkPath))
            {
                Output.Log("当前服务端工作目录未配置或不存在。", 2, ThisProgramName);
                return;
            }

            BackupServer(currentKey, entry);
        }

        #endregion

        public static void StartAutoTips()
        {
            if (_tipsRunning) return;

            if (!Config.App.EnableAutoTips)
                return;

            _tipsRunning = true;
            _tipsCts = new CancellationTokenSource();
            var token = _tipsCts.Token;

            _ = Task.Run(() => MonitorTips(token), token);
            Output.Log("自动提示已启动", 1, "Tips");
        }

        public static void StopAutoTips()
        {
            if (!_tipsRunning) return;

            _tipsCts?.Cancel();
            _tipsRunning = false;
            Output.Log("自动提示已停止", 1, "Tips");
        }

        private static void MonitorTips(CancellationToken token)
        {
            var compiledTips = new List<(Regex Regex, string Tips)>();

            foreach (var tip in ContentManager.GetAllTips())
            {
                try
                {
                    var regex = new Regex(tip.Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                    compiledTips.Add((regex, tip.Tips));
                }
                catch (Exception ex)
                {
                    Output.Log($"提示正则无效: {ex.Message}", 2, "Tips");
                }
            }

            var donePatterns = new List<Regex>();
            foreach (var pattern in Config.App.DonePatterns)
            {
                try
                {
                    donePatterns.Add(new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase));
                }
                catch (Exception ex)
                {
                    Output.Log($"Done正则无效: {ex.Message}", 2, "Tips");
                }
            }

            if (compiledTips.Count == 0 && donePatterns.Count == 0)
            {
                _tipsRunning = false;
                return;
            }

            Output.Log($"已加载 {compiledTips.Count} 条自动提示规则, {donePatterns.Count} 条Done消息规则", 1, "Tips");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    List<string> newLines = new List<string>();
                    int currentCount;

                    lock (Analyzer._logBufferLock)
                    {
                        currentCount = Analyzer._logBuffer.Count;
                        if (_tipsLastIndex > currentCount)
                            _tipsLastIndex = 0;

                        for (int i = _tipsLastIndex; i < currentCount; i++)
                        {
                            newLines.Add(Analyzer._logBuffer[i]);
                        }
                        _tipsLastIndex = currentCount;
                    }

                    foreach (var line in newLines)
                    {
                        // Done消息检测
                        if (donePatterns.Count > 0)
                        {
                            foreach (var doneRegex in donePatterns)
                            {
                                if (doneRegex.IsMatch(line))
                                {
                                    Output.Log("[green][[Done]][/] MC服务端已启动完成!", 1, "Tips");
                                    EventBus.Publish(new ServerDoneEvent(Config.App.CurrentServer, line));
                                    break;
                                }
                            }
                        }

                        // 常规Tips检测
                        foreach (var (regex, tips) in compiledTips)
                        {
                            if (regex.IsMatch(line))
                            {
                                Output.Log("[yellow][[Tips]][/]", 1, "Tips");
                                Output.Log($"  {tips}", 1, "Tips");
                                break;
                            }
                        }
                    }

                    Thread.Sleep(1000);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }

            _tipsRunning = false;
        }

        private enum ServerApi { Vanilla, VanillaSnapshot, PaperMC, Leaf, Purpur, Fabric, Manual }

        private class ServerTypeInfo
        {
            public string Name { get; }
            public ServerApi Api { get; }
            public string DefaultFileName { get; }
            public string? ProjectId { get; }
            public string? Website { get; }

            public ServerTypeInfo(string name, ServerApi api, string defaultFileName, string? projectId = null, string? website = null)
            {
                Name = name;
                Api = api;
                DefaultFileName = defaultFileName;
                ProjectId = projectId;
                Website = website;
            }
        }

        #region AI自动化管理 - 内置工具集

        /// <summary>
        /// AI内置工具集 - 实现MC服务端自动化管理工具
        /// </summary>
        internal static class AiTools
        {
            private const string ToolsName = "AiTools";

            /// <summary>所有内置工具名称列表</summary>
            public static readonly string[] BuiltInTools = new[]
            {
                "read_file",
                "get_server_plugin_list",
                "get_server_log",
                "run_script",
                "modify_file",
                "toggle_plugin",
                "run_command",
                "restart_server"
            };

            private static void Log(string msg, int level = 1)
            {
                Output.Log(msg, level, ToolsName);
            }

            /// <summary>
            /// 检查工具是否被允许调用
            /// </summary>
            public static bool IsToolAllowed(string toolName, AiToolsConfig toolsConfig, List<string>? taskAllowedTools = null)
            {
                if (toolsConfig == null || !toolsConfig.Enabled) return false;

                // deny 优先
                if (toolsConfig.Deny != null && toolsConfig.Deny.Contains(toolName))
                    return false;

                // 任务级别限制
                if (taskAllowedTools != null && taskAllowedTools.Count > 0)
                {
                    if (!taskAllowedTools.Contains(toolName))
                        return false;
                }

                // allow 为空则允许所有未在 deny 中的
                if (toolsConfig.Allow == null || toolsConfig.Allow.Count == 0)
                    return true;

                return toolsConfig.Allow.Contains(toolName);
            }

            /// <summary>
            /// 执行工具调用
            /// </summary>
            public static async Task<string> ExecuteToolAsync(string toolName, string argsJson)
            {
                try
                {
                    JObject args = string.IsNullOrWhiteSpace(argsJson) ? new JObject() : (JObject.Parse(argsJson));

                    string result = toolName switch
                    {
                        "read_file" => Tool_ReadFile(args),
                        "get_server_plugin_list" => Tool_GetServerPluginList(args),
                        "get_server_log" => Tool_GetServerLog(args),
                        "run_script" => Tool_RunScript(args),
                        "modify_file" => Tool_ModifyFile(args),
                        "toggle_plugin" => Tool_TogglePlugin(args),
                        "run_command" => Tool_RunCommand(args),
                        "restart_server" => await Tool_RestartServerAsync(args),
                        _ => $"[错误] 未知工具: {toolName}"
                    };

                    return result ?? "[工具执行完毕，无返回内容]";
                }
                catch (JsonException je)
                {
                    return $"[参数解析失败] {je.Message}，原始参数: {argsJson}";
                }
                catch (Exception ex)
                {
                    return $"[工具执行异常] {ex.Message}";
                }
            }

            /// <summary>
            /// 获取工具描述信息(供AI提示词使用)
            /// </summary>
            public static string GetToolsDescription(AiToolsConfig toolsConfig)
            {
                var sb = new StringBuilder();
                sb.AppendLine("可用工具列表(通过 [rt:tools\"(工具名{参数JSON})\"] 调用):");

                if (toolsConfig?.ToolsPrompt != null)
                {
                    foreach (var kv in toolsConfig.ToolsPrompt)
                    {
                        sb.AppendLine($"- {kv.Key}: {kv.Value}");
                    }
                }
                else
                {
                    foreach (var t in BuiltInTools)
                        sb.AppendLine($"- {t}");
                }

                return sb.ToString();
            }

            #region 工具实现

            /// <summary>读取MC服务端目录中文件内容</summary>
            private static string Tool_ReadFile(JObject args)
            {
                string? relPath = args["path"]?.ToString();
                if (string.IsNullOrWhiteSpace(relPath))
                    return "[错误] 缺少参数: path";

                string workPath = GetServerWorkPath();
                if (string.IsNullOrEmpty(workPath))
                    return "[错误] 未配置MC服务端工作目录";

                // 防止路径穿越
                string fullPath = Path.GetFullPath(Path.Combine(workPath, relPath));
                string rootPath = Path.GetFullPath(workPath);
                if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                    return "[错误] 禁止访问工作目录外的文件";

                if (!File.Exists(fullPath))
                    return $"[错误] 文件不存在: {relPath}";

                var info = new FileInfo(fullPath);
                // 限制读取大小(避免超大文件)
                long maxSize = 512 * 1024; // 512KB
                if (info.Length > maxSize)
                    return $"[错误] 文件过大({info.Length} 字节)，最大支持 {maxSize} 字节";

                try
                {
                    string content = File.ReadAllText(fullPath, Encoding.GetEncoding(0));
                    Log($"读取文件: {relPath} ({content.Length} 字符)", 1);
                    return content;
                }
                catch (Exception ex)
                {
                    return $"[读取失败] {ex.Message}";
                }
            }

            /// <summary>获取MC服务端插件列表</summary>
            private static string Tool_GetServerPluginList(JObject args)
            {
                string workPath = GetServerWorkPath();
                if (string.IsNullOrEmpty(workPath))
                    return "[错误] 未配置MC服务端工作目录";

                string pluginsDir = Path.Combine(workPath, "plugins");
                if (!Directory.Exists(pluginsDir))
                    return "[信息] plugins 目录不存在";

                var result = new JArray();
                try
                {
                    var files = new DirectoryInfo(pluginsDir)
                        .GetFiles("*.jar")
                        .Concat(new DirectoryInfo(pluginsDir).GetFiles("*.disjar"))
                        .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    foreach (var file in files)
                    {
                        bool isDisabled = file.Extension.Equals(".disjar", StringComparison.OrdinalIgnoreCase);
                        result.Add(new JObject
                        {
                            ["file"] = file.Name,
                            ["enabled"] = !isDisabled,
                            ["size"] = file.Length
                        });
                    }

                    Log($"获取插件列表: 共 {result.Count} 个插件", 1);
                    return result.ToString(Formatting.Indented);
                }
                catch (Exception ex)
                {
                    return $"[获取失败] {ex.Message}";
                }
            }

            /// <summary>获取MC服务端日志</summary>
            private static string Tool_GetServerLog(JObject args)
            {
                int lines = args["lines"]?.ToObject<int>() ?? 100;
                if (lines <= 0) lines = 100;
                if (lines > 2000) lines = 2000;

                List<string> logLines = new List<string>();

                // 优先使用内存缓冲区
                lock (Analyzer._logBufferLock)
                {
                    if (Analyzer._logBuffer.Count > 0)
                    {
                        int start = Math.Max(0, Analyzer._logBuffer.Count - lines);
                        for (int i = start; i < Analyzer._logBuffer.Count; i++)
                            logLines.Add(Analyzer._logBuffer[i]);
                    }
                }

                // 缓冲区为空则尝试从日志文件读取
                if (logLines.Count == 0)
                {
                    string workPath = GetServerWorkPath();
                    if (!string.IsNullOrEmpty(workPath))
                    {
                        string logFile = Path.Combine(workPath, "logs", "latest.log");
                        if (File.Exists(logFile))
                        {
                            try
                            {
                                var allLines = File.ReadAllLines(logFile, Encoding.GetEncoding(0));
                                int start = Math.Max(0, allLines.Length - lines);
                                for (int i = start; i < allLines.Length; i++)
                                    logLines.Add(allLines[i]);
                            }
                            catch (Exception ex)
                            {
                                return $"[读取日志文件失败] {ex.Message}";
                            }
                        }
                    }
                }

                if (logLines.Count == 0)
                    return "[信息] 暂无日志数据(服务端可能未启动或未连接)";

                Log($"获取日志: 最近 {logLines.Count} 行", 1);
                return string.Join("\n", logLines);
            }

            /// <summary>运行Scripts中已配置的脚本</summary>
            private static string Tool_RunScript(JObject args)
            {
                string? scriptName = args["name"]?.ToString();
                string? input = args["input"]?.ToString();

                if (string.IsNullOrWhiteSpace(scriptName))
                    return "[错误] 缺少参数: name";

                try
                {
                    var settings = Scripts.GetSettings();
                    if (settings.Scripts.TryGetValue(scriptName, out var item))
                    {
                        if (!string.IsNullOrWhiteSpace(input))
                            item.Input = input;

                        Scripts.ExecuteScript(scriptName, item);
                        Log($"运行脚本: {scriptName}", 1);
                        return $"[成功] 脚本 {scriptName} 已触发执行";
                    }
                    return $"[错误] 未找到脚本: {scriptName}";
                }
                catch (Exception ex)
                {
                    return $"[脚本执行失败] {ex.Message}";
                }
            }

            /// <summary>修改MC服务端目录中文件内容</summary>
            private static string Tool_ModifyFile(JObject args)
            {
                string? relPath = args["path"]?.ToString();
                string? content = args["content"]?.ToString();

                if (string.IsNullOrWhiteSpace(relPath))
                    return "[错误] 缺少参数: path";
                if (content == null)
                    return "[错误] 缺少参数: content";

                string workPath = GetServerWorkPath();
                if (string.IsNullOrEmpty(workPath))
                    return "[错误] 未配置MC服务端工作目录";

                string fullPath = Path.GetFullPath(Path.Combine(workPath, relPath));
                string rootPath = Path.GetFullPath(workPath);
                if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                    return "[错误] 禁止修改工作目录外的文件";

                // 防止修改核心配置文件
                string fileName = Path.GetFileName(fullPath).ToLowerInvariant();
                string[] protectedFiles = { "server.properties", "bukkit.yml", "spigot.yml", "paper.yml", "eula.txt" };
                if (protectedFiles.Contains(fileName))
                    return $"[错误] 受保护的核心文件 {fileName} 禁止修改";

                try
                {
                    string? dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    File.WriteAllText(fullPath, content, Encoding.GetEncoding(0));
                    Log($"修改文件: {relPath} ({content.Length} 字符)", 1);
                    return $"[成功] 已修改文件: {relPath}";
                }
                catch (Exception ex)
                {
                    return $"[修改失败] {ex.Message}";
                }
            }

            /// <summary>启用或禁用插件(jar&lt;-&gt;disjar)</summary>
            private static string Tool_TogglePlugin(JObject args)
            {
                string? pluginName = args["plugin"]?.ToString();
                bool disable = args["disable"]?.ToObject<bool>() ?? true;

                if (string.IsNullOrWhiteSpace(pluginName))
                    return "[错误] 缺少参数: plugin";

                string workPath = GetServerWorkPath();
                if (string.IsNullOrEmpty(workPath))
                    return "[错误] 未配置MC服务端工作目录";

                string pluginsDir = Path.Combine(workPath, "plugins");
                if (!Directory.Exists(pluginsDir))
                    return "[错误] plugins 目录不存在";

                // 规范化文件名
                string fileName = pluginName;
                if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                    !fileName.EndsWith(".disjar", StringComparison.OrdinalIgnoreCase))
                {
                    fileName += ".jar";
                }

                string sourcePath = Path.Combine(pluginsDir, fileName);
                string targetName;
                string targetPath;

                if (disable)
                {
                    // 将 .jar 改为 .disjar
                    if (fileName.EndsWith(".disjar", StringComparison.OrdinalIgnoreCase))
                        return $"[信息] 插件 {fileName} 已是禁用状态";

                    targetName = Path.GetFileNameWithoutExtension(fileName) + ".disjar";
                    targetPath = Path.Combine(pluginsDir, targetName);

                    if (!File.Exists(sourcePath))
                        return $"[错误] 插件文件不存在: {fileName}";

                    try
                    {
                        File.Move(sourcePath, targetPath);
                        Log($"禁用插件: {fileName} -> {targetName}", 1);
                        return $"[成功] 已禁用插件: {targetName}";
                    }
                    catch (Exception ex)
                    {
                        return $"[禁用失败] {ex.Message}";
                    }
                }
                else
                {
                    // 将 .disjar 改为 .jar
                    if (fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                        return $"[信息] 插件 {fileName} 已是启用状态";

                    // 确保 .disjar 后缀
                    if (!fileName.EndsWith(".disjar", StringComparison.OrdinalIgnoreCase))
                        fileName += ".disjar";

                    sourcePath = Path.Combine(pluginsDir, fileName);
                    targetName = Path.GetFileNameWithoutExtension(fileName) + ".jar";
                    targetPath = Path.Combine(pluginsDir, targetName);

                    if (!File.Exists(sourcePath))
                        return $"[错误] 插件文件不存在: {fileName}";

                    try
                    {
                        File.Move(sourcePath, targetPath);
                        Log($"启用插件: {fileName} -> {targetName}", 1);
                        return $"[成功] 已启用插件: {targetName} (需重启服务端生效)";
                    }
                    catch (Exception ex)
                    {
                        return $"[启用失败] {ex.Message}";
                    }
                }
            }

            /// <summary>运行程序命令或向MC服务器发送命令(禁止/op)</summary>
            private static string Tool_RunCommand(JObject args)
            {
                string? command = args["command"]?.ToString();
                if (string.IsNullOrWhiteSpace(command))
                    return "[错误] 缺少参数: command";

                // 安全检查: 禁止 /op 命令
                string trimmedCmd = command.Trim();
                if (IsForbiddenCommand(trimmedCmd))
                    return $"[错误] 禁止执行的命令: {trimmedCmd}";

                // 判断是发送到MC服务器还是运行系统命令
                // 以 / 开头或匹配MC命令特征则发送到服务器
                bool isMcCommand = trimmedCmd.StartsWith("/") || IsMcServerCommand(trimmedCmd);

                if (isMcCommand)
                {
                    string mcCmd = trimmedCmd.TrimStart('/');
                    try
                    {
                        Analyzer.SendCommand(mcCmd);
                        Log($"向服务器发送命令: {mcCmd}", 1);
                        return $"[成功] 已发送命令到服务器: {mcCmd}";
                    }
                    catch (Exception ex)
                    {
                        return $"[发送命令失败] {ex.Message}";
                    }
                }
                else
                {
                    // 系统命令执行(受限)
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/sh",
                            Arguments = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                                ? $"/c {trimmedCmd}"
                                : $"-c \"{trimmedCmd.Replace("\"", "\\\"")}\"",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true,
                            StandardOutputEncoding = Encoding.GetEncoding(0),
                            StandardErrorEncoding = Encoding.GetEncoding(0)
                        };

                        using var proc = Process.Start(psi);
                        if (proc == null)
                            return "[错误] 无法启动进程";

                        if (!proc.WaitForExit(30000))
                        {
                            try { proc.Kill(); } catch { }
                            return "[错误] 命令执行超时(30秒)";
                        }

                        string stdout = proc.StandardOutput.ReadToEnd();
                        string stderr = proc.StandardError.ReadToEnd();

                        var sb = new StringBuilder();
                        if (!string.IsNullOrWhiteSpace(stdout))
                            sb.Append("STDOUT:\n").Append(stdout).Append("\n");
                        if (!string.IsNullOrWhiteSpace(stderr))
                            sb.Append("STDERR:\n").Append(stderr);

                        Log($"运行系统命令: {trimmedCmd} (退出码: {proc.ExitCode})", 1);
                        return sb.Length > 0 ? sb.ToString() : $"[成功] 命令执行完毕 (退出码: {proc.ExitCode})";
                    }
                    catch (Exception ex)
                    {
                        return $"[系统命令执行失败] {ex.Message}";
                    }
                }
            }

            /// <summary>重启MC服务端</summary>
            private static async Task<string> Tool_RestartServerAsync(JObject args)
            {
                try
                {
                    if (!Analyzer.IsRunModeActive)
                        return "[错误] MC服务端未运行(仅支持Run/RR/RM模式的重启)";

                    Log("AI 触发重启服务器...", 2);

                    Analyzer.StopServer();
                    await Task.Delay(2000);
                    Analyzer.StartServer();

                    return "[成功] 已触发服务器重启";
                }
                catch (Exception ex)
                {
                    return $"[重启失败] {ex.Message}";
                }
            }

            #endregion

            #region 辅助方法

            private static string GetServerWorkPath()
            {
                string? workPath = Config.CurrentServer.WorkPath;
                if (!string.IsNullOrWhiteSpace(workPath) && Directory.Exists(workPath))
                    return workPath;
                return "";
            }

            /// <summary>判断是否为禁止的命令(如 /op)</summary>
            private static bool IsForbiddenCommand(string command)
            {
                string lower = command.ToLowerInvariant().TrimStart('/');
                string[] forbidden = { "op ", "op:", "deop ", "deop:", "ban ", "pardon ", "whitelist add", "whitelist remove" };

                foreach (var f in forbidden)
                {
                    if (lower.StartsWith(f))
                        return true;
                }

                // 严格匹配 /op
                if (lower == "op" || lower.StartsWith("op "))
                    return true;

                return false;
            }

            /// <summary>判断是否为MC服务器命令(而非系统命令)</summary>
            private static bool IsMcServerCommand(string command)
            {
                string lower = command.ToLowerInvariant().TrimStart('/');
                string[] mcCommands = {
                    "say", "list", "stop", "save-all", "save-on", "save-off", "save-flush",
                    "whitelist", "ban", "banlist", "pardon", "kick", "tp", "teleport",
                    "gamemode", "gamerule", "give", "clear", "effect", "enchant",
                    "setblock", "fill", "clone", "execute", "function", "particle",
                    "playsound", "title", "tellraw", "bossbar", "scoreboard", "tag",
                    "team", "advancement", "recipe", "xp", "experience", "spawnpoint",
                    "setworldspawn", "weather", "time", "difficulty", "defaultgamemode",
                    "seed", "reload", "perms", "permission", "plugins", "version", "tps",
                    "gc", "restart", "timings"
                };

                foreach (var mc in mcCommands)
                {
                    if (lower == mc || lower.StartsWith(mc + " "))
                        return true;
                }

                return false;
            }

            /// <summary>获取主机CPU使用率(%)</summary>
            public static double GetCpuUsage()
            {
                try
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        using var searcher = new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_Processor");
                        foreach (var obj in searcher.Get())
                        {
                            return Convert.ToDouble(obj["LoadPercentage"]);
                        }
                    }
                }
                catch { }
                return -1;
            }

            /// <summary>获取主机内存使用情况(已用MB, 总MB)</summary>
            public static (double usedMb, double totalMb) GetMemoryUsage()
            {
                try
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
                        foreach (var obj in searcher.Get())
                        {
                            double totalKb = Convert.ToDouble(obj["TotalVisibleMemorySize"]);
                            double freeKb = Convert.ToDouble(obj["FreePhysicalMemory"]);
                            double usedKb = totalKb - freeKb;
                            return (usedKb / 1024.0, totalKb / 1024.0);
                        }
                    }
                }
                catch { }
                return (-1, -1);
            }

            /// <summary>获取MC服务器TPS(通过发送 /tps 命令并解析, 此处返回占位说明)</summary>
            public static string GetServerTpsInfo()
            {
                try
                {
                    if (!Analyzer.IsRunModeActive && !Analyzer.IsAttached)
                        return "[服务器未运行，无法获取TPS]";

                    // 尝试从最近日志中查找 TPS 信息
                    List<string> recentLines = new List<string>();
                    lock (Analyzer._logBufferLock)
                    {
                        int start = Math.Max(0, Analyzer._logBuffer.Count - 100);
                        for (int i = start; i < Analyzer._logBuffer.Count; i++)
                            recentLines.Add(Analyzer._logBuffer[i]);
                    }

                    // 查找 TPS 相关行
                    var tpsPattern = new Regex(@"tps[^\d]*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                    foreach (var line in recentLines)
                    {
                        var match = tpsPattern.Match(line);
                        if (match.Success)
                            return $"TPS: {match.Groups[1].Value} (来源: 日志)";
                    }

                    // 没找到则发送命令请求
                    Analyzer.SendCommand("tps");
                    return "[已发送 /tps 命令到服务器，请稍后查看日志获取TPS数据]";
                }
                catch (Exception ex)
                {
                    return $"[获取TPS失败] {ex.Message}";
                }
            }

            #endregion
        }

        #endregion

        #region AI自动化管理 - 运行器

        /// <summary>
        /// AI自动化管理运行器 - 实现MC服务端无人自动化管理
        /// </summary>
        internal static class AiAutoRunner
        {
            private const string RunnerName = "AiAuto";

            /// <summary>工具调用格式正则: [rt:tools"(工具名{参数JSON})"]</summary>
            private static readonly Regex ToolCallPattern = new Regex(
                @"\[rt:tools""?\(([^{}\s]+)\s*(\{[^}]*\})?\)""?\]",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            /// <summary>任务上下文缓存(键:任务类别, 值:消息列表)</summary>
            private static readonly Dictionary<string, List<ChatMessage>> _contextCache = new();
            private static readonly object _cacheLock = new();

            private static readonly HttpClient _httpClient = new HttpClient();
            private static bool _isRunning = false;
            private static readonly object _runLock = new();

            /// <summary>是否正在运行</summary>
            public static bool IsRunning => _isRunning;

            /// <summary>当前会话状态(用于.ui显示)</summary>
            public static readonly Dictionary<string, DateTime> LastRunTime = new();
            public static readonly Dictionary<string, int> RunCount = new();

            /// <summary>上次检测到有玩家在线的时间(用于空闲时长计算)</summary>
            private static DateTime _lastPlayerOnlineTime = DateTime.Now;
            /// <summary>上次检测到服务器崩溃的时间</summary>
            private static DateTime? _lastCrashTime = null;

            private static void Log(string msg, int level = 1)
            {
                Output.Log(msg, level, RunnerName);
            }

            /// <summary>启动AI自动化</summary>
            public static void Start()
            {
                lock (_runLock)
                {
                    if (_isRunning)
                    {
                        Log("AI自动化管理已在运行中。", 2);
                        return;
                    }

                    var config = ContentManager.Ai.ServerAutoAi;
                    if (config == null)
                    {
                        Log("未配置 server_auto_ai，无法启动。", 3);
                        return;
                    }

                    // 确保MC服务端启动
                    if (Analyzer.NeedsRunServer && !Analyzer.IsRunModeActive)
                    {
                        // 校验服务器工作目录有效性
                        string? workPath = Config.CurrentServer.WorkPath;
                        if (string.IsNullOrWhiteSpace(workPath))
                        {
                            Log("未配置MC服务端工作目录(work_path)，无法启动AI自动化。", 3);
                            return;
                        }
                        if (!Directory.Exists(workPath))
                        {
                            Log($"MC服务端工作目录不存在: {workPath}，无法启动AI自动化。", 3);
                            return;
                        }
                        if (!HasServerCoreFile(workPath))
                        {
                            Log($"MC服务端工作目录中未发现服务端核心文件(*.jar): {workPath}，无法启动AI自动化。", 3);
                            return;
                        }

                        Log("MC服务端未启动，正在自动启动...", 1);
                        Analyzer.StartServer();
                        Thread.Sleep(3000);

                        // 校验服务端是否真正启动成功
                        if (!Analyzer.IsRunModeActive)
                        {
                            Log("MC服务端启动失败(可能Java路径错误、启动参数错误或核心文件损坏)，AI自动化管理未启动。", 3);
                            return;
                        }
                    }
                    else if (!Analyzer.NeedsRunServer && !Analyzer.IsAttached)
                    {
                        Log("当前模式为 Rcon，请先使用 .server get + .server connect 连接服务端后再次启动AI。", 2);
                        return;
                    }

                    _isRunning = true;

                    // 确保Hangfire调度器已启动(AI任务依赖Hangfire执行cron定时任务)
                    if (!Scheduler.IsRunning)
                    {
                        Log("正在启动Hangfire调度器...", 1);
                        Scheduler.Start();
                    }

                    RegisterAllTasks();
                    Log($"AI自动化管理已启动，共注册 {config.Tasks.Count} 个任务。", 1);
                }
            }

            /// <summary>检查工作目录中是否存在服务端核心文件(*.jar)</summary>
            private static bool HasServerCoreFile(string workPath)
            {
                try
                {
                    return Directory.GetFiles(workPath, "*.jar").Length > 0;
                }
                catch { return false; }
            }

            /// <summary>停止AI自动化</summary>
            public static void Stop()
            {
                lock (_runLock)
                {
                    if (!_isRunning) return;

                    foreach (var kv in config_TasksSnapshot())
                    {
                        try { RecurringJob.RemoveIfExists($"ai_{kv.Key}"); } catch { }
                    }

                    _isRunning = false;
                    Log("AI自动化管理已停止。", 1);
                }
            }

            /// <summary>重载任务配置</summary>
            public static void Reload()
            {
                if (!_isRunning)
                {
                    Start();
                    return;
                }

                Stop();
                Thread.Sleep(500);
                Start();
                Log("AI自动化管理已重载。", 1);
            }

            /// <summary>列出所有任务状态</summary>
            public static void ListTasks()
            {
                var config = ContentManager.Ai.ServerAutoAi;
                if (config?.Tasks == null || config.Tasks.Count == 0)
                {
                    Log("未配置任何AI任务。", 1);
                    return;
                }

                var table = new Table();
                table.Border(TableBorder.Rounded);
                table.Title = new TableTitle($"[cyan]AI自动化任务列表[/] {(_isRunning ? "[green]● 运行中[/]" : "[red]● 已停止[/]")}");
                table.AddColumn("标识");
                table.AddColumn("名称");
                table.AddColumn("状态");
                table.AddColumn("触发条件");
                table.AddColumn("间隔Cron");
                table.AddColumn("已执行");
                table.AddColumn("最后运行");

                foreach (var kv in config.Tasks)
                {
                    string status = kv.Value.Enabled ? "[green]启用[/]" : "[grey]禁用[/]";
                    string lastRun = LastRunTime.TryGetValue(kv.Key, out var t) ? t.ToString("MM-dd HH:mm:ss") : "-";
                    int count = RunCount.TryGetValue(kv.Key, out var c) ? c : 0;

                    table.AddRow(
                        Markup.Escape(kv.Key),
                        Markup.Escape(kv.Value.Name),
                        status,
                        Markup.Escape(kv.Value.Trigger),
                        Markup.Escape(kv.Value.Interval),
                        count.ToString(),
                        lastRun
                    );
                }

                AnsiConsole.Write(table);
            }

            /// <summary>手动触发指定任务(跳过trigger检查，强制执行)</summary>
            public static async Task<bool> TriggerTaskAsync(string taskKey)
            {
                var config = ContentManager.Ai.ServerAutoAi;
                if (config?.Tasks == null || !config.Tasks.TryGetValue(taskKey, out var task))
                {
                    Log($"未找到任务: {taskKey}", 2);
                    return false;
                }

                if (!task.Enabled)
                {
                    Log($"任务 {taskKey} 已禁用，无法触发。", 2);
                    return false;
                }

                Log($"手动触发任务: {taskKey}", 1);
                await ExecuteTaskAsync(taskKey, task, skipTrigger: true);
                return true;
            }

            /// <summary>注册所有任务到调度器</summary>
            private static void RegisterAllTasks()
            {
                var config = ContentManager.Ai.ServerAutoAi;
                if (config?.Tasks == null) return;

                foreach (var kv in config.Tasks)
                {
                    if (!kv.Value.Enabled) continue;

                    string jobKey = $"ai_{kv.Key}";
                    string cronExpr = NormalizeCron(kv.Value.Interval);

                    try
                    {
                        RecurringJob.AddOrUpdate(
                            jobKey,
                            () => HangfireExecute(kv.Key),
                            cronExpr);
                        Log($"任务 [{kv.Key}] 已注册: {kv.Value.Name} | Cron: {cronExpr}", 1);
                    }
                    catch (Exception ex)
                    {
                        Log($"任务 [{kv.Key}] 注册失败: {ex.Message}", 3);
                    }
                }
            }

            /// <summary>Hangfire执行入口</summary>
            public static async Task HangfireExecute(string taskKey)
            {
                var config = ContentManager.Ai.ServerAutoAi;
                if (config?.Tasks == null || !config.Tasks.TryGetValue(taskKey, out var task))
                    return;

                if (!_isRunning) return;
                if (!task.Enabled) return;

                try
                {
                    await ExecuteTaskAsync(taskKey, task);
                }
                catch (Exception ex)
                {
                    Log($"任务 [{taskKey}] 执行异常: {ex.Message}", 3);
                }
            }

            /// <summary>快照任务列表(供停止时使用)</summary>
            private static IEnumerable<KeyValuePair<string, AiTask>> config_TasksSnapshot()
            {
                var config = ContentManager.Ai.ServerAutoAi;
                if (config?.Tasks == null) return new List<KeyValuePair<string, AiTask>>();
                return config.Tasks.ToList();
            }

            /// <summary>
            /// 执行单个AI任务
            /// </summary>
            /// <param name="skipTrigger">是否跳过触发条件检查(手动/事件触发时为true)</param>
            private static async Task ExecuteTaskAsync(string taskKey, AiTask task, bool skipTrigger = false)
            {
                // 1. 检查触发条件(cron轮询时检查；手动/事件触发时跳过)
                if (!skipTrigger && !EvaluateTrigger(task.Trigger))
                {
                    Log($"任务 [{taskKey}] 触发条件不满足: {task.Trigger}", 1);
                    return;
                }

                // 2. 收集输入数据
                string inputData = CollectInput(task.Input, task.Limit);
                if (string.IsNullOrWhiteSpace(inputData))
                {
                    Log($"任务 [{taskKey}] 无可用的输入数据，跳过本次执行。", 1);
                    return;
                }

                // 3. 解析动作
                bool doAi = false;
                List<string> allowedTools = new List<string>();
                foreach (var action in task.Actions)
                {
                    string a = action.Trim().TrimStart('-').Trim();
                    if (string.IsNullOrEmpty(a)) continue;

                    if (a.Equals("ai", StringComparison.OrdinalIgnoreCase))
                    {
                        doAi = true;
                    }
                    else if (a.StartsWith("tools:", StringComparison.OrdinalIgnoreCase))
                    {
                        string tools = a.Substring("tools:".Length).Trim();
                        foreach (var t in tools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            allowedTools.Add(t);
                    }
                }

                if (!doAi)
                {
                    Log($"任务 [{taskKey}] 未配置 - ai 动作，跳过AI分析。", 1);
                    return;
                }

                // 4. 执行AI分析(含工具调用循环)
                Log($"任务 [{taskKey}] 开始AI分析...", 1);

                var config = ContentManager.Ai.ServerAutoAi;
                int maxRounds = config.MaxToolRounds > 0 ? config.MaxToolRounds : 5;
                string? finalResponse = await RunAiWithToolsAsync(taskKey, task, inputData, allowedTools, maxRounds);

                // 5. 更新统计
                lock (_cacheLock)
                {
                    LastRunTime[taskKey] = DateTime.Now;
                    if (!RunCount.ContainsKey(taskKey)) RunCount[taskKey] = 0;
                    RunCount[taskKey]++;
                }

                // 6. 保存回复
                if (!string.IsNullOrWhiteSpace(finalResponse) && config.SaveResponse)
                {
                    SaveAiContextResponse(taskKey, task.Name, inputData, finalResponse);
                }

                Log($"任务 [{taskKey}] AI分析完成。", 1);
            }

            /// <summary>
            /// 评估触发条件
            /// </summary>
            private static bool EvaluateTrigger(string trigger)
            {
                if (string.IsNullOrWhiteSpace(trigger)) return true;
                string t = trigger.Trim().ToLowerInvariant();

                if (t == "always" || t == "true" || t == "1") return true;
                if (t == "server_running") return Analyzer.IsRunModeActive || Analyzer.IsAttached;

                // TPS 条件: tps < 18, tps > 20
                var tpsMatch = Regex.Match(t, @"tps\s*(>=|<=|>|<|=|==)\s*(\d+(?:\.\d+)?)");
                if (tpsMatch.Success)
                {
                    double currentTps = TryGetCurrentTps();
                    if (currentTps < 0) return false; // 无法获取TPS, 不触发

                    double threshold = double.Parse(tpsMatch.Groups[2].Value);
                    string op = tpsMatch.Groups[1].Value;
                    return op switch
                    {
                        ">" => currentTps > threshold,
                        ">=" => currentTps >= threshold,
                        "<" => currentTps < threshold,
                        "<=" => currentTps <= threshold,
                        "=" or "==" => Math.Abs(currentTps - threshold) < 0.01,
                        _ => false
                    };
                }

                // CPU 条件: cpu > 80
                var cpuMatch = Regex.Match(t, @"cpu\s*(>=|<=|>|<|=|==)\s*(\d+(?:\.\d+)?)");
                if (cpuMatch.Success)
                {
                    double cpu = AiTools.GetCpuUsage();
                    if (cpu < 0) return false;
                    double threshold = double.Parse(cpuMatch.Groups[2].Value);
                    string op = cpuMatch.Groups[1].Value;
                    return op switch
                    {
                        ">" => cpu > threshold,
                        ">=" => cpu >= threshold,
                        "<" => cpu < threshold,
                        "<=" => cpu <= threshold,
                        "=" or "==" => Math.Abs(cpu - threshold) < 0.01,
                        _ => false
                    };
                }

                // 内存条件: memory > 80 (百分比)
                var memMatch = Regex.Match(t, @"memory\s*(>=|<=|>|<|=|==)\s*(\d+(?:\.\d+)?)");
                if (memMatch.Success)
                {
                    var (used, total) = AiTools.GetMemoryUsage();
                    if (total <= 0) return false;
                    double percent = (used / total) * 100;
                    double threshold = double.Parse(memMatch.Groups[2].Value);
                    string op = memMatch.Groups[1].Value;
                    return op switch
                    {
                        ">" => percent > threshold,
                        ">=" => percent >= threshold,
                        "<" => percent < threshold,
                        "<=" => percent <= threshold,
                        "=" or "==" => Math.Abs(percent - threshold) < 0.01,
                        _ => false
                    };
                }

                // 在线玩家数量条件: players == 0, players > 0, players < 5
                var playersMatch = Regex.Match(t, @"players\s*(>=|<=|>|<|=|==)\s*(\d+)");
                if (playersMatch.Success)
                {
                    int currentPlayers = GetOnlinePlayerCount();
                    if (currentPlayers > 0)
                        _lastPlayerOnlineTime = DateTime.Now;

                    int threshold = int.Parse(playersMatch.Groups[2].Value);
                    string op = playersMatch.Groups[1].Value;
                    return op switch
                    {
                        ">" => currentPlayers > threshold,
                        ">=" => currentPlayers >= threshold,
                        "<" => currentPlayers < threshold,
                        "<=" => currentPlayers <= threshold,
                        "=" or "==" => currentPlayers == threshold,
                        _ => false
                    };
                }

                // 空闲时长条件(分钟): idle_minutes > 360 (即6小时)
                var idleMatch = Regex.Match(t, @"idle_minutes\s*(>=|<=|>|<|=|==)\s*(\d+(?:\.\d+)?)");
                if (idleMatch.Success)
                {
                    if (GetOnlinePlayerCount() > 0)
                    {
                        _lastPlayerOnlineTime = DateTime.Now;
                        return false; // 当前有玩家，不满足空闲条件
                    }

                    double idleMinutes = (DateTime.Now - _lastPlayerOnlineTime).TotalMinutes;
                    double threshold = double.Parse(idleMatch.Groups[2].Value);
                    string op = idleMatch.Groups[1].Value;
                    return op switch
                    {
                        ">" => idleMinutes > threshold,
                        ">=" => idleMinutes >= threshold,
                        "<" => idleMinutes < threshold,
                        "<=" => idleMinutes <= threshold,
                        "=" or "==" => Math.Abs(idleMinutes - threshold) < 1,
                        _ => false
                    };
                }

                // 崩溃检测: crash_detected
                if (t == "crash_detected")
                {
                    bool crashed = DetectServerCrash();
                    if (crashed)
                    {
                        _lastCrashTime = DateTime.Now;
                        return true;
                    }
                    return false;
                }

                // 服务器已停止: server_stopped
                if (t == "server_stopped")
                {
                    return !Analyzer.IsRunModeActive && !Analyzer.IsAttached;
                }

                // 未知条件默认通过(避免阻塞)
                Log($"未知触发条件(默认通过): {trigger}", 1);
                return true;
            }

            /// <summary>获取当前在线玩家数(通过日志解析加入/离开事件估算)</summary>
            private static int GetOnlinePlayerCount()
            {
                try
                {
                    var recentLines = new List<string>();
                    lock (Analyzer._logBufferLock)
                    {
                        int start = Math.Max(0, Analyzer._logBuffer.Count - 500);
                        for (int i = start; i < Analyzer._logBuffer.Count; i++)
                            recentLines.Add(Analyzer._logBuffer[i]);
                    }

                    int count = 0;
                    for (int i = 0; i < recentLines.Count; i++)
                    {
                        string line = recentLines[i];
                        if (Regex.IsMatch(line, @"joined the game|logged in", RegexOptions.IgnoreCase))
                            count++;
                        else if (Regex.IsMatch(line, @"left the game|lost connection|disconnected", RegexOptions.IgnoreCase))
                            count--;
                    }
                    return Math.Max(0, count);
                }
                catch { return 0; }
            }

            /// <summary>检测服务器是否崩溃(cron兜底轮询，使用精准关键字单行命中判定)</summary>
            private static bool DetectServerCrash()
            {
                try
                {
                    var recentLines = new List<string>();
                    lock (Analyzer._logBufferLock)
                    {
                        int start = Math.Max(0, Analyzer._logBuffer.Count - 100);
                        for (int i = start; i < Analyzer._logBuffer.Count; i++)
                            recentLines.Add(Analyzer._logBuffer[i]);
                    }

                    if (recentLines.Count == 0) return false;

                    // 精准崩溃标志(与 Analyzer.ProcessCrashDetectionLine 保持一致)
                    foreach (var line in recentLines)
                    {
                        if (line.Contains("---- Minecraft Crash Report ----", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("This crash report has been saved to", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("Shutting down the server", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("Server thread/FATAL", StringComparison.OrdinalIgnoreCase) ||
                            (line.Contains("Server thread/ERROR", StringComparison.OrdinalIgnoreCase) &&
                             line.Contains("Crash", StringComparison.OrdinalIgnoreCase)))
                        {
                            return true;
                        }
                    }
                    return false;
                }
                catch { return false; }
            }

            /// <summary>获取最近的崩溃报告内容</summary>
            private static string GetRecentCrashInfo()
            {
                try
                {
                    string? workPath = Config.CurrentServer.WorkPath;
                    if (string.IsNullOrEmpty(workPath)) return "[未配置工作目录]";

                    string crashDir = Path.Combine(workPath, "crash-reports");
                    if (!Directory.Exists(crashDir)) return "[无崩溃报告目录]";

                    var latestCrash = new DirectoryInfo(crashDir)
                        .GetFiles("*.txt")
                        .OrderByDescending(f => f.LastWriteTime)
                        .FirstOrDefault();

                    if (latestCrash == null) return "[无崩溃报告文件]";

                    string content = File.ReadAllText(latestCrash.FullName, Encoding.GetEncoding(0));
                    if (content.Length > 4000)
                        content = content.Substring(0, 4000) + "\n...(内容已截断)";

                    return $"崩溃报告文件: {latestCrash.Name}\n生成时间: {latestCrash.LastWriteTime}\n\n{content}";
                }
                catch (Exception ex) { return $"[获取崩溃报告失败: {ex.Message}]"; }
            }

            /// <summary>尝试获取当前TPS</summary>
            private static double TryGetCurrentTps()
            {
                try
                {
                    List<string> recentLines = new List<string>();
                    lock (Analyzer._logBufferLock)
                    {
                        int start = Math.Max(0, Analyzer._logBuffer.Count - 50);
                        for (int i = start; i < Analyzer._logBuffer.Count; i++)
                            recentLines.Add(Analyzer._logBuffer[i]);
                    }

                    // 匹配各种TPS格式
                    var patterns = new[]
                    {
                        new Regex(@"tps[^\d]*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase),
                        new Regex(@"from\s+the\s+last\s+\w+\s*,\s*tps[^\d]*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase),
                        new Regex(@"""tps""\s*:\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)
                    };

                    for (int i = recentLines.Count - 1; i >= 0; i--)
                    {
                        foreach (var p in patterns)
                        {
                            var m = p.Match(recentLines[i]);
                            if (m.Success && double.TryParse(m.Groups[1].Value, out double tps))
                                return tps;
                        }
                    }
                }
                catch { }
                return -1;
            }

            /// <summary>
            /// 收集任务输入数据
            /// </summary>
            private static string CollectInput(List<string> inputs, int limit)
            {
                if (inputs == null || inputs.Count == 0) return "";

                var sb = new StringBuilder();
                int charLimit = limit * 4; // 1 token ≈ 4字符的粗略估算
                if (charLimit <= 0) charLimit = 16000;

                foreach (var input in inputs)
                {
                    string? content = null;
                    string label = input;

                    try
                    {
                        switch (input.ToLowerInvariant())
                        {
                            case "server_logs":
                                content = GetRecentServerLogs(200);
                                label = "服务器日志(最近200行)";
                                break;
                            case "app_logs":
                                content = GetRecentAppLogs(100);
                                label = "程序日志(最近100行)";
                                break;
                            case "server_tps":
                                content = AiTools.GetServerTpsInfo();
                                label = "服务器TPS";
                                break;
                            case "server_plugin":
                                content = GetPluginListSummary();
                                label = "服务器插件列表";
                                break;
                            case "host_cpu":
                                double cpu = AiTools.GetCpuUsage();
                                content = cpu < 0 ? "[无法获取CPU信息]" : $"CPU使用率: {cpu:F1}%";
                                label = "主机CPU";
                                break;
                            case "host_memory":
                                var (used, total) = AiTools.GetMemoryUsage();
                                content = total < 0 ? "[无法获取内存信息]" : $"内存: 已用 {used:F0}MB / 总 {total:F0}MB ({used / total * 100:F1}%)";
                                label = "主机内存";
                                break;
                            case "server_players":
                                int playerCount = GetOnlinePlayerCount();
                                content = $"当前在线玩家数: {playerCount}\n上次检测到有玩家的时间: {_lastPlayerOnlineTime:yyyy-MM-dd HH:mm:ss}\n空闲时长: {(DateTime.Now - _lastPlayerOnlineTime).TotalMinutes:F1} 分钟";
                                label = "服务器在线玩家";
                                break;
                            case "server_status":
                                var statusSb = new StringBuilder();
                                statusSb.AppendLine($"服务器运行状态: {(Analyzer.IsRunModeActive || Analyzer.IsAttached ? "运行中" : "已停止")}");
                                statusSb.AppendLine($"需要运行服务端模式: {Analyzer.NeedsRunServer}");
                                statusSb.AppendLine($"运行模式激活: {Analyzer.IsRunModeActive}");
                                statusSb.AppendLine($"已附加进程: {Analyzer.IsAttached}");
                                statusSb.AppendLine($"上次崩溃时间: {(_lastCrashTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "无")}");
                                content = statusSb.ToString();
                                label = "服务器状态";
                                break;
                            case "crash_report":
                                content = GetRecentCrashInfo();
                                label = "崩溃报告";
                                break;
                            default:
                                content = $"[未知输入源: {input}]";
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        content = $"[获取 {input} 失败: {ex.Message}]";
                    }

                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        // 截断超长内容
                        if (content.Length > charLimit / Math.Max(1, inputs.Count))
                            content = content.Substring(0, charLimit / Math.Max(1, inputs.Count)) + "\n...(内容已截断)";

                        sb.AppendLine($"=== {label} ===");
                        sb.AppendLine(content);
                        sb.AppendLine();
                    }
                }

                return sb.ToString();
            }

            /// <summary>获取最近的MC服务端日志</summary>
            private static string GetRecentServerLogs(int lines)
            {
                var logLines = new List<string>();
                lock (Analyzer._logBufferLock)
                {
                    if (Analyzer._logBuffer.Count > 0)
                    {
                        int start = Math.Max(0, Analyzer._logBuffer.Count - lines);
                        for (int i = start; i < Analyzer._logBuffer.Count; i++)
                            logLines.Add(Analyzer._logBuffer[i]);
                    }
                }

                if (logLines.Count == 0)
                {
                    string? workPath = Config.CurrentServer.WorkPath;
                    if (!string.IsNullOrEmpty(workPath))
                    {
                        string logFile = Path.Combine(workPath, "logs", "latest.log");
                        if (File.Exists(logFile))
                        {
                            var allLines = File.ReadAllLines(logFile, Encoding.GetEncoding(0));
                            int start = Math.Max(0, allLines.Length - lines);
                            for (int i = start; i < allLines.Length; i++)
                                logLines.Add(allLines[i]);
                        }
                    }
                }

                return logLines.Count == 0 ? "[暂无服务端日志]" : string.Join("\n", logLines);
            }

            /// <summary>获取程序自身日志</summary>
            private static string GetRecentAppLogs(int lines)
            {
                try
                {
                    string logDir = Config.LogsPath;
                    if (!Directory.Exists(logDir)) return "[无程序日志目录]";

                    var logFile = new DirectoryInfo(logDir)
                        .GetFiles("*.log")
                        .OrderByDescending(f => f.LastWriteTime)
                        .FirstOrDefault();

                    if (logFile == null) return "[无程序日志文件]";

                    var allLines = File.ReadAllLines(logFile.FullName, Encoding.UTF8);
                    int start = Math.Max(0, allLines.Length - lines);
                    var sb = new StringBuilder();
                    for (int i = start; i < allLines.Length; i++)
                        sb.AppendLine(allLines[i]);
                    return sb.ToString();
                }
                catch (Exception ex)
                {
                    return $"[读取程序日志失败: {ex.Message}]";
                }
            }

            /// <summary>获取插件列表摘要</summary>
            private static string GetPluginListSummary()
            {
                string? workPath = Config.CurrentServer.WorkPath;
                if (string.IsNullOrEmpty(workPath)) return "[未配置工作目录]";

                string pluginsDir = Path.Combine(workPath, "plugins");
                if (!Directory.Exists(pluginsDir)) return "[plugins 目录不存在]";

                try
                {
                    var files = new DirectoryInfo(pluginsDir)
                        .GetFiles("*.jar")
                        .Concat(new DirectoryInfo(pluginsDir).GetFiles("*.disjar"))
                        .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (files.Count == 0) return "[无插件]";

                    var sb = new StringBuilder();
                    int enabled = 0, disabled = 0;
                    foreach (var f in files)
                    {
                        bool isDisabled = f.Extension.Equals(".disjar", StringComparison.OrdinalIgnoreCase);
                        if (isDisabled) disabled++; else enabled++;
                        sb.AppendLine($"  {(isDisabled ? "[禁用]" : "[启用]")} {f.Name}");
                    }
                    sb.Insert(0, $"共 {files.Count} 个插件(启用: {enabled}, 禁用: {disabled}):\n");
                    return sb.ToString();
                }
                catch (Exception ex)
                {
                    return $"[获取插件列表失败: {ex.Message}]";
                }
            }

            /// <summary>
            /// 运行AI对话(含工具调用循环)
            /// </summary>
            private static async Task<string?> RunAiWithToolsAsync(
                string taskKey, AiTask task, string inputData,
                List<string> allowedTools, int maxRounds)
            {
                var config = ContentManager.Ai.ServerAutoAi;

                // 构建系统提示
                string systemPrompt = BuildSystemPrompt(task, allowedTools);

                // 获取/初始化上下文缓存
                List<ChatMessage> contextMessages;
                lock (_cacheLock)
                {
                    if (!_contextCache.TryGetValue(taskKey, out var existing))
                    {
                        contextMessages = new List<ChatMessage>();
                        _contextCache[taskKey] = contextMessages;
                    }
                    else
                    {
                        contextMessages = existing;
                    }

                    // 控制缓存大小
                    int maxCache = config.ContextCacheSize > 0 ? config.ContextCacheSize : 20;
                    while (contextMessages.Count > maxCache * 2)
                    {
                        contextMessages.RemoveAt(0);
                    }
                }

                // 构建用户消息
                string userContent = $"{task.Prompt}\n\n=== 输入数据 ===\n{inputData}\n\n请根据以上数据分析并给出结论。如需调用工具请使用 [rt:tools\"(工具名{{参数JSON}})\"] 格式。";

                // 调用AI
                string? aiResponse = await CallAiAsync(systemPrompt, userContent, contextMessages);
                if (string.IsNullOrWhiteSpace(aiResponse))
                {
                    Log($"任务 [{taskKey}] AI返回空内容。", 2);
                    return null;
                }

                // 记录到缓存
                lock (_cacheLock)
                {
                    contextMessages.Add(new ChatMessage { Role = "user", Content = userContent });
                    contextMessages.Add(new ChatMessage { Role = "assistant", Content = aiResponse });
                }

                Log($"任务 [{taskKey}] AI首轮回复:\n{aiResponse}", 1);

                // 工具调用循环
                for (int round = 1; round <= maxRounds; round++)
                {
                    var toolCalls = ParseToolCalls(aiResponse!);
                    if (toolCalls.Count == 0) break;

                    Log($"任务 [{taskKey}] 第 {round} 轮工具调用，共 {toolCalls.Count} 个。", 1);

                    // 执行所有工具调用
                    var toolResults = new StringBuilder();
                    foreach (var (toolName, argsJson) in toolCalls)
                    {
                        if (!AiTools.IsToolAllowed(toolName, config.Tools, allowedTools))
                        {
                            string msg = $"[工具 {toolName} 不允许调用]";
                            toolResults.AppendLine($"--- 工具 {toolName} 结果 ---\n{msg}\n");
                            Log($"任务 [{taskKey}] 工具 {toolName} 不允许调用", 2);
                            continue;
                        }

                        Log($"任务 [{taskKey}] 调用工具: {toolName} 参数: {argsJson}", 1);
                        string result = await AiTools.ExecuteToolAsync(toolName, argsJson);
                        toolResults.AppendLine($"--- 工具 {toolName} 结果 ---\n{result}\n");
                    }

                    // 将工具结果反馈给AI
                    string followUpContent = $"工具执行结果如下:\n\n{toolResults}\n\n请根据以上工具执行结果继续分析，并给出最终结论。如不需要更多工具调用，请直接输出结论。";

                    lock (_cacheLock)
                    {
                        contextMessages.Add(new ChatMessage { Role = "user", Content = followUpContent });
                    }

                    string? followUpResponse = await CallAiAsync(systemPrompt, followUpContent, contextMessages);
                    if (string.IsNullOrWhiteSpace(followUpResponse))
                    {
                        Log($"任务 [{taskKey}] AI第{round + 1}轮返回空内容。", 2);
                        break;
                    }

                    lock (_cacheLock)
                    {
                        contextMessages.Add(new ChatMessage { Role = "assistant", Content = followUpResponse });
                    }

                    aiResponse = followUpResponse;
                    Log($"任务 [{taskKey}] AI第{round + 1}轮回复:\n{aiResponse}", 1);

                    // 检查是否还有工具调用
                    var nextToolCalls = ParseToolCalls(aiResponse);
                    if (nextToolCalls.Count == 0)
                    {
                        Log($"任务 [{taskKey}] AI已完成分析，无更多工具调用。", 1);
                        break;
                    }
                }

                return aiResponse;
            }

            /// <summary>构建系统提示词</summary>
            private static string BuildSystemPrompt(AiTask task, List<string> allowedTools)
            {
                var config = ContentManager.Ai.ServerAutoAi;
                var sb = new StringBuilder();

                sb.AppendLine(config.Prompt);
                sb.AppendLine();

                // 任务上下文
                sb.AppendLine($"# 当前任务: {task.Name}");
                sb.AppendLine($"# 任务提示: {task.Prompt}");
                sb.AppendLine();

                // 工具说明
                if (config.Tools != null && config.Tools.Enabled)
                {
                    sb.AppendLine("# 工具说明");
                    sb.AppendLine(AiTools.GetToolsDescription(config.Tools));

                    if (allowedTools.Count > 0)
                    {
                        sb.AppendLine($"# 本次任务允许调用的工具: {string.Join(", ", allowedTools)}");
                    }
                    sb.AppendLine();
                }

                // 技能
                if (config.Skills != null && config.Skills.Count > 0)
                {
                    sb.AppendLine("# 可用技能");
                    foreach (var skill in config.Skills)
                    {
                        sb.AppendLine($"- {skill.Name}: {skill.Description}");
                        if (!string.IsNullOrWhiteSpace(skill.Behavior))
                            sb.AppendLine($"  行为: {skill.Behavior}");
                    }
                    sb.AppendLine();
                }

                // 规则
                if (config.Rules != null && config.Rules.Count > 0)
                {
                    sb.AppendLine("# 必须遵守的规则");
                    foreach (var rule in config.Rules)
                    {
                        sb.AppendLine($"- {rule}");
                    }
                    sb.AppendLine();
                }

                sb.AppendLine("# 输出要求");
                sb.AppendLine("- 如需调用工具，必须在回复中包含 [rt:tools\"(工具名{参数JSON})\"] 格式的调用。");
                sb.AppendLine("- 工具调用格式示例: [rt:tools\"(get_server_log{\"lines\":100})\"]");
                sb.AppendLine("- 每次回复可以包含多个工具调用。");
                sb.AppendLine("- 在调用工具后，请等待工具返回结果后再继续分析。");
                sb.AppendLine("- 最终必须给出明确的结论和建议。");

                return sb.ToString();
            }

            /// <summary>解析AI回复中的工具调用</summary>
            private static List<(string toolName, string argsJson)> ParseToolCalls(string content)
            {
                var result = new List<(string, string)>();
                var matches = ToolCallPattern.Matches(content);

                foreach (Match m in matches)
                {
                    string toolName = m.Groups[1].Value.Trim();
                    string argsJson = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "{}";
                    result.Add((toolName, argsJson));
                }

                return result;
            }

            /// <summary>调用AI API(带上下文)</summary>
            private static async Task<string?> CallAiAsync(string systemPrompt, string userContent, List<ChatMessage> contextMessages)
            {
                var config = ContentManager.Ai.ServerAutoAi;
                if (config == null) return null;

                // 复用Console_Error配置(若ServerAutoAi未配置)
                string apiEndpoint = string.IsNullOrWhiteSpace(config.ApiEndpoint) ? ContentManager.Ai.Console_Error.ApiEndpoint : config.ApiEndpoint;
                string apiKey = string.IsNullOrWhiteSpace(config.ApiKey) ? ContentManager.Ai.Console_Error.ApiKey : config.ApiKey;
                string model = string.IsNullOrWhiteSpace(config.Model) ? ContentManager.Ai.Console_Error.Model : config.Model;

                if (string.IsNullOrWhiteSpace(apiEndpoint))
                {
                    Log("AI API地址未配置。", 3);
                    return null;
                }

                try
                {
                    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds) };
                    if (!string.IsNullOrWhiteSpace(apiKey))
                        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

                    // 构建消息列表(系统提示 + 上下文 + 当前用户消息)
                    var messages = new List<object>
                    {
                        new { role = "system", content = systemPrompt }
                    };

                    // 添加上下文(历史消息)
                    lock (_cacheLock)
                    {
                        foreach (var msg in contextMessages)
                        {
                            messages.Add(new { role = msg.Role, content = msg.Content });
                        }
                    }

                    // 当前用户消息
                    messages.Add(new { role = "user", content = userContent });

                    var requestBody = new
                    {
                        model = model,
                        messages = messages,
                        max_tokens = config.MaxTokens,
                        temperature = config.Temperature,
                        stream = false
                    };

                    var json = JsonConvert.SerializeObject(requestBody);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    HttpResponseMessage response;

                    if (config.RetryOnError)
                    {
                        var retryPipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
                            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
                            {
                                MaxRetryAttempts = config.RetryCount,
                                Delay = TimeSpan.FromSeconds(3),
                                BackoffType = DelayBackoffType.Exponential,
                                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                                    .Handle<HttpRequestException>()
                                    .Handle<TaskCanceledException>()
                                    .HandleResult(r => !r.IsSuccessStatusCode),
                                OnRetry = args =>
                                {
                                    Log($"AI请求失败，正在重试 ({args.AttemptNumber + 1}/{config.RetryCount})...", 2);
                                    return default;
                                }
                            })
                            .Build();

                        response = await retryPipeline.ExecuteAsync(async token =>
                        {
                            return await httpClient.PostAsync(apiEndpoint, content, token);
                        });
                    }
                    else
                    {
                        response = await httpClient.PostAsync(apiEndpoint, content);
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody = await response.Content.ReadAsStringAsync();
                        Log($"AI请求失败 (HTTP {(int)response.StatusCode}): {errorBody}", 3);
                        return null;
                    }

                    var responseBody = await response.Content.ReadAsStringAsync();
                    var jsonResponse = JObject.Parse(responseBody);
                    string? aiMessage = jsonResponse["choices"]?[0]?["message"]?["content"]?.ToString();

                    return aiMessage;
                }
                catch (TaskCanceledException)
                {
                    Log("AI请求超时。", 3);
                    return null;
                }
                catch (Exception ex)
                {
                    Log($"AI调用出错: {ex.Message}", 3);
                    return null;
                }
            }

            /// <summary>保存AI回复到ai_save.yml(上下文缓存格式)</summary>
            private static void SaveAiContextResponse(string taskKey, string taskName, string inputData, string aiResponse)
            {
                try
                {
                    string filePath = Path.Combine(Config.DataPath, "ai_save.yml");
                    var existing = new List<Dictionary<string, object>>();

                    if (File.Exists(filePath))
                    {
                        try
                        {
                            var yaml = File.ReadAllText(filePath);
                            var deserializer = new DeserializerBuilder()
                                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                                .Build();
                            var loaded = deserializer.Deserialize<List<Dictionary<string, object>>>(yaml);
                            if (loaded != null) existing = loaded;
                        }
                        catch { }
                    }

                    // 截断过长的输入内容
                    string trimmedInput = inputData.Length > 2000 ? inputData.Substring(0, 1997) + "..." : inputData;

                    existing.Add(new Dictionary<string, object>
                    {
                        ["task_key"] = taskKey,
                        ["task_name"] = taskName,
                        ["input_summary"] = trimmedInput,
                        ["ai_response"] = aiResponse,
                        ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    });

                    // 限制保存条数(避免无限增长)
                    if (existing.Count > 500)
                        existing = existing.Skip(existing.Count - 500).ToList();

                    var serializer = new SerializerBuilder()
                        .WithNamingConvention(UnderscoredNamingConvention.Instance)
                        .Build();
                    File.WriteAllText(filePath, serializer.Serialize(existing));
                }
                catch (Exception ex)
                {
                    Log($"保存AI回复失败: {ex.Message}", 2);
                }
            }

            /// <summary>
            /// 标准化Cron表达式
            /// Hangfire使用Quartz格式(6位或7位): 秒 分 时 日 月 周 [年]
            /// 用户输入示例: "0 0 0/1 * * ?" 表示每小时
            /// </summary>
            private static string NormalizeCron(string cronExpr)
            {
                if (string.IsNullOrWhiteSpace(cronExpr)) return "0 0 * * * *"; // 默认每小时

                string trimmed = cronExpr.Trim();

                // 将 ? 替换为 * (Hangfire不识别 ?)
                trimmed = trimmed.Replace('?', '*');

                // 检查字段数
                var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                // Hangfire需要6或7个字段
                if (parts.Length == 5)
                {
                    // 5位Cron: 分 时 日 月 周 -> 秒 分 时 日 月 周
                    trimmed = "0 " + trimmed;
                }
                else if (parts.Length < 5)
                {
                    Log($"Cron表达式字段不足，使用默认每小时: {cronExpr}", 2);
                    return "0 0 * * * *";
                }

                return trimmed;
            }

            /// <summary>清除指定任务的上下文缓存</summary>
            public static void ClearContextCache(string taskKey)
            {
                lock (_cacheLock)
                {
                    if (_contextCache.ContainsKey(taskKey))
                    {
                        _contextCache[taskKey].Clear();
                        Log($"已清除任务 [{taskKey}] 的上下文缓存。", 1);
                    }
                }
            }

            /// <summary>清除所有任务的上下文缓存</summary>
            public static void ClearAllContextCache()
            {
                lock (_cacheLock)
                {
                    _contextCache.Clear();
                    Log("已清除所有任务的上下文缓存。", 1);
                }
            }
        }

        #endregion

        #region AI自动化管理 - 数据模型

        /// <summary>聊天消息(用于上下文缓存)</summary>
        internal class ChatMessage
        {
            public string Role { get; set; } = "user";
            public string Content { get; set; } = "";
        }

        #endregion
    }
}
