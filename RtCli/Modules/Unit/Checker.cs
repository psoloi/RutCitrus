using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Spectre.Console;

namespace RtCli.Modules.Unit
{
    internal class Checker
    {
        private static string? _cachedJavaResult;
        private static string? _cachedDotNetResult;
        private static string? _cachedPythonResult;

        private const string GitHubRepoApi = "https://api.github.com/repos/psoloi/RutCitrus/releases/latest";

        public static string CheckJava()
        {
            if (_cachedJavaResult != null) return _cachedJavaResult;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "java",
                    Arguments = "-version",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    return _cachedJavaResult = I18n.Get("checker_nojava");
                }

                string output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                {
                    return _cachedJavaResult = I18n.Get("checker_nojava");
                }

                string? versionLine = output.Split('\n').FirstOrDefault(line => line.Contains("version"));
                if (versionLine != null)
                {
                    int startIdx = versionLine.IndexOf("version") + "version".Length;
                    string version = versionLine.Substring(startIdx).Trim().Trim('"');
                    return _cachedJavaResult = $"{I18n.Get("checker_java")} {version}";
                }

                return _cachedJavaResult = I18n.Get("checker_nojava");
            }
            catch
            {
                return _cachedJavaResult = I18n.Get("checker_nojava");
            }
        }

        public static string CheckDotNet()
        {
            if (_cachedDotNetResult != null) return _cachedDotNetResult;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    return _cachedDotNetResult = I18n.Get("checker_nodotnet");
                }
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                {
                    return _cachedDotNetResult = I18n.Get("checker_nodotnet");
                }
                return _cachedDotNetResult = $"{I18n.Get("checker_dotnet")} {output.Trim()}";
            }
            catch
            {
                return _cachedDotNetResult = I18n.Get("checker_nodotnet");
            }
        }

        public static string CheckPython()
        {
            if (_cachedPythonResult != null) return _cachedPythonResult;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    return _cachedPythonResult = I18n.Get("checker_nopython");
                }
                string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                {
                    return _cachedPythonResult = I18n.Get("checker_nopython");
                }
                string version = output.Trim();
                return _cachedPythonResult = $"{I18n.Get("checker_python")} {version}";
            }
            catch
            {
                return _cachedPythonResult = I18n.Get("checker_nopython");
            }
        }

        private static long ExtractVersionDate()
        {
            var match = Regex.Match(Program.RtCliVersion, @"(\d{8})");
            if (match.Success && long.TryParse(match.Groups[1].Value, out long date))
            {
                return date;
            }
            return 0;
        }

        public static void CheckUpdateVersion()
        {
            var updateTask = Task.Run(async () =>
            {
                try
                {
                    using var httpClient = new HttpClient();
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "RtCli-UpdateCheck");
                    httpClient.Timeout = TimeSpan.FromSeconds(10);

                    var response = await httpClient.GetStringAsync(GitHubRepoApi);
                    var json = JObject.Parse(response);
                    var tagName = json["tag_name"]?.ToString();
                    var htmlUrl = json["html_url"]?.ToString();

                    if (string.IsNullOrEmpty(tagName))
                    {
                        Output.Log("无法获取最新版本信息", 0, "Checker");
                        return;
                    }

                    var match = Regex.Match(tagName, @"(\d{8})");
                    if (!match.Success)
                    {
                        Output.Log($"无法解析版本标签: {tagName}", 0, "Checker");
                        return;
                    }

                    long remoteDate = long.Parse(match.Groups[1].Value);
                    long localDate = ExtractVersionDate();

                    if (localDate == 0)
                    {
                        Output.Log($"无法解析当前版本号: {Program.RtCliVersion}", 0, "Checker");
                        return;
                    }

                    if (remoteDate > localDate)
                    {
                        Output.Log($"发现新版本 {match.Groups[1].Value} (当前版本: {Program.RtCliVersion})", 0, "Checker");
                        if (!string.IsNullOrEmpty(htmlUrl))
                        {
                            Output.Log($"{htmlUrl}", 0, "Checker");
                        }
                    }
                    else
                    {
                        Output.Log("当前已是最新版本", 0, "Checker");
                    }
                }
                catch (TaskCanceledException)
                {
                    Output.Log("版本检查超时", 0, "Checker");
                }
                catch (HttpRequestException ex)
                {
                    Output.Log($"版本检查网络错误: {ex.Message}", 0, "Checker");
                }
                catch (Exception ex)
                {
                    Output.Log($"版本检查失败: {ex.Message}", 0, "Checker");
                }
            });
            _ = updateTask.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    Output.Log($"版本检查内部错误: {t.Exception.InnerException?.Message}", 0, "Checker");
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        public static void CheckOS()
        {
            string os = Environment.OSVersion.Platform switch
            {
                PlatformID.Win32NT => "Windows",
                PlatformID.Unix => "Unix/Linux",
                PlatformID.MacOSX => "macOS",
                _ => "Unknown"
            };
            Output.Log($"当前系统为 {os}", 1, "Checker");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Output.Log("当前系统支持全部功能", 1, "Checker");
            }
            else 
            {
                var osNameAndVersion = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
                Output.Log($"当前 {osNameAndVersion} 可能存在兼容性问题，部分功能可能无法使用", 2, "Checker");
            }
            
        }
        public static void CheckOSBit()
        {
            bool is64Bit = Environment.Is64BitOperatingSystem;
            Output.Log($"{(is64Bit ? "当前系统为64位" : $"{I18n.Get("checker_osbit")}")}", 1, "Checker");
        }
        public static void CheckAll()
        {
            if (Config.App.CheckJava)
            {
                string javaResult = CheckJava();
                Output.Log(javaResult, 1, "Checker");
            }
            if (Config.App.CheckDotNet)
            {
                string dotNetResult = CheckDotNet();
                Output.Log(dotNetResult, 1, "Checker");
            }
            if (Config.App.CheckPython)
            {
                string pythonResult = CheckPython();
                Output.Log(pythonResult, 1, "Checker");
            }
            if (Config.App.CheckOSBit)
            {
                CheckOSBit();
            }
            if (Config.App.CheckUpdate)
            {
                CheckUpdateVersion();
            }
        }
    }
}
