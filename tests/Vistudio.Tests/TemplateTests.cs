using System;
using System.IO;
using System.Linq;
using Vistudio.Core.Projects;
using Vistudio.Core.Templates;

/// <summary>Generator checks for the New Project templates; <see cref="Generate"/> writes every template for real Gradle runs.</summary>
internal static class TemplateTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        check(KotlinProjectTemplate.PackageFor("My Kotlin-App 2") == "mykotlinapp2", "package names keep ASCII letters and digits");
        check(KotlinProjectTemplate.PackageFor("1st") == "app1st", "package names never start with a digit");
        check(KotlinProjectTemplate.PackageFor("Object") == "objectapp", "package names avoid Kotlin keywords");
        check(KotlinProjectTemplate.PackageFor("Класс") == "app", "package names fall back to app");
        check(KotlinProjectTemplate.GradleProjectName("a:b/c") == "a-b-c", "Gradle project names drop separators");
        check(KotlinProjectTemplate.KotlinString("a\"$b\\") == "\"a\\\"\\$b\\\\\"", "Kotlin strings escape quotes, templates and backslashes");

        var wrapper = Path.Combine(root, "wrapper");
        Directory.CreateDirectory(Path.Combine(wrapper, "gradle", "wrapper"));
        File.WriteAllText(Path.Combine(wrapper, "gradlew.bat"), "@rem test wrapper");
        File.WriteAllBytes(Path.Combine(wrapper, "gradle", "wrapper", "gradle-wrapper.jar"), new byte[] { 0x50, 0x4B, 3, 4 });

        foreach (KotlinTemplateKind kind in Enum.GetValues(typeof(KotlinTemplateKind)))
        {
            var folder = Path.Combine(root, "templates", kind.ToString());
            var options = new KotlinTemplateOptions(kind, "Demo " + kind);
            var files = KotlinProjectTemplate.Files(options);
            check(files["settings.gradle.kts"].Contains("rootProject.name = \"Demo " + kind + "\""), kind + " names the root project");
            check(files["gradle/wrapper/gradle-wrapper.properties"].Contains("gradle-8.14.7-bin.zip"), kind + " pins the sample Gradle version");
            check(files.Keys.Any(f => f.EndsWith("Test.kt", StringComparison.Ordinal)), kind + " has a kotlin.test test");
            check(files.Keys.All(f => !f.Contains("\\") && !f.StartsWith("/", StringComparison.Ordinal)), kind + " uses relative forward-slash paths");
            var project = KotlinProjectTemplate.Create(folder, options, wrapper);
            check(File.Exists(Path.Combine(folder, "gradlew.bat")) && File.Exists(Path.Combine(folder, "gradle", "wrapper", "gradle-wrapper.jar")), kind + " copies the wrapper");
            var loaded = KotlinProjectFile.Load(project.FilePath);
            check(loaded.FilePath.EndsWith(KotlinProjectFile.FileName, StringComparison.Ordinal), kind + " writes vistudio.kotlinproj");
            check(loaded.Get("TestTasks") == KotlinProjectTemplate.TestTasks(options) && loaded.Get("RunTasks") == KotlinProjectTemplate.RunTasks(options), kind + " stores its Gradle tasks");
            check(loaded.Name == kind.ToString(), kind + " project name follows the folder");
            var threw = false;
            try { KotlinProjectTemplate.Create(folder, options, wrapper); } catch (IOException) { threw = true; }
            check(threw, kind + " never overwrites an existing project");
        }

        var console = KotlinProjectTemplate.Files(new KotlinTemplateOptions(KotlinTemplateKind.JvmConsole, "Hello"));
        check(console["build.gradle.kts"].Contains("kotlin(\"jvm\") version \"2.4.21\"") && console["build.gradle.kts"].Contains("mainClass.set(\"hello.MainKt\")"), "console app has the sample Kotlin version and main class");
        check(console.ContainsKey("src/main/kotlin/hello/Main.kt") && console.ContainsKey("src/test/kotlin/hello/MainTest.kt"), "console app sources follow the package");
        var library = KotlinProjectTemplate.Files(new KotlinTemplateOptions(KotlinTemplateKind.JvmLibrary, "Lib"));
        check(library["build.gradle.kts"].Contains("`java-library`") && !library["build.gradle.kts"].Contains("application"), "library uses java-library without application");
        var mixed = KotlinProjectTemplate.Files(new KotlinTemplateOptions(KotlinTemplateKind.KotlinJava, "Mixed"));
        check(mixed.ContainsKey("src/main/java/mixed/Greeter.java") && mixed["src/main/kotlin/mixed/Main.kt"].Contains("Greeter("), "Kotlin + Java app calls Java from Kotlin");

        var web = new KotlinTemplateOptions(KotlinTemplateKind.Multiplatform, "Web", new[] { "wasmJs", "js" });
        check(web.Targets.SequenceEqual(new[] { "js", "wasmJs" }), "targets keep the canonical order");
        var webFiles = KotlinProjectTemplate.Files(web);
        check(webFiles.ContainsKey("src/jsMain/kotlin/web/Platform.js.kt") && webFiles.ContainsKey("src/wasmJsMain/kotlin/web/Platform.wasmJs.kt")
            && !webFiles.ContainsKey("src/jvmMain/kotlin/web/Platform.jvm.kt"), "only chosen targets get actual declarations");
        check(!webFiles["build.gradle.kts"].Contains("jvm {") && !webFiles["build.gradle.kts"].Contains("mingwX64"), "unchosen targets are not declared");
        check(KotlinProjectTemplate.RunTasks(web) == "jsNodeDevelopmentRun", "first chosen target supplies the run task");
        var all = KotlinProjectTemplate.Files(new KotlinTemplateOptions(KotlinTemplateKind.Multiplatform, "All"));
        check(all["build.gradle.kts"].Contains("entryPoint = \"all.main\"") && all["build.gradle.kts"].Contains("mainClass.set(\"all.MainKt\")"), "native and JVM entry points follow the package");
        Reject(() => new KotlinTemplateOptions(KotlinTemplateKind.Multiplatform, "None", new string[0]), check, "no targets are rejected");
        Reject(() => new KotlinTemplateOptions(KotlinTemplateKind.Multiplatform, "Ios", new[] { "iosArm64" }), check, "unknown targets are rejected");
        var compose = KotlinProjectTemplate.Files(new KotlinTemplateOptions(KotlinTemplateKind.ComposeDesktop, "Paint"));
        check(compose["settings.gradle.kts"].Contains("id(\"org.jetbrains.compose\") version \"1.12.2\"") && compose["src/commonMain/kotlin/paint/App.kt"].Contains("@Preview"), "Compose app has the sample plugin version and a preview");

        var stub = Path.Combine(root, "stub.kotlinproj");
        File.WriteAllText(stub, KotlinProjectTemplate.Stub(KotlinTemplateKind.ComposeDesktop, "Paint"));
        var read = KotlinProjectTemplate.TryReadStub(stub);
        check(read != null && read.Value.Kind == KotlinTemplateKind.ComposeDesktop && read.Value.ProjectName == "Paint", "template stubs round-trip");
        check(KotlinProjectTemplate.TryReadStub(Path.Combine(root, "templates", "JvmConsole", KotlinProjectFile.FileName)) == null, "real project files are not stubs");
        File.WriteAllText(stub, "<VistudioTemplate Kind=\"Android\" ProjectName=\"x\" />");
        var unknownKind = false;
        try { KotlinProjectTemplate.TryReadStub(stub); } catch (InvalidDataException) { unknownKind = true; }
        check(unknownKind, "unknown template kinds are reported");
    }

    private static void Reject(Action action, Action<bool, string> check, string message)
    {
        var threw = false;
        try { action(); } catch (ArgumentException) { threw = true; }
        check(threw, message);
    }

    /// <summary>--templates &lt;wrapper dir&gt; &lt;output dir&gt;: writes every template (KMP with all targets) for Gradle validation.</summary>
    public static int Generate(string wrapper, string output)
    {
        foreach (KotlinTemplateKind kind in Enum.GetValues(typeof(KotlinTemplateKind)))
        {
            var project = KotlinProjectTemplate.Create(Path.Combine(output, kind.ToString()), new KotlinTemplateOptions(kind, kind.ToString()), wrapper);
            Console.WriteLine("Created " + project.FilePath);
        }
        return 0;
    }
}
