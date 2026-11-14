using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vistudio.Core;
using Vistudio.Core.Preview;

internal static class ComposePreviewTests
{
    private const string Source = @"@file:JvmName(""CardPreviews"")
package sample.ui

import androidx.compose.runtime.Composable
import androidx.compose.ui.tooling.preview.Preview

// @Preview fun Commented() {}
/* @Preview
   @Composable fun AlsoCommented() {} /* nested */ */
val text = ""@Preview fun InString() { }""
val raw = """"""
@Preview fun InRaw() {}
""""""

@Preview
@Composable
fun Plain() { Text(""${'{'}"") }

@Preview(name = ""Badge, white"", widthDp = 160, heightDp = 6_0, showBackground = true, backgroundColor = 0xFF_FF_00_00)
@Preview(""Positional"", group = ""g"")
@Composable
private fun `Badge preview`() {}

@androidx.compose.ui.tooling.preview.Preview
@Composable internal fun Qualified() = Unit

@Preview @Composable fun WithParameter(@PreviewParameter(Provider::class) value: String) {}
@Preview @Composable fun String.WithReceiver() {}
@PreviewParameter fun NotAPreview() {}

object Holder {
    @Preview @Composable fun Nested() {}
}

@Preview
annotation class MultiPreview
";

    internal static void Run(Action<bool, string> check)
    {
        var file = ComposePreviewScanner.Scan(@"C:\p\Cards.kt", Source.Replace("\r\n", "\n"));
        var names = file.Previews.Select(p => p.Function).ToArray();
        check(file.JvmClass == "sample.ui.CardPreviews", "@file:JvmName selects the facade class: " + file.JvmClass);
        check(names.SequenceEqual(new[] { "Plain", "Badge preview", "Badge preview", "Qualified", "WithParameter", "WithReceiver", "Nested" }),
            "Previews in comments and strings are ignored, others found in order: " + string.Join(", ", names));
        check(file.Previews.Select(p => p.Id).SequenceEqual(Enumerable.Range(0, 7).Select(i => "p" + i)), "Sequential ids");
        var plain = file.Previews[0];
        check(plain.DisplayName == "Plain" && plain.WidthDp == 0 && plain.HeightDp == 0 && !plain.ShowBackground && plain.Unsupported == null
            && plain.Line == 17, "Default preview arguments and line " + plain.Line);
        var badge = file.Previews[1];
        check(badge.DisplayName == "Badge, white" && badge.WidthDp == 160 && badge.HeightDp == 60 && badge.ShowBackground
            && badge.BackgroundColor == 0xFFFF0000u && badge.Unsupported == null, "Named arguments, underscores and quoted commas");
        check(file.Previews[2].DisplayName == "Positional" && file.Previews[2].WidthDp == 0, "Second @Preview with positional name");
        check(file.Previews[3].Unsupported == null, "Qualified annotation and expression body");
        check(file.Previews[4].Unsupported?.Contains("parameters") == true, "Parameters are unsupported");
        check(file.Previews[5].Unsupported?.Contains("receiver") == true, "Receivers are unsupported");
        check(file.Previews[6].Unsupported?.Contains("top-level") == true, "Nested previews are unsupported");
        check(file.Renderable.Count() == 4, "Four renderable previews");
        var request = file.CreateRequest().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        check(request.Length == 4 && request[1] == "p1\tsample.ui.CardPreviews\tBadge preview\t160\t60\ttrue\tFFFF0000"
            && request[0] == "p0\tsample.ui.CardPreviews\tPlain\t0\t0\tfalse\t00000000", "Renderer request lines: " + request.FirstOrDefault());

        check(ComposePreviewScanner.FacadeName("Cards.kt") == "CardsKt" && ComposePreviewScanner.FacadeName("main.kt") == "MainKt"
            && ComposePreviewScanner.FacadeName("App.desktop.kt") == "App_desktopKt" && ComposePreviewScanner.FacadeName("1st-file.kt") == "_1st_fileKt",
            "Kotlin file facade names");
        var noPackage = ComposePreviewScanner.Scan(@"C:\p\screens.kt", "@Preview\n@Composable\nfun Screen() {}\n");
        check(noPackage.JvmClass == "ScreensKt" && noPackage.Previews.Count == 1, "Default package facade");
        check(!ComposePreviewScanner.MayContainPreviews("fun main() {}") && ComposePreviewScanner.Scan("a.kt", "fun main() {}").Previews.Count == 0,
            "Files without previews");

        var results = ComposePreviewResult.ParseManifest("p0\tOK\tp0.png\t416\t207\t2.0\r\np2\tERROR\tjava.lang.IllegalStateException: boom\\nat sample.ui.CardsKt.F(Cards.kt:61)\\tx\\\\y\n"
            + "p3\tOK\t..\\evil.png\t1\t1\t2\njunk\n");
        check(results.Count == 2 && results["p0"].File == "p0.png" && results["p0"].Width == 416 && results["p0"].Scale == 2.0,
            "Successful result parsed; unsafe file names rejected");
        check(results["p2"].Error == "java.lang.IllegalStateException: boom\nat sample.ui.CardsKt.F(Cards.kt:61)\tx\\y", "Escaped error message");
        check(ComposePreviewResult.GradleError("VISTUDIO_PREVIEW_ERROR\tNo JVM target.") == "No JVM target."
            && ComposePreviewResult.GradleError("> Task :x") == null, "Gradle error marker");
    }

