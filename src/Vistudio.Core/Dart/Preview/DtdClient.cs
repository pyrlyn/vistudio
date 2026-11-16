using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Vistudio.Core.Dart.Preview
{
    /// <summary>A JSON-RPC error returned by the Dart Tooling Daemon or a service registered on it.</summary>
    public sealed class DtdException : Exception
    {
        public DtdException(int code, string message) : base(message + " (DTD error " + code + ")") { Code = code; }
        public int Code { get; }
    }

    /// <summary>
    /// A client of the Dart Tooling Daemon (<c>dart tooling-daemon</c>): JSON-RPC 2.0 over a WebSocket. Vistudio uses it to
    /// provide the <c>Editor</c> service the widget previewer looks for (active file, theme, navigation to code) and to
    /// call the previewer's own service (hot restart). Thread-safe; sends are serialized.
    /// </summary>
    public sealed class DtdClient : IDisposable
    {
        private readonly ClientWebSocket socket = new ClientWebSocket();
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JToken>> pending = new ConcurrentDictionary<int, TaskCompletionSource<JToken>>();
        private readonly ConcurrentDictionary<string, Func<JObject, Task<JObject>>> handlers = new ConcurrentDictionary<string, Func<JObject, Task<JObject>>>(StringComparer.Ordinal);
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        private int nextId;
        private Task? receiver;

        /// <summary>An event on a stream this client listens to: stream id, event kind, data.</summary>
        public event Action<string, string, JObject>? EventReceived;
        /// <summary>The connection ended (null: closed by this client).</summary>
        public event Action<Exception?>? Closed;

        public bool IsOpen => socket.State == WebSocketState.Open;

        public static async Task<DtdClient> ConnectAsync(Uri uri, CancellationToken token)
        {
            var client = new DtdClient();
            try
            {
                await client.socket.ConnectAsync(uri, token).ConfigureAwait(false);
                client.receiver = Task.Run(client.ReceiveLoopAsync);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        /// <summary>Calls <paramref name="method"/> (<c>getRegisteredServices</c>, <c>streamListen</c>, <c>Service.method</c>) and returns its result.</summary>
        public async Task<JToken> CallAsync(string method, JObject? parameters, CancellationToken token)
        {
            var id = Interlocked.Increment(ref nextId);
            var completion = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = completion;
            try
            {
                var message = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
                if (parameters != null) message["params"] = parameters;
                await SendAsync(message, token).ConfigureAwait(false);
                using (token.Register(() => completion.TrySetCanceled()))
                    return await completion.Task.ConfigureAwait(false);
            }
            finally { pending.TryRemove(id, out _); }
        }

        /// <summary>Registers <c>service.method</c> on the daemon; calls from other clients run <paramref name="handler"/>.</summary>
        public async Task RegisterServiceAsync(string service, string method, Func<JObject, Task<JObject>> handler, JObject? capabilities, CancellationToken token)
        {
            handlers[service + "." + method] = handler;
            var parameters = new JObject { ["service"] = service, ["method"] = method };
            if (capabilities != null) parameters["capabilities"] = capabilities;
            await CallAsync("registerService", parameters, token).ConfigureAwait(false);
        }

        public Task StreamListenAsync(string stream, CancellationToken token) =>
            CallAsync("streamListen", new JObject { ["streamId"] = stream }, token);

        public Task PostEventAsync(string stream, string kind, JObject data, CancellationToken token) =>
            CallAsync("postEvent", new JObject { ["streamId"] = stream, ["eventKind"] = kind, ["eventData"] = data }, token);

        /// <summary>The <c>clientServices</c> of <c>getRegisteredServices</c>: service name and its method names.</summary>
        public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetRegisteredServicesAsync(CancellationToken token)
        {
            var result = await CallAsync("getRegisteredServices", null, token).ConfigureAwait(false);
            var services = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            if (result["clientServices"] is JArray clients)
                foreach (var service in clients.OfType<JObject>())
                {
                    var name = (string?)service["name"];
                    if (name == null) continue;
                    var methods = new List<string>();
                    if (service["methods"] is JObject map) foreach (var property in map.Properties()) methods.Add(property.Name);
                    else if (service["methods"] is JArray list) foreach (var item in list) if (item is JObject m && (string?)m["name"] is string n) methods.Add(n); else if (item.Type == JTokenType.String) methods.Add((string)item!);
                    services[name] = methods;
                }
            return services;
        }

        private async Task SendAsync(JObject message, CancellationToken token)
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
            await sendGate.WaitAsync(token).ConfigureAwait(false);
            try { await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false); }
            finally { sendGate.Release(); }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[16384];
            Exception? failure = null;
            try
            {
                using (var message = new MemoryStream())
                    while (socket.State == WebSocketState.Open)
                    {
                        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), closing.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        message.Write(buffer, 0, result.Count);
                        if (!result.EndOfMessage) continue;
                        var text = Encoding.UTF8.GetString(message.ToArray());
                        message.SetLength(0);
                        Dispatch(text);
                    }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is WebSocketException || e is IOException || e is ObjectDisposedException) { failure = e; }
            foreach (var request in pending.Values) request.TrySetException(failure ?? new IOException("The Dart Tooling Daemon connection closed."));
            Closed?.Invoke(closing.IsCancellationRequested ? null : failure ?? new IOException("The Dart Tooling Daemon closed the connection."));
        }

        private void Dispatch(string text)
        {
            JObject message;
            try { message = JObject.Parse(text); }
            catch (JsonException) { return; }
            var method = (string?)message["method"];
            if (method == null)
            {
                if (message["id"]?.Type != JTokenType.Integer || !pending.TryGetValue((int)message["id"]!, out var completion)) return;
                if (message["error"] is JObject error) completion.TrySetException(new DtdException((int?)error["code"] ?? -1, (string?)error["message"] ?? "error"));
                else completion.TrySetResult(message["result"] ?? JValue.CreateNull());
                return;
            }
            var parameters = message["params"] as JObject ?? new JObject();
            if (method == "streamNotify")
            {
                EventReceived?.Invoke((string?)parameters["streamId"] ?? "", (string?)parameters["eventKind"] ?? "", parameters["eventData"] as JObject ?? new JObject());
                return;
            }
            var id = message["id"];
            if (id == null) return;
            _ = Task.Run(async () =>
            {
                JObject response;
                if (handlers.TryGetValue(method, out var handler))
                {
                    try { response = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = await handler(parameters).ConfigureAwait(false) }; }
                    catch (Exception e) { response = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JObject { ["code"] = -32000, ["message"] = e.Message } }; }
                }
                else response = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JObject { ["code"] = -32601, ["message"] = "Method not found: " + method } };
                try { await SendAsync(response, closing.Token).ConfigureAwait(false); }
                catch (Exception e) when (e is WebSocketException || e is OperationCanceledException || e is ObjectDisposedException || e is InvalidOperationException) { }
            });
        }

        public void Dispose()
        {
            if (closing.IsCancellationRequested) return;
            closing.Cancel();
            try
            {
                if (socket.State == WebSocketState.Open)
                    socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(1000);
            }
            catch (Exception e) when (e is AggregateException || e is WebSocketException || e is ObjectDisposedException) { }
            socket.Dispose();
        }
    }

    /// <summary>
    /// A minimal LSP client over a server's standard streams (Content-Length framing). Used for the analysis server that
    /// serves the widget previewer through DTD; it answers server-to-client requests with empty results.
    /// </summary>
    public sealed class LspStdioClient : IDisposable
    {
        private readonly Stream input;
        private readonly Stream output;
        private readonly Action<string>? log;
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JToken?>> pending = new ConcurrentDictionary<int, TaskCompletionSource<JToken?>>();
        private int nextId;
        private Task? reader;

        /// <param name="input">The server's standard input.</param>
        /// <param name="output">The server's standard output.</param>
        public LspStdioClient(Stream input, Stream output, Action<string>? log)
        {
            this.input = input; this.output = output; this.log = log;
        }

        public void Start() => reader = Task.Run(ReadLoopAsync);

        public async Task<JToken?> RequestAsync(string method, JToken parameters, CancellationToken token)
        {
            var id = Interlocked.Increment(ref nextId);
            var completion = new TaskCompletionSource<JToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = completion;
            try
            {
                await WriteAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, token).ConfigureAwait(false);
                using (token.Register(() => completion.TrySetCanceled()))
                    return await completion.Task.ConfigureAwait(false);
            }
            finally { pending.TryRemove(id, out _); }
        }

        public Task NotifyAsync(string method, JToken parameters, CancellationToken token) =>
            WriteAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }, token);

        /// <summary>One LSP frame for <paramref name="message"/>.</summary>
        public static byte[] Frame(JObject message)
        {
            var body = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
            var header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
            var frame = new byte[header.Length + body.Length];
            Buffer.BlockCopy(header, 0, frame, 0, header.Length);
            Buffer.BlockCopy(body, 0, frame, header.Length, body.Length);
            return frame;
        }

        private async Task WriteAsync(JObject message, CancellationToken token)
        {
            var frame = Frame(message);
            await sendGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await input.WriteAsync(frame, 0, frame.Length, token).ConfigureAwait(false);
                await input.FlushAsync(token).ConfigureAwait(false);
            }
            finally { sendGate.Release(); }
        }

        /// <summary>Reads frames until the stream ends; usable on any stream (tests feed it a MemoryStream).</summary>
        public static async Task ReadFramesAsync(Stream stream, Action<JObject> onMessage)
        {
            var buffer = new List<byte>();
            var chunk = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, 0, chunk.Length).ConfigureAwait(false);
                if (read <= 0) return;
                for (var i = 0; i < read; i++) buffer.Add(chunk[i]);
                while (true)
                {
                    var headerEnd = IndexOfHeaderEnd(buffer);
                    if (headerEnd < 0) break;
                    var header = Encoding.ASCII.GetString(buffer.GetRange(0, headerEnd).ToArray());
                    var length = -1;
                    foreach (var line in header.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line.Substring(15).Trim(), out length);
                    if (length < 0) { buffer.RemoveRange(0, headerEnd + 4); continue; }
                    if (buffer.Count < headerEnd + 4 + length) break;
                    var body = Encoding.UTF8.GetString(buffer.GetRange(headerEnd + 4, length).ToArray());
                    buffer.RemoveRange(0, headerEnd + 4 + length);
                    try { onMessage(JObject.Parse(body)); }
                    catch (JsonException) { }
                }
            }
        }

        private static int IndexOfHeaderEnd(List<byte> buffer)
        {
            for (var i = 0; i + 3 < buffer.Count; i++)
                if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10) return i;
            return -1;
        }

        private async Task ReadLoopAsync()
        {
            try { await ReadFramesAsync(output, OnMessage).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is InvalidOperationException) { }
            foreach (var request in pending.Values) request.TrySetException(new IOException("The analysis server exited."));
        }

        private void OnMessage(JObject message)
        {
            var method = (string?)message["method"];
            var id = message["id"];
            if (method == null)
            {
                if (id?.Type == JTokenType.Integer && pending.TryGetValue((int)id, out var completion))
                {
                    if (message["error"] is JObject error) completion.TrySetException(new InvalidOperationException((string?)error["message"] ?? "LSP error"));
                    else completion.TrySetResult(message["result"]);
                }
                return;
            }
            if (method == "window/logMessage" || method == "window/showMessage") log?.Invoke("Analysis server: " + (string?)message["params"]?["message"]);
            if (id == null) return;
            // Server-to-client requests (progress tokens, capability registration, configuration): accept with empty results.
            JToken result = JValue.CreateNull();
            if (method == "workspace/configuration" && message["params"]?["items"] is JArray items)
            {
                var answers = new JArray();
                foreach (var _ in items) answers.Add(JValue.CreateNull());
                result = answers;
            }
            _ = WriteAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }, CancellationToken.None)
                .ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
        }

        public void Dispose()
        {
            foreach (var request in pending.Values) request.TrySetCanceled();
        }
    }
}
