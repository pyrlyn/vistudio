using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Vistudio.Core.Mise
{
    /// <summary>One tool entry in a mise config file. Offsets are into the scanned text.</summary>
    public sealed class MiseToolEntry
    {
        public MiseToolEntry(string name, int line, int start, int end)
        { Name = name; Line = line; Start = start; End = end; }
        /// <summary>Tool name as written, without quotes (<c>node</c>, <c>npm:prettier</c>, <c>aqua:BurntSushi/ripgrep</c>).</summary>
        public string Name { get; }
        /// <summary>0-based line.</summary>
        public int Line { get; }
        /// <summary>Offset of the name's first character (inside quotes).</summary>
        public int Start { get; }
        public int End { get; }
    }

    /// <summary>The tool lines of a mise config, found by a line scanner (mise itself reports versions; this only places them).</summary>
    public sealed class MiseToolLayout
    {
        public MiseToolLayout(IReadOnlyList<MiseToolEntry> tools, int? headerLine, int headerStart, int headerEnd)
        { Tools = tools; HeaderLine = headerLine; HeaderStart = headerStart; HeaderEnd = headerEnd; }
        public IReadOnlyList<MiseToolEntry> Tools { get; }
        /// <summary>0-based line of the <c>[tools]</c> header (else the first tool line); null without tools.</summary>
        public int? HeaderLine { get; }
        public int HeaderStart { get; }
        public int HeaderEnd { get; }
    }

    public static class MiseToolLocator
    {
        // [table] but not [[array of tables]]; trailing comment allowed.
        private static readonly Regex Header = new Regex(@"^\s*\[(?!\[)\s*(?<key>[^\]]+?)\s*\]\s*(#.*)?$", RegexOptions.CultureInvariant);
        private static readonly Regex KeyLine = new Regex(@"^\s*(?<key>""(?:[^""\\]|\\.)*""|'[^']*'|[A-Za-z0-9_\-]+(?:\s*\.\s*(?:""(?:[^""\\]|\\.)*""|'[^']*'|[A-Za-z0-9_\-]+))*)\s*=", RegexOptions.CultureInvariant);
        private static readonly Regex KeyPart = new Regex(@"""(?:[^""\\]|\\.)*""|'[^']*'|[A-Za-z0-9_\-]+", RegexOptions.CultureInvariant);

        public static MiseToolLayout Find(string text, bool toolVersions) => toolVersions ? FindToolVersions(text) : FindToml(text);

        private static MiseToolLayout FindToolVersions(string text)
        {
            var tools = new List<MiseToolEntry>();
            int offset = 0, line = 0;
            foreach (var raw in Lines(text))
            {
                var index = 0;
                while (index < raw.Length && char.IsWhiteSpace(raw[index])) index++;
                if (index < raw.Length && raw[index] != '#')
                {
                    var end = index;
                    while (end < raw.Length && !char.IsWhiteSpace(raw[end])) end++;
                    tools.Add(new MiseToolEntry(raw.Substring(index, end - index), line, offset + index, offset + end));
                }
                offset += raw.Length + LineBreakLength(text, offset + raw.Length);
                line++;
            }
            return tools.Count == 0 ? new MiseToolLayout(tools, null, 0, 0) : new MiseToolLayout(tools, tools[0].Line, tools[0].Start, tools[0].End);
        }

        private static MiseToolLayout FindToml(string text)
        {
            var tools = new List<MiseToolEntry>();
            int? headerLine = null;
            int headerStart = 0, headerEnd = 0;
            var inTools = false;
            var topLevel = true;
            int offset = 0, line = 0;
            foreach (var raw in Lines(text))
            {
                var header = Header.Match(raw);
                if (header.Success)
                {
                    topLevel = false;
                    var parts = Parts(header.Groups["key"]);
                    inTools = parts.Count == 1 && parts[0].Name == "tools";
                    if (parts.Count >= 1 && parts[0].Name == "tools" && headerLine == null)
                    {
                        headerLine = line;
                        headerStart = offset + header.Groups["key"].Index - 1;
                        headerEnd = offset + header.Groups["key"].Index + header.Groups["key"].Length + 1;
                    }
                    // [tools.node] / [tools."npm:prettier"]: the table is the tool entry.
                    if (parts.Count == 2 && parts[0].Name == "tools")
                        tools.Add(new MiseToolEntry(parts[1].Name, line, offset + parts[1].Start, offset + parts[1].End));
                }
                else if (inTools || topLevel)
                {
                    var key = KeyLine.Match(raw);
                    if (key.Success)
                    {
                        var parts = Parts(key.Groups["key"]);
                        if (inTools && parts.Count >= 1)
                            tools.Add(new MiseToolEntry(parts[0].Name, line, offset + parts[0].Start, offset + parts[0].End));
                        else if (topLevel && parts.Count >= 2 && parts[0].Name == "tools")
                        {
                            // Dotted keys before any table: tools.node = "22"
                            tools.Add(new MiseToolEntry(parts[1].Name, line, offset + parts[1].Start, offset + parts[1].End));
                            if (headerLine == null) { headerLine = line; headerStart = offset + parts[0].Start; headerEnd = offset + parts[0].End; }
                        }
                    }
                }
                offset += raw.Length + LineBreakLength(text, offset + raw.Length);
                line++;
            }
            if (headerLine == null && tools.Count > 0) { headerLine = tools[0].Line; headerStart = tools[0].Start; headerEnd = tools[0].End; }
            return new MiseToolLayout(tools, headerLine, headerStart, headerEnd);
        }

        private readonly struct Part
        {
            public Part(string name, int start, int end) { Name = name; Start = start; End = end; }
            public string Name { get; }
            public int Start { get; }
            public int End { get; }
        }

        /// <summary>Dotted key parts with their offsets in the line (quotes removed, offsets inside them).</summary>
        private static List<Part> Parts(Group key)
        {
            var result = new List<Part>();
            foreach (Match part in KeyPart.Matches(key.Value))
            {
                var value = part.Value;
                var quoted = value.Length >= 2 && (value[0] == '"' || value[0] == '\'');
                var name = quoted ? value.Substring(1, value.Length - 2) : value;
                if (quoted && value[0] == '"') name = Regex.Unescape(name);
                var start = key.Index + part.Index + (quoted ? 1 : 0);
                result.Add(new Part(name, start, start + (quoted ? value.Length - 2 : value.Length)));
            }
            return result;
        }

        private static IEnumerable<string> Lines(string text)
        {
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\r' && text[i] != '\n') continue;
                yield return text.Substring(start, i - start);
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
            yield return text.Substring(start);
        }

        private static int LineBreakLength(string text, int at) =>
            at >= text.Length ? 0 : text[at] == '\r' && at + 1 < text.Length && text[at + 1] == '\n' ? 2 : 1;

        /// <summary>Matches a written tool name to the name mise reports (<c>nodejs</c> is <c>node</c>, a backend prefix may be added or dropped).</summary>
        public static bool SameTool(string written, string reported)
        {
            if (string.Equals(written, reported, StringComparison.OrdinalIgnoreCase)) return true;
            string Strip(string name) { var colon = name.IndexOf(':'); return colon >= 0 && name.IndexOf('/') < 0 ? name.Substring(colon + 1) : name; }
            string Alias(string name) => name.Equals("nodejs", StringComparison.OrdinalIgnoreCase) ? "node" : name.Equals("golang", StringComparison.OrdinalIgnoreCase) ? "go" : name;
            return string.Equals(Alias(Strip(written)), Alias(Strip(reported)), StringComparison.OrdinalIgnoreCase);
        }
    }
}
