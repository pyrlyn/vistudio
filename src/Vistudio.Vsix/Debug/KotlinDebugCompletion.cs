using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;

namespace Vistudio
{
    [Export(typeof(IVsTextViewCreationListener)), ContentType("any"), TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class KotlinDebugCompletionListener : IVsTextViewCreationListener
    {
        [Import] internal IVsEditorAdaptersFactoryService Adapters = null!;
        [Import] internal ICompletionBroker Broker = null!;
        [Import] internal IContentTypeRegistryService ContentTypes = null!;
        [Import] internal KotlinLanguageClient Language = null!;

#pragma warning disable CS0649
        [Export, Name("VistudioKotlinDebugExpression"), BaseDefinition("code")]
        internal static ContentTypeDefinition? DebugExpressionType;
#pragma warning restore CS0649

        public void VsTextViewCreated(IVsTextView adapter)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var view = Adapters.GetWpfTextView(adapter);
            if (view == null) return;
            if (!view.Roles.Contains("DEBUGVIEW") && !view.Roles.Contains("COMMANDVIEW")) return;
            var dte = ServiceProvider.GlobalProvider.GetService(typeof(DTE)) as DTE;
            if (dte == null) return;
            view.Properties.GetOrCreateSingletonProperty(() => new KotlinDebugCompletionController(view, adapter, Broker, dte, ContentTypes, Language));
        }
    }

    [Export(typeof(ICompletionSourceProvider)), ContentType("any"), Name("Kotlin debug frame completion")]
    internal sealed class KotlinDebugCompletionProvider : ICompletionSourceProvider
    {
        public ICompletionSource TryCreateCompletionSource(ITextBuffer buffer) => new KotlinDebugCompletionSource(buffer);
    }

