namespace Vistudio.Core.Editor
{
    /// <summary>
    /// The languages whose editors Vistudio itself decorates (Sticky Scroll structure, rainbow braces, scroll bar map and
    /// inlay hint display). C and C++ are not here on purpose: their editors belong to Visual Studio's own C/C++ language
    /// service, which already draws Sticky Scroll, has its own Scroll Bars and Inlay Hints pages under Text Editor &gt; C/C++,
    /// and the T58-T66 creator decision leaves those built-in editor features untouched.
    /// </summary>
    public enum EditorLanguage { Kotlin, Java, Dart, Zig, Rust, Wat }

    /// <summary>
    /// Editor display settings that every language in <see cref="EditorLanguage"/> keeps in its own settings file (Tools
    /// &gt; Options page and <c>vistudio.&lt;language&gt;.*</c> workspace keys). Kotlin and Java share Kotlin's settings, as before.
    /// </summary>
    public interface IEditorSettings
    {
        /// <summary>Matching curly braces share one of six colors (Fonts and Colors &gt; Rainbow brace 1-6).</summary>
        bool RainbowBraces { get; }
        /// <summary>Vertical scroll bar mode of the language's editors. Inherit keeps Text Editor &gt; All Languages &gt; Scroll Bars.</summary>
        ScrollBarMode ScrollBarMode { get; }
        ScrollBarMapWidth ScrollBarMapWidth { get; }
        bool ScrollBarMapPreview { get; }
        /// <summary>When the language's editors show the language server's inlay hints (Visual Studio's own default: while Alt+F1 is held).</summary>
        InlayHintsDisplay InlayHintsDisplay { get; }
    }
}
