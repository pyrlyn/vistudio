using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Vistudio.Core.Wasm;

namespace Vistudio.Wasm
{
    /// <summary>Tools &gt; Options &gt; WebAssembly &gt; General. Persists to <see cref="WasmSettings"/>' file; saving restarts wat_server.</summary>
    [ComVisible(true)]
    [Guid("dec38874-a9cf-47d1-8bef-7494f2b4b184")]
    public sealed class WasmOptionsPage : DialogPage
    {
        // Global defaults only; a workspace's .vs\settings.json overrides them per window and is never written here.
        private WasmSettings settings = WasmSettings.LoadGlobal();

        [Category("wat_server"), DisplayName("wat_server path"), Description("wat_server.exe from wasm-language-tools (or its folder). Empty searches PATH, then %USERPROFILE%\\.cargo\\bin. Provides completion, navigation, hover, diagnostics and inlay hints for .wat files (not .wast).")]
        public string LanguageServerPath { get => settings.LanguageServerPath; set => settings.LanguageServerPath = value; }
        [Category("wat_server"), DisplayName("Arguments"), Description("Extra wat_server arguments. Double quotes group an argument.")]
        public string LanguageServerArguments { get => settings.LanguageServerArguments; set => settings.LanguageServerArguments = value; }
        [Category("Editor"), DisplayName("Inlay hints"), Description("Type, block-ending and index hints from wat_server. Restarts the server.")]
        public bool InlayHints { get => settings.InlayHints; set => settings.InlayHints = value; }
        [Category("Editor"), DisplayName("CodeLens"), Description("Reference counts above functions.")]
        public bool CodeLens { get => settings.CodeLens; set => settings.CodeLens = value; }
        [Category("Tools"), DisplayName("wasmtime path"), Description("wasmtime.exe (or its folder) for running modules. Empty searches PATH.")]
        public string WasmtimePath { get => settings.WasmtimePath; set => settings.WasmtimePath = value; }
        [Category("Tools"), DisplayName("wasm-tools path"), Description("wasm-tools.exe (or its folder) for converting between .wat and .wasm. Empty searches PATH.")]
        public string WasmToolsPath { get => settings.WasmToolsPath; set => settings.WasmToolsPath = value; }

        public override void LoadSettingsFromStorage() => settings = WasmSettings.LoadGlobal();

        public override void SaveSettingsToStorage()
        {
            settings.Save();
            WasmLanguageClient.OnSettingsChanged();
        }

        protected override void OnApply(PageApplyEventArgs e)
        {
            try { settings.Validate(); }
            catch (ArgumentException error)
            {
                e.ApplyBehavior = ApplyKind.CancelNoNavigate;
                VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider, error.Message, "WebAssembly settings", OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }
            base.OnApply(e);
        }

        public override void ResetSettings() => settings = new WasmSettings();
    }
}
