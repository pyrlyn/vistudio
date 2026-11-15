using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Vistudio.Core.Bazel
{
    /// <summary>
    /// Bazel workspace facts read from the files themselves (no <c>bazel query</c>): the workspace root, packages (folders
    /// with a BUILD file), the targets a BUILD file declares, the repositories MODULE.bazel or WORKSPACE names, and the
    /// symbols a .bzl file defines. Targets created inside macros are not known.
    /// </summary>
    public static class BazelWorkspace
    {
        /// <summary>Files whose presence makes a folder a Bazel workspace root, in Bazel's own order of preference.</summary>
        public static readonly string[] RootFiles = { "MODULE.bazel", "REPO.bazel", "WORKSPACE.bazel", "WORKSPACE" };
        public static readonly string[] BuildFiles = { "BUILD.bazel", "BUILD" };
        private static readonly string[] StarlarkNames = { "BUILD", "BUILD.bazel", "WORKSPACE", "WORKSPACE.bazel", "WORKSPACE.bzlmod", "MODULE.bazel", "REPO.bazel" };
        private static readonly string[] StarlarkExtensions = { ".bzl", ".bazel", ".star", ".sky", ".bzlmod" };

        /// <summary>BUILD, MODULE.bazel, WORKSPACE, .bzl and other Starlark files Bazel reads.</summary>
        public static bool IsStarlarkFile(string? path)
        {
            var name = Path.GetFileName(path ?? "");
            return StarlarkNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                || StarlarkExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
        }

        public static BazelFileKind KindOf(string? path)
        {
            var name = Path.GetFileName(path ?? "");
            if (BuildFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) return BazelFileKind.Build;
            if (name.Equals("MODULE.bazel", StringComparison.OrdinalIgnoreCase)) return BazelFileKind.Module;
            if (name.StartsWith("WORKSPACE", StringComparison.OrdinalIgnoreCase)) return BazelFileKind.Workspace;
            if (name.Equals("REPO.bazel", StringComparison.OrdinalIgnoreCase)) return BazelFileKind.Repo;
            if (Path.GetExtension(name).Equals(".bzl", StringComparison.OrdinalIgnoreCase)) return BazelFileKind.Extension;
            return BazelFileKind.Other;
        }

        /// <summary>The nearest folder at or above <paramref name="folder"/> that holds a workspace root file; null outside a workspace.</summary>
        public static string? FindRoot(string? folder)
        {
            try
            {
                for (var current = folder == null ? null : new DirectoryInfo(folder); current != null; current = current.Parent)
                    if (RootFiles.Any(f => File.Exists(Path.Combine(current.FullName, f)))) return current.FullName;
            }
            catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException || e is NotSupportedException) { }
            return null;
        }

        /// <summary>A Bazel project starts as Bazel when its own folder is a workspace root.</summary>
        public static bool IsWorkspaceRoot(string folder) => RootFiles.Any(f => File.Exists(Path.Combine(folder, f)));

        public static string? BuildFileOf(string folder)
        {
            foreach (var name in BuildFiles)
            {
                var path = Path.Combine(folder, name);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        public static bool IsPackage(string folder) => BuildFileOf(folder) != null;

        /// <summary>The package folder of a file: the nearest folder with a BUILD file, not above <paramref name="root"/>.</summary>
        public static string? PackageFolder(string file, string? root)
        {
            for (var folder = Path.GetDirectoryName(Path.GetFullPath(file)); folder != null; folder = Path.GetDirectoryName(folder))
            {
                if (IsPackage(folder)) return folder;
                if (root != null && string.Equals(folder.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;
            }
            return null;
        }

        /// <summary>The package path of a folder (<c>a/b</c>; empty for the root package).</summary>
        public static string PackagePath(string root, string folder)
        {
            var full = Path.GetFullPath(folder).TrimEnd('\\');
            var rootFull = Path.GetFullPath(root).TrimEnd('\\');
            if (full.Equals(rootFull, StringComparison.OrdinalIgnoreCase)) return "";
            if (!full.StartsWith(rootFull + "\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException(folder + " is outside " + root + ".");
            return full.Substring(rootFull.Length + 1).Replace('\\', '/');
        }

        public static string FolderOf(string root, string packagePath) =>
            packagePath.Length == 0 ? root : Path.Combine(root, packagePath.Replace('/', '\\'));

        /// <summary>
        /// Folders Bazel never treats as source: its output symlinks (<c>bazel-*</c> at the root) and hidden folders.
        /// </summary>
        public static bool IsIgnoredFolder(string name, bool atRoot) =>
            name.StartsWith(".", StringComparison.Ordinal) || atRoot && name.StartsWith("bazel-", StringComparison.OrdinalIgnoreCase)
            || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, (DateTime Time, long Length, StarlarkFile File)> parsed =
            new ConcurrentDictionary<string, (DateTime, long, StarlarkFile)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The parsed file, cached by write time and length; null when it cannot be read.</summary>
        public static StarlarkFile? ParseFile(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                if (parsed.TryGetValue(path, out var cached) && cached.Time == info.LastWriteTimeUtc && cached.Length == info.Length) return cached.File;
                var file = StarlarkFile.Parse(File.ReadAllText(path));
                parsed[path] = (info.LastWriteTimeUtc, info.Length, file);
                return file;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException) { return null; }
        }

        /// <summary>The targets a BUILD file declares: <c>name</c> of every top-level call plus genrule-style outputs.</summary>
        public static IReadOnlyList<BazelTarget> Targets(StarlarkFile file)
        {
            var result = new List<BazelTarget>();
            foreach (var call in file.Calls)
            {
                if (call.Name == "load" || call.Name == "package" || call.Name == "licenses") continue;
                var name = call.StringArgument("name");
                if (name != null) result.Add(new BazelTarget(name.Text, call.Name, name, false));
                foreach (var output in call.Strings("outs").Concat(call.Strings("out")))
                    result.Add(new BazelTarget(output.Text, call.Name, output, true));
                if (call.Name == "exports_files")
                    foreach (var exported in call.Positional.SelectMany(p => p).Where(t => t.Kind == StarlarkTokenKind.String))
                        result.Add(new BazelTarget(exported.Text, call.Name, exported, true));
            }
            return result;
        }

        public static IReadOnlyList<BazelTarget> Targets(string packageFolder)
        {
            var build = BuildFileOf(packageFolder);
            var file = build == null ? null : ParseFile(build);
            return file == null ? Array.Empty<BazelTarget>() : Targets(file);
        }

        /// <summary>
        /// Repository names visible in the main module: <c>module(name, repo_name)</c>, <c>bazel_dep(name, repo_name)</c>,
        /// <c>use_repo</c> names, and WORKSPACE repository rules (any top-level call with a <c>name</c>).
        /// </summary>
        public static IReadOnlyCollection<string> Repositories(string root)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal) { "bazel_tools" };
            var module = ParseFile(Path.Combine(root, "MODULE.bazel"));
            if (module != null)
            {
                foreach (var call in module.Calls)
                {
                    switch (call.Name)
                    {
                        case "module":
                        case "bazel_dep":
                            var repo = call.StringArgument("repo_name") ?? call.StringArgument("name");
                            if (repo != null && repo.Text.Length != 0) names.Add(repo.Text);
                            if (call.Name == "bazel_dep" && call.StringArgument("name")?.Text == "platforms") names.Add("platforms");
                            break;
                        case "use_repo":
                            foreach (var positional in call.Positional.Skip(1).Where(p => p.Count == 1 && p[0].Kind == StarlarkTokenKind.String)) names.Add(positional[0].Text);
                            foreach (var keyword in call.Keywords) names.Add(keyword.Keyword.Text);
                            break;
                        case "local_path_override":
                        case "archive_override":
                        case "git_override":
                            break;
                        default:
                            // use_repo_rule results: http_archive(name = "x") directly in MODULE.bazel.
                            var named = call.StringArgument("name");
                            if (named != null && call.Name != "register_toolchains") names.Add(named.Text);
                            break;
                    }
                }
            }
            foreach (var workspace in new[] { "WORKSPACE.bazel", "WORKSPACE", "WORKSPACE.bzlmod" })
            {
                var file = ParseFile(Path.Combine(root, workspace));
                if (file == null) continue;
                foreach (var call in file.Calls)
                {
                    var named = call.Name == "workspace" ? null : call.StringArgument("name");
                    if (named != null) names.Add(named.Text);
                }
            }
            return names;
        }

        /// <summary>
        /// Repositories can be checked only in a pure Bzlmod workspace: WORKSPACE macros (<c>rules_x_dependencies()</c>)
        /// declare repositories that no file names.
        /// </summary>
        public static bool RepositoriesAreKnown(string root)
        {
            if (!File.Exists(Path.Combine(root, "MODULE.bazel"))) return false;
            foreach (var workspace in new[] { "WORKSPACE.bazel", "WORKSPACE", "WORKSPACE.bzlmod" })
            {
                var file = ParseFile(Path.Combine(root, workspace));
                if (file != null && file.Calls.Count != 0) return false;
            }
            return true;
        }

        /// <summary>Public symbols of a .bzl file: top-level functions and globals not starting with '_'.</summary>
        public static IEnumerable<string> PublicSymbols(StarlarkFile file) =>
            file.Functions.Concat(file.Globals).Select(t => t.Text).Where(n => !n.StartsWith("_", StringComparison.Ordinal)).Distinct();

        /// <summary>The file a label names inside this workspace (main repository only); null for external or invalid labels.</summary>
        public static string? ResolveFile(string root, string? currentPackageFolder, string label)
        {
            var parsedLabel = BazelLabel.Parse(label);
            if (parsedLabel == null || parsedLabel.Repository != null) return null;
            string folder;
            if (parsedLabel.IsAbsolute) folder = FolderOf(root, parsedLabel.Package);
            else if (currentPackageFolder != null) folder = currentPackageFolder;
            else return null;
            return Path.Combine(folder, parsedLabel.Target.Replace('/', '\\'));
        }
    }

    public enum BazelFileKind { Build, Module, Workspace, Repo, Extension, Other }

    public sealed class BazelTarget
    {
        internal BazelTarget(string name, string rule, StarlarkToken token, bool isOutput)
        {
            Name = name; Rule = rule; Token = token; IsOutput = isOutput;
        }

        public string Name { get; }
        /// <summary>The rule or macro that declares it (<c>cc_library</c>).</summary>
        public string Rule { get; }
        public StarlarkToken Token { get; }
        /// <summary>A file the rule writes (<c>outs</c>) or exports, not a <c>name</c>.</summary>
        public bool IsOutput { get; }
    }

    /// <summary>A Bazel label: <c>@repo//pkg:target</c>, <c>//pkg:target</c>, <c>//pkg</c>, <c>:target</c> or a relative <c>target</c>.</summary>
    public sealed class BazelLabel
    {
        private BazelLabel(string? repository, bool isAbsolute, string package, string target)
        {
            Repository = repository; IsAbsolute = isAbsolute; Package = package; Target = target;
        }

        /// <summary>Repository name without '@' (canonical names keep one '@'); null for the main repository.</summary>
        public string? Repository { get; }
        /// <summary>Starts with <c>//</c> (after an optional repository).</summary>
        public bool IsAbsolute { get; }
        public string Package { get; }
        public string Target { get; }

        /// <summary>Parses a label; null when it is malformed (with the reason from <see cref="Problem"/>).</summary>
        public static BazelLabel? Parse(string text) => Problem(text) == null ? ParseCore(text) : null;

        private static BazelLabel ParseCore(string text)
        {
            string? repository = null;
            var rest = text;
            if (rest.StartsWith("@", StringComparison.Ordinal))
            {
                var slash = rest.IndexOf("//", StringComparison.Ordinal);
                var name = slash < 0 ? rest.Substring(1) : rest.Substring(1, slash - 1);
                repository = name;
                if (slash < 0) return new BazelLabel(repository, true, "", name.TrimStart('@'));
                rest = rest.Substring(slash);
            }
            if (rest.StartsWith("//", StringComparison.Ordinal))
            {
                var body = rest.Substring(2);
                var colon = body.IndexOf(':');
                var package = colon < 0 ? body : body.Substring(0, colon);
                var target = colon < 0 ? package.Substring(package.LastIndexOf('/') + 1) : body.Substring(colon + 1);
                return new BazelLabel(repository, true, package, target);
            }
            return new BazelLabel(repository, false, "", rest.StartsWith(":", StringComparison.Ordinal) ? rest.Substring(1) : rest);
        }

        /// <summary>Why <paramref name="text"/> is not a valid label; null when it is.</summary>
        public static string? Problem(string text)
        {
            if (text.Length == 0) return "empty label";
            if (text.Any(c => char.IsControl(c))) return "labels cannot contain control characters";
            var rest = text;
            if (rest.StartsWith("@", StringComparison.Ordinal))
            {
                var slash = rest.IndexOf("//", StringComparison.Ordinal);
                var name = (slash < 0 ? rest.Substring(1) : rest.Substring(1, slash - 1)).TrimStart('@');
                if (name.Length == 0 && slash < 0) return "a repository name is missing after '@'";
                if (name.Any(c => !(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '+' || c == '~')))
                    return "invalid repository name '" + name + "'";
                if (slash < 0) return null;
                rest = rest.Substring(slash);
            }
            string target;
            if (rest.StartsWith("//", StringComparison.Ordinal))
            {
                var body = rest.Substring(2);
                var colon = body.IndexOf(':');
                var package = colon < 0 ? body : body.Substring(0, colon);
                if (package.StartsWith("/", StringComparison.Ordinal) || package.EndsWith("/", StringComparison.Ordinal) || package.Contains("//"))
                    return "invalid package name '" + package + "'";
                if (package.Split('/').Any(s => s == ".." || s == "."))
                    return "package names cannot contain '.' or '..' segments";
                if (colon < 0) return package.Length == 0 ? "'//' needs a package or ':target'" : null;
                target = body.Substring(colon + 1);
                if (target.Length == 0) return "the target name after ':' is empty";
            }
            else target = rest.StartsWith(":", StringComparison.Ordinal) ? rest.Substring(1) : rest;
            if (target.Contains(":")) return "a label has at most one ':'";
            if (target.Length == 0) return "the target name is empty";
            if (target.StartsWith("/", StringComparison.Ordinal) || target.EndsWith("/", StringComparison.Ordinal) || target.Contains("//"))
                return "invalid target name '" + target + "'";
            if (target.Split('/').Any(s => s == ".." || s == "."))
                return "target names cannot contain '.' or '..' segments";
            return null;
        }

        public override string ToString() =>
            (Repository == null ? "" : "@" + Repository) + (IsAbsolute ? "//" + Package + ":" + Target : ":" + Target);
    }
}
