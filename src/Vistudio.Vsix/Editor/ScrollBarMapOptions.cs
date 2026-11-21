using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Vistudio.Core;
using Vistudio.Core.Editor;

namespace Vistudio
{
    // Visual Studio has no per-language Scroll Bars page for remote-code content types, so Vistudio sets the map mode
    // options on each view's local options from its language's settings (EditorLanguages). Inherit clears them.
    // C and C++ are not listed: Visual Studio has Text Editor > C/C++ > Scroll Bars for them.
    [Export(typeof(IWpfTextViewCreationListener)), TextViewRole(PredefinedTextViewRoles.Document)]
    [ContentType(KotlinContentTypes.Kotlin), ContentType(JavaContentTypes.Java), ContentType(Dart.DartContentTypes.Dart)]
    [ContentType(Zig.ZigContentTypes.Zig), ContentType(Rust.RustContentTypes.Rust), ContentType(Wasm.WasmContentTypes.Wat)]
    internal sealed class ScrollBarMapOptions : IWpfTextViewCreationListener
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

        private static void Apply(ITextView view)
        {
            if (EditorLanguages.Of(view.TextBuffer.ContentType) is EditorLanguage language) Apply(view, EditorLanguages.Settings(language));
        }

        internal static void Apply(ITextView view, IEditorSettings settings)
        {
            var values = ScrollBarMap.Resolve(settings);
            foreach (var name in ScrollBarMap.OptionNames)
            {
                if (values.TryGetValue(name, out var value)) view.Options.SetOptionValue(name, value);
                else if (view.Options.IsOptionDefined(name, localScopeOnly: true)) view.Options.ClearOptionValue(name);
            }
        }
    }
}
