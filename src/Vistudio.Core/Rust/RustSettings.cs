using Vistudio.Core.Zig;

namespace Vistudio.Core.Rust
{
    /// <summary>
    /// Rust settings (Tools &gt; Options &gt; Rust), overridden per window by <c>vistudio.rust.*</c> keys in the workspace's
    /// <c>.vs\settings.json</c>. Coloring comes from Visual Studio's own Rust grammar and works without a server.
    /// </summary>
    public sealed class RustSettings : StoredSettings<RustSettings>, Editor.IEditorSettings
    {
        /// <summary>rust-analyzer.exe (or its folder). Empty searches PATH, then <c>%USERPROFILE%\.cargo\bin</c>.</summary>
        public string LanguageServerPath { get; set; } = "";
        public string LanguageServerArguments { get; set; } = "";
        /// <summary>Type, parameter-name, chaining and closing-brace hints (rust-analyzer <c>inlayHints.*</c>).</summary>
        public bool InlayHints { get; set; } = true;
        /// <summary>Reference counts above declarations and Run | Debug above <c>fn main</c>.</summary>
        public bool CodeLens { get; set; } = true;

        // Editor display (IEditorSettings), the same settings Kotlin has under Tools > Options > Kotlin > Editor.
        public bool RainbowBraces { get; set; } = true;
        public ScrollBarMode ScrollBarMode { get; set; } = ScrollBarMode.Inherit;
        public ScrollBarMapWidth ScrollBarMapWidth { get; set; } = ScrollBarMapWidth.Medium;
        public bool ScrollBarMapPreview { get; set; } = true;
        public InlayHintsDisplay InlayHintsDisplay { get; set; } = InlayHintsDisplay.Always;

        public RustSettings() : base("rust-settings.xml", WorkspaceSettings.RustPrefix) { }

        public override void Validate() => ZigArguments.Split(LanguageServerArguments, "rust-analyzer arguments");
    }
}
