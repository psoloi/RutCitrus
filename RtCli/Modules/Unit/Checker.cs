using System.Diagnostics;

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
    }
}
