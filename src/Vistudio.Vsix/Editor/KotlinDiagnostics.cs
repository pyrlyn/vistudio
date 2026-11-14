using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Vistudio.Core;

namespace Vistudio
{
    // Updated on the UI thread with the same batch as Error List.
    internal static class KotlinDiagnosticStore
    {
        internal static BuildDiagnostic[] Items { get; private set; } = Array.Empty<BuildDiagnostic>();
        internal static DateTime BuildStartedUtc { get; private set; }
        internal static event EventHandler? Changed;
        internal static void Publish(IEnumerable<BuildDiagnostic> diagnostics, DateTime buildStartedUtc = default)
        {
            BuildStartedUtc = buildStartedUtc;
            Items = diagnostics.ToArray(); Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    [Export(typeof(IViewTaggerProvider)), ContentType(KotlinContentTypes.Kotlin), TagType(typeof(IErrorTag)), TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class KotlinBuildDiagnosticProvider : IViewTaggerProvider
    {
        [Import] internal ITextDocumentFactoryService Documents = null!;
        public ITagger<T>? CreateTagger<T>(ITextView view, ITextBuffer buffer) where T : ITag
        {
            if (buffer != view.TextBuffer || !KotlinSettings.Load().EditorDiagnostics || !Documents.TryGetTextDocument(buffer, out var document)) return null;
            return new KotlinBuildDiagnosticTagger(view, document) as ITagger<T>;
        }
    }

    internal sealed class KotlinBuildDiagnosticTagger : ITagger<IErrorTag>, IDisposable
    {
        private readonly ITextView view;
        private readonly ITextDocument document;
        private bool stale;
        public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;
        public KotlinBuildDiagnosticTagger(ITextView view, ITextDocument document)
        {
            this.view = view; this.document = document; stale = IsStale();
            KotlinDiagnosticStore.Changed += Published;
            view.TextBuffer.Changed += Edited; view.Closed += Closed;
        }
        private bool IsStale()
        {
            try { return document.IsDirty || File.GetLastWriteTimeUtc(document.FilePath) > KotlinDiagnosticStore.BuildStartedUtc; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }
        private void Published(object? sender, EventArgs e) { stale = IsStale(); Invalidate(); }
        private void Edited(object? sender, TextContentChangedEventArgs e) { stale = true; Invalidate(); }
        private void Invalidate() => TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(view.TextSnapshot, 0, view.TextSnapshot.Length)));
        public IEnumerable<ITagSpan<IErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (stale || spans.Count == 0) yield break;
            var snapshot = spans[0].Snapshot;
            foreach (var diagnostic in KotlinDiagnosticStore.Items)
            {
                if (!string.Equals(diagnostic.File, document.FilePath, StringComparison.OrdinalIgnoreCase) || diagnostic.Line < 0 || diagnostic.Line >= snapshot.LineCount) continue;
                var line = snapshot.GetLineFromLineNumber(diagnostic.Line);
                int column = Math.Max(0, Math.Min(diagnostic.Column, Math.Max(0, line.Length - 1)));
                var span = new SnapshotSpan(line.Start + column, line.Length == 0 ? 0 : 1);
                if (spans.IntersectsWith(span))
                    yield return new TagSpan<IErrorTag>(span, new ErrorTag(diagnostic.Severity == BuildDiagnosticSeverity.Error ? PredefinedErrorTypeNames.SyntaxError : PredefinedErrorTypeNames.Warning, diagnostic.Message));
            }
        }
        private void Closed(object? sender, EventArgs e) => Dispose();
        public void Dispose()
        {
            KotlinDiagnosticStore.Changed -= Published; view.TextBuffer.Changed -= Edited; view.Closed -= Closed;
        }
    }
}
