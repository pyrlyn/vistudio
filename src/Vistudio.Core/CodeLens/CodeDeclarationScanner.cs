using System;
using System.Collections.Generic;

namespace Vistudio.Core.CodeLens
{
    public enum CodeDeclarationKind { Class, Interface, Object, Enum, Function, Property, Constructor }

    /// <summary>Further facts about a declaration that select optional lenses.</summary>
    [Flags]
    public enum CodeDeclarationTraits
    {
        None = 0,
        /// <summary>Kotlin <c>override</c> or Java <c>@Override</c>.</summary>
        Override = 1,
        /// <summary>A Kotlin <c>open</c> member with a body (overridable, not abstract, not an interface member).</summary>
        Open = 2,
        /// <summary>A Kotlin function annotated <c>@Preview</c> (Compose).</summary>
        Preview = 4,
        /// <summary>Kotlin <c>expect</c>, explicit or as a member of an <c>expect</c> type.</summary>
        Expect = 8,
        /// <summary>Kotlin <c>actual</c>.</summary>
        Actual = 16,
    }

    /// <summary>A declaration that gets CodeLens: positions are 0-based (LSP) and in UTF-16 units.</summary>
    public sealed class CodeDeclaration
    {
        public CodeDeclaration(string name, CodeDeclarationKind kind, string? container, int startOffset, int nameOffset, int nameLength, int nameLine, int nameColumn, bool canBeImplemented, bool isMain,
            CodeDeclarationTraits traits = CodeDeclarationTraits.None)
        {
            Name = name; Kind = kind; Container = container; StartOffset = startOffset; NameOffset = nameOffset; NameLength = nameLength;
            NameLine = nameLine; NameColumn = nameColumn; CanBeImplemented = canBeImplemented; IsMain = isMain; Traits = traits;
        }
        public string Name { get; }
        public CodeDeclarationKind Kind { get; }
        /// <summary>Dotted chain of enclosing type names, or null at file level.</summary>
        public string? Container { get; }
        /// <summary>Offset of the first annotation or modifier of the declaration (where the lens line goes above).</summary>
        public int StartOffset { get; }
        public int NameOffset { get; }
        /// <summary>Length of the name as written, including Kotlin backticks.</summary>
        public int NameLength { get; }
        /// <summary>End of the name: the lens range spans <see cref="StartOffset"/> to here.</summary>
        public int EndOffset => NameOffset + NameLength;
        public int NameLine { get; }
        public int NameColumn { get; }
        /// <summary>Interfaces, abstract/open/sealed classes and abstract/open (or interface) members.</summary>
        public bool CanBeImplemented { get; }
        /// <summary>A JVM entry point: Kotlin top-level <c>fun main</c> or Java <c>public static void main</c>.</summary>
        public bool IsMain { get; }
        public CodeDeclarationTraits Traits { get; }
        public bool IsOverride => (Traits & CodeDeclarationTraits.Override) != 0;
        public bool IsOpen => (Traits & CodeDeclarationTraits.Open) != 0;
        public bool IsPreview => (Traits & CodeDeclarationTraits.Preview) != 0;
        public bool IsExpect => (Traits & CodeDeclarationTraits.Expect) != 0;
        public bool IsActual => (Traits & CodeDeclarationTraits.Actual) != 0;
        public bool IsType => Kind == CodeDeclarationKind.Class || Kind == CodeDeclarationKind.Interface || Kind == CodeDeclarationKind.Object || Kind == CodeDeclarationKind.Enum;
        public string QualifiedName => Container == null ? Name : Container + "." + Name;
        public override string ToString() => Kind + " " + QualifiedName + " @" + NameLine + ":" + NameColumn;
    }

