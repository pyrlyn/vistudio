using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Vistudio
{
    // Parse only a name/member chain, never an expression to execute in the target.
    internal sealed class KotlinCompletionQuery
    {
        private const string Identifier = @"(?:[\p{L}_][\p{L}\p{Nd}_]*|`[^`\r\n]+`)";
        private static readonly Regex Tail = new Regex(@"(?<receiver>(?:" + Identifier + @"\?*\.)*)(?<prefix>[\p{L}\p{Nd}_]*|`[^`\r\n]*)$", RegexOptions.CultureInvariant);
        private static readonly Regex Plain = new Regex(@"^[\p{L}_][\p{L}\p{Nd}_]*$", RegexOptions.CultureInvariant);
        internal string[] Receiver { get; private set; } = Array.Empty<string>();
        internal int Start { get; private set; }
        internal int ChainStart { get; private set; }
        internal int Length { get; private set; }

        internal static KotlinCompletionQuery? Parse(string text, int caret)
        {
            if (text.Length > 2048 || caret < 0 || caret > text.Length || text.TrimStart().StartsWith(">", StringComparison.Ordinal)) return null;
            var before = text.Substring(0, caret);
            // Strings/comments are not completion expressions. Backtick names are allowed.
            bool quotedName = false;
            for (var i = 0; i < before.Length; i++)
            {
                var c = before[i];
                if (c == '`') quotedName = !quotedName;
                if (!quotedName && (c == '"' || c == '\'' || (c == '/' && i + 1 < before.Length && (before[i + 1] == '/' || before[i + 1] == '*')))) return null;
            }
            var match = Tail.Match(before);
            if (!match.Success) return null;
            if (match.Index > 0 && (before[match.Index - 1] == '.' || before[match.Index - 1] == '?' || before[match.Index - 1] == '`' || char.IsLetterOrDigit(before[match.Index - 1]))) return null;
            var receiver = match.Groups["receiver"].Value;
            if (receiver.Contains("??")) return null;
            var names = receiver.Length == 0 ? Array.Empty<string>() : Regex.Matches(receiver, Identifier).Cast<Match>().Select(m => m.Value.Trim('`')).ToArray();
            if (names.Length > 8) return null;
            var start = match.Groups["prefix"].Index;
            var end = caret;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
            if (start < text.Length && text[start] == '`' && end < text.Length && text[end] == '`') end++;
            return new KotlinCompletionQuery
            {
                Receiver = names,
                Start = start, Length = end - start, ChainStart = match.Index
            };
        }

        internal static string? Insertion(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOfAny(new[] { '`', '$', '\r', '\n', '[', ']' }) >= 0) return null;
            const string keywords = " as break class continue do else false for fun if in interface is null object package return super this throw true try typealias typeof val var when while ";
            return Plain.IsMatch(name) && (!keywords.Contains(" " + name + " ") || name == "this" || name == "super") ? name : "`" + name + "`";
        }

        private static readonly Regex JavaName = new Regex(@"^[\p{L}_$][\p{L}\p{Nd}_$]*$", RegexOptions.CultureInvariant);

        /// <summary>Java frames: identifiers are inserted as-is; names Java cannot spell (no backticks) are skipped.</summary>
        internal static string? JavaInsertion(string name)
        {
            const string keywords = " abstract assert boolean break byte case catch char class const continue default do double else enum extends final finally float for goto if implements import instanceof int interface long native new package private protected public return short static strictfp super switch synchronized this throw throws transient try void volatile while true false null ";
            if (string.IsNullOrEmpty(name) || !JavaName.IsMatch(name)) return null;
            return !keywords.Contains(" " + name + " ") || name == "this" || name == "super" ? name : null;
        }
    }
}
