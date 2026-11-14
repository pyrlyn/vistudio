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
using Vistudio.Core.Zig;

namespace Vistudio.Zig
{
    /// <summary>
    /// Zig IntelliSense and navigation (completion with keywords, builtins and std/module members, member completion,
    /// signature help, go to definition, find all references, document symbols, hover) from a user-installed ZLS.
    /// Coloring, ast-check diagnostics and builds work without it.
    /// </summary>
    [Export(typeof(ILanguageClient)), Export(typeof(ZigLanguageClient))]
    [ContentType(ZigContentTypes.Zig), PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class ZigLanguageClient : ILanguageClient, ILanguageClientCustomMessage2
    {
        private readonly ZigEnvironment environment;
        private readonly SemaphoreSlim processGate = new SemaphoreSlim(1, 1);
        private OwnedProcess? server;
        private Task? errorPump;
        private bool loaded;
        private JsonRpc? rpc;
        private volatile bool initialized;

        [ImportingConstructor]
        public ZigLanguageClient(ZigEnvironment environment)
        {
            this.environment = environment;
            environment.WorkspaceChanged += OnWorkspaceChangedAsync;
            ZigEnvironment.SettingsChanged += (_, __) => RestartAsync().Forget();
        }

        /// <summary>
        /// Visual Studio waits for workspace-change handlers while it closes a folder, also on exit; restarting ZLS
        /// inside the handler started a new server during shutdown and hung the exit (Visual Studio 18). Closing the
        /// folder keeps the running server (Visual Studio stops it on exit); opening a folder restarts it detached.
        /// </summary>
        private async Task OnWorkspaceChangedAsync()
        {
            if (await environment.GetFolderAsync() == null) return;
            RestartAsync().Forget();
        }

        public string Name => "Zig (Vistudio, ZLS)";
        // No configuration sections: ZLS reads zls.json (--config-path); see ZigLspCompatibility.
        public IEnumerable<string>? ConfigurationSections => null;
        public object? InitializationOptions => null;
        public IEnumerable<string> FilesToWatch => new[] { "**/*.zig", "**/*.zon" };
        public bool ShowNotificationOnInitializeFailed => true;
        public event AsyncEventHandler<EventArgs>? StartAsync;
        public event AsyncEventHandler<EventArgs>? StopAsync;

        public object? MiddleLayer => new ZigDiagnosticsMiddleLayer();
        public object? CustomMessageTarget => null;
        public Task AttachForCustomMessageAsync(JsonRpc jsonRpc) { rpc = jsonRpc; return Task.CompletedTask; }

        /// <summary>True when ZLS answers editor requests (CodeLens counts).</summary>
        internal bool IsServerReady => rpc != null && initialized;

        /// <summary>An LSP request for editor features such as CodeLens counts; null until ZLS is initialized.</summary>
        internal async Task<JToken?> RequestAsync(string method, object args, CancellationToken token)
        {
            var connection = rpc;
            if (connection == null || !initialized) return null;
            return await connection.InvokeWithParameterObjectAsync<JToken?>(method, args, token).ConfigureAwait(false);
        }

        public async Task OnLoadedAsync()
        {
            loaded = true;
            LogToolchainAsync().Forget();
            await StartConfiguredAsync();
        }

        /// <summary>Logs the zig version and std folder from <c>zig env</c> when the first Zig document opens (also without ZLS).</summary>
        private async Task LogToolchainAsync()
        {
            try
            {
                await environment.GetFolderAsync(); // this window's settings first
                if (await environment.GetToolchainAsync(CancellationToken.None).ConfigureAwait(false) == null)
                    environment.Log("zig.exe was not found: set Tools > Options > Zig > zig.exe path, or put zig.exe on PATH.");
            }
            catch (Exception e) { environment.Log("zig env: " + e.Message); }
        }

        private async Task StartConfiguredAsync()
        {
            await environment.GetFolderAsync(); // Applies this window's .vs\settings.json before reading settings.
            if (string.IsNullOrWhiteSpace(ZigSettings.Load().LanguageServerPath))
            {
                environment.Log("Zig completion and navigation are off: set Tools > Options > Zig > ZLS path to a zls.exe matching your Zig version. Coloring, ast-check and builds work without it.");
                return;
            }
            if (StartAsync != null) await StartAsync.InvokeAsync(this, EventArgs.Empty);
        }

        public async Task<Connection?> ActivateAsync(CancellationToken token)
        {
            var root = await environment.GetFolderAsync() ?? Path.GetTempPath();
            var settings = ZigSettings.Load();
            var zig = ZigToolchain.FindExecutable(settings);
            if (zig == null) environment.Log("ZLS: zig.exe was not found; set Tools > Options > Zig > zig.exe path for std completion and build.zig modules.");
            var library = ZigToolchain.LibDirectory(settings);
            var start = ZigLanguageServer.Create(settings, zig, library, root, ZigLanguageServer.ConfigFolderFor(WorkspaceSettings.CurrentRoot));
            await processGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await CleanupAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var process = server = new OwnedProcess(start);
                errorPump = OwnedProcess.PumpAsync(process.Error, line => environment.Log("[ZLS] " + line));
                ObserveExitAsync(process).Forget();
                environment.Log("Starting ZLS (" + settings.LanguageServerPath + ") in " + root + (zig == null ? "" : " with " + zig));
                return new Connection(process.Output, new KotlinInitializeStream(process.Input, ZigLspCompatibility.NormalizeInitialize));
            }
            finally { processGate.Release(); }
        }

        private async Task ObserveExitAsync(OwnedProcess process)
        {
#pragma warning disable VSTHRD003 // Process completion has no UI-thread dependency.
            try { environment.Log("ZLS exited with code " + await process.Exit.ConfigureAwait(false) + "."); }
#pragma warning restore VSTHRD003
            catch (Exception e) { environment.Log("ZLS process: " + e.Message); }
        }

        public Task OnServerInitializedAsync()
        {
            environment.Log("ZLS initialized.");
            initialized = true;
            ZigCodeLensRefresh.InvalidateAll();
            return Task.CompletedTask;
        }

        public async Task<InitializationFailureContext?> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo initializationState)
        {
            await StopProcessAsync().ConfigureAwait(false);
            return new InitializationFailureContext { FailureMessage = "ZLS initialization failed. Check the Zig Output pane and Tools > Options > Zig (ZLS must match the Zig version)." };
        }

