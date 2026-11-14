using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;
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
    internal sealed class ZigLanguageClient : StdioLanguageClient
    {
        private readonly ZigEnvironment environment;

        [ImportingConstructor]
        public ZigLanguageClient(ZigEnvironment environment) : base(environment)
        {
            this.environment = environment;
            ZigEnvironment.SettingsChanged += (_, __) => RestartAsync().Forget();
        }

        public override string Name => "Zig (Vistudio, ZLS)";
        // No configuration sections: ZLS reads zls.json (--config-path); see ZigLspCompatibility.
        public override IEnumerable<string> FilesToWatch => new[] { "**/*.zig", "**/*.zon" };
        public override object? MiddleLayer => new ZigDiagnosticsMiddleLayer();
        protected override string ServerLabel => "ZLS";
        protected override string InitializeFailedMessage => "ZLS initialization failed. Check the Zig Output pane and Tools > Options > Zig (ZLS must match the Zig version).";

        // A changed zig.exe path logs its version again on restart.
        protected override void OnStarting() => LogToolchainAsync().Forget();

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

        protected override bool CanStart()
        {
            if (!string.IsNullOrWhiteSpace(ZigSettings.Load().LanguageServerPath)) return true;
            environment.Log("Zig completion and navigation are off: set Tools > Options > Zig > ZLS path to a zls.exe matching your Zig version. Coloring, ast-check and builds work without it.");
            return false;
        }

        protected override LanguageServerLaunch CreateLaunch(string root)
        {
            var settings = ZigSettings.Load();
            var zig = ZigToolchain.FindExecutable(settings);
            if (zig == null) environment.Log("ZLS: zig.exe was not found; set Tools > Options > Zig > zig.exe path for std completion and build.zig modules.");
            var library = ZigToolchain.LibDirectory(settings);
            var start = ZigLanguageServer.Create(settings, zig, library, root, ZigLanguageServer.ConfigFolderFor(WorkspaceSettings.CurrentRoot));
            return new LanguageServerLaunch(start, "Starting ZLS (" + settings.LanguageServerPath + ") in " + root + (zig == null ? "" : " with " + zig),
                ZigLspCompatibility.NormalizeInitialize);
        }

        protected override void OnInitialized() => ZigCodeLensRefresh.InvalidateAll();
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
