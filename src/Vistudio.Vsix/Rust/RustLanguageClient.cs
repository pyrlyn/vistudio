using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Vistudio.Core;
using Vistudio.Core.Rust;

namespace Vistudio.Rust
{
    /// <summary>
    /// Rust completion, navigation, hover, diagnostics (cargo check on save) and inlay hints from a user-installed
    /// rust-analyzer. Visual Studio's LSP client draws the inlay hints; coloring comes from Visual Studio's grammar.
    /// </summary>
    [Export(typeof(ILanguageClient)), Export(typeof(RustLanguageClient))]
    [ContentType(RustContentTypes.Rust), PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class RustLanguageClient : StdioLanguageClient
    {
        private static readonly Guid PaneId = new Guid("61962964-8b31-4f9c-9f73-f2cfaa09e39e");

        /// <summary>Raised after Tools &gt; Options &gt; Rust is saved or the workspace's <c>vistudio.rust.*</c> keys change.</summary>
        public static event EventHandler? SettingsChanged;

        internal static void OnSettingsChanged()
        {
            SettingsChanged?.Invoke(null, EventArgs.Empty);
            LspLensLanguage.Rust.Refresh.InvalidateAll(); // CodeLens may have been switched on or off
        }

        [ImportingConstructor]
        public RustLanguageClient([Import(typeof(SVsServiceProvider))] IServiceProvider services, WorkspaceSettingsMonitor workspaceSettings)
            : base(new LanguageEnvironment(services, workspaceSettings, PaneId, "Rust"))
        {
            SettingsChanged += (_, __) => RestartAsync().Forget();
        }

        /// <summary>The "Rust" Output pane, also used by the cargo run lens.</summary>
        internal LanguageEnvironment Output => (LanguageEnvironment)Environment;

        public override string Name => "Rust (Vistudio, rust-analyzer)";
        public override object? InitializationOptions => RustLanguageServer.Configuration(RustSettings.Load());
        public override IEnumerable<string> FilesToWatch => new[] { "**/*.rs", "**/Cargo.toml", "**/Cargo.lock", "**/rust-project.json" };
        protected override string ServerLabel => "rust-analyzer";
        protected override string InitializeFailedMessage => "rust-analyzer failed to initialize. Check the Rust Output pane and Tools > Options > Rust.";

        protected override bool CanStart()
        {
            var settings = RustSettings.Load();
            if (RustLanguageServer.Find(settings) != null) return true;
            Environment.Log(string.IsNullOrWhiteSpace(settings.LanguageServerPath)
                ? "Rust completion and navigation are off: rust-analyzer was not found on PATH or in %USERPROFILE%\\.cargo\\bin. Install it (rustup component add rust-analyzer) or set Tools > Options > Rust > rust-analyzer path. Coloring works without it."
                : "Rust completion and navigation are off: the configured rust-analyzer does not exist: " + settings.LanguageServerPath);
            return false;
        }

        protected override string DefaultRoot => WorkspaceSettings.CurrentRoot ?? Path.GetTempPath();

        protected override LanguageServerLaunch CreateLaunch(string root)
        {
            var settings = RustSettings.Load();
            var server = RustLanguageServer.Find(settings) ?? throw new FileNotFoundException("rust-analyzer was not found; see Tools > Options > Rust.");
            return new LanguageServerLaunch(RustLanguageServer.Create(server, settings, root), "Starting rust-analyzer (" + server + ") in " + root,
                parameters => LspInitialize.WithRoot(LspInitialize.WithPulledConfiguration(parameters), root),
                // rust-analyzer asks for its section after initialize; the answer repeats the initialization options.
                section => section == RustLanguageServer.ConfigurationSection ? RustLanguageServer.Configuration(RustSettings.Load()) : null);
        }

        protected override void OnInitialized() => LspLensLanguage.Rust.Refresh.InvalidateAll();
    }
}
