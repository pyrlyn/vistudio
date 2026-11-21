using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Vistudio.Core;
using Vistudio.Core.Editor;

namespace Vistudio
{
    // Visual Studio's LSP client draws inlay hints only as its EnableInlayHints editor option allows, and that option
    // defaults to "while pressing Alt+F1". Vistudio sets it on each view's local options from its language's Inlay hints
    // setting (EditorLanguages) and reapplies it to open views when an options page or .vs\settings.json is saved.
    // C and C++ are not listed: their hints come from Visual Studio's C/C++ language service (Text Editor > C/C++ > Inlay Hints).
    [Export(typeof(IWpfTextViewCreationListener)), TextViewRole(PredefinedTextViewRoles.Document)]
    [ContentType(KotlinContentTypes.Kotlin), ContentType(JavaContentTypes.Java), ContentType(Dart.DartContentTypes.Dart)]
    [ContentType(Zig.ZigContentTypes.Zig), ContentType(Rust.RustContentTypes.Rust), ContentType(Wasm.WasmContentTypes.Wat)]
    internal sealed class InlayHintsViewOptions : IWpfTextViewCreationListener
    {
        internal static event EventHandler? SettingsChanged;

        internal static void OnSettingsChanged() => SettingsChanged?.Invoke(null, EventArgs.Empty);

        public void TextViewCreated(IWpfTextView view)
        {
            Apply(view);
            // Raised by the options pages and the workspace settings monitor on the UI thread.
            EventHandler changed = (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); if (!view.IsClosed) Apply(view); };
            SettingsChanged += changed;
            view.Closed += (s, e) => SettingsChanged -= changed;
        }

        // DefaultTextViewOptions.EnableInlayHintsOptionId and its enum InlayHintsEnableKind are not public in the SDK,
        // so the option is set by name with a value of the option's own enum type. Editors without it are left alone.
        internal const string OptionName = "EnableInlayHints";

        private static void Apply(ITextView view)
        {
            if (EditorLanguages.Of(view.TextBuffer.ContentType) is EditorLanguage language) Apply(view, EditorLanguages.Settings(language));
        }

        internal static void Apply(ITextView view, IEditorSettings settings)
        {
            if (!view.Options.IsOptionDefined(OptionName, localScopeOnly: false)) return;
            var current = view.Options.GetOptionValue(OptionName);
            if (current == null || !current.GetType().IsEnum) return;
            view.Options.SetOptionValue(OptionName, Enum.ToObject(current.GetType(), InlayHints.EnableKind(settings.InlayHintsDisplay)));
        }
    }
}
