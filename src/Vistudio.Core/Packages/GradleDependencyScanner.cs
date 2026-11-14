using System;
using System.Collections.Generic;
using System.Linq;
using static Vistudio.Core.CodeLens.CodeDeclarationScanner;

namespace Vistudio.Core.Packages
{
    internal enum GradleDeclarationKind { Literal, Variable, Kotlin, Catalog, Bundle }

    /// <summary>One external module a build script declares, with the spans an edit needs.</summary>
    internal sealed class GradleDeclaration
    {
        public string Name = "";
        public string Configuration = "";
        public PackageScope Scope;
        public GradleDeclarationKind Kind;
        public string Requested = "";

        /// <summary>1-based line of the configuration name in the build file.</summary>
        public int Line;

        /// <summary>The statement in the build file, from the configuration name to the closing parenthesis or lambda.</summary>
        public int Start;
        public int End;

        /// <summary>The file holding the version text (the build file or the version catalog) and its span; start -1 when there is none to rewrite.</summary>
        public string VersionFile = "";
        public int VersionStart = -1;
        public int VersionLength;
        public int VersionLine;

        /// <summary>The catalog accessor (<c>libs.x.y</c>, <c>libs.bundles.z</c>) the build file uses.</summary>
        public string? Accessor;
    }

    /// <summary>
    /// Lexical scanner for the <c>dependencies { }</c> blocks of <c>build.gradle.kts</c> and <c>build.gradle</c>. It reuses
    /// the Kotlin tokenizer of <see cref="Vistudio.Core.CodeLens.CodeDeclarationScanner"/>, which also skips Groovy's
    /// single-quoted strings and comments, so text inside strings and comments never produces a declaration.
    /// </summary>
    internal sealed class GradleDependencyScanner
    {
        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        { "val", "var", "def", "if", "else", "for", "while", "when", "return", "fun", "import", "throw", "try" };
        private static readonly HashSet<string> NotPackages = new HashSet<string>(StringComparer.Ordinal)
        { "project", "files", "fileTree", "gradleApi", "localGroovy", "gradleTestKit", "layout", "sourceSets" };
        private static readonly HashSet<string> Wrappers = new HashSet<string>(StringComparer.Ordinal)
        { "platform", "enforcedPlatform", "testFixtures" };

        private readonly string path;
        private readonly string text;
        private readonly GradleVersionCatalog? catalog;
        private readonly List<Token> tokens;
        private readonly bool groovy;

        private GradleDependencyScanner(string path, string text, GradleVersionCatalog? catalog)
        {
            this.path = path; this.text = text; this.catalog = catalog;
            groovy = !path.EndsWith(".kts", StringComparison.OrdinalIgnoreCase);
            tokens = Tokenize(text, java: false);
        }

        public List<GradleDeclaration> Declarations { get; } = new List<GradleDeclaration>();

        /// <summary>Offsets of the braces of the first top-level <c>dependencies { }</c>; -1 when the script has none.</summary>
        public int TopLevelOpen { get; private set; } = -1;
        public int TopLevelClose { get; private set; } = -1;

        public static GradleDependencyScanner Scan(string path, string text, GradleVersionCatalog? catalog)
        {
            var scanner = new GradleDependencyScanner(path, text, catalog);
            scanner.Run();
            return scanner;
        }

        private sealed class Block
        {
            public string? Name;
            public string? Qualifier;
            public bool TopLevelDependencies;
        }

