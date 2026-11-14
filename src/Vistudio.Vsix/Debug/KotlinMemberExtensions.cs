using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Vistudio
{
    // DocumentSymbol provides scope and declaration ranges; Kotlin signature help,
    // not a C# approximation of Kotlin's type system, decides applicability.
    internal static class KotlinMemberExtensions
    {
        internal static IReadOnlyList<string> Candidates(JToken symbols, string text, int frameOffset, string prefix)
        {
            var result = new List<string>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var starts = new List<int> { 0 };
            for (var i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
            var budget = 4096;
            int Offset(JToken? position)
            {
                var line = (int?)position?["line"] ?? -1;
                var column = (int?)position?["character"] ?? -1;
                if (line < 0 || line >= starts.Count || column < 0) return -1;
                var end = line + 1 < starts.Count ? starts[line + 1] - 1 : text.Length;
                return column <= end - starts[line] ? starts[line] + column : -1;
            }
            void Visit(JArray nodes, int depth)
            {
                if (depth > 32 || budget <= 0 || result.Count >= 32) return;
                foreach (var node in nodes)
                {
                    if (--budget < 0 || result.Count >= 32) return;
                    var start = Offset(node["range"]?["start"]);
                    var end = Offset(node["range"]?["end"]);
                    if (start < 0 || end <= start || frameOffset < start || frameOffset >= end) continue;
                    if (!(node["children"] is JArray children)) continue;
                    var kind = (int?)node["kind"] ?? 0;
                    if (kind == 5 || kind == 10 || kind == 11 || kind == 19 || kind == 23)
                    {
                        foreach (var child in children.Take(2048))
                        {
                            var childKind = (int?)child["kind"] ?? 0;
                            if (childKind != 6 && childKind != 12) continue;
                            var name = (string?)child["name"] ?? "";
                            var insertion = KotlinCompletionQuery.Insertion(name);
                            if (insertion == null || name.Length > 256 || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                            var declaration = Offset(child["range"]?["start"]);
                            var declarationEnd = Offset(child["range"]?["end"]);
                            var selection = Offset(child["selectionRange"]?["start"]);
                            var selectionEnd = Offset(child["selectionRange"]?["end"]);
                            if (declaration < start || declarationEnd > end || declarationEnd < selectionEnd ||
                                selection < declaration || selectionEnd <= selection) continue;
                            var spelling = text.Substring(selection, selectionEnd - selection);
                            if (spelling != name && spelling != insertion) continue;
                            if (LastCodeCharacter(text, declaration, selection) != '.') continue;
                            if (names.Add(name)) result.Add(name);
                            if (result.Count >= 32) return;
                        }
                    }
                    Visit(children, depth + 1);
                }
            }
            if (symbols is JArray roots) Visit(roots, 0);
            return result;
        }

        private static char LastCodeCharacter(string text, int start, int end)
        {
            char last = '\0';
            var comments = 0;
            for (var i = start; i < end; i++)
            {
                var c = text[i];
                var next = i + 1 < end ? text[i + 1] : '\0';
                if (c == '/' && next == '*') { comments++; i++; continue; }
                if (comments != 0)
                {
                    if (c == '*' && next == '/') { comments--; i++; }
                    continue;
                }
                if (c == '/' && next == '/') { while (i < end && text[i] != '\n') i++; continue; }
                // Annotations may contain strings; their contents are not syntax.
                if (c == '"' || c == '\'' || c == '`')
                {
                    var quote = c;
                    var raw = c == '"' && i + 2 < end && text[i + 1] == '"' && text[i + 2] == '"';
                    if (raw) i += 2;
                    while (++i < end)
                    {
                        if (!raw && quote != '`' && text[i] == '\\') { i++; continue; }
                        if (text[i] != quote) continue;
                        if (!raw) break;
                        if (i + 2 < end && text[i + 1] == '"' && text[i + 2] == '"') { i += 2; break; }
                    }
                    last = quote;
                }
                else if (!char.IsWhiteSpace(c)) last = c;
            }
            return comments == 0 ? last : '\0';
        }

        internal static IReadOnlyList<KotlinCallable> Decode(string name, JToken response)
        {
            var insertion = KotlinCompletionQuery.Insertion(name);
            var result = new List<KotlinCallable>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (insertion == null || !(response["signatures"] is JArray signatures)) return result;
            foreach (var signature in signatures.Take(64))
            {
                var label = (string?)signature["label"] ?? "";
                if (label.Length > 4096 || label.IndexOfAny(new[] { '\r', '\n' }) >= 0) continue;
                var position = label.IndexOf(insertion + "(", StringComparison.Ordinal);
                if (position < 0 || (position != 0 && label[position - 1] != '.')) continue;
                if (seen.Add(label)) result.Add(new KotlinCallable(name, insertion, name + " — " + label));
            }
            return result;
        }
    }
}
