using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vistudio.Core;
using Vistudio.Core.Dart;
using Vistudio.Dart;

/// <summary>
/// Live Dart and Flutter checks against a real SDK (VISTUDIO_FLUTTER_SDK, Tools &gt; Options &gt; Dart, or PATH), on copies of
/// samples/dart-console and samples/flutter-app: pub get, dart run, dart analyze, the analysis server over LSP, unit,
/// widget and integration tests, the Dart and Flutter debug adapters (breakpoints, stack, variables, evaluation, test
/// debugging, hot reload during a debug session) and flutter run --machine with hot reload and hot restart.
/// </summary>
internal static partial class DartTests
{
    private static readonly TimeSpan FlutterBuild = TimeSpan.FromMinutes(12);

    internal static async Task<bool> RunLiveAsync(string root, Action<bool, string> check, string stages)
    {
        var settings = new DartSettings { FlutterSdkPath = Environment.GetEnvironmentVariable("VISTUDIO_FLUTTER_SDK") ?? DartSettings.LoadGlobal().FlutterSdkPath, DartSdkPath = DartSettings.LoadGlobal().DartSdkPath };
        var sdk = DartToolchain.Find(settings);
        if (sdk == null) { Console.WriteLine("SKIP: no Dart SDK (set VISTUDIO_FLUTTER_SDK) - live Dart checks."); return false; }
        bool Stage(string name) => stages == "all" || stages.Split(',').Contains(name);
        // .NET Framework writes Console.InputEncoding's preamble into every redirected stdin at Process.Start. Under a
        // UTF-8 console (chcp 65001) that is a BOM, which the analysis server rejects ("Invalid value in input: 239").
        // devenv has no console (ANSI code page, no preamble); the test console gets the same behavior here.
        if (Console.InputEncoding.GetPreamble().Length > 0) Console.InputEncoding = new UTF8Encoding(false);
        var version = await DartToolchain.ReadVersionAsync(sdk, CancellationToken.None);
        Console.WriteLine("Live Dart " + version + " (" + sdk.Dart + "), Flutter " + (sdk.FlutterRoot == null ? "none" : DartToolchain.ReadFlutterVersion(sdk.FlutterRoot) + " (" + sdk.FlutterRoot + ")"));
        check(version == DartToolchain.ReadDartVersion(sdk), "dart --version matches the SDK version file: " + version);

        var console = Path.Combine(root, "dart live", "dart-console");
        CopySample("dart-console", console);
        var tool = DartProject.Find(console)!;
        check(tool != null && !tool.IsFlutter && tool.Name == "dart_console", "Console sample is a Dart package");
        check(await RunToolAsync(DartCommandFactory.PubGet(sdk, tool)) == 0, "dart pub get");

        if (Stage("run")) await DartRunAsync(sdk, tool, check);
        if (Stage("analyze")) await AnalyzeAsync(sdk, tool, check);
        if (Stage("tests")) await DartTestsAsync(sdk, tool, check);
        if (Stage("lsp")) await LanguageServerAsync(sdk, tool, check);
        if (Stage("debug")) await DartDebugAsync(sdk, tool, check);
        if (Stage("debugtest")) await DartTestDebugAsync(sdk, tool, check);

        if (sdk.FlutterRoot == null) { Console.WriteLine("SKIP: no Flutter SDK - live Flutter checks."); return true; }
        var appFolder = Path.Combine(root, "dart live", "flutter-app");
        CopySample("flutter-app", appFolder);
        // A print in build() shows in the app's output which title the running code has: proof that a hot reload took effect.
        var main = Path.Combine(appFolder, "lib", "main.dart");
        File.WriteAllText(main, File.ReadAllText(main).Replace("    return Scaffold(", "    print('build title=${widget.title}');\n    return Scaffold("));
        var app = DartProject.Find(appFolder)!;
        check(app.IsFlutter && app.HasIntegrationTests && app.DefaultEntryPoint() == main, "Flutter sample: Flutter package with integration tests and lib\\main.dart");
        check(await RunToolAsync(DartCommandFactory.PubGet(sdk, app)) == 0, "flutter pub get");
        if (Stage("widget")) await WidgetTestsAsync(sdk, app, check);
        if (Stage("flutterrun")) await FlutterRunAsync(sdk, app, main, check);
        if (Stage("flutterdebug")) await FlutterDebugAsync(sdk, app, main, check);
        if (Stage("integration")) await IntegrationTestsAsync(sdk, app, check);
        return true;
    }

    private static async Task DartRunAsync(DartSdk sdk, DartProject tool, Action<bool, string> check)
    {
        var output = new StringBuilder();
        var code = await RunToolAsync(DartCommandFactory.Run(sdk, tool, Path.Combine(tool.Root, "bin", "dart_console.dart"), ""), output);
        check(code == 0 && output.ToString().Contains("Total area: 9.14") && output.ToString().Contains("2 + 3 = 5"), "dart run of the console sample: " + output.ToString().Trim());

        var broken = Path.Combine(tool.Root, "bin", "broken.dart");
        File.WriteAllText(broken, "void main() {\n  print(missingName);\n}\n");
        output.Clear();
        var parser = new DartDiagnosticParser(tool.Root);
        code = await OwnedProcess.RunAsync(DartCommandFactory.Run(sdk, tool, broken, ""), line => { lock (parser) output.AppendLine(parser.Accept(line)); }, CancellationToken.None);
        var error = parser.Diagnostics.FirstOrDefault();
        check(code != 0 && error != null && error.File == broken && error.Line == 1 && error.Column == 8 && error.Message.Contains("missingName"),
            "dart run compile error parsed with its location: " + output.ToString().Trim());
        File.Delete(broken);
    }

