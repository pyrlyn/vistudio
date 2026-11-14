namespace Vistudio.Core
{
    public enum BuildDiagnosticSeverity { Error, Warning }

    public sealed class BuildDiagnostic
    {
        public BuildDiagnostic(string file, int line, int column, string message, BuildDiagnosticSeverity severity)
        {
            File = file; Line = line; Column = column; Message = message; Severity = severity;
        }
        public string File { get; }
        // Zero-based coordinates, shared by ErrorTask and IVsTextView. -1 means no location.
        public int Line { get; }
        public int Column { get; }
        public string Message { get; }
        public BuildDiagnosticSeverity Severity { get; }
    }
}
