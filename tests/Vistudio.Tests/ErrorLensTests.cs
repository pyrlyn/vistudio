using System;
using Vistudio.Core;

internal static class ErrorLensTests
{
    internal static void Run(Action<bool, string> check)
    {
        check(ErrorLens.Classify("syntax error") == ErrorLensSeverity.Error, "Syntax error is an error");
        check(ErrorLens.Classify("compiler error") == ErrorLensSeverity.Error, "Compiler error is an error");
        check(ErrorLens.Classify("other error") == ErrorLensSeverity.Error, "Other error is an error");
        check(ErrorLens.Classify(null) == ErrorLensSeverity.Error, "Untyped error tag is an error");
        check(ErrorLens.Classify("Warning") == ErrorLensSeverity.Warning, "Warning is case-insensitive");
        check(ErrorLens.Classify("compiler warning") == ErrorLensSeverity.Warning, "Compiler warning");
        check(ErrorLens.Classify("suggestion") == ErrorLensSeverity.Info, "Suggestion is info");
        check(ErrorLens.Classify("hinted suggestion") == ErrorLensSeverity.None, "Hinted suggestion is hidden");
        check(ErrorLens.Classify("spelling error") == ErrorLensSeverity.None, "Spelling is hidden");

        var summary = ErrorLens.Summary(new[]
        {
            new ErrorLensEntry(ErrorLensSeverity.Warning, "Unused variable 'x'"),
            new ErrorLensEntry(ErrorLensSeverity.Error, "Unresolved reference:\r\n  foo"),
            new ErrorLensEntry(ErrorLensSeverity.Error, "  Unresolved reference:\r\n  foo  "),
            new ErrorLensEntry(ErrorLensSeverity.Info, ""),
            new ErrorLensEntry(ErrorLensSeverity.None, "hidden"),
        }, out var severity, out var tooltip);
        check(severity == ErrorLensSeverity.Error, "Most severe diagnostic wins");
        check(summary == "Unresolved reference: foo  (+1)", "Summary flattens the top message and counts the rest: " + summary);
        check(tooltip.StartsWith("Unresolved reference:") && tooltip.EndsWith("Unused variable 'x'") && !tooltip.Contains("hidden"),
            "Tooltip lists distinct messages by severity");

        var ordered = ErrorLens.Ordered(new[]
        {
            new ErrorLensEntry(ErrorLensSeverity.Info, "a"), new ErrorLensEntry(ErrorLensSeverity.Warning, "b"),
            new ErrorLensEntry(ErrorLensSeverity.Info, "c"), new ErrorLensEntry(ErrorLensSeverity.Warning, "d"),
        });
        check(string.Concat(ordered.ConvertAll(x => x.Message)) == "bdac", "Ordering is stable within a severity");

        check(ErrorLens.Summary(Array.Empty<ErrorLensEntry>(), out var none, out _) == null && none == ErrorLensSeverity.None, "No diagnostics, no label");
        check(ErrorLens.Summary(new[] { new ErrorLensEntry(ErrorLensSeverity.Warning, "One") }, out _, out _) == "One", "Single message has no count");
    }
}
