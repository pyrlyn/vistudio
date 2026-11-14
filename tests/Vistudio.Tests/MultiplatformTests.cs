using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Vistudio.Core;

internal static class MultiplatformTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var catalog = new GradleTaskCatalog();
        string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        var targets = new[] { ":desktop:jvmJar", ":desktop:jvmRun", ":desktop:jvmTest", ":shared:linkDebugExecutableMingwX64", ":shared:runDebugExecutableMingwX64", ":shared:mingwX64Test", ":web:jsBrowserProductionWebpack", ":web:jsNodeDevelopmentRun", ":web:jsNodeTest", ":web:wasmJsBrowserDistribution", ":web:wasmJsNodeDevelopmentRun", ":web:wasmJsNodeTest", ":allTests" };
        foreach (var target in targets) catalog.Accept("VISTUDIO_TASK\t" + target + "\t" + Encode("build") + "\t" + Encode("Target — " + target));
        catalog.Accept("VISTUDIO_TASK\t:allTests\t\t");
        foreach (var invalid in new[] { "random output", "VISTUDIO_TASK\t:bad&whoami\t\t", "VISTUDIO_TASK\t:valid\t???\t", "VISTUDIO_TASK\t:one :two\t\t", "VISTUDIO_TASK\tnoRoot\t\t" }) catalog.Accept(invalid);
        var discovered = catalog.Complete(0);
        check(discovered.Length == targets.Length, "Discovery rejects malformed records and deduplicates task paths");
        check(discovered.Any(x => x.Description.Contains("Target —")), "Discovery preserves Unicode metadata");
        try { catalog.Complete(1); throw new Exception("Failed discovery accepted"); }
        catch (InvalidOperationException) { check(true, "Failed discovery cannot execute a partial catalog"); }
        File.WriteAllText(Path.Combine(root, "gradlew.bat"), "@echo off\r\necho ARGS:%*\r\nexit /b 0\r\n");
        foreach (var task in discovered)
        {
            var output = new ConcurrentQueue<string>();
            var code = await OwnedProcess.RunAsync(GradleCommand.Create(root, "", new[] { task.Path }), output.Enqueue, CancellationToken.None);
            check(code == 0 && output.Any(x => x.EndsWith(task.Path, StringComparison.Ordinal)), "Exact target task forwarded: " + task.Path);
        }
        var script = Path.Combine(root, "discovery script.gradle"); File.WriteAllText(script, "// fixture");
        var discovery = GradleCommand.CreateDiscovery(root, "", script);
        check(discovery.Arguments.Contains("--init-script \"" + script + "\"") && discovery.Arguments.Contains(GradleCommand.DiscoveryTask), "Discovery script path is quoted and task is fixed");
        var discoveredOutput = new ConcurrentQueue<string>();
        check(await OwnedProcess.RunAsync(discovery, discoveredOutput.Enqueue, CancellationToken.None) == 0, "Discovery wrapper launches with spaced script path");
        check(discoveredOutput.Any(x => x.Contains("--no-configuration-cache")), "Discovery bypasses incompatible configuration caching");
        using (var reader = new StringReader("<KotlinSettings><BuildTasks>:app:build</BuildTasks></KotlinSettings>"))
        {
            var legacy = (KotlinSettings)new XmlSerializer(typeof(KotlinSettings)).Deserialize(reader);
            check(legacy.RunTasks == "run" && legacy.EditorDiagnostics && legacy.RainbowBraces, "Existing settings inherit enabled editor defaults");
        }
        VerifyBraces(check);
    }

    private static void VerifyBraces(Action<bool, string> check)
    {
        var code = "{} {} { {} {} { {} } }";
        var braces = KotlinBraceScanner.Scan(code);
        var stack = new System.Collections.Generic.Stack<ColoredBrace>();
        ColoredBrace? previous = null;
        foreach (var brace in braces)
        {
            if (code[brace.Position] == '{')
            {
                check(!previous.HasValue || previous.Value.Color != brace.Color, "Adjacent pairs use different colors");
                check(stack.Count == 0 || stack.Peek().Color != brace.Color, "Nested pairs use different colors");
                stack.Push(brace);
            }
            else check(stack.Pop().Color == brace.Color, "Matching braces share a color");
            previous = brace;
        }
        check(braces.Count == code.Count(c => c == '{' || c == '}'), "All code braces are classified");
        check(KotlinBraceScanner.Scan("\"{}\" '\u007b' `br{ace}` // {}\n/* { /* } */ } */ \"\"\"{}\"\"\"").Count == 0, "Strings, characters, escaped identifiers and nested comments are ignored");
        var template = "\"text ${run { \"${value}\" }} literal {}\"";
        check(KotlinBraceScanner.Scan(template).Count == 6, "Nested template code and its pairs are classified");
        check(KotlinBraceScanner.Scan("\"\\${literal}\"").Count == 0, "Escaped interpolation stays literal");
        check(KotlinBraceScanner.Scan("$$\"\"\"${literal} $${run { 1 }}\"\"\"").Count == 4, "Multi-dollar interpolation respects the string prefix");
        check(KotlinBraceScanner.Scan("\"\"\"${run { 1 }} {}\"\"\"").Count == 4, "Raw string interpolation is code");
        check(KotlinBraceScanner.Scan(new string('{', 20000) + new string('}', 20000)).Count == 40000, "Deep nesting uses bounded call stack");
        check(KotlinBraceScanner.Scan("} { /* unterminated {").Count == 1, "Incomplete documents remain safe");
    }
}
