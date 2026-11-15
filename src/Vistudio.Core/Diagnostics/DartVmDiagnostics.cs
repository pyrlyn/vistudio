using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Vistudio.Core.Diagnostics
{
    /// <summary>
    /// Replies of the Dart VM service protocol (https://github.com/dart-lang/sdk/blob/main/runtime/vm/service/service.md).
    /// The service answers while isolates are paused at a breakpoint, because it runs on its own isolate.
    /// </summary>
    public static class DartVmService
    {
        public const string Runtime = "Dart heap";

        /// <summary>
        /// The WebSocket address of a VM service announced as <c>ws://host:port/token=/ws</c> or <c>http://host:port/token=/</c>
        /// (the debug adapter's <c>dart.debuggerUris</c> event, the VM's own "listening on" line).
        /// </summary>
        public static Uri WebSocketUri(string announced)
        {
            if (!Uri.TryCreate(announced.Trim(), UriKind.Absolute, out var uri)) throw new ArgumentException("Not a VM service address: " + announced);
            var scheme = uri.Scheme == "https" || uri.Scheme == "wss" ? "wss" : uri.Scheme == "http" || uri.Scheme == "ws" ? "ws" : null;
            if (scheme == null) throw new ArgumentException("Not a VM service address: " + announced);
            var path = uri.AbsolutePath;
            if (!path.EndsWith("/ws", StringComparison.Ordinal)) path = path.TrimEnd('/') + "/ws";
            return new UriBuilder(scheme, uri.Host, uri.Port, path).Uri;
        }

        /// <summary>The VM's process id and its non-system isolates, from a <c>getVM</c> result.</summary>
        public static (int? Pid, IReadOnlyList<string> Isolates) ParseVm(JObject result)
        {
            var pid = result["pid"]?.Type == JTokenType.Integer ? (int?)result["pid"] : null;
            var isolates = (result["isolates"] as JArray ?? new JArray()).OfType<JObject>()
                .Where(i => (bool?)i["isSystemIsolate"] != true).Select(i => (string?)i["id"]).Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).ToList();
            return (pid, isolates);
        }

        /// <summary>Heap of several isolates added up (<c>getMemoryUsage</c> results).</summary>
        public static RuntimeHeap ParseMemoryUsage(IEnumerable<JObject> results)
        {
            long used = 0, capacity = 0, external = 0;
            foreach (var r in results)
            {
                used += (long?)r["heapUsage"] ?? 0;
                capacity += (long?)r["heapCapacity"] ?? 0;
                external += (long?)r["externalUsage"] ?? 0;
            }
            return new RuntimeHeap(Runtime, used, capacity, external: external);
        }

        /// <summary>Live objects by class from <c>getAllocationProfile</c> results (classes with no live instance left out).</summary>
        public static HeapSnapshot ParseAllocationProfile(IEnumerable<JObject> results, DateTime time)
        {
            var classes = new List<HeapClassEntry>();
            foreach (var r in results)
                foreach (var member in (r["members"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    var instances = (long?)member["instancesCurrent"] ?? 0;
                    var bytes = (long?)member["bytesCurrent"] ?? 0;
                    if (instances == 0 && bytes == 0) continue;
                    var cls = member["class"] as JObject;
                    var library = (string?)cls?["library"]?["uri"];
                    var name = (string?)cls?["name"] ?? "?";
                    classes.Add(new HeapClassEntry(library == null || library.StartsWith("dart:", StringComparison.Ordinal) ? name : name + " (" + library + ")", instances, bytes));
                }
            return new HeapSnapshot(time, "Dart VM service getAllocationProfile", classes);
        }
    }

    /// <summary>JSON-RPC 2.0 over the VM service WebSocket, one request at a time (the probe polls once a second).</summary>
    public sealed class DartVmClient : IDisposable
    {
        private readonly Uri uri;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private ClientWebSocket? socket;
        private int nextId;

        public DartVmClient(string announcedUri) { uri = DartVmService.WebSocketUri(announcedUri); }

        public async Task<JObject> CallAsync(string method, JObject? parameters, CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (socket == null || socket.State != WebSocketState.Open)
                {
                    socket?.Dispose();
                    socket = new ClientWebSocket();
                    await socket.ConnectAsync(uri, token).ConfigureAwait(false);
                }
                var id = (++nextId).ToString(CultureInfo.InvariantCulture);
                var request = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JObject() };
                var bytes = Encoding.UTF8.GetBytes(request.ToString(Formatting.None));
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
                while (true)
                {
                    var message = await ReceiveAsync(socket, token).ConfigureAwait(false);
                    // Stream notifications can arrive between requests; only the reply with our id counts.
                    if ((string?)message["id"] != id) continue;
                    if (message["error"] is JObject error) throw new InvalidOperationException(method + ": " + (string?)error["message"] + " " + (string?)error["data"]?["details"]);
                    return message["result"] as JObject ?? throw new InvalidDataException(method + " returned no result.");
                }
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                socket?.Dispose();
                socket = null;
                throw;
            }
            finally { gate.Release(); }
        }

        private static async Task<JObject> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[64 * 1024];
            using (var message = new MemoryStream())
            {
                while (true)
                {
                    var part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (part.MessageType == WebSocketMessageType.Close) throw new IOException("The Dart VM service closed the connection.");
                    message.Write(buffer, 0, part.Count);
                    if (part.EndOfMessage) break;
                }
                return JObject.Parse(Encoding.UTF8.GetString(message.ToArray()));
            }
        }

        public void Dispose() { socket?.Dispose(); gate.Dispose(); }
    }

    /// <summary>Heap and class figures of a Dart program through the VM service its debug adapter announced.</summary>
    public sealed class DartVmProbe : IRuntimeProbe
    {
        private readonly DartVmClient client;

        public DartVmProbe(string announcedUri) { client = new DartVmClient(announcedUri); }

        public string Name => "Dart VM";

        /// <summary>The program's process id (the Flutter app or the <c>dart</c> VM running the program).</summary>
        public async Task<int> ProcessIdAsync(CancellationToken token) =>
            DartVmService.ParseVm(await client.CallAsync("getVM", null, token).ConfigureAwait(false)).Pid
                ?? throw new InvalidDataException("The Dart VM service did not report a process id.");

        private async Task<IReadOnlyList<string>> IsolatesAsync(CancellationToken token) =>
            DartVmService.ParseVm(await client.CallAsync("getVM", null, token).ConfigureAwait(false)).Isolates;

        public async Task<RuntimeHeap?> ReadAsync(CancellationToken token)
        {
            var results = new List<JObject>();
            foreach (var isolate in await IsolatesAsync(token).ConfigureAwait(false))
                results.Add(await client.CallAsync("getMemoryUsage", new JObject { ["isolateId"] = isolate }, token).ConfigureAwait(false));
            return results.Count == 0 ? null : DartVmService.ParseMemoryUsage(results);
        }

        public async Task<HeapSnapshot> SnapshotAsync(CancellationToken token)
        {
            var results = new List<JObject>();
            foreach (var isolate in await IsolatesAsync(token).ConfigureAwait(false))
                results.Add(await client.CallAsync("getAllocationProfile", new JObject { ["isolateId"] = isolate }, token).ConfigureAwait(false));
            return DartVmService.ParseAllocationProfile(results, DateTime.Now);
        }

        public void Dispose() => client.Dispose();
    }
}
