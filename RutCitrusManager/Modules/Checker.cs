﻿using Newtonsoft.Json.Linq;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading.Tasks;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization;
using System.Runtime.InteropServices;

namespace RutCitrusManager.Modules
{
    internal class Checker
    {
        public static void CheckUpdate(string mode)
        {

        }

        public static void CheckWindowsVersion()
        {
            int majorVersion = Environment.OSVersion.Version.Major;
            if (majorVersion >= 10)
            {
                Output.Text_Time("[white]当前系统版本支持运行所有SDK功能[/]", 1);
            }
            else
            {
                Output.Text_Time("[yellow]当前系统版本可能不支持运行所有SDK功能！[/]", 2);
            }
        }

        #region 检查是否为虚拟机

        public static void CheckVM()
        {
            bool isVM = false;
            try
            {
                string configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config.json");
                if (!File.Exists(configFilePath))
                {
                    throw new FileNotFoundException("Config.json文件不存在");
                }

                var config = JObject.Parse(File.ReadAllText(configFilePath));
                bool checkVM = config["Check_VM"]?.Value<bool>() ?? false;

                if (checkVM)
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        isVM = CheckWindowsVM();
                    }
                    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        isVM = CheckLinuxVM();
                    }
                    else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    {
                        isVM = CheckMacVM();
                    }

