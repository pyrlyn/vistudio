using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Vistudio.Core.Editor;
using Vistudio.Core.Structure;

namespace Vistudio
{
    /// <summary>
    /// Base of the per-language structure tagger providers. Visual Studio builds Sticky Scroll and structure guide lines
    /// only from <see cref="IStructureTag"/>, which its LSP client never produces, so each language's lexical scanner
    /// (<see cref="SourceStructure"/>) supplies them. One tagger per buffer; the language follows the buffer's content type.
    /// </summary>
    internal abstract class StructureTaggerProvider : ITaggerProvider
    {
        public ITagger<T>? CreateTagger<T>(ITextBuffer buffer) where T : ITag =>
            buffer.Properties.GetOrCreateSingletonProperty(() => new SourceStructureTagger(buffer)) as ITagger<T>;
    }

    /// <summary>Kotlin and Java block structure from <see cref="CodeStructureScanner"/>.</summary>
    [Export(typeof(ITaggerProvider)), TagType(typeof(IStructureTag))]
    [ContentType(KotlinContentTypes.Kotlin), ContentType(JavaContentTypes.Java)]
    internal sealed class JvmStructureTaggerProvider : StructureTaggerProvider { }

    /// <summary>
    /// Tags are not collapsible: outlining stays with the language servers' folding ranges.
    /// </summary>
    internal sealed class SourceStructureTagger : ITagger<IStructureTag>
    {
        private ITextSnapshot? snapshot;
        private IReadOnlyList<CodeBlock> blocks = Array.Empty<CodeBlock>();

        public SourceStructureTagger(ITextBuffer buffer)
        {
            buffer.Changed += (_, e) => TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(e.After, 0, e.After.Length)));
            buffer.ContentTypeChanged += (_, e) => { snapshot = null; TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(e.After, 0, e.After.Length))); };
        }

        public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

        public IEnumerable<ITagSpan<IStructureTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0) yield break;
            var current = spans[0].Snapshot;
            if (current != snapshot)
            {
                blocks = EditorLanguages.Of(current.ContentType) is EditorLanguage language
                    ? SourceStructure.Scan(language, current.GetText())
                    : Array.Empty<CodeBlock>();
                snapshot = current;
            }
            foreach (var block in blocks)
            {
                if (block.CloseBrace + 1 > current.Length || block.HeaderEnd <= block.HeaderStart) continue;
                var extent = Span.FromBounds(block.HeaderStart, block.CloseBrace + 1);
                if (!spans.IntersectsWith(new SnapshotSpan(current, extent))) continue;
                var line = current.GetLineFromPosition(block.HeaderStart);
                // Sticky Scroll pins the primary header; a multi-line primary header makes it build overlapping
                // projection spans ("Attempted to introduce overlapping source spans"), so it is the first line only.
                var primary = Span.FromBounds(block.HeaderStart, Math.Min(block.HeaderEnd, line.End.Position));
                var anchor = line.Start.Position;
                while (anchor < line.End.Position && char.IsWhiteSpace(current[anchor])) anchor++;
                yield return new TagSpan<IStructureTag>(new SnapshotSpan(current, extent), new StructureTag(
                    current,
                    outliningSpan: Span.FromBounds(block.OpenBrace, block.CloseBrace + 1),
                    headerSpan: Span.FromBounds(block.HeaderStart, block.HeaderEnd),
                    guideLineSpan: extent,
                    guideLineHorizontalAnchor: anchor,
                    type: TagType(block.Kind),
                    isCollapsible: false,
                    primaryHeaderSpan: primary));
            }
        }

        private static string TagType(CodeBlockKind kind) => kind switch
        {
            CodeBlockKind.Type => PredefinedStructureTagTypes.Type,
            CodeBlockKind.Member => PredefinedStructureTagTypes.Member,
            CodeBlockKind.Conditional => PredefinedStructureTagTypes.Conditional,
            CodeBlockKind.Loop => PredefinedStructureTagTypes.Loop,
            CodeBlockKind.Statement => PredefinedStructureTagTypes.Statement,
            _ => PredefinedStructureTagTypes.Expression,
        };
    }
}
