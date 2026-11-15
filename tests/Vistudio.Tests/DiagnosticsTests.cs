using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Vistudio.Core;
using Vistudio.Core.Dart;
using Vistudio.Core.Diagnostics;

/// <summary>
/// T71: CPU/memory sampling, runtime heap probes and the diagnostics timeline behind the Vistudio Diagnostics window.
/// The live part runs a real JVM (JDK found through JAVA_HOME/PATH) and a real Dart VM (VISTUDIO_FLUTTER_SDK or the Dart
/// settings), directly and through the SDK's debug adapter, when they are installed.
/// </summary>
internal static class DiagnosticsTests
{
    internal static void Run(Action<bool, string> check)
    {
        ProcessTreeChecks(check);
        UsageChecks(check);
        JvmParserChecks(check);
        DartParserChecks(check);
        SnapshotChecks(check);
        MonitorChecks(check);
    }

    private static void ProcessTreeChecks(Action<bool, string> check)
    {
        var table = new[]
        {
            new ProcessEntry(10, 1, "devenv.exe"), new ProcessEntry(20, 10, "cmd.exe"), new ProcessEntry(30, 20, "wasmtime.exe"),
            new ProcessEntry(40, 20, "stale.exe"), new ProcessEntry(50, 99, "other.exe"), new ProcessEntry(20, 20, "self.exe"),
        };
        var created = new Dictionary<int, long> { [10] = 100, [20] = 200, [30] = 300, [40] = 150, [50] = 400 };
        var tree = ProcessTree.WithDescendants(20, table, pid => created.TryGetValue(pid, out var t) ? t : (long?)null);
        check(tree.SequenceEqual(new[] { 20, 30 }), "Process tree: root and its child; a reused id created before its parent is left out: " + string.Join(",", tree));
        check(ProcessTree.WithDescendants(77, table, _ => null).Count == 0, "Process tree of an exited root is empty");
        check(ProcessTree.WithDescendants(10, table, pid => created.TryGetValue(pid, out var t) ? t : (long?)null).SequenceEqual(new[] { 10, 20, 30 }), "Process tree includes grandchildren");

        // gradlew.bat --no-daemon: cmd.exe -> java.exe (client) -> java.exe (single-use daemon) -> node.exe / app.exe.
        var gradle = new[]
        {
            new ProcessEntry(100, 1, "cmd.exe"), new ProcessEntry(101, 100, "Java.exe"), new ProcessEntry(102, 101, "conhost.exe"),
            new ProcessEntry(103, 101, "java.exe"), new ProcessEntry(104, 103, "node.exe"), new ProcessEntry(105, 103, "app.kexe"),
        };
        var program = ProgramProcesses.Without(new[] { 100, 101, 102, 103, 104, 105 }, gradle, ProgramProcesses.GradleImages);
        check(program.SequenceEqual(new[] { 104, 105 }), "Program under Gradle: Gradle's cmd, JVMs and console hosts left out (any case): " + string.Join(",", program));
        check(ProgramProcesses.Without(new[] { 100, 101 }, gradle, ProgramProcesses.GradleImages).Count == 0, "Program under Gradle: nothing yet while only Gradle runs");
    }

    private static void UsageChecks(Action<bool, string> check)
    {
        var calculator = new UsageCalculator(2);
        var t0 = new DateTime(2026, 11, 15, 12, 0, 0);
        var first = calculator.Next(t0, new[] { new ProcessCounters(1, 5, 1_000_000, 100, 200), new ProcessCounters(2, 6, 0, 10, 20) });
        check(first.CpuPercent == null && first.WorkingSet == 110 && first.PrivateBytes == 220 && first.Processes == 2, "First sample: memory summed, no CPU % yet");
        // One second later process 1 used one second of CPU on a two-processor machine: 50 %.
        var second = calculator.Next(t0.AddSeconds(1), new[] { new ProcessCounters(1, 5, 11_000_000, 100, 200), new ProcessCounters(3, 7, 50_000_000, 1, 1) });
        check(second.CpuPercent is double c && Math.Abs(c - 50) < 0.001, "CPU %: one CPU second per wall second of two processors is 50 % (new process without baseline ignored): " + second.CpuPercent);
        var reused = calculator.Next(t0.AddSeconds(2), new[] { new ProcessCounters(1, 999, 90_000_000, 1, 1) });
        check(reused.CpuPercent == 0, "CPU %: a reused process id (other creation time) has no baseline");
        var busy = new UsageCalculator(1);
        busy.Next(t0, new[] { new ProcessCounters(1, 5, 0, 0, 0) });
        check(busy.Next(t0.AddSeconds(1), new[] { new ProcessCounters(1, 5, 30_000_000, 0, 0) }).CpuPercent == 100, "CPU % is capped at 100");
        check(DiagnosticsTimeline.FormatBytes(512) == "512 B" && DiagnosticsTimeline.FormatBytes(1536) == "1.5 KB" && DiagnosticsTimeline.FormatBytes(5L << 20) == "5.0 MB"
            && DiagnosticsTimeline.FormatBytes(-(3L << 30)) == "-3.00 GB", "Byte formatting");
        var timeline = new DiagnosticsTimeline("t", capacity: 3);
        for (var i = 0; i < 5; i++) timeline.Add(new UsageSample(t0.AddSeconds(i), i, i, i, 1));
        check(timeline.Samples.Count == 3 && timeline.Samples[0].WorkingSet == 2 && timeline.Latest!.WorkingSet == 4, "Timeline keeps the newest samples up to its capacity");
    }