        private void Run()
        {
            var blocks = new Stack<Block>();
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Is("{")) { blocks.Push(Open(i, blocks.Count == 0)); continue; }
                if (token.Is("}"))
                {
                    if (blocks.Count > 0 && blocks.Pop().TopLevelDependencies && TopLevelClose < 0) TopLevelClose = token.Offset;
                    continue;
                }
                if (token.Kind != TokenKind.Identifier || blocks.Count == 0 || blocks.Peek().Name != "dependencies") continue;
                // buildscript { dependencies { classpath(...) } } configures the build itself, not the project.
                if (blocks.Any(b => b.Name == "buildscript")) continue;
                if (!StatementStart(i) || Keywords.Contains(token.Text)) continue;
                var test = blocks.Any(b => EndsWithTest(b.Name) || EndsWithTest(b.Qualifier));
                Statement(i, test);
            }
        }

        private Block Open(int index, bool topLevel)
        {
            var block = new Block();
            var p = Previous(index);
            if (p >= 0 && tokens[p].Kind == TokenKind.Identifier)
            {
                block.Name = tokens[p].Text;
                var dot = Previous(p);
                if (dot >= 0 && tokens[dot].Is(".") && Previous(dot) is var q && q >= 0 && tokens[q].Kind == TokenKind.Identifier) block.Qualifier = tokens[q].Text;
                // val jvmTest by getting { } names the source set before "by".
                if ((block.Name == "getting" || block.Name == "creating") && Previous(p) is var by && by >= 1 && tokens[by].Is("by")
                    && Previous(by) is var named && named >= 0 && tokens[named].Kind == TokenKind.Identifier)
                    block.Name = tokens[named].Text;
            }
            else if (p >= 0 && tokens[p].Is(")"))
            {
                var open = MatchBackward(p);
                var callee = open < 0 ? -1 : Previous(open);
                if (callee >= 0 && tokens[callee].Kind == TokenKind.Identifier) block.Name = tokens[callee].Text;
            }
            if (topLevel && block.Name == "dependencies" && block.Qualifier == null && TopLevelOpen < 0)
            {
                block.TopLevelDependencies = true;
                TopLevelOpen = tokens[index].Offset;
            }
            return block;
        }

        private static bool EndsWithTest(string? name) => name != null && name.Length > 4 && name.EndsWith("Test", StringComparison.Ordinal);

        private bool StatementStart(int i)
        {
            if (i == 0) return true;
            var before = tokens[i - 1];
            return before.Kind == TokenKind.Newline || before.Is("{") || before.Is(";") || before.Is("}");
        }

        private void Statement(int i, bool test)
        {
            var configuration = tokens[i].Text;
            int argsStart, argsEnd, end;
            if (i + 1 < tokens.Count && tokens[i + 1].Is("("))
            {
                var close = MatchForward(i + 1);
                if (close < 0) return;
                argsStart = i + 2; argsEnd = close;
                end = tokens[close].Offset + 1;
                if (close + 1 < tokens.Count && tokens[close + 1].Is("{"))
                {
                    var lambda = MatchForward(close + 1);
                    if (lambda >= 0) end = tokens[lambda].Offset + 1;
                }
            }
            else if (groovy && i + 1 < tokens.Count && (tokens[i + 1].Kind == TokenKind.Literal || tokens[i + 1].Kind == TokenKind.Identifier))
            {
                // Groovy command call: the argument runs to the end of the line.
                argsStart = i + 1;
                argsEnd = argsStart;
                while (argsEnd < tokens.Count && tokens[argsEnd].Kind != TokenKind.Newline) argsEnd++;
                var last = tokens[argsEnd - 1];
                end = last.Kind == TokenKind.Literal ? (Literal(last.Offset)?.End ?? last.Offset + 1) : last.Offset + last.Text.Length;
            }
            else return;
            var found = Argument(argsStart, argsEnd);
            foreach (var declaration in found)
            {
                declaration.Configuration = configuration;
                declaration.Scope = ScopeOf(configuration, test);
                declaration.Line = LineOf(text, tokens[i].Offset);
                declaration.Start = tokens[i].Offset;
                declaration.End = end;
                Declarations.Add(declaration);
            }
        }

        internal static PackageScope ScopeOf(string configuration, bool testSourceSet)
        {
            if (testSourceSet || configuration.StartsWith("test", StringComparison.Ordinal) || configuration.StartsWith("androidTest", StringComparison.Ordinal))
                return PackageScope.Development;
            if (configuration == "compileOnly" || configuration == "compileOnlyApi" || configuration.EndsWith("CompileOnly", StringComparison.Ordinal)
                || configuration.StartsWith("kapt", StringComparison.Ordinal) || configuration.StartsWith("ksp", StringComparison.Ordinal)
                || configuration == "annotationProcessor" || configuration.EndsWith("AnnotationProcessor", StringComparison.Ordinal))
                return PackageScope.Build;
            return PackageScope.Runtime;
        }

        private IEnumerable<GradleDeclaration> Argument(int start, int end)
        {
            if (start >= end) yield break;
            var first = tokens[start];
            if (first.Kind == TokenKind.Literal)
            {
                var coordinate = Coordinate(first.Offset);
                if (coordinate != null) yield return coordinate;
                yield break;
            }
            if (first.Kind != TokenKind.Identifier) yield break;
            if (start + 1 < end && (tokens[start + 1].Is(":") || tokens[start + 1].Is("=")) && (first.Text == "group" || first.Text == "name" || first.Text == "version"))
            {
                var named = Named(start, end);
                if (named != null) yield return named;
                yield break;
            }
            var call = start + 1 < end && tokens[start + 1].Is("(");
            if (call && Wrappers.Contains(first.Text))
            {
                var close = MatchForward(start + 1);
                if (close > 0) foreach (var inner in Argument(start + 2, close)) yield return inner;
                yield break;
            }
            if (call && first.Text == "kotlin")
            {
                var kotlin = Kotlin(start + 2, MatchForward(start + 1));
                if (kotlin != null) yield return kotlin;
                yield break;
            }
            if (NotPackages.Contains(first.Text) || first.Text != "libs" || catalog == null) yield break;
            var chain = new List<string>();
            var k = start + 1;
            while (k + 1 < end && tokens[k].Is(".") && tokens[k + 1].Kind == TokenKind.Identifier) { chain.Add(tokens[k + 1].Text); k += 2; }
            // .get() / .asProvider() only unwrap the provider; they are not part of the alias.
            if (chain.Count > 0 && (chain[chain.Count - 1] == "get" || chain[chain.Count - 1] == "asProvider") && k < end && tokens[k].Is("(")) chain.RemoveAt(chain.Count - 1);
            if (chain.Count == 0) yield break;
            if (chain[0] == "bundles" && chain.Count > 1)
            {
                var accessor = string.Join(".", chain.Skip(1));
                foreach (var library in catalog.Bundle(accessor) ?? Array.Empty<GradleCatalogLibrary>())
                    yield return FromCatalog(library, GradleDeclarationKind.Bundle, "libs.bundles." + accessor);
                yield break;
            }
            if (chain[0] == "versions" || chain[0] == "plugins") yield break;
            var found = catalog.Find(string.Join(".", chain));
            if (found != null) yield return FromCatalog(found, GradleDeclarationKind.Catalog, "libs." + string.Join(".", chain));
        }

        private GradleDeclaration FromCatalog(GradleCatalogLibrary library, GradleDeclarationKind kind, string accessor) => new GradleDeclaration
        {
            Name = library.Module, Kind = kind, Requested = library.Version, Accessor = accessor,
            VersionFile = catalog!.Path, VersionStart = library.VersionStart, VersionLength = library.VersionLength, VersionLine = library.Line
        };

        private GradleDeclaration? Coordinate(int offset)
        {
            var literal = Literal(offset);
            if (literal == null) return null;
            var (content, contentStart, _, template) = literal.Value;
            var parts = content.Split(':');
            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;
            if (parts[0].IndexOf('$') >= 0 || parts[1].IndexOf('$') >= 0) return null;
            var declaration = new GradleDeclaration { Name = parts[0] + ":" + parts[1], Kind = GradleDeclarationKind.Literal, VersionFile = path };
            if (parts.Length < 3) return declaration;
            var version = parts[2];
            var at = version.IndexOf('@');
            if (at >= 0) version = version.Substring(0, at);
            declaration.Requested = version;
            if (template && version.IndexOf('$') >= 0) { declaration.Kind = GradleDeclarationKind.Variable; return declaration; }
            declaration.VersionStart = contentStart + parts[0].Length + parts[1].Length + 2;
            declaration.VersionLength = version.Length;
            declaration.VersionLine = LineOf(text, declaration.VersionStart);
            return declaration;
        }

        /// <summary><c>group: 'g', name: 'a', version: 'v'</c> (Groovy) or <c>group = "g", name = "a", version = "v"</c> (Kotlin).</summary>
        private GradleDeclaration? Named(int start, int end)
        {
            var values = new Dictionary<string, (string Content, int Start, bool Template)>();
            for (var k = start; k + 2 < end; k++)
            {
                if (tokens[k].Kind != TokenKind.Identifier || !(tokens[k + 1].Is(":") || tokens[k + 1].Is("=")) || tokens[k + 2].Kind != TokenKind.Literal) continue;
                var literal = Literal(tokens[k + 2].Offset);
                if (literal != null) values[tokens[k].Text] = (literal.Value.Content, literal.Value.ContentStart, literal.Value.Template);
            }
            if (!values.TryGetValue("group", out var group) || !values.TryGetValue("name", out var name) || group.Template || name.Template) return null;
            var declaration = new GradleDeclaration { Name = group.Content + ":" + name.Content, Kind = GradleDeclarationKind.Literal, VersionFile = path };
            if (!values.TryGetValue("version", out var version)) return declaration;
            declaration.Requested = version.Content;
            if (version.Template && version.Content.IndexOf('$') >= 0) { declaration.Kind = GradleDeclarationKind.Variable; return declaration; }
            declaration.VersionStart = version.Start;
            declaration.VersionLength = version.Content.Length;
            declaration.VersionLine = LineOf(text, version.Start);
            return declaration;
        }

        private GradleDeclaration? Kotlin(int start, int close)
        {
            if (close < 0 || start >= close || tokens[start].Kind != TokenKind.Literal) return null;
            var module = Literal(tokens[start].Offset);
            if (module == null || module.Value.Template) return null;
            var declaration = new GradleDeclaration { Name = "org.jetbrains.kotlin:kotlin-" + module.Value.Content, Kind = GradleDeclarationKind.Kotlin, VersionFile = path };
            if (start + 2 < close && tokens[start + 1].Is(",") && tokens[start + 2].Kind == TokenKind.Literal)
                declaration.Requested = Literal(tokens[start + 2].Offset)?.Content ?? "";
            return declaration;
        }

        /// <summary>A single-line string literal: content, content offset, end offset, and whether it contains a template.</summary>
        private (string Content, int ContentStart, int End, bool Template)? Literal(int offset)
        {
            var quote = text[offset];
            if (quote != '"' && quote != '\'') return null;
            if (offset + 2 < text.Length && text[offset + 1] == quote && text[offset + 2] == quote) return null; // multi-line strings are not coordinates
            var template = false;
            var i = offset + 1;
            while (i < text.Length)
            {
                var c = text[i];
                if (c == '\n') return null;
                if (c == '\\') { i += 2; continue; }
                if (c == quote) return (text.Substring(offset + 1, i - offset - 1), offset + 1, i + 1, template);
                // Groovy single-quoted strings are never templates; Kotlin and Groovy double-quoted ones are.
                if (c == '$' && quote == '"') template = true;
                i++;
            }
            return null;
        }

        private int Previous(int index)
        {
            for (var k = index - 1; k >= 0; k--) if (tokens[k].Kind != TokenKind.Newline) return k;
            return -1;
        }

        private int MatchForward(int open)
        {
            var opening = tokens[open].Text;
            var closing = opening == "(" ? ")" : opening == "{" ? "}" : "]";
            var depth = 0;
            for (var k = open; k < tokens.Count; k++)
            {
                if (tokens[k].Is(opening)) depth++;
                else if (tokens[k].Is(closing) && --depth == 0) return k;
            }
            return -1;
        }

        private int MatchBackward(int close)
        {
            var depth = 0;
            for (var k = close; k >= 0; k--)
            {
                if (tokens[k].Is(")")) depth++;
                else if (tokens[k].Is("(") && --depth == 0) return k;
            }
            return -1;
        }

        internal static int LineOf(string text, int offset)
        {
            var line = 1;
            for (var i = 0; i < offset && i < text.Length; i++) if (text[i] == '\n') line++;
            return line;
        }
    }
}
