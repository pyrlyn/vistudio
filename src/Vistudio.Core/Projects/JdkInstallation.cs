using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Vistudio.Core.Projects
{
    public sealed class JdkInstallation
    {
        public string Home { get; }
        public string Version { get; }
        public string Vendor { get; }
        private JdkInstallation(string home, string version, string vendor)
        { Home = home; Version = version; Vendor = vendor; }
        public override string ToString() => Version + " — " + Vendor + " — " + Home;

        public static JdkInstallation Read(string home)
        {
            home = Path.GetFullPath(home);
            if (!File.Exists(Path.Combine(home, "bin", "java.exe")) || !File.Exists(Path.Combine(home, "bin", "javac.exe")))
                throw new ArgumentException("Select a JDK home containing bin\\java.exe and bin\\javac.exe (not a JRE or bin directory).");
            var release = Path.Combine(home, "release");
            if (!File.Exists(release)) throw new ArgumentException("The JDK home must contain a release file to identify its version.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(release))
            {
                var separator = line.IndexOf('=');
                if (separator > 0) values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim().Trim('"');
            }
            if (!values.TryGetValue("JAVA_VERSION", out var version) || string.IsNullOrWhiteSpace(version))
                throw new ArgumentException("The JDK release file does not declare JAVA_VERSION.");
            values.TryGetValue("IMPLEMENTOR", out var vendor);
            return new JdkInstallation(home, version, vendor ?? "Unknown vendor");
        }

        /// <summary>Bounded discovery; reads metadata only, never starts Java or downloads a JDK.</summary>
        public static JdkInstallation[] Discover(string configuredHome, Action<string> warning)
        {
            var candidates = new List<string> { configuredHome, Environment.GetEnvironmentVariable("JAVA_HOME") ?? "" };
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (var parent in new[] { Path.Combine(programFiles, "Java"), Path.Combine(programFiles, "Microsoft"),
                Path.Combine(programFiles, "Eclipse Adoptium"), Path.Combine(programFiles, "Amazon Corretto"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks") })
            {
                try { if (Directory.Exists(parent)) candidates.AddRange(Directory.EnumerateDirectories(parent)); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { warning(parent + ": " + e.Message); }
            }
            var result = new List<JdkInstallation>();
            foreach (var candidate in candidates.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(Path.Combine(candidate, "bin", "javac.exe"))) result.Add(Read(candidate));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
                { warning(candidate + ": " + e.Message); }
            }
            return result.GroupBy(j => j.Home, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(j => j.Version, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}
