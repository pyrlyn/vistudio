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

namespace Vistudio
{
    /// <summary>
    /// Java editing features (completion, navigation, hover, diagnostics) for <c>.java</c> files in a Kotlin/Gradle
    /// workspace, from a user-installed Eclipse JDT LS. The Kotlin server still provides Kotlin, Kotlin-to-Java
    /// navigation and the shared JVM debugger; Visual Studio's built-in TextMate grammar colors Java either way.
    /// </summary>
    [Export(typeof(ILanguageClient)), Export(typeof(JavaLanguageClient))]
    [ContentType(JavaContentTypes.Java), PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class JavaLanguageClient : ILanguageClient, ILanguageClientCustomMessage2
    {
        private readonly KotlinEnvironment environment;
        private readonly SemaphoreSlim processGate = new SemaphoreSlim(1, 1);
        private OwnedProcess? server;
        private Task? errorPump;
        private bool loaded;
        private JsonRpc? rpc;
        private volatile bool initialized;

        public object? MiddleLayer => new InlayHintMiddleLayer();
        public object? CustomMessageTarget => null;
        public Task AttachForCustomMessageAsync(JsonRpc jsonRpc) { rpc = jsonRpc; return Task.CompletedTask; }

        /// <summary>True when JDT LS answers editor requests (CodeLens counts).</summary>
        internal bool IsServerReady => rpc != null && initialized;

        /// <summary>An LSP request for editor features such as CodeLens counts; null until the server is initialized.</summary>
        internal async Task<JToken?> RequestAsync(string method, object args, CancellationToken token)
        {
            var connection = rpc;
            if (connection == null || !initialized) return null;
            return await connection.InvokeWithParameterObjectAsync<JToken?>(method, args, token).ConfigureAwait(false);
        }

        [ImportingConstructor]
        public JavaLanguageClient(KotlinEnvironment environment)
        {
            this.environment = environment;
            environment.WorkspaceChanged += WorkspaceChangedAsync;
        }

        public string Name => "Java (Vistudio, Eclipse JDT LS)";
        public IEnumerable<string> ConfigurationSections => new[] { "java" };
        public object? InitializationOptions => BuildInitializationOptions();
        public IEnumerable<string> FilesToWatch => new[] { "**/*.java", "**/build.gradle", "**/build.gradle.kts", "**/settings.gradle", "**/settings.gradle.kts" };
        public bool ShowNotificationOnInitializeFailed => true;
        public event AsyncEventHandler<EventArgs>? StartAsync;
        public event AsyncEventHandler<EventArgs>? StopAsync;

        public async Task OnLoadedAsync()
        {
            loaded = true;
            await StartConfiguredAsync();
        }

        private async Task StartConfiguredAsync()
        {
            var folder = await environment.GetFolderAsync(); // Applies this window's .vs\settings.json before reading settings.
            if (string.IsNullOrWhiteSpace(KotlinSettings.Load().JavaLanguageServerPath))
            {
                environment.Log("Java completion and navigation are off: set Tools > Options > Kotlin > Java language server to an Eclipse JDT LS folder. Java coloring and debugging work without it.");
                return;
            }
            if (folder == null) return;
            if (StartAsync != null) await StartAsync.InvokeAsync(this, EventArgs.Empty);
        }

        public async Task<Connection?> ActivateAsync(CancellationToken token)
        {
            var root = await environment.GetFolderAsync() ?? throw new InvalidOperationException("Open a Gradle project with File > Open > Folder before starting the Java language server.");
            var options = KotlinSettings.Load();
            var javaHome = JavaLanguageServer.ResolveJavaHome(options.JavaLanguageServerJavaHome, options.LanguageServerJavaHome, options.LanguageServerPath);
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vistudio", "jdtls-workspaces");
            var start = JavaLanguageServer.Create(options.JavaLanguageServerPath, javaHome, root, data);
            await processGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await CleanupAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var process = server = new OwnedProcess(start);
                errorPump = OwnedProcess.PumpAsync(process.Error, line => environment.Log("[Java LSP] " + line));
                ObserveExitAsync(process).Forget();
                environment.Log("Starting Java language server (Eclipse JDT LS) in " + root);
                // JDT LS registers navigation dynamically when allowed; Visual Studio then never enables Go To Definition.
                return new Connection(process.Output, new KotlinInitializeStream(process.Input, JavaLspCompatibility.NormalizeInitialize));
            }
            finally { processGate.Release(); }
        }

        private JObject BuildInitializationOptions()
        {
            var options = KotlinSettings.Load();
            var gradle = new JObject { ["enabled"] = true, ["wrapper"] = new JObject { ["enabled"] = true } };
            // Lets Java resolve Kotlin declarations through the Kotlin compiler's class output (see the script).
            var interop = Path.Combine(Path.GetDirectoryName(typeof(JavaLanguageClient).Assembly.Location)!, "Build", "JavaKotlinInterop.gradle");
            if (File.Exists(interop)) gradle["arguments"] = JavaLanguageServer.InitScriptArgument(interop);
            // Gradle must run on the project's JDK: the JDT LS runtime (21+) can be newer than the wrapper supports.
            if (!string.IsNullOrWhiteSpace(options.GradleJavaHome)) gradle["java"] = new JObject { ["home"] = options.GradleJavaHome };
            return new JObject
            {
                ["settings"] = new JObject
                {
                    ["java"] = new JObject
                    {
                        // Eclipse .project/.classpath/.settings stay in the JDT LS data directory, not in the user's folder.
                        ["import"] = new JObject { ["gradle"] = gradle, ["maven"] = new JObject { ["enabled"] = false }, ["generatesMetadataFilesAtProjectRoot"] = false },
                        ["autobuild"] = new JObject { ["enabled"] = false },
                        ["completion"] = new JObject { ["enabled"] = true },
                        // Parameter names, var/lambda parameter types, format parameters (Tools > Options > Kotlin > Inlay hints).
                        ["inlayHints"] = InlayHintConfiguration.JavaInlayHints(options)
                    }
                },
                ["extendedClientCapabilities"] = new JObject { ["classFileContentsSupport"] = false }
            };
        }

        private async Task ObserveExitAsync(OwnedProcess process)
        {
#pragma warning disable VSTHRD003 // Process completion has no UI-thread dependency.
            try { environment.Log("Java language server exited with code " + await process.Exit.ConfigureAwait(false) + "."); }
#pragma warning restore VSTHRD003
            catch (Exception e) { environment.Log("Java LSP process: " + e.Message); }
        }

        public Task OnServerInitializedAsync()
        {
            environment.Log("Java language server initialized.");
            initialized = true;
            return Task.CompletedTask;
        }

        public async Task<InitializationFailureContext?> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo initializationState)
        {
            await StopProcessAsync().ConfigureAwait(false);
            return new InitializationFailureContext { FailureMessage = "Java language server (Eclipse JDT LS) initialization failed. Check the Kotlin Output pane, the JDT LS folder and its Java 21+ runtime in Tools > Options > Kotlin." };
        }

        /// <summary>Restarts JDT LS with the current settings (for example after the workspace's .vs\settings.json changed).</summary>
        internal Task RestartAsync() => WorkspaceChangedAsync();

        private async Task WorkspaceChangedAsync()
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

    internal static class JavaContentTypes
    {
#pragma warning disable CS0649 // MEF metadata declarations are intentionally unassigned.
        // The content type name is the LSP languageId JDT LS expects. Visual Studio's built-in Java TextMate grammar
        // keeps coloring documents of this remote-code content type, as it does for Kotlin.
        internal const string Java = "java";
        [Export, Name(Java), BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
        internal static ContentTypeDefinition? Definition;
        [Export, FileExtension(".java"), ContentType(Java)]
        internal static FileExtensionToContentTypeDefinition? File;
#pragma warning restore CS0649
    }
}
