using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Vistudio.Core;
using Vistudio.Core.Dart;
using Vistudio.Dart;

/// <summary>Dart and Flutter support: arguments, SDK resolution, projects, commands, parsers, scanner, launch arguments, settings, grammar, DAP framing.</summary>
internal static partial class DartTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        Arguments(check);
        Toolchain(Path.Combine(root, "dart toolchain"), check);
        Projects(Path.Combine(root, "dart projects"), check);
        Commands(Path.Combine(root, "dart commands"), check);
        Diagnostics(check);
        Daemon(check);
        TestEvents(check);
        Scanner(check);
        Launch(Path.Combine(root, "dart launch"), check);
        Settings(Path.Combine(root, "dart settings"), check);
        Grammar(check);
        Framing(check).GetAwaiter().GetResult();
    }

    private static void Arguments(Action<bool, string> check)
    {
        check(DartArguments.Split("--flavor dev \"--dart-define=A=b c\"").SequenceEqual(new[] { "--flavor", "dev", "--dart-define=A=b c" }), "Dart arguments split with quotes");
        check(Throws(() => DartArguments.Split("a\nb")), "Dart arguments reject line breaks");
        check(Throws(() => DartArguments.Split("\"open")), "Dart arguments reject an unbalanced quote");
        check(DartArguments.ValidateDevice("windows") == "windows" && DartArguments.ValidateDevice(" emulator-5554 ") == "emulator-5554", "Device ids accepted");
        check(Throws(() => DartArguments.ValidateDevice("-d")) && Throws(() => DartArguments.ValidateDevice("win dows")) && Throws(() => DartArguments.ValidateDevice("a&b")), "Device ids with options, spaces or shell characters rejected");
        var line = DartArguments.BatchCommandLine(@"C:\sdk\flutter\bin\flutter.bat", new[] { "test", "--plain-name", "a & b | c > d", @"C:\dir\" });
        check(line == "/d /v:off /s /c \"\"C:\\sdk\\flutter\\bin\\flutter.bat\" \"test\" \"--plain-name\" \"a & b | c > d\" \"C:\\dir\\.\"\"", "flutter.bat command line quotes every argument: " + line);
        check(Throws(() => DartArguments.BatchCommandLine("flutter.bat", new[] { "%PATH%" })), "flutter.bat arguments reject % (cmd.exe expands it inside quotes)");
        check(Throws(() => DartArguments.BatchCommandLine("flutter.bat", new[] { "say \"hi\"" })), "flutter.bat arguments reject double quotes");
    }

    private static void Toolchain(string root, Action<bool, string> check)
    {
        var flutter = Path.Combine(root, "flutter sdk");
        Touch(Path.Combine(flutter, "bin", "flutter.bat"));
        Touch(Path.Combine(flutter, "bin", "dart.bat"));
        Touch(Path.Combine(flutter, "bin", "cache", "dart-sdk", "bin", "dart.exe"));
        File.WriteAllText(Path.Combine(flutter, "bin", "cache", "dart-sdk", "version"), "3.14.0\n");
        File.WriteAllText(Path.Combine(flutter, "bin", "cache", "flutter.version.json"), "{\"frameworkVersion\":\"3.50.0\",\"dartSdkVersion\":\"3.14.0\"}");
        var dartOnly = Path.Combine(root, "dart sdk");
        Touch(Path.Combine(dartOnly, "bin", "dart.exe"));

        var sdk = DartToolchain.Find(new DartSettings { FlutterSdkPath = flutter }, "");
        check(sdk != null && sdk.FlutterRoot == Path.GetFullPath(flutter) && sdk.Dart == Path.Combine(flutter, "bin", "cache", "dart-sdk", "bin", "dart.exe"), "Flutter SDK setting: Dart from the Flutter cache");
        check(sdk?.Flutter == Path.Combine(flutter, "bin", "flutter.bat"), "flutter.bat inside the Flutter SDK");
        check(DartToolchain.Find(new DartSettings { FlutterSdkPath = Path.Combine(flutter, "bin") }, "")?.FlutterRoot == Path.GetFullPath(flutter), "Flutter SDK setting accepts the bin folder");
        check(DartToolchain.Find(new DartSettings { FlutterSdkPath = flutter, DartSdkPath = dartOnly }, "")?.Dart == Path.Combine(dartOnly, "bin", "dart.exe"), "Dart SDK setting wins over the Flutter cache");
        check(DartToolchain.Find(new DartSettings(), Path.Combine(flutter, "bin"))?.FlutterRoot == Path.GetFullPath(flutter), "flutter.bat found on PATH");
        check(DartToolchain.Find(new DartSettings(), Path.Combine(dartOnly, "bin"))?.FlutterRoot == null, "Dart-only SDK on PATH has no Flutter");
        check(DartToolchain.Find(new DartSettings(), "") == null, "No SDK: null");
        check(DartToolchain.Find(new DartSettings { FlutterSdkPath = Path.Combine(root, "missing") }, Path.Combine(dartOnly, "bin")) == null ||
              DartToolchain.Find(new DartSettings { FlutterSdkPath = Path.Combine(root, "missing") }, Path.Combine(dartOnly, "bin"))!.FlutterRoot == null, "Missing configured Flutter SDK is not replaced by PATH");
        check(Throws<FileNotFoundException>(() => new DartSdk(Path.Combine(dartOnly, "bin", "dart.exe"), null).RequireFlutter()), "Flutter required but missing: clear error");
        check(DartToolchain.ReadDartVersion(sdk!) == "3.14.0" && DartToolchain.ReadFlutterVersion(flutter) == "3.50.0", "Versions from the SDK files");
        check(DartToolchain.ParseDartVersion("Dart SDK version: 3.14.0 (stable) (Tue Nov 4 2026) on \"windows_x64\"") == "3.14.0", "dart --version parsed");
    }

    private static void Projects(string root, Action<bool, string> check)
    {
        var flutterPubspec = "name: counter_app\ndescription: x\nenvironment:\n  sdk: ^3.14.0\ndependencies:\n  flutter:\n    sdk: flutter\ndev_dependencies:\n  flutter_test:\n    sdk: flutter\n";
        var app = DartProject.FromPubspec(root, flutterPubspec);
        check(app.IsFlutter && app.Name == "counter_app", "Flutter package detected from sdk: flutter");
        var plain = DartProject.FromPubspec(root, "name: 'tool'\nenvironment:\n  sdk: ^3.0.0\ndev_dependencies:\n  test: ^1.25.0\n# sdk: flutter\n");
        check(!plain.IsFlutter && plain.Name == "tool", "Dart package (a commented sdk: flutter does not count)");

        var package = Path.Combine(root, "pkg");
        File.WriteAllText(Touch(Path.Combine(package, "pubspec.yaml")), "name: pkg\n");
        Touch(Path.Combine(package, "bin", "pkg.dart"));
        Touch(Path.Combine(package, "test", "a_test.dart"));
        Touch(Path.Combine(package, "test", "helper.dart"));
        Touch(Path.Combine(package, "integration_test", "app_test.dart"));
        var found = DartProject.Find(Path.Combine(package, "test", "a_test.dart"));
        check(found != null && found.Root == Path.GetFullPath(package) && found.Name == "pkg", "pubspec.yaml found above a test file");
        check(found!.IsTestFile(Path.Combine(package, "test", "a_test.dart")) && !found.IsTestFile(Path.Combine(package, "test", "helper.dart")), "Test files are *_test.dart under test\\");
        check(found.IsIntegrationTest(Path.Combine(package, "integration_test", "app_test.dart")) && found.HasIntegrationTests, "Integration tests under integration_test\\");
        check(found.DefaultEntryPoint() == Path.Combine(package, "bin", "pkg.dart"), "Dart entry point bin\\<name>.dart");
        check(found.Relative(Path.Combine(package, "test", "a_test.dart")) == "test/a_test.dart", "Relative path with forward slashes");
        check(DartProject.Find(Path.Combine(package, "test"), Path.Combine(package, "test")) == null, "Search stops at the limit folder");
    }

    private static void Commands(string root, Action<bool, string> check)
    {
        var flutterRoot = Path.Combine(root, "flutter");
        Touch(Path.Combine(flutterRoot, "bin", "flutter.bat"));
        var dart = Touch(Path.Combine(flutterRoot, "bin", "cache", "dart-sdk", "bin", "dart.exe"));
        var sdk = new DartSdk(dart, flutterRoot);
        var dartPackage = DartProject.FromPubspec(Path.Combine(root, "tool"), "name: tool\n");
        var flutterPackage = DartProject.FromPubspec(Path.Combine(root, "app"), "name: app\ndependencies:\n  flutter:\n    sdk: flutter\n");

        var test = DartCommandFactory.Test(sdk, dartPackage, new[] { Path.Combine(root, "tool", "test", "a_test.dart") }, "group one", "--tags fast");
        check(test.FileName == dart && test.Arguments == "test --reporter json --plain-name \"group one\" --tags fast test/a_test.dart" && test.WorkingDirectory == dartPackage.Root,
            "dart test with the JSON reporter, name filter and file: " + test.Arguments);
        var flutterTest = DartCommandFactory.Test(sdk, flutterPackage, Array.Empty<string>(), null, "");
        check(flutterTest.FileName.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase) && flutterTest.Arguments.EndsWith("flutter.bat\" \"test\" \"--machine\"\"", StringComparison.Ordinal),
            "flutter test --machine through cmd.exe: " + flutterTest.Arguments);
        var integration = DartCommandFactory.IntegrationTest(sdk, flutterPackage, Array.Empty<string>(), null, "windows", "");
        check(integration.Arguments.Contains("\"test\" \"--machine\" \"-d\" \"windows\" \"integration_test\""), "flutter test integration_test -d windows: " + integration.Arguments);
        check(Throws<InvalidOperationException>(() => DartCommandFactory.IntegrationTest(sdk, dartPackage, Array.Empty<string>(), null, "windows", "")), "Integration tests need Flutter");
        var run = DartCommandFactory.FlutterRun(sdk, flutterPackage, Path.Combine(flutterPackage.Root, "lib", "main.dart"), "windows", "--flavor dev");
        check(run.Arguments.Contains("\"run\" \"--machine\" \"-d\" \"windows\" \"--target\" \"lib/main.dart\" \"--flavor\" \"dev\""), "flutter run --machine on a device: " + run.Arguments);
        check(run.EnvironmentVariables["FLUTTER_ROOT"] == flutterRoot && run.EnvironmentVariables["NO_COLOR"] == "1", "Flutter environment: FLUTTER_ROOT, no color");
        var server = DartCommandFactory.LanguageServer(sdk, root, "");
        check(server.FileName == dart && server.Arguments == "language-server --protocol=lsp --client-id=vistudio --client-version=0.1.0", "dart language-server over stdio: " + server.Arguments);
        check(DartCommandFactory.DebugAdapter(sdk, false, true, root).Arguments == "debug_adapter --test", "dart debug_adapter --test");
        check(DartCommandFactory.DebugAdapter(sdk, true, false, root).Arguments.EndsWith("\"debug_adapter\"\"", StringComparison.Ordinal), "flutter debug_adapter");
        check(DartCommandFactory.Run(sdk, dartPackage, Path.Combine(dartPackage.Root, "bin", "tool.dart"), "one \"two three\"").Arguments == "run bin/tool.dart one \"two three\"", "dart run with program arguments");
        check(DartCommandFactory.Analyze(sdk, root).Arguments == "analyze --format=machine .", "dart analyze --format=machine");
        check(Throws(() => DartCommandFactory.FlutterCreate(sdk, Path.Combine(root, "My App!"), null)), "flutter create rejects invalid package names");
        check(DartCommandFactory.FlutterCreate(sdk, Path.Combine(root, "my-app"), null).Arguments.Contains("\"create\" \"--project-name\" \"my_app\""), "flutter create derives the package name");
    }

    private static void Diagnostics(Action<bool, string> check)
    {
        var machine = DartDiagnosticParser.ParseMachineLine(@"ERROR|COMPILE_TIME_ERROR|UNDEFINED_IDENTIFIER|C:\\work\\app\\lib\\main.dart|5|3|9|Undefined name 'undefined'.");
        check(machine != null && machine.Severity == DartDiagnosticSeverity.Error && machine.File == @"C:\work\app\lib\main.dart" && machine.Line == 4 && machine.Column == 2 &&
              machine.Length == 9 && machine.Code == "undefined_identifier" && machine.Message == "Undefined name 'undefined'.", "dart analyze machine line (escaped backslashes)");
        var pipes = DartDiagnosticParser.ParseMachineLine(@"INFO|LINT|PREFER_CONST|C:\\a.dart|1|1|1|Use a\|b here.");
        check(pipes?.Message == "Use a|b here." && pipes.Severity == DartDiagnosticSeverity.Info, "Escaped pipe inside the message");
        check(DartDiagnosticParser.ParseMachineLine("Analyzing app...") == null && DartDiagnosticParser.ParseMachineLine("3 issues found.") == null, "Other analyze lines ignored");

        var parser = new DartDiagnosticParser(@"C:\work\app");
        var shown = parser.Accept("lib/main.dart:12:5: Error: Undefined name 'foo'.");
        check(parser.Diagnostics.Count == 1 && parser.Diagnostics[0].File == @"C:\work\app\lib\main.dart" && parser.Diagnostics[0].Line == 11 && parser.Diagnostics[0].Column == 4,
            "Compiler error with a relative path");
        check(shown == @"C:\work\app\lib\main.dart(12,5): error: Undefined name 'foo'.", "Compiler line rewritten for Output pane navigation: " + shown);
        check(DartDiagnosticParser.ParseCompilerLine("file:///C:/work/app/lib/a.dart:3:1: Warning: Unused.", @"C:\x")?.File == @"C:\work\app\lib\a.dart", "file:/// URI paths");
        check(DartDiagnosticParser.ParseCompilerLine("C:/work/app/lib/a.dart:3:1: Error: Bad.", @"C:\x")?.File == @"C:\work\app\lib\a.dart", "Absolute forward-slash paths");
        check(DartDiagnosticParser.ParseCompilerLine("lib/a.dart:3:1: Context: Found this candidate.", @"C:\x") == null, "Context lines are not diagnostics");
        check(parser.Accept("Launching lib\\main.dart on Windows in debug mode...") == "Launching lib\\main.dart on Windows in debug mode..." && parser.Diagnostics.Count == 1, "Plain output passes through");
    }

    private static void Daemon(Action<bool, string> check)
    {
        var start = FlutterDaemonMessage.Parse("[{\"event\":\"app.start\",\"params\":{\"appId\":\"abc\",\"deviceId\":\"windows\",\"directory\":\"C:\\\\app\",\"supportsRestart\":true,\"launchMode\":\"run\"}}]");
        check(start?.Event == "app.start" && start.AppId == "abc" && !start.IsResponse, "app.start event");
        var ok = FlutterDaemonMessage.Parse("[{\"id\":3,\"result\":{\"code\":0,\"message\":\"Reloaded 1 of 700 libraries.\"}}]");
        check(ok != null && ok.IsResponse && ok.Id == 3 && ok.Succeeded(out var message) && message == "Reloaded 1 of 700 libraries.", "Hot reload success response");
        var failed = FlutterDaemonMessage.Parse("[{\"id\":4,\"result\":{\"code\":1,\"message\":\"Reload rejected: const class changed\"}}]");
        check(failed != null && !failed.Succeeded(out var reason) && reason.Contains("rejected"), "Hot reload failure response");
        var error = FlutterDaemonMessage.Parse("[{\"id\":5,\"error\":\"app 'x' not found\"}]");
        check(error != null && !error.Succeeded(out var text) && text == "app 'x' not found", "Error response");
        check(FlutterDaemonMessage.Parse("Building Windows application...") == null && FlutterDaemonMessage.Parse("[1,2]") == null, "Plain lines are not daemon messages");
        var restart = JArray.Parse(FlutterDaemon.Restart(7, "abc", false, "save"))[0];
        check((string?)restart["method"] == "app.restart" && (int?)restart["id"] == 7 && (bool?)restart["params"]!["fullRestart"] == false && (string?)restart["params"]!["reason"] == "save", "app.restart request");
        var devices = FlutterDaemon.ParseDevices("[{\"name\":\"Windows\",\"id\":\"windows\",\"isSupported\":true,\"targetPlatform\":\"windows-x64\"},{\"name\":\"Chrome\",\"id\":\"chrome\",\"isSupported\":false,\"targetPlatform\":\"web-javascript\"}]");
        check(devices.Count == 2 && devices[0].Id == "windows" && devices[0].IsSupported && !devices[1].IsSupported && devices[0].ToString() == "Windows (windows, windows-x64)", "flutter devices --machine parsed");
    }

    private static void TestEvents(Action<bool, string> check)
    {
        var run = new DartTestRun(@"C:\work\pkg");
        var lines = new[]
        {
            "{\"protocolVersion\":\"0.1.1\",\"runnerVersion\":\"1.25.0\",\"pid\":1,\"type\":\"start\",\"time\":0}",
            "{\"suite\":{\"id\":0,\"platform\":\"vm\",\"path\":\"test/a_test.dart\"},\"type\":\"suite\",\"time\":0}",
            "{\"test\":{\"id\":1,\"name\":\"loading test/a_test.dart\",\"suiteID\":0,\"groupIDs\":[],\"metadata\":{},\"line\":null,\"column\":null,\"url\":null},\"type\":\"testStart\",\"time\":1}",
            "{\"testID\":1,\"result\":\"success\",\"skipped\":false,\"hidden\":true,\"type\":\"testDone\",\"time\":200}",
            "{\"group\":{\"id\":2,\"suiteID\":0,\"parentID\":null,\"name\":\"\",\"testCount\":2},\"type\":\"group\",\"time\":201}",
            "{\"test\":{\"id\":3,\"name\":\"math adds\",\"suiteID\":0,\"groupIDs\":[2],\"line\":5,\"column\":5,\"url\":\"file:///C:/work/pkg/test/a_test.dart\"},\"type\":\"testStart\",\"time\":202}",
            "{\"testID\":3,\"messageType\":\"print\",\"message\":\"hello\",\"type\":\"print\",\"time\":203}",
            "{\"testID\":3,\"result\":\"success\",\"skipped\":false,\"hidden\":false,\"type\":\"testDone\",\"time\":210}",
            "{\"test\":{\"id\":4,\"name\":\"math fails\",\"suiteID\":0,\"groupIDs\":[2],\"line\":9,\"column\":5,\"url\":\"file:///C:/work/pkg/test/a_test.dart\"},\"type\":\"testStart\",\"time\":211}",
            "{\"testID\":4,\"error\":\"Expected: <3>\\n  Actual: <2>\",\"stackTrace\":\"test/a_test.dart 10:7  main.<fn>\",\"isFailure\":true,\"type\":\"error\",\"time\":215}",
            "{\"testID\":4,\"result\":\"failure\",\"skipped\":false,\"hidden\":false,\"type\":\"testDone\",\"time\":216}",
            "{\"success\":false,\"type\":\"done\",\"time\":220}",
        };
        var shown = lines.Select(run.Accept).Where(s => s != null).ToList();
        check(run.Accept("[{\"event\":\"test.startedProcess\",\"params\":{\"vmServiceUri\":null}}]") == null, "flutter test daemon-style lines are hidden");
        check(run.Tests.Count == 2 && run.Passed == 1 && run.Failed == 1 && run.Success == false, "Test events: loading test hidden, one pass, one failure: " + run.Summary());
        check(shown.Contains("hello") && shown.Any(s => s!.StartsWith("PASS  math adds")) && shown.Any(s => s!.StartsWith("FAIL  math fails")) && shown.Any(s => s!.Contains("Expected: <3>")), "Readable output: " + string.Join(" | ", shown));
        var diagnostics = run.Diagnostics();
        check(diagnostics.Count == 1 && diagnostics[0].File == @"C:\work\pkg\test\a_test.dart" && diagnostics[0].Line == 8 && diagnostics[0].Message.StartsWith("Test failed: math fails: Expected: <3>"),
            "Failed test in the Error List at its declaration");

        var broken = new DartTestRun(@"C:\work\pkg");
        broken.Accept("{\"test\":{\"id\":1,\"name\":\"loading test/b_test.dart\",\"suiteID\":0,\"groupIDs\":[],\"line\":null,\"column\":null,\"url\":null},\"type\":\"testStart\",\"time\":1}");
        broken.Accept("{\"testID\":1,\"error\":\"Failed to load \\\"test/b_test.dart\\\":\\ntest/b_test.dart:4:3: Error: Undefined name 'nope'.\",\"stackTrace\":\"\",\"isFailure\":false,\"type\":\"error\",\"time\":5}");
        broken.Accept("{\"testID\":1,\"result\":\"error\",\"skipped\":false,\"hidden\":true,\"type\":\"testDone\",\"time\":6}");
        var load = broken.Diagnostics();
        check(broken.LoadErrors.Count == 1 && load.Any(d => d.File == @"C:\work\pkg\test\b_test.dart" && d.Line == 3 && d.Message == "Undefined name 'nope'."), "Suite compile error reported with its location");
    }

    private static void Scanner(Action<bool, string> check)
    {
        var text = string.Join("\n", new[]
        {
            "import 'package:test/test.dart';",
            "// void commented() {}",
            "/* class Hidden { void nope() {} } /* nested */ still comment */",
            "const banner = 'void inString() {}';",
            "final raw = r'class RawHidden {}';",
            "final multi = '''",
            "void insideTriple() {}",
            "''';",
            "abstract class Shape {",
            "  double get area;",
            "  String describe() => '$runtimeType ${area.toStringAsFixed(2)}';",
            "  Shape();",
            "}",
            "class Box extends Shape {",
            "  @override",
            "  double get area => 1;",
            "  Future<void> load(int id) async {",
            "    void local() {}",
            "    local();",
            "  }",
            "}",
            "Map<String, int> counts(List<String> words) {",
            "  return {};",
            "}",
            "void main() {",
            "  group('shapes', () {",
            "    test('box area', () {});",
            "    group(\"nested\", () {",
            "      testWidgets('builds', (tester) async {});",
            "    });",
            "  });",
            "  test('id $value', () {});",
            "  test('plain' ' joined', () {});",
            "}",
            "extension StringX on String { int twice() => length * 2; }",
            "enum Color { red, green }",
        });
        var declarations = DartSourceScanner.Scan(text);
        string Names(DartDeclarationKind kind) => string.Join(",", declarations.Where(d => d.Kind == kind).Select(d => d.QualifiedName));
        check(Names(DartDeclarationKind.Type) == "Shape,Box,StringX,Color", "Types (comments and strings ignored): " + Names(DartDeclarationKind.Type));
        check(Names(DartDeclarationKind.Function) == "Shape.describe,Box.load,counts,StringX.twice", "Functions and members, no local functions or constructors without body: " + Names(DartDeclarationKind.Function));
        check(Names(DartDeclarationKind.Main) == "main", "main found");
        check(Names(DartDeclarationKind.Group) == "shapes,shapes nested", "Groups with full names: " + Names(DartDeclarationKind.Group));
        check(Names(DartDeclarationKind.Test) == "shapes box area,shapes nested builds,id $value,plain joined", "Tests with full names: " + Names(DartDeclarationKind.Test));
        var widget = declarations.First(d => d.Name == "builds");
        check(widget.IsWidgetTest && !widget.IsDynamicName && declarations.First(d => d.Name == "id $value").IsDynamicName, "Widget tests and interpolated names flagged");
        var counts = declarations.First(d => d.Name == "counts");
        check(text.Substring(counts.NameStart, counts.NameEnd - counts.NameStart) == "counts" && counts.Signature == "Map<String, int> counts(List<String> words)", "Name offsets and signature: " + counts.Signature);
        var masked = DartSourceScanner.Mask(text);
        check(masked.Length == text.Length && masked.Split('\n').Length == text.Split('\n').Length && !masked.Contains("inString") && !masked.Contains("Hidden"), "Mask keeps offsets and lines");
    }

    private static void Launch(string root, Action<bool, string> check)
    {
        var settings = new DartSettings { FlutterDevice = "windows", FlutterRunArguments = "--flavor dev", ProgramArguments = "one two", TestArguments = "--tags fast" };
        var tool = DartProject.FromPubspec(Path.Combine(root, "tool"), "name: tool\n");
        var program = DartDebugLaunch.Create(tool, Path.Combine(tool.Root, "bin", "tool.dart"), settings, DartLaunchKind.Program, noDebug: false);
        check((string?)program["type"] == "dart" && (string?)program["request"] == "launch" && (string?)program["program"] == Path.Combine(tool.Root, "bin", "tool.dart") &&
              program["args"]!.Select(a => (string?)a).SequenceEqual(new[] { "one", "two" }) && program["toolArgs"] == null && (bool?)program["noDebug"] == false, "Dart program launch: " + program);
        check(DartDebugLaunch.Adapter(program) == (false, false, tool.Root), "Adapter for a Dart program");
        var app = DartProject.FromPubspec(Path.Combine(root, "app"), "name: app\ndependencies:\n  flutter:\n    sdk: flutter\n");
        var flutter = DartDebugLaunch.Create(app, Path.Combine(app.Root, "lib", "main.dart"), settings, DartLaunchKind.Program, noDebug: true);
        check((string?)flutter["type"] == "flutter" && flutter["toolArgs"]!.Select(a => (string?)a).SequenceEqual(new[] { "-d", "windows", "--flavor", "dev" }) && (bool?)flutter["noDebug"] == true, "Flutter app launch on the device");
        check(DartDebugLaunch.Adapter(flutter) == (true, false, app.Root), "Adapter for a Flutter app");
        var single = DartDebugLaunch.Create(tool, Path.Combine(tool.Root, "test", "a_test.dart"), settings, DartLaunchKind.Test, false, "shapes box area");
        check(single["args"]!.Select(a => (string?)a).SequenceEqual(new[] { "--plain-name", "shapes box area", "--tags", "fast" }) && DartDebugLaunch.Adapter(single).Test, "Single test debug launch");
        var integration = DartDebugLaunch.Create(app, Path.Combine(app.Root, "integration_test", "app_test.dart"), settings, DartLaunchKind.IntegrationTest, false);
        check(integration["toolArgs"]!.Select(a => (string?)a).SequenceEqual(new[] { "-d", "windows" }) && DartDebugLaunch.Adapter(integration) == (true, true, app.Root), "Integration test launch on the device");
        var merged = DartDebugLaunch.Merge((JObject)flutter.DeepClone(), JObject.Parse("{\"deviceId\":\"chrome\",\"args\":[\"x\"],\"cwd\":\"sub\"}"), app.Root);
        check(merged["toolArgs"]!.Select(a => (string?)a).SequenceEqual(new[] { "-d", "chrome", "--flavor", "dev" }) && (string?)merged["args"]![0] == "x" && (string?)merged["cwd"] == Path.Combine(app.Root, "sub"), "launch.vs.json overrides");
        check(Throws<InvalidOperationException>(() => DartDebugLaunch.Create(tool, "x_test.dart", settings, DartLaunchKind.IntegrationTest, false)), "Integration test launch needs Flutter");
    }

    private static void Settings(string root, Action<bool, string> check)
    {
        var file = Path.Combine(root, "dart-settings.xml");
        new DartSettings { FlutterSdkPath = @"C:\sdk\flutter", FlutterDevice = "chrome", HotReloadOnSave = false, CodeLens = false }.Save(file);
        var loaded = DartSettings.Load(file);
        check(loaded.FlutterSdkPath == @"C:\sdk\flutter" && loaded.FlutterDevice == "chrome" && !loaded.HotReloadOnSave && !loaded.CodeLens && loaded.LanguageServer, "Dart settings round trip");
        check(Throws(() => new DartSettings { FlutterDevice = "a b" }.Save(file)) && Throws(() => new DartSettings { TestArguments = "\"x" }.Save(file)), "Invalid settings rejected on save");
        check(new DartSettings().FlutterDevice == "windows" && new DartSettings().HotReloadOnSave, "Defaults: Windows device, hot reload on save");

        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, ".vs"));
        File.WriteAllText(Path.Combine(workspace, ".vs", "settings.json"),
            "{ // comment\n \"vistudio.dart.flutterSdkPath\": \"tools/flutter\", \"vistudio\": { \"dart\": { \"flutterDevice\": \"chrome\", \"hotReloadOnSave\": false } }, \"vistudio.dart.testArguments\": \"\\\"broken\", }");
        var target = new DartSettings();
        var warnings = WorkspaceSettings.ApplyTo(target, workspace);
        check(target.FlutterSdkPath == Path.Combine(workspace, "tools", "flutter") && target.FlutterDevice == "chrome" && !target.HotReloadOnSave && target.TestArguments == "",
            "vistudio.dart.* workspace overrides (relative SDK path resolved, invalid value kept global)");
        check(warnings.Count == 1 && warnings[0].StartsWith("vistudio.dart.testArguments"), "Invalid workspace value reported: " + string.Join("; ", warnings));
    }

    private static void Grammar(Action<bool, string> check)
    {
        var path = FindRepositoryFile(Path.Combine("src", "Vistudio.Vsix", "Grammars", "Dart.tmLanguage.json"));
        var grammar = JObject.Parse(File.ReadAllText(path));
        check((string?)grammar["scopeName"] == "source.dart" && grammar["fileTypes"]!.Any(t => (string?)t == "dart"), "Dart grammar scope and file type");
        var json = grammar.ToString();
        foreach (var scope in new[] { "keyword.control.dart", "storage.type.class.dart", "string.quoted.triple.dart", "meta.embedded.expression.dart", "comment.block.documentation.dart", "storage.type.annotation.dart", "constant.numeric.hex.dart" })
            check(json.Contains(scope), "Dart grammar has " + scope);
        var patterns = grammar.SelectTokens("$..match").Concat(grammar.SelectTokens("$..begin")).Concat(grammar.SelectTokens("$..end")).Select(t => (string?)t).Where(p => p != null).ToList();
        var invalid = patterns.Where(p => { try { _ = new System.Text.RegularExpressions.Regex(p!); return false; } catch (ArgumentException) { return true; } }).ToList();
        check(patterns.Count > 40 && invalid.Count == 0, "Dart grammar regexes compile: " + string.Join(" ", invalid));
        var interpolation = new System.Text.RegularExpressions.Regex((string)grammar["repository"]!["interpolation"]!["patterns"]![1]!["match"]!);
        check(interpolation.Match("'hi $name'").Groups[2].Value == "name", "Simple interpolation $name");
    }

    private static async Task Framing(Action<bool, string> check)
    {
        var body1 = Encoding.UTF8.GetBytes("{\"seq\":1,\"type\":\"request\",\"command\":\"initialize\"}");
        var body2 = Encoding.UTF8.GetBytes("{\"seq\":2,\"type\":\"request\",\"command\":\"launch\",\"arguments\":{\"text\":\"ü\"}}");
        var all = DapFraming.Frame(body1).Concat(DapFraming.Frame(body2)).ToArray();
        var forwarded = new List<byte[]>();
        var input = new DapInputStream(frame => { forwarded.Add(frame); return Task.CompletedTask; });
        // The host may write a frame in pieces: only whole frames go to the adapter.
        await input.WriteAsync(all, 0, 7);
        check(forwarded.Count == 0, "Partial header is held back");
        await input.WriteAsync(all, 7, DapFraming.Frame(body1).Length);
        check(forwarded.Count == 1 && forwarded[0].SequenceEqual(DapFraming.Frame(body1)), "First whole frame forwarded");
        await input.WriteAsync(all, 7 + DapFraming.Frame(body1).Length, all.Length - 7 - DapFraming.Frame(body1).Length);
        check(forwarded.Count == 2 && forwarded[1].SequenceEqual(DapFraming.Frame(body2)), "Second frame (UTF-8 body) forwarded whole");

        var output = new DapOutputStream();
        output.Enqueue(DapFraming.Frame(body1));
        output.Enqueue(DapFraming.Frame(body2));
        output.Complete();
        var first = await DapFraming.ReadFrameAsync(output);
        var second = await DapFraming.ReadFrameAsync(output);
        var end = await DapFraming.ReadFrameAsync(output);
        check(first != null && first.SequenceEqual(body1) && second != null && second.SequenceEqual(body2) && end == null, "Output stream yields frames, then end of stream");
    }

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, "");
        return path;
    }

    private static bool Throws(Action action) => Throws<ArgumentException>(action);

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    internal static string FindRepositoryFile(string relative)
    {
        for (var folder = AppDomain.CurrentDomain.BaseDirectory; folder != null; folder = Path.GetDirectoryName(folder))
        {
            var candidate = Path.Combine(folder, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Repository file not found above the test binary: " + relative);
    }
}
