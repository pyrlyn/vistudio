using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Vistudio.Core.Dart;

namespace Vistudio.Core.Packages
{
    /// <summary>
    /// pub (Dart and Flutter): <c>pubspec.yaml</c> / <c>pubspec.lock</c> and <c>dart pub</c>. Process start information
    /// comes from <see cref="DartCommandFactory"/> so pub sees the same environment as every other Dart command of
    /// the extension. pubspec.yaml is read with an indentation-aware reader for the dependency sections only.
    /// </summary>
    public sealed class PubPackageManager : IPackageManager
    {
        private static readonly Regex PackageName = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

        public PackageEcosystem Ecosystem => PackageEcosystem.Pub;
        public string DisplayName => "pub";
        public IReadOnlyList<string> ToolNames { get; } = new[] { "dart", "flutter" };
        public IReadOnlyList<string> ManifestNames { get; } = new[] { "pubspec.yaml" };

        public IReadOnlyList<PackageEntry> ReadManifest(string manifestPath)
        {
            string text;
            try { text = File.ReadAllText(manifestPath); }
            catch (IOException ex) { throw new PackageManagerException("Cannot read " + manifestPath + ": " + ex.Message); }
            catch (UnauthorizedAccessException ex) { throw new PackageManagerException("Cannot read " + manifestPath + ": " + ex.Message); }

            var locked = ReadLock(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, "pubspec.lock"));
            var entries = new List<PackageEntry>();
            foreach (var declared in Parse(text))
            {
                locked.TryGetValue(declared.Name, out var current);
                entries.Add(new PackageEntry(declared.Name, declared.Requested, declared.Scope, declared.Group, manifestPath, declared.Line, declared.Source, current));
            }
            return entries;
        }

        public IReadOnlyList<ProcessStartInfo> CreateOutdated(string tool, string manifestPath, IReadOnlyList<PackageEntry> declared)
        {
            // --show-all lists up-to-date direct packages too; dev dependencies are included by default. The JSON
            // report always carries transitive packages (--transitive is rejected with --json), they are filtered by name later.
            return new[] { Start(tool, manifestPath, "pub", "outdated", "--json", "--show-all") };
        }

        public IReadOnlyList<PackageEntry> ApplyOutdated(IReadOnlyList<PackageEntry> declared, IReadOnlyList<PackageToolOutput> outputs)
        {
            if (outputs.Count != 1) throw new PackageManagerException("Expected the output of one 'dart pub outdated' run.");
            var output = outputs[0];
            if (output.ExitCode != 0)
                throw new PackageManagerException("dart pub outdated failed (exit code " + output.ExitCode + "): " + FirstText(output.StandardError, output.StandardOutput));

            var start = output.StandardOutput.IndexOf('{');
            JObject report;
            try { report = JObject.Parse(start < 0 ? "" : output.StandardOutput.Substring(start)); }
            catch (Newtonsoft.Json.JsonException ex) { throw new PackageManagerException("dart pub outdated printed no JSON report: " + ex.Message); }

            var byName = new Dictionary<string, JToken>(StringComparer.Ordinal);
            if (report["packages"] is JArray packages)
                foreach (var item in packages)
                {
                    var name = (string?)item["package"];
                    if (name != null) byName[name] = item;
                }

            var result = new List<PackageEntry>();
            foreach (var entry in declared)
            {
                // path and sdk packages are not in the report; they keep what the manifest and lock file say.
                if (!byName.TryGetValue(entry.Name, out var item)) { result.Add(entry); continue; }
                result.Add(entry.WithVersions(Version(item["current"]) ?? entry.Current, Version(item["upgradable"]), Version(item["latest"])));
            }
            return result;
        }

        public PackageAction Add(string tool, string manifestPath, PackageRequest request)
        {
            if (request.Scope == PackageScope.Build) throw new PackageManagerException("pub has no build dependencies; add the package as a runtime or development dependency.");
            var name = CheckName(request.Name);
            // "@" is the descriptor separator since Dart 2.19; "dev:" selects dev_dependencies.
            var spec = (request.Scope == PackageScope.Development ? "dev:" : "") + name + (request.Version == null ? "" : "@" + request.Version);
            var arguments = new List<string> { "pub", "add", spec };
            if (request.Source != null) { arguments.Add("--hosted-url"); arguments.Add(request.Source); }
            return Action(tool, manifestPath, arguments);
        }

