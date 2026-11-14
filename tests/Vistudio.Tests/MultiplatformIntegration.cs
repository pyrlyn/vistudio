using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vistudio.Core;

internal static class MultiplatformIntegration
{
    public static async Task<int> RunAsync(string root, string script, string logs)
    {
        root = Path.GetFullPath(root); logs = Path.GetFullPath(logs);
        Directory.CreateDirectory(logs);
        var failureSource = Path.Combine(root, "src", "commonMain", "kotlin", "VistudioFailure" + Guid.NewGuid().ToString("N") + ".kt");
        try
        {
            var catalog = new GradleTaskCatalog();
            var discoveryLines = new ConcurrentQueue<string>();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15)))
            {
                var code = await OwnedProcess.RunAsync(GradleCommand.CreateDiscovery(root, "", script), line => { catalog.Accept(line); discoveryLines.Enqueue(line); }, timeout.Token);
                File.WriteAllLines(Path.Combine(logs, "discovery.log"), discoveryLines);
                var tasks = catalog.Complete(code).Select(x => x.Path).ToArray();
                foreach (var required in new[] { ":jvmJar", ":jvmRun", ":linkDebugExecutableMingwX64", ":runDebugExecutableMingwX64", ":jsNodeDevelopmentRun", ":wasmJsNodeDevelopmentRun", ":allTests" })
                    if (!tasks.Contains(required)) throw new Exception("Missing real Gradle target task: " + required);
                Console.WriteLine("PASS: discovered " + tasks.Length + " real Gradle tasks.");
            }
            // KGP development and production JS outputs share paths: build and run in separate invocations.
            var build = await RunTasksAsync(root, new[] { "build" }, logs, "build");
            if (build.Exit != 0) throw new Exception("Target build failed. See build.log.");
            var success = await RunTasksAsync(root, new[] { "verifyTargets" }, logs, "run");
            if (success.Exit != 0 || success.Lines.Count(x => x.Trim() == "VISTUDIO_KMP_PASS") != 4)
                throw new Exception("Run self-tests did not pass on all four runtimes. See run.log.");
            Console.WriteLine("PASS: build and executable self-tests on JVM, Windows Native, JS/Node and Wasm/Node.");
            File.WriteAllText(failureSource, "package sample\nfun vistudioFailureProbe() = vistudioMissingSymbol\n");
            foreach (var task in new[] { "compileKotlinJvm", "compileKotlinMingwX64", "compileKotlinJs", "compileKotlinWasmJs" })
            {
                var failed = await RunTasksAsync(root, new[] { task }, logs, task + "-failure");
                var diagnostics = new BuildDiagnostics(root);
                foreach (var line in failed.Lines) diagnostics.Accept(line);
                if (failed.Exit == 0 || !diagnostics.Complete(failed.Exit).Any(x => string.Equals(x.File, failureSource, StringComparison.OrdinalIgnoreCase) && x.Line == 1 && x.Severity == BuildDiagnosticSeverity.Error))
                    throw new Exception("Expected located compiler error from " + task);
                Console.WriteLine("PASS: actual compiler failure and source diagnostic from " + task);
            }
            File.Delete(failureSource);
            var recovery = await RunTasksAsync(root, new[] { "verifyTargets" }, logs, "recovery");
            if (recovery.Exit != 0 || recovery.Lines.Count(x => x.Trim() == "VISTUDIO_KMP_PASS") != 4)
                throw new Exception("Target recovery failed after removing the deliberate compiler error.");
            Console.WriteLine("PASS: all targets recover after fixing the shared source.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { if (File.Exists(failureSource)) File.Delete(failureSource); }
    }

    private static async Task<(int Exit, string[] Lines)> RunTasksAsync(string root, string[] tasks, string logs, string name)
    {
        Console.WriteLine("Gradle " + string.Join(" ", tasks));
        var lines = new ConcurrentQueue<string>();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20)))
        using (var log = new StreamWriter(Path.Combine(logs, name + ".log")) { AutoFlush = true })
        {
            var code = await OwnedProcess.RunAsync(GradleCommand.Create(root, "", tasks), line => { lines.Enqueue(line); lock (log) log.WriteLine(line); }, timeout.Token);
            var output = lines.ToArray();
            return (code, output);
        }
    }
}
