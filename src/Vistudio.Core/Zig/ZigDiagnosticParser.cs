using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Vistudio.Core.Zig
{
    public enum ZigDiagnosticSeverity { Error, Warning, Note }

    public sealed class ZigDiagnostic
    {
        public ZigDiagnostic(string file, int line, int column, ZigDiagnosticSeverity severity, string message)
        { File = file; Line = line; Column = column; Severity = severity; Message = message; }
        /// <summary>Absolute path, or "" for a message without a location.</summary>
        public string File { get; }
        /// <summary>Zero-based, as ErrorTask and IVsTextView use. -1 without a location.</summary>
        public int Line { get; }
        public int Column { get; }
        public ZigDiagnosticSeverity Severity { get; }
        public string Message { get; }
        /// <summary>The <c>note:</c> lines that followed this error.</summary>
        public List<ZigDiagnostic> Notes { get; } = new List<ZigDiagnostic>();
        public bool HasLocation => Line >= 0 && File.Length > 0;

        /// <summary>Message text for an error-only view: notes appended.</summary>
        public string MessageWithNotes => Notes.Count == 0 ? Message : Message + " (" + string.Join("; ", Notes.Select(n => "note: " + n.Message)) + ")";
    }

    /// <summary>
    /// Parses zig compiler output (<c>ast-check</c>, <c>build-obj</c>, <c>run</c>, <c>build</c>; <c>--color off</c>):
    /// <c>path:line:col: error|warning|note: message</c> headers followed by a source line and a caret line, notes
    /// after their error, and location-free <c>error: message</c> lines. Zig has no JSON diagnostic format.
    /// </summary>
    public sealed class ZigDiagnosticParser
    {
        private static readonly Regex Located = new Regex(@"^(?<file>.+?):(?<line>\d+):(?<column>\d+): (?<severity>error|warning|note): (?<message>.*)$", RegexOptions.CultureInvariant);
        private static readonly Regex Bare = new Regex(@"^(?<severity>error|warning|note): (?<message>.*)$", RegexOptions.CultureInvariant);
        // Summaries `zig build` prints after the located errors; they are not separate problems.
        private static readonly Regex Summary = new Regex(@"^(?:\d+ compilation errors?|the following (?:build )?command (?:failed|exited|terminated).*)$", RegexOptions.CultureInvariant);

        private readonly string workingDirectory;
        private readonly string? stdinFile;
        private readonly List<ZigDiagnostic> diagnostics = new List<ZigDiagnostic>();
        private ZigDiagnostic? lastError;

        /// <param name="workingDirectory">Folder relative paths are resolved against (the process working directory).</param>
        /// <param name="stdinFile">Document whose text was piped to the compiler; replaces <c>&lt;stdin&gt;</c>.</param>
        public ZigDiagnosticParser(string workingDirectory, string? stdinFile = null)
        {
            this.workingDirectory = workingDirectory;
            this.stdinFile = stdinFile;
        }

        /// <summary>Top-level errors and warnings (and notes without a preceding error), in output order.</summary>
        public IReadOnlyList<ZigDiagnostic> Diagnostics => diagnostics;

        public static IReadOnlyList<ZigDiagnostic> Parse(string output, string workingDirectory, string? stdinFile = null)
        {
            var parser = new ZigDiagnosticParser(workingDirectory, stdinFile);
            foreach (var line in output.Replace("\r\n", "\n").Split('\n')) parser.Accept(line);
            return parser.Diagnostics;
        }

        /// <summary>
        /// Consumes one output line and returns the text to show in the Output window: located messages are rewritten
        /// to <c>path(line,col): error: message</c>, which Visual Studio navigates to on double-click.
        /// </summary>
        public string Accept(string line)
        {
            var match = Located.Match(line);
            if (match.Success)
            {
                var file = ResolvePath(match.Groups["file"].Value);
                var row = int.Parse(match.Groups["line"].Value) - 1;
                var column = int.Parse(match.Groups["column"].Value) - 1;
                var severity = ParseSeverity(match.Groups["severity"].Value);
                var message = match.Groups["message"].Value.Trim();
                Add(new ZigDiagnostic(file ?? "", file == null ? -1 : Math.Max(0, row), file == null ? -1 : Math.Max(0, column), severity, message));
                return file == null ? line : file + "(" + (row + 1) + "," + (column + 1) + "): " + match.Groups["severity"].Value + ": " + message;
            }
            match = Bare.Match(line);
            if (match.Success)
            {
                var message = match.Groups["message"].Value.Trim();
                if (!Summary.IsMatch(message)) Add(new ZigDiagnostic("", -1, -1, ParseSeverity(match.Groups["severity"].Value), message));
            }
            return line;
        }

        private void Add(ZigDiagnostic diagnostic)
        {
            if (diagnostic.Severity == ZigDiagnosticSeverity.Note && lastError != null)
            {
                lastError.Notes.Add(diagnostic);
                return;
            }
            diagnostics.Add(diagnostic);
            if (diagnostic.Severity != ZigDiagnosticSeverity.Note) lastError = diagnostic;
        }

        private string? ResolvePath(string path)
        {
            if (path == "<stdin>") return stdinFile;
            try { return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path)); }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
        }

        private static ZigDiagnosticSeverity ParseSeverity(string value) =>
            value == "warning" ? ZigDiagnosticSeverity.Warning : value == "note" ? ZigDiagnosticSeverity.Note : ZigDiagnosticSeverity.Error;

        /// <summary>
        /// Flattens diagnostics for the Error List and squiggles: with <see cref="ZigDiagnosticLevel.Errors"/> notes
        /// are folded into the error message; otherwise each located note is its own entry.
        /// </summary>
        public static IReadOnlyList<ZigDiagnostic> Flatten(IEnumerable<ZigDiagnostic> diagnostics, ZigDiagnosticLevel level)
        {
            var result = new List<ZigDiagnostic>();
            foreach (var diagnostic in diagnostics)
            {
                if (level == ZigDiagnosticLevel.Errors)
                {
                    if (diagnostic.Severity == ZigDiagnosticSeverity.Note) continue;
                    result.Add(diagnostic.Notes.Count == 0 ? diagnostic
                        : new ZigDiagnostic(diagnostic.File, diagnostic.Line, diagnostic.Column, diagnostic.Severity, diagnostic.MessageWithNotes));
                    continue;
                }
                result.Add(diagnostic);
                foreach (var note in diagnostic.Notes)
                    result.Add(note.HasLocation ? note : new ZigDiagnostic(diagnostic.File, diagnostic.Line, diagnostic.Column, ZigDiagnosticSeverity.Note, note.Message));
            }
            return result;
        }
    }
}
