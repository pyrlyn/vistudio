using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Vistudio.Core;
using Vistudio.Core.Wasm;

namespace Vistudio.Wasm
{
    /// <summary>
    /// WebAssembly text (.wat) completion, navigation, hover, diagnostics and inlay hints from a user-installed
    /// wat_server (wasm-language-tools). WAST scripts are colored only: the server does not parse them.
    /// </summary>
    [Export(typeof(ILanguageClient)), Export(typeof(WasmLanguageClient))]
    [ContentType(WasmContentTypes.Wat), PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class WasmLanguageClient : StdioLanguageClient
    {
        private static readonly Guid PaneId = new Guid("15353c9e-885c-48e2-8e87-acfa9aee228e");

        /// <summary>Raised after Tools &gt; Options &gt; WebAssembly is saved or the workspace's <c>vistudio.wasm.*</c> keys change.</summary>
        public static event EventHandler? SettingsChanged;

        internal static void OnSettingsChanged()
        {
            SettingsChanged?.Invoke(null, EventArgs.Empty);
            LspLensLanguage.Wat.Refresh.InvalidateAll(); // CodeLens may have been switched on or off
        }

        [ImportingConstructor]
        public WasmLanguageClient([Import(typeof(SVsServiceProvider))] IServiceProvider services, WorkspaceSettingsMonitor workspaceSettings)
            : base(new LanguageEnvironment(services, workspaceSettings, PaneId, "WebAssembly"))
        {
            SettingsChanged += (_, __) => RestartAsync().Forget();
        }

        public override string Name => "WebAssembly (Vistudio, wat_server)";
        public override IEnumerable<string> FilesToWatch => new[] { "**/*.wat" };
        protected override string ServerLabel => "wat_server";
        protected override string InitializeFailedMessage => "wat_server failed to initialize. Check the WebAssembly Output pane and Tools > Options > WebAssembly.";

        protected override bool CanStart()
        {
            var settings = WasmSettings.Load();
            if (WasmLanguageServer.Find(settings) != null) return true;
            Environment.Log(string.IsNullOrWhiteSpace(settings.LanguageServerPath)
                ? "WebAssembly completion and diagnostics are off: wat_server was not found on PATH or in %USERPROFILE%\\.cargo\\bin. Install wasm-language-tools or set Tools > Options > WebAssembly > wat_server path. Coloring works without it."
                : "WebAssembly completion and diagnostics are off: the configured wat_server does not exist: " + settings.LanguageServerPath);
            return false;
        }

        protected override string DefaultRoot => WorkspaceSettings.CurrentRoot ?? Path.GetTempPath();

        protected override LanguageServerLaunch CreateLaunch(string root)
        {
            var settings = WasmSettings.Load();
            var server = WasmLanguageServer.Find(settings) ?? throw new FileNotFoundException("wat_server was not found; see Tools > Options > WebAssembly.");
            return new LanguageServerLaunch(WasmLanguageServer.Create(server, settings, root), "Starting wat_server (" + server + ") in " + root,
                parameters => LspInitialize.WithRoot(LspInitialize.WithPulledConfiguration(parameters), root),
                // wat_server ignores initializationOptions and asks for this section per opened document.
                section => section == WasmLanguageServer.ConfigurationSection ? WasmLanguageServer.Configuration(WasmSettings.Load()) : null);
        }

        protected override void OnInitialized() => LspLensLanguage.Wat.Refresh.InvalidateAll();
    }
}
