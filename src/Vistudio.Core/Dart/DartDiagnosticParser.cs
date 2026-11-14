using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Vistudio.Core.Dart
{
    public enum DartDiagnosticSeverity { Error, Warning, Info }

    /// <summary>One Dart problem with a zero-based location (Visual Studio's convention); <see cref="File"/> is absolute when known.</summary>
    public sealed class DartDiagnostic
    {
        public DartDiagnostic(DartDiagnosticSeverity severity, string message, string file, int line, int column, int length = 0, string code = "")
        { Severity = severity; Message = message; File = file; Line = line; Column = column; Length = length; Code = code; }
        public DartDiagnosticSeverity Severity { get; }
        public string Message { get; }
        public string File { get; }
        public int Line { get; }
        public int Column { get; }
        public int Length { get; }
        /// <summary>Analyzer code (<c>undefined_identifier</c>) or empty.</summary>
        public string Code { get; }
        public bool HasLocation => File.Length > 0 && Line >= 0;
        public override string ToString() => File + "(" + (Line + 1) + "," + (Column + 1) + "): " + Severity.ToString().ToLowerInvariant() + ": " + Message;
    }

    /// <summary>
    /// Parses Dart tool output: <c>dart analyze --format=machine</c> lines and the front-end compiler messages printed by
    /// <c>dart run</c>, <c>flutter run</c> and the test runners (<c>lib/main.dart:12:5: Error: ...</c>). Compiler lines are
    /// rewritten to the <c>path(line,col): error: message</c> form that the Output pane navigates on double-click.
    /// </summary>
    public sealed class DartDiagnosticParser
    {
        private readonly string workingDirectory;
        private readonly List<DartDiagnostic> diagnostics = new List<DartDiagnostic>();

        public DartDiagnosticParser(string workingDirectory) => this.workingDirectory = workingDirectory;

        public IReadOnlyList<DartDiagnostic> Diagnostics => diagnostics;

        // Front-end (CFE) messages. The path may be relative, absolute (C:/ or C:\) or a file:/// URI.
        private static readonly Regex Compiler = new Regex(
            @"^(?<file>(?:file:///)?(?:[A-Za-z]:[\\/])?[^:*?""<>|\r\n]+?\.dart):(?<line>\d+):(?<col>\d+): (?<severity>Error|Warning|Info|Context): (?<message>.*)$",
            RegexOptions.CultureInvariant);

        /// <summary>Records a diagnostic when the line is a compiler message; returns the line to show in the Output pane.</summary>
        public string Accept(string line)
        {
            var diagnostic = ParseCompilerLine(line, workingDirectory);
            if (diagnostic == null) return line;
            diagnostics.Add(diagnostic);
            return diagnostic.ToString();
        }

        public void Add(DartDiagnostic diagnostic) => diagnostics.Add(diagnostic);

        /// <summary>Forgets earlier diagnostics (before a hot reload recompiles).</summary>
        public void Clear() => diagnostics.Clear();

        public static DartDiagnostic? ParseCompilerLine(string line, string workingDirectory)
        {
            var match = Compiler.Match(line.TrimEnd());
            if (!match.Success || match.Groups["severity"].Value == "Context") return null;
            var severity = match.Groups["severity"].Value switch
            {
                "Error" => DartDiagnosticSeverity.Error,
                "Warning" => DartDiagnosticSeverity.Warning,
                _ => DartDiagnosticSeverity.Info,
            };
            return new DartDiagnostic(severity, match.Groups["message"].Value.Trim(), ResolvePath(match.Groups["file"].Value, workingDirectory),
                Number(match.Groups["line"].Value) - 1, Number(match.Groups["col"].Value) - 1);
        }

        /// <summary>
        /// One <c>dart analyze --format=machine</c> line: <c>SEVERITY|TYPE|CODE|FILE|LINE|COLUMN|LENGTH|MESSAGE</c>, where
        /// <c>\</c> escapes <c>|</c> and <c>\</c> inside fields. Null for other lines ("Analyzing ...", summaries).
        /// </summary>
        public static DartDiagnostic? ParseMachineLine(string line)
        {
            var fields = SplitMachine(line.TrimEnd('\r', '\n'));
            if (fields.Count < 8) return null;
            DartDiagnosticSeverity severity;
            switch (fields[0])
            {
                case "ERROR": severity = DartDiagnosticSeverity.Error; break;
                case "WARNING": severity = DartDiagnosticSeverity.Warning; break;
                case "INFO": severity = DartDiagnosticSeverity.Info; break;
                default: return null;
            }
            if (!int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var row) ||
                !int.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out var column)) return null;
            int.TryParse(fields[6], NumberStyles.None, CultureInfo.InvariantCulture, out var length);
            var message = string.Join("|", fields.GetRange(7, fields.Count - 7));
            return new DartDiagnostic(severity, message, fields[3], row - 1, column - 1, length, fields[2].ToLowerInvariant());
        }

        public static IReadOnlyList<DartDiagnostic> ParseMachine(string output)
        {
            var result = new List<DartDiagnostic>();
            foreach (var line in output.Split('\n'))
            {
                var diagnostic = ParseMachineLine(line);
                if (diagnostic != null) result.Add(diagnostic);
            }
            return result;
        }

        private static List<string> SplitMachine(string line)
        {
            var fields = new List<string>();
            var current = new StringBuilder();
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '\\' && i + 1 < line.Length) { current.Append(line[++i]); continue; }
                if (c == '|') { fields.Add(current.ToString()); current.Clear(); continue; }
                current.Append(c);
            }
            fields.Add(current.ToString());
            return fields;
        }

        /// <summary>An absolute Windows path for a relative path, a forward-slash path or a <c>file:///</c> URI.</summary>
        public static string ResolvePath(string file, string workingDirectory)
        {
            var value = file.Trim();
            if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
                return uri.LocalPath;
            value = value.Replace('/', Path.DirectorySeparatorChar);
            try { return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(workingDirectory, value)); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { return value; }
        }

        private static int Number(string value) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 1;
    }
}
