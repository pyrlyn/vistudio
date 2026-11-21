using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Vistudio.Core.Languages
{
    /// <summary>
    /// Located errors and warnings in the output of a language project's build (Solution Build of a <c>.rustproj</c>,
    /// <c>.cproj</c>, <c>.cppproj</c>, <c>.javaproj</c>, <c>.wasmproj</c>, <c>.zigproj</c> or <c>.dartproj</c>), for the Error
    /// List: the counterpart of the Gradle, Zig and Dart parsers for the tools these projects run. Recognized formats:
    /// <list type="bullet">
    /// <item>MSVC / MSBuild (<c>cmake --build</c>, <c>flutter build windows</c>): <c>file(line[,col]): error C2065: message [project]</c>;</item>
    /// <item>GCC, Clang, javac (through Gradle), zig build and the Dart compiler: <c>file:line[:col]: error: message</c>;</item>
    /// <item>rustc / cargo and wasm-tools: an <c>error[E0308]: message</c> (or <c>N: message</c> under <c>Caused by:</c>) line,
    /// then <c>--&gt; file:line:col</c>;</item>
    /// <item><c>dart analyze</c>: <c>error - file:line:col - message - code</c>.</item>
    /// </list>
    /// Notes, infos and unlocated summaries ("could not compile") stay in the Build pane; the exit code still decides success.
    /// </summary>
    public static class ToolDiagnosticParser
    {
        private const int MaxLine = 4000;

        private static readonly Regex Msvc = new Regex(
            @"^\s*(?:\d+>)?(?<file>(?:[A-Za-z]:)?[^:()\r\n""<>|]+?)\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\)\s*:\s*(?<sev>fatal error|error|warning)\s*(?<code>[A-Za-z]+\d+)?\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?\s*$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex Gnu = new Regex(
            @"^\s*(?<file>(?:[A-Za-z]:[\\/])?[^:\r\n""<>|]+?):(?<line>\d+):(?:(?<col>\d+):)?\s*(?<sev>fatal error|error|warning)(?:\[[\w-]+\])?\s*:\s*(?<msg>.+?)\s*$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex Headline = new Regex(@"^(?<sev>error|warning)(?:\[(?<code>[\w-]+)\])?:\s*(?<msg>.+?)\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex CausedBy = new Regex(@"^\s*\d+:\s*(?<msg>.+?)\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex Arrow = new Regex(@"^\s*-->\s*(?<file>.+?):(?<line>\d+):(?<col>\d+)\s*$", RegexOptions.CultureInvariant);

        private static readonly Regex DartAnalyze = new Regex(
            @"^\s*(?<sev>error|warning)\s+-\s+(?<file>.+?):(?<line>\d+):(?<col>\d+)\s+-\s+(?<msg>.+?)(?:\s+-\s+(?<code>[a-z0-9_]+))?\s*$",
            RegexOptions.CultureInvariant);

        public static IReadOnlyList<BuildDiagnostic> Parse(IEnumerable<string> lines, string workingDirectory)
        {
            var result = new List<BuildDiagnostic>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            // rustc / wasm-tools: the message line comes before its location line.
            string? pendingMessage = null;
            var pendingSeverity = BuildDiagnosticSeverity.Error;
            var linesSincePending = 0;
            foreach (var raw in lines)
            {
                if (raw == null || raw.Length > MaxLine) continue;
                var line = raw.TrimEnd('\r');
                Match match;
                if ((match = Arrow.Match(line)).Success)
                {
                    if (pendingMessage != null && linesSincePending <= 3)
                        Add(result, seen, match, workingDirectory, pendingMessage, pendingSeverity);
                    pendingMessage = null;
                    continue;
                }
                if ((match = Headline.Match(line)).Success)
                {
                    pendingMessage = (match.Groups["code"].Success ? match.Groups["code"].Value + ": " : "") + match.Groups["msg"].Value;
                    pendingSeverity = match.Groups["sev"].Value == "warning" ? BuildDiagnosticSeverity.Warning : BuildDiagnosticSeverity.Error;
                    linesSincePending = 0;
                    continue;
                }
                if (pendingMessage != null && pendingSeverity == BuildDiagnosticSeverity.Error && (match = CausedBy.Match(line)).Success)
                {
                    pendingMessage = match.Groups["msg"].Value; // the innermost cause names the problem at the location
                    linesSincePending = 0;
                    continue;
                }
                linesSincePending++;
                if ((match = DartAnalyze.Match(line)).Success)
                {
                    // The lint code follows the message there; it is not a compiler code to prefix.
                    Add(result, seen, match, workingDirectory, match.Groups["msg"].Value, SeverityOf(match));
                    continue;
                }
                if ((match = Msvc.Match(line)).Success || (match = Gnu.Match(line)).Success)
                {
                    var code = match.Groups["code"].Success ? match.Groups["code"].Value : "";
                    Add(result, seen, match, workingDirectory, (code.Length > 0 ? code + ": " : "") + match.Groups["msg"].Value, SeverityOf(match));
                }
            }
            return result;
        }

        private static BuildDiagnosticSeverity SeverityOf(Match match) =>
            match.Groups["sev"].Value.EndsWith("warning", StringComparison.OrdinalIgnoreCase) ? BuildDiagnosticSeverity.Warning : BuildDiagnosticSeverity.Error;

        private static void Add(List<BuildDiagnostic> result, HashSet<string> seen, Match match, string workingDirectory, string message, BuildDiagnosticSeverity severity)
        {
            var file = Resolve(match.Groups["file"].Value.Trim(), workingDirectory);
            if (file == null) return;
            var line = int.Parse(match.Groups["line"].Value) - 1;
            var column = match.Groups["col"].Success ? Math.Max(0, int.Parse(match.Groups["col"].Value) - 1) : 0;
            if (line < 0) return;
            var key = file + "|" + line + "|" + column + "|" + message;
            if (!seen.Add(key)) return;
            result.Add(new BuildDiagnostic(file, line, column, message, severity));
        }

        private static string? Resolve(string file, string workingDirectory)
        {
            if (file.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) file = Uri.UnescapeDataString(file.Substring(8));
            try
            {
                var path = Path.IsPathRooted(file) ? file : Path.Combine(workingDirectory, file);
                return Path.GetFullPath(path);
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { return null; }
        }
    }
}
