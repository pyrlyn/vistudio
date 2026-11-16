using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Vistudio
{
    // Compatibility for the pinned Kotlin backend. Only variables requests are retried:
    // evaluating an expression again could execute user code twice.
    internal sealed class KotlinDapSession
    {
        private readonly Stream backend;
        private readonly KotlinDebugMonitor? monitor;
        private readonly SemaphoreSlim writeGate = new SemaphoreSlim(1, 1);
        private readonly object sync = new object();
        private readonly Dictionary<int, Pending> requests = new Dictionary<int, Pending>();
        private readonly Dictionary<int, int> reverseRequests = new Dictionary<int, int>();
        private readonly TimeSpan valueTimeout;
        private readonly TimeSpan exitTimeout;
        private readonly TaskCompletionSource<bool> disconnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int toBackend, toHost, generation;
        private long evaluationId;
        private bool exited;
        private JObject? terminated;
        private Task? exitDeadline;
        private Task<JObject?>? reading;

        internal KotlinDapSession(Stream backend, TimeSpan? valueTimeout = null, TimeSpan? exitTimeout = null, KotlinDebugMonitor? monitor = null)
        {
            this.backend = backend;
            this.monitor = monitor;
            this.valueTimeout = valueTimeout ?? TimeSpan.FromSeconds(2);
            this.exitTimeout = exitTimeout ?? TimeSpan.FromSeconds(5);
        }

        internal async Task SendAsync(JObject message, CancellationToken token)
        {
            Pending? pending = null;
            monitor?.Sent(message);
            if ((string?)message["type"] == "request")
            {
                var command = (string?)message["command"];
                lock (sync)
                {
                    if (command == "evaluate") KotlinEvaluation.Prepare(message, checked(++evaluationId));
                    if (command == "continue" || command == "next" || command == "stepIn" || command == "stepOut" ||
                        command == "restartFrame" || command == "disconnect" || command == "terminate") generation++;
                    if (command == "disconnect" || command == "terminate") disconnect.TrySetResult(true);
                    if (command == "cancel" && message["arguments"]?["requestId"] is JToken id)
                    {
                        foreach (var item in requests)
                            if (item.Value.HostId == (int)id)
                            { item.Value.Canceled = true; message["arguments"]!["requestId"] = item.Key; break; }
                    }
                    pending = new Pending((int)message["seq"]!, (JObject)message.DeepClone(), generation);
                }
            }
            else if ((string?)message["type"] == "response")
            {
                lock (sync)
                {
                    var id = (int)message["request_seq"]!;
                    if (reverseRequests.TryGetValue(id, out var original))
                    { message["request_seq"] = original; reverseRequests.Remove(id); }
                }
            }
            await WriteAsync(message, pending, token).ConfigureAwait(false);
        }

        private async Task<bool> WriteAsync(JObject message, Pending? pending, CancellationToken token, bool retry = false)
        {
            await writeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                lock (sync)
                {
                    if (retry && (pending!.Generation != generation || pending.Canceled || disconnect.Task.IsCompleted)) return false;
                    message["seq"] = checked(++toBackend);
                    if (pending != null)
                    {
                        requests.Remove(pending.WireId);
                        pending.WireId = toBackend;
                        requests.Add(toBackend, pending);
                    }
                }
                var frame = KotlinDapSourceStream.Encode(message);
                await backend.WriteAsync(frame, 0, frame.Length, token).ConfigureAwait(false);
                await backend.FlushAsync(token).ConfigureAwait(false);
                return true;
            }
            finally { writeGate.Release(); }
        }

        internal async Task<JObject?> ReadAsync(CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (terminated != null && (exited || disconnect.Task.IsCompleted || exitDeadline!.IsCompleted))
                    return EmitTermination();

                // Keep exactly one read alive across the termination timeout. Never cancel a
                // half-read frame and start a second reader on the same socket.
                if (reading == null) reading = KotlinDapSourceStream.ReadMessageAsync(backend, token);
                if (terminated != null)
                {
#pragma warning disable VSTHRD003 // Socket reads and timer/TCS completions never need the VS main thread.
                    await Task.WhenAny(reading, exitDeadline!, disconnect.Task).ConfigureAwait(false);
#pragma warning restore VSTHRD003
                    if (!reading.IsCompleted) return EmitTermination();
                }
                var message = await reading.ConfigureAwait(false);
                reading = null;
                if (message == null) return terminated == null ? null : EmitTermination();
                monitor?.Received(message);
                var kind = (string?)message["type"];
                if (kind == "event")
                {
                    var name = (string?)message["event"];
                    if (name == "continued" || name == "stopped") lock (sync) generation++;
                    if (name == "exited") exited = true;
                    if (name == "terminated" && !exited && !disconnect.Task.IsCompleted)
                    {
                        if (terminated == null) { terminated = message; exitDeadline = Task.Delay(exitTimeout, token); }
                        continue;
                    }
                }
                else if (kind == "response")
                {
                    Pending? pending;
                    lock (sync)
                    {
                        var id = (int?)message["request_seq"] ?? 0;
                        requests.TryGetValue(id, out pending);
                    }
                    if (pending != null)
                    {
                        var placeholders = PendingValues(message).ToArray();
                        if (placeholders.Length != 0)
                        {
                            bool current;
                            lock (sync) current = pending.Generation == generation && !pending.Canceled;
                            if (current && !disconnect.Task.IsCompleted && pending.Elapsed.Elapsed < valueTimeout)
                            {
                                await Task.Delay(50, token).ConfigureAwait(false);
                                lock (sync) current = pending.Generation == generation && !pending.Canceled;
                                if (current && !disconnect.Task.IsCompleted)
                                {
                                    if (await WriteAsync((JObject)pending.Request.DeepClone(), pending, token, retry: true).ConfigureAwait(false)) continue;
                                    current = false;
                                }
                            }
                            // An unresponsive renderer must not leave a permanent spinner or
                            // prevent Stop/Continue. Preserve expansion handles and report absence.
                            foreach (var value in placeholders)
                                value["value"] = current ? "<object preview unavailable>" : "<execution resumed>";
                        }
                        message["request_seq"] = pending.HostId;
                        lock (sync) requests.Remove(pending.WireId);
                    }
                }
                return Emit(message);
            }
        }

        private JObject EmitTermination()
        {
            var message = terminated!;
            terminated = null;
            return Emit(message);
        }

        private JObject Emit(JObject message)
        {
            lock (sync)
            {
                var original = (int?)message["seq"] ?? 0;
                message["seq"] = checked(++toHost);
                if ((string?)message["type"] == "request") reverseRequests.Add(toHost, original);
            }
            return message;
        }

        private static IEnumerable<JObject> PendingValues(JObject response)
        {
            if ((string?)response["command"] != "variables" || (bool?)response["success"] != true) yield break;
            if (!(response["body"]?["variables"] is JArray variables)) yield break;
            foreach (var value in variables.OfType<JObject>())
                // Real string values are quoted and have no children; do not change literals.
                if ((int?)value["variablesReference"] > 0 && (string?)value["value"] == "Collecting data…") yield return value;
        }

        private sealed class Pending
        {
            internal readonly int HostId, Generation;
            internal readonly JObject Request;
            internal bool Canceled;
            internal int WireId;
            internal readonly Stopwatch Elapsed = Stopwatch.StartNew();
            internal Pending(int hostId, JObject request, int generation)
            { HostId = hostId; Request = request; Generation = generation; }
        }
    }
}
