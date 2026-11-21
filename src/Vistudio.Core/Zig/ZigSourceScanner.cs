using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Vistudio.Core.Structure;

namespace Vistudio.Core.Zig
{
    public enum ZigDeclarationKind { Function, Struct, Enum, Union, Opaque, ErrorSet, Test, Constant, Variable }

    public sealed class ZigDeclaration
    {
        internal ZigDeclaration(ZigDeclarationKind kind, string name, string container, string signature, int startOffset, int nameStart, int nameEnd, int line, bool isPublic)
        {
            Kind = kind; Name = name; Container = container; Signature = signature; StartOffset = startOffset;
            NameStart = nameStart; NameEnd = nameEnd; Line = line; IsPublic = isPublic;
        }
        public ZigDeclarationKind Kind { get; }
        public string Name { get; }
        /// <summary>Dotted names of the enclosing containers ("" at file level).</summary>
        public string Container { get; }
        public string QualifiedName => Container.Length == 0 ? Name : Container + "." + Name;
        /// <summary>Functions: <c>pub fn name(params) Return</c> on one line, comments removed. Others: the declaration head.</summary>
        public string Signature { get; }
        /// <summary>First token of the declaration (<c>pub</c>, <c>export</c>, <c>fn</c>, <c>const</c>, <c>test</c>, ...).</summary>
        public int StartOffset { get; }
        public int NameStart { get; }
        public int NameEnd { get; }
        /// <summary>0-based line of the name.</summary>
        public int Line { get; }
        public bool IsPublic { get; }
        public override string ToString() => Kind + " " + QualifiedName;
    }

    public sealed class ZigScanResult
    {
        internal ZigScanResult(IReadOnlyList<ZigDeclaration> declarations, IReadOnlyList<CodeBlock> blocks, IReadOnlyList<string> imports)
        { Declarations = declarations; Blocks = blocks; Imports = imports; }
        public IReadOnlyList<ZigDeclaration> Declarations { get; }
        /// <summary>Brace blocks for Sticky Scroll and structure guides (function, container, test and control-flow bodies).</summary>
        public IReadOnlyList<CodeBlock> Blocks { get; }
        /// <summary>Arguments of <c>@import("...")</c>, in order, without duplicates.</summary>
        public IReadOnlyList<string> Imports { get; }
    }

    /// <summary>
    /// Lexical Zig scanner: tokenizes (comments, <c>"..."</c> and <c>\\</c> multiline strings, char literals,
    /// <c>@"..."</c> identifiers, builtins) and reads declarations and brace blocks. It is not a parser; ZLS provides
    /// semantic answers, this feeds CodeLens anchors, signatures and block structure without a server.
    /// </summary>
    public static class ZigSourceScanner
    {
        private enum TokenKind { Identifier, Builtin, String, Char, Number, Punct }

        private readonly struct Token
        {
            public Token(TokenKind kind, int start, int end, string text) { Kind = kind; Start = start; End = end; Text = text; }
            public TokenKind Kind { get; }
            public int Start { get; }
            public int End { get; }
            public string Text { get; }
            public bool Is(string text) => (Kind == TokenKind.Identifier || Kind == TokenKind.Punct) && Text == text;
        }

        private static readonly HashSet<string> ContainerKeywords = new HashSet<string>(StringComparer.Ordinal) { "struct", "enum", "union", "opaque", "error" };
        private static readonly HashSet<string> Modifiers = new HashSet<string>(StringComparer.Ordinal) { "pub", "extern", "export", "inline", "noinline", "threadlocal", "comptime" };
        private static readonly Regex Comment = new Regex(@"//[^\n]*", RegexOptions.CultureInvariant);
        private static readonly Regex Space = new Regex(@"\s+", RegexOptions.CultureInvariant);

        /// <summary>Offsets of the real <c>{</c> and <c>}</c> tokens (not in comments, strings, characters or <c>\\</c> lines).</summary>
        public static IReadOnlyList<int> BracePositions(string text) =>
            Tokenize(text).Where(t => t.Kind == TokenKind.Punct && (t.Text == "{" || t.Text == "}")).Select(t => t.Start).ToList();