    private static async Task AnalyzeAsync(DartSdk sdk, DartProject tool, Action<bool, string> check)
    {
        var file = Path.Combine(tool.Root, "lib", "extra.dart");
        File.WriteAllText(file, "int broken() {\n  return undefinedThing;\n}\n\nvoid unusedLocal() {\n  var x = 1;\n}\n");
        var output = new StringBuilder();
        var code = await RunToolAsync(DartCommandFactory.Analyze(sdk, tool.Root), output);
        var diagnostics = DartDiagnosticParser.ParseMachine(output.ToString());
        check(code != 0 && diagnostics.Any(d => d.Severity == DartDiagnosticSeverity.Error && d.File == file && d.Line == 1 && d.Column == 9 && d.Code == "undefined_identifier"),
            "dart analyze --format=machine: undefined identifier at extra.dart(2,10): " + output.ToString().Trim());
        check(diagnostics.Any(d => d.Severity == DartDiagnosticSeverity.Warning && d.File == file && d.Line == 5 && d.Code == "unused_local_variable"), "dart analyze warning parsed (unused_local_variable)");
        File.Delete(file);
    }

    private static async Task DartTestsAsync(DartSdk sdk, DartProject tool, Action<bool, string> check)
    {
        var run = await RunTestsAsync(DartCommandFactory.Test(sdk, tool, Array.Empty<string>(), null, ""), tool.Root);
        check(run.Passed == 3 && run.Failed == 0 && run.Success == true, "dart test --reporter json: 3 passed: " + run.Summary());
        check(run.Tests.Any(t => t.Name == "shapes total area" && t.File == Path.Combine(tool.Root, "test", "dart_console_test.dart") && t.Line == 9), "Test names and zero-based locations from the reporter: " + string.Join(", ", run.Tests.Select(t => t.Name + "@" + t.Line)));
        var single = await RunTestsAsync(DartCommandFactory.Test(sdk, tool, new[] { Path.Combine(tool.Root, "test", "dart_console_test.dart") }, "shapes total area", ""), tool.Root);
        check(single.Tests.Count == 1 && single.Passed == 1 && single.Tests[0].Name == "shapes total area", "One test by its full name (--plain-name): " + single.Summary());

        var failing = Path.Combine(tool.Root, "test", "failing_test.dart");
        File.WriteAllText(failing, "import 'package:test/test.dart';\n\nvoid main() {\n  test('wrong sum', () {\n    expect(1 + 1, 3);\n  });\n}\n");
        var failed = await RunTestsAsync(DartCommandFactory.Test(sdk, tool, new[] { failing }, null, ""), tool.Root);
        var diagnostic = failed.Diagnostics().FirstOrDefault();
        check(failed.Failed == 1 && diagnostic != null && diagnostic.File == failing && diagnostic.Line == 3 && diagnostic.Message.Contains("wrong sum") && diagnostic.Message.Contains("Expected: <3>"),
            "Failed test reported at its declaration: " + (diagnostic?.ToString() ?? failed.Summary()));
        File.WriteAllText(failing, "import 'package:test/test.dart';\n\nvoid main() {\n  test('does not compile', () {\n    expect(nope, 3);\n  });\n}\n");
        var broken = await RunTestsAsync(DartCommandFactory.Test(sdk, tool, new[] { failing }, null, ""), tool.Root);
        check(broken.Diagnostics().Any(d => d.File == failing && d.Line == 4 && d.Message.Contains("nope")), "Test file compile error in the Error List: " + string.Join(" | ", broken.LoadErrors));
        File.Delete(failing);
    }

    private static async Task WidgetTestsAsync(DartSdk sdk, DartProject app, Action<bool, string> check)
    {
        var run = await RunTestsAsync(DartCommandFactory.Test(sdk, app, Array.Empty<string>(), null, ""), app.Root);
        check(run.Passed == 1 && run.Failed == 0 && run.Tests.Any(t => t.Name == "Counter increments smoke test"), "flutter test --machine (widget test): " + run.Summary());
    }

    private static async Task IntegrationTestsAsync(DartSdk sdk, DartProject app, Action<bool, string> check)
    {
        var watch = Stopwatch.StartNew();
        var run = await RunTestsAsync(DartCommandFactory.IntegrationTest(sdk, app, Array.Empty<string>(), null, "windows", ""), app.Root, FlutterBuild);
        check(run.Passed == 1 && run.Failed == 0 && run.Tests.Any(t => t.Name == "counter app tapping the button increments the counter"),
            "flutter test integration_test -d windows: " + run.Summary() + " in " + watch.Elapsed.TotalSeconds.ToString("0") + " s" + (run.LoadErrors.Count > 0 ? ": " + run.LoadErrors[0] : ""));
    }

