using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Vistudio.Core;
using Vistudio.Core.Zig;

namespace Vistudio.Zig
{
    /// <summary>Attaches one <see cref="ZigAstCheckSession"/> to every <c>.zig</c>/<c>.zon</c> document opened in an editor.</summary>
    // Matched on "text" plus the file name: the `zig` content type can be assigned after the view opens.
    [Export(typeof(IWpfTextViewCreationListener)), ContentType("text"), TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class ZigAstCheckListener : IWpfTextViewCreationListener
    {
        [Import] internal ITextDocumentFactoryService Documents = null!;
        [Import] internal ZigEnvironment Environment = null!;
        [Import] internal ZigErrorList ErrorList = null!;

        public void TextViewCreated(IWpfTextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var buffer = view.TextDataModel.DocumentBuffer;
            if (!Documents.TryGetTextDocument(buffer, out var document) || !ZigCommandFactory.IsZigFile(document.FilePath)) return;
            var session = ZigAstCheckSession.For(buffer, () => new ZigAstCheckSession(buffer, document, Environment, ErrorList));
            session.AddView(view);
        }
    }

    /// <summary>
    /// Runs <c>zig ast-check</c> for one document: on open, on save, and (optionally) after typing pauses, always on the
    /// current text through stdin. Results feed the squiggle tagger and the Error List. Never runs <c>zig build</c>.
    /// </summary>
    internal sealed class ZigAstCheckSession : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
        private readonly ITextBuffer buffer;
        private readonly ITextDocument document;
        private readonly ZigEnvironment environment;
        private readonly ZigErrorList errorList;
        private readonly object gate = new object();
        private CancellationTokenSource? pending;
        private int views;
        private bool missingZigReported;

        internal ZigAstCheckSession(ITextBuffer buffer, ITextDocument document, ZigEnvironment environment, ZigErrorList errorList)
        {
            this.buffer = buffer; this.document = document; this.environment = environment; this.errorList = errorList;
            buffer.Changed += Edited;
            document.FileActionOccurred += FileAction;
            ZigEnvironment.SettingsChanged += SettingsChanged;
        }

        internal static ZigAstCheckSession For(ITextBuffer buffer, Func<ZigAstCheckSession> create) =>
            buffer.Properties.GetOrCreateSingletonProperty(typeof(ZigAstCheckSession), create);

        internal static ZigAstCheckSession? Get(ITextBuffer buffer) =>
            buffer.Properties.TryGetProperty(typeof(ZigAstCheckSession), out ZigAstCheckSession session) ? session : null;

        /// <summary>Latest results on the snapshot they were computed for.</summary>
        internal IReadOnlyList<(ITrackingSpan Span, ZigDiagnostic Diagnostic)> Results { get; private set; } = Array.Empty<(ITrackingSpan, ZigDiagnostic)>();
        internal event EventHandler? ResultsChanged;

        internal void AddView(ITextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            views++;
            view.Closed += (_, __) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (--views == 0) Dispose();
            };
            var settings = ZigSettings.Load();
            if (views == 1 && (settings.AstCheckOnSave || settings.AstCheckWhileTyping)) Schedule(TimeSpan.Zero);
        }

        /// <summary>Runs ast-check now (the Zig: Run ast-check command), regardless of the automatic settings.</summary>
        internal void CheckNow() => Schedule(TimeSpan.Zero);

        private void Edited(object? sender, TextContentChangedEventArgs e)
        {
            var settings = ZigSettings.Load();
            if (settings.AstCheckWhileTyping) Schedule(TimeSpan.FromMilliseconds(Math.Max(100, settings.AstCheckDelayMilliseconds)));
        }

        private void FileAction(object? sender, TextDocumentFileActionEventArgs e)
        {
            if ((e.FileActionType & FileActionTypes.ContentSavedToDisk) != 0 && ZigSettings.Load().AstCheckOnSave) Schedule(TimeSpan.Zero);
            if ((e.FileActionType & FileActionTypes.DocumentRenamed) != 0) Schedule(TimeSpan.Zero);
        }

