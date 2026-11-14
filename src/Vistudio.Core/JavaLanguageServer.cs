using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Vistudio.Core
{
    /// <summary>
    /// Starts a user-installed Eclipse JDT Language Server for Java documents. The Kotlin server parses Java but serves no
    /// Java editing features, so <c>.java</c> completion and navigation come from this separate stdio process.
    /// </summary>
    public static class JavaLanguageServer
    {
        /// <summary>JDT LS needs a Java 21+ runtime; it imports Gradle builds with the project's own JDK.</summary>
        public static ProcessStartInfo Create(string installation, string javaHome, string workspace, string dataRoot)
        {
            if (string.IsNullOrWhiteSpace(installation) || !Path.IsPathRooted(installation) || !Directory.Exists(installation))
                throw new DirectoryNotFoundException("Set Tools > Options > Kotlin > Java language server to the folder of an extracted Eclipse JDT Language Server.");
            var launcher = FindLauncher(installation)
                ?? throw new FileNotFoundException("The Java language server folder has no plugins\\org.eclipse.equinox.launcher_*.jar.");
            var configuration = Path.Combine(installation, "config_win");
            if (!Directory.Exists(configuration)) throw new DirectoryNotFoundException("The Java language server folder has no config_win directory.");
            if (string.IsNullOrWhiteSpace(javaHome)) throw new DirectoryNotFoundException("Set a Java 21+ JDK for the Java language server.");
            var java = Path.Combine(Path.GetFullPath(javaHome), "bin", "java.exe");
            if (!File.Exists(java)) throw new FileNotFoundException("The Java language server JDK has no bin\\java.exe.", java);
            var data = DataDirectory(dataRoot, workspace);
            var arguments = string.Join(" ", new[]
            {
                "-Declipse.application=org.eclipse.jdt.ls.core.id1", "-Dosgi.bundles.defaultStartLevel=4",
                "-Declipse.product=org.eclipse.jdt.ls.core.product", "-Dosgi.checkConfiguration=true",
                Quote("-Dosgi.sharedConfiguration.area=" + configuration), "-Dosgi.sharedConfiguration.area.readOnly=true",
                "-Dosgi.configuration.cascaded=true",
                // Keep Eclipse .project/.classpath/.settings in the data directory, never in the user's folder.
                "-Djava.import.generatesMetadataFilesAtProjectRoot=false", "-Dlog.level=ALL", "-Xms256m", "-Xmx2G",
                "--add-modules=ALL-SYSTEM", "--add-opens", "java.base/java.util=ALL-UNNAMED", "--add-opens", "java.base/java.lang=ALL-UNNAMED",
                "-jar", Quote(launcher), "-data", Quote(data)
            });
            var start = ProcessCommands.CreateRedirected(java, arguments, workspace);
            start.EnvironmentVariables["JAVA_HOME"] = Path.GetFullPath(javaHome);
            return start;
        }

        public static string? FindLauncher(string installation)
        {
            var plugins = Path.Combine(installation, "plugins");
            if (!Directory.Exists(plugins)) return null;
            return Directory.EnumerateFiles(plugins, "org.eclipse.equinox.launcher_*.jar")
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }

        /// <summary>One JDT LS workspace per opened folder, outside the user's source tree.</summary>
        public static string DataDirectory(string dataRoot, string workspace)
        {
            using (var sha = SHA256.Create())
            {
                var key = Path.GetFullPath(workspace).TrimEnd('\\', '/').ToUpperInvariant();
                var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key)), 0, 8).Replace("-", "").ToLowerInvariant();
                return Path.Combine(dataRoot, hash);
            }
        }

        /// <summary>
        /// The runtime for JDT LS: the explicit setting, else the Kotlin server's JDK setting, else the JetBrains Runtime
        /// shipped next to the configured Kotlin server (Java 21+), else none.
        /// </summary>
        public static string ResolveJavaHome(string configured, string kotlinServerJavaHome, string kotlinServerPath)
        {
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
            if (!string.IsNullOrWhiteSpace(kotlinServerJavaHome)) return kotlinServerJavaHome;
            if (!string.IsNullOrWhiteSpace(kotlinServerPath) && Path.IsPathRooted(kotlinServerPath))
            {
                var bin = Path.GetDirectoryName(kotlinServerPath);
                var home = bin == null ? null : Path.GetDirectoryName(bin);
                if (home != null && File.Exists(Path.Combine(home, "jbr", "bin", "java.exe"))) return Path.Combine(home, "jbr");
            }
            return "";
        }

        /// <summary>The <c>java.import.gradle.arguments</c> value that adds Vistudio's Kotlin interop init script.</summary>
        public static string InitScriptArgument(string script) => "--init-script " + Quote(Path.GetFullPath(script));

        private static string Quote(string value) => value.IndexOf(' ') >= 0 ? "\"" + value + "\"" : value;
    }
}
