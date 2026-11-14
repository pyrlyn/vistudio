using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vistudio.Core;

#pragma warning disable VSTHRD003 // The adapter process tasks have no UI-thread dependency.
namespace Vistudio.Dart
{
    /// <summary>
    /// A DAP connection to an SDK debug adapter process. The host's frames pass through unchanged (<see cref="HostInput"/>,
    /// <see cref="HostOutput"/>); Vistudio can add requests of its own (<c>hotReload</c>, <c>hotRestart</c>) between them,
    /// and their responses are consumed here, so the host's sequence bookkeeping is unaffected. No Visual Studio
    /// dependencies: linked into the regression tests, which drive real <c>dart</c>/<c>flutter debug_adapter</c> processes.
    /// </summary>
    internal sealed class DartDapConnection : IDisposable
    {
        internal const int OwnSequenceStart = 1_000_000;
        private readonly OwnedProcess process;
        private readonly SemaphoreSlim writeGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> own = new ConcurrentDictionary<int, TaskCompletionSource<JObject>>();
        private readonly DapOutputStream output = new DapOutputStream();
        private readonly Action<string>? errorLine;
        private int ownSequence = OwnSequenceStart;
        private int exited;

        internal DartDapConnection(ProcessStartInfo start, Action<string>? errorLine)
        {
            this.errorLine = errorLine;
            process = new OwnedProcess(start);
            HostInput = new DapInputStream(WriteFrameAsync);
            _ = Task.Run(ReadAsync);
            _ = Task.Run(async () =>
            {
                try { await OwnedProcess.PumpAsync(process.Error, line => this.errorLine?.Invoke(line)).ConfigureAwait(false); }
                catch (Exception) { }
            });
            _ = Task.Run(ObserveExitAsync);
        }

        /// <summary>Frames from the host (the debugger) to the adapter.</summary>
        internal Stream HostInput { get; }
        /// <summary>Frames from the adapter to the host, without the responses to Vistudio's own requests.</summary>
        internal Stream HostOutput => output;
        internal bool HasExited => Volatile.Read(ref exited) != 0;
        /// <summary>True after the Flutter adapter reported the app running (<c>flutter.appStarted</c>): hot reload can be sent.</summary>
        internal bool AppStarted { get; private set; }
        /// <summary>Every event the adapter sends (on the reader thread).</summary>
        internal event Action<JObject>? EventReceived;
        internal event Action? Exited;

        private async Task WriteFrameAsync(byte[] frame)
        {
            await writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await process.Input.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
                await process.Input.FlushAsync().ConfigureAwait(false);
            }
            finally { writeGate.Release(); }
        }

        private async Task ReadAsync()
        {
            try
            {
                while (true)
                {
                    var frame = await DapFraming.ReadFrameAsync(process.Output).ConfigureAwait(false);
                    if (frame == null) break;
                    if (Inspect(frame)) output.Enqueue(DapFraming.Frame(frame));
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception e) { errorLine?.Invoke("Invalid frame from the debug adapter: " + e.Message); }
            finally { output.Complete(); }
        }

        /// <summary>Returns false for responses to Vistudio's own requests; tracks the app state from adapter events.</summary>
        private bool Inspect(byte[] body)
        {
            JObject message;
            try { message = JObject.Parse(Encoding.UTF8.GetString(body)); }
            catch (JsonException) { return true; }
            var type = (string?)message["type"];
            if (type == "response" && message["request_seq"]?.Type == JTokenType.Integer && (int)message["request_seq"]! >= OwnSequenceStart)
            {
                if (own.TryRemove((int)message["request_seq"]!, out var waiter)) waiter.TrySetResult(message);
                return false;
            }
            if (type == "event")
            {
                var name = (string?)message["event"];
                if (name == "flutter.appStarted") AppStarted = true;
                else if (name == "terminated" || name == "exited") AppStarted = false;
                try { EventReceived?.Invoke(message); }
                catch (Exception) { }
            }
            return true;
        }

        /// <summary>Sends a request of Vistudio's own (for example <c>hotReload</c>) and waits for its response.</summary>
        internal async Task<JObject> RequestAsync(string command, JObject arguments, CancellationToken token)
        {
            if (HasExited) throw new InvalidOperationException("The debug session has ended.");
            var seq = Interlocked.Increment(ref ownSequence);
            var waiter = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            own[seq] = waiter;
            var body = new JObject { ["seq"] = seq, ["type"] = "request", ["command"] = command, ["arguments"] = arguments }.ToString(Formatting.None);
            await WriteFrameAsync(DapFraming.Frame(Encoding.UTF8.GetBytes(body))).ConfigureAwait(false);
            using (token.Register(() => { own.TryRemove(seq, out _); waiter.TrySetCanceled(); }))
                return await waiter.Task.ConfigureAwait(false);
        }

        private async Task ObserveExitAsync()
        {
            try { await process.Exit.ConfigureAwait(false); }
            catch (Exception) { }
            // The adapter can exit after "disconnect" while what it started keeps running: in Visual Studio, Stop
            // Debugging left flutter_counter.exe and the Dart development service alive (Flutter 3.50), which then
            // locked the next Windows build. Ending the adapter's process tree (its job) ends them too.
            try { await process.StopAsync().ConfigureAwait(false); }
            catch (Exception) { }
            MarkExited();
        }

        private void MarkExited()
        {
            if (Interlocked.Exchange(ref exited, 1) != 0) return;
            foreach (var waiter in own.Values) waiter.TrySetException(new InvalidOperationException("The debug adapter has exited."));
            output.Complete();
            Exited?.Invoke();
        }

        /// <summary>Ends the adapter and everything it started (flutter.bat, the app).</summary>
        internal async Task StopAsync()
        {
            if (!HasExited)
            {
                try { await process.StopAsync().ConfigureAwait(false); }
                catch (Exception) { }
            }
            MarkExited();
        }

        // Disposing before the exit notification arrives makes Process.Exited read ExitCode of a disposed process
        // (InvalidOperationException on a pool thread), so only an exited process is disposed.
        public void Dispose() { if (process.Exit.IsCompleted) process.Dispose(); }
    }