                    Output.Text_Time(isVM ? "程序运行于虚拟化环境中" : "[white]程序正在运行于物理机中[/]", 1);
                }
            }
            catch (Exception ex)
            {
                Output.Text_Time("[red]检测虚拟机环境失败：[/]", 3);
                AnsiConsole.WriteException(ex);
            }
        }
        
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:验证平台兼容性", Justification = "<挂起>")]
        private static bool CheckWindowsVM()
        {
            // 方法1：检查注册表
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                var systemManufacturer = key?.GetValue("SystemManufacturer")?.ToString() ?? "";
                var productName = key?.GetValue("SystemProductName")?.ToString() ?? "";

                var vmKeywords = new[] { "vmware", "virtual", "qemu", "xen", "kvm", "hyper-v" };
                return vmKeywords.Any(k =>
                    systemManufacturer.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                    productName.Contains(k, StringComparison.OrdinalIgnoreCase));
            }
            catch { /* 忽略注册表访问错误 */ }

            return false;
        }

        private static bool CheckLinuxVM()
        {
            // 方法1：检查 /sys/class/dmi/id 下的特征文件
            try
            {
                var checkPaths = new[] {
            "/sys/class/dmi/id/product_name",
            "/sys/class/dmi/id/sys_vendor",
            "/sys/class/dmi/id/board_vendor"
        };

                var vmKeywords = new[] { "vmware", "virtual", "qemu", "xen", "kvm", "amazon" };

                foreach (var path in checkPaths.Where(File.Exists))
                {
                    var content = File.ReadAllText(path).ToLower();
                    if (vmKeywords.Any(k => content.Contains(k)))
                        return true;
                }
            }
            catch { /* 忽略文件访问错误 */ }

            // 方法2：检查 /proc/cpuinfo 的 hypervisor 标志
            try
            {
                if (File.Exists("/proc/cpuinfo"))
                {
                    var cpuInfo = File.ReadAllText("/proc/cpuinfo");
                    if (cpuInfo.Contains("hypervisor"))
                        return true;
                }
            }
            catch { /* 忽略文件访问错误 */ }

            return false;
        }

        private static bool CheckMacVM()
        {
            // 方法1：检查系统信息
            try
            {
                var process = new System.Diagnostics.Process()
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "sysctl",
                        Arguments = "-n hw.model",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                return output.ToLower().Contains("vmware") ||
                       output.ToLower().Contains("virtual");
            }
            catch { /* 忽略执行错误 */ }

            return false;
        }

        #endregion

        public static void CheckFile()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string dataDirectory = Path.Combine(baseDirectory, "Data");
            string extensionDirectory = Path.Combine(baseDirectory, "Extension");
            string configFilePath = Path.Combine(baseDirectory, "Config.json");
            string dllFilePath = Path.Combine(baseDirectory, "RutCitrusManager.dll");
            string uiSettingsFilePath = Path.Combine(baseDirectory, "UI_Settings.yaml");

            try
            {
                // 检查并创建Data文件夹
                if (!Directory.Exists(dataDirectory))
                {
                    Directory.CreateDirectory(dataDirectory);
                    Output.Text_Time("[white]Data文件夹已创建[/]", 1);
                }

                // 检查并创建Extension文件夹
                if (!Directory.Exists(extensionDirectory))
                {
                    Directory.CreateDirectory(extensionDirectory);
                    Output.Text_Time("[white]Extension文件夹已创建[/]", 1);
                }

                // 检查并创建Config.json文件
                if (!File.Exists(configFilePath))
                {
                    var config = new JObject
                    {
                        ["Check_Update"] = true,
                        ["Check_VM"] = true,
                        ["Extension"] = new JObject
                        {
                            ["Enable"] = true,
                            ["Load_Lists"] = new JArray { "a.cs", "b.exe", "c.py"  }
                        },
                        ["OnlyMode"] = "None"
                    };
                    File.WriteAllText(configFilePath, config.ToString());
                    Output.Text_Time("[white]Config配置文件已创建[/]", 1);
                }

                // 检查并创建UI_Settings.yaml文件
                if (!File.Exists(uiSettingsFilePath))
                {
                    var serializer = new SerializerBuilder()
                        .WithNamingConvention(CamelCaseNamingConvention.Instance)
                        .Build();

                    var uiSettings = new
                    {
                        rcmWindowPercent = 0.75
                    };

                    var yaml = serializer.Serialize(uiSettings);
                    yaml = "# 设定RCM的窗口大小百分比(默认0.75)\n" + yaml;

                    File.WriteAllText(uiSettingsFilePath, yaml);
                    Output.Text_Time("[white]UI_Settings配置文件已创建[/]", 1);
                }

                // 检查RutCitrusManager.dll文件
                if (!File.Exists(dllFilePath))
                {
                    Output.Text_Time("[red]核心文件不全！[/]", 3);
                    throw new FileNotFoundException("核心文件缺失");
                }

                // 检查系统dll文件
                if (!File.Exists(Path.Combine(Environment.SystemDirectory, "user32.dll")) ||
                    !File.Exists(Path.Combine(Environment.SystemDirectory, "kernel32.dll")))
                {
                    Output.Text_Time("系统DLL文件缺失！", 3);
                    throw new FileNotFoundException("系统DLL文件缺失");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex);
            }
            Output.Text_Time("[white]所有文件检查完毕[/]", 1);
        }
        // 临时代码后面将会按情况删除
        public static string CheckDirectory()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string extensionDirectory = System.IO.Path.Combine(baseDirectory, "Extension");
            string dataDirectory = System.IO.Path.Combine(baseDirectory, "Data");

            if (System.IO.Directory.Exists(extensionDirectory) && System.IO.Directory.Exists(dataDirectory))
            {
                Output.Log("文件目录存在", 1);
                return "文件目录存在";
            }
            else
            {
                Output.Log("文件目录不存在", 3);
                return "文件目录不存在";
            }
        }

        private static string CurrentDirectory => AppDomain.CurrentDomain.BaseDirectory;

        public static void CreateFile()
        {
            List<string> requiredItems = new List<string>
            {
                "Extension",
                "Extension\\example.rcp",
                "Data",
                "Data\\config.cfg"
            };

            foreach (string item in requiredItems)
            {
                string fullPath = Path.Combine(CurrentDirectory, item);

                if (File.Exists(fullPath))
                {
                    // Console.WriteLine($"文件 {item} 已存在。");
                }
                else if (Directory.Exists(fullPath))
                {
                    // Console.WriteLine($"文件夹 {item} 已存在。");
                }
                else
                {
                    try
                    {
                        if (item.EndsWith(".rcp") || item.EndsWith(".cfg"))
                        {
                            File.Create(fullPath).Dispose();
                            // Console.WriteLine($"文件 {item} 创建成功。");
                        }
                        else
                        {
                            Directory.CreateDirectory(fullPath);
                            // Console.WriteLine($"文件夹 {item} 创建成功。");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"创建 {item} 时发生错误： {ex.Message}");
                    }
                }
            }
        }
        public static void DeleteFile()
        {
            List<string> requiredItems = new List<string>
            {
                "Extension",
                "Extension\\example.rcp",
                "Data",
                "Data\\config.cfg"
            };
            foreach (string item in requiredItems)
            {
                string fullPath = Path.Combine(CurrentDirectory, item);
                if (File.Exists(fullPath))
                {
                    try
                    {
                        File.Delete(fullPath);
                        // Console.WriteLine($"文件 {item} 删除成功。");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"删除 {item} 时发生错误： {ex.Message}");
                    }
                }
                else if (Directory.Exists(fullPath))
                {
                    try
                    {
                        Directory.Delete(fullPath);
                        // Console.WriteLine($"文件夹 {item} 删除成功。");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"删除 {item} 时发生错误： {ex.Message}");
                    }
                }
                else
                {
                    // Console.WriteLine($"文件 {item} 不存在。");
                }
            }
        }
    }
}
