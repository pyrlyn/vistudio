using System;
using System.Collections.Generic;
using System.Linq;
using Vistudio.Core.Editor;
using Vistudio.Core.Languages;
using Vistudio.Core.Structure;

/// <summary>The language × feature matrix is complete, every gap has a reason, and its editor cells match the scanners.</summary>
internal static class FeatureMatrixTests
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var language in LanguageFeatureMatrix.Languages)
            foreach (var feature in LanguageFeatureMatrix.Features)
            {
                FeatureCell? cell = null;
                try { cell = LanguageFeatureMatrix.Get(language, feature); } catch (ArgumentException) { }
                check(cell != null, "Matrix cell exists: " + language.DisplayName + " / " + feature);
                if (cell != null && (cell.Status == FeatureStatus.Unsupported || cell.Status == FeatureStatus.Open))
                    check(cell.Note.Length > 10, "Stubs and open cells say why: " + language.DisplayName + " / " + feature);
            }
        check(LanguageFeatureMatrix.Languages.Select(l => l.Id).OrderBy(x => x).SequenceEqual(LanguageCatalog.All.Select(l => l.Id).OrderBy(x => x)), "The matrix covers every catalog language");

        var editors = new Dictionary<EditorLanguage, (LanguageInfo Info, string Sample)>
        {
            [EditorLanguage.Kotlin] = (LanguageCatalog.Kotlin, "fun f() {\n}\n"),
            [EditorLanguage.Java] = (LanguageCatalog.Java, "class A {\n}\n"),
            [EditorLanguage.Dart] = (LanguageCatalog.Dart, "void f() {\n}\n"),
            [EditorLanguage.Zig] = (LanguageCatalog.Zig, "fn f() void {\n}\n"),
            [EditorLanguage.Rust] = (LanguageCatalog.Rust, "fn f() {\n}\n"),
            [EditorLanguage.Wat] = (LanguageCatalog.Wasm, "(module\n)\n"),
        };
        check(editors.Count == Enum.GetValues(typeof(EditorLanguage)).Length, "Every editor language is checked");
        foreach (var pair in editors)
        {
            var (info, sample) = pair.Value;
            var structure = LanguageFeatureMatrix.Get(info, LanguageFeatureMatrix.StickyScroll).Status == FeatureStatus.Supported;
            check(structure == (SourceStructure.Scan(pair.Key, sample).Count == 1), info.DisplayName + ": the Sticky Scroll cell matches its scanner");
            var braces = LanguageFeatureMatrix.Get(info, LanguageFeatureMatrix.RainbowBraces).Status == FeatureStatus.Supported;
            check(braces == RainbowBraces.Supports(pair.Key) && braces == (RainbowBraces.Scan(pair.Key, "f() { }").Count == 2), info.DisplayName + ": the rainbow brace cell matches its scanner");
            foreach (var feature in new[] { LanguageFeatureMatrix.ScrollBarMap, LanguageFeatureMatrix.InlayHintsDisplay })
                check(LanguageFeatureMatrix.Get(info, feature).Status == FeatureStatus.Supported, info.DisplayName + ": " + feature + " is set from its IEditorSettings");
        }
        foreach (var native in new[] { LanguageCatalog.C, LanguageCatalog.Cpp })
            check(LanguageFeatureMatrix.Get(native, LanguageFeatureMatrix.StickyScroll).Status == FeatureStatus.VisualStudio, native.DisplayName + " editors stay with Visual Studio's C/C++ service");

        var markdown = LanguageFeatureMatrix.ToMarkdown();
        check(markdown.Split('\n').Count(l => l.StartsWith("| ")) == LanguageFeatureMatrix.Features.Count + 2 && markdown.Contains("Run \\| Debug"), "Markdown has a header, a separator and one row per feature, with | escaped");
    }
}
