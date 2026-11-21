using Microsoft.VisualStudio.Utilities;
using Vistudio.Core;
using Vistudio.Core.Dart;
using Vistudio.Core.Editor;
using Vistudio.Core.Rust;
using Vistudio.Core.Wasm;
using Vistudio.Core.Zig;

namespace Vistudio
{
    /// <summary>
    /// The content types of the <see cref="EditorLanguage"/>s and the settings each one reads its editor display options
    /// from. The editor parts (structure, rainbow braces, scroll bar map, inlay hint display) are exported for these
    /// content types and pick the language from the buffer's content type when they run.
    /// </summary>
    /// <remarks>
    /// Not here, on purpose: C and C++ (Visual Studio's own C/C++ language service owns those editors, see
    /// <see cref="EditorLanguage"/>), Starlark (Bazel build files, outside the eight-language matrix) and WAST, which has
    /// the WAT grammar but no language server and no settings of its own.
    /// </remarks>
    internal static class EditorLanguages
    {
        internal static EditorLanguage? Of(IContentType contentType)
        {
            if (contentType.IsOfType(JavaContentTypes.Java)) return EditorLanguage.Java;
            if (contentType.IsOfType(KotlinContentTypes.Kotlin)) return EditorLanguage.Kotlin;
            if (contentType.IsOfType(Dart.DartContentTypes.Dart)) return EditorLanguage.Dart;
            if (contentType.IsOfType(Zig.ZigContentTypes.Zig)) return EditorLanguage.Zig;
            if (contentType.IsOfType(Rust.RustContentTypes.Rust)) return EditorLanguage.Rust;
            if (contentType.IsOfType(Wasm.WasmContentTypes.Wat)) return EditorLanguage.Wat;
            return null;
        }

        /// <summary>This window's effective settings (Tools &gt; Options overridden by <c>.vs\settings.json</c>).</summary>
        internal static IEditorSettings Settings(EditorLanguage language) => language switch
        {
            EditorLanguage.Dart => DartSettings.Load(),
            EditorLanguage.Zig => ZigSettings.Load(),
            EditorLanguage.Rust => RustSettings.Load(),
            EditorLanguage.Wat => WasmSettings.Load(),
            _ => KotlinSettings.Load(), // Kotlin and Java share Tools > Options > Kotlin
        };

        /// <summary>Reapplies the scroll bar map and inlay hint display to open editors; options pages call it on save.</summary>
        internal static void OnSettingsChanged()
        {
            ScrollBarMapOptions.OnSettingsChanged();
            InlayHintsViewOptions.OnSettingsChanged();
        }
    }
}
