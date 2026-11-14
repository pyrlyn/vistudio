using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Vistudio.Core.Rust;

namespace Vistudio.Rust
{
    /// <summary>Tools &gt; Options &gt; Rust &gt; General. Persists to <see cref="RustSettings"/>' file; saving restarts rust-analyzer.</summary>
    [ComVisible(true)]
    [Guid("3e477e11-cda3-4fc9-a2bc-3288201dd1bb")]
    public sealed class RustOptionsPage : DialogPage
    {
        // Global defaults only; a workspace's .vs\settings.json overrides them per window and is never written here.
        private RustSettings settings = RustSettings.LoadGlobal();

        [Category("rust-analyzer"), DisplayName("rust-analyzer path"), Description("rust-analyzer.exe (or its folder). Empty searches PATH, then %USERPROFILE%\\.cargo\\bin (rustup component add rust-analyzer). Provides completion, navigation, hover, diagnostics and inlay hints.")]
        public string LanguageServerPath { get => settings.LanguageServerPath; set => settings.LanguageServerPath = value; }
        [Category("rust-analyzer"), DisplayName("Arguments"), Description("Extra rust-analyzer arguments, for example --log-file C:\\temp\\ra.log. Double quotes group an argument.")]
        public string LanguageServerArguments { get => settings.LanguageServerArguments; set => settings.LanguageServerArguments = value; }
        [Category("Editor"), DisplayName("Inlay hints"), Description("Type, parameter-name, method-chain and closing-brace hints from rust-analyzer. Restarts the server.")]
        public bool InlayHints { get => settings.InlayHints; set => settings.InlayHints = value; }
        [Category("Editor"), DisplayName("CodeLens"), Description("Reference counts above functions, methods, structs, enums and traits; Run | Debug above fn main.")]
        public bool CodeLens { get => settings.CodeLens; set => settings.CodeLens = value; }

        public override void LoadSettingsFromStorage() => settings = RustSettings.LoadGlobal();

        public override void SaveSettingsToStorage()
        {
            settings.Save();
            RustLanguageClient.OnSettingsChanged();
        }

        protected override void OnApply(PageApplyEventArgs e)
        {
            try { settings.Validate(); }
            catch (ArgumentException error)
            {
                e.ApplyBehavior = ApplyKind.CancelNoNavigate;
                VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider, error.Message, "Rust settings", OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }
            base.OnApply(e);
        }

        public override void ResetSettings() => settings = new RustSettings();
    }
}
