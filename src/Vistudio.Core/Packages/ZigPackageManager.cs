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
    /// Zig's <c>build.zig.zon</c>. Zig has no registry: a dependency is a URL plus a content hash, so versions are read
    /// from the URL (a tag or a commit) and newer ones are found by asking the dependency's git repository for its tags.
    /// <c>zig fetch --save</c> adds and re-pins; removing a dependency is a manifest edit.
    /// </summary>
    public sealed class ZigPackageManager : IPackageManager
    {
        private static readonly string[] GitHosts = { "github.com", "codeberg.org", "gitlab.com" };
        private static readonly string[] ArchiveExtensions = { ".tar.gz", ".tar.xz", ".tar.zst", ".tar.bz2", ".tgz", ".zip", ".tar" };
        private static readonly Regex VersionLike = new Regex(@"^[vV]?\d+(\.\d+)*([-+][0-9A-Za-z.+\-]*)?$", RegexOptions.CultureInvariant);
        private static readonly Regex CommitLike = new Regex(@"^([0-9a-fA-F]{40}|[0-9a-fA-F]{64})$", RegexOptions.CultureInvariant);
        private static readonly Regex SaveName = new Regex(@"^[A-Za-z_][A-Za-z0-9_.\-]*$", RegexOptions.CultureInvariant);
        // name-version-<44 characters>: Zig 0.14's package hash keeps 200 bits of SHA-256 as base64url text after the name and version.
        private static readonly Regex NewHash = new Regex(@"^(?<name>[^-]+)-(?<version>.+)-[A-Za-z0-9_\-]{44}$", RegexOptions.CultureInvariant);

        public PackageEcosystem Ecosystem => PackageEcosystem.Zig;

        public string DisplayName => "Zig";

        public IReadOnlyList<string> ToolNames { get; } = new[] { "zig" };

        public IReadOnlyList<string> ManifestNames { get; } = new[] { "build.zig.zon" };

        /// <summary><c>git.exe</c> on PATH; null when git is not installed.</summary>
        public static string? FindGit() => ToolLocator.Find(new[] { "git.exe" });

        public IReadOnlyList<PackageEntry> ReadManifest(string manifestPath)
        {
            var text = Read(manifestPath);
            return Scan(text).Select(d => ToEntry(d, manifestPath)).ToList();
        }

        public IReadOnlyList<ProcessStartInfo> CreateOutdated(string tool, string manifestPath, IReadOnlyList<PackageEntry> declared)
        {
            var git = FindGit();
            if (git == null) return new ProcessStartInfo[0];
            var folder = Folder(manifestPath);
            var result = new List<ProcessStartInfo>();
            foreach (var entry in declared)
            {
                var repo = TaggedRepository(entry);
                if (repo == null) continue;
                var start = ProcessCommands.CreateRedirected(git, PackageArguments.Join(new[] { "ls-remote", "--tags", "--refs", repo }), folder);
                // A private repository would otherwise wait for a password on a console nobody sees.
                start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                result.Add(start);
            }
            return result;
        }

        public IReadOnlyList<PackageEntry> ApplyOutdated(IReadOnlyList<PackageEntry> declared, IReadOnlyList<PackageToolOutput> outputs)
        {
            if (outputs.Count == 0) return declared;
            // The same filter and order as CreateOutdated: that is what ties output number n to its package.
            var asked = declared.Where(e => TaggedRepository(e) != null).ToList();
            if (asked.Count != outputs.Count)
                throw new PackageManagerException("Zig expected " + asked.Count + " git results but got " + outputs.Count + ".");
            var result = new List<PackageEntry>(declared.Count);
            var next = 0;
            foreach (var entry in declared)
            {
                if (TaggedRepository(entry) == null) { result.Add(entry); continue; }
                var output = outputs[next++];
                var latest = output.ExitCode == 0 ? Newest(Tags(output.StandardOutput)) : null;
                result.Add(latest == null ? entry : entry.WithVersions(entry.Current, latest, latest));
            }
            return result;
        }

        public PackageAction Add(string tool, string manifestPath, PackageRequest request)
        {
            if (request.Source == null) throw new PackageManagerException("Zig needs the URL of the package: a tarball, or a git+https URL.");
            var name = PackageArguments.ValidateName(request.Name);
            if (!SaveName.IsMatch(name)) throw new PackageManagerException("Not usable as a Zig dependency name: " + name);
            return Fetch(tool, manifestPath, name, PackageArguments.ValidateSource(request.Source), false);
        }

        public PackageAction Remove(string tool, string manifestPath, PackageEntry package)
        {
            var text = Read(manifestPath);
            var dependency = Scan(text).FirstOrDefault(d => d.Name == package.Name);
            if (dependency == null) throw new PackageManagerException(package.Name + " is not listed in " + Path.GetFileName(manifestPath) + " any more; reload the packages.");
            var start = dependency.Start;
            var lineStart = text.LastIndexOf('\n', Math.Max(start - 1, 0)) + 1;
            var wholeLine = text.Substring(lineStart, start - lineStart).Trim().Length == 0;
            var end = dependency.End;
            if (wholeLine)
            {
                // The line goes away completely, trailing comment included, so no blank line is left behind.
                var rest = end;
                while (rest < text.Length && (text[rest] == ' ' || text[rest] == '\t')) rest++;
                if (rest + 1 < text.Length && text[rest] == '/' && text[rest + 1] == '/')
                    while (rest < text.Length && text[rest] != '\n' && text[rest] != '\r') rest++;
                if (rest >= text.Length) end = rest;
                else if (text[rest] == '\n') end = rest + 1;
                else if (text[rest] == '\r') end = rest + (rest + 1 < text.Length && text[rest + 1] == '\n' ? 2 : 1);
                start = lineStart;
            }
            // An entry sharing its line with others takes the spaces after it along, so no double space is left.
            else while (end < text.Length && (text[end] == ' ' || text[end] == '\t')) end++;
            var edited = text.Remove(start, end - start);
            return new PackageAction("zig: remove dependency " + package.Name + " from " + Path.GetFileName(manifestPath),
                null, new[] { new ManifestEdit(manifestPath, edited) });
        }

        public PackageAction Update(string tool, string manifestPath, PackageEntry? package, bool toLatest)
        {
            if (toLatest)
            {
                if (package == null) throw new PackageManagerException("Updating every Zig dependency to its newest version needs each dependency's version check; update them one at a time.");
                return ToLatest(tool, manifestPath, package);
            }
            var floating = (package == null ? ReadManifest(manifestPath) : new[] { package })
                .Where(e => e.Source != null && ParseSource(e.Source).Kind == RefKind.Branch).ToList();
            if (floating.Count == 0)
            {
                var what = package == null ? "Zig dependencies are" : "Zig dependency " + package.Name + " is";
                return new PackageAction(what + " pinned by hash: re-fetching a tag, a commit or a local path gives the same package. Use Update to latest to move to a newer tag.");
            }
            // A branch archive or a git URL without a commit is the one case where fetching again can bring new content.
            var commands = floating.Select(e => FetchCommand(tool, manifestPath, e.Name, e.Source!, false)).ToList();
            return new PackageAction("zig fetch --save for " + string.Join(", ", floating.Select(e => e.Name)), commands);
        }

        private PackageAction ToLatest(string tool, string manifestPath, PackageEntry package)
        {
            var source = package.Source == null ? null : ParseSource(package.Source);
            if (source == null || source.Kind != RefKind.Tag)
                throw new PackageManagerException(package.Name + " is not pinned to a version tag in its URL, so there is no newer version to move to.");
            if (package.Latest == null) throw new PackageManagerException("The newest version of " + package.Name + " is not known yet; run the version check first.");
            if (PackageVersion.Compare(package.Latest, source.Ref) <= 0) throw new PackageManagerException(package.Name + " already uses the newest tag, " + source.Ref + ".");
            string url;
            try { url = PackageArguments.ValidateSource(source.WithTag(package.Latest)); }
            catch (ArgumentException e) { throw new PackageManagerException("The new URL for " + package.Name + " is not usable: " + e.Message); }
            // git+ URLs are saved as a commit unless told otherwise; the tag in the URL is what the next version check reads.
            return Fetch(tool, manifestPath, package.Name, url, url.StartsWith("git+", StringComparison.Ordinal));
        }

        private static PackageAction Fetch(string tool, string manifestPath, string name, string url, bool exact)
        {
            var command = FetchCommand(tool, manifestPath, name, url, exact);
            return new PackageAction("zig fetch --save" + (exact ? "-exact" : "") + "=" + name + " " + url, new[] { command });
        }

        private static ProcessStartInfo FetchCommand(string tool, string manifestPath, string name, string url, bool exact) =>
            // zig fetch finds build.zig.zon by walking up from the working directory.
            ProcessCommands.CreateRedirected(tool, PackageArguments.Join(new[] { "fetch", (exact ? "--save-exact=" : "--save=") + name, url }), Folder(manifestPath));

        private static string Folder(string manifestPath) => Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";

        private static string Read(string path)
        {
            try { return File.ReadAllText(path); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new PackageManagerException("Cannot read " + path + ": " + e.Message);
            }
        }

        /// <summary>The git repository to ask for tags, only for a dependency pinned to a version tag on a known git host.</summary>
        private static string? TaggedRepository(PackageEntry entry)
        {
            if (entry.Source == null) return null;
            var source = ParseSource(entry.Source);
            if (source.Kind != RefKind.Tag || source.Repository == null) return null;
            try { return PackageArguments.ValidateSource(source.Repository); }
            catch (ArgumentException) { return null; }
        }

        private static IEnumerable<string> Tags(string lsRemote)
        {
            const string prefix = "refs/tags/";
            foreach (var line in lsRemote.Split('\n'))
            {
                var at = line.IndexOf(prefix, StringComparison.Ordinal);
                if (at < 0) continue;
                var tag = line.Substring(at + prefix.Length).Trim();
                if (VersionLike.IsMatch(tag)) yield return tag;
            }
        }

        private static string? Newest(IEnumerable<string> tags) => PackageVersion.Newest(tags.Distinct(StringComparer.Ordinal));

        private static PackageEntry ToEntry(ZonDependency dependency, string manifestPath)
        {
            var source = dependency.Url ?? dependency.Path;
            var hashVersion = HashVersion(dependency.Hash);
            var parsed = dependency.Url == null ? null : ParseSource(dependency.Url);
            var tagged = parsed != null && parsed.Kind == RefKind.Tag;
            var requested = parsed != null && parsed.Kind != RefKind.None ? parsed.Ref : hashVersion ?? "";
            // The tag is what the git host's own tag list is compared with; the hash version is the package's self-declared one.
            var current = tagged ? parsed!.Ref : hashVersion;
            return new PackageEntry(dependency.Name, requested, PackageScope.Runtime, "dependencies", manifestPath, dependency.Line, source, current);
        }

        /// <summary>Version inside a Zig 0.14+ package hash (<c>name-version-hashtext</c>); null for a legacy <c>1220…</c> hash.</summary>
        public static string? HashVersion(string? hash)
        {
            if (hash == null) return null;
            var match = NewHash.Match(hash);
            return match.Success ? match.Groups["version"].Value : null;
        }

        private enum RefKind { None, Tag, Commit, Branch }

        private sealed class SourceInfo
        {
            public RefKind Kind;
            public string Ref = "";
            public string? Repository;
            public string Url = "";
            /// <summary>Where in <see cref="Url"/> the ref may appear; later text only.</summary>
            public int Search;

            /// <summary>The URL with the tag replaced everywhere it appears after the ref marker (GitLab repeats it in the file name).</summary>
            public string WithTag(string tag) => Url.Substring(0, Search) + Url.Substring(Search).Replace(Ref, tag);
        }

        private static SourceInfo ParseSource(string url)
        {
            var info = new SourceInfo { Url = url };
            if (url.StartsWith("git+", StringComparison.Ordinal))
            {
                var hash = url.IndexOf('#');
                var query = url.IndexOf('?');
                var repoEnd = new[] { hash, query }.Where(i => i >= 0).DefaultIfEmpty(url.Length).Min();
                var repo = url.Substring(4, repoEnd - 4);
                if (!repo.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !repo.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return info;
                info.Repository = repo;
                var fragment = hash < 0 ? "" : url.Substring(hash + 1);
                info.Search = hash < 0 ? url.Length : hash + 1;
                info.Ref = fragment;
                info.Kind = fragment.Length == 0 ? RefKind.Branch : Classify(fragment, RefKind.Branch);
                return info;
            }
            Uri? uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri == null || (uri.Scheme != "https" && uri.Scheme != "http")) return info;
            var host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.Ordinal)) host = host.Substring(4);
            if (!GitHosts.Contains(host)) return info;
            var path = uri.AbsolutePath;
            var archive = path.IndexOf("/archive/", StringComparison.Ordinal);
            var download = path.IndexOf("/releases/download/", StringComparison.Ordinal);
            string repoPath, after;
            if (archive >= 0) { repoPath = path.Substring(0, archive); after = path.Substring(archive + "/archive/".Length); }
            else if (download >= 0) { repoPath = path.Substring(0, download); after = path.Substring(download + "/releases/download/".Length); }
            else return info;
            if (repoPath.EndsWith("/-", StringComparison.Ordinal)) repoPath = repoPath.Substring(0, repoPath.Length - 2);
            if (repoPath.Trim('/').Split('/').Length < 2) return info;
            info.Repository = "https://" + host + repoPath;
            string reference;
            var kind = RefKind.Branch;
            if (download >= 0) { reference = after.Split('/')[0]; kind = RefKind.None; }
            else if (after.StartsWith("refs/tags/", StringComparison.Ordinal)) { reference = StripExtension(after.Substring("refs/tags/".Length)); kind = RefKind.None; }
            else if (after.StartsWith("refs/heads/", StringComparison.Ordinal)) { reference = StripExtension(after.Substring("refs/heads/".Length)); }
            else reference = StripExtension(after.Split('/')[0]);
            info.Ref = reference;
            info.Kind = kind == RefKind.Branch && after.StartsWith("refs/heads/", StringComparison.Ordinal) ? RefKind.Branch : Classify(reference, kind);
            // The tag is searched from the first place it appears after the marker; the host and the repository name come before it.
            info.Search = Math.Max(url.IndexOf(archive >= 0 ? "/archive/" : "/releases/download/", StringComparison.Ordinal), 0);
            return info;
        }

        private static RefKind Classify(string reference, RefKind otherwise)
        {
            if (CommitLike.IsMatch(reference)) return RefKind.Commit;
            if (VersionLike.IsMatch(reference)) return RefKind.Tag;
            return otherwise;
        }

        private static string StripExtension(string name)
        {
            foreach (var extension in ArchiveExtensions)
                if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return name.Substring(0, name.Length - extension.Length);
            return name;
        }

        // ---- build.zig.zon scanning ------------------------------------------------------------------------------

        private sealed class ZonDependency
        {
            public string Name = "";
            /// <summary>Offset of the leading dot.</summary>
            public int Start;
            /// <summary>Offset after the entry's trailing comma, or after its value when it has none.</summary>
            public int End;
            public int Line;
            public string? Url, Hash, Path;
        }

        private sealed class ZonField
        {
            public string Name = "";
            public int Start, ValueStart, ValueEnd, End;
        }

        /// <summary>
        /// A small scanner of ZON rather than a parser: it only has to find the entries of <c>.dependencies</c> and the exact
        /// text each one occupies, skipping comments, strings and nested braces. The ZON reader that exists for <c>zig env</c>
        /// (ZigEnvironmentInfo) reads top-level string fields by regular expression and cannot give spans or nesting.
        /// </summary>
        private static List<ZonDependency> Scan(string text)
        {
            var result = new List<ZonDependency>();
            var root = Trivia(text, 0);
            if (root + 1 >= text.Length || text[root] != '.' || text[root + 1] != '{')
                throw new PackageManagerException("build.zig.zon does not start with a ZON struct (.{ ... }).");
            var dependencies = Fields(text, root).FirstOrDefault(f => f.Name == "dependencies");
            if (dependencies == null || !IsStruct(text, dependencies.ValueStart)) return result;
            foreach (var field in Fields(text, dependencies.ValueStart))
            {
                var dependency = new ZonDependency
                {
                    Name = field.Name,
                    Start = field.Start,
                    End = field.End,
                    Line = 1 + text.Take(field.Start).Count(c => c == '\n')
                };
                if (IsStruct(text, field.ValueStart))
                    foreach (var inner in Fields(text, field.ValueStart))
                    {
                        var value = inner.ValueStart < text.Length && text[inner.ValueStart] == '"' ? ReadString(text, inner.ValueStart, out _) : null;
                        if (inner.Name == "url") dependency.Url = value;
                        else if (inner.Name == "hash") dependency.Hash = value;
                        else if (inner.Name == "path") dependency.Path = value;
                    }
                result.Add(dependency);
            }
            return result;
        }

        private static bool IsStruct(string text, int index) => index + 1 < text.Length && text[index] == '.' && text[index + 1] == '{';

        /// <summary>The <c>.name = value,</c> fields of the struct whose <c>.{</c> starts at <paramref name="open"/>.</summary>
        private static List<ZonField> Fields(string text, int open)
        {
            var fields = new List<ZonField>();
            var i = open + 2;
            while (true)
            {
                i = Trivia(text, i);
                if (i >= text.Length) throw new PackageManagerException("build.zig.zon ends inside a struct that was never closed.");
                if (text[i] == '}') return fields;
                if (text[i] != '.') throw new PackageManagerException("build.zig.zon: expected a field like .name = ... at line " + (1 + text.Take(i).Count(c => c == '\n')) + ".");
                var field = new ZonField { Start = i };
                i++;
                if (i + 1 < text.Length && text[i] == '@' && text[i + 1] == '"') { field.Name = ReadString(text, i + 1, out i); }
                else
                {
                    var begin = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    field.Name = text.Substring(begin, i - begin);
                }
                i = Trivia(text, i);
                if (i >= text.Length || text[i] != '=') throw new PackageManagerException("build.zig.zon: expected '=' after ." + field.Name + ".");
                i = Trivia(text, i + 1);
                field.ValueStart = i;
                field.ValueEnd = ValueEnd(text, i);
                i = Trivia(text, field.ValueEnd);
                field.End = field.ValueEnd;
                if (i < text.Length && text[i] == ',') { field.End = i + 1; i++; }
                fields.Add(field);
            }
        }

        /// <summary>The end of the value that starts at <paramref name="start"/>: the last significant character before the comma or closing brace that ends it.</summary>
        private static int ValueEnd(string text, int start)
        {
            var depth = 0;
            var last = start;
            var i = start;
            while (i < text.Length)
            {
                var c = text[i];
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { i = LineEnd(text, i); continue; }
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '"') { ReadString(text, i, out i); last = i; continue; }
                if (c == '\\' && i + 1 < text.Length && text[i + 1] == '\\') { i = LineEnd(text, i); last = i; continue; }
                if (c == '{') depth++;
                else if (c == '}') { if (depth == 0) return last; depth--; }
                else if (c == ',' && depth == 0) return last;
                i++;
                last = i;
            }
            return last;
        }

        private static int Trivia(string text, int i)
        {
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i])) i++;
                else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/') i = LineEnd(text, i);
                else break;
            }
            return i;
        }

        private static int LineEnd(string text, int i)
        {
            while (i < text.Length && text[i] != '\n' && text[i] != '\r') i++;
            return i;
        }

        /// <summary>Reads the string literal that starts at the quote at <paramref name="start"/>; <paramref name="end"/> is the offset after the closing quote.</summary>
        private static string ReadString(string text, int start, out int end)
        {
            var builder = new StringBuilder();
            var i = start + 1;
            while (i < text.Length && text[i] != '"' && text[i] != '\n')
            {
                if (text[i] == '\\' && i + 1 < text.Length)
                {
                    i++;
                    builder.Append(text[i] == 'n' ? '\n' : text[i] == 't' ? '\t' : text[i] == 'r' ? '\r' : text[i]);
                }
                else builder.Append(text[i]);
                i++;
            }
            end = i < text.Length && text[i] == '"' ? i + 1 : i;
            return builder.ToString();
        }
    }
}