        /// <summary>Restarts ZLS after a settings or workspace change (or the Restart command).</summary>
        internal async Task RestartAsync()
        {
            if (!loaded) return;
            LogToolchainAsync().Forget(); // a changed zig.exe path logs its version again
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

    /// <summary>Initialize request adjustments for ZLS under the Visual Studio LSP client.</summary>
    internal static class ZigLspCompatibility
    {
        /// <summary>
        /// ZLS takes its configuration from zls.json (--config-path). Visual Studio would answer
        /// <c>workspace/configuration</c> with nulls, so that capability is not advertised.
        /// </summary>
        internal static JToken NormalizeInitialize(JToken parameters)
        {
            if (!(parameters is JObject root)) return parameters;
            var capabilities = root["capabilities"] as JObject ?? (JObject)(root["capabilities"] = new JObject());
            var workspace = capabilities["workspace"] as JObject ?? (JObject)(capabilities["workspace"] = new JObject());
            workspace["configuration"] = false;
            workspace["didChangeConfiguration"] = new JObject { ["dynamicRegistration"] = false };
            return root;
        }
    }

    /// <summary>
    /// Drops ZLS's own diagnostics while Vistudio runs ast-check (they are the same parse errors), unless enabled.
    /// Also refreshes the CodeLens counts once ZLS has loaded build.zig: before that, references of a declaration do
    /// not include the files that import it (checked with ZLS 0.16.0).
    /// </summary>
    internal sealed class ZigDiagnosticsMiddleLayer : ILanguageClientMiddleLayer2<JToken>
    {
        internal const string BuildLoadedMessage = "Loaded build file";

        public bool CanHandle(string methodName) => methodName == "textDocument/publishDiagnostics" || methodName == "window/logMessage";

        public Task HandleNotificationAsync(string methodName, JToken parameters, Func<JToken, Task> sendNotification)
        {
            if (methodName == "window/logMessage")
            {
                if (((string?)parameters["message"] ?? "").StartsWith(BuildLoadedMessage, StringComparison.Ordinal)) ZigCodeLensRefresh.AfterBuildLoaded();
                return sendNotification(parameters);
            }
            var settings = ZigSettings.Load();
            if (settings.LanguageServerDiagnostics || !(settings.AstCheckOnSave || settings.AstCheckWhileTyping)) return sendNotification(parameters);
            var cleared = (JToken)parameters.DeepClone();
            cleared["diagnostics"] = new JArray();
            return sendNotification(cleared);
        }

        public Task<JToken?> HandleRequestAsync(string methodName, JToken parameters, Func<JToken, Task<JToken?>> sendRequest) => sendRequest(parameters);
    }
}
