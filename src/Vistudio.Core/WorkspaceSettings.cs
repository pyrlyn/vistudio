using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vistudio.Core.Dart;
using Vistudio.Core.Rust;
using Vistudio.Core.Wasm;
using Vistudio.Core.Zig;

namespace Vistudio.Core
{
    /// <summary>One parsed <c>.vs\settings.json</c>: flattened <c>vistudio.*</c> keys plus file-level problems.</summary>
    public sealed class WorkspaceSettingsFile
    {
        internal static readonly WorkspaceSettingsFile Empty = new WorkspaceSettingsFile(null, new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase), Array.Empty<string>());

        internal WorkspaceSettingsFile(string? path, Dictionary<string, JToken> values, IReadOnlyList<string> warnings)
        {
            Path = path;
            Values = values;
            Warnings = warnings;
        }

        /// <summary>The file that was read, or null when the workspace has none.</summary>
        public string? Path { get; }
        public IReadOnlyDictionary<string, JToken> Values { get; }
        public IReadOnlyList<string> Warnings { get; }
    }

    /// <summary>
    /// Workspace overrides from <c>&lt;root&gt;\.vs\settings.json</c> applied on top of the global Tools &gt; Options values.
    /// Every Visual Studio instance is its own <c>devenv</c> process, so the process-wide <see cref="CurrentRoot"/> is the
    /// workspace of exactly one window and two windows never share overrides.
    /// </summary>
    /// <remarks>
    /// Keys are <c>vistudio.kotlin.&lt;property&gt;</c>, <c>vistudio.java.&lt;property&gt;</c> (the <c>Java*</c> properties of
    /// <see cref="KotlinSettings"/>) and <c>vistudio.&lt;language&gt;.&lt;property&gt;</c> for each <see cref="StoredSettings"/> section
    /// (zig, dart, rust, wasm), written flat or as nested objects. Property names
    /// are the settings property names in any case. Precedence: global &lt; <c>.kotlinproj</c> &lt; <c>.vs\settings.json</c>.
    /// </remarks>
    public static class WorkspaceSettings
    {
        public const string Section = "vistudio.";
        public const string KotlinPrefix = "vistudio.kotlin.";
        public const string JavaPrefix = "vistudio.java.";
        public const string ZigPrefix = "vistudio.zig.";
        public const string DartPrefix = "vistudio.dart.";
        public const string RustPrefix = "vistudio.rust.";
        public const string WasmPrefix = "vistudio.wasm.";

        // Every section a workspace file may contain; other vistudio.* keys are reported as unknown.
        private static readonly string[] KnownPrefixes = { KotlinPrefix, JavaPrefix, ZigPrefix, DartPrefix, RustPrefix, WasmPrefix };

        // Path settings: variables expanded, relative values resolved against the workspace root.
        private static readonly HashSet<string> RootedPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            "KotlinSettings.LanguageServerPath", "KotlinSettings.LanguageServerJavaHome", "KotlinSettings.JavaLanguageServerPath",
            "KotlinSettings.JavaLanguageServerJavaHome", "KotlinSettings.GradleJavaHome",
            "ZigSettings.ZigPath", "ZigSettings.LibDirectory", "ZigSettings.LanguageServerPath",
            "DartSettings.FlutterSdkPath", "DartSettings.DartSdkPath",
            "RustSettings.LanguageServerPath",
            "WasmSettings.LanguageServerPath", "WasmSettings.WasmtimePath", "WasmSettings.WasmToolsPath",
        };
        // GradleRoot is already relative to the workspace (KotlinEnvironment.ResolveGradleRoot): variables only.
        private static readonly HashSet<string> ExpandedPaths = new HashSet<string>(StringComparer.Ordinal) { "KotlinSettings.GradleRoot" };
        private static readonly Regex WorkspaceVariable = new Regex(@"\$\{(workspaceFolder|workspaceRoot)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly object cacheGate = new object();
        private static readonly Dictionary<string, (DateTime Time, long Length, WorkspaceSettingsFile File)> cache =
            new Dictionary<string, (DateTime, long, WorkspaceSettingsFile)>(StringComparer.OrdinalIgnoreCase);
        private static string? currentRoot;

        /// <summary>The workspace of this Visual Studio instance (Open Folder location, project or solution folder); null without one.</summary>
        public static string? CurrentRoot
        {
            get => Volatile.Read(ref currentRoot);
            set => Volatile.Write(ref currentRoot, Normalize(value));
        }

        public static string FilePath(string root) => Path.Combine(root, ".vs", "settings.json");

        /// <summary><see cref="CurrentRoot"/> when <paramref name="folder"/> is inside it, otherwise <paramref name="folder"/> itself.</summary>
        public static string? RootFor(string? folder)
        {
            var root = CurrentRoot;
            var target = Normalize(folder);
            if (target == null) return root;
            if (root != null && (string.Equals(root, target, StringComparison.OrdinalIgnoreCase)
                || target.StartsWith(root.EndsWith(Path.DirectorySeparatorChar.ToString()) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return root;
            return target;
        }

        /// <summary>Reads <c>&lt;root&gt;\.vs\settings.json</c>, cached by write time and length.</summary>
        public static WorkspaceSettingsFile Read(string? root)
        {
            root = Normalize(root);
            if (root == null) return WorkspaceSettingsFile.Empty;
            var path = FilePath(root);
            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists) return WorkspaceSettingsFile.Empty;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException) { return WorkspaceSettingsFile.Empty; }
            lock (cacheGate)
            {
                if (cache.TryGetValue(path, out var cached) && cached.Time == info.LastWriteTimeUtc && cached.Length == info.Length) return cached.File;
            }
            var file = Parse(path, ReadText(path, out var error), error);
            lock (cacheGate) cache[path] = (info.LastWriteTimeUtc, info.Length, file);
            return file;
        }

        private static string? ReadText(string path, out string? error)
        {
            error = null;
            // Visual Studio or an editor may still hold the file while saving; retry briefly.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream))
                        return reader.ReadToEnd();
                }
                catch (IOException e) when (attempt < 3) { error = e.Message; Thread.Sleep(50); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { error = e.Message; return null; }
            }
        }

        /// <summary>Parses settings text; exposed for tests.</summary>
        public static WorkspaceSettingsFile Parse(string path, string? text, string? readError = null)
        {
            var values = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
            var warnings = new List<string>();
            if (text == null)
            {
                warnings.Add(path + ": cannot read the file (" + (readError ?? "unknown error") + "); global settings are used.");
                return new WorkspaceSettingsFile(path, values, warnings);
            }
            if (text.Trim().Length == 0) return new WorkspaceSettingsFile(path, values, warnings);
            JToken root;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                {
                    root = JToken.ReadFrom(reader, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore, DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Replace });
                    // Anything but trailing comments after the root object is an error, as in Visual Studio Code.
                    while (reader.Read())
                        if (reader.TokenType != JsonToken.Comment) throw new JsonReaderException("Unexpected content after the settings object.");
                }
            }
            catch (JsonException e)
            {
                warnings.Add(path + ": invalid JSON (" + e.Message + "); global settings are used.");
                return new WorkspaceSettingsFile(path, values, warnings);
            }
            if (!(root is JObject settings))
            {
                warnings.Add(path + ": the file must contain one JSON object; global settings are used.");
                return new WorkspaceSettingsFile(path, values, warnings);
            }
            Flatten(settings, "", values);
            foreach (var key in values.Keys)
            {
                if (!key.StartsWith(Section, StringComparison.OrdinalIgnoreCase)) continue;
                if (!KnownPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    warnings.Add(key + ": unknown section; use " + string.Join(", ", KnownPrefixes.Select(prefix => prefix + "*")) + ".");
            }
            return new WorkspaceSettingsFile(path, values, warnings);
        }

        // Objects become dotted keys so "vistudio": { "zig": { "zigPath": ... } } equals "vistudio.zig.zigPath".
        private static void Flatten(JObject source, string prefix, Dictionary<string, JToken> values)
        {
            foreach (var property in source.Properties())
            {
                var key = prefix + property.Name;
                if (property.Value is JObject nested) Flatten(nested, key + ".", values);
                else values[key] = property.Value;
            }
        }

        /// <summary>Applies the workspace overrides to <paramref name="settings"/>; returns warnings for keys that were ignored.</summary>
        public static IReadOnlyList<string> ApplyTo(KotlinSettings settings, string? root)
        {
            var file = Read(root);
            var warnings = new List<string>();
            foreach (var pair in file.Values)
            {
                if (pair.Key.StartsWith(KotlinPrefix, StringComparison.OrdinalIgnoreCase))
                    Set(settings, pair.Key.Substring(KotlinPrefix.Length), pair.Key, pair.Value, root!, warnings);
                else if (pair.Key.StartsWith(JavaPrefix, StringComparison.OrdinalIgnoreCase))
                    Set(settings, "Java" + pair.Key.Substring(JavaPrefix.Length), pair.Key, pair.Value, root!, warnings);
            }
            return warnings;
        }

        /// <summary>
        /// Applies the overrides of the settings' own section (<c>vistudio.zig.*</c>, <c>vistudio.dart.*</c>, ...) to
        /// <paramref name="settings"/>; returns warnings for keys that were ignored. Settings without a section are unchanged.
        /// </summary>
        public static IReadOnlyList<string> ApplyTo(StoredSettings settings, string? root)
        {
            var warnings = new List<string>();
            var prefix = settings.WorkspacePrefix;
            if (prefix == null) return warnings;
            foreach (var pair in Read(root).Values)
                if (pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    Set(settings, pair.Key.Substring(prefix.Length), pair.Key, pair.Value, root!, warnings);
            return warnings;
        }

        /// <summary>Every problem in the workspace file: file-level warnings plus ignored keys and values.</summary>
        public static IReadOnlyList<string> Check(string? root)
        {
            var warnings = new List<string>(Read(root).Warnings);
            warnings.AddRange(ApplyTo(new KotlinSettings(), root));
            foreach (var settings in new StoredSettings[] { new ZigSettings(), new DartSettings(), new RustSettings(), new WasmSettings() })
                warnings.AddRange(ApplyTo(settings, root));
            return warnings;
        }

        private static void Set(object target, string name, string key, JToken value, string root, List<string> warnings)
        {
            var property = name.Length == 0 ? null : target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null || !property.CanWrite || property.GetIndexParameters().Length != 0)
            {
                warnings.Add(key + ": unknown setting.");
                return;
            }
            if (!TryConvert(value, property.PropertyType, out var converted))
            {
                warnings.Add(key + ": expected " + Describe(property.PropertyType) + ".");
                return;
            }
            var id = target.GetType().Name + "." + property.Name;
            if (converted is string text && text.Length != 0)
            {
                if (RootedPaths.Contains(id)) converted = ResolvePath(text, root);
                else if (ExpandedPaths.Contains(id)) converted = ExpandVariables(text, root);
            }
            var previous = property.GetValue(target);
            property.SetValue(target, converted);
            try { Validate(target, property.Name); }
            catch (ArgumentException e)
            {
                property.SetValue(target, previous);
                warnings.Add(key + ": " + e.Message);
            }
        }

        private static bool TryConvert(JToken value, Type type, out object? converted)
        {
            converted = null;
            if (type == typeof(string))
            {
                if (value.Type == JTokenType.Null) { converted = ""; return true; }
                if (value.Type != JTokenType.String) return false;
                converted = (string?)value ?? "";
                return true;
            }
            if (type == typeof(bool))
            {
                if (value.Type != JTokenType.Boolean) return false;
                converted = (bool)value;
                return true;
            }
            if (type == typeof(int))
            {
                if (value.Type != JTokenType.Integer) return false;
                var number = (long)value;
                if (number < int.MinValue || number > int.MaxValue) return false;
                converted = (int)number;
                return true;
            }
            if (type.IsEnum)
            {
                if (value.Type != JTokenType.String) return false;
                var text = ((string?)value ?? "").Trim();
                // Names only: Enum.TryParse would also accept "1" or "A, B".
                foreach (var name in Enum.GetNames(type))
                    if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase)) { converted = Enum.Parse(type, name); return true; }
                return false;
            }
            return false;
        }

        private static string Describe(Type type)
        {
            if (type == typeof(string)) return "a string";
            if (type == typeof(bool)) return "true or false";
            if (type == typeof(int)) return "an integer";
            if (type.IsEnum) return "one of " + string.Join(", ", Enum.GetNames(type));
            return type.Name;
        }

        // Same checks as KotlinSettings.Save / ZigSettings.Save, so a workspace cannot inject what Tools > Options rejects.
        private static void Validate(object target, string property)
        {
            switch (target)
            {
                case KotlinSettings kotlin:
                    if (property == nameof(KotlinSettings.BuildTasks)) GradleCommand.ParseTasks(kotlin.BuildTasks);
                    else if (property == nameof(KotlinSettings.TestTasks)) GradleCommand.ParseTasks(kotlin.TestTasks);
                    else if (property == nameof(KotlinSettings.RunTasks)) GradleCommand.ParseTasks(kotlin.RunTasks);
                    break;
                case ZigSettings zig:
                    if (property == nameof(ZigSettings.DefaultFlags)) ZigArguments.Split(zig.DefaultFlags);
                    else if (property == nameof(ZigSettings.BuildFlags)) ZigArguments.Split(zig.BuildFlags);
                    else if (property == nameof(ZigSettings.LanguageServerArguments)) ZigArguments.Split(zig.LanguageServerArguments);
                    else if (property == nameof(ZigSettings.BuildStep) && zig.BuildStep.Length > 0) ZigArguments.ValidateStep(zig.BuildStep);
                    else if (property == nameof(ZigSettings.AstCheckDelayMilliseconds) && zig.AstCheckDelayMilliseconds < 100)
                        throw new ArgumentException("must be at least 100.");
                    break;
                case StoredSettings stored:
                    stored.Validate();
                    break;
            }
        }

        public static string ExpandVariables(string value, string root) =>
            Environment.ExpandEnvironmentVariables(WorkspaceVariable.Replace(value.Trim().Trim('"'), root.Replace("$", "$$")));

        public static string ResolvePath(string value, string root)
        {
            var expanded = ExpandVariables(value, root);
            if (expanded.Length == 0) return expanded;
            try { return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(root, expanded)); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { return expanded; }
        }

        private static string? Normalize(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            try
            {
                var full = Path.GetFullPath(folder);
                var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                // Keep a drive root as "C:\"; "C:" would mean the current folder on that drive.
                return trimmed.Length > (Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length ?? 0) ? trimmed : full;
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { return null; }
        }
    }
}
