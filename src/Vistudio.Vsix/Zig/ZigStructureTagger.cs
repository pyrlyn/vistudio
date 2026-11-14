using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Vistudio.Core.Structure;
using Vistudio.Core.Zig;

namespace Vistudio.Zig
{
    /// <summary>
    /// Block structure for Zig editors (Sticky Scroll headers and structure guide lines), from the lexical
    /// <see cref="ZigSourceScanner"/>. Not collapsible: outlining comes from ZLS folding ranges, as for Kotlin/Java.
    /// </summary>
    [Export(typeof(ITaggerProvider)), TagType(typeof(IStructureTag)), ContentType(ZigContentTypes.Zig)]
    internal sealed class ZigStructureTaggerProvider : ITaggerProvider
    {
        public ITagger<T>? CreateTagger<T>(ITextBuffer buffer) where T : ITag =>
            buffer.Properties.GetOrCreateSingletonProperty(() => new ZigStructureTagger(buffer)) as ITagger<T>;
    }

    internal sealed class ZigStructureTagger : ITagger<IStructureTag>
    {
        private ITextSnapshot? snapshot;
        private IReadOnlyList<CodeBlock> blocks = Array.Empty<CodeBlock>();

        public ZigStructureTagger(ITextBuffer buffer) =>
            buffer.Changed += (_, e) => TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(e.After, 0, e.After.Length)));

        public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

        public IEnumerable<ITagSpan<IStructureTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0) yield break;
            var current = spans[0].Snapshot;
            if (current != snapshot)
            {
                blocks = ZigSourceScanner.Scan(current.GetText()).Blocks;
                snapshot = current;
            }
            foreach (var block in blocks)
            {
                if (block.CloseBrace + 1 > current.Length || block.HeaderEnd <= block.HeaderStart) continue;
                var extent = Span.FromBounds(block.HeaderStart, block.CloseBrace + 1);
                if (!spans.IntersectsWith(new SnapshotSpan(current, extent))) continue;
                var line = current.GetLineFromPosition(block.HeaderStart);
                // A multi-line primary header makes Sticky Scroll build overlapping projection spans: first line only.
                var primary = Span.FromBounds(block.HeaderStart, Math.Min(block.HeaderEnd, line.End.Position));
                var anchor = line.Start.Position;
                while (anchor < line.End.Position && char.IsWhiteSpace(current[anchor])) anchor++;
                yield return new TagSpan<IStructureTag>(new SnapshotSpan(current, extent), new StructureTag(
                    current,
                    outliningSpan: Span.FromBounds(block.OpenBrace, block.CloseBrace + 1),
                    headerSpan: Span.FromBounds(block.HeaderStart, block.HeaderEnd),
                    guideLineSpan: extent,
                    guideLineHorizontalAnchor: anchor,
                    type: block.Kind switch
                    {
                        CodeBlockKind.Type => PredefinedStructureTagTypes.Type,
                        CodeBlockKind.Member => PredefinedStructureTagTypes.Member,
                        CodeBlockKind.Conditional => PredefinedStructureTagTypes.Conditional,
                        CodeBlockKind.Loop => PredefinedStructureTagTypes.Loop,
                        CodeBlockKind.Statement => PredefinedStructureTagTypes.Statement,
                        _ => PredefinedStructureTagTypes.Expression,
                    },
                    isCollapsible: false,
                    primaryHeaderSpan: primary));
            }
        }
    }
}
