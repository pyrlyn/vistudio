using System;
using System.IO;
using System.Linq;
using Vistudio.Core;
using Vistudio.Core.Languages;

/// <summary>Located build errors of language projects (Rust, C, C++, Java, WebAssembly, Zig, Dart) for the Error List.</summary>
internal static class ToolDiagnosticTests
{
    internal static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "proj");
        string At(params string[] parts) => Path.GetFullPath(Path.Combine(new[] { root }.Concat(parts).ToArray()));

        // cargo build (rustc human output, as Visual Studio's Build pane receives it).
        var rust = ToolDiagnosticParser.Parse(new[]
        {
            "   Compiling hello v0.1.0 (C:\\proj)",
            "warning: unused variable: `x`",
            " --> src\\main.rs:2:9",
            "  |",
            "2 |     let x = 1;",
            "  |         ^ help: if this is intentional, prefix it with an underscore: `_x`",
            "error[E0425]: cannot find value `y` in this scope",
            " --> src/main.rs:3:20",
            "error: could not compile `hello` (bin \"hello\") due to 1 previous error; 1 warning emitted",
        }, root);
        check(rust.Count == 2, "cargo: one warning and one error, the summary has no location: " + rust.Count);
        check(rust[0].Severity == BuildDiagnosticSeverity.Warning && rust[0].File == At("src", "main.rs") && rust[0].Line == 1 && rust[0].Column == 8 && rust[0].Message == "unused variable: `x`", "cargo warning located through its --> line, zero-based");
        check(rust[1].Severity == BuildDiagnosticSeverity.Error && rust[1].Message == "E0425: cannot find value `y` in this scope" && rust[1].Line == 2 && rust[1].Column == 19, "cargo error keeps its code");

        // cmake --build with MSVC (MSBuild prefixes and project suffix), and clang/gcc.
        var msvc = ToolDiagnosticParser.Parse(new[]
        {
            "  main.cpp",
            "C:\\proj\\src\\main.cpp(4,13): error C2065: 'y': undeclared identifier [C:\\proj\\build\\hello.vcxproj]",
            "1>C:\\proj\\src\\util.h(10): warning C4244: 'return': conversion from 'double' to 'int', possible loss of data",
            "src/main.c:7:5: error: implicit declaration of function 'foo' [-Wimplicit-function-declaration]",
            "src/main.c:9:1: note: declared here",
        }, root);
        check(msvc.Count == 3, "MSVC and clang: two errors, one warning, notes skipped: " + msvc.Count);
        check(msvc[0].File == @"C:\proj\src\main.cpp" && msvc[0].Line == 3 && msvc[0].Column == 12 && msvc[0].Message == "C2065: 'y': undeclared identifier", "MSVC error: location, code, project suffix removed");
        check(msvc[1].Severity == BuildDiagnosticSeverity.Warning && msvc[1].Line == 9 && msvc[1].Column == 0, "MSVC warning without a column, after an MSBuild node prefix");
        check(msvc[2].File == At("src", "main.c") && msvc[2].Line == 6 && msvc[2].Column == 4, "GCC/Clang error, relative path resolved against the project folder");

        // gradlew build of a .javaproj (javac has no column), zig build, and dart analyze / the Dart compiler.
        var mixed = ToolDiagnosticParser.Parse(new[]
        {
            "> Task :compileJava FAILED",
            "C:\\proj\\src\\main\\java\\app\\Main.java:5: error: ';' expected",
            "src/main.zig:3:5: error: use of undeclared identifier 'y'",
            "  error - bin\\main.dart:3:7 - Undefined name 'x'. - undefined_identifier",
            "  info - bin\\main.dart:1:1 - Unused import. - unused_import",
            "lib/app.dart:12:3: Error: Expected ';' after this.",
        }, root);
        check(mixed.Count == 4, "javac, zig, dart analyze and the Dart compiler; infos skipped: " + mixed.Count);
        check(mixed[0].File == @"C:\proj\src\main\java\app\Main.java" && mixed[0].Line == 4 && mixed[0].Column == 0 && mixed[0].Message == "';' expected", "javac error without a column");
        check(mixed[1].File == At("src", "main.zig") && mixed[1].Message == "use of undeclared identifier 'y'", "zig build error");
        check(mixed[2].File == At("bin", "main.dart") && mixed[2].Line == 2 && mixed[2].Column == 6 && mixed[2].Message == "Undefined name 'x'.", "dart analyze error without its lint code");
        check(mixed[3].Message == "Expected ';' after this." && mixed[3].Severity == BuildDiagnosticSeverity.Error, "Dart compiler 'Error:'");

        // wasm-tools parse: the innermost cause names the problem at the location.
        var wasm = ToolDiagnosticParser.Parse(new[]
        {
            "error: failed to parse WebAssembly module",
            "",
            "Caused by:",
            "    0: expected `)`",
            "            --> src/main.wat:3:5",
            "             |",
        }, root);
        check(wasm.Count == 1 && wasm[0].Message == "expected `)`" && wasm[0].File == At("src", "main.wat") && wasm[0].Line == 2, "wasm-tools error located through Caused by and -->");

        check(ToolDiagnosticParser.Parse(new[] { "error: x", " --> a.rs:1:1", " --> a.rs:1:1", "error: x", " --> a.rs:1:1" }, root).Count == 1, "Repeated messages are listed once");
        check(ToolDiagnosticParser.Parse(new[] { "Finished `dev` profile", "Build succeeded.", "http://example.com:80: error page", new string('x', 5000) + ".c:1:1: error: long" }, root).Count == 0, "Ordinary output, URLs and overlong lines are not diagnostics");
    }
}
