using System.Collections.Generic;
using System.Linq;
using Vistudio.Core.CodeLens;
using Vistudio.Core.Dart;
using Vistudio.Core.Rust;
using Vistudio.Core.Zig;

namespace Vistudio.Core.Editor
{
    /// <summary>
    /// Color assignment for matching braces: a pair shares one of <see cref="ColorCount"/> colors, and a new pair never
    /// takes the color of the previous pair or of the pair it is nested in. Extracted from the Kotlin scanner so every
    /// language's lexer only has to say where its real braces are.
    /// </summary>
    public sealed class BracePalette
    {
        public const int ColorCount = 6;
        private readonly List<ColoredBrace> result = new List<ColoredBrace>();
        private readonly Stack<int> colors = new Stack<int>();
        private int last = -1, next;

        /// <summary>Open braces not closed yet.</summary>
        public int Depth => colors.Count;
        public IReadOnlyList<ColoredBrace> Result => result;

        public void Open(int position)
        {
            while (next == last || (colors.Count > 0 && next == colors.Peek())) next = (next + 1) % ColorCount;
            last = next;
            colors.Push(next);
            result.Add(new ColoredBrace(position, next));
            next = (next + 1) % ColorCount;
        }

        /// <summary>An unmatched closing brace (nothing open) is left uncolored.</summary>
        public void Close(int position)
        {
            if (colors.Count == 0) return;
            last = colors.Pop();
            result.Add(new ColoredBrace(position, last));
        }
    }

    /// <summary>Rainbow brace positions for each <see cref="EditorLanguage"/>, from that language's own lexer.</summary>
    public static class RainbowBraces
    {
        public static bool Supports(EditorLanguage language) => language != EditorLanguage.Wat;

        public static IReadOnlyList<ColoredBrace> Scan(EditorLanguage language, string text)
        {
            switch (language)
            {
                case EditorLanguage.Kotlin:
                    // Kotlin keeps its own lexer: braces of code inside "${...}" string templates are colored too.
                    return KotlinBraceScanner.Scan(text);
                case EditorLanguage.Java:
                    // Java strings have no templates: "${" inside them is text, so the Kotlin lexer would be wrong here.
                    return FromPositions(text, CodeDeclarationScanner.Tokenize(text, java: true)
                        .Where(t => t.Kind == CodeDeclarationScanner.TokenKind.Punctuation && (t.Text == "{" || t.Text == "}"))
                        .Select(t => t.Offset));
                case EditorLanguage.Dart:
                    // Dart's mask blanks comments and strings, including the code of "${...}" interpolations.
                    return FromMasked(DartSourceScanner.Mask(text));
                case EditorLanguage.Zig:
                    return FromPositions(text, ZigSourceScanner.BracePositions(text));
                case EditorLanguage.Rust:
                    return FromMasked(RustSourceScanner.Mask(text));
                default:
                    // Stub (EditorLanguage.Wat): the WebAssembly text format is S-expressions; it has no curly braces, and
                    // coloring its parentheses would be a different feature from the one the other languages share.
                    return new ColoredBrace[0];
            }
        }

        /// <summary>Braces of a text whose comments and literals are already blanked.</summary>
        public static IReadOnlyList<ColoredBrace> FromMasked(string masked)
        {
            var palette = new BracePalette();
            for (var i = 0; i < masked.Length; i++)
            {
                if (masked[i] == '{') palette.Open(i);
                else if (masked[i] == '}') palette.Close(i);
            }
            return palette.Result;
        }

        private static IReadOnlyList<ColoredBrace> FromPositions(string text, IEnumerable<int> positions)
        {
            var palette = new BracePalette();
            foreach (var position in positions)
            {
                if (text[position] == '{') palette.Open(position);
                else palette.Close(position);
            }
            return palette.Result;
        }
    }
}