    private static void JvmParserChecks(Action<bool, string> check)
    {
        var jps = JvmTools.ParseJps("1920 jdk.jcmd/sun.tools.jps.Jps\r\n16068 Spin\r\n11732 Unknown\r\n123 -- process information unavailable\r\n\r\nnoise\r\n77\r\n");
        check(jps.Count == 5 && jps[1] == (16068, "Spin") && jps[2].Main == "Unknown" && jps[4] == (77, ""), "jps -l lines parsed: " + string.Join(";", jps));
        check(JvmTools.IsMain("sample.MainKt", "sample.MainKt") && !JvmTools.IsMain("org.gradle.launcher.daemon.bootstrap.GradleDaemon", "sample.MainKt") && !JvmTools.IsMain("sample.MainKtx", "sample.MainKt"),
            "jps main class must equal the launched class");

        // JDK 21 output captured from a running JVM and from one held suspended by JDWP ("-" for times not measured yet).
        const string header = "    S0C         S1C         S0U         S1U          EC           EU           OC           OU          MC         MU       CCSC      CCSU     YGC     YGCT     FGC    FGCT     CGC    CGCT       GCT   ";
        var running = JvmTools.ParseJstatGc(header, "        0.0      4096.0         0.0      2612.3     491520.0      98304.0     290816.0      38912.0      384.0      153.6     128.0       6.4     27     0.054     0     0.000     0     0.000     0.054");
        check(running != null && running.Used == (long)((2612.3 + 98304 + 38912) * 1024) && running.Capacity == (long)((4096 + 491520 + 290816.0) * 1024)
            && running.Collections == 27 && Math.Abs(running.CollectionSeconds!.Value - 0.054) < 1e-9 && running.Runtime == JvmTools.Runtime,
            "jstat -gc row: heap used/capacity from survivor, eden and old (KB), collections, GC time");
        var suspended = JvmTools.ParseJstatGc(header, "        0.0         0.0         0.0         0.0      12288.0          0.0     249856.0          0.0        0.0        0.0       0.0       0.0      0         -     0         -     0         -         -");
        check(suspended != null && suspended.Used == 0 && suspended.Collections == 0 && suspended.CollectionSeconds == 0, "jstat -gc row of a JVM suspended at start: '-' read as 0");
        var jdk8 = JvmTools.ParseJstatGc(" S0C    S1C    S0U    S1U      EC       EU        OC         OU       MC     MU    CCSC   CCSU   YGC     YGCT    FGC    FGCT     GCT",
            "512.0  512.0   0.0    32.0   4224.0   1104.0   10496.0     0.0     4480.0 780.0  384.0   76.0      1    0.003   0      0.000    0.003");
        check(jdk8 != null && jdk8.Collections == 1 && jdk8.Used == (long)((32 + 1104.0) * 1024), "jstat -gc of JDK 8 (no CGC columns) read by column name");
        check(JvmTools.ParseJstatGc(header, "1 2 3") == null && JvmTools.ParseJstatGc("A B", "x y") == null, "jstat rows that do not match the header are ignored");

        var histogram = JvmTools.ParseClassHistogram("11732:\n num     #instances         #bytes  class name (module)\n-------------------------------------------------------\n" +
            "   1:          2078          87328  [B (java.base@21.0.12.1)\n   2:           539          66992  java.lang.Class (java.base@21.0.12.1)\n" +
            "   3:            10            240  sample.Shape\n   4:             1             16  sample.Box$Inner (unnamed)\nTotal          2628         154576\n", DateTime.Now);
        check(histogram.Classes.Count == 4 && histogram.Classes[0].Name == "[B" && histogram.Classes[0].Instances == 2078 && histogram.Classes[2].Name == "sample.Shape"
            && histogram.Classes[3].Name == "sample.Box$Inner" && histogram.TotalBytes == 154576 && histogram.TotalInstances == 2628,
            "jcmd GC.class_histogram: rows, module suffix removed, Total line");
        var noClasses = false;
        try { JvmTools.ParseClassHistogram("12: Could not attach", DateTime.Now); } catch (InvalidDataException) { noClasses = true; }
        check(noClasses, "A histogram without classes is an error, not an empty snapshot");

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\jdk17\bin\jstat.exe", @"C:\home\bin\jstat.exe", @"C:\path\jstat.exe", @"C:\jre\bin\java.exe" };
        Func<string, string?> env = name => name == "JAVA_HOME" ? @"C:\home" : name == "PATH" ? @"C:\x;""C:\path""" : null;
        check(JvmTools.FindTool("jstat", @"C:\jdk17\bin\java.exe", @"C:\cfg", env, files.Contains) == @"C:\jdk17\bin\jstat.exe", "JDK tool next to the debuggee's java.exe first");
        check(JvmTools.FindTool("jstat", @"C:\jre\bin\java.exe", @"C:\cfg", env, files.Contains) == @"C:\home\bin\jstat.exe", "A JRE has no jstat: JAVA_HOME next");
        check(JvmTools.FindTool("jstat", null, null, n => n == "PATH" ? @"C:\x;""C:\path""" : null, files.Contains) == @"C:\path\jstat.exe", "Then PATH (quoted entries)");
        check(JvmTools.FindTool("jcmd", null, null, env, files.Contains) == null, "No JDK tool found: null");

        var replies = new Queue<string>(new[] { "5 Unknown\n", "5 sample.MainKt\n6 org.gradle.Main\n", "5 sample.MainKt\n9 sample.MainKt\n" });
        var pid = JvmTools.FindJvmAsync(_ => Task.FromResult(replies.Count > 1 ? replies.Dequeue() : replies.Peek()), "sample.MainKt", new HashSet<int> { 5 }, TimeSpan.FromSeconds(10), CancellationToken.None).GetAwaiter().GetResult();
        check(pid == 9, "JVM discovery: the new JVM running the main class, not one that ran before the launch: " + pid);
        var timedOut = false;
        try { JvmTools.FindJvmAsync(_ => Task.FromResult("5 sample.MainKt"), "sample.MainKt", new HashSet<int> { 5 }, TimeSpan.FromMilliseconds(700), CancellationToken.None).GetAwaiter().GetResult(); }
        catch (TimeoutException) { timedOut = true; }
        check(timedOut, "JVM discovery times out when the program never starts");
    }

