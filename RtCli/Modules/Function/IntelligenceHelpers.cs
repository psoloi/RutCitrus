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

        internal static async Task<bool> DownloadServerJar(string url, string savePath, string serverName)
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

        // 自动提示按实例隔离: 每个实例独立 Cts/游标, 多实例并发运行时互不影响
        private sealed class TipsState
        {
            public CancellationTokenSource? Cts;
            public volatile bool Running;
            public long LastSequence;
        }
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TipsState> _tipsStates =
            new(System.StringComparer.OrdinalIgnoreCase);
        private static TipsState GetTipsState(string? instanceId)
        {
            string id = string.IsNullOrWhiteSpace(instanceId) ? Config.App.CurrentServer : instanceId!;
            return _tipsStates.GetOrAdd(id, _ => new TipsState());
        }
        public static bool IsTipsRunning => _tipsStates.Values.Any(s => s.Running);
    }
}
