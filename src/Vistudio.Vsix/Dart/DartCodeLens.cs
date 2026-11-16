using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TextManager.Interop;
using Newtonsoft.Json.Linq;
using Vistudio.Core.CodeLens;
using Vistudio.Core.Dart;

namespace Vistudio.Dart
{
    /// <summary>
    /// Marks Dart declarations as CodeLens code elements (identity <c>vistudiodart|Kind|Qualified name[#n]</c>):
    /// <c>main</c> (Run, Debug, references), tests and groups (Run test, Debug test), other functions and types (references).
    /// </summary>
    [VisualStudioContribution]
    internal sealed class DartCodeLensTaggerProvider : ExtensionPart, ITextViewTaggerProvider<CodeLensTag>, ITextViewChangedListener
    {
        private readonly object gate = new object();
        private readonly Dictionary<Uri, List<DartCodeLensTagger>> taggers = new Dictionary<Uri, List<DartCodeLensTagger>>();

        // Matched by file name in code, like the Kotlin/Java/Zig taggers: the `dart` content type may be assigned late.
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new() { AppliesTo = new[] { DocumentFilter.FromDocumentType("text") } };

        public Task<TextViewTaggerBase<CodeLensTag>> CreateTaggerAsync(ITextViewSnapshot textView, CancellationToken cancellationToken)
        {
            var tagger = new DartCodeLensTagger(this, textView.Document.Uri);
            lock (gate)
            {
                if (!taggers.TryGetValue(tagger.Uri, out var list)) taggers[tagger.Uri] = list = new List<DartCodeLensTagger>();
                list.Add(tagger);
            }
            return Task.FromResult<TextViewTaggerBase<CodeLensTag>>(tagger);
        }

        internal void Remove(DartCodeLensTagger tagger)
        {
            lock (gate)
            {
                if (!taggers.TryGetValue(tagger.Uri, out var list)) return;
                list.Remove(tagger);
                if (list.Count == 0) taggers.Remove(tagger.Uri);
            }
        }

        public async Task TextViewChangedAsync(TextViewChangedArgs args, CancellationToken cancellationToken)
        {
            if (args.Edits.Count == 0) return;
            var uri = args.AfterTextView.Document.Uri;
            if (!DartCodeElement.IsDart(uri)) return;
            DartCodeLensTagger[] current;
            lock (gate) current = taggers.TryGetValue(uri, out var list) ? list.ToArray() : Array.Empty<DartCodeLensTagger>();
            foreach (var tagger in current) await tagger.DocumentChangedAsync(args.AfterTextView.Document, cancellationToken);
            DartCodeLensRefresh.Schedule();
        }
    }

    internal sealed class DartCodeLensTagger : TextViewTagger<CodeLensTag>
    {
        private readonly DartCodeLensTaggerProvider provider;

        public DartCodeLensTagger(DartCodeLensTaggerProvider provider, Uri uri)
        {
            this.provider = provider;
            Uri = uri;
            Initialize(provider, uri);
        }

        internal Uri Uri { get; }

        protected override Task OnRequestTagsAsync(NormalizedTextRangeCollection requestedRanges, bool recalculateAll, CancellationToken cancellationToken) =>
            requestedRanges.Count == 0 ? Task.CompletedTask : UpdateAllAsync(requestedRanges.First().Document, cancellationToken);

        protected override Task OnTextViewChangedAsync(TextViewChangedArgs args, CancellationToken cancellationToken) => Task.CompletedTask;

        internal Task DocumentChangedAsync(ITextDocumentSnapshot document, CancellationToken cancellationToken) => UpdateAllAsync(document, cancellationToken);