    private static void DartParserChecks(Action<bool, string> check)
    {
        check(DartVmService.WebSocketUri("ws://127.0.0.1:55354/XrzoL-_Leak=/ws").ToString() == "ws://127.0.0.1:55354/XrzoL-_Leak=/ws", "VM service ws address kept");
        check(DartVmService.WebSocketUri("http://127.0.0.1:55354/XrzoL-_Leak=/").ToString() == "ws://127.0.0.1:55354/XrzoL-_Leak=/ws", "VM service http address -> ws .../ws");
        check(DartVmService.WebSocketUri("https://h:1/t=").ToString() == "wss://h:1/t=/ws", "https -> wss");
        var rejected = 0;
        foreach (var bad in new[] { "file:///c:/x", "not a uri" })
            try { DartVmService.WebSocketUri(bad); } catch (ArgumentException) { rejected++; }
        check(rejected == 2, "Non-VM-service addresses rejected");

        var vm = JObject.Parse("{\"type\":\"VM\",\"pid\":10748,\"isolates\":[{\"type\":\"@Isolate\",\"id\":\"isolates/1\",\"isSystemIsolate\":false},{\"id\":\"isolates/2\",\"isSystemIsolate\":true},{\"id\":\"isolates/3\"}]}");
        var (vmPid, isolates) = DartVmService.ParseVm(vm);
        check(vmPid == 10748 && isolates.SequenceEqual(new[] { "isolates/1", "isolates/3" }), "getVM: process id and non-system isolates");
        var heap = DartVmService.ParseMemoryUsage(new[] { JObject.Parse("{\"heapUsage\":100,\"heapCapacity\":200,\"externalUsage\":5}"), JObject.Parse("{\"heapUsage\":1,\"heapCapacity\":2}") });
        check(heap.Used == 101 && heap.Capacity == 202 && heap.External == 5 && heap.Collections == null && heap.Runtime == DartVmService.Runtime, "getMemoryUsage of several isolates added up");
        var profile = DartVmService.ParseAllocationProfile(new[] { JObject.Parse(
            "{\"members\":[{\"class\":{\"name\":\"_List\",\"library\":{\"uri\":\"dart:core\"}},\"instancesCurrent\":1282,\"bytesCurrent\":287168}," +
            "{\"class\":{\"name\":\"Shape\",\"library\":{\"uri\":\"package:app/shapes.dart\"}},\"instancesCurrent\":3,\"bytesCurrent\":96}," +
            "{\"class\":{\"name\":\"Unused\"},\"instancesCurrent\":0,\"bytesCurrent\":0}]}") }, DateTime.Now);
        check(profile.Classes.Count == 2 && profile.Classes[0].Name == "_List" && profile.Classes[1].Name == "Shape (package:app/shapes.dart)" && profile.TotalBytes == 287264,
            "getAllocationProfile: live classes only, user classes carry their library");
    }

    private static void SnapshotChecks(Action<bool, string> check)
    {
        var now = DateTime.Now;
        var before = new HeapSnapshot(now, "t", new[] { new HeapClassEntry("A", 10, 1000), new HeapClassEntry("B", 5, 500), new HeapClassEntry("Gone", 1, 50) });
        var after = new HeapSnapshot(now, "t", new[] { new HeapClassEntry("A", 12, 1200), new HeapClassEntry("B", 5, 500), new HeapClassEntry("New", 100, 4000) });
        var diff = after.Compare(before);
        check(diff[0].Name == "New" && diff[0].BytesChange == 4000 && diff[1].Name == "A" && diff[1].InstancesChange == 2 && diff[2].Name == "Gone" && diff[2].BytesChange == -50
            && diff[3].Name == "B" && diff[3].BytesChange == 0, "Snapshot difference: largest size change first, new and freed classes included");
        check(after.Compare(null).All(d => d.BytesChange == d.Bytes), "First snapshot compares with nothing");
        check(after.Classes[0].Name == "New" && after.TotalBytes == 5700 && after.TotalInstances == 117, "Snapshot classes ordered by size, totals summed");
    }

    private sealed class FakeCounters : IProcessCounterSource
    {
        internal readonly Dictionary<int, ProcessCounters> Now = new Dictionary<int, ProcessCounters>();
        public bool TryRead(int pid, out ProcessCounters counters) => Now.TryGetValue(pid, out counters);
    }

    private sealed class FakeProbe : IRuntimeProbe
    {
        internal RuntimeHeap? Heap;
        internal bool Fail, Disposed;
        public string Name => "Fake VM";
        public Task<RuntimeHeap?> ReadAsync(CancellationToken token) => Fail ? throw new IOException("gone") : Task.FromResult(Heap);
        public Task<HeapSnapshot> SnapshotAsync(CancellationToken token) => Task.FromResult(new HeapSnapshot(DateTime.Now, "fake", new[] { new HeapClassEntry("X", 2, 64) }));
        public void Dispose() => Disposed = true;
    }

