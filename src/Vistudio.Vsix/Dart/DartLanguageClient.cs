using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Vistudio.Core;
using Vistudio.Core.Dart;

namespace Vistudio.Dart
{
    /// <summary>
    /// Dart IntelliSense, navigation and diagnostics from the Dart analysis server of the configured SDK
    /// (<c>dart language-server</c>): completion, signature help, hover, go to definition, find all references,
    /// document symbols, and errors/warnings/lints as squiggles and Error List entries. Coloring works without it.
    /// </summary>
    [Export(typeof(ILanguageClient)), Export(typeof(DartLanguageClient))]
    [ContentType(DartContentTypes.Dart), PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class DartLanguageClient : ILanguageClient, ILanguageClientCustomMessage2
    {
        private readonly DartEnvironment environment;
        private readonly SemaphoreSlim processGate = new SemaphoreSlim(1, 1);
        private OwnedProcess? server;
        private Task? errorPump;
        private bool loaded;
        private JsonRpc? rpc;
        private volatile bool initialized;

        [ImportingConstructor]
        public DartLanguageClient(DartEnvironment environment)
        {
            this.environment = environment;
            environment.WorkspaceChanged += OnWorkspaceChangedAsync;
            DartEnvironment.SettingsChanged += (_, __) => RestartAsync().Forget();
        }

        // Same rule as the Zig client: never restart inside the close-folder handler (it runs during shutdown).
        private async Task OnWorkspaceChangedAsync()
        {
            if (await environment.GetFolderAsync() == null) return;
            RestartAsync().Forget();
        }

        public string Name => "Dart (Vistudio, analysis server)";
        public IEnumerable<string>? ConfigurationSections => null;
        /// <summary>Analysis server options: analyze the whole workspace (Error List shows every file), suggest unimported libraries.</summary>
        public object? InitializationOptions => new JObject
        {
            ["onlyAnalyzeProjectsWithOpenFiles"] = false,
            ["suggestFromUnimportedLibraries"] = true,
            ["closingLabels"] = false,
            ["outline"] = false,
            ["flutterOutline"] = false,
        };
        public IEnumerable<string> FilesToWatch => new[] { "**/*.dart", "**/pubspec.yaml", "**/analysis_options.yaml", "**/.dart_tool/package_config.json" };
        public bool ShowNotificationOnInitializeFailed => true;
        public event AsyncEventHandler<EventArgs>? StartAsync;
        public event AsyncEventHandler<EventArgs>? StopAsync;

        public object? MiddleLayer => null;
        public object? CustomMessageTarget => null;
        public Task AttachForCustomMessageAsync(JsonRpc jsonRpc) { rpc = jsonRpc; return Task.CompletedTask; }

        internal bool IsServerReady => rpc != null && initialized;

        /// <summary>An LSP request for editor features such as CodeLens counts; null until the server is initialized.</summary>
        internal async Task<JToken?> RequestAsync(string method, object args, CancellationToken token)
        {
            var connection = rpc;
            if (connection == null || !initialized) return null;
            return await connection.InvokeWithParameterObjectAsync<JToken?>(method, args, token).ConfigureAwait(false);
        }

        public async Task OnLoadedAsync()
        {
            loaded = true;
            await StartConfiguredAsync();
        }

        private async Task StartConfiguredAsync()
        {
            await environment.GetFolderAsync(); // Applies this window's .vs\settings.json before reading settings.
            if (!DartSettings.Load().LanguageServer)
            {
                environment.Log("The Dart analysis server is off (Tools > Options > Dart > Analysis server). Coloring, run, test and debug still work.");
                return;
            }
            try { await environment.RequireSdkAsync(); }
            catch (Exception e)
            {
                environment.Log("Dart IntelliSense is off: " + e.Message);
                return;
            }
            if (StartAsync != null) await StartAsync.InvokeAsync(this, EventArgs.Empty);
        }

        public async Task<Connection?> ActivateAsync(CancellationToken token)
        {
            var root = await environment.GetFolderAsync() ?? Path.GetTempPath();
            var settings = DartSettings.Load();
            var sdk = await environment.RequireSdkAsync();
            var start = DartCommandFactory.LanguageServer(sdk, root, settings.LanguageServerArguments);
            await processGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await CleanupAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var process = server = new OwnedProcess(start);
                errorPump = OwnedProcess.PumpAsync(process.Error, line => environment.Log("[analysis server] " + line));
                ObserveExitAsync(process).Forget();
                environment.Log("Starting the Dart analysis server in " + root);
                return new Connection(process.Output, new KotlinInitializeStream(process.Input, DartLspCompatibility.NormalizeInitialize));
            }
            finally { processGate.Release(); }
        }

        private async Task ObserveExitAsync(OwnedProcess process)
        {
#pragma warning disable VSTHRD003 // Process completion has no UI-thread dependency.
            try { environment.Log("The Dart analysis server exited with code " + await process.Exit.ConfigureAwait(false) + "."); }
#pragma warning restore VSTHRD003
            catch (Exception e) { environment.Log("Dart analysis server process: " + e.Message); }
        }

        public Task OnServerInitializedAsync()
        {
            environment.Log("Dart analysis server initialized.");
            initialized = true;
            DartCodeLensRefresh.InvalidateAll();
            return Task.CompletedTask;
        }

        public async Task<InitializationFailureContext?> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo initializationState)
        {
            await StopProcessAsync().ConfigureAwait(false);
            return new InitializationFailureContext { FailureMessage = "The Dart analysis server failed to initialize. Check the Dart Output pane and Tools > Options > Dart." };
        }

