using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RtCli.Modules.Unit;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RtCli.Modules.Function
{
    internal class Intelligence
    {
        private static readonly string ThisProgramName = "Guide";

        private static readonly string[] PopularVersions = new[]
        {
            "1.21.4", "1.21.3", "1.21.1", "1.21",
            "1.20.6", "1.20.4", "1.20.2", "1.20.1",
            "1.19.4", "1.19.2",
            "1.18.2",
            "1.16.5",
            "1.12.2",
        };

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
            AnsiConsole.Write(new Rule("[yellow]Minecraft 服务端架设引导[/]").RuleStyle("grey").Centered());

            if (!await Step1_CheckJava()) return;
            if (!await Step2_DownloadServer()) return;
            if (!await Step3_ConfigureAndStart()) return;

            AnsiConsole.Write(new Rule("[green]引导完成！[/]").RuleStyle("green").Centered());
            Output.Log("服务端架设引导完成！之后可以使用 .server start 启动服务端。", 1, ThisProgramName);
        }

        private static async Task<bool> Step1_CheckJava()
        {
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
                return false;
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
                return false;
            }

            Output.Log("Java 版本满足要求 (JDK 17+)。", 1, ThisProgramName);
            return true;
        }

        private static async Task<bool> Step2_DownloadServer()
        {
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

                var availablePopular = filteredVersions.Where(v => PopularVersions.Contains(v.Id)).ToList();
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
            try
            {
                Output.Log($"正在获取 {projectId} 版本列表...", 1, ThisProgramName);

                string projectJson = await _httpClient.GetStringAsync($"https://api.papermc.io/v2/projects/{projectId}");
                var project = JObject.Parse(projectJson);
                var allVersions = project["versions"]!.Select(v => v.ToString()).ToList();

                var availablePopular = allVersions.Where(v => PopularVersions.Contains(v)).ToList();
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
            try
            {
                Output.Log("正在获取 Purpur 版本列表...", 1, ThisProgramName);

                string versionsJson = await _httpClient.GetStringAsync("https://api.purpurmc.org/v2/purpur");
                var versionsData = JObject.Parse(versionsJson);
                var allVersions = versionsData["versions"]!.Select(v => v.ToString()).ToList();

                var availablePopular = allVersions.Where(v => PopularVersions.Contains(v)).ToList();
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
            try
            {
                Output.Log("正在获取 Fabric 版本列表...", 1, ThisProgramName);

                string gameVersionsJson = await _httpClient.GetStringAsync("https://meta.fabricmc.net/v2/versions/game");
                var gameVersions = JArray.Parse(gameVersionsJson);
                var stableVersions = gameVersions.Where(v => v["stable"]?.Value<bool>() == true).ToList();

                var allVersionIds = stableVersions.Select(v => v["version"]!.ToString()).ToList();
                var availablePopular = allVersionIds.Where(v => PopularVersions.Contains(v)).ToList();
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
            Output.Log("[[步骤 3/3]] 配置并启动服务端...", 1, ThisProgramName);

            if (string.IsNullOrWhiteSpace(Config.App.WorkPath) || string.IsNullOrWhiteSpace(Config.App.RunServerFlags))
            {
                Output.Log("配置未完成，请检查 work_path 和 run_server_flags。", 3, ThisProgramName);
                return false;
            }

            Config.App.AnalyzerMode = "RUN";
            SaveConfig();
            Analyzer.Initialize();
            Output.Log("已将模式设置为 RUN。", 1, ThisProgramName);

            bool shouldStart = AnsiConsole.Confirm("是否现在启动服务端？", false);
            if (!shouldStart)
            {
                Output.Log("稍后可使用 .server start 启动服务端。", 1, ThisProgramName);
                return true;
            }

            string workPath = Config.App.WorkPath;
            var jarFiles = Directory.GetFiles(workPath, "*.jar");
            var otherFiles = Directory.GetFiles(workPath).Where(f => !f.EndsWith(".jar")).ToList();
            var otherDirs = Directory.GetDirectories(workPath).ToList();

            bool isEmptyDir = jarFiles.Length > 0 && otherFiles.Count == 0 && otherDirs.Count == 0;

            Analyzer.StartServer();

            if (isEmptyDir)
            {
                Output.Log("检测到服务端目录为空（首次运行），等待 EULA 确认...", 1, ThisProgramName);

                await Task.Delay(5000);

                bool serverExited = !Analyzer.IsRunModeActive;

                if (serverExited)
                {
                    string eulaPath = Path.Combine(workPath, "eula.txt");
                    if (File.Exists(eulaPath))
                    {
                        string eulaContent = File.ReadAllText(eulaPath);
                        if (eulaContent.Contains("eula=false"))
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
                        Output.Log("服务端启动后意外退出，未检测到 eula.txt。", 2, ThisProgramName);
                        Output.Log("请检查服务端日志确认问题。", 2, ThisProgramName);
                    }
                }
                else
                {
                    Output.Log("服务端正在运行中。", 1, ThisProgramName);
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
            string workPath = Config.App.WorkPath;

            if (!string.IsNullOrWhiteSpace(workPath) && Directory.Exists(workPath))
            {
                bool useExisting = AnsiConsole.Confirm($"当前工作目录为 {workPath}，是否使用？", true);
                if (useExisting) return workPath;
            }

            Output.Log("请输入 MC 服务端的工作目录路径（jar 文件所在目录）：", 1, ThisProgramName);
            workPath = Console.ReadLine()?.Trim().Trim('"') ?? "";

            if (string.IsNullOrWhiteSpace(workPath))
            {
                Output.Log("未输入工作目录。", 2, ThisProgramName);
                return "";
            }

            return workPath;
        }

        private static void UpdateConfig(string workPath, string jarFileName)
        {
            Config.App.WorkPath = workPath;

            string flags = Config.App.RunServerFlags;
            if (flags.Contains("-jar"))
            {
                flags = Regex.Replace(flags, @"-jar\s+\S+", $"-jar {jarFileName}");
            }
            else
            {
                flags += $" -jar {jarFileName}";
            }
            Config.App.RunServerFlags = flags;

            SaveConfig();

            Output.Log($"已更新配置: work_path={workPath}", 1, ThisProgramName);
            Output.Log($"已更新配置: run_server_flags 包含 -jar {jarFileName}", 1, ThisProgramName);
        }

        private static void SaveConfig()
        {
            try
            {
                string configPath = Path.Combine(Config.DataPath, "config.yml");
                var serializer = new YamlDotNet.Serialization.SerializerBuilder()
                    .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
                    .Build();
                var yaml = serializer.Serialize(Config.App);
                File.WriteAllText(configPath, yaml);
            }
            catch (Exception ex)
            {
                Output.Log($"保存配置失败: {ex.Message}", 3, ThisProgramName);
            }
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

        public static void Auto()
        {
            // Microsoft.Extensions.AI
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
