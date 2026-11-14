using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Vistudio.Core
{
    public static class GradleDiagnosticParser
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);
        private static readonly Regex Ansi = new Regex(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant, Timeout);
        private static readonly Regex Prefix = new Regex(@"^\s*(?<severity>[ew]):\s+(?<body>.+)$", RegexOptions.CultureInvariant, Timeout);
        // Kotlin 2.x: file:///C:/src/Main.kt:8:5 Message (optional colon after column).
        private static readonly Regex Modern = new Regex(@"^(?<file>.+?\.kts?):(?<line>\d+):(?<column>\d+):?\s+(?<message>.+)$", RegexOptions.CultureInvariant, Timeout);
        // Kotlin 1.x: C:\src\Main.kt: (8, 5): Message.
        private static readonly Regex Legacy = new Regex(@"^(?<file>.+?\.kts?):\s*\((?<line>\d+),\s*(?<column>\d+)\):?\s+(?<message>.+)$", RegexOptions.CultureInvariant, Timeout);

        public static bool TryParse(string output, string root, out BuildDiagnostic? diagnostic)
        {
            diagnostic = null;
            if (output == null || output.Length > 65536) return false;
            try
            {
                var prefix = Prefix.Match(Ansi.Replace(output, ""));
                if (!prefix.Success) return false;
                var body = prefix.Groups["body"].Value;
                var match = Modern.Match(body);
                if (!match.Success) match = Legacy.Match(body);
                // Non-located Gradle failures are reported from the exit status instead.
                if (!match.Success) return false;
                if (!int.TryParse(match.Groups["line"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var line) || line < 1 ||
                    !int.TryParse(match.Groups["column"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var column) || column < 1)
                    return false;
                var file = match.Groups["file"].Value.Trim();
                if (file.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Uri.TryCreate(file, UriKind.Absolute, out var uri) || !uri.IsFile || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
                    file = uri.LocalPath;
                }
                else if (file.Contains("://")) return false;
                // Drive-relative paths depend on hidden process state; never guess their base.
                if (file.Length >= 2 && file[1] == ':' && (file.Length < 3 || (file[2] != '\\' && file[2] != '/'))) return false;
                file = Path.GetFullPath(Path.Combine(root, file));
                diagnostic = new BuildDiagnostic(file, line - 1, column - 1, match.Groups["message"].Value.Trim(),
                    prefix.Groups["severity"].Value == "e" ? BuildDiagnosticSeverity.Error : BuildDiagnosticSeverity.Warning);
                return true;
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is IOException || e is RegexMatchTimeoutException)
            {
                return false; // Unknown output remains available in the Output pane.
            }
        }
    }
}
