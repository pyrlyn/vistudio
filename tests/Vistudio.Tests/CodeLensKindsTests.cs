using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Vistudio.Core.CodeLens;

/// <summary>Scanner traits and helpers behind the further lens kinds: overrides, Compose preview, expect/actual, Gradle tasks.</summary>
internal static class CodeLensKindsTests
{
    internal static void Run(Action<bool, string> check, string root)
    {
        RunTraits(check);
        RunElements(check);
        RunGradleScripts(check, root);
        RunExpectActual(check, root);
        RunSources(check, root);
    }

    private static void RunTraits(Action<bool, string> check)
    {
        var kotlin = CodeDeclarationScanner.ScanKotlin(@"package ui

interface Shape { fun area(): Double; val name: String }
open class Square(val side: Double) : Shape {
    override fun area() = side * side
    override val name = ""square""
    open fun scale(k: Double) = Square(side * k)
    open val open = 1
    fun plain() = 0
}
abstract class Base { abstract fun f(); open fun g() {} }

@Preview
@Composable
fun CardPreview() {}

@androidx.compose.ui.tooling.preview.Preview(name = ""Wide"", widthDp = 400)
@Composable
private fun WidePreview() {}

@Composable
fun Card() {}

expect fun platformName(): String
expect class Clock { fun now(): Long; val zone: String }
actual fun other(): Int = 1
actual class Timer actual constructor() { actual fun start() {} }
");
        CodeDeclaration Get(string name) => kotlin.Single(d => d.QualifiedName == name);
        check(Get("Square.area").IsOverride && Get("Square.name").IsOverride && !Get("Square.scale").IsOverride, "Kotlin override trait");
        check(Get("Square.scale").IsOpen && Get("Square.open").IsOpen && !Get("Square.plain").IsOpen && !Get("Square").IsOpen, "Kotlin open members (not classes)");
        check(!Get("Base.f").IsOpen && Get("Base.g").IsOpen && !Get("Shape.area").IsOpen, "Abstract and interface members are not 'open'");
        check(Get("CardPreview").IsPreview && Get("WidePreview").IsPreview && !Get("Card").IsPreview, "@Preview, also fully qualified, but not @Composable alone");
        check(Get("platformName").IsExpect && !Get("platformName").IsActual, "expect function");
        check(Get("Clock").IsExpect && Get("Clock.now").IsExpect && Get("Clock.zone").IsExpect, "Members of an expect class are expect");
        check(Get("other").IsActual && Get("Timer").IsActual && Get("Timer.start").IsActual && !Get("Timer.start").IsExpect, "actual declarations");
        check(!kotlin.Any(d => d.Kind == CodeDeclarationKind.Constructor), "A primary (also actual) constructor in the header gets no lens");
        check(!Get("Square.area").IsPreview && Get("Square.area").Traits == CodeDeclarationTraits.Override, "Only the matching traits are set");

        var java = CodeDeclarationScanner.ScanJava(@"package mixed;
public class Card extends Base implements Payment {
    @Override
    public long amount() { return 1; }
    @java.lang.Override public String toString() { return """"; }
    @Deprecated public void old() {}
    public Card() {}
}");
        check(java.Single(d => d.Name == "amount").IsOverride && java.Single(d => d.Name == "toString").IsOverride, "Java @Override");
        check(!java.Single(d => d.Name == "old").IsOverride && java.All(d => !d.IsOpen && !d.IsPreview && !d.IsExpect), "Java: other annotations set no trait");

        check(CodeDeclarationScanner.PackageOf("A.kt", "@file:JvmName(\"X\")\n\npackage a.b.`c`\n\nfun f() {}") == "a.b.c", "Kotlin package after a file annotation");
        check(CodeDeclarationScanner.PackageOf("A.java", "// c\npackage p.q;\nclass A {}") == "p.q" && CodeDeclarationScanner.PackageOf("A.kt", "fun f() {}") == "", "Java package and default package");
    }

    private static void RunElements(Action<bool, string> check)
    {
        var declarations = CodeDeclarationScanner.ScanKotlin("open class A { open fun f() {} }\nexpect fun e()\nfun plain() {}");
        var ids = CodeLensElement.Identifiers(declarations);
        check(ids[2] == "vistudio|--|Function/8|e" && ids[3] == "vistudio|--|Function|plain", "Traits follow the kind only when present: " + string.Join(", ", ids));
        var f = CodeLensElement.TryParse(ids[1])!;
        check(f.Kind == "Function" && f.Name == "A.f" && f.Has(CodeDeclarationTraits.Open) && !f.Has(CodeDeclarationTraits.Expect) && !f.IsGradleTask, "Parse traits, kind and name");
        var tasks = CodeLensElement.Identifiers(GradleScriptScanner.Scan("tasks.register(\"a\")\ntasks.named(\"a\") {}\n"));
        check(tasks[0] == "vistudio|--|GradleTask|a" && tasks[1] == "vistudio|--|GradleTask|a#1", "Gradle task identifiers: " + string.Join(", ", tasks));
        var task = CodeLensElement.TryParse(tasks[1])!;
        check(task.IsGradleTask && task.Name == "a" && !task.CanBeImplemented && task.Traits == CodeDeclarationTraits.None, "Parse a Gradle task element");
    }

    private static void RunGradleScripts(Action<bool, string> check, string root)
    {
        const string script = @"plugins { kotlin(""jvm"") version ""2.4.21""; application }
// tasks.register(""commented"")
val text = ""tasks.register(\""inString\"")""
application { mainClass.set(""MainKt"") }
tasks.register(""verifyJvm"") { group = ""verification""; dependsOn(""jvmRun"") }
tasks.register<Copy>(""copyDocs"") { from(""docs"") }
tasks.named<Test>(""test"") { useJUnitPlatform() }
tasks.create(""legacy"")
task(""older"")
val bundle by tasks.registering(Zip::class) { }
val pack by tasks.creating
tasks {
    register(""inTasks"")
    named(""jar"") { }
    withType<Test> { register(""nested"") }
}
subprojects { tasks.register(""perProject"") }
tasks.register(""tmpl$suffix"")
tasks.register(name)
foo.tasks.register(""qualified"")
";
        var tasks = GradleScriptScanner.Scan(script);
        var names = tasks.Select(t => t.Name).ToArray();
        check(names.SequenceEqual(new[] { "run", "verifyJvm", "copyDocs", "test", "legacy", "older", "bundle", "pack", "inTasks", "jar" }), "Gradle script tasks: " + string.Join(", ", names));
        var verify = tasks.Single(t => t.Name == "verifyJvm");
        check(script.Substring(verify.StartOffset, verify.EndOffset - verify.StartOffset) == "tasks.register(\"verifyJvm\"", "Task range spans the call up to the name");
        var run = tasks.Single(t => t.Name == "run");
        check(run.IsApplication && script.Substring(run.StartOffset, run.EndOffset - run.StartOffset) == "application", "application block stands for run");
        var bundle = tasks.Single(t => t.Name == "bundle");
        check(script.Substring(bundle.StartOffset, bundle.EndOffset - bundle.StartOffset) == "val bundle", "Delegated task range");
        check(GradleScriptScanner.Scan("plugins { application }\nkotlin { jvm { application { } } }").Count == 0, "application plugin id and nested blocks are not the run task");
        check(GradleScriptScanner.IsGradleScript(@"C:\p\build.gradle.kts") && !GradleScriptScanner.IsGradleScript(@"C:\p\build.gradle") && !GradleScriptScanner.IsGradleScript(@"C:\p\Main.kt"), "Gradle script detection");

        var build = Path.Combine(root, "gradle-build");
        Directory.CreateDirectory(Path.Combine(build, "app", "core"));
        File.WriteAllText(Path.Combine(build, "settings.gradle.kts"), "include(\"app\", \"app:core\")");
        var rootProject = GradleScriptScanner.ProjectOf(Path.Combine(build, "build.gradle.kts"));
        var core = GradleScriptScanner.ProjectOf(Path.Combine(build, "app", "core", "build.gradle.kts"));
        check(rootProject?.Root == build && rootProject?.ProjectPath == ":" && core?.ProjectPath == ":app:core", "Gradle project paths: " + rootProject + " " + core);
        check(GradleScriptScanner.TaskPath(":", "verifyJvm") == ":verifyJvm" && GradleScriptScanner.TaskPath(":app:core", "run") == ":app:core:run", "Task paths");
        check(GradleScriptScanner.ProjectOf(Path.Combine(Path.GetPathRoot(root)!, "no-such-dir-" + Guid.NewGuid().ToString("N"), "build.gradle.kts")) == null, "No settings file, no project");
    }

    private static void RunExpectActual(Action<bool, string> check, string root)
    {
        var src = Path.Combine(root, "kmp", "src");
        void Write(string relative, string text)
        {
            var path = Path.Combine(src, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Write(@"commonMain\kotlin\Platform.kt", "package p\n\n// expect fun decoy(): Int\nexpect fun platformName(): String\nexpect class Clock { fun now(): Long }\n");
        Write(@"commonMain\kotlin\Other.kt", "package q\n\nexpect fun platformName(): String\n");
        Write(@"webMain\kotlin\Web.kt", "package p\n\nexpect fun webOnly(): String\n");
        Write(@"jvmMain\kotlin\Platform.jvm.kt", "package p\n\nactual fun platformName(): String = \"JVM\"\nactual fun webOnly() = \"\"\nactual class Clock { actual fun now(): Long = 0 }\nactual fun missing() = 1\n");
        var actualPath = Path.Combine(src, @"jvmMain\kotlin\Platform.jvm.kt");
        var actualText = File.ReadAllText(actualPath);
        var actuals = CodeDeclarationScanner.ScanKotlin(actualText);
        CodeLocation? Expect(string name) => KotlinSourceSets.FindExpect(actualPath, actualText, actuals.Single(d => d.QualifiedName == name));
        var platform = Expect("platformName");
        check(platform != null && platform.Path == Path.Combine(src, @"commonMain\kotlin\Platform.kt") && platform.Line == 3 && platform.Column == 11, "expect of an actual function (same package, common first): " + platform?.Path + " " + platform?.Line);
        check(Expect("webOnly")?.Path == Path.Combine(src, @"webMain\kotlin\Web.kt"), "expect in an intermediate source set");
        check(Expect("Clock.now")?.Line == 4 && Expect("Clock")?.Line == 4, "expect class and its member");
        check(Expect("missing") == null, "No expect, no location");
        check(KotlinSourceSets.SourceSetOf(actualPath) == "jvmMain" && KotlinSourceSets.SourceSetOf(@"C:\x\Main.kt") == null, "Source set of a file");
        check(KotlinSourceSets.TargetName("wasmJsMain") == "wasmJs" && KotlinSourceSets.TargetName("jvmTest") == "jvm" && KotlinSourceSets.TargetName("main") == "main", "Target names for labels");
    }

    private static void RunSources(Action<bool, string> check, string root)
    {
        var jar = Path.Combine(root, "lib sources.jar");
        using (var zip = ZipFile.Open(jar, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("kotlin/Any.kt").Open()))
            writer.Write("package kotlin\n\npublic open class Any {\n    public open fun toString(): String\n}\n");
        var uri = "jar:///" + Uri.EscapeDataString(jar.Replace('\\', '/')).Replace("%2F", "/") + "!/kotlin/Any.kt";
        var text = KotlinSourceSets.ReadSource(uri);
        check(text != null && text.Contains("class Any"), "Read a source from a jar URI: " + uri);
        check(KotlinSourceSets.ReadSource(uri.Replace("Any.kt", "None.kt")) == null && KotlinSourceSets.ReadSource("jdt://contents/rt.jar/java.lang/Object.class") == null, "Unreadable sources give null");
        var member = KotlinSourceSets.FindMember("Any.kt", text!, "Any", "toString", CodeDeclarationKind.Function);
        check(member != null && member.NameLine == 3, "Base member in a supertype source");
        var file = Path.Combine(root, "Shapes.kt");
        File.WriteAllText(file, "interface Shape {\n    fun area(): Double\n}\nclass Other { fun area() = 1.0 }\n");
        var source = KotlinSourceSets.ReadSource(new Uri(file).AbsoluteUri);
        check(KotlinSourceSets.FindMember(file, source!, "Shape", "area", CodeDeclarationKind.Function)?.NameLine == 1, "Member chosen by its type name");
        check(KotlinSourceSets.FindMember(file, source!, "Shape", "volume", CodeDeclarationKind.Function) == null, "Missing member");
    }
}
