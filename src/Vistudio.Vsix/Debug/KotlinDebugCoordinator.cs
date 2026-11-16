using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Workspace.Debug;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vistudio.Core;

namespace Vistudio
{
    [Export, PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class KotlinDebugCoordinator
    {
        internal const string EngineId = "7b672d65-cf4f-424e-8584-49f2f208e46c";
        private readonly KotlinEnvironment environment;
        private readonly KotlinLanguageClient language;
        private readonly KotlinHotReload hotReload;
        private readonly IServiceProvider services;
        private readonly SemaphoreSlim launchGate = new SemaphoreSlim(1, 1);
        private KotlinDebugLifecycle? session;

        [ImportingConstructor]
        public KotlinDebugCoordinator(KotlinEnvironment environment, KotlinLanguageClient language, KotlinHotReload hotReload,
            [Import(typeof(SVsServiceProvider))] IServiceProvider services)
        { this.environment = environment; this.language = language; this.hotReload = hotReload; this.services = services; }

        internal Task LaunchAsync(string workspace, DebugLaunchActionContext context, CancellationToken token) =>
            LaunchAsync(workspace, JObject.FromObject(context.LaunchConfiguration), context.ProjectFileContext?.FilePath, token);

        /// <summary>
        /// Shared by Open Folder launch.vs.json targets and solution-loaded .kotlinproj projects (F5 / Ctrl+F5).
        /// Keys: mainClass, project (Kotlin source file), cwd, args, vmArgs, env, noDebug, gradleProject, sourceSet.
        /// </summary>
        internal async Task LaunchAsync(string workspace, JObject config, string? contextFile, CancellationToken token, bool buildFirst = true)
        {
            if (!await launchGate.WaitAsync(0, token)) return;
            var launched = false;
            var lease = false;
            LaunchedProgram? program = null;
            try
            {
                var source = (string?)config["target"] ?? (string?)config["project"] ?? contextFile;
                var mainClass = (string?)config["mainClass"];
                if (string.IsNullOrWhiteSpace(source)) throw new InvalidOperationException("Set project to the Kotlin or Java source file containing main in .vs/launch.vs.json.");
                source = Path.GetFullPath(Path.Combine(workspace, source));
                if (!File.Exists(source)) throw new FileNotFoundException("Debug source file does not exist.", source);
                // F5 on the current .kt/.java document: derive the entry class from the file when none is configured.
                if (string.IsNullOrWhiteSpace(mainClass) || mainClass == "your.package.MainKt")
                    mainClass = KotlinJvmLaunch.InferMainClass(source, File.ReadAllText(source))
                        ?? throw new InvalidOperationException(Path.GetFileName(source) + " has no main entry point. Set mainClass to the JVM entry class (for example sample.MainKt) in .vs/launch.vs.json.");
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                if (!language.IsLoaded)
                {
                    // Visual Studio loads the language client only for an open Kotlin document; open the entry source so
                    // server initialization overlaps the Gradle build below.
                    VsShellUtilities.OpenDocument(services, source);
                }
                var shell = services.GetService(typeof(SVsShell)) as IVsShell ?? throw new InvalidOperationException("Visual Studio shell unavailable.");
                var packageId = typeof(KotlinPackage).GUID;
                ErrorHandler.ThrowOnFailure(shell.LoadPackage(ref packageId, out var package));
                var settings = await Task.Run(() => KotlinSettings.LoadForFolder(workspace), token);
                var gradleRoot = KotlinEnvironment.ResolveGradleRoot(workspace, settings.GradleRoot);
                // The Kotlin server's debugger gives the program's JVM 30 s from the launch request, and a Gradle client
                // that first starts a JVM for the build can need longer. This session therefore gets a Gradle daemon of
                // its own: the build below and the warm-up before the launch run on it, the server's Gradle client
                // finds it warm, and it ends with the session (together with the program it started).
                program = new LaunchedProgram(new GradleSessionDaemon(GradleSessionDaemon.RegistryFor(gradleRoot)), mainClass!, (bool?)config["noDebug"] == true);
                if (buildFirst && !await ((KotlinPackage)package).BuildForDebugAsync(token, program.Daemon)) return;
                token.ThrowIfCancellationRequested();
                if (!string.Equals(workspace, await environment.GetFolderAsync(), StringComparison.OrdinalIgnoreCase))
                    throw new OperationCanceledException("Workspace changed before debug launch.");

                for (var waited = 0; !language.IsLoaded; waited += 250)
                {
                    if (waited >= 120000) throw new TimeoutException("The Kotlin language client did not load for " + Path.GetFileName(source) + ". Check Tools > Options > Kotlin.");
                    await Task.Delay(250, token);
                }
                // A debugger session depends on the server's imported Gradle model and DAP endpoint.
                language.RetainDebugSession();
                lease = true;
                using (var startup = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    startup.CancelAfter(TimeSpan.FromMinutes(3));
                    var rpc = await language.GetDebugRpcAsync(startup.Token);
                    var uri = new Uri(source).AbsoluteUri;
                    // Server 271+: the launch itself names the main class and its file; the server finds the module.
                    var model = await rpc.InvokeWithParameterObjectAsync<JObject?>("workspace/executeCommand",
                        new { command = "intellij.java.buildTool", arguments = new object[] { new { uri } } }, startup.Token);
                    var launch = KotlinJvmLaunch.Create(workspace, source, mainClass!, config, model, settings.GradleJavaHome, program.Daemon.ClientArguments);
                    // Gradle decides how long this takes, not the server: no startup timeout while it runs.
                    startup.CancelAfter(Timeout.InfiniteTimeSpan);
                    await WarmUpAsync((KotlinPackage)package, program.Daemon, gradleRoot, launch, startup.Token);
                    startup.CancelAfter(TimeSpan.FromMinutes(3));
                    var port = await rpc.InvokeWithParameterObjectAsync<int>("workspace/executeCommand",
                        new { command = "start_debug_server", arguments = new[] { new Uri(workspace.TrimEnd('\\', '/') + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/') } }, startup.Token);
                    if (port < 1 || port > 65535) throw new InvalidOperationException("Kotlin server returned an invalid DAP port.");
                    launch["vistudioPort"] = port;
                    // The adapter connection (KotlinAdapterLauncher) reports what it sees of this launch to the program's monitor.
                    launch["vistudioSession"] = program.Monitor.Id;
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(startup.Token);
                    var debugger = services.GetService(typeof(SVsShellDebugger)) as IVsDebugger4 ?? throw new InvalidOperationException("Visual Studio debugger unavailable.");
                    session?.Dispose();
                    // The session ends (design mode, or a launch that failed) for the server lease, for hot reload and
                    // for the session's Gradle daemon alike.
                    var ending = program;
                    session = new KotlinDebugLifecycle((IVsDebugger)debugger, () => { language.ReleaseDebugSession(); hotReload.ProgramEnded(); End(ending); });
                    lease = false;
                    program = null; // The lifecycle owns it from here.
                    var javaHome = (string?)launch["env"]?["JAVA_HOME"] ?? settings.GradleJavaHome;
                    ending.JavaHome = javaHome;
                    // Hot reload (Tools > Kotlin > Hot Reload, or saving a source file) compiles in the Gradle build of this
                    // workspace and finds the JVM by its main class, like Vistudio Diagnostics below.
                    hotReload.ProgramStarting(KotlinEnvironment.ResolveGradleRoot(workspace, settings.GradleRoot), source, mainClass!, javaHome);
                    var target = new VsDebugTargetInfo4
                    {
                        dlo = (uint)DEBUG_LAUNCH_OPERATION.DLO_CreateProcess,
                        bstrExe = mainClass, bstrCurDir = (string?)launch["cwd"],
                        bstrOptions = launch.ToString(Formatting.None),
                        guidLaunchDebugEngine = new Guid(EngineId), fSendToOutputWindow = true
                    };
                    environment.Log("Launching Kotlin/JVM debugger for " + mainClass + " (Gradle). Program output appears in Debug Output.");
                    // Visual Studio's Diagnostic Tools refuses Debug Adapter Host engines (T71); Vistudio Diagnostics
                    // finds the JVM that Gradle starts and shows its CPU, memory and heap instead.
                    Diagnostics.DiagnosticsSessions.WatchJvm(mainClass!, javaHome);
                    ending.StartedUtc = DateTime.UtcNow;
                    try { debugger.LaunchDebugTargets4(1, new[] { target }, new VsDebugTargetProcessInfo[1]); }
                    catch { Diagnostics.DiagnosticsSessions.CancelPending("The Kotlin debug launch failed."); throw; }
                    launched = true; // Native debugger events now own the server lease.
                }
            }
            catch (OperationCanceledException) { environment.Log("Kotlin debug launch canceled or server initialization timed out."); }
            catch (Exception e)
            {
                environment.Log("Cannot start Kotlin debugging: " + e.Message);
                await environment.ShowOutputAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                VsShellUtilities.ShowMessageBox(services, e.Message, "Kotlin debugging", OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
            finally
            {
                if (!launched && session != null)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    session.Dispose();
                    session = null;
                }
                if (lease) language.ReleaseDebugSession();
                // A launch that stopped before the debugger took over (build failed, canceled, server not ready).
                if (program != null) End(program);
                launchGate.Release();
            }
        }

        /// <summary>
        /// Makes sure the launch finds the session's daemon running with the launch's JDK. Nothing runs when the build
        /// before the launch just used it; otherwise Gradle's <c>help</c> task starts it (through the package's Gradle
        /// gate, so Cancel Gradle ends it).
        /// </summary>
        private async Task WarmUpAsync(KotlinPackage package, GradleSessionDaemon daemon, string gradleRoot, JObject launch, CancellationToken token)
        {
            var variables = KotlinJvmLaunch.EnvironmentOf(launch);
            if (await Task.Run(() => daemon.IsWarmFor((string?)launch["env"]?["JAVA_HOME"]), token)) return;
            environment.Log("Starting the Gradle daemon of this debug session (the Kotlin server's debugger waits only " + KotlinDebugMonitor.ServerAttachSeconds +
                " s for the program, so Gradle has to be running before the launch)...");
            var started = DateTime.UtcNow;
            var lines = new System.Collections.Generic.List<string>();
            var exit = await package.RunExclusiveGradleAsync(async gradle =>
            {
                using (var both = CancellationTokenSource.CreateLinkedTokenSource(gradle, token))
                    // Off the UI thread from the first statement: the command is built from files before its first await.
                    return await Task.Run(() => daemon.WarmUpAsync(gradleRoot, variables, line => { lock (lines) lines.Add(line); }, both.Token)).ConfigureAwait(false);
            });
            token.ThrowIfCancellationRequested();
            if (exit != 0)
            {
                // Gradle's output is only worth reading when it failed.
                lock (lines) foreach (var line in lines) environment.Log(line);
                throw new InvalidOperationException("Gradle could not start for the debug launch (exit " + exit + "). See Output > Kotlin.");
            }
            environment.Log("Gradle daemon ready (" + (int)(DateTime.UtcNow - started).TotalSeconds + " s).");
        }

        /// <summary>
        /// The end of a program's session: its Gradle daemon ends, with the program when it still runs. A debug session
        /// that ended without the debugger ever attaching gets its reason in the Kotlin Output pane, and a JVM it left
        /// waiting for a debugger outside the daemon is found by its main class and ended.
        /// </summary>
        private void End(LaunchedProgram program)
        {
            program.Monitor.Forget();
            var endedUtc = DateTime.UtcNow;
            // Ending process trees and asking jps never needs the UI thread.
            _ = Task.Run(async () =>
            {
                try
                {
                    var reason = program.StartedUtc == null ? null : program.Monitor.Describe(program.MainClass, program.NoDebug);
                    // Ends the program too when it ran on the session's daemon, attached or not.
                    program.Daemon.Dispose();
                    // A debug launch nobody attached to (the server gave up, or Stop Debugging while Gradle was still
                    // starting it) can have left a JVM outside the session's daemon, when the server's Gradle client
                    // started one of its own. A session whose debugger attached ended its program itself.
                    var unattached = program.StartedUtc != null && !program.NoDebug && program.Monitor.ProgramMayBeLeft;
                    if (unattached && Core.Diagnostics.JvmTools.FindTool("jps", null, program.JavaHome) is string jps)
                    {
                        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                            foreach (var pid in GradleSessionDaemon.ProgramJvms(await Core.Diagnostics.JvmTools.RunAsync(jps, "-l", timeout.Token).ConfigureAwait(false),
                                program.MainClass, program.StartedUtc!.Value, endedUtc, Core.HotReload.HotReloadJvm.StartTimeUtc))
                            {
                                GradleSessionDaemon.EndTree(pid);
                                environment.Log("Kotlin debugging: ended the JVM of " + program.MainClass + " that was still waiting for a debugger (process " + pid + ").");
                            }
                    }
                    if (reason == null) return;
                    environment.Log(reason);
                    await environment.ShowOutputAsync();
                }
                catch (Exception e) when (e is System.IO.IOException || e is InvalidOperationException || e is System.ComponentModel.Win32Exception || e is OperationCanceledException || e is UnauthorizedAccessException)
                {
                    environment.Log("Kotlin debugging: the session's Gradle daemon or program could not be ended cleanly: " + e.Message);
                }
            });
        }

        /// <summary>One program started through the coordinator: the session's Gradle daemon and what is known of the launch.</summary>
        private sealed class LaunchedProgram
        {
            internal LaunchedProgram(GradleSessionDaemon daemon, string mainClass, bool noDebug) { Daemon = daemon; MainClass = mainClass; NoDebug = noDebug; }
            internal GradleSessionDaemon Daemon { get; }
            internal KotlinDebugMonitor Monitor { get; } = KotlinDebugMonitor.Register();
            internal string MainClass { get; }
            internal bool NoDebug { get; }
            internal string? JavaHome;
            /// <summary>Set right before the launch request; null for a launch that never got that far.</summary>
            internal DateTime? StartedUtc;
        }
    }

    internal sealed class KotlinDebugLifecycle : IVsDebuggerEvents, IDisposable
    {
        private readonly IVsDebugger debugger;
        private readonly Action release;
        private uint cookie;
        private bool disposed;
        internal KotlinDebugLifecycle(IVsDebugger debugger, Action release)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.debugger = debugger;
            this.release = release;
            ErrorHandler.ThrowOnFailure(debugger.AdviseDebuggerEvents(this, out cookie));
        }
        public int OnModeChange(DBGMODE mode)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (mode == DBGMODE.DBGMODE_Design) Dispose();
            return VSConstants.S_OK;
        }
        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed) return;
            disposed = true;
            debugger.UnadviseDebuggerEvents(cookie);
            release();
        }
    }
}
