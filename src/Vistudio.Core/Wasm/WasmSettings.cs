using Vistudio.Core.Zig;

namespace Vistudio.Core.Wasm
{
    /// <summary>
    /// WebAssembly settings (Tools &gt; Options &gt; WebAssembly), overridden per window by <c>vistudio.wasm.*</c> keys in the
    /// workspace's <c>.vs\settings.json</c>. Coloring comes from Vistudio's WAT grammar and works without a server.
    /// </summary>
    public sealed class WasmSettings : StoredSettings<WasmSettings>, Editor.IEditorSettings
    {
        /// <summary>wat_server.exe from wasm-language-tools (or its folder). Empty searches PATH, then <c>%USERPROFILE%\.cargo\bin</c>.</summary>
        public string LanguageServerPath { get; set; } = "";
        public string LanguageServerArguments { get; set; } = "";
        /// <summary>Type, block-ending and index hints (wasm-language-tools <c>inlayHint.*</c>).</summary>
        public bool InlayHints { get; set; } = true;
        /// <summary>Reference counts above functions.</summary>
        public bool CodeLens { get; set; } = true;
        /// <summary>wasmtime.exe (or its folder) for running modules. Empty searches PATH.</summary>
        public string WasmtimePath { get; set; } = "";
        /// <summary>wasm-tools.exe (or its folder) for converting between text and binary. Empty searches PATH.</summary>
        public string WasmToolsPath { get; set; } = "";

        // Editor display (IEditorSettings), the same settings Kotlin has under Tools > Options > Kotlin > Editor.
        public ScrollBarMode ScrollBarMode { get; set; } = ScrollBarMode.Inherit;
        public ScrollBarMapWidth ScrollBarMapWidth { get; set; } = ScrollBarMapWidth.Medium;
        public bool ScrollBarMapPreview { get; set; } = true;
        public InlayHintsDisplay InlayHintsDisplay { get; set; } = InlayHintsDisplay.Always;
        // Stub, not a setting: the WebAssembly text format is S-expressions and has no curly braces to color (see
        // RainbowBraces.Scan). Explicit, so it is neither stored nor a vistudio.wasm.* workspace key.
        bool Editor.IEditorSettings.RainbowBraces => false;

        public WasmSettings() : base("wasm-settings.xml", WorkspaceSettings.WasmPrefix) { }

        public override void Validate() => ZigArguments.Split(LanguageServerArguments, "wat_server arguments");
    }
}
