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
    internal partial class Intelligence
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

        // ===== AI 任务绑定实例 =====

        /// <summary>当前 AI 执行绑定的实例标识(AsyncLocal; 任务外为 null, 表示当前实例)。
        /// 放在外层类, 供 AiAutoRunner(调度)与 AiTools(工具)共享。</summary>
        private static readonly AsyncLocal<string?> _aiTargetServerKey = new();

        /// <summary>任务是否适用于当前实例(ServerKeys 为空表示全部实例)</summary>
        internal static bool IsAiTaskApplicable(AiTask task)
        {
            if (task.ServerKeys == null || task.ServerKeys.Count == 0)
                return true;
            return task.ServerKeys.Contains(Config.App.CurrentServer);
        }

        /// <summary>任务是否适用于指定实例(用于事件携带实例的多实例场景)</summary>
        internal static bool IsAiTaskApplicableTo(AiTask task, string? serverKey)
        {
            if (task.ServerKeys == null || task.ServerKeys.Count == 0)
                return true;
            if (string.IsNullOrEmpty(serverKey))
                return IsAiTaskApplicable(task);
            return task.ServerKeys.Contains(serverKey);
        }

        /// <summary>任务绑定的实例标识: 不适用于当前实例时返回 null</summary>
        internal static string? ResolveAiTaskServerKey(AiTask task)
        {
            return IsAiTaskApplicable(task) ? Config.App.CurrentServer : null;
        }

        /// <summary>解析当前 AI 执行的目标实例标识(任务上下文外回退到当前实例)</summary>
        internal static string GetAiTargetServerKey()
        {
            string? key = _aiTargetServerKey.Value;
            if (!string.IsNullOrEmpty(key) && Config.App.ServerList.ContainsKey(key))
                return key;
            return Config.App.CurrentServer;
        }

        /// <summary>解析当前 AI 执行的目标实例配置(任务上下文外回退到当前实例)</summary>
        internal static ServerEntry? GetAiTargetServer()
        {
            if (Config.App.ServerList.TryGetValue(GetAiTargetServerKey(), out var entry))
                return entry;
            return Config.CurrentServer;
        }

        /// <summary>当前 AI 执行的目标实例工作目录(不存在则返回空字符串)</summary>
        internal static string GetAiTargetWorkPath()
        {
            string? workPath = GetAiTargetServer()?.WorkPath;
            return !string.IsNullOrWhiteSpace(workPath) && Directory.Exists(workPath) ? workPath : "";
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
    }

    /// <summary>
    /// AI记忆管理器: 将AI自动化调用后生成的记忆保存到 memory.json
    /// 每条记忆格式为: 时间(time) / 记忆主题(topic) / 记忆内容(content)
    /// </summary>
    public static class AiMemoryManager
    {
        private static readonly object _lock = new();

        /// <summary>memory.json 文件路径(位于 RtCli 数据目录)</summary>
        public static string FilePath => Path.Combine(Config.DataPath, "memory.json");

        /// <summary>一条AI记忆</summary>
        public class MemoryEntry
        {
            [JsonProperty("time")] public string Time { get; set; } = "";
            [JsonProperty("topic")] public string Topic { get; set; } = "";
            [JsonProperty("content")] public string Content { get; set; } = "";
        }

        /// <summary>记忆去重键: 主题+内容(空白归一化, 大小写不敏感)</summary>
        private static string NormalizeKey(string? topic, string? content)
        {
            static string Norm(string? s) => string.IsNullOrEmpty(s)
                ? ""
                : System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();
            return Norm(topic) + "\u0001" + Norm(content);
        }

        /// <summary>
        /// 召回最近若干条记忆, 供注入系统提示词(长期知识)。
        /// 记忆中记录的是"以前发生过/处理过什么", 若不回灌, agent 就只是一次性无状态执行。
        /// </summary>
        /// <param name="max">最多条数</param>
        /// <param name="maxChars">内容总字符上限</param>
        public static List<MemoryEntry> Recall(int max, int maxChars)
        {
            var result = new List<MemoryEntry>();
            if (max <= 0) return result;

            var all = Load();
            int chars = 0;
            for (int i = all.Count - 1; i >= 0 && result.Count < max; i--)
            {
                var e = all[i];
                if (e == null || string.IsNullOrWhiteSpace(e.Topic)) continue;
                int len = e.Content?.Length ?? 0;
                if (result.Count > 0 && chars + len > maxChars) break;
                result.Insert(0, e);
                chars += len;
            }
            return result;
        }

        /// <summary>读取全部记忆(文件不存在或解析失败时返回空列表)</summary>
        public static List<MemoryEntry> Load()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(FilePath)) return new List<MemoryEntry>();
                    string json = File.ReadAllText(FilePath, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json)) return new List<MemoryEntry>();
                    return JsonConvert.DeserializeObject<List<MemoryEntry>>(json) ?? new List<MemoryEntry>();
                }
                catch
                {
                    return new List<MemoryEntry>();
                }
            }
        }

        /// <summary>覆盖写入全部记忆(原子写, 防止损坏)</summary>
        public static void Save(List<MemoryEntry> entries)
        {
            lock (_lock)
            {
                try
                {
                    string json = JsonConvert.SerializeObject(entries, Formatting.Indented);
                    AtomicFile.WriteAllText(FilePath, json, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    Output.Log($"保存记忆失败: {ex.Message}", 3, "AiMemory");
                }
            }
        }

        /// <summary>创建一条记忆(自动截断超长内容)</summary>
        public static MemoryEntry Create(string topic, string content, int maxContentLength)
        {
            content = (content ?? "").Trim();
            if (maxContentLength > 0 && content.Length > maxContentLength)
                content = content.Substring(0, maxContentLength) + "...(已截断)";

            return new MemoryEntry
            {
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Topic = (topic ?? "").Trim(),
                Content = content
            };
        }

        /// <summary>追加记忆并裁剪到最大条数, 返回最终条数</summary>
        public static int AddRange(IEnumerable<MemoryEntry> newEntries, int maxEntries, int maxContentLength)
        {
            lock (_lock)
            {
                var list = Load();
                foreach (var e in newEntries)
                {
                    if (e == null || string.IsNullOrWhiteSpace(e.Topic)) continue;
                    if (maxContentLength > 0 && (e.Content?.Length ?? 0) > maxContentLength)
                        e.Content = e.Content!.Substring(0, maxContentLength) + "...(已截断)";

                    // 去重: 同主题+同内容(空白归一化后)不再追加, 只刷新时间戳。
                    // 否则每次任务执行都会把同一批"常见错误"再写一遍, 很快把记忆撑满并挤掉早期有效记忆。
                    string key = NormalizeKey(e.Topic, e.Content);
                    int dup = list.FindIndex(x => NormalizeKey(x.Topic, x.Content) == key);
                    if (dup >= 0)
                    {
                        list[dup].Time = e.Time;
                        continue;
                    }
                    list.Add(e);
                }

                if (maxEntries > 0 && list.Count > maxEntries)
                    list.RemoveRange(0, list.Count - maxEntries);

                Save(list);
                return list.Count;
            }
        }
    }
}
