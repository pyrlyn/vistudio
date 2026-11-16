using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using Vistudio.Core.Dart;
using Vistudio.Core.Dart.Preview;
using Task = System.Threading.Tasks.Task;

namespace Vistudio.Dart.Preview
{
    /// <summary>Applies the "Add @Preview" quick fix to the editor buffer of a file (opening it when needed), as one undo step.</summary>
    internal static class FlutterPreviewQuickFix
    {
        public static void Apply(string file, string widgetClass)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, file, VSConstants.LOGVIEWID_Primary, out _, out _, out var frame, out IVsTextView view);
            frame?.Show();
            var model = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel ?? throw new InvalidOperationException("Visual Studio component model is unavailable.");
            var buffer = view == null ? null : model.GetService<IVsEditorAdaptersFactoryService>().GetWpfTextView(view)?.TextBuffer;
            if (buffer == null) throw new InvalidOperationException("Could not open " + file + " in the editor.");
            Apply(buffer, widgetClass);
        }

        public static void Apply(ITextBuffer buffer, string widgetClass)
        {
            var text = buffer.CurrentSnapshot.GetText();
            var file = WidgetPreviewScanner.Scan(text);
            var widget = file.Widgets.FirstOrDefault(w => w.Name == widgetClass) ?? throw new InvalidOperationException("No widget class '" + widgetClass + "' in this file.");
            var insertions = PreviewQuickFix.AddPreview(text, file, widget);
            using (var edit = buffer.CreateEdit())
            {
                foreach (var insertion in insertions) edit.Insert(insertion.Offset, insertion.Text);
                edit.Apply();
            }
        }
    }

    /// <summary>
    /// Light bulb on a widget class without <c>@Preview</c>: "Add @Preview for 'Name'" (writes a preview function into
    /// the file) and "Preview 'Name' (temporary project)" (no source change).
    /// </summary>
    [Export(typeof(ISuggestedActionsSourceProvider)), Name("Vistudio Flutter preview actions"), ContentType("text")]
    internal sealed class FlutterPreviewSuggestedActionsProvider : ISuggestedActionsSourceProvider
    {
        [Import] internal ITextDocumentFactoryService Documents = null!;
        [Import] internal FlutterPreviewService Service = null!;

        public ISuggestedActionsSource? CreateSuggestedActionsSource(ITextView textView, ITextBuffer textBuffer)
        {
            if (!Documents.TryGetTextDocument(textBuffer, out var document) || !DartProject.IsDartFile(document.FilePath)) return null;
            return new Source(this, textView, textBuffer, document.FilePath);
        }

        private sealed class Source : ISuggestedActionsSource
        {
            private readonly FlutterPreviewSuggestedActionsProvider provider;
            private readonly ITextView view;
            private readonly ITextBuffer buffer;
            private readonly string path;

            public Source(FlutterPreviewSuggestedActionsProvider provider, ITextView view, ITextBuffer buffer, string path)
            { this.provider = provider; this.view = view; this.buffer = buffer; this.path = path; }

            public event EventHandler<EventArgs>? SuggestedActionsChanged { add { } remove { } }

            private WidgetClassInfo? WidgetAt(SnapshotSpan range)
            {
                var settings = DartSettings.Load();
                if (!settings.WidgetPreview || FlutterWorkspace.ProjectOf(path) == null) return null;
                var text = range.Snapshot.GetText();
                var file = WidgetPreviewScanner.Scan(text);
                var caret = range.Start.Position;
                // On the class header line, without an existing preview.
                return file.Widgets.FirstOrDefault(w => w.IsWidget == true && !w.HasPreview && w.Problem == null && w.Construction != null
                    && range.Snapshot.GetLineNumberFromPosition(w.NameStart) == range.Snapshot.GetLineNumberFromPosition(caret));
            }

            public Task<bool> HasSuggestedActionsAsync(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancellationToken) =>
                Task.FromResult(WidgetAt(range) != null);

            public IEnumerable<SuggestedActionSet> GetSuggestedActions(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancellationToken)
            {
                var widget = WidgetAt(range);
                if (widget == null) return Enumerable.Empty<SuggestedActionSet>();
                var actions = new ISuggestedAction[]
                {
                    new Action("Add @Preview for '" + widget.Name + "'", () => FlutterPreviewQuickFix.Apply(buffer, widget.Name)),
                    new Action("Preview '" + widget.Name + "' (temporary project, no source change)", () => provider.Service.Run(() => FlutterPreviewCommands.ShowAsync(path, widget.Name))),
                };
                return new[] { new SuggestedActionSet(PredefinedSuggestedActionCategoryNames.Refactoring, actions, "Flutter widget preview", SuggestedActionSetPriority.Medium, range) };
            }

            public bool TryGetTelemetryId(out Guid telemetryId) { telemetryId = Guid.Empty; return false; }
            public void Dispose() { }
        }

        private sealed class Action : ISuggestedAction
        {
            private readonly System.Action run;
            public Action(string text, System.Action run) { DisplayText = text; this.run = run; }
            public string DisplayText { get; }
            public bool HasActionSets => false;
            public bool HasPreview => false;
            public ImageMoniker IconMoniker => default;
            public string? IconAutomationText => null;
            public string? InputGestureText => null;
            public Task<IEnumerable<SuggestedActionSet>?> GetActionSetsAsync(CancellationToken cancellationToken) => Task.FromResult<IEnumerable<SuggestedActionSet>?>(Enumerable.Empty<SuggestedActionSet>());
            public Task<object?> GetPreviewAsync(CancellationToken cancellationToken) => Task.FromResult<object?>(null);
            public void Invoke(CancellationToken cancellationToken)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                run();
            }
            public bool TryGetTelemetryId(out Guid telemetryId) { telemetryId = Guid.Empty; return false; }
            public void Dispose() { }
        }
    }

    /// <summary>
    /// Saves and edits of Dart files while a preview runs: a save reloads a temporary host project (the package's own
    /// previewer reloads by itself), and with "reload after typing" the edited file is saved after the configured pause.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener)), ContentType("text"), TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class FlutterPreviewEditTracker : IWpfTextViewCreationListener
    {
        [Import] internal ITextDocumentFactoryService Documents = null!;
        [Import] internal FlutterPreviewService Service = null!;

        public void TextViewCreated(IWpfTextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var buffer = view.TextDataModel.DocumentBuffer;
            if (!Documents.TryGetTextDocument(buffer, out var document) || !DartProject.IsDartFile(document.FilePath)) return;
            var tracker = buffer.Properties.GetOrCreateSingletonProperty(typeof(FlutterPreviewEditTracker), () => new BufferTracker(Service, document));
            tracker.Views++;
            view.Closed += (_, __) =>
            {
                if (--tracker.Views > 0) return;
                tracker.Dispose();
                buffer.Properties.RemoveProperty(typeof(FlutterPreviewEditTracker));
            };
        }

        private sealed class BufferTracker : IDisposable
        {
            private readonly FlutterPreviewService service;
            private readonly ITextDocument document;
            private CancellationTokenSource? pending;
            public int Views;

            public BufferTracker(FlutterPreviewService service, ITextDocument document)
            {
                this.service = service;
                this.document = document;
                document.FileActionOccurred += OnFileAction;
                document.TextBuffer.Changed += OnChanged;
            }

            private void OnFileAction(object? sender, TextDocumentFileActionEventArgs e)
            {
                if ((e.FileActionType & FileActionTypes.ContentSavedToDisk) != 0) service.OnDartFileSaved(e.FilePath);
            }

            private void OnChanged(object? sender, TextContentChangedEventArgs e)
            {
                if (!service.IsPreviewing(document.FilePath)) return;
                var settings = DartSettings.Load();
                if (settings.WidgetPreviewReload != WidgetPreviewReloadTrigger.AfterDelay) return;
                pending?.Cancel();
                var source = pending = new CancellationTokenSource();
                service.Run(async () =>
                {
                    try { await Task.Delay(settings.WidgetPreviewReloadDelay, source.Token); }
                    catch (OperationCanceledException) { return; }
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (source.IsCancellationRequested || !document.IsDirty) return;
                    // The previewer watches the files on disk: saving is what makes it reload.
                    document.Save();
                });
            }

            public void Dispose()
            {
                pending?.Cancel();
                document.FileActionOccurred -= OnFileAction;
                document.TextBuffer.Changed -= OnChanged;
            }
        }
    }
}
