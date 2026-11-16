using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Vistudio;
using Vistudio.Core;
using Vistudio.Core.Diagnostics;
using Vistudio.Core.HotReload;

/// <summary>
/// Kotlin/JVM hot reload under the real debugger (T72): a copy of samples/kotlin-jvm runs through the installed Kotlin
/// server's DAP endpoint with the launch arguments and DAP session code Visual Studio uses, and is reloaded with the
/// production reload code. Checks that the JVM is found by its main class, that a body change reaches the running
/// program, that a reload asked for at a breakpoint is applied when the program continues, how breakpoints in a reloaded
/// class behave, and a run without debugging (Ctrl+F5) with vmArgs.
/// </summary>
internal static class HotReloadDebugProbe
{
    /// <summary>One Content-Length JSON connection: LSP on the server's stdio, or DAP through <see cref="KotlinDapSession"/>.</summary>
    private sealed class Peer
    {
        private readonly Func<JObject, Task> send;
        private readonly bool lsp;
        private readonly List<JObject> pending = new List<JObject>();
        private int sequence;
        private volatile string? closed;
        private readonly DateTime created = DateTime.UtcNow;
        private readonly List<string> events = new List<string>();

        /// <summary>DAP events in arrival order with their time since the connection (output events without their text).</summary>
        internal string[] Events(bool withOutput = false) { lock (events) return events.Where(e => withOutput || !e.EndsWith(" output")).Select(e => e.Length > 160 ? e.Substring(0, 160) + "..." : e).ToArray(); }

        internal Peer(Func<CancellationToken, Task<JObject?>> read, Func<JObject, Task> send, bool lsp)
        {
            this.send = send;
            this.lsp = lsp;
            _ = Task.Run(async () =>
            {
                try
                {
                    JObject? message;
                    while ((message = await read(CancellationToken.None)) != null)
                    {
                        if (lsp && message["method"] != null && message["id"] != null)
                        {
                            // Requests from the server: configuration items are answered with nulls, the rest with null.
                            JToken result = (string?)message["method"] == "workspace/configuration"
                                ? new JArray(((JArray?)message["params"]?["items"] ?? new JArray()).Select(_ => JValue.CreateNull()))
                                : JValue.CreateNull();
                            await send(new JObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["result"] = result });
                            continue;
                        }
                        if (!lsp && (string?)message["type"] == "event")
                            lock (events) events.Add(((DateTime.UtcNow - created).TotalSeconds).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s " + (string?)message["event"] +
                                ((string?)message["event"] == "output" ? "" : " " + (message["body"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "")));
                        lock (pending) pending.Add(message);
                    }
                    closed = "connection closed";
                }
                catch (Exception e) { closed = e.Message; }
            });
        }

        internal async Task<JObject?> PollAsync(Func<JObject, bool> predicate, double seconds)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (true)
            {
                lock (pending)
                {
                    var index = pending.FindIndex(m => predicate(m));
                    if (index >= 0) { var found = pending[index]; pending.RemoveAt(index); return found; }
                }
                if (DateTime.UtcNow >= end || closed != null) return null;
                await Task.Delay(50);
            }
        }

        internal async Task<JObject> WaitAsync(Func<JObject, bool> predicate, double seconds, string what) =>
            await PollAsync(predicate, seconds) ?? throw new TimeoutException("No " + what + " within " + seconds + " s" + (closed == null ? "" : " (" + closed + ")"));

        /// <summary>What arrived and was not consumed, for a failed check: one short line per message.</summary>
        internal void Dump(string title)
        {
            lock (pending)
            {
                Console.WriteLine("    " + title + ": " + pending.Count + " unread message(s)" + (closed == null ? "" : ", " + closed));
                foreach (var message in pending.Skip(Math.Max(0, pending.Count - 40)))
                {
                    var line = message.ToString(Newtonsoft.Json.Formatting.None);
                    Console.WriteLine("      " + (line.Length > 400 ? line.Substring(0, 400) + "..." : line));
                }
            }
        }