    internal sealed class KotlinDebugCompletionSource : ICompletionSource
    {
        private readonly ITextBuffer buffer;
        internal KotlinDebugCompletionSource(ITextBuffer buffer) { this.buffer = buffer; }
        public void Dispose() { }
        public void AugmentCompletionSession(ICompletionSession session, IList<CompletionSet> sets)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (session.TextView.Roles.Contains("DEBUGVIEW")) return;
            if (buffer != session.TextView.TextBuffer) return;
            if (!session.TextView.Properties.TryGetProperty(typeof(KotlinDebugCompletionController), out KotlinDebugCompletionController controller)) return;
            var point = session.GetTriggerPoint(buffer.CurrentSnapshot);
            if (point == null) return;
            var line = point.Value.GetContainingLine();
            if (session.TextView.Roles.Contains("COMMANDVIEW") && !line.GetText().TrimStart().StartsWith("?", StringComparison.Ordinal)) return;
            var query = KotlinCompletionQuery.Parse(line.GetText(), point.Value.Position - line.Start.Position);
            if (query == null) return;
            var entries = controller.GetMembers(query);
            if (entries.Count == 0) return;
            var span = buffer.CurrentSnapshot.CreateTrackingSpan(line.Start.Position + query.Start, query.Length, SpanTrackingMode.EdgeInclusive);
            sets.Add(new CompletionSet("KotlinFrame", "Kotlin frame", span,
                entries.Select(entry => new KotlinDebugCompletionEntry(entry, controller, span)), null));
        }
    }

    internal sealed class KotlinDebugCompletionEntry : Completion, ICustomCommit
    {
        private readonly KotlinDebugCompletionController controller;
        private readonly ITrackingSpan span;
        internal KotlinDebugCompletionEntry(Completion entry, KotlinDebugCompletionController controller, ITrackingSpan span)
            : base(entry.DisplayText, entry.InsertionText, entry.Description, null, null)
        { this.controller = controller; this.span = span; }
        public void Commit() { ThreadHelper.ThrowIfNotOnUIThread(); controller.InsertCompletion(span, InsertionText); }
    }

    internal sealed class KotlinDebugCompletionController : IOleCommandTarget
    {
        private readonly IWpfTextView view;
        private readonly IVsTextView adapter;
        private readonly ICompletionBroker broker;
        private readonly DTE dte;
        private readonly DebuggerEvents events;
        private readonly IOleCommandTarget next;
        private ICompletionSession? session;
        private KotlinNativeCompletionSet? nativeSession;
        private readonly IContentTypeRegistryService contentTypes;
        private readonly IContentType originalContentType;
        private readonly KotlinLanguageClient language;
        private CancellationTokenSource? semanticRequest;
        private IReadOnlyList<KotlinCallable> callables = Array.Empty<KotlinCallable>();

        internal KotlinDebugCompletionController(IWpfTextView view, IVsTextView adapter, ICompletionBroker broker, DTE dte, IContentTypeRegistryService contentTypes, KotlinLanguageClient language)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.view = view; this.adapter = adapter; this.broker = broker; this.dte = dte;
            this.contentTypes = contentTypes; originalContentType = view.TextBuffer.ContentType;
            this.language = language;
            adapter.AddCommandFilter(this, out next);
            events = dte.Events.DebuggerEvents;
            events.OnContextChanged += ContextChanged;
            events.OnEnterRunMode += ModeChanged;
            events.OnEnterDesignMode += ModeChanged;
            view.LostAggregateFocus += LostFocus;
            view.GotAggregateFocus += GotFocus;
            view.Closed += Closed;
            EnsureContentType();
        }

        private void EnsureContentType()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // The native Watch completion shim requires a code buffer, including
            // before the first Ctrl+Space in an empty expression editor.
            if (view.Roles.Contains("DEBUGVIEW") && IsKotlinFrame() && !view.TextBuffer.ContentType.IsOfType("VistudioKotlinDebugExpression"))
                view.TextBuffer.ChangeContentType(contentTypes.GetContentType("VistudioKotlinDebugExpression"), this);
        }

        private void StartCompletion()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Dismiss();
            ShowCompletion();
            var caret = view.Caret.Position.BufferPosition;
            var line = caret.GetContainingLine();
            var query = KotlinCompletionQuery.Parse(line.GetText(), caret.Position - line.Start.Position);
            if (query == null || query.Receiver.Length == 0) return;
            try
            {
                var frame = dte.Debugger.CurrentStackFrame as EnvDTE90a.StackFrame2;
                if (frame == null || string.IsNullOrEmpty(frame.FileName)) return;
                // Capture COM properties on the UI thread. No values or evaluator calls.
                var file = frame.FileName;
                var frameLine = (int)frame.LineNumber;
                Trace("Frame " + file + ":" + frameLine);
                var chain = line.GetText().Substring(query.ChainStart, caret.Position - line.Start.Position - query.ChainStart);
                var request = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                semanticRequest = request;
#pragma warning disable VSSDK007 // Editor requests are canceled on dismissal/closure and observed by FileAndForget; never block a command on LSP.
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        await Task.Delay(150, request.Token).ConfigureAwait(false);
                        var source = File.ReadAllText(file);
                        var document = KotlinSemanticCompletion.Create(file, source, frameLine, chain);
                        if (document == null) return;
                        var result = await language.CompleteDebugAsync(document, request.Token).ConfigureAwait(false);
                        Trace("Candidates " + result.Count);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(request.Token);
                        if (!ReferenceEquals(semanticRequest, request) || !CanComplete() || !view.HasAggregateFocus ||
                            view.TextSnapshot != caret.Snapshot || view.Caret.Position.BufferPosition.Position != caret.Position) return;
                        callables = result;
                        ShowCompletion();
                    }
                    catch (OperationCanceledException) { Trace("Canceled"); }
                    catch (Exception error) { ActivityLog.LogWarning("Vistudio", "Debugger semantic completion: " + error.Message); }
                    finally
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (ReferenceEquals(semanticRequest, request)) semanticRequest = null;
                        request.Dispose();
                    }
                }).FileAndForget("Vistudio/DebuggerCompletion");
