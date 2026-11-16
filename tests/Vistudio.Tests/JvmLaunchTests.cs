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
        check(KotlinJvmLaunch.EnvironmentOf(launch).Count == 0, "No environment variables without env");

        // A debug session's launch runs on the session's Gradle daemon (the server's debugger waits 30 s for the JVM).
        var registry = Path.Combine(workspace, "daemon registry", "s1");
        using (var daemon = new Vistudio.Core.GradleSessionDaemon(registry))
        {
            launch = KotlinJvmLaunch.Create(workspace, kotlin, "sample.MainKt", new JObject(), gradle, "C:/setting-jdk", daemon.ClientArguments);
            check(launch["toolArgs"]!.ToString(Newtonsoft.Json.Formatting.None) ==
                new JArray("--daemon", "-Dorg.gradle.daemon.registry.base=" + registry, "-Dorg.gradle.daemon.idletimeout=600000", "--console=plain").ToString(Newtonsoft.Json.Formatting.None),
                "Session daemon: daemon, private registry (one unquoted argument, spaces kept), idle timeout, plain console: " + launch["toolArgs"]!.ToString(Newtonsoft.Json.Formatting.None));
            var environment = KotlinJvmLaunch.EnvironmentOf(launch);
            check(environment.Count == 2 && environment.Exists(v => v.Key == "JAVA_HOME" && v.Value == "C:/setting-jdk") && environment.Exists(v => v.Key == "SERVER" && v.Value == "1"),
                "Session daemon: the warm-up gets the launch's environment");
            check(!daemon.IsWarmFor("C:/setting-jdk") && daemon.RunningDaemons().Count == 0, "Session daemon: not warm before a Gradle run");
        }
        check(!Directory.Exists(registry), "Session daemon: no registry folder is left by a session that never ran Gradle");
        check(Vistudio.Core.GradleSessionDaemon.DaemonPid("daemon-4242.out.log") == 4242 && Vistudio.Core.GradleSessionDaemon.DaemonPid("daemon-x.out.log") == null &&
            Vistudio.Core.GradleSessionDaemon.DaemonPid("registry.bin") == null, "Session daemon: process id from the daemon's log name");
        var firstRegistry = Vistudio.Core.GradleSessionDaemon.RegistryFor("C:\\Projects\\My App");
        check(firstRegistry != Vistudio.Core.GradleSessionDaemon.RegistryFor("C:\\Projects\\My App") &&
            Path.GetDirectoryName(firstRegistry) == Path.GetDirectoryName(Vistudio.Core.GradleSessionDaemon.RegistryFor("c:\\projects\\my app\\")) &&
            firstRegistry.StartsWith(Vistudio.Core.SettingsXml.PathFor("debug-daemon"), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(Path.GetDirectoryName(firstRegistry))!.StartsWith("myapp-"),
            "Session daemon: a registry of its own per session under the Gradle root's folder: " + firstRegistry);
        try { new Vistudio.Core.GradleSessionDaemon("C:\\bad%path"); check(false, "Session daemon: shell expansion characters must be rejected"); }
        catch (ArgumentException) { check(true, "Session daemon: shell expansion characters in the registry path are rejected"); }
        var sampleRoot = Path.GetDirectoryName(DartTests.FindRepositoryFile(Path.Combine("samples", "kotlin-jvm", "gradlew.bat")))!;
        var build = Vistudio.Core.GradleCommand.Create(sampleRoot, "", new[] { "build" }, false, registry);
        check(build.Arguments.Contains("--console=plain --daemon \"-Dorg.gradle.daemon.registry.base=" + registry + "\" -Dorg.gradle.daemon.idletimeout=600000 build\"") && !build.Arguments.Contains("--no-daemon"),
            "Session daemon: the build before a debug launch runs on it: " + build.Arguments);
        var warmUp = Vistudio.Core.GradleCommand.CreateDaemonWarmUp(sampleRoot, "", registry);
        check(warmUp.Arguments.EndsWith("-Dorg.gradle.daemon.idletimeout=600000 help\"") && warmUp.Arguments.Contains("--daemon ") && warmUp.WorkingDirectory == sampleRoot, "Session daemon: the warm-up runs Gradle's help task on it: " + warmUp.Arguments);
        check(Vistudio.Core.GradleCommand.Create(sampleRoot, "", new[] { "build" }).Arguments.Contains("--no-daemon"), "Builds outside a debug session stay without a daemon");

        // What a session left behind: JVMs of the main class that started while it lasted.
        var sessionStart = new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);
        var starts = new System.Collections.Generic.Dictionary<int, DateTime>
        {
            [10] = sessionStart.AddMinutes(-5), [11] = sessionStart.AddSeconds(-1), [12] = sessionStart.AddSeconds(40), [13] = sessionStart.AddSeconds(70), [14] = sessionStart.AddSeconds(20)
        };
        const string rows = "10 sample.MainKt\n11 sample.MainKt\n12 sample.MainKt\n13 sample.MainKt\n14 sample.OtherKt\n15 sample.MainKt\n16 org.gradle.launcher.daemon.bootstrap.GradleDaemon\n";
        var leftovers = Vistudio.Core.GradleSessionDaemon.ProgramJvms(rows, "sample.MainKt", sessionStart, sessionStart.AddSeconds(60), pid => starts.TryGetValue(pid, out var s) ? s : (DateTime?)null);
        check(System.Linq.Enumerable.SequenceEqual(leftovers, new[] { 11, 12 }),
            "Leftover JVMs: the main class, started within the session (not an earlier run, the next session's, another class, a process that is gone): " + string.Join(",", leftovers));

        // What the DAP traffic of a session says about its launch.
        JObject Request(string command) => new JObject { ["type"] = "request", ["command"] = command };
        JObject Event(string name, JObject? body = null) => new JObject { ["type"] = "event", ["event"] = name, ["body"] = body ?? new JObject() };
        var clock = sessionStart;
        var monitor = new KotlinDebugMonitor(() => clock);
        check(monitor.Describe("sample.MainKt", false) == null, "Launch monitor: nothing to say before a launch request");
        monitor.Sent(Request("initialize")); monitor.Received(Event("initialized")); monitor.Sent(Request("launch"));
        monitor.Received(Event("output", new JObject { ["output"] = "[Run] :" }));
        check(!monitor.Attached, "Launch monitor: Gradle's output is not an attached debugger");
        clock = sessionStart.AddSeconds(33);
        monitor.Received(Event("terminated"));
        monitor.Sent(Request("disconnect"));
        var reason = monitor.Describe("sample.MainKt", false);
        check(reason != null && reason.Contains("did not attach to sample.MainKt") && reason.Contains("waits 30 s") && reason.Contains("after 33 s") && reason.Contains("4294967295") && reason.Contains("Ctrl+F5"),
            "Launch monitor: a session the server ended after its attach wait is explained: " + reason);
        check(monitor.Describe("sample.MainKt", true) == null, "Launch monitor: a run without debugging has no attach to miss");

        monitor = new KotlinDebugMonitor(() => clock);
        monitor.Sent(Request("launch")); monitor.Received(Event("thread", new JObject { ["reason"] = "started", ["threadId"] = 1 })); monitor.Received(Event("terminated"));
        check(monitor.Attached && monitor.Describe("sample.MainKt", false) == null, "Launch monitor: a session whose debugger attached ends without a message");
        monitor = new KotlinDebugMonitor(() => clock);
        monitor.Sent(Request("launch")); monitor.Received(Event("stopped", new JObject { ["reason"] = "breakpoint" }));
        check(monitor.Attached, "Launch monitor: a stop is an attached debugger");

        monitor = new KotlinDebugMonitor(() => clock);
        monitor.Sent(Request("launch")); monitor.Sent(Request("disconnect")); monitor.Received(Event("terminated"));
        check(monitor.Describe("sample.MainKt", false) == null, "Launch monitor: Stop Debugging while the program starts is not a failed attach");

        clock = sessionStart;
        monitor = new KotlinDebugMonitor(() => clock);
        monitor.Sent(Request("launch"));
        clock = sessionStart.AddSeconds(12);
        check(monitor.ProgramMayBeLeft, "Launch monitor: a launch without a stop or an exit code can have left its JVM");
        monitor.Received(Event("exited", new JObject { ["exitCode"] = 1 })); monitor.Received(Event("terminated"));
        check(monitor.Describe("sample.MainKt", false) == null && !monitor.ProgramMayBeLeft, "Launch monitor: a run that ended with an exit code (a Gradle failure, a program without breakpoints) is Visual Studio's to report");
        clock = sessionStart.AddSeconds(31);
        monitor = new KotlinDebugMonitor(() => clock);
        monitor.Sent(Request("launch"));
        clock = sessionStart.AddSeconds(95);
        monitor.Received(Event("exited", new JObject { ["exitCode"] = 0 })); monitor.Received(Event("terminated"));
        check(monitor.Describe("sample.MainKt", false) == null, "Launch monitor: a program that ran without ever stopping and ended is not a failed attach");
        foreach (var seconds in new[] { 8, 70 })
        {
            clock = sessionStart;
            monitor = new KotlinDebugMonitor(() => clock);
            monitor.Sent(Request("launch"));
            clock = sessionStart.AddSeconds(seconds);
            monitor.Received(Event("terminated"));
            reason = monitor.Describe("sample.MainKt", false);
            check(reason != null && reason.Contains("ended " + seconds + " s after the launch request without an exit code") && !reason.Contains("waits 30 s"),
                "Launch monitor: an end without an exit code outside the attach wait is not blamed on it: " + reason);
        }

        var registered = KotlinDebugMonitor.Register();
        check(KotlinDebugMonitor.Find(registered.Id) == registered && KotlinDebugMonitor.Find("missing") == null && KotlinDebugMonitor.Find(null) == null, "Launch monitor: found by the id the launch carries");
        registered.Forget();
        check(KotlinDebugMonitor.Find(registered.Id) == null, "Launch monitor: forgotten with its session");

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
