using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Shell;
using Vistudio.Core.Mise;

namespace Vistudio.Mise
{
    /// <summary>
    /// Marks the <c>[tools]</c> header of a mise config (the first tool line of <c>.tool-versions</c>) as a CodeLens code
    /// element (identity <c>vistudiomise|Tools|&lt;path&gt;</c>). Lives next to the Kotlin/Java/Zig parts in the same
    /// in-process VisualStudio.Extensibility extension.
    /// </summary>
    [VisualStudioContribution]
    internal sealed class MiseCodeLensTaggerProvider : ExtensionPart, ITextViewTaggerProvider<CodeLensTag>, ITextViewChangedListener
    {
        private readonly object gate = new object();
        private readonly Dictionary<Uri, List<MiseCodeLensTagger>> taggers = new Dictionary<Uri, List<MiseCodeLensTagger>>();

        // Matched by file name in code: mise configs have no content type of their own.
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new() { AppliesTo = new[] { DocumentFilter.FromDocumentType("text") } };

        public Task<TextViewTaggerBase<CodeLensTag>> CreateTaggerAsync(ITextViewSnapshot textView, CancellationToken cancellationToken)
        {
            var tagger = new MiseCodeLensTagger(this, textView.Document.Uri);
            lock (gate)
            {
                if (!taggers.TryGetValue(tagger.Uri, out var list)) taggers[tagger.Uri] = list = new List<MiseCodeLensTagger>();
                list.Add(tagger);
            }
            return Task.FromResult<TextViewTaggerBase<CodeLensTag>>(tagger);
        }

