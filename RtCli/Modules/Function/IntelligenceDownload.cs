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

        internal static async Task<string?> DownloadPaperMC(string workPath, string projectId, string defaultFileName)
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
    }
}
