using System;
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;

namespace Vistudio
{
    /// <summary>
    /// What the DAP traffic of one Kotlin/JVM debug session says about its launch: whether the Kotlin server's debugger
    /// ever attached to the program's JVM, and how the session ended when it did not. The server waits 30 s after the
    /// launch request for the JVM and then only sends <c>terminated</c>; Visual Studio shows that as "The program ''
    /// has exited with code 4294967295". <see cref="Describe"/> gives the user the reason instead.
    /// No Visual Studio dependencies; linked into the regression tests.
    /// </summary>
    internal sealed class KotlinDebugMonitor
    {
        /// <summary>The server's debugger polls the JVM's debug port for this long (IntelliJ's local start timeout; not configurable).</summary>
        internal const int ServerAttachSeconds = 30;

        // The debug adapter host creates the adapter connection (KotlinAdapterLauncher) from the launch JSON alone, so the
        // launch carries this id and the connection looks its monitor up. Only these observations cross that boundary.
        private static readonly ConcurrentDictionary<string, KotlinDebugMonitor> Sessions = new ConcurrentDictionary<string, KotlinDebugMonitor>(StringComparer.Ordinal);
        private readonly object sync = new object();
        private readonly Func<DateTime> now;
        private DateTime? launchRequested, ended;
        private bool attached, clientEnded, serverTerminated;
        private int? exitCode;

        internal KotlinDebugMonitor(Func<DateTime>? utcNow = null) => now = utcNow ?? (() => DateTime.UtcNow);

        /// <summary>The value of the launch's <c>vistudioSession</c> property.</summary>
        internal string Id { get; } = Guid.NewGuid().ToString("N");

        internal static KotlinDebugMonitor Register()
        {
            var monitor = new KotlinDebugMonitor();
            Sessions[monitor.Id] = monitor;
            return monitor;
        }

        internal static KotlinDebugMonitor? Find(string? id) => id != null && Sessions.TryGetValue(id, out var monitor) ? monitor : null;

        internal void Forget() => Sessions.TryRemove(Id, out _);

        /// <summary>A message from Visual Studio to the server.</summary>
        internal void Sent(JObject message)
        {
            if ((string?)message["type"] != "request") return;
            var command = (string?)message["command"];
            lock (sync)
            {
                if (command == "launch" && launchRequested == null) launchRequested = now();
                // Stop Debugging while the program is still starting is the user's choice, not a failed attach.
                if ((command == "disconnect" || command == "terminate") && !serverTerminated) clientEnded = true;
            }
        }

        /// <summary>A message from the server, as it arrives (before the session holds a <c>terminated</c> back for <c>exited</c>).</summary>
        internal void Received(JObject message)
        {
            if ((string?)message["type"] != "event") return;
            var name = (string?)message["event"];
            lock (sync)
            {
                // Sent only for a JVM the debugger is connected to. A program that never stops sends none of them:
                // the server reports no threads and no process on its own (only "initialized", output and the end).
                if (name == "thread" || name == "stopped" || name == "continued") attached = true;
                if (name == "exited") exitCode = (int?)message["body"]?["exitCode"] ?? 0;
                if (name == "terminated" && !serverTerminated) { serverTerminated = true; ended = now(); }
            }
        }

        /// <summary>True once the debugger reported a thread or a stop of the program. False does not mean it never attached.</summary>
        internal bool Attached { get { lock (sync) return attached; } }

        /// <summary>
        /// True for a launch whose program is not known to have run to its end: no stop was seen and the server never
        /// reported an exit code. Its JVM can still exist, waiting for a debugger.
        /// </summary>
        internal bool ProgramMayBeLeft { get { lock (sync) return launchRequested != null && !attached && exitCode == null; } }

        /// <summary>
        /// Why a debug session ended without the debugger ever attaching, for the Kotlin Output pane. The server ends
        /// such a session with <c>terminated</c> alone; a program that ran, and a Gradle run that failed, end with an
        /// exit code (<c>exited</c>), which Visual Studio shows. Null for those, for a run without debugging, for a
        /// session the user stopped, and for one that never sent its launch request.
        /// </summary>
        internal string? Describe(string mainClass, bool noDebug)
        {
            lock (sync)
            {
                if (noDebug || attached || clientEnded || launchRequested == null || exitCode != null || !serverTerminated) return null;
                var seconds = (int)Math.Round((ended!.Value - launchRequested.Value).TotalSeconds);
                // The wait itself, plus what the server needs to notice and report it.
                return seconds >= ServerAttachSeconds - 5 && seconds <= ServerAttachSeconds + 15
                    ? "Kotlin debugging: the debugger did not attach to " + mainClass + ". The Kotlin server waits " + ServerAttachSeconds + " s after the launch request for the program's JVM to accept a debugger " +
                      "and ended the session after " + seconds + " s; Gradle had not started the program by then. Visual Studio reports this as exit code 4294967295. " +
                      "The Gradle run of this launch was ended, so no JVM is left waiting for a debugger. Debug Output shows how far Gradle got. Start again, or run without debugging (Ctrl+F5), which has no such limit."
                    : "Kotlin debugging: the session for " + mainClass + " ended " + seconds + " s after the launch request without an exit code from the program, so the debugger was not attached to it any more or never was. " +
                      "Gradle's messages are in Debug Output. The Gradle run of this launch was ended.";
            }
        }
    }
}