    private static async Task FlutterRunAsync(DartSdk sdk, DartProject app, string main, Action<bool, string> check)
    {
        var log = new ConcurrentQueue<string>();
        var start = DartCommandFactory.FlutterRun(sdk, app, main, "windows", "");
        var original = File.ReadAllText(main);
        using (var session = new FlutterAppSession(app, start, line => { log.Enqueue(line); }))
        {
            var watch = Stopwatch.StartNew();
            var started = await Task.WhenAny(session.Started, Task.Delay(FlutterBuild));
            check(started == session.Started && session.Started.Status == TaskStatus.RanToCompletion && session.AppId != null && session.DeviceId == "windows",
                "flutter run --machine started the app on Windows in " + watch.Elapsed.TotalSeconds.ToString("0") + " s" + (session.Started.IsFaulted ? ": " + session.Started.Exception!.InnerException!.Message + " | " + Tail(log) : ""));
            if (session.Started.Status != TaskStatus.RanToCompletion) { await session.StopAsync(); return; }
            check(await WaitForAsync(() => log.Any(l => l.Contains("build title=Flutter Demo Home Page")), TimeSpan.FromSeconds(30)), "The app printed its first build");

            File.WriteAllText(main, File.ReadAllText(main).Replace("Flutter Demo Home Page", "Hot Reloaded Page"));
            var reload = await session.RestartAsync(false, "save", CancellationToken.None);
            check(await WaitForAsync(() => log.Any(l => l.Contains("build title=Hot Reloaded Page")), TimeSpan.FromSeconds(30)),
                "Hot reload applied the edited title without restarting (" + reload + ")");
            var restart = await session.RestartAsync(true, "manual", CancellationToken.None);
            check(session.Started.Result.Length > 0 && !session.HasExited, "Hot restart succeeded (" + restart + ")");

            File.WriteAllText(main, File.ReadAllText(main).Replace("print('build title", "print(undefinedSymbol);\n    print('build title"));
            var rejected = false;
            try { await session.RestartAsync(false, "save", CancellationToken.None); }
            catch (InvalidOperationException) { rejected = true; }
            await Task.Delay(1000);
            check(session.Diagnostics.Any(d => d.File == main && d.Message.Contains("undefinedSymbol")) || rejected,
                "Hot reload with a compile error reports it: " + string.Join(" | ", session.Diagnostics.Select(d => d.ToString())));
            File.WriteAllText(main, File.ReadAllText(main).Replace("print(undefinedSymbol);\n    ", ""));

            await session.StopAsync();
            check(session.HasExited, "Stop ended flutter run and the app");
            File.WriteAllText(main, original); // later stages expect the sample's original title
        }
    }

    private static async Task DartDebugAsync(DartSdk sdk, DartProject tool, Action<bool, string> check)
    {
        var program = Path.Combine(tool.Root, "bin", "dart_console.dart");
        var line = LineOf(program, "final total = totalArea(shapes);");
        var launch = DartDebugLaunch.Create(tool, program, new DartSettings(), DartLaunchKind.Program, noDebug: false);
        using (var dap = new DapClient(DartCommandFactory.DebugAdapter(sdk, false, false, tool.Root)))
        {
            await dap.InitializeAsync("dart");
            var launched = dap.RequestAsync("launch", launch);
            await dap.WaitEventAsync("initialized", TimeSpan.FromSeconds(60));
            var breakpoints = await dap.RequestAsync("setBreakpoints", new JObject { ["source"] = new JObject { ["path"] = program }, ["breakpoints"] = new JArray(new JObject { ["line"] = line }) });
            check((bool?)breakpoints["success"] == true, "setBreakpoints accepted");
            await dap.RequestAsync("configurationDone", new JObject());
            check((bool?)(await launched)["success"] == true, "Dart launch request succeeded");
            var stopped = await dap.WaitStoppedAsync(TimeSpan.FromSeconds(90));
            check((string?)stopped["body"]!["reason"] == "breakpoint", "Stopped at the breakpoint: " + stopped.ToString(Formatting.None));
            var thread = (int)stopped["body"]!["threadId"]!;
            var stack = await dap.RequestAsync("stackTrace", new JObject { ["threadId"] = thread });
            var top = stack["body"]!["stackFrames"]![0]!;
            check(string.Equals((string?)top["source"]?["path"], program, StringComparison.OrdinalIgnoreCase) && (int?)top["line"] == line && ((string?)top["name"])!.Contains("main"),
                "Top frame is main at dart_console.dart:" + line + ": " + top.ToString(Formatting.None));
            var scopes = await dap.RequestAsync("scopes", new JObject { ["frameId"] = (int)top["id"]! });
            var locals = scopes["body"]!["scopes"]!.First(s => (string?)s["name"] == "Locals");
            var variables = await dap.RequestAsync("variables", new JObject { ["variablesReference"] = (int)locals["variablesReference"]! });
            check(variables["body"]!["variables"]!.Any(v => (string?)v["name"] == "shapes" && ((string?)v["value"] ?? "").Contains("List")), "Locals show shapes: " + variables["body"]!["variables"]!.ToString(Formatting.None));
            var evaluated = await dap.RequestAsync("evaluate", new JObject { ["expression"] = "shapes.length + 40", ["frameId"] = (int)top["id"]!, ["context"] = "watch" });
            check((string?)evaluated["body"]?["result"] == "42", "Watch expression shapes.length + 40 = " + evaluated["body"]?["result"]);
            // A request of Vistudio's own between the host's frames: answered to Vistudio, never seen by the host.
            var own = await dap.Connection.RequestAsync("threads", new JObject(), CancellationToken.None);
            check((bool?)own["success"] == true && own["body"]?["threads"]?.Any() == true && !dap.SawResponseTo(DartDapConnection.OwnSequenceStart + 1), "Injected request answered privately");
            // Stepping over a line with a call can stop again on the same line (the assignment), so step until it changes.
            JObject stepped = stopped;
            JToken after = top;
            for (var i = 0; i < 3 && (int?)after["line"] == line; i++)
            {
                await dap.RequestAsync("next", new JObject { ["threadId"] = thread });
                stepped = await dap.WaitStoppedAsync(TimeSpan.FromSeconds(30));
                after = (await dap.RequestAsync("stackTrace", new JObject { ["threadId"] = thread }))["body"]!["stackFrames"]![0]!;
            }
            check((string?)stepped["body"]!["reason"] == "step" && (int?)after["line"] == line + 1, "Step over reached line " + after["line"]);
            await dap.RequestAsync("continue", new JObject { ["threadId"] = thread });
            await dap.WaitEventAsync("terminated", TimeSpan.FromSeconds(60));
            check(dap.Output.Contains("Total area: 9.14"), "Program output arrived as DAP output events: " + dap.Output.Trim());
            Console.WriteLine("    Dart adapter events: " + dap.EventNames);
        }
    }

