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
using Vistudio.Core.Bazel;

namespace Vistudio.Bazel
{
    /// <summary>Attaches one <see cref="BazelCheckSession"/> to every Bazel/Starlark document opened in an editor.</summary>
    // Matched on "text" plus the file name: BUILD and WORKSPACE have no extension, and the content type can arrive late.
    [Export(typeof(IWpfTextViewCreationListener)), ContentType("text"), TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class BazelCheckListener : IWpfTextViewCreationListener
    {
        [Import] internal ITextDocumentFactoryService Documents = null!;
        [Import] internal BazelLanguageClient Client = null!;
        [Import] internal BazelErrorList ErrorList = null!;

        public void TextViewCreated(IWpfTextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var buffer = view.TextDataModel.DocumentBuffer;
            if (!Documents.TryGetTextDocument(buffer, out var document) || !BazelWorkspace.IsStarlarkFile(document.FilePath)) return;
            var session = BazelCheckSession.For(buffer, () => new BazelCheckSession(buffer, document, Client.Output, ErrorList));
            session.AddView(view);
        }
    }

    /// <summary>
    /// Checks one document: Vistudio's own checks (<see cref="BazelValidator"/>) on open, save and after typing pauses,
    /// and <c>buildifier --lint=warn</c> on open and save, both on the current text. Results feed squiggles and the Error List.
    /// </summary>
    internal sealed class BazelCheckSession : IDisposable
    {
        private static readonly TimeSpan BuildifierTimeout = TimeSpan.FromSeconds(15);
        private readonly ITextBuffer buffer;
        private readonly ITextDocument document;
        private readonly LanguageEnvironment output;
        private readonly BazelErrorList errorList;
        private readonly object gate = new object();
        private CancellationTokenSource? pending;
        private IReadOnlyList<BazelDiagnostic> own = Array.Empty<BazelDiagnostic>();
        private IReadOnlyList<BazelDiagnostic> lint = Array.Empty<BazelDiagnostic>();
        private int views;
        private bool missingBuildifierReported;

        internal BazelCheckSession(ITextBuffer buffer, ITextDocument document, LanguageEnvironment output, BazelErrorList errorList)
        {
            this.buffer = buffer; this.document = document; this.output = output; this.errorList = errorList;
            buffer.Changed += Edited;
            document.FileActionOccurred += FileAction;
            BazelLanguageClient.SettingsChanged += SettingsChanged;
        }

        internal static BazelCheckSession For(ITextBuffer buffer, Func<BazelCheckSession> create) =>
            buffer.Properties.GetOrCreateSingletonProperty(typeof(BazelCheckSession), create);

        internal static BazelCheckSession? Get(ITextBuffer buffer) =>
            buffer.Properties.TryGetProperty(typeof(BazelCheckSession), out BazelCheckSession session) ? session : null;

        internal IReadOnlyList<(ITrackingSpan Span, BazelDiagnostic Diagnostic)> Results { get; private set; } = Array.Empty<(ITrackingSpan, BazelDiagnostic)>();
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
            if (views == 1) Schedule(TimeSpan.Zero, buildifier: true);
        }

        private void Edited(object? sender, TextContentChangedEventArgs e)
        {
            if (BazelSettings.Load().ValidateWhileTyping) Schedule(TimeSpan.FromMilliseconds(400), buildifier: false);
        }

        private void FileAction(object? sender, TextDocumentFileActionEventArgs e)
        {
            if ((e.FileActionType & (FileActionTypes.ContentSavedToDisk | FileActionTypes.DocumentRenamed)) != 0) Schedule(TimeSpan.Zero, buildifier: true);
        }

        private void SettingsChanged(object? sender, EventArgs e) => Schedule(TimeSpan.Zero, buildifier: true);

        private void Schedule(TimeSpan delay, bool buildifier)
        {
            CancellationTokenSource source;
            lock (gate)
            {
                pending?.Cancel();
                pending = source = new CancellationTokenSource();
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, source.Token).ConfigureAwait(false);
                    await RunAsync(buffer.CurrentSnapshot, buildifier, source.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { output.Log("Bazel check " + Path.GetFileName(document.FilePath) + ": " + e.Message); }
            });
        }