    /// <summary>
    /// Lexical scanner for the declarations that carry CodeLens in Kotlin (<c>.kt</c>) and Java sources: types and
    /// their members at file level and inside type bodies. Function bodies, initializers, lambdas, strings and
    /// comments are skipped, so local declarations never get lenses. No compiler: the scanner tolerates broken code
    /// and the language server answers the semantic questions (references, implementations) per declaration.
    /// </summary>
    public static class CodeDeclarationScanner
    {
        internal enum TokenKind { Identifier, Punctuation, Literal, Newline }

        internal readonly struct Token
        {
            public Token(TokenKind kind, string text, int offset) { Kind = kind; Text = text; Offset = offset; }
            public TokenKind Kind { get; }
            public string Text { get; }
            public int Offset { get; }
            public bool Is(string text) => Text == text && Kind != TokenKind.Literal;
        }

        private sealed class Scope
        {
            public Scope(bool declarations, string? name, bool isInterface, bool isEnum, int parenBase, bool isExpect = false)
            { Declarations = declarations; Name = name; IsInterface = isInterface; IsEnum = isEnum; ParenBase = parenBase; EnumConstants = isEnum; IsExpect = isExpect; }
            /// <summary>File level or a type body; bodies of functions, initializers and lambdas are opaque.</summary>
            public bool Declarations { get; }
            public string? Name { get; }
            public bool IsInterface { get; }
            public bool IsEnum { get; }
            public int ParenBase { get; }
            /// <summary>Body of a Kotlin <c>expect</c> type: its members are implicitly <c>expect</c>.</summary>
            public bool IsExpect { get; }
            /// <summary>Java enum bodies start with constants up to the first <c>;</c>.</summary>
            public bool EnumConstants { get; set; }
        }

        private static readonly HashSet<string> KotlinModifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            "public", "private", "protected", "internal", "abstract", "open", "sealed", "final", "override", "data", "inner",
            "enum", "annotation", "value", "companion", "inline", "noinline", "crossinline", "suspend", "operator", "infix",
            "tailrec", "external", "const", "lateinit", "expect", "actual", "vararg", "reified",
        };