        /// <summary>Forgets queued messages of one kind, so the next wait sees only what happens from now on.</summary>
        internal void Forget(Func<JObject, bool> predicate) { lock (pending) pending.RemoveAll(m => predicate(m)); }

        internal async Task<int> RequestAsync(string method, JToken arguments)
        {
            var id = Interlocked.Increment(ref sequence);
            await send(lsp
                ? new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = arguments }
                : new JObject { ["seq"] = id, ["type"] = "request", ["command"] = method, ["arguments"] = arguments });
            return id;
        }

        internal Task NotifyAsync(string method, JToken arguments) => send(new JObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = arguments });

        internal async Task<JToken?> ResponseAsync(int id, double seconds, string what)
        {
            var response = await WaitAsync(m => lsp ? m["method"] == null && (int?)m["id"] == id : (string?)m["type"] == "response" && (int?)m["request_seq"] == id, seconds, "response to " + what);
            if (response["error"] != null || (bool?)response["success"] == false) throw new InvalidOperationException(what + " failed: " + response.ToString(Newtonsoft.Json.Formatting.None));
            return lsp ? response["result"] : response["body"];
        }

        internal async Task<JToken?> CallAsync(string method, JToken arguments, double seconds = 120) => await ResponseAsync(await RequestAsync(method, arguments), seconds, method);
    }

    private static string? FindServer()
    {
        var configured = Environment.GetEnvironmentVariable("KOTLIN_LSP");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        try { configured = KotlinSettings.LoadGlobal().LanguageServerPath; } catch (Exception) { configured = null; }
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var servers = SettingsXml.PathFor("servers");
        if (!Directory.Exists(servers)) return null;
        return Directory.GetDirectories(servers, "kotlin-server-*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(d => Path.Combine(d, "bin", "intellij-server.exe")).FirstOrDefault(File.Exists);
    }

    /// <param name="cold">Also try a launch without a daemon first and report how it ends (the state before the session daemon).</param>
    internal static async Task<bool> RunAsync(string root, Action<bool, string> check, bool cold = false)
    {
        var server = FindServer();
        var jps = JvmTools.FindTool("jps", null, null);
        if (server == null || jps == null)
        {
            Console.WriteLine("SKIP: " + (server == null ? "no Kotlin language server (KOTLIN_LSP, Tools > Options, %LOCALAPPDATA%\\Vistudio\\servers)" : "no JDK (jps)") + " - hot reload under the debugger.");
            return false;
        }
        var sample = Path.GetDirectoryName(DartTests.FindRepositoryFile(Path.Combine("samples", "kotlin-jvm", "gradlew.bat")))!;
        var build = Path.GetDirectoryName(DartTests.FindRepositoryFile(Path.Combine("src", "Vistudio.Vsix", "Build", "HotReload.gradle")))!;
        var project = Path.Combine(root, "hot reload debug");
        HotReloadTests.Copy(sample, project);
        var kotlin = Path.Combine(project, "src", "main", "kotlin");
        var entry = Path.Combine(kotlin, "HotLoop.kt");
        var textFile = Path.Combine(kotlin, "HotText.kt");
        File.WriteAllText(entry, "package sample\n\nfun main() {\n    var i = 0\n    while (i < 4800) {\n        println(\"TICK \" + HotText.value())\n        Thread.sleep(250)\n        i++\n    }\n}\n");
        // The return statement stays on line 6 in every version, so a breakpoint there is valid before and after a reload.
        const int breakLine = 6;
        void Text(string value) => File.WriteAllText(textFile, "package sample\n\nobject HotText {\n    fun value(): String {\n        val text = \"" + value + "\"\n        return text\n    }\n}\n");
        Text("kotlin-1");
        const string mainClass = "sample.HotLoopKt";

        // The server's debugger stops trying to attach 30 s after the launch request (its log: "Debugger is waiting for
        // application to start", then "Disconnected from the target VM"), and a Gradle client that first has to start a
        // JVM for the build can need longer. As F5 in Visual Studio does (KotlinDebugCoordinator), every launch here runs
        // on the session's Gradle daemon, warmed up right before the launch request. The registry path has a space.
        var daemon = new GradleSessionDaemon(Path.Combine(root, "launch daemon", Guid.NewGuid().ToString("N").Substring(0, 8)));

        var start = ProcessCommands.LanguageServer(server, "--stdio \"--system-path=" + SettingsXml.PathFor("kotlin-lsp-hot-reload-probe") + "\"", project);
        // As the other server probes do on this machine: the Windows certificate store for the server's own downloads.
        start.EnvironmentVariables["IJ_JAVA_OPTIONS"] = "-Djavax.net.ssl.trustStoreType=Windows-ROOT -Djavax.net.ssl.trustStore=NUL";
        KotlinServerCompatibility.Configure(start);
        // See DartLiveTests: under a UTF-8 console .NET Framework writes a BOM into every redirected stdin, which breaks the
        // first frame. devenv has no console, so only the test host needs this.
        if (Console.InputEncoding.GetPreamble().Length > 0) Console.InputEncoding = new System.Text.UTF8Encoding(false);
        var serverErrors = new Queue<string>();
        using (var process = new OwnedProcess(start))
        {
            _ = Task.Run(() => OwnedProcess.PumpAsync(process.Error, line => { lock (serverErrors) { serverErrors.Enqueue(line); if (serverErrors.Count > 30) serverErrors.Dequeue(); } }));
            var writeGate = new SemaphoreSlim(1, 1);
            var lsp = new Peer(token => KotlinDapSourceStream.ReadMessageAsync(process.Output, token), async message =>
            {
                var frame = KotlinDapSourceStream.Encode(message);
                await writeGate.WaitAsync();
                try { await process.Input.WriteAsync(frame, 0, frame.Length); await process.Input.FlushAsync(); }
                finally { writeGate.Release(); }
            }, lsp: true);
            try
            {
                var rootUri = new Uri(project.TrimEnd('\\', '/') + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
                await lsp.CallAsync("initialize", new JObject
                {
                    ["processId"] = System.Diagnostics.Process.GetCurrentProcess().Id, ["rootUri"] = rootUri,
                    ["workspaceFolders"] = new JArray(new JObject { ["uri"] = rootUri, ["name"] = Path.GetFileName(project) }),
                    ["capabilities"] = new JObject { ["workspace"] = new JObject { ["configuration"] = true }, ["window"] = new JObject { ["workDoneProgress"] = true } },
                }, 300);
                await lsp.NotifyAsync("initialized", new JObject());
                foreach (var path in new[] { entry, textFile })
                    await lsp.NotifyAsync("textDocument/didOpen", new JObject { ["textDocument"] = new JObject { ["uri"] = new Uri(path).AbsoluteUri, ["languageId"] = "kotlin", ["version"] = 1, ["text"] = File.ReadAllText(path) } });
                await lsp.WaitAsync(m => (string?)m["method"] == "intellij/importLog" && (bool?)m["params"]?["succeeded"] == true, 900, "Gradle import by the Kotlin server");
                check(true, "Debugger: the Kotlin server imported the Gradle project");

                Func<JObject, bool> Output(string text) => m => (string?)m["event"] == "output" && ((string?)m["body"]?["output"] ?? "").Contains(text);
                JObject Source() => new JObject { ["path"] = textFile };
                JObject Breakpoints(params int[] lines) => new JObject { ["source"] = Source(), ["breakpoints"] = new JArray(lines.Select(l => new JObject { ["line"] = l })) };

                async Task<(Peer Dap, TcpClient Client)> LaunchAsync(JObject config, int[] breakpoints, bool cold = false)
                {
                    var tool = (JObject?)await lsp.CallAsync("workspace/executeCommand", new JObject { ["command"] = "intellij.java.buildTool", ["arguments"] = new JArray(new JObject { ["uri"] = new Uri(entry).AbsoluteUri }) });
                    // The production path: launch arguments from KotlinJvmLaunch, the daemon warmed with the launch's environment.
                    var arguments = KotlinJvmLaunch.Create(project, entry, mainClass, config, tool, "", cold ? null : daemon.ClientArguments);
                    if (!cold)
                    {
                        var warmStarted = DateTime.UtcNow;
                        var wasWarm = daemon.IsWarmFor((string?)arguments["env"]?["JAVA_HOME"]);
                        var gradle = new List<string>();
                        var warm = await daemon.WarmUpAsync(project, KotlinJvmLaunch.EnvironmentOf(arguments), line => { lock (gradle) gradle.Add(line); }, CancellationToken.None);
                        Console.WriteLine("    INFO daemon " + (wasWarm ? "already warm" : "warmed up in " + (int)(DateTime.UtcNow - warmStarted).TotalSeconds + " s") + " (server environment: " +
                            (tool?["env"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "none") + ")");
                        if (warm != 0) throw new InvalidOperationException("Gradle daemon warm-up failed (exit " + warm + "): " + string.Join(" | ", gradle.Skip(Math.Max(0, gradle.Count - 8))));
                    }
                    var port = (int)(await lsp.CallAsync("workspace/executeCommand", new JObject { ["command"] = "start_debug_server", ["arguments"] = new JArray(rootUri) }))!;
                    var client = new TcpClient { NoDelay = true };
                    await client.ConnectAsync(IPAddress.Loopback, port);
                    // Frames go through KotlinDapSession, as in Visual Studio.
                    var session = new KotlinDapSession(client.GetStream());
                    var dap = new Peer(session.ReadAsync, message => session.SendAsync(message, CancellationToken.None), lsp: false);
                    await dap.CallAsync("initialize", new JObject { ["clientID"] = "vistudio-hot-reload-test", ["adapterID"] = "intellij_gradle", ["linesStartAt1"] = true, ["columnsStartAt1"] = true, ["pathFormat"] = "path" });
                    var launch = await dap.RequestAsync("launch", arguments);
                    await dap.WaitAsync(m => (string?)m["event"] == "initialized", 120, "initialized event");
                    await dap.CallAsync("setBreakpoints", Breakpoints(breakpoints));
                    await dap.CallAsync("configurationDone", new JObject());
                    await dap.ResponseAsync(launch, 900, "launch");
                    return (dap, client);
                }
                async Task<(int Thread, string? File, int Line, JObject Frame)?> StoppedAsync(Peer dap, double seconds)
                {
                    var stopped = await dap.PollAsync(m => (string?)m["event"] == "stopped", seconds);
                    if (stopped == null) return null;
                    var thread = (int)stopped["body"]!["threadId"]!;
                    var frame = (JObject)(await dap.CallAsync("stackTrace", new JObject { ["threadId"] = thread }))!["stackFrames"]![0]!;
                    var path = (string?)frame["source"]?["path"];
                    return (thread, path == null ? null : Path.GetFileName(path), (int)frame["line"]!, frame);
                }
                async Task EndAsync(Peer dap, TcpClient client)
                {
                    try { await dap.CallAsync("disconnect", new JObject { ["terminateDebuggee"] = true }, 20); }
                    catch (Exception) { }
                    client.Dispose();
                }
                HotReloadSession NewSession(string name) => new HotReloadSession(new HotReloadTarget(project, "", entry, Path.Combine(build, "HotReload.gradle"),
                    Path.Combine(build, "VistudioHotReloadAgent.java"), Path.Combine(root, name)));
                async Task<int?> FindAsync(DateTime sessionStart) =>
                    HotReloadJvm.Find(await JvmTools.RunAsync(jps, "-l", CancellationToken.None), mainClass, sessionStart, HotReloadJvm.StartTimeUtc);

                async Task EndProgramsAsync()
                {
                    // A launch the debugger gave up on leaves its JVM waiting for a debugger, and the Gradle daemon busy with it.
                    foreach (var jvm in JvmTools.ParseJps(await JvmTools.RunAsync(jps, "-l", CancellationToken.None)).Where(j => JvmTools.IsMain(j.Main, mainClass)))
                        try { using (var leftover = System.Diagnostics.Process.GetProcessById(jvm.Pid)) leftover.Kill(); }
                        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is System.ComponentModel.Win32Exception) { }
                }

                var started = DateTime.UtcNow;
                Peer dap = null!;
                TcpClient client = null!;
                if (cold)
                {
                    // What F5 did before the session daemon: a launch without a daemon. Where Gradle needs more than the
                    // server's 30 s, the session ends without a stop and the JVM Gradle starts afterwards is left waiting
                    // for a debugger. Informational: on a fast machine this launch simply works.
                    (dap, client) = await LaunchAsync(new JObject(), new[] { breakLine }, cold: true);
                    var coldStop = await StoppedAsync(dap, 300);
                    Console.WriteLine("    INFO launch without a daemon: " + (coldStop != null ? "stopped at the breakpoint" : "no stop") + " after " + (int)(DateTime.UtcNow - started).TotalSeconds + " s");
                    foreach (var line in dap.Events()) Console.WriteLine("      DAP " + line);
                    if (coldStop == null)
                    {
                        // The client's view of the end, as Visual Studio has it: terminated, then the socket.
                        client.Dispose();
                        var leftover = (int?)null;
                        for (var i = 0; i < 90 && leftover == null; i++) { await Task.Delay(1000); leftover = await FindAsync(started); }
                        Console.WriteLine("    INFO JVM left behind by the launch without a daemon: " + (leftover == null ? "none within 90 s" : "process " + leftover));
                        if (leftover != null)
                        {
                            GradleSessionDaemon.EndTree(leftover.Value);
                            await Task.Delay(1000);
                            check(await FindAsync(started) == null, "Debugger: the JVM a failed attach left waiting is found by its main class and ended");
                        }
                    }
                    else await EndAsync(dap, client);
                    await EndProgramsAsync();
                    await Task.Delay(3000);
                }

                // The Gradle build F5 runs before the launch (Open Folder, a project that is not up to date) goes to the
                // session's daemon and starts it; the launch below then needs no warm-up of its own.
                {
                    var gradle = new List<string>();
                    var buildStarted = DateTime.UtcNow;
                    var built = await daemon.RunAsync(GradleCommand.Create(project, "", new[] { "build" }, false, daemon.Registry), line => { lock (gradle) gradle.Add(line); }, line => { lock (gradle) gradle.Add(line); }, CancellationToken.None);
                    check(built == 0, "Debugger: the build before the launch runs on the session's daemon (exit " + built + ", " + (int)(DateTime.UtcNow - buildStarted).TotalSeconds + " s)" +
                        (built == 0 ? "" : ": " + string.Join(" | ", gradle.Skip(Math.Max(0, gradle.Count - 8)))));
                    check(daemon.RunningDaemons().Count == 1, "Debugger: the build left one daemon running in the session's registry (" + daemon.RunningDaemons().Count + ")");
                }

                // 1. Debug session (F5): stop at the breakpoint once, then let the program run. No waiting after the
                // import: F5 in Visual Studio launches while the server still indexes, too.
                (int Thread, string? File, int Line, JObject Frame)? first = null;
                for (var attempt = 1; attempt <= 3 && first == null; attempt++)
                {
                    await EndProgramsAsync();
                    started = DateTime.UtcNow;
                    (dap, client) = await LaunchAsync(new JObject(), new[] { breakLine });
                    var requested = DateTime.UtcNow;
                    first = await StoppedAsync(dap, 300);
                    Console.WriteLine("    INFO launch attempt " + attempt + ": " + (first != null ? "stopped at the breakpoint " : "the debugger did not attach, ") + (int)(DateTime.UtcNow - requested).TotalSeconds + " s after the launch request");
                    foreach (var line in dap.Events()) Console.WriteLine("      DAP " + line);
                    check(attempt > 1 || first != null, "Debugger: the first launch on the session's warm daemon reaches the breakpoint");
                    if (first != null) break;
                    if (attempt == 3) dap.Dump("DAP");
                    await EndAsync(dap, client);
                    await Task.Delay(5000);
                }
                check(daemon.RunningDaemons().Count == 1, "Debugger: the launch runs on the one daemon of the session's registry (" + daemon.RunningDaemons().Count + " running in \"" + daemon.Registry + "\")");
                using (var session = NewSession("debug work 1"))
                {
                    check(first != null && first.Value.File == "HotText.kt" && first.Value.Line == breakLine, "Debugger: the session stops at HotText.kt:" + breakLine + ": " + first);
                    if (first == null) return true;
                    var pid = await FindAsync(started);
                    check(pid != null, "Debugger: jps finds the JVM the Kotlin server started by its main class (pid " + pid + ")");
                    if (pid == null) { await EndAsync(dap, client); return true; }
                    await dap.CallAsync("setBreakpoints", Breakpoints());
                    await dap.CallAsync("continue", new JObject { ["threadId"] = first.Value.Thread });
                    check(await dap.PollAsync(Output("TICK kotlin-1"), 30) != null, "Debugger: the program prints the first text");

                    // 2. A body change while the program runs under the debugger.
                    var log = new List<string>();
                    Text("kotlin-2");
                    var result = await session.ApplyAsync(await session.CompileAsync(_ => { }, CancellationToken.None), pid.Value, log.Add, CancellationToken.None);
                    check(result.Error == null && result.Reloaded.SequenceEqual(new[] { "sample.HotText" }), "Debugger: a running debuggee reloads sample.HotText: " + result.Summary);
                    check(await dap.PollAsync(Output("TICK kotlin-2"), 30) != null, "Debugger: the running debuggee prints the new text");
                    Console.WriteLine("    INFO JVM prints the dynamic agent warning without vmArgs: " + (await dap.PollAsync(Output("A Java agent has been loaded dynamically"), 3) != null));

                    // 3. A breakpoint set before a reload of its class.
                    await dap.CallAsync("setBreakpoints", Breakpoints(breakLine));
                    var hit = await StoppedAsync(dap, 30);
                    check(hit != null && hit.Value.Line == breakLine, "Debugger: the breakpoint stops again before the next reload");
                    if (hit == null) { await EndAsync(dap, client); return true; }

                    // 4. Reload asked for while the program is paused at the breakpoint: applied when it continues.
                    Text("kotlin-3");
                    var compilation = await session.CompileAsync(_ => { }, CancellationToken.None);
                    check(compilation.Error == null, "Debugger: the changed class compiles while the program is paused: " + compilation.Error);
                    var apply = session.ApplyAsync(compilation, pid.Value, log.Add, CancellationToken.None);
                    await Task.WhenAny(apply, Task.Delay(8000));
                    check(!apply.IsCompleted, "Debugger: the reload waits while the program is paused at a breakpoint");
                    check(log.Any(l => l.Contains("paused in the debugger")), "Debugger: the wait is announced: " + log.FirstOrDefault(l => l.Contains("paused in the debugger")));
                    dap.Forget(m => (string?)m["event"] == "stopped");
                    await dap.CallAsync("continue", new JObject { ["threadId"] = hit.Value.Thread });
                    await Task.WhenAny(apply, Task.Delay(60000));
                    check(apply.IsCompleted && apply.Result.Error == null && apply.Result.Reloaded.SequenceEqual(new[] { "sample.HotText" }),
                        "Debugger: the reload is applied when the program continues: " + (apply.IsCompleted ? apply.Result.Summary : "still waiting"));

                    // After the reload: the JVM drops the breakpoints of a redefined class. Does the debugger stop there again?
                    var again = await StoppedAsync(dap, 12);
                    Console.WriteLine("    INFO breakpoint in a reloaded class stops without being set again: " + (again != null));
                    if (again == null)
                    {
                        await dap.CallAsync("setBreakpoints", Breakpoints(breakLine));
                        again = await StoppedAsync(dap, 12);
                        Console.WriteLine("    INFO breakpoint stops after the same setBreakpoints is sent again: " + (again != null));
                    }
                    if (again == null)
                    {
                        await dap.CallAsync("setBreakpoints", Breakpoints());
                        await dap.CallAsync("setBreakpoints", Breakpoints(breakLine));
                        again = await StoppedAsync(dap, 12);
                        Console.WriteLine("    INFO breakpoint stops after it is removed and set again: " + (again != null));
                    }
                    check(again != null && again.Value.Line == breakLine, "Debugger: a breakpoint in a reloaded class can stop again");
                    if (again != null)
                    {
                        var scopes = (JArray)(await dap.CallAsync("scopes", new JObject { ["frameId"] = again.Value.Frame["id"]!.DeepClone() }))!["scopes"]!;
                        var variables = (JArray)(await dap.CallAsync("variables", new JObject { ["variablesReference"] = scopes[0]["variablesReference"]!.DeepClone() }))!["variables"]!;
                        var text = variables.FirstOrDefault(v => (string?)v["name"] == "text");
                        check(((string?)text?["value"] ?? "").Contains("kotlin-3"), "Debugger: the frame stopped after the reload runs the new code: text = " + (string?)text?["value"]);
                        await dap.CallAsync("setBreakpoints", Breakpoints());
                        await dap.CallAsync("continue", new JObject { ["threadId"] = again.Value.Thread });
                    }
                    check(await dap.PollAsync(Output("TICK kotlin-3"), 30) != null, "Debugger: the program prints the text reloaded at the breakpoint");
                    await EndAsync(dap, client);
                }
                await Task.Delay(3000);

                // 5. Run without debugging (Ctrl+F5), with vmArgs: the flag reaches the JVM and hides its agent warning.
                Text("kotlin-4");
                await EndProgramsAsync();
                started = DateTime.UtcNow;
                (dap, client) = await LaunchAsync(new JObject { ["noDebug"] = true, ["vmArgs"] = new JArray("-XX:+EnableDynamicAgentLoading") }, new int[0]);
                using (var session = NewSession("debug work 2"))
                {
                    check(await dap.PollAsync(Output("TICK kotlin-4"), 900) != null, "Run without debugging: the program prints");
                    var pid = await FindAsync(started);
                    check(pid != null, "Run without debugging: jps finds the JVM by its main class");
                    if (pid != null)
                    {
                        Text("kotlin-5");
                        var result = await session.ReloadAsync(pid.Value, _ => { }, CancellationToken.None);
                        check(result.Error == null && result.Reloaded.SequenceEqual(new[] { "sample.HotText" }), "Run without debugging: reloads sample.HotText: " + result.Summary);
                        check(await dap.PollAsync(Output("TICK kotlin-5"), 30) != null, "Run without debugging: the program prints the new text");
                        Console.WriteLine("    INFO JVM prints the dynamic agent warning with vmArgs -XX:+EnableDynamicAgentLoading: " +
                            (await dap.PollAsync(Output("A Java agent has been loaded dynamically"), 3) != null));
                    }
                    // 6. The end of a session without a disconnect request (the debugger window closed, Visual Studio
                    // gone): the program still runs, and ending the session's daemon ends it.
                    client.Dispose();
                    var running = await FindAsync(started);
                    var daemons = daemon.RunningDaemons();
                    daemon.Dispose();
                    await Task.Delay(2000);
                    var alive = daemons.Where(id => HotReloadJvm.StartTimeUtc(id) != null).ToArray();
                    check(daemons.Count >= 1 && alive.Length == 0, "End of the session: its Gradle daemon is ended (" + daemons.Count + " ran, " + alive.Length + " left)");
                    check(await FindAsync(started) == null, "End of the session: no JVM of the program is left (process " + running + " ran until then)");
                    check(!Directory.Exists(daemon.Registry), "End of the session: the daemon registry folder is removed");
                }
            }
            catch (Exception)
            {
                lock (serverErrors) foreach (var line in serverErrors) Console.WriteLine("    server stderr: " + (line.Length > 300 ? line.Substring(0, 300) : line));
                throw;
            }
            finally
            {
                await process.StopAsync();
                daemon.Dispose();
            }
        }
        return true;
    }
}