        private async Task RunAsync(ITextSnapshot snapshot, bool runBuildifier, CancellationToken token)
        {
            var settings = BazelSettings.Load();
            var path = document.FilePath;
            var text = snapshot.GetText();
            var ownResults = settings.Validation ? BazelValidator.Validate(path, text) : Array.Empty<BazelDiagnostic>();
            var lintResults = lint;
            if (!settings.BuildifierLint) lintResults = Array.Empty<BazelDiagnostic>();
            else if (runBuildifier) lintResults = await BuildifierAsync(settings, path, text, token).ConfigureAwait(false) ?? lint;
            token.ThrowIfCancellationRequested();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            own = ownResults;
            lint = lintResults;
            Publish(snapshot);
        }

        /// <summary>buildifier's findings; null when it is not installed (earlier lint results then stay).</summary>
        private async Task<IReadOnlyList<BazelDiagnostic>?> BuildifierAsync(BazelSettings settings, string path, string text, CancellationToken token)
        {
            var buildifier = Buildifier.Find(settings);
            if (buildifier == null)
            {
                if (!missingBuildifierReported)
                    output.Log("buildifier lint is off: buildifier was not found on PATH or in %USERPROFILE%\\go\\bin. Install it or set Tools > Options > Bazel > buildifier path. Vistudio's own checks still run.");
                missingBuildifierReported = true;
                return Array.Empty<BazelDiagnostic>();
            }
            missingBuildifierReported = false;
            var start = Buildifier.Create(buildifier, path, BazelWorkspace.FindRoot(Path.GetDirectoryName(path)));
            string stdout, stderr;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(BuildifierTimeout);
                using (var process = new OwnedProcess(start))
                using (timeout.Token.Register(() => { try { process.StopAsync().Forget(); } catch (Exception) { } }))
                {
                    var outputTask = Task.Run(async () => { using (var reader = new StreamReader(process.Output, Encoding.UTF8)) return await reader.ReadToEndAsync().ConfigureAwait(false); });
                    var errorTask = process.Error.ReadToEndAsync();
                    var bytes = new UTF8Encoding(false).GetBytes(text);
                    try
                    {
                        await process.Input.WriteAsync(bytes, 0, bytes.Length, timeout.Token).ConfigureAwait(false);
                        process.Input.Close();
                    }
                    catch (IOException) { }
#pragma warning disable VSTHRD003 // Process output tasks have no UI-thread dependency.
                    stdout = await outputTask.ConfigureAwait(false);
                    stderr = await errorTask.ConfigureAwait(false);
                    await process.Exit.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                }
                token.ThrowIfCancellationRequested();
                if (timeout.IsCancellationRequested) throw new TimeoutException("buildifier did not finish within " + BuildifierTimeout.TotalSeconds + " s.");
            }
            return Buildifier.Parse(stdout, stderr, settings.ReportFormatting);
        }

        private void Publish(ITextSnapshot snapshot)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var all = own.Concat(lint).ToList();
            var results = new List<(ITrackingSpan, BazelDiagnostic)>();
            foreach (var diagnostic in all)
            {
                var line = snapshot.GetLineFromLineNumber(Math.Max(0, Math.Min(diagnostic.Line, snapshot.LineCount - 1)));
                var start = line.Start.Position + Math.Max(0, Math.Min(diagnostic.Column, line.Length));
                var end = Math.Min(line.End.Position, start + diagnostic.Length);
                if (end == start && start < line.End.Position) end = start + 1;
                results.Add((snapshot.CreateTrackingSpan(Span.FromBounds(start, end), SpanTrackingMode.EdgeExclusive), diagnostic));
            }
            Results = results;
            ResultsChanged?.Invoke(this, EventArgs.Empty);
            errorList.Publish(document.FilePath, all);
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            lock (gate) { pending?.Cancel(); pending = null; }
            buffer.Changed -= Edited;
            document.FileActionOccurred -= FileAction;
            BazelLanguageClient.SettingsChanged -= SettingsChanged;
            buffer.Properties.RemoveProperty(typeof(BazelCheckSession));
            errorList.Clear(document.FilePath);
        }
    }

    /// <summary>"Bazel" entries in the Error List, per document, replaced by every check. UI thread only.</summary>
    [Export, PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class BazelErrorList
    {
        private readonly IServiceProvider services;
        private ErrorListProvider? provider;
        private readonly Dictionary<string, List<ErrorTask>> byFile = new Dictionary<string, List<ErrorTask>>(StringComparer.OrdinalIgnoreCase);

        [ImportingConstructor]
        public BazelErrorList([Import(typeof(SVsServiceProvider))] IServiceProvider services) { this.services = services; }

        private ErrorListProvider Provider => provider ??= new ErrorListProvider(services)
        {
            ProviderGuid = new Guid("d4e5f6a7-8b9c-4d0e-a1f2-3b4c5d6e7f80"),
            ProviderName = "Bazel",
        };

        public void Publish(string file, IReadOnlyList<BazelDiagnostic> diagnostics)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var errors = Provider;
            errors.SuspendRefresh();
            try
            {
                Clear(file);
                if (diagnostics.Count == 0) return;
                var tasks = diagnostics.Select(d =>
                {
                    var task = new ErrorTask
                    {
                        Category = TaskCategory.CodeSense,
                        ErrorCategory = d.Severity == BazelSeverity.Error ? TaskErrorCategory.Error : d.Severity == BazelSeverity.Warning ? TaskErrorCategory.Warning : TaskErrorCategory.Message,
                        Text = d.Source + " (" + d.Code + "): " + d.Message,
                        Document = file, Line = d.Line, Column = d.Column, CanDelete = false,
                    };
                    task.Navigate += Navigate;
                    return task;
                }).ToList();
                foreach (var task in tasks) errors.Tasks.Add(task);
                byFile[file] = tasks;
            }
            finally { errors.ResumeRefresh(); }
        }

        public void Clear(string file)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (provider == null || !byFile.TryGetValue(file, out var previous)) return;
            foreach (var task in previous) { task.Navigate -= Navigate; provider.Tasks.Remove(task); }
            byFile.Remove(file);
        }

        private void Navigate(object? sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(sender is ErrorTask task) || string.IsNullOrEmpty(task.Document)) return;
            VsShellUtilities.OpenDocument(services, task.Document, Microsoft.VisualStudio.VSConstants.LOGVIEWID_Primary, out _, out _, out var frame);
            if (frame != null && VsShellUtilities.GetTextView(frame) is Microsoft.VisualStudio.TextManager.Interop.IVsTextView view)
            {
                view.SetCaretPos(task.Line, task.Column);
                view.CenterLines(task.Line, 1);
            }
        }
    }

    /// <summary>Squiggles for Bazel check results; spans track edits until the next check replaces them.</summary>
    [Export(typeof(IViewTaggerProvider)), ContentType("text"), TagType(typeof(IErrorTag)), TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class BazelErrorTaggerProvider : IViewTaggerProvider
    {
        [Import] internal ITextDocumentFactoryService Documents = null!;

        public ITagger<T>? CreateTagger<T>(ITextView view, ITextBuffer buffer) where T : ITag
        {
            if (buffer != view.TextDataModel.DocumentBuffer || !Documents.TryGetTextDocument(buffer, out var document) || !BazelWorkspace.IsStarlarkFile(document.FilePath)) return null;
            return new BazelErrorTagger(view, buffer) as ITagger<T>;
        }
    }

    internal sealed class BazelErrorTagger : ITagger<IErrorTag>, IDisposable
    {
        private readonly ITextView view;
        private readonly ITextBuffer buffer;
        private BazelCheckSession? session;
        public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

        public BazelErrorTagger(ITextView view, ITextBuffer buffer)
        {
            this.view = view; this.buffer = buffer;
            view.Closed += Closed;
            Attach();
            if (session == null) buffer.Changed += AttachLate;
        }

        private void Attach()
        {
            session = BazelCheckSession.Get(buffer);
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
                if (!spans.IntersectsWith(span)) continue;
                var type = diagnostic.Severity switch
                {
                    BazelSeverity.Error => PredefinedErrorTypeNames.SyntaxError,
                    BazelSeverity.Warning => PredefinedErrorTypeNames.Warning,
                    _ => PredefinedErrorTypeNames.HintedSuggestion,
                };
                yield return new TagSpan<IErrorTag>(span, new ErrorTag(type, diagnostic.Source + ": " + diagnostic.Message));
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
