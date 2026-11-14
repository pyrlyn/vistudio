using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Vistudio.Core.CodeLens
{
    /// <summary>One declaration that gets lenses, mapped from a language server's <c>textDocument/documentSymbol</c> answer.</summary>
    public sealed class LspLensElement
    {
        internal LspLensElement(string name, string qualifiedName, string kind, int start, int nameStart, int nameEnd, bool isMain)
        {
            Name = name; QualifiedName = qualifiedName; Kind = kind; Start = start; NameStart = nameStart; NameEnd = nameEnd; IsMain = isMain;
        }

        public string Name { get; }
        /// <summary>Container names and the name, joined with the language's separator (<c>Shape::area</c>).</summary>
        public string QualifiedName { get; }
        /// <summary>Lens kind name: Function, Method, Struct, Enum or Trait.</summary>
        public string Kind { get; }
        /// <summary>Offset of the first non-blank character on the name's line: the lens sits above that line.</summary>
        public int Start { get; }
        public int NameStart { get; }
        public int NameEnd { get; }
        /// <summary>A program entry point (Rust top-level <c>fn main</c>), which gets Run and Debug lenses.</summary>
        public bool IsMain { get; }
        /// <summary>Unique within the file (<see cref="LspSymbolLens.Identifiers"/>).</summary>
        public string Identifier { get; internal set; } = "";
    }

    /// <summary>
    /// Maps document symbols of rust-analyzer and wat_server to CodeLens elements with identity
    /// <c>vistudiolsp|&lt;language&gt;|&lt;Kind&gt;|&lt;Qualified name&gt;[#n]</c>. Kept free of Visual Studio types so the
    /// mapping is unit-tested; the editor parts only turn the offsets into tagged ranges.
    /// </summary>
    public static class LspSymbolLens
    {
        public const string Prefix = "vistudiolsp|";
        public const string Rust = "rust";
        public const string Wat = "wat";

        // LSP SymbolKind values (LSP 3.17 specification, "SymbolKind").
        private const int Method = 6, Enum = 10, Interface = 11, Function = 12, Struct = 23;

        /// <summary>The symbol kinds that get lenses per language, with their lens kind names.</summary>
        private static IReadOnlyDictionary<int, string> KindsFor(string language) => language switch
        {
            // rust-analyzer reports traits as Interface.
            Rust => new Dictionary<int, string> { [Function] = "Function", [Method] = "Method", [Struct] = "Struct", [Enum] = "Enum", [Interface] = "Trait" },
            Wat => new Dictionary<int, string> { [Function] = "Function" },
            _ => new Dictionary<int, string>(),
        };

        private static string Separator(string language) => language == Rust ? "::" : ".";

        /// <summary>
        /// Accepts both answer shapes: hierarchical <c>DocumentSymbol[]</c> (uses <c>selectionRange</c> for the name) and
        /// flat <c>SymbolInformation[]</c> (finds the name inside <c>location.range</c>, containers from <c>containerName</c>).
        /// Positions are 0-based lines and UTF-16 characters, which index .NET strings directly.
        /// </summary>
        public static IReadOnlyList<LspLensElement> FromDocumentSymbols(JToken? result, string language, string text)
        {
            var elements = new List<LspLensElement>();
            if (!(result is JArray symbols)) return elements;
            var kinds = KindsFor(language);
            var lines = LineStarts(text);
            foreach (var symbol in symbols.OfType<JObject>())
                Visit(symbol, null, 0, language, kinds, text, lines, elements);
            elements.Sort((a, b) => a.NameStart.CompareTo(b.NameStart));
            var identifiers = Identifiers(elements, language);
            for (var i = 0; i < elements.Count; i++) elements[i].Identifier = identifiers[i];
            return elements;
        }

        private static void Visit(JObject symbol, string? container, int depth, string language, IReadOnlyDictionary<int, string> kinds,
            string text, IReadOnlyList<int> lines, List<LspLensElement> elements)
        {
            var name = ((string?)symbol["name"] ?? "").Trim();
            var flatContainer = (string?)symbol["containerName"];
            var parent = container ?? (string.IsNullOrEmpty(flatContainer) ? null : flatContainer!.Replace(".", Separator(language)));
            var qualified = parent == null ? name : parent + Separator(language) + name;
            if (name.Length != 0 && symbol["kind"]?.Type == JTokenType.Integer && kinds.TryGetValue((int)symbol["kind"]!, out var kind)
                && NameSpan(symbol, name, text, lines) is (int nameStart, int nameEnd))
            {
                var isMain = language == Rust && kind == "Function" && name == "main" && parent == null && depth == 0;
                elements.Add(new LspLensElement(name, qualified, kind, LineIndentEnd(text, lines, nameStart), nameStart, nameEnd, isMain));
            }
            if (symbol["children"] is JArray children)
                foreach (var child in children.OfType<JObject>())
                    Visit(child, qualified.Length == 0 ? null : qualified, depth + 1, language, kinds, text, lines, elements);
        }

        private static (int, int)? NameSpan(JObject symbol, string name, string text, IReadOnlyList<int> lines)
        {
            if (Offset(symbol["selectionRange"]?["start"], text, lines) is int start && Offset(symbol["selectionRange"]?["end"], text, lines) is int end && end > start)
                return (start, end);
            var range = symbol["location"]?["range"] ?? symbol["range"];
            if (!(Offset(range?["start"], text, lines) is int from)) return null;
            var to = Offset(range?["end"], text, lines) ?? text.Length;
            // Flat answers have no name range: the name is the first whole-word occurrence inside the declaration.
            for (var at = text.IndexOf(name, from, StringComparison.Ordinal); at >= 0 && at + name.Length <= to; at = text.IndexOf(name, at + 1, StringComparison.Ordinal))
                if ((at == 0 || !IsWord(text[at - 1])) && (at + name.Length >= text.Length || !IsWord(text[at + name.Length]))) return (at, at + name.Length);
            return (from, Math.Min(from + name.Length, text.Length));
        }

        private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

        private static int? Offset(JToken? position, string text, IReadOnlyList<int> lines)
        {
            if (position?["line"]?.Type != JTokenType.Integer || position["character"]?.Type != JTokenType.Integer) return null;
            var line = (int)position["line"]!;
            var character = (int)position["character"]!;
            if (line < 0 || character < 0 || line >= lines.Count) return null;
            var lineEnd = line + 1 < lines.Count ? lines[line + 1] : text.Length;
            return Math.Min(lines[line] + character, lineEnd);
        }

        // LSP ends lines at \r\n, \n or \r.
        private static IReadOnlyList<int> LineStarts(string text)
        {
            var starts = new List<int> { 0 };
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                if (text[i] == '\n' || text[i] == '\r') starts.Add(i + 1);
            }
            return starts;
        }

        private static int LineIndentEnd(string text, IReadOnlyList<int> lines, int offset)
        {
            var line = 0;
            while (line + 1 < lines.Count && lines[line + 1] <= offset) line++;
            var start = lines[line];
            while (start < offset && (text[start] == ' ' || text[start] == '\t')) start++;
            return start;
        }

        /// <summary>One identifier per element, in order; unique within the file.</summary>
        public static IReadOnlyList<string> Identifiers(IReadOnlyList<LspLensElement> elements, string language) =>
            CodeLensElement.WithOrdinals(elements.Select(e => Prefix + language + "|" + e.Kind + "|" + e.QualifiedName));

        /// <summary>The language of a code element tagged by <see cref="FromDocumentSymbols"/>; null for anyone else's.</summary>
        public static string? LanguageOf(string? identifier)
        {
            if (identifier == null || !identifier.StartsWith(Prefix, StringComparison.Ordinal)) return null;
            var end = identifier.IndexOf('|', Prefix.Length);
            return end > Prefix.Length ? identifier.Substring(Prefix.Length, end - Prefix.Length) : null;
        }

        /// <summary>True for the identifier of a Rust top-level <c>fn main</c> (no container, so no <c>::</c>).</summary>
        public static bool IsMain(string? identifier) =>
            identifier != null && (identifier == Prefix + Rust + "|Function|main" || identifier.StartsWith(Prefix + Rust + "|Function|main#", StringComparison.Ordinal));
    }
}
