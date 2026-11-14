using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Vistudio.Core.Preview
{
    /// <summary>One <c>@Preview</c> annotation on a Kotlin function, as written in the source.</summary>
    public sealed class ComposePreview
    {
        internal ComposePreview(string id, string function, string displayName, int line, int widthDp, int heightDp,
            bool showBackground, uint backgroundColor, string? unsupported)
        {
            Id = id; Function = function; DisplayName = displayName; Line = line; WidthDp = widthDp; HeightDp = heightDp;
            ShowBackground = showBackground; BackgroundColor = backgroundColor; Unsupported = unsupported;
        }

        /// <summary>Stable within one scan (p0, p1, ...); also the PNG file name.</summary>
        public string Id { get; }
        public string Function { get; }
        /// <summary>The annotation's <c>name</c>, or the function name.</summary>
        public string DisplayName { get; }
        /// <summary>One-based line of the function name.</summary>
        public int Line { get; }
        /// <summary>0 when not set: the renderer wraps the content.</summary>
        public int WidthDp { get; }
        public int HeightDp { get; }
        public bool ShowBackground { get; }
        /// <summary>ARGB; 0 when not set (white is used with <see cref="ShowBackground"/>).</summary>
        public uint BackgroundColor { get; }
        /// <summary>Why the preview cannot be rendered (nested, parameters, receiver), or null.</summary>
        public string? Unsupported { get; }
    }

    /// <summary>The previews of one Kotlin file and the JVM class that holds its top-level functions.</summary>
    public sealed class ComposePreviewFile
    {
        internal ComposePreviewFile(string path, string? package, string facadeClass, IReadOnlyList<ComposePreview> previews)
        { Path = path; Package = package; FacadeClass = facadeClass; Previews = previews; }

        public string Path { get; }
        public string? Package { get; }
        public string FacadeClass { get; }
        public string JvmClass => string.IsNullOrEmpty(Package) ? FacadeClass : Package + "." + FacadeClass;
        public IReadOnlyList<ComposePreview> Previews { get; }
        public IEnumerable<ComposePreview> Renderable => Previews.Where(p => p.Unsupported == null);

        /// <summary>The renderer request: id, JVM class, method, widthDp, heightDp, showBackground, background ARGB (hex).</summary>
        public string CreateRequest()
        {
            var text = new StringBuilder();
            foreach (var preview in Renderable)
                text.Append(preview.Id).Append('\t').Append(JvmClass).Append('\t').Append(preview.Function).Append('\t')
                    .Append(preview.WidthDp.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(preview.HeightDp.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(preview.ShowBackground ? "true" : "false").Append('\t')
                    .Append(preview.BackgroundColor.ToString("X8", CultureInfo.InvariantCulture)).Append('\n');
            return text.ToString();
        }
    }

    /// <summary>
    /// Finds Compose <c>@Preview</c> functions lexically: comments and string literals are ignored, only top-level
    /// parameterless functions are renderable (the JVM facade class holds them as static methods).
    /// </summary>
    public static class ComposePreviewScanner
    {
        private static readonly Regex PreviewAnnotation = new Regex(@"\G@(?:[A-Za-z_][A-Za-z0-9_]*\.)*Preview(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);
        private static readonly Regex OtherAnnotation = new Regex(@"\G@(?:[A-Za-z_][A-Za-z0-9_]*\s*:\s*)?[A-Za-z_][A-Za-z0-9_.]*", RegexOptions.CultureInvariant);
        private static readonly Regex Modifier = new Regex(
            @"\G(?:private|internal|public|protected|inline|suspend|override|open|final|actual|expect|tailrec|external)(?![A-Za-z0-9_])",
            RegexOptions.CultureInvariant);
        private static readonly Regex Function = new Regex(@"\Gfun(?![A-Za-z0-9_])\s*(?:<[^>{}()]*>\s*)?((?:`[^`\r\n]+`|[A-Za-z_][A-Za-z0-9_]*)(?:\s*\.\s*(?:`[^`\r\n]+`|[A-Za-z_][A-Za-z0-9_]*))*)\s*\(",
            RegexOptions.CultureInvariant);
        private static readonly Regex Package = new Regex(@"(?m)^\s*package\s+([A-Za-z_`][A-Za-z0-9_.`]*)", RegexOptions.CultureInvariant);
        private static readonly Regex JvmName = new Regex(@"@file\s*:\s*(?:kotlin\.jvm\.)?JvmName\s*\(\s*""([^""\\]+)""\s*\)", RegexOptions.CultureInvariant);

        /// <summary>Quick check before scanning, without lexing.</summary>
        public static bool MayContainPreviews(string text) => text.IndexOf("Preview", StringComparison.Ordinal) >= 0;

        public static ComposePreviewFile Scan(string path, string text)
        {
            var code = MaskCommentsAndStrings(text);
            var package = Package.Match(code) is { Success: true } p ? p.Groups[1].Value.Replace("`", "") : null;
            var jvmName = JvmName.Match(text) is { Success: true } j && JvmName.Match(code).Success ? j.Groups[1].Value : null;
            var previews = new List<ComposePreview>();
            var depth = 0;
            for (var i = 0; i < code.Length; i++)
            {
                var ch = code[i];
                if (ch == '{') depth++;
                else if (ch == '}') depth = Math.Max(0, depth - 1);
                else if (ch == '@' && (i == 0 || !IsIdentifier(code[i - 1])) && PreviewAnnotation.Match(code, i) is { Success: true } annotation)
                {
                    // Collect every @Preview on the same declaration, then the declaration itself.
                    var annotations = new List<Dictionary<string, string>>();
                    var position = i;
                    var declarationDepth = depth;
                    while (true)
                    {
                        position = SkipSpace(code, position);
                        var preview = PreviewAnnotation.Match(code, position);
                        Match? other = preview.Success ? null : OtherAnnotation.Match(code, position);
                        Match? modifier = preview.Success || other!.Success ? null : Modifier.Match(code, position);
                        if (preview.Success)
                        {
                            position = preview.Index + preview.Length;
                            annotations.Add(ParseArguments(code, text, ref position));
                        }
                        else if (other!.Success)
                        {
                            position = other.Index + other.Length;
                            ParseArguments(code, text, ref position);
                        }
                        else if (modifier!.Success) position = modifier.Index + modifier.Length;
                        else break;
                    }
                    var function = Function.Match(code, position);
                    if (function.Success)
                    {
                        var qualified = function.Groups[1].Value;
                        var receiver = qualified.Replace("`", "").Contains(".");
                        var name = receiver ? qualified.Substring(qualified.LastIndexOf('.') + 1).Trim() : qualified;
                        name = name.Trim('`');
                        var open = function.Index + function.Length - 1;
                        var close = MatchingParenthesis(code, open);
                        var hasParameters = close > open && code.Substring(open + 1, close - open - 1).Trim().Length != 0;
                        var line = LineOf(text, function.Groups[1].Index);
                        var unsupported = declarationDepth > 0 ? "Only top-level preview functions can be rendered."
                            : receiver ? "Preview functions with a receiver cannot be rendered."
                            : hasParameters ? "Preview functions with parameters (including @PreviewParameter) cannot be rendered."
                            : null;
                        foreach (var arguments in annotations)
                        {
                            var displayName = arguments.TryGetValue("name", out var label) && label.Length != 0 ? label : name;
                            if (annotations.Count > 1 && displayName == name) displayName = name + " #" + (annotations.IndexOf(arguments) + 1);
                            previews.Add(new ComposePreview("p" + previews.Count.ToString(CultureInfo.InvariantCulture), name, displayName, line,
                                Int(arguments, "widthDp"), Int(arguments, "heightDp"),
                                arguments.TryGetValue("showBackground", out var background) && background == "true",
                                Color(arguments), unsupported));
                        }
                    }
                    i = Math.Max(i, position - 1);
                    // Braces skipped by the annotation parser are balanced, so depth stays correct.
                }
            }
            return new ComposePreviewFile(path, package, jvmName ?? FacadeName(System.IO.Path.GetFileName(path)), previews);
        }

        /// <summary>Kotlin's file facade name: Cards.kt → CardsKt, App.desktop.kt → App_desktopKt.</summary>
        public static string FacadeName(string fileName)
        {
            var name = fileName.EndsWith(".kt", StringComparison.OrdinalIgnoreCase) ? fileName.Substring(0, fileName.Length - 3) : fileName;
            if (name.Length == 0) return "_Kt";
            var builder = new StringBuilder(name.Length + 3);
            if (!(char.IsLetter(name[0]) || name[0] == '_' || name[0] == '$')) builder.Append('_');
            foreach (var ch in name) builder.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '$' ? ch : '_');
            if (builder[0] >= 'a' && builder[0] <= 'z') builder[0] = char.ToUpperInvariant(builder[0]);
            return builder.Append("Kt").ToString();
        }

        private static int Int(Dictionary<string, string> arguments, string key) =>
            arguments.TryGetValue(key, out var value) && int.TryParse(value.Replace("_", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
                && result > 0 && result <= 4000 ? result : 0;

        private static uint Color(Dictionary<string, string> arguments)
        {
            if (!arguments.TryGetValue("backgroundColor", out var value)) return 0;
            value = value.Replace("_", "").TrimEnd('L', 'l', 'u', 'U');
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && ulong.TryParse(value.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex)) return unchecked((uint)hex);
            return ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? unchecked((uint)number) : 0;
        }

        /// <summary>Parses <c>(a = 1, "name", ...)</c> after an annotation; positional first argument is the name.</summary>
        private static Dictionary<string, string> ParseArguments(string code, string text, ref int position)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var open = SkipSpace(code, position);
            if (open >= code.Length || code[open] != '(') return result;
            var close = MatchingParenthesis(code, open);
            if (close < 0) { position = code.Length; return result; }
            position = close + 1;
            var start = open + 1;
            var index = 0;
            for (var i = start; i <= close; i++)
            {
                if (i < close && code[i] != ',') { if (code[i] == '(' || code[i] == '[' || code[i] == '{') i = Math.Max(i, Matching(code, i)); continue; }
                var argument = text.Substring(start, i - start);
                var masked = code.Substring(start, i - start);
                var equals = masked.IndexOf('=');
                if (equals > 0 && Regex.IsMatch(masked.Substring(0, equals).Trim(), @"\A[A-Za-z_][A-Za-z0-9_]*\z"))
                    result[masked.Substring(0, equals).Trim()] = Value(argument.Substring(equals + 1));
                else if (index == 0 && masked.Trim().Length != 0) result["name"] = Value(argument);
                index++;
                start = i + 1;
            }
            return result;
        }

        private static string Value(string raw)
        {
            raw = raw.Trim();
            if (raw.Length >= 2 && raw[0] == '"' && raw[raw.Length - 1] == '"')
            {
                var body = raw.StartsWith("\"\"\"", StringComparison.Ordinal) && raw.Length >= 6 ? raw.Substring(3, raw.Length - 6) : raw.Substring(1, raw.Length - 2);
                return Regex.Replace(body, @"\\(.)", m => m.Groups[1].Value switch { "n" => " ", "t" => " ", var other => other });
            }
            return raw;
        }

        private static int MatchingParenthesis(string code, int open) => open < code.Length && code[open] == '(' ? Matching(code, open) : -1;

        private static int Matching(string code, int open)
        {
            var close = code[open] == '(' ? ')' : code[open] == '[' ? ']' : '}';
            var depth = 0;
            for (var i = open; i < code.Length; i++)
            {
                if (code[i] == code[open]) depth++;
                else if (code[i] == close && --depth == 0) return i;
            }
            return -1;
        }

        private static int SkipSpace(string code, int position)
        {
            while (position < code.Length && char.IsWhiteSpace(code[position])) position++;
            return position;
        }

        private static bool IsIdentifier(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

        private static int LineOf(string text, int index)
        {
            var line = 1;
            for (var i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') line++;
            return line;
        }

        /// <summary>
        /// Replaces comment and string-literal content with spaces (quotes and line breaks are kept), so that structure
        /// can be read from the result while values are read from the original text at the same offsets.
        /// Template expressions inside strings are masked too; their braces are balanced and do not change the depth.
        /// </summary>
        public static string MaskCommentsAndStrings(string text)
        {
            var masked = text.ToCharArray();
            void Blank(int from, int to) { for (var k = from; k < to && k < masked.Length; k++) if (masked[k] != '\n' && masked[k] != '\r') masked[k] = ' '; }
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                var next = i + 1 < text.Length ? text[i + 1] : '\0';
                if (ch == '/' && next == '/')
                {
                    var end = text.IndexOf('\n', i);
                    if (end < 0) end = text.Length;
                    Blank(i, end); i = end - 1;
                }
                else if (ch == '/' && next == '*')
                {
                    var depth = 0;
                    var j = i;
                    for (; j < text.Length; j++)
                    {
                        if (text[j] == '/' && j + 1 < text.Length && text[j + 1] == '*') { depth++; j++; }
                        else if (text[j] == '*' && j + 1 < text.Length && text[j + 1] == '/') { j++; if (--depth == 0) break; }
                    }
                    Blank(i, Math.Min(j + 1, text.Length)); i = j;
                }
                else if (ch == '"' || ch == '\'')
                {
                    var end = SkipLiteral(text, i);
                    var quote = ch == '"' && i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"' ? 3 : 1;
                    Blank(i + quote, Math.Max(i + quote, end - quote));
                    i = end - 1;
                }
                else if (ch == '`')
                {
                    var end = text.IndexOf('`', i + 1);
                    i = end < 0 ? text.Length : end;
                }
            }
            return new string(masked);
        }

        /// <summary>Returns the index just after the literal starting at <paramref name="start"/>.</summary>
        private static int SkipLiteral(string text, int start)
        {
            if (text[start] == '\'')
            {
                for (var i = start + 1; i < text.Length; i++)
                {
                    if (text[i] == '\\') { i++; continue; }
                    if (text[i] == '\'' || text[i] == '\n') return i + 1;
                }
                return text.Length;
            }
            var raw = start + 2 < text.Length && text[start + 1] == '"' && text[start + 2] == '"';
            for (var i = start + (raw ? 3 : 1); i < text.Length; i++)
            {
                var ch = text[i];
                if (!raw && ch == '\\') { i++; continue; }
                if (!raw && ch == '\n') return i;
                if (ch == '$' && i + 1 < text.Length && text[i + 1] == '{')
                {
                    // Template expression: skip balanced code, which may itself contain string literals.
                    var depth = 0;
                    var j = i + 1;
                    for (; j < text.Length; j++)
                    {
                        if (text[j] == '"' || text[j] == '\'') { j = SkipLiteral(text, j) - 1; continue; }
                        if (text[j] == '{') depth++;
                        else if (text[j] == '}' && --depth == 0) break;
                    }
                    i = j;
                    continue;
                }
                if (ch == '"')
                {
                    if (!raw) return i + 1;
                    if (i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"')
                    {
                        var end = i + 3;
                        while (end < text.Length && text[end] == '"') end++;
                        return end;
                    }
                }
            }
            return text.Length;
        }
    }

    /// <summary>Result of one preview in the renderer's results.tsv.</summary>
    public sealed class ComposePreviewResult
    {
        internal ComposePreviewResult(string id, string? file, int width, int height, double scale, string? error)
        { Id = id; File = file; Width = width; Height = height; Scale = scale; Error = error; }
        public string Id { get; }
        /// <summary>PNG file name inside the output directory; null on failure.</summary>
        public string? File { get; }
        public int Width { get; }
        public int Height { get; }
        /// <summary>Pixels per dp; the image is shown at Width / Scale device-independent units.</summary>
        public double Scale { get; }
        public string? Error { get; }

        public static IReadOnlyDictionary<string, ComposePreviewResult> ParseManifest(string text)
        {
            var results = new Dictionary<string, ComposePreviewResult>(StringComparer.Ordinal);
            foreach (var line in text.Split('\n'))
            {
                var field = line.TrimEnd('\r').Split('\t');
                if (field.Length < 3 || !Regex.IsMatch(field[0], @"\Ap[0-9]+\z")) continue;
                if (field[1] == "OK" && field.Length == 6 && Regex.IsMatch(field[2], @"\Ap[0-9]+\.png\z")
                    && int.TryParse(field[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
                    && int.TryParse(field[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
                    && double.TryParse(field[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) && scale > 0)
                    results[field[0]] = new ComposePreviewResult(field[0], field[2], width, height, scale, null);
                else if (field[1] == "ERROR")
                    results[field[0]] = new ComposePreviewResult(field[0], null, 0, 0, 1, Unescape(string.Join("\t", field.Skip(2))));
            }
            return results;
        }

        /// <summary>The message of a <c>VISTUDIO_PREVIEW_ERROR</c> line printed by the Gradle script, or null.</summary>
        public static string? GradleError(string line) =>
            line.StartsWith("VISTUDIO_PREVIEW_ERROR\t", StringComparison.Ordinal) ? line.Substring("VISTUDIO_PREVIEW_ERROR\t".Length).Trim() : null;

        private static string Unescape(string value) =>
            Regex.Replace(value, @"\\([\\tn])", m => m.Groups[1].Value == "t" ? "\t" : m.Groups[1].Value == "n" ? "\n" : "\\");
    }
}
