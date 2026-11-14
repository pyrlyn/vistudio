using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vistudio.Core;

internal static class DiagnosticTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var file = Path.Combine(root, "module with spaces", "Main.kt");
        var uri = new Uri(file).AbsoluteUri;
        check(GradleDiagnosticParser.TryParse("e: " + uri + ":12:7 Unresolved reference 'missing'.", root, out var modern) &&
            modern!.File == file && modern.Line == 11 && modern.Column == 6 && modern.Message == "Unresolved reference 'missing'." &&
            modern.Severity == BuildDiagnosticSeverity.Error, "Modern URI diagnostic preserves path, position and message");
        check(GradleDiagnosticParser.TryParse("w: " + file + ": (3, 1): Deprecated: use another function", root, out var old) &&
            old!.File == file && old.Line == 2 && old.Column == 0 && old.Severity == BuildDiagnosticSeverity.Warning &&
            old.Message == "Deprecated: use another function", "Legacy warning and first column");
        check(GradleDiagnosticParser.TryParse("\u001b[31me: module/src/Main.kt:1:1: Type mismatch\u001b[0m", root, out var relative) &&
            relative!.File == Path.Combine(root, "module", "src", "Main.kt") && relative.Line == 0 && relative.Column == 0 &&
            relative.Message == "Type mismatch", "Relative paths, ANSI escapes and one-based conversion");
        check(GradleDiagnosticParser.TryParse("e: build.gradle.kts:9:2: Unresolved reference: plugins", root, out var script) &&
            script!.File == Path.Combine(root, "build.gradle.kts"), "Kotlin Gradle script diagnostic");
        var unicode = Path.Combine(root, "space # percent% (copy)", "\u03b1.kt");
        check(GradleDiagnosticParser.TryParse("e: " + new Uri(unicode).AbsoluteUri + ":2:4 Broken", root, out var escaped) &&
            escaped!.File == unicode, "URI percent decoding preserves Unicode, hash, percent and parentheses");
        foreach (var text in new[] { "> Task :compileKotlin FAILED", "BUILD FAILED in 1s", "e: General compiler failure",
            "e: Main.kt:0:1 Wrong", "e: Main.kt:1:0 Wrong", "e: Main.kt:99999999999999:1 Wrong",
            "e: Main.kt:1:1", "e: C:Main.kt:1:1 Wrong", "e: https://host/Main.kt:1:1 Wrong",
            "e: Main.kt: (0, 1): Wrong", "at Compiler.run(Compiler.kt:12)", new string('x', 65537) })
            check(!GradleDiagnosticParser.TryParse(text, root, out _), "Non-diagnostic or invalid location rejected: " + text.Substring(0, Math.Min(text.Length, 50)));

        var build = new BuildDiagnostics(root);
        Parallel.For(0, 40, _ => build.Accept("e: " + uri + ":12:7 Repeated error"));
        build.Accept("w: " + uri + ":12:7 Repeated error");
        build.Accept("e: " + uri + ":12:7 Different error");
        var items = build.Complete(1);
        check(items.Length == 3, "Concurrent stream duplicate suppression preserves distinct severity and message");
        check(items.All(x => x.File.Length > 0), "Located errors suppress generic exit failure");
        check(build.Complete(1).Length == 3, "Taking a snapshot does not mutate collection");
        var warningOnly = new BuildDiagnostics(root);
        warningOnly.Accept("w: " + uri + ":2:1 Deprecated");
        check(warningOnly.Complete(0).Single().Severity == BuildDiagnosticSeverity.Warning, "Successful build retains warnings");
        check(warningOnly.Complete(1).Length == 2 && warningOnly.Complete(1).Last().File == "" &&
            warningOnly.Complete(1).Last().Line == -1, "Non-source failure has no invented navigation target");
        check(new BuildDiagnostics(root).Complete(0).Length == 0, "New successful build has no stale diagnostics");
    }
}
