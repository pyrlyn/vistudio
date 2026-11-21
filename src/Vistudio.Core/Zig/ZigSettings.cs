namespace Vistudio.Core.Zig
{
    /// <summary>Which zig ast-check messages reach squiggles and the Error List.</summary>
    public enum ZigDiagnosticLevel
    {
        /// <summary>Errors only; notes are appended to the error text.</summary>
        Errors,
        /// <summary>Errors plus each note as its own Error List message at the note's location.</summary>
        ErrorsAndNotes,
    }

    /// <summary>
    /// Zig settings (Tools &gt; Options &gt; Zig). Stored next to, and independent of, the Kotlin settings so the two
    /// languages never overwrite each other.
    /// </summary>
    public sealed class ZigSettings : StoredSettings<ZigSettings>, Editor.IEditorSettings
    {
        /// <summary>Absolute path to zig.exe. Empty searches PATH.</summary>
        public string ZigPath { get; set; } = "";
        /// <summary>Zig library folder (contains <c>std</c>) passed as <c>--zig-lib-dir</c>. Empty uses the one bundled with zig.exe.</summary>
        public string LibDirectory { get; set; } = "";
        /// <summary>Extra compiler flags for Build File and Run, for example <c>-O ReleaseSafe -lc</c>.</summary>
        public string DefaultFlags { get; set; } = "";
        /// <summary>Extra flags for <c>zig build</c>, for example <c>-Doptimize=ReleaseFast</c>.</summary>
        public string BuildFlags { get; set; } = "";
        /// <summary>Build step for Build Project. Empty runs the default step (install).</summary>
        public string BuildStep { get; set; } = "";
        public bool AstCheckOnSave { get; set; } = true;
        /// <summary>Also check the unsaved text after typing stops.</summary>
        public bool AstCheckWhileTyping { get; set; } = true;
        public int AstCheckDelayMilliseconds { get; set; } = 600;
        public ZigDiagnosticLevel DiagnosticLevel { get; set; } = ZigDiagnosticLevel.ErrorsAndNotes;
        /// <summary>Absolute path to a user-installed ZLS executable. Empty disables Zig IntelliSense and navigation.</summary>
        public string LanguageServerPath { get; set; } = "";
        public string LanguageServerArguments { get; set; } = "";
        /// <summary>Show ZLS's own diagnostics as well. Off while ast-check runs, so parse errors are not reported twice.</summary>
        public bool LanguageServerDiagnostics { get; set; }
        public bool CodeLens { get; set; } = true;

        // Editor display (IEditorSettings), the same settings Kotlin has under Tools > Options > Kotlin > Editor.
        public bool RainbowBraces { get; set; } = true;
        public ScrollBarMode ScrollBarMode { get; set; } = ScrollBarMode.Inherit;
        public ScrollBarMapWidth ScrollBarMapWidth { get; set; } = ScrollBarMapWidth.Medium;
        public bool ScrollBarMapPreview { get; set; } = true;
        public InlayHintsDisplay InlayHintsDisplay { get; set; } = InlayHintsDisplay.Always;

        public ZigSettings() : base("zig-settings.xml", WorkspaceSettings.ZigPrefix) { }

        // Zig keeps its own checks: workspace files validate one property at a time (WorkspaceSettings.Validate), while
        // a save also raises a too small delay instead of rejecting it.
        protected override void PrepareSave()
        {
            ZigArguments.Split(DefaultFlags);
            ZigArguments.Split(BuildFlags);
            ZigArguments.Split(LanguageServerArguments);
            if (BuildStep.Length > 0) ZigArguments.ValidateStep(BuildStep);
            if (AstCheckDelayMilliseconds < 100) AstCheckDelayMilliseconds = 100;
        }
    }
}