#pragma warning restore VSSDK007
            }
            catch (COMException) { }
        }

        private void ShowCompletion()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var previous = session; session = null;
            if (previous != null && !previous.IsDismissed) previous.Dismiss();
            if (view.Roles.Contains("DEBUGVIEW"))
            {
                EnsureContentType();
                var caret = view.Caret.Position.BufferPosition;
                var line = caret.GetContainingLine();
                var query = KotlinCompletionQuery.Parse(line.GetText(), caret.Position - line.Start.Position);
                if (query == null) return;
                var entries = GetMembers(query);
                if (entries.Count == 0) return;
                nativeSession = new KotlinNativeCompletionSet(entries, line.LineNumber, query.Start, query.Start + query.Length);
                var created = nativeSession;
                created.OnDismiss = () => { ThreadHelper.ThrowIfNotOnUIThread(); if (ReferenceEquals(nativeSession, created)) CancelSemantic(); };
                ErrorHandler.ThrowOnFailure(adapter.UpdateCompletionStatus(nativeSession,
                    (uint)(UpdateCompletionFlags.UCS_NAMESCHANGED | UpdateCompletionFlags.UCS_EXTENTCHANGED | UpdateCompletionFlags.UCS_FLAGSCHANGED)));
                return;
            }
            session = broker.TriggerCompletion(view);
            if (session == null || session.IsDismissed) return;
            var currentSession = session;
            currentSession.Dismissed += (sender, args) => { ThreadHelper.ThrowIfNotOnUIThread(); if (ReferenceEquals(session, currentSession)) CancelSemantic(); };
            session.Filter();
            session.SelectedCompletionSet?.SelectBestMatch();
            session.SelectedCompletionSet?.Recalculate();
        }

        // Kotlin and Java frames of the shared JVM session. Semantic (LSP) suggestions stay Kotlin-only:
        // KotlinSemanticCompletion accepts only .kt frame sources.
        private bool IsKotlinFrame() => FrameLanguage() != null;

        private string? FrameLanguage()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode) return null;
                var frame = dte.Debugger.CurrentStackFrame;
                var language = frame?.Language;
                if (!string.Equals(language, "Kotlin", StringComparison.OrdinalIgnoreCase) && !string.Equals(language, "Java", StringComparison.OrdinalIgnoreCase)) return null;
                // The engine maps .java frames to Java; the source extension also decides on hosts without that mapping.
                var file = (frame as EnvDTE90a.StackFrame2)?.FileName;
                return string.Equals(language, "Java", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetExtension(file ?? ""), ".java", StringComparison.OrdinalIgnoreCase) ? "Java" : "Kotlin";
            }
            catch (COMException) { return null; }
        }

        private static void Trace(string message)
        {
            if (Environment.GetEnvironmentVariable("VISTUDIO_COMPLETION_TRACE") == "1")
                ActivityLog.LogInformation("VistudioCompletion", message);
        }

        private bool CanComplete()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (view.IsClosed || !IsKotlinFrame()) return false;
            if (!view.Roles.Contains("COMMANDVIEW")) return true;
            var line = view.Caret.Position.BufferPosition.GetContainingLine();
            // Leave IDE commands and read-only command history to Visual Studio.
            return line.LineNumber == view.TextSnapshot.LineCount - 1 && line.GetText().TrimStart().StartsWith("?", StringComparison.Ordinal);
        }

        internal void InsertCompletion(ITrackingSpan span, string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!CanComplete()) return;
            ErrorHandler.ThrowOnFailure(adapter.GetBuffer(out var lines));
            ErrorHandler.ThrowOnFailure(lines.GetStateFlags(out var flags));
            try
            {
                // Native debugger editors are read-only outside command processing.
                ErrorHandler.ThrowOnFailure(lines.SetStateFlags(flags & ~(uint)BUFFERSTATEFLAGS.BSF_USER_READONLY));
                var replacement = span.GetSpan(view.TextSnapshot);
                var changed = view.TextBuffer.Replace(replacement.Span, text);
                view.Caret.MoveTo(new SnapshotPoint(changed, replacement.Start.Position + text.Length));
            }
            finally { ErrorHandler.ThrowOnFailure(lines.SetStateFlags(flags)); }
        }

        internal List<Completion> GetMembers(KotlinCompletionQuery query)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var results = new List<Completion>();
            if (!IsKotlinFrame()) return results;
            try
            {
                // Use the host's selected frame and debug properties, never GetExpression.
                var frame = dte.Debugger.CurrentStackFrame;
                var extended = frame as EnvDTE90a.StackFrame2;
                var values = extended == null ? new List<Expression>() : Read(extended.Locals2[false]).Concat(Read(extended.Arguments2[false])).ToList();
                foreach (var name in query.Receiver)
                {
                    var parent = values.FirstOrDefault(v => { ThreadHelper.ThrowIfNotOnUIThread(); return v.Name == name; });
                    if (parent == null) { values.Clear(); break; }
                    values = Read(parent.DataMembers).ToList();
                }
                var names = new HashSet<string>(StringComparer.Ordinal);
                var java = FrameLanguage() == "Java";
                foreach (var value in values)
                {
                    var insertion = java ? KotlinCompletionQuery.JavaInsertion(value.Name) : KotlinCompletionQuery.Insertion(value.Name);
                    if (insertion != null && names.Add(insertion)) results.Add(new Completion(insertion, insertion, value.Type, null, null));
                }
                results.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.DisplayText, b.DisplayText));
            }
            catch (COMException) { results.Clear(); }
            results.AddRange(callables.Select(item => new Completion(item.Description, item.Insertion, item.Description, null, null)));
            return results;
        }

        private static IEnumerable<Expression> Read(Expressions expressions)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var count = Math.Min(expressions.Count, 512);
            for (var i = 1; i <= count; i++) yield return expressions.Item(i);
        }

        private void ContextChanged(EnvDTE.Process process, Program program, EnvDTE.Thread thread, StackFrame frame) { ThreadHelper.ThrowIfNotOnUIThread(); Dismiss(); if (!IsKotlinFrame()) RestoreContentType(); }
        private void ModeChanged(dbgEventReason reason) { ThreadHelper.ThrowIfNotOnUIThread(); Dismiss(); RestoreContentType(); }
        private void RestoreContentType()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (view.TextBuffer.ContentType.IsOfType("VistudioKotlinDebugExpression")) view.TextBuffer.ChangeContentType(originalContentType, this);
        }
        private void LostFocus(object sender, EventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); Dismiss(); }
        private void GotFocus(object sender, EventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); EnsureContentType(); }
        private void Dismiss()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            CancelSemantic();
            callables = Array.Empty<KotlinCallable>();
            var current = session; session = null;
            if (current != null && !current.IsDismissed) current.Dismiss();
            if (nativeSession != null)
            {
                nativeSession = null;
                adapter.UpdateCompletionStatus(null!, 0);
            }
        }
        private void CancelSemantic()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var request = semanticRequest; semanticRequest = null;
            request?.Cancel();
        }
        private void Closed(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Dismiss();
            events.OnContextChanged -= ContextChanged;
            events.OnEnterRunMode -= ModeChanged;
            events.OnEnterDesignMode -= ModeChanged;
            view.LostAggregateFocus -= LostFocus;
            view.GotAggregateFocus -= GotFocus;
            view.Closed -= Closed;
            adapter.RemoveCommandFilter(this);
            RestoreContentType();
        }

        public int QueryStatus(ref Guid group, uint count, OLECMD[] commands, IntPtr text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (group == VSConstants.VSStd2K && CanComplete())
                for (var i = 0; i < count; i++)
                    if (IsCompletionCommand(commands[i].cmdID)) { commands[i].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED); return VSConstants.S_OK; }
            return next.QueryStatus(ref group, count, commands, text);
        }
        private static bool IsCompletionCommand(uint id) => id == (uint)VSConstants.VSStd2KCmdID.AUTOCOMPLETE || id == (uint)VSConstants.VSStd2KCmdID.COMPLETEWORD || id == (uint)VSConstants.VSStd2KCmdID.SHOWMEMBERLIST;
        public int Exec(ref Guid group, uint id, uint options, IntPtr input, IntPtr output)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Command-window bookkeeping uses other groups while a popup is open.
            if (group != VSConstants.VSStd2K) return next.Exec(ref group, id, options, input, output);
            if (!CanComplete()) { Dismiss(); return next.Exec(ref group, id, options, input, output); }
            if (IsCompletionCommand(id)) { StartCompletion(); return VSConstants.S_OK; }
            if (session != null && !session.IsDismissed)
            {
                if (id == (uint)VSConstants.VSStd2KCmdID.CANCEL) { Dismiss(); return VSConstants.S_OK; }
                if (id == (uint)VSConstants.VSStd2KCmdID.RETURN || id == (uint)VSConstants.VSStd2KCmdID.TAB)
                {
                    var selectedSet = session.SelectedCompletionSet;
                    if (id == (uint)VSConstants.VSStd2KCmdID.TAB && selectedSet?.SelectionStatus.Completion != null)
                        selectedSet.SelectionStatus = new CompletionSelectionStatus(selectedSet.SelectionStatus.Completion, true, true);
                    if (session.SelectedCompletionSet?.SelectionStatus.IsSelected == true) { session.Commit(); session = null; return VSConstants.S_OK; }
                    Dismiss();
                }
            }
            var result = next.Exec(ref group, id, options, input, output);
            if (id == (uint)VSConstants.VSStd2KCmdID.TYPECHAR && input != IntPtr.Zero)
            {
                var c = (char)(ushort)Marshal.GetObjectForNativeVariant(input);
                if (c == '.') StartCompletion();
                else if (char.IsLetterOrDigit(c) || c == '_' || c == '`')
                {
                    StartCompletion();
                }
                else Dismiss();
            }
            else if (id == (uint)VSConstants.VSStd2KCmdID.BACKSPACE || id == (uint)VSConstants.VSStd2KCmdID.DELETE) Dismiss();
            return result;
        }
    }
}
