using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Vistudio.Core.Dart.Preview
{
    /// <summary>One text insertion; apply a list from the highest offset down.</summary>
    public sealed class SourceInsertion
    {
        public SourceInsertion(int offset, string text) { Offset = offset; Text = text; }
        public int Offset { get; }
        public string Text { get; }
    }

    /// <summary>
    /// The "Add @Preview" quick fix: a preview function after the widget class (and the widget_previews import when the
    /// file lacks it). The user's source changes only when they choose the fix.
    /// </summary>
    public static class PreviewQuickFix
    {
        private static readonly Regex ImportOrPart = new Regex(@"^\s*(?:import|export|library|part)\b[^;]*;[ \t]*\r?\n?", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        /// <summary>The insertions that add a <c>@Preview</c> function for <paramref name="widget"/>; throws when it cannot be built without arguments.</summary>
        public static IReadOnlyList<SourceInsertion> AddPreview(string text, WidgetPreviewFile file, WidgetClassInfo widget)
        {
            if (widget.Construction == null) throw new InvalidOperationException(widget.Problem ?? "'" + widget.Name + "' cannot be created without arguments.");
            if (widget.IsPrivate || widget.IsAbstract) throw new InvalidOperationException(widget.Problem ?? "'" + widget.Name + "' cannot be previewed.");
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var annotation = file.PreviewPrefix == null ? "@Preview" : "@" + file.PreviewPrefix + ".Preview";
            var function = UniqueName(text, char.ToLowerInvariant(widget.Name[0]) + widget.Name.Substring(1) + "Preview");
            var at = Math.Min(text.Length, widget.BodyClose + 1);
            // The class's own line break (if any) ends the inserted function.
            var trailing = at < text.Length && (text[at] == '\n' || text[at] == '\r') ? "" : newline;
            var insertions = new List<SourceInsertion>
            {
                new SourceInsertion(at, newline + newline + annotation + "(name: '" + widget.Name + "')" + newline + "Widget " + function + "() => " + widget.Construction + ";" + trailing),
            };
            if (!file.HasPreviewImport)
            {
                var masked = DartSourceScanner.Mask(text);
                var last = ImportOrPart.Matches(text).Cast<Match>().Where(m => string.CompareOrdinal(masked, m.Index + (m.Value.Length - m.Value.TrimStart().Length), m.Value.TrimStart(), 0, 4) == 0).LastOrDefault();
                var offset = last == null ? 0 : last.Index + last.Length;
                var prefixNewline = last != null && !last.Value.EndsWith("\n", StringComparison.Ordinal) ? newline : "";
                insertions.Add(new SourceInsertion(offset, prefixNewline + "import '" + WidgetPreviewScanner.PreviewLibrary + "';" + newline + (last == null ? newline : "")));
            }
            return insertions.OrderByDescending(i => i.Offset).ToList();
        }

        public static string Apply(string text, IEnumerable<SourceInsertion> insertions)
        {
            var builder = new StringBuilder(text);
            foreach (var insertion in insertions.OrderByDescending(i => i.Offset)) builder.Insert(insertion.Offset, insertion.Text);
            return builder.ToString();
        }

        private static string UniqueName(string text, string name)
        {
            var candidate = name;
            for (var n = 2; Regex.IsMatch(text, @"(?<![\w$])" + Regex.Escape(candidate) + @"(?![\w$])"); n++) candidate = name + n;
            return candidate;
        }
    }

    /// <summary>A widget the temporary host project previews.</summary>
    public sealed class WrappedWidget
    {
        public WrappedWidget(string file, string className, string construction) { File = file; ClassName = className; Construction = construction; }
        public string File { get; }
        public string ClassName { get; }
        /// <summary>From <see cref="WidgetClassInfo.Construction"/>, for example <c>const GreetingCard()</c>.</summary>
        public string Construction { get; }
    }

    /// <summary>
    /// A temporary Flutter project outside the user's package (under <c>%TEMP%\Vistudio\flutter-preview</c>) that depends
    /// on it by path and declares one <c>@Preview</c> function per wrapped widget, so widgets without <c>@Preview</c> can
    /// be previewed without touching the user's sources. The previewer only watches its own project, so
    /// <see cref="Touch"/> rewrites the generated file after the user's package changes, which makes it hot reload.
    /// </summary>
    public sealed class PreviewHostProject
    {
        public const string HostPackageName = "vistudio_preview_host";
        public const string PreviewsFile = "vistudio_previews.dart";
        private readonly List<WrappedWidget> widgets = new List<WrappedWidget>();
        private int revision;

        public PreviewHostProject(string packageRoot, string packageName, string folder)
        {
            PackageRoot = Path.GetFullPath(packageRoot);
            PackageName = packageName;
            Folder = folder;
        }

        public string PackageRoot { get; }
        public string PackageName { get; }
        public string Folder { get; }
        public IReadOnlyList<WrappedWidget> Widgets => widgets;
        public string PreviewsPath => Path.Combine(Folder, "lib", PreviewsFile);

        /// <summary>The host folder for a package: stable per package path, so pub get and the previewer scaffold are reused.</summary>
        public static string DefaultFolder(string packageRoot, string packageName)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(packageRoot).TrimEnd('\\', '/').ToLowerInvariant()));
                return Path.Combine(Path.GetTempPath(), "Vistudio", "flutter-preview", packageName + "-" + string.Concat(hash.Take(6).Select(b => b.ToString("x2"))));
            }
        }

        /// <summary>The <c>package:</c> import of a file under the package's <c>lib</c> folder; throws for other files (test, bin, example).</summary>
        public string ImportUri(string file)
        {
            var lib = Path.Combine(PackageRoot, "lib") + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(file);
            if (!full.StartsWith(lib, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only widgets under the package's lib folder can be previewed without @Preview (other files cannot be imported by package URI): " + full);
            return "package:" + PackageName + "/" + full.Substring(lib.Length).Replace('\\', '/');
        }

        /// <summary>Adds (or moves to the front) a widget; at most <paramref name="keep"/> recent widgets stay.</summary>
        public void Add(WrappedWidget widget, int keep = 8)
        {
            ImportUri(widget.File);
            if (!Regex.IsMatch(widget.Construction, @"^(const\s+)?[A-Za-z_$][\w$]*(\.[A-Za-z_$][\w$]*)?\(\)$")) throw new ArgumentException("Unsupported widget construction: " + widget.Construction);
            widgets.RemoveAll(w => string.Equals(w.File, widget.File, StringComparison.OrdinalIgnoreCase) && w.ClassName == widget.ClassName);
            widgets.Insert(0, widget);
            if (widgets.Count > keep) widgets.RemoveRange(keep, widgets.Count - keep);
        }

        /// <summary>Writes pubspec.yaml, lib\main.dart and the previews file. Returns true when pubspec.yaml changed (pub get needed).</summary>
        public bool Write(string userPubspec)
        {
            Directory.CreateDirectory(Path.Combine(Folder, "lib"));
            var pubspec = Pubspec(userPubspec);
            var pubspecPath = Path.Combine(Folder, "pubspec.yaml");
            var changed = !File.Exists(pubspecPath) || File.ReadAllText(pubspecPath) != pubspec;
            if (changed) File.WriteAllText(pubspecPath, pubspec, new UTF8Encoding(false));
            WriteIfChanged(Path.Combine(Folder, "lib", "main.dart"),
                "// Generated by Vistudio: host app of the widget previews of package:" + PackageName + ". Safe to delete.\nimport 'package:flutter/widgets.dart';\n\nvoid main() => runApp(const SizedBox.shrink());\n");
            WriteIfChanged(Path.Combine(Folder, "README.md"),
                "Temporary project generated by Vistudio to preview widgets of `" + PackageRoot + "` that have no `@Preview` annotation.\nIt is recreated when needed and can be deleted at any time.\n");
            File.WriteAllText(PreviewsPath, Previews(), new UTF8Encoding(false));
            return changed;
        }

        /// <summary>Rewrites the previews file with a new revision so the previewer's file watcher triggers a hot reload.</summary>
        public void Touch()
        {
            revision++;
            Directory.CreateDirectory(Path.GetDirectoryName(PreviewsPath)!);
            File.WriteAllText(PreviewsPath, Previews(), new UTF8Encoding(false));
        }

        public string Previews()
        {
            var builder = new StringBuilder();
            builder.Append("// Generated by Vistudio for widgets of package:").Append(PackageName).Append(" without @Preview. Do not edit.\n");
            builder.Append("// Revision ").Append(revision).Append('\n');
            builder.Append("import 'package:flutter/widgets.dart';\nimport 'package:flutter/widget_previews.dart';\n");
            var imports = widgets.Select(w => ImportUri(w.File)).Distinct(StringComparer.Ordinal).ToList();
            for (var i = 0; i < imports.Count; i++) builder.Append("import '").Append(imports[i]).Append("' as w").Append(i).Append(";\n");
            for (var i = 0; i < widgets.Count; i++)
            {
                var widget = widgets[i];
                var prefix = "w" + imports.IndexOf(ImportUri(widget.File));
                var construction = Regex.Replace(widget.Construction, @"^(const\s+)?", m => m.Value + prefix + ".");
                builder.Append('\n').Append("@Preview(name: '").Append(widget.ClassName).Append("', group: '").Append(Path.GetFileName(widget.File)).Append("')\n");
                builder.Append("Widget vistudioPreview").Append(i).Append("() => ").Append(construction).Append(";\n");
            }
            return builder.ToString();
        }

        private static readonly Regex EnvironmentBlock = new Regex(@"^environment:[^\n]*\n(?:[ \t]+[^\n]*\n?)*", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        public string Pubspec(string userPubspec)
        {
            var environment = EnvironmentBlock.Match(userPubspec.Replace("\r\n", "\n"));
            var builder = new StringBuilder();
            builder.Append("name: ").Append(HostPackageName).Append('\n');
            builder.Append("description: Temporary widget preview host generated by Vistudio. Safe to delete.\n");
            builder.Append("publish_to: none\n");
            builder.Append(environment.Success ? environment.Value.TrimEnd('\n') + "\n" : "environment:\n  sdk: '>=3.0.0 <4.0.0'\n");
            builder.Append("dependencies:\n  flutter:\n    sdk: flutter\n");
            builder.Append("  ").Append(PackageName).Append(":\n    path: '").Append(PackageRoot.Replace('\\', '/').Replace("'", "''")).Append("'\n");
            builder.Append("flutter:\n  uses-material-design: true\n");
            return builder.ToString();
        }

        private static void WriteIfChanged(string path, string content)
        {
            if (File.Exists(path) && File.ReadAllText(path) == content) return;
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
    }
}
