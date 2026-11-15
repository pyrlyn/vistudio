using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Vistudio.Core.Bazel
{
    public enum BazelSeverity { Error, Warning, Suggestion }

    /// <summary>One problem in a Starlark file. Line and column are zero-based; the span covers <see cref="Length"/> characters.</summary>
    public sealed class BazelDiagnostic
    {
        public BazelDiagnostic(int line, int column, int length, BazelSeverity severity, string code, string message, string source = "Bazel")
        {
            Line = line; Column = column; Length = Math.Max(1, length); Severity = severity; Code = code; Message = message; Source = source;
        }

        public int Line { get; }
        public int Column { get; }
        public int Length { get; }
        public BazelSeverity Severity { get; }
        /// <summary>Short id of the check (<c>duplicate-target</c>) or the buildifier warning category.</summary>
        public string Code { get; }
        public string Message { get; }
        /// <summary>"Bazel" for Vistudio's own checks, "buildifier" for lint results.</summary>
        public string Source { get; }

        public override string ToString() => (Line + 1) + ":" + (Column + 1) + " " + Severity + " " + Code + ": " + Message;
    }

    /// <summary>
    /// Vistudio's own checks of a BUILD, MODULE.bazel, WORKSPACE or .bzl file, fast enough to run while typing and
    /// without Bazel installed: Starlark syntax (strings, brackets, Python statements Starlark does not have), load()
    /// statements, duplicate target names, malformed labels, and labels or files that do not exist in the workspace.
    /// Targets created by macros are unknown, so a missing <c>:target</c> is only a suggestion.
    /// </summary>
    public static class BazelValidator
    {
        private static readonly HashSet<string> PythonOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            "class", "import", "try", "except", "finally", "raise", "while", "with", "yield", "global", "nonlocal", "async", "await", "del",
        };

        /// <param name="path">The file's path; used for its kind and package. The file need not exist.</param>
        /// <param name="text">The current text (unsaved edits included).</param>
        public static IReadOnlyList<BazelDiagnostic> Validate(string path, string text)
        {
            var diagnostics = new List<BazelDiagnostic>();
            var tokens = StarlarkLexer.Tokenize(text);
            Syntax(tokens, diagnostics);
            var kind = BazelWorkspace.KindOf(path);
            var file = StarlarkFile.Parse(tokens);
            foreach (var function in file.Functions)
                if (kind == BazelFileKind.Build || kind == BazelFileKind.Module || kind == BazelFileKind.Workspace)
                    diagnostics.Add(At(function, BazelSeverity.Error, "def-in-build", "Functions can only be defined in .bzl files; move '" + function.Text + "' to a .bzl file and load() it."));
            foreach (var load in file.Loads) Load(load, kind, diagnostics);

            var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var root = BazelWorkspace.FindRoot(folder);
            if (kind == BazelFileKind.Build)
            {
                var targets = BazelWorkspace.Targets(file);
                foreach (var duplicate in targets.Where(t => !t.IsOutput).GroupBy(t => t.Name).Where(g => g.Count() > 1))
                    foreach (var target in duplicate.Skip(1))
                        diagnostics.Add(At(target.Token, BazelSeverity.Error, "duplicate-target", "Target '" + target.Name + "' is already defined in this package (line " + (duplicate.First().Token.Line + 1) + ")."));
                Labels(file, targets, folder, root, diagnostics);
            }
            else if (kind == BazelFileKind.Module && root != null)
                foreach (var call in file.Calls.Where(c => c.Name == "register_toolchains" || c.Name == "register_execution_platforms"))
                    foreach (var token in call.Positional.SelectMany(p => p).Where(t => t.Kind == StarlarkTokenKind.String))
                        Label(token, token.Text, null, folder, root, BazelSeverity.Warning, diagnostics, allowPatterns: true);
            return diagnostics.OrderBy(d => d.Line).ThenBy(d => d.Column).ToList();
        }

        private static void Syntax(IReadOnlyList<StarlarkToken> tokens, List<BazelDiagnostic> diagnostics)
        {
            var open = new Stack<StarlarkToken>();
            var statementStart = true;
            foreach (var token in tokens)
            {
                switch (token.Kind)
                {
                    case StarlarkTokenKind.String when !token.Closed:
                        diagnostics.Add(At(token, BazelSeverity.Error, "unterminated-string", "This string has no closing quote."));
                        break;
                    case StarlarkTokenKind.Open:
                        open.Push(token);
                        break;
                    case StarlarkTokenKind.Close:
                        if (open.Count == 0)
                            diagnostics.Add(At(token, BazelSeverity.Error, "unbalanced", "'" + token.Text + "' has no matching opening bracket."));
                        else if (Closing(open.Peek().Text) != token.Text)
                        {
                            var opened = open.Pop();
                            diagnostics.Add(At(token, BazelSeverity.Error, "unbalanced", "'" + token.Text + "' closes '" + opened.Text + "' from line " + (opened.Line + 1) + "; expected '" + Closing(opened.Text) + "'."));
                        }
                        else open.Pop();
                        break;
                    case StarlarkTokenKind.Identifier when statementStart && PythonOnly.Contains(token.Text):
                        diagnostics.Add(At(token, BazelSeverity.Error, "not-starlark", "'" + token.Text + "' is Python; Starlark has no " + token.Text + " statement."));
                        break;
                }
                statementStart = token.Kind == StarlarkTokenKind.Newline || token.Kind == StarlarkTokenKind.Colon && open.Count == 0;
            }
            foreach (var unclosed in open)
                diagnostics.Add(At(unclosed, BazelSeverity.Error, "unbalanced", "'" + unclosed.Text + "' is never closed."));
        }

        private static string Closing(string open) => open == "(" ? ")" : open == "[" ? "]" : "}";

        private static void Load(StarlarkCall load, BazelFileKind kind, List<BazelDiagnostic> diagnostics)
        {
            if (kind == BazelFileKind.Module)
            {
                diagnostics.Add(At(load.Function, BazelSeverity.Error, "load-in-module", "MODULE.bazel cannot load(); use use_extension() or use_repo_rule()."));
                return;
            }
            var first = load.Positional.FirstOrDefault();
            if (first == null || first.Count != 1 || first[0].Kind != StarlarkTokenKind.String)
            {
                diagnostics.Add(At(load.Function, BazelSeverity.Error, "load", "load() needs the .bzl file's label as its first argument, as a string."));
                return;
            }
            var label = first[0];
            var problem = BazelLabel.Problem(label.Text);
            if (problem != null) diagnostics.Add(At(label, BazelSeverity.Error, "invalid-label", "Invalid label: " + problem + "."));
            else if (!label.Text.EndsWith(".bzl", StringComparison.Ordinal))
                diagnostics.Add(At(label, BazelSeverity.Error, "load", "load() reads .bzl files; '" + label.Text + "' does not end in .bzl."));
            if (load.Positional.Count < 2 && load.Keywords.Count == 0)
                diagnostics.Add(At(load.Function, BazelSeverity.Error, "load", "load() must name at least one symbol to load."));
            foreach (var symbol in load.Positional.Skip(1).Select(p => p.FirstOrDefault()).Where(t => t != null && t.Kind == StarlarkTokenKind.String)
                         .Concat(load.Keywords.Select(k => k.Value.FirstOrDefault()).Where(t => t != null && t.Kind == StarlarkTokenKind.String)))
                if (symbol!.Text.StartsWith("_", StringComparison.Ordinal))
                    diagnostics.Add(At(symbol, BazelSeverity.Error, "private-symbol", "'" + symbol.Text + "' is private to its .bzl file and cannot be loaded."));
        }

        private static void Labels(StarlarkFile file, IReadOnlyList<BazelTarget> targets, string folder, string? root, List<BazelDiagnostic> diagnostics)
        {
            var names = new HashSet<string>(targets.Select(t => t.Name), StringComparer.Ordinal);
            foreach (var call in file.Calls)
            {
                foreach (var (keyword, value) in call.Keywords)
                {
                    var isFile = BazelRules.FileAttributes.Contains(keyword.Text);
                    var isLabel = BazelRules.LabelAttributes.Contains(keyword.Text);
                    if (!isFile && !isLabel && keyword.Text != "visibility") continue;
                    foreach (var token in Literals(value))
                    {
                        if (keyword.Text == "visibility") { Visibility(token, folder, root, diagnostics); continue; }
                        if (isFile && !token.Text.StartsWith("//", StringComparison.Ordinal) && !token.Text.StartsWith("@", StringComparison.Ordinal) && !token.Text.StartsWith(":", StringComparison.Ordinal))
                        {
                            LocalFile(token, names, folder, diagnostics);
                            continue;
                        }
                        Label(token, token.Text, names, folder, root, BazelSeverity.Suggestion, diagnostics, allowPatterns: false);
                    }
                }
                if (BazelRules.FileFunctions.Contains(call.Name))
                    foreach (var token in call.Positional.SelectMany(Literals))
                        LocalFile(token, new HashSet<string>(), folder, diagnostics);
            }
        }

        /// <summary>String literals that are values: list items, select() branch values; not glob() patterns or select() keys.</summary>
        private static IEnumerable<StarlarkToken> Literals(IReadOnlyList<StarlarkToken> value)
        {
            var skipDepth = -1;
            var depth = 0;
            for (var i = 0; i < value.Count; i++)
            {
                var token = value[i];
                if (token.Kind == StarlarkTokenKind.Open)
                {
                    // glob(...) arguments are patterns; select({...}) keys are config_setting labels checked as labels.
                    if (skipDepth < 0 && i > 0 && value[i - 1].Kind == StarlarkTokenKind.Identifier && value[i - 1].Text == "glob") skipDepth = depth;
                    depth++;
                    continue;
                }
                if (token.Kind == StarlarkTokenKind.Close)
                {
                    depth--;
                    if (depth == skipDepth) skipDepth = -1;
                    continue;
                }
                if (skipDepth >= 0 || token.Kind != StarlarkTokenKind.String || !token.Closed) continue;
                if (token.Text == "//conditions:default") continue;
                // A string followed by '+' or '%' is part of an expression, not a literal label.
                if (i + 1 < value.Count && value[i + 1].Kind == StarlarkTokenKind.Operator) continue;
                if (i > 0 && value[i - 1].Kind == StarlarkTokenKind.Operator) continue;
                if (token.Text.Contains("$(") || token.Text.Contains("{")) continue;
                yield return token;
            }
        }

        private static void LocalFile(StarlarkToken token, HashSet<string> targets, string folder, List<BazelDiagnostic> diagnostics)
        {
            var name = token.Text;
            if (name.IndexOfAny(new[] { '*', '?' }) >= 0) return;
            var problem = BazelLabel.Problem(name);
            if (problem != null) { diagnostics.Add(At(token, BazelSeverity.Error, "invalid-label", "Invalid label: " + problem + ".")); return; }
            if (targets.Contains(name)) return;
            string full;
            try { full = Path.Combine(folder, name.Replace('/', '\\')); }
            catch (ArgumentException) { return; }
            // A file in a subpackage belongs to that package: Bazel reports "crosses boundary of subpackage".
            var parent = Path.GetDirectoryName(full);
            while (parent != null && parent.Length > folder.TrimEnd('\\').Length)
            {
                if (BazelWorkspace.IsPackage(parent))
                {
                    var package = name.Substring(0, parent.Length - folder.TrimEnd('\\').Length - 1).Replace('\\', '/');
                    diagnostics.Add(At(token, BazelSeverity.Error, "crosses-package", "'" + name + "' is inside the subpackage '" + package + "'; refer to it through that package's label."));
                    return;
                }
                parent = Path.GetDirectoryName(parent);
            }
            if (File.Exists(full)) return;
            if (Directory.Exists(full))
            {
                diagnostics.Add(At(token, BazelSeverity.Warning, "directory-label", "'" + name + "' is a folder; list its files or use glob([\"" + name + "/**\"])."));
                return;
            }
            diagnostics.Add(At(token, BazelSeverity.Warning, "missing-file", "No file '" + name + "' in this package and no target of that name."));
        }

        private static void Label(StarlarkToken token, string text, HashSet<string>? localTargets, string folder, string? root, BazelSeverity missingTarget,
            List<BazelDiagnostic> diagnostics, bool allowPatterns)
        {
            if (allowPatterns && (text.EndsWith("/...", StringComparison.Ordinal) || text.EndsWith(":all", StringComparison.Ordinal) || text.EndsWith(":*", StringComparison.Ordinal)))
            {
                // Target patterns: //pkg/... needs the folder, //pkg:all the package.
                if (root == null || !text.StartsWith("//", StringComparison.Ordinal)) return;
                var recursive = text.EndsWith("/...", StringComparison.Ordinal);
                var package = recursive ? text.Substring(2, text.Length - 6) : text.Substring(2, text.LastIndexOf(':') - 2);
                var patternFolder = BazelWorkspace.FolderOf(root, package);
                if (recursive ? !Directory.Exists(patternFolder) : !BazelWorkspace.IsPackage(patternFolder))
                    diagnostics.Add(At(token, BazelSeverity.Warning, "missing-package", (recursive ? "Folder //" : "Package //") + package + " does not exist."));
                return;
            }
            var problem = BazelLabel.Problem(text);
            if (problem != null) { diagnostics.Add(At(token, BazelSeverity.Error, "invalid-label", "Invalid label: " + problem + ".")); return; }
            var label = BazelLabel.Parse(text)!;
            if (label.Repository != null)
            {
                var name = label.Repository;
                if (root == null || name.StartsWith("@", StringComparison.Ordinal) || name.Length == 0 || !BazelWorkspace.RepositoriesAreKnown(root)) return;
                if (!BazelWorkspace.Repositories(root).Contains(name))
                    diagnostics.Add(At(token, BazelSeverity.Warning, "unknown-repository", "Repository '@" + name + "' is not declared in MODULE.bazel (bazel_dep or use_repo)."));
                return;
            }
            if (label.IsAbsolute)
            {
                if (root == null) return;
                var packageFolder = BazelWorkspace.FolderOf(root, label.Package);
                if (!BazelWorkspace.IsPackage(packageFolder))
                {
                    diagnostics.Add(At(token, BazelSeverity.Warning, "missing-package", Directory.Exists(packageFolder)
                        ? "//" + label.Package + " is a folder without a BUILD file, so it is not a package."
                        : "Package //" + label.Package + " does not exist."));
                    return;
                }
                if (label.Target.StartsWith("__", StringComparison.Ordinal)) return; // __pkg__, __subpackages__
                var known = BazelWorkspace.Targets(packageFolder).Any(t => t.Name == label.Target) || File.Exists(Path.Combine(packageFolder, label.Target.Replace('/', '\\')));
                if (!known)
                    diagnostics.Add(At(token, BazelSeverity.Suggestion, "unknown-target", "//" + label.Package + " declares no target or file '" + label.Target + "' (targets from macros are not known)."));
                return;
            }
            if (localTargets == null || localTargets.Contains(label.Target)) return;
            if (File.Exists(Path.Combine(folder, label.Target.Replace('/', '\\')))) return;
            diagnostics.Add(At(token, missingTarget, "unknown-target", "This package declares no target or file '" + label.Target + "'" + (missingTarget == BazelSeverity.Suggestion ? " (targets from macros are not known)." : ".")));
        }

        private static void Visibility(StarlarkToken token, string folder, string? root, List<BazelDiagnostic> diagnostics)
        {
            var text = token.Text;
            if (text == "//visibility:public" || text == "//visibility:private") return;
            if (text.StartsWith("//visibility:", StringComparison.Ordinal))
            {
                diagnostics.Add(At(token, BazelSeverity.Error, "visibility", "Use //visibility:public or //visibility:private."));
                return;
            }
            Label(token, text, null, folder, root, BazelSeverity.Suggestion, diagnostics, allowPatterns: false);
        }

        private static BazelDiagnostic At(StarlarkToken token, BazelSeverity severity, string code, string message) =>
            new BazelDiagnostic(token.Line, token.Column, token.End - token.Start, severity, code, message);
    }
}
