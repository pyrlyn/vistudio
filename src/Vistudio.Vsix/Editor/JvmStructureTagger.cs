using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Vistudio.Core.Structure;

namespace Vistudio
{
    /// <summary>
    /// Block structure for Kotlin and Java editors. Visual Studio builds Sticky Scroll and structure guide lines only
    /// from <see cref="IStructureTag"/>, which the LSP client never produces, so the lexical scanner supplies it.
    /// Tags are not collapsible: outlining stays with the language servers' folding ranges.
    /// </summary>
    [Export(typeof(ITaggerProvider)), TagType(typeof(IStructureTag))]
    [ContentType(KotlinContentTypes.Kotlin), ContentType(JavaContentTypes.Java)]
    internal sealed class JvmStructureTaggerProvider : ITaggerProvider
    {
        public ITagger<T>? CreateTagger<T>(ITextBuffer buffer) where T : ITag =>
            buffer.Properties.GetOrCreateSingletonProperty(() => new JvmStructureTagger(buffer)) as ITagger<T>;
    }

    internal sealed class JvmStructureTagger : ITagger<IStructureTag>
    {
        private ITextSnapshot? snapshot;
        private IReadOnlyList<CodeBlock> blocks = Array.Empty<CodeBlock>();

        public JvmStructureTagger(ITextBuffer buffer)
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
                blocks = CodeStructureScanner.Scan(current.GetText(), current.ContentType.IsOfType(JavaContentTypes.Java));
                snapshot = current;
            }
            foreach (var block in blocks)
            {
                if (block.CloseBrace + 1 > current.Length) continue;
                var extent = Span.FromBounds(block.HeaderStart, block.CloseBrace + 1);
                if (!spans.IntersectsWith(new SnapshotSpan(current, extent))) continue;
                var header = Span.FromBounds(block.HeaderStart, block.HeaderEnd);
                var line = current.GetLineFromPosition(block.HeaderStart);
                // Sticky Scroll pins the primary header; a multi-line primary header makes it build overlapping
                // projection spans ("Attempted to introduce overlapping source spans"), so it is the first line only.
                var primary = Span.FromBounds(block.HeaderStart, Math.Min(block.HeaderEnd, line.End.Position));
                var anchor = line.Start.Position;
                while (anchor < line.End.Position && char.IsWhiteSpace(current[anchor])) anchor++;
                var tag = new StructureTag(
                    current,
                    outliningSpan: Span.FromBounds(block.OpenBrace, block.CloseBrace + 1),
                    headerSpan: header,
                    guideLineSpan: extent,
                    guideLineHorizontalAnchor: anchor,
                    type: TagType(block.Kind),
                    isCollapsible: false,
                    primaryHeaderSpan: primary);
                yield return new TagSpan<IStructureTag>(new SnapshotSpan(current, extent), tag);
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