    private static async Task DartTestDebugAsync(DartSdk sdk, DartProject tool, Action<bool, string> check)
    {
        var file = Path.Combine(tool.Root, "test", "dart_console_test.dart");
        var line = LineOf(file, "expect(totalArea(");
        var launch = DartDebugLaunch.Create(tool, file, new DartSettings(), DartLaunchKind.Test, false, "shapes total area");
        using (var dap = new DapClient(DartCommandFactory.DebugAdapter(sdk, false, true, tool.Root)))
        {
            await dap.InitializeAsync("dart");
            var launched = dap.RequestAsync("launch", launch);
            await dap.WaitEventAsync("initialized", TimeSpan.FromSeconds(60));
            await dap.RequestAsync("setBreakpoints", new JObject { ["source"] = new JObject { ["path"] = file }, ["breakpoints"] = new JArray(new JObject { ["line"] = line }) });
            await dap.RequestAsync("configurationDone", new JObject());
            await launched;
            var stopped = await dap.WaitStoppedAsync(TimeSpan.FromSeconds(120));
            var thread = (int)stopped["body"]!["threadId"]!;
            var top = (await dap.RequestAsync("stackTrace", new JObject { ["threadId"] = thread }))["body"]!["stackFrames"]![0]!;
            check((string?)stopped["body"]!["reason"] == "breakpoint" && (int?)top["line"] == line, "Test debugging stopped in 'shapes total area' at line " + top["line"]);
            await dap.RequestAsync("continue", new JObject { ["threadId"] = thread });
            await dap.WaitEventAsync("terminated", TimeSpan.FromSeconds(60));
            var results = dap.Events.Where(e => (string?)e["event"] == "dart.testNotification").Select(e => e["body"]!).ToList();
            var done = results.Where(r => (string?)r["type"] == "testDone" && (bool?)r["hidden"] != true).ToList();
            check(done.Count == 1 && (string?)done[0]["result"] == "success", "Only the selected test ran and passed: " + done.Count + " test(s)");
        }
    }