        internal void Remove(MiseCodeLensTagger tagger)
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
            if (!MiseLens.IsMise(uri)) return;
            MiseCodeLensTagger[] current;
            lock (gate) current = taggers.TryGetValue(uri, out var list) ? list.ToArray() : Array.Empty<MiseCodeLensTagger>();
            foreach (var tagger in current) await tagger.DocumentChangedAsync(args.AfterTextView.Document, cancellationToken);
        }
    }

    internal sealed class MiseCodeLensTagger : TextViewTagger<CodeLensTag>
    {
        private readonly MiseCodeLensTaggerProvider provider;

        public MiseCodeLensTagger(MiseCodeLensTaggerProvider provider, Uri uri)
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
            if (!MiseLens.IsMise(document.Uri)) return;
            var tags = new List<TaggedTrackingTextRange<CodeLensTag>>();
            var path = MiseLens.Path(document.Uri);
            var layout = MiseToolLocator.Find(document.Text.CopyToString(), MiseConfigFiles.IsToolVersions(path));
            if (MiseService.Settings.CodeLens && layout.HeaderLine != null && layout.HeaderEnd > layout.HeaderStart)
            {
                var tag = new CodeLensTag(CodeElementKind.KnownValues.Method) { Description = "mise tools", UniqueIdentifier = MiseLens.Prefix + path };
                tags.Add(new TaggedTrackingTextRange<CodeLensTag>(
                    new TextRange(document, layout.HeaderStart, layout.HeaderEnd - layout.HeaderStart), TextRangeTrackingMode.ExtendForwardAndBackward, tag));
            }
            await UpdateTagsAsync(new[] { new TextRange(document, 0, document.Length) }, tags, cancellationToken);
        }

        public override void Dispose()
        {
            provider.Remove(this);
            base.Dispose();
        }
    }

    internal static class MiseLens
    {
        internal const string Prefix = "vistudiomise|Tools|";

        internal static string Path(Uri uri) => uri.IsFile ? uri.LocalPath : uri.ToString();
        internal static bool IsMise(Uri uri) => MiseConfigFiles.IsConfigFile(Path(uri));
        internal static bool IsOurs(CodeElement element) => element.UniqueIdentifier?.StartsWith(Prefix, StringComparison.Ordinal) == true;
        internal static string FileOf(CodeElementContext context) => Path(context.Range.Document.Uri);

        internal static async Task<MiseService> ServiceAsync()
        {
            var model = await AsyncServiceProvider.GlobalProvider.GetServiceAsync<SComponentModel, IComponentModel>();
            return model.GetService<MiseService>();
        }

        /// <summary>The report for the lens's file, or null while mise is still checking (the lens refreshes when it arrives).</summary>
        internal static async Task<(MiseService Service, MiseScope Scope, MiseReport? Report)> StateAsync(CodeElementContext context)
        {
            var service = await ServiceAsync();
            MiseLensRefresh.Attach(service);
            var scope = MiseScope.ForFile(FileOf(context));
            var report = service.Cached(scope.Folder);
            if (report == null || report.CreatedUtc == DateTime.MinValue) _ = service.GetReportAsync(scope.Folder);
            return (service, scope, report);
        }

        internal static Task RunAsync(MiseService service, MiseAction action, MiseScope scope) =>
            Task.Run(async () =>
            {
                try { await service.RunAsync(action, scope); }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    service.Log("mise: " + e.Message);
                    await service.ShowOutputAsync();
                }
            });

        internal static string ToolLines(IEnumerable<MiseToolStatus> tools, Func<MiseToolStatus, bool> include) =>
            string.Join("\n", tools.Where(include).Select(t => "  " + t.Name + ": " + t.Hint()));
    }

    [VisualStudioContribution]
    internal sealed class MiseStatusCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new() { AppliesTo = new[] { DocumentFilter.FromDocumentType("text") } };
        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("mise status") { Priority = 100 };

        public Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token) =>
            Task.FromResult<CodeLens?>(MiseLens.IsOurs(codeElement) ? new MiseStatusCodeLens() : null);
    }

    [VisualStudioContribution]
    internal sealed class MiseUpgradeCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new() { AppliesTo = new[] { DocumentFilter.FromDocumentType("text") } };
        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("mise upgrade") { Priority = 110 };

        public Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token) =>
            Task.FromResult<CodeLens?>(MiseLens.IsOurs(codeElement) ? new MiseUpgradeCodeLens(bump: false) : null);
    }

    [VisualStudioContribution]
    internal sealed class MiseUpgradeLatestCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new() { AppliesTo = new[] { DocumentFilter.FromDocumentType("text") } };
        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("mise upgrade --bump") { Priority = 120 };

        public Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token) =>
            Task.FromResult<CodeLens?>(MiseLens.IsOurs(codeElement) ? new MiseUpgradeCodeLens(bump: true) : null);
    }

    /// <summary>"mise: 1 not installed · 2 updates". A click installs the missing tools, or checks again when none is missing.</summary>
    internal sealed class MiseStatusCodeLens : InvokableCodeLens
    {
        internal MiseStatusCodeLens() => MiseLensRefresh.Register(this);

        public override async Task<CodeLensLabel> GetLabelAsync(CodeElementContext codeElementContext, CancellationToken token)
        {
            var (_, scope, report) = await MiseLens.StateAsync(codeElementContext);
            if (report == null) return new CodeLensLabel { Text = "mise: checking…", Tooltip = "mise is reading this config and looking for updates." };
            if (report.Tools.Count == 0 && report.Error != null)
                return new CodeLensLabel { Text = report.UntrustedPath != null ? "mise: not trusted" : "mise: error", Tooltip = report.Error + "\nClick to check again." };
            var tools = report.ForFiles(scope.Files);
            var missing = tools.Any(t => t.State == MiseToolState.Missing);
            var details = string.Join("\n", tools.Select(t => "  " + t.Name + ": " + (t.State == MiseToolState.Missing ? "" : t.State == MiseToolState.UpdateAvailable ? "↑ " : "✓ ") + t.Hint()));
            return new CodeLensLabel
            {
                Text = "mise: " + MiseReport.Summary(tools) + (report.Error != null ? " (update check failed)" : ""),
                Tooltip = details + (report.Error != null ? "\n" + report.Error : "") + "\n" + (missing ? "Click to install the missing tools (mise install)." : "Click to check again."),
            };
        }

        public override async Task ExecuteAsync(CodeElementContext codeElementContext, IClientContext clientContext, CancellationToken cancelToken)
        {
            var (service, scope, report) = await MiseLens.StateAsync(codeElementContext);
            if (report != null && report.ForFiles(scope.Files).Any(t => t.State == MiseToolState.Missing)) _ = MiseLens.RunAsync(service, MiseAction.Install, scope);
            else
            {
                var fresh = await service.GetReportAsync(scope.Folder, force: true);
                service.LogReport(scope, fresh);
            }
        }

        internal void Refresh() => Invalidate();
        public override void Dispose() => MiseLensRefresh.Unregister(this);
    }

    /// <summary>"Upgrade all" (mise upgrade, inside the requested versions) or "Upgrade all to latest" (mise upgrade --bump).</summary>
    internal sealed class MiseUpgradeCodeLens : InvokableCodeLens
    {
        private readonly bool bump;

        internal MiseUpgradeCodeLens(bool bump)
        {
            this.bump = bump;
            MiseLensRefresh.Register(this);
        }

        public override async Task<CodeLensLabel> GetLabelAsync(CodeElementContext codeElementContext, CancellationToken token)
        {
            var (_, scope, report) = await MiseLens.StateAsync(codeElementContext);
            var tools = report?.ForFiles(scope.Files) ?? Array.Empty<MiseToolStatus>();
            var count = MiseService.Targets(tools, bump ? MiseAction.UpgradeToLatest : MiseAction.Upgrade).Count;
            var text = (bump ? "Upgrade all to latest" : "Upgrade all") + (report == null ? "" : " (" + count + ")");
            var tooltip = bump
                ? "mise upgrade --bump: newest versions overall; rewrites the requested versions in this file.\n" + MiseLens.ToolLines(tools, t => t.State != MiseToolState.UpToDate)
                : "mise upgrade: newest versions inside the requested ones; this file is not changed.\n" + MiseLens.ToolLines(tools, t => t.State == MiseToolState.Missing || t.LatestInRange != null);
            return new CodeLensLabel { Text = text, Tooltip = tooltip.TrimEnd() };
        }

        public override async Task ExecuteAsync(CodeElementContext codeElementContext, IClientContext clientContext, CancellationToken cancelToken)
        {
            var (service, scope, _) = await MiseLens.StateAsync(codeElementContext);
            _ = MiseLens.RunAsync(service, bump ? MiseAction.UpgradeToLatest : MiseAction.Upgrade, scope);
        }

        internal void Refresh() => Invalidate();
        public override void Dispose() => MiseLensRefresh.Unregister(this);
    }

    /// <summary>Lenses are refreshed whenever a mise report changes.</summary>
    internal static class MiseLensRefresh
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<InvokableCodeLens> Lenses = new HashSet<InvokableCodeLens>();
        private static MiseService? attached;

        internal static void Register(InvokableCodeLens lens) { lock (Gate) Lenses.Add(lens); }
        internal static void Unregister(InvokableCodeLens lens) { lock (Gate) Lenses.Remove(lens); }

        internal static void Attach(MiseService service)
        {
            lock (Gate)
            {
                if (attached != null) return;
                attached = service;
            }
            service.ReportChanged += (_, __) => InvalidateAll();
            MiseService.SettingsChanged += (_, __) => InvalidateAll();
        }

        private static void InvalidateAll()
        {
            InvokableCodeLens[] current;
            lock (Gate) current = Lenses.ToArray();
            foreach (var lens in current)
            {
                try
                {
                    if (lens is MiseStatusCodeLens status) status.Refresh();
                    else if (lens is MiseUpgradeCodeLens upgrade) upgrade.Refresh();
                }
                catch (ObjectDisposedException) { Unregister(lens); }
            }
        }
    }
}
