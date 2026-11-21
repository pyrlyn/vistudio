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
        // Editor display (IEditorSettings), the same options as Tools > Options > Kotlin > Editor.
        [Category("Editor"), DisplayName("Scroll bar mode"), Description("Vertical scroll bar in WebAssembly text editors. Map shows a minimap of the file colored like the editor, with the visible area, changes, find results, bookmarks, breakpoints and errors. Inherit keeps Text Editor > All Languages > Scroll Bars. Applies to open editors.")]
        public Vistudio.Core.ScrollBarMode ScrollBarMode { get => settings.ScrollBarMode; set => settings.ScrollBarMode = value; }
        [Category("Editor"), DisplayName("Scroll bar map width"), Description("Source overview width when Scroll bar mode is Map. Off keeps map mode marks without the code image.")]
        public Vistudio.Core.ScrollBarMapWidth ScrollBarMapWidth { get => settings.ScrollBarMapWidth; set => settings.ScrollBarMapWidth = value; }
        [Category("Editor"), DisplayName("Scroll bar map preview"), Description("Show a code preview tooltip when hovering the map. Used when Scroll bar mode is Map.")]
        public bool ScrollBarMapPreview { get => settings.ScrollBarMapPreview; set => settings.ScrollBarMapPreview = value; }
        [Category("Editor"), DisplayName("Show inlay hints"), Description("When WebAssembly text editors show the language server's inlay hints. Visual Studio's own default shows them only while Alt+F1 is held. Applies to open editors.")]
        public Vistudio.Core.InlayHintsDisplay InlayHintsDisplay { get => settings.InlayHintsDisplay; set => settings.InlayHintsDisplay = value; }
        [Category("Tools"), DisplayName("wasmtime path"), Description("wasmtime.exe (or its folder) for running modules. Empty searches PATH.")]
        public string WasmtimePath { get => settings.WasmtimePath; set => settings.WasmtimePath = value; }
        [Category("Tools"), DisplayName("wasm-tools path"), Description("wasm-tools.exe (or its folder) for converting between .wat and .wasm. Empty searches PATH.")]
        public string WasmToolsPath { get => settings.WasmToolsPath; set => settings.WasmToolsPath = value; }

        public override void LoadSettingsFromStorage() => settings = WasmSettings.LoadGlobal();

        public override void SaveSettingsToStorage()
        {
            settings.Save();
            WasmLanguageClient.OnSettingsChanged();
            EditorLanguages.OnSettingsChanged();
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