        private async Task UpdateAllAsync(ITextDocumentSnapshot document, CancellationToken cancellationToken)
        {
            if (!DartCodeElement.IsDart(document.Uri) || !DartSettings.Load().CodeLens) return;
            var text = document.Text.CopyToString();
            var declarations = DartSourceScanner.Scan(text);
            var identifiers = DartCodeElement.Identifiers(declarations);
            var tags = new List<TaggedTrackingTextRange<CodeLensTag>>();
            for (var i = 0; i < declarations.Count; i++)
            {
                var declaration = declarations[i];
                var kind = declaration.Kind == DartDeclarationKind.Type ? CodeElementKind.KnownValues.Type : CodeElementKind.KnownValues.Method;
                var tag = new CodeLensTag(kind) { Description = declaration.QualifiedName, UniqueIdentifier = identifiers[i] };
                // The range ends at the name: the reference lens queries the server at the name's last character.
                tags.Add(new TaggedTrackingTextRange<CodeLensTag>(
                    new TextRange(document, declaration.StartOffset, declaration.NameEnd - declaration.StartOffset), TextRangeTrackingMode.ExtendForwardAndBackward, tag));
            }
            // Flutter widget previews: one element per @Preview annotation line ("Preview" lens); widget classes reuse their Type element.
            var path = DartCodeElement.PathOf(new TextRange(document, 0, 0));
            if (Preview.FlutterPreviewLensData.Enabled(path))
            {
                var uri = document.Uri.IsFile ? new Uri(document.Uri.LocalPath).AbsoluteUri : document.Uri.ToString();
                var targets = await Preview.FlutterPreviewLensData.TargetsAsync(path, uri, text, cancellationToken);
                var previewIds = CodeLensElement.WithOrdinals(targets.Select(t => Preview.FlutterPreviewLensData.PreviewPrefix + t.Name));
                for (var i = 0; i < targets.Count; i++)
                {
                    var target = targets[i];
                    var start = Math.Min(target.AnnotationStart, text.Length);
                    var end = start;
                    while (end < text.Length && text[end] != '\n' && text[end] != '\r') end++;
                    var tag = new CodeLensTag(CodeElementKind.KnownValues.Method) { Description = "Preview " + target.Name, UniqueIdentifier = previewIds[i] };
                    tags.Add(new TaggedTrackingTextRange<CodeLensTag>(new TextRange(document, start, Math.Max(1, end - start)), TextRangeTrackingMode.ExtendForwardAndBackward, tag));
                }
            }
            await UpdateTagsAsync(new[] { new TextRange(document, 0, document.Length) }, tags, cancellationToken);
        }

        public override void Dispose()
        {
            provider.Remove(this);
            base.Dispose();
        }
    }

    internal static class DartCodeElement
    {
        private const string Prefix = "vistudiodart|";

        internal static bool IsDart(Uri uri) => DartProject.IsDartFile(uri.IsFile ? uri.LocalPath : uri.ToString());

        internal static IReadOnlyList<string> Identifiers(IReadOnlyList<DartDeclaration> declarations) =>
            CodeLensElement.WithOrdinals(declarations.Select(d => Prefix + d.Kind + "|" + d.QualifiedName));

        internal static DartDeclarationKind? Kind(CodeElement element)
        {
            var id = element.UniqueIdentifier;
            if (id == null || !id.StartsWith(Prefix, StringComparison.Ordinal)) return null;
            var end = id.IndexOf('|', Prefix.Length);
            return end > 0 && Enum.TryParse<DartDeclarationKind>(id.Substring(Prefix.Length, end - Prefix.Length), out var kind) ? kind : (DartDeclarationKind?)null;
        }

        /// <summary>The declaration under a tagged range, rescanned from the current text (offsets move with edits).</summary>
        internal static DartDeclaration? Declaration(TextRange range)
        {
            var declarations = DartSourceScanner.Scan(range.Document.Text.CopyToString());
            int start = range.Start, end = range.End;
            return declarations.FirstOrDefault(d => d.NameEnd == end) ?? declarations.FirstOrDefault(d => d.StartOffset >= start && d.NameEnd <= end);
        }

        internal static string PathOf(TextRange range) => range.Document.Uri.IsFile ? range.Document.Uri.LocalPath : range.Document.Uri.ToString();
    }

    internal enum DartLensAction { Run, Debug }

    /// <summary>"Run" / "Debug" above main (the program) and above tests and groups (that test by full name).</summary>
    [VisualStudioContribution]
    internal sealed class DartRunCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new() { AppliesTo = new[] { DocumentFilter.FromDocumentType("text") } };
        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("Dart run") { Priority = 100 };

