using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Vistudio
{
    // Kotlin server 263.7084 emits file URIs in Source.path even when DAP pathFormat is path.
    // Visual Studio expects local paths for source navigation and breakpoint rebinding.
    internal sealed class KotlinDapSourceStream : Stream
    {
        private readonly Func<CancellationToken, Task<JObject?>> readMessage;
        private readonly Action ended;
        private byte[] frame = Array.Empty<byte>();
        private int offset;
        internal KotlinDapSourceStream(Stream source, Action ended) : this(token => ReadMessageAsync(source, token), ended) { }
        internal KotlinDapSourceStream(Func<CancellationToken, Task<JObject?>> readMessage, Action ended)
        { this.readMessage = readMessage; this.ended = ended; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
#pragma warning disable VSTHRD002 // Stream's synchronous API; every await below is context-free.
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
        public override async Task<int> ReadAsync(byte[] buffer, int start, int count, CancellationToken token)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (start < 0 || count < 0 || start > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return 0;
            if (offset == frame.Length)
            {
                try { if (!await ReadFrameAsync(token).ConfigureAwait(false)) { ended(); return 0; } }
                catch { ended(); throw; }
            }
            var size = Math.Min(count, frame.Length - offset);
            Buffer.BlockCopy(frame, offset, buffer, start, size);
            offset += size;
            return size;
        }
        private async Task<bool> ReadFrameAsync(CancellationToken token)
        {
            var message = await readMessage(token).ConfigureAwait(false);
            if (message == null) return false;
            NormalizeSources(message);
            frame = Encode(message);
            offset = 0;
            return true;
        }
        internal static async Task<JObject?> ReadMessageAsync(Stream source, CancellationToken token)
        {
            var one = new byte[1];
            var header = new StringBuilder();
            while (true)
            {
                if (await source.ReadAsync(one, 0, 1, token).ConfigureAwait(false) == 0)
                {
                    if (header.Length != 0) throw new EndOfStreamException("Truncated DAP header.");
                    return null;
                }
                header.Append((char)one[0]);
                if (header.Length > 16384) throw new InvalidDataException("DAP header is too large.");
                var n = header.Length;
                if (n >= 4 && header[n - 4] == '\r' && header[n - 3] == '\n' && header[n - 2] == '\r' && header[n - 1] == '\n') break;
            }
            var length = ParseLength(header.ToString());
            var body = new byte[length];
            var read = 0;
            while (read < length)
            {
                var size = await source.ReadAsync(body, read, length - read, token).ConfigureAwait(false);
                if (size == 0) throw new EndOfStreamException("Truncated DAP message.");
                read += size;
            }
            return JObject.Parse(new UTF8Encoding(false, true).GetString(body));
        }
        internal static int ParseLength(string header)
        {
            var length = -1;
            foreach (var line in header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    if (length != -1 || !int.TryParse(line.Substring(15).Trim(), out length)) throw new InvalidDataException("Invalid DAP Content-Length.");
                }
            if (length < 0 || length > 16 * 1024 * 1024) throw new InvalidDataException("Invalid DAP frame length.");
            return length;
        }
        internal static byte[] Encode(JObject message)
        {
            var body = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
            var prefix = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
            var frame = new byte[prefix.Length + body.Length];
            Buffer.BlockCopy(prefix, 0, frame, 0, prefix.Length);
            Buffer.BlockCopy(body, 0, frame, prefix.Length, body.Length);
            return frame;
        }
        internal static void NormalizeSources(JToken token)
        {
            if (token is JObject obj)
            {
                // Source objects occur inside stack frames, breakpoints, loadedSources and source events.
                var path = obj["path"];
                if (path?.Type == JTokenType.String && Uri.TryCreate((string?)path, UriKind.Absolute, out var uri) && uri.IsFile)
                {
                    var local = uri.LocalPath;
                    // URI-encoded drive colons are parsed as /c:/... by System.Uri.
                    if (local.Length >= 4 && local[0] == '/' && char.IsLetter(local[1]) && local[2] == ':') local = local.Substring(1);
                    obj["path"] = Path.GetFullPath(local);
                }
            }
            foreach (var child in token.Children()) NormalizeSources(child);
        }
    }
}
