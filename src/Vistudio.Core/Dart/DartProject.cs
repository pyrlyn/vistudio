using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Vistudio.Core.Dart
{
    /// <summary>A Dart package: the folder with pubspec.yaml, its name and whether it depends on the Flutter SDK.</summary>
    public sealed class DartProject
    {
        private DartProject(string root, string name, bool isFlutter)
        { Root = root; Name = name; IsFlutter = isFlutter; }

        public string Root { get; }
        public string Name { get; }
        /// <summary>True when pubspec.yaml depends on <c>sdk: flutter</c>: run, test and debug go through flutter.bat.</summary>
        public bool IsFlutter { get; }
        public string IntegrationTestFolder => Path.Combine(Root, "integration_test");
        public bool HasIntegrationTests => Directory.Exists(IntegrationTestFolder) && SafeFiles(IntegrationTestFolder, "*.dart").Any();

        private static readonly Regex NameLine = new Regex(@"^name:\s*['""]?(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex FlutterSdk = new Regex(@"^\s+sdk:\s*['""]?flutter['""]?\s*(?:#.*)?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        public static DartProject FromPubspec(string root, string pubspec)
        {
            var name = NameLine.Match(pubspec);
            return new DartProject(Path.GetFullPath(root), name.Success ? name.Groups["name"].Value : Path.GetFileName(root.TrimEnd('\\', '/')), FlutterSdk.IsMatch(pubspec));
        }

        /// <summary>The package containing <paramref name="start"/> (a file or folder), stopping at <paramref name="limit"/> when given.</summary>
        public static DartProject? Find(string start, string? limit = null)
        {
            var root = FindRoot(start, limit);
            if (root == null) return null;
            try { return FromPubspec(root, File.ReadAllText(Path.Combine(root, "pubspec.yaml"))); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public static string? FindRoot(string start, string? limit = null)
        {
            var folder = File.Exists(start) ? Path.GetDirectoryName(start) : start;
            while (!string.IsNullOrEmpty(folder))
            {
                if (File.Exists(Path.Combine(folder, "pubspec.yaml"))) return folder;
                if (limit != null && string.Equals(folder!.TrimEnd('\\', '/'), limit.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return null;
                folder = Path.GetDirectoryName(folder);
            }
            return null;
        }

        private static readonly Regex WorkspaceMember = new Regex(@"^resolution:\s*['""]?workspace['""]?\s*(?:#.*)?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        /// <summary>
        /// Why the package in <paramref name="root"/> needs <c>pub get</c>, or null when its dependencies are resolved
        /// (or the folder has no pubspec.yaml): no <c>.dart_tool\package_config.json</c>, or pubspec.yaml or pubspec.lock
        /// is newer than it. A pub workspace member (<c>resolution: workspace</c>) uses the nearest package_config.json
        /// above it, which <c>pub get</c> in any member writes for the whole workspace.
        /// </summary>
        public static string? UnresolvedReason(string root)
        {
            try
            {
                var pubspec = Path.Combine(root, "pubspec.yaml");
                if (!File.Exists(pubspec)) return null;
                var config = Path.Combine(root, ".dart_tool", "package_config.json");
                if (WorkspaceMember.IsMatch(File.ReadAllText(pubspec)))
                {
                    var folder = Path.GetDirectoryName(Path.GetFullPath(root).TrimEnd('\\', '/'));
                    while (!File.Exists(config) && !string.IsNullOrEmpty(folder))
                    {
                        config = Path.Combine(folder, ".dart_tool", "package_config.json");
                        folder = Path.GetDirectoryName(folder);
                    }
                }
                if (!File.Exists(config)) return "no .dart_tool\\package_config.json";
                var resolved = File.GetLastWriteTimeUtc(config);
                if (File.GetLastWriteTimeUtc(pubspec) > resolved) return "pubspec.yaml changed after the last pub get";
                var lockFile = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(config))!, "pubspec.lock");
                if (File.Exists(lockFile) && File.GetLastWriteTimeUtc(lockFile) > resolved) return "pubspec.lock changed after the last pub get";
                return null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException) { return null; }
        }

        public static bool IsDartFile(string? path) => path != null && path.EndsWith(".dart", StringComparison.OrdinalIgnoreCase);

        /// <summary>A unit/widget test file: <c>*_test.dart</c> under <c>test\</c>.</summary>
        public bool IsTestFile(string path) => IsDartFile(path) && path.EndsWith("_test.dart", StringComparison.OrdinalIgnoreCase) && IsUnder(path, Path.Combine(Root, "test"));

        /// <summary>A Flutter integration test: a .dart file under <c>integration_test\</c>.</summary>
        public bool IsIntegrationTest(string path) => IsDartFile(path) && IsUnder(path, IntegrationTestFolder);

        /// <summary>
        /// The program F5 / Run starts when the active document has no <c>main</c>: <c>lib\main.dart</c> for Flutter,
        /// otherwise <c>bin\&lt;name&gt;.dart</c>, <c>bin\main.dart</c> or the only file in <c>bin\</c>.
        /// </summary>
        public string? DefaultEntryPoint()
        {
            var candidates = IsFlutter
                ? new[] { Path.Combine(Root, "lib", "main.dart") }
                : new[] { Path.Combine(Root, "bin", Name + ".dart"), Path.Combine(Root, "bin", "main.dart"), Path.Combine(Root, "lib", "main.dart") };
            var found = candidates.FirstOrDefault(File.Exists);
            if (found != null || IsFlutter) return found;
            var bin = Path.Combine(Root, "bin");
            var files = Directory.Exists(bin) ? SafeFiles(bin, "*.dart").ToList() : new List<string>();
            return files.Count == 1 ? files[0] : null;
        }

        /// <summary>Path relative to the package root with forward slashes, as dart and flutter print and accept it.</summary>
        public string Relative(string path)
        {
            var full = Path.GetFullPath(path);
            var root = Root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length).Replace('\\', '/') : full;
        }

        private static bool IsUnder(string path, string folder)
        {
            var full = Path.GetFullPath(path);
            return full.StartsWith(folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> SafeFiles(string folder, string pattern)
        {
            try { return Directory.GetFiles(folder, pattern, SearchOption.AllDirectories); }
            catch (IOException) { return Array.Empty<string>(); }
            catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        }
    }
}