        private static readonly HashSet<string> JavaModifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            "public", "private", "protected", "static", "final", "abstract", "default", "synchronized", "native",
            "transient", "volatile", "strictfp", "sealed", "non-sealed",
        };

        public static bool IsSupportedFile(string path) =>
            path.EndsWith(".kt", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".java", StringComparison.OrdinalIgnoreCase);

        public static IReadOnlyList<CodeDeclaration> Scan(string path, string text) =>
            path.EndsWith(".java", StringComparison.OrdinalIgnoreCase) ? ScanJava(text) : ScanKotlin(text);

        /// <summary>The <c>package</c> name of a Kotlin or Java source, or "" for the default package.</summary>
        public static string PackageOf(string path, string text)
        {
            var tokens = Tokenize(text, path.EndsWith(".java", StringComparison.OrdinalIgnoreCase));
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Kind == TokenKind.Newline) continue;
                if (token.Is("@"))
                {
                    // Kotlin file annotations (@file:JvmName("X")) precede the package: skip to the end of the line.
                    while (i + 1 < tokens.Count && tokens[i + 1].Kind != TokenKind.Newline) i++;
                    continue;
                }
                if (!token.Is("package")) return "";
                var name = new System.Text.StringBuilder();
                for (var j = i + 1; j < tokens.Count && (tokens[j].Kind == TokenKind.Identifier || tokens[j].Is(".")); j++)
                    name.Append(Unquote(tokens[j].Text));
                return name.ToString();
            }
            return "";
        }

        public static IReadOnlyList<CodeDeclaration> ScanKotlin(string text) => new Parser(text, Tokenize(text, java: false), java: false).Run();
        public static IReadOnlyList<CodeDeclaration> ScanJava(string text) => new Parser(text, Tokenize(text, java: true), java: true).Run();

        private sealed class Parser
        {
            private readonly string text;
            private readonly List<Token> tokens;
            private readonly bool java;
            private readonly List<int> lineStarts = new List<int> { 0 };
            private readonly List<CodeDeclaration> result = new List<CodeDeclaration>();
            private readonly Stack<Scope> scopes = new Stack<Scope>();
            private Scope? pendingType;
            /// <summary>Simple names of the annotations of the declaration being recognized.</summary>
            private readonly HashSet<string> annotations = new HashSet<string>(StringComparer.Ordinal);
            private int parens;
            private int index;

            public Parser(string text, List<Token> tokens, bool java)
            {
                this.text = text; this.tokens = tokens; this.java = java;
                for (var i = 0; i < text.Length; i++) if (text[i] == '\n') lineStarts.Add(i + 1);
            }

            private Token Current => tokens[index];
            private bool AtEnd => index >= tokens.Count;

            public IReadOnlyList<CodeDeclaration> Run()
            {
                scopes.Push(new Scope(true, null, false, false, 0));
                var statementStart = true;
                while (!AtEnd)
                {
                    var token = Current;
                    var scope = scopes.Peek();
                    if (token.Kind == TokenKind.Newline)
                    {
                        index++;
                        if (!java && parens == scope.ParenBase) statementStart = true;
                        continue;
                    }
                    if (statementStart && scope.Declarations && parens == scope.ParenBase && TryDeclaration(scope))
                    {
                        statementStart = false;
                        continue;
                    }
                    index++;
                    switch (token.Kind == TokenKind.Punctuation ? token.Text : "")
                    {
                        case "(": case "[": parens++; break;
                        case ")": case "]": if (parens > 0) parens--; break;
                        case "{":
                            // Only the body that follows a type header holds member declarations.
                            var type = pendingType != null && parens == pendingType.ParenBase ? pendingType : null;
                            scopes.Push(type ?? new Scope(false, null, false, false, parens));
                            // A lambda inside the header's parentheses (`class A(val f: () -> Unit = {})`) keeps the
                            // type pending; any other brace ends the header.
                            if (type != null || pendingType == null || parens <= pendingType.ParenBase) pendingType = null;
                            statementStart = true;
                            continue;
                        case "}":
                            if (scopes.Count > 1)
                            {
                                var closed = scopes.Pop();
                                parens = closed.ParenBase;
                                if (pendingType != null && closed.ParenBase <= pendingType.ParenBase) pendingType = null;
                            }
                            else pendingType = null;
                            statementStart = true;
                            continue;
                        case ",":
                            if (scope.IsEnum && scope.EnumConstants && parens == scope.ParenBase) { statementStart = true; continue; }
                            break;
                        case ";":
                            if (scope.IsEnum) scope.EnumConstants = false;
                            if (parens == scope.ParenBase) pendingType = null;
                            statementStart = true;
                            continue;
                    }
                    statementStart = false;
                }
                return result;
            }

            /// <summary>Recognizes a declaration at the current statement start; consumes its header up to the name.</summary>
            private bool TryDeclaration(Scope scope)
            {
                var start = index;
                var modifiers = new HashSet<string>(StringComparer.Ordinal);
                annotations.Clear();
                SkipModifiers(modifiers);
                if (AtEnd) { index = start; return false; }
                var startOffset = tokens[start].Offset;
                var recognized = java ? JavaDeclaration(scope, modifiers, startOffset) : KotlinDeclaration(scope, modifiers, startOffset);
                if (!recognized) index = start;
                return recognized;
            }

            private void SkipModifiers(HashSet<string> modifiers)
            {
                var set = java ? JavaModifiers : KotlinModifiers;
                while (!AtEnd)
                {
                    var token = Current;
                    if (token.Kind == TokenKind.Newline) { index++; continue; }
                    if (token.Is("@") && index + 1 < tokens.Count && tokens[index + 1].Is("interface")) return; // Java @interface
                    if (token.Is("@")) { SkipAnnotation(); continue; }
                    if (token.Kind != TokenKind.Identifier || !set.Contains(token.Text)) return;
                    // Soft Kotlin modifiers are also plain names: `open` is a modifier only before another word.
                    var next = NextSignificant(index + 1);
                    if (!java && (next < 0 || tokens[next].Kind != TokenKind.Identifier)) return;
                    modifiers.Add(token.Text);
                    index++;
                }
            }

            private void SkipAnnotation()
            {
                index++; // @
                if (!AtEnd && Current.Is("["))
                {
                    SkipBalanced("[", "]");
                    return;
                }
                // Kotlin use-site targets: @file:JvmName, @get:Rule.
                while (!AtEnd && Current.Kind == TokenKind.Identifier)
                {
                    var segment = Current.Text;
                    index++;
                    if (AtEnd || !(Current.Is(".") || Current.Is(":"))) annotations.Add(segment);
                    if (!AtEnd && (Current.Is(".") || Current.Is(":")) && index + 1 < tokens.Count && tokens[index + 1].Kind == TokenKind.Identifier) { index++; continue; }
                    break;
                }
                if (!AtEnd && Current.Is("<")) SkipGenerics();
                if (!AtEnd && Current.Is("(")) SkipBalanced("(", ")");
            }

            private bool KotlinDeclaration(Scope scope, HashSet<string> modifiers, int startOffset)
            {
                var keyword = Current;
                if (keyword.Kind != TokenKind.Identifier) return false;
                switch (keyword.Text)
                {
                    case "class":
                    case "interface":
                    case "object":
                    {
                        var isInterface = keyword.Text == "interface";
                        index++;
                        if (keyword.Text == "object" && (AtEnd || Current.Kind != TokenKind.Identifier))
                        {
                            // Unnamed companion object: no lens, but its body still holds members.
                            if (!modifiers.Contains("companion")) return true;
                            pendingType = new Scope(true, Join(scope.Name, "Companion"), false, false, parens, IsExpect(scope, modifiers));
                            return true;
                        }
                        if (AtEnd || Current.Kind != TokenKind.Identifier) return true;
                        var name = Current;
                        index++;
                        var kind = isInterface ? CodeDeclarationKind.Interface
                            : keyword.Text == "object" ? CodeDeclarationKind.Object
                            : modifiers.Contains("enum") ? CodeDeclarationKind.Enum : CodeDeclarationKind.Class;
                        var implementable = isInterface || (kind == CodeDeclarationKind.Class && (modifiers.Contains("abstract") || modifiers.Contains("open") || modifiers.Contains("sealed")));
                        Add(name, kind, scope, startOffset, implementable, false, traits: KotlinTraits(scope, modifiers, member: false));
                        pendingType = new Scope(true, Join(scope.Name, name.Text), isInterface, false, parens, IsExpect(scope, modifiers));
                        return true;
                    }
                    case "fun":
                    {
                        index++;
                        // `fun interface Name` declares a functional interface.
                        if (!AtEnd && Current.Is("interface")) return KotlinDeclaration(scope, modifiers, startOffset);
                        var name = MemberName(stopAt: "(", out var hasReceiver);
                        if (name == null) return true;
                        var implementable = IsImplementableMember(scope, modifiers);
                        var isMain = name.Value.Text == "main" && scopes.Count == 1 && !hasReceiver;
                        var traits = KotlinTraits(scope, modifiers, member: true) | (annotations.Contains("Preview") ? CodeDeclarationTraits.Preview : 0);
                        Add(name.Value, CodeDeclarationKind.Function, scope, startOffset, implementable, isMain, traits: traits);
                        pendingType = null;
                        return true;
                    }
                    case "val":
                    case "var":
                    {
                        index++;
                        if (!AtEnd && Current.Is("(")) return true; // destructuring
                        var name = MemberName(stopAt: null, out _);
                        if (name == null) return true;
                        Add(name.Value, CodeDeclarationKind.Property, scope, startOffset, IsImplementableMember(scope, modifiers), false, traits: KotlinTraits(scope, modifiers, member: true));
                        pendingType = null;
                        return true;
                    }
                    case "constructor":
                        if (scope.Name == null || index + 1 >= tokens.Count || !tokens[index + 1].Is("(")) return false;
                        Add(keyword, CodeDeclarationKind.Constructor, scope, startOffset, false, false, LastSegment(scope.Name), KotlinTraits(scope, modifiers, member: false));
                        index++;
                        pendingType = null;
                        return true;
                    case "typealias":
                    case "import":
                    case "package":
                    case "init":
                        index++;
                        pendingType = null;
                        return true;
                }
                return false;
            }

            private static bool IsExpect(Scope scope, HashSet<string> modifiers) => scope.IsExpect || modifiers.Contains("expect");

            private static CodeDeclarationTraits KotlinTraits(Scope scope, HashSet<string> modifiers, bool member)
            {
                var traits = CodeDeclarationTraits.None;
                if (modifiers.Contains("override")) traits |= CodeDeclarationTraits.Override;
                if (member && modifiers.Contains("open") && !modifiers.Contains("abstract") && !scope.IsInterface) traits |= CodeDeclarationTraits.Open;
                if (IsExpect(scope, modifiers)) traits |= CodeDeclarationTraits.Expect;
                if (modifiers.Contains("actual")) traits |= CodeDeclarationTraits.Actual;
                return traits;
            }

            private static bool IsImplementableMember(Scope scope, HashSet<string> modifiers) =>
                modifiers.Contains("abstract") || modifiers.Contains("open") || (scope.IsInterface && !modifiers.Contains("private"));

            /// <summary>
            /// Kotlin member name after <c>fun</c>/<c>val</c>/<c>var</c>: skips type parameters and an extension
            /// receiver (<c>fun &lt;T&gt; List&lt;T&gt;.name(</c>) and returns the last identifier of the chain.
            /// </summary>
            private Token? MemberName(string? stopAt, out bool hasReceiver)
            {
                hasReceiver = false;
                if (!AtEnd && Current.Is("<")) SkipGenerics();
                Token? last = null;
                while (!AtEnd)
                {
                    var token = Current;
                    if (token.Kind == TokenKind.Identifier) { last = token; index++; continue; }
                    if (token.Is("<")) { SkipGenerics(); continue; }
                    if (token.Is("?")) { index++; continue; }
                    if (token.Is(".")) { hasReceiver = true; index++; continue; }
                    if (token.Is("(") && stopAt == null) return null;
                    if (token.Is("(") && last == null) { SkipBalanced("(", ")"); continue; } // function type receiver
                    break;
                }
                return last;
            }

            private bool JavaDeclaration(Scope scope, HashSet<string> modifiers, int startOffset)
            {
                var token = Current;
                if (token.Is("@") && index + 1 < tokens.Count && tokens[index + 1].Is("interface")) { index++; token = Current; }
                if (token.Kind == TokenKind.Identifier && (token.Text == "class" || token.Text == "interface" || token.Text == "enum" || token.Text == "record"))
                {
                    index++;
                    if (AtEnd || Current.Kind != TokenKind.Identifier) return true;
                    var name = Current;
                    index++;
                    var isInterface = token.Text == "interface";
                    var kind = isInterface ? CodeDeclarationKind.Interface : token.Text == "enum" ? CodeDeclarationKind.Enum : CodeDeclarationKind.Class;
                    Add(name, kind, scope, startOffset, isInterface || (kind == CodeDeclarationKind.Class && modifiers.Contains("abstract")), false);
                    pendingType = new Scope(true, Join(scope.Name, name.Text), isInterface, token.Text == "enum", parens);
                    return true;
                }
                if (scope.Name == null) return token.Kind == TokenKind.Identifier && (token.Text == "package" || token.Text == "import");
                if (scope.IsEnum && scope.EnumConstants)
                {
                    if (token.Kind != TokenKind.Identifier) return false;
                    Add(token, CodeDeclarationKind.Property, scope, startOffset, false, false);
                    index++;
                    if (!AtEnd && Current.Is("(")) SkipBalanced("(", ")");
                    return true;
                }
                // Member header: [type params] type name ( ... | = ... | ; ...
                Token? last = null;
                Token? type = null;
                var words = 0;
                while (!AtEnd)
                {
                    var current = Current;
                    if (current.Kind == TokenKind.Newline) { index++; continue; }
                    if (current.Kind == TokenKind.Identifier) { type = last; last = current; words++; index++; continue; }
                    if (current.Is("<")) { SkipGenerics(); continue; }
                    if (current.Is("[") || current.Is("]") || current.Is(".") || current.Is("?") || current.Is("...")) { index++; continue; }
                    if (current.Is("@")) { SkipAnnotation(); continue; }
                    break;
                }
                if (last == null || AtEnd) return last != null;
                var terminator = Current;
                if (terminator.Is("("))
                {
                    var isConstructor = words == 1 && last.Value.Text == LastSegment(scope.Name);
                    var isMain = last.Value.Text == "main" && type?.Text == "void" && modifiers.Contains("public") && modifiers.Contains("static");
                    var implementable = modifiers.Contains("abstract") || (scope.IsInterface && !modifiers.Contains("static") && !modifiers.Contains("private") && !modifiers.Contains("default"));
                    Add(last.Value, isConstructor ? CodeDeclarationKind.Constructor : CodeDeclarationKind.Function, scope, startOffset, implementable && !isConstructor, isMain,
                        traits: annotations.Contains("Override") && !isConstructor ? CodeDeclarationTraits.Override : CodeDeclarationTraits.None);
                    return true;
                }
                if (words >= 2 && (terminator.Is("=") || terminator.Is(";") || terminator.Is(",")))
                {
                    Add(last.Value, CodeDeclarationKind.Property, scope, startOffset, false, false);
                    return true;
                }
                return words >= 2;
            }

            private void Add(Token name, CodeDeclarationKind kind, Scope scope, int startOffset, bool implementable, bool isMain, string? displayName = null,
                CodeDeclarationTraits traits = CodeDeclarationTraits.None)
            {
                var line = Line(name.Offset);
                var plain = displayName ?? Unquote(name.Text);
                result.Add(new CodeDeclaration(plain, kind, scope.Name, startOffset, name.Offset, name.Text.Length, line, name.Offset - lineStarts[line], implementable, isMain, traits));
            }

            private int Line(int offset)
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

            private int NextSignificant(int from)
            {
                for (var i = from; i < tokens.Count; i++) if (tokens[i].Kind != TokenKind.Newline) return i;
                return -1;
            }

            private void SkipBalanced(string open, string close)
            {
                var depth = 0;
                while (!AtEnd)
                {
                    var token = Current;
                    index++;
                    if (token.Is(open)) depth++;
                    else if (token.Is(close) && --depth <= 0) return;
                }
            }

            /// <summary>Skips <c>&lt;...&gt;</c> type arguments; stops at tokens that cannot be inside them.</summary>
            private void SkipGenerics()
            {
                var depth = 0;
                while (!AtEnd)
                {
                    var token = Current;
                    if (token.Is("<")) depth++;
                    else if (token.Is(">")) { if (--depth <= 0) { index++; return; } }
                    else if (token.Is("{") || token.Is("}") || token.Is(";") || token.Is("=") || token.Is("(") && depth == 0) return;
                    index++;
                }
            }
        }

        private static string Join(string? container, string name) => container == null ? name : container + "." + name;
        private static string LastSegment(string? name) => name == null ? "" : name.Substring(name.LastIndexOf('.') + 1);
        private static string Unquote(string name) => name.Length > 1 && name[0] == '`' ? name.Substring(1, name.Length - 2) : name;

        internal static List<Token> Tokenize(string text, bool java)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (c == '\n') { tokens.Add(new Token(TokenKind.Newline, "\n", i)); i++; continue; }
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') { i = SkipBlockComment(text, i, nested: !java); continue; }
                if (c == '"') { var start = i; i = SkipString(text, i, java); tokens.Add(new Token(TokenKind.Literal, "\"", start)); continue; }
                if (c == '\'') { var start = i; i = SkipChar(text, i); tokens.Add(new Token(TokenKind.Literal, "'", start)); continue; }
                if (c == '`' && !java)
                {
                    var end = text.IndexOf('`', i + 1);
                    if (end < 0 || text.IndexOf('\n', i, end - i) >= 0) { i++; continue; }
                    tokens.Add(new Token(TokenKind.Identifier, text.Substring(i, end - i + 1), i));
                    i = end + 1;
                    continue;
                }
                if (char.IsLetter(c) || c == '_' || c == '$')
                {
                    var start = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '$')) i++;
                    // Java `non-sealed` is one modifier.
                    if (java && text.Substring(start, i - start) == "non" && i + 7 <= text.Length && text.Substring(i, 7) == "-sealed") i += 7;
                    tokens.Add(new Token(TokenKind.Identifier, text.Substring(start, i - start), start));
                    continue;
                }
                if (char.IsDigit(c))
                {
                    var start = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1]))) i++;
                    tokens.Add(new Token(TokenKind.Literal, "0", start));
                    continue;
                }
                if (c == '.' && i + 2 < text.Length && text[i + 1] == '.' && text[i + 2] == '.') { tokens.Add(new Token(TokenKind.Punctuation, "...", i)); i += 3; continue; }
                // `->`, `::`, `?.` and the like stay single characters: only brackets, `<`/`>`, `.`, `=`, `;`, `,` matter.
                if (c == '-' && i + 1 < text.Length && text[i + 1] == '>') { tokens.Add(new Token(TokenKind.Punctuation, "->", i)); i += 2; continue; }
                if (c == '=' && i + 1 < text.Length && text[i + 1] == '=') { tokens.Add(new Token(TokenKind.Punctuation, "==", i)); i += 2; continue; }
                tokens.Add(new Token(TokenKind.Punctuation, c.ToString(), i));
                i++;
            }
            return tokens;
        }

        private static int SkipBlockComment(string text, int i, bool nested)
        {
            var depth = 0;
            while (i < text.Length)
            {
                if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*') { depth++; i += 2; if (!nested && depth > 1) depth = 1; continue; }
                if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/') { i += 2; if (--depth == 0) return i; continue; }
                i++;
            }
            return i;
        }

        private static int SkipChar(string text, int i)
        {
            i++;
            while (i < text.Length && text[i] != '\'' && text[i] != '\n') i += text[i] == '\\' ? 2 : 1;
            return Math.Min(text.Length, i + 1);
        }

        /// <summary>Skips a string or text block; Kotlin templates <c>${...}</c> may nest strings and braces.</summary>
        private static int SkipString(string text, int i, bool java)
        {
            var raw = i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"';
            i += raw ? 3 : 1;
            while (i < text.Length)
            {
                var c = text[i];
                if (raw && c == '"' && i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"')
                {
                    i += 3;
                    while (i < text.Length && text[i] == '"') i++; // `""""` ends with extra quotes in the content
                    return i;
                }
                if (!raw && c == '"') return i + 1;
                if (!raw && c == '\n') return i; // unterminated
                if (c == '\\' && (java || !raw)) { i += 2; continue; }
                if (!java && c == '$' && i + 1 < text.Length && text[i + 1] == '{')
                {
                    i += 2;
                    var depth = 1;
                    while (i < text.Length && depth > 0)
                    {
                        var t = text[i];
                        if (t == '"') { i = SkipString(text, i, java); continue; }
                        if (t == '\'') { i = SkipChar(text, i); continue; }
                        if (t == '{') depth++;
                        else if (t == '}') depth--;
                        i++;
                    }
                    continue;
                }
                i++;
            }
            return i;
        }
    }
}