        public PackageAction Remove(string tool, string manifestPath, PackageEntry package) =>
            Action(tool, manifestPath, new[] { "pub", "remove", CheckName(package.Name) });

        public PackageAction Update(string tool, string manifestPath, PackageEntry? package, bool toLatest)
        {
            var arguments = new List<string> { "pub", "upgrade" };
            if (toLatest) arguments.Add("--major-versions");
            if (package != null) arguments.Add(CheckName(package.Name));
            return Action(tool, manifestPath, arguments);
        }

        private static PackageAction Action(string tool, string manifestPath, IReadOnlyList<string> arguments) =>
            new PackageAction("dart " + string.Join(" ", arguments), new[] { Start(tool, manifestPath, arguments.ToArray()) });

        private static ProcessStartInfo Start(string tool, string manifestPath, params string[] arguments)
        {
            if (!Path.IsPathRooted(tool)) throw new ArgumentException("The dart executable must be an absolute path: " + tool);
            var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
            // dart.exe inside a Flutter SDK (bin\cache\dart-sdk\bin\dart.exe) needs FLUTTER_ROOT to resolve "sdk: flutter" dependencies.
            return DartCommandFactory.Dart(new DartSdk(tool, FlutterRootOf(tool)), arguments, directory);
        }

        private static string? FlutterRootOf(string dart)
        {
            var folder = Path.GetFullPath(dart);
            for (var i = 0; i < 5 && folder != null; i++) folder = Path.GetDirectoryName(folder);
            return folder == null ? null : DartToolchain.FindFlutterRoot(folder, null);
        }

        private static string CheckName(string name)
        {
            // "dev:" and "@" are descriptor syntax, so a name may not contain them.
            if (!PackageName.IsMatch(name)) throw new PackageManagerException("Not a valid pub package name: " + name);
            return name;
        }

        private static string? Version(JToken? node) => node == null || node.Type == JTokenType.Null ? null : (string?)node["version"];

        private static string FirstText(string first, string second) => (first.Trim().Length != 0 ? first : second).Trim();

        private sealed class Declared
        {
            public string Name = "", Requested = "", Group = "";
            public PackageScope Scope;
            public int Line;
            public string? Source;
        }

        private sealed class Line
        {
            public Line(int number, int indent, string text) { Number = number; Indent = indent; Text = text; }
            public int Number { get; }
            public int Indent { get; }
            public string Text { get; }
        }