        public static ZigScanResult Scan(string text)
        {
            var tokens = Tokenize(text);
            var lines = LineStarts(text);
            var declarations = new List<ZigDeclaration>();
            var blocks = new List<CodeBlock>();
            var imports = new List<string>();

            // One frame per open brace: whether it is a container body (members separated by commas/semicolons),
            // the qualified name it adds, and the block being recorded.
            var frames = new Stack<Frame>();
            var parens = 0;
            var statementStart = 0;
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Kind == TokenKind.Builtin && token.Text == "@import" && i + 3 < tokens.Count && tokens[i + 1].Is("(") && tokens[i + 2].Kind == TokenKind.String && tokens[i + 3].Is(")"))
                {
                    var target = tokens[i + 2].Text;
                    if (!imports.Contains(target)) imports.Add(target);
                }
                if (token.Kind == TokenKind.Punct)
                {
                    switch (token.Text)
                    {
                        case "(": case "[": parens++; continue;
                        case ")": case "]": parens = Math.Max(0, parens - 1); continue;
                        case ";":
                            if (parens == 0) statementStart = i + 1;
                            continue;
                        case ",":
                            if (parens == 0) statementStart = i + 1;
                            continue;
                        case "{":
                            // `fn f() error{Oops}!void {`: an inline error set in a signature is part of the type.
                            if (i > 0 && tokens[i - 1].Is("error") && FirstNonModifier(tokens, statementStart, i) < i && tokens[FirstNonModifier(tokens, statementStart, i)].Is("fn"))
                            {
                                i = MatchingBrace(tokens, i);
                                continue;
                            }
                            var opened = OpenBlock(text, tokens, frames, statementStart, i, declarations, parens > 0);
                            opened.SavedParens = parens;
                            opened.SavedStatementStart = statementStart;
                            frames.Push(opened);
                            parens = 0;
                            statementStart = i + 1;
                            continue;
                        case "}":
                            if (frames.Count == 0) { parens = 0; statementStart = i + 1; continue; }
                            var frame = frames.Pop();
                            if (frame.Kind.HasValue)
                                blocks.Add(new CodeBlock(frame.Kind.Value, frame.HeaderStart, frame.HeaderEnd, frame.OpenBrace, token.Start,
                                    LineOf(lines, frame.HeaderStart), LineOf(lines, token.Start)));
                            // An expression literal continues its statement (`foo(.{ .a = 1 }, x)`); a block ends it.
                            parens = frame.Inline ? frame.SavedParens : 0;
                            statementStart = frame.Inline ? frame.SavedStatementStart : i + 1;
                            continue;
                    }
                    continue;
                }
                // Body-less declarations (`extern fn f() void;`, `const x = 1;`) are read at the `fn`/`const` token itself.
                if (i == FirstNonModifier(tokens, statementStart, i) && parens == 0)
                    ReadDeclaration(text, tokens, lines, frames, statementStart, i, declarations);
            }
            blocks.Sort((a, b) => a.HeaderStart.CompareTo(b.HeaderStart));
            return new ZigScanResult(declarations, blocks, imports);
        }

        private sealed class Frame
        {
            public CodeBlockKind? Kind;
            public int HeaderStart, HeaderEnd, OpenBrace;
            public bool IsContainer;
            /// <summary>An expression literal (<c>.{}</c>, <c>T{}</c>, or a brace inside parentheses): closing it resumes the statement.</summary>
            public bool Inline;
            public int SavedParens, SavedStatementStart;
            /// <summary>Name this block adds to qualified names (containers, and functions returning anonymous containers).</summary>
            public string Scope = "";
        }

        private static int MatchingBrace(List<Token> tokens, int open)
        {
            var depth = 0;
            for (var i = open; i < tokens.Count; i++)
            {
                if (tokens[i].Is("{")) depth++;
                else if (tokens[i].Is("}") && --depth == 0) return i;
            }
            return tokens.Count - 1;
        }

        private static string ContainerPath(Stack<Frame> frames) =>
            string.Join(".", frames.Reverse().Where(f => f.Scope.Length > 0).Select(f => f.Scope));

        private static bool InsideFunctionBody(Stack<Frame> frames) => frames.Count > 0 && !frames.Peek().IsContainer;

        private static int FirstNonModifier(List<Token> tokens, int start, int limit)
        {
            var i = start;
            while (i < limit && tokens[i].Kind == TokenKind.Identifier && Modifiers.Contains(tokens[i].Text)) i++;
            // `extern "c" fn`: a library name string after extern.
            if (i < limit && i > start && tokens[i].Kind == TokenKind.String && tokens[i - 1].Is("extern")) i++;
            while (i < limit && tokens[i].Kind == TokenKind.Identifier && Modifiers.Contains(tokens[i].Text)) i++;
            return i;
        }

        private static void ReadDeclaration(string text, List<Token> tokens, int[] lines, Stack<Frame> frames, int statementStart, int i, List<ZigDeclaration> declarations)
        {
            var token = tokens[i];
            var isPublic = statementStart < i && tokens.Skip(statementStart).Take(i - statementStart).Any(t => t.Is("pub"));
            var start = tokens[statementStart].Start;
            if (token.Is("fn") && i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Identifier)
            {
                var name = tokens[i + 1];
                var end = SignatureEnd(tokens, i + 2);
                var signature = Normalize(text.Substring(start, end - start));
                declarations.Add(new ZigDeclaration(ZigDeclarationKind.Function, name.Text, ContainerPath(frames), signature, start, name.Start, name.End, LineOf(lines, name.Start), isPublic));
                return;
            }
            if (token.Is("test") && !InsideFunctionBody(frames) && i + 1 < tokens.Count && (tokens[i + 1].Kind == TokenKind.String || tokens[i + 1].Kind == TokenKind.Identifier || tokens[i + 1].Is("{")))
            {
                var named = tokens[i + 1].Is("{") ? token : tokens[i + 1];
                var label = named.Kind == TokenKind.String ? named.Text : named.Kind == TokenKind.Identifier && !named.Is("test") ? named.Text : "";
                declarations.Add(new ZigDeclaration(ZigDeclarationKind.Test, label, ContainerPath(frames), Normalize(text.Substring(start, named.End - start)), start, named.Start, named.End, LineOf(lines, named.Start), false));
                return;
            }
            if ((token.Is("const") || token.Is("var")) && !InsideFunctionBody(frames) && i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Identifier)
            {
                var name = tokens[i + 1];
                var kind = token.Is("var") ? ZigDeclarationKind.Variable : ZigDeclarationKind.Constant;
                // `const Name = [extern|packed] struct|enum|union|opaque|error ...`
                var j = i + 2;
                var depth = 0;
                while (j < tokens.Count && !(depth == 0 && (tokens[j].Is("=") || tokens[j].Is(";"))))
                {
                    if (tokens[j].Is("(") || tokens[j].Is("[")) depth++;
                    else if (tokens[j].Is(")") || tokens[j].Is("]")) depth--;
                    j++;
                }
                if (j < tokens.Count && tokens[j].Is("="))
                {
                    var k = j + 1;
                    while (k < tokens.Count && (tokens[k].Is("extern") || tokens[k].Is("packed"))) k++;
                    if (k < tokens.Count && tokens[k].Kind == TokenKind.Identifier && ContainerKeywords.Contains(tokens[k].Text) && k + 1 < tokens.Count && (tokens[k + 1].Is("{") || tokens[k + 1].Is("(")))
                        kind = ContainerKind(tokens[k].Text);
                }
                declarations.Add(new ZigDeclaration(kind, name.Text, ContainerPath(frames), Normalize(text.Substring(start, name.End - start)), start, name.Start, name.End, LineOf(lines, name.Start), isPublic));
            }
        }

        private static ZigDeclarationKind ContainerKind(string keyword) => keyword switch
        {
            "struct" => ZigDeclarationKind.Struct,
            "enum" => ZigDeclarationKind.Enum,
            "union" => ZigDeclarationKind.Union,
            "opaque" => ZigDeclarationKind.Opaque,
            _ => ZigDeclarationKind.ErrorSet,
        };

        /// <summary>End offset of a function signature: past the parameter list and the return type, before `{` or `;`.</summary>
        private static int SignatureEnd(List<Token> tokens, int i)
        {
            var depth = 0;
            var last = i > 0 ? tokens[i - 1].End : 0;
            for (; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Is("{") && tokens[i - 1].Is("error"))
                {
                    i = MatchingBrace(tokens, i);
                    last = tokens[i].End;
                    continue;
                }
                if (depth == 0 && (t.Is("{") || t.Is(";"))) break;
                if (t.Is("(") || t.Is("[")) depth++;
                else if (t.Is(")") || t.Is("]")) depth--;
                last = t.End;
            }
            return last;
        }

        private static Frame OpenBlock(string text, List<Token> tokens, Stack<Frame> frames, int statementStart, int brace, List<ZigDeclaration> declarations, bool insideParentheses)
        {
            var frame = new Frame { OpenBrace = tokens[brace].Start };
            var previous = brace > 0 ? tokens[brace - 1] : default;
            // `.{ ... }` anonymous literals and `Type{ ... }` initializers are expressions, not structure.
            if (previous.Is(".") || (insideParentheses && !(previous.Kind == TokenKind.Identifier && ContainerKeywords.Contains(previous.Text))))
            {
                frame.IsContainer = true;
                frame.Inline = true;
                return frame;
            }
            if (brace == 0 || statementStart >= brace) return frame;
            var first = FirstNonModifier(tokens, statementStart, brace);
            var head = first < brace ? tokens[first] : tokens[statementStart];
            frame.HeaderStart = tokens[statementStart].Start;
            frame.HeaderEnd = previous.End;

            // Container keyword right before the brace, or before its `(...)` tag: `struct {`, `union(enum) {`, `enum(u8) {`.
            var k = brace - 1;
            if (tokens[k].Is(")"))
            {
                var depth = 0;
                for (; k >= statementStart; k--)
                {
                    if (tokens[k].Is(")")) depth++;
                    else if (tokens[k].Is("(") && --depth == 0) { k--; break; }
                }
            }
            if (k >= statementStart && tokens[k].Kind == TokenKind.Identifier && ContainerKeywords.Contains(tokens[k].Text))
            {
                frame.Kind = CodeBlockKind.Type;
                frame.IsContainer = true;
                // Anonymous containers (`return struct { ... }` in `fn List(comptime T: type) type`) add no name of
                // their own: the generic function's frame already contributes `List`.
                var named = declarations.LastOrDefault(d => d.StartOffset == frame.HeaderStart && d.Kind != ZigDeclarationKind.Function);
                if (named != null) frame.Scope = named.Name;
                return frame;
            }
            if (head.Is("fn") || tokens.Skip(first).Take(brace - first).Any(t => t.Is("fn")))
            {
                frame.Kind = CodeBlockKind.Member;
                var function = declarations.LastOrDefault(d => d.Kind == ZigDeclarationKind.Function && d.StartOffset == frame.HeaderStart);
                // A function body adds its name only for anonymous containers it returns (generic types).
                if (function != null) frame.Scope = ReturnsType(function) ? function.Name : "";
                return frame;
            }
            if (head.Is("test")) { frame.Kind = CodeBlockKind.Member; return frame; }
            if (head.Is("if") || head.Is("else") || head.Is("switch")) { frame.Kind = CodeBlockKind.Conditional; return frame; }
            if (head.Is("while") || head.Is("for")) { frame.Kind = CodeBlockKind.Loop; return frame; }
            if (head.Is("comptime") || head.Is("defer") || head.Is("errdefer") || head.Is("nosuspend") || head.Is("suspend")) { frame.Kind = CodeBlockKind.Statement; return frame; }
            // Labeled blocks `blk: {`, `catch |e| {`, `orelse {`, switch prongs `.a => {`.
            if (previous.Is(":") || previous.Is("=>") || previous.Is("|") || previous.Is("orelse") || previous.Is("catch") || previous.Is("else"))
            {
                frame.Kind = previous.Is("=>") ? CodeBlockKind.Conditional : CodeBlockKind.Statement;
                return frame;
            }
            // `Type{ .field = 1 }` initializer: an expression inside its statement.
            frame.IsContainer = true;
            frame.Inline = true;
            frame.Kind = null;
            return frame;
        }

        private static bool ReturnsType(ZigDeclaration function) => Regex.IsMatch(function.Signature, @"\)\s*type$");

        private static string Normalize(string signature) => Space.Replace(Comment.Replace(signature, " "), " ").Replace("( ", "(").Replace(" )", ")").Trim();

        private static int[] LineStarts(string text)
        {
            var starts = new List<int> { 0 };
            for (var i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
            return starts.ToArray();
        }

        private static int LineOf(int[] starts, int offset)
        {
            var index = Array.BinarySearch(starts, offset);
            return index >= 0 ? index : ~index - 1;
        }

        private static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                var start = i;
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
                if (c == '\\' && i + 1 < text.Length && text[i + 1] == '\\')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    tokens.Add(new Token(TokenKind.String, start, i, text.Substring(start + 2, i - start - 2).TrimEnd('\r')));
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    var value = ReadQuoted(text, ref i, c);
                    tokens.Add(new Token(c == '"' ? TokenKind.String : TokenKind.Char, start, i, value));
                    continue;
                }
                if (c == '@')
                {
                    i++;
                    if (i < text.Length && text[i] == '"')
                    {
                        var value = ReadQuoted(text, ref i, '"');
                        tokens.Add(new Token(TokenKind.Identifier, start, i, value));
                        continue;
                    }
                    while (i < text.Length && IsIdentifierPart(text[i])) i++;
                    tokens.Add(new Token(TokenKind.Builtin, start, i, text.Substring(start, i - start)));
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    while (i < text.Length && IsIdentifierPart(text[i])) i++;
                    tokens.Add(new Token(TokenKind.Identifier, start, i, text.Substring(start, i - start)));
                    continue;
                }
                if (char.IsDigit(c))
                {
                    i++;
                    while (i < text.Length)
                    {
                        var d = text[i];
                        if (IsIdentifierPart(d)) { i++; continue; }
                        if (d == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])) { i++; continue; }
                        if ((d == '+' || d == '-') && "eEpP".IndexOf(text[i - 1]) >= 0) { i++; continue; }
                        break;
                    }
                    tokens.Add(new Token(TokenKind.Number, start, i, text.Substring(start, i - start)));
                    continue;
                }
                // `=>` matters for switch prongs; every other operator is one character for this scanner.
                if (c == '=' && i + 1 < text.Length && text[i + 1] == '>') { i += 2; tokens.Add(new Token(TokenKind.Punct, start, i, "=>")); continue; }
                i++;
                tokens.Add(new Token(TokenKind.Punct, start, i, c.ToString()));
            }
            return tokens;
        }

        private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>Reads a quoted literal from the opening quote at <paramref name="i"/>; stops at the closing quote or end of line.</summary>
        private static string ReadQuoted(string text, ref int i, char quote)
        {
            var value = new StringBuilder();
            i++;
            while (i < text.Length && text[i] != quote && text[i] != '\n')
            {
                if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] != '\n') { value.Append(text[i]).Append(text[i + 1]); i += 2; continue; }
                value.Append(text[i++]);
            }
            if (i < text.Length && text[i] == quote) i++;
            return value.ToString();
        }

        /// <summary>Only function declarations: the CodeLens anchors.</summary>
        public static IReadOnlyList<ZigDeclaration> Functions(string text) =>
            Scan(text).Declarations.Where(d => d.Kind == ZigDeclarationKind.Function).ToList();
    }
}
