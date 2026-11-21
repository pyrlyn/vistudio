using System;
using System.IO;
using System.Linq;
using Vistudio.Core.Rust;
using Vistudio.Core.Zig;

/// <summary>Run test / Debug test lenses for Rust and Zig: test discovery and the commands they run (T75).</summary>
internal static class TestLensTests
{
    private const string Rust = @"fn helper() {}

#[test]
fn top_level() {}

/// #[test] in a doc comment
fn documented() { let s = ""#[test]""; }

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    #[should_panic(expected = ""boom"")]
    fn panics() { panic!(""boom""); }

    #[tokio::test]
    async fn async_one() {}

    mod nested {
        #[test]
        pub fn deep() {}
    }

    #[test_case(1)]
    fn not_a_plain_test(x: i32) {}

    fn helper_in_tests() {}
}

mod tests_file;
";

    internal static void RunRust(string root, Action<bool, string> check)
    {
        var tests = RustSourceScanner.Tests(Rust);
        check(string.Join(", ", tests.Select(t => t.Path)) == "top_level, tests::panics, tests::async_one, tests::nested::deep",
            "Rust tests: #[test] and #[x::test] functions with their module paths; comments, strings, cfg(test), test_case and helpers are not tests: " + string.Join(", ", tests.Select(t => t.Path)));
        var deep = tests.Last();
        check(Rust.Substring(deep.NameStart, deep.NameEnd - deep.NameStart) == "deep", "Rust test name offsets");

        // A package with a library, a binary, an integration test and a module tree.
        var package = Path.Combine(root, "pkg");
        foreach (var folder in new[] { "src/net", "src/bin/tool", "tests" }) Directory.CreateDirectory(Path.Combine(package, folder));
        File.WriteAllText(Path.Combine(package, "Cargo.toml"), "[package]\nname = \"my-app\"\nversion = \"0.1.0\"\n");
        File.WriteAllText(Path.Combine(package, "src", "main.rs"), "fn main() {}\n");
        string Args(System.Collections.Generic.IReadOnlyList<string> a) => string.Join(" ", a);

        var main = RustCargo.TestCommand(Path.Combine(package, "src", "main.rs"), "tests::adds")!;
        check(main.Folder == package && Args(main.RunArguments) == "test --bin my-app -- tests::adds --exact" && main.CrateRoot == Path.Combine(package, "src", "main.rs"),
            "Rust: a binary-only package tests its [package] name binary: " + Args(main.RunArguments));
        check(Args(main.BuildArguments) == "test --bin my-app --no-run --message-format=json" && Args(main.ProgramArguments) == "tests::adds --exact --nocapture", "Rust: Debug test builds without running, then filters in the program");

        File.WriteAllText(Path.Combine(package, "src", "lib.rs"), "pub mod net;\n");
        var module = RustCargo.TestCommand(Path.Combine(package, "src", "net", "http.rs"), "tests::parses")!;
        check(Args(module.RunArguments) == "test --lib -- net::http::tests::parses --exact" && module.CrateRoot == Path.Combine(package, "src", "lib.rs"),
            "Rust: a module file adds its module path, in the library: " + Args(module.RunArguments));
        var modRs = RustCargo.TestCommand(Path.Combine(package, "src", "net", "mod.rs"), "tests::x")!;
        check(modRs.Filter == "net::tests::x", "Rust: mod.rs adds no module of its own: " + modRs.Filter);
        var integration = RustCargo.TestCommand(Path.Combine(package, "tests", "api.rs"), "works")!;
        check(Args(integration.RunArguments) == "test --test api -- works --exact" && integration.CrateRoot == Path.Combine(package, "tests", "api.rs"), "Rust: integration tests use --test");
        var bin = RustCargo.TestCommand(Path.Combine(package, "src", "bin", "tool", "cli.rs"), "tests::t")!;
        check(Args(bin.RunArguments) == "test --bin tool -- cli::tests::t --exact" && bin.CrateRoot == Path.Combine(package, "src", "bin", "tool", "main.rs"),
            "Rust: a module of a src/bin/<name>/ binary: " + Args(bin.RunArguments));
        check(RustCargo.TestCommand(Path.Combine(root, "loose.rs"), "x") == null, "Rust: no Cargo.toml, no command");

        var lib = Path.Combine(package, "src", "lib.rs").Replace("\\", "\\\\");
        var json = new[]
        {
            "{\"reason\":\"compiler-artifact\",\"target\":{\"src_path\":\"" + lib + "\"},\"profile\":{\"test\":false},\"executable\":null}",
            "{\"reason\":\"compiler-message\",\"message\":{\"rendered\":\"warning: unused variable: `x`\\n --> src/lib.rs:1:5\\n\"}}",
            "{\"reason\":\"compiler-artifact\",\"target\":{\"src_path\":\"C:\\\\other\\\\main.rs\"},\"profile\":{\"test\":true},\"executable\":\"C:\\\\t\\\\other.exe\"}",
            "{\"reason\":\"compiler-artifact\",\"target\":{\"src_path\":\"" + lib + "\"},\"profile\":{\"test\":true},\"executable\":\"C:\\\\t\\\\my_app-1234.exe\"}",
            "not json",
            "{\"reason\":\"build-finished\",\"success\":true}",
        };
        check(RustCargo.TestExecutable(json, Path.Combine(package, "src", "lib.rs")) == @"C:\t\my_app-1234.exe", "Rust: the test program of the test's crate root is picked from cargo's artifacts");
        check(RustCargo.TestExecutable(json.Take(3), Path.Combine(package, "src", "lib.rs")) == @"C:\t\other.exe" && RustCargo.TestExecutable(new[] { "x" }, "a.rs") == null, "Rust: another test program as a fallback, none without artifacts");
        check(RustCargo.RenderedMessage(json[1]) == "warning: unused variable: `x`\n --> src/lib.rs:1:5" && RustCargo.RenderedMessage(json[0]) == null && RustCargo.RenderedMessage("{bad") == null,
            "Rust: compiler messages of the JSON build are shown as rustc renders them");
    }

