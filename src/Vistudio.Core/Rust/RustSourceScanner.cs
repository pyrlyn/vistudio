namespace Vistudio.Core.Rust
{
    /// <summary>
    /// Lexical helpers for Rust sources (no compiler, tolerant of broken code), used for editor structure and brace
    /// colors. rust-analyzer stays the source of everything semantic.
    /// </summary>
    public static class RustSourceScanner
    {
        /// <summary>
        /// The text with every comment blanked and every string, byte string, raw string and character literal replaced by
        /// <c>0</c> followed by blanks, so it reads as one literal token with no brackets or quotes inside. Lifetimes and
        /// labels (<c>'a</c>, <c>'static</c>, <c>'outer:</c>) lose their quote only. Line breaks stay, so offsets and lines match.
        /// </summary>
        public static string Mask(string text)
        {
            var c = text.ToCharArray();
            var i = 0;
            while (i < c.Length)
            {
                var ch = c[i];
                if (ch == '/' && i + 1 < c.Length && c[i + 1] == '/')
                {
                    while (i < c.Length && c[i] != '\n' && c[i] != '\r') c[i++] = ' ';
                    continue;
                }
                if (ch == '/' && i + 1 < c.Length && c[i + 1] == '*')
                {
                    // Rust block comments nest.
                    var depth = 0;
                    while (i < c.Length)
                    {
                        if (c[i] == '/' && i + 1 < c.Length && c[i + 1] == '*') { depth++; Blank(c, i); Blank(c, i + 1); i += 2; continue; }
                        if (c[i] == '*' && i + 1 < c.Length && c[i + 1] == '/') { Blank(c, i); Blank(c, i + 1); i += 2; if (--depth == 0) break; continue; }
                        Blank(c, i++);
                    }
                    continue;
                }
                if (IsIdentifierStart(ch) && (i == 0 || !IsIdentifierPart(c[i - 1])))
                {
                    if (RawStringEnd(c, i) is int rawEnd) { Literal(c, i, rawEnd); i = rawEnd; continue; }
                    // b"..", c"..", b'..' prefixes; anything else is an identifier or keyword.
                    if ((ch == 'b' || ch == 'c') && i + 1 < c.Length && (c[i + 1] == '"' || ch == 'b' && c[i + 1] == '\''))
                    {
                        var end = c[i + 1] == '"' ? StringEnd(c, i + 1) : CharEnd(c, i + 1) ?? i + 2;
                        Literal(c, i, end);
                        i = end;
                        continue;
                    }
                    while (i < c.Length && IsIdentifierPart(c[i])) i++;
                    continue;
                }
                if (ch == '"')
                {
                    var end = StringEnd(c, i);
                    Literal(c, i, end);
                    i = end;
                    continue;
                }
                if (ch == '\'')
                {
                    if (CharEnd(c, i) is int end) { Literal(c, i, end); i = end; continue; }
                    c[i++] = ' '; // a lifetime or label: the name stays an identifier
                    continue;
                }
                i++;
            }
            return new string(c);
        }

        /// <summary>End (exclusive) of <c>r"..."</c>, <c>r#"..."#</c>, <c>br##"..."##</c> or <c>cr"..."</c> starting at <paramref name="i"/>; null otherwise (also for raw identifiers such as <c>r#type</c>).</summary>
        private static int? RawStringEnd(char[] c, int i)
        {
            var j = i;
            if ((c[j] == 'b' || c[j] == 'c') && j + 1 < c.Length && c[j + 1] == 'r') j++;
            if (c[j] != 'r') return null;
            j++;
            var hashes = 0;
            while (j < c.Length && c[j] == '#') { hashes++; j++; }
            if (j >= c.Length || c[j] != '"') return null;
            for (j++; j < c.Length; j++)
            {
                if (c[j] != '"') continue;
                var k = j + 1;
                var closing = 0;
                while (closing < hashes && k < c.Length && c[k] == '#') { closing++; k++; }
                if (closing == hashes) return k;
            }
            return c.Length; // unterminated: the rest of the file is the string
        }

        /// <summary>End (exclusive) of a string starting at its opening quote; strings may span lines.</summary>
        private static int StringEnd(char[] c, int quote)
        {
            for (var j = quote + 1; j < c.Length; j++)
            {
                if (c[j] == '\\') { j++; continue; }
                if (c[j] == '"') return j + 1;
            }
            return c.Length;
        }

        /// <summary>End (exclusive) of a character literal at <paramref name="quote"/>, or null when the quote starts a lifetime or label.</summary>
        private static int? CharEnd(char[] c, int quote)
        {
            var j = quote + 1;
            if (j >= c.Length || c[j] == '\n' || c[j] == '\r') return null;
            if (c[j] == '\\')
            {
                // '\n', '\'', '\x7f', '\u{1F600}': up to the closing quote on the same line.
                for (j += 2; j < c.Length && c[j] != '\n'; j++)
                    if (c[j] == '\'') return j + 1;
                return null;
            }
            // One character (two UTF-16 units for a surrogate pair) and the closing quote; otherwise 'a is a lifetime.
            var width = char.IsHighSurrogate(c[j]) && j + 1 < c.Length && char.IsLowSurrogate(c[j + 1]) ? 2 : 1;
            return j + width < c.Length && c[j + width] == '\'' ? j + width + 1 : (int?)null;
        }

        private static void Literal(char[] c, int start, int end)
        {
            c[start] = '0';
            for (var j = start + 1; j < end && j < c.Length; j++) Blank(c, j);
        }

        private static void Blank(char[] c, int i)
        {
            if (c[i] != '\n' && c[i] != '\r') c[i] = ' ';
        }

        private static bool IsIdentifierStart(char ch) => char.IsLetter(ch) || ch == '_';
        private static bool IsIdentifierPart(char ch) => char.IsLetterOrDigit(ch) || ch == '_';
    }
}
