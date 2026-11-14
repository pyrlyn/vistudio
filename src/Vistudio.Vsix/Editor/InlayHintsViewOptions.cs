using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Vistudio.Core;

namespace Vistudio
{
    // Visual Studio's LSP client draws inlay hints only as its EnableInlayHints editor option allows, and that option
    // defaults to "while pressing Alt+F1". Vistudio sets it on each Kotlin/Java view's local options from
    // Tools > Options > Kotlin > Inlay hints, and reapplies it to open views when the options page is saved.
    [Export(typeof(IWpfTextViewCreationListener)), ContentType(KotlinContentTypes.Kotlin), ContentType(JavaContentTypes.Java), TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class InlayHintsViewOptions : IWpfTextViewCreationListener
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

        // DefaultTextViewOptions.EnableInlayHintsOptionId and its enum InlayHintsEnableKind are not public in the SDK,
        // so the option is set by name with a value of the option's own enum type. Editors without it are left alone.
        internal const string OptionName = "EnableInlayHints";

        internal static void Apply(ITextView view, KotlinSettings settings)
        {
            if (!view.Options.IsOptionDefined(OptionName, localScopeOnly: false)) return;
            var current = view.Options.GetOptionValue(OptionName);
            if (current == null || !current.GetType().IsEnum) return;
            view.Options.SetOptionValue(OptionName, Enum.ToObject(current.GetType(), InlayHints.EnableKind(settings.InlayHints)));
        }
    }
}
