using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Vistudio
{
    // Host writes may split or combine frames. Buffer at most one bounded frame.
    internal sealed class KotlinDapInputStream : Stream
    {
        private readonly KotlinDapSession session;
        private readonly Action failed;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly StringBuilder header = new StringBuilder();
        private byte[]? body;
        private int received;
        internal KotlinDapInputStream(KotlinDapSession session, Action failed) { this.session = session; this.failed = failed; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { } // Every complete frame is flushed by the session.
#pragma warning disable VSTHRD002 // Required synchronous Stream API; all awaits are context-free.
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                while (count > 0)
                {
                    if (body == null)
                    {
                        header.Append((char)buffer[offset++]); count--;
                        var n = header.Length;
                        if (n > 16384) throw new InvalidDataException("DAP header is too large.");
                        if (n < 4 || header[n - 4] != '\r' || header[n - 3] != '\n' || header[n - 2] != '\r' || header[n - 1] != '\n') continue;
                        body = new byte[KotlinDapSourceStream.ParseLength(header.ToString())];
                        header.Clear();
                    }
                    var size = Math.Min(count, body.Length - received);
                    Buffer.BlockCopy(buffer, offset, body, received, size);
                    offset += size; count -= size; received += size;
                    if (received != body.Length) continue;
                    var message = JObject.Parse(new UTF8Encoding(false, true).GetString(body));
                    body = null; received = 0;
                    await session.SendAsync(message, token).ConfigureAwait(false);
                }
            }
            catch { failed(); throw; }
            finally { gate.Release(); }
        }
    }
}
