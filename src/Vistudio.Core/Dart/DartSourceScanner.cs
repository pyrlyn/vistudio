using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Vistudio.Core.Dart
{
    public enum DartDeclarationKind { Type, Function, Main, Test, Group }

    /// <summary>A declaration or test call found by <see cref="DartSourceScanner"/>; offsets index the scanned text.</summary>
    public sealed class DartDeclaration
    {
        internal DartDeclaration(DartDeclarationKind kind, string name, string qualifiedName, int startOffset, int nameStart, int nameEnd, string signature)
        { Kind = kind; Name = name; QualifiedName = qualifiedName; StartOffset = startOffset; NameStart = nameStart; NameEnd = nameEnd; Signature = signature; }

        public DartDeclarationKind Kind { get; }
        /// <summary>The identifier (functions, types), or the literal test/group description.</summary>
        public string Name { get; }
        /// <summary><c>Class.method</c> for members; for tests the full name (enclosing group descriptions and the test, space separated).</summary>
        public string QualifiedName { get; }
        /// <summary>First non-blank character of the line holding the declaration.</summary>
        public int StartOffset { get; }
        public int NameStart { get; }
        public int NameEnd { get; }
        /// <summary>Header text up to the parameter list (functions) or the test call (tests), whitespace collapsed.</summary>
        public string Signature { get; }
        /// <summary>For tests and groups: the description contains interpolation, so it cannot be selected by name.</summary>
        public bool IsDynamicName { get; internal set; }
        /// <summary><c>testWidgets</c>: needs the Flutter test environment.</summary>
        public bool IsWidgetTest { get; internal set; }
        public bool IsTest => Kind == DartDeclarationKind.Test || Kind == DartDeclarationKind.Group;
    }

    /// <summary>
    /// Lexical Dart scanner for CodeLens: top-level and member functions, types, <c>main</c>, and <c>test</c> /
    /// <c>testWidgets</c> / <c>group</c> calls with their full names. Comments and string contents (including
    /// interpolation, raw and triple-quoted strings) are masked first so they never produce declarations.
    /// </summary>
    public static class DartSourceScanner
    {
        private static readonly HashSet<string> NotFunctions = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "for", "while", "switch", "catch", "return", "new", "const", "await", "assert", "throw", "super", "this",
            "on", "when", "yield", "do", "else", "try", "finally", "case", "default", "in", "is", "as", "rethrow", "with",
        };

        private static readonly Regex TypeHeader = new Regex(
            @"\b(?:(?:abstract|base|final|interface|sealed|macro)\s+)*(?<keyword>class|mixin\s+class|mixin|enum|extension\s+type|extension)\s+(?<name>[A-Za-z_$][\w$]*)",
            RegexOptions.CultureInvariant);
        private static readonly Regex Call = new Regex(@"(?<name>[A-Za-z_$][\w$]*)\s*(?:<[^(){};=]*>)?\s*\(", RegexOptions.CultureInvariant);
        private static readonly Regex TestCall = new Regex(@"(?<![\w$.])(?<name>testWidgets|test|group)\s*\(", RegexOptions.CultureInvariant);

        public static IReadOnlyList<DartDeclaration> Scan(string text)
        {
            var masked = Mask(text);
            var result = new List<DartDeclaration>();
            // Brace ranges of type bodies; functions count only at top level or directly inside one of them.
            var types = new List<(int Open, int Close, string Name)>();
            foreach (Match match in TypeHeader.Matches(masked))
            {
                var open = masked.IndexOfAny(new[] { '{', ';' }, match.Index + match.Length);
                if (open < 0 || masked[open] != '{') continue;
                var close = Matching(masked, open, '{', '}');
                if (close < 0) close = masked.Length - 1;
                var name = match.Groups["name"];
                if (name.Value == "on") continue; // unnamed extension: "extension on Foo"
                types.Add((open, close, name.Value));
                result.Add(new DartDeclaration(DartDeclarationKind.Type, name.Value, name.Value, LineStart(text, match.Index), name.Index, name.Index + name.Length,
                    Collapse(text.Substring(LineStart(text, match.Index), open - LineStart(text, match.Index)))));
            }
            var depth = Depths(masked);
            foreach (Match match in Call.Matches(masked))
            {
                var name = match.Groups["name"];
                if (NotFunctions.Contains(name.Value)) continue;
                var before = name.Index - 1;
                while (before >= 0 && (masked[before] == ' ' || masked[before] == '\t')) before--;
                if (before >= 0 && (masked[before] == '.' || masked[before] == '?' && before > 0 && masked[before - 1] == '.')) continue; // a.b(), a?.b()
                if (before >= 0 && masked[before] == '=' ) continue; // x = f(...)
                var open = match.Index + match.Length - 1;
                var close = Matching(masked, open, '(', ')');
                if (close < 0) continue;
                var after = SkipBlank(masked, close + 1);
                foreach (var modifier in new[] { "async*", "async", "sync*" })
                    if (string.CompareOrdinal(masked, after, modifier, 0, modifier.Length) == 0) { after = SkipBlank(masked, after + modifier.Length); break; }
                var isBody = after < masked.Length && (masked[after] == '{' || (masked[after] == '=' && after + 1 < masked.Length && masked[after + 1] == '>'));
                if (!isBody) continue;
                var owner = types.Where(t => t.Open < name.Index && name.Index < t.Close).OrderByDescending(t => t.Open).Select(t => (int?)t.Open).FirstOrDefault();
                var level = depth[name.Index];
                var ownerName = owner == null ? null : types.First(t => t.Open == owner).Name;
                if (owner == null ? level != 0 : level != depth[owner.Value] + 1) continue; // local functions and closures
                // Without a return type or modifier in front this is a call statement followed by a block (not valid Dart), skip it.
                if (owner == null && before >= 0 && masked[before] == ')') continue;
                var start = LineStart(text, name.Index);
                var kind = owner == null && name.Value == "main" ? DartDeclarationKind.Main : DartDeclarationKind.Function;
                result.Add(new DartDeclaration(kind, name.Value, ownerName == null ? name.Value : ownerName + "." + name.Value, start,
                    name.Index, name.Index + name.Length, Collapse(text.Substring(start, close + 1 - start))));
            }
            AddTests(text, masked, result);
            return result.OrderBy(d => d.NameStart).ToList();
        }

        private static void AddTests(string text, string masked, List<DartDeclaration> result)
        {
            var calls = new List<(Match Match, int Open, int Close, string? Description, bool Dynamic, int LiteralStart, int LiteralEnd)>();
            foreach (Match match in TestCall.Matches(masked))
            {
                var open = match.Index + match.Length - 1;
                var close = Matching(masked, open, '(', ')');
                if (close < 0) close = masked.Length - 1;
                var literal = SkipBlank(text, open + 1);
                var description = ReadLiteral(text, literal, out var literalEnd, out var dynamic);
                if (description == null) continue; // test(variable, ...) - not a test declaration we can name
                calls.Add((match, open, close, description, dynamic, literal, literalEnd));
            }
            foreach (var call in calls)
            {
                var groups = calls.Where(c => c.Match.Groups["name"].Value == "group" && c.Open < call.Match.Index && call.Match.Index < c.Close).OrderBy(c => c.Open).ToList();
                var full = string.Join(" ", groups.Select(g => g.Description).Concat(new[] { call.Description }));
                var keyword = call.Match.Groups["name"].Value;
                var start = LineStart(text, call.Match.Index);
                var declaration = new DartDeclaration(keyword == "group" ? DartDeclarationKind.Group : DartDeclarationKind.Test, call.Description!, full, start,
                    call.Match.Index, call.Match.Index + keyword.Length, Collapse(keyword + "(" + text.Substring(call.LiteralStart, call.LiteralEnd - call.LiteralStart) + ")"))
                {
                    IsDynamicName = call.Dynamic || groups.Any(g => g.Dynamic),
                    IsWidgetTest = keyword == "testWidgets",
                };
                result.Add(declaration);
            }
        }

        /// <summary>The value of a string literal at <paramref name="start"/> (adjacent literals concatenated); null when there is none.</summary>
        private static string? ReadLiteral(string text, int start, out int end, out bool dynamic)
        {
            dynamic = false;
            end = start;
            var builder = new StringBuilder();
            var any = false;
            var i = start;
            while (i < text.Length)
            {
                var raw = false;
                var j = i;
                if (j < text.Length && (text[j] == 'r' || text[j] == 'R') && j + 1 < text.Length && (text[j + 1] == '\'' || text[j + 1] == '"')) { raw = true; j++; }
                if (j >= text.Length || (text[j] != '\'' && text[j] != '"')) break;
                var quote = text[j];
                var triple = j + 2 < text.Length && text[j + 1] == quote && text[j + 2] == quote;
                j += triple ? 3 : 1;
                var closed = false;
                while (j < text.Length)
                {
                    if (triple ? j + 2 < text.Length && text[j] == quote && text[j + 1] == quote && text[j + 2] == quote : text[j] == quote)
                    { j += triple ? 3 : 1; closed = true; break; }
                    if (!triple && (text[j] == '\n' || text[j] == '\r')) break;
                    if (!raw && text[j] == '\\' && j + 1 < text.Length)
                    {
                        var next = text[j + 1];
                        builder.Append(next switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => next });
                        j += 2;
                        continue;
                    }
                    if (!raw && text[j] == '$') dynamic = true;
                    builder.Append(text[j]);
                    j++;
                }
                if (!closed) return null;
                any = true;
                end = j;
                i = SkipBlank(text, j);
            }
            return any ? builder.ToString() : null;
        }

        /// <summary>
        /// The text with comments and string contents replaced by spaces (quotes and line breaks kept, so offsets and lines
        /// match). Interpolated expressions inside strings are masked too.
        /// </summary>
        public static string Mask(string text)
        {
            var chars = text.ToCharArray();
            var i = 0;
            MaskCode(chars, ref i, stopAtBrace: false);
            return new string(chars);
        }

        private static void MaskCode(char[] c, ref int i, bool stopAtBrace)
        {
            var braces = 0;
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
                    var nesting = 0;
                    while (i < c.Length)
                    {
                        if (c[i] == '/' && i + 1 < c.Length && c[i + 1] == '*') { nesting++; Blank(c, i); Blank(c, i + 1); i += 2; continue; }
                        if (c[i] == '*' && i + 1 < c.Length && c[i + 1] == '/') { nesting--; Blank(c, i); Blank(c, i + 1); i += 2; if (nesting == 0) break; continue; }
                        Blank(c, i++);
                    }
                    continue;
                }
                if (ch == '\'' || ch == '"')
                {
                    var raw = i > 0 && (c[i - 1] == 'r' || c[i - 1] == 'R') && (i < 2 || !IsIdentifier(c[i - 2]));
                    MaskString(c, ref i, raw);
                    continue;
                }
                if (stopAtBrace)
                {
                    if (ch == '{') braces++;
                    else if (ch == '}')
                    {
                        if (braces == 0) return; // end of ${...}; the caller masks the brace
                        braces--;
                    }
                }
                i++;
            }
        }

        private static void MaskString(char[] c, ref int i, bool raw)
        {
            var quote = c[i];
            var triple = i + 2 < c.Length && c[i + 1] == quote && c[i + 2] == quote;
            i += triple ? 3 : 1;
            while (i < c.Length)
            {
                if (triple ? i + 2 < c.Length && c[i] == quote && c[i + 1] == quote && c[i + 2] == quote : c[i] == quote)
                {
                    i += triple ? 3 : 1;
                    return;
                }
                if (!triple && (c[i] == '\n' || c[i] == '\r')) return; // unterminated
                if (!raw && c[i] == '\\' && i + 1 < c.Length) { Blank(c, i); Blank(c, i + 1); i += 2; continue; }
                if (!raw && c[i] == '$' && i + 1 < c.Length && c[i + 1] == '{')
                {
                    var start = i;
                    i += 2;
                    MaskCode(c, ref i, stopAtBrace: true);
                    if (i < c.Length) i++; // closing brace
                    for (var k = start; k < i && k < c.Length; k++) Blank(c, k);
                    continue;
                }
                Blank(c, i++);
            }
        }

        private static void Blank(char[] c, int i)
        {
            if (i < c.Length && c[i] != '\n' && c[i] != '\r') c[i] = ' ';
        }

        private static bool IsIdentifier(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

        /// <summary>Brace depth before each offset of masked text.</summary>
        private static int[] Depths(string masked)
        {
            var depth = new int[masked.Length + 1];
            var current = 0;
            for (var i = 0; i < masked.Length; i++)
            {
                depth[i] = current;
                if (masked[i] == '{') current++;
                else if (masked[i] == '}' && current > 0) current--;
            }
            depth[masked.Length] = current;
            return depth;
        }

        private static int Matching(string masked, int open, char opening, char closing)
        {
            var level = 0;
            for (var i = open; i < masked.Length; i++)
            {
                if (masked[i] == opening) level++;
                else if (masked[i] == closing && --level == 0) return i;
            }
            return -1;
        }

        private static int SkipBlank(string text, int i)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            return i;
        }

        private static int LineStart(string text, int offset)
        {
            var start = offset;
            while (start > 0 && text[start - 1] != '\n' && text[start - 1] != '\r') start--;
            while (start < offset && (text[start] == ' ' || text[start] == '\t')) start++;
            return start;
        }

        private static string Collapse(string text)
        {
            var collapsed = Regex.Replace(text, @"\s+", " ").Trim();
            return collapsed.Length > 160 ? collapsed.Substring(0, 157) + "..." : collapsed;
        }
    }
}
