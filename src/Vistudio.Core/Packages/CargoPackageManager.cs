using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Vistudio.Core.Packages
{
    /// <summary>
    /// Cargo (Rust): <c>Cargo.toml</c> / <c>Cargo.lock</c> and <c>cargo add | remove | update</c>. Cargo has no
    /// JSON outdated report, so versions come from the status lines of <c>cargo update --dry-run --verbose</c>:
    /// that run resolves against the registry, prints what it would change plus a newer version where one exists
    /// (<c>Updating foo v1.0.0 -> v1.0.1 (available: v2.0.0)</c>, <c>Unchanged bar v0.3.1 (available: v0.4.0)</c>) and
    /// writes nothing. The lines appear since Cargo 1.78; older cargo prints none and every package then stays unchecked.
    /// </summary>
    public sealed class CargoPackageManager : IPackageManager
    {
        private static readonly Regex CrateName = new Regex(@"^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);
        private static readonly Regex TargetText = new Regex(@"^[A-Za-z0-9_.,()= ""'-]+$", RegexOptions.CultureInvariant);
        private static readonly Regex Ansi = new Regex("\u001b\\[[0-9;]*[A-Za-z]", RegexOptions.CultureInvariant);
        // Cargo right-aligns the status word; "Updating crates.io index" and "Updating git repository `..`" have no "name vN" and do not match.
        private static readonly Regex StatusLine = new Regex(@"^\s*(?<kind>Updating|Downgrading|Adding|Removing|Unchanged)\s+(?<name>[A-Za-z0-9_-]+)\s+v(?<version>\d\S*)(?<rest>.*)$", RegexOptions.CultureInvariant);
        private static readonly Regex Moved = new Regex(@"->\s+v(?<version>\d[^\s(,]*)", RegexOptions.CultureInvariant);
        private static readonly Regex Available = new Regex(@"\(available:\s*v(?<version>[^\s,)]+)", RegexOptions.CultureInvariant);
        private static readonly Regex LockingLine = new Regex(@"^\s*Locking\s+\d+\s+packages?\b", RegexOptions.CultureInvariant | RegexOptions.Multiline);

        public PackageEcosystem Ecosystem => PackageEcosystem.Cargo;
        public string DisplayName => "Cargo";
        public IReadOnlyList<string> ToolNames { get; } = new[] { "cargo" };
        public IReadOnlyList<string> ManifestNames { get; } = new[] { "Cargo.toml" };

        public IReadOnlyList<PackageEntry> ReadManifest(string manifestPath)
        {
            var scan = Scan(ReadText(manifestPath));
            var locked = ReadLock(manifestPath);
            var entries = new List<PackageEntry>();
            foreach (var dependency in scan.Dependencies)
            {
                var requested = dependency.Version ?? dependency.Field("version") as string ?? "";
                var parts = new List<string>();
                if (dependency.Field("workspace") is bool workspace && workspace) parts.Add("workspace");
                else if (dependency.Field("path") is string) parts.Add("path");
                else if (dependency.Field("git") is string git) parts.Add(git);
                if (dependency.Field("package") is string real) parts.Add("package " + real);
                var source = parts.Count == 0 ? null : string.Join("; ", parts);
                var current = Pick(locked, RealName(dependency.Name, source), requested);
                entries.Add(new PackageEntry(dependency.Name, requested, dependency.Scope, dependency.Group, manifestPath, dependency.Line, source, current));
            }
            return entries;
        }

        public IReadOnlyList<ProcessStartInfo> CreateOutdated(string tool, string manifestPath, IReadOnlyList<PackageEntry> declared)
        {
            // --dry-run resolves and reports without touching Cargo.lock; Unchanged lines (the only place a newer
            // version of an already locked package shows up) need --verbose.
            return new[] { Start(tool, manifestPath, "update", "--dry-run", "--verbose") };
        }

        public IReadOnlyList<PackageEntry> ApplyOutdated(IReadOnlyList<PackageEntry> declared, IReadOnlyList<PackageToolOutput> outputs)
        {
            if (outputs.Count != 1) throw new PackageManagerException("Expected the output of one 'cargo update --dry-run' run.");
            var output = outputs[0];
            // Cargo reports progress on stderr; stdout is read as well in case a wrapper redirects it.
            var text = Ansi.Replace(output.StandardError + "\n" + output.StandardOutput, "");
            if (output.ExitCode != 0) throw new PackageManagerException("cargo update --dry-run failed (exit code " + output.ExitCode + "): " + ErrorText(text));

            var records = ParseStatusLines(text);
            // Without any status line the cargo in use may predate the report (before 1.78): nothing can be concluded.
            var reported = records.Count != 0 || LockingLine.IsMatch(text);
            var result = new List<PackageEntry>();
            foreach (var entry in declared)
            {
                var name = RealName(entry.Name, entry.Source);
                var candidates = records.Where(r => Same(r.Name, name)).ToList();
                StatusRecord? record = null;
                if (entry.Current != null) record = candidates.FirstOrDefault(r => r.From != null && PackageVersion.Same(r.From, entry.Current));
                else
                {
                    var fitting = candidates.Where(r => entry.Requested.Length == 0 || CargoRequirement.Matches(entry.Requested, r.To) != false).ToList();
                    if (fitting.Count == 1) record = fitting[0];
                }

                if (record == null)
                {
                    // Registry packages Cargo reports nothing about are current (another locked version of the name may have a line); path and git sources are never reported.
                    result.Add(reported && entry.Current != null && IsRegistry(entry) ?entry.WithVersions(entry.Current, entry.Current, entry.Current) : entry);
                    continue;
                }

                if (record.Kind == "Unchanged")
                {
                    var current = record.To;
                    if (record.Available == null) result.Add(entry.WithVersions(current, current, current));
                    else
                    {
                        var reachable = CargoRequirement.Matches(entry.Requested.Length == 0 ? "^" + current : entry.Requested, record.Available)
                            ?? CargoRequirement.Matches("^" + current, record.Available) ?? false;
                        result.Add(entry.WithVersions(current, reachable ? record.Available : current, record.Available));
                    }
                }
                else result.Add(entry.WithVersions(record.From, record.To, record.Available ?? record.To));
            }
            return result;
        }

        public PackageAction Add(string tool, string manifestPath, PackageRequest request)
        {
            var name = CheckName(request.Name);
            RequirePackageManifest(manifestPath);
            var arguments = new List<string> { "add", name + (request.Version == null ? "" : "@" + request.Version) };
            AddScope(arguments, request.Scope);
            if (request.Source != null)
            {
                if (!CrateName.IsMatch(request.Source)) throw new PackageManagerException("A Cargo registry is given by the name configured in .cargo/config.toml: " + request.Source);
                arguments.Add("--registry");
                arguments.Add(request.Source);
            }
            return Action(tool, manifestPath, arguments);
        }

        public PackageAction Remove(string tool, string manifestPath, PackageEntry package)
        {
            if (package.Group == "workspace.dependencies")
                throw new PackageManagerException("'" + package.Name + "' is declared in [workspace.dependencies]; cargo remove edits member packages. Remove it there by hand once no member inherits it.");
            RequirePackageManifest(manifestPath);
            var arguments = new List<string> { "remove", CheckName(package.Name) };
            AddScope(arguments, package.Scope);
            AddTarget(arguments, package.Group);
            return Action(tool, manifestPath, arguments);
        }

        public PackageAction Update(string tool, string manifestPath, PackageEntry? package, bool toLatest)
        {
            if (package == null)
            {
                // Without the entries there is no version to write, so only the lock file moves; majors are applied per package.
                if (!toLatest) return Action(tool, manifestPath, new[] { "update" });
                return Action(tool, manifestPath, new[] { "update" },
                    "cargo update (lock file only: major upgrades are applied per package, use 'Update to latest' on each outdated package)");
            }

            var real = CheckName(RealName(package.Name, package.Source));
            if (!toLatest) return Action(tool, manifestPath, new[] { "update", real });

            if (package.Latest == null) throw new PackageManagerException("The latest version of '" + package.Name + "' is not known yet: check for updates first.");
            if (!IsRegistry(package) || package.Source == "workspace")
                throw new PackageManagerException("'" + package.Name + "' does not come from a registry in this manifest (" + package.Source + "); change its version where it is declared.");
            RequirePackageManifest(manifestPath);
            // Re-adding at an exact version rewrites the requirement; cargo add keeps the entry's other keys.
            var arguments = new List<string> { "add", real + "@" + PackageArguments.ValidateVersion(package.Latest) };
            if (!string.Equals(real, package.Name, StringComparison.Ordinal)) { arguments.Add("--rename"); arguments.Add(CheckName(package.Name)); }
            AddScope(arguments, package.Scope);
            AddTarget(arguments, package.Group);
            return Action(tool, manifestPath, arguments);
        }

        private static PackageAction Action(string tool, string manifestPath, IReadOnlyList<string> arguments, string? description = null) =>
            new PackageAction(description ?? "cargo " + string.Join(" ", arguments.Select(PackageArguments.Quote)), new[] { Start(tool, manifestPath, arguments.ToArray()) });

        private static ProcessStartInfo Start(string tool, string manifestPath, params string[] arguments)
        {
            if (!Path.IsPathRooted(tool)) throw new ArgumentException("The cargo executable must be an absolute path: " + tool);
            var start = ProcessCommands.CreateRedirected(tool, PackageArguments.Join(arguments), Path.GetDirectoryName(Path.GetFullPath(manifestPath))!);
            // Plain text for the parser and the Output pane.
            start.EnvironmentVariables["NO_COLOR"] = "1";
            start.EnvironmentVariables["CARGO_TERM_COLOR"] = "never";
            return start;
        }

        private static void AddScope(List<string> arguments, PackageScope scope)
        {
            if (scope == PackageScope.Development) arguments.Add("--dev");
            else if (scope == PackageScope.Build) arguments.Add("--build");
        }

        /// <summary>Entries of <c>[target.'cfg(..)'.dependencies]</c> need <c>--target</c> or cargo looks in the plain table.</summary>
        private static void AddTarget(List<string> arguments, string group)
        {
            if (!group.StartsWith("target.", StringComparison.Ordinal)) return;
            var end = group.LastIndexOf('.');
            var target = end > 7 ? group.Substring(7, end - 7) : "";
            if (!TargetText.IsMatch(target) || target[0] == '-') throw new PackageManagerException("Unsupported target in the manifest: " + group);
            arguments.Add("--target");
            arguments.Add(target);
        }

        private static string CheckName(string name)
        {
            if (!CrateName.IsMatch(name) || name[0] == '-') throw new PackageManagerException("Not a valid crate name: " + name);
            return name;
        }

        /// <summary>cargo add and remove need a package manifest; a workspace root without [package] is rejected by cargo itself, so say so first.</summary>
        private static void RequirePackageManifest(string manifestPath)
        {
            string text;
            try { text = File.ReadAllText(manifestPath); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            if (!Scan(text).HasPackage)
                throw new PackageManagerException(Path.GetFileName(manifestPath) + " has no [package] (a virtual workspace manifest). Run the action on a member package's Cargo.toml.");
        }

        private static bool IsRegistry(PackageEntry entry)
        {
            var primary = (entry.Source ?? "").Split(new[] { "; " }, StringSplitOptions.None).FirstOrDefault(p => p.Length != 0 && !p.StartsWith("package ", StringComparison.Ordinal));
            return primary == null || primary == "workspace";
        }

        /// <summary>The crate a renamed dependency (<c>alias = { package = "real" }</c>) really is; Source carries it as <c>package real</c>.</summary>
        private static string RealName(string key, string? source)
        {
            foreach (var part in (source ?? "").Split(new[] { "; " }, StringSplitOptions.None))
                if (part.StartsWith("package ", StringComparison.Ordinal)) return part.Substring(8);
            return key;
        }

        private static bool Same(string left, string right) =>
            string.Equals(left.Replace('_', '-'), right.Replace('_', '-'), StringComparison.OrdinalIgnoreCase);

        private static string ErrorText(string text)
        {
            // Everything from the first "error:" on (cargo adds "Caused by:" detail lines below it); progress lines before it are noise.
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length != 0).ToList();
            var first = lines.FindIndex(l => l.StartsWith("error", StringComparison.OrdinalIgnoreCase));
            return string.Join(" ", first < 0 ? lines : lines.Skip(first));
        }

        private static string ReadText(string path)
        {
            try { return File.ReadAllText(path); }
            catch (IOException ex) { throw new PackageManagerException("Cannot read " + path + ": " + ex.Message); }
            catch (UnauthorizedAccessException ex) { throw new PackageManagerException("Cannot read " + path + ": " + ex.Message); }
        }

        // ---- cargo update --dry-run --verbose output

        private sealed class StatusRecord
        {
            public StatusRecord(string kind, string name, string? from, string to, string? available)
            { Kind = kind; Name = name; From = from; To = to; Available = available; }
            public string Kind { get; }
            public string Name { get; }
            /// <summary>The locked version before the change; null for a package that is added.</summary>
            public string? From { get; }
            /// <summary>The version after the change; the locked one for <c>Unchanged</c>.</summary>
            public string To { get; }
            public string? Available { get; }
        }

        private static List<StatusRecord> ParseStatusLines(string text)
        {
            var result = new List<StatusRecord>();
            foreach (var raw in text.Split('\n'))
            {
                var match = StatusLine.Match(raw.TrimEnd('\r'));
                if (!match.Success) continue;
                var kind = match.Groups["kind"].Value;
                var name = match.Groups["name"].Value;
                var version = match.Groups["version"].Value;
                var rest = match.Groups["rest"].Value;
                var available = Available.Match(rest);
                var newer = available.Success ? available.Groups["version"].Value : null;
                if (kind == "Removing") continue;
                if (kind == "Updating" || kind == "Downgrading")
                {
                    // A git source moves to "#commit" and has no version to report.
                    var moved = Moved.Match(rest);
                    if (moved.Success) result.Add(new StatusRecord(kind, name, version, moved.Groups["version"].Value, newer));
                }
                else if (kind == "Adding") result.Add(new StatusRecord(kind, name, null, version, newer));
                else result.Add(new StatusRecord(kind, name, version, version, newer));
            }
            return result;
        }

        // ---- Cargo.lock

        private sealed class LockedPackage
        {
            public LockedPackage(string name, string version) { Name = name; Version = version; }
            public string Name { get; }
            public string Version { get; }
        }

        private static readonly Regex LockField = new Regex(@"^\s*(?<key>name|version)\s*=\s*""(?<value>[^""]*)""", RegexOptions.CultureInvariant);

        private static List<LockedPackage> ReadLock(string manifestPath)
        {
            var result = new List<LockedPackage>();
            var file = FindLock(manifestPath);
            if (file == null) return result;
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { return result; }
            catch (UnauthorizedAccessException) { return result; }

            string? name = null, version = null;
            void Flush() { if (name != null && version != null) result.Add(new LockedPackage(name, version)); name = version = null; }
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal)) { Flush(); continue; }
                var match = LockField.Match(line);
                if (!match.Success) continue;
                if (match.Groups["key"].Value == "name") name = match.Groups["value"].Value;
                else version = match.Groups["value"].Value;
            }
            Flush();
            return result;
        }

        /// <summary>Cargo.lock next to the manifest, else in the closest parent folder whose Cargo.toml declares a workspace.</summary>
        private static string? FindLock(string manifestPath)
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
            var own = true;
            while (!string.IsNullOrEmpty(folder))
            {
                try
                {
                    var lockFile = Path.Combine(folder, "Cargo.lock");
                    var toml = Path.Combine(folder, "Cargo.toml");
                    var isWorkspaceRoot = own || (File.Exists(toml) && Regex.IsMatch(File.ReadAllText(toml), @"^\s*\[workspace[\].]", RegexOptions.Multiline | RegexOptions.CultureInvariant));
                    if (isWorkspaceRoot && File.Exists(lockFile)) return lockFile;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                own = false;
                folder = Path.GetDirectoryName(folder);
            }
            return null;
        }

        /// <summary>The locked version of <paramref name="name"/>; with several versions the one the requirement allows, else null.</summary>
        private static string? Pick(List<LockedPackage> locked, string name, string requested)
        {
            var versions = locked.Where(p => Same(p.Name, name)).Select(p => p.Version).ToList();
            if (versions.Count <= 1) return versions.FirstOrDefault();
            var fitting = requested.Length == 0 ? new List<string>() : versions.Where(v => CargoRequirement.Matches(requested, v) == true).ToList();
            return fitting.Count == 1 ? fitting[0] : null;
        }

        // ---- Cargo.toml

        private sealed class Dependency
        {
            public string Name = "", Group = "";
            public PackageScope Scope;
            public int Line;
            public string? Version;
            public readonly Dictionary<string, object?> Fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            public object? Field(string key) => Fields.TryGetValue(key, out var value) ? value : null;
        }

        private sealed class ManifestScan
        {
            public readonly List<Dependency> Dependencies = new List<Dependency>();
            public bool HasPackage;
        }

        /// <summary>
        /// Line-oriented reader for the dependency tables only. A statement may continue over several lines (arrays,
        /// inline tables, multi-line strings); <c>[[array tables]]</c> and everything outside the dependency tables is skipped.
        /// </summary>
        private static ManifestScan Scan(string text)
        {
            var scan = new ManifestScan();
            var byKey = new Dictionary<string, Dependency>(StringComparer.Ordinal);
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            List<string>? table = new List<string>();
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var startLine = i + 1;
                var position = 0;
                if (line[0] == '[')
                {
                    if (line.StartsWith("[[", StringComparison.Ordinal)) { table = null; continue; }
                    position = 1;
                    table = TomlReader.Key(line, ref position);
                    if (table == null) continue;
                    if (table[0] == "package") scan.HasPackage = true;
                    Record(scan, byKey, table, startLine, null, false);
                    continue;
                }
                var key = TomlReader.Key(line, ref position);
                if (key == null || position >= line.Length || line[position] != '=') continue;
                var value = line.Substring(position + 1);
                while (!TomlReader.IsComplete(value) && i + 1 < lines.Length) value += "\n" + lines[++i];
                if (table == null) continue;
                var full = table.Concat(key).ToList();
                if (full[0] == "package") scan.HasPackage = true;
                Record(scan, byKey, full, startLine, value, true);
            }
            return scan;
        }

        private static void Record(ManifestScan scan, Dictionary<string, Dependency> byKey, List<string> path, int line, string? valueText, bool isAssignment)
        {
            if (!Classify(path, out var group, out var scope, out var rest) || rest.Count == 0) return;
            if (!isAssignment && rest.Count != 1) return;
            var name = rest[0];
            var id = group + "\0" + name;
            if (!byKey.TryGetValue(id, out var dependency))
            {
                dependency = new Dependency { Name = name, Group = group, Scope = scope, Line = line };
                byKey[id] = dependency;
                scan.Dependencies.Add(dependency);
            }
            if (!isAssignment) return;

            object? value;
            try { value = TomlReader.ParseValue(valueText!); }
            catch (FormatException) { return; }
            if (rest.Count == 1)
            {
                if (value is string version) dependency.Version = version;
                else if (value is Dictionary<string, object?> table) foreach (var pair in table) dependency.Fields[pair.Key] = pair.Value;
            }
            else TomlReader.Set(dependency.Fields, rest.Skip(1).ToList(), value);
        }

        private static bool Classify(List<string> path, out string group, out PackageScope scope, out List<string> rest)
        {
            group = "";
            scope = PackageScope.Runtime;
            rest = new List<string>();
            if (path.Count == 0) return false;
            int skip;
            string table;
            if (path[0] == "workspace" && path.Count >= 2 && path[1] == "dependencies") { group = "workspace.dependencies"; skip = 2; table = "dependencies"; }
            else if (path[0] == "target" && path.Count >= 3 && TableScope(path[2]) != null) { group = "target." + path[1] + "." + path[2]; skip = 3; table = path[2]; }
            else if (TableScope(path[0]) != null) { group = path[0]; skip = 1; table = path[0]; }
            else return false;
            scope = TableScope(table)!.Value;
            rest = path.Skip(skip).ToList();
            return true;
        }

        private static PackageScope? TableScope(string table)
        {
            switch (table)
            {
                case "dependencies": return PackageScope.Runtime;
                case "dev-dependencies": case "dev_dependencies": return PackageScope.Development;
                case "build-dependencies": case "build_dependencies": return PackageScope.Build;
                default: return null;
            }
        }

        /// <summary>The TOML values a dependency declaration uses: strings, booleans, arrays and inline tables.</summary>
        private static class TomlReader
        {
            /// <summary>Dotted key at <paramref name="i"/> (bare, "basic" or 'literal' parts); null when there is none. Stops after trailing blanks.</summary>
            public static List<string>? Key(string text, ref int i)
            {
                var parts = new List<string>();
                while (true)
                {
                    while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
                    if (i >= text.Length) return null;
                    string part;
                    if (text[i] == '"') { try { part = BasicString(text, ref i); } catch (FormatException) { return null; } }
                    else if (text[i] == '\'') part = LiteralString(text, ref i);
                    else
                    {
                        var start = i;
                        while (i < text.Length && (char.IsLetterOrDigit(text[i]) && text[i] < 128 || text[i] == '_' || text[i] == '-')) i++;
                        if (i == start) return null;
                        part = text.Substring(start, i - start);
                    }
                    parts.Add(part);
                    while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
                    if (i < text.Length && text[i] == '.') { i++; continue; }
                    return parts;
                }
            }

            /// <summary>False while a bracket, brace or multi-line string opened in <paramref name="text"/> is still open.</summary>
            public static bool IsComplete(string text)
            {
                var depth = 0;
                for (var i = 0; i < text.Length; i++)
                {
                    var c = text[i];
                    if (c == '#') { while (i < text.Length && text[i] != '\n') i++; }
                    else if (c == '"' || c == '\'')
                    {
                        var triple = string.CompareOrdinal(text, i, new string(c, 3), 0, 3) == 0;
                        if (triple)
                        {
                            var end = FindClose(text, i + 3, c, true);
                            if (end < 0) return false;
                            i = end + 2;
                        }
                        else
                        {
                            var end = FindClose(text, i + 1, c, false);
                            i = end < 0 ? text.Length : end;
                        }
                    }
                    else if (c == '[' || c == '{') depth++;
                    else if (c == ']' || c == '}') depth--;
                }
                return depth <= 0;
            }

            private static int FindClose(string text, int from, char quote, bool triple)
            {
                for (var i = from; i < text.Length; i++)
                {
                    if (quote == '"' && text[i] == '\\') { i++; continue; }
                    if (!triple && text[i] == '\n') return -1;
                    if (text[i] != quote) continue;
                    if (!triple) return i;
                    if (string.CompareOrdinal(text, i, new string(quote, 3), 0, 3) == 0) return i;
                }
                return -1;
            }

            public static object? ParseValue(string text)
            {
                var i = 0;
                return Value(text, ref i);
            }

            public static void Set(Dictionary<string, object?> table, List<string> path, object? value)
            {
                for (var n = 0; n < path.Count - 1; n++)
                {
                    if (!(table.TryGetValue(path[n], out var inner) && inner is Dictionary<string, object?> child))
                    {
                        child = new Dictionary<string, object?>(StringComparer.Ordinal);
                        table[path[n]] = child;
                    }
                    table = child;
                }
                table[path[path.Count - 1]] = value;
            }

            private static object? Value(string text, ref int i)
            {
                Skip(text, ref i, true);
                if (i >= text.Length) throw new FormatException("A value is missing.");
                var c = text[i];
                if (c == '"') return BasicString(text, ref i);
                if (c == '\'') return LiteralString(text, ref i);
                if (c == '[')
                {
                    i++;
                    var list = new List<object?>();
                    while (true)
                    {
                        Skip(text, ref i, true);
                        if (i >= text.Length) throw new FormatException("Unterminated array.");
                        if (text[i] == ']') { i++; return list; }
                        list.Add(Value(text, ref i));
                        Skip(text, ref i, true);
                        if (i < text.Length && text[i] == ',') i++;
                    }
                }
                if (c == '{')
                {
                    i++;
                    var table = new Dictionary<string, object?>(StringComparer.Ordinal);
                    while (true)
                    {
                        Skip(text, ref i, true);
                        if (i >= text.Length) throw new FormatException("Unterminated inline table.");
                        if (text[i] == '}') { i++; return table; }
                        var key = Key(text, ref i) ?? throw new FormatException("Bad key in inline table.");
                        if (i >= text.Length || text[i] != '=') throw new FormatException("'=' expected.");
                        i++;
                        Set(table, key, Value(text, ref i));
                        Skip(text, ref i, true);
                        if (i < text.Length && text[i] == ',') i++;
                    }
                }
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != ',' && text[i] != ']' && text[i] != '}' && text[i] != '#') i++;
                var token = text.Substring(start, i - start);
                if (token.Length == 0) throw new FormatException("Unexpected character '" + c + "'.");
                return token == "true" ? (object)true : token == "false" ? false : token;
            }

            private static void Skip(string text, ref int i, bool newlines)
            {
                while (i < text.Length)
                {
                    if (text[i] == '#') { while (i < text.Length && text[i] != '\n') i++; }
                    else if (text[i] == ' ' || text[i] == '\t' || (newlines && (text[i] == '\n' || text[i] == '\r'))) i++;
                    else break;
                }
            }

            private static string LiteralString(string text, ref int i)
            {
                if (string.CompareOrdinal(text, i, "'''", 0, 3) == 0)
                {
                    var close = text.IndexOf("'''", i + 3, StringComparison.Ordinal);
                    if (close < 0) throw new FormatException("Unterminated string.");
                    var body = text.Substring(i + 3, close - i - 3);
                    i = close + 3;
                    return body.StartsWith("\n", StringComparison.Ordinal) ? body.Substring(1) : body;
                }
                var end = text.IndexOf('\'', i + 1);
                if (end < 0) throw new FormatException("Unterminated string.");
                var value = text.Substring(i + 1, end - i - 1);
                i = end + 1;
                return value;
            }

            private static string BasicString(string text, ref int i)
            {
                var triple = string.CompareOrdinal(text, i, "\"\"\"", 0, 3) == 0;
                var builder = new StringBuilder();
                var p = i + (triple ? 3 : 1);
                if (triple && p < text.Length && text[p] == '\n') p++;
                while (p < text.Length)
                {
                    var c = text[p];
                    if (c == '"' && (!triple || string.CompareOrdinal(text, p, "\"\"\"", 0, 3) == 0)) { i = p + (triple ? 3 : 1); return builder.ToString(); }
                    if (c == '\n' && !triple) break;
                    if (c != '\\') { builder.Append(c); p++; continue; }
                    p++;
                    if (p >= text.Length) break;
                    var e = text[p++];
                    switch (e)
                    {
                        case 'n': builder.Append('\n'); break;
                        case 't': builder.Append('\t'); break;
                        case 'r': builder.Append('\r'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case 'u':
                        case 'U':
                            var length = e == 'u' ? 4 : 8;
                            if (p + length > text.Length || !int.TryParse(text.Substring(p, length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) throw new FormatException("Bad unicode escape.");
                            builder.Append(char.ConvertFromUtf32(code));
                            p += length;
                            break;
                        default:
                            // A backslash at the end of a line in a multi-line string joins it with the next one.
                            if (triple && (e == '\n' || e == ' ' || e == '\t' || e == '\r')) { while (p < text.Length && char.IsWhiteSpace(text[p])) p++; }
                            else throw new FormatException("Bad escape.");
                            break;
                    }
                }
                throw new FormatException("Unterminated string.");
            }
        }

        // ---- Cargo version requirements

        /// <summary>Cargo's requirement syntax: <c>1.2</c> and <c>^1.2</c> (caret), <c>~1.2</c>, <c>=1.2.3</c>, <c>&gt;=1, &lt;2</c>, <c>1.*</c>, <c>*</c>.</summary>
        private static class CargoRequirement
        {
            /// <summary>Whether <paramref name="version"/> satisfies <paramref name="requirement"/>; null when either cannot be read.</summary>
            public static bool? Matches(string requirement, string version)
            {
                if (!TryVersion(version, out var candidate, out var candidatePre)) return null;
                var versionPre = version.Contains("-") ? version.Substring(version.IndexOf('-') + 1).Split('+')[0] : "";
                var anyPre = false;
                foreach (var raw in requirement.Split(','))
                {
                    var text = raw.Trim();
                    if (text.Length == 0) return null;
                    var op = "";
                    foreach (var known in new[] { ">=", "<=", ">", "<", "=", "^", "~" })
                        if (text.StartsWith(known, StringComparison.Ordinal)) { op = known; text = text.Substring(known.Length).Trim(); break; }
                    if (text.Contains("-")) anyPre = true;
                    if (!TryPartial(text, out var a, out var count, out var wildcard)) return null;
                    var ok = Check(op, a, count, wildcard, candidate);
                    // An exact pre-release pin (=1.0.0-alpha.0) excludes its siblings (1.0.0-alpha.1), which share the numeric part.
                    var dash = text.IndexOf('-');
                    if (ok == true && op == "=" && count == 3 && dash >= 0 && !string.Equals(text.Substring(dash + 1), versionPre, StringComparison.Ordinal)) ok = false;
                    if (ok == null) return null;
                    if (!ok.Value) return false;
                }
                return !(candidatePre && !anyPre);
            }

            private static bool? Check(string op, long[] a, int n, bool wildcard, long[] v)
            {
                if (n == 0) return op.Length == 0 || op == "^" || op == "=" ? true : (bool?)null;
                if (op == "" && wildcard) op = "=";
                if (op == "" ) op = "^";
                switch (op)
                {
                    case "=": return v.Take(n).SequenceEqual(a.Take(n));
                    case ">=": return Compare(v, a) >= 0;
                    case "<": return Compare(v, a) < 0;
                    case ">": return n == 3 ? Compare(v, a) > 0 : Compare(v, Bump(a, n)) >= 0;
                    case "<=": return n == 3 ? Compare(v, a) <= 0 : Compare(v, Bump(a, n)) < 0;
                    case "~": return Compare(v, a) >= 0 && Compare(v, n == 1 ? new[] { a[0] + 1, 0, 0 } : new[] { a[0], a[1] + 1, 0 }) < 0;
                    default:
                        long[] upper;
                        if (a[0] > 0 || n == 1) upper = new[] { a[0] + 1, 0, 0 };
                        else if (n == 2 || a[1] > 0) upper = new[] { 0, a[1] + 1, 0 };
                        else upper = new[] { 0, 0, a[2] + 1 };
                        return Compare(v, a) >= 0 && Compare(v, upper) < 0;
                }
            }

            /// <summary>The first version after everything <paramref name="a"/> (n components given) covers.</summary>
            private static long[] Bump(long[] a, int n) => n == 1 ? new[] { a[0] + 1, 0, 0 } : new[] { a[0], a[1] + 1, 0 };

            private static int Compare(long[] left, long[] right)
            {
                for (var i = 0; i < 3; i++) if (left[i] != right[i]) return left[i] < right[i] ? -1 : 1;
                return 0;
            }

            private static bool TryVersion(string text, out long[] numbers, out bool pre)
            {
                text = text.Trim();
                if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V')) text = text.Substring(1);
                var plus = text.IndexOf('+');
                if (plus >= 0) text = text.Substring(0, plus);
                var dash = text.IndexOf('-');
                pre = dash >= 0;
                if (pre) text = text.Substring(0, dash);
                numbers = new long[3];
                var parts = text.Split('.');
                if (parts.Length != 3) return false;
                for (var i = 0; i < 3; i++) if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
                return true;
            }

            private static bool TryPartial(string text, out long[] numbers, out int count, out bool wildcard)
            {
                numbers = new long[3];
                count = 0;
                wildcard = false;
                var dash = text.IndexOf('-');
                if (dash >= 0) text = text.Substring(0, dash);
                var plus = text.IndexOf('+');
                if (plus >= 0) text = text.Substring(0, plus);
                if (text == "*") { wildcard = true; return true; }
                var parts = text.Split('.');
                if (parts.Length == 0 || parts.Length > 3) return false;
                foreach (var part in parts)
                {
                    if (part == "*" || part == "x" || part == "X") { wildcard = true; break; }
                    if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[count])) return false;
                    count++;
                }
                return count != 0 || wildcard;
            }
        }
    }
}
