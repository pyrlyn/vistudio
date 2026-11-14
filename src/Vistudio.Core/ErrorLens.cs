using System;
using System.Collections.Generic;
using System.Linq;

namespace Vistudio.Core
{
    public enum ErrorLensSeverity { None, Info, Warning, Error }

    public readonly struct ErrorLensEntry
    {
        public ErrorLensEntry(ErrorLensSeverity severity, string message) { Severity = severity; Message = message; }
        public ErrorLensSeverity Severity { get; }
        public string Message { get; }
    }

    // Pure presentation rules for end-of-line diagnostics; the editor layer only draws the result.
    public static class ErrorLens
    {
        // Maps editor error type names (PredefinedErrorTypeNames, Roslyn, LSP client) to a severity.
        // Hinted suggestions (faded "unnecessary code") and spelling are not shown, like Error Lens hints.
        public static ErrorLensSeverity Classify(string? errorType)
        {
            if (string.IsNullOrWhiteSpace(errorType)) return ErrorLensSeverity.Error;
            var type = errorType!.ToLowerInvariant();
            if (type.Contains("spell") || type.Contains("hinted")) return ErrorLensSeverity.None;
            if (type.Contains("warning")) return ErrorLensSeverity.Warning;
            if (type.Contains("suggestion") || type.Contains("info") || type.Contains("message") || type.Contains("hint")) return ErrorLensSeverity.Info;
            return ErrorLensSeverity.Error;
        }

        // One display line: the most severe message first, then a count of the rest.
        public static string? Summary(IEnumerable<ErrorLensEntry> entries, out ErrorLensSeverity severity, out string tooltip)
        {
            var ordered = Ordered(entries);
            severity = ordered.Count == 0 ? ErrorLensSeverity.None : ordered[0].Severity;
            tooltip = string.Join(Environment.NewLine, ordered.Select(x => x.Message));
            if (ordered.Count == 0) return null;
            var first = Flatten(ordered[0].Message);
            return ordered.Count == 1 ? first : first + "  (+" + (ordered.Count - 1) + ")";
        }

        public static List<ErrorLensEntry> Ordered(IEnumerable<ErrorLensEntry> entries)
        {
            var result = new List<ErrorLensEntry>();
            foreach (var entry in entries)
            {
                if (entry.Severity == ErrorLensSeverity.None || string.IsNullOrWhiteSpace(entry.Message)) continue;
                var message = entry.Message.Trim();
                if (result.Any(x => x.Message == message)) continue;
                result.Add(new ErrorLensEntry(entry.Severity, message));
            }
            // Stable: equal severities keep their source order.
            return result.Select((x, i) => (x, i)).OrderByDescending(p => p.x.Severity).ThenBy(p => p.i).Select(p => p.x).ToList();
        }

        public static string Flatten(string message)
        {
            var parts = message.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0);
            return string.Join(" ", parts);
        }
    }
}
