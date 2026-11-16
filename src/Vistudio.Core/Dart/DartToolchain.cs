using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Vistudio.Core.Dart.Preview;

namespace Vistudio.Core.Dart
{
    /// <summary>The resolved SDKs: dart.exe always, flutter.bat when a Flutter SDK is configured or on PATH.</summary>
    public sealed class DartSdk
    {
        public DartSdk(string dart, string? flutterRoot)
        { Dart = dart; FlutterRoot = flutterRoot; }

        /// <summary>Absolute path to dart.exe.</summary>
        public string Dart { get; }
        /// <summary>Flutter SDK folder, or null for a Dart-only setup.</summary>
        public string? FlutterRoot { get; }
        public string? Flutter => FlutterRoot == null ? null : Path.Combine(FlutterRoot, "bin", "flutter.bat");
        public string DartSdkRoot => Path.GetDirectoryName(Path.GetDirectoryName(Dart)!)!;

        /// <summary>flutter.bat, or a message telling the user how to configure the Flutter SDK.</summary>
        public string RequireFlutter() => Flutter ?? throw new FileNotFoundException(
            "This is a Flutter project, but no Flutter SDK was found. Set Tools > Options > Dart > Flutter SDK folder, or put flutter\\bin on PATH.");
    }

    public static class DartToolchain
    {
        /// <summary>
        /// The Dart and Flutter SDKs from the settings, else the Flutter SDK search of <see cref="FlutterSdkLocator"/>
        /// (FLUTTER_ROOT, PATH, fvm/mise/asdf, standard folders), else dart.exe on PATH; null when no dart.exe is found.
        /// </summary>
        public static DartSdk? Find(DartSettings settings) => Find(settings, FlutterSdkEnvironment.Current, null);

        /// <summary>PATH-only search (no FLUTTER_ROOT, version managers or standard folders).</summary>
        public static DartSdk? Find(DartSettings settings, string? path) => Find(settings, FlutterSdkEnvironment.PathOnly(path), null);

        /// <summary>The SDKs for a package: <paramref name="projectFolder"/> lets fvm, mise and asdf project pins choose the Flutter SDK.</summary>
        public static DartSdk? Find(DartSettings settings, FlutterSdkEnvironment environment, string? projectFolder)
        {
            var flutter = LocateFlutter(settings, environment, projectFolder).Location?.Root;
            string? dart;
            if (!string.IsNullOrWhiteSpace(settings.DartSdkPath)) dart = DartInSdk(Expand(settings.DartSdkPath));
            else if (flutter != null) dart = DartInSdk(Path.Combine(flutter, "bin", "cache", "dart-sdk"));
            else dart = FindDartOnPath(environment.Variable("PATH"));
            return dart == null ? null : new DartSdk(dart, flutter);
        }

        /// <summary>The Flutter SDK of the settings and environment, with the place it was found or why none was.</summary>
        public static FlutterSdkLookup LocateFlutter(DartSettings settings, FlutterSdkEnvironment environment, string? projectFolder) =>
            FlutterSdkLocator.Locate(settings.FlutterSdkPath, environment, projectFolder);

        /// <summary>dart.exe or a message for the user explaining how to configure the SDK.</summary>
        public static DartSdk Require(DartSettings settings)
        {
            var sdk = Find(settings);
            if (sdk != null) return sdk;
            if (!string.IsNullOrWhiteSpace(settings.DartSdkPath))
                throw new FileNotFoundException("The configured Dart SDK folder has no bin\\dart.exe: " + settings.DartSdkPath);
            if (!string.IsNullOrWhiteSpace(settings.FlutterSdkPath))
            {
                var root = FindFlutterRoot(settings.FlutterSdkPath, null);
                throw new FileNotFoundException(root == null
                    ? "The configured Flutter SDK folder has no bin\\flutter.bat: " + settings.FlutterSdkPath
                    : "The Flutter SDK has not downloaded its Dart SDK yet (bin\\cache\\dart-sdk). Run 'flutter --version' once in " + root + ".");
            }
            return sdk ?? throw new FileNotFoundException("No Dart or Flutter SDK was found (searched FLUTTER_ROOT, PATH, fvm, mise, asdf and standard folders). Set Tools > Options > Dart > Flutter SDK folder or Dart SDK folder.");
        }

