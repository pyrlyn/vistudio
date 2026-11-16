using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Vistudio.Core.Dart.Preview
{
    /// <summary>
    /// What the widget previewer needs from a pubspec.yaml: the package name and whether <c>dependencies:</c> (not only
    /// <c>dev_dependencies:</c>) contains <c>flutter: sdk: flutter</c>. A line-based reader for the block YAML pub writes;
    /// flow mappings (<c>flutter: {sdk: flutter}</c>) are accepted too.
    /// </summary>
    public static class FlutterPubspec
    {
        private static readonly Regex TopKey = new Regex(@"^(?<key>[A-Za-z_][\w-]*)\s*:(?<rest>.*)$", RegexOptions.CultureInvariant);
        private static readonly Regex NestedKey = new Regex(@"^(?<indent>\s+)(?<key>['""]?[A-Za-z_][\w-]*['""]?)\s*:(?<rest>.*)$", RegexOptions.CultureInvariant);
        private static readonly Regex FlowSdk = new Regex(@"^\{\s*['""]?sdk['""]?\s*:\s*['""]?flutter['""]?\s*\}$", RegexOptions.CultureInvariant);
        private static readonly Regex SdkLine = new Regex(@"^\s+['""]?sdk['""]?\s*:\s*['""]?flutter['""]?$", RegexOptions.CultureInvariant);

        /// <summary>The <c>name:</c> value, or null.</summary>
        public static string? PackageName(string pubspec)
        {
            foreach (var line in Lines(pubspec))
            {
                var match = TopKey.Match(line);
                if (match.Success && match.Groups["key"].Value == "name")
                {
                    var value = Unquote(match.Groups["rest"].Value.Trim());
                    return value.Length == 0 ? null : value;
                }
            }
            return null;
        }

        /// <summary>True when the regular <c>dependencies:</c> section depends on the Flutter SDK.</summary>
        public static bool HasFlutterDependency(string pubspec)
        {
            var lines = Lines(pubspec).ToList();
            for (var i = 0; i < lines.Count; i++)
            {
                var top = TopKey.Match(lines[i]);
                if (!top.Success || top.Groups["key"].Value != "dependencies") continue;
                int? childIndent = null;
                for (var j = i + 1; j < lines.Count; j++)
                {
                    var line = lines[j];
                    if (line.Trim().Length == 0) continue;
                    if (!char.IsWhiteSpace(line[0])) break; // next top-level key ends the section
                    var nested = NestedKey.Match(line);
                    if (!nested.Success) continue;
                    var indent = nested.Groups["indent"].Value.Length;
                    childIndent ??= indent;
                    if (indent != childIndent || Unquote(nested.Groups["key"].Value) != "flutter") continue;
                    var rest = nested.Groups["rest"].Value.Trim();
                    if (rest.Length > 0) return FlowSdk.IsMatch(rest);
                    // Block form: the next more-indented lines belong to the flutter entry.
                    for (var k = j + 1; k < lines.Count; k++)
                    {
                        var inner = lines[k];
                        if (inner.Trim().Length == 0) continue;
                        var innerIndent = inner.Length - inner.TrimStart().Length;
                        if (innerIndent <= indent) break;
                        if (SdkLine.IsMatch(inner)) return true;
                    }
                    return false;
                }
            }
            return false;
        }

        /// <summary>Lines with comments removed (a <c>#</c> outside quotes starts a comment) and trailing blanks trimmed.</summary>
        private static IEnumerable<string> Lines(string text)
        {
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var quote = '\0';
                var end = raw.Length;
                for (var i = 0; i < raw.Length; i++)
                {
                    var c = raw[i];
                    if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                    if (c == '\'' || c == '"') quote = c;
                    else if (c == '#' && (i == 0 || char.IsWhiteSpace(raw[i - 1]))) { end = i; break; }
                }
                yield return raw.Substring(0, end).TrimEnd();
            }
        }

        private static string Unquote(string value) =>
            value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[value.Length - 1] == value[0] ? value.Substring(1, value.Length - 2) : value;
    }

    /// <summary>A Flutter package found in the workspace: a folder with a pubspec.yaml that depends on the Flutter SDK.</summary>
    public sealed class FlutterProjectInfo
    {
        public FlutterProjectInfo(string root, string name) { Root = root; Name = name; }
        public string Root { get; }
        public string Name { get; }
        public override string ToString() => Name + " (" + Root + ")";
    }

    /// <summary>Finds the Flutter packages of a solution or Open Folder workspace.</summary>
    public static class FlutterWorkspace
    {
        /// <summary>Folders that never hold the user's packages: tool output, caches, platform build trees and generated previewer projects.</summary>
        private static readonly HashSet<string> Skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".dart_tool", ".git", ".hg", ".svn", ".vs", ".idea", ".vscode", ".fvm", ".symlinks", ".plugin_symlinks", ".pub-cache",
            "build", "node_modules", "Pods", "ephemeral", "bin", "obj", "out", ".gradle", "widget_preview_scaffold", ".widget_preview",
        };

        /// <summary>
        /// Every Flutter package at or below <paramref name="roots"/> (solution folder, project folders, Open Folder root),
        /// at most <paramref name="maxDepth"/> folders deep, sorted by path. Unreadable folders are skipped.
        /// </summary>
        public static IReadOnlyList<FlutterProjectInfo> FindProjects(IEnumerable<string> roots, int maxDepth = 6)
        {
            var found = new Dictionary<string, FlutterProjectInfo>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
            {
                string full;
                try { full = Path.GetFullPath(root); }
                catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { continue; }
                if (File.Exists(full)) full = Path.GetDirectoryName(full)!;
                Walk(full, 0, maxDepth, found, visited);
            }
            return found.Values.OrderBy(p => p.Root, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The Flutter package containing <paramref name="path"/> (a file or folder), or null when its package is not a Flutter package.</summary>
        public static FlutterProjectInfo? ProjectOf(string path)
        {
            var root = DartProject.FindRoot(path);
            return root == null ? null : Read(root);
        }

        /// <summary>The package in <paramref name="folder"/> when its pubspec.yaml depends on Flutter.</summary>
        public static FlutterProjectInfo? Read(string folder)
        {
            try
            {
                var pubspec = File.ReadAllText(Path.Combine(folder, "pubspec.yaml"));
                if (!FlutterPubspec.HasFlutterDependency(pubspec)) return null;
                return new FlutterProjectInfo(Path.GetFullPath(folder), FlutterPubspec.PackageName(pubspec) ?? Path.GetFileName(folder.TrimEnd('\\', '/')));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException) { return null; }
        }

        private static void Walk(string folder, int depth, int maxDepth, Dictionary<string, FlutterProjectInfo> found, HashSet<string> visited)
        {
            if (!visited.Add(folder)) return;
            if (File.Exists(Path.Combine(folder, "pubspec.yaml")))
            {
                var project = Read(folder);
                if (project != null) found[project.Root] = project;
            }
            if (depth >= maxDepth) return;
            string[] children;
            try { children = Directory.GetDirectories(folder); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return; }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (Skipped.Contains(name)) continue;
                try
                {
                    // Junctions and symlinks (pub workspaces, plugin links) can loop; follow real folders only.
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { continue; }
                Walk(child, depth + 1, maxDepth, found, visited);
            }
        }
    }
}
