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
using Vistudio.Core.Zig;

namespace Vistudio.Zig
{
    /// <summary>
    /// Marks Zig function declarations as CodeLens code elements (identity <c>vistudiozig|Function|Qualified.name[#n]</c>).
    /// Lives next to the Kotlin/Java parts in the same in-process VisualStudio.Extensibility extension.
    /// </summary>
    [VisualStudioContribution]
    internal sealed class ZigCodeLensTaggerProvider : ExtensionPart, ITextViewTaggerProvider<CodeLensTag>, ITextViewChangedListener
    {
        private readonly object gate = new object();
        private readonly Dictionary<Uri, List<ZigCodeLensTagger>> taggers = new Dictionary<Uri, List<ZigCodeLensTagger>>();

        // Matched by file name in code, like the Kotlin/Java tagger: the `zig` content type may be assigned late.
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
        {
            AppliesTo = new[] { DocumentFilter.FromDocumentType("text") },
        };

        public Task<TextViewTaggerBase<CodeLensTag>> CreateTaggerAsync(ITextViewSnapshot textView, CancellationToken cancellationToken)
        {
            var tagger = new ZigCodeLensTagger(this, textView.Document.Uri);
            lock (gate)
            {
                if (!taggers.TryGetValue(tagger.Uri, out var list)) taggers[tagger.Uri] = list = new List<ZigCodeLensTagger>();
                list.Add(tagger);
            }
            return Task.FromResult<TextViewTaggerBase<CodeLensTag>>(tagger);
        }

