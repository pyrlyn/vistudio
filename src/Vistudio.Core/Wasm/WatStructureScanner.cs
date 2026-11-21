using System.Collections.Generic;
using Vistudio.Core.Structure;

namespace Vistudio.Core.Wasm
{
    /// <summary>
    /// Lexical block structure of the WebAssembly text format for editor scopes (Sticky Scroll, structure guides), the
    /// counterpart of <see cref="CodeStructureScanner"/> for a language without braces. Two block shapes:
    /// <list type="bullet">
    /// <item>folded S-expressions that span lines, <c>(module</c>, <c>(func $f ...</c>, <c>(block $b</c>, <c>(if</c>, ...:
    /// the header is the opening line from the parenthesis, <see cref="CodeBlock.OpenBrace"/> / <see cref="CodeBlock.CloseBrace"/>
    /// are the parentheses;</item>
    /// <item>flat instructions <c>block</c>, <c>loop</c>, <c>if</c>, <c>try</c>, <c>try_table</c> ... <c>end</c>: the header is the
    /// instruction's line, the block closes at the last character of its <c>end</c>.</item>
    /// </list>
    /// Comments (<c>;;</c> and nested <c>(; ;)</c>) and strings are skipped; unmatched parentheses or <c>end</c>s only drop their own block.
    /// </summary>
    public static class WatStructureScanner
    {
        public static IReadOnlyList<CodeBlock> Scan(string text)
        {
            var lineStarts = new List<int> { 0 };
            for (var i = 0; i < text.Length; i++) if (text[i] == '\n') lineStarts.Add(i + 1);
            var result = new List<CodeBlock>();
            // Open folded forms and flat blocks, innermost last. A flat block lives inside the form that holds it.
            var open = new Stack<Open>();
            var i2 = 0;
            while (i2 < text.Length)
            {
                var c = text[i2];
                if (c == ';' && i2 + 1 < text.Length && text[i2 + 1] == ';') { while (i2 < text.Length && text[i2] != '\n') i2++; continue; }
                if (c == '(' && i2 + 1 < text.Length && text[i2 + 1] == ';') { i2 = SkipBlockComment(text, i2); continue; }
                if (c == '"') { i2 = SkipString(text, i2); continue; }
                if (c == '(')
                {
                    var keywordStart = i2 + 1;
                    while (keywordStart < text.Length && (text[keywordStart] == ' ' || text[keywordStart] == '\t')) keywordStart++;
                    var keyword = Word(text, keywordStart);
                    open.Push(new Open(folded: true, keyword, i2, Line(lineStarts, i2)));
                    i2 = keywordStart + keyword.Length;
                    continue;
                }
                if (c == ')')
                {
                    // Flat blocks left open inside this form are unterminated: drop them.
                    while (open.Count > 0 && !open.Peek().Folded) open.Pop();
                    if (open.Count > 0) Close(text, open.Pop(), i2, lineStarts, open, result);
                    i2++;
                    continue;
                }
                if (IsWordStart(c) && (i2 == 0 || !IsWordPart(text[i2 - 1])))
                {
                    var word = Word(text, i2);
                    if (word is "block" or "loop" or "if" or "try" or "try_table")
                        open.Push(new Open(folded: false, word, i2, Line(lineStarts, i2)));
                    else if (word == "end" && open.Count > 0 && !open.Peek().Folded)
                        Close(text, open.Pop(), i2 + word.Length - 1, lineStarts, open, result);
                    i2 += word.Length;
                    continue;
                }
                i2++;
            }
            result.Sort((a, b) => a.HeaderStart != b.HeaderStart ? a.HeaderStart.CompareTo(b.HeaderStart) : b.CloseBrace.CompareTo(a.CloseBrace));
            return result;
        }

        private sealed class Open
        {
            public Open(bool folded, string keyword, int start, int line) { Folded = folded; Keyword = keyword; Start = start; Line = line; }
            public bool Folded { get; }
            public string Keyword { get; }
            public int Start { get; }
            public int Line { get; }
        }

        private static void Close(string text, Open block, int close, List<int> lineStarts, Stack<Open> open, List<CodeBlock> result)
        {
            var endLine = Line(lineStarts, close);
            if (endLine <= block.Line || block.Keyword.Length == 0) return; // one line, or `(` without a keyword (`((`)
            // Line-based scopes: a form whose header shares the line of an enclosing header (`(if (then`) would pin it twice.
            foreach (var outer in open) if (outer.Line == block.Line) return;
            result.Add(new CodeBlock(Kind(block.Keyword), block.Start, HeaderEnd(text, block.Start), block.Start, close, block.Line, endLine));
        }

        private static CodeBlockKind Kind(string keyword) => keyword switch
        {
            "module" or "component" or "type" or "rec" or "struct" or "array" => CodeBlockKind.Type,
            "func" => CodeBlockKind.Member,
            "loop" => CodeBlockKind.Loop,
            "if" or "then" or "else" => CodeBlockKind.Conditional,
            "block" or "try" or "try_table" or "catch" or "catch_all" or "do" => CodeBlockKind.Statement,
            _ => CodeBlockKind.Expression,
        };

        /// <summary>End of the header: the last code character on the opening line (a trailing comment is left out).</summary>
        private static int HeaderEnd(string text, int start)
        {
            var end = start;
            var i = start;
            while (i < text.Length && text[i] != '\n' && text[i] != '\r')
            {
                if (text[i] == ';' && i + 1 < text.Length && text[i + 1] == ';') break;
                if (text[i] == '(' && i + 1 < text.Length && text[i + 1] == ';') break;
                if (text[i] == '"') { i = SkipString(text, i); end = i; continue; }
                if (!char.IsWhiteSpace(text[i])) end = i + 1;
                i++;
            }
            return end;
        }

        private static int SkipBlockComment(string text, int i)
        {
            var depth = 0;
            while (i < text.Length)
            {
                if (text[i] == '(' && i + 1 < text.Length && text[i + 1] == ';') { depth++; i += 2; continue; }
                if (text[i] == ';' && i + 1 < text.Length && text[i + 1] == ')') { i += 2; if (--depth == 0) return i; continue; }
                i++;
            }
            return i;
        }

        private static int SkipString(string text, int quote)
        {
            for (var i = quote + 1; i < text.Length; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == '"' || text[i] == '\n') return i + 1;
            }
            return text.Length;
        }

        private static string Word(string text, int start)
        {
            var end = start;
            while (end < text.Length && IsWordPart(text[end])) end++;
            return text.Substring(start, end - start);
        }

        // WAT keywords are lower-case letters, digits, '_' and '.' (`i32.add`); `$name` identifiers never start a block.
        private static bool IsWordStart(char c) => c >= 'a' && c <= 'z';
        private static bool IsWordPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '$';

        private static int Line(List<int> lineStarts, int offset)
        {
            int low = 0, high = lineStarts.Count - 1;
            while (low < high)
            {
                var mid = (low + high + 1) / 2;
                if (lineStarts[mid] <= offset) low = mid; else high = mid - 1;
            }
            return low;
        }
    }
}
