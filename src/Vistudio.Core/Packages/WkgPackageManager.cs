using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Vistudio.Core.Packages
{
    /// <summary>
    /// <see cref="IPackageManager"/> for wkg (wasm-pkg-tools). wkg has no manifest of its own for dependencies: they are
    /// the <c>ns:pkg/item@version</c> references inside the project's WIT files, <c>wkg.toml</c> only carries optional
    /// <c>[overrides]</c> and a <c>[workspace]</c> member list, and <c>wkg.lock</c> records what was resolved. wkg also
    /// has no command that lists newer versions (<c>wkg get</c> without a version downloads the newest one and
    /// <c>wkg wit update</c> ends in an unimplemented panic), so this adapter reports no update information and
    /// leaves every <see cref="PackageEntry.Compatible"/> and <see cref="PackageEntry.Latest"/> null.
    /// </summary>
    public sealed class WkgPackageManager : IPackageManager
    {
        private const string LockName = "wkg.lock";

        // ids are kebab-case words, optionally escaped with '%' when they collide with a WIT keyword.
        private const string Id = @"%?[A-Za-z][A-Za-z0-9]*(?:-[A-Za-z0-9]+)*";
        private const string SemVer = @"\d+\.\d+\.\d+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?";

        // Requiring "/item" is what tells a package reference from "import name: func()". A trailing ".{" of a use
        // list is not part of the version because pre-release and build parts need a following identifier.
        private static readonly Regex Reference = new Regex(
            @"(?<![\w%-])(?<kw>import|export|use|include)\s+(?<ns>" + Id + @"):(?<pkg>" + Id + @")/" + Id + @"(?:@(?<ver>" + SemVer + @"))?",
            RegexOptions.CultureInvariant);

        private static readonly Regex PackageDeclaration = new Regex(
            @"(?<![\w%-])package\s+(?<ns>" + Id + @"):(?<pkg>" + Id + @")(?:@" + SemVer + @")?\s*[;{]",
            RegexOptions.CultureInvariant);

        private static readonly Regex StrictVersion = new Regex("^" + SemVer + "$", RegexOptions.CultureInvariant);

        public PackageEcosystem Ecosystem => PackageEcosystem.Wkg;

        public string DisplayName => "wkg";

        public IReadOnlyList<string> ToolNames { get; } = new[] { "wkg" };

        /// <summary>
        /// <c>wkg.toml</c> is optional for wkg, so a project without one is recognised by the conventional world file
        /// that <c>wkg wit fetch</c> reads from the <c>wit</c> folder.
        /// </summary>
        public IReadOnlyList<string> ManifestNames { get; } = new[] { "wkg.toml", "world.wit" };

        public IReadOnlyList<PackageEntry> ReadManifest(string manifestPath)
        {
            var project = Locate(manifestPath);
            var current = ReadLock(Path.Combine(project.Directory, LockName));
            var entries = new List<PackageEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var file in project.WitFiles())
            {
                var text = ReadText(file);
                var blank = BlankComments(text);
                var own = new HashSet<string>(
                    PackageDeclaration.Matches(blank).Cast<Match>().Select(m => m.Groups["ns"].Value + ":" + m.Groups["pkg"].Value),
                    StringComparer.Ordinal);
                foreach (Match match in Reference.Matches(blank))
                {
                    var name = match.Groups["ns"].Value + ":" + match.Groups["pkg"].Value;
                    if (own.Contains(name) || project.OwnPackages.Contains(name) || !IsAcceptable(name, match.Groups["ver"])) continue;
                    if (!seen.Add(name)) continue;
                    var requested = match.Groups["ver"].Success ? match.Groups["ver"].Value : "";
                    project.Overrides.TryGetValue(name, out var source);
                    entries.Add(new PackageEntry(name, requested, PackageScope.Runtime, match.Groups["kw"].Value, file,
                        LineOf(text, match.Index), source, LockedVersion(current, name, requested)));
                }
            }
            return entries;
        }

        public IReadOnlyList<ProcessStartInfo> CreateOutdated(string tool, string manifestPath, IReadOnlyList<PackageEntry> declared) =>
            new ProcessStartInfo[0];

        public IReadOnlyList<PackageEntry> ApplyOutdated(IReadOnlyList<PackageEntry> declared, IReadOnlyList<PackageToolOutput> outputs) =>
            (declared ?? throw new ArgumentNullException(nameof(declared))).ToList();

        public PackageAction Add(string tool, string manifestPath, PackageRequest request) =>
            throw new PackageManagerException("wkg has no add command: a dependency is whatever the WIT files reference. Add a line such as "
                + "'import " + (request ?? throw new ArgumentNullException(nameof(request))).Name + "/<interface>"
                + (request.Version == null ? "" : "@" + request.Version) + ";' (or 'use' / 'include') to the world file, then run 'wkg wit fetch'. "
                + "The interface name cannot be guessed from the package name.");

        public PackageAction Remove(string tool, string manifestPath, PackageEntry package) =>
            throw new PackageManagerException("wkg has no remove command: delete the import/use/include of "
                + (package ?? throw new ArgumentNullException(nameof(package))).Name + " from the WIT files yourself, then run 'wkg wit fetch'. "
                + "Deleting the statements automatically could leave dangling type references.");

        public PackageAction Update(string tool, string manifestPath, PackageEntry? package, bool toLatest)
        {
            if (string.IsNullOrWhiteSpace(tool)) throw new ArgumentException("Tool path is empty.", nameof(tool));
            var project = Locate(manifestPath);
            var edits = new List<ManifestEdit>();
            string description;

            if (package != null && toLatest)
            {
                if (string.IsNullOrEmpty(package.Latest))
                    throw new PackageManagerException("wkg cannot report the newest version of " + package.Name
                        + ". Change the '@version' in the WIT files by hand, then run 'wkg wit fetch'.");
                // The text is written into WIT files, so it must be a plain version, not merely free of shell metacharacters.
                var latest = PackageArguments.ValidateVersion(package.Latest!);
                if (!StrictVersion.IsMatch(latest)) throw new PackageManagerException("'" + latest + "' is not a semantic version that WIT accepts after '@'.");
                var name = PackageArguments.ValidateName(package.Name);
                foreach (var file in project.WitFiles())
                {
                    var text = ReadText(file);
                    var rewritten = WithVersion(text, name, latest);
                    if (rewritten != text) edits.Add(new ManifestEdit(file, rewritten));
                }
                description = "Set " + name + " to @" + latest + " in " + edits.Count + " WIT file(s), then wkg wit fetch";
            }
            else
            {
                // Versions in WIT are exact ('=1.2.3' in wkg.lock) and wkg has no command that moves them: 'wkg wit fetch'
                // only resolves what the lock does not cover, and 'wkg wit update' is not used because wkg 0.16.2 panics after running it.
                description = package == null
                    ? "wkg wit fetch (fetches dependencies not yet in wkg.lock; wkg has no command that raises versions)"
                    : "wkg wit fetch for " + package.Name + " (wkg has no command that raises versions)";
            }

            var arguments = new List<string> { "wit", "fetch" };
            if (project.FetchDirectory != null) arguments.Add(project.FetchDirectory);
            var start = ProcessCommands.CreateRedirected(tool, PackageArguments.Join(arguments), project.Directory);
            return new PackageAction(description, new[] { start }, edits);
        }

        private static string WithVersion(string text, string name, string version)
        {
            var blank = BlankComments(text);
            var result = new StringBuilder();
            var position = 0;
            foreach (Match match in Reference.Matches(blank))
            {
                var ver = match.Groups["ver"];
                if (!ver.Success || match.Groups["ns"].Value + ":" + match.Groups["pkg"].Value != name) continue;
                result.Append(text, position, ver.Index - position).Append(version);
                position = ver.Index + ver.Length;
            }
            return result.Append(text, position, text.Length - position).ToString();
        }

        private static bool IsAcceptable(string name, Group version)
        {
            try
            {
                PackageArguments.ValidateName(name);
                if (version.Success) PackageArguments.ValidateVersion(version.Value);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// Comments become spaces (newlines stay) so offsets and line numbers of the original text still apply.
        /// WIT block comments nest.
        /// </summary>
        private static string BlankComments(string text)
        {
            var result = new StringBuilder(text.Length);
            var depth = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                var next = i + 1 < text.Length ? text[i + 1] : '\0';
                if (depth == 0 && c == '/' && next == '/')
                {
                    while (i < text.Length && text[i] != '\n' && text[i] != '\r') { result.Append(' '); i++; }
                    i--;
                }
                else if (c == '/' && next == '*') { depth++; result.Append("  "); i++; }
                else if (depth > 0 && c == '*' && next == '/') { depth--; result.Append("  "); i++; }
                else result.Append(depth > 0 && c != '\n' && c != '\r' ? ' ' : c);
            }
            return result.ToString();
        }

        private static int LineOf(string text, int index)
        {
            var line = 1;
            for (var i = 0; i < index; i++) if (text[i] == '\n') line++;
            return line;
        }

        private static string ReadText(string path)
        {
            try { return File.ReadAllText(path); }
            catch (IOException ex) { throw new PackageManagerException("Cannot read " + path + ": " + ex.Message); }
            catch (UnauthorizedAccessException ex) { throw new PackageManagerException("Cannot read " + path + ": " + ex.Message); }
        }

        // ---- project layout ------------------------------------------------------------------------------

        private sealed class Project
        {
            public string Directory = "";
            public List<string> WitDirectories = new List<string>();
            public bool Workspace;
            public HashSet<string> OwnPackages = new HashSet<string>(StringComparer.Ordinal);
            public Dictionary<string, string> Overrides = new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>The directory argument for <c>wkg wit fetch</c>; null when wkg's default (<c>wit</c>, or the workspace members) is right.</summary>
            public string? FetchDirectory
            {
                get
                {
                    if (Workspace || WitDirectories.Count != 1) return null;
                    var dir = WitDirectories[0];
                    var prefix = Directory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                    var relative = dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? dir.Substring(prefix.Length) : dir;
                    if (string.Equals(relative, "wit", StringComparison.OrdinalIgnoreCase)) return null;
                    // A relative path that starts with '-' would be read as an option.
                    return relative.StartsWith("-", StringComparison.Ordinal) ? ".\\" + relative : relative;
                }
            }

            /// <summary>Only the top level of a WIT folder: <c>deps/</c> holds what wkg fetched, not what the project declares.</summary>
            public IEnumerable<string> WitFiles()
            {
                foreach (var dir in WitDirectories)
                {
                    if (!System.IO.Directory.Exists(dir)) continue;
                    var files = System.IO.Directory.GetFiles(dir, "*.wit", SearchOption.TopDirectoryOnly);
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    foreach (var file in files) yield return file;
                }
            }
        }

        private static Project Locate(string manifestPath)
        {
            if (string.IsNullOrWhiteSpace(manifestPath)) throw new ArgumentException("Manifest path is empty.", nameof(manifestPath));
            var full = Path.GetFullPath(manifestPath);
            if (!File.Exists(full)) throw new PackageManagerException("File not found: " + full);
            var dir = Path.GetDirectoryName(full) ?? "";
            var project = new Project();

            if (string.Equals(Path.GetExtension(full), ".wit", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(dir);
                project.Directory = string.Equals(Path.GetFileName(dir), "wit", StringComparison.OrdinalIgnoreCase) && parent != null ? parent : dir;
                project.WitDirectories.Add(dir);
                ReadToml(project, Path.Combine(project.Directory, "wkg.toml"), false);
                return project;
            }

            project.Directory = dir;
            ReadToml(project, Path.Combine(dir, "wkg.toml"), true);
            if (project.WitDirectories.Count == 0 && !project.Workspace)
            {
                var wit = Path.Combine(dir, "wit");
                if (Directory.Exists(wit)) project.WitDirectories.Add(wit);
            }
            return project;
        }

        private static void ReadToml(Project project, string path, bool members)
        {
            if (!File.Exists(path)) return;
            foreach (var line in Toml.Read(ReadText(path)))
            {
                if (line.Key == null) continue;
                if (line.Table == "overrides")
                {
                    var match = Regex.Match(line.Value, "\\bpath\\s*=\\s*(\"(?:[^\"\\\\]|\\\\.)*\"|'[^']*')");
                    var value = match.Success ? Toml.Strings(match.Groups[1].Value).FirstOrDefault() : null;
                    if (value != null) project.Overrides[line.Key] = value;
                }
                else if (line.Table.StartsWith("overrides.", StringComparison.Ordinal) && line.Key == "path")
                {
                    var value = Toml.Strings(line.Value).FirstOrDefault();
                    if (value != null) project.Overrides[Toml.SplitDotted(line.Table).Last()] = value;
                }
                else if (members && line.Table == "workspace" && line.Key == "members")
                {
                    project.Workspace = true;
                    foreach (var member in Toml.Strings(line.Value))
                    {
                        if (member.IndexOfAny(new[] { '*', '?', '[', '{' }) >= 0)
                            throw new PackageManagerException("Glob patterns in [workspace] members are not supported by this reader: " + member
                                + ". List the WIT folders explicitly.");
                        project.WitDirectories.Add(Path.GetFullPath(Path.Combine(project.Directory, member)));
                    }
                }
            }
        }

        // ---- wkg.lock --------------------------------------------------------------------------------------

        private sealed class LockedPackage
        {
            public string Name = "";
            public List<KeyValuePair<string, string>> Versions = new List<KeyValuePair<string, string>>();
        }

        // A lock file that cannot be read only costs the Current column, never the dependency list.
        private static List<LockedPackage> ReadLock(string path)
        {
            var packages = new List<LockedPackage>();
            try
            {
                if (!File.Exists(path)) return packages;
                LockedPackage? package = null;
                string requirement = "", version = "";
                var inVersion = false;
                void Close()
                {
                    if (inVersion && package != null && version.Length != 0) package.Versions.Add(new KeyValuePair<string, string>(requirement, version));
                    requirement = version = "";
                    inVersion = false;
                }
                foreach (var line in Toml.Read(File.ReadAllText(path)))
                {
                    if (line.Key == null)
                    {
                        Close();
                        var table = string.Join(".", Toml.SplitDotted(line.Table));
                        if (!line.IsArray) { package = null; continue; }
                        if (table == "packages" || table == "package") { package = new LockedPackage(); packages.Add(package); }
                        else if (package != null && (table == "packages.versions" || table == "packages.version"
                            || table == "package.versions" || table == "package.version")) inVersion = true;
                        continue;
                    }
                    var value = Toml.Strings(line.Value).FirstOrDefault() ?? "";
                    if (inVersion) { if (line.Key == "requirement") requirement = value; else if (line.Key == "version") version = value; }
                    else if (package != null && line.Key == "name") package.Name = value;
                }
                Close();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is PackageManagerException)
            {
                return new List<LockedPackage>();
            }
            return packages;
        }

        // wkg writes the requirement for '@1.2.3' as '=1.2.3' and for a reference without a version as '*'; a lock entry made
        // for a different requirement says nothing about the text in the WIT files now.
        private static string? LockedVersion(List<LockedPackage> packages, string name, string requested)
        {
            foreach (var package in packages.Where(p => p.Name == name))
                foreach (var pair in package.Versions)
                {
                    var requirement = pair.Key.Trim();
                    var matches = requested.Length == 0
                        ? requirement == "*"
                        : requirement.StartsWith("=", StringComparison.Ordinal) && PackageVersion.Same(requirement.Substring(1), requested);
                    if (matches) return pair.Value;
                }
            return null;
        }

        // ---- the TOML subset wkg.toml and wkg.lock use ------------------------------------------------------

        private static class Toml
        {
            public sealed class Line
            {
                public string Table = "";
                public bool IsArray;
                /// <summary>Null for a table header line.</summary>
                public string? Key;
                public string Value = "";
            }

            public static List<Line> Read(string text)
            {
                var result = new List<Line>();
                var table = "";
                var isArray = false;
                var rows = text.Replace("\r\n", "\n").Split('\n');
                for (var i = 0; i < rows.Length; i++)
                {
                    var row = StripComment(rows[i]).Trim();
                    if (row.Length == 0) continue;
                    if (row[0] == '[')
                    {
                        isArray = row.StartsWith("[[", StringComparison.Ordinal);
                        var start = isArray ? 2 : 1;
                        var close = row.IndexOf(isArray ? "]]" : "]", start, StringComparison.Ordinal);
                        if (close < 0) throw new PackageManagerException("Unterminated table header at line " + (i + 1) + ": " + row);
                        table = row.Substring(start, close - start).Trim();
                        result.Add(new Line { Table = table, IsArray = isArray });
                        continue;
                    }
                    var key = ParseKey(row, out var rest);
                    if (key == null) continue;
                    var value = rest;
                    // Arrays may continue over several lines.
                    while (Depth(value) > 0 && i + 1 < rows.Length) value += " " + StripComment(rows[++i]).Trim();
                    result.Add(new Line { Table = table, IsArray = isArray, Key = key, Value = value.Trim() });
                }
                return result;
            }

            public static List<string> SplitDotted(string table)
            {
                var parts = new List<string>();
                var current = new StringBuilder();
                char quote = '\0';
                foreach (var c in table)
                {
                    if (quote != '\0') { if (c == quote) quote = '\0'; else current.Append(c); }
                    else if (c == '"' || c == '\'') quote = c;
                    else if (c == '.') { parts.Add(current.ToString().Trim()); current.Clear(); }
                    else current.Append(c);
                }
                parts.Add(current.ToString().Trim());
                return parts;
            }

            /// <summary>Every quoted string in <paramref name="raw"/> (one for a scalar, all elements for an array).</summary>
            public static IEnumerable<string> Strings(string raw)
            {
                for (var i = 0; i < raw.Length; i++)
                {
                    var c = raw[i];
                    if (c != '"' && c != '\'') continue;
                    var value = new StringBuilder();
                    for (i++; i < raw.Length && raw[i] != c; i++)
                    {
                        if (c == '"' && raw[i] == '\\' && i + 1 < raw.Length)
                        {
                            i++;
                            value.Append(raw[i] == 'n' ? '\n' : raw[i] == 't' ? '\t' : raw[i]);
                        }
                        else value.Append(raw[i]);
                    }
                    yield return value.ToString();
                }
            }

            private static string? ParseKey(string row, out string rest)
            {
                rest = "";
                string key;
                int end;
                if (row[0] == '"' || row[0] == '\'')
                {
                    end = row.IndexOf(row[0], 1);
                    if (end < 0) return null;
                    key = row.Substring(1, end - 1);
                    end++;
                }
                else
                {
                    end = row.IndexOf('=');
                    if (end < 0) return null;
                    key = row.Substring(0, end).Trim();
                }
                var equals = row.IndexOf('=', end);
                if (equals < 0) return null;
                rest = row.Substring(equals + 1).Trim();
                return key;
            }

            private static int Depth(string value)
            {
                var depth = 0;
                char quote = '\0';
                for (var i = 0; i < value.Length; i++)
                {
                    var c = value[i];
                    if (quote != '\0') { if (c == '\\' && quote == '"') i++; else if (c == quote) quote = '\0'; }
                    else if (c == '"' || c == '\'') quote = c;
                    else if (c == '[') depth++;
                    else if (c == ']') depth--;
                }
                return depth;
            }

            private static string StripComment(string row)
            {
                char quote = '\0';
                for (var i = 0; i < row.Length; i++)
                {
                    var c = row[i];
                    if (quote != '\0') { if (c == '\\' && quote == '"') i++; else if (c == quote) quote = '\0'; }
                    else if (c == '"' || c == '\'') quote = c;
                    else if (c == '#') return row.Substring(0, i);
                }
                return row;
            }
        }
    }
}
