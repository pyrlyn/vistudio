using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Vistudio.Core;
using Vistudio.Core.Structure;
using Vistudio.Core.Zig;

/// <summary>Zig support (T44-T48): settings, toolchain, diagnostics, build steps, source scanner, grammar, live zig.exe.</summary>
internal static class ZigTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        Arguments(check);
        Environment(check);
        Settings(root, check);
        Diagnostics(check);
        BuildSteps(check);
        Scanner(check);
        Grammar(check);
        Commands(check);
        LanguageServer(root, check);
    }

    private static void LanguageServer(string root, Action<bool, string> check)
    {
        var json = JObject.Parse(ZigLanguageServer.ConfigurationJson("C:\\zig \"x\"\\zig.exe", null));
        check((string?)json["zig_exe_path"] == "C:\\zig \"x\"\\zig.exe" && json["zig_lib_path"] == null && (bool?)json["enable_build_on_save"] == false &&
            (bool?)json["enable_snippets"] == false && (bool?)json["enable_argument_placeholders"] == false, "zls.json: toolchain, no build-on-save, no snippets");
        check(JObject.Parse(ZigLanguageServer.ConfigurationJson(null, "C:\\lib"))["zig_exe_path"] == null, "zls.json without zig.exe leaves ZLS's own lookup");
        var folder = Path.Combine(root, "zls");
        var server = Path.Combine(folder, "zls.exe");
        Directory.CreateDirectory(folder);
        File.WriteAllText(server, "");
        var start = ZigLanguageServer.Create(new ZigSettings { LanguageServerPath = server, LanguageServerArguments = "--enable-stderr-logs" }, "C:\\zig\\zig.exe", "C:\\lib dir", root, Path.Combine(folder, "config"));
        var config = Path.Combine(folder, "config", "zls.json");
        check(start.FileName == server && start.Arguments == "--config-path " + ZigArguments.Quote(config) + " --enable-stderr-logs" && start.WorkingDirectory == root,
            "ZLS start: " + start.Arguments);
        check(File.Exists(config) && (string?)JObject.Parse(File.ReadAllText(config))["zig_lib_path"] == "C:\\lib dir", "zls.json written next to the launch");
        check(Throws(() => ZigLanguageServer.Create(new ZigSettings { LanguageServerPath = "" }, null, null, root, folder)) &&
            Throws(() => ZigLanguageServer.Create(new ZigSettings { LanguageServerPath = "relative\\zls.exe" }, null, null, root, folder)), "Missing or relative ZLS path rejected");
    }

    private static void Arguments(Action<bool, string> check)
    {
        var split = ZigArguments.Split("-O ReleaseSafe \"-I C:\\a b\" -lc  ");
        check(split.SequenceEqual(new[] { "-O", "ReleaseSafe", "-I C:\\a b", "-lc" }), "Zig flags split with quotes");
        check(ZigArguments.Split("").Count == 0 && ZigArguments.Split(null).Count == 0, "Empty Zig flags");
        check(ZigArguments.Split("-Dname=\"\"").SequenceEqual(new[] { "-Dname=" }), "Empty quoted part");
        foreach (var bad in new[] { "\"open", "-O\nReleaseFast", "a\rb" })
            check(Throws(() => ZigArguments.Split(bad)), "Rejected Zig flags: " + bad.Replace("\n", "\\n").Replace("\r", "\\r"));
        check(ZigArguments.Quote("plain") == "plain", "Plain argument unquoted");
        check(ZigArguments.Quote("C:\\a b\\") == "\"C:\\a b\\\\\"", "Trailing backslash doubled inside quotes");
        check(ZigArguments.Quote("a\"b") == "\"a\\\"b\"", "Embedded quote escaped");
        check(ZigArguments.Quote("") == "\"\"", "Empty argument quoted");
        check(ZigArguments.ValidateStep("run") == "run" && Throws(() => ZigArguments.ValidateStep("-l")) && Throws(() => ZigArguments.ValidateStep("a b")), "Build step validation");
    }

    private const string ZonEnvironment = ".{\n    .zig_exe = \"C:\\\\zig\\\\zig.exe\",\n    .lib_dir = \"C:\\\\zig\\\\lib\",\n    .std_dir = \"C:\\\\zig\\\\lib\\\\std\",\n    .global_cache_dir = \"C:\\\\Users\\\\u\\\\AppData\\\\Local\\\\zig\",\n    .version = \"0.16.0\",\n    .target = \"x86_64-windows.win11_dt...win11_dt-gnu\",\n    .env = .{\n        .ZIG_LIB_DIR = null,\n        .LOCALAPPDATA = \"C:\\\\Users\\\\u\\\\AppData\\\\Local\",\n    },\n}\n";

    private static void Environment(Action<bool, string> check)
    {
        var zon = ZigEnvironmentInfo.Parse(ZonEnvironment);
        check(zon != null && zon.Version == "0.16.0" && zon.LibDirectory == "C:\\zig\\lib" && zon.StdDirectory == "C:\\zig\\lib\\std" && zon.Executable == "C:\\zig\\zig.exe", "zig env ZON (0.15+)");
        var json = ZigEnvironmentInfo.Parse("{\n \"zig_exe\": \"C:\\\\z\\\\zig.exe\",\n \"lib_dir\": \"C:\\\\z\\\\lib\",\n \"version\": \"0.13.0\"\n}");
        check(json != null && json.Version == "0.13.0" && json.StdDirectory == Path.Combine("C:\\z\\lib", "std"), "zig env JSON (before 0.15), std derived from lib");
        check(ZigEnvironmentInfo.Parse("error: something") == null, "zig env without version");
        // Zig 0.16 run from its own folder prints `lib\std`: resolved against the process working directory.
        var relative = ZigEnvironmentInfo.Parse(".{\n    .lib_dir = \"lib\",\n    .std_dir = \"lib\\\\std\",\n    .version = \"0.16.0\",\n}\n", "C:\\zig");
        check(relative != null && relative.LibDirectory == "C:\\zig\\lib" && relative.StdDirectory == "C:\\zig\\lib\\std", "Relative zig env paths made absolute");

        var folder = Path.Combine(Path.GetTempPath(), "Vistudio zig path " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var exe = Path.Combine(folder, "zig.exe");
            File.WriteAllText(exe, "");
            check(ZigToolchain.FindExecutable(null, "C:\\missing" + Path.PathSeparator + folder) == exe, "zig.exe found on PATH");
            check(ZigToolchain.FindExecutable(folder, null) == exe, "Configured zig folder");
            check(ZigToolchain.FindExecutable(exe, null) == exe, "Configured zig.exe");
            check(ZigToolchain.FindExecutable(Path.Combine(folder, "nope.exe"), folder) == null, "Missing configured zig.exe is not replaced by PATH");
            check(ZigToolchain.FindExecutable("", "") == null, "No zig");
            check(Throws(() => ZigToolchain.LibDirectory(new ZigSettings { LibDirectory = folder })), "Lib folder without std rejected");
            Directory.CreateDirectory(Path.Combine(folder, "std"));
            File.WriteAllText(Path.Combine(folder, "std", "std.zig"), "");
            check(ZigToolchain.LibDirectory(new ZigSettings { LibDirectory = folder }) == folder, "Lib folder with std accepted");
            check(ZigToolchain.LibDirectory(new ZigSettings()) == null, "Empty lib folder uses the bundled one");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void Settings(string root, Action<bool, string> check)
    {
        var path = Path.Combine(root, "zig", "zig-settings.xml");
        var settings = new ZigSettings
        {
            ZigPath = "C:\\zig\\zig.exe", LibDirectory = "C:\\zig\\lib", DefaultFlags = "-O ReleaseSafe", BuildFlags = "-Doptimize=ReleaseFast",
            BuildStep = "run", AstCheckOnSave = false, AstCheckWhileTyping = false, AstCheckDelayMilliseconds = 10,
            DiagnosticLevel = ZigDiagnosticLevel.Errors, LanguageServerPath = "C:\\zls\\zls.exe", LanguageServerArguments = "--enable-stderr-logs",
            LanguageServerDiagnostics = true, CodeLens = false,
        };
        settings.Save(path);
        var loaded = ZigSettings.Load(path);
        check(loaded.ZigPath == settings.ZigPath && loaded.LibDirectory == settings.LibDirectory && loaded.DefaultFlags == settings.DefaultFlags &&
            loaded.BuildFlags == settings.BuildFlags && loaded.BuildStep == "run" && !loaded.AstCheckOnSave && !loaded.AstCheckWhileTyping &&
            loaded.AstCheckDelayMilliseconds == 100 && loaded.DiagnosticLevel == ZigDiagnosticLevel.Errors && loaded.LanguageServerPath == settings.LanguageServerPath &&
            loaded.LanguageServerArguments == settings.LanguageServerArguments && loaded.LanguageServerDiagnostics && !loaded.CodeLens, "Zig settings round trip (delay clamped to 100 ms)");
        check(Throws(() => new ZigSettings { DefaultFlags = "\"x" }.Save(path)), "Invalid flags are not saved");
        check(ZigSettings.Load(Path.Combine(root, "zig", "missing.xml")).AstCheckOnSave, "Defaults without a settings file");
        File.WriteAllText(path, "<broken");
        check(ZigSettings.Load(path).DiagnosticLevel == ZigDiagnosticLevel.ErrorsAndNotes, "Corrupt settings fall back to defaults");
    }

    private static void Diagnostics(Action<bool, string> check)
    {
        var root = "C:\\work\\proj";
        // Real zig 0.16.0 `ast-check --color off` output on stdin.
        var stdin = ZigDiagnosticParser.Parse("<stdin>:2:9: error: unused local variable\n    var unused: u32 = 1;\n        ^~~~~~\n", root, "C:\\work\\proj\\src\\a.zig");
        check(stdin.Count == 1 && stdin[0].File == "C:\\work\\proj\\src\\a.zig" && stdin[0].Line == 1 && stdin[0].Column == 8 &&
            stdin[0].Severity == ZigDiagnosticSeverity.Error && stdin[0].Message == "unused local variable", "ast-check stdin error mapped to the document");

        // Real zig 0.16.0 `zig build` output: tree, located error, summaries, failed command.
        var build = "install\n+- install zinit\n   +- compile exe zinit Debug native 1 errors\nsrc\\main.zig:5:5: error: use of undeclared identifier 'foo'\n    foo();\n    ^~~\n" +
            "error: 1 compilation errors\nfailed command: \"C:\\\\zig\\\\zig.exe\" build-exe -ODebug\n\nBuild Summary: 0/3 steps succeeded (1 failed)\n" +
            "error: the following build command failed with exit code 1:\n.zig-cache\\o\\3ef1\\build.exe C:\\zig\\zig.exe\n";
        var parser = new ZigDiagnosticParser(root);
        var shown = new List<string>();
        foreach (var line in build.Split('\n')) shown.Add(parser.Accept(line));
        check(parser.Diagnostics.Count == 1 && parser.Diagnostics[0].File == "C:\\work\\proj\\src\\main.zig" && parser.Diagnostics[0].Line == 4 && parser.Diagnostics[0].Column == 4,
            "zig build: one located error, summaries ignored");
        check(shown.Contains("C:\\work\\proj\\src\\main.zig(5,5): error: use of undeclared identifier 'foo'"), "Output line rewritten to the clickable path(line,col) form");
        check(shown.Contains("    foo();") && shown.Contains("install"), "Other output lines unchanged");

        var notes = ZigDiagnosticParser.Parse(
            "C:\\work\\proj\\src\\main.zig:10:12: error: expected type 'u32', found '*const [1:0]u8'\n    const x: u32 = \"s\";\n           ^~~\n" +
            "src\\lib.zig:3:5: note: declared here\nnote: called from here\nerror: unable to open output directory 'zig-out': FileNotFound\n" +
            "D:\\x.zig:1:1: warning: deprecated\n", root);
        check(notes.Count == 3 && notes[0].Notes.Count == 2 && notes[0].Notes[0].File == "C:\\work\\proj\\src\\lib.zig" && notes[0].Notes[0].Line == 2 && !notes[0].Notes[1].HasLocation,
            "Notes attach to the preceding error");
        check(!notes[1].HasLocation && notes[1].Message.StartsWith("unable to open output directory") && notes[2].Severity == ZigDiagnosticSeverity.Warning,
            "Location-free errors and warnings kept");
        var errorsOnly = ZigDiagnosticParser.Flatten(notes, ZigDiagnosticLevel.Errors);
        check(errorsOnly.Count == 3 && errorsOnly[0].Message.Contains("(note: declared here; note: called from here)"), "Errors level folds notes into the message");
        var withNotes = ZigDiagnosticParser.Flatten(notes, ZigDiagnosticLevel.ErrorsAndNotes);
        check(withNotes.Count == 5 && withNotes[1].Severity == ZigDiagnosticSeverity.Note && withNotes[1].Line == 2 && withNotes[2].File == "C:\\work\\proj\\src\\main.zig" && withNotes[2].Line == 9,
            "ErrorsAndNotes level lists notes; a location-free note uses its error's location");
        check(ZigDiagnosticParser.Parse("referenced by:\n    main: src\\main.zig:10:5\n", root).Count == 0, "Reference trace lines are not diagnostics");
    }

    private const string BuildScript = "const std = @import(\"std\");\n\npub fn build(b: *std.Build) void {\n" +
        "    // b.step(\"commented\", \"not a step\");\n    const exe = b.addExecutable(.{\n        .name = \"hello\",\n        .root_module = b.createModule(.{ .root_source_file = b.path(\"src/main.zig\") }),\n    });\n" +
        "    const run_step = b.step(\"run\", \"Run the app\");\n    const docs = b.step(\"docs\", \"See http://example.com\");\n    const lib = b.addLibrary(.{ .linkage = .static, .name = \"util\", .root_module = mod });\n" +
        "    const tests = b.addTest(.{ .root_module = exe.root_module });\n    _ = .{ run_step, docs, lib, tests };\n}\n";

    private static void BuildSteps(Action<bool, string> check)
    {
        // Real `zig build -l` output of zig 0.16.0 (zig init).
        var listed = ZigBuildSteps.ParseList("  install (default)            Copy build artifacts to prefix path\r\n  uninstall                    Remove build artifacts from prefix path\r\n  run                          Run the app\r\n  test                         Run tests\r\n");
        check(listed.Select(s => s.Name).SequenceEqual(new[] { "install", "uninstall", "run", "test" }) && listed[0].IsDefault && !listed[2].IsDefault && listed[2].Description == "Run the app",
            "zig build -l steps parsed");
        check(ZigBuildSteps.ParseList("error: no build.zig file found\n").Count == 0, "No steps from an error");
        var scanned = ZigBuildSteps.Scan(BuildScript);
        check(scanned.Select(s => s.Name).SequenceEqual(new[] { "install", "uninstall", "run", "docs" }) && scanned.First(s => s.Name == "docs").Description == "See http://example.com",
            "Static build.zig scan: built-in steps, b.step declarations, comments ignored, URLs in strings kept");
        var artifacts = ZigBuildSteps.ScanArtifacts(BuildScript);
        check(artifacts.Count == 2 && artifacts[0].Kind == "Executable" && artifacts[0].Name == "hello" && artifacts[1].Kind == "Library" && artifacts[1].Name == "util",
            "Artifacts scanned (named tests are not listed without .name)");
    }

    private const string Source =
        "//! Module doc with fn notAFunction() in a comment.\n" +
        "const std = @import(\"std\");\n" +
        "const shapes = @import(\"shapes.zig\");\n" +
        "const text =\n    \\\\fn fake(a: u8) void {\n    \\\\}\n;\n" +
        "pub const Rect = struct {\n" +
        "    width: u32,\n" +
        "    callback: *const fn (u32) void,\n" +
        "    pub fn area(self: Rect) u32 { // area\n        return self.width;\n    }\n" +
        "    const Inner = enum(u8) { a, b };\n" +
        "    fn nested(self: *Rect, opts: struct { x: u8 = 0 }) !void {\n        _ = .{ self, opts };\n        try std.io.print(.{ .a = 1 }, \"{\");\n        if (true) {\n            while (false) {}\n        }\n    }\n" +
        "};\n" +
        "pub fn List(comptime T: type) type {\n    return struct {\n        items: []T,\n        pub fn len(self: @This()) usize { return self.items.len; }\n    };\n}\n" +
        "const Shape = union(enum) { circle: f32, square: f32 };\n" +
        "const Err = error{ Oops, Bad };\n" +
        "extern \"c\" fn printf(format: [*:0]const u8, ...) c_int;\n" +
        "export fn @\"quoted name\"() void {}\n" +
        "fn fails() error{Oops}!void {\n    return error.Oops;\n}\n" +
        "pub inline fn twice(\n    x: i32, // first\n    y: i32,\n) i32 {\n    return x * 2 + y;\n}\n" +
        "test \"rect area\" {\n    const r = Rect{ .width = 2, .callback = undefined };\n    _ = r.area();\n}\n" +
        "test {\n}\n";

    private static void Scanner(Action<bool, string> check)
    {
        var result = ZigSourceScanner.Scan(Source);
        var functions = result.Declarations.Where(d => d.Kind == ZigDeclarationKind.Function).ToList();
        var names = functions.Select(f => f.QualifiedName).ToArray();
        check(names.SequenceEqual(new[] { "Rect.area", "Rect.nested", "List", "List.len", "printf", "quoted name", "fails", "twice" }),
            "Zig functions with containers (comments, strings, fn types excluded): " + string.Join(", ", names));
        check(functions[0].Signature == "pub fn area(self: Rect) u32" && functions[0].IsPublic, "Method signature: " + functions[0].Signature);
        check(functions[1].Signature == "fn nested(self: *Rect, opts: struct { x: u8 = 0 }) !void" && !functions[1].IsPublic, "Signature with inline struct parameter: " + functions[1].Signature);
        check(functions[4].Signature == "extern \"c\" fn printf(format: [*:0]const u8, ...) c_int", "Extern declaration signature: " + functions[4].Signature);
        check(functions[6].Signature == "fn fails() error{Oops}!void", "Inline error set return type: " + functions[6].Signature);
        check(functions[7].Signature == "pub inline fn twice(x: i32, y: i32,) i32", "Multi-line signature joined, comments dropped: " + functions[7].Signature);
        var area = functions[0];
        check(Source.Substring(area.NameStart, area.NameEnd - area.NameStart) == "area" && Source.Substring(area.StartOffset, 3) == "pub", "Name and start offsets");
        check(Source.Split('\n')[area.Line].Contains("pub fn area"), "Function line");
        var quoted = functions[5];
        check(Source.Substring(quoted.NameStart, quoted.NameEnd - quoted.NameStart) == "@\"quoted name\"", "Quoted identifier span");

        var kinds = result.Declarations.Where(d => d.Kind != ZigDeclarationKind.Function).Select(d => d.Kind + ":" + d.QualifiedName).ToArray();
        check(kinds.SequenceEqual(new[] { "Constant:std", "Constant:shapes", "Constant:text", "Struct:Rect", "Enum:Rect.Inner", "Union:Shape", "ErrorSet:Err", "Test:rect area", "Test:" }),
            "Containers, constants and tests (locals excluded): " + string.Join(", ", kinds));
        check(result.Imports.SequenceEqual(new[] { "std", "shapes.zig" }), "@import targets");

        var blocks = result.Blocks;
        string HeaderOf(CodeBlock b) => Source.Substring(b.HeaderStart, b.HeaderEnd - b.HeaderStart);
        check(blocks.Any(b => b.Kind == CodeBlockKind.Type && HeaderOf(b) == "pub const Rect = struct"), "Struct block");
        check(blocks.Any(b => b.Kind == CodeBlockKind.Member && HeaderOf(b).StartsWith("pub fn area")), "Method block");
        check(blocks.Any(b => b.Kind == CodeBlockKind.Type && HeaderOf(b) == "const Inner = enum(u8)"), "Tagged enum block");
        check(blocks.Any(b => b.Kind == CodeBlockKind.Conditional && HeaderOf(b) == "if (true)") && blocks.Any(b => b.Kind == CodeBlockKind.Loop && HeaderOf(b) == "while (false)"), "Control-flow blocks");
        check(blocks.Any(b => b.Kind == CodeBlockKind.Member && HeaderOf(b) == "test \"rect area\""), "Test block");
        check(!blocks.Any(b => HeaderOf(b).Contains(".a = 1") || HeaderOf(b).StartsWith("_ =") || HeaderOf(b).StartsWith("const r")), "Literals are not blocks");
        check(blocks.Any(b => b.Kind == CodeBlockKind.Member && HeaderOf(b) == "fn fails() error{Oops}!void"), "Function with inline error set has one body block");
        var areaBlock = blocks.First(b => HeaderOf(b).StartsWith("pub fn area"));
        check(Source[areaBlock.OpenBrace] == '{' && Source[areaBlock.CloseBrace] == '}' && areaBlock.EndLine == areaBlock.HeaderLine + 2, "Block braces and lines");
        check(ZigSourceScanner.Scan("fn broken( {\n").Declarations.Count == 1 && ZigSourceScanner.Scan("}}}{").Blocks.Count == 0, "Unbalanced input does not throw");
    }

    private static void Grammar(Action<bool, string> check)
    {
        var path = FindRepositoryFile(Path.Combine("src", "Vistudio.Vsix", "Grammars", "Zig.tmLanguage.json"));
        var grammar = JObject.Parse(File.ReadAllText(path));
        check((string?)grammar["scopeName"] == "source.zig" && grammar["fileTypes"]!.Values<string>().SequenceEqual(new[] { "zig", "zon" }), "Zig grammar scope and file types");
        var text = grammar.ToString();
        foreach (var word in new[] { "pub", "fn", "const", "var", "struct", "enum", "union", "error", "try", "catch", "comptime", "defer", "errdefer", "orelse", "test" })
            check(System.Text.RegularExpressions.Regex.IsMatch(text, "[|(]" + word + "[|)]"), "Grammar keyword " + word);
        check(text.Contains("@import") && text.Contains("support.function.builtin.zig"), "Grammar builtins");
        foreach (var pattern in grammar.SelectTokens("$..match").Concat(grammar.SelectTokens("$..begin")).Concat(grammar.SelectTokens("$..end")))
            check(TryRegex((string)pattern!), "Grammar regex compiles: " + pattern);
        var keywords = new System.Text.RegularExpressions.Regex((string)grammar.SelectToken("$.repository.keywords.patterns[1].match")!);
        check(keywords.IsMatch("pub const") && !keywords.IsMatch("constant"), "Keyword word boundaries");
    }

    private static void Commands(Action<bool, string> check)
    {
        var zig = "C:\\zig dir\\zig.exe";
        var ast = ZigCommandFactory.AstCheck(zig, "C:\\p\\a.zon");
        check(ast.FileName == zig && ast.Arguments == "ast-check --color off --zon" && ast.RedirectStandardInput && ast.EnvironmentVariables["NO_COLOR"] == "1", "ast-check command (ZON, stdin)");
        var obj = ZigCommandFactory.BuildObject(zig, "C:\\p\\src\\main.zig", "C:\\p", "C:\\lib dir", "-O ReleaseSafe", out var objectFile);
        check(obj.Arguments == "build-obj --color off C:\\p\\src\\main.zig -femit-bin=C:\\p\\zig-out\\vistudio\\main.obj --zig-lib-dir \"C:\\lib dir\" -O ReleaseSafe" &&
            objectFile == "C:\\p\\zig-out\\vistudio\\main.obj" && obj.WorkingDirectory == "C:\\p", "build-obj command: " + obj.Arguments);
        var run = ZigCommandFactory.Run(zig, "C:\\p\\a b.zig", "C:\\p", null, "", "x \"y z\"");
        check(run.Arguments == "run --color off \"C:\\p\\a b.zig\" -- x \"y z\"", "run command: " + run.Arguments);
        var build = ZigCommandFactory.Build(zig, "C:\\p", "test", null, "-Doptimize=ReleaseFast");
        check(build.Arguments == "build test --color off -Doptimize=ReleaseFast", "build command: " + build.Arguments);
        check(ZigCommandFactory.Build(zig, "C:\\p", "", null, "").Arguments == "build --color off", "Default build step");
        check(Throws(() => ZigCommandFactory.Build(zig, "C:\\p", "--help", null, "")), "Option as step rejected");
        check(ZigCommandFactory.ListSteps(zig, "C:\\p", null).Arguments == "build -l --color off", "Step listing command");
        check(ZigCommandFactory.IsZigFile("a.ZIG") && ZigCommandFactory.IsZigFile("build.zig.zon") && !ZigCommandFactory.IsZigFile("a.kt"), "Zig file extensions");
        var sample = Path.GetDirectoryName(FindRepositoryFile(Path.Combine("samples", "zig-hello", "build.zig")))!;
        check(ZigCommandFactory.FindBuildRoot(Path.Combine(sample, "src", "main.zig")) == sample, "build.zig found above a source file");
    }

    /// <summary>Live checks against a real zig.exe (VISTUDIO_ZIG, PATH, or the mise install); skipped when none is found.</summary>
    internal static async Task<bool> RunLiveAsync(string root, Action<bool, string> check)
    {
        var zig = FindZig();
        if (zig == null) { Console.WriteLine("SKIP: zig.exe not found (set VISTUDIO_ZIG) - live Zig checks."); return false; }
        var info = await ZigToolchain.ReadEnvironmentAsync(zig, CancellationToken.None);
        check(info.Version.Length > 0 && File.Exists(Path.Combine(info.StdDirectory, "std.zig")), "Live zig env: " + info.Version + " " + info.StdDirectory);

        var file = Path.Combine(root, "zig live", "src", "bad.zig");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "pub fn main() void {\n    var unused: u32 = 1;\n}\n");
        var output = new StringBuilder();
        var astCheck = ZigCommandFactory.AstCheck(zig, file);
        var code = await RunWithInputAsync(astCheck, "pub fn main() void {\n    const y = 2\n    _ = y;\n}\n", output);
        var diagnostics = ZigDiagnosticParser.Parse(output.ToString(), astCheck.WorkingDirectory, file);
        check(code != 0 && diagnostics.Count == 1 && diagnostics[0].File == file && diagnostics[0].Line == 1 && diagnostics[0].Message.Contains("expected ';'"),
            "Live ast-check on unsaved text through stdin: " + output.ToString().Trim());
        output.Clear();
        code = await RunWithInputAsync(ZigCommandFactory.AstCheck(zig, file), "pub fn main() void {}\n", output);
        check(code == 0 && output.ToString().Trim().Length == 0, "Live ast-check of valid text is silent");

        var sample = Path.GetDirectoryName(FindRepositoryFile(Path.Combine("samples", "zig-hello", "build.zig")))!;
        var copy = Path.Combine(root, "zig live", "hello");
        CopyDirectory(sample, copy);
        output.Clear();
        code = await OwnedProcess.RunAsync(ZigCommandFactory.ListSteps(zig, copy, null), line => { lock (output) output.AppendLine(line); }, CancellationToken.None);
        var steps = ZigBuildSteps.ParseList(output.ToString());
        check(code == 0 && steps.Select(s => s.Name).SequenceEqual(new[] { "install", "uninstall", "run", "test" }) && steps[0].IsDefault, "Live zig build -l on the sample: " + output.ToString().Trim());

        output.Clear();
        var program = new List<string>();
        code = await OwnedProcess.RunAsync(ZigCommandFactory.Run(zig, Path.Combine(copy, "src", "main.zig"), copy, null, ""), line => { lock (program) program.Add(line); }, CancellationToken.None);
        check(code == 0 && program.Contains("sum 42"), "Live zig run: " + string.Join(" | ", program));

        Directory.CreateDirectory(Path.Combine(copy, "zig-out", "vistudio"));
        var objectStart = ZigCommandFactory.BuildObject(zig, Path.Combine(copy, "src", "shapes.zig"), copy, null, "", out var objectFile);
        code = await OwnedProcess.RunAsync(objectStart, _ => { }, CancellationToken.None);
        check(code == 0 && File.Exists(objectFile), "Live zig build-obj writes " + objectFile);

        File.WriteAllText(Path.Combine(copy, "src", "main.zig"), File.ReadAllText(Path.Combine(copy, "src", "main.zig")).Replace("return a + b;", "return a + missing;"));
        var parser = new ZigDiagnosticParser(copy);
        var shown = new List<string>();
        code = await OwnedProcess.RunAsync(ZigCommandFactory.Build(zig, copy, null, null, ""), line => { lock (parser) shown.Add(parser.Accept(line)); }, CancellationToken.None);
        // Zig occasionally adds a transient error of its own (seen once: "unable to load 'BuiltinFn.zig': Unexpected" in std).
        var error = parser.Diagnostics.FirstOrDefault(d => d.HasLocation && d.File == Path.Combine(copy, "src", "main.zig"));
        check(code != 0 && error != null && error.File == Path.Combine(copy, "src", "main.zig") && error.Line == 10 && error.Message.Contains("missing"),
            "Live zig build error located: " + string.Join(" | ", shown.Where(s => s.Contains("error"))));
        check(shown.Any(s => s.StartsWith(Path.Combine(copy, "src", "main.zig") + "(11,")), "Live build output rewritten for navigation");

        await RunZlsAsync(root, check, zig, info, sample);
        return true;
    }

    /// <summary>Live ZLS (VISTUDIO_ZLS or the mise install): the requests behind completion, signature help, navigation and CodeLens.</summary>
    private static async Task RunZlsAsync(string root, Action<bool, string> check, string zig, ZigEnvironmentInfo info, string sample)
    {
        var zls = FindTool("VISTUDIO_ZLS", "zls", "zls.exe");
        if (zls == null) { Console.WriteLine("SKIP: zls.exe not found (set VISTUDIO_ZLS) - live ZLS checks."); return; }
        var project = Path.Combine(root, "zig live", "zls");
        CopyDirectory(sample, project);
        var mainPath = Path.Combine(project, "src", "main.zig");
        var shapesPath = Path.Combine(project, "src", "shapes.zig");
        var main = File.ReadAllText(mainPath).Replace("\r\n", "\n");
        var shapes = File.ReadAllText(shapesPath).Replace("\r\n", "\n");
        var mainUri = new Uri(mainPath).AbsoluteUri;
        var shapesUri = new Uri(shapesPath).AbsoluteUri;
        var start = ZigLanguageServer.Create(new ZigSettings { LanguageServerPath = zls }, zig, null, project, Path.Combine(root, "zig live", "zls-config"));
        using (var server = new ZigLspProbe(start))
        {
            try
            {
                var initialize = await server.RequestAsync("initialize", new
                {
                    processId = System.Diagnostics.Process.GetCurrentProcess().Id,
                    rootUri = new Uri(project).AbsoluteUri,
                    capabilities = new
                    {
                        workspace = new { configuration = false },
                        textDocument = new
                        {
                            completion = new { completionItem = new { snippetSupport = false } },
                            signatureHelp = new { signatureInformation = new { parameterInformation = new { labelOffsetSupport = true } } },
                            documentSymbol = new { hierarchicalDocumentSymbolSupport = true },
                        },
                    },
                });
                var capabilities = initialize?["capabilities"];
                check(capabilities?["completionProvider"] != null && capabilities["definitionProvider"] != null && capabilities["referencesProvider"] != null &&
                    capabilities["signatureHelpProvider"] != null && capabilities["documentSymbolProvider"] != null,
                    "ZLS " + (string?)initialize?["serverInfo"]?["version"] + " advertises completion, definition, references, signature help, symbols");
                await server.NotifyAsync("initialized", new { });
                await server.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri = mainUri, languageId = "zig", version = 1, text = main } });
                await server.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri = shapesUri, languageId = "zig", version = 1, text = shapes } });

                string[] Labels(JToken? result) => ((result as JObject)?["items"] ?? result as JArray ?? new JArray()).Select(i => (string?)i["label"] ?? "").ToArray();

                var stdMembers = Labels(await server.RequestAsync("textDocument/completion", new { textDocument = new { uri = mainUri }, position = ZigLspProbe.Position(main, "std.debug", 4) }));
                check(stdMembers.Contains("debug") && stdMembers.Contains("mem") && stdMembers.Contains("testing"), "ZLS std member completion after 'std.': " + string.Join(",", stdMembers.Take(12)));

                var methods = Labels(await server.RequestAsync("textDocument/completion", new { textDocument = new { uri = mainUri }, position = ZigLspProbe.Position(main, "square.area", 7) }));
                check(methods.Contains("area") && methods.Contains("perimeter") && methods.Contains("width"), "ZLS member completion on a struct from another module (@import): " + string.Join(",", methods));

                var builtins = Labels(await server.RequestAsync("textDocument/completion", new { textDocument = new { uri = mainUri }, position = ZigLspProbe.Position(main, "@as", 1) }));
                check(builtins.Contains("@import") && builtins.Contains("@as"), "ZLS builtin completion after '@': " + builtins.Length + " items");

                var signature = await server.RequestAsync("textDocument/signatureHelp", new { textDocument = new { uri = mainUri }, position = ZigLspProbe.Position(main, "add(2, 40)", 4) });
                var label = (string?)signature?["signatures"]?[0]?["label"] ?? "";
                check(label.Contains("a: i32") && label.Contains("b: i32"), "ZLS signature help for add(: " + label);

                var definition = await server.RequestAsync("textDocument/definition", new { textDocument = new { uri = mainUri }, position = ZigLspProbe.Position(main, "add(2, 40)", 1) });
                var target = definition is JArray targets ? targets.FirstOrDefault() : definition;
                var definitionLine = (int?)(target?["range"] ?? target?["targetSelectionRange"])?["start"]?["line"];
                check(definitionLine == main.Split('\n').ToList().FindIndex(l => l.StartsWith("pub fn add")), "ZLS go to definition of add: line " + definitionLine);

                var crossFile = await server.RequestAsync("textDocument/definition", new { textDocument = new { uri = mainUri }, position = ZigLspProbe.Position(main, "square.area", 8) });
                var crossTarget = crossFile is JArray crossTargets ? crossTargets.FirstOrDefault() : crossFile;
                check(((string?)(crossTarget?["uri"] ?? crossTarget?["targetUri"]))?.EndsWith("shapes.zig", StringComparison.OrdinalIgnoreCase) == true, "ZLS go to definition across files (area in shapes.zig)");

                // The CodeLens query: references at the declaration name, declaration excluded.
                var functions = ZigSourceScanner.Functions(main);
                var add = functions.First(f => f.Name == "add");
                var (line, character) = CodeLensPosition(main, add);
                var references = await server.RequestAsync("textDocument/references", new { textDocument = new { uri = mainUri }, position = new { line, character }, context = new { includeDeclaration = false } });
                check(references is JArray refs && refs.Count == 2, "ZLS references for the add CodeLens (call in main and in the test): " + references?.ToString(Newtonsoft.Json.Formatting.None));

                // References of a declaration include importing files only after ZLS has loaded build.zig (its build
                // runner takes seconds); the extension refreshes the CodeLens counts on the same log message.
                var fromCall = await server.RequestAsync("textDocument/references", new { textDocument = new { uri = mainUri }, position = ZigLspProbe.Position(main, "square.area", 8), context = new { includeDeclaration = true } });
                check(fromCall is JArray callRefs && callRefs.Any(r => ((string?)r["uri"])?.EndsWith("shapes.zig", StringComparison.OrdinalIgnoreCase) == true) && callRefs.Count == 2,
                    "ZLS references from a call site span both files before build.zig is loaded");
                var loaded = false;
                for (var i = 0; i < 120 && !loaded; i++)
                {
                    lock (server.Log) loaded = server.Log.ToString().Contains("Loaded build file");
                    if (!loaded) await Task.Delay(500);
                }
                check(loaded && server.Log.ToString().Contains("Loaded config:") && server.Log.ToString().Contains("zls.json"), "ZLS loaded zls.json (--config-path) and build.zig");
                var area = ZigSourceScanner.Functions(shapes).First(f => f.Name == "area");
                var (areaLine, areaCharacter) = CodeLensPosition(shapes, area);
                JToken? areaReferences = null;
                var waited = System.Diagnostics.Stopwatch.StartNew();
                bool InMain(JToken? result) => result is JArray found && found.Any(r => ((string?)r["uri"])?.EndsWith("main.zig", StringComparison.OrdinalIgnoreCase) == true);
                while (waited.Elapsed < TimeSpan.FromSeconds(60))
                {
                    areaReferences = await server.RequestAsync("textDocument/references", new { textDocument = new { uri = shapesUri }, position = new { line = areaLine, character = areaCharacter }, context = new { includeDeclaration = false } });
                    if (InMain(areaReferences)) break;
                    await Task.Delay(500);
                }
                check(InMain(areaReferences), "ZLS references across files for Rect.area (" + waited.Elapsed.TotalSeconds.ToString("0.0") + " s after build.zig loaded): " + areaReferences?.ToString(Newtonsoft.Json.Formatting.None));
                Console.WriteLine("INFO: cross-file references of a declaration appeared " + waited.Elapsed.TotalSeconds.ToString("0.0") + " s after ZLS loaded build.zig.");

                var symbols = await server.RequestAsync("textDocument/documentSymbol", new { textDocument = new { uri = shapesUri } });
                var symbolText = symbols?.ToString(Newtonsoft.Json.Formatting.None) ?? "";
                check(symbolText.Contains("\"Rect\"") && symbolText.Contains("\"area\"") && symbolText.Contains("\"perimeter\""), "ZLS document symbols (outline) of shapes.zig");

                var hover = await server.RequestAsync("textDocument/hover", new { textDocument = new { uri = mainUri }, position = ZigLspProbe.Position(main, "add(2, 40)", 1) });
                check((hover?["contents"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "").Contains("fn add"), "ZLS hover on add");
                await server.RequestAsync("shutdown", null, TimeSpan.FromSeconds(10));
                await server.NotifyAsync("exit", new { });
            }
            catch (Exception e) { check(false, "Live ZLS: " + e.Message + "\n" + server.Log); }
        }
        _ = info;
        // ZLS's build runner child can hold files briefly after the job is terminated.
        for (var i = 0; i < 40 && Directory.Exists(project); i++)
        {
            try { Directory.Delete(project, true); }
            catch (IOException) { await Task.Delay(250); }
            catch (UnauthorizedAccessException) { await Task.Delay(250); }
        }
    }

    /// <summary>The position the references CodeLens queries: the last character of the function name.</summary>
    private static (int Line, int Character) CodeLensPosition(string text, ZigDeclaration function) =>
        Vistudio.Core.CodeLens.CodeLensElement.NamePosition(text, function.StartOffset, function.NameEnd);

    private static string? FindTool(string variable, string miseName, string exe)
    {
        var configured = System.Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
        var mise = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "mise", "installs", miseName);
        return Directory.Exists(mise) ? Directory.GetFiles(mise, exe, SearchOption.AllDirectories).OrderByDescending(p => p, StringComparer.Ordinal).FirstOrDefault() : null;
    }

    private static async Task<int> RunWithInputAsync(System.Diagnostics.ProcessStartInfo start, string input, StringBuilder output)
    {
        using (var process = new System.Diagnostics.Process { StartInfo = start })
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var bytes = new UTF8Encoding(false).GetBytes(input);
            await process.StandardInput.BaseStream.WriteAsync(bytes, 0, bytes.Length);
            process.StandardInput.Close();
            output.Append(await stdout).Append(await stderr);
            await Task.Run(() => process.WaitForExit());
            return process.ExitCode;
        }
    }

    private static string? FindZig()
    {
        var configured = System.Environment.GetEnvironmentVariable("VISTUDIO_ZIG");
        var found = ZigToolchain.FindExecutable(configured, System.Environment.GetEnvironmentVariable("PATH"));
        if (found != null) return found;
        var mise = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "mise", "installs", "zig");
        return Directory.Exists(mise) ? Directory.GetFiles(mise, "zig.exe", SearchOption.AllDirectories).OrderByDescending(p => p, StringComparer.Ordinal).FirstOrDefault() : null;
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = file.Substring(source.Length + 1);
            if (relative.StartsWith("zig-out", StringComparison.OrdinalIgnoreCase) || relative.StartsWith(".zig-cache", StringComparison.OrdinalIgnoreCase)) continue;
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    private static string FindRepositoryFile(string relative)
    {
        for (var folder = AppDomain.CurrentDomain.BaseDirectory; folder != null; folder = Path.GetDirectoryName(folder))
        {
            var candidate = Path.Combine(folder, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Repository file not found above the test binary: " + relative);
    }

    private static bool TryRegex(string pattern)
    {
        // TextMate uses Oniguruma; these patterns stay within the subset .NET also understands.
        try { _ = new System.Text.RegularExpressions.Regex(pattern); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
        catch (IOException) { return true; }
        catch (InvalidOperationException) { return true; }
    }
}