    /// <summary>Renders the Compose sample's previews through the real Gradle wrapper and the shipped init script/renderer.</summary>
    internal static async Task<int> RunIntegrationAsync(string sampleRoot, string javaHome, string buildDirectory)
    {
        var source = Path.Combine(sampleRoot, "composeApp", "src", "commonMain", "kotlin", "sample", "ui", "Cards.kt");
        var file = ComposePreviewScanner.Scan(source, File.ReadAllText(source));
        var work = Path.Combine(Path.GetTempPath(), "Vistudio preview " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var request = Path.Combine(work, "request.tsv");
            File.WriteAllText(request, file.CreateRequest());
            var output = Path.Combine(work, "out");
            var start = GradleCommand.CreatePreview(sampleRoot, javaHome, Path.Combine(buildDirectory, "ComposePreview.gradle"),
                Path.Combine(buildDirectory, "VistudioPreviewRenderer.java"), source, request, output);
            string? gradleError = null;
            var code = await OwnedProcess.RunAsync(start, line =>
            {
                Console.WriteLine(line);
                gradleError ??= ComposePreviewResult.GradleError(line);
            }, CancellationToken.None);
            if (code != 0) { Console.Error.WriteLine("FAIL: Gradle exit " + code + " " + gradleError); return 1; }
            var results = ComposePreviewResult.ParseManifest(File.ReadAllText(Path.Combine(output, "results.tsv")));
            foreach (var preview in file.Renderable)
            {
                var ok = results.TryGetValue(preview.Id, out var result);
                Console.WriteLine("RESULT " + preview.DisplayName + ": " + (!ok ? "missing" : result!.File != null
                    ? result.Width + "x" + result.Height + " px, " + new FileInfo(Path.Combine(output, result.File)).Length + " bytes" : "error " + result.Error));
            }
            var expectedErrors = file.Renderable.Where(p => p.Function == "FailingPreview").Select(p => p.Id).ToArray();
            var pass = file.Renderable.All(p => results.ContainsKey(p.Id) && (expectedErrors.Contains(p.Id)
                ? results[p.Id].Error?.Contains("This preview fails on purpose") == true
                : results[p.Id].File != null && File.Exists(Path.Combine(output, results[p.Id].File!))));
            Console.WriteLine(pass ? "PASS: Compose previews rendered through Gradle." : "FAIL: unexpected preview results.");
            return pass ? 0 : 1;
        }
        finally { Directory.Delete(work, true); }
    }
}
