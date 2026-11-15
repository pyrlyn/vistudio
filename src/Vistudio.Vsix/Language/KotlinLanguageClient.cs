using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Vistudio.Core;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace Vistudio
{
    [Export(typeof(ILanguageClient)), Export(typeof(KotlinLanguageClient))]
    [ContentType(KotlinContentTypes.Kotlin), PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class KotlinLanguageClient : ILanguageClient, ILanguageClientCustomMessage2
    {
        private readonly KotlinEnvironment environment;
        private readonly SemaphoreSlim processGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim restartGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim completionGate = new SemaphoreSlim(1, 1);
        private OwnedProcess? server;
        private KotlinSystemPathLease? systemPath;
        private Task? errorPump;
        private bool loaded;
        private JsonRpc? rpc;
        private readonly KotlinServerLifecycle lifecycle = new KotlinServerLifecycle();
        private int debugLeases;
        public object? MiddleLayer => new KotlinDebugDocumentFilter();
        public object CustomMessageTarget => new ImportNotifications(this);
        public Task AttachForCustomMessageAsync(JsonRpc jsonRpc) { rpc = jsonRpc; return Task.CompletedTask; }

        private sealed class ImportNotifications
        {
            private readonly KotlinLanguageClient owner;
            public ImportNotifications(KotlinLanguageClient owner) => this.owner = owner;
            [JsonRpcMethod("intellij/importLog", UseSingleObjectParameterDeserialization = true)]
            public void ImportLog(JObject message) => owner.OnImportLog(message);
        }

        private void OnImportLog(JObject message)
        {
            if ((bool?)message["started"] == true) lifecycle.ImportStarted();
            if ((bool?)message["succeeded"] == true) lifecycle.ImportFinished(null);
            if ((bool?)message["failed"] == true) lifecycle.ImportFinished((string?)message["message"] ?? "Gradle import failed.");
        }

        internal async Task<JsonRpc> GetDebugRpcAsync(CancellationToken token)
        {
            if (!loaded) throw new InvalidOperationException("Open a Kotlin source file first to start the Kotlin language server.");
            if (string.IsNullOrWhiteSpace(KotlinSettings.Load().LanguageServerPath))
                throw new InvalidOperationException("The Kotlin language server is not configured. Set Tools > Options > Kotlin > General, then run Tools > Kotlin > Restart Language Server.");
            environment.Log("Waiting for Kotlin server initialization and Gradle import...");
            // Fails immediately when the server process already exited or exits while waiting.
            await lifecycle.WaitForDebugAsync(token).ConfigureAwait(false);
            return rpc ?? throw new InvalidOperationException("Kotlin language server disconnected.");
        }

        /// <summary>True once Visual Studio has loaded this client for an opened Kotlin document.</summary>
        internal bool IsLoaded => Volatile.Read(ref loaded);

        /// <summary>True when the server answers editor requests: initialized and the Gradle model imported.</summary>
        internal bool IsServerReady => rpc != null && lifecycle.IsReady;

        /// <summary>An LSP request for editor features such as CodeLens counts; null until the server is ready.</summary>
        internal async Task<JToken?> RequestAsync(string method, object args, CancellationToken token)
        {
            var connection = rpc;
            if (connection == null || !lifecycle.IsReady) return null;
            return await connection.InvokeWithParameterObjectAsync<JToken?>(method, args, token).ConfigureAwait(false);
        }

        internal void RetainDebugSession() => Interlocked.Increment(ref debugLeases);
        internal void ReleaseDebugSession() => Interlocked.Decrement(ref debugLeases);

        internal async Task<IReadOnlyList<KotlinCallable>> CompleteDebugAsync(KotlinSemanticCompletion document, CancellationToken token)
        {
            var connection = rpc;
            if (connection == null || !lifecycle.IsReady) return Array.Empty<KotlinCallable>();
            // At most one copied source context is open, even when Watch and
            // Immediate race or an older canceled request is still closing.
            await completionGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await document.RequestAsync(
                    (method, args, ct) => connection.InvokeWithParameterObjectAsync<JToken>(method, args, ct),
                    (method, args) => connection.NotifyWithParameterObjectAsync(method, args), token).ConfigureAwait(false);
            }
            finally { completionGate.Release(); }
        }

        [ImportingConstructor]
        public KotlinLanguageClient(KotlinEnvironment environment)
        {
            this.environment = environment;
            environment.WorkspaceChanged += WorkspaceChangedAsync;
        }

        public string Name => "Kotlin (Vistudio)";
        public IEnumerable<string> ConfigurationSections => Array.Empty<string>();
        public object? InitializationOptions => null;
        public IEnumerable<string> FilesToWatch => new[] { "**/*.kt", "**/*.kts", "**/*.java", "**/build.gradle", "**/settings.gradle", "**/gradle.properties", "**/libs.versions.toml" };
        public bool ShowNotificationOnInitializeFailed => true;
        public event AsyncEventHandler<EventArgs>? StartAsync;
        public event AsyncEventHandler<EventArgs>? StopAsync;

        public async Task OnLoadedAsync()
        {
            loaded = true;
            await environment.GetFolderAsync();
            await StartConfiguredAsync();
        }

        private async Task StartConfiguredAsync()
        {
            if (string.IsNullOrWhiteSpace(KotlinSettings.Load().LanguageServerPath))
            {
                environment.Log("LSP is not configured. Set Tools > Options > Kotlin > General, then run Tools > Kotlin > Restart Language Server.");
                return;
            }
            if (await environment.GetFolderAsync() == null) return;
            if (StartAsync != null) await StartAsync.InvokeAsync(this, EventArgs.Empty);
        }

        public async Task<Connection?> ActivateAsync(CancellationToken token)
        {
            var root = await environment.GetFolderAsync();
            if (root == null) throw new InvalidOperationException("Open a Kotlin project with File > Open > Folder before starting LSP.");
            var options = KotlinSettings.Load();
            // Validates the configured launcher before any slot is taken.
            ProcessCommands.LanguageServer(options.LanguageServerPath, options.LanguageServerArguments, root);
            string? javaHome = null;
            if (!string.IsNullOrWhiteSpace(options.LanguageServerJavaHome))
            {
                javaHome = Path.GetFullPath(options.LanguageServerJavaHome);
                if (!File.Exists(Path.Combine(javaHome, "bin", "java.exe"))) throw new DirectoryNotFoundException("LSP JAVA_HOME has no bin\\java.exe.");
            }
            await processGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await CleanupAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                // Kotlin LSP 271 allows one server per --system-path; every Visual Studio instance takes its own slot.
                var lease = systemPath = KotlinSystemPath.Acquire(options.LanguageServerPath, options.LanguageServerArguments,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vistudio", "kotlin-lsp"));
                if (lease.SystemPath != null) environment.Log("Kotlin LSP system path: " + lease.SystemPath + (lease.Slot > 1 ? " (another Visual Studio instance uses the configured one)" : ""));
                var start = ProcessCommands.LanguageServer(options.LanguageServerPath, lease.Arguments, root);
                KotlinServerCompatibility.Configure(start);
                if (javaHome != null) start.EnvironmentVariables["JAVA_HOME"] = javaHome;
                var process = server = new OwnedProcess(start);
                lifecycle.Started(process);
                errorPump = OwnedProcess.PumpAsync(process.Error, line =>
                {
                    lifecycle.ErrorLine(process, line);
                    environment.Log("[LSP] " + line);
                });
                ObserveExitAsync(process).Forget();
                environment.Log("Starting Kotlin LSP in " + root);
                // The SDK's middle layer does not intercept initialize. Normalize its first
                // outbound frame here; all navigation and document traffic stays host-owned.
                var writer = new KotlinInitializeStream(server.Input);
                // The server reads its inlay hint options through workspace/configuration (section jetbrains.kotlin)
                // before each request and returns no hints without them; Visual Studio cannot answer it.
                var reader = new LspConfigurationReader(server.Output, writer,
                    section => section == InlayHints.KotlinSection ? InlayHintConfiguration.KotlinSection(KotlinSettings.Load()) : null,
                    e => environment.Log("Kotlin LSP configuration answer failed: " + e.Message));
                return new Connection(reader, writer);
            }
            finally { processGate.Release(); }
        }

        private async Task ObserveExitAsync(OwnedProcess process)
        {
#pragma warning disable VSTHRD003 // Process completion has no UI-thread dependency.
            try
            {
                var code = await process.Exit.ConfigureAwait(false);
                // Let the stderr pump deliver the last lines (for example "This build has expired") before recording the reason.
                await Task.Delay(200).ConfigureAwait(false);
                var failure = lifecycle.Exited(process, code);
                environment.Log(failure ?? "Kotlin LSP exited with code " + code + ".");
            }
#pragma warning restore VSTHRD003
            catch (Exception e) { environment.Log("LSP process: " + e.Message); }
        }

        public Task OnServerInitializedAsync()
        {
            environment.Log("Kotlin LSP initialized.");
            lifecycle.Initialized();
            return Task.CompletedTask;
        }

        public async Task<InitializationFailureContext?> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo initializationState)
        {
            // A server that died at startup (for example an expired build) is reported with its exit code and stderr,
            // not as a generic initialization failure; the cleanup below would otherwise mark that exit as expected.
            var process = server;
            string? exitFailure = null;
#pragma warning disable VSTHRD003 // Process completion has no UI-thread dependency.
            if (process != null && await Task.WhenAny(process.Exit, Task.Delay(1000)).ConfigureAwait(false) == process.Exit)
            {
                await Task.Delay(200).ConfigureAwait(false); // let the stderr pump deliver the last lines
                exitFailure = lifecycle.Exited(process, await process.Exit.ConfigureAwait(false));
            }
#pragma warning restore VSTHRD003
            if (exitFailure != null) environment.Log(exitFailure);
            else lifecycle.Fail("Kotlin LSP initialization failed. Check the Kotlin Output pane and the configured stdio launcher/JDK, then run Tools > Kotlin > Restart Language Server.");
            await StopProcessAsync().ConfigureAwait(false);
            return new InitializationFailureContext { FailureMessage = "Kotlin LSP initialization failed. Check the Kotlin Output pane and the configured stdio launcher/JDK, then restart the language server." };
        }

        public async Task RestartAsync()
        {
            if (Volatile.Read(ref debugLeases) != 0)
                throw new InvalidOperationException("Stop Kotlin debugging before restarting the language server or changing folders.");
            await restartGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (StopAsync != null)
                {
                    var stop = StopAsync.InvokeAsync(this, EventArgs.Empty);
                    if (await Task.WhenAny(stop, Task.Delay(3000)).ConfigureAwait(false) == stop) await stop.ConfigureAwait(false);
                    else { environment.Log("LSP shutdown timed out; stopping its process."); stop.Forget(); }
                }
                await StopProcessAsync().ConfigureAwait(false);
                if (loaded) await StartConfiguredAsync();
                else environment.Log("Open a .kt or .kts file to activate the language client.");
            }
            finally { restartGate.Release(); }
        }

        private async Task WorkspaceChangedAsync()
        {
            if (!loaded) return;
            if (Volatile.Read(ref debugLeases) != 0)
            {
                environment.Log("Workspace changed: stopping the Kotlin debug session.");
                await environment.StopDebuggingAsync();
                for (var i = 0; i < 30 && Volatile.Read(ref debugLeases) != 0; i++) await Task.Delay(100).ConfigureAwait(false);
                // Closing the owned server also terminates its Gradle/debuggee descendants if the host did not finish.
                await StopProcessAsync().ConfigureAwait(false);
            }
            await RestartAsync();
        }

        public async Task StopProcessAsync()
        {
            await processGate.WaitAsync().ConfigureAwait(false);
            try { await CleanupAsync().ConfigureAwait(false); }
            finally { processGate.Release(); }
        }

        private async Task CleanupAsync()
        {
            try { await StopServerAsync().ConfigureAwait(false); }
            finally
            {
                // The slot is free again once the server process is gone (or never started).
                systemPath?.Dispose();
                systemPath = null;
            }
        }

        private async Task StopServerAsync()
        {
            if (server == null && rpc == null) return;
            rpc = null;
            if (server != null) lifecycle.ExpectExit(server);
            lifecycle.Reset();
            if (server == null) return;
            await server.StopAsync().ConfigureAwait(false);
            if (errorPump != null)
            {
                try { await errorPump.ConfigureAwait(false); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
            server.Dispose();
            server = null;
            errorPump = null;
        }
    }
}
