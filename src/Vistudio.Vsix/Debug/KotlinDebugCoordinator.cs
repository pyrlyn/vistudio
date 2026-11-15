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
        private readonly IServiceProvider services;
        private readonly SemaphoreSlim launchGate = new SemaphoreSlim(1, 1);
        private KotlinDebugLifecycle? session;

        [ImportingConstructor]
        public KotlinDebugCoordinator(KotlinEnvironment environment, KotlinLanguageClient language,
            [Import(typeof(SVsServiceProvider))] IServiceProvider services)
        { this.environment = environment; this.language = language; this.services = services; }

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
                if (buildFirst && !await ((KotlinPackage)package).BuildForDebugAsync(token)) return;
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
                    var settings = await Task.Run(() => KotlinSettings.LoadForFolder(workspace), startup.Token);
                    var launch = KotlinJvmLaunch.Create(workspace, source, mainClass!, config, model, settings.GradleJavaHome);
                    var port = await rpc.InvokeWithParameterObjectAsync<int>("workspace/executeCommand",
                        new { command = "start_debug_server", arguments = new[] { new Uri(workspace.TrimEnd('\\', '/') + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/') } }, startup.Token);
                    if (port < 1 || port > 65535) throw new InvalidOperationException("Kotlin server returned an invalid DAP port.");
                    launch["vistudioPort"] = port;
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(startup.Token);
                    var debugger = services.GetService(typeof(SVsShellDebugger)) as IVsDebugger4 ?? throw new InvalidOperationException("Visual Studio debugger unavailable.");
                    session?.Dispose();
                    session = new KotlinDebugLifecycle((IVsDebugger)debugger, language.ReleaseDebugSession);
                    lease = false;
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
                    Diagnostics.DiagnosticsSessions.WatchJvm(mainClass!, (string?)launch["env"]?["JAVA_HOME"] ?? settings.GradleJavaHome);
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
                launchGate.Release();
            }
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
