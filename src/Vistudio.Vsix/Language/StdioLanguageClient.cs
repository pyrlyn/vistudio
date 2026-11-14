using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Vistudio.Core;

namespace Vistudio
{
    /// <summary>How one server process starts: its command, the Output pane line, and the initialize/configuration handling.</summary>
    internal sealed class LanguageServerLaunch
    {
        public LanguageServerLaunch(ProcessStartInfo start, string description, Func<JToken, JToken> normalizeInitialize, Func<string, JToken?>? configuration = null)
        {
            Start = start; Description = description; NormalizeInitialize = normalizeInitialize; Configuration = configuration;
        }

        public ProcessStartInfo Start { get; }
        public string Description { get; }
        /// <summary>Adjusts the initialize request (the SDK's middle layer does not see it).</summary>
        public Func<JToken, JToken> NormalizeInitialize { get; }
        /// <summary>Answers <c>workspace/configuration</c> for owned sections (null leaves Visual Studio's null answer); null when the server asks for none.</summary>
        public Func<string, JToken?>? Configuration { get; }
    }

    /// <summary>
    /// A user-installed language server on stdio under Visual Studio's LSP client: one owned process at a time, restart
    /// on settings and workspace changes, stderr in the language's Output pane, and <see cref="RequestAsync"/> for editor
    /// features the LSP client does not draw (CodeLens). Derived clients supply the launch and their messages.
    /// </summary>
    internal abstract class StdioLanguageClient : ILanguageClient, ILanguageClientCustomMessage2
    {
        private readonly SemaphoreSlim processGate = new SemaphoreSlim(1, 1);
        private OwnedProcess? server;
        private Task? errorPump;
        private bool loaded;
        private JsonRpc? rpc;
        private volatile bool initialized;

        protected StdioLanguageClient(ILanguageEnvironment environment)
        {
            Environment = environment;
            environment.WorkspaceChanged += OnWorkspaceChangedAsync;
        }

        protected ILanguageEnvironment Environment { get; }

        /// <summary>Short server name in Output pane lines: "[ZLS] ...", "ZLS exited with code 1.".</summary>
        protected abstract string ServerLabel { get; }
        /// <summary>Shown by Visual Studio when initialize fails.</summary>
        protected abstract string InitializeFailedMessage { get; }

        /// <summary>
        /// Visual Studio waits for workspace-change handlers while it closes a folder, also on exit; restarting the server
        /// inside the handler started a new server during shutdown and hung the exit (Visual Studio 18, seen with ZLS).
        /// Closing the folder keeps the running server (Visual Studio stops it on exit); opening a folder restarts it detached.
        /// </summary>
        private async Task OnWorkspaceChangedAsync()
        {
            if (await Environment.GetFolderAsync() == null) return;
            RestartAsync().Forget();
        }

        public abstract string Name { get; }
        public virtual IEnumerable<string>? ConfigurationSections => null;
        public virtual object? InitializationOptions => null;
        public abstract IEnumerable<string> FilesToWatch { get; }
        public bool ShowNotificationOnInitializeFailed => true;
        public event AsyncEventHandler<EventArgs>? StartAsync;
        public event AsyncEventHandler<EventArgs>? StopAsync;

        public virtual object? MiddleLayer => null;
        public object? CustomMessageTarget => null;
        public Task AttachForCustomMessageAsync(JsonRpc jsonRpc) { rpc = jsonRpc; return Task.CompletedTask; }

        /// <summary>True when the server answers editor requests (CodeLens counts).</summary>
        internal bool IsServerReady => rpc != null && initialized;

        /// <summary>An LSP request for editor features such as CodeLens counts; null until the server is initialized.</summary>
        internal async Task<JToken?> RequestAsync(string method, object args, CancellationToken token)
        {
            var connection = rpc;
            if (connection == null || !initialized) return null;
            return await connection.InvokeWithParameterObjectAsync<JToken?>(method, args, token).ConfigureAwait(false);
        }

        /// <summary>Runs on the first document and before every restart, also when no server is configured.</summary>
        protected virtual void OnStarting() { }

        /// <summary>Applies this window's settings first; returns false (after logging why) when no server should start.</summary>
        protected abstract bool CanStart();

        /// <summary>Working folder without an Open Folder workspace.</summary>
        protected virtual string DefaultRoot => Path.GetTempPath();

        /// <summary>The server command for <paramref name="root"/> (the Open Folder root, else <see cref="DefaultRoot"/>).</summary>
        protected abstract LanguageServerLaunch CreateLaunch(string root);

