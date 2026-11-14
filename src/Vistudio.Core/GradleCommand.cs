using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Vistudio.Core
{
    public static class GradleCommand
    {
        private static readonly Regex TaskPath = new Regex(
            @"\A:?[A-Za-z_][A-Za-z0-9_.-]*(?::[A-Za-z_][A-Za-z0-9_.-]*)*\z",
            RegexOptions.CultureInvariant);

        public static string[] ParseTasks(string value)
        {
            var tasks = (value ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tasks.Length == 0 || tasks.Any(t => !TaskPath.IsMatch(t)))
                throw new ArgumentException("Enter Gradle task paths separated by spaces, for example :app:build. Command-line options are not accepted.");
            return tasks;
        }

        public static ProcessStartInfo Create(string root, string javaHome, IEnumerable<string> tasks)
            => CreateCore(root, javaHome, string.Join(" ", ParseTasks(string.Join(" ", tasks))));

        public const string DiscoveryTask = "vistudioDiscoverTasks6cc1f5c0";

        public static ProcessStartInfo CreateDiscovery(string root, string javaHome, string script)
            => CreateInitScript(root, javaHome, script, DiscoveryTask);

        public static ProcessStartInfo CreateModelImport(string root, string javaHome, string script, string output)
        {
            var start = CreateInitScript(root, javaHome, script, ":vistudioImportModel6cc1f5c0");
            start.EnvironmentVariables["VISTUDIO_MODEL_OUTPUT"] = Path.GetFullPath(output);
            start.EnvironmentVariables["VISTUDIO_MODEL_ROOT"] = Path.GetFullPath(root);
            return start;
        }

        public const string PreviewTask = ":vistudioComposePreview6cc1f5c0";

        /// <summary>Renders the previews listed in <paramref name="request"/> for one Kotlin source file (ComposePreview.gradle).</summary>
        public static ProcessStartInfo CreatePreview(string root, string javaHome, string script, string renderer, string sourceFile, string request, string output)
        {
            if (!File.Exists(renderer)) throw new FileNotFoundException("The Compose preview renderer is missing.", renderer);
            var start = CreateInitScript(root, javaHome, script, PreviewTask);
            start.EnvironmentVariables["VISTUDIO_PREVIEW_ROOT"] = Path.GetFullPath(root);
            start.EnvironmentVariables["VISTUDIO_PREVIEW_FILE"] = Path.GetFullPath(sourceFile);
            start.EnvironmentVariables["VISTUDIO_PREVIEW_RENDERER"] = Path.GetFullPath(renderer);
            start.EnvironmentVariables["VISTUDIO_PREVIEW_REQUEST"] = Path.GetFullPath(request);
            start.EnvironmentVariables["VISTUDIO_PREVIEW_OUTPUT"] = Path.GetFullPath(output);
            return start;
        }

        private static ProcessStartInfo CreateInitScript(string root, string javaHome, string script, string task)
        {
            script = Path.GetFullPath(script);
            if (!File.Exists(script)) throw new FileNotFoundException("Gradle discovery script is missing.", script);
            if (Regex.IsMatch(script, "[\"%!\r\n]"))
                throw new ArgumentException("The discovery script path contains shell expansion characters.");
            return CreateCore(root, javaHome, "--no-configuration-cache --no-configure-on-demand --init-script \"" + script + "\" " + task);
        }

        private static ProcessStartInfo CreateCore(string root, string javaHome, string arguments)
        {
            root = Path.GetFullPath(root);
            if (!File.Exists(Path.Combine(root, "gradlew.bat")))
                throw new FileNotFoundException("No gradlew.bat in the Gradle root. Open the wrapper's folder or set Gradle root in Kotlin settings.");
            var info = ProcessCommands.CreateRedirected(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                "/d /v:off /s /c \"\"gradlew.bat\" --console=plain --no-daemon " + arguments + "\"", root);
            if (!string.IsNullOrWhiteSpace(javaHome))
            {
                javaHome = Path.GetFullPath(javaHome);
                if (!File.Exists(Path.Combine(javaHome, "bin", "java.exe")))
                    throw new DirectoryNotFoundException("The configured Gradle JAVA_HOME does not contain bin\\java.exe.");
                info.EnvironmentVariables["JAVA_HOME"] = javaHome;
            }
            return info;
        }
    }
}
