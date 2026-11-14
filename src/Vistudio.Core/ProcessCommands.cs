using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Vistudio.Core
{
    public static class ProcessCommands
    {
        public static ProcessStartInfo CreateRedirected(string executable, string arguments, string directory) =>
            new ProcessStartInfo(executable, arguments)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

        public static ProcessStartInfo LanguageServer(string executable, string arguments, string directory)
        {
            if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathRooted(executable) || !File.Exists(executable))
                throw new FileNotFoundException("Configure an existing absolute language server executable or .cmd/.bat launcher in Tools > Options > Kotlin.");
            var extension = Path.GetExtension(executable);
            if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                if (Regex.IsMatch(executable, "[\"%!\r\n]") || Regex.IsMatch(arguments, "[&|<>^%!\r\n]"))
                    throw new ArgumentException("Batch server launchers cannot contain shell expansion characters; use a direct executable for complex arguments.");
                return CreateRedirected(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    "/d /v:off /s /c \"\"" + executable + "\" " + arguments + "\"", directory);
            }
            return CreateRedirected(executable, arguments, directory);
        }
    }
}
