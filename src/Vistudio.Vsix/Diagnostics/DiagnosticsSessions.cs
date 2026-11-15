using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Vistudio.Core.Diagnostics;

namespace Vistudio.Diagnostics
{
    /// <summary>
    /// The current Vistudio Diagnostics session. The launch code of each language calls one Watch method; the session
    /// follows the debugger's break and design modes and opens the Vistudio Diagnostics window.
    /// </summary>
    internal static class DiagnosticsSessions
    {
        private static readonly object Gate = new object();
        private static DiagnosticsMonitor? current;
        private static bool currentFollowsDebugger;
        private static CancellationTokenSource? pending;
        private static bool pendingFollowsDebugger;
        private static string status = "Start a Kotlin, Java, Dart/Flutter or WebAssembly program with F5 to see its CPU, memory and heap here. " +
            "Rust, Zig, C and C++ programs debug in the native engine: use Debug > Windows > Show Diagnostic Tools for them.";

        internal static DiagnosticsMonitor? Current { get { lock (Gate) return current; } }
        internal static string Status { get { lock (Gate) return status; } }
        /// <summary>Raised on any thread when the session or the status line changes.</summary>
        internal static event Action? Changed;

        /// <summary>Kotlin/Java: the Kotlin server starts the JVM through Gradle, so it is found by its main class once it runs.</summary>
        internal static void WatchJvm(string mainClass, string? javaHome) =>
            Begin("Waiting for the JVM running " + mainClass + " (the Kotlin server builds and starts it through Gradle)...", followsDebugger: true, async token =>
            {
                var jps = JvmTools.FindTool("jps", null, javaHome)
                    ?? throw new InvalidOperationException("jps was not found in the configured JDK, JAVA_HOME or PATH, so the program's JVM cannot be found.");
                var before = await DiagnosticsTargets.RunningJvmsAsync(jps, token).ConfigureAwait(false);
                return await DiagnosticsTargets.ForJvmAsync(jps, mainClass, before, javaHome, TimeSpan.FromMinutes(5), token).ConfigureAwait(false);
            });

        /// <summary>Dart/Flutter: the debug adapter announced the program's VM service.</summary>
        internal static void WatchDartVm(string vmServiceUri, string title) =>
            Begin("Connecting to the Dart VM service of " + title + "...", followsDebugger: true, token => DiagnosticsTargets.ForDartVmAsync(vmServiceUri, title, token));

        /// <summary>A program run without a debugger (WebAssembly F5): the process and its children until they exit.</summary>
        internal static void WatchProcessTree(int pid, string title) =>
            Begin("Watching " + title + "...", followsDebugger: false, token => Task.FromResult(DiagnosticsTargets.ForProcessTree(pid, title)));

        /// <summary>A Kotlin JS, wasmJs or Native target run through Gradle on F5: the program under the Gradle process.</summary>
        internal static void WatchProgramUnderGradle(int gradlePid, string title) =>
            Begin("Watching " + title + "...", followsDebugger: false, token => Task.FromResult(DiagnosticsTargets.ForProgramUnderGradle(gradlePid, title)));

        /// <summary>
        /// The Gradle run that should have started the watched program has finished; a session still waiting for its
        /// program (the build failed) would otherwise wait for ever.
        /// </summary>
        internal static void EndRun(string reason)
        {
            DiagnosticsMonitor? monitor;
            lock (Gate) monitor = currentFollowsDebugger ? null : current;
            monitor?.Stop(reason);
            Changed?.Invoke();
        }

        private static void Begin(string waiting, bool followsDebugger, Func<CancellationToken, Task<DiagnosticsMonitor>> start)
        {
            var cancellation = new CancellationTokenSource();
            lock (Gate)
            {
                pending?.Cancel();
                pending = cancellation;
                pendingFollowsDebugger = followsDebugger;
                status = waiting;
            }
            Changed?.Invoke();
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ShowWindowAsync();
                await TaskScheduler.Default;
                DiagnosticsMonitor monitor;
                try { monitor = await start(cancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    SetStatus(cancellation, "Diagnostics could not start: " + e.Message);
                    return;
                }
                DiagnosticsMonitor? replaced;
                lock (Gate)
                {
                    if (pending != cancellation)
                    {
                        replaced = monitor; // a newer launch won while this one was starting
                    }
                    else
                    {
                        pending = null;
                        replaced = current;
                        current = monitor;
                        currentFollowsDebugger = followsDebugger;
                        status = monitor.Timeline.Title;
                    }
                }
                replaced?.Stop("A new diagnostics session started.");
                Changed?.Invoke();
            }).FileAndForget("Vistudio/Diagnostics/Start");
        }

        private static void SetStatus(CancellationTokenSource owner, string text)
        {
            lock (Gate)
            {
                if (pending != owner) return;
                pending = null;
                status = text;
            }
            Changed?.Invoke();
        }

        /// <summary>A launch failed before its program started.</summary>
        internal static void CancelPending(string reason)
        {
            lock (Gate)
            {
                if (pending == null) return;
                pending.Cancel();
                pending = null;
                status = reason;
            }
            Changed?.Invoke();
        }

        internal static void OnDebuggerMode(DBGMODE mode)
        {
            DiagnosticsMonitor? monitor;
            bool follows;
            lock (Gate) { monitor = current; follows = currentFollowsDebugger; }
            if (mode == DBGMODE.DBGMODE_Break) monitor?.SetBroken(true);
            else if (mode == DBGMODE.DBGMODE_Run) monitor?.SetBroken(false);
            else if (mode == DBGMODE.DBGMODE_Design)
            {
                lock (Gate)
                {
                    if (pending != null && pendingFollowsDebugger)
                    {
                        pending.Cancel();
                        pending = null;
                        status = "Debugging stopped before the program was found.";
                    }
                }
                // The recorded data stays in the window after Stop Debugging, as in Diagnostic Tools.
                if (monitor != null && follows) monitor.Stop("Debugging stopped.");
                Changed?.Invoke();
            }
        }

        private static async Task ShowWindowAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!(ServiceProvider.GlobalProvider.GetService(typeof(SVsShell)) is IVsShell shell)) return;
            var id = new Guid(DiagnosticsPackage.PackageGuid);
            if (ErrorHandler.Failed(shell.LoadPackage(ref id, out var loaded)) || !(loaded is DiagnosticsPackage package)) return;
            await package.ShowAsync(activate: false);
        }
    }
}