        /// <summary>Restarts the analysis server after a settings or workspace change (or the Restart command).</summary>
        internal async Task RestartAsync()
        {
            if (!loaded) return;
            if (StopAsync != null)
            {
                var stop = StopAsync.InvokeAsync(this, EventArgs.Empty);
                if (await Task.WhenAny(stop, Task.Delay(3000)).ConfigureAwait(false) == stop) await stop.ConfigureAwait(false);
                else stop.Forget();
            }
            await StopProcessAsync().ConfigureAwait(false);
            await StartConfiguredAsync();
        }

        private async Task StopProcessAsync()
        {
            await processGate.WaitAsync().ConfigureAwait(false);
            try { await CleanupAsync().ConfigureAwait(false); }
            finally { processGate.Release(); }
        }

        private async Task CleanupAsync()
        {
            initialized = false;
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

    /// <summary>Initialize request adjustments for the Dart analysis server under the Visual Studio LSP client.</summary>
    internal static class DartLspCompatibility
    {
        /// <summary>
        /// Visual Studio answers <c>workspace/configuration</c> with nulls when no sections are declared, so that capability
        /// is not advertised and the server keeps its defaults. Snippets are switched off because Visual Studio inserts
        /// the snippet text literally (<c>${1:name}</c>).
        /// </summary>
        internal static JToken NormalizeInitialize(JToken parameters)
        {
            if (!(parameters is JObject root)) return parameters;
            var capabilities = root["capabilities"] as JObject ?? (JObject)(root["capabilities"] = new JObject());
            var workspace = capabilities["workspace"] as JObject ?? (JObject)(capabilities["workspace"] = new JObject());
            workspace["configuration"] = false;
            workspace["didChangeConfiguration"] = new JObject { ["dynamicRegistration"] = false };
            if (capabilities["textDocument"]?["completion"]?["completionItem"] is JObject item) item["snippetSupport"] = false;
            // The analysis server registers inlay hints dynamically when the client says it can, and only then
            // advertises nothing in its static capabilities; static registration is what Visual Studio's client
            // reliably acts on (the same reason Java declares dynamic registration unsupported).
            if (capabilities["textDocument"]?["inlayHint"] is JObject inlayHint) inlayHint["dynamicRegistration"] = false;
            return root;
        }
    }
}
