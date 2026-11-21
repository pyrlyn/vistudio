using System;
using System.Collections.Generic;
using Vistudio.Core.CodeLens;
using Vistudio.Core.Dart;
using Vistudio.Core.Editor;
using Vistudio.Core.Rust;
using Token = Vistudio.Core.CodeLens.CodeDeclarationScanner.Token;
using TokenKind = Vistudio.Core.CodeLens.CodeDeclarationScanner.TokenKind;

namespace Vistudio.Core.Structure
{
    public enum CodeBlockKind { Type, Member, Conditional, Loop, Statement, Expression }

    /// <summary>A multi-line <c>{ ... }</c> block with its header; offsets are UTF-16 positions in the scanned text.</summary>
    public sealed class CodeBlock
    {
        public CodeBlock(CodeBlockKind kind, int headerStart, int headerEnd, int openBrace, int closeBrace, int headerLine, int endLine)
        {
            Kind = kind; HeaderStart = headerStart; HeaderEnd = headerEnd; OpenBrace = openBrace; CloseBrace = closeBrace;
            HeaderLine = headerLine; EndLine = endLine;
        }

        public CodeBlockKind Kind { get; }
        /// <summary>First token of the statement that owns the block, after leading annotations.</summary>
        public int HeaderStart { get; }
        /// <summary>End of the last header token before the opening brace.</summary>
        public int HeaderEnd { get; }
        public int OpenBrace { get; }
        public int CloseBrace { get; }
        /// <summary>0-based line of <see cref="HeaderStart"/>.</summary>
        public int HeaderLine { get; }
        /// <summary>0-based line of <see cref="CloseBrace"/>.</summary>
        public int EndLine { get; }
        public override string ToString() => Kind + " " + HeaderLine + "-" + EndLine;
    }

    /// <summary>
    /// Lexical block structure of Kotlin, Java, Dart and Rust sources for editor scopes (Sticky Scroll, structure guides): every
    /// brace block that spans lines, with the statement header that owns it. A header may span lines
    /// (<c>fun f(\n  a: Int\n): Int {</c>) and starts at the declaration or keyword, not at the line with the brace.
    /// No compiler: the scanner tolerates broken code, and unmatched braces only drop their own block.
    /// </summary>
    public static class CodeStructureScanner
    {
        private static readonly HashSet<string> Modifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            "public", "private", "protected", "internal", "abstract", "open", "sealed", "final", "override", "data", "inner",
            "enum", "annotation", "value", "companion", "inline", "suspend", "operator", "infix", "tailrec", "external",
            "expect", "actual", "static", "default", "synchronized", "native", "strictfp", "non-sealed",
        };

        // A Kotlin newline continues the statement after these tokens (not `->`: after lambda parameters a new
        // statement starts) ...
        private static readonly HashSet<string> ContinuesAfter = new HashSet<string>(StringComparer.Ordinal)
        {
            ",", "(", "[", ".", ":", "=", "&", "|", "+", "-", "/", "%", "==", "<",
        };

        // ... and before these.
        private static readonly HashSet<string> ContinuesBefore = new HashSet<string>(StringComparer.Ordinal)
        {
            ".", "?", ":", "=", "->", "&", "|", ")", "]", ",", "==", "else", "catch", "finally", "where",
        };

        public static IReadOnlyList<CodeBlock> Scan(string path, string text) =>
            Scan(text, path.EndsWith(".java", StringComparison.OrdinalIgnoreCase));

        public static IReadOnlyList<CodeBlock> Scan(string text, bool java) => Scan(text, java ? EditorLanguage.Java : EditorLanguage.Kotlin);