        /// <summary>After initialize succeeded, for example to refresh CodeLens.</summary>
        protected virtual void OnInitialized() { }

        public async Task OnLoadedAsync()
        {
            loaded = true;
            OnStarting();
            await StartConfiguredAsync();
        }

        private async Task StartConfiguredAsync()
        {
            await Environment.GetFolderAsync(); // Applies this window's .vs\settings.json before reading settings.
            if (!CanStart()) return;
            if (StartAsync != null) await StartAsync.InvokeAsync(this, EventArgs.Empty);
        }

        public async Task<Connection?> ActivateAsync(CancellationToken token)
        {
            var root = await Environment.GetFolderAsync() ?? DefaultRoot;
            var launch = CreateLaunch(root);
            await processGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await CleanupAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var process = server = new OwnedProcess(launch.Start);
                errorPump = OwnedProcess.PumpAsync(process.Error, line => Environment.Log("[" + ServerLabel + "] " + line));
                ObserveExitAsync(process).Forget();
                Environment.Log(launch.Description);
                var writer = new KotlinInitializeStream(process.Input, launch.NormalizeInitialize);
                // Visual Studio answers workspace/configuration only from Open Folder settings; owned sections are answered here.
                var reader = launch.Configuration == null ? process.Output
                    : new LspConfigurationReader(process.Output, writer, launch.Configuration, e => Environment.Log(ServerLabel + " configuration answer failed: " + e.Message));
                return new Connection(reader, writer);
            }
            finally { processGate.Release(); }
        }

        private async Task ObserveExitAsync(OwnedProcess process)
        {
#pragma warning disable VSTHRD003 // Process completion has no UI-thread dependency.
            try { Environment.Log(ServerLabel + " exited with code " + await process.Exit.ConfigureAwait(false) + "."); }
#pragma warning restore VSTHRD003
            catch (Exception e) { Environment.Log(ServerLabel + " process: " + e.Message); }
        }

        public Task OnServerInitializedAsync()
        {
            Environment.Log(ServerLabel + " initialized.");
            initialized = true;
            OnInitialized();
            return Task.CompletedTask;
        }

        public async Task<InitializationFailureContext?> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo initializationState)
        {
            await StopProcessAsync().ConfigureAwait(false);
            return new InitializationFailureContext { FailureMessage = InitializeFailedMessage };
        }

        /// <summary>Restarts the server after a settings or workspace change (or a Restart command).</summary>
        internal async Task RestartAsync()
        {
            if (!loaded) return;
            OnStarting();
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

    /// <summary>Initialize request adjustments shared by the servers Vistudio configures itself.</summary>
    internal static class LspInitialize
    {
        /// <summary>
        /// Advertises pulled configuration (Vistudio answers the server's sections through <see cref="LspConfigurationReader"/>)
        /// without dynamic didChangeConfiguration registration: a settings change restarts the server instead. Snippets are
        /// switched off because Visual Studio inserts the snippet text literally (<c>${1:name}</c>), as for Dart.
        /// </summary>
        internal static JToken WithPulledConfiguration(JToken parameters)
        {
            if (!(parameters is JObject root)) return parameters;
            var capabilities = root["capabilities"] as JObject ?? (JObject)(root["capabilities"] = new JObject());
            var workspace = capabilities["workspace"] as JObject ?? (JObject)(capabilities["workspace"] = new JObject());
            workspace["configuration"] = true;
            workspace["didChangeConfiguration"] = new JObject { ["dynamicRegistration"] = false };
            if (capabilities["textDocument"]?["completion"]?["completionItem"] is JObject item) item["snippetSupport"] = false;
            return root;
        }

        /// <summary>
        /// Gives the server a workspace root when Visual Studio sent none (a solution rather than Open Folder): servers such
        /// as rust-analyzer discover projects only below their roots. The temp folder is never offered as a root.
        /// </summary>
        internal static JToken WithRoot(JToken parameters, string folder)
        {
            if (!(parameters is JObject root) || root["rootUri"] is JValue { Type: JTokenType.String }) return parameters;
            string full;
            try { full = Path.GetFullPath(folder).TrimEnd('\\', '/'); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { return parameters; }
            if (string.Equals(full, Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return parameters;
            var uri = new Uri(full + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
            root["rootUri"] = uri;
            root["rootPath"] = full;
            if (!(root["workspaceFolders"] is JArray folders) || folders.Count == 0)
                root["workspaceFolders"] = new JArray(new JObject { ["uri"] = uri, ["name"] = Path.GetFileName(full) });
            return root;
        }
    }
}
