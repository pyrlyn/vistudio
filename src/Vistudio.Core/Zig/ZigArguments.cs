using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Vistudio.Core.Zig
{
    /// <summary>
    /// Command-line handling for zig.exe. Processes are started directly (never through cmd.exe), so the only
    /// job is to split user flags the way a shell would and to quote each argument for CommandLineToArgvW.
    /// </summary>
    public static class ZigArguments
    {
        /// <summary>Splits flags on whitespace; double quotes group an argument. Rejects control characters and unbalanced quotes.</summary>
        public static IReadOnlyList<string> Split(string? text)
        {
            var result = new List<string>();
            if (text == null || string.IsNullOrWhiteSpace(text)) return result;
            if (text.Any(c => c < ' ' && c != '\t')) throw new ArgumentException("Zig flags cannot contain line breaks or control characters.");
            var current = new StringBuilder();
            var quoted = false;
            var started = false;
            foreach (var c in text)
            {
                if (c == '"') { quoted = !quoted; started = true; continue; }
                if (!quoted && (c == ' ' || c == '\t'))
                {
                    if (started) { result.Add(current.ToString()); current.Clear(); started = false; }
                    continue;
                }
                current.Append(c);
                started = true;
            }
            if (quoted) throw new ArgumentException("Zig flags have an unbalanced double quote.");
            if (started) result.Add(current.ToString());
            return result;
        }

        /// <summary>A build step name from <c>zig build -l</c>: no spaces, quotes, options or control characters.</summary>
        public static string ValidateStep(string step)
        {
            if (string.IsNullOrWhiteSpace(step) || step.StartsWith("-", StringComparison.Ordinal) || step.Any(c => char.IsWhiteSpace(c) || c == '"' || c < ' '))
                throw new ArgumentException("Invalid zig build step: '" + step + "'.");
            return step;
        }

        /// <summary>Quotes one argument for CommandLineToArgvW (the rules the zig.exe runtime parses with).</summary>
        public static string Quote(string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return argument;
            var builder = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var c in argument)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { builder.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
                builder.Append('\\', backslashes).Append(c);
                backslashes = 0;
            }
            return builder.Append('\\', backslashes * 2).Append('"').ToString();
        }

        public static string Join(IEnumerable<string> arguments) => string.Join(" ", arguments.Select(Quote));
    }
}
