using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Vistudio;

internal static class DapSessionTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var process = new System.Diagnostics.ProcessStartInfo(@"C:\server\bin\intellij-server.exe", "--stdio");
        process.EnvironmentVariables["IJ_JAVA_OPTIONS"] = "-Xmx2g -Dexample=preserved";
        KotlinServerCompatibility.Configure(process);
        check(process.EnvironmentVariables["IJ_JAVA_OPTIONS"] == "-Xmx2g -Dexample=preserved -Ddap.evaluate.hover.timeout.ms=4000",
            "Native backend gets bounded cold-hover budget without losing inherited JVM options");
        KotlinServerCompatibility.Configure(process);
        check(process.EnvironmentVariables["IJ_JAVA_OPTIONS"]!.Split(new[] { "-Ddap.evaluate.hover.timeout.ms=" }, StringSplitOptions.None).Length == 2,
            "Repeated startup configuration does not duplicate the backend option");
        process.EnvironmentVariables["IJ_JAVA_OPTIONS"] = "-Ddap.evaluate.hover.timeout.ms=2500 -Xmx1g";
        KotlinServerCompatibility.Configure(process);
        check(process.EnvironmentVariables["IJ_JAVA_OPTIONS"] == "-Ddap.evaluate.hover.timeout.ms=2500 -Xmx1g", "Explicit backend timeout override is preserved");
        process.FileName = @"C:\other-server\java.exe";
        process.EnvironmentVariables["IJ_JAVA_OPTIONS"] = "-Xmx1g";
        KotlinServerCompatibility.Configure(process);
        check(process.EnvironmentVariables["IJ_JAVA_OPTIONS"] == "-Xmx1g", "Other language server launchers are not configured as the native Kotlin backend");

        using (var pair = await Pair.OpenAsync())
        {
            var session = new KotlinDapSession(pair.Client.GetStream());
            var request = Request(42, "evaluate", new JObject { ["context"] = "hover", ["noSideEffects"] = true,
                ["expression"] = "counter.slowNext()", ["frameId"] = 8, ["timeout"] = 5000 });
            var original = request["arguments"]!.DeepClone();
            await session.SendAsync(request, CancellationToken.None);
            var sent = await pair.ReceiveAsync();
            var reading = session.ReadAsync(CancellationToken.None);
            await Task.Delay(650); // Beyond the old backend hover deadline.
            check(!reading.IsCompleted && !pair.Server.GetStream().DataAvailable, "Slow hover is awaited without a second evaluation");
            check(JToken.DeepEquals(sent["arguments"], original), "Saved Watch retains hover context, restriction, expression and frame");
            await pair.SendAsync(Response((int)sent["seq"]!, "evaluate", new JObject { ["result"] = "1", ["variablesReference"] = 0 }));
            var result = (await reading)!;
            check((int?)result["request_seq"] == 42 && (string?)result["body"]?["result"] == "1", "First slow hover result reaches its original host request");
            check(!pair.Server.GetStream().DataAvailable, "Completing slow hover does not replay side effects");
        }

        using (var pair = await Pair.OpenAsync())
        {
            var session = new KotlinDapSession(pair.Client.GetStream());
            string? previous = null;
            foreach (var context in new[] { "watch", "repl", "repl", "variables", null, null })
            {
                const string expression = "counter.next() // user comment";
                var evaluation = Request(50, "evaluate", new JObject
                {
                    ["expression"] = expression, ["frameId"] = 7, ["context"] = context,
                    ["timeout"] = 5000, ["noSideEffects"] = false, ["noFuncEval"] = false,
                    ["format"] = new JObject { ["hex"] = false }
                });
                if (context == null) ((JObject)evaluation["arguments"]!).Remove("context");
                await session.SendAsync(evaluation, CancellationToken.None);
                var sent = await pair.ReceiveAsync();
                var args = sent["arguments"]!;
                var forwarded = (string)args["expression"]!;
                check((string?)args["context"] == "repl" && forwarded.StartsWith(expression + "\n/*", StringComparison.Ordinal), "Watch/Immediate request final presentation without rewriting the expression");
                check(forwarded != previous && (int?)args["frameId"] == 7, "Every explicit evaluation bypasses backend expression cache in the selected frame");
                check((int?)args["timeout"] == 5000 && (bool?)args["noSideEffects"] == false && (bool?)args["noFuncEval"] == false && (bool?)args["format"]?["hex"] == false, "SDK evaluation options are preserved");
                previous = forwarded;
                var response = Response((int)sent["seq"]!, "evaluate", new JObject { ["result"] = "Greeting(name=Watch)", ["type"] = "Greeting", ["variablesReference"] = 77 });
                await pair.SendAsync(response);
                var result = (await session.ReadAsync(CancellationToken.None))!;
                check((int?)result["request_seq"] == 50 && JToken.DeepEquals(result["body"], response["body"]), "Evaluation type, result and expandable handle reach the host unchanged");
                check(!pair.Server.GetStream().DataAvailable, "A completed evaluation causes no additional expression execution");
            }
            foreach (var flag in new[] { "noSideEffects", "noFuncEval" })
            {
                var restricted = Request(51, "evaluate", new JObject { ["expression"] = "greeting.name", ["context"] = "watch", [flag] = true });
                var before = restricted.DeepClone();
                KotlinEvaluation.Prepare(restricted, 100);
                check(JToken.DeepEquals(restricted, before), "Restricted evaluation is not promoted to repl");
            }
            var hover = Request(51, "evaluate", new JObject { ["expression"] = "greeting.name", ["frameId"] = 8, ["context"] = "hover", ["noSideEffects"] = true });
            var original = hover["arguments"]!.DeepClone();
            await session.SendAsync(hover, CancellationToken.None);
            var hoverSent = await pair.ReceiveAsync();
            check(JToken.DeepEquals(hoverSent["arguments"], original), "Hover and restricted evaluation are not promoted to interactive context");
            var failure = Response((int)hoverSent["seq"]!, "evaluate", new JObject { ["error"] = new JObject { ["id"] = 100, ["format"] = "Evaluation failed" } });
            failure["success"] = false;
            failure["message"] = "Evaluation failed";
            await pair.SendAsync(failure);
            var failed = (await session.ReadAsync(CancellationToken.None))!;
            check((int?)failed["request_seq"] == 51 && (bool?)failed["success"] == false && JToken.DeepEquals(failed["body"], failure["body"]), "Backend evaluation errors remain errors and retain request correlation");
        }

        using (var pair = await Pair.OpenAsync())
        {
            var session = new KotlinDapSession(pair.Client.GetStream());
            var input = new KotlinDapInputStream(session, () => { });
            var request = Request(70, "variables", new JObject { ["variablesReference"] = 7, ["start"] = 2, ["count"] = 4 });
            var bytes = KotlinDapSourceStream.Encode(request);
            for (var i = 0; i < bytes.Length; i += 3) await input.WriteAsync(bytes, i, Math.Min(3, bytes.Length - i));
            var sent = await pair.ReceiveAsync();
            var reading = session.ReadAsync(CancellationToken.None);
            await pair.SendAsync(Event("invalidated", new JObject { ["areas"] = new JArray("variables") }));
            check((string?)(await reading)!["event"] == "invalidated", "Invalidation before variables response does not consume the response");
            reading = session.ReadAsync(CancellationToken.None);
            await pair.SendAsync(Variables((int)sent["seq"]!, "Collecting data…"));
            var retry = await pair.ReceiveAsync();
            check((int)retry["seq"]! > (int)sent["seq"]! && JToken.DeepEquals(retry["arguments"], request["arguments"]), "Retry has unique ID and preserves paging arguments");
            // Another host request may interleave with the internal retry.
            await session.SendAsync(Request(71, "threads"), CancellationToken.None);
            var threads = await pair.ReceiveAsync();
            await pair.SendAsync(Response((int)threads["seq"]!, "threads", new JObject { ["threads"] = new JArray() }));
            check((int)(await reading)!["request_seq"]! == 71, "Interleaved host response retains original ID");
            reading = session.ReadAsync(CancellationToken.None);
            await pair.SendAsync(Variables((int)retry["seq"]!, "Greeting(name=Kotlin)"));
            var resolved = (await reading)!;
            check((int)resolved["request_seq"]! == 70 && (string?)resolved["body"]?["variables"]?[0]?["value"] == "Greeting(name=Kotlin)", "Object preview resolves before original host response");
            check((int?)resolved["body"]?["variables"]?[0]?["variablesReference"] == 8, "Resolved object remains expandable");

            await session.SendAsync(Request(72, "variables"), CancellationToken.None);
            sent = await pair.ReceiveAsync();
            reading = session.ReadAsync(CancellationToken.None);
            var literal = Variables((int)sent["seq"]!, "Collecting data…");
            literal["body"]!["variables"]![0]!["variablesReference"] = 0;
            await pair.SendAsync(literal);
            check((string?)(await reading)!["body"]?["variables"]?[0]?["value"] == "Collecting data…", "Literal matching the placeholder is preserved");

            await session.SendAsync(Request(73, "evaluate", new JObject { ["expression"] = "sideEffect()" }), CancellationToken.None);
            sent = await pair.ReceiveAsync();
            reading = session.ReadAsync(CancellationToken.None);
            await pair.SendAsync(Response((int)sent["seq"]!, "evaluate", new JObject { ["result"] = "Collecting data…", ["variablesReference"] = 8 }));
            check((int)(await reading)!["request_seq"]! == 73, "Expression evaluation is never retried");

            reading = session.ReadAsync(CancellationToken.None);
            await pair.SendAsync(Request(900, "runInTerminal"));
            var reverse = (await reading)!;
            await session.SendAsync(Response((int)reverse["seq"]!, "runInTerminal", new JObject()), CancellationToken.None);
            check((int)(await pair.ReceiveAsync())["request_seq"]! == 900, "Reverse requests map to original backend ID");
        }

        using (var pair = await Pair.OpenAsync())
        {
            var session = new KotlinDapSession(pair.Client.GetStream(), valueTimeout: TimeSpan.Zero);
            await session.SendAsync(Request(1, "variables"), CancellationToken.None);
            var sent = await pair.ReceiveAsync();
            await pair.SendAsync(Variables((int)sent["seq"]!, "Collecting data…"));
            var result = (await session.ReadAsync(CancellationToken.None))!;
            check((string?)result["body"]?["variables"]?[0]?["value"] == "<object preview unavailable>", "Expired renderer reports unavailable instead of indefinite placeholder");
        }

        using (var pair = await Pair.OpenAsync())
        {
            var session = new KotlinDapSession(pair.Client.GetStream());
            await session.SendAsync(Request(1, "variables"), CancellationToken.None);
            var sent = await pair.ReceiveAsync();
            await session.SendAsync(Request(2, "continue"), CancellationToken.None);
            await pair.ReceiveAsync();
            await pair.SendAsync(Variables((int)sent["seq"]!, "Collecting data…"));
            var result = (await session.ReadAsync(CancellationToken.None))!;
            check((string?)result["body"]?["variables"]?[0]?["value"] == "<execution resumed>", "Resume invalidates pending object handles without retry");
        }

        using (var pair = await Pair.OpenAsync())
        {
            var session = new KotlinDapSession(pair.Client.GetStream());
            await session.SendAsync(Request(80, "variables"), CancellationToken.None);
            var sent = await pair.ReceiveAsync();
            await session.SendAsync(Request(81, "cancel", new JObject { ["requestId"] = 80 }), CancellationToken.None);
            var cancel = await pair.ReceiveAsync();
            check((int?)cancel["arguments"]?["requestId"] == (int?)sent["seq"], "Cancellation maps to active backend request");
            await pair.SendAsync(Variables((int)sent["seq"]!, "Collecting data…"));
            var result = (await session.ReadAsync(CancellationToken.None))!;
            check((int?)result["request_seq"] == 80 && (string?)result["body"]?["variables"]?[0]?["value"] != "Collecting data…", "Canceled preview does not issue another request");
        }

        foreach (var exitCode in new[] { 0, 7, -1 })
        using (var pair = await Pair.OpenAsync())
        {
            var session = new KotlinDapSession(pair.Client.GetStream());
            var reading = session.ReadAsync(CancellationToken.None);
            await pair.SendAsync(Event("terminated"));
            await pair.SendAsync(Event("output", new JObject { ["output"] = "last output" }));
            check((string?)(await reading)!["event"] == "output", "Trailing output survives early termination");
            await pair.SendAsync(Event("exited", new JObject { ["exitCode"] = exitCode }));
            var exited = (await session.ReadAsync(CancellationToken.None))!;
            var terminated = (await session.ReadAsync(CancellationToken.None))!;
            check((string?)exited["event"] == "exited" && (int?)exited["body"]?["exitCode"] == exitCode, "Actual exit code precedes termination without conversion");
            check((string?)terminated["event"] == "terminated" && (int)terminated["seq"]! > (int)exited["seq"]!, "Reordered output sequence remains increasing");
        }

        using (var pair = await Pair.OpenAsync())
        {
            var session = new KotlinDapSession(pair.Client.GetStream(), exitTimeout: TimeSpan.FromMilliseconds(40));
            await pair.SendAsync(Event("terminated"));
            check((string?)(await session.ReadAsync(CancellationToken.None))!["event"] == "terminated", "Missing exit event has bounded wait and no fabricated success");
            await pair.SendAsync(Event("output", new JObject { ["output"] = "late" }));
            check((string?)(await session.ReadAsync(CancellationToken.None))!["event"] == "output", "Timeout retains single pending frame reader");
        }

        using (var pair = await Pair.OpenAsync())
        {
            var session = new KotlinDapSession(pair.Client.GetStream());
            await pair.SendAsync(Event("terminated"));
            var reading = session.ReadAsync(CancellationToken.None);
            await session.SendAsync(Request(3, "disconnect"), CancellationToken.None);
            await pair.ReceiveAsync();
            check((string?)(await reading)!["event"] == "terminated", "Stop bypasses exit wait");
        }

        using (var pair = await Pair.OpenAsync())
        {
            var session = new KotlinDapSession(pair.Client.GetStream());
            await pair.SendAsync(Event("terminated"));
            pair.Server.Client.Shutdown(SocketShutdown.Send);
            check((string?)(await session.ReadAsync(CancellationToken.None))!["event"] == "terminated", "EOF flushes pending termination");
            check(await session.ReadAsync(CancellationToken.None) == null, "EOF after pending termination completes stream");
        }

        // The launch monitor sees the session's traffic: what the Kotlin server sends when its debugger never attached
        // (the events recorded from the server: "initialized", Gradle's output, then "terminated" alone).
        using (var pair = await Pair.OpenAsync())
        {
            var monitor = new KotlinDebugMonitor();
            var session = new KotlinDapSession(pair.Client.GetStream(), exitTimeout: TimeSpan.FromMilliseconds(40), monitor: monitor);
            await session.SendAsync(Request(1, "launch"), CancellationToken.None);
            await pair.ReceiveAsync();
            await pair.SendAsync(Event("initialized"));
            await pair.SendAsync(Event("output", new JObject { ["output"] = "[Run] :" }));
            await session.ReadAsync(CancellationToken.None);
            await session.ReadAsync(CancellationToken.None);
            check(monitor.ProgramMayBeLeft && monitor.Describe("sample.MainKt", false) == null, "Monitored session: a launch that still runs has nothing to report");
            await pair.SendAsync(Event("terminated"));
            check((string?)(await session.ReadAsync(CancellationToken.None))!["event"] == "terminated", "Monitored session: termination still reaches the host");
            // The host answers "terminated" with a disconnect request, as Visual Studio does.
            await session.SendAsync(Request(2, "disconnect"), CancellationToken.None);
            var reason = monitor.Describe("sample.MainKt", false);
            check(reason != null && reason.Contains("sample.MainKt") && monitor.ProgramMayBeLeft, "Monitored session: terminated without an exit code or a stop is reported: " + reason);
        }
        using (var pair = await Pair.OpenAsync())
        {
            var monitor = new KotlinDebugMonitor();
            var session = new KotlinDapSession(pair.Client.GetStream(), monitor: monitor);
            await session.SendAsync(Request(1, "launch"), CancellationToken.None);
            await pair.ReceiveAsync();
            await pair.SendAsync(Event("stopped", new JObject { ["reason"] = "breakpoint", ["threadId"] = 6 }));
            await session.ReadAsync(CancellationToken.None);
            await pair.SendAsync(Event("exited", new JObject { ["exitCode"] = 0 }));
            await pair.SendAsync(Event("terminated"));
            await session.ReadAsync(CancellationToken.None);
            await session.ReadAsync(CancellationToken.None);
            check(monitor.Attached && !monitor.ProgramMayBeLeft && monitor.Describe("sample.MainKt", false) == null, "Monitored session: a program that stopped at a breakpoint and exited is not reported");
        }
    }

    private static JObject Request(int seq, string command, JObject? args = null) => new JObject { ["seq"] = seq, ["type"] = "request", ["command"] = command, ["arguments"] = args ?? new JObject() };
    private static JObject Response(int id, string command, JObject body) => new JObject { ["seq"] = 1, ["type"] = "response", ["request_seq"] = id, ["success"] = true, ["command"] = command, ["body"] = body };
    private static JObject Variables(int id, string value) => Response(id, "variables", new JObject { ["variables"] = new JArray(new JObject { ["name"] = "greeting", ["value"] = value, ["variablesReference"] = 8 }) });
    private static JObject Event(string name, JObject? body = null) => new JObject { ["seq"] = 1, ["type"] = "event", ["event"] = name, ["body"] = body ?? new JObject() };

    private sealed class Pair : IDisposable
    {
        internal readonly TcpClient Client, Server;
        private readonly CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        private Pair(TcpClient client, TcpClient server) { Client = client; Server = server; }
        internal static async Task<Pair> OpenAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                return new Pair(client, await listener.AcceptTcpClientAsync());
            }
            finally { listener.Stop(); }
        }
        internal async Task<JObject> ReceiveAsync() => (await KotlinDapSourceStream.ReadMessageAsync(Server.GetStream(), timeout.Token))!;
        internal async Task SendAsync(JObject message)
        {
            var bytes = KotlinDapSourceStream.Encode(message);
            await Server.GetStream().WriteAsync(bytes, 0, bytes.Length, timeout.Token);
        }
        public void Dispose() { Client.Dispose(); Server.Dispose(); timeout.Dispose(); }
    }
}