        /// <summary>
        /// Kotlin, Java, Dart and Rust: the brace languages that share this header logic. Dart and Rust are scanned on
        /// their masked text (comments and literals blanked by <see cref="DartSourceScanner.Mask"/> and
        /// <see cref="RustSourceScanner.Mask"/>), with the offsets of the original text. Zig and WebAssembly text have
        /// scanners of their own (<see cref="SourceStructure"/>).
        /// </summary>
        public static IReadOnlyList<CodeBlock> Scan(string text, EditorLanguage language)
        {
            switch (language)
            {
                case EditorLanguage.Kotlin:
                case EditorLanguage.Java:
                    return Scan(text, text, language);
                case EditorLanguage.Dart:
                    // The mask keeps the quotes; without them the tokenizer sees no literal at all, which is what a
                    // header needs (Dart headers do not end in a string).
                    return Scan(DartSourceScanner.Mask(text).Replace('\'', ' ').Replace('"', ' '), text, language);
                case EditorLanguage.Rust:
                    return Scan(RustSourceScanner.Mask(text), text, language);
                default:
                    throw new ArgumentOutOfRangeException(nameof(language), language, "Use SourceStructure.Scan for " + language + ".");
            }
        }

        private static IReadOnlyList<CodeBlock> Scan(string scanned, string text, EditorLanguage language)
        {
            // Kotlin ends statements at line breaks; Java, Dart and Rust at `;` and braces only.
            var java = language != EditorLanguage.Kotlin;
            var tokens = CodeDeclarationScanner.Tokenize(scanned, java);
            var lineStarts = new List<int> { 0 };
            for (var i = 0; i < text.Length; i++) if (text[i] == '\n') lineStarts.Add(i + 1);

            var result = new List<CodeBlock>();
            var open = new Stack<(int Token, int HeaderToken, CodeBlockKind Kind, string Keyword, int HeaderLine, int BraceLine)>();
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Kind != TokenKind.Punctuation) continue;
                if (token.Text == "{")
                {
                    CodeBlockKind? parentKind = null;
                    string? parentKeyword = null;
                    if (open.Count > 0) { var parent = open.Peek(); parentKind = parent.Kind; parentKeyword = parent.Keyword; }
                    // Rust match arms end at a comma (`A => 1,`); elsewhere commas are part of headers (`impl<A, B>`).
                    var header = HeaderStart(tokens, i, java, language, commaEnds: language == EditorLanguage.Rust && parentKeyword == "match");
                    var keyword = Keyword(tokens, header, i, language);
                    // Expression assignments: Kotlin `val x = when (y) {`, Dart `final x = switch (y) {`, Rust `let x = match y {`.
                    var assigned = keyword == "fun" ? -1 : AfterTopLevel(tokens, header, i, "=");
                    if (assigned >= 0 && tokens[assigned].Kind == TokenKind.Identifier && AssignedKeywords(language).Contains(tokens[assigned].Text)) keyword = tokens[assigned].Text;
                    open.Push((i, header, Classify(tokens, header, i, keyword, parentKind, parentKeyword, language), keyword,
                        Line(lineStarts, tokens[header].Offset), Line(lineStarts, token.Offset)));
                }
                else if (token.Text == "}" && open.Count > 0)
                {
                    var block = open.Pop();
                    if (block.HeaderToken >= block.Token) continue; // a bare `{` has no header to show
                    var headerLine = Line(lineStarts, tokens[block.HeaderToken].Offset);
                    var endLine = Line(lineStarts, token.Offset);
                    if (endLine <= block.BraceLine) continue;
                    // Editor scopes are line based: a block whose header shares lines with an enclosing header
                    // (`run { items.map {`) would pin the same line twice.
                    if (SharesHeaderLines(open, block.HeaderLine, block.BraceLine)) continue;
                    var last = tokens[LastSignificant(tokens, block.Token - 1)];
                    var headerStart = tokens[block.HeaderToken].Offset;
                    // A Rust loop label loses its quote in the mask (`'outer:` scans as `outer:`); the header shows it.
                    if (language == EditorLanguage.Rust && headerStart > 0 && text[headerStart - 1] == '\'') headerStart--;
                    result.Add(new CodeBlock(block.Kind, headerStart, TokenEnd(text, last), tokens[block.Token].Offset, token.Offset, headerLine, endLine));
                }
            }
            result.Sort((a, b) => a.HeaderStart != b.HeaderStart ? a.HeaderStart.CompareTo(b.HeaderStart) : b.CloseBrace.CompareTo(a.CloseBrace));
            return result;
        }

        private static bool SharesHeaderLines(Stack<(int Token, int HeaderToken, CodeBlockKind Kind, string Keyword, int HeaderLine, int BraceLine)> open, int first, int last)
        {
            foreach (var outer in open)
                if (outer.HeaderToken < outer.Token && outer.HeaderLine <= last && first <= outer.BraceLine) return true;
            return false;
        }

        /// <summary>Walks back from the opening brace to the first token of the statement that owns it.</summary>
        private static int HeaderStart(List<Token> tokens, int brace, bool java, EditorLanguage language, bool commaEnds)
        {
            var start = brace;
            var j = brace - 1;
            while (j >= 0)
            {
                var token = tokens[j];
                if (token.Kind == TokenKind.Newline)
                {
                    if (java) { j--; continue; }
                    var previous = LastSignificant(tokens, j);
                    if (previous < 0) break;
                    var next = start < tokens.Count ? tokens[start] : default;
                    // The newline right before an Allman-style brace never ends the header.
                    var continues = start == brace || ContinuesBefore.Contains(next.Text) && next.Kind != TokenKind.Literal
                        || ContinuesAfter.Contains(tokens[previous].Text) && tokens[previous].Kind == TokenKind.Punctuation;
                    if (!continues) break;
                    j = previous;
                    continue;
                }
                if (token.Kind == TokenKind.Punctuation)
                {
                    // A call chain continues after a lambda argument: `items.filter { ... }.forEach {`.
                    if (token.Text == "}" && start < brace && (tokens[start].Is(".") || tokens[start].Is("?")))
                    {
                        var lambda = MatchBraceBackward(tokens, j);
                        if (lambda < 0) break;
                        start = lambda;
                        j = lambda - 1;
                        continue;
                    }
                    if (token.Text == ";" || token.Text == "{" || token.Text == "}" || token.Text == "(" || token.Text == "[" || commaEnds && token.Text == ",") break;
                    if (token.Text == ")" || token.Text == "]")
                    {
                        var match = MatchBackward(tokens, j, token.Text == ")" ? "(" : "[", token.Text);
                        if (match < 0) break;
                        start = match;
                        j = match - 1;
                        continue;
                    }
                }
                start = j;
                j--;
            }
            return language == EditorLanguage.Rust ? SkipAttributes(tokens, start, brace) : SkipAnnotations(tokens, start, brace);
        }

        private static int MatchBraceBackward(List<Token> tokens, int close)
        {
            var depth = 0;
            for (var j = close; j >= 0; j--)
            {
                if (tokens[j].Is("}")) depth++;
                else if (tokens[j].Is("{") && --depth == 0) return j;
            }
            return -1;
        }

        /// <summary>Matching opening bracket; lambdas inside the brackets (<c>f({ a })</c>) are skipped whole.</summary>
        private static int MatchBackward(List<Token> tokens, int close, string openText, string closeText)
        {
            var depth = 0;
            var braces = 0;
            for (var j = close; j >= 0; j--)
            {
                var token = tokens[j];
                if (token.Kind != TokenKind.Punctuation) continue;
                if (token.Text == "}") braces++;
                else if (token.Text == "{") { if (--braces < 0) return -1; }
                else if (braces > 0) continue;
                else if (token.Text == closeText) depth++;
                else if (token.Text == openText && --depth == 0) return j;
                else if (token.Text == ";") return -1;
            }
            return -1;
        }

        /// <summary>Leading <c>@Annotation(...)</c> entries are not part of the scope header.</summary>
        private static int SkipAnnotations(List<Token> tokens, int start, int brace)
        {
            var i = start;
            while (i < brace && tokens[i].Is("@") && !(i + 1 < brace && tokens[i + 1].Is("interface")))
            {
                var after = i + 1;
                // Kotlin use-site targets and qualified names: @file:JvmName, @kotlin.Deprecated.
                while (after < brace && tokens[after].Kind == TokenKind.Identifier)
                {
                    after++;
                    if (after + 1 < brace && (tokens[after].Is(".") || tokens[after].Is(":")) && tokens[after + 1].Kind == TokenKind.Identifier) { after++; continue; }
                    break;
                }
                if (after < brace && tokens[after].Is("("))
                {
                    var depth = 0;
                    for (; after < brace; after++)
                    {
                        if (tokens[after].Is("(")) depth++;
                        else if (tokens[after].Is(")") && --depth == 0) { after++; break; }
                    }
                }
                while (after < brace && tokens[after].Kind == TokenKind.Newline) after++;
                if (after >= brace || after == i + 1) break;
                i = after;
            }
            return i;
        }

        /// <summary>Leading Rust attributes (<c>#[derive(Debug)]</c>, <c>#![allow(x)]</c>) are not part of the scope header.</summary>
        private static int SkipAttributes(List<Token> tokens, int start, int brace)
        {
            var i = start;
            while (i < brace && tokens[i].Is("#"))
            {
                var open = NextSignificant(tokens, i + 1, brace);
                if (open < brace && tokens[open].Is("!")) open = NextSignificant(tokens, open + 1, brace);
                if (open >= brace || !tokens[open].Is("[")) break;
                var depth = 0;
                var after = open;
                for (; after < brace; after++)
                {
                    if (tokens[after].Is("[")) depth++;
                    else if (tokens[after].Is("]") && --depth == 0) { after++; break; }
                }
                after = NextSignificant(tokens, after, brace);
                if (after >= brace) break;
                i = after;
            }
            return i;
        }

        private static readonly HashSet<string> DartModifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "base", "final", "sealed", "static", "external", "late", "const", "covariant", "factory", "augment",
        };

        private static readonly HashSet<string> RustModifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            "pub", "async", "unsafe", "const", "extern", "default", "static",
        };

        private static HashSet<string> ModifiersOf(EditorLanguage language) =>
            language == EditorLanguage.Dart ? DartModifiers : language == EditorLanguage.Rust ? RustModifiers : Modifiers;

        private static readonly HashSet<string> KotlinAssigned = new HashSet<string>(StringComparer.Ordinal) { "if", "when", "try", "object" };
        private static readonly HashSet<string> DartAssigned = new HashSet<string>(StringComparer.Ordinal) { "switch" };
        private static readonly HashSet<string> RustAssigned = new HashSet<string>(StringComparer.Ordinal) { "if", "match", "loop", "unsafe" };
        private static readonly HashSet<string> NoneAssigned = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Keywords that name the block when they follow a top-level <c>=</c> in the header.</summary>
        private static HashSet<string> AssignedKeywords(EditorLanguage language) => language switch
        {
            EditorLanguage.Kotlin => KotlinAssigned,
            EditorLanguage.Dart => DartAssigned,
            EditorLanguage.Rust => RustAssigned,
            _ => NoneAssigned,
        };

        /// <summary>The first word of the header after modifiers, or an empty string.</summary>
        private static string Keyword(List<Token> tokens, int header, int brace, EditorLanguage language)
        {
            var modifiers = ModifiersOf(language);
            for (var i = header; i < brace; i++)
            {
                var token = tokens[i];
                if (token.Kind == TokenKind.Newline) continue;
                if (token.Is("@") && i + 1 < brace && tokens[i + 1].Is("interface")) return "interface";
                if (token.Kind != TokenKind.Identifier) return token.Kind == TokenKind.Punctuation ? token.Text : "";
                var next = NextSignificant(tokens, i + 1, brace);
                var following = tokens[next];
                if (language == EditorLanguage.Rust)
                {
                    // `pub(crate) fn`, `extern "C" fn` / `extern "C" {`, and loop labels (`'outer: loop {`, masked to `outer:`).
                    if (token.Text == "pub" && following.Is("(")) { i = MatchForward(tokens, next, brace, "(", ")"); continue; }
                    if (token.Text == "extern" && following.Kind == TokenKind.Literal)
                    {
                        if (NextSignificant(tokens, next + 1, brace) >= brace) return "extern";
                        i = next;
                        continue;
                    }
                    if (following.Is(":") && next + 1 < brace && tokens[NextSignificant(tokens, next + 1, brace)].Kind == TokenKind.Identifier
                        && tokens[NextSignificant(tokens, next + 1, brace)].Text is "loop" or "while" or "for") { i = next; continue; }
                }
                // Java `enum Name` and Kotlin `fun interface Name` name the kind themselves.
                if (token.Text == "enum" && !following.Is("class")) return "enum";
                if (token.Text == "fun" && following.Is("interface")) return "interface";
                if ((token.Text == "return" || token.Text == "throw") && following.Kind == TokenKind.Identifier) continue;
                if (modifiers.Contains(token.Text) && i + 1 < brace && following.Kind == TokenKind.Identifier) continue;
                return token.Text;
            }
            return "";
        }

        /// <summary>The token index of the bracket closing the one at <paramref name="open"/>, or the token before <paramref name="limit"/>.</summary>
        private static int MatchForward(List<Token> tokens, int open, int limit, string openText, string closeText)
        {
            var depth = 0;
            for (var i = open; i < limit; i++)
            {
                if (tokens[i].Is(openText)) depth++;
                else if (tokens[i].Is(closeText) && --depth == 0) return i;
            }
            return limit - 1;
        }

        private static CodeBlockKind Classify(List<Token> tokens, int header, int brace, string keyword, CodeBlockKind? parent, string? parentKeyword, EditorLanguage language)
        {
            var java = language == EditorLanguage.Java;
            if (language == EditorLanguage.Rust)
            {
                switch (keyword)
                {
                    case "struct": case "union": case "trait": case "impl": case "mod": case "extern":
                        return CodeBlockKind.Type;
                    case "fn": case "macro_rules":
                        return CodeBlockKind.Member;
                    case "match":
                        return CodeBlockKind.Conditional;
                    case "loop":
                        return CodeBlockKind.Loop;
                    case "unsafe":
                        return CodeBlockKind.Statement;
                }
                if (parentKeyword == "match") return CodeBlockKind.Conditional;
            }
            if (language == EditorLanguage.Dart)
            {
                switch (keyword)
                {
                    case "mixin": case "extension":
                        return CodeBlockKind.Type;
                    case "on":
                        return CodeBlockKind.Statement; // `on FormatException catch (e) {`
                }
                if (!IsControlKeyword(keyword) && tokens[header].Kind == TokenKind.Identifier)
                {
                    // Getters and setters: `int get length {`, `set value(int v) {`.
                    if (HasWord(tokens, header, brace, "get") || HasWord(tokens, header, brace, "set")) return CodeBlockKind.Member;
                    // Any other `name(...) {` is a declaration in Dart (functions, methods, constructors, local functions):
                    // calls never take a block after their parentheses. An `=` before the parenthesis is an assignment.
                    var paren = FirstTopLevel(tokens, header, brace, "(");
                    var assignment = FirstTopLevel(tokens, header, brace, "=");
                    if (paren >= 0 && (assignment < 0 || assignment > paren)) return CodeBlockKind.Member;
                }
            }
            switch (keyword)
            {
                case "class": case "interface": case "object": case "enum": case "record":
                    return CodeBlockKind.Type;
                case "fun": case "constructor": case "init": case "get": case "set":
                    return CodeBlockKind.Member;
                case "if": case "else": case "when": case "switch":
                    return CodeBlockKind.Conditional;
                case "for": case "while": case "do":
                    return CodeBlockKind.Loop;
                case "try": case "catch": case "finally": case "synchronized":
                    return CodeBlockKind.Statement;
            }
            if (parentKeyword == "when" || parentKeyword == "switch") return CodeBlockKind.Conditional;
            // Java anonymous class body: `new Name(...) {`.
            if (java && tokens[LastSignificant(tokens, brace - 1)].Is(")") && HasWord(tokens, header, brace, "new")) return CodeBlockKind.Type;
            if (header < brace && tokens[header].Is("static") && NextSignificant(tokens, header + 1, brace + 1) == brace) return CodeBlockKind.Member;
            // Java compact record constructor: `Name {` in a type body.
            if (java && parent == CodeBlockKind.Type && tokens[header].Kind == TokenKind.Identifier && NextSignificant(tokens, header + 1, brace + 1) == brace) return CodeBlockKind.Member;
            // Java members: `Type name(...) [throws X] {` directly in a type body.
            if (parent == CodeBlockKind.Type && HasTopLevel(tokens, header, brace, "(") && !HasTopLevel(tokens, header, brace, "=") && !HasTopLevel(tokens, header, brace, "->"))
                return CodeBlockKind.Member;
            return CodeBlockKind.Expression;
        }

        /// <summary>The first significant token after the last top-level <paramref name="text"/>, or -1.</summary>
        private static int AfterTopLevel(List<Token> tokens, int from, int to, string text)
        {
            var depth = 0;
            var found = -1;
            for (var i = from; i < to; i++)
            {
                var token = tokens[i];
                if (token.Kind != TokenKind.Punctuation) continue;
                if (depth == 0 && token.Text == text) found = i;
                if (token.Text == "(" || token.Text == "[") depth++;
                else if ((token.Text == ")" || token.Text == "]") && depth > 0) depth--;
            }
            if (found < 0) return -1;
            var next = NextSignificant(tokens, found + 1, to);
            return next < to ? next : -1;
        }

        private static bool IsControlKeyword(string keyword) =>
            keyword is "if" or "else" or "for" or "while" or "do" or "switch" or "try" or "catch" or "finally" or "on" or "return" or "await";

        /// <summary>Index of the first top-level <paramref name="text"/> (outside brackets), or -1.</summary>
        private static int FirstTopLevel(List<Token> tokens, int from, int to, string text)
        {
            var depth = 0;
            for (var i = from; i < to; i++)
            {
                var token = tokens[i];
                if (token.Kind != TokenKind.Punctuation) continue;
                if (depth == 0 && token.Text == text) return i;
                if (token.Text == "(" || token.Text == "[") depth++;
                else if ((token.Text == ")" || token.Text == "]") && depth > 0) depth--;
            }
            return -1;
        }

        private static bool HasWord(List<Token> tokens, int from, int to, string word)
        {
            for (var i = from; i < to; i++) if (tokens[i].Kind == TokenKind.Identifier && tokens[i].Text == word) return true;
            return false;
        }

        private static bool HasTopLevel(List<Token> tokens, int from, int to, string text)
        {
            var depth = 0;
            for (var i = from; i < to; i++)
            {
                var token = tokens[i];
                if (token.Kind != TokenKind.Punctuation) continue;
                if (depth == 0 && token.Text == text) return true;
                if (token.Text == "(" || token.Text == "[") depth++;
                else if ((token.Text == ")" || token.Text == "]") && depth > 0) depth--;
            }
            return false;
        }

        private static int LastSignificant(List<Token> tokens, int from)
        {
            for (var i = from; i >= 0; i--) if (tokens[i].Kind != TokenKind.Newline) return i;
            return -1;
        }

        private static int NextSignificant(List<Token> tokens, int from, int limit)
        {
            for (var i = from; i < limit && i < tokens.Count; i++) if (tokens[i].Kind != TokenKind.Newline) return i;
            return Math.Min(limit, tokens.Count - 1);
        }

        /// <summary>Literal tokens keep only their first character: find their real end in the text.</summary>
        private static int TokenEnd(string text, Token token)
        {
            if (token.Kind != TokenKind.Literal) return token.Offset + token.Text.Length;
            var end = token.Offset + 1;
            // The original text decides: a masked Dart/Rust literal is the token "0" at the position of its quote.
            if (text[token.Offset] == '"' || text[token.Offset] == '\'')
            {
                var quote = text[token.Offset];
                var raw = quote == '"' && token.Offset + 2 < text.Length && text[token.Offset + 1] == '"' && text[token.Offset + 2] == '"';
                if (raw) { var close = text.IndexOf("\"\"\"", token.Offset + 3, StringComparison.Ordinal); return close < 0 ? text.Length : close + 3; }
                while (end < text.Length && text[end] != quote && text[end] != '\n') end += text[end] == '\\' ? 2 : 1;
                return Math.Min(text.Length, end + 1);
            }
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_' || text[end] == '.')) end++;
            return end;
        }

        private static int Line(List<int> lineStarts, int offset)
        {
            var low = 0;
            var high = lineStarts.Count - 1;
            while (low < high)
            {
                var mid = (low + high + 1) / 2;
                if (lineStarts[mid] <= offset) low = mid; else high = mid - 1;
            }
            return low;
        }
    }
}
