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

        /// <param name="buildCache">Adds --build-cache unless gradle.properties sets org.gradle.caching itself.</param>
        /// <param name="daemonRegistry">
        /// The registry of a debug session's daemon (<see cref="GradleSessionDaemon"/>): the build before a debug launch
        /// runs on it, so the launch that follows finds a warm daemon. Null for every other build: no daemon.
        /// </param>
        public static ProcessStartInfo Create(string root, string javaHome, IEnumerable<string> tasks, bool buildCache = false, string? daemonRegistry = null)
        {
            var selected = string.Join(" ", ParseTasks(string.Join(" ", tasks)));
            var cache = buildCache && Languages.BuildCache.GradleProperty(Path.GetFullPath(root), "org.gradle.caching") == null ? "--build-cache " : "";
            return daemonRegistry == null
                ? CreateCore(root, javaHome, cache + selected)
                : CreateCore(root, javaHome, SessionDaemon(daemonRegistry) + cache + selected, daemon: true);
        }

        /// <summary>
        /// Starts a debug session's daemon and configures the build in it (Gradle's <c>help</c> task), so the Gradle
        /// client the Kotlin server starts for the launch reaches the program within the server's 30 s attach limit.
        /// </summary>
        public static ProcessStartInfo CreateDaemonWarmUp(string root, string javaHome, string daemonRegistry) =>
            CreateCore(root, javaHome, SessionDaemon(daemonRegistry) + "help", daemon: true);

        private static string SessionDaemon(string daemonRegistry)
        {
            daemonRegistry = Path.GetFullPath(daemonRegistry);
            if (Regex.IsMatch(daemonRegistry, "[\"%!\r\n]")) throw new ArgumentException("The daemon registry path contains shell expansion characters.");
            return "\"-Dorg.gradle.daemon.registry.base=" + daemonRegistry + "\" -Dorg.gradle.daemon.idletimeout=" + GradleSessionDaemon.IdleTimeoutMilliseconds + " ";
        }

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

        public const string LivePreviewTask = ":vistudioComposeLive6cc1f5c0";

        /// <summary>
        /// Compiles the JVM target that owns <paramref name="sourceFile"/> and the interactive preview host, and writes
        /// the host's class path to <paramref name="manifest"/> (ComposePreview.gradle). Every save of a previewed file
        /// runs this, so like hot reload it uses a daemon of its own: registered under
        /// <paramref name="daemonRegistry"/>, gone after ten idle minutes, ended by the caller with the preview.
        /// </summary>
        public static ProcessStartInfo CreateLivePreview(string root, string javaHome, string script, string renderer, string sourceFile, string manifest, string daemonRegistry)
        {
            if (!File.Exists(renderer)) throw new FileNotFoundException("The Compose preview renderer is missing.", renderer);
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(renderer))!, "VistudioLivePreviewHost.java")))
                throw new FileNotFoundException("The Compose live preview host is missing next to the renderer.", renderer);
            script = Path.GetFullPath(script);
            if (!File.Exists(script)) throw new FileNotFoundException("Gradle init script is missing.", script);
            daemonRegistry = Path.GetFullPath(daemonRegistry);
            if (Regex.IsMatch(script, "[\"%!\r\n]") || Regex.IsMatch(daemonRegistry, "[\"%!\r\n]"))
                throw new ArgumentException("The init script or daemon registry path contains shell expansion characters.");
            var start = CreateCore(root, javaHome, "\"-Dorg.gradle.daemon.registry.base=" + daemonRegistry + "\" -Dorg.gradle.daemon.idletimeout=600000 " +
                "--no-configuration-cache --no-configure-on-demand --init-script \"" + script + "\" " + LivePreviewTask, daemon: true);
            start.EnvironmentVariables["VISTUDIO_PREVIEW_ROOT"] = Path.GetFullPath(root);
            start.EnvironmentVariables["VISTUDIO_PREVIEW_FILE"] = Path.GetFullPath(sourceFile);
            start.EnvironmentVariables["VISTUDIO_PREVIEW_RENDERER"] = Path.GetFullPath(renderer);
            start.EnvironmentVariables["VISTUDIO_PREVIEW_MANIFEST"] = Path.GetFullPath(manifest);
            return start;
        }

        /// <summary>
        /// Runs one task an init script registers: wrapper through cmd.exe, no configuration cache and no
        /// configure-on-demand (the script must see every project), no daemon left behind. Paths for the script go in
        /// environment variables, never on the command line.
        /// </summary>
        public static ProcessStartInfo CreateInitScript(string root, string javaHome, string script, string task)
        {
            script = Path.GetFullPath(script);
            if (!File.Exists(script)) throw new FileNotFoundException("Gradle init script is missing.", script);
            if (Regex.IsMatch(script, "[\"%!\r\n]"))
                throw new ArgumentException("The init script path contains shell expansion characters.");
            return CreateCore(root, javaHome, "--no-configuration-cache --no-configure-on-demand --init-script \"" + script + "\" " + task);
        }

        public const string HotReloadTask = ":vistudioHotReload6cc1f5c0";

        /// <summary>
        /// Compiles the JVM classes of the module that owns <paramref name="entryFile"/> and lists class files before and
        /// after (HotReload.gradle). Unlike every other Gradle run this one uses a daemon, because a reload has to take
        /// seconds: the daemon is registered under <paramref name="daemonRegistry"/> (so no other Gradle client shares
        /// it), exits after ten idle minutes, and the caller ends it with the debug session.
        /// </summary>
        /// <param name="hashBefore">Also hash every class file as it is before the compilation (a session's first reload).</param>
        public static ProcessStartInfo CreateHotReload(string root, string javaHome, string script, string agentSource, string entryFile, string output, string daemonRegistry, bool hashBefore = false)
        {
            if (!File.Exists(agentSource)) throw new FileNotFoundException("The hot reload agent source is missing.", agentSource);
            script = Path.GetFullPath(script);
            if (!File.Exists(script)) throw new FileNotFoundException("Gradle init script is missing.", script);
            daemonRegistry = Path.GetFullPath(daemonRegistry);
            if (Regex.IsMatch(script, "[\"%!\r\n]") || Regex.IsMatch(daemonRegistry, "[\"%!\r\n]"))
                throw new ArgumentException("The init script or daemon registry path contains shell expansion characters.");
            var start = CreateCore(root, javaHome, "\"-Dorg.gradle.daemon.registry.base=" + daemonRegistry + "\" -Dorg.gradle.daemon.idletimeout=600000 " +
                "--no-configuration-cache --no-configure-on-demand --init-script \"" + script + "\" " + HotReloadTask, daemon: true);
            start.EnvironmentVariables["VISTUDIO_HOTRELOAD_ROOT"] = Path.GetFullPath(root);
            start.EnvironmentVariables["VISTUDIO_HOTRELOAD_FILE"] = Path.GetFullPath(entryFile);
            start.EnvironmentVariables["VISTUDIO_HOTRELOAD_AGENT"] = Path.GetFullPath(agentSource);
            start.EnvironmentVariables["VISTUDIO_HOTRELOAD_OUTPUT"] = Path.GetFullPath(output);
            // Set either way: a daemon reused from the previous reload must not keep the earlier value.
            start.EnvironmentVariables["VISTUDIO_HOTRELOAD_HASH"] = hashBefore ? "1" : "0";
            return start;
        }

        private static ProcessStartInfo CreateCore(string root, string javaHome, string arguments, bool daemon = false)
        {
            root = Path.GetFullPath(root);
            if (!File.Exists(Path.Combine(root, "gradlew.bat")))
                throw new FileNotFoundException("No gradlew.bat in the Gradle root. Open the wrapper's folder or set Gradle root in Kotlin settings.");
            // ".\" keeps the wrapper resolvable when NoDefaultCurrentDirectoryInExePath is set (Windows hardening):
            // cmd.exe then skips the current directory for bare command names.
            var info = ProcessCommands.CreateRedirected(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                "/d /v:off /s /c \"\".\\gradlew.bat\" --console=plain " + (daemon ? "--daemon " : "--no-daemon ") + arguments + "\"", root);
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
