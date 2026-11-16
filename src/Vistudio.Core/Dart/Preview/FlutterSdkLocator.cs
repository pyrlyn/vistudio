using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Vistudio.Core.Dart.Preview
{
    /// <summary>Where a Flutter SDK was found, in search order.</summary>
    public enum FlutterSdkSource { Setting, FlutterRootVariable, Path, Fvm, Mise, Asdf, StandardFolder }

    /// <summary>A Flutter SDK folder (contains <c>bin\flutter.bat</c>) and how it was found.</summary>
    public sealed class FlutterSdkLocation
    {
        public FlutterSdkLocation(string root, FlutterSdkSource source, string detail) { Root = root; Source = source; Detail = detail; }
        public string Root { get; }
        public FlutterSdkSource Source { get; }
        /// <summary>For the Output pane and messages, for example "PATH" or "fvm (.fvmrc: 3.50.0)".</summary>
        public string Detail { get; }
        public override string ToString() => Root + " (" + Detail + ")";
    }

    /// <summary>The result of a search: the SDK, or why none was found and what was searched.</summary>
    public sealed class FlutterSdkLookup
    {
        public FlutterSdkLookup(FlutterSdkLocation? location, string? error, IReadOnlyList<string> searched)
        { Location = location; Error = error; Searched = searched; }
        public FlutterSdkLocation? Location { get; }
        /// <summary>Set when the configured folder is invalid (the search does not fall back then) or nothing was found.</summary>
        public string? Error { get; }
        public IReadOnlyList<string> Searched { get; }
    }

    /// <summary>The process environment the locator reads; tests supply their own.</summary>
    public sealed class FlutterSdkEnvironment
    {
        public FlutterSdkEnvironment(Func<string, string?> variable, string? home, string? localAppData, string? programFiles, string? systemDrive, bool searchStandardFolders = true)
        {
            Variable = variable; Home = home; LocalAppData = localAppData; ProgramFiles = programFiles; SystemDrive = systemDrive;
            SearchStandardFolders = searchStandardFolders;
        }

        public Func<string, string?> Variable { get; }
        public string? Home { get; }
        public string? LocalAppData { get; }
        public string? ProgramFiles { get; }
        public string? SystemDrive { get; }
        public bool SearchStandardFolders { get; }

        public static FlutterSdkEnvironment Current => new FlutterSdkEnvironment(Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetEnvironmentVariable("SystemDrive") ?? "C:");

        /// <summary>Only the given PATH: no variables, version managers or standard folders (deterministic tests, PATH-only callers).</summary>
        public static FlutterSdkEnvironment PathOnly(string? path) =>
            new FlutterSdkEnvironment(name => string.Equals(name, "PATH", StringComparison.OrdinalIgnoreCase) ? path : null, null, null, null, null, searchStandardFolders: false);
    }

    /// <summary>
    /// Finds the Flutter SDK in this order: the setting (Tools &gt; Options &gt; Dart &gt; Flutter SDK folder, or the
    /// workspace's <c>vistudio.dart.flutterSdkPath</c>), <c>FLUTTER_ROOT</c>, <c>flutter.bat</c> on <c>PATH</c>, version
    /// managers (fvm, mise, asdf; project pins first), then standard install folders. A configured but invalid folder is
    /// an error and is not silently replaced by another SDK.
    /// </summary>
    public static class FlutterSdkLocator
    {
        public static FlutterSdkLookup Locate(string? configured, FlutterSdkEnvironment environment, string? projectFolder)
        {
            var searched = new List<string>();
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var root = Normalize(Expand(configured!));
                searched.Add("setting: " + configured);
                return root != null && IsSdk(root)
                    ? new FlutterSdkLookup(new FlutterSdkLocation(root, FlutterSdkSource.Setting, "Flutter SDK folder setting"), null, searched)
                    : new FlutterSdkLookup(null, "The configured Flutter SDK folder has no bin\\flutter.bat: " + configured, searched);
            }
            searched.Add("setting (empty)");
            foreach (var candidate in Candidates(environment, projectFolder, searched))
                if (IsSdk(candidate.Root)) return new FlutterSdkLookup(candidate, null, searched);
            return new FlutterSdkLookup(null, "No Flutter SDK was found. Searched: " + string.Join("; ", searched) + ".", searched);
        }

        /// <summary>True when <paramref name="root"/> holds <c>bin\flutter.bat</c>.</summary>
        public static bool IsSdk(string? root)
        {
            if (string.IsNullOrWhiteSpace(root)) return false;
            try { return Path.IsPathRooted(root) && File.Exists(Path.Combine(root, "bin", "flutter.bat")); }
            catch (ArgumentException) { return false; }
        }

        private static IEnumerable<FlutterSdkLocation> Candidates(FlutterSdkEnvironment env, string? projectFolder, List<string> searched)
        {
            var flutterRoot = env.Variable("FLUTTER_ROOT");
            searched.Add("FLUTTER_ROOT" + (string.IsNullOrWhiteSpace(flutterRoot) ? " (not set)" : ": " + flutterRoot));
            if (!string.IsNullOrWhiteSpace(flutterRoot) && Normalize(Expand(flutterRoot!)) is string fromVariable)
                yield return new FlutterSdkLocation(fromVariable, FlutterSdkSource.FlutterRootVariable, "FLUTTER_ROOT");

            searched.Add("PATH");
            foreach (var folder in PathFolders(env.Variable("PATH")))
                if (File.Exists(Path.Combine(folder, "flutter.bat")) && Normalize(Path.Combine(folder, "..")) is string onPath)
                    yield return new FlutterSdkLocation(onPath, FlutterSdkSource.Path, "PATH");

            searched.Add("fvm");
            foreach (var fvm in FvmCandidates(env, projectFolder)) yield return fvm;
            searched.Add("mise");
            foreach (var mise in ToolManagerCandidates(env, projectFolder, FlutterSdkSource.Mise)) yield return mise;
            searched.Add("asdf");
            foreach (var asdf in ToolManagerCandidates(env, projectFolder, FlutterSdkSource.Asdf)) yield return asdf;

            if (!env.SearchStandardFolders) yield break;
            searched.Add("standard folders");
            foreach (var folder in StandardFolders(env))
                if (Normalize(folder) is string standard) yield return new FlutterSdkLocation(standard, FlutterSdkSource.StandardFolder, "standard folder");
        }

        /// <summary>Common install locations on Windows: manual unzip folders from the Flutter docs, Scoop and Chocolatey.</summary>
        public static IEnumerable<string> StandardFolders(FlutterSdkEnvironment env)
        {
            var drive = string.IsNullOrEmpty(env.SystemDrive) ? null : env.SystemDrive!.TrimEnd('\\') + "\\";
            if (drive != null)
            {
                yield return Path.Combine(drive, "src", "flutter");
                yield return Path.Combine(drive, "flutter");
                yield return Path.Combine(drive, "tools", "flutter");
                yield return Path.Combine(drive, "dev", "flutter");
                yield return Path.Combine(drive, "ProgramData", "chocolatey", "lib", "flutter", "tools", "flutter");
            }
            if (!string.IsNullOrEmpty(env.Home))
            {
                yield return Path.Combine(env.Home, "flutter");
                yield return Path.Combine(env.Home, "development", "flutter");
                yield return Path.Combine(env.Home, "dev", "flutter");
                yield return Path.Combine(env.Home, "tools", "flutter");
                yield return Path.Combine(env.Home, "src", "flutter");
                yield return Path.Combine(env.Home, "scoop", "apps", "flutter", "current");
            }
            if (!string.IsNullOrEmpty(env.LocalAppData)) yield return Path.Combine(env.LocalAppData, "flutter");
            if (!string.IsNullOrEmpty(env.ProgramFiles)) yield return Path.Combine(env.ProgramFiles, "flutter");
        }

        // fvm 3: <project>\.fvmrc {"flutter": "3.50.0"} and the .fvm\flutter_sdk link; fvm 2: .fvm\fvm_config.json
        // {"flutterSdkVersion": ...}. Cache: FVM_CACHE_PATH (or the older FVM_HOME), else %LOCALAPPDATA%\fvm, else ~\fvm.
        private static IEnumerable<FlutterSdkLocation> FvmCandidates(FlutterSdkEnvironment env, string? projectFolder)
        {
            var caches = new List<string>();
            foreach (var variable in new[] { "FVM_CACHE_PATH", "FVM_HOME" })
                if (!string.IsNullOrWhiteSpace(env.Variable(variable))) caches.Add(Expand(env.Variable(variable)!));
            if (!string.IsNullOrEmpty(env.LocalAppData)) caches.Add(Path.Combine(env.LocalAppData, "fvm"));
            if (!string.IsNullOrEmpty(env.Home)) caches.Add(Path.Combine(env.Home, "fvm"));

            foreach (var folder in Ancestors(projectFolder))
            {
                var link = Path.Combine(folder, ".fvm", "flutter_sdk");
                if (Normalize(link) is string linked) yield return new FlutterSdkLocation(linked, FlutterSdkSource.Fvm, "fvm (" + Path.Combine(folder, ".fvm", "flutter_sdk") + ")");
                var version = FvmPinnedVersion(folder);
                if (version == null) continue;
                foreach (var cache in caches)
                    if (Normalize(Path.Combine(cache, "versions", version)) is string pinned)
                        yield return new FlutterSdkLocation(pinned, FlutterSdkSource.Fvm, "fvm (project pin " + version + ")");
                break; // the nearest pin decides
            }
            foreach (var cache in caches)
                if (Normalize(Path.Combine(cache, "default")) is string global)
                    yield return new FlutterSdkLocation(global, FlutterSdkSource.Fvm, "fvm (global default)");
        }

        /// <summary>The Flutter version an fvm config in <paramref name="folder"/> pins, or null.</summary>
        public static string? FvmPinnedVersion(string folder)
        {
            foreach (var (file, key) in new[] { (Path.Combine(folder, ".fvmrc"), "flutter"), (Path.Combine(folder, ".fvm", "fvm_config.json"), "flutterSdkVersion") })
            {
                try
                {
                    if (!File.Exists(file)) continue;
                    var value = (string?)JObject.Parse(File.ReadAllText(file))[key];
                    if (!string.IsNullOrWhiteSpace(value) && IsSafeVersionFolder(value!)) return value!.Trim();
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is Newtonsoft.Json.JsonException) { }
            }
            return null;
        }

        private static readonly Regex MiseTomlFlutter = new Regex(@"^\s*['""]?flutter['""]?\s*=\s*(?:['""](?<version>[^'""]+)['""]|\[\s*['""](?<version>[^'""]+)['""])", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex ToolVersionsFlutter = new Regex(@"^\s*flutter\s+(?<version>\S+)", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        /// <summary>
        /// The Flutter version pinned for <paramref name="folder"/> by mise (<c>mise.toml</c>, <c>.mise.toml</c>,
        /// <c>.config\mise.toml</c>, <c>.tool-versions</c>) or asdf (<c>.tool-versions</c>), or null.
        /// </summary>
        public static string? ToolManagerPinnedVersion(string folder, FlutterSdkSource manager)
        {
            var files = manager == FlutterSdkSource.Mise
                ? new[] { "mise.local.toml", "mise.toml", ".mise.toml", Path.Combine(".config", "mise.toml"), Path.Combine(".mise", "config.toml"), ".tool-versions" }
                : new[] { ".tool-versions" };
            foreach (var name in files)
            {
                var file = Path.Combine(folder, name);
                try
                {
                    if (!File.Exists(file)) continue;
                    var text = File.ReadAllText(file);
                    var match = name.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) ? MiseTomlFlutter.Match(text) : ToolVersionsFlutter.Match(text);
                    if (match.Success && IsSafeVersionFolder(match.Groups["version"].Value)) return match.Groups["version"].Value.Trim();
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
            return null;
        }

        // mise: MISE_DATA_DIR or %LOCALAPPDATA%\mise, then installs\flutter\<version>; asdf: ASDF_DATA_DIR or ~\.asdf.
        // Plugin versions often carry the channel ("3.50.0-stable"), so a pin matches a folder that starts with it.
        private static IEnumerable<FlutterSdkLocation> ToolManagerCandidates(FlutterSdkEnvironment env, string? projectFolder, FlutterSdkSource manager)
        {
            var dataVariable = manager == FlutterSdkSource.Mise ? "MISE_DATA_DIR" : "ASDF_DATA_DIR";
            var data = env.Variable(dataVariable);
            string? baseFolder = !string.IsNullOrWhiteSpace(data) ? Expand(data!)
                : manager == FlutterSdkSource.Mise ? (string.IsNullOrEmpty(env.LocalAppData) ? null : Path.Combine(env.LocalAppData, "mise"))
                : (string.IsNullOrEmpty(env.Home) ? null : Path.Combine(env.Home, ".asdf"));
            if (baseFolder == null) yield break;
            var installs = Path.Combine(baseFolder, "installs", "flutter");
            string[] versions;
            try { versions = Directory.Exists(installs) ? Directory.GetDirectories(installs) : Array.Empty<string>(); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { yield break; }
            if (versions.Length == 0) yield break;
            var label = manager == FlutterSdkSource.Mise ? "mise" : "asdf";
            foreach (var folder in Ancestors(projectFolder))
            {
                var pin = ToolManagerPinnedVersion(folder, manager);
                if (pin == null) continue;
                foreach (var match in versions.Where(v => Path.GetFileName(v).Equals(pin, StringComparison.OrdinalIgnoreCase))
                    .Concat(versions.Where(v => Path.GetFileName(v).StartsWith(pin + "-", StringComparison.OrdinalIgnoreCase))))
                    if (Normalize(match) is string pinned) yield return new FlutterSdkLocation(pinned, manager, label + " (project pin " + pin + ")");
                break;
            }
            // Without a pin: the newest installed version.
            foreach (var folder in versions.OrderByDescending(v => FlutterVersion.TryParse(Path.GetFileName(v)) ?? new FlutterVersion(0, 0, 0, null)))
                if (Normalize(folder) is string newest) yield return new FlutterSdkLocation(newest, manager, label + " (installed " + Path.GetFileName(folder) + ")");
        }

        private static IEnumerable<string> Ancestors(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) yield break;
            string? current;
            try { current = Path.GetFullPath(folder); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { yield break; }
            while (!string.IsNullOrEmpty(current))
            {
                yield return current!;
                current = Path.GetDirectoryName(current);
            }
        }

        private static IEnumerable<string> PathFolders(string? path)
        {
            foreach (var entry in (path ?? "").Split(Path.PathSeparator))
            {
                var folder = entry.Trim().Trim('"');
                bool rooted;
                try { rooted = folder.Length > 0 && Path.IsPathRooted(folder); }
                catch (ArgumentException) { continue; }
                if (rooted) yield return folder;
            }
        }

        /// <summary>A version used as a folder name: no separators or <c>..</c>.</summary>
        private static bool IsSafeVersionFolder(string value)
        {
            var v = value.Trim();
            return v.Length > 0 && v.Length < 80 && v.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) < 0 && !v.Contains("..");
        }

        /// <summary>Accepts the SDK folder, its <c>bin</c> folder or <c>flutter.bat</c>; returns the full SDK path or null.</summary>
        internal static string? Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            try
            {
                var folder = value!.Trim().Trim('"');
                if (!Path.IsPathRooted(folder)) return null;
                if (File.Exists(folder)) folder = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(folder))!)!;
                else if (string.Equals(Path.GetFileName(folder.TrimEnd('\\', '/')), "bin", StringComparison.OrdinalIgnoreCase)) folder = Path.GetDirectoryName(folder.TrimEnd('\\', '/'))!;
                return Path.GetFullPath(folder).TrimEnd('\\', '/');
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException || e is IOException) { return null; }
        }

        private static string Expand(string value) => Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
    }

    /// <summary>A Flutter framework version such as <c>3.50.0</c> or <c>3.38.0-0.1.pre</c>.</summary>
    public sealed class FlutterVersion : IComparable<FlutterVersion>
    {
        public FlutterVersion(int major, int minor, int patch, string? suffix) { Major = major; Minor = minor; Patch = patch; Suffix = suffix; }
        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }
        /// <summary>Pre-release or channel part after <c>-</c>, for example <c>0.1.pre</c> or <c>stable</c>.</summary>
        public string? Suffix { get; }

        private static readonly Regex Pattern = new Regex(@"^\s*v?(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?(?:[-+](?<suffix>[0-9A-Za-z.\-+]+))?", RegexOptions.CultureInvariant);

        public static FlutterVersion? TryParse(string? text)
        {
            if (text == null) return null;
            var match = Pattern.Match(text);
            if (!match.Success) return null;
            try
            {
                return new FlutterVersion(int.Parse(match.Groups["major"].Value), int.Parse(match.Groups["minor"].Value),
                    match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0, match.Groups["suffix"].Success ? match.Groups["suffix"].Value : null);
            }
            catch (OverflowException) { return null; }
        }

        /// <summary>Numeric comparison; a pre-release (<c>-x.y.pre</c>) sorts before its release, channel names do not.</summary>
        public int CompareTo(FlutterVersion? other)
        {
            if (other == null) return 1;
            var result = Major.CompareTo(other.Major);
            if (result == 0) result = Minor.CompareTo(other.Minor);
            if (result == 0) result = Patch.CompareTo(other.Patch);
            if (result == 0) result = (IsPreRelease ? 0 : 1).CompareTo(other.IsPreRelease ? 0 : 1);
            return result;
        }

        public bool IsPreRelease => Suffix != null && Suffix.EndsWith("pre", StringComparison.OrdinalIgnoreCase);

        public override string ToString() => Major + "." + Minor + "." + Patch + (Suffix == null ? "" : "-" + Suffix);
    }

    /// <summary>Whether a Flutter SDK can run <c>flutter widget-preview start</c> as Vistudio uses it.</summary>
    public static class WidgetPreviewSupport
    {
        /// <summary>The widget previewer, <c>--machine</c> and <c>--web-server</c> exist since Flutter 3.35.</summary>
        public static readonly FlutterVersion Minimum = new FlutterVersion(3, 35, 0, null);

        /// <summary>Null when <paramref name="version"/> supports the previewer, otherwise a message with what to do.</summary>
        public static string? Check(string? version)
        {
            var parsed = FlutterVersion.TryParse(version);
            if (parsed == null)
                return "The Flutter SDK version could not be read (bin\\cache\\flutter.version.json or version). Run 'flutter --version' once in the SDK folder.";
            // Pre-releases of 3.35 already had the command; compare the numbers only.
            if (new FlutterVersion(parsed.Major, parsed.Minor, parsed.Patch, null).CompareTo(Minimum) < 0)
                return "Flutter " + parsed + " has no widget previewer. Widget previews need Flutter " + Minimum.Major + "." + Minimum.Minor + " or newer: run 'flutter upgrade' (or switch to a newer SDK), then restart the preview.";
            return null;
        }
    }
}