        private void SettingsChanged(object? sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // raised by the options page
            var settings = ZigSettings.Load();
            if (settings.AstCheckOnSave || settings.AstCheckWhileTyping) Schedule(TimeSpan.Zero);
            else Publish(buffer.CurrentSnapshot, Array.Empty<ZigDiagnostic>());
        }

        private void Schedule(TimeSpan delay)
        {
            CancellationTokenSource source;
            lock (gate)
            {
                pending?.Cancel();
                pending = source = new CancellationTokenSource();
            }
            var snapshot = buffer.CurrentSnapshot;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, source.Token).ConfigureAwait(false);
                    await RunAsync(buffer.CurrentSnapshot, source.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { environment.Log("ast-check " + Path.GetFileName(document.FilePath) + ": " + e.Message); }
            });
        }

        private async Task RunAsync(ITextSnapshot snapshot, CancellationToken token)
        {
            var settings = ZigSettings.Load();
            var zig = ZigToolchain.FindExecutable(settings);
            if (zig == null)
            {
                if (!missingZigReported) environment.Log("ast-check is off: zig.exe was not found. Set Tools > Options > Zig > zig.exe path.");
                missingZigReported = true;
                return;
            }
            missingZigReported = false;
            var file = document.FilePath;
            var start = ZigCommandFactory.AstCheck(zig, file);
            var output = new StringBuilder();
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(Timeout);
                using (var process = new OwnedProcess(start))
                using (timeout.Token.Register(() => { try { process.StopAsync().Forget(); } catch (Exception) { } }))
                {
                    var stdout = Task.Run(async () => { using (var reader = new StreamReader(process.Output, Encoding.UTF8)) return await reader.ReadToEndAsync().ConfigureAwait(false); });
                    var stderr = process.Error.ReadToEndAsync();
                    var bytes = new UTF8Encoding(false).GetBytes(snapshot.GetText());
                    try
                    {
                        await process.Input.WriteAsync(bytes, 0, bytes.Length, timeout.Token).ConfigureAwait(false);
                        process.Input.Close();
                    }
                    catch (IOException) { } // the compiler stops reading after a fatal error
#pragma warning disable VSTHRD003 // Process output tasks have no UI-thread dependency.
                    output.Append(await stdout.ConfigureAwait(false)).Append(await stderr.ConfigureAwait(false));
                    await process.Exit.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                }
                token.ThrowIfCancellationRequested();
                if (timeout.IsCancellationRequested) throw new TimeoutException("zig ast-check did not finish within " + Timeout.TotalSeconds + " s.");
            }
            var parsed = ZigDiagnosticParser.Parse(output.ToString(), start.WorkingDirectory, file);
            // Only this document's messages: ast-check reports nothing else, but keep foreign paths out of squiggles.
            var diagnostics = ZigDiagnosticParser.Flatten(parsed, settings.DiagnosticLevel)
                .Where(d => !d.HasLocation || string.Equals(d.File, file, StringComparison.OrdinalIgnoreCase)).ToList();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            Publish(snapshot, diagnostics);
        }

        private void Publish(ITextSnapshot snapshot, IReadOnlyList<ZigDiagnostic> diagnostics)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var results = new List<(ITrackingSpan, ZigDiagnostic)>();
            foreach (var diagnostic in diagnostics)
            {
                var line = snapshot.GetLineFromLineNumber(Math.Max(0, Math.Min(diagnostic.HasLocation ? diagnostic.Line : 0, snapshot.LineCount - 1)));
                var start = line.Start.Position + Math.Max(0, Math.Min(diagnostic.HasLocation ? diagnostic.Column : 0, line.Length));
                var end = start;
                while (end < line.End.Position && (char.IsLetterOrDigit(snapshot[end]) || snapshot[end] == '_' || snapshot[end] == '@')) end++;
                if (end == start && start < line.End.Position) end = start + 1;
                results.Add((snapshot.CreateTrackingSpan(Span.FromBounds(start, end), SpanTrackingMode.EdgeExclusive), diagnostic));
            }
            Results = results;
            ResultsChanged?.Invoke(this, EventArgs.Empty);
            errorList.PublishAstCheck(document.FilePath, diagnostics);
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // the last view closed
            lock (gate) { pending?.Cancel(); pending = null; }
            buffer.Changed -= Edited;
            document.FileActionOccurred -= FileAction;
            ZigEnvironment.SettingsChanged -= SettingsChanged;
            buffer.Properties.RemoveProperty(typeof(ZigAstCheckSession));
            errorList.ClearAstCheck(document.FilePath);
        }
    }

    /// <summary>Squiggles for ast-check results; spans track edits until the next check replaces them.</summary>
    [Export(typeof(IViewTaggerProvider)), ContentType("text"), TagType(typeof(IErrorTag)), TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class ZigErrorTaggerProvider : IViewTaggerProvider
    {
        [Import] internal ITextDocumentFactoryService Documents = null!;

        public ITagger<T>? CreateTagger<T>(ITextView view, ITextBuffer buffer) where T : ITag
        {
            if (buffer != view.TextDataModel.DocumentBuffer || !Documents.TryGetTextDocument(buffer, out var document) || !ZigCommandFactory.IsZigFile(document.FilePath)) return null;
            return new ZigErrorTagger(view, buffer) as ITagger<T>;
        }
    }

    internal sealed class ZigErrorTagger : ITagger<IErrorTag>, IDisposable
    {
        private readonly ITextView view;
        private readonly ITextBuffer buffer;
        private ZigAstCheckSession? session;
        public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

        public ZigErrorTagger(ITextView view, ITextBuffer buffer)
        {
            this.view = view; this.buffer = buffer;
            view.Closed += Closed;
            Attach();
            // The session is created by the view listener, possibly after this tagger.
            if (session == null) buffer.Changed += AttachLate;
        }

        private void Attach()
        {
            session = ZigAstCheckSession.Get(buffer);
            if (session != null) session.ResultsChanged += Changed;
        }

        private void AttachLate(object? sender, TextContentChangedEventArgs e)
        {
            if (session != null) { buffer.Changed -= AttachLate; return; }
            Attach();
            if (session != null) buffer.Changed -= AttachLate;
        }

        private void Changed(object? sender, EventArgs e)
        {
            var snapshot = buffer.CurrentSnapshot;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }

        public IEnumerable<ITagSpan<IErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0) yield break;
            if (session == null) { Attach(); if (session == null) yield break; buffer.Changed -= AttachLate; }
            var snapshot = spans[0].Snapshot;
            foreach (var (tracking, diagnostic) in session.Results)
            {
                var span = tracking.GetSpan(snapshot);
                if (!spans.IntersectsWith(span) && !(span.Length == 0 && spans.Any(s => s.Contains(span.Start) || s.End == span.Start))) continue;
                var type = diagnostic.Severity switch
                {
                    ZigDiagnosticSeverity.Error => PredefinedErrorTypeNames.SyntaxError,
                    ZigDiagnosticSeverity.Warning => PredefinedErrorTypeNames.Warning,
                    _ => PredefinedErrorTypeNames.HintedSuggestion,
                };
                yield return new TagSpan<IErrorTag>(span, new ErrorTag(type, "zig ast-check: " + diagnostic.Message));
            }
        }

        private void Closed(object? sender, EventArgs e) => Dispose();

        public void Dispose()
        {
            view.Closed -= Closed;
            buffer.Changed -= AttachLate;
            if (session != null) session.ResultsChanged -= Changed;
        }
    }
}
