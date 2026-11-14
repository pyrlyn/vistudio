using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Vistudio.Core.Templates;

namespace Vistudio.Core.Languages
{
    /// <summary>
    /// New Project templates of the language project types: a minimal program in the language's own layout, its
    /// package manifest, and the project file. The layouts follow what each tool's own <c>new</c>/<c>init</c> command
    /// creates, written here so that creating a project needs no tool installed. Versions are not pinned: manifests
    /// declare no dependencies except the Dart test package the SDK's own template also adds.
    /// </summary>
    public static class LanguageProjectTemplate
    {
        public const string StubElement = "VistudioLanguageTemplate";

        /// <summary>
        /// Lower-case identifier usable as a Cargo crate, Dart package, Zig executable and CMake target name:
        /// letters, digits and '_', starting with a letter.
        /// </summary>
        public static string Identifier(string projectName)
        {
            var text = new string((projectName ?? "").ToLowerInvariant().Select(c => c >= 'a' && c <= 'z' || c >= '0' && c <= '9' ? c : '_').ToArray()).Trim('_');
            while (text.Contains("__")) text = text.Replace("__", "_");
            if (text.Length == 0) return "app";
            return char.IsDigit(text[0]) ? "app_" + text : text;
        }

        /// <summary>Text files of the new project keyed by relative path with forward slashes.</summary>
        public static IReadOnlyDictionary<string, string> Files(LanguageInfo language, string projectName)
        {
            var id = Identifier(projectName);
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            switch (language.Id)
            {
                case "rust":
                    files[".gitignore"] = Lines("/target", ".vs/");
                    files["Cargo.toml"] = Lines("[package]", "name = \"" + id + "\"", "version = \"0.1.0\"", "edition = \"2024\"", "", "[dependencies]");
                    files["src/main.rs"] = Lines("fn main() {", "    println!(\"Hello from {}!\", add(40, 2));", "}", "",
                        "fn add(a: i32, b: i32) -> i32 {", "    a + b", "}", "", "#[cfg(test)]", "mod tests {", "    use super::*;", "",
                        "    #[test]", "    fn adds() {", "        assert_eq!(add(40, 2), 42);", "    }", "}");
                    break;
                case "zig":
                    files[".gitignore"] = Lines(".zig-cache/", "zig-out/", ".vs/");
                    files["build.zig"] = Lines(
                        "const std = @import(\"std\");", "",
                        "pub fn build(b: *std.Build) void {",
                        "    const target = b.standardTargetOptions(.{});",
                        "    const optimize = b.standardOptimizeOption(.{});", "",
                        "    const exe = b.addExecutable(.{",
                        "        .name = \"" + id + "\",",
                        "        .root_module = b.createModule(.{",
                        "            .root_source_file = b.path(\"src/main.zig\"),",
                        "            .target = target,",
                        "            .optimize = optimize,",
                        "        }),",
                        "    });",
                        "    b.installArtifact(exe);", "",
                        "    const run_step = b.step(\"run\", \"Run the app\");",
                        "    const run_cmd = b.addRunArtifact(exe);",
                        "    run_cmd.step.dependOn(b.getInstallStep());",
                        "    run_step.dependOn(&run_cmd.step);", "",
                        "    const tests = b.addTest(.{ .root_module = exe.root_module });",
                        "    const test_step = b.step(\"test\", \"Run unit tests\");",
                        "    test_step.dependOn(&b.addRunArtifact(tests).step);",
                        "}");
                    files["src/main.zig"] = Lines("const std = @import(\"std\");", "",
                        "pub fn main() void {", "    std.debug.print(\"Hello from {d}!\\n\", .{add(40, 2)});", "}", "",
                        "pub fn add(a: i32, b: i32) i32 {", "    return a + b;", "}", "",
                        "test \"add\" {", "    try std.testing.expectEqual(@as(i32, 42), add(40, 2));", "}");
                    break;
                case "dart":
                    files[".gitignore"] = Lines(".dart_tool/", "build/", ".vs/");
                    files["pubspec.yaml"] = Lines("name: " + id, "description: A Dart console program.", "version: 1.0.0", "publish_to: none", "",
                        "environment:", "  sdk: ^3.0.0", "", "dev_dependencies:", "  test: ^1.25.0");
                    files["bin/" + id + ".dart"] = Lines("import 'package:" + id + "/" + id + ".dart';", "",
                        "void main(List<String> arguments) {", "  print('Hello from ${add(40, 2)}!');", "}");
                    files["lib/" + id + ".dart"] = Lines("int add(int a, int b) => a + b;");
                    files["test/" + id + "_test.dart"] = Lines("import 'package:" + id + "/" + id + ".dart';", "import 'package:test/test.dart';", "",
                        "void main() {", "  test('add', () {", "    expect(add(40, 2), 42);", "  });", "}");
                    break;
                case "c":
                case "cpp":
                    var cpp = language.Id == "cpp";
                    var source = cpp ? "src/main.cpp" : "src/main.c";
                    files[".gitignore"] = Lines("build/", ".vs/", "CMakeUserPresets.json");
                    files["CMakeLists.txt"] = Lines("cmake_minimum_required(VERSION 3.20)",
                        "project(" + id + " LANGUAGES " + (cpp ? "CXX" : "C") + ")", "",
                        cpp ? "set(CMAKE_CXX_STANDARD 20)" : "set(CMAKE_C_STANDARD 17)",
                        cpp ? "set(CMAKE_CXX_STANDARD_REQUIRED ON)" : "set(CMAKE_C_STANDARD_REQUIRED ON)", "",
                        "add_executable(" + id + " " + source + ")", "",
                        "enable_testing()", "add_test(NAME " + id + "_runs COMMAND " + id + ")");
                    // Packages added in Solution Explorer go to [requires]; find_package/target_link_libraries stay the user's call.
                    files["conanfile.txt"] = Lines("[requires]", "", "[generators]", "CMakeDeps", "CMakeToolchain");
                    files[source] = cpp
                        ? Lines("#include <iostream>", "", "static int add(int a, int b) { return a + b; }", "",
                            "int main() {", "    std::cout << \"Hello from \" << add(40, 2) << \"!\\n\";", "    return 0;", "}")
                        : Lines("#include <stdio.h>", "", "static int add(int a, int b) { return a + b; }", "",
                            "int main(void) {", "    printf(\"Hello from %d!\\n\", add(40, 2));", "    return 0;", "}");
                    break;
                case "wasm":
                    files[".gitignore"] = Lines("build/", "wit/deps/", ".vs/");
                    // A WASI command: wasmtime calls _start; fd_write prints through the host's stdout.
                    files["src/main.wat"] = Lines("(module",
                        "  (import \"wasi_snapshot_preview1\" \"fd_write\" (func $fd_write (param i32 i32 i32 i32) (result i32)))",
                        "  (memory (export \"memory\") 1)",
                        "  (data (i32.const 16) \"Hello from WebAssembly!\\n\")", "",
                        "  (func $add (export \"add\") (param $a i32) (param $b i32) (result i32)",
                        "    (i32.add (local.get $a) (local.get $b)))", "",
                        "  (func $main (export \"_start\")",
                        "    ;; iovec at 0: pointer 16, length 24",
                        "    (i32.store (i32.const 0) (i32.const 16))",
                        "    (i32.store (i32.const 4) (i32.const 24))",
                        "    (drop (call $fd_write (i32.const 1) (i32.const 0) (i32.const 1) (i32.const 8)))))");
                    break;
                case "java":
                    var pkg = KotlinProjectTemplate.PackageFor(projectName);
                    files[".gitignore"] = Lines(".gradle/", "build/", ".vs/", "*.user");
                    files["gradle/wrapper/gradle-wrapper.properties"] = KotlinProjectTemplate.WrapperProperties();
                    files["settings.gradle.kts"] = Lines("rootProject.name = " + KotlinProjectTemplate.KotlinString(KotlinProjectTemplate.GradleProjectName(projectName)));
                    files["build.gradle.kts"] = Lines("plugins {", "    application", "}", "", "repositories {", "    mavenCentral()", "}", "",
                        "dependencies {", "}", "", "application {", "    mainClass = \"" + pkg + ".Main\"", "}");
                    files["src/main/java/" + pkg + "/Main.java"] = Lines("package " + pkg + ";", "", "public final class Main {",
                        "    static int add(int a, int b) {", "        return a + b;", "    }", "",
                        "    public static void main(String[] args) {", "        System.out.println(\"Hello from \" + add(40, 2) + \"!\");", "    }", "}");
                    break;
                default:
                    throw new ArgumentException("No project template for " + language.DisplayName + ".");
            }
            return files;
        }