    internal static void RunZig(Action<bool, string> check)
    {
        const string Zig = "const std = @import(\"std\");\nfn add(a: i32, b: i32) i32 { return a + b; }\ntest \"adds two numbers\" {\n    try std.testing.expect(add(1, 2) == 3);\n}\ntest add {\n}\ntest {\n}\nfn f() void {\n    // test \"in comment\" {\n}\n";
        var tests = ZigSourceScanner.Scan(Zig).Declarations.Where(d => d.Kind == ZigDeclarationKind.Test).ToList();
        check(string.Join("|", tests.Select(t => t.Name)) == "adds two numbers|add|", "Zig tests: named, decltest and unnamed blocks; not comments: " + string.Join("|", tests.Select(t => t.Name)));

        var run = ZigCommandFactory.Test(@"C:\zig\zig.exe", @"C:\p\src\main.zig", @"C:\p", null, "-lc", "adds two numbers");
        check(run.Arguments == "test --color off C:\\p\\src\\main.zig -lc --test-filter \"adds two numbers\"" && run.WorkingDirectory == @"C:\p", "Zig Run test: zig test with the name as filter: " + run.Arguments);
        var all = ZigCommandFactory.Test(@"C:\zig\zig.exe", @"C:\p\src\main.zig", @"C:\p", @"C:\zig\lib", "", "");
        check(all.Arguments == "test --color off C:\\p\\src\\main.zig --zig-lib-dir C:\\zig\\lib", "Zig: an unnamed test block runs the file's tests (no filter): " + all.Arguments);
        var binary = ZigCommandFactory.TestBinary(@"C:\p", @"C:\p\src\main.zig");
        var debug = ZigCommandFactory.Test(@"C:\zig\zig.exe", @"C:\p\src\main.zig", @"C:\p", null, "", "add", binary);
        check(binary == @"C:\p\zig-out\vistudio\test-main.exe" && debug.Arguments.EndsWith("--test-filter add --test-no-exec -femit-bin=C:\\p\\zig-out\\vistudio\\test-main.exe"),
            "Zig Debug test: the test program is built, not run, for the native debugger: " + debug.Arguments);
        check(ZigCommandFactory.Test("zig", "a.zig", ".", null, "", "say \"hi\"").Arguments.Contains("\"say \\\"hi\\\"\""), "Zig: quotes in a test name are escaped for the command line");
    }
}