    private static void MonitorChecks(Action<bool, string> check)
    {
        var counters = new FakeCounters();
        var probe = new FakeProbe();
        var time = new DateTime(2026, 11, 15, 12, 0, 0);
        var pids = new List<int> { 4 };
        var monitor = new DiagnosticsMonitor("fake", () => pids, probe, counters, TimeSpan.FromMilliseconds(10), () => time);
        var ended = 0;
        monitor.Ended += () => ended++;
        check(monitor.SampleAsync(CancellationToken.None).GetAwaiter().GetResult() && monitor.Timeline.Samples.Count == 0, "No process yet: keep waiting, no sample");
        counters.Now[4] = new ProcessCounters(4, 1, 0, 1000, 2000);
        probe.Heap = new RuntimeHeap("Fake heap", 100 << 20, 200 << 20, 3, 0.010);
        monitor.SampleAsync(CancellationToken.None).GetAwaiter().GetResult();
        time = time.AddSeconds(1);
        counters.Now[4] = new ProcessCounters(4, 1, 2_500_000, 1100, 2100);
        probe.Heap = new RuntimeHeap("Fake heap", 40 << 20, 200 << 20, 5, 0.030);
        monitor.SetBroken(true);
        monitor.SampleAsync(CancellationToken.None).GetAwaiter().GetResult();
        var samples = monitor.Timeline.Samples;
        var events = monitor.Timeline.Events;
        check(samples.Count == 2 && samples[0].Heap!.Used == 100 << 20 && !samples[0].Broken && samples[1].Broken && samples[1].CpuPercent > 0, "Samples carry the runtime heap and the break mark");
        check(events.Any(e => e.Kind == DiagnosticsEventKind.Break) && events.Any(e => e.Kind == DiagnosticsEventKind.GarbageCollection && e.Text.StartsWith("2 garbage collections (20 ms), heap 100.0 MB -> 40.0 MB")),
            "Break and garbage collection events: " + string.Join(" | ", events));
        monitor.SetBroken(true);
        monitor.SetBroken(false);
        check(monitor.Timeline.Events.Count(e => e.Kind == DiagnosticsEventKind.Break) == 1 && monitor.Timeline.Events.Any(e => e.Kind == DiagnosticsEventKind.Continue), "Repeated break mode recorded once, then continue");

        probe.Fail = true;
        monitor.SampleAsync(CancellationToken.None).GetAwaiter().GetResult();
        monitor.SampleAsync(CancellationToken.None).GetAwaiter().GetResult();
        check(monitor.Timeline.Events.Count(e => e.Text.StartsWith("Fake VM data unavailable")) == 1 && monitor.Timeline.Samples.Last().Heap == null, "A failing runtime is noted once; process samples go on");
        probe.Fail = false;
        probe.Heap = new RuntimeHeap("Dart heap", 50 << 20, 64 << 20);
        monitor.SampleAsync(CancellationToken.None).GetAwaiter().GetResult();
        probe.Heap = new RuntimeHeap("Dart heap", 20 << 20, 64 << 20);
        monitor.SampleAsync(CancellationToken.None).GetAwaiter().GetResult();
        check(monitor.Timeline.Events.Any(e => e.Text == "Dart heap shrank 50.0 MB -> 20.0 MB (garbage collection)"), "Without a collection count, a large heap drop is a collection");

        var snapshot = monitor.TakeSnapshotAsync(CancellationToken.None).GetAwaiter().GetResult();
        check(snapshot.Number == 1 && snapshot.Classes.Count == 1 && snapshot.PrivateBytes == 2100 && monitor.Timeline.Events.Last().Text.StartsWith("Snapshot #1: 1 classes"), "Snapshot from the runtime, numbered, with process memory");

        pids.Clear();
        check(!monitor.SampleAsync(CancellationToken.None).GetAwaiter().GetResult(), "All processes gone after being seen: the session ends");
        monitor.Stop("Debugging stopped.");
        monitor.Stop("again");
        check(ended == 1 && probe.Disposed && monitor.Timeline.Events.Count(e => e.Kind == DiagnosticsEventKind.Exited) == 1, "Stop ends the session once and releases the runtime probe");

        var plain = new DiagnosticsMonitor("plain", () => new[] { 8 }, null, new FakeCounters { Now = { [8] = new ProcessCounters(8, 1, 0, 10, 4096) } }, clock: () => time);
        plain.SampleAsync(CancellationToken.None).GetAwaiter().GetResult();
        var counted = plain.TakeSnapshotAsync(CancellationToken.None).GetAwaiter().GetResult();
        check(!plain.HasRuntime && counted.Classes.Count == 0 && counted.PrivateBytes == 4096 && counted.Source == "process counters", "Without a runtime, a snapshot records process memory");

        // The background loop: samples on its own, then ends when the process disappears.
        var loopCounters = new FakeCounters { Now = { [9] = new ProcessCounters(9, 1, 0, 1, 1) } };
        var loop = new DiagnosticsMonitor("loop", () => new[] { 9 }, null, loopCounters, TimeSpan.FromMilliseconds(20));
        var done = new TaskCompletionSource<bool>();
        loop.Ended += () => done.TrySetResult(true);
        loop.Start("loop test");
        Thread.Sleep(150);
        loopCounters.Now.Clear();
        check(done.Task.Wait(5000) && loop.Timeline.Samples.Count >= 2 && !loop.IsRunning && loop.Timeline.Events.Last().Text == "The program exited.", "Background sampling ends by itself when the program exits");
    }

    // ---- Live checks ----

