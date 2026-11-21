using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vistudio.Core;

internal static class Program
{
    private static int assertions;
    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }
    private static void Reject(Action action)
    {
        try { action(); } catch (ArgumentException) { assertions++; return; }
        throw new Exception("Expected invalid arguments to be rejected.");
    }

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--gradle-import") return await GradleImportIntegration.RunAsync(args[1], args[2], args[3]);
        if (args.Length == 4 && args[0] == "--multiplatform") return await MultiplatformIntegration.RunAsync(args[1], args[2], args[3]);
        if (args.Length == 4 && args[0] == "--compose-preview") return await ComposePreviewTests.RunIntegrationAsync(args[1], args[2], args[3]);
        if (args.Length == 4 && args[0] == "--compose-live") return await ComposeLivePreviewTests.RunLiveAsync(args[1], args[2], args[3]);
        if (args.Length == 4 && args[0] == "--kotlin-targets") return await KotlinTargetsIntegration.RunAsync(args[1], args[2], args[3]);
        if (args.Length == 3 && args[0] == "--templates") return TemplateTests.Generate(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--etw-emit") return EtwTests.Emit(int.Parse(args[1]));
        if (args.Length == 1 && args[0] == "--etw")
        {
            EtwTests.Run(Check);
            Console.WriteLine("PASS: " + assertions + " assertions (ETW provider and EventSource).");
            return 0;
        }
        var root = Path.Combine(Path.GetTempPath(), "Vistudio tests " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if (args.Length == 1 && args[0] == "--jvm-languages")
            {
                JvmLaunchTests.Run(Check);
                JavaLanguageServerTests.Run(root, Check);
                CompletionTests.Run(Check);
                await DapSourceTests.RunAsync(Check);
                Console.WriteLine("PASS: " + assertions + " assertions (Kotlin/Java launch, Java language server, debugger completion).");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--template-checks")
            {
                TemplateTests.Run(root, Check);
                Console.WriteLine("PASS: " + assertions + " assertions (project templates).");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--feature-matrix")
            {
                // The language × feature table of docs/feature-matrix.md, printed from Core's LanguageFeatureMatrix after its checks.
                var failures = new System.Collections.Generic.List<string>();
                FeatureMatrixTests.Run((condition, message) => { assertions++; if (!condition) failures.Add(message); });
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                Console.Write(Vistudio.Core.Languages.LanguageFeatureMatrix.ToMarkdown());
                foreach (var failure in failures) Console.WriteLine("FAIL: " + failure);
                Console.WriteLine((failures.Count == 0 ? "PASS: " : "FAIL: " + failures.Count + " of ") + assertions + " assertions (feature matrix).");
                return failures.Count == 0 ? 0 : 1;
            }
            if (args.Length == 1 && args[0] == "--structure")
            {
                var failures = new System.Collections.Generic.List<string>();
                StructureTests.Run((condition, message) => { assertions++; if (!condition) failures.Add(message); });
                EditorLanguageTests.Run((condition, message) => { assertions++; if (!condition) failures.Add(message); });
                FeatureMatrixTests.Run((condition, message) => { assertions++; if (!condition) failures.Add(message); });
                foreach (var failure in failures) Console.WriteLine("FAIL: " + failure);
                Console.WriteLine((failures.Count == 0 ? "PASS: " : "FAIL: " + failures.Count + " of ") + assertions + " assertions (structure, editor features of every language).");
                return failures.Count == 0 ? 0 : 1;
            }
            if (args.Length == 1 && args[0] == "--zig")
            {
                // Reports every failing Zig check; the live part runs against a real zig.exe when one is found.
                var failures = new System.Collections.Generic.List<string>();
                Action<bool, string> collect = (condition, message) => { assertions++; if (!condition) failures.Add(message); };
                ZigTests.Run(root, collect);
                TestLensTests.RunZig(collect);
                var live = await ZigTests.RunLiveAsync(root, collect);
                foreach (var failure in failures) Console.WriteLine("FAIL: " + failure);
                Console.WriteLine((failures.Count == 0 ? "PASS: " : "FAIL: " + failures.Count + " of ") + assertions + " assertions (Zig" + (live ? ", live zig.exe" : "") + ").");
                return failures.Count == 0 ? 0 : 1;
            }
            if (args.Length == 1 && args[0] == "--rust-wasm")
            {
                // Reports every failing Rust/WebAssembly check; the live part runs against rust-analyzer and wat_server when found.
                var failures = new System.Collections.Generic.List<string>();
                Action<bool, string> collect = (condition, message) => { assertions++; if (!condition) failures.Add(message); };
                RustWasmTests.Run(Path.Combine(root, "rust-wasm"), collect);
                TestLensTests.RunRust(Path.Combine(root, "test-lens"), collect);
                var live = await RustWasmTests.RunLiveAsync(root, collect);
                foreach (var failure in failures) Console.WriteLine("FAIL: " + failure);
                Console.WriteLine((failures.Count == 0 ? "PASS: " : "FAIL: " + failures.Count + " of ") + assertions + " assertions (Rust and WebAssembly" + (live ? ", live servers" : "") + ").");
                return failures.Count == 0 ? 0 : 1;
            }
            if (args.Length >= 1 && args[0] == "--dart")
            {
                // Reports every failing Dart check; the live part runs against a real SDK when one is found.
                // Optional second argument: comma-separated live stages (run, analyze, tests, lsp, debug, debugtest,
                // widget, flutterrun, flutterdebug, integration), "none" for unit checks only; default all.
                var failures = new System.Collections.Generic.List<string>();
                Action<bool, string> collect = (condition, message) =>
                {
                    assertions++;
                    if (!condition) failures.Add(message);
                    Console.WriteLine((condition ? "  ok   " : "  FAIL ") + (message.Length > 300 ? message.Substring(0, 300) + "..." : message));
                };
                DartTests.Run(root, collect);
                var stages = args.Length > 1 ? args[1] : "all";
                var live = false;
                if (stages != "none")
                {
                    try { live = await DartTests.RunLiveAsync(root, collect, stages); }
                    catch (Exception e) { failures.Add("Live Dart checks stopped: " + e); }
                }
                foreach (var failure in failures) Console.WriteLine("FAIL: " + failure);
                Console.WriteLine((failures.Count == 0 ? "PASS: " : "FAIL: " + failures.Count + " of ") + assertions + " assertions (Dart" + (live ? ", live SDK" : "") + ").");
                return failures.Count == 0 ? 0 : 1;
            }
            if (args.Length >= 1 && args[0] == "--flutter-preview")
            {
                // Flutter widget preview: unit checks, then the live previewer on samples/flutter-preview (skipped with a
                // message when no Flutter 3.35+ SDK or browser is found). Optional second argument: "none" for unit checks
                // only, or comma-separated live stages (dtd, preview, host, cancel, twopages); default all.
                var failures = new System.Collections.Generic.List<string>();
                Action<bool, string> collect = (condition, message) =>
                {
                    assertions++;
                    if (!condition) failures.Add(message);
                    Console.WriteLine((condition ? "  ok   " : "  FAIL ") + (message.Length > 300 ? message.Substring(0, 300) + "..." : message));
                };
                FlutterPreviewTests.Run(root, collect);
                var stages = args.Length > 1 ? args[1] : "all";
                var live = false;
                if (stages != "none")
                {
                    try { live = await FlutterPreviewIntegration.RunAsync(root, collect, stages); }
                    catch (Exception e) { failures.Add("Live widget preview checks stopped: " + e); }
                }
                foreach (var failure in failures) Console.WriteLine("FAIL: " + failure);
                Console.WriteLine((failures.Count == 0 ? "PASS: " : "FAIL: " + failures.Count + " of ") + assertions + " assertions (Flutter widget preview" + (live ? ", live SDK" : ", live part skipped") + ").");
                return failures.Count == 0 ? 0 : 1;
            }
            if (args.Length == 1 && args[0] == "--mise")
            {
                // Reports every failing mise check; the live part runs against the installed mise.exe when one is found.
                var failures = new System.Collections.Generic.List<string>();
                Action<bool, string> collect = (condition, message) => { assertions++; if (!condition) failures.Add(message); };
                MiseTests.Run(root, collect);
                var live = await MiseTests.RunLiveAsync(root, collect);
                foreach (var failure in failures) Console.WriteLine("FAIL: " + failure);
                Console.WriteLine((failures.Count == 0 ? "PASS: " : "FAIL: " + failures.Count + " of ") + assertions + " assertions (mise" + (live ? ", live mise.exe" : "") + ").");
                return failures.Count == 0 ? 0 : 1;
            }
            if (args.Length >= 1 && args[0] == "--packages")
            {
                // Package managers in Solution Explorer (T60), language catalog and projects (T58, T59). The live parts run
                // against the managers found on this machine (network access to their registries); "--packages none" skips them.
                var failures = new System.Collections.Generic.List<string>();
                Action<bool, string> collect = (condition, message) => { assertions++; if (!condition) failures.Add(message); };
                LanguageTests.Run(collect);
                LanguageProjectTests.Run(Path.Combine(root, "language-projects"), collect);
                ToolDiagnosticTests.Run(collect);
                PackagesPubTests.Run(Path.Combine(root, "pub"), collect);
                PackagesCargoTests.Run(Path.Combine(root, "cargo"), collect);
                PackagesConanTests.Run(Path.Combine(root, "conan"), collect);
                PackagesZigTests.Run(Path.Combine(root, "zig"), collect);
                PackagesGradleTests.Run(Path.Combine(root, "gradle"), collect);
                PackagesWkgTests.Run(Path.Combine(root, "wkg"), collect);
                var live = new System.Collections.Generic.List<string>();
                if (!(args.Length > 1 && args[1] == "none"))
                {
                    async Task Live(string name, Func<Task<bool>> run)
                    {
                        try { if (await run()) live.Add(name); }
                        catch (Exception e) { failures.Add("Live " + name + " checks stopped: " + e); }
                    }
                    await Live("pub", () => PackagesPubTests.RunLiveAsync(Path.Combine(root, "pub-live"), collect));
                    await Live("Cargo", () => PackagesCargoTests.RunLiveAsync(Path.Combine(root, "cargo-live"), collect));
                    await Live("Conan", () => PackagesConanTests.RunLiveAsync(Path.Combine(root, "conan-live"), collect));
                    await Live("Zig", () => PackagesZigTests.RunLiveAsync(Path.Combine(root, "zig-live"), collect));
                    await Live("wkg", () => PackagesWkgTests.RunLiveAsync(Path.Combine(root, "wkg-live"), collect));
                    await Live("Gradle", () => PackagesGradleTests.RunLiveAsync(Path.Combine(root, "gradle-live"), collect, Path.GetDirectoryName(DartTests.FindRepositoryFile("AGENTS.md"))!));
                }
                foreach (var failure in failures) Console.WriteLine("FAIL: " + failure);
                Console.WriteLine((failures.Count == 0 ? "PASS: " : "FAIL: " + failures.Count + " of ") + assertions + " assertions (languages, projects, packages"
                    + (live.Count == 0 ? "" : "; live: " + string.Join(", ", live)) + ").");
                return failures.Count == 0 ? 0 : 1;
            }
            if (args.Length >= 1 && args[0] == "--diagnostics")
            {
                // CPU/memory sampling and runtime heap probes (T71); the live part runs a JVM and a Dart VM when a JDK and a
                // Dart SDK are found. "--diagnostics none" runs the unit checks only; "--diagnostics flutter" adds a Flutter
                // Windows app debug session (a full Windows build); "--diagnostics kotlin-targets" runs the JS, wasmJs and mingwX64
                // targets of samples/kotlin-multiplatform under the program-under-Gradle monitor (F5 on those targets).
                var failures = new System.Collections.Generic.List<string>();
                Action<bool, string> collect = (condition, message) => { assertions++; if (!condition) failures.Add(message); };
                DiagnosticsTests.Run(collect);
                var stage = args.Length > 1 ? args[1] : "all";
                var live = stage != "none" && await DiagnosticsTests.RunLiveAsync(root, collect, flutter: stage == "flutter", kotlinTargets: stage == "kotlin-targets");
                foreach (var failure in failures) Console.WriteLine("FAIL: " + failure);
                Console.WriteLine((failures.Count == 0 ? "PASS: " : "FAIL: " + failures.Count + " of ") + assertions + " assertions (diagnostics" + (live ? ", live runtimes" : "") + ").");
                return failures.Count == 0 ? 0 : 1;
            }
            if (args.Length == 1 && args[0] == "--bazel")
            {
                // Bazel files, the Bazel build system and build caches; no Bazel, buildifier or sccache is needed.
                var failures = new System.Collections.Generic.List<string>();
                BazelTests.Run(Path.Combine(root, "bazel"), (condition, message) => { assertions++; if (!condition) failures.Add(message); });
                foreach (var failure in failures) Console.WriteLine("FAIL: " + failure);
                Console.WriteLine((failures.Count == 0 ? "PASS: " : "FAIL: " + failures.Count + " of ") + assertions + " assertions (Bazel, build cache).");
                return failures.Count == 0 ? 0 : 1;
            }
            if (args.Length >= 1 && args[0] == "--hot-reload")
            {
                // Kotlin/JVM hot reload (T72). The live part starts a Kotlin + Java program with the Gradle wrapper of a copy
                // of samples/kotlin-jvm and reloads it (needs a JDK; several minutes). "--hot-reload none" runs the unit checks
                // only; "--hot-reload debugger" instead runs the program under the installed Kotlin server's debugger (F5 and
                // Ctrl+F5 launches on the session's Gradle daemon, a reload at a breakpoint, the end of the session);
                // "--hot-reload debugger-cold" first tries a launch without a daemon and reports how it ends;
                // "--hot-reload all" runs both live parts.
                var failures = new System.Collections.Generic.List<string>();
                Action<bool, string> collect = (condition, message) =>
                {
                    assertions++;
                    if (!condition) failures.Add(message);
                    Console.WriteLine((condition ? "  ok   " : "  FAIL ") + (message.Length > 400 ? message.Substring(0, 400) + "..." : message));
                };
                HotReloadTests.Run(Path.Combine(root, "hot-reload"), collect);
                var stage = args.Length > 1 ? args[1] : "live";
                var live = new System.Collections.Generic.List<string>();
                if (stage == "live" || stage == "all")
                {
                    try { if (await HotReloadTests.RunLiveAsync(root, collect)) live.Add("Gradle and JVM"); }
                    catch (Exception e) { failures.Add("Live hot reload checks stopped: " + e); }
                }
                if (stage == "debugger" || stage == "debugger-cold" || stage == "all")
                {
                    try { if (await HotReloadDebugProbe.RunAsync(root, collect, cold: stage == "debugger-cold")) live.Add("Kotlin server debugger"); }
                    catch (Exception e) { failures.Add("Hot reload checks under the debugger stopped: " + e); }
                }
                foreach (var failure in failures) Console.WriteLine("FAIL: " + failure);
                Console.WriteLine((failures.Count == 0 ? "PASS: " : "FAIL: " + failures.Count + " of ") + assertions + " assertions (Kotlin/JVM hot reload" +
                    (live.Count == 0 ? ", live parts skipped" : ", live: " + string.Join(", ", live)) + ").");
                return failures.Count == 0 ? 0 : 1;
            }
            if (args.Length == 1 && args[0] == "--codelens")
            {
                // Reports every failing CodeLens check, not only the first one.
                var failures = new System.Collections.Generic.List<string>();
                CodeLensTests.Run((condition, message) => { assertions++; if (!condition) failures.Add(message); });
                CodeLensKindsTests.Run((condition, message) => { assertions++; if (!condition) failures.Add(message); }, root);
                foreach (var failure in failures) Console.WriteLine("FAIL: " + failure);
                Console.WriteLine((failures.Count == 0 ? "PASS: " : "FAIL: " + failures.Count + " of ") + assertions + " assertions (CodeLens).");
                return failures.Count == 0 ? 0 : 1;
            }
            DiagnosticTests.Run(root, Check);
            DiagnosticsTests.Run(Check);
            ErrorLensTests.Run(Check);
            EtwTests.Run(Check);
            ScrollBarMapTests.Run(Check);
            await InlayHintTests.RunAsync(Check);
            KotlinSystemPathTests.Run(Path.Combine(root, "system-path"), Check);
            ComposePreviewTests.Run(Check);
            ComposeLivePreviewTests.Run(Check);
            CodeLensTests.Run(Check);
            CodeLensKindsTests.Run(Check, root);
            StructureTests.Run(Check);
            EditorLanguageTests.Run(Check);
            ToolDiagnosticTests.Run(Check);
            FeatureMatrixTests.Run(Check);
            TestLensTests.RunRust(Path.Combine(root, "test-lens"), Check);
            TestLensTests.RunZig(Check);
            ZigTests.Run(root, Check);
            DartTests.Run(root, Check);
            FlutterPreviewTests.Run(root, Check);
            MiseTests.Run(root, Check);
            BazelTests.Run(Path.Combine(root, "bazel"), Check);
            HotReloadTests.Run(Path.Combine(root, "hot-reload"), Check);
            await ProjectFileTests.RunAsync(root, Check);
            ProjectTreeTests.Run(root, Check);
            KotlinTargetTests.Run(root, Check);
            LaunchTargetTests.Run(root, Check);
            ProjectSettingsTests.Run(root, Check);
            WorkspaceSettingsTests.Run(Path.Combine(root, "workspace-settings"), Check);
            TemplateTests.Run(Path.Combine(root, "templates"), Check);
            await GradleModelTests.RunAsync(root, Check);
            await MultiplatformTests.RunAsync(root, Check);
            if (args.Length == 1 && args[0] == "--target-features")
            {
                Console.WriteLine("PASS: " + assertions + " assertions (target workflows, braces, settings and diagnostics).");
                return 0;
            }
            CompletionTests.Run(Check);
            await SemanticCompletionTests.RunAsync(Check);
            JvmLaunchTests.Run(Check);
            JavaLanguageServerTests.Run(root, Check);
            LspCompatibilityTests.Run(Check);
            await LspCompatibilityTests.RunStreamAsync(Check);
            await DapSourceTests.RunAsync(Check);
            await ServerLifecycleTests.RunAsync(Check);
            Check(GradleCommand.ParseTasks(":app:build clean").SequenceEqual(new[] { ":app:build", "clean" }), "Task selection");
            foreach (var value in new[] { "", "build & whoami", "build\n", "--daemon", "build|dir", "%TEMP%", "build\"", "build;exit" })
                Reject(() => GradleCommand.ParseTasks(value));
            File.WriteAllText(Path.Combine(root, "gradlew.bat"), "@echo off\r\necho ARGS:%*\r\necho compiler-error 1>&2\r\nexit /b 7\r\n");
            var command = GradleCommand.Create(root, "", new[] { ":app:build" });
            Check(command.WorkingDirectory == root, "Working directory");
            Check(command.EnvironmentVariables["JAVA_HOME"] == Environment.GetEnvironmentVariable("JAVA_HOME"), "Inherited JAVA_HOME");
            var lines = new ConcurrentQueue<string>();
            var code = await OwnedProcess.RunAsync(command, lines.Enqueue, CancellationToken.None);
            Check(code == 7, "Preserve nonzero exit code");
            Check(lines.Any(x => x.Contains("--console=plain --no-daemon :app:build")), "Wrapper receives task and fixed options");
            Check(lines.Any(x => x.Trim() == "compiler-error"), "stderr drained");
            File.WriteAllText(Path.Combine(root, "gradlew.bat"), "@echo off\r\necho e: src/Main.kt:4:13 Missing reference\r\necho w: src/Main.kt:2:1 Deprecated 1>&2\r\nexit /b 1\r\n");
            var buildDiagnostics = new BuildDiagnostics(root);
            var failedBuild = await OwnedProcess.RunAsync(command, buildDiagnostics.Accept, CancellationToken.None);
            var published = buildDiagnostics.Complete(failedBuild);
            Check(published.Length == 2 && published.Any(x => x.Severity == BuildDiagnosticSeverity.Error && x.Line == 3 && x.Column == 12) &&
                published.Any(x => x.Severity == BuildDiagnosticSeverity.Warning), "Wrapper stdout and stderr flow into build diagnostics");
            var batch = Path.Combine(root, "server.cmd");
            File.WriteAllText(batch, "@echo off\r\necho server-ready\r\n");
            var server = ProcessCommands.LanguageServer(batch, "--stdio", root);
            Check(await OwnedProcess.RunAsync(server, lines.Enqueue, CancellationToken.None) == 0, "Batch server launcher with spaces");
            Reject(() => ProcessCommands.LanguageServer(batch, "--stdio & whoami", root));

            File.WriteAllText(Path.Combine(root, "gradlew.bat"), "@echo off\r\necho started\r\nping -n 30 127.0.0.1 >nul\r\necho late>late.txt\r\n");
            using (var cancellation = new CancellationTokenSource())
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    await OwnedProcess.RunAsync(GradleCommand.Create(root, "", new[] { "build" }),
                        line => { if (line == "started") cancellation.CancelAfter(150); }, cancellation.Token);
                    throw new Exception("Expected cancellation");
                }
                catch (OperationCanceledException) { Check(watch.Elapsed < TimeSpan.FromSeconds(8), "Cancellation must finish promptly"); }
                Check(!File.Exists(Path.Combine(root, "late.txt")), "Canceled wrapper must not continue");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try { await OwnedProcess.RunAsync(command, _ => { }, cancellation.Token); throw new Exception("Expected pre-cancel"); }
                catch (OperationCanceledException) { assertions++; }
            }
            Console.WriteLine("PASS: " + assertions + " assertions (diagnostics, arguments, wrapper exit/I/O, launcher, cancellation).");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally
        {
            // This directory is created exclusively by this test run.
            // A Flutter app that just exited can hold its DLLs for a moment; retry, then leave the folder to the OS temp cleanup.
            for (var attempt = 0; Directory.Exists(root) && attempt < 10; attempt++)
            {
                try { Directory.Delete(root, true); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { if (attempt == 9) Console.WriteLine("WARN: could not delete " + root + ": " + e.Message); else Thread.Sleep(2000); }
            }
        }
    }
}
