using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Vistudio
{
    // A write-only adapter for the client-to-server stream. It rewrites the initial client frame and otherwise passes
    // client bytes through unchanged. It also tracks frame boundaries so that LspConfigurationReader can answer server
    // requests (workspace/configuration) with complete frames placed between client frames, never inside one.
    internal sealed class KotlinInitializeStream : Stream
    {
        private const int MaximumHeader = 16384;
        private const int MaximumInitializeBody = 4 * 1024 * 1024;
        private readonly Stream destination;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly MemoryStream pending = new MemoryStream();
        private bool initialized;
        private int headerLength = -1;
        private int bodyLength;
        // After initialize: bytes of the current client header seen so far, and body bytes still to pass.
        private readonly MemoryStream header = new MemoryStream();
        private int bodyRemaining;
        private readonly System.Collections.Generic.Queue<byte[]> injected = new System.Collections.Generic.Queue<byte[]>();

        private bool AtBoundary => initialized && bodyRemaining == 0 && header.Length == 0;

        /// <summary>Writes a complete frame to the server between client frames (now, or right after the current one).</summary>
        public async Task InjectAsync(byte[] frame, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                injected.Enqueue(frame);
                if (AtBoundary) await FlushInjectedAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        private async Task FlushInjectedAsync(CancellationToken cancellationToken)
        {
            if (injected.Count == 0) return;
            while (injected.Count > 0)
            {
                var frame = injected.Dequeue();
                await destination.WriteAsync(frame, 0, frame.Length, cancellationToken).ConfigureAwait(false);
            }
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // Passes client bytes after initialize, splitting them at frame ends so queued frames go in between.
        private async Task PassAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (AtBoundary) await FlushInjectedAsync(cancellationToken).ConfigureAwait(false);
            var start = offset;
            var end = offset + count;
            var i = offset;
            while (i < end)
            {
                if (bodyRemaining > 0)
                {
                    var take = Math.Min(bodyRemaining, end - i);
                    i += take;
                    bodyRemaining -= take;
                }
                else
                {
                    header.WriteByte(buffer[i++]);
                    var length = header.Length;
                    var bytes = header.GetBuffer();
                    if (length >= 4 && bytes[length - 4] == 13 && bytes[length - 3] == 10 && bytes[length - 2] == 13 && bytes[length - 1] == 10)
                    {
                        bodyRemaining = ContentLength(bytes, (int)length);
                        header.SetLength(0);
                    }
                    else if (length > MaximumHeader) throw new InvalidDataException("LSP header is too large.");
                }
                if (AtBoundary && injected.Count > 0)
                {
                    await destination.WriteAsync(buffer, start, i - start, cancellationToken).ConfigureAwait(false);
                    start = i;
                    await FlushInjectedAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            if (i > start) await destination.WriteAsync(buffer, start, i - start, cancellationToken).ConfigureAwait(false);
        }

        private static int ContentLength(byte[] bytes, int length)
        {
            foreach (var line in Encoding.ASCII.GetString(bytes, 0, length).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = line.IndexOf(':');
                if (colon > 0 && line.Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line.Substring(colon + 1).Trim(), out var parsed) && parsed >= 0)
                    return parsed;
            }
            throw new InvalidDataException("LSP frame has no Content-Length.");
        }

        private readonly Func<JToken, JToken> normalize;

        public KotlinInitializeStream(Stream destination) : this(destination, KotlinLspCompatibility.NormalizeInitialize) { }

        /// <summary>Also used for the Java server, with its own initialize normalization.</summary>
        public KotlinInitializeStream(Stream destination, Func<JToken, JToken> normalize)
        { this.destination = destination; this.normalize = normalize; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => destination.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

#pragma warning disable VSTHRD002 // Required synchronous Stream API; all awaits are context-free.
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (initialized) { await PassAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false); return; }
                var frame = Transform(buffer, offset, count, out var tail);
                if (frame == null) return;
                await destination.WriteAsync(frame, 0, frame.Length, cancellationToken).ConfigureAwait(false);
                // Bytes after the initialize frame start the next client frame.
                if (tail.Length > 0) await PassAsync(tail, 0, tail.Length, cancellationToken).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        private byte[]? Transform(byte[] buffer, int offset, int count, out byte[] tail)
        {
            tail = Array.Empty<byte>();
            pending.Write(buffer, offset, count);
            var bytes = pending.GetBuffer();
            var size = checked((int)pending.Length);
            if (headerLength < 0)
            {
                for (var i = 0; i + 3 < size && i < MaximumHeader; i++)
                    if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10)
                    { headerLength = i + 4; break; }
                if (headerLength < 0)
                {
                    if (size >= MaximumHeader) throw new InvalidDataException("LSP initialize header is too large.");
                    return null;
                }
                int? length = null;
                foreach (var line in Encoding.ASCII.GetString(bytes, 0, headerLength).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var colon = line.IndexOf(':');
                    if (colon < 0 || !line.Substring(0, colon).Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                    if (length != null || !int.TryParse(line.Substring(colon + 1).Trim(), out var parsed) || parsed <= 0 || parsed > MaximumInitializeBody)
                        throw new InvalidDataException("Invalid LSP initialize Content-Length.");
                    length = parsed;
                }
                bodyLength = length ?? throw new InvalidDataException("LSP initialize has no Content-Length.");
            }
            if (size - headerLength < bodyLength) return null;
            var message = JObject.Parse(new UTF8Encoding(false, true).GetString(bytes, headerLength, bodyLength));
            if ((string?)message["method"] != "initialize" || message["params"] == null)
                throw new InvalidDataException("Expected initialize as the first LSP request.");
            message["params"] = normalize(message["params"]!);
            var body = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
            var prefix = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
            var tailStart = headerLength + bodyLength;
            var result = new byte[prefix.Length + body.Length];
            Buffer.BlockCopy(prefix, 0, result, 0, prefix.Length);
            Buffer.BlockCopy(body, 0, result, prefix.Length, body.Length);
            tail = new byte[size - tailStart];
            Buffer.BlockCopy(bytes, tailStart, tail, 0, tail.Length);
            initialized = true;
            pending.SetLength(0);
            pending.Capacity = 0;
            return result;
        }

        public override void Flush() { gate.Wait(); try { destination.Flush(); } finally { gate.Release(); } }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await destination.FlushAsync(cancellationToken).ConfigureAwait(false); }
            finally { gate.Release(); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { pending.Dispose(); header.Dispose(); destination.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
