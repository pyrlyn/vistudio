using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Vistudio.Core;
using Vistudio.Core.CodeLens;
using Vistudio.Core.Dart;
using Vistudio.Core.Languages;
using Vistudio.Core.Mise;
using Vistudio.Core.Rust;
using Vistudio.Core.Wasm;
using Vistudio.Core.Zig;

/// <summary>Rust (T61) and WebAssembly text (T62) editor support: settings, servers, WAT grammar, LSP-backed CodeLens.</summary>
internal static class RustWasmTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        Settings(root, check);
        WorkspaceOverrides(Path.Combine(root, "workspace"), check);
        Discovery(Path.Combine(root, "discovery"), check);
        ServerConfiguration(root, check);
        Cargo(Path.Combine(root, "cargo"), check);
        Grammar(check);
        RustSymbols(check);
        WatSymbols(check);
    }

    private static void Settings(string root, Action<bool, string> check)
    {
        var rustPath = Path.Combine(root, "rust-settings.xml");
        new RustSettings { LanguageServerPath = @"C:\ra\rust-analyzer.exe", LanguageServerArguments = "--log-file \"C:\\a b.log\"", InlayHints = false, CodeLens = false }.Save(rustPath);
        var rust = RustSettings.Load(rustPath);
        check(rust.LanguageServerPath == @"C:\ra\rust-analyzer.exe" && rust.LanguageServerArguments == "--log-file \"C:\\a b.log\"" && !rust.InlayHints && !rust.CodeLens,
            "Rust settings round trip");
        // Four rust-analyzer/CodeLens properties plus the five IEditorSettings ones (T74); the shared base adds none.
        var rustElements = XDocument.Load(rustPath).Root!.Elements().Select(e => e.Name.LocalName).ToArray();
        check(XDocument.Load(rustPath).Root!.Name.LocalName == "RustSettings" && rustElements.Length == 9
            && new[] { "RainbowBraces", "ScrollBarMode", "ScrollBarMapWidth", "ScrollBarMapPreview", "InlayHintsDisplay" }.All(rustElements.Contains),
            "Rust settings file holds exactly its own and the editor properties (the shared base adds none): " + string.Join(", ", rustElements));
        check(RustSettings.FilePath.EndsWith(@"Vistudio\rust-settings.xml", StringComparison.OrdinalIgnoreCase) && WasmSettings.FilePath.EndsWith(@"Vistudio\wasm-settings.xml", StringComparison.OrdinalIgnoreCase),
            "Rust and WebAssembly settings live in their own files");

        var wasmPath = Path.Combine(root, "wasm-settings.xml");
        new WasmSettings { LanguageServerPath = @"C:\wlt\wat_server.exe", WasmtimePath = @"C:\wt\wasmtime.exe", WasmToolsPath = @"C:\wt\wasm-tools.exe", InlayHints = false }.Save(wasmPath);
        var wasm = WasmSettings.Load(wasmPath);
        check(wasm.LanguageServerPath == @"C:\wlt\wat_server.exe" && wasm.WasmtimePath == @"C:\wt\wasmtime.exe" && wasm.WasmToolsPath == @"C:\wt\wasm-tools.exe" && !wasm.InlayHints && wasm.CodeLens,
            "WebAssembly settings round trip");
        check(Throws(() => new RustSettings { LanguageServerArguments = "\"open" }.Save(Path.Combine(root, "bad.xml"))) && !File.Exists(Path.Combine(root, "bad.xml")),
            "Unbalanced server arguments are rejected before anything is written");
        File.WriteAllText(Path.Combine(root, "broken.xml"), "<RustSettings><InlayHints>maybe</InlayHints>");
        check(RustSettings.Load(Path.Combine(root, "broken.xml")).InlayHints && RustSettings.Load(Path.Combine(root, "missing.xml")).CodeLens,
            "A broken or missing file gives the defaults");

        // The shared base keeps the existing formats: files written by the old per-language code still load.
        var zigPath = Path.Combine(root, "zig-settings.xml");
        File.WriteAllText(zigPath, "<?xml version=\"1.0\" encoding=\"utf-8\"?><ZigSettings xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\"><ZigPath>C:\\zig\\zig.exe</ZigPath><AstCheckDelayMilliseconds>50</AstCheckDelayMilliseconds><DiagnosticLevel>Errors</DiagnosticLevel></ZigSettings>");
        var zig = ZigSettings.Load(zigPath);
        check(zig.ZigPath == @"C:\zig\zig.exe" && zig.DiagnosticLevel == ZigDiagnosticLevel.Errors && zig.AstCheckDelayMilliseconds == 50, "An existing zig-settings.xml loads unchanged");
        zig.Save(zigPath);
        check(ZigSettings.Load(zigPath).AstCheckDelayMilliseconds == 100 && XDocument.Load(zigPath).Root!.Name.LocalName == "ZigSettings", "Zig save still raises a too small delay");
        var misePath = Path.Combine(root, "mise-settings.xml");
        new MiseSettings { CacheMinutes = 0, InfoBar = false }.Save(misePath);
        check(MiseSettings.Load(misePath).CacheMinutes == 1 && !MiseSettings.Load(misePath).InfoBar, "Mise save still raises the cache time to one minute");
        check(Throws(() => new DartSettings { FlutterDevice = "bad device!" }.Save(Path.Combine(root, "dart.xml"))), "Dart save still validates");
    }

    private static void WorkspaceOverrides(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(Path.Combine(root, ".vs"));
        File.WriteAllText(WorkspaceSettings.FilePath(root), @"{
  ""vistudio.rust.languageServerPath"": ""tools/rust-analyzer.exe"",
  ""vistudio"": { ""rust"": { ""inlayHints"": false }, ""wasm"": { ""languageServerPath"": ""${workspaceFolder}\\wlt\\wat_server.exe"", ""codeLens"": false, ""wasmtimePath"": ""bin/wasmtime.exe"" } },
  ""vistudio.dart.flutterDevice"": ""chrome"",
}");
        var rust = new RustSettings();
        var warnings = WorkspaceSettings.ApplyTo(rust, root);
        check(warnings.Count == 0 && rust.LanguageServerPath == Path.Combine(root, "tools", "rust-analyzer.exe") && !rust.InlayHints && rust.CodeLens,
            "vistudio.rust.* overrides; relative server paths resolve against the workspace: " + string.Join("; ", warnings));
        var wasm = new WasmSettings();
        WorkspaceSettings.ApplyTo(wasm, root);
        check(wasm.LanguageServerPath == Path.Combine(root, "wlt", "wat_server.exe") && wasm.WasmtimePath == Path.Combine(root, "bin", "wasmtime.exe") && !wasm.CodeLens && wasm.InlayHints,
            "vistudio.wasm.* overrides with ${workspaceFolder} and relative tool paths");
        check(WorkspaceSettings.Check(root).Count == 0, "Rust, WebAssembly and Dart sections are known sections: " + string.Join("; ", WorkspaceSettings.Check(root)));
        check(RustSettings.LoadForWorkspace(root).LanguageServerPath == rust.LanguageServerPath, "LoadForWorkspace applies the workspace file");
        check(WorkspaceSettings.ApplyTo(new MiseSettings(), root).Count == 0, "Settings without a workspace section ignore the file");

        File.WriteAllText(WorkspaceSettings.FilePath(root), "{ \"vistudio.rust.languageServerArguments\": \"\\\"open\", \"vistudio.wasm.inlayHints\": \"no\", \"vistudio.rust.noSuch\": 1 }");
        var guarded = new RustSettings { LanguageServerArguments = "--keep" };
        warnings = WorkspaceSettings.ApplyTo(guarded, root);
        check(guarded.LanguageServerArguments == "--keep" && warnings.Count == 2 && warnings.Any(w => w.StartsWith("vistudio.rust.noSuch: unknown setting", StringComparison.Ordinal)),
            "Invalid Rust overrides keep the global value and are reported: " + string.Join("; ", warnings));
        check(WorkspaceSettings.Check(root).Count == 3, "Check reports Rust and WebAssembly problems together");
    }

    private static void Discovery(string root, Action<bool, string> check)
    {
        var configured = Path.Combine(root, "configured", "rust-analyzer.exe");
        var onPath = Path.Combine(root, "path dir", "wat_server.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(configured)!);
        Directory.CreateDirectory(Path.GetDirectoryName(onPath)!);
        File.WriteAllText(configured, "");
        File.WriteAllText(onPath, "");
        check(RustLanguageServer.Find(new RustSettings { LanguageServerPath = configured }) == configured, "A configured rust-analyzer.exe is used");
        check(RustLanguageServer.Find(new RustSettings { LanguageServerPath = "\"" + Path.GetDirectoryName(configured) + "\"" }) == configured, "A configured folder finds rust-analyzer.exe in it");
        check(RustLanguageServer.Find(new RustSettings { LanguageServerPath = Path.Combine(root, "missing.exe") }) == null
            && RustLanguageServer.Find(new RustSettings { LanguageServerPath = "relative\\rust-analyzer.exe" }) == null,
            "A configured path that does not exist is not replaced by a PATH lookup");
        var previous = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(onPath) + ";" + previous);
            check(Same(WasmLanguageServer.Find(new WasmSettings()), onPath), "wat_server.exe is found on PATH");
            check(Same(LanguageServerLocator.Find("", "wat_server", new[] { Path.Combine(root, "configured") }), onPath), "PATH wins over the installer folders");
            check(Same(LanguageServerLocator.Find("", "rust-analyzer", new[] { Path.Combine(root, "configured") }), configured), "The installer folder (.cargo\\bin) is the fallback");
        }
        finally { Environment.SetEnvironmentVariable("PATH", previous); }
        var start = RustLanguageServer.Create(configured, new RustSettings { LanguageServerArguments = "--log-file \"C:\\a b.log\"" }, root);
        check(start.FileName == configured && start.Arguments == "--log-file \"C:\\a b.log\"" && start.WorkingDirectory == root && start.RedirectStandardInput,
            "rust-analyzer starts on stdio in the workspace: " + start.Arguments);
        check(WasmLanguageServer.Create(onPath, new WasmSettings(), root).Arguments == "", "wat_server starts without arguments by default");
        check(Throws(() => WasmLanguageServer.Create(onPath, new WasmSettings { LanguageServerArguments = "a\nb" }, root)), "Server arguments with line breaks are rejected");
    }

    private static void ServerConfiguration(string root, Action<bool, string> check)
    {
        var on = RustLanguageServer.Configuration(new RustSettings());
        check(new[] { "typeHints", "parameterHints", "chainingHints", "closingBraceHints" }.All(k => (bool?)on.SelectToken("inlayHints." + k + ".enable") == true),
            "rust-analyzer options: inlayHints.{typeHints,parameterHints,chainingHints,closingBraceHints}.enable = true: " + on.ToString(Newtonsoft.Json.Formatting.None));
        var off = RustLanguageServer.Configuration(new RustSettings { InlayHints = false });
        check(off.SelectTokens("inlayHints.*.enable").All(t => (bool)t! == false) && off.SelectTokens("inlayHints.*.enable").Count() == 4 && off.Properties().Count() == 1,
            "Inlay hints off disables all four kinds and sets nothing else");
        check(RustLanguageServer.ConfigurationSection == "rust-analyzer", "rust-analyzer configuration section");
        var wasm = WasmLanguageServer.Configuration(new WasmSettings());
        check((bool?)wasm.SelectToken("inlayHint.types") == true && (bool?)wasm.SelectToken("inlayHint.ending") == true && (bool?)wasm.SelectToken("inlayHint.index") == true,
            "wasmLanguageTools.inlayHint.{types,ending,index} follow the switch (on)");
        check(WasmLanguageServer.Configuration(new WasmSettings { InlayHints = false }).SelectTokens("inlayHint.*").All(t => (bool)t! == false), "... and off");
        check(WasmLanguageServer.ConfigurationSection == "wasmLanguageTools", "wat_server configuration section");
        check(WasmLanguageServer.IsServerFile(@"C:\m\a.WAT") && !WasmLanguageServer.IsServerFile("a.wast") && !WasmLanguageServer.IsServerFile("a.wasm"),
            "Only .wat goes to wat_server (it does not parse WAST scripts)");
    }

    private static void Cargo(string root, Action<bool, string> check)
    {
        var package = Path.Combine(root, "hello");
        foreach (var folder in new[] { "src\\bin\\tool", "examples" }) Directory.CreateDirectory(Path.Combine(package, folder));
        File.WriteAllText(Path.Combine(package, "Cargo.toml"), "[package]\nname = \"hello\"\n");
        string Run(string relative) => RustCargo.RunCommand(Path.Combine(package, relative)) is var (folder, arguments) && folder == package ? string.Join(" ", arguments) : "?";
        check(Run("src\\main.rs") == "run", "src/main.rs runs the default binary");
        check(Run("src\\bin\\extra.rs") == "run --bin extra" && Run("src\\bin\\tool\\main.rs") == "run --bin tool", "src/bin targets are named");
        check(Run("examples\\demo.rs") == "run --example demo", "examples are named");
        check(RustCargo.RunCommand(Path.Combine(root, "loose.rs")) == null, "No Cargo.toml above the file: no cargo run");
    }

    private static void Grammar(Action<bool, string> check)
    {
        var path = DartTests.FindRepositoryFile(Path.Combine("src", "Vistudio.Vsix", "Grammars", "Wat.tmLanguage.json"));
        var grammar = JObject.Parse(File.ReadAllText(path));
        check((string?)grammar["scopeName"] == "source.wat" && grammar["fileTypes"]!.Values<string>().SequenceEqual(new[] { "wat", "wast" }), "WAT grammar scope and file types");
        foreach (var pattern in grammar.SelectTokens("$..match").Concat(grammar.SelectTokens("$..begin")).Concat(grammar.SelectTokens("$..end")))
            check(ZigTests.TryRegex((string)pattern!), "WAT grammar regex compiles: " + pattern);

        // Each sample must be matched (whole) by a pattern carrying the expected scope prefix.
        bool Scoped(string scope, string sample) => grammar.SelectTokens("$..[?(@.match)]").OfType<JObject>()
            .Where(p => ((string?)p["name"] ?? "").StartsWith(scope, StringComparison.Ordinal))
            .Any(p => new Regex((string)p["match"]!).Matches(sample).Cast<Match>().Any(m => m.Value.Trim() == sample.Trim('(').Trim()));
        foreach (var word in new[] { "module", "func", "param", "result", "local", "global", "memory", "data", "table", "elem", "type", "import", "export", "start" })
            check(Scoped("storage.type.declaration.wat", "(" + word), "Keyword after ( : " + word);
        foreach (var word in new[] { "mut", "offset" }) check(Scoped("storage.modifier.wat", word), "Modifier " + word);
        foreach (var type in new[] { "i32", "i64", "f32", "f64", "v128", "funcref", "externref" }) check(Scoped("storage.type.wat", type), "Value type " + type);
        foreach (var instruction in new[] { "i32.add", "local.get", "global.set", "memory.grow", "f64.convert_i32_s", "i8x16.shuffle", "ref.func" })
            check(Scoped("keyword.operator.instruction.wat", instruction), "Dotted instruction " + instruction);
        foreach (var instruction in new[] { "call", "br_if", "unreachable", "end" }) check(Scoped("keyword.control.instruction.wat", instruction), "Control instruction " + instruction);
        check(!Scoped("storage.type.wat", "i32.add") && !Scoped("keyword.control.instruction.wat", "callx"), "Instructions and types match whole words only");
        foreach (var id in new[] { "$add", "$my.func", "$0" }) check(Scoped("variable.other.identifier.wat", id), "Identifier " + id);
        foreach (var number in new[] { "42", "-1_000", "3.14", "1e-7", "6.02E+23", "0xFF", "-0x1.8p+3", "0x1_000" }) check(Scoped("constant.numeric", number), "Number " + number);
        foreach (var special in new[] { "inf", "-nan", "nan:0x200000" }) check(Scoped("constant.numeric.special.wat", special), "Float constant " + special);
        check(Scoped("comment.line.double-semicolon.wat", ";; a comment"), "Line comment");
        var block = grammar.SelectToken("$.repository.block-comment")!;
        check((string?)block["name"] == "comment.block.wat" && new Regex((string)block["begin"]!).IsMatch("(; x") && new Regex((string)block["end"]!).IsMatch("x ;)")
            && (string?)block.SelectToken("patterns[0].include") == "#block-comment", "Block comments (; ;) nest through a recursive include");
        check(Scoped("variable.parameter.memarg.wat", "offset=") || new Regex((string)grammar.SelectToken("$.repository.keywords.patterns[0].match")!).IsMatch("offset=8"),
            "Memory argument offset= is not the offset keyword");
        var strings = grammar.SelectToken("$.repository.strings.patterns[0]")!;
        check((string?)strings["name"] == "string.quoted.double.wat" && new Regex((string)strings.SelectToken("patterns[0].match")!).IsMatch("\\0a"), "Strings with hex escapes");
    }

    private static JObject Symbol(string name, int kind, int line, int start, int end, params JObject[] children) => new JObject
    {
        ["name"] = name, ["kind"] = kind,
        ["range"] = new JObject { ["start"] = new JObject { ["line"] = line, ["character"] = 0 }, ["end"] = new JObject { ["line"] = line + 1, ["character"] = 1 } },
        ["selectionRange"] = new JObject { ["start"] = new JObject { ["line"] = line, ["character"] = start }, ["end"] = new JObject { ["line"] = line, ["character"] = end } },
        ["children"] = new JArray(children),
    };

    private static void RustSymbols(Action<bool, string> check)
    {
        var text = "struct Shape;\r\nimpl Shape {\r\n    fn area(&self) {}\r\n}\r\n/// Entry point\r\nfn main() {}\r\nmod tests {\r\n    fn main() {}\r\n}\r\ntrait Draw {}\r\nenum Kind { A }\r\nconst X: i32 = 1;\r\nfn helper() {}\r\nfn helper() {}\r\n";
        var symbols = new JArray(
            Symbol("Shape", 23, 0, 7, 12),
            Symbol("impl Shape", 19, 1, 5, 10, Symbol("area", 6, 2, 7, 11)),
            Symbol("main", 12, 5, 3, 7),
            Symbol("tests", 2, 6, 4, 9, Symbol("main", 12, 7, 7, 11)),
            Symbol("Draw", 11, 9, 6, 10),
            Symbol("Kind", 10, 10, 5, 9),
            Symbol("X", 14, 11, 6, 7),
            Symbol("helper", 12, 12, 3, 9),
            Symbol("helper", 12, 13, 3, 9));
        var elements = LspSymbolLens.FromDocumentSymbols(symbols, LspSymbolLens.Rust, text);
        check(elements.Select(e => e.QualifiedName).SequenceEqual(new[] { "Shape", "impl Shape::area", "main", "tests::main", "Draw", "Kind", "helper", "helper" }),
            "Rust lenses: structs, methods, functions, traits, enums (not impl blocks, modules or consts): " + string.Join(", ", elements.Select(e => e.QualifiedName)));
        check(elements.Select(e => e.Kind).SequenceEqual(new[] { "Struct", "Method", "Function", "Function", "Trait", "Enum", "Function", "Function" }), "Rust lens kinds (Interface is a trait)");
        var area = elements[1];
        check(text.Substring(area.NameStart, area.NameEnd - area.NameStart) == "area" && text.Substring(area.Start, 2) == "fn", "Name span from selectionRange; the lens starts at the line's first non-blank character (\\r\\n lines)");
        var main = elements.Single(e => e.IsMain);
        check(main.QualifiedName == "main" && text.Substring(main.Start, 7) == "fn main" && LspSymbolLens.IsMain(main.Identifier), "Top-level fn main is the entry point, below its doc comment");
        check(!LspSymbolLens.IsMain(elements[3].Identifier) && !elements[3].IsMain, "A main inside a module is not an entry point");
        check(elements[6].Identifier == "vistudiolsp|rust|Function|helper" && elements[7].Identifier == "vistudiolsp|rust|Function|helper#1", "Duplicate names get ordinals");
        check(elements.All(e => LspSymbolLens.LanguageOf(e.Identifier) == LspSymbolLens.Rust) && LspSymbolLens.LanguageOf("vistudiozig|Function|x") == null, "Identifiers carry the language");
        var (line, character) = CodeLensElement.NamePosition(text.Replace("\r\n", "\n"), 0, 0);
        check(line == 0 && character == 0, "Name position helper is shared with the Zig lenses");

        // Flat SymbolInformation answers: the name is searched inside the declaration, containers from containerName.
        var flatText = "pub fn run_all() {}\nimpl Runner { fn go(&self) {} }\n";
        var flat = new JArray(
            new JObject { ["name"] = "run_all", ["kind"] = 12, ["location"] = new JObject { ["uri"] = "file:///a.rs", ["range"] = new JObject { ["start"] = new JObject { ["line"] = 0, ["character"] = 0 }, ["end"] = new JObject { ["line"] = 0, ["character"] = 19 } } } },
            new JObject { ["name"] = "go", ["kind"] = 6, ["containerName"] = "Runner", ["location"] = new JObject { ["uri"] = "file:///a.rs", ["range"] = new JObject { ["start"] = new JObject { ["line"] = 1, ["character"] = 14 }, ["end"] = new JObject { ["line"] = 1, ["character"] = 29 } } } });
        var flatElements = LspSymbolLens.FromDocumentSymbols(flat, LspSymbolLens.Rust, flatText);
        check(flatElements.Count == 2 && flatText.Substring(flatElements[0].NameStart, 7) == "run_all" && flatElements[1].QualifiedName == "Runner::go"
            && flatText.Substring(flatElements[1].NameStart, 2) == "go", "Flat symbol answers map to name spans and containers");
        check(LspSymbolLens.FromDocumentSymbols(JValue.CreateNull(), LspSymbolLens.Rust, text).Count == 0
            && LspSymbolLens.FromDocumentSymbols(new JArray(Symbol("far", 12, 99, 0, 3)), LspSymbolLens.Rust, text).Count == 0, "Null answers and out-of-range positions give no lenses");
    }

    private static void WatSymbols(Action<bool, string> check)
    {
        var text = "(module\n  (func $add (param i32 i32) (result i32))\n  (global $g i32 (i32.const 0))\n  (func $main))\n";
        var symbols = new JArray(Symbol("$add", 12, 1, 8, 12), Symbol("$g", 13, 2, 10, 12), Symbol("$main", 12, 3, 8, 13));
        var elements = LspSymbolLens.FromDocumentSymbols(symbols, LspSymbolLens.Wat, text);
        check(elements.Select(e => e.Name).SequenceEqual(new[] { "$add", "$main" }) && elements.All(e => e.Kind == "Function" && !e.IsMain),
            "WAT lenses: functions only, and no Run lens ($main is not a Rust entry point)");
        check(text.Substring(elements[0].Start, 5) == "(func" && elements[0].Identifier == "vistudiolsp|wat|Function|$add", "WAT lens range and identity");
    }

    /// <summary>
    /// Initialize handshake with the installed servers: they must start on stdio and advertise inlay hints. Skipped
    /// (false) when neither rust-analyzer nor wat_server is found.
    /// </summary>
    internal static async Task<bool> RunLiveAsync(string root, Action<bool, string> check)
    {
        var any = false;
        foreach (var (name, server) in new[] { ("rust-analyzer", RustLanguageServer.Find(RustSettings.LoadGlobal())), ("wat_server", WasmLanguageServer.Find(WasmSettings.LoadGlobal())) })
        {
            if (server == null) { Console.WriteLine("SKIP: " + name + " not found (Tools > Options path, PATH or %USERPROFILE%\\.cargo\\bin) - live " + name + " checks."); continue; }
            any = true;
            var capabilities = await InitializeAsync(LanguageServerLocator.Create(server, "", root, name + " arguments"), root);
            check(capabilities?["inlayHintProvider"] != null, name + " answers initialize and advertises inlay hints: " + capabilities?.ToString(Newtonsoft.Json.Formatting.None));
            check(capabilities?["documentSymbolProvider"] != null && capabilities?["referencesProvider"] != null, name + " provides document symbols and references (CodeLens)");
        }
        return any;
    }

    private static async Task<JToken?> InitializeAsync(ProcessStartInfo start, string root)
    {
        using (var process = new OwnedProcess(start))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            _ = OwnedProcess.PumpAsync(process.Error, _ => { });
            var body = Encoding.UTF8.GetBytes(new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
                ["params"] = new JObject { ["processId"] = Process.GetCurrentProcess().Id, ["rootUri"] = new Uri(root + "\\").AbsoluteUri, ["capabilities"] = new JObject() },
            }.ToString(Newtonsoft.Json.Formatting.None));
            var header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
            await process.Input.WriteAsync(header, 0, header.Length, timeout.Token);
            await process.Input.WriteAsync(body, 0, body.Length, timeout.Token);
            await process.Input.FlushAsync(timeout.Token);
            try
            {
                while (true)
                {
                    var message = await ReadFrameAsync(process.Output, timeout.Token);
                    if (message == null) return null;
                    if ((int?)message["id"] == 1) return message["result"]?["capabilities"];
                }
            }
            catch (OperationCanceledException) { return null; }
            finally { await process.StopAsync(); }
        }
    }

    private static async Task<JObject?> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var header = new StringBuilder();
        var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, 0, 1, token) == 0) return null;
            header.Append((char)one[0]);
        }
        var length = int.Parse(Regex.Match(header.ToString(), @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase).Groups[1].Value);
        var body = new byte[length];
        for (var read = 0; read < length;)
        {
            var count = await stream.ReadAsync(body, read, length - read, token);
            if (count == 0) return null;
            read += count;
        }
        return JObject.Parse(Encoding.UTF8.GetString(body));
    }

    // PATH lookups return the extension as PATHEXT spells it (.EXE); Windows paths compare without case.
    private static bool Same(string? found, string expected) => string.Equals(found, expected, StringComparison.OrdinalIgnoreCase);

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
    }
}
