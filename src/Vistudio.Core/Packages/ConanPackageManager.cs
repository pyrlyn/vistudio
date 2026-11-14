using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Vistudio.Core.Packages
{
    /// <summary>
    /// Conan 2 for C and C++. <c>conanfile.txt</c> is read and edited as text (Conan has no add/remove command);
    /// <c>conanfile.py</c> is Python, so it is only read, on a best-effort basis, and never rewritten.
    /// Versions come from <c>conan graph outdated --format=json</c>.
    /// </summary>
    public sealed class ConanPackageManager : IPackageManager
    {
        private static readonly Regex SectionHeader = new Regex(@"^\s*\[(?<name>[^\]]+)\]\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex NameRule = new Regex(@"^[A-Za-z0-9_][A-Za-z0-9_+.\-]*$", RegexOptions.CultureInvariant);
        private static readonly Regex PythonAttribute = new Regex(@"^[ \t]*(?<kind>requires|tool_requires|build_requires|test_requires)[ \t]*=[ \t]*", RegexOptions.CultureInvariant | RegexOptions.Multiline);
        private static readonly Regex PythonCall = new Regex(@"\bself\.(?<kind>requires|tool_requires|build_requires|test_requires)\s*\(\s*(?<q>[""'])(?<ref>[^""'\r\n]*)\k<q>", RegexOptions.CultureInvariant);
        private static readonly Regex Literal = new Regex(@"""(?<a>[^""\r\n]*)""|'(?<b>[^'\r\n]*)'", RegexOptions.CultureInvariant);

        public PackageEcosystem Ecosystem => PackageEcosystem.Conan;

        public string DisplayName => "Conan";

        public IReadOnlyList<string> ToolNames { get; } = new[] { "conan" };

        public IReadOnlyList<string> ManifestNames { get; } = new[] { "conanfile.txt", "conanfile.py" };

        public IReadOnlyList<PackageEntry> ReadManifest(string manifestPath)
        {
            string text;
            try { text = File.ReadAllText(manifestPath); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new PackageManagerException("Cannot read " + manifestPath + ": " + e.Message);
            }
            var entries = IsPython(manifestPath) ? ReadPython(text, manifestPath) : ReadTxt(text, manifestPath);
            var locked = ReadLock(Path.Combine(Folder(manifestPath), "conan.lock"));
            if (locked.Count == 0) return entries;
            return entries.Select(e => locked.TryGetValue(e.Name, out var version) ? e.WithVersions(version, e.Compatible, e.Latest) : e).ToList();
        }

        public IReadOnlyList<ProcessStartInfo> CreateOutdated(string tool, string manifestPath, IReadOnlyList<PackageEntry> declared)
        {
            if (declared.Count == 0) return new ProcessStartInfo[0];
            var folder = Folder(manifestPath);
            // The folder, not the file: Conan then also finds a conan.lock next to the recipe.
            return new[] { Command(tool, folder, "graph", "outdated", folder, "--format=json") };
        }

        public IReadOnlyList<PackageEntry> ApplyOutdated(IReadOnlyList<PackageEntry> declared, IReadOnlyList<PackageToolOutput> outputs)
        {
            if (outputs.Count == 0) return declared;
            var output = outputs[0];
            if (output.ExitCode != 0) throw new PackageManagerException(FailureText(output));
            JObject report;
            try { report = ParseReport(output.StandardOutput); }
            catch (Exception e) when (e is Newtonsoft.Json.JsonException || e is InvalidCastException || e is InvalidOperationException)
            {
                throw new PackageManagerException("conan graph outdated returned output that is not JSON: " + e.Message);
            }
            var result = new List<PackageEntry>(declared.Count);
            foreach (var entry in declared)
            {
                if (!(report[entry.Name] is JObject item))
                {
                    // Conan leaves a package out of the report when nothing newer exists, so an exactly pinned one is at its pinned version.
                    var pinned = entry.Current == null && entry.Requested.Length > 0 && entry.Requested.IndexOfAny(new[] { '[', '*', '<', '>' }) < 0;
                    result.Add(pinned ? entry.WithVersions(entry.Requested, null, null) : entry);
                    continue;
                }
                var versions = ((item["current_versions"] as JArray) ?? new JArray()).Select(v => VersionOf((string?)v)).Where(v => v != null).Select(v => v!).ToList();
                // Several versions can be in one graph; the declared one wins, else the newest.
                var current = versions.FirstOrDefault(v => v == entry.Requested) ?? PackageVersion.Newest(versions, true) ?? entry.Current;
                var ranged = item["version_ranges"] is JArray ranges && ranges.Count > 0;
                // Conan resolves a range to the newest version inside it, so the resolved version is the compatible one.
                var compatible = ranged ? current : null;
                var latest = VersionOf((string?)(item["latest_remote"] as JObject)?["ref"]);
                result.Add(entry.WithVersions(current, compatible, latest));
            }
            return result;
        }

        public PackageAction Add(string tool, string manifestPath, PackageRequest request)
        {
            RequireText(manifestPath, "added");
            if (request.Version == null) throw new PackageManagerException("Conan needs a version: enter one such as 1.2.3 or [>=1.0 <2].");
            if (request.Source != null) throw new PackageManagerException("Conan takes packages from the remotes set up with 'conan remote'; a source cannot be given here.");
            if (!NameRule.IsMatch(request.Name)) throw new PackageManagerException("Not a Conan package name: " + request.Name);
            var section = SectionOf(request.Scope);
            var document = ConanText.Parse(ReadText(manifestPath));
            if (document.Entries().Any(e => e.Section == section && e.Name == request.Name))
                throw new PackageManagerException(request.Name + " is already listed in [" + section + "].");
            document.Insert(section, request.Name + "/" + request.Version);
            return Reinstall("conan: add " + request.Name + "/" + request.Version + " to [" + section + "], then install", tool, manifestPath, document.ToString());
        }

        public PackageAction Remove(string tool, string manifestPath, PackageEntry package)
        {
            RequireText(manifestPath, "removed");
            var document = ConanText.Parse(ReadText(manifestPath));
            var found = Find(document, package);
            document.RemoveLine(found.Index);
            return Reinstall("conan: remove " + package.Name + " from [" + found.Section + "], then install", tool, manifestPath, document.ToString());
        }

        public PackageAction Update(string tool, string manifestPath, PackageEntry? package, bool toLatest)
        {
            if (!toLatest)
            {
                var update = package == null ? "--update" : "--update=" + PackageArguments.ValidateName(package.Name);
                var folder = Folder(manifestPath);
                return new PackageAction("conan install " + folder + " " + update + " --build=missing" + ProfileHint,
                    new[] { Command(tool, folder, "install", folder, update, "--build=missing") });
            }
            if (package == null) throw new PackageManagerException("Updating every Conan package to its newest version needs the version check results of each package; update them one at a time.");
            RequireText(manifestPath, "updated");
            if (package.Latest == null) throw new PackageManagerException("The newest version of " + package.Name + " is not known yet; run the version check first.");
            var latest = PackageArguments.ValidateVersion(package.Latest);
            var document = ConanText.Parse(ReadText(manifestPath));
            var found = Find(document, package);
            document.ReplaceVersion(found.Index, latest);
            return Reinstall("conan: " + package.Name + " " + (package.Requested.Length == 0 ? "" : package.Requested + " ") + "-> " + latest + ", then install",
                tool, manifestPath, document.ToString());
        }

        private const string ProfileHint = " (a default profile is needed: run 'conan profile detect' once if conan reports it missing)";

        private static PackageAction Reinstall(string description, string tool, string manifestPath, string text)
        {
            var folder = Folder(manifestPath);
            return new PackageAction(description + ProfileHint,
                new[] { Command(tool, folder, "install", folder, "--build=missing") },
                new[] { new ManifestEdit(manifestPath, text) });
        }

        private static ProcessStartInfo Command(string tool, string folder, params string[] arguments) =>
            ProcessCommands.CreateRedirected(tool, PackageArguments.Join(arguments), folder);

        private static (int Index, string Section) Find(ConanText document, PackageEntry package)
        {
            var entries = document.Entries().Where(e => e.Name == package.Name).ToList();
            if (entries.Count == 0) throw new PackageManagerException(package.Name + " is not listed in the manifest any more; reload the packages.");
            var exact = entries.FirstOrDefault(e => e.Line == package.Line) ?? entries.FirstOrDefault(e => SectionOf(package.Scope) == e.Section) ?? entries[0];
            return (exact.Index, exact.Section);
        }

        private static void RequireText(string manifestPath, string verb)
        {
            if (IsPython(manifestPath))
                throw new PackageManagerException("Python conanfiles are edited by hand: a package cannot be " + verb + " in " + Path.GetFileName(manifestPath) + ".");
        }

        private static string ReadText(string path)
        {
            try { return File.ReadAllText(path); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new PackageManagerException("Cannot read " + path + ": " + e.Message);
            }
        }

        private static bool IsPython(string path) => path.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

        private static string Folder(string manifestPath) => Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";

        private static string SectionOf(PackageScope scope) => scope == PackageScope.Build ? "tool_requires" : scope == PackageScope.Development ? "test_requires" : "requires";

        private static PackageScope ScopeOf(string section) =>
            section == "tool_requires" || section == "build_requires" ? PackageScope.Build : section == "test_requires" ? PackageScope.Development : PackageScope.Runtime;

        private static string FailureText(PackageToolOutput output)
        {
            var errors = output.StandardError.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("ERROR", StringComparison.Ordinal)).ToList();
            if (errors.Count > 0) return string.Join(" ", errors);
            var text = output.StandardError.Trim();
            return text.Length > 0 ? text : "conan graph outdated failed with exit code " + output.ExitCode + ".";
        }

        private static JObject ParseReport(string standardOutput)
        {
            // Progress goes to stderr, but a plugin or a conf can still print before the JSON; the report is its own line.
            var line = standardOutput.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("{", StringComparison.Ordinal));
            return JObject.Parse(line ?? standardOutput.Trim());
        }

        /// <summary><c>zlib/1.2.13@user/channel#rev</c> to <c>1.2.13</c>; null when the reference has no version.</summary>
        private static string? VersionOf(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            var parsed = Reference.Parse(reference!);
            return parsed?.Version;
        }

        private static IReadOnlyList<PackageEntry> ReadTxt(string text, string path)
        {
            var result = new List<PackageEntry>();
            foreach (var item in ConanText.Parse(text).Entries())
            {
                var reference = Reference.Parse(item.Text);
                if (reference == null) continue;
                result.Add(new PackageEntry(reference.Name, reference.Version, ScopeOf(item.Section), "[" + item.Section + "]", path, item.Line));
            }
            return result;
        }

        private static IReadOnlyList<PackageEntry> ReadPython(string text, string path)
        {
            var code = BlankComments(text);
            var found = new List<(int Offset, string Kind, string Reference)>();
            foreach (Match match in PythonAttribute.Matches(code))
            {
                var start = match.Index + match.Length;
                var end = ValueEnd(code, start);
                foreach (Match literal in Literal.Matches(code.Substring(start, end - start)))
                    found.Add((start + literal.Index, match.Groups["kind"].Value, literal.Groups["a"].Success ? literal.Groups["a"].Value : literal.Groups["b"].Value));
            }
            foreach (Match match in PythonCall.Matches(code))
                found.Add((match.Index, match.Groups["kind"].Value, match.Groups["ref"].Value));
            var result = new List<PackageEntry>();
            foreach (var item in found.OrderBy(f => f.Offset))
            {
                var reference = Reference.Parse(item.Reference);
                if (reference == null) continue;
                var line = 1 + code.Take(item.Offset).Count(c => c == '\n');
                result.Add(new PackageEntry(reference.Name, reference.Version, ScopeOf(item.Kind), item.Kind, path, line));
            }
            return result;
        }

        /// <summary>End of the value after <c>requires =</c>: the closing bracket of a tuple or list, else the end of the line.</summary>
        private static int ValueEnd(string code, int start)
        {
            var i = start;
            while (i < code.Length && (code[i] == ' ' || code[i] == '\t')) i++;
            if (i < code.Length && (code[i] == '(' || code[i] == '['))
            {
                var depth = 0;
                char quote = '\0';
                for (; i < code.Length; i++)
                {
                    var c = code[i];
                    if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                    if (c == '"' || c == '\'') quote = c;
                    else if (c == '(' || c == '[') depth++;
                    else if ((c == ')' || c == ']') && --depth == 0) return i + 1;
                }
                return code.Length;
            }
            var end = code.IndexOf('\n', start);
            return end < 0 ? code.Length : end;
        }

        /// <summary>Replaces Python comments with spaces so offsets and line numbers stay; a <c>#</c> inside a string (a recipe revision) is kept.</summary>
        private static string BlankComments(string text)
        {
            var builder = new StringBuilder(text);
            char quote = '\0';
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quote != '\0')
                {
                    if (c == '\\') i++;
                    else if (c == quote || c == '\n') quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') quote = c;
                else if (c == '#')
                {
                    while (i < text.Length && text[i] != '\n' && text[i] != '\r') builder[i++] = ' ';
                    i--;
                }
            }
            return builder.ToString();
        }

        /// <summary>Versions a <c>conan.lock</c> pins, by package name (<c>requires</c> and <c>build_requires</c>).</summary>
        private static Dictionary<string, string> ReadLock(string path)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(path)) return result;
                var root = JObject.Parse(File.ReadAllText(path));
                foreach (var key in new[] { "requires", "build_requires" })
                    foreach (var token in root[key] as JArray ?? new JArray())
                    {
                        var reference = Reference.Parse((string?)token ?? "");
                        if (reference == null || reference.Version.Length == 0) continue;
                        result[reference.Name] = result.TryGetValue(reference.Name, out var known) && PackageVersion.Compare(known, reference.Version) > 0 ? known : reference.Version;
                    }
            }
            // A lock file this reader does not understand only means that no Current is shown.
            catch (Exception e) when (e is Newtonsoft.Json.JsonException || e is IOException || e is UnauthorizedAccessException || e is InvalidCastException) { }
            return result;
        }

        /// <summary><c>name/version[@user/channel][#revision][%timestamp]</c>.</summary>
        private sealed class Reference
        {
            public string Name { get; private set; } = "";
            public string Version { get; private set; } = "";

            public static Reference? Parse(string text)
            {
                var value = text.Trim();
                var slash = value.IndexOf('/');
                if (slash <= 0) return null;
                var rest = value.Substring(slash + 1);
                // A version range holds spaces, operators and commas but never @, # or %, so the first of those ends the version.
                var end = rest.IndexOfAny(new[] { '@', '#', '%' });
                var version = (end < 0 ? rest : rest.Substring(0, end)).Trim();
                return new Reference { Name = value.Substring(0, slash).Trim(), Version = version };
            }
        }

        /// <summary>A <c>conanfile.txt</c> kept as its original lines so an edit changes nothing but the touched line.</summary>
        private sealed class ConanText
        {
            private readonly List<string> _lines = new List<string>();
            private readonly List<string> _ends = new List<string>();
            private string _newline = "\r\n";

            public static ConanText Parse(string text)
            {
                var document = new ConanText();
                var position = 0;
                var first = true;
                while (position < text.Length)
                {
                    var end = text.IndexOfAny(new[] { '\r', '\n' }, position);
                    if (end < 0) { document._lines.Add(text.Substring(position)); document._ends.Add(""); break; }
                    var length = text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1;
                    document._lines.Add(text.Substring(position, end - position));
                    document._ends.Add(text.Substring(end, length));
                    if (first) { document._newline = document._ends[0]; first = false; }
                    position = end + length;
                }
                return document;
            }

            public sealed class Item
            {
                public int Index { get; set; }
                public int Line => Index + 1;
                public string Section { get; set; } = "";
                public string Text { get; set; } = "";
                public string Name => Reference.Parse(Text)?.Name ?? "";
            }

            public IEnumerable<Item> Entries()
            {
                var section = "";
                for (var i = 0; i < _lines.Count; i++)
                {
                    var trimmed = _lines[i].Trim();
                    var header = SectionHeader.Match(trimmed);
                    if (header.Success) { section = header.Groups["name"].Value.Trim(); continue; }
                    if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                    if (section == "requires" || section == "tool_requires" || section == "build_requires" || section == "test_requires")
                        yield return new Item { Index = i, Section = section, Text = trimmed };
                }
            }

            public void Insert(string section, string entry)
            {
                var header = -1;
                var last = -1;
                for (var i = 0; i < _lines.Count; i++)
                {
                    var match = SectionHeader.Match(_lines[i].Trim());
                    if (match.Success)
                    {
                        if (header >= 0) break;
                        if (match.Groups["name"].Value.Trim() == section) { header = i; last = i; }
                    }
                    else if (header >= 0 && _lines[i].Trim().Length > 0) last = i;
                }
                if (header < 0)
                {
                    EndLastLine();
                    if (_lines.Count > 0 && _lines[_lines.Count - 1].Trim().Length > 0) Append("");
                    Append("[" + section + "]");
                    Append(entry);
                    return;
                }
                if (_ends[last].Length == 0) _ends[last] = _newline;
                _lines.Insert(last + 1, entry);
                _ends.Insert(last + 1, _newline);
            }

            public void RemoveLine(int index)
            {
                _lines.RemoveAt(index);
                var end = _ends[index];
                _ends.RemoveAt(index);
                // Without a final newline the previous line becomes the last one and must not keep a stray terminator.
                if (end.Length == 0 && index > 0) _ends[index - 1] = "";
            }

            /// <summary>Keeps indentation and an <c>@user/channel</c> suffix; a pinned revision belongs to the old version and is dropped.</summary>
            public void ReplaceVersion(int index, string version)
            {
                var line = _lines[index];
                var indent = line.Substring(0, line.Length - line.TrimStart().Length);
                var parsed = Reference.Parse(line)!;
                var at = line.IndexOf('@');
                var tail = "";
                if (at >= 0)
                {
                    var end = line.IndexOfAny(new[] { '#', '%' }, at);
                    tail = (end < 0 ? line.Substring(at) : line.Substring(at, end - at)).TrimEnd();
                }
                _lines[index] = indent + parsed.Name + "/" + version + tail;
            }

            private void EndLastLine()
            {
                if (_lines.Count > 0 && _ends[_lines.Count - 1].Length == 0) _ends[_lines.Count - 1] = _newline;
            }

            private void Append(string line) { _lines.Add(line); _ends.Add(_newline); }

            public override string ToString()
            {
                var builder = new StringBuilder();
                for (var i = 0; i < _lines.Count; i++) builder.Append(_lines[i]).Append(_ends[i]);
                return builder.ToString();
            }
        }
    }
}
