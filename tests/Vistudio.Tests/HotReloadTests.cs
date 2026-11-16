using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vistudio.Core;
using Vistudio.Core.Diagnostics;
using Vistudio.Core.HotReload;

/// <summary>
/// Kotlin/JVM hot reload (T72): class lists, the reload plan, agent results and the Gradle command; the live part runs a
/// real Kotlin + Java program through the Gradle wrapper and reloads it with the shipped init script and agent.
/// </summary>
internal static class HotReloadTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);

        // Class lists written by HotReload.gradle.
        var after = HotReloadSnapshot.Parse(
            "D\tC:\\app\\build\\classes\\kotlin\\main\r\n" +
            "C\tsample\\Greeting.class\t2458\t1794788514684\r\n" +
            "C\tsample\\Outer$Inner.class\t703\t1794788514686\r\n" +
            "C\tMainKt.class\t10\t5\r\n" +
            "C\tmodule-info.class\t10\t5\r\n" +
            "C\tMETA-INF\\versions\\17\\sample\\Greeting.class\t10\t5\r\n" +
            "C\tsample\\notes.txt\t10\t5\r\n" +
            "C\tsample\\Broken.class\tx\t5\r\n" +
            "D\tC:\\app\\build\\classes\\java\\main\n" +
            "C\tsample\\JavaText.class\t300\t1794788514000\t0a1b2c\n" +
            "A\tC:\\work\\agent\\vistudio-hot-reload-agent-e61ec212ec19.jar\n" +
            "M\t:app\t:app:classes\n");
        check(after.Classes.Count == 4, "Class list: class files only, module descriptors, multi-release copies and bad rows left out: " + after.Classes.Count);
        check(after.Classes[0].Path == "C:\\app\\build\\classes\\kotlin\\main\\sample\\Greeting.class" && after.Classes[0].ClassName == "sample.Greeting" &&
            after.Classes[0].Length == 2458 && after.Classes[0].Modified == 1794788514684, "Class list: path, binary name, length, time");
        check(after.Classes[1].ClassName == "sample.Outer$Inner" && after.Classes[2].ClassName == "MainKt", "Class list: nested and default-package names");
        check(after.Classes[3].Path == "C:\\app\\build\\classes\\java\\main\\sample\\JavaText.class", "Class list: rows belong to the directory above them");
        check(after.Classes[3].Hash == "0a1b2c" && after.Classes[0].Hash == null, "Class list: optional hash column");
        check(after.AgentJar == "C:\\work\\agent\\vistudio-hot-reload-agent-e61ec212ec19.jar" && after.Module == ":app" && after.Tasks == ":app:classes", "Class list: agent jar, module and tasks");
        check(HotReloadSnapshot.Parse("\n").Classes.Count == 0 && HotReloadSnapshot.Parse("C\ta.class\t1\t1\n").Classes.Count == 0, "Class list: empty list, row without a directory");
        check(HotReloadSnapshot.ClassName("a/b/C.class") == "a.b.C" && HotReloadSnapshot.ClassName("a\\module-info.class") == null && HotReloadSnapshot.ClassName("C.txt") == null, "Class names from relative paths");

        // The reload plan.
        HotReloadClassFile File_(string name, long length, long modified, string? hash = null) => new HotReloadClassFile("C:\\out\\" + name.Replace('.', '\\') + ".class", name, length, modified, hash);
        HotReloadSnapshot List(params HotReloadClassFile[] files) => HotReloadSnapshot.Parse("D\tC:\\out\n" +
            string.Concat(files.Select(f => "C\t" + f.ClassName.Replace('.', '\\') + ".class\t" + f.Length + "\t" + f.Modified + (f.Hash == null ? "" : "\t" + f.Hash) + "\n")));
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hashed = new List<string>();
        Func<string, string> hashOf = path => { hashed.Add(path); return hashes.TryGetValue(path, out var h) ? h : throw new FileNotFoundException(path); };
        const long jvmStart = 1000;

        var state = new HotReloadState();
        var a1 = File_("sample.A", 10, 500);
        var b1 = File_("sample.B", 20, 500);
        var plan = state.Plan(List(a1, b1), List(a1, b1), jvmStart, hashOf);
        check(plan.Count == 0 && hashed.Count == 0 && state.KnownCount == 2, "Plan: classes on disk before the JVM started are what it runs; nothing is read");

        var a2 = File_("sample.A", 11, 2000);
        hashes[a2.Path] = "a2";
        plan = state.Plan(List(a1, b1), List(a2, b1), jvmStart, hashOf);
        check(plan.Count == 1 && plan[0].File.ClassName == "sample.A" && plan[0].Hash == "a2", "Plan: a rewritten class is a candidate");
        state.Apply(plan, HotReloadResult.ParseAgent("reloaded\tsample.A\ndone\n"));
        check(state.Plan(List(a2, b1), List(a2, b1), jvmStart, hashOf).Count == 0, "Plan: a reloaded class is current");

        var a3 = File_("sample.A", 11, 3000);
        check(state.Plan(List(a2, b1), List(a3, b1), jvmStart, hashOf).Count == 0, "Plan: the same bytes written again are not reloaded");
        hashes[a3.Path] = "a3";
        plan = state.Plan(List(a2, b1), List(File_("sample.A", 11, 4000), b1), jvmStart, hashOf);
        check(plan.Count == 1 && plan[0].Hash == "a3", "Plan: same length, new bytes: a candidate");

        // The JVM refuses a structural change: the class stays pending until bytes it accepts arrive.
        state.Apply(plan, HotReloadResult.ParseAgent("failed\tsample.A\tUnsupportedOperationException: class redefinition failed: attempted to add a method\ndone\n"));
        var a4 = File_("sample.A", 11, 4000);
        plan = state.Plan(List(a4, b1), List(a4, b1), jvmStart, hashOf);
        check(plan.Count == 1 && plan[0].File.ClassName == "sample.A", "Plan: a refused class is offered again although its file did not change");
        state.Apply(plan, HotReloadResult.ParseAgent("reloaded\tsample.A\ndone\n"));
        check(state.Plan(List(a4, b1), List(a4, b1), jvmStart, hashOf).Count == 0, "Plan: accepted afterwards, it is current");

        // First sight of files: written by this compile, newer than the JVM (another build ran), or already there.
        state = new HotReloadState();
        var newer = File_("sample.Newer", 5, 1500);
        var added = File_("sample.Added", 6, 400);
        hashes[newer.Path] = "n";
        hashes[added.Path] = "x";
        plan = state.Plan(List(a1, newer), List(a1, newer, added), jvmStart, hashOf);
        check(plan.Select(c => c.File.ClassName).OrderBy(n => n).SequenceEqual(new[] { "sample.Added", "sample.Newer" }),
            "Plan (first reload): a class compiled after the JVM started and a new class file are candidates, an older unchanged one is not");
        state.Apply(plan, HotReloadResult.ParseAgent("reloaded\tsample.Newer\nnotloaded\tsample.Added\ndone\n"));
        check(state.Plan(List(a1, newer, added), List(a1, newer, added), jvmStart, hashOf).Count == 0, "Plan: a class the JVM has not loaded yet is current (read from disk when used)");

        // A session's first list carries the hashes from before the compilation: a class the compiler rewrote with the
        // same bytes (a Kotlin file that only depends on the edited one) is not reloaded, even on the first reload.
        state = new HotReloadState();
        var same = File_("sample.Same", 7, 2000);
        var edited = File_("sample.Edited", 8, 2000);
        var late = File_("sample.Late", 9, 2000);
        hashes[same.Path] = "same";
        hashes[edited.Path] = "edited-2";
        hashes[late.Path] = "late";
        plan = state.Plan(List(File_("sample.Same", 7, 500, "same"), File_("sample.Edited", 8, 500, "edited-1"), File_("sample.Late", 9, 1500, "late"), File_("sample.Kept", 3, 500, "kept")),
            List(same, edited, late, File_("sample.Kept", 3, 500)), jvmStart, hashOf);
        check(plan.Select(c => c.File.ClassName).OrderBy(n => n).SequenceEqual(new[] { "sample.Edited", "sample.Late" }),
            "Plan (first reload, hashed list): same bytes skipped, changed bytes reloaded, a class another build wrote after the JVM started reloaded: " +
            string.Join(",", plan.Select(c => c.File.ClassName)));
        state.Apply(plan, HotReloadResult.ParseAgent("reloaded\tsample.Edited\nreloaded\tsample.Late\ndone\n"));
        hashes["C:\\out\\sample\\Kept.class"] = "kept";
        check(state.Plan(List(), List(same, edited, late, File_("sample.Kept", 3, 9000)), jvmStart, hashOf).Count == 0,
            "Plan: an untouched class rewritten later with the same bytes is compared with the hash it started with");

        // The agent did not run (JVM paused and the wait canceled, attach refused): everything is offered again.
        state = new HotReloadState();
        plan = state.Plan(List(a1), List(a2), jvmStart, hashOf);
        state.Apply(plan, HotReloadResult.Failure("The JVM did not load the reload agent."));
        check(state.Plan(List(a2), List(a2), jvmStart, hashOf).Count == 1, "Plan: classes of a reload that did not reach the JVM are offered again");
        state = new HotReloadState();
        check(state.Plan(List(), List(File_("sample.Gone", 1, 5000)), jvmStart, hashOf).Count == 0, "Plan: a class file that vanished before it could be read is skipped");

        // Agent results and what the user reads.
        var result = HotReloadResult.ParseAgent("reloaded\tsample.A\r\nreloaded\tsample.B\r\nnotloaded\tsample.C\r\n" +
            "failed\tsample.D\tUnsupportedOperationException: class redefinition failed: attempted to add a method\r\ndone\r\n");
        check(result.Error == null && result.Reloaded.Count == 2 && result.NotLoaded.Single() == "sample.C" && result.Failed.Single().ClassName == "sample.D", "Agent result: per-class outcomes");
        check(result.Failed[0].Message == "class redefinition failed: attempted to add a method", "Agent result: the JVM's reason without the exception type");
        check(result.Summary == "Hot reload: 2 classes reloaded, 1 class not loaded yet (read from disk when first used), 1 class could not be reloaded.", "Summary: " + result.Summary);
        var details = result.Details.ToList();
        check(details.Count == 2 && details[0] == "  sample.D: class redefinition failed: attempted to add a method" && details[1].Contains("restart"), "Details: refused class and the restart advice");
        check(HotReloadResult.ParseAgent("reloaded\tsample.A\ndone\n").Summary == "Hot reload: 1 class reloaded." && !HotReloadResult.ParseAgent("reloaded\tsample.A\ndone\n").Details.Any(), "Summary: one class, no details");
        check(HotReloadResult.NoChanges.Summary == "Hot reload: no changed classes.", "Summary: nothing changed");
        check(HotReloadResult.ParseAgent("reloaded\tsample.A\n").Error != null, "Agent result without the final line is an error");
        var agentError = HotReloadResult.ParseAgent("error\tIllegalStateException: this JVM does not support class redefinition\ndone\n");
        check(agentError.Error == "IllegalStateException: this JVM does not support class redefinition" && agentError.Summary.StartsWith("Hot reload failed: "), "Agent error line");
        check(HotReloadResult.Failure("Compilation failed: x").Summary == "Hot reload failed: Compilation failed: x", "Summary of a failed reload");
        check(HotReloadSession.CompilerMessage("file:///C:/Users/me/My%20App/src/main/kotlin/HotText.kt:4:27 Unresolved reference 'missing'.") == "HotText.kt:4:27 Unresolved reference 'missing'.",
            "Compiler message: kotlinc file URI shortened to the file name");
        check(HotReloadSession.CompilerMessage("C:\\Users\\me\\My App\\src\\main\\java\\sample\\JavaText.java:4: error: ';' expected") == "JavaText.java:4: error: ';' expected",
            "Compiler message: javac path shortened to the file name");
        check(HotReloadSession.CompilerMessage("Unresolved reference 'missing'.") == "Unresolved reference 'missing'.", "Compiler message without a location is kept");

        // Finding the program's JVM among jps rows.
        var session = new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);
        var starts = new Dictionary<int, DateTime?> { [10] = session.AddMinutes(-5), [11] = session.AddSeconds(3), [12] = session.AddSeconds(9), [13] = session.AddSeconds(4), [14] = null };
        var jps = "10 sample.MainKt\r\n11 sample.MainKt\r\n12 sample.MainKt\r\n13 org.gradle.launcher.daemon.bootstrap.GradleDaemon\r\n14 sample.MainKt\r\n15 jdk.jcmd/sun.tools.jps.Jps\r\n";
        check(HotReloadJvm.Find(jps, "sample.MainKt", session, pid => starts.TryGetValue(pid, out var s) ? s : null) == 12, "JVM: newest process running the main class since the session started");
        check(HotReloadJvm.Find(jps, "sample.Other", session, pid => starts[pid]) == null, "JVM: no process runs the main class");
        check(HotReloadJvm.Find("10 sample.MainKt\n", "sample.MainKt", session, pid => starts[pid]) == null, "JVM: a run from before this session is not the program");
        check(HotReloadJvm.Find("11 sample.MainKt\n", "sample.MainKt", session.AddSeconds(4), pid => starts[pid]) == 11, "JVM: started within the slack before the recorded session start");
        check(HotReloadJvm.StartTimeUtc(Process.GetCurrentProcess().Id) is DateTime own && own <= DateTime.UtcNow && HotReloadJvm.StartTimeUtc(-1) == null, "JVM: process start time, missing process");

        // The Gradle command: a daemon in a private registry, every path in the environment.
        var gradleRoot = Path.Combine(root, "hot reload project");
        Directory.CreateDirectory(gradleRoot);
        File.WriteAllText(Path.Combine(gradleRoot, "gradlew.bat"), "@echo off\r\n");
        var script = Path.Combine(root, "HotReload.gradle");
        var agent = Path.Combine(root, "VistudioHotReloadAgent.java");
        File.WriteAllText(script, "");
        File.WriteAllText(agent, "");
        var work = Path.Combine(root, "work dir");
        var start = GradleCommand.CreateHotReload(gradleRoot, "", script, agent, Path.Combine(gradleRoot, "src", "Main.kt"), work, Path.Combine(work, "daemon"));
        check(start.WorkingDirectory == gradleRoot && start.Arguments.Contains("--console=plain --daemon ") && !start.Arguments.Contains("--no-daemon"), "Hot reload Gradle run uses a daemon: " + start.Arguments);
        check(start.Arguments.Contains("\"-Dorg.gradle.daemon.registry.base=" + Path.Combine(work, "daemon") + "\"") && start.Arguments.Contains("-Dorg.gradle.daemon.idletimeout=600000"),
            "Hot reload daemon: private registry, ten idle minutes");
        check(start.Arguments.Contains("--init-script \"" + script + "\" " + GradleCommand.HotReloadTask + "\""), "Hot reload Gradle run: init script and task");
        check(start.EnvironmentVariables["VISTUDIO_HOTRELOAD_ROOT"] == gradleRoot && start.EnvironmentVariables["VISTUDIO_HOTRELOAD_FILE"] == Path.Combine(gradleRoot, "src", "Main.kt") &&
            start.EnvironmentVariables["VISTUDIO_HOTRELOAD_AGENT"] == agent && start.EnvironmentVariables["VISTUDIO_HOTRELOAD_OUTPUT"] == work, "Hot reload Gradle run: inputs in environment variables");
        check(start.EnvironmentVariables["VISTUDIO_HOTRELOAD_HASH"] == "0" &&
            GradleCommand.CreateHotReload(gradleRoot, "", script, agent, "Main.kt", work, work, hashBefore: true).EnvironmentVariables["VISTUDIO_HOTRELOAD_HASH"] == "1",
            "Hot reload Gradle run: the hash request is set either way (a reused daemon takes each run's environment)");
        check(GradleCommand.Create(gradleRoot, "", new[] { "build" }).Arguments.Contains("--no-daemon"), "Other Gradle runs stay without a daemon");
        var rejected = 0;
        foreach (var bad in new[] { Path.Combine(root, "a%b"), Path.Combine(root, "a!b") })
            try { GradleCommand.CreateHotReload(gradleRoot, "", script, agent, "Main.kt", work, bad); } catch (ArgumentException) { rejected++; }
        check(rejected == 2, "Hot reload Gradle run: shell expansion characters in the registry path are rejected");
        try { GradleCommand.CreateHotReload(gradleRoot, "", script, Path.Combine(root, "missing.java"), "Main.kt", work, work); check(false, "Missing agent source must be rejected"); }
        catch (FileNotFoundException) { check(true, "Missing agent source is rejected"); }

        var first = HotReloadTarget.WorkDirectoryFor("C:\\Projects\\My App");
        check(first == HotReloadTarget.WorkDirectoryFor("c:\\projects\\my app\\") && first != HotReloadTarget.WorkDirectoryFor("C:\\Projects\\Other") &&
            first.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vistudio", "hot-reload") + "\\", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(first).StartsWith("myapp-", StringComparison.Ordinal), "Work directory: per Gradle root, outside the project: " + first);
        check(new KotlinSettings().HotReloadOnSave, "Hot reload on save is on by default");

        // The shipped files: the agent declares what the Gradle script packs, and both name the same things.
        var shippedScript = File.ReadAllText(DartTests.FindRepositoryFile(Path.Combine("src", "Vistudio.Vsix", "Build", "HotReload.gradle")));
        var shippedAgent = File.ReadAllText(DartTests.FindRepositoryFile(Path.Combine("src", "Vistudio.Vsix", "Build", "VistudioHotReloadAgent.java")));
        check(shippedScript.Contains("'" + GradleCommand.HotReloadTask.TrimStart(':') + "'") && shippedScript.Contains("'Agent-Class': 'VistudioHotReloadAgent'") &&
            shippedScript.Contains("'Can-Redefine-Classes': 'true'"), "HotReload.gradle registers the task and the agent manifest");
        foreach (var name in new[] { "VISTUDIO_HOTRELOAD_ROOT", "VISTUDIO_HOTRELOAD_FILE", "VISTUDIO_HOTRELOAD_AGENT", "VISTUDIO_HOTRELOAD_OUTPUT" })
            check(shippedScript.Contains("'" + name + "'"), "HotReload.gradle reads " + name);
        check(shippedAgent.Contains("public final class VistudioHotReloadAgent") && shippedAgent.Contains("public static void agentmain(String request, Instrumentation instrumentation)") &&
            shippedAgent.Contains("\"VISTUDIO_HOTRELOAD_ERROR\\t\""), "Agent source: class, agent entry point and error line");
        var project = File.ReadAllText(DartTests.FindRepositoryFile(Path.Combine("src", "Vistudio.Vsix", "Vistudio.Vsix.csproj")));
        check(project.Contains("Build/HotReload.gradle") && project.Contains("Build/VistudioHotReloadAgent.java"), "The VSIX ships the script and the agent source");
    }

    /// <summary>
    /// A Kotlin + Java program started with the Gradle wrapper (as the Kotlin server starts a debuggee) is reloaded with
    /// the shipped script and agent: body changes in both languages, a structural change the JVM refuses, a compiler
    /// error, a new class. Returns false (skipped) without a JDK or the repository's sample.
    /// </summary>
    internal static async Task<bool> RunLiveAsync(string root, Action<bool, string> check)
    {
        var jps = JvmTools.FindTool("jps", null, null);
        if (jps == null) { Console.WriteLine("SKIP: no JDK (jps on JAVA_HOME or PATH) - live hot reload."); return false; }
        string sample, build;
        try
        {
            sample = Path.GetDirectoryName(DartTests.FindRepositoryFile(Path.Combine("samples", "kotlin-jvm", "gradlew.bat")))!;
            build = Path.GetDirectoryName(DartTests.FindRepositoryFile(Path.Combine("src", "Vistudio.Vsix", "Build", "HotReload.gradle")))!;
        }
        catch (FileNotFoundException) { Console.WriteLine("SKIP: samples/kotlin-jvm not found - live hot reload."); return false; }

        // A copy with a space in its path, like real project folders; build outputs of the sample are not copied.
        var project = Path.Combine(root, "hot reload live");
        Copy(sample, project);
        var kotlin = Path.Combine(project, "src", "main", "kotlin");
        var java = Path.Combine(project, "src", "main", "java", "sample");
        Directory.CreateDirectory(java);
        var buildScript = Path.Combine(project, "build.gradle.kts");
        File.WriteAllText(buildScript, File.ReadAllText(buildScript).Replace("sample.MainKt", "sample.HotLoopKt"));
        var entry = Path.Combine(kotlin, "HotLoop.kt");
        File.WriteAllText(entry, "package sample\n\nfun main() {\n    while (true) {\n        println(\"TICK \" + HotText.value() + \" \" + JavaText.value())\n        Thread.sleep(250)\n    }\n}\n");
        var kotlinText = Path.Combine(kotlin, "HotText.kt");
        var javaText = Path.Combine(java, "JavaText.java");
        void Kotlin(string body) => File.WriteAllText(kotlinText, "package sample\n\nobject HotText {\n" + body + "\n}\n");
        void Java(string value) => File.WriteAllText(javaText, "package sample;\n\npublic final class JavaText {\n    public static String value() { return \"" + value + "\"; }\n}\n");
        Kotlin("    fun value(): String = \"kotlin-1\"");
        Java("java-1");

        var lines = new ConcurrentQueue<string>();
        var sessionStart = DateTime.UtcNow;
        var target = new HotReloadTarget(project, "", entry, Path.Combine(build, "HotReload.gradle"), Path.Combine(build, "VistudioHotReloadAgent.java"), Path.Combine(root, "hot reload work"));
        using (var stop = new CancellationTokenSource())
        using (var session = new HotReloadSession(target))
        {
            // The program runs under "gradlew run" for the whole test; reloads compile in the same build while it runs.
            var run = OwnedProcess.RunAsync(GradleCommand.Create(project, "", new[] { "run" }), line => lines.Enqueue(line), line => lines.Enqueue(line), stop.Token);
            try
            {
                async Task<bool> PrintsAsync(string text, int seconds)
                {
                    var watch = Stopwatch.StartNew();
                    while (watch.Elapsed < TimeSpan.FromSeconds(seconds) && !run.IsCompleted)
                    {
                        // Only output from now on counts: older lines show what ran before the reload.
                        while (lines.TryDequeue(out var line)) if (line.Trim() == text) return true;
                        await Task.Delay(100);
                    }
                    return false;
                }
                var started = await PrintsAsync("TICK kotlin-1 java-1", 900);
                check(started, "Live: the program started under gradlew run" + (started ? "" : ": " + string.Join(" | ", lines.Skip(Math.Max(0, lines.Count - 8)))));
                if (!started) return true;

                var pid = HotReloadJvm.Find(await JvmTools.RunAsync(jps, "-l", CancellationToken.None), "sample.HotLoopKt", sessionStart, HotReloadJvm.StartTimeUtc);
                check(pid != null, "Live: jps finds the program's JVM by its main class");
                if (pid == null) return true;
                check(HotReloadJvm.JavaExecutable(pid.Value, null) is string javaExe && File.Exists(javaExe), "Live: java.exe of the program's JDK");

                var log = new List<string>();
                var gradle = new List<string>();
                async Task<HotReloadResult> ReloadAsync()
                {
                    var watch = Stopwatch.StartNew();
                    gradle.Clear();
                    var compilation = await session.CompileAsync(gradle.Add, CancellationToken.None);
                    var compiled = watch.Elapsed;
                    var result = await session.ApplyAsync(compilation, pid.Value, log.Add, CancellationToken.None);
                    Console.WriteLine("    reload: compile " + compiled.TotalSeconds.ToString("0.0") + " s, total " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s: " + result.Summary);
                    return result;
                }

                var result = await ReloadAsync();
                check(result.Error == null && result.Reloaded.Count == 0 && result.Failed.Count == 0 && result.NotLoaded.Count == 0,
                    "Live: a reload without edits changes nothing: " + result.Summary + " " + string.Join(" | ", gradle.Skip(Math.Max(0, gradle.Count - 6))));
                check(Directory.GetFiles(Path.Combine(target.WorkDirectory, "agent"), "vistudio-hot-reload-agent-*.jar").Length == 1 &&
                    !Directory.Exists(Path.Combine(project, "build", "vistudio")), "Live: the agent jar is built outside the project");

                Kotlin("    fun value(): String = \"kotlin-2\"");
                result = await ReloadAsync();
                check(result.Error == null && result.Reloaded.SequenceEqual(new[] { "sample.HotText" }) && result.Failed.Count == 0, "Live: a Kotlin body change reloads its class only: " + result.Summary);
                check(await PrintsAsync("TICK kotlin-2 java-1", 20), "Live: the running program prints the new Kotlin text");

                Java("java-2");
                result = await ReloadAsync();
                check(result.Error == null && result.Reloaded.SequenceEqual(new[] { "sample.JavaText" }), "Live: a Java body change reloads its class only: " + result.Summary);
                check(await PrintsAsync("TICK kotlin-2 java-2", 20), "Live: the running program prints the new Java text");

                // A new method: HotSpot refuses the new class shape and keeps the old code.
                Kotlin("    fun value(): String = \"kotlin-3\" + suffix()\n    private fun suffix(): String = \"!\"");
                result = await ReloadAsync();
                check(result.Error == null && result.Reloaded.Count == 0 && result.Failed.Count == 1 && result.Failed[0].ClassName == "sample.HotText" &&
                    result.Failed[0].Message.Contains("add a method"), "Live: an added method is refused with the JVM's reason: " + result.Summary + " " + string.Join(" ", result.Details));
                lines = new ConcurrentQueue<string>();
                await Task.Delay(1500);
                check(lines.Any(l => l.Trim() == "TICK kotlin-2 java-2") && !lines.Any(l => l.Contains("kotlin-3")), "Live: the program keeps running the old code after a refused reload");

                // Back to the old shape with another body: the refused class is offered again and accepted.
                Kotlin("    fun value(): String = \"kotlin-4\"");
                result = await ReloadAsync();
                check(result.Error == null && result.Reloaded.SequenceEqual(new[] { "sample.HotText" }), "Live: the class reloads once its shape matches again: " + result.Summary);
                check(await PrintsAsync("TICK kotlin-4 java-2", 20), "Live: the running program prints the text after the refused reload");

                Kotlin("    fun value(): String = missing");
                result = await ReloadAsync();
                check(result.Error != null && result.Error.StartsWith("Compilation failed: HotText.kt:4:", StringComparison.Ordinal) && result.Error.Contains("missing"),
                    "Live: a compiler error stops the reload: " + result.Error);
                check(gradle.Any(l => l.StartsWith("e: ", StringComparison.Ordinal) && l.Contains("HotText.kt")), "Live: the compiler diagnostic line reaches the caller");

                Kotlin("    fun value(): String = \"kotlin-5\"");
                File.WriteAllText(Path.Combine(kotlin, "HotExtra.kt"), "package sample\n\nclass HotExtra {\n    fun value(): String = \"extra\"\n}\n");
                result = await ReloadAsync();
                check(result.Error == null && result.Reloaded.SequenceEqual(new[] { "sample.HotText" }) && result.NotLoaded.SequenceEqual(new[] { "sample.HotExtra" }),
                    "Live: after the fix the class reloads and a new, unused class is left to the class loader: " + result.Summary);
                check(await PrintsAsync("TICK kotlin-5 java-2", 20), "Live: the running program prints the fixed text");
                check(!log.Any(l => l.Contains("waiting for the JVM")), "Live: a running program takes the classes without the paused-program notice");

                var daemons = Directory.Exists(target.DaemonRegistry) ? Directory.GetFiles(target.DaemonRegistry, "daemon-*.out.log", SearchOption.AllDirectories) : Array.Empty<string>();
                var daemonIds = daemons.Select(f => int.TryParse(Path.GetFileName(f).Split('-', '.')[1], out var id) ? id : 0).Where(id => id != 0 && HotReloadJvm.StartTimeUtc(id) != null).ToList();
                check(daemonIds.Count == 1, "Live: one Gradle daemon serves the session's reloads (private registry): " + daemonIds.Count);
                session.Dispose();
                await Task.Delay(1500);
                check(daemonIds.All(id => HotReloadJvm.StartTimeUtc(id) == null), "Live: ending the session ends its Gradle daemon");
                check(HotReloadJvm.StartTimeUtc(pid.Value) != null, "Live: the program itself keeps running when the reload session ends");
            }
            finally
            {
                stop.Cancel();
                try { await run; } catch (OperationCanceledException) { }
            }
        }
        return true;
    }

    internal static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (name == "build" || name == ".gradle" || name == ".kotlin" || name == ".vs") continue;
            Copy(directory, Path.Combine(destination, name));
        }
    }
}