        public static string? FindFlutterRoot(string? configured, string? path)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var folder = Expand(configured!);
                // Accept the SDK folder, its bin folder or flutter.bat itself.
                if (File.Exists(folder)) folder = Path.GetDirectoryName(Path.GetDirectoryName(folder)!)!;
                else if (string.Equals(Path.GetFileName(folder.TrimEnd('\\', '/')), "bin", StringComparison.OrdinalIgnoreCase)) folder = Path.GetDirectoryName(folder.TrimEnd('\\', '/'))!;
                return Path.IsPathRooted(folder) && File.Exists(Path.Combine(folder, "bin", "flutter.bat")) ? Path.GetFullPath(folder) : null;
            }
            foreach (var folder in Folders(path))
                if (File.Exists(Path.Combine(folder, "flutter.bat"))) return Path.GetFullPath(Path.Combine(folder, ".."));
            return null;
        }

        private static string? DartInSdk(string folder)
        {
            if (File.Exists(folder) && folder.EndsWith("dart.exe", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(folder);
            if (string.Equals(Path.GetFileName(folder.TrimEnd('\\', '/')), "bin", StringComparison.OrdinalIgnoreCase)) folder = Path.GetDirectoryName(folder.TrimEnd('\\', '/'))!;
            var dart = Path.Combine(folder, "bin", "dart.exe");
            return Path.IsPathRooted(dart) && File.Exists(dart) ? Path.GetFullPath(dart) : null;
        }

        private static string? FindDartOnPath(string? path)
        {
            foreach (var folder in Folders(path))
            {
                var exe = Path.Combine(folder, "dart.exe");
                if (File.Exists(exe)) return Path.GetFullPath(exe);
                // flutter\bin has dart.bat only; its real dart.exe is in the cache.
                if (File.Exists(Path.Combine(folder, "dart.bat")))
                {
                    var cached = Path.Combine(folder, "cache", "dart-sdk", "bin", "dart.exe");
                    if (File.Exists(cached)) return Path.GetFullPath(cached);
                }
            }
            return null;
        }

        private static System.Collections.Generic.IEnumerable<string> Folders(string? path)
        {
            foreach (var entry in (path ?? "").Split(Path.PathSeparator))
            {
                string folder;
                try { folder = entry.Trim().Trim('"'); if (folder.Length == 0 || !Path.IsPathRooted(folder)) continue; }
                catch (ArgumentException) { continue; }
                yield return folder;
            }
        }

        private static string Expand(string value) => Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));

        private static readonly Regex DartVersion = new Regex(@"Dart SDK version:\s*(?<version>\S+)(?:\s*\((?<channel>[^)]*)\))?", RegexOptions.CultureInvariant);

        /// <summary>The version from <c>dart --version</c> ("Dart SDK version: 3.14.0 (stable) ..."); null when absent.</summary>
        public static string? ParseDartVersion(string output)
        {
            var match = DartVersion.Match(output);
            return match.Success ? match.Groups["version"].Value : null;
        }

        /// <summary>The Dart SDK version from its <c>version</c> file (no process needed).</summary>
        public static string? ReadDartVersion(DartSdk sdk)
        {
            try
            {
                var file = Path.Combine(sdk.DartSdkRoot, "version");
                return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>The Flutter framework version from <c>bin\cache\flutter.version.json</c> or the <c>version</c> file.</summary>
        public static string? ReadFlutterVersion(string flutterRoot)
        {
            try
            {
                var json = Path.Combine(flutterRoot, "bin", "cache", "flutter.version.json");
                if (File.Exists(json))
                {
                    var version = (string?)JObject.Parse(File.ReadAllText(json))["frameworkVersion"];
                    if (!string.IsNullOrEmpty(version)) return version;
                }
                var plain = Path.Combine(flutterRoot, "version");
                return File.Exists(plain) ? File.ReadAllText(plain).Trim() : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (Newtonsoft.Json.JsonException) { return null; }
        }

        /// <summary>Runs <c>dart --version</c> and returns the version.</summary>
        public static async Task<string> ReadVersionAsync(DartSdk sdk, CancellationToken token)
        {
            var output = new StringBuilder();
            var code = await OwnedProcess.RunAsync(DartCommandFactory.Dart(sdk, new[] { "--version" }, Path.GetTempPath()),
                line => { lock (output) output.AppendLine(line); }, token).ConfigureAwait(false);
            return (code == 0 ? ParseDartVersion(output.ToString()) : null)
                ?? throw new InvalidOperationException("'dart --version' failed (exit code " + code + "): " + output.ToString().Trim());
        }
    }
}
