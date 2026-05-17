using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Polly;
using Polly.Retry;
using RtCli.Modules.Extension;
using RtCli.Modules.Unit;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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
            new ServerTypeInfo("Leaf (Paper分支)", ServerApi.Manual, "leaf.jar", website: "https://github.com/Winds-Studio/Leaf/releases"),
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
                var serverChoices = Config.App.ServerList.Keys.ToList();
                serverChoices.Add("[添加新服务端]");
                var selected = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("检测到多个服务端配置，请选择要配置的服务端：")
                        .AddChoices(serverChoices));

                if (selected == "[添加新服务端]")
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
                Output.Log("未检测到 Java 环境，请先安装 JDK 17 或更高版本。", 3, ThisProgramName);

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
                Output.Log($"正在获取 {projectId} 版本列表...", 1, ThisProgramName);

                string projectJson = await _httpClient.GetStringAsync($"https://api.papermc.io/v2/projects/{projectId}");
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

                string buildsJson = await _httpClient.GetStringAsync($"https://api.papermc.io/v2/projects/{projectId}/versions/{version}");
                var buildsData = JObject.Parse(buildsJson);
                var builds = buildsData["builds"]!.Select(b => b.ToString()).ToList();

                if (!builds.Any())
                {
                    Output.Log($"版本 {version} 没有可用的构建。", 2, ThisProgramName);
                    return null;
                }

                string latestBuild = builds.Last();

                string buildDetailJson = await _httpClient.GetStringAsync($"https://api.papermc.io/v2/projects/{projectId}/versions/{version}/builds/{latestBuild}");
                var buildDetail = JObject.Parse(buildDetailJson);
                string? fileName = buildDetail["downloads"]?["application"]?["name"]?.ToString();

                if (string.IsNullOrEmpty(fileName))
                {
                    fileName = $"{projectId}-{version}-{latestBuild}.jar";
                }

                string downloadUrl = $"https://api.papermc.io/v2/projects/{projectId}/versions/{version}/builds/{latestBuild}/downloads/{fileName}";
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
                            eulaContent = eulaContent.Replace("eula=false", "eula=true");
                            File.WriteAllText(eulaPath, eulaContent);
                            Output.Log("已自动同意 EULA（配置: auto_agree_eula = true）。", 1, ThisProgramName);

                            bool restart = AnsiConsole.Confirm("是否重新启动服务端？", true);
                            if (restart)
                            {
                                Analyzer.StartServer();
                                Output.Log("服务端已重新启动。", 1, ThisProgramName);
                            }
                        }
                        else
                        {
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

        private enum ServerApi { Vanilla, VanillaSnapshot, PaperMC, Purpur, Fabric, Manual }

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
    }
}
