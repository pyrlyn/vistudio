using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Vistudio.Core;
using Vistudio.Core.Editor;

namespace Vistudio
{
    /// <summary>
    /// Rainbow braces for every <see cref="EditorLanguage"/> with curly braces (Kotlin, Java, Dart, Zig, Rust), each from
    /// its own lexer (<see cref="RainbowBraces"/>) and switched by its own "Rainbow braces" setting. WebAssembly text has
    /// no braces (stub in <see cref="RainbowBraces.Scan"/>); C and C++ editors belong to Visual Studio's C/C++ service.
    /// </summary>
    [Export(typeof(ITaggerProvider)), TagType(typeof(ClassificationTag))]
    [ContentType(KotlinContentTypes.Kotlin), ContentType(JavaContentTypes.Java), ContentType(Dart.DartContentTypes.Dart)]
    [ContentType(Zig.ZigContentTypes.Zig), ContentType(Rust.RustContentTypes.Rust)]
    internal sealed class RainbowBraceProvider : ITaggerProvider
    {
        [Import] internal IClassificationTypeRegistryService Registry = null!;

        // Read when the editor opens: reopen editors after changing the setting.
        public ITagger<T>? CreateTagger<T>(ITextBuffer buffer) where T : ITag =>
            EditorLanguages.Of(buffer.ContentType) is EditorLanguage language && RainbowBraces.Supports(language) && EditorLanguages.Settings(language).RainbowBraces
                ? buffer.Properties.GetOrCreateSingletonProperty(() => new RainbowBraceTagger(buffer, Registry)) as ITagger<T>
                : null;
    }

    internal sealed class RainbowBraceTagger : ITagger<ClassificationTag>
    {
        private readonly ClassificationTag[] tags = new ClassificationTag[BracePalette.ColorCount];
        private ITextSnapshot? snapshot;
        private IReadOnlyList<ColoredBrace> braces = Array.Empty<ColoredBrace>();
        public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

        public RainbowBraceTagger(ITextBuffer buffer, IClassificationTypeRegistryService registry)
        {
            for (int i = 0; i < tags.Length; i++) tags[i] = new ClassificationTag(registry.GetClassificationType("KotlinBrace" + i));
            buffer.Changed += (_, e) => { snapshot = null; TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(e.After, 0, e.After.Length))); };
            buffer.ContentTypeChanged += (_, e) => { snapshot = null; TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(e.After, 0, e.After.Length))); };
        }

        public IEnumerable<ITagSpan<ClassificationTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0) yield break;
            if (snapshot != spans[0].Snapshot)
            {
                snapshot = spans[0].Snapshot;
                braces = EditorLanguages.Of(snapshot.ContentType) is EditorLanguage language ? RainbowBraces.Scan(language, snapshot.GetText()) : Array.Empty<ColoredBrace>();
            }
            foreach (var brace in braces)
            {
                var span = new SnapshotSpan(snapshot!, brace.Position, 1);
                if (spans.IntersectsWith(span)) yield return new TagSpan<ClassificationTag>(span, tags[brace.Color]);
            }
        }
    }

    // The classification names keep their original "KotlinBrace" ids so colors users set in Fonts and Colors stay.
    internal static class KotlinBraceTypes
    {
#pragma warning disable CS0649
        [Export, Name("KotlinBrace0"), BaseDefinition("formal language")] internal static ClassificationTypeDefinition? C0;
        [Export, Name("KotlinBrace1"), BaseDefinition("formal language")] internal static ClassificationTypeDefinition? C1;
        [Export, Name("KotlinBrace2"), BaseDefinition("formal language")] internal static ClassificationTypeDefinition? C2;
        [Export, Name("KotlinBrace3"), BaseDefinition("formal language")] internal static ClassificationTypeDefinition? C3;
        [Export, Name("KotlinBrace4"), BaseDefinition("formal language")] internal static ClassificationTypeDefinition? C4;
        [Export, Name("KotlinBrace5"), BaseDefinition("formal language")] internal static ClassificationTypeDefinition? C5;
#pragma warning restore CS0649
    }
    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "KotlinBrace0"), Name("KotlinBrace0"), UserVisible(true), Order(After = Priority.High)]
    internal sealed class BraceFormat0 : ClassificationFormatDefinition { public BraceFormat0() { DisplayName = "Rainbow brace 1"; ForegroundColor = Color.FromRgb(192, 132, 0); } }
    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "KotlinBrace1"), Name("KotlinBrace1"), UserVisible(true), Order(After = Priority.High)]
    internal sealed class BraceFormat1 : ClassificationFormatDefinition { public BraceFormat1() { DisplayName = "Rainbow brace 2"; ForegroundColor = Color.FromRgb(160, 100, 215); } }
    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "KotlinBrace2"), Name("KotlinBrace2"), UserVisible(true), Order(After = Priority.High)]
    internal sealed class BraceFormat2 : ClassificationFormatDefinition { public BraceFormat2() { DisplayName = "Rainbow brace 3"; ForegroundColor = Color.FromRgb(0, 155, 170); } }
    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "KotlinBrace3"), Name("KotlinBrace3"), UserVisible(true), Order(After = Priority.High)]
    internal sealed class BraceFormat3 : ClassificationFormatDefinition { public BraceFormat3() { DisplayName = "Rainbow brace 4"; ForegroundColor = Color.FromRgb(215, 105, 80); } }
    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "KotlinBrace4"), Name("KotlinBrace4"), UserVisible(true), Order(After = Priority.High)]
    internal sealed class BraceFormat4 : ClassificationFormatDefinition { public BraceFormat4() { DisplayName = "Rainbow brace 5"; ForegroundColor = Color.FromRgb(80, 155, 65); } }
    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "KotlinBrace5"), Name("KotlinBrace5"), UserVisible(true), Order(After = Priority.High)]
    internal sealed class BraceFormat5 : ClassificationFormatDefinition { public BraceFormat5() { DisplayName = "Rainbow brace 6"; ForegroundColor = Color.FromRgb(80, 140, 225); } }
}