        /// <summary>
        /// Writes the project into <paramref name="folder"/> (created when missing) and returns its project file.
        /// Existing files are never replaced. <paramref name="gradleWrapper"/> supplies the Java template's wrapper.
        /// </summary>
        public static LanguageProjectFile Create(string folder, LanguageInfo language, string projectName, string? gradleWrapper = null)
        {
            folder = Path.GetFullPath(folder);
            var text = Files(language, projectName);
            var projectFile = projectName + language.ProjectExtension;
            var wrapper = language == LanguageCatalog.Java ? KotlinProjectTemplate.WrapperFiles : Array.Empty<string>();
            var existing = text.Keys.Concat(wrapper).Append(projectFile).Where(f => File.Exists(Path.Combine(folder, f))).ToArray();
            if (existing.Length != 0)
                throw new IOException("The folder already contains " + string.Join(", ", existing) + ". Choose an empty location for the new " + language.DisplayName + " project: " + folder);
            if (wrapper.Count != 0) KotlinProjectTemplate.CheckWrapper(gradleWrapper ?? throw new ArgumentException("The Java template needs the Gradle wrapper folder."));
            foreach (var file in text)
            {
                var path = Path.Combine(folder, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.Value, new UTF8Encoding(false));
            }
            if (wrapper.Count != 0) KotlinProjectTemplate.CopyWrapper(folder, gradleWrapper!);
            return LanguageProjectFile.Create(folder, language, projectName, Identifier(projectName));
        }

        /// <summary>The stub project file of a .vstemplate: the language plus the name typed in the New Project dialog.</summary>
        public static string Stub(LanguageInfo language, string projectName) =>
            new XDocument(new XElement(StubElement, new XAttribute("Language", language.Id), new XAttribute("ProjectName", projectName))).ToString();

        /// <summary>Reads a template stub; returns null for any other file (for example a real project file).</summary>
        public static (LanguageInfo Language, string ProjectName)? TryReadStub(string path)
        {
            XDocument document;
            try
            {
                using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 }))
                    document = XDocument.Load(reader);
            }
            catch (XmlException) { return null; }
            if (document.Root?.Name != StubElement) return null;
            var language = LanguageCatalog.ById((string?)document.Root.Attribute("Language") ?? "");
            if (language == null || language == LanguageCatalog.Kotlin)
                throw new InvalidDataException("Unknown language project template: " + (string?)document.Root.Attribute("Language"));
            var name = (string?)document.Root.Attribute("ProjectName") ?? "";
            if (name.Length == 0 || name.Contains("$")) name = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))) ?? "";
            return (language, name);
        }

        private static string Lines(params string[] lines) => string.Join("\r\n", lines) + "\r\n";
    }
}
