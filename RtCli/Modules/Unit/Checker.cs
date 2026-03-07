using System.Diagnostics;

namespace RtCli.Modules.Unit
{
    internal class Checker
    {
        public static string CheckJava()
        {
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
                    return "当前设备环境没有检测到Java";
                }

                string output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                {
                    return "当前设备环境没有检测到Java";
                }

                string? versionLine = output.Split('\n').FirstOrDefault(line => line.Contains("version"));
                if (versionLine != null)
                {
                    int startIdx = versionLine.IndexOf("version") + "version".Length;
                    string version = versionLine.Substring(startIdx).Trim().Trim('"');
                    return $"检测到的Java版本: {version}";
                }

                return "当前设备环境没有检测到Java";
            }
            catch
            {
                return "当前设备环境没有检测到Java";
            }
        }
        public static string CheckDotNet()
        {
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
                    return "当前设备环境没有检测到.NET，推荐安装.NET8.0及更高版本";
                }
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                {
                    return "当前设备环境没有检测到.NET，推荐安装.NET8.0及更高版本";
                }
                return $"检测到的.NET版本: {output.Trim()}";
            }
            catch
            {
                return "当前设备环境没有检测到.NET，推荐安装.NET8.0及更高版本";
            }
        }
    }
}
