using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Vistudio
{
    // Rename results are applied only to an analysis string. No workspace edit,
    // original document notification, or file operation is issued.
    internal static class KotlinCompletionIsolation
    {
        internal sealed class Owner
        {
            internal string Name = "", Alias = "";
            internal JObject Position = new JObject();
        }
        internal static IReadOnlyList<Owner> Owners(JToken symbols, string text, int line)
        {
            var owners = new List<Owner>();
            var budget = 4096;
            void Visit(JArray nodes, int depth)
            {
                if (depth > 32) return;
                foreach (var node in nodes)
                {
                    if (--budget < 0) return;
                    var start = (int?)node["range"]?["start"]?["line"] ?? -1;
                    var end = (int?)node["range"]?["end"]?["line"] ?? -1;
                    if (start < 0 || line < start || line > end) continue;
                    var kind = (int?)node["kind"] ?? 0;
                    if ((kind == 5 || kind == 10 || kind == 11 || kind == 19 || kind == 23) &&
                        node["selectionRange"]?["start"] is JObject position)
                    {
                        var name = (string?)node["name"] ?? "";
                        var first = Offset(text, position);
                        var last = Offset(text, node["selectionRange"]?["end"]);
                        var spelling = KotlinCompletionQuery.Insertion(name);
                        if (first < 0 || last <= first || spelling == null ||
                            (text.Substring(first, last - first) != spelling && text.Substring(first, last - first) != name)) continue;
                        owners.Add(new Owner { Name = name, Alias = "__VistudioOwner_" + Guid.NewGuid().ToString("N"), Position = position });
                    }
                    if (node["children"] is JArray children) Visit(children, depth + 1);
                }
            }
            if (symbols is JArray roots) Visit(roots, 0);
            return owners.Count <= 8 ? owners : Array.Empty<Owner>();
        }

        internal static int Offset(string text, JToken? position)
        {
            var line = (int?)position?["line"] ?? -1;
            var column = (int?)position?["character"] ?? -1;
            if (line < 0 || column < 0) return -1;
            var start = 0;
            for (var i = 0; i < line; i++)
            {
                var end = text.IndexOf('\n', start);
                if (end < 0) return -1;
                start = end + 1;
            }
            var newline = text.IndexOf('\n', start);
            if (newline < 0) newline = text.Length;
            return column <= newline - start ? start + column : -1;
        }

        internal static bool SameDocument(string? left, string right) =>
            System.Uri.TryCreate(left, UriKind.Absolute, out var a) && a.IsFile &&
            System.Uri.TryCreate(right, UriKind.Absolute, out var b) && b.IsFile &&
            string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(System.Uri.UnescapeDataString(a.AbsolutePath).TrimStart('/'),
                System.Uri.UnescapeDataString(b.AbsolutePath).TrimStart('/'), StringComparison.OrdinalIgnoreCase);

        internal static string? Apply(string text, string uri, int probeLine, IReadOnlyList<Tuple<Owner, JToken>> responses)
        {
            var edits = new List<Tuple<int, int, string>>();
            foreach (var response in responses)
            {
                var ownEdits = new List<JToken>();
                var result = response.Item2;
                if (result["changes"] is JObject changes)
                    foreach (var entry in changes.Properties())
                        if (SameDocument(entry.Name, uri) && entry.Value is JArray array) ownEdits.AddRange(array);
                if (result["documentChanges"] is JArray documents)
                    foreach (var document in documents)
                        if (SameDocument((string?)document["textDocument"]?["uri"], uri) && document["edits"] is JArray array) ownEdits.AddRange(array);
                if (ownEdits.Count == 0 || ownEdits.Count > 512) return null;
                var declaration = Offset(text, response.Item1.Position);
                var touchesDeclaration = false;
                foreach (var edit in ownEdits)
                {
                    var range = edit["range"];
                    var start = Offset(text, range?["start"]);
                    var end = Offset(text, range?["end"]);
                    var replacement = (string?)edit["newText"];
                    // The inserted chain and its position must remain unchanged.
                    if (start < 0 || end < start || replacement == null || replacement.IndexOfAny(new[] { '\r', '\n' }) >= 0 ||
                        (int?)range?["start"]?["line"] != (int?)range?["end"]?["line"] ||
                        (int?)range?["start"]?["line"] == probeLine) return null;
                    touchesDeclaration |= start <= declaration && end >= declaration;
                    edits.Add(Tuple.Create(start, end, replacement));
                    if (edits.Count > 512) return null;
                }
                if (!touchesDeclaration || text.Contains(response.Item1.Alias)) return null;
            }
            var ordered = edits.OrderByDescending(e => e.Item1).ToArray();
            var boundary = text.Length + 1;
            foreach (var edit in ordered)
            {
                if (edit.Item2 > boundary) return null;
                text = text.Remove(edit.Item1, edit.Item2 - edit.Item1).Insert(edit.Item1, edit.Item3);
                boundary = edit.Item1;
            }
            return responses.All(r => text.Contains(r.Item1.Alias)) ? text : null;
        }
    }
}
