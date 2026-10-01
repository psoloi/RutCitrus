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
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.Extensions.AI;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RtCli.Modules.Function
{
    /// <summary>Intelligence 分部实现(自 Intelligence.cs 按职责拆分, 类成员同源)。</summary>
    internal partial class Intelligence
    {
        #region 面板向导支持 (非交互, 供 gRPC 调用)

        /// <summary>面板向导用的服务端类型信息</summary>
        internal class PanelServerTypeInfo
        {
            public string Name { get; set; } = "";
            public string TypeKey { get; set; } = "";
            public bool Downloadable { get; set; }
            public string Website { get; set; } = "";
            public string DefaultJar { get; set; } = "";
        }

        /// <summary>从显示名提取类型键: "Paper (高性能)" → "paper"</summary>
        private static string TypeKeyFromName(string name)
        {
            int idx = name.IndexOf(" (");
            return (idx > 0 ? name[..idx] : name).Trim().ToLowerInvariant();
        }

        private static ServerTypeInfo? FindServerByTypeKey(string typeKey)
        {
            if (string.IsNullOrWhiteSpace(typeKey)) return null;
            string key = typeKey.Trim().ToLowerInvariant();
            return ServerList.FirstOrDefault(s => TypeKeyFromName(s.Name) == key);
        }

        /// <summary>面板向导: 服务端类型列表(对应 .guide 步骤2的类型选择)</summary>
        internal static List<PanelServerTypeInfo> GetServerTypeListForPanel()
        {
            return ServerList.Select(s => new PanelServerTypeInfo
            {
                Name = s.Name,
                TypeKey = TypeKeyFromName(s.Name),
                Downloadable = s.Api != ServerApi.Manual,
                Website = s.Website ?? "",
                DefaultJar = s.DefaultFileName
            }).ToList();
        }

        /// <summary>面板向导: 获取指定类型的版本列表(常用版本 + 全部版本)</summary>
        internal static async Task<(List<string> Popular, List<string> All, string Error)> GetServerVersionListForPanel(string typeKey)
        {
            var info = FindServerByTypeKey(typeKey);
            if (info == null) return (new List<string>(), new List<string>(), $"未知的服务端类型: {typeKey}");

            try
            {
                List<string> all = info.Api switch
                {
                    ServerApi.Vanilla => await GetVanillaVersionList(false),
                    ServerApi.VanillaSnapshot => await GetVanillaVersionList(true),
                    ServerApi.PaperMC => await GetPaperVersionList(info.ProjectId!),
                    ServerApi.Leaf => await GetLeafVersionList(info.ProjectId!),
                    ServerApi.Purpur => await GetPurpurVersionList(),
                    ServerApi.Fabric => await GetFabricVersionList(),
                    _ => new List<string>()
                };

                var popular = all.Where(v => GetPopularVersions().Contains(v)).ToList();
                if (popular.Count == 0) popular = all.Take(10).ToList();
                return (popular, all, "");
            }
            catch (Exception ex)
            {
                return (new List<string>(), new List<string>(), $"获取版本列表失败: {ex.Message}");
            }
        }

        /// <summary>面板向导: 下载指定类型/版本的服务端 jar (version 留空 = 最新版)</summary>
        internal static async Task<(bool Success, string Message, string JarName)> DownloadServerJarForPanel(string typeKey, string version, string workPath)
        {
            string ThisName = "PanelGuide";
            var info = FindServerByTypeKey(typeKey);
            if (info == null) return (false, $"未知的服务端类型: {typeKey}", "");
            if (info.Api == ServerApi.Manual)
                return (false, $"{info.Name} 需要手动下载, 请访问: {info.Website}", "");

            try
            {
                if (string.IsNullOrWhiteSpace(workPath)) return (false, "工作目录不能为空", "");
                if (!Directory.Exists(workPath))
                {
                    Directory.CreateDirectory(workPath);
                    Output.Log($"已创建工作目录: {workPath}", 1, ThisName);
                }

                string? jarName = info.Api switch
                {
                    ServerApi.Vanilla => await DownloadVanillaVersion(workPath, version, false),
                    ServerApi.VanillaSnapshot => await DownloadVanillaVersion(workPath, version, true),
                    ServerApi.PaperMC => await DownloadPaperVersion(workPath, info.ProjectId!, version),
                    ServerApi.Leaf => await DownloadLeafVersion(workPath, info.ProjectId!, version),
                    ServerApi.Purpur => await DownloadPurpurVersion(workPath, version),
                    ServerApi.Fabric => await DownloadFabricVersion(workPath, version),
                    _ => null
                };

                if (string.IsNullOrEmpty(jarName))
                    return (false, "下载失败, 详情请查看 RtCli 控制台日志", "");

                Output.Log($"面板向导下载完成: {info.Name} {version} → {jarName}", 1, ThisName);
                return (true, $"已下载: {jarName}", jarName);
            }
            catch (Exception ex)
            {
                Output.Log($"面板向导下载失败: {ex.Message}", 3, ThisName);
                return (false, $"下载失败: {ex.Message}", "");
            }
        }

        private static void EnsurePaperUserAgent()
        {
            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent",
                    $"RtCli/{Program.RtCliVersion} (https://github.com/RutCitrus/RtCli)");
            }
        }

        private static async Task<List<string>> GetVanillaVersionList(bool includeSnapshots)
        {
            string manifestJson = await _httpClient.GetStringAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
            var manifest = JObject.Parse(manifestJson);
            var types = includeSnapshots ? new[] { "release", "snapshot" } : new[] { "release" };
            return manifest["versions"]!
                .Where(v => types.Contains(v["type"]?.ToString()))
                .Select(v => v["id"]!.ToString())
                .ToList();
        }

        private static async Task<List<string>> GetPaperVersionList(string projectId)
        {
            EnsurePaperUserAgent();
            string projectJson = await _httpClient.GetStringAsync($"https://fill.papermc.io/v3/projects/{projectId}");
            var project = JObject.Parse(projectJson);
            var versionsObj = project["versions"] as JObject;

            var all = new List<string>();
            if (versionsObj != null)
            {
                foreach (var prop in versionsObj.Properties())
                {
                    if (prop.Value is JArray arr)
                    {
                        foreach (var v in arr)
                            all.Add(v.ToString());
                    }
                }
            }
            return all;
        }

        private static async Task<List<string>> GetLeafVersionList(string projectId)
        {
            string projectJson = await _httpClient.GetStringAsync($"https://api.leafmc.one/v2/projects/{projectId}");
            var project = JObject.Parse(projectJson);
            return project["versions"]!.Select(v => v.ToString()).ToList();
        }

        private static async Task<List<string>> GetPurpurVersionList()
        {
            string versionsJson = await _httpClient.GetStringAsync("https://api.purpurmc.org/v2/purpur");
            var versionsData = JObject.Parse(versionsJson);
            return versionsData["versions"]!.Select(v => v.ToString()).ToList();
        }

        private static async Task<List<string>> GetFabricVersionList()
        {
            string gameVersionsJson = await _httpClient.GetStringAsync("https://meta.fabricmc.net/v2/versions/game");
            var gameVersions = JArray.Parse(gameVersionsJson);
            return gameVersions
                .Where(v => v["stable"]?.Value<bool>() == true)
                .Select(v => v["version"]!.ToString())
                .ToList();
        }

        /// <summary>下载指定 Vanilla 版本(version 留空 = 最新)</summary>
        private static async Task<string?> DownloadVanillaVersion(string workPath, string versionId, bool includeSnapshots)
        {
            string ThisName = "PanelGuide";
            string label = includeSnapshots ? "Vanilla/Snapshot" : "Vanilla";
            try
            {
                var filtered = (await GetVanillaVersionList(includeSnapshots)).ToList();
                if (filtered.Count == 0) return null;

                string vid = string.IsNullOrWhiteSpace(versionId) ? filtered[0] : versionId.Trim();
                if (!filtered.Contains(vid))
                {
                    Output.Log($"版本 {vid} 不存在或不可用。", 2, ThisName);
                    return null;
                }

                string manifestJson = await _httpClient.GetStringAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
                var manifest = JObject.Parse(manifestJson);
                string? versionUrl = manifest["versions"]!
                    .FirstOrDefault(v => v["id"]?.ToString() == vid)?["url"]?.ToString();
                if (string.IsNullOrEmpty(versionUrl))
                {
                    Output.Log($"未找到版本 {vid} 的详细信息。", 2, ThisName);
                    return null;
                }

                string versionDetailJson = await _httpClient.GetStringAsync(versionUrl);
                var versionDetail = JObject.Parse(versionDetailJson);
                string? serverUrl = versionDetail["downloads"]?["server"]?["url"]?.ToString();
                if (string.IsNullOrEmpty(serverUrl))
                {
                    Output.Log($"版本 {vid} 没有服务端下载。", 2, ThisName);
                    return null;
                }

                string jarName = $"server-{vid}.jar";
                string jarPath = Path.Combine(workPath, jarName);
                if (File.Exists(jarPath))
                {
                    Output.Log($"文件已存在: {jarPath}", 1, ThisName);
                    return jarName;
                }

                bool downloaded = await DownloadServerJar(serverUrl, jarPath, $"{label} {vid}");
                return downloaded ? jarName : null;
            }
            catch (Exception ex)
            {
                Output.Log($"获取 {label} 下载信息失败: {ex.Message}", 3, ThisName);
                return null;
            }
        }

        /// <summary>下载指定 PaperMC 系版本(version 留空 = 最新), 适用 paper/folia/velocity</summary>
        private static async Task<string?> DownloadPaperVersion(string workPath, string projectId, string version)
        {
            string ThisName = "PanelGuide";
            try
            {
                EnsurePaperUserAgent();
                const string apiBase = "https://fill.papermc.io/v3";

                if (string.IsNullOrWhiteSpace(version))
                {
                    var all = await GetPaperVersionList(projectId);
                    if (all.Count == 0)
                    {
                        Output.Log($"未找到 {projectId} 的可用版本。", 2, ThisName);
                        return null;
                    }
                    version = all[0];
                }

                string buildsJson = await _httpClient.GetStringAsync($"{apiBase}/projects/{projectId}/versions/{version}/builds");
                var buildsArray = JArray.Parse(buildsJson);
                if (buildsArray.Count == 0)
                {
                    Output.Log($"版本 {version} 没有可用的构建。", 2, ThisName);
                    return null;
                }

                var stableBuilds = buildsArray.Where(b => b["channel"]?.ToString() == "STABLE").ToList();
                var recommendedBuilds = buildsArray.Where(b => b["channel"]?.ToString() == "RECOMMENDED").ToList();
                JToken? selectedBuild = stableBuilds.Any() ? stableBuilds.Last()
                    : recommendedBuilds.Any() ? recommendedBuilds.Last()
                    : buildsArray.Last();

                int buildId = selectedBuild["id"]?.ToObject<int>() ?? 0;
                string channel = selectedBuild["channel"]?.ToString() ?? "UNKNOWN";

                string? downloadUrl = selectedBuild["downloads"]?["server:default"]?["url"]?.ToString();
                string? fileName = selectedBuild["downloads"]?["server:default"]?["name"]?.ToString();
                if (string.IsNullOrEmpty(downloadUrl))
                {
                    Output.Log($"构建 {buildId} 没有可用的下载链接。", 2, ThisName);
                    return null;
                }
                if (string.IsNullOrEmpty(fileName)) fileName = $"{projectId}-{version}-{buildId}.jar";

                string jarName = $"{projectId}-{version}.jar";
                string jarPath = Path.Combine(workPath, jarName);
                if (File.Exists(jarPath))
                {
                    Output.Log($"文件已存在: {jarPath}", 1, ThisName);
                    return jarName;
                }

                Output.Log($"选择构建: {buildId} (渠道: {channel})", 1, ThisName);
                bool downloaded = await DownloadServerJar(downloadUrl, jarPath, $"{projectId} {version} (build {buildId})");
                return downloaded ? jarName : null;
            }
            catch (Exception ex)
            {
                Output.Log($"获取 {projectId} 下载信息失败: {ex.Message}", 3, ThisName);
                return null;
            }
        }

        /// <summary>下载指定 Leaf 版本(version 留空 = 最新)</summary>
        private static async Task<string?> DownloadLeafVersion(string workPath, string projectId, string version)
        {
            string ThisName = "PanelGuide";
            try
            {
                if (string.IsNullOrWhiteSpace(version))
                {
                    var all = await GetLeafVersionList(projectId);
                    if (all.Count == 0)
                    {
                        Output.Log($"未找到 {projectId} 的可用版本。", 2, ThisName);
                        return null;
                    }
                    version = all[^1];
                }

                string buildsJson = await _httpClient.GetStringAsync($"https://api.leafmc.one/v2/projects/{projectId}/versions/{version}");
                var buildsData = JObject.Parse(buildsJson);
                var builds = buildsData["builds"]!.Select(b => b.ToString()).ToList();
                if (!builds.Any())
                {
                    Output.Log($"版本 {version} 没有可用的构建。", 2, ThisName);
                    return null;
                }

                string latestBuild = builds.Last();
                string buildDetailJson = await _httpClient.GetStringAsync($"https://api.leafmc.one/v2/projects/{projectId}/versions/{version}/builds/{latestBuild}");
                var buildDetail = JObject.Parse(buildDetailJson);
                string? fileName = buildDetail["downloads"]?["application"]?["name"]?.ToString();
                if (string.IsNullOrEmpty(fileName)) fileName = $"{projectId}-{version}-{latestBuild}.jar";

                string downloadUrl = $"https://api.leafmc.one/v2/projects/{projectId}/versions/{version}/builds/{latestBuild}/downloads/{fileName}";
                string jarName = $"{projectId}-{version}.jar";
                string jarPath = Path.Combine(workPath, jarName);
                if (File.Exists(jarPath))
                {
                    Output.Log($"文件已存在: {jarPath}", 1, ThisName);
                    return jarName;
                }

                bool downloaded = await DownloadServerJar(downloadUrl, jarPath, $"{projectId} {version} (build {latestBuild})");
                return downloaded ? jarName : null;
            }
            catch (Exception ex)
            {
                Output.Log($"获取 {projectId} 下载信息失败: {ex.Message}", 3, ThisName);
                return null;
            }
        }

        /// <summary>下载指定 Purpur 版本(version 留空 = 最新)</summary>
        private static async Task<string?> DownloadPurpurVersion(string workPath, string version)
        {
            string ThisName = "PanelGuide";
            try
            {
                if (string.IsNullOrWhiteSpace(version))
                {
                    var all = await GetPurpurVersionList();
                    if (all.Count == 0)
                    {
                        Output.Log("未找到 Purpur 的可用版本。", 2, ThisName);
                        return null;
                    }
                    version = all[^1];
                }

                string jarName = $"purpur-{version}.jar";
                string jarPath = Path.Combine(workPath, jarName);
                if (File.Exists(jarPath))
                {
                    Output.Log($"文件已存在: {jarPath}", 1, ThisName);
                    return jarName;
                }

                string downloadUrl = $"https://api.purpurmc.org/v2/purpur/{version}/latest/download";
                bool downloaded = await DownloadServerJar(downloadUrl, jarPath, $"Purpur {version}");
                return downloaded ? jarName : null;
            }
            catch (Exception ex)
            {
                Output.Log($"获取 Purpur 下载信息失败: {ex.Message}", 3, ThisName);
                return null;
            }
        }

        /// <summary>下载指定 Fabric 版本(version 留空 = 最新稳定游戏版本)</summary>
        private static async Task<string?> DownloadFabricVersion(string workPath, string gameVersion)
        {
            string ThisName = "PanelGuide";
            try
            {
                var all = await GetFabricVersionList();
                if (all.Count == 0)
                {
                    Output.Log("未找到 Fabric 的可用版本。", 2, ThisName);
                    return null;
                }

                string gv = string.IsNullOrWhiteSpace(gameVersion) ? all[0] : gameVersion.Trim();
                if (!all.Contains(gv))
                {
                    Output.Log($"版本 {gv} 不存在或不是稳定版。", 2, ThisName);
                    return null;
                }

                string loaderVersionsJson = await _httpClient.GetStringAsync("https://meta.fabricmc.net/v2/versions/loader");
                var loaderVersions = JArray.Parse(loaderVersionsJson);
                string? latestLoader = loaderVersions.FirstOrDefault()?["version"]?.ToString();
                if (string.IsNullOrEmpty(latestLoader))
                {
                    Output.Log("无法获取 Fabric Loader 版本。", 2, ThisName);
                    return null;
                }

                string jarName = $"fabric-{gv}.jar";
                string jarPath = Path.Combine(workPath, jarName);
                if (File.Exists(jarPath))
                {
                    Output.Log($"文件已存在: {jarPath}", 1, ThisName);
                    return jarName;
                }

                string downloadUrl = $"https://meta.fabricmc.net/v2/versions/loader/{gv}/{latestLoader}/server/jar";
                bool downloaded = await DownloadServerJar(downloadUrl, jarPath, $"Fabric {gv} (loader {latestLoader})");
                return downloaded ? jarName : null;
            }
            catch (Exception ex)
            {
                Output.Log($"获取 Fabric 下载信息失败: {ex.Message}", 3, ThisName);
                return null;
            }
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
    }
}