        public Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token)
        {
            var kind = DartCodeElement.Kind(codeElement);
            return Task.FromResult<CodeLens?>(kind == DartDeclarationKind.Main || kind == DartDeclarationKind.Test || kind == DartDeclarationKind.Group
                ? new DartActionCodeLens(DartLensAction.Run) : null);
        }
    }

    [VisualStudioContribution]
    internal sealed class DartDebugCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new() { AppliesTo = new[] { DocumentFilter.FromDocumentType("text") } };
        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("Dart debug") { Priority = 101 };

        public Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token)
        {
            var kind = DartCodeElement.Kind(codeElement);
            return Task.FromResult<CodeLens?>(kind == DartDeclarationKind.Main || kind == DartDeclarationKind.Test || kind == DartDeclarationKind.Group
                ? new DartActionCodeLens(DartLensAction.Debug) : null);
        }
    }

    [VisualStudioContribution]
    internal sealed class DartReferencesCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new() { AppliesTo = new[] { DocumentFilter.FromDocumentType("text") } };
        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("Dart references") { Priority = 110 };

        public Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token)
        {
            var kind = DartCodeElement.Kind(codeElement);
            return Task.FromResult<CodeLens?>(kind == DartDeclarationKind.Function || kind == DartDeclarationKind.Type || kind == DartDeclarationKind.Main
                ? new DartReferencesCodeLens() : null);
        }
    }

    internal sealed class DartActionCodeLens : InvokableCodeLens
    {
        private readonly DartLensAction action;

        internal DartActionCodeLens(DartLensAction action) => this.action = action;

        public override void Dispose() { }

        public override Task<CodeLensLabel> GetLabelAsync(CodeElementContext codeElementContext, CancellationToken token)
        {
            var declaration = DartCodeElement.Declaration(codeElementContext.Range);
            var test = declaration?.IsTest == true;
            var text = action == DartLensAction.Run ? (test ? "Run test" : "Run") : (test ? "Debug test" : "Debug");
            if (declaration?.Kind == DartDeclarationKind.Group) text += "s";
            var target = declaration == null ? "" : test ? (declaration.IsDynamicName ? " (all tests of the file: the name uses interpolation)" : " '" + declaration.QualifiedName + "'") : " this program";
            return Task.FromResult(new CodeLensLabel { Text = text, Tooltip = text + target + (action == DartLensAction.Debug ? " in the Visual Studio debugger." : "; output in the Dart pane.") });
        }

        public override async Task ExecuteAsync(CodeElementContext codeElementContext, IClientContext clientContext, CancellationToken cancelToken)
        {
            var declaration = DartCodeElement.Declaration(codeElementContext.Range);
            var path = DartCodeElement.PathOf(codeElementContext.Range);
            var model = await AsyncServiceProvider.GlobalProvider.GetServiceAsync<SComponentModel, IComponentModel>();
            var environment = model.GetService<DartEnvironment>();
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancelToken);
                (await AsyncServiceProvider.GlobalProvider.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>())?.ExecuteCommand("File.SaveAll");
                var name = declaration == null || declaration.IsDynamicName ? null : declaration.QualifiedName;
                if (declaration?.IsTest == true)
                {
                    if (action == DartLensAction.Run) await model.GetService<DartTestService>().RunFileAsync(path, name);
                    else await model.GetService<DartDebugCoordinator>().DebugTestAsync(path, name);
                }
                else if (action == DartLensAction.Run) await model.GetService<DartRunService>().RunAsync(path);
                else await model.GetService<DartDebugCoordinator>().DebugProgramAsync(path, noDebug: false);
            }
            catch (Exception e)
            {
                environment.Log("Dart: " + e.Message);
                await environment.ShowOutputAsync();
            }
        }
    }

    /// <summary>"N references" from the analysis server; a click runs Find All References at the name.</summary>
    internal sealed class DartReferencesCodeLens : InvokableCodeLens
    {
        internal DartReferencesCodeLens() => DartCodeLensRefresh.Register(this);

        public override async Task<CodeLensLabel> GetLabelAsync(CodeElementContext codeElementContext, CancellationToken token)
        {
            var (path, uri, line, character) = Position(codeElementContext.Range);
            var model = await AsyncServiceProvider.GlobalProvider.GetServiceAsync<SComponentModel, IComponentModel>();
            var client = model.GetService<DartLanguageClient>();
            if (!client.IsServerReady)
            {
                DartCodeLensRefresh.WaitForServer();
                return new CodeLensLabel { Text = "- references", Tooltip = "Waiting for the Dart analysis server." };
            }
            JToken? result;
            try
            {
                result = await client.RequestAsync("textDocument/references", new
                {
                    textDocument = new { uri },
                    position = new { line, character },
                    context = new { includeDeclaration = false },
                }, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { return new CodeLensLabel { Text = "? references", Tooltip = "The analysis server failed: " + e.Message }; }
            var count = result is JArray array ? array.Count : 0;
            var text = CodeLensElement.CountLabel(count, "reference");
            return new CodeLensLabel { Text = text, Tooltip = text + " (Dart analysis server). Click to find all references." };
        }

        public override async Task ExecuteAsync(CodeElementContext codeElementContext, IClientContext clientContext, CancellationToken cancelToken)
        {
            var (path, _, line, character) = Position(codeElementContext.Range);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancelToken);
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path, Guid.Empty, out _, out _, out var frame, out IVsTextView view);
            frame?.Show();
            if (view == null) return;
            view.SetCaretPos(line, character);
            view.SendExplicitFocus();
            var dte = await AsyncServiceProvider.GlobalProvider.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancelToken);
            dte.ExecuteCommand("Edit.FindAllReferences");
        }

        /// <summary>File path, file URI and the 0-based line/character of the declaration name's last character.</summary>
        private static (string Path, string Uri, int Line, int Character) Position(TextRange range)
        {
            var text = range.Document.Text.CopyToString();
            var declaration = DartCodeElement.Declaration(range);
            int start = declaration?.NameStart ?? range.Start, end = declaration?.NameEnd ?? range.End;
            var (line, character) = CodeLensElement.NamePosition(text, start, end);
            var path = DartCodeElement.PathOf(range);
            var uri = range.Document.Uri.IsFile ? new Uri(path).AbsoluteUri : range.Document.Uri.ToString();
            return (path, uri, line, character);
        }

        internal void Refresh() => Invalidate();

        public override void Dispose() => DartCodeLensRefresh.Unregister(this);
    }

    /// <summary>Reference counts are workspace-wide: refreshed after edits (debounced) and when the analysis server becomes ready.</summary>
    internal static class DartCodeLensRefresh
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<DartReferencesCodeLens> Lenses = new HashSet<DartReferencesCodeLens>();
        private static CancellationTokenSource? pending;
        private static bool waiting;

        internal static void Register(DartReferencesCodeLens lens) { lock (Gate) Lenses.Add(lens); }
        internal static void Unregister(DartReferencesCodeLens lens) { lock (Gate) Lenses.Remove(lens); }

        internal static void Schedule(int delayMilliseconds = 1500)
        {
            CancellationTokenSource source;
            lock (Gate) { pending?.Cancel(); pending = source = new CancellationTokenSource(); }
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(delayMilliseconds, source.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                InvalidateAll();
            });
        }

        internal static void WaitForServer()
        {
            lock (Gate) { if (waiting) return; waiting = true; }
            _ = Task.Run(async () =>
            {
                try
                {
                    var model = await AsyncServiceProvider.GlobalProvider.GetServiceAsync<SComponentModel, IComponentModel>().ConfigureAwait(false);
                    var client = model.GetService<DartLanguageClient>();
                    for (var i = 0; i < 600 && !client.IsServerReady; i++) await Task.Delay(1000).ConfigureAwait(false);
                    // The server answers references only after its first analysis; ask again a little later too.
                    await Task.Delay(3000).ConfigureAwait(false);
                }
                catch (Exception) { }
                finally { lock (Gate) waiting = false; }
                InvalidateAll();
            });
        }

        internal static void InvalidateAll()
        {
            DartReferencesCodeLens[] current;
            lock (Gate) current = Lenses.ToArray();
            foreach (var lens in current)
            {
                try { lens.Refresh(); }
                catch (ObjectDisposedException) { Unregister(lens); }
            }
        }
    }
}
