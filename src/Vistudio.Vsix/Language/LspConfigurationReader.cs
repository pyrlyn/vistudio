using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Vistudio
{
    /// <summary>
    /// Server-to-client stream that answers the server's workspace/configuration requests for sections Vistudio owns
    /// (the Kotlin server's jetbrains.kotlin inlay hint options). Visual Studio answers that request only from Open
    /// Folder workspace settings, and an LSP middle layer sees only client-to-server traffic. The answer goes back
    /// through <see cref="KotlinInitializeStream.InjectAsync"/> between client frames; every other frame passes
    /// through byte for byte.
    /// </summary>
    internal sealed class LspConfigurationReader : Stream
    {
        private const int MaximumHeader = 16384;
        private static readonly byte[] Marker = Encoding.ASCII.GetBytes("\"workspace/configuration\"");
        private readonly Stream source;
        private readonly KotlinInitializeStream writer;
        private readonly Func<string, JToken?> section;
        private readonly Action<Exception>? failed;
        private readonly byte[] input = new byte[64 * 1024];
        private int inputStart, inputEnd;
        private byte[] output = Array.Empty<byte>();
        private int outputStart;

        /// <param name="section">Returns the value of an owned section, or null when Visual Studio's answer (null) applies.</param>
        public LspConfigurationReader(Stream source, KotlinInitializeStream writer, Func<string, JToken?> section, Action<Exception>? failed = null)
        { this.source = source; this.writer = writer; this.section = section; this.failed = failed; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

#pragma warning disable VSTHRD002 // Required synchronous Stream API; all awaits are context-free.
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            while (outputStart >= output.Length)
            {
                var frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
                if (frame == null) return 0;
                if (!TryAnswer(frame.Value.Bytes, frame.Value.BodyStart)) { output = frame.Value.Bytes; outputStart = 0; }
            }
            var take = Math.Min(count, output.Length - outputStart);
            Buffer.BlockCopy(output, outputStart, buffer, offset, take);
            outputStart += take;
            return take;
        }

        // A complete frame (header and body bytes) or null at the end of the stream.
        private async Task<(byte[] Bytes, int BodyStart)?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            int headerEnd;
            while ((headerEnd = HeaderEnd()) < 0)
            {
                if (inputEnd - inputStart > MaximumHeader) throw new InvalidDataException("LSP header is too large.");
                if (!await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (inputEnd == inputStart) return null;
                    throw new EndOfStreamException("LSP stream ended inside a header.");
                }
            }
            var headerLength = headerEnd - inputStart;
            var bodyLength = ContentLength(input, inputStart, headerLength);
            var frame = new byte[headerLength + bodyLength];
            var copied = 0;
            while (copied < frame.Length)
            {
                if (inputStart == inputEnd && !await FillAsync(cancellationToken).ConfigureAwait(false))
                    throw new EndOfStreamException("LSP stream ended inside a message.");
                var take = Math.Min(frame.Length - copied, inputEnd - inputStart);
                Buffer.BlockCopy(input, inputStart, frame, copied, take);
                inputStart += take;
                copied += take;
            }
            return (frame, headerLength);
        }

        private int HeaderEnd()
        {
            for (var i = inputStart; i + 3 < inputEnd; i++)
                if (input[i] == 13 && input[i + 1] == 10 && input[i + 2] == 13 && input[i + 3] == 10) return i + 4;
            return -1;
        }

        private async Task<bool> FillAsync(CancellationToken cancellationToken)
        {
            if (inputStart > 0)
            {
                Buffer.BlockCopy(input, inputStart, input, 0, inputEnd - inputStart);
                inputEnd -= inputStart;
                inputStart = 0;
            }
            if (inputEnd == input.Length) throw new InvalidDataException("LSP header is too large.");
            var read = await source.ReadAsync(input, inputEnd, input.Length - inputEnd, cancellationToken).ConfigureAwait(false);
            inputEnd += read;
            return read > 0;
        }

        private static int ContentLength(byte[] bytes, int offset, int length)
        {
            foreach (var line in Encoding.ASCII.GetString(bytes, offset, length).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = line.IndexOf(':');
                if (colon > 0 && line.Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line.Substring(colon + 1).Trim(), out var parsed) && parsed >= 0)
                    return parsed;
            }
            throw new InvalidDataException("LSP frame has no Content-Length.");
        }

        // Answers a workspace/configuration request that names an owned section; other frames return false.
        private bool TryAnswer(byte[] frame, int bodyStart)
        {
            if (!Contains(frame, bodyStart, Marker)) return false; // skip JSON parsing for all other traffic
            JObject message;
            try { message = JObject.Parse(Encoding.UTF8.GetString(frame, bodyStart, frame.Length - bodyStart)); }
            catch (JsonException) { return false; }
            if ((string?)message["method"] != "workspace/configuration" || message["id"] == null) return false;
            if (!(message["params"]?["items"] is JArray items)) return false;
            var values = items.Select(item => (string?)item["section"] is string name ? section(name) : null).ToArray();
            if (values.All(value => value == null)) return false;
            var response = new JObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["result"] = new JArray(values.Select(value => value ?? JValue.CreateNull())) };            var body = Encoding.UTF8.GetBytes(response.ToString(Formatting.None));
            var header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
            var bytes = new byte[header.Length + body.Length];
            Buffer.BlockCopy(header, 0, bytes, 0, header.Length);
            Buffer.BlockCopy(body, 0, bytes, header.Length, body.Length);
            // Written from another task: the server may be blocked writing output this reader has not consumed yet.
            _ = Task.Run(async () =>
            {
                try { await writer.InjectAsync(bytes).ConfigureAwait(false); }
                catch (Exception e) { failed?.Invoke(e); }
            });
            return true;
        }

        private static bool Contains(byte[] bytes, int start, byte[] marker)
        {
            for (var i = start; i + marker.Length <= bytes.Length; i++)
            {
                var match = true;
                for (var j = 0; j < marker.Length && match; j++) match = bytes[i + j] == marker[j];
                if (match) return true;
            }
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) source.Dispose();
            base.Dispose(disposing);
        }
    }
}
