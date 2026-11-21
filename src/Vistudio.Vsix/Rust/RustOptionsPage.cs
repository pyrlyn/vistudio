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
        // Editor display (IEditorSettings), the same options as Tools > Options > Kotlin > Editor.
        [Category("Editor"), DisplayName("Rainbow braces"), Description("Color matching curly braces in Rust editors (Fonts and Colors > Rainbow brace 1-6). Reopen editors after changing this setting.")]
        public bool RainbowBraces { get => settings.RainbowBraces; set => settings.RainbowBraces = value; }
        [Category("Editor"), DisplayName("Scroll bar mode"), Description("Vertical scroll bar in Rust editors. Map shows a minimap of the file colored like the editor, with the visible area, changes, find results, bookmarks, breakpoints and errors. Inherit keeps Text Editor > All Languages > Scroll Bars. Applies to open editors.")]
        public Vistudio.Core.ScrollBarMode ScrollBarMode { get => settings.ScrollBarMode; set => settings.ScrollBarMode = value; }
        [Category("Editor"), DisplayName("Scroll bar map width"), Description("Source overview width when Scroll bar mode is Map. Off keeps map mode marks without the code image.")]
        public Vistudio.Core.ScrollBarMapWidth ScrollBarMapWidth { get => settings.ScrollBarMapWidth; set => settings.ScrollBarMapWidth = value; }
        [Category("Editor"), DisplayName("Scroll bar map preview"), Description("Show a code preview tooltip when hovering the map. Used when Scroll bar mode is Map.")]
        public bool ScrollBarMapPreview { get => settings.ScrollBarMapPreview; set => settings.ScrollBarMapPreview = value; }
        [Category("Editor"), DisplayName("Show inlay hints"), Description("When Rust editors show the language server's inlay hints. Visual Studio's own default shows them only while Alt+F1 is held. Applies to open editors.")]
        public Vistudio.Core.InlayHintsDisplay InlayHintsDisplay { get => settings.InlayHintsDisplay; set => settings.InlayHintsDisplay = value; }

        public override void LoadSettingsFromStorage() => settings = RustSettings.LoadGlobal();

        public override void SaveSettingsToStorage()
        {
            settings.Save();
            RustLanguageClient.OnSettingsChanged();
            EditorLanguages.OnSettingsChanged();
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
