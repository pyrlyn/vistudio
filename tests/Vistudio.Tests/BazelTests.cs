using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Vistudio.Core;
using Vistudio.Core.Bazel;
using Vistudio.Core.Languages;

/// <summary>Bazel files (scanner, labels, checks, completion, buildifier), the Bazel build system and per-language build caches.</summary>
internal static class BazelTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        Lexer(check);
        Labels(check);
        var workspace = Workspace(Path.Combine(root, "ws"));
        WorkspaceFacts(workspace, check);
        Validation(workspace, check);
        Completion(workspace, check);
        BuildifierOutput(check);
        Cache(Path.Combine(root, "cache"), check);
        BuildSystem(Path.Combine(root, "projects"), check);
        Icons(root, check);
    }

    private static void Lexer(Action<bool, string> check)
    {
        var tokens = StarlarkLexer.Tokenize("cc_library(  # comment\n    name = \"lib\",\n    srcs = ['a.cc', r\"b\\c.cc\"],\n)\nx = \"\"\"doc\n\"q\" \"\"\"\nbad = \"open\n");
        var strings = tokens.Where(t => t.Kind == StarlarkTokenKind.String).ToList();
        check(strings.Select(s => s.Text).Take(4).SequenceEqual(new[] { "lib", "a.cc", "b\\c.cc", "doc\n\"q\" " }), "Strings decoded (single, raw, triple): " + string.Join("|", strings.Select(s => s.Text)));
        check(strings.Last().Text == "open" && !strings.Last().Closed, "Unterminated string stops at the line end");
        check(!tokens.Any(t => t.Text.Contains("comment")), "Comments are skipped");
        check(tokens.Count(t => t.Kind == StarlarkTokenKind.Newline) == 3, "Line breaks inside brackets are not statements: " + tokens.Count(t => t.Kind == StarlarkTokenKind.Newline));
        var name = strings[0];
        check(name.Line == 1 && name.Column == 11 && name.ValueStart == name.Start + 1, "String position: " + name.Line + ":" + name.Column);

        var file = StarlarkFile.Parse("load(\"//:defs.bzl\", \"my_rule\", alias = \"other\")\n\ndef helper(x):\n    native.cc_library(name = x)\n\nVALUE = 1\nmy_rule(name = \"a\", deps = [\":b\"] + glob([\"*.h\"]))\n");
        check(file.Calls.Select(c => c.Name).SequenceEqual(new[] { "load", "my_rule" }), "Top-level calls only: " + string.Join(",", file.Calls.Select(c => c.Name)));
        check(file.Functions.Single().Text == "helper" && file.Globals.Single().Text == "VALUE", "def and globals");
        check(file.LoadedSymbols.SequenceEqual(new[] { "my_rule", "alias" }), "Loaded symbols with aliases: " + string.Join(",", file.LoadedSymbols));
        var call = file.Calls[1];
        check(call.StringArgument("name")?.Text == "a" && call.Strings("deps").Select(s => s.Text).SequenceEqual(new[] { ":b", "*.h" }), "Keyword arguments and their strings");
    }

    private static void Labels(Action<bool, string> check)
    {
        var full = BazelLabel.Parse("@rules_cc//cc:defs.bzl")!;
        check(full.Repository == "rules_cc" && full.IsAbsolute && full.Package == "cc" && full.Target == "defs.bzl", "Repository label");
        var shorthand = BazelLabel.Parse("//app/core")!;
        check(shorthand.Package == "app/core" && shorthand.Target == "core", "//pkg means //pkg:pkg");
        check(BazelLabel.Parse(":lib")!.Target == "lib" && !BazelLabel.Parse(":lib")!.IsAbsolute, "Local label");
        check(BazelLabel.Parse("@repo")!.Target == "repo", "@repo means @repo//:repo");
        foreach (var bad in new[] { "//", "//a/", "//a:", "//a//b", "a:b:c", "//../x", "@", "" })
            check(BazelLabel.Problem(bad) != null, "Invalid label rejected: '" + bad + "'");
        foreach (var good in new[] { "//:all_files", "@@canonical+//pkg:t", "src/main.cc", "//visibility:public", "@bazel_tools//tools/build_defs/repo:http.bzl" })
            check(BazelLabel.Problem(good) == null, "Valid label: " + good + " (" + BazelLabel.Problem(good) + ")");
    }

    private static string Workspace(string root)
    {
        void Write(string relative, string text)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Write("MODULE.bazel", "module(name = \"demo\", version = \"1.0\")\nbazel_dep(name = \"rules_cc\", version = \"0.1.1\")\nbazel_dep(name = \"googletest\", version = \"1.15.2\", repo_name = \"gtest\")\next = use_extension(\"//:ext.bzl\", \"ext\")\nuse_repo(ext, \"generated_repo\")\n");
        Write("BUILD.bazel", "exports_files([\"README.md\"])\n");
        Write("README.md", "demo");
        Write("defs.bzl", "def cc_app(name, **kwargs):\n    native.cc_binary(name = name, **kwargs)\n\n_PRIVATE = 1\nCOPTS = [\"-Wall\"]\n");
        Write(@"app\BUILD", "load(\"//:defs.bzl\", \"cc_app\")\n\ncc_app(\n    name = \"app\",\n    srcs = [\"main.cc\"],\n    deps = [\"//lib:util\"],\n)\n");
        Write(@"app\main.cc", "int main() {}");
        Write(@"app\src\extra.cc", "");
        Write(@"app\src\extra.h", "");
        Write(@"lib\BUILD.bazel", "cc_library(\n    name = \"util\",\n    hdrs = [\"util.h\"],\n    visibility = [\"//visibility:public\"],\n)\ngenrule(name = \"gen\", outs = [\"gen.h\"], cmd = \"echo > $@\")\n");
        Write(@"lib\util.h", "");
        Write(@"lib\sub\BUILD", "filegroup(name = \"files\", srcs = [\"data.txt\"])\n");
        Write(@"lib\sub\data.txt", "");
        Write(@"tools\notes.txt", "");
        Directory.CreateDirectory(Path.Combine(root, "bazel-out"));
        return root;
    }

    private static void WorkspaceFacts(string root, Action<bool, string> check)
    {
        check(BazelWorkspace.FindRoot(Path.Combine(root, "lib", "sub")) == root, "Workspace root found from a subpackage");
        check(BazelWorkspace.PackageFolder(Path.Combine(root, "app", "src", "extra.cc"), root) == Path.Combine(root, "app"), "Package of a file in a subfolder");
        check(BazelWorkspace.PackagePath(root, Path.Combine(root, "lib", "sub")) == "lib/sub" && BazelWorkspace.PackagePath(root, root) == "", "Package paths");
        var targets = BazelWorkspace.Targets(Path.Combine(root, "lib")).Select(t => t.Name).ToList();
        check(targets.SequenceEqual(new[] { "util", "gen", "gen.h" }), "Targets and genrule outputs: " + string.Join(",", targets));
        var repositories = BazelWorkspace.Repositories(root);
        check(new[] { "demo", "rules_cc", "gtest", "generated_repo", "bazel_tools" }.All(repositories.Contains) && !repositories.Contains("googletest"),
            "Repositories use repo_name: " + string.Join(",", repositories));
        check(BazelWorkspace.RepositoriesAreKnown(root), "A Bzlmod-only workspace has known repositories");
        check(BazelWorkspace.IsStarlarkFile(@"C:\x\BUILD") && BazelWorkspace.IsStarlarkFile(@"C:\x\MODULE.bazel") && BazelWorkspace.IsStarlarkFile(@"C:\x\defs.bzl")
            && !BazelWorkspace.IsStarlarkFile(@"C:\x\BUILD.txt") && !BazelWorkspace.IsStarlarkFile(@"C:\x\build.gradle"), "Starlark file names");
    }

    private static IReadOnlyList<BazelDiagnostic> Check(string root, string relative, string text) =>
        BazelValidator.Validate(Path.Combine(root, relative), text);

    private static bool Has(IEnumerable<BazelDiagnostic> diagnostics, string code, BazelSeverity severity, int line) =>
        diagnostics.Any(d => d.Code == code && d.Severity == severity && d.Line == line);

    private static void Validation(string root, Action<bool, string> check)
    {
        var clean = Check(root, @"app\BUILD", File.ReadAllText(Path.Combine(root, "app", "BUILD")));
        check(clean.Count == 0, "The sample BUILD file has no problems: " + string.Join("; ", clean));
        check(Check(root, @"lib\BUILD.bazel", File.ReadAllText(Path.Combine(root, "lib", "BUILD.bazel"))).Count == 0, "visibility and genrule outputs are fine");

        var text = string.Join("\n", new[]
        {
            "load(\"//:defs.bzl\", \"_PRIVATE\")",                                   // 0 private symbol
            "load(\"//:defs.py\", \"x\")",                                           // 1 not .bzl
            "cc_library(name = \"a\", srcs = [\"missing.cc\", \"main.cc\"])",        // 2 missing file
            "cc_library(name = \"a\", deps = [\"//nope:x\", \"//lib:util\"])",        // 3 duplicate, missing package
            "cc_test(name = \"t\", deps = [\"@unknown//x\", \"@gtest//:gtest_main\", \":ghost\", \"//lib:nothing\"])", // 4
            "cc_binary(name = \"b\", srcs = [\"../lib/util.h\", \"src\", \"lib/x.h\"], visibility = [\"//visibility:everyone\"])", // 5
            "def helper():",                                                          // 6 def in BUILD
            "    pass",
            "while True:",                                                            // 8 Python
            "x = [1, 2)",                                                             // 9 mismatched
            "y = \"open",                                                             // 10 unterminated
            "filegroup(name = \"g\", srcs = glob([\"**/*.cc\"]) + [\":a\"])",        // 11 glob patterns skipped
        });
        var found = Check(root, @"app\BUILD", text);
        string Dump() => string.Join("\n  ", found);
        check(Has(found, "private-symbol", BazelSeverity.Error, 0), "Private symbols cannot be loaded\n  " + Dump());
        check(Has(found, "load", BazelSeverity.Error, 1), "load() needs a .bzl file");
        check(Has(found, "missing-file", BazelSeverity.Warning, 2) && found.Count(d => d.Line == 2) == 1, "Missing source file, existing one accepted");
        check(Has(found, "duplicate-target", BazelSeverity.Error, 3), "Duplicate target name");
        check(Has(found, "missing-package", BazelSeverity.Warning, 3) && found.Count(d => d.Line == 3 && d.Code == "missing-package") == 1, "Missing package, existing label accepted");
        check(Has(found, "unknown-repository", BazelSeverity.Warning, 4) && found.Count(d => d.Line == 4 && d.Code == "unknown-repository") == 1, "Unknown repository; repo_name accepted");
        check(Has(found, "unknown-target", BazelSeverity.Suggestion, 4) && found.Count(d => d.Line == 4 && d.Code == "unknown-target") == 2, "Unknown local and remote targets are suggestions");
        check(Has(found, "invalid-label", BazelSeverity.Error, 5), "'..' in a label is invalid");
        check(Has(found, "directory-label", BazelSeverity.Warning, 5), "A folder in srcs");
        check(Has(found, "visibility", BazelSeverity.Error, 5), "Unknown //visibility value");
        check(Has(found, "def-in-build", BazelSeverity.Error, 6), "def in a BUILD file");
        check(Has(found, "not-starlark", BazelSeverity.Error, 8), "Python while statement");
        check(Has(found, "unbalanced", BazelSeverity.Error, 9), "Mismatched brackets");
        check(Has(found, "unterminated-string", BazelSeverity.Error, 10), "Unterminated string");
        check(!found.Any(d => d.Line == 11), "glob() patterns and labels in expressions are not files: " + string.Join("; ", found.Where(d => d.Line == 11)));

        var crossing = Check(root, @"lib\BUILD.bazel", "filegroup(name = \"x\", srcs = [\"sub/data.txt\"])\n");
        check(Has(crossing, "crosses-package", BazelSeverity.Error, 0), "A file of a subpackage needs that package's label");
        var module = Check(root, "MODULE.bazel", "load(\"//:defs.bzl\", \"x\")\nregister_toolchains(\"//nope:all\", \"//lib/...\")\n");
        check(Has(module, "load-in-module", BazelSeverity.Error, 0) && Has(module, "missing-package", BazelSeverity.Warning, 1) && module.Count(d => d.Line == 1) == 1,
            "MODULE.bazel: no load(), toolchain patterns accepted: " + string.Join("; ", module));
        var bzl = Check(root, "defs.bzl", "def f(ctx):\n    return [DefaultInfo(files = depset(ctx.files.srcs))]\n");
        check(bzl.Count == 0, ".bzl definitions are fine: " + string.Join("; ", bzl));
    }

    private static IReadOnlyList<string> Labels(BazelCompletionResult? result) => result?.Items.Select(i => i.InsertText).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();

    private static BazelCompletionResult? At(string root, string relative, string text, bool names = true)
    {
        var caret = text.IndexOf('|');
        return BazelCompletion.Complete(Path.Combine(root, relative), text.Remove(caret, 1), caret, names);
    }

    private static void Completion(string root, Action<bool, string> check)
    {
        var files = At(root, @"app\BUILD", "cc_binary(\n    name = \"app\",\n    srcs = [\"|\"],\n)\n");
        var items = Labels(files);
        check(files != null && files.InString && files.Length == 0, "srcs: string context with an empty prefix");
        check(items.Contains("main.cc") && items.Contains("src/") && !items.Contains("BUILD") && items.Contains(":app"), "srcs: files, folders and local targets: " + string.Join(",", items));
        var nested = Labels(At(root, @"app\BUILD", "cc_binary(name = \"app\", srcs = [\"src/ex|\"])"));
        check(nested.SequenceEqual(new[] { "src/extra.cc", "src/extra.h" }), "Files of a subfolder: " + string.Join(",", nested));
        var globbed = Labels(At(root, @"app\BUILD", "cc_binary(name = \"app\", srcs = glob([\"|"));
        check(globbed.Contains("main.cc") && globbed.Contains("src/") && !globbed.Any(i => i.StartsWith(":")), "glob(): files only: " + string.Join(",", globbed));
        var packages = Labels(At(root, @"app\BUILD", "cc_binary(name = \"app\", deps = [\"//|\"])"));
        check(packages.Contains("//:") && packages.Contains("//app") && packages.Contains("//lib") && packages.Contains("//lib/") && !packages.Any(p => p.Contains("bazel-out")) && !packages.Contains("//tools"),
            "// packages and folders: " + string.Join(",", packages));
        var targets = Labels(At(root, @"app\BUILD", "cc_binary(name = \"app\", deps = [\"//lib:|\"])"));
        check(targets.Contains("//lib:util") && targets.Contains("//lib:gen") && !targets.Contains("//lib:util.h"), "Targets of a package (labels only for deps): " + string.Join(",", targets));
        var sub = Labels(At(root, @"app\BUILD", "cc_binary(name = \"app\", deps = [\"//lib/|\"])"));
        check(sub.SequenceEqual(new[] { "//lib:", "//lib/sub" }), "Subpackages: " + string.Join(",", sub));
        var local = Labels(At(root, @"lib\BUILD.bazel", "cc_test(name = \"t\", deps = [\":|\"])"));
        check(local.Contains(":util") && local.Contains(":gen"), "Local targets: " + string.Join(",", local));
        var repositories = Labels(At(root, @"app\BUILD", "cc_test(name = \"t\", deps = [\"@|\"])"));
        check(repositories.Contains("@gtest//") && repositories.Contains("@rules_cc//"), "Repositories: " + string.Join(",", repositories));
        var load = Labels(At(root, @"app\BUILD", "load(\"//:|"));
        check(load.Contains("//:defs.bzl") && !load.Contains("//:README.md"), "load(): .bzl files: " + string.Join(",", load));
        var symbols = Labels(At(root, @"app\BUILD", "load(\"//:defs.bzl\", \"|"));
        check(symbols.Contains("cc_app") && symbols.Contains("COPTS") && !symbols.Contains("_PRIVATE"), "load(): public symbols: " + string.Join(",", symbols));
        var visibility = Labels(At(root, @"lib\BUILD.bazel", "cc_library(name = \"x\", visibility = [\"|\"])"));
        check(visibility.Contains("//visibility:public") && visibility.Contains("//visibility:private"), "visibility values");
        check(At(root, @"lib\BUILD.bazel", "cc_library(name = \"|\")") == null, "No completion in name");

        var attributes = At(root, @"lib\BUILD.bazel", "cc_library(\n    name = \"x\",\n    hd|\n)");
        check(attributes != null && !attributes.InString && Labels(attributes).Contains("hdrs = ") && !Labels(attributes).Contains("name = "), "Attributes of the call, used ones left out: " + string.Join(",", Labels(attributes).Take(8)));
        var rules = Labels(At(root, @"lib\BUILD.bazel", "cc_library(name = \"x\")\nc|"));
        check(rules.Contains("cc_binary") && rules.Contains("rust_library") && !rules.Contains("bazel_dep") && !rules.Contains("len"), "Rules at a statement start in BUILD: " + rules.Count);
        var moduleItems = Labels(At(root, "MODULE.bazel", "bazel_dep(name = \"x\")\n|"));
        check(moduleItems.Contains("bazel_dep") && moduleItems.Contains("use_repo") && !moduleItems.Contains("cc_binary"), "MODULE.bazel functions");
        check(At(root, @"lib\BUILD.bazel", "x = native.|") == null && At(root, @"lib\BUILD.bazel", "x = 12|") == null, "Nothing after a dot or a number");
        check(At(root, @"lib\BUILD.bazel", "cc_library(\n    hd|\n)", names: false) == null, "Names are left to a running language server");
        check(BazelCompletion.ApplicableStart("srcs = [\"src/ma", 15) == 9 && BazelCompletion.ApplicableStart("x = 1.", 6) == null, "Applicable span without disk access");
    }

    private static void BuildifierOutput(Action<bool, string> check)
    {
        const string json = "{\"success\":false,\"files\":[{\"filename\":\"app/BUILD\",\"formatted\":false,\"valid\":true,\"warnings\":[{\"start\":{\"line\":3,\"column\":5},\"end\":{\"line\":3,\"column\":12},\"category\":\"load\",\"actionable\":true,\"autoFixable\":true,\"message\":\"Loaded symbol \\\"x\\\" is unused.\",\"url\":\"https://github.com/bazelbuild/buildtools/blob/main/WARNINGS.md#load\"}]}]}";
        var parsed = Buildifier.Parse(json, "", reportFormatting: true);
        check(parsed.Count == 2 && parsed[0].Line == 2 && parsed[0].Column == 4 && parsed[0].Length == 7 && parsed[0].Code == "load" && parsed[0].Source == "buildifier"
            && parsed[0].Message.Contains("WARNINGS.md#load"), "buildifier warning position, category and link: " + string.Join("; ", parsed));
        check(parsed[1].Code == "format" && parsed[1].Severity == BazelSeverity.Suggestion, "Unformatted file is a suggestion");
        check(Buildifier.Parse(json, "", reportFormatting: false).Count == 1, "Formatting report can be switched off");
        var syntax = Buildifier.Parse("{\"success\":false,\"files\":[{\"filename\":\"BUILD\",\"formatted\":false,\"valid\":false,\"warnings\":[]}]}", "app/BUILD:4:7: syntax error near ]\n", true);
        check(syntax.Count == 1 && syntax[0].Severity == BazelSeverity.Error && syntax[0].Line == 3 && syntax[0].Column == 6, "Syntax errors from stderr; invalid files are not 'unformatted'");
        check(Buildifier.Parse("not json", "", true).Count == 0, "Unknown output gives nothing");
        var start = Buildifier.Create(@"C:\tools\buildifier.exe", @"C:\ws\app\BUILD", @"C:\ws");
        check(start.Arguments == "--mode=check --lint=warn --format=json --path=app/BUILD" && start.WorkingDirectory == @"C:\ws" && start.RedirectStandardInput, "buildifier command: " + start.Arguments);
        var settings = new BazelSettings();
        check(BazelLanguageServer.Arguments(@"C:\x\starpls.exe", settings) == "server" && BazelLanguageServer.Arguments(@"C:\x\bazel-lsp.exe", settings) == "", "starpls gets its server subcommand");
        settings.LanguageServerArguments = "server --experimental_infer_ctx_attributes";
        check(BazelLanguageServer.Arguments(@"C:\x\starpls.exe", settings) == "server --experimental_infer_ctx_attributes", "An explicit server subcommand is kept");
    }

    private static void Cache(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        var sccache = @"C:\tools\sccache.exe";
        BuildCacheOptions Options(string directory, bool sccacheInstalled = true, string remote = "", string? wrapper = null) => new BuildCacheOptions
        {
            WorkingDirectory = directory, BazelDiskCache = @"C:\cache\bazel", RemoteCache = remote, StateFolder = Path.Combine(root, "state"),
            FindTool = name => name == "sccache" && sccacheInstalled ? sccache : null,
            GetEnvironment = name => name == "RUSTC_WRAPPER" ? wrapper : null,
        };
        string Join(BuildCacheResult r) => string.Join(" ", r.Arguments);

        var bazelRoot = Path.Combine(root, "bazel");
        Directory.CreateDirectory(bazelRoot);
        File.WriteAllText(Path.Combine(bazelRoot, "MODULE.bazel"), "");
        var bazel = BuildCache.Apply(@"C:\x\bazelisk.exe", new[] { "--output_user_root=C:/o", "build", "//..." }, Options(bazelRoot, remote: "grpcs://cache.example:443"));
        check(Join(bazel) == "--output_user_root=C:/o build --disk_cache=C:\\cache\\bazel --remote_cache=grpcs://cache.example:443 //...", "Bazel flags after the command: " + Join(bazel));
        check(Join(BuildCache.Apply("bazel", new[] { "clean" }, Options(bazelRoot))) == "clean", "bazel clean is left alone");
        check(Join(BuildCache.Apply("bazel", new[] { "test", "--disk_cache=", "//..." }, Options(bazelRoot))) == "test --disk_cache= //...", "An explicit --disk_cache wins");
        File.WriteAllText(Path.Combine(bazelRoot, ".bazelrc"), "# build --remote_cache=x\nbuild --disk_cache=~/.cache/bazel\n");
        check(Join(BuildCache.Apply("bazel", new[] { "run", "//:app" }, Options(bazelRoot, remote: "https://c"))) == "run --remote_cache=https://c //:app", ".bazelrc disk_cache wins, commented remote_cache does not");
        check(Join(BuildCache.Apply("bazel", new[] { "build", "//..." }, new BuildCacheOptions { Enabled = false, WorkingDirectory = bazelRoot, BazelDiskCache = "x" })) == "build //...", "Cache off adds nothing");

        var gradleRoot = Path.Combine(root, "gradle");
        Directory.CreateDirectory(gradleRoot);
        var gradle = BuildCache.Apply(Path.Combine(gradleRoot, "gradlew.bat"), new[] { "build" }, Options(gradleRoot));
        check(Join(gradle) == "--build-cache build" && gradle.Environment.Count == 0, "Gradle --build-cache: " + Join(gradle));
        var remote = BuildCache.Apply("gradlew.bat", new[] { "build" }, Options(gradleRoot, remote: "https://cache.example/cache/"));
        var script = Path.Combine(root, "state", "gradle-remote-cache.gradle");
        check(remote.Arguments.Take(2).SequenceEqual(new[] { "--init-script", script }) && remote.Environment[BuildCache.GradleRemoteVariable] == "https://cache.example/cache/"
            && File.ReadAllText(script).Contains("beforeSettings") && File.ReadAllText(script).Contains("push = false"), "Gradle HTTP remote cache through an init script");
        File.WriteAllText(Path.Combine(gradleRoot, "gradle.properties"), "org.gradle.caching=false\n");
        check(Join(BuildCache.Apply("gradlew.bat", new[] { "build" }, Options(gradleRoot))) == "build", "gradle.properties org.gradle.caching wins");
        check(Join(BuildCache.Apply("gradlew.bat", new[] { "--no-build-cache", "build" }, Options(root))) == "--no-build-cache build", "--no-build-cache on the command line wins");

        var cargo = BuildCache.Apply("cargo", new[] { "build", "--release" }, Options(root));
        check(cargo.Environment["RUSTC_WRAPPER"] == sccache && Join(cargo) == "build --release", "Cargo uses sccache when installed");
        check(BuildCache.Apply("cargo", new[] { "build" }, Options(root, sccacheInstalled: false)).Environment.Count == 0, "No sccache, no wrapper");
        check(BuildCache.Apply("cargo", new[] { "build" }, Options(root, wrapper: "other")).Environment.Count == 0, "An existing RUSTC_WRAPPER wins");
        check(BuildCache.Apply("cargo", new[] { "clean" }, Options(root)).Environment.Count == 0, "cargo clean gets no wrapper");

        var configure = BuildCache.Apply("cmake", new[] { "-S", ".", "-B", "build", "-G", "Ninja" }, Options(root));
        check(configure.Arguments.Contains("-DCMAKE_CXX_COMPILER_LAUNCHER=C:/tools/sccache.exe") && configure.Arguments.Contains("-DCMAKE_MSVC_DEBUG_INFORMATION_FORMAT=Embedded"), "CMake configure gets the sccache launcher");
        check(Join(BuildCache.Apply("cmake", new[] { "--build", "build" }, Options(root))) == "--build build", "cmake --build is left alone");
        foreach (var tool in new[] { "zig", "dart", "flutter.bat", "wasm-tools" })
            check(BuildCache.Apply(tool, new[] { "build" }, Options(root)).Arguments.SequenceEqual(new[] { "build" }), tool + " caches by itself");

        File.WriteAllText(Path.Combine(root, "gradlew.bat"), "@echo off\r\n");
        var kotlin = GradleCommand.Create(root, "", new[] { "build" }, buildCache: true);
        check(kotlin.Arguments.Contains("--no-daemon --build-cache build"), "Kotlin Gradle build with --build-cache: " + kotlin.Arguments);
        check(!GradleCommand.Create(root, "", new[] { "build" }).Arguments.Contains("--build-cache"), "Kotlin Gradle cache off");
    }

    private static void BuildSystem(string root, Action<bool, string> check)
    {
        var bazelFolder = Path.Combine(root, "bazel-rust");
        Directory.CreateDirectory(bazelFolder);
        File.WriteAllText(Path.Combine(bazelFolder, "MODULE.bazel"), "module(name = \"app\")\n");
        var project = LanguageProjectFile.Create(bazelFolder, LanguageCatalog.Rust, "app");
        check(project.BuildSystem == LanguageBuildSystem.Bazel && project.Get("BuildCommand") == "bazel build //..." && project.Get("RunCommand") == "bazel run //:app"
            && project.Get("DebugProgram") == @"bazel-bin\app.exe" && project.BuildCacheEnabled, "A Bazel workspace starts as a Bazel project");

        // A fake bazelisk in the project folder stands in for Bazel: "bazel" falls back to it.
        File.WriteAllText(Path.Combine(bazelFolder, "bazelisk.cmd"), "@echo off\r\n");
        var commands = project.CommandsWithNotes("build");
        check(commands.Count == 1 && commands[0].Start.Arguments.IndexOf("bazelisk.cmd", StringComparison.OrdinalIgnoreCase) >= 0 && commands[0].Start.Arguments.Contains("--disk_cache=")
            && commands[0].CacheNote != null && commands[0].CacheNote!.StartsWith("Bazel cache"), "bazel resolves to bazelisk and gets the disk cache: " + commands[0].Start.Arguments);
        project.Set("BuildCache", "false");
        check(!LanguageProjectFile.Load(project.FilePath).Commands("build")[0].Arguments.Contains("--disk_cache"), "Build cache off");

        var nativeFolder = Path.Combine(root, "native-rust");
        Directory.CreateDirectory(nativeFolder);
        var native = LanguageProjectFile.Create(nativeFolder, LanguageCatalog.Rust, "tool");
        check(native.BuildSystem == LanguageBuildSystem.Native && native.Get("BuildCommand") == "cargo build", "Other folders start native");
        native.Set("TestCommand", "cargo test --workspace");
        native.Set("BuildSystem", "bazel");
        var switched = LanguageProjectFile.Load(native.FilePath);
        check(switched.Get("BuildSystem") == "Bazel" && switched.Get("BuildCommand") == "bazel build //..." && switched.Get("RunCommand") == "bazel run //:tool"
            && switched.Get("DebugProgram") == @"bazel-bin\tool.exe" && switched.Get("TestCommand") == "cargo test --workspace", "Switching replaces default commands and keeps edited ones");
        switched.Set("BuildSystem", "Native");
        var back = LanguageProjectFile.Load(native.FilePath);
        check(back.Get("BuildCommand") == "cargo build" && back.Get("DebugProgram") == @"target\debug\tool.exe", "Switching back restores the native defaults");
        foreach (var (name, value) in new[] { ("BuildSystem", "make"), ("BuildCache", "yes"), ("RemoteCache", "ftp://x"), ("RemoteCache", "not a url") })
        {
            var rejected = false;
            try { back.Set(name, value); } catch (ArgumentException) { rejected = true; }
            check(rejected, name + " rejects '" + value + "'");
        }
        back.Set("RemoteCache", "https://cache.example");
        check(LanguageProjectFile.Load(native.FilePath).RemoteCache == "https://cache.example", "Remote cache saved");
        check(BuildCache.Describe(LanguageCatalog.Zig, LanguageBuildSystem.Native).Contains("nothing to add") && BuildCache.Describe(LanguageCatalog.Java, LanguageBuildSystem.Bazel).StartsWith("Bazel"),
            "Per-language cache descriptions");
    }

    private static void Icons(string root, Action<bool, string> check)
    {
        check(LanguageIcons.ForFileName(@"C:\x\BUILD") == LanguageIcons.BazelFile && LanguageIcons.ForFileName(@"C:\x\main.cc") == 0, "Bazel file icon by name");
        var flutter = Path.Combine(root, "flutter-app");
        Directory.CreateDirectory(flutter);
        File.WriteAllText(Path.Combine(flutter, "pubspec.yaml"), "name: app\nenvironment:\n  sdk: ^3.0.0\ndependencies:\n  flutter:\n    sdk: flutter\n");
        var dart = Path.Combine(root, "dart-app");
        Directory.CreateDirectory(dart);
        File.WriteAllText(Path.Combine(dart, "pubspec.yaml"), "name: app\nenvironment:\n  sdk: ^3.0.0\n");
        check(LanguageIcons.ForProject(LanguageProjectFile.Create(flutter, LanguageCatalog.Dart, "app")) == LanguageIcons.FlutterProject, "Flutter projects get the Flutter icon");
        check(LanguageIcons.ForProject(LanguageProjectFile.Create(dart, LanguageCatalog.Dart, "app")) == LanguageCatalog.Dart.IconId, "Dart projects keep the Dart icon");

        var manifest = XDocument.Load(DartTests.FindRepositoryFile(Path.Combine("src", "Vistudio.Vsix", "Icons", "Languages.imagemanifest")));
        XNamespace ns = "http://schemas.microsoft.com/VisualStudio/ImageManifestSchema/2014";
        var images = manifest.Descendants(ns + "Image").ToDictionary(i => (int)i.Attribute("ID")!, i => (string)i.Element(ns + "Source")!.Attribute("Uri")!);
        check(images[LanguageIcons.FlutterProject].EndsWith("FlutterProject.xaml") && images[LanguageIcons.BazelFile].EndsWith("BazelFile.xaml"), "Flutter and Bazel icons in the image manifest");
        var registration = File.ReadAllText(DartTests.FindRepositoryFile(Path.Combine("src", "Vistudio.Vsix", "Registration", "BazelFiles.pkgdef")));
        foreach (var extension in new[] { ".bzl", ".bazel", ".star" })
            check(registration.Contains("ShellFileAssociations\\" + extension + "]\n\"DefaultIconMoniker\"=\"" + LanguageCatalog.IconGuid.ToString("D") + ":" + LanguageIcons.BazelFile + "\"")
                || registration.Contains("ShellFileAssociations\\" + extension + "]\r\n\"DefaultIconMoniker\"=\"" + LanguageCatalog.IconGuid.ToString("D") + ":" + LanguageIcons.BazelFile + "\""),
                extension + " files show the Bazel icon");
    }
}