        internal void Remove(ZigCodeLensTagger tagger)
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
            if (!ZigCodeElement.IsZig(uri)) return;
            ZigCodeLensTagger[] current;
            lock (gate) current = taggers.TryGetValue(uri, out var list) ? list.ToArray() : Array.Empty<ZigCodeLensTagger>();
            foreach (var tagger in current) await tagger.DocumentChangedAsync(args.AfterTextView.Document, cancellationToken);
            ZigCodeLensRefresh.Schedule();
        }
    }

    internal sealed class ZigCodeLensTagger : TextViewTagger<CodeLensTag>
    {
        private readonly ZigCodeLensTaggerProvider provider;

        public ZigCodeLensTagger(ZigCodeLensTaggerProvider provider, Uri uri)
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
            if (!ZigCodeElement.IsZig(document.Uri) || !ZigSettings.Load().CodeLens) return;
            var functions = ZigSourceScanner.Functions(document.Text.CopyToString());
            var identifiers = ZigCodeElement.Identifiers(functions);
            var tags = new List<TaggedTrackingTextRange<CodeLensTag>>();
            for (var i = 0; i < functions.Count; i++)
            {
                var function = functions[i];
                var tag = new CodeLensTag(CodeElementKind.KnownValues.Method) { Description = function.QualifiedName, UniqueIdentifier = identifiers[i] };
                // The range ends at the name: the lenses query the server at the name's last character.
                tags.Add(new TaggedTrackingTextRange<CodeLensTag>(
                    new TextRange(document, function.StartOffset, function.NameEnd - function.StartOffset), TextRangeTrackingMode.ExtendForwardAndBackward, tag));
            }
            await UpdateTagsAsync(new[] { new TextRange(document, 0, document.Length) }, tags, cancellationToken);
        }

        public override void Dispose()
        {
            provider.Remove(this);
            base.Dispose();
        }
    }

    internal static class ZigCodeElement
    {
        private const string Prefix = "vistudiozig|Function|";

        internal static bool IsZig(Uri uri) => ZigCommandFactory.IsZigFile(uri.IsFile ? uri.LocalPath : uri.ToString());

        internal static IReadOnlyList<string> Identifiers(IReadOnlyList<ZigDeclaration> functions) =>
            CodeLensElement.WithOrdinals(functions.Select(f => Prefix + f.QualifiedName));

        internal static bool IsOurs(CodeElement element) => element.UniqueIdentifier?.StartsWith(Prefix, StringComparison.Ordinal) == true;

        /// <summary>A top-level <c>main</c> (no container in its qualified name), the program's entry point.</summary>
        internal static bool IsMain(CodeElement element) => IsMain(element.UniqueIdentifier);

        internal static bool IsMain(string? identifier) =>
            identifier != null && (identifier == Prefix + "main" || identifier.StartsWith(Prefix + "main#", StringComparison.Ordinal));

        /// <summary>The declaration under a tagged range, rescanned from the current text (offsets move with edits).</summary>
        internal static ZigDeclaration? Declaration(TextRange range)
        {
            var text = range.Document.Text.CopyToString();
            int start = range.Start, end = range.End;
            var functions = ZigSourceScanner.Functions(text);
            return functions.FirstOrDefault(f => f.NameEnd == end) ?? functions.FirstOrDefault(f => f.StartOffset >= start && f.NameEnd <= end);
        }
    }

    [VisualStudioContribution]
    internal sealed class ZigReferencesCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new() { AppliesTo = new[] { DocumentFilter.FromDocumentType("text") } };
        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("Zig references") { Priority = 110 };

        public Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token) =>
            Task.FromResult<CodeLens?>(ZigCodeElement.IsOurs(codeElement) ? new ZigReferencesCodeLens() : null);
    }

    [VisualStudioContribution]
    internal sealed class ZigSignatureCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new() { AppliesTo = new[] { DocumentFilter.FromDocumentType("text") } };
        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("Zig signature") { Priority = 120 };

        public Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token) =>
            Task.FromResult<CodeLens?>(ZigCodeElement.IsOurs(codeElement) ? new ZigSignatureCodeLens() : null);
    }

    /// <summary>"N references" from ZLS; a click runs Find All References at the function name.</summary>
    internal sealed class ZigReferencesCodeLens : InvokableCodeLens, IRefreshableLens
    {
        internal ZigReferencesCodeLens() => ZigCodeLensRefresh.Register(this);

        public override async Task<CodeLensLabel> GetLabelAsync(CodeElementContext codeElementContext, CancellationToken token)
        {
            var position = LensPosition.Of(codeElementContext.Range);
            var client = await ZigLensServices.ClientAsync();
            if (string.IsNullOrWhiteSpace(ZigSettings.Load().LanguageServerPath))
                return new CodeLensLabel { Text = "- references", Tooltip = "Set Tools > Options > Zig > ZLS path to count references." };
            if (!client.IsServerReady)
            {
                ZigCodeLensRefresh.WaitForServer();
                return new CodeLensLabel { Text = "- references", Tooltip = "Waiting for ZLS." };
            }
            JToken? result;
            try
            {
                result = await client.RequestAsync("textDocument/references", new
                {
                    textDocument = new { uri = position.Uri },
                    position = new { line = position.Line, character = position.Character },
                    context = new { includeDeclaration = false },
                }, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { return new CodeLensLabel { Text = "? references", Tooltip = "ZLS failed: " + e.Message }; }
            var count = result is JArray array ? array.Count : 0;
            var text = CodeLensElement.CountLabel(count, "reference");
            return new CodeLensLabel { Text = text, Tooltip = text + " (ZLS). Click to find all references." };
        }

        public override Task ExecuteAsync(CodeElementContext codeElementContext, IClientContext clientContext, CancellationToken cancelToken) =>
            LensCommands.ExecuteAtAsync(LensPosition.Of(codeElementContext.Range), "Edit.FindAllReferences", cancelToken);

        public void Refresh() => Invalidate();

        public override void Dispose() => ZigCodeLensRefresh.Unregister(this);
    }

    /// <summary>The function signature (<c>pub fn name(params) Return</c>); a click puts the caret on the declaration.</summary>
    internal sealed class ZigSignatureCodeLens : InvokableCodeLens
    {
        public override void Dispose() { }

        public override Task<CodeLensLabel> GetLabelAsync(CodeElementContext codeElementContext, CancellationToken token)
        {
            var declaration = ZigCodeElement.Declaration(codeElementContext.Range);
            var signature = declaration?.Signature ?? "fn";
            var shown = signature.Length > 120 ? signature.Substring(0, 117) + "..." : signature;
            return Task.FromResult(new CodeLensLabel
            {
                Text = shown,
                Tooltip = (declaration == null ? signature : declaration.QualifiedName + "\n" + signature) + "\nClick to go to the declaration.",
            });
        }

        public override async Task ExecuteAsync(CodeElementContext codeElementContext, IClientContext clientContext, CancellationToken cancelToken)
        {
            var range = codeElementContext.Range;
            var text = range.Document.Text.CopyToString();
            int nameStart = ZigCodeElement.Declaration(range)?.NameStart ?? range.Start;
            var (line, character) = CodeLensElement.NamePosition(text, nameStart, nameStart + 1);
            var path = LensPosition.Of(range).Path;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancelToken);
            ZigNavigation.Open(ServiceProvider.GlobalProvider, path, line, character);
        }
    }

    [VisualStudioContribution]
    internal sealed class ZigRunCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new() { AppliesTo = new[] { DocumentFilter.FromDocumentType("text") } };
        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("Zig run") { Priority = 100 };

        public Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token) =>
            Task.FromResult<CodeLens?>(ZigCodeElement.IsMain(codeElement) ? new ZigLaunchCodeLens(debug: false) : null);
    }

    [VisualStudioContribution]
    internal sealed class ZigDebugCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new() { AppliesTo = new[] { DocumentFilter.FromDocumentType("text") } };
        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("Zig debug") { Priority = 101 };

        public Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token) =>
            Task.FromResult<CodeLens?>(ZigCodeElement.IsMain(codeElement) ? new ZigLaunchCodeLens(debug: true) : null);
    }

    /// <summary>
    /// "Run" / "Debug" above a top-level Zig <c>main</c>, as for Rust (<see cref="LaunchCodeLens"/>): in a <c>.zigproj</c>
    /// Visual Studio builds the project and starts <c>zig-out\bin\&lt;name&gt;.exe</c> under its native debugger (Zig on
    /// Windows writes PDBs); elsewhere Tools &gt; Zig &gt; Run File (<c>zig run</c>) runs the file in the Zig Output pane.
    /// </summary>
    internal sealed class ZigLaunchCodeLens : LaunchCodeLens
    {
        internal ZigLaunchCodeLens(bool debug) : base(Core.Languages.LanguageCatalog.Zig, debug) { }

        protected override string RunWithoutProjectDescription => "zig run in the Zig Output pane";

        protected override async Task RunWithoutProjectAsync(string path, EnvDTE.DTE dte)
        {
            var model = await AsyncServiceProvider.GlobalProvider.GetServiceAsync<SComponentModel, IComponentModel>();
            var environment = model.GetService<ZigEnvironment>();
            var build = model.GetService<ZigBuildService>();
            await environment.ShowOutputAsync();
            if (Debug) environment.Log("Debugging needs a Zig project (.zigproj) around " + path + "; running without the debugger.");
            if (build.IsBusy) { environment.Log("A Zig command is already running; wait for it or use Tools > Zig > Cancel."); return; }
            try { await build.RunFileAsync(path); } // the run's output and exit code go to the Zig pane
            catch (Exception e) when (e is InvalidOperationException || e is System.IO.IOException || e is ArgumentException) { environment.Log("zig run: " + e.Message); }
        }
    }

    internal static class ZigLensServices
    {
        internal static async Task<ZigLanguageClient> ClientAsync()
        {
            var model = await AsyncServiceProvider.GlobalProvider.GetServiceAsync<SComponentModel, IComponentModel>();
            return model.GetService<ZigLanguageClient>();
        }
    }

    /// <summary>Reference counts are workspace-wide: refreshed after edits (debounced) and when ZLS becomes ready.</summary>
    internal static class ZigCodeLensRefresh
    {
        private static readonly LensRefresh Lenses = new LensRefresh();

        internal static void Register(ZigReferencesCodeLens lens) => Lenses.Register(lens);
        internal static void Unregister(ZigReferencesCodeLens lens) => Lenses.Unregister(lens);
        internal static void Schedule(int delayMilliseconds = 1500) => Lenses.Schedule(delayMilliseconds);

        /// <summary>
        /// After ZLS reports "Loaded build file", references of a declaration start to include importing files a few
        /// seconds later (about 5.6 s for samples/zig-hello with ZLS 0.16.0), so the counts are asked again in steps.
        /// </summary>
        internal static void AfterBuildLoaded() => Lenses.InSteps(new[] { 1, 3, 6, 10, 20 });

        internal static void WaitForServer() => Lenses.WaitForServer(async () => await ZigLensServices.ClientAsync().ConfigureAwait(false));

        internal static void InvalidateAll() => Lenses.InvalidateAll();
    }
}