    private static async Task FlutterDebugAsync(DartSdk sdk, DartProject app, string main, Action<bool, string> check)
    {
        var line = LineOf(main, "print('build title=");
        var launch = DartDebugLaunch.Create(app, main, new DartSettings { FlutterDevice = "windows" }, DartLaunchKind.Program, noDebug: false);
        using (var dap = new DapClient(DartCommandFactory.DebugAdapter(sdk, true, false, app.Root)))
        {
            await dap.InitializeAsync("dart");
            var launched = dap.RequestAsync("launch", launch, FlutterBuild);
            await dap.WaitEventAsync("initialized", FlutterBuild);
            await dap.RequestAsync("setBreakpoints", new JObject { ["source"] = new JObject { ["path"] = main }, ["breakpoints"] = new JArray(new JObject { ["line"] = line }) });
            await dap.RequestAsync("configurationDone", new JObject());
            var watch = Stopwatch.StartNew();
            var stopped = await dap.WaitStoppedAsync(FlutterBuild);
            var thread = (int)stopped["body"]!["threadId"]!;
            var top = (await dap.RequestAsync("stackTrace", new JObject { ["threadId"] = thread }))["body"]!["stackFrames"]![0]!;
            check((string?)stopped["body"]!["reason"] == "breakpoint" && (int?)top["line"] == line && ((string?)top["name"])!.Contains("build"),
                "Flutter debugging stopped in build() on Windows after " + watch.Elapsed.TotalSeconds.ToString("0") + " s: " + top.ToString(Formatting.None));
            var title = await dap.RequestAsync("evaluate", new JObject { ["expression"] = "widget.title", ["frameId"] = (int)top["id"]!, ["context"] = "watch" });
            check(((string?)title["body"]?["result"] ?? "").Contains("Flutter Demo Home Page"), "Evaluate widget.title in a Flutter frame: " + title["body"]?["result"]);
            await dap.RequestAsync("continue", new JObject { ["threadId"] = thread });
            check(await WaitForAsync(() => dap.Connection.AppStarted, TimeSpan.FromMinutes(2)), "flutter.appStarted event received");
            await launched;

            // Hot reload during the debug session: the edited title is rebuilt (the breakpoint in build() stops once more).
            File.WriteAllText(main, File.ReadAllText(main).Replace("Flutter Demo Home Page", "Reloaded While Debugging"));
            dap.ClearEvents("stopped");
            var reload = dap.Connection.RequestAsync("hotReload", new JObject { ["reason"] = "save" }, CancellationToken.None);
            var again = await dap.WaitStoppedAsync(TimeSpan.FromMinutes(2));
            var frame = (await dap.RequestAsync("stackTrace", new JObject { ["threadId"] = (int)again["body"]!["threadId"]! }))["body"]!["stackFrames"]![0]!;
            var reloadedTitle = await dap.RequestAsync("evaluate", new JObject { ["expression"] = "widget.title", ["frameId"] = (int)frame["id"]!, ["context"] = "watch" });
            check(((string?)reloadedTitle["body"]?["result"] ?? "").Contains("Reloaded While Debugging"), "After hot reload the debugger sees the new title: " + reloadedTitle["body"]?["result"]);
            await dap.RequestAsync("setBreakpoints", new JObject { ["source"] = new JObject { ["path"] = main }, ["breakpoints"] = new JArray() });
            await dap.RequestAsync("continue", new JObject { ["threadId"] = (int)again["body"]!["threadId"]! });
            var response = await reload;
            check((bool?)response["success"] == true, "hotReload request answered with success: " + response.ToString(Formatting.None));
            var restart = await dap.Connection.RequestAsync("hotRestart", new JObject { ["reason"] = "manual" }, CancellationToken.None);
            check((bool?)restart["success"] == true, "hotRestart during debugging succeeded");
            await dap.RequestAsync("disconnect", new JObject { ["terminateDebuggee"] = true });
            check(await WaitForAsync(() => dap.Connection.HasExited || dap.Events.Any(e => (string?)e["event"] == "terminated"), TimeSpan.FromSeconds(60)), "Disconnect ended the Flutter debug session");
            // Found in Visual Studio: the adapter exited after disconnect but the app it started kept running.
            check(await WaitForAsync(() => dap.Connection.HasExited, TimeSpan.FromSeconds(60)), "The Flutter debug adapter exited after disconnect");
            check(await WaitForAsync(() => !AppProcesses(app.Root).Any(), TimeSpan.FromSeconds(20)), "No app process is left after the debug session: " + string.Join(", ", AppProcesses(app.Root)));
        }
    }

    private static async Task LanguageServerAsync(DartSdk sdk, DartProject tool, Action<bool, string> check)
    {
        var bin = Path.Combine(tool.Root, "bin", "dart_console.dart");
        var lib = Path.Combine(tool.Root, "lib", "dart_console.dart");
        using (var lsp = new LspClient(DartCommandFactory.LanguageServer(sdk, tool.Root, "")))
        {
            var init = await lsp.RequestAsync("initialize", new JObject
            {
                ["processId"] = Process.GetCurrentProcess().Id,
                ["rootUri"] = new Uri(tool.Root).AbsoluteUri,
                ["capabilities"] = new JObject
                {
                    ["workspace"] = new JObject { ["configuration"] = false },
                    // inlayHint as DartLspCompatibility leaves it: no dynamic registration, so the server advertises it statically.
                    ["textDocument"] = new JObject { ["publishDiagnostics"] = new JObject(), ["completion"] = new JObject { ["completionItem"] = new JObject { ["snippetSupport"] = false } },
                        ["inlayHint"] = new JObject { ["dynamicRegistration"] = false } },
                },
                ["initializationOptions"] = new JObject { ["onlyAnalyzeProjectsWithOpenFiles"] = false, ["suggestFromUnimportedLibraries"] = true },
            });
            var capabilities = init["result"]?["capabilities"];
            check(capabilities?["referencesProvider"] != null && capabilities["completionProvider"] != null && capabilities["definitionProvider"] != null && capabilities["hoverProvider"] != null,
                "Analysis server capabilities: references, completion, definition, hover");
            await lsp.NotifyAsync("initialized", new JObject());
            var text = File.ReadAllText(bin).Replace("print('Total area: $total');", "print('Total area: $total');\n  print(missingThing);");
            await lsp.NotifyAsync("textDocument/didOpen", new JObject { ["textDocument"] = new JObject { ["uri"] = new Uri(bin).AbsoluteUri, ["languageId"] = "dart", ["version"] = 1, ["text"] = text } });
            var diagnostics = await lsp.WaitNotificationAsync("textDocument/publishDiagnostics", p => (string?)p["uri"] == new Uri(bin).AbsoluteUri && ((JArray)p["diagnostics"]!).Count > 0, TimeSpan.FromSeconds(90));
            var error = diagnostics["diagnostics"]!.FirstOrDefault(d => (string?)d["code"] == "undefined_identifier");
            var errorLine = text.Split('\n').ToList().FindIndex(l => l.Contains("missingThing"));
            check(error != null && (int?)error["severity"] == 1 && (int?)error["range"]!["start"]!["line"] == errorLine, "Unsaved error published as an LSP diagnostic at line " + (errorLine + 1) + ": " + error?.ToString(Formatting.None));

            var addLine = File.ReadAllLines(lib).ToList().FindIndex(l => l.StartsWith("int add("));
            var references = await lsp.RequestAsync("textDocument/references", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = new Uri(lib).AbsoluteUri },
                ["position"] = new JObject { ["line"] = addLine, ["character"] = 6 },
                ["context"] = new JObject { ["includeDeclaration"] = false },
            });
            var found = references["result"] as JArray;
            check(found != null && found.Count >= 1 && found.Any(r => ((string?)r["uri"] ?? "").EndsWith("bin/dart_console.dart")), "References to add() (CodeLens count): " + found?.Count);

