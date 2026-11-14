using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Vistudio;

internal static class JvmLaunchTests
{
    public static void Run(Action<bool, string> check)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "jvm-launch");
        var kotlin = Path.Combine(workspace, "src", "main", "kotlin", "Main.kt");
        var java = Path.Combine(workspace, "src", "main", "java", "Entry.java");
        var gradle = JObject.Parse(@"{""tool"":""gradle"",""env"":{""JAVA_HOME"":""C:/server-jdk"",""SERVER"":""1""}}");

        check(KotlinJvmLaunch.IsSourceFile(kotlin) && KotlinJvmLaunch.IsSourceFile(java) && KotlinJvmLaunch.IsSourceFile("A.JAVA"), "Kotlin and Java entry files are launchable");
        check(!KotlinJvmLaunch.IsSourceFile("build.gradle.kts") && !KotlinJvmLaunch.IsSourceFile("Main.js"), "Scripts and other languages are not JVM entry files");

        // Server 271 launch protocol: no resolved module or class path, the server finds the module itself.
        var launch = KotlinJvmLaunch.Create(workspace, java, "mixed.JavaEntry", new JObject(), gradle, null);
        check((string?)launch["type"] == "intellij_gradle" && (string?)launch["tool"] == "gradle" && (string?)launch["request"] == "launch", "Gradle launch type and tool");
        check((string?)launch["mainClass"] == "mixed.JavaEntry" && (string?)launch["file"] == java && (string?)launch["cwd"] == Path.GetFullPath(workspace), "Main class, Java entry file and default cwd");
        check(launch["buildToolTarget"] == null && launch["classPaths"] == null, "No pre-271 resolution fields");
        check(launch["toolArgs"]!.ToString(Newtonsoft.Json.Formatting.None) == @"[""--no-daemon"",""--console=plain""]", "Plain console without daemon");
        check(launch["projectPath"] == null && launch["sourceSet"] == null && launch["args"] == null, "Absent options are not sent as null");
        check((string?)launch["env"]!["JAVA_HOME"] == "C:/server-jdk" && (string?)launch["env"]!["SERVER"] == "1", "Server-detected environment is kept");
        check(launch["$languageMappings"] == null, "Frame languages come from the engine registration, not the launch");
        var pkgdef = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "src", "Vistudio.Vsix", "Registration", "KotlinDebugAdapter.pkgdef"));
        check(pkgdef.Contains("LanguageMappings\\Kotlin]") && pkgdef.Contains("\"LanguageId\"=\"{" + KotlinJvmLaunch.KotlinLanguageId + "}\"") && pkgdef.Contains("\"Extensions\"=\"*\""), "Kotlin is the engine's default frame language");
        check(pkgdef.Contains("LanguageMappings\\Java]") && pkgdef.Contains("\"LanguageId\"=\"{" + KotlinJvmLaunch.JavaLanguageId + "}\"") && pkgdef.Contains("\"Extensions\"=\"java;.java\""), "Java frames map by extension in the engine registration");
        check(KotlinJvmLaunch.KotlinLanguageId != KotlinJvmLaunch.JavaLanguageId, "Distinct language ids");

        var config = JObject.Parse(@"{""cwd"":""run"",""gradleProject"":"":app"",""sourceSet"":""main"",""args"":[""a""],""vmArgs"":[""-Xmx1g""],""noDebug"":true,""env"":{""JAVA_HOME"":""C:/custom"",""X"":""y""}}");
        launch = KotlinJvmLaunch.Create(workspace, kotlin, "sample.MainKt", config, gradle, "C:/setting-jdk");
        check((string?)launch["projectPath"] == ":app" && (string?)launch["sourceSet"] == "main", "Gradle project and source set");
        check((string?)launch["cwd"] == Path.Combine(Path.GetFullPath(workspace), "run"), "Relative cwd");
        check((string?)launch["args"]![0] == "a" && (string?)launch["vmArgs"]![0] == "-Xmx1g" && (bool?)launch["noDebug"] == true, "Program options pass through");
        check((string?)launch["env"]!["JAVA_HOME"] == "C:/custom" && (string?)launch["env"]!["X"] == "y" && (string?)launch["env"]!["SERVER"] == "1", "Configuration env wins");
        launch = KotlinJvmLaunch.Create(workspace, kotlin, "sample.MainKt", new JObject(), gradle, "C:/setting-jdk");
        check((string?)launch["env"]!["JAVA_HOME"] == "C:/setting-jdk", "Gradle JDK setting wins over the server's JDK");
        launch = KotlinJvmLaunch.Create(workspace, kotlin, "sample.MainKt", new JObject(), JObject.Parse(@"{""tool"":""gradle""}"), null);
        check(launch["env"] == null, "No empty env");

        // Current-document F5: the entry class comes from the file when launch.vs.json names none.
        check(KotlinJvmLaunch.InferMainClass("C:/p/MixedMain.kt", "package mixed\n\nfun main() {\n}\n") == "mixed.MixedMainKt", "Kotlin top-level main");
        check(KotlinJvmLaunch.InferMainClass("C:/p/app.kt", "fun main(args: Array<String>) {}") == "AppKt", "Kotlin default package, capitalized facade");
        check(KotlinJvmLaunch.InferMainClass("C:/p/Run.kt", "@file:JvmName(\"Launcher\")\npackage a.b\nsuspend fun main() {}") == "a.b.Launcher", "Kotlin @file:JvmName and suspend main");
        check(KotlinJvmLaunch.InferMainClass("C:/p/Types.kt", "package a\nclass A {\n    fun main() {}\n}") == null, "Kotlin member main is not an entry point");
        check(KotlinJvmLaunch.InferMainClass("C:/p/JavaEntry.java", "package mixed;\n\npublic final class JavaEntry {\n    public static void main(String[] args) {}\n}") == "mixed.JavaEntry", "Java static main");
        check(KotlinJvmLaunch.InferMainClass("C:/p/Ledger.java", "package mixed;\npublic final class Ledger { public int add(int x) { return x; } }") == null, "Java class without main");
        check(KotlinJvmLaunch.InferMainClass("C:/p/build.gradle.kts", "fun main() {}") == null, "Scripts are not entry files");

        foreach (var model in new[] { null, JObject.Parse(@"{""tool"":""maven""}"), new JObject() })
        {
            try { KotlinJvmLaunch.Create(workspace, kotlin, "sample.MainKt", new JObject(), model, null); check(false, "Non-Gradle module rejected"); }
            catch (InvalidOperationException e) { check(e.Message.Contains("Gradle JVM module"), "Non-Gradle module reason: " + e.Message); }
        }
        try { KotlinJvmLaunch.Create(workspace, Path.Combine(workspace, "main.js"), "x", new JObject(), gradle, null); check(false, "Non-JVM entry rejected"); }
        catch (InvalidOperationException e) { check(e.Message.Contains(".java"), "Non-JVM entry reason"); }
    }
}
