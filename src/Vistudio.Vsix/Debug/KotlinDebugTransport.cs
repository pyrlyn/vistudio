using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Debugger.DebugAdapterHost.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Vistudio
{
    internal sealed class KotlinDebugTransport : ITargetHostProcess
    {
        private readonly TcpClient client;
        private readonly Action release;
        private int exited;
        private KotlinDebugTransport(TcpClient client, Action release)
        {
            this.client = client;
            this.release = release;
            var session = new KotlinDapSession(client.GetStream());
            StandardInput = new KotlinDapInputStream(session, Terminate);
            StandardOutput = new KotlinDapSourceStream(session.ReadAsync, Terminate);
        }
        internal static async Task<KotlinDebugTransport> ConnectAsync(int port, Action release, CancellationToken token)
        {
            if (port < 1 || port > 65535) throw new InvalidOperationException("Kotlin server returned an invalid DAP port.");
            var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port).WithCancellation(token).ConfigureAwait(false);
                return new KotlinDebugTransport(client, release);
            }
            catch { client.Dispose(); throw; }
        }
        public IntPtr Handle => IntPtr.Zero;
        public Stream StandardInput { get; }
        public Stream StandardOutput { get; }
        public bool HasExited => Volatile.Read(ref exited) != 0;
        public event EventHandler? Exited;
        // This socket has no stderr channel. DAP output events go to the native Debug Output pane.
        public event DataReceivedEventHandler ErrorDataReceived { add { } remove { } }
        public void Terminate()
        {
            if (Interlocked.Exchange(ref exited, 1) != 0) return;
            client.Dispose();
            release();
            Exited?.Invoke(this, EventArgs.Empty);
        }
    }
}
