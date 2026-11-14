using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Vistudio.Core;

namespace Vistudio
{
    // Visual Studio has no per-language Scroll Bars page for remote-code content types, so Vistudio
    // sets the map mode options on each Kotlin/Java view's local options. Inherit clears them.
    [Export(typeof(IWpfTextViewCreationListener)), ContentType(KotlinContentTypes.Kotlin), ContentType(JavaContentTypes.Java), TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class ScrollBarMapOptions : IWpfTextViewCreationListener
    {
        internal static event EventHandler? SettingsChanged;

        internal static void OnSettingsChanged() => SettingsChanged?.Invoke(null, EventArgs.Empty);

        public void TextViewCreated(IWpfTextView view)
        {
            Apply(view, KotlinSettings.Load());
            // Raised by the options page on the UI thread.
            EventHandler changed = (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); if (!view.IsClosed) Apply(view, KotlinSettings.Load()); };
            SettingsChanged += changed;
            view.Closed += (s, e) => SettingsChanged -= changed;
        }

        internal static void Apply(ITextView view, KotlinSettings settings)
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
