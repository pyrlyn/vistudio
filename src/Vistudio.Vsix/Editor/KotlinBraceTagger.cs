using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Vistudio.Core;

namespace Vistudio
{
    [Export(typeof(ITaggerProvider)), ContentType(KotlinContentTypes.Kotlin), TagType(typeof(ClassificationTag))]
    internal sealed class KotlinBraceProvider : ITaggerProvider
    {
        [Import] internal IClassificationTypeRegistryService Registry = null!;
        public ITagger<T>? CreateTagger<T>(ITextBuffer buffer) where T : ITag =>
            KotlinSettings.Load().RainbowBraces ? buffer.Properties.GetOrCreateSingletonProperty(() => new KotlinBraceTagger(buffer, Registry)) as ITagger<T> : null;
    }

    internal sealed class KotlinBraceTagger : ITagger<ClassificationTag>
    {
        private readonly ClassificationTag[] tags = new ClassificationTag[KotlinBraceScanner.ColorCount];
        private ITextSnapshot? snapshot;
        private IReadOnlyList<ColoredBrace> braces = Array.Empty<ColoredBrace>();
        public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;
        public KotlinBraceTagger(ITextBuffer buffer, IClassificationTypeRegistryService registry)
        {
            for (int i = 0; i < tags.Length; i++) tags[i] = new ClassificationTag(registry.GetClassificationType("KotlinBrace" + i));
            buffer.Changed += (_, e) => { snapshot = null; TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(e.After, 0, e.After.Length))); };
        }
        public IEnumerable<ITagSpan<ClassificationTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0) yield break;
            if (snapshot != spans[0].Snapshot)
            {
                snapshot = spans[0].Snapshot; braces = KotlinBraceScanner.Scan(snapshot.GetText());
            }
            foreach (var brace in braces)
            {
                var span = new SnapshotSpan(snapshot!, brace.Position, 1);
                if (spans.IntersectsWith(span)) yield return new TagSpan<ClassificationTag>(span, tags[brace.Color]);
            }
        }
    }

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
    internal sealed class BraceFormat0 : ClassificationFormatDefinition { public BraceFormat0() { DisplayName = "Kotlin brace 1"; ForegroundColor = Color.FromRgb(192, 132, 0); } }
    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "KotlinBrace1"), Name("KotlinBrace1"), UserVisible(true), Order(After = Priority.High)]
    internal sealed class BraceFormat1 : ClassificationFormatDefinition { public BraceFormat1() { DisplayName = "Kotlin brace 2"; ForegroundColor = Color.FromRgb(160, 100, 215); } }
    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "KotlinBrace2"), Name("KotlinBrace2"), UserVisible(true), Order(After = Priority.High)]
    internal sealed class BraceFormat2 : ClassificationFormatDefinition { public BraceFormat2() { DisplayName = "Kotlin brace 3"; ForegroundColor = Color.FromRgb(0, 155, 170); } }
    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "KotlinBrace3"), Name("KotlinBrace3"), UserVisible(true), Order(After = Priority.High)]
    internal sealed class BraceFormat3 : ClassificationFormatDefinition { public BraceFormat3() { DisplayName = "Kotlin brace 4"; ForegroundColor = Color.FromRgb(215, 105, 80); } }
    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "KotlinBrace4"), Name("KotlinBrace4"), UserVisible(true), Order(After = Priority.High)]
    internal sealed class BraceFormat4 : ClassificationFormatDefinition { public BraceFormat4() { DisplayName = "Kotlin brace 5"; ForegroundColor = Color.FromRgb(80, 155, 65); } }
    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "KotlinBrace5"), Name("KotlinBrace5"), UserVisible(true), Order(After = Priority.High)]
    internal sealed class BraceFormat5 : ClassificationFormatDefinition { public BraceFormat5() { DisplayName = "Kotlin brace 6"; ForegroundColor = Color.FromRgb(80, 140, 225); } }
}
