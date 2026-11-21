using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Vistudio.Core.Zig;

namespace Vistudio.Zig
{
    /// <summary>Tools &gt; Options &gt; Zig &gt; General. Persists to <see cref="ZigSettings.FilePath"/>.</summary>
    [ComVisible(true)]
    [Guid("0c4f6a1e-7d0b-4b8e-9a51-2f3e8d6c1b74")]
    public sealed class ZigOptionsPage : DialogPage
    {
        // Global defaults only; a workspace's .vs\settings.json overrides them per window and is never written here.
        private ZigSettings settings = ZigSettings.LoadGlobal();

        [Category("Compiler"), DisplayName("zig.exe path"), Description("Absolute path to zig.exe (or its folder). Empty searches PATH. The version is read from 'zig env' and shown in the Zig Output pane.")]
        public string ZigPath { get => settings.ZigPath; set => settings.ZigPath = value; }
        [Category("Compiler"), DisplayName("Standard library folder"), Description("Zig library folder that contains std\\std.zig, passed as --zig-lib-dir to zig build, build-obj and run, and to ZLS as zig_lib_path. Empty uses the library bundled with zig.exe.")]
        public string LibDirectory { get => settings.LibDirectory; set => settings.LibDirectory = value; }
        [Category("Compiler"), DisplayName("Default flags"), Description("Flags added to Build File (zig build-obj) and Run (zig run), for example -O ReleaseSafe -lc. Double quotes group an argument.")]
        public string DefaultFlags { get => settings.DefaultFlags; set => settings.DefaultFlags = value; }
        [Category("Build"), DisplayName("zig build flags"), Description("Flags added to Build Project (zig build), for example -Doptimize=ReleaseFast.")]
        public string BuildFlags { get => settings.BuildFlags; set => settings.BuildFlags = value; }
        [Category("Build"), DisplayName("Build step"), Description("Step for Build Project; the Zig toolbar drop-down changes it. Empty runs the default step (install).")]
        public string BuildStep { get => settings.BuildStep; set => settings.BuildStep = value; }
        [Category("Diagnostics"), DisplayName("ast-check on save"), Description("Run 'zig ast-check' when a .zig or .zon file is saved.")]
        public bool AstCheckOnSave { get => settings.AstCheckOnSave; set => settings.AstCheckOnSave = value; }
        [Category("Diagnostics"), DisplayName("ast-check while typing"), Description("Also check the unsaved text after typing stops.")]
        public bool AstCheckWhileTyping { get => settings.AstCheckWhileTyping; set => settings.AstCheckWhileTyping = value; }
        [Category("Diagnostics"), DisplayName("Typing delay (ms)"), Description("How long typing must pause before ast-check runs. Minimum 100.")]
        public int AstCheckDelayMilliseconds { get => settings.AstCheckDelayMilliseconds; set => settings.AstCheckDelayMilliseconds = value; }
        [Category("Diagnostics"), DisplayName("Diagnostic output"), Description("Errors: errors only, notes appended to the error text. ErrorsAndNotes: each 'note:' also becomes its own Error List message at its location.")]
        public ZigDiagnosticLevel DiagnosticLevel { get => settings.DiagnosticLevel; set => settings.DiagnosticLevel = value; }
        [Category("Language server (ZLS)"), DisplayName("ZLS path"), Description("Absolute path to a user-installed zls.exe matching your Zig version. Provides completion, signature help, go to definition, references, symbols and CodeLens counts. Empty disables them; coloring, ast-check and builds still work.")]
        public string LanguageServerPath { get => settings.LanguageServerPath; set => settings.LanguageServerPath = value; }
        [Category("Language server (ZLS)"), DisplayName("ZLS arguments"), Description("Extra ZLS arguments, for example --enable-stderr-logs. ZLS restarts when settings are saved.")]
        public string LanguageServerArguments { get => settings.LanguageServerArguments; set => settings.LanguageServerArguments = value; }
        [Category("Language server (ZLS)"), DisplayName("ZLS diagnostics"), Description("Also show ZLS's own diagnostics. Off by default while ast-check runs, so parse errors are not reported twice.")]
        public bool LanguageServerDiagnostics { get => settings.LanguageServerDiagnostics; set => settings.LanguageServerDiagnostics = value; }
        [Category("Editor"), DisplayName("CodeLens"), Description("Reference counts and signatures above Zig functions, Run | Debug above main. Reopen editors after changing.")]
        public bool CodeLens { get => settings.CodeLens; set => settings.CodeLens = value; }
        // Editor display (IEditorSettings), the same options as Tools > Options > Kotlin > Editor.
        [Category("Editor"), DisplayName("Rainbow braces"), Description("Color matching curly braces in Zig editors (Fonts and Colors > Rainbow brace 1-6). Reopen editors after changing this setting.")]
        public bool RainbowBraces { get => settings.RainbowBraces; set => settings.RainbowBraces = value; }
        [Category("Editor"), DisplayName("Scroll bar mode"), Description("Vertical scroll bar in Zig editors. Map shows a minimap of the file colored like the editor, with the visible area, changes, find results, bookmarks, breakpoints and errors. Inherit keeps Text Editor > All Languages > Scroll Bars. Applies to open editors.")]
        public Vistudio.Core.ScrollBarMode ScrollBarMode { get => settings.ScrollBarMode; set => settings.ScrollBarMode = value; }
        [Category("Editor"), DisplayName("Scroll bar map width"), Description("Source overview width when Scroll bar mode is Map. Off keeps map mode marks without the code image.")]
        public Vistudio.Core.ScrollBarMapWidth ScrollBarMapWidth { get => settings.ScrollBarMapWidth; set => settings.ScrollBarMapWidth = value; }
        [Category("Editor"), DisplayName("Scroll bar map preview"), Description("Show a code preview tooltip when hovering the map. Used when Scroll bar mode is Map.")]
        public bool ScrollBarMapPreview { get => settings.ScrollBarMapPreview; set => settings.ScrollBarMapPreview = value; }
        [Category("Editor"), DisplayName("Show inlay hints"), Description("When Zig editors show the language server's inlay hints. Visual Studio's own default shows them only while Alt+F1 is held. Applies to open editors.")]
        public Vistudio.Core.InlayHintsDisplay InlayHintsDisplay { get => settings.InlayHintsDisplay; set => settings.InlayHintsDisplay = value; }

        public override void LoadSettingsFromStorage() => settings = ZigSettings.LoadGlobal();

        public override void SaveSettingsToStorage()
        {
            settings.Save();
            ZigEnvironment.OnSettingsChanged();
            EditorLanguages.OnSettingsChanged();
        }

        public override void ResetSettings() => settings = new ZigSettings();
    }
}
