using System;
using System.IO;
using System.Linq;
using Vistudio.Core.Languages;
using Vistudio.Core.Packages;

/// <summary>Language project files, commands, trees and templates (T59).</summary>
internal static class LanguageProjectTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        Commands(check);
        foreach (var language in LanguageCatalog.All.Where(l => l != LanguageCatalog.Kotlin))
            Template(Path.Combine(root, "templates", language.Id), language, check);
        Validation(Path.Combine(root, "validation"), check);
        Defaults(Path.Combine(root, "defaults"), check);
        DartDependencies(Path.Combine(root, "dart-dependencies"), check);
    }

    /// <summary>Build of a .dartproj runs pub get first when dependencies are unresolved or stale (fake dart.bat/flutter.bat in the project folder).</summary>
    private static void DartDependencies(string folder, Action<bool, string> check)
    {
        var app = Path.Combine(folder, "app");
        Directory.CreateDirectory(app);
        var pubspec = Path.Combine(app, "pubspec.yaml");
        File.WriteAllText(pubspec, "name: app\r\nenvironment:\r\n  sdk: ^3.0.0\r\n");
        File.WriteAllText(Path.Combine(app, "dart.bat"), "@exit /b 0");
        var project = LanguageProjectFile.Create(app, LanguageCatalog.Dart, "app");
        project.Set("WorkingDirectory", "bin");
        Directory.CreateDirectory(Path.Combine(app, "bin"));
        var past = DateTime.UtcNow.AddHours(-2);
        File.SetLastWriteTimeUtc(pubspec, past);

        var build = project.CommandsWithNotes("build");
        check(build.Count == 2 && build[0].Start.Arguments.IndexOf("dart.bat", StringComparison.OrdinalIgnoreCase) >= 0 && build[0].Start.Arguments.Contains("pub") && build[0].Start.Arguments.Contains("get"),
            "Unresolved Dart package: pub get runs before the build command: " + (build.Count == 0 ? "" : build[0].Start.Arguments));
        check(build.Count == 2 && build[0].Start.WorkingDirectory == app && build[1].Start.WorkingDirectory == Path.Combine(app, "bin"),
            "pub get runs in the package folder, the build in the working directory");
        check(build.Count == 2 && build[0].Note != null && build[0].Note!.Contains("package_config.json"), "The output pane says why pub get runs: " + build[0].Note);
        check(project.Commands("run").Count == 1 && project.Commands("test").Count == 1, "Run and Test resolve by themselves; nothing is added");

        var config = Path.Combine(app, ".dart_tool", "package_config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "{\"configVersion\":2,\"packages\":[]}");
        File.SetLastWriteTimeUtc(config, past.AddMinutes(10));
        check(project.Commands("build").Count == 1 && Vistudio.Core.Dart.DartProject.UnresolvedReason(app) == null, "Resolved package: the build runs alone");
        File.WriteAllText(Path.Combine(app, "pubspec.lock"), "packages: {}\r\n");
        File.SetLastWriteTimeUtc(Path.Combine(app, "pubspec.lock"), past.AddMinutes(20));
        check(Vistudio.Core.Dart.DartProject.UnresolvedReason(app)?.Contains("pubspec.lock") == true, "A pubspec.lock newer than package_config.json is stale");
        File.SetLastWriteTimeUtc(Path.Combine(app, "pubspec.lock"), past.AddMinutes(5));
        File.SetLastWriteTimeUtc(pubspec, past.AddMinutes(30));
        var stale = project.CommandsWithNotes("build");
        check(stale.Count == 2 && stale[0].Note!.Contains("pubspec.yaml changed"), "An edited pubspec.yaml runs pub get again: " + stale[0].Note);

        project.Set("CleanCommand", "cmd.exe /c exit 0");
        File.SetLastWriteTimeUtc(pubspec, past);
        var rebuild = project.Commands("rebuild");
        check(rebuild.Count == 3 && rebuild[1].Arguments.Contains("pub") && rebuild[1].WorkingDirectory == app, "Rebuild resolves again between clean and build");
        project.Set("BuildCommand", "dart pub get && dart analyze");
        File.Delete(config);
        check(project.Commands("build").Count == 2, "A build command that already runs pub get gets no second one");
        project.Set("BuildCommand", "");
        check(project.Commands("build").Count == 0, "No build command: nothing runs");
        project.Set("BuildCommand", "dart analyze --fatal-infos"); // not the Native default, so the Bazel switch keeps it
        project.Set("BuildSystem", "Bazel");
        check(!project.Commands("build").Any(c => c.Arguments.Contains("pub")), "Bazel builds resolve their own dependencies");

        var flutter = Path.Combine(folder, "flutter_app");
        Directory.CreateDirectory(flutter);
        File.WriteAllText(Path.Combine(flutter, "pubspec.yaml"), "name: flutter_app\r\ndependencies:\r\n  flutter:\r\n    sdk: flutter\r\n");
        File.WriteAllText(Path.Combine(flutter, "flutter.bat"), "@exit /b 0");
        var flutterProject = LanguageProjectFile.Create(flutter, LanguageCatalog.Dart, "flutter_app");
        var flutterBuild = flutterProject.Commands("build");
        check(flutterBuild.Count == 2 && flutterBuild[0].Arguments.IndexOf("flutter.bat", StringComparison.OrdinalIgnoreCase) >= 0 && flutterBuild[0].Arguments.Contains("pub"), "Flutter packages resolve with flutter pub get");

        var workspace = Path.Combine(folder, "workspace");
        var member = Path.Combine(workspace, "packages", "member");
        Directory.CreateDirectory(member);
        File.WriteAllText(Path.Combine(workspace, "pubspec.yaml"), "name: _\r\nworkspace:\r\n  - packages/member\r\n");
        File.WriteAllText(Path.Combine(member, "pubspec.yaml"), "name: member\r\nresolution: workspace\r\n");
        check(Vistudio.Core.Dart.DartProject.UnresolvedReason(member) != null, "Unresolved pub workspace member");
        Directory.CreateDirectory(Path.Combine(workspace, ".dart_tool"));
        File.SetLastWriteTimeUtc(Path.Combine(member, "pubspec.yaml"), past);
        File.WriteAllText(Path.Combine(workspace, ".dart_tool", "package_config.json"), "{}");
        check(Vistudio.Core.Dart.DartProject.UnresolvedReason(member) == null, "A workspace member uses the workspace root's package_config.json");
        check(Vistudio.Core.Dart.DartProject.UnresolvedReason(Path.Combine(folder, "no-package")) == null, "A folder without pubspec.yaml needs no pub get");
    }

    private static void Commands(Action<bool, string> check)
    {
        var chain = LanguageCommandLine.Parse("conan install . --output-folder=build && cmake -S . -B \"my build\"");
        check(chain.Count == 2 && chain[0].Program == "conan" && chain[0].Arguments.SequenceEqual(new[] { "install", ".", "--output-folder=build" }),
            "First command of a chain");
        check(chain[1].Arguments.Last() == "my build" && chain[1].ToString() == "cmake -S . -B \"my build\"", "Quotes group and are restored: " + chain[1]);
        check(LanguageCommandLine.Parse("").Count == 0 && LanguageCommandLine.Parse("   ").Count == 0, "Empty command setting runs nothing");
        foreach (var bad in new[] { "cargo build | more", "cargo build > log.txt", "cargo build & del x", "cargo build &&", "&& cargo build", "a && && b", "x \"unbalanced" })
        {
            var rejected = false;
            try { LanguageCommandLine.Parse(bad); } catch (ArgumentException) { rejected = true; }
            check(rejected, "Rejected without a shell: " + bad);
        }
    }

    private static void Template(string folder, LanguageInfo language, Action<bool, string> check)
    {
        var wrapper = language == LanguageCatalog.Java ? WrapperFolder() : null;
        var project = LanguageProjectTemplate.Create(folder, language, "My App", wrapper);
        check(project.FilePath == Path.Combine(folder, "My App" + language.ProjectExtension), language.Id + ": project file named after the project");
        var loaded = LanguageProjectFile.Load(project.FilePath);
        check(loaded.Language == language && loaded.Name == "My App" && Guid.TryParse(loaded.ProjectGuid, out _), language.Id + ": loads with language, name and guid");
        check(loaded.Get("BuildCommand").Length != 0 && loaded.Get("RunCommand").Length != 0, language.Id + ": has build and run commands");
        check(LanguageCommandLine.Parse(loaded.Get("BuildCommand")).Count >= 1, language.Id + ": build command parses");
        var manifests = loaded.FindManifests();
        // Zig's build.zig.zon carries a fingerprint only `zig init` generates, and a WIT world is optional for a module.
        var noManifest = language == LanguageCatalog.Wasm || language == LanguageCatalog.Zig;
        check(manifests.Count == 1 && manifests[0].Manager.Ecosystem == language.Packages || noManifest && manifests.Count == 0,
            language.Id + ": the template's manifest belongs to the language's package manager (" + manifests.Count + ")");
        if (manifests.Count == 1)
        {
            var declared = manifests[0].Manager.ReadManifest(manifests[0].ManifestPath);
            check(language != LanguageCatalog.Dart || declared.Any(p => p.Name == "test" && p.Scope == PackageScope.Development), language.Id + ": declared packages read: " + declared.Count);
        }
        var tree = LanguageProjectTree.Build(loaded, manifests.Select(m => PackageManagers.Node(m, null, "Not checked yet.")));
        if (manifests.Count == 1)
        {
            // Identity survives caption changes, so Solution Explorer keeps the node (expansion, selection) when versions arrive.
            var declared = manifests[0].Manager.ReadManifest(manifests[0].ManifestPath);
            var outdated = declared.Select(p => p.WithVersions("0.0.1", "0.0.2", "9.9.9")).ToList();
            var before = PackageManagers.Node(manifests[0], declared, "Not checked yet.");
            var after = PackageManagers.Node(manifests[0], outdated, null);
            check(before.Key == after.Key && (declared.Count == 0 || before.Caption != after.Caption), language.Id + ": the Packages node keeps its key when the outdated count changes");
            check(declared.Count == 0 || PackageManagers.PackageNode(declared[0]).Key == PackageManagers.PackageNode(outdated[0]).Key,
                language.Id + ": a package keeps its key when its versions arrive");
        }
        var top = tree.Children.Select(c => c.Caption).ToList();
        check(tree.Caption == "My App" && (manifests.Count == 0 || top[0].StartsWith("Packages (")), language.Id + ": packages come first: " + string.Join(", ", top));
        check(top.Contains("src") || language == LanguageCatalog.Dart && top.Contains("bin"), language.Id + ": sources are listed");
        Directory.CreateDirectory(Path.Combine(folder, "target"));
        Directory.CreateDirectory(Path.Combine(folder, "zig-out"));
        var refreshed = LanguageProjectTree.Build(loaded, Enumerable.Empty<Vistudio.Core.Projects.ProjectTreeNode>()).Children.Select(c => c.Caption).ToList();
        check(!refreshed.Contains("target") && !refreshed.Contains("zig-out"), language.Id + ": build output is not listed");
        var again = false;
        try { LanguageProjectTemplate.Create(folder, language, "My App", wrapper); } catch (IOException) { again = true; }
        check(again, language.Id + ": a template never overwrites a project");
        check(LanguageProjectFile.Find(folder) == project.FilePath, language.Id + ": the folder's only project file is found");
        var stub = Path.Combine(folder, "stub.xml");
        File.WriteAllText(stub, LanguageProjectTemplate.Stub(language, "$projectname$"));
        var read = LanguageProjectTemplate.TryReadStub(stub);
        check(read != null && read.Value.Language == language && read.Value.ProjectName == language.Id, language.Id + ": stub with an unexpanded name falls back to the folder name");
        check(LanguageProjectTemplate.TryReadStub(project.FilePath) == null, language.Id + ": a real project file is not a stub");
    }

    private static void Validation(string folder, Action<bool, string> check)
    {
        var project = LanguageProjectTemplate.Create(folder, LanguageCatalog.Rust, "demo");
        project.Set("RunCommand", "cargo run --release -- \"a b\"");
        check(LanguageProjectFile.Load(project.FilePath).Get("RunCommand") == "cargo run --release -- \"a b\"", "Set saves and reloads");
        foreach (var (name, value) in new[] { ("BuildCommand", "cargo build | tee x"), ("ProjectGuid", "{}"), ("Language", "zig"), ("DebugProgram", "a*.exe"), ("RunCommand", "a\r\nb") })
        {
            var rejected = false;
            try { project.Set(name, value); } catch (ArgumentException) { rejected = true; }
            check(rejected, "Set rejects " + name + " = " + value.Replace("\r\n", "\\r\\n"));
        }
        check(LanguageProjectFile.Load(project.FilePath).Get("BuildCommand") == "cargo build", "Rejected values leave the file unchanged");
        check(project.DebugProgram == Path.Combine(folder, "target", "debug", "demo.exe"), "Debug program resolves under the project folder: " + project.DebugProgram);
        var text = File.ReadAllText(project.FilePath);
        File.WriteAllText(project.FilePath, text.Replace("<Language>rust</Language>", "<Language>zig</Language>"));
        Throws<InvalidDataException>(() => LanguageProjectFile.Load(project.FilePath), "Language must match the extension", check);
        File.WriteAllText(project.FilePath, text.Replace("<BuildCommand>cargo build</BuildCommand>", "<BuildCommand>cargo build > x</BuildCommand>"));
        Throws<InvalidDataException>(() => LanguageProjectFile.Load(project.FilePath), "Invalid command in the file is rejected on load", check);
        File.WriteAllText(project.FilePath, "<!DOCTYPE x [<!ENTITY a \"b\">]>" + text);
        Throws<System.Xml.XmlException>(() => LanguageProjectFile.Load(project.FilePath), "DTDs are prohibited", check);
        File.WriteAllText(project.FilePath, text.Replace("VistudioSchemaVersion=\"1\"", "VistudioSchemaVersion=\"2\""));
        Throws<InvalidDataException>(() => LanguageProjectFile.Load(project.FilePath), "Unknown schema version is rejected", check);
        File.WriteAllText(project.FilePath, text);
        var missing = LanguageProjectFile.Load(project.FilePath);
        missing.Set("BuildCommand", "vistudio-no-such-tool build");
        Throws<FileNotFoundException>(() => missing.Commands("build"), "A missing program is reported", check);
        missing.Set("BuildCommand", "cmd.exe /c exit 0");
        missing.Set("CleanCommand", "cmd.exe /c exit 0");
        var start = missing.Commands("rebuild");
        check(start.Count == 2 && start[1].FileName.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase) && start[1].WorkingDirectory == folder,
            "Rebuild runs clean, then build, in the project folder");
        File.WriteAllText(Path.Combine(folder, "tool.bat"), "@exit /b 0");
        missing.Set("BuildCommand", "tool.bat \"x y\"");
        var batch = missing.Commands("build")[0];
        check(batch.FileName.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase) && batch.Arguments.Contains("\"x y\"") && batch.Arguments.StartsWith("/d /v:off /s /c"),
            "Batch tools run through cmd.exe with quoted arguments: " + batch.Arguments);
    }

    private static void Defaults(string folder, Action<bool, string> check)
    {
        Directory.CreateDirectory(folder);
        var plain = LanguageProjectDefaults.For(LanguageCatalog.Cpp, folder, "app");
        check(plain["BuildCommand"].StartsWith("cmake -S . -B build && cmake --build build"), "C++ without Conan configures and builds with CMake");
        File.WriteAllText(Path.Combine(folder, "conanfile.txt"), "[requires]\r\n");
        var conan = LanguageProjectDefaults.For(LanguageCatalog.Cpp, folder, "app");
        check(conan["BuildCommand"].StartsWith("conan install . --output-folder=build") && conan["BuildCommand"].Contains("conan_toolchain.cmake"), "C++ with Conan installs packages first");
        File.WriteAllText(Path.Combine(folder, "pubspec.yaml"), "name: x\r\ndependencies:\r\n  flutter:\r\n    sdk: flutter\r\n");
        check(LanguageProjectDefaults.For(LanguageCatalog.Dart, folder, "x")["RunCommand"] == "flutter run -d windows", "Flutter packages run on Windows desktop");
        var ide = Environment.GetEnvironmentVariable("VSAPPIDDIR");
        try
        {
            Environment.SetEnvironmentVariable("VSAPPIDDIR", @"C:\VS\Common7\IDE\");
            check(LanguageProjectDefaults.InstallFolders(LanguageCatalog.Cpp).Single() == @"C:\VS\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin",
                "C and C++ commands look in the running Visual Studio's bundled CMake first");
        }
        finally { Environment.SetEnvironmentVariable("VSAPPIDDIR", ide); }
        var project = LanguageProjectTemplate.Create(Path.Combine(folder, "path-check"), LanguageCatalog.Cpp, "PathCheck");
        project.Set("BuildCommand", "cmd.exe /c exit 0");
        var start = project.Commands("build")[0];
        check(start.EnvironmentVariables["PATH"]!.StartsWith(project.DirectoryPath + ";", StringComparison.OrdinalIgnoreCase),
            "Commands pass their search folders on to the tools they start: " + start.EnvironmentVariables["PATH"]!.Split(';')[0]);
        check(LanguageProjectTemplate.Identifier("My-App 2") == "my_app_2" && LanguageProjectTemplate.Identifier("42") == "app_42" && LanguageProjectTemplate.Identifier("--") == "app",
            "Identifiers for crates, packages and targets");
    }

    private static void Throws<T>(Action action, string message, Action<bool, string> check) where T : Exception
    {
        try { action(); }
        catch (T) { check(true, message); return; }
        catch (Exception e) { check(false, message + " (threw " + e.GetType().Name + ": " + e.Message + ")"); return; }
        check(false, message + " (no exception)");
    }

    private static string WrapperFolder()
    {
        var gradlew = DartTests.FindRepositoryFile(Path.Combine("samples", "kotlin-jvm", "gradlew.bat"));
        return Path.GetDirectoryName(gradlew)!;
    }
}
