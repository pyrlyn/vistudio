using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Vistudio.Core;
using Vistudio.Core.Projects;

internal static class LaunchTargetTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "launch project");
        void Write(string relative, string text) { var path = Path.Combine(folder, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        Write("gradlew.bat", "@echo off\r\n");
        Write("app/src/main/kotlin/demo/Main.kt", "package demo\n\nfun main() = println(\"hi\")\n");
        Write("app/src/main/kotlin/demo/Util.kt", "package demo\n\nclass Holder { fun main() {} }\nfun helper() = 1\n");
        Write("app/src/test/kotlin/demo/MainTest.kt", "package demo\n\nfun main() {}\n");
        var project = KotlinProjectFile.Create(folder, new KotlinSettings());
        project.Save();
        // The conventional fallback applies to the root placeholder module, so point it at the app module.
        void SetModulePath(string path)
        {
            var xml = XDocument.Load(project.FilePath);
            xml.Root!.Elements("ItemGroup").Elements("Module").Single().SetElementValue("Path", path);
            xml.Save(project.FilePath);
        }
        SetModulePath("app");
        KotlinLaunchTarget Resolve() => KotlinLaunchTarget.Resolve(KotlinProjectFile.Load(project.FilePath));
        void Set(string name, string value)
        {
            var xml = XDocument.Load(project.FilePath);
            xml.Root!.Elements("PropertyGroup").Single(e => (string?)e.Attribute("Label") == "Vistudio").SetElementValue(name, value);
            xml.Save(project.FilePath);
        }
        void Rejected(string message)
        {
            try { Resolve(); throw new Exception(message); }
            catch (InvalidDataException) { check(true, message); }
            catch (FileNotFoundException) { check(true, message); }
        }

        var target = Resolve();
        check(target.MainClass == "demo.MainKt" && target.SourceFile == Path.Combine(folder, "app", "src", "main", "kotlin", "demo", "Main.kt"),
            "The single top-level main in production sources is detected; member mains and test sources are ignored");
        check(target.WorkingDirectory == folder && target.Arguments.Count == 0, "Defaults run from the Gradle root without arguments");

        Write("app/src/main/kotlin/demo/tool-runner.kt", "@file:JvmName(\"ToolRunner\")\npackage demo\n\nsuspend fun main(args: Array<String>) {}\n");
        Rejected("Several entry points require DebugMainClass");
        Set("DebugMainClass", "demo.ToolRunner");
        target = Resolve();
        check(target.MainClass == "demo.ToolRunner" && target.SourceFile.EndsWith("tool-runner.kt"), "DebugMainClass selects among candidates and honors @file:JvmName and suspend main");
        Set("DebugMainClass", "demo.Missing");
        Rejected("An unknown DebugMainClass without a source file is rejected");
        Set("DebugMainClass", "");
        Set("DebugSourceFile", "app/src/main/kotlin/demo/Main.kt");
        Set("DebugArguments", "one \"two words\" \\\"q\\\"  ");
        Set("DebugWorkingDirectory", "app");
        target = Resolve();
        check(target.MainClass == "demo.MainKt" && target.Arguments.SequenceEqual(new[] { "one", "two words", "\"q\"" }) && target.WorkingDirectory == Path.Combine(folder, "app"),
            "DebugSourceFile, quoted DebugArguments and a relative working directory are honored");
        Set("DebugSourceFile", "app/src/main/kotlin/demo/Util.kt");
        Rejected("A source file without a top-level main needs DebugMainClass");
        Set("DebugMainClass", "demo.UtilKt");
        check(Resolve().MainClass == "demo.UtilKt", "An explicit class and source are trusted together");
        Set("DebugSourceFile", "app/missing.kt");
        Rejected("A missing DebugSourceFile is rejected");
        Set("DebugSourceFile", "");
        Set("DebugMainClass", "");
        Set("DebugArguments", "\"unterminated");
        Rejected("Unterminated quotes in DebugArguments are rejected");
        Set("DebugArguments", "");
        File.Delete(Path.Combine(folder, "app", "src", "main", "kotlin", "demo", "Main.kt"));
        File.Delete(Path.Combine(folder, "app", "src", "main", "kotlin", "demo", "tool-runner.kt"));
        Rejected("No entry point produces an explicit configuration hint");
        check(KotlinLaunchTarget.FacadeName("my-app.utils") == "My_app_utilsKt" && KotlinLaunchTarget.FacadeName("main") == "MainKt", "Facade names follow Kotlin file naming");

        // Fast up-to-date check: inputs only, deterministic timestamps.
        var inputs = Path.Combine(root, "inputs");
        var source = Path.Combine(inputs, "src", "A.kt");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(Path.Combine(inputs, "build", "classes"));
        File.WriteAllText(source, "fun a() = 1");
        File.WriteAllText(Path.Combine(inputs, "build", "classes", "A.class"), "x");
        File.WriteAllText(Path.Combine(inputs, "vistudio.kotlinproj"), "<Project />");
        var past = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var path in Directory.GetFiles(inputs, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(path, past);
        foreach (var path in Directory.GetDirectories(inputs, "*", SearchOption.AllDirectories).Concat(new[] { inputs })) Directory.SetLastWriteTimeUtc(path, past);
        var built = GradleInputs.Capture(inputs);
        check(built.Files.Count == 1 && built.Files[0] == source, "Only source inputs are captured; outputs and IDE metadata are excluded");
        check(!GradleInputs.IsUpToDate(inputs, null), "Never built in this session means out of date");
        check(GradleInputs.IsUpToDate(inputs, built), "Unchanged inputs are up to date");
        File.WriteAllText(Path.Combine(inputs, "build", "classes", "B.class"), "y");
        File.WriteAllText(Path.Combine(inputs, "vistudio.kotlinproj"), "<Project><!-- reimported --></Project>");
        Directory.CreateDirectory(Path.Combine(inputs, ".kotlin", "sessions"));
        Directory.SetLastWriteTimeUtc(inputs, DateTime.UtcNow);
        check(GradleInputs.IsUpToDate(inputs, built), "Build outputs, tool folders, IDE metadata and directory timestamps are not inputs");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(5));
        check(!GradleInputs.IsUpToDate(inputs, built), "An edited source makes the project out of date");
        File.SetLastWriteTimeUtc(source, past);
        var added = Path.Combine(inputs, "src", "B.kt");
        File.WriteAllText(added, "fun b() = 2");
        File.SetLastWriteTimeUtc(added, past);
        check(!GradleInputs.IsUpToDate(inputs, built), "An added source makes the project out of date even with an old timestamp");
        File.Delete(added);
        check(GradleInputs.IsUpToDate(inputs, built), "Removing the added file restores the captured input set");
        File.Delete(source);
        check(!GradleInputs.IsUpToDate(inputs, built), "A deleted source makes the project out of date");
    }
}
