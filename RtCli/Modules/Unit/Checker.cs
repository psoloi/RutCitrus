using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RtCli.Modules.Unit
{
    internal class Checker
    {
        private static string? _cachedJavaResult;
        private static string? _cachedDotNetResult;

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


        public static void CheckOS()
        {
            string os = Environment.OSVersion.Platform switch
            {
                PlatformID.Win32NT => "Windows",
                PlatformID.Unix => "Unix/Linux",
                PlatformID.MacOSX => "macOS",
                _ => "Unknown"
            };
            Output.Log($"系统为 {os}", 1, "Checker");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Output.Log("Windows支持大多数功能", 1, "Checker");
            }
            var osNameAndVersion = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
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
            if (Config.App.CheckOSBit)
            {
                CheckOSBit();
            }
        }
    }
}
