using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Vistudio.Core.Packages
{
    /// <summary>A <c>[libraries]</c> entry of a version catalog and where its version text lives.</summary>
    internal sealed class GradleCatalogLibrary
    {
        public GradleCatalogLibrary(string alias, string module, string version, int line, int versionStart, int versionLength, string? versionRef)
        {
            Alias = alias; Module = module; Version = version; Line = line;
            VersionStart = versionStart; VersionLength = versionLength; VersionRef = versionRef;
        }

        public string Alias { get; }

        /// <summary><c>group:artifact</c>.</summary>
        public string Module { get; }

        /// <summary>The version text; empty when the catalog gives none (a platform or constraint supplies it).</summary>
        public string Version { get; }

        /// <summary>1-based catalog line holding the version (the <c>[versions]</c> line for <c>version.ref</c>), or the library line.</summary>
        public int Line { get; }

        /// <summary>Offset of the version text inside the catalog; -1 when there is no plain string to rewrite.</summary>
        public int VersionStart { get; }
        public int VersionLength { get; }
        public string? VersionRef { get; }
    }

    /// <summary>
    /// The default version catalog <c>gradle/libs.versions.toml</c> of a settings root, read with a small TOML
    /// subset parser (tables, dotted keys, basic and literal strings, inline tables, arrays). Offsets are kept so a
    /// version can be rewritten in place without reformatting the file.
    /// </summary>
    internal sealed class GradleVersionCatalog
    {
        private readonly Dictionary<string, GradleCatalogLibrary> libraries = new Dictionary<string, GradleCatalogLibrary>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> bundles = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        private GradleVersionCatalog(string path, string text) { Path = path; Text = text; }

        public string Path { get; }
        public string Text { get; }

        public static string FileOf(string settingsRoot) => System.IO.Path.Combine(settingsRoot, "gradle", "libs.versions.toml");

        public static GradleVersionCatalog? Load(string settingsRoot)
        {
            var path = FileOf(settingsRoot);
            return File.Exists(path) ? Parse(path, File.ReadAllText(path)) : null;
        }

        public static GradleVersionCatalog Parse(string path, string text)
        {
            var catalog = new GradleVersionCatalog(path, text);
            var root = new TomlParser(path, text).Parse();
            var versions = Table(root, "versions");
            foreach (var pair in Table(root, "libraries"))
            {
                var library = catalog.Library(pair.Key, pair.Value, versions);
                if (library != null) catalog.libraries[Accessor(pair.Key)] = library;
            }
            foreach (var pair in Table(root, "bundles"))
                if (pair.Value.Items != null)
                    catalog.bundles[Accessor(pair.Key)] = pair.Value.Items.Where(i => i.Table == null && i.Items == null).Select(i => Accessor(i.Text)).ToList();
            return catalog;
        }

        /// <summary>The library behind <c>libs.&lt;accessor&gt;</c>, accessor without the <c>libs.</c> prefix.</summary>
        public GradleCatalogLibrary? Find(string accessor) =>
            libraries.TryGetValue(Accessor(accessor), out var library) ? library : null;

        /// <summary>The libraries of <c>libs.bundles.&lt;name&gt;</c>; null when the bundle is unknown.</summary>
        public IReadOnlyList<GradleCatalogLibrary>? Bundle(string name) =>
            bundles.TryGetValue(Accessor(name), out var aliases) ? aliases.Where(libraries.ContainsKey).Select(a => libraries[a]).ToList() : null;

        // Gradle turns '-', '_' and '.' in aliases into accessor separators, so all three spellings name the same entry.
        internal static string Accessor(string alias) => alias.Replace('-', '.').Replace('_', '.').ToLowerInvariant();

        private static Dictionary<string, TomlNode> Table(TomlNode root, string name) =>
            root.Table != null && root.Table.TryGetValue(name, out var node) && node.Table != null ? node.Table : new Dictionary<string, TomlNode>();

        private GradleCatalogLibrary? Library(string alias, TomlNode node, Dictionary<string, TomlNode> versions)
        {
            if (node.Table == null && node.Items == null)
            {
                // "group:artifact:version" string notation.
                var parts = node.Text.Split(':');
                if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;
                var module = parts[0] + ":" + parts[1];
                if (parts.Length < 3) return new GradleCatalogLibrary(alias, module, "", LineOf(node.Start), -1, 0, null);
                var start = node.Start + parts[0].Length + parts[1].Length + 2;
                return new GradleCatalogLibrary(alias, module, parts[2], LineOf(node.Start), node.Raw ? start : -1, parts[2].Length, null);
            }
            if (node.Table == null) return null;
            var table = node.Table;
            string? moduleName = Get(table, "module")?.Text;
            if (moduleName == null)
            {
                var group = Get(table, "group")?.Text;
                var name = Get(table, "name")?.Text;
                if (group == null || name == null) return null;
                moduleName = group + ":" + name;
            }
            if (moduleName.Split(':').Length != 2) return null;
            var line = LineOf(node.Start);
            if (!table.TryGetValue("version", out var version)) return new GradleCatalogLibrary(alias, moduleName, "", line, -1, 0, null);
            var reference = version.Table != null ? Get(version.Table, "ref") : null;
            if (reference != null)
            {
                if (!versions.TryGetValue(reference.Text, out var target))
                    throw new PackageManagerException("Version catalog " + Path + " line " + LineOf(reference.Start) + ": [versions] has no '" + reference.Text + "'.");
                var plain = Plain(target);
                return plain == null
                    ? new GradleCatalogLibrary(alias, moduleName, Display(target), LineOf(target.Start), -1, 0, reference.Text)
                    : new GradleCatalogLibrary(alias, moduleName, plain.Text, LineOf(plain.Start), plain.Start, plain.Length, reference.Text);
            }
            var inline = Plain(version);
            return inline == null
                ? new GradleCatalogLibrary(alias, moduleName, Display(version), line, -1, 0, null)
                : new GradleCatalogLibrary(alias, moduleName, inline.Text, LineOf(inline.Start), inline.Start, inline.Length, null);
        }

        private static TomlNode? Get(Dictionary<string, TomlNode> table, string key) =>
            table.TryGetValue(key, out var node) && node.Table == null && node.Items == null ? node : null;

        /// <summary>The single string to rewrite: the version itself, or the <c>require</c>/<c>strictly</c>/<c>prefer</c> of a rich version.</summary>
        private static TomlNode? Plain(TomlNode version)
        {
            if (version.Table == null && version.Items == null) return version.Raw ? version : null;
            if (version.Table == null) return null;
            foreach (var key in new[] { "require", "strictly", "prefer" })
                if (Get(version.Table, key) is TomlNode node && node.Raw) return node;
            return null;
        }

        private static string Display(TomlNode version) =>
            version.Table == null ? version.Text : string.Join(" ", version.Table.Where(p => p.Value.Table == null && p.Value.Items == null).Select(p => p.Key + "=" + p.Value.Text));

        private int LineOf(int offset)
        {
            var line = 1;
            for (var i = 0; i < offset && i < Text.Length; i++) if (Text[i] == '\n') line++;
            return line;
        }

        internal sealed class TomlNode
        {
            public string Text = "";
            /// <summary>Offset and length of the string content (between the quotes).</summary>
            public int Start;
            public int Length;
            /// <summary>The content equals the source text, so it can be replaced in place (no escapes, single-line string).</summary>
            public bool Raw;
            public Dictionary<string, TomlNode>? Table;
            public List<TomlNode>? Items;
        }

        private sealed class TomlParser
        {
            private readonly string path;
            private readonly string text;
            private int pos;

            public TomlParser(string path, string text) { this.path = path; this.text = text; }

            public TomlNode Parse()
            {
                var root = new TomlNode { Table = new Dictionary<string, TomlNode>(StringComparer.Ordinal) };
                var current = root;
                while (true)
                {
                    SkipBlank(newlines: true);
                    if (pos >= text.Length) return root;
                    if (text[pos] == '[')
                    {
                        var array = pos + 1 < text.Length && text[pos + 1] == '[';
                        pos += array ? 2 : 1;
                        var keys = Keys();
                        Expect(']');
                        if (array) Expect(']');
                        // Arrays of tables do not occur in version catalogs; their entries are parsed into a throwaway table.
                        current = array ? new TomlNode { Table = new Dictionary<string, TomlNode>() } : Descend(root, keys);
                        EndOfLine();
                        continue;
                    }
                    var path = Keys();
                    SkipBlank(newlines: false);
                    Expect('=');
                    SkipBlank(newlines: false);
                    var value = Value();
                    Assign(current, path, value);
                    EndOfLine();
                }
            }

            private TomlNode Descend(TomlNode table, List<string> keys)
            {
                foreach (var key in keys)
                {
                    if (!table.Table!.TryGetValue(key, out var next) || next.Table == null)
                        table.Table[key] = next = new TomlNode { Table = new Dictionary<string, TomlNode>(StringComparer.Ordinal), Start = pos };
                    table = next;
                }
                return table;
            }

            private void Assign(TomlNode table, List<string> keys, TomlNode value)
            {
                var parent = Descend(table, keys.Take(keys.Count - 1).ToList());
                parent.Table![keys[keys.Count - 1]] = value;
            }

            private List<string> Keys()
            {
                var keys = new List<string>();
                while (true)
                {
                    SkipBlank(newlines: false);
                    if (pos < text.Length && (text[pos] == '"' || text[pos] == '\'')) keys.Add(String().Text);
                    else
                    {
                        var start = pos;
                        while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_' || text[pos] == '-')) pos++;
                        if (pos == start) throw Error("a key was expected");
                        keys.Add(text.Substring(start, pos - start));
                    }
                    SkipBlank(newlines: false);
                    if (pos < text.Length && text[pos] == '.') { pos++; continue; }
                    return keys;
                }
            }

            private TomlNode Value()
            {
                if (pos >= text.Length) throw Error("a value was expected");
                var c = text[pos];
                if (c == '"' || c == '\'') return String();
                if (c == '{')
                {
                    var node = new TomlNode { Table = new Dictionary<string, TomlNode>(StringComparer.Ordinal), Start = pos };
                    pos++;
                    SkipBlank(newlines: false);
                    if (pos < text.Length && text[pos] == '}') { pos++; return node; }
                    while (true)
                    {
                        var keys = Keys();
                        SkipBlank(newlines: false);
                        Expect('=');
                        SkipBlank(newlines: false);
                        Assign(node, keys, Value());
                        SkipBlank(newlines: false);
                        if (pos < text.Length && text[pos] == ',') { pos++; continue; }
                        Expect('}');
                        return node;
                    }
                }
                if (c == '[')
                {
                    var node = new TomlNode { Items = new List<TomlNode>(), Start = pos };
                    pos++;
                    while (true)
                    {
                        SkipBlank(newlines: true);
                        if (pos < text.Length && text[pos] == ']') { pos++; return node; }
                        node.Items.Add(Value());
                        SkipBlank(newlines: true);
                        if (pos < text.Length && text[pos] == ',') { pos++; continue; }
                        Expect(']');
                        return node;
                    }
                }
                var begin = pos;
                while (pos < text.Length && ",]}#\r\n".IndexOf(text[pos]) < 0) pos++;
                var bare = text.Substring(begin, pos - begin).TrimEnd();
                if (bare.Length == 0) throw Error("a value was expected");
                return new TomlNode { Text = bare, Start = begin, Length = bare.Length, Raw = true };
            }

            private TomlNode String()
            {
                var quote = text[pos];
                if (pos + 2 < text.Length && text[pos + 1] == quote && text[pos + 2] == quote)
                {
                    var close = text.IndexOf(new string(quote, 3), pos + 3, StringComparison.Ordinal);
                    if (close < 0) throw Error("unterminated multi-line string");
                    var content = text.Substring(pos + 3, close - pos - 3);
                    var multi = new TomlNode { Text = content, Start = pos + 3, Length = content.Length, Raw = false };
                    pos = close + 3;
                    return multi;
                }
                var start = ++pos;
                var value = new StringBuilder();
                var raw = true;
                while (true)
                {
                    if (pos >= text.Length || text[pos] == '\n') throw Error("unterminated string");
                    var c = text[pos];
                    if (c == quote) break;
                    if (c == '\\' && quote == '"' && pos + 1 < text.Length)
                    {
                        raw = false;
                        var next = text[pos + 1];
                        value.Append(next == 'n' ? '\n' : next == 't' ? '\t' : next == 'r' ? '\r' : next);
                        pos += 2;
                        continue;
                    }
                    value.Append(c);
                    pos++;
                }
                var node = new TomlNode { Text = value.ToString(), Start = start, Length = pos - start, Raw = raw };
                pos++;
                return node;
            }

            private void SkipBlank(bool newlines)
            {
                while (pos < text.Length)
                {
                    var c = text[pos];
                    if (c == ' ' || c == '\t' || (newlines && (c == '\r' || c == '\n'))) { pos++; continue; }
                    if (c == '#') { while (pos < text.Length && text[pos] != '\n') pos++; continue; }
                    return;
                }
            }

            private void EndOfLine()
            {
                SkipBlank(newlines: false);
                if (pos < text.Length && text[pos] == '\r') pos++;
                if (pos < text.Length && text[pos] != '\n') throw Error("unexpected '" + text[pos] + "'");
            }

            private void Expect(char c)
            {
                if (pos >= text.Length || text[pos] != c) throw Error("'" + c + "' was expected");
                pos++;
            }

            private PackageManagerException Error(string message)
            {
                var line = 1;
                for (var i = 0; i < pos && i < text.Length; i++) if (text[i] == '\n') line++;
                return new PackageManagerException("Cannot read version catalog " + path + " line " + line + ": " + message + ".");
            }
        }
    }
}