    /// <param name="flutter">Also debug the Flutter sample on the Windows desktop device (a full Windows build, minutes).</param>
    internal static async Task<bool> RunLiveAsync(string root, Action<bool, string> collect, bool flutter = false, bool kotlinTargets = false)
    {
        // Live results are printed: their messages carry the measured values (the validation record quotes them).
        Action<bool, string> check = (condition, message) => { Console.WriteLine("    " + (condition ? "ok: " : "FAIL: ") + message); collect(condition, message); };
        var any = false;
        any |= await ProcessTreeLiveAsync(check);
        any |= await ProgramUnderGradleLiveAsync(check);
        any |= await JvmLiveAsync(root, check);
        any |= await DartLiveAsync(root, check);
        if (flutter) any |= await FlutterLiveAsync(root, check);
        if (kotlinTargets) any |= await KotlinTargetsLiveAsync(root, check);
        return any;
    }

    /// <summary>
    /// F5 on a non-JVM Kotlin target (creator decision, T71): the target's Gradle run task under the program-under-Gradle
    /// monitor, on a copy of samples/kotlin-multiplatform whose main keeps working a few seconds so that it can be sampled.
    /// </summary>
    private static async Task<bool> KotlinTargetsLiveAsync(string root, Action<bool, string> check)
    {
        var source = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "samples", "kotlin-multiplatform"));
        if (!File.Exists(Path.Combine(source, "gradlew.bat"))) { Console.WriteLine("SKIP: samples/kotlin-multiplatform not found - live Kotlin target diagnostics."); return false; }
        var folder = Path.Combine(root, "diagnostics kmp");
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = file.Substring(source.Length + 1);
            var top = relative.Split(Path.DirectorySeparatorChar)[0];
            if (top == "build" || top == ".gradle" || top == ".kotlin") continue;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, relative))!);
            File.Copy(file, Path.Combine(folder, relative));
        }
        var main = Directory.GetFiles(Path.Combine(folder, "src", "commonMain"), "Main.kt", SearchOption.AllDirectories).Single();
        File.WriteAllText(main, File.ReadAllText(main).Replace("    println(\"VISTUDIO_KMP_PASS\")",
            "    val mark = kotlin.time.TimeSource.Monotonic.markNow()\n    var spins = 0L\n    while (mark.elapsedNow().inWholeMilliseconds < 4000) spins++\n    println(\"VISTUDIO_KMP_PASS \" + (spins > 0))"));
        foreach (var (target, build, run) in new[]
        {
            ("js", ":jsMainClasses", ":jsNodeDevelopmentRun"),
            ("wasmJs", ":wasmJsMainClasses", ":wasmJsNodeDevelopmentRun"),
            ("mingwX64", ":linkDebugExecutableMingwX64", ":runDebugExecutableMingwX64"),
        })
        {
            var output = new System.Text.StringBuilder();
            // Build and run are separate invocations, as in the Kotlin target integration (shared JS/Wasm output paths).
            var built = await OwnedProcess.RunAsync(GradleCommand.Create(folder, "", new[] { build }), line => output.AppendLine(line), CancellationToken.None);
            check(built == 0, "Kotlin " + target + ": Gradle " + build + " succeeded" + (built == 0 ? "" : ": " + string.Join(" | ", output.ToString().Split('\n').Reverse().Take(8).Reverse())));
            if (built != 0) continue;
            output.Clear();
            DiagnosticsMonitor? monitor = null;
            var exit = await OwnedProcess.RunAsync(GradleCommand.Create(folder, "", new[] { run }), line => output.AppendLine(line), line => output.AppendLine(line), CancellationToken.None,
                gradle => monitor = DiagnosticsTargets.ForProgramUnderGradle(gradle, "kmp " + target));
            monitor!.Stop("The Gradle run finished.");
            var samples = monitor.Timeline.Samples;
            check(exit == 0 && output.ToString().Contains("VISTUDIO_KMP_PASS true"), "Kotlin " + target + ": Gradle " + run + " ran the program");
            check(samples.Count >= 2 && samples.Any(s => s.CpuPercent > 1) && samples.All(s => s.PrivateBytes > 0),
                "Kotlin " + target + ": the program under Gradle was sampled without Gradle's JVMs: " + samples.Count + " samples, processes " +
                string.Join(",", samples.Select(s => s.Processes).Distinct()) + ", max CPU " + samples.Select(s => s.CpuPercent ?? 0).DefaultIfEmpty().Max().ToString("0.0") +
                " %, private bytes " + DiagnosticsTimeline.FormatBytes(samples.Select(s => s.PrivateBytes).DefaultIfEmpty().Max()));
        }
        return true;
    }

    private static async Task<bool> FlutterLiveAsync(string root, Action<bool, string> check)
    {
        var settings = new DartSettings { FlutterSdkPath = Environment.GetEnvironmentVariable("VISTUDIO_FLUTTER_SDK") ?? DartSettings.LoadGlobal().FlutterSdkPath, DartSdkPath = DartSettings.LoadGlobal().DartSdkPath };
        var sdk = DartToolchain.Find(settings);
        if (sdk?.FlutterRoot == null) { Console.WriteLine("SKIP: no Flutter SDK - live Flutter diagnostics."); return false; }
        var folder = Path.Combine(root, "diagnostics flutter");
        DartTests.CopySample("flutter-app", folder);
        var app = DartProject.Find(folder)!;
        check(await OwnedProcess.RunAsync(DartCommandFactory.PubGet(sdk, app), _ => { }, CancellationToken.None) == 0, "flutter pub get for the Flutter sample");
        var main = Path.Combine(folder, "lib", "main.dart");
        var launch = DartDebugLaunch.Create(app, main, new DartSettings { FlutterDevice = "windows" }, DartLaunchKind.Program, noDebug: false);
        var build = TimeSpan.FromMinutes(12);
        using (var dap = new DartTests.DapClient(DartCommandFactory.DebugAdapter(sdk, true, false, folder)))
        {
            await dap.InitializeAsync("dart");
            var launched = dap.RequestAsync("launch", launch, build);
            await dap.WaitEventAsync("initialized", build);
            await dap.RequestAsync("configurationDone", new JObject());
            var announced = await dap.WaitEventAsync("dart.debuggerUris", build);
            var vmService = (string?)announced["body"]?["vmServiceUri"];
            check(!string.IsNullOrEmpty(vmService), "Flutter adapter sent dart.debuggerUris with vmServiceUri: " + announced["body"]);
            var monitor = await DiagnosticsTargets.ForDartVmAsync(vmService!, "flutter_app", CancellationToken.None);
            var pid = int.Parse(Regex.Match(monitor.Timeline.Title, @"pid (\d+)").Groups[1].Value);
            var image = Path.GetFileName(Win32ProcessCounters.ImagePath(pid) ?? "");
            check(image.Length != 0 && !image.StartsWith("dart", StringComparison.OrdinalIgnoreCase), "Flutter session: the VM service names the Windows app process (pid " + pid + ", " + image + ")");
            await WaitAsync(() => monitor.Timeline.Samples.Count(s => s.Heap != null) >= 3, TimeSpan.FromSeconds(30));
            var samples = monitor.Timeline.Samples;
            check(samples.Any(s => s.CpuPercent != null) && samples.Any(s => s.Heap != null && s.Heap.Used > 0) && samples.All(s => s.PrivateBytes > 0),
                "Flutter session: CPU, process memory and Dart heap sampled: private bytes " + DiagnosticsTimeline.FormatBytes(samples.Last().PrivateBytes) + ", heap " +
                DiagnosticsTimeline.FormatBytes(samples.LastOrDefault(s => s.Heap != null)?.Heap?.Used ?? 0) + ", max CPU " + samples.Max(s => s.CpuPercent ?? 0).ToString("0.0") + " %");
            var snapshot = await monitor.TakeSnapshotAsync(CancellationToken.None);
            check(snapshot.Classes.Count > 50, "Flutter session: allocation profile snapshot (" + snapshot.Classes.Count + " classes, " + DiagnosticsTimeline.FormatBytes(snapshot.TotalBytes) + ")");
            await dap.Connection.StopAsync();
            await WaitAsync(() => !monitor.IsRunning, TimeSpan.FromSeconds(20));
            check(!monitor.IsRunning, "Flutter session: the session ended when the app stopped");
            try { await launched; } catch (Exception) { }
        }
        return true;
    }

    private static async Task<bool> ProcessTreeLiveAsync(Action<bool, string> check)
    {
        // A shell that starts a busy child stands in for "wasmtime run": the monitor must see both and the CPU work.
        var started = new TaskCompletionSource<int>();
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            "/c powershell.exe -NoProfile -Command \"$e=[DateTime]::Now.AddSeconds(4); while([DateTime]::Now -lt $e){}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var run = OwnedProcess.RunAsync(start, _ => { }, _ => { }, CancellationToken.None, pid => started.TrySetResult(pid));
        var root = await started.Task;
        var monitor = DiagnosticsTargets.ForProcessTree(root, "busy loop");
        var ended = new TaskCompletionSource<bool>();
        monitor.Ended += () => ended.TrySetResult(true);
        await run;
        check(await Task.WhenAny(ended.Task, Task.Delay(10000)) == ended.Task, "Live process tree: the session ended when the program exited");
        var samples = monitor.Timeline.Samples;
        check(samples.Any(s => s.Processes >= 2) && samples.Any(s => s.CpuPercent > 1) && samples.All(s => s.PrivateBytes > 0),
            "Live process tree: shell and child counted, busy CPU and memory seen: max CPU " + samples.Max(s => s.CpuPercent ?? 0).ToString("0.0") + " %, processes " + samples.Max(s => s.Processes));
        return true;
    }

    private static async Task<bool> ProgramUnderGradleLiveAsync(Action<bool, string> check)
    {
        // Stand-in for "gradlew.bat --no-daemon <run task>": cmd.exe runs a JVM first (the build), then the program. Only
        // the program may be counted, and sampling starts when it appears.
        var java = JvmTools.FindTool("java", null, null);
        if (java == null) { Console.WriteLine("SKIP: no java.exe - live program-under-Gradle check."); return false; }
        var started = new TaskCompletionSource<int>();
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            "/d /s /c \"\"" + java + "\" -XX:+UseSerialGC -cp . -version 2>nul & powershell.exe -NoProfile -Command \"$e=[DateTime]::Now.AddSeconds(3); while([DateTime]::Now -lt $e){}\"\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var run = OwnedProcess.RunAsync(start, _ => { }, _ => { }, CancellationToken.None, pid => started.TrySetResult(pid));
        var monitor = DiagnosticsTargets.ForProgramUnderGradle(await started.Task, "stand-in target");
        var ended = new TaskCompletionSource<bool>();
        monitor.Ended += () => ended.TrySetResult(true);
        await run;
        check(await Task.WhenAny(ended.Task, Task.Delay(10000)) == ended.Task, "Live program under Gradle: the session ended when the program exited");
        var samples = monitor.Timeline.Samples;
        check(samples.Count >= 2 && samples.All(s => s.Processes == 1) && samples.Any(s => s.CpuPercent > 1),
            "Live program under Gradle: only the program counted (cmd, java and conhost left out), busy CPU seen: " + samples.Count + " samples, processes " +
            string.Join(",", samples.Select(s => s.Processes).Distinct()) + ", max CPU " + samples.Select(s => s.CpuPercent ?? 0).DefaultIfEmpty().Max().ToString("0.0") + " %");
        return true;
    }

    private static async Task<bool> JvmLiveAsync(string root, Action<bool, string> check)
    {
        var javac = JvmTools.FindTool("javac", null, null);
        var jps = JvmTools.FindTool("jps", null, null);
        if (javac == null || jps == null) { Console.WriteLine("SKIP: no JDK (javac/jps on JAVA_HOME or PATH) - live JVM diagnostics."); return false; }
        var java = Path.Combine(Path.GetDirectoryName(javac)!, "java.exe");
        var folder = Path.Combine(root, "diagnostics jvm");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "VistudioSpin.java"),
            "public class VistudioSpin { static class Shape { long[] data = new long[16]; } public static void main(String[] a) throws Exception {\n" +
            "  java.util.List<Object> keep = new java.util.ArrayList<>(); for (long i = 0;; i++) { keep.add(new Shape()); keep.add(new byte[512]);\n" +
            "  if (keep.size() > 40000) keep.clear(); if (i % 20000 == 0) Thread.sleep(5); } } }\n");
        check(await OwnedProcess.RunAsync(JvmStart(javac, "VistudioSpin.java", folder), _ => { }, CancellationToken.None) == 0, "javac compiled the live JVM program");

        var before = await DiagnosticsTargets.RunningJvmsAsync(jps, CancellationToken.None);
        using (var program = new OwnedProcess(JvmStart(java, "-agentlib:jdwp=transport=dt_socket,server=y,suspend=n,address=127.0.0.1:0 -cp . VistudioSpin", folder)))
        {
            var found = Stopwatch.StartNew();
            var monitor = await DiagnosticsTargets.ForJvmAsync(jps, "VistudioSpin", before, null, TimeSpan.FromSeconds(60), CancellationToken.None);
            Console.WriteLine("    JVM found in " + found.ElapsedMilliseconds + " ms: " + monitor.Timeline.Title);
            check(monitor.Timeline.Title.Contains("pid " + program.Id), "Live JVM: discovery found the program's JVM (pid " + program.Id + "): " + monitor.Timeline.Title);
            await WaitAsync(() => monitor.Timeline.Samples.Count(s => s.Heap != null) >= 3, TimeSpan.FromSeconds(20));
            var samples = monitor.Timeline.Samples;
            check(samples.Any(s => s.CpuPercent > 0) && samples.All(s => s.WorkingSet > 0), "Live JVM: CPU and memory sampled: max CPU " + samples.Max(s => s.CpuPercent ?? 0).ToString("0.0") + " %");
            var heap = samples.Last(s => s.Heap != null).Heap!;
            check(heap.Capacity > 0 && heap.Used > 0 && heap.Collections != null, "Live JVM: jstat heap " + DiagnosticsTimeline.FormatBytes(heap.Used) + " of " + DiagnosticsTimeline.FormatBytes(heap.Capacity) + ", " + heap.Collections + " GCs");
            await WaitAsync(() => monitor.Timeline.Events.Any(e => e.Kind == DiagnosticsEventKind.GarbageCollection), TimeSpan.FromSeconds(15));
            check(monitor.Timeline.Events.Any(e => e.Kind == DiagnosticsEventKind.GarbageCollection), "Live JVM: garbage collections appear as events");
            var snapshot = await monitor.TakeSnapshotAsync(CancellationToken.None);
            check(snapshot.Classes.Any(c => c.Name == "VistudioSpin$Shape" && c.Instances > 0) && snapshot.Classes.Any(c => c.Name == "[B"), "Live JVM: jcmd snapshot lists the program's classes");
            await program.StopAsync();
            await WaitAsync(() => !monitor.IsRunning, TimeSpan.FromSeconds(10));
            check(!monitor.IsRunning && monitor.Timeline.Events.Last().Kind == DiagnosticsEventKind.Exited, "Live JVM: the session ended when the JVM exited");
        }

        // As at a breakpoint: every Java thread suspended by JDWP. jstat and jcmd still answer.
        using (var held = new OwnedProcess(JvmStart(java, "-agentlib:jdwp=transport=dt_socket,server=y,suspend=y,address=127.0.0.1:0 -cp . VistudioSpin", folder)))
        {
            await Task.Delay(1500);
            var java2 = Win32ProcessCounters.ImagePath(held.Id);
            using (var probe = new JvmProbe(held.Id, JvmTools.FindTool("jstat", java2, null), JvmTools.FindTool("jcmd", java2, null)))
            {
                RuntimeHeap? heap = null;
                await WaitAsync(() => (heap = probe.ReadAsync(CancellationToken.None).Result) != null, TimeSpan.FromSeconds(15));
                check(heap != null && heap.Capacity > 0, "Live JVM suspended by JDWP: jstat still reports the heap");
                var snapshot = await probe.SnapshotAsync(CancellationToken.None);
                check(snapshot.Classes.Count > 10, "Live JVM suspended by JDWP: jcmd class histogram still works (" + snapshot.Classes.Count + " classes)");
            }
            await held.StopAsync();
        }
        return true;
    }

    private static ProcessStartInfo JvmStart(string tool, string arguments, string folder) => new ProcessStartInfo(tool, arguments)
    { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };

    private static async Task<bool> DartLiveAsync(string root, Action<bool, string> check)
    {
        var settings = new DartSettings { FlutterSdkPath = Environment.GetEnvironmentVariable("VISTUDIO_FLUTTER_SDK") ?? DartSettings.LoadGlobal().FlutterSdkPath, DartSdkPath = DartSettings.LoadGlobal().DartSdkPath };
        var sdk = DartToolchain.Find(settings);
        if (sdk == null) { Console.WriteLine("SKIP: no Dart SDK (set VISTUDIO_FLUTTER_SDK) - live Dart diagnostics."); return false; }
        if (Console.InputEncoding.GetPreamble().Length > 0) Console.InputEncoding = new System.Text.UTF8Encoding(false);
        var folder = Path.Combine(root, "diagnostics dart");
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "pubspec.yaml"), "name: vistudio_spin\nenvironment:\n  sdk: ^3.0.0\n");
        var program = Path.Combine(folder, "bin", "vistudio_spin.dart");
        File.WriteAllText(program, "class Shape { final List<int> data = List.filled(16, 0); }\n" +
            "void main() async { final keep = <Object>[]; for (var i = 0;; i++) { keep.add(Shape()); if (keep.length > 40000) keep.clear(); if (i % 20000 == 0) await Future.delayed(const Duration(milliseconds: 5)); } }\n");
        var project = DartProject.Find(folder)!;
        check(await OwnedProcess.RunAsync(DartCommandFactory.PubGet(sdk, project), _ => { }, CancellationToken.None) == 0, "dart pub get for the live Dart program");

        // 1. The VM service of a plain `dart run`, paused at start: process id, heap and allocation profile still answer.
        var uri = new TaskCompletionSource<string>();
        var start = new ProcessStartInfo(sdk.Dart, "run --enable-vm-service=0 --pause-isolates-on-start bin/vistudio_spin.dart")
        { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var paused = new OwnedProcess(start))
        {
            _ = Task.Run(() => OwnedProcess.PumpAsync(new StreamReader(paused.Output), line =>
            {
                var m = Regex.Match(line, @"listening on (http://\S+)");
                if (m.Success) uri.TrySetResult(m.Groups[1].Value);
            }));
            check(await Task.WhenAny(uri.Task, Task.Delay(60000)) == uri.Task, "dart run announced its VM service");
            var monitor = await DiagnosticsTargets.ForDartVmAsync(uri.Task.Result, "vistudio_spin", CancellationToken.None);
            await WaitAsync(() => monitor.Timeline.Samples.Count(s => s.Heap != null) >= 2, TimeSpan.FromSeconds(15));
            var sample = monitor.Timeline.Samples.LastOrDefault(s => s.Heap != null);
            check(sample != null && sample.Heap!.Capacity > 0 && sample.WorkingSet > 0, "Live Dart VM paused at start: heap " + (sample == null ? "none" : DiagnosticsTimeline.FormatBytes(sample.Heap!.Used)) + " and process memory sampled");
            var snapshot = await monitor.TakeSnapshotAsync(CancellationToken.None);
            check(snapshot.Classes.Count > 10, "Live Dart VM paused at start: allocation profile snapshot (" + snapshot.Classes.Count + " classes)");
            await paused.StopAsync();
            await WaitAsync(() => !monitor.IsRunning, TimeSpan.FromSeconds(10));
            check(!monitor.IsRunning, "Live Dart VM: the session ended when the program exited");
        }

        // 2. Through the SDK's debug adapter, as in a Visual Studio F5 session: dart.debuggerUris leads to the program.
        var launch = DartDebugLaunch.Create(project, program, new DartSettings(), DartLaunchKind.Program, noDebug: false);
        using (var dap = new DartTests.DapClient(DartCommandFactory.DebugAdapter(sdk, false, false, folder)))
        {
            await dap.InitializeAsync("dart");
            var launched = dap.RequestAsync("launch", launch);
            await dap.WaitEventAsync("initialized", TimeSpan.FromSeconds(60));
            await dap.RequestAsync("configurationDone", new JObject());
            check((bool?)(await launched)["success"] == true, "Dart adapter launched the live program");
            var announced = await dap.WaitEventAsync("dart.debuggerUris", TimeSpan.FromSeconds(60));
            var vmService = (string?)announced["body"]?["vmServiceUri"];
            check(!string.IsNullOrEmpty(vmService), "Dart adapter sent dart.debuggerUris with vmServiceUri: " + announced["body"]);
            var monitor = await DiagnosticsTargets.ForDartVmAsync(vmService!, "vistudio_spin", CancellationToken.None);
            var pid = int.Parse(Regex.Match(monitor.Timeline.Title, @"pid (\d+)").Groups[1].Value);
            var image = Path.GetFileName(Win32ProcessCounters.ImagePath(pid) ?? "");
            check(image.Equals("dart.exe", StringComparison.OrdinalIgnoreCase) || image.Equals("dartvm.exe", StringComparison.OrdinalIgnoreCase), "Debug adapter session: the VM service names the Dart VM process (pid " + pid + ", " + image + ")");
            await WaitAsync(() => monitor.Timeline.Samples.Count(s => s.Heap != null && s.CpuPercent > 0) >= 2, TimeSpan.FromSeconds(20));
            var samples = monitor.Timeline.Samples;
            check(samples.Any(s => s.CpuPercent > 0) && samples.Any(s => s.Heap != null && s.Heap.Used > 0),
                "Debug adapter session: CPU and Dart heap sampled: max CPU " + samples.Max(s => s.CpuPercent ?? 0).ToString("0.0") + " %, heap " + DiagnosticsTimeline.FormatBytes(samples.Last(s => s.Heap != null).Heap!.Used));
            var snapshot = await monitor.TakeSnapshotAsync(CancellationToken.None);
            check(snapshot.Classes.Any(c => c.Name.StartsWith("Shape (") && c.Instances > 0), "Debug adapter session: snapshot lists the program's Shape objects");
            await dap.Connection.StopAsync();
            await WaitAsync(() => !monitor.IsRunning, TimeSpan.FromSeconds(15));
            check(!monitor.IsRunning, "Debug adapter session: the session ended with the adapter");
        }
        return true;
    }

    private static async Task WaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < timeout) await Task.Delay(200);
    }
}