            var completionLine = text.Split('\n').ToList().FindIndex(l => l.Contains("Rectangle(2, 3)"));
            var column = text.Split('\n')[completionLine].IndexOf("Rectangle(2, 3)") + "Rectangle(2, 3)".Length;
            var edited = text.Replace("Rectangle(2, 3)", "Rectangle(2, 3).");
            await lsp.NotifyAsync("textDocument/didChange", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = new Uri(bin).AbsoluteUri, ["version"] = 2 },
                ["contentChanges"] = new JArray(new JObject { ["text"] = edited }),
            });
            var completion = await lsp.RequestAsync("textDocument/completion", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = new Uri(bin).AbsoluteUri },
                ["position"] = new JObject { ["line"] = completionLine, ["character"] = column + 1 },
            });
            var items = (completion["result"] as JObject)?["items"] as JArray ?? completion["result"] as JArray;
            check(items != null && items.Any(i => (string?)i["label"] == "area") && items.Any(i => ((string?)i["label"] ?? "").StartsWith("describe")), "Member completion after Rectangle(2, 3). lists area and describe()");

            var definitionLine = text.Split('\n').ToList().FindIndex(l => l.Contains("totalArea(shapes)"));
            var definition = await lsp.RequestAsync("textDocument/definition", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = new Uri(bin).AbsoluteUri },
                ["position"] = new JObject { ["line"] = definitionLine, ["character"] = text.Split('\n')[definitionLine].IndexOf("totalArea") + 2 },
            });
            var target = (definition["result"] as JArray)?.FirstOrDefault() ?? definition["result"];
            check(((string?)target?["uri"] ?? (string?)target?["targetUri"] ?? "").EndsWith("lib/dart_console.dart"), "Go to definition of totalArea into lib/dart_console.dart");
            var hover = await lsp.RequestAsync("textDocument/hover", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = new Uri(bin).AbsoluteUri },
                ["position"] = new JObject { ["line"] = definitionLine, ["character"] = text.Split('\n')[definitionLine].IndexOf("totalArea") + 2 },
            });
            check(hover["result"]?["contents"]?.ToString().Contains("double totalArea") == true, "Hover shows the signature of totalArea");
            check(capabilities?["inlayHintProvider"] != null && capabilities["inlayHintProvider"]!.Type != JTokenType.Null,
                "Analysis server advertises inlay hints statically: " + capabilities?["inlayHintProvider"]?.ToString(Formatting.None));
            var lines = edited.Split('\n');
            var hints = await lsp.RequestAsync("textDocument/inlayHint", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = new Uri(bin).AbsoluteUri },
                ["range"] = new JObject { ["start"] = new JObject { ["line"] = 0, ["character"] = 0 }, ["end"] = new JObject { ["line"] = lines.Length, ["character"] = 0 } },
            });
            var hintList = hints["result"] as JArray;
            var labels = hintList?.Select(h => h["label"] is JArray parts ? string.Concat(parts.Select(p => (string?)p["value"])) : (string?)h["label"]).ToList();
            check(labels != null && labels.Count > 0 && hintList!.Any(h => (int?)h["kind"] == 1),
                "Inline type hints for bin/dart_console.dart with default settings: " + (labels == null ? "none" : string.Join(" | ", labels.Take(8))));
            await lsp.RequestAsync("shutdown", null);
            await lsp.NotifyAsync("exit", null);
        }
    }

    private static async Task<DartTestRun> RunTestsAsync(ProcessStartInfo start, string root, TimeSpan? timeout = null)
    {
        var run = new DartTestRun(root);
        using (var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5)))
            await OwnedProcess.RunAsync(start, line => { lock (run) run.Accept(line); }, cancellation.Token);
        return run;
    }

    private static async Task<int> RunToolAsync(ProcessStartInfo start, StringBuilder? output = null)
    {
        output ??= new StringBuilder();
        var code = await OwnedProcess.RunAsync(start, line => { lock (output) output.AppendLine(line); }, CancellationToken.None);
        if (code != 0) Console.WriteLine("  (" + start.Arguments + " exited with " + code + ")" + Environment.NewLine + output.ToString().Trim());
        return code;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > timeout) return false;
            await Task.Delay(200);
        }
        return true;
    }

    /// <summary>Running processes started from the package's build output (the Windows app).</summary>
    private static IEnumerable<string> AppProcesses(string root)
    {
        var build = Path.Combine(root, "build") + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            string? path = null;
            try { path = process.MainModule?.FileName; }
            catch (Exception) { }
            if (path != null && path.StartsWith(build, StringComparison.OrdinalIgnoreCase)) yield return process.ProcessName + " " + process.Id;
        }
    }

    private static string Tail(ConcurrentQueue<string> log) => string.Join(" | ", log.Reverse().Take(15).Reverse());

    private static int LineOf(string file, string text) => File.ReadAllLines(file).ToList().FindIndex(l => l.Contains(text)) + 1;

    private static void CopySample(string name, string target)
    {
        var source = Path.GetDirectoryName(FindRepositoryFile(Path.Combine("samples", name, "pubspec.yaml")))!;
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = file.Substring(source.Length + 1);
            if (relative.StartsWith(".dart_tool", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("build" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }

    /// <summary>A test-side DAP client playing the Visual Studio debugger: writes frames through <see cref="DartDapConnection.HostInput"/> in pieces and reads <see cref="DartDapConnection.HostOutput"/>.</summary>
    private sealed class DapClient : IDisposable
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> pending = new ConcurrentDictionary<int, TaskCompletionSource<JObject>>();
        private readonly List<JObject> events = new List<JObject>();
        private readonly HashSet<int> responses = new HashSet<int>();
        private readonly StringBuilder output = new StringBuilder();
        private readonly SemaphoreSlim eventSignal = new SemaphoreSlim(0);
        private int seq;

        internal DapClient(ProcessStartInfo start)
        {
            Connection = new DartDapConnection(start, line => Console.WriteLine("    [adapter] " + line));
            _ = Task.Run(ReadAsync);
        }

        internal DartDapConnection Connection { get; }
        internal string Output { get { lock (output) return output.ToString(); } }
        internal IReadOnlyList<JObject> Events { get { lock (events) return events.ToList(); } }
        internal bool SawResponseTo(int requestSeq) { lock (responses) return responses.Contains(requestSeq); }

        private async Task ReadAsync()
        {
            while (true)
            {
                var body = await DapFraming.ReadFrameAsync(Connection.HostOutput).ConfigureAwait(false);
                if (body == null) { foreach (var waiter in pending.Values) waiter.TrySetException(new IOException("The adapter closed the connection.")); return; }
                var message = JObject.Parse(Encoding.UTF8.GetString(body));
                var type = (string?)message["type"];
                if (type == "response")
                {
                    var id = (int)message["request_seq"]!;
                    lock (responses) responses.Add(id);
                    if (pending.TryRemove(id, out var waiter)) waiter.TrySetResult(message);
                }
                else if (type == "event")
                {
                    if ((string?)message["event"] == "output") lock (output) output.Append((string?)message["body"]?["output"]);
                    else lock (allNames) allNames.Add((string?)message["event"] + ((string?)message["event"] == "stopped" ? "(" + (string?)message["body"]?["reason"] + ")" : ""));
                    lock (events) events.Add(message);
                    eventSignal.Release();
                }
                else if (type == "request")
                {
                    // Reverse requests (runInTerminal) are not used with console=debugConsole; answer them as unsupported.
                    var reply = new JObject { ["seq"] = Interlocked.Increment(ref seq), ["type"] = "response", ["request_seq"] = message["seq"], ["success"] = false, ["command"] = message["command"] };
                    await WriteAsync(reply).ConfigureAwait(false);
                }
            }
        }

        private async Task WriteAsync(JObject message)
        {
            var frame = DapFraming.Frame(Encoding.UTF8.GetBytes(message.ToString(Formatting.None)));
            // Split every frame in two writes, as a host may do.
            var half = frame.Length / 2;
            await Connection.HostInput.WriteAsync(frame, 0, half).ConfigureAwait(false);
            await Connection.HostInput.WriteAsync(frame, half, frame.Length - half).ConfigureAwait(false);
        }

        internal async Task<JObject> RequestAsync(string command, JObject arguments, TimeSpan? timeout = null)
        {
            var id = Interlocked.Increment(ref seq);
            var waiter = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = waiter;
            await WriteAsync(new JObject { ["seq"] = id, ["type"] = "request", ["command"] = command, ["arguments"] = arguments }).ConfigureAwait(false);
            var done = await Task.WhenAny(waiter.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(60))).ConfigureAwait(false);
            if (done != waiter.Task) throw new TimeoutException("No DAP response to " + command);
            var response = await waiter.Task.ConfigureAwait(false);
            if ((bool?)response["success"] != true) Console.WriteLine("    DAP " + command + " failed: " + response.ToString(Formatting.None));
            return response;
        }

        internal async Task InitializeAsync(string adapter)
        {
            var response = await RequestAsync("initialize", new JObject
            {
                ["clientID"] = "vistudio-tests", ["adapterID"] = adapter, ["linesStartAt1"] = true, ["columnsStartAt1"] = true,
                ["pathFormat"] = "path", ["supportsVariableType"] = true, ["supportsRunInTerminalRequest"] = false,
            });
            if ((bool?)response["success"] != true) throw new InvalidOperationException("DAP initialize failed: " + response);
        }

        internal void ClearEvents(string name) { lock (events) events.RemoveAll(e => (string?)e["event"] == name); }

        /// <summary>
        /// The next "stopped" event that is not the adapter's pause-on-start ("entry"): the Dart adapter reports the
        /// isolate paused at start while it sets breakpoints, then resumes it by itself.
        /// </summary>
        internal async Task<JObject> WaitStoppedAsync(TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                var stopped = await WaitEventAsync("stopped", timeout - watch.Elapsed).ConfigureAwait(false);
                if ((string?)stopped["body"]?["reason"] != "entry") return stopped;
                lock (entries) entries.Add(stopped);
            }
        }

        private readonly List<JObject> entries = new List<JObject>();
        internal int EntryStops { get { lock (entries) return entries.Count; } }
        internal string EventNames { get { lock (allNames) return string.Join(",", allNames); } }
        private readonly List<string> allNames = new List<string>();

        internal async Task<JObject> WaitEventAsync(string name, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                lock (events)
                {
                    var found = events.FirstOrDefault(e => (string?)e["event"] == name);
                    if (found != null) { events.Remove(found); return found; }
                }
                var left = timeout - watch.Elapsed;
                if (left <= TimeSpan.Zero) throw new TimeoutException("No DAP '" + name + "' event within " + timeout.TotalSeconds + " s. Output: " + Output.Trim());
                await eventSignal.WaitAsync(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            try { Connection.StopAsync().Wait(TimeSpan.FromSeconds(30)); } catch (Exception) { }
            Connection.Dispose();
        }
    }

    /// <summary>A test-side LSP client: JSON-RPC over the analysis server's stdio, answering server requests with defaults.</summary>
    private sealed class LspClient : IDisposable
    {
        private readonly OwnedProcess process;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> pending = new ConcurrentDictionary<int, TaskCompletionSource<JObject>>();
        private readonly List<JObject> notifications = new List<JObject>();
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private int id;

        internal LspClient(ProcessStartInfo start)
        {
            process = new OwnedProcess(start);
            _ = Task.Run(ReadAsync);
            _ = Task.Run(() => OwnedProcess.PumpAsync(process.Error, line => Console.WriteLine("    [analysis server] " + line)));
        }

        private async Task ReadAsync()
        {
            while (true)
            {
                byte[]? body;
                try { body = await DapFraming.ReadFrameAsync(process.Output).ConfigureAwait(false); }
                catch (IOException) { return; }
                if (body == null) return;
                var message = JObject.Parse(Encoding.UTF8.GetString(body));
                if (message["method"] != null && message["id"] != null)
                {
                    var method = (string?)message["method"];
                    JToken result = method == "workspace/configuration" ? new JArray(((JArray?)message["params"]?["items"] ?? new JArray()).Select(_ => (JToken)JValue.CreateNull())) : JValue.CreateNull();
                    await SendAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = message["id"], ["result"] = result }).ConfigureAwait(false);
                }
                else if (message["method"] != null) lock (notifications) notifications.Add(message);
                else if (message["id"]?.Type == JTokenType.Integer && pending.TryRemove((int)message["id"]!, out var waiter)) waiter.TrySetResult(message);
            }
        }

        private async Task SendAsync(JObject message)
        {
            var frame = DapFraming.Frame(Encoding.UTF8.GetBytes(message.ToString(Formatting.None)));
            await gate.WaitAsync().ConfigureAwait(false);
            try { await process.Input.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false); await process.Input.FlushAsync().ConfigureAwait(false); }
            finally { gate.Release(); }
        }

        internal async Task<JObject> RequestAsync(string method, JObject? parameters)
        {
            var key = Interlocked.Increment(ref id);
            var waiter = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[key] = waiter;
            var message = new JObject { ["jsonrpc"] = "2.0", ["id"] = key, ["method"] = method };
            if (parameters != null) message["params"] = parameters;
            await SendAsync(message).ConfigureAwait(false);
            if (await Task.WhenAny(waiter.Task, Task.Delay(TimeSpan.FromSeconds(90))).ConfigureAwait(false) != waiter.Task) throw new TimeoutException("No LSP response to " + method);
            return await waiter.Task.ConfigureAwait(false);
        }

        internal Task NotifyAsync(string method, JObject? parameters)
        {
            var message = new JObject { ["jsonrpc"] = "2.0", ["method"] = method };
            if (parameters != null) message["params"] = parameters;
            return SendAsync(message);
        }

        internal async Task<JObject> WaitNotificationAsync(string method, Func<JObject, bool> match, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < timeout)
            {
                lock (notifications)
                {
                    var found = notifications.FirstOrDefault(n => (string?)n["method"] == method && match((JObject)n["params"]!));
                    if (found != null) return (JObject)found["params"]!;
                }
                await Task.Delay(100).ConfigureAwait(false);
            }
            throw new TimeoutException("No " + method + " notification within " + timeout.TotalSeconds + " s.");
        }

        public void Dispose()
        {
            try { process.StopAsync().Wait(TimeSpan.FromSeconds(15)); } catch (Exception) { }
            if (process.Exit.IsCompleted) process.Dispose();
        }
    }
}
