using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RutCitrusManager.Modules
{
    internal class Extension
    {
        public static async Task LoadAsync()
        {
            try
            {
                // 获取当前目录的 Config.json 路径
                var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var configPath = Path.Combine(baseDirectory, "Config.json");

                if (!File.Exists(configPath))
                {
                    Output.Text_Time("[[Extension]][red]配置文件不存在！[/]", 3);
                    return;
                }

                // 读取并解析 JSON
                var jsonText = await File.ReadAllTextAsync(configPath);
                var json = JObject.Parse(jsonText);

                // 检查 Enable 是否为 true
                var enable = json["Extension"]?["Enable"]?.Value<bool>();
                if (enable != true)
                {
                    Output.Text_Time("[[Extension]][yellow]扩展功能未启用！[/]", 2);
                    return;
                }

                // 获取 Load_Lists 中的 .exe 文件
                var loadLists = json["Extension"]?["Load_Lists"]?.ToObject<string[]>();
                var exeFiles = loadLists?
                    .Where(file => file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (exeFiles == null || !exeFiles.Any())
                {
                    Output.Text_Time("[[Extension]][white]未加载到任何插件。[/]", 1);
                    return;
                }

                // 定位到 Extension 文件夹
                var extensionPath = Path.Combine(baseDirectory, "Extension");
                if (!Directory.Exists(extensionPath))
                {
                    Output.Text_Time("[[Extension]][red]扩展目录不存在！[/]", 3);
                    return;
                }

                // 执行扩展程序
                foreach (var exe in exeFiles)
                {
                    var fullPath = Path.Combine(extensionPath, exe);

                    if (!File.Exists(fullPath))
                    {
                        Output.Text_Time($"[[Extension]][red]找不到文件：[/] [yellow]{exe}[/]", 3);
                        continue;
                    }

                    Output.Text_Time($"[[Extension]][white]正在加载插件：[/] [green]{exe}[/]", 1);

                    try
                    {
                        // 异步执行（不等待进程结束）
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = fullPath,
                            WorkingDirectory = extensionPath,  // 设置工作目录
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        Output.Text_Time($"[[Extension-{exe}]][white]加载插件失败：[/] [red]{ex.Message}[/]", 3);
                    }
                }
            }
            catch (Exception ex)
            {
                Output.Text_Time($"[[Extension]][red]加载插件时发生错误： [/]", 3);
                Output.EX(ex);

            }
        }
        public static void Load()
        {
            try
            {
                // 获取当前目录的 Config.json 路径
                var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var configPath = Path.Combine(baseDirectory, "Config.json");

                if (!File.Exists(configPath))
                {
                    Output.Text_Time("[[Extension]][red]配置文件不存在！[/]", 3);
                    return;
                }

                // 读取并解析 JSON
                var jsonText = File.ReadAllText(configPath);
                var json = JObject.Parse(jsonText);

                // 检查 Enable 是否为 true
                var enable = json["Extension"]?["Enable"]?.Value<bool>();
                if (enable != true)
                {
                    Output.Text_Time("[[Extension]][yellow]扩展功能未启用！[/]", 2);
                    return;
                }

                // 获取 Load_Lists 中的 .exe 文件
                var loadLists = json["Extension"]?["Load_Lists"]?.ToObject<string[]>();
                var exeFiles = loadLists?
                    .Where(file => file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (exeFiles == null || !exeFiles.Any())
                {
                    Output.Text_Time("[[Extension]][white]未加载到任何插件。[/]", 1);
                    return;
                }

                // 定位到 Extension 文件夹
                var extensionPath = Path.Combine(baseDirectory, "Extension");
                if (!Directory.Exists(extensionPath))
                {
                    Output.Text_Time("[[Extension]][red]扩展目录不存在！[/]", 3);
                    return;
                }

                // 执行扩展程序
                foreach (var exe in exeFiles)
                {
                    var fullPath = Path.Combine(extensionPath, exe);

                    if (!File.Exists(fullPath))
                    {
                        Output.Text_Time($"[[Extension]][red]找不到文件：[/] [yellow]{exe}[/]", 3);
                        continue;
                    }

                    Output.Text_Time($"[[Extension]][white]正在加载插件：[/] [green]{exe}[/]", 1);

                    try
                    {
                        // 异步执行（不等待进程结束）
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = fullPath,
                            WorkingDirectory = extensionPath,  // 设置工作目录
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        Output.Text_Time($"[[Extension-{exe}]][white]加载插件失败：[/] [red]{ex.Message}[/]", 3);
                    }
                }
            }
            catch (Exception ex)
            {
                Output.Text_Time($"[[Extension]][red]加载插件时发生错误： [/]", 3);
                Output.EX(ex);

            }
        }
    }
}
