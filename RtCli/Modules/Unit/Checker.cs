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
                    return I18n.Get("checker_nojava");
                }

                string output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                {
                    return I18n.Get("checker_nojava");
                }

                string? versionLine = output.Split('\n').FirstOrDefault(line => line.Contains("version"));
                if (versionLine != null)
                {
                    int startIdx = versionLine.IndexOf("version") + "version".Length;
                    string version = versionLine.Substring(startIdx).Trim().Trim('"');
                    return $"{I18n.Get("checker_java")} {version}";
                }

                return I18n.Get("checker_nojava");
            }
            catch
            {
                return I18n.Get("checker_nojava");
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
                    return I18n.Get("checker_nodotnet");
                }
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                {
                    return I18n.Get("checker_nodotnet");
                }
                return $"{I18n.Get("checker_dotnet")} {output.Trim()}";
            }
            catch
            {
                return I18n.Get("checker_nodotnet");
            }
        }
    }
}