        private static List<Declared> Parse(string text)
        {
            var lines = Lines(text);
            var result = new List<Declared>();
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.Indent != 0 || !TrySplit(line.Text, out var section, out _)) continue;
                var scope = section == "dependencies" ? PackageScope.Runtime : section == "dev_dependencies" ? PackageScope.Development : (PackageScope?)null;
                if (scope == null) continue;
                var end = ChildrenEnd(lines, i, 0);
                if (end > i + 1)
                {
                    var indent = lines[i + 1].Indent;
                    for (var j = i + 1; j < end; j++)
                    {
                        if (lines[j].Indent != indent || !TrySplit(lines[j].Text, out var name, out var value)) continue;
                        var next = ChildrenEnd(lines, j, indent);
                        result.Add(Entry(name, value, lines, j, next, scope.Value, section));
                        j = next - 1;
                    }
                }
                i = end - 1;
            }
            return result;
        }

        private static Declared Entry(string name, string value, List<Line> lines, int at, int end, PackageScope scope, string group)
        {
            var declared = new Declared { Name = name, Group = group, Scope = scope, Line = lines[at].Number };
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            if (value.StartsWith("{", StringComparison.Ordinal)) ReadFlowMap(value, fields);
            else if (value.Length != 0) { declared.Requested = value; return declared; }
            else if (end > at + 1) ReadBlockMap(lines, at + 1, end, fields);
            // "any" and an empty value both mean no constraint.

            fields.TryGetValue("version", out var version);
            declared.Requested = version ?? "";
            if (fields.ContainsKey("sdk")) declared.Source = "sdk";
            else if (fields.ContainsKey("path")) declared.Source = "path";
            else if (fields.ContainsKey("git")) declared.Source = "git";
            else if (fields.TryGetValue("hosted", out var hosted)) declared.Source = fields.TryGetValue("hosted.url", out var url) ? url : hosted.Length != 0 ? hosted : null;
            return declared;
        }

        /// <summary>The <c>key: value</c> pairs below an entry; nested maps are flattened to <c>outer.inner</c> (<c>hosted.url</c>).</summary>
        private static void ReadBlockMap(List<Line> lines, int from, int to, Dictionary<string, string> fields, string prefix = "")
        {
            var indent = lines[from].Indent;
            for (var i = from; i < to; i++)
            {
                if (lines[i].Indent != indent || !TrySplit(lines[i].Text, out var key, out var value)) continue;
                fields[prefix + key] = value;
                var end = ChildrenEnd(lines, i, indent);
                if (value.Length == 0 && end > i + 1) ReadBlockMap(lines, i + 1, end, fields, prefix + key + ".");
                i = end - 1;
            }
        }

        private static void ReadFlowMap(string text, Dictionary<string, string> fields)
        {
            var body = text.Trim().TrimStart('{').TrimEnd('}');
            foreach (var part in body.Split(','))
                if (TrySplit(part.Trim(), out var key, out var value)) fields[key] = value;
        }

        /// <summary>Index after the last line indented deeper than <paramref name="indent"/> that follows line <paramref name="at"/>.</summary>
        private static int ChildrenEnd(List<Line> lines, int at, int indent)
        {
            var end = at + 1;
            while (end < lines.Count && lines[end].Indent > indent) end++;
            return end;
        }

        private static bool TrySplit(string text, out string key, out string value)
        {
            key = value = "";
            var colon = -1;
            var quote = '\0';
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c == '"' || c == '\'') { if (i == 0) quote = c; continue; }
                if (c == ':' && (i + 1 == text.Length || text[i + 1] == ' ')) { colon = i; break; }
            }
            if (colon <= 0) return false;
            key = Unquote(text.Substring(0, colon).Trim());
            value = Unquote(text.Substring(colon + 1).Trim());
            return key.Length != 0;
        }

        private static string Unquote(string value) =>
            value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[value.Length - 1] == value[0] ? value.Substring(1, value.Length - 2) : value;

        /// <summary>Non-blank, non-comment lines with their 1-based number and indentation; a trailing <c># comment</c> is cut off.</summary>
        private static List<Line> Lines(string text)
        {
            var result = new List<Line>();
            var number = 0;
            foreach (var raw in text.Split('\n'))
            {
                number++;
                var line = StripComment(raw.TrimEnd('\r'));
                var trimmed = line.TrimStart(' ');
                if (trimmed.Trim().Length == 0) continue;
                result.Add(new Line(number, line.Length - trimmed.Length, trimmed.TrimEnd()));
            }
            return result;
        }

        private static string StripComment(string line)
        {
            var quote = '\0';
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; }
                else if (c == '"' || c == '\'') quote = c;
                // A comment starts at '#' at the start of the line or after whitespace.
                else if (c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1]))) return line.Substring(0, i);
            }
            return line;
        }

        /// <summary>name to version from the <c>packages:</c> map of pubspec.lock; empty when there is no lock file.</summary>
        private static Dictionary<string, string> ReadLock(string path)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            string text;
            try { if (!File.Exists(path)) return result; text = File.ReadAllText(path); }
            catch (IOException) { return result; }
            catch (UnauthorizedAccessException) { return result; }

            var lines = Lines(text);
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Indent != 0 || !TrySplit(lines[i].Text, out var section, out _) || section != "packages") continue;
                var end = ChildrenEnd(lines, i, 0);
                if (end > i + 1)
                {
                    var indent = lines[i + 1].Indent;
                    for (var j = i + 1; j < end; j++)
                    {
                        if (lines[j].Indent != indent || !TrySplit(lines[j].Text, out var name, out _)) continue;
                        var next = ChildrenEnd(lines, j, indent);
                        // Only the entry's own "version:" counts; "description:" has nested keys of its own.
                        for (var k = j + 1; k < next; k++)
                            if (lines[k].Indent == lines[j + 1].Indent && TrySplit(lines[k].Text, out var key, out var value) && key == "version") { result[name] = value; break; }
                        j = next - 1;
                    }
                }
                break;
            }
            return result;
        }
    }
}
