using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Vistudio.Core.Bazel
{
    public enum BazelCompletionKind { Rule, Function, Attribute, Keyword, Symbol, File, Folder, Package, Target, Repository, Value }

    public sealed class BazelCompletionItem
    {
        public BazelCompletionItem(string label, string insertText, BazelCompletionKind kind, string description)
        {
            Label = label; InsertText = insertText; Kind = kind; Description = description;
        }

        /// <summary>Text shown in the list; also what typing filters on.</summary>
        public string Label { get; }
        public string InsertText { get; }
        public BazelCompletionKind Kind { get; }
        public string Description { get; }

        public override string ToString() => Kind + " " + Label;
    }

    /// <summary>Completion items and the text they replace: <see cref="Start"/> up to the caret.</summary>
    public sealed class BazelCompletionResult
    {
        internal BazelCompletionResult(int start, int caret, bool inString, IReadOnlyList<BazelCompletionItem> items)
        {
            Start = start; Length = caret - start; InString = inString; Items = items;
        }

        public int Start { get; }
        public int Length { get; }
        /// <summary>The caret is inside a string literal (labels, files); otherwise names (rules, attributes).</summary>
        public bool InString { get; }
        public IReadOnlyList<BazelCompletionItem> Items { get; }
    }

    /// <summary>
    /// Completion in Starlark files from the workspace on disk: files and folders of the package in <c>srcs</c>-like
    /// attributes and <c>glob()</c>, <c>//package</c> paths, <c>:target</c> and <c>//package:target</c> labels,
    /// <c>@repository</c> names, .bzl labels and their public symbols in <c>load()</c>, visibility values, rules and
    /// functions at the start of a statement, and attribute names inside a call.
    /// </summary>
    public static class BazelCompletion
    {
        private const int MaxItems = 400;

        private sealed class Frame
        {
            public Frame(string open, string? call) { Open = open; Call = call; }
            public string Open { get; }
            public string? Call { get; }
            public string? Keyword;
            public int ArgumentIndex;
            public bool AtArgumentStart = true;
            public string? FirstString;
            public readonly HashSet<string> UsedKeywords = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Where completion would apply, without reading the disk (for the editor's UI thread): the start of the string
        /// value or name before the caret; null where Vistudio offers nothing (after a dot, a number, a closed string).
        /// </summary>
        public static int? ApplicableStart(string text, int caret, bool names = true)
        {
            caret = Math.Max(0, Math.Min(caret, text.Length));
            var tokens = StarlarkLexer.Tokenize(text.Substring(0, caret));
            var last = tokens.Count == 0 ? null : tokens[tokens.Count - 1];
            if (last != null && last.End == caret)
            {
                if (last.Kind == StarlarkTokenKind.String) return last.Closed ? (int?)null : last.ValueStart;
                if (last.Kind == StarlarkTokenKind.Number || last.Kind == StarlarkTokenKind.Dot) return null;
                if (last.Kind == StarlarkTokenKind.Identifier)
                {
                    var before = tokens.Count > 1 ? tokens[tokens.Count - 2] : null;
                    return names && !(before != null && before.Kind == StarlarkTokenKind.Dot) ? last.Start : (int?)null;
                }
            }
            return names ? caret : (int?)null;
        }

        /// <param name="names">False leaves out rules, functions, attributes and symbols (a language server supplies them).</param>
        public static BazelCompletionResult? Complete(string path, string text, int caret, bool names = true)
        {
            caret = Math.Max(0, Math.Min(caret, text.Length));
            var tokens = StarlarkLexer.Tokenize(text.Substring(0, caret));
            var last = tokens.Count == 0 ? null : tokens[tokens.Count - 1];
            var inString = last != null && last.Kind == StarlarkTokenKind.String && !last.Closed && last.End == caret;
            var inName = last != null && last.Kind == StarlarkTokenKind.Identifier && last.End == caret;
            if (last != null && !inString && !inName && last.End == caret && (last.Kind == StarlarkTokenKind.String || last.Kind == StarlarkTokenKind.Number)) return null;
            if (last != null && last.Kind == StarlarkTokenKind.Dot && last.End == caret) return null; // members: the language server's business
            var context = inString || inName ? tokens.Take(tokens.Count - 1).ToList() : tokens.ToList();
            var frames = Frames(context);
            var previous = context.Count == 0 ? null : context[context.Count - 1];
            if (inName && previous != null && previous.Kind == StarlarkTokenKind.Dot) return null;

            var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var root = BazelWorkspace.FindRoot(folder);
            var kind = BazelWorkspace.KindOf(path);
            var items = new List<BazelCompletionItem>();
            int start;
            if (inString)
            {
                start = last!.ValueStart;
                var prefix = text.Substring(start, caret - start);
                if (prefix.Contains("\\")) return null;
                Strings(items, frames, prefix, path, folder, root);
            }
            else
            {
                if (!names) return null;
                start = inName ? last!.Start : caret;
                Names(items, frames, previous, kind, text);
            }
            return items.Count == 0 ? null : new BazelCompletionResult(start, caret, inString,
                items.GroupBy(i => i.InsertText).Select(g => g.First()).Take(MaxItems).ToList());
        }

        private static List<Frame> Frames(IReadOnlyList<StarlarkToken> tokens)
        {
            var stack = new List<Frame>();
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                var top = stack.Count == 0 ? null : stack[stack.Count - 1];
                switch (token.Kind)
                {
                    case StarlarkTokenKind.Open:
                        var call = token.Text == "(" && i > 0 && tokens[i - 1].Kind == StarlarkTokenKind.Identifier ? tokens[i - 1].Text : null;
                        if (top != null) top.AtArgumentStart = false;
                        stack.Add(new Frame(token.Text, call));
                        continue;
                    case StarlarkTokenKind.Close:
                        if (stack.Count != 0) stack.RemoveAt(stack.Count - 1);
                        if (stack.Count != 0) stack[stack.Count - 1].AtArgumentStart = false;
                        continue;
                    case StarlarkTokenKind.Comma:
                        if (top != null) { top.Keyword = null; top.ArgumentIndex++; top.AtArgumentStart = true; }
                        continue;
                    case StarlarkTokenKind.Newline:
                        stack.Clear();
                        continue;
                }
                if (top == null) continue;
                if (top.Open == "(" && top.AtArgumentStart && token.Kind == StarlarkTokenKind.Identifier
                    && i + 1 < tokens.Count && tokens[i + 1].Kind == StarlarkTokenKind.Equals)
                {
                    top.Keyword = token.Text;
                    top.UsedKeywords.Add(token.Text);
                    top.AtArgumentStart = false;
                    i++;
                    continue;
                }
                if (token.Kind == StarlarkTokenKind.String && top.Open == "(" && top.ArgumentIndex == 0 && top.Keyword == null) top.FirstString ??= token.Text;
                top.AtArgumentStart = false;
            }
            return stack;
        }

        private static void Names(List<BazelCompletionItem> items, List<Frame> frames, StarlarkToken? previous, BazelFileKind kind, string text)
        {
            var top = frames.Count == 0 ? null : frames[frames.Count - 1];
            var file = StarlarkFile.Parse(text);
            if (top != null && top.Open == "(" && top.Call != null && top.AtArgumentStart && top.Call != "load")
            {
                foreach (var attribute in BazelRules.AttributesOf(top.Call).Where(a => !top.UsedKeywords.Contains(a)))
                    items.Add(new BazelCompletionItem(attribute, attribute + " = ", BazelCompletionKind.Attribute, top.Call + " attribute"));
                if (items.Count != 0) return;
            }
            var statementStart = top == null && (previous == null || previous.Kind == StarlarkTokenKind.Newline || previous.Kind == StarlarkTokenKind.Colon);
            if (top == null && !statementStart && previous != null && previous.Kind != StarlarkTokenKind.Equals && previous.Kind != StarlarkTokenKind.Operator) return;
            foreach (var function in BazelRules.For(kind))
            {
                var isRule = BazelRules.IsRule(function);
                if (statementStart && function.Description == "Starlark built-in function.") continue;
                items.Add(new BazelCompletionItem(function.Name, function.Name, isRule ? BazelCompletionKind.Rule : BazelCompletionKind.Function, function.Description));
            }
            foreach (var symbol in file.LoadedSymbols.Distinct())
                items.Add(new BazelCompletionItem(symbol, symbol, BazelCompletionKind.Symbol, "loaded with load()"));
            if (kind == BazelFileKind.Extension || kind == BazelFileKind.Other)
            {
                foreach (var symbol in file.Functions.Concat(file.Globals).Select(t => t.Text).Distinct())
                    items.Add(new BazelCompletionItem(symbol, symbol, BazelCompletionKind.Symbol, "defined in this file"));
                if (statementStart)
                    foreach (var keyword in new[] { "def", "if", "for", "return", "pass", "load" })
                        items.Add(new BazelCompletionItem(keyword, keyword, BazelCompletionKind.Keyword, "Starlark"));
            }
            if (!statementStart)
                foreach (var value in new[] { "True", "False", "None" })
                    items.Add(new BazelCompletionItem(value, value, BazelCompletionKind.Value, "Starlark constant"));
        }

        private static void Strings(List<BazelCompletionItem> items, List<Frame> frames, string prefix, string path, string folder, string? root)
        {
            var calls = frames.Where(f => f.Open == "(").Reverse().ToList();
            var inner = calls.FirstOrDefault();
            var attribute = calls.Select(f => f.Keyword).FirstOrDefault(k => k != null);
            var packageFolder = root == null ? folder : BazelWorkspace.PackageFolder(path, root) ?? folder;
            if (inner?.Call == "load" || inner?.Call == "use_extension" || inner?.Call == "use_repo_rule")
            {
                if (inner.ArgumentIndex == 0 && inner.Keyword == null)
                {
                    Labels(items, prefix, root, packageFolder, bzlOnly: true, files: true);
                    return;
                }
                if (inner.Call == "load" && inner.FirstString != null) LoadedFileSymbols(items, inner.FirstString, root, packageFolder);
                return;
            }
            if (attribute == "name" || inner?.Call is "bazel_dep" or "module" or "use_repo" or "single_version_override" or "git_override" or "archive_override") return;
            if (attribute == "visibility" || attribute == "default_visibility")
            {
                foreach (var value in new[] { "//visibility:public", "//visibility:private" })
                    items.Add(new BazelCompletionItem(value, value, BazelCompletionKind.Value, value.EndsWith("public") ? "visible to every package" : "visible in this package only"));
                if (root != null && prefix.StartsWith("//", StringComparison.Ordinal) && prefix.Contains(":"))
                {
                    var package = prefix.Substring(2, prefix.IndexOf(':') - 2);
                    foreach (var special in new[] { "__pkg__", "__subpackages__" })
                        items.Add(new BazelCompletionItem("//" + package + ":" + special, "//" + package + ":" + special, BazelCompletionKind.Value,
                            special == "__pkg__" ? "this package" : "this package and its subpackages"));
                }
            }
            var isGlob = inner?.Call == "glob";
            var wantsFiles = isGlob || attribute != null && BazelRules.FileAttributes.Contains(attribute) || inner?.Call != null && BazelRules.FileFunctions.Contains(inner.Call);
            var wantsLabels = attribute != null && (BazelRules.LabelAttributes.Contains(attribute) || attribute.EndsWith("deps", StringComparison.Ordinal));
            if (prefix.StartsWith("@", StringComparison.Ordinal) || prefix.StartsWith("//", StringComparison.Ordinal) || prefix.StartsWith(":", StringComparison.Ordinal))
            {
                if (!isGlob) Labels(items, prefix, root, packageFolder, bzlOnly: false, files: wantsFiles);
                return;
            }
            if (wantsFiles) PackageFiles(items, packageFolder, prefix, "", null);
            if ((wantsFiles && !isGlob || wantsLabels) && prefix.Length == 0)
                foreach (var target in LocalTargets(packageFolder, path))
                    items.Add(target);
        }

        private static IEnumerable<BazelCompletionItem> LocalTargets(string packageFolder, string path)
        {
            // The open document's own BUILD file is read from disk; unsaved targets appear after the next save.
            return BazelWorkspace.Targets(packageFolder).Where(t => !t.IsOutput)
                .Select(t => new BazelCompletionItem(":" + t.Name, ":" + t.Name, BazelCompletionKind.Target, t.Rule + " in this package"));
        }

        private static void Labels(List<BazelCompletionItem> items, string prefix, string? root, string packageFolder, bool bzlOnly, bool files)
        {
            if (prefix.StartsWith("@", StringComparison.Ordinal))
            {
                if (prefix.Contains("//") || root == null) return;
                foreach (var repository in BazelWorkspace.Repositories(root))
                    items.Add(new BazelCompletionItem("@" + repository + "//", "@" + repository + "//", BazelCompletionKind.Repository, "repository"));
                return;
            }
            if (prefix.StartsWith(":", StringComparison.Ordinal))
            {
                if (!bzlOnly)
                    foreach (var target in BazelWorkspace.Targets(packageFolder).Where(t => !t.IsOutput || files))
                        items.Add(new BazelCompletionItem(":" + target.Name, ":" + target.Name, BazelCompletionKind.Target, target.Rule));
                if (files || bzlOnly) PackageFiles(items, packageFolder, prefix.Substring(1), ":", bzlOnly ? ".bzl" : null);
                return;
            }
            if (root == null) return;
            var body = prefix.Substring(2);
            var colon = body.IndexOf(':');
            if (colon >= 0)
            {
                var package = body.Substring(0, colon);
                var target = BazelWorkspace.FolderOf(root, package);
                if (!BazelWorkspace.IsPackage(target)) return;
                var head = "//" + package + ":";
                if (!bzlOnly)
                    foreach (var declared in BazelWorkspace.Targets(target).Where(t => !t.IsOutput || files))
                        items.Add(new BazelCompletionItem(head + declared.Name, head + declared.Name, BazelCompletionKind.Target, declared.Rule + " in //" + package));
                if (files || bzlOnly) PackageFiles(items, target, body.Substring(colon + 1), head, bzlOnly ? ".bzl" : null);
                return;
            }
            var slash = body.LastIndexOf('/');
            var parent = slash < 0 ? "" : body.Substring(0, slash);
            var parentFolder = BazelWorkspace.FolderOf(root, parent);
            if (parent.Length == 0 && BazelWorkspace.IsPackage(root))
                items.Add(new BazelCompletionItem("//:", "//:", BazelCompletionKind.Package, "the root package"));
            if (parent.Length != 0 && BazelWorkspace.IsPackage(parentFolder))
                items.Add(new BazelCompletionItem("//" + parent + ":", "//" + parent + ":", BazelCompletionKind.Package, "targets of //" + parent));
            foreach (var child in Subfolders(parentFolder, parent.Length == 0))
            {
                var package = (parent.Length == 0 ? "" : parent + "/") + Path.GetFileName(child);
                if (BazelWorkspace.IsPackage(child))
                    items.Add(new BazelCompletionItem("//" + package, "//" + package, BazelCompletionKind.Package, "package"));
                if (HasSubfolders(child))
                    items.Add(new BazelCompletionItem("//" + package + "/", "//" + package + "/", BazelCompletionKind.Folder, "folder"));
            }
        }

        /// <summary>Files and folders of a package below <paramref name="relative"/>'s folder; subpackages are left out.</summary>
        private static void PackageFiles(List<BazelCompletionItem> items, string packageFolder, string relative, string head, string? extension)
        {
            var slash = relative.LastIndexOf('/');
            var directory = slash < 0 ? "" : relative.Substring(0, slash + 1);
            string folder;
            try { folder = Path.Combine(packageFolder, directory.Replace('/', '\\')); }
            catch (ArgumentException) { return; }
            if (!Directory.Exists(folder)) return;
            foreach (var child in Subfolders(folder, false))
            {
                if (BazelWorkspace.IsPackage(child)) continue; // another package's files need its label
                var value = head + directory + Path.GetFileName(child) + "/";
                items.Add(new BazelCompletionItem(value, value, BazelCompletionKind.Folder, "folder"));
            }
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder).ToList(); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return; }
            foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith(".", StringComparison.Ordinal) || BazelWorkspace.BuildFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (extension != null && !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
                var value = head + directory + name;
                items.Add(new BazelCompletionItem(value, value, BazelCompletionKind.File, "file"));
            }
        }

        private static void LoadedFileSymbols(List<BazelCompletionItem> items, string label, string? root, string packageFolder)
        {
            if (root == null) return;
            var file = BazelWorkspace.ResolveFile(root, packageFolder, label);
            var parsed = file == null ? null : BazelWorkspace.ParseFile(file);
            if (parsed == null) return;
            foreach (var symbol in BazelWorkspace.PublicSymbols(parsed))
                items.Add(new BazelCompletionItem(symbol, symbol, BazelCompletionKind.Symbol, "defined in " + label));
        }

        private static IEnumerable<string> Subfolders(string folder, bool atRoot)
        {
            try
            {
                return Directory.EnumerateDirectories(folder).Where(d => !BazelWorkspace.IsIgnoredFolder(Path.GetFileName(d), atRoot))
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException) { return Enumerable.Empty<string>(); }
        }

        private static bool HasSubfolders(string folder)
        {
            try { return Directory.EnumerateDirectories(folder).Any(d => !BazelWorkspace.IsIgnoredFolder(Path.GetFileName(d), false)); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return false; }
        }
    }
}