    /// <summary>DAP base protocol framing: <c>Content-Length: N\r\n\r\n</c> followed by N bytes of JSON.</summary>
    internal static class DapFraming
    {
        internal static byte[] Frame(byte[] body)
        {
            var header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
            var frame = new byte[header.Length + body.Length];
            Buffer.BlockCopy(header, 0, frame, 0, header.Length);
            Buffer.BlockCopy(body, 0, frame, header.Length, body.Length);
            return frame;
        }

        /// <summary>The body of the next frame, or null at the end of the stream.</summary>
        internal static async Task<byte[]?> ReadFrameAsync(Stream stream)
        {
            var header = new StringBuilder();
            var one = new byte[1];
            var length = -1;
            while (true)
            {
                var line = new StringBuilder();
                while (true)
                {
                    var read = await stream.ReadAsync(one, 0, 1).ConfigureAwait(false);
                    if (read == 0) return null;
                    if (one[0] == '\n') break;
                    if (one[0] != '\r') line.Append((char)one[0]);
                    if (line.Length > 8192) throw new IOException("DAP header line too long.");
                }
                if (line.Length == 0) break;
                header.AppendLine(line.ToString());
                if (line.ToString().StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.ToString().Substring(15).Trim());
            }
            if (length < 0) throw new IOException("DAP frame without Content-Length: " + header);
            var body = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await stream.ReadAsync(body, offset, length - offset).ConfigureAwait(false);
                if (read == 0) return null;
                offset += read;
            }
            return body;
        }

        /// <summary>Splits complete frames off the front of <paramref name="buffer"/>; incomplete data stays.</summary>
        internal static IEnumerable<byte[]> TakeFrames(MemoryStream buffer)
        {
            var frames = new List<byte[]>();
            while (true)
            {
                var data = buffer.GetBuffer();
                var count = (int)buffer.Length;
                var end = -1;
                for (var i = 0; i + 3 < count; i++)
                    if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n') { end = i; break; }
                if (end < 0) break;
                var header = Encoding.ASCII.GetString(data, 0, end);
                var length = -1;
                foreach (var line in header.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim());
                if (length < 0) throw new IOException("DAP frame without Content-Length.");
                var total = end + 4 + length;
                if (count < total) break;
                var frame = new byte[total];
                Buffer.BlockCopy(data, 0, frame, 0, total);
                frames.Add(frame);
                var rest = count - total;
                Buffer.BlockCopy(data, total, data, 0, rest);
                buffer.SetLength(rest);
            }
            return frames;
        }
    }

    /// <summary>Host-to-adapter stream: forwards whole frames only, so Vistudio's requests can be placed between them.</summary>
    internal sealed class DapInputStream : Stream
    {
        private readonly Func<byte[], Task> writeFrame;
        private readonly MemoryStream pending = new MemoryStream();
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        internal DapInputStream(Func<byte[], Task> writeFrame) => this.writeFrame = writeFrame;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

#pragma warning disable VSTHRD002 // The host calls the synchronous Stream contract on its own background thread.
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                pending.Write(buffer, offset, count);
                foreach (var frame in DapFraming.TakeFrames(pending)) await writeFrame(frame).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
    }

    /// <summary>Adapter-to-host stream fed with the frames the host should see.</summary>
    internal sealed class DapOutputStream : Stream
    {
        private readonly object gate = new object();
        private readonly Queue<byte[]> frames = new Queue<byte[]>();
        private readonly SemaphoreSlim available = new SemaphoreSlim(0);
        private byte[]? current;
        private int position;
        private bool completed;

        internal void Enqueue(byte[] frame)
        {
            lock (gate) { if (completed) return; frames.Enqueue(frame); }
            available.Release();
        }

        internal void Complete()
        {
            lock (gate) { if (completed) return; completed = true; }
            available.Release();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

#pragma warning disable VSTHRD002 // The host calls the synchronous Stream contract on its own background thread.
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (gate)
                {
                    if (current != null && position < current.Length)
                    {
                        var n = Math.Min(count, current.Length - position);
                        Buffer.BlockCopy(current, position, buffer, offset, n);
                        position += n;
                        return n;
                    }
                    if (frames.Count > 0) { current = frames.Dequeue(); position = 0; continue; }
                    if (completed) { available.Release(); return 0; }
                }
                await available.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
