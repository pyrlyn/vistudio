using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using Vistudio.Core;
using Vistudio.Core.CodeLens;
using Vistudio.Core.Languages;
using Vistudio.Core.Packages;
using Vistudio.Core.Rust;
using Vistudio.Core.Wasm;
using Vistudio.Core.Zig;
using Vistudio.Languages;
using Vistudio.Rust;
using Vistudio.Wasm;

namespace Vistudio
{
    /// <summary>
    /// A language whose CodeLens elements come from its language server's document symbols instead of a Vistudio
    /// scanner (Rust through rust-analyzer, WebAssembly text through wat_server).
    /// </summary>
    internal sealed class LspLensLanguage
    {
        internal static readonly LspLensLanguage Rust = new LspLensLanguage(LspSymbolLens.Rust, "rust-analyzer", "Rust",
            path => LanguageCatalog.Rust.Claims(path), () => RustSettings.Load().CodeLens, () => RustLanguageServer.Find(RustSettings.Load()) != null,
            model => model.GetService<RustLanguageClient>());

        internal static readonly LspLensLanguage Wat = new LspLensLanguage(LspSymbolLens.Wat, "wat_server", "WebAssembly",
            WasmLanguageServer.IsServerFile, () => WasmSettings.Load().CodeLens, () => WasmLanguageServer.Find(WasmSettings.Load()) != null,
            model => model.GetService<WasmLanguageClient>());

        private static readonly LspLensLanguage[] All = { Rust, Wat };
        private readonly Func<string, bool> claims;
        private readonly Func<bool> codeLens, serverFound;
        private readonly Func<IComponentModel, StdioLanguageClient> client;

        private LspLensLanguage(string id, string server, string optionsPage, Func<string, bool> claims, Func<bool> codeLens, Func<bool> serverFound,
            Func<IComponentModel, StdioLanguageClient> client)
        {
            Id = id; Server = server; OptionsPage = optionsPage;
            this.claims = claims; this.codeLens = codeLens; this.serverFound = serverFound; this.client = client;
        }

        /// <summary><see cref="LspSymbolLens"/> language id.</summary>
        internal string Id { get; }
        internal string Server { get; }
        /// <summary>The Tools &gt; Options page name.</summary>
        internal string OptionsPage { get; }
        internal LensRefresh Refresh { get; } = new LensRefresh();
        internal bool CodeLensEnabled => codeLens();
        internal bool ServerFound => serverFound();

        internal static LspLensLanguage? For(Uri uri)
        {
            var path = PathOf(uri);
            return All.FirstOrDefault(language => language.claims(path));
        }

        internal static LspLensLanguage? For(CodeElement element)
        {
            var id = LspSymbolLens.LanguageOf(element.UniqueIdentifier);
            return All.FirstOrDefault(language => language.Id == id);
        }

        internal static string PathOf(Uri uri) => uri.IsFile ? uri.LocalPath : uri.ToString();

        internal async Task<StdioLanguageClient> ClientAsync()
        {
            var model = await AsyncServiceProvider.GlobalProvider.GetServiceAsync<SComponentModel, IComponentModel>();
            return client(model);
        }
    }

    /// <summary>
    /// Tags Rust and WAT declarations as CodeLens code elements from <c>textDocument/documentSymbol</c>. Elements appear
    /// once the server is ready and are asked again whenever the language's lenses refresh (after edits, debounced), so
    /// the symbols come from text the server has already received.
    /// </summary>
    [VisualStudioContribution]
    internal sealed class LspCodeLensTaggerProvider : ExtensionPart, ITextViewTaggerProvider<CodeLensTag>, ITextViewChangedListener
    {
        // Matched by file name in code, like the Zig and Dart taggers: the content type may be assigned late.
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new() { AppliesTo = new[] { DocumentFilter.FromDocumentType("text") } };

        public Task<TextViewTaggerBase<CodeLensTag>> CreateTaggerAsync(ITextViewSnapshot textView, CancellationToken cancellationToken) =>
            Task.FromResult<TextViewTaggerBase<CodeLensTag>>(new LspCodeLensTagger(this, textView.Document.Uri, LspLensLanguage.For(textView.Document.Uri)));

        public Task TextViewChangedAsync(TextViewChangedArgs args, CancellationToken cancellationToken)
        {
            if (args.Edits.Count == 0) return Task.CompletedTask;
            LspCodeLensTagger.DocumentChanged(args.AfterTextView.Document);
            LspLensLanguage.For(args.AfterTextView.Document.Uri)?.Refresh.Schedule();
            return Task.CompletedTask;
        }
    }

    internal sealed class LspCodeLensTagger : TextViewTagger<CodeLensTag>
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<Uri, List<LspCodeLensTagger>> Open = new Dictionary<Uri, List<LspCodeLensTagger>>();
        private readonly LspLensLanguage? language;
        private readonly Uri uri;
        private ITextDocumentSnapshot? latest;
        private int version;

        public LspCodeLensTagger(LspCodeLensTaggerProvider provider, Uri uri, LspLensLanguage? language)
        {
            this.uri = uri;
            this.language = language;
            Initialize(provider, uri);
            if (language == null) return;
            lock (Gate)
            {
                if (!Open.TryGetValue(uri, out var list)) Open[uri] = list = new List<LspCodeLensTagger>();
                list.Add(this);
            }
            language.Refresh.Invalidated += OnInvalidated;
        }

        /// <summary>Remembers the newest text; the tags follow at the next refresh, when the server has seen it.</summary>
        internal static void DocumentChanged(ITextDocumentSnapshot document)
        {
            lock (Gate)
                if (Open.TryGetValue(document.Uri, out var list))
                    foreach (var tagger in list) tagger.latest = document;
        }

        protected override Task OnRequestTagsAsync(NormalizedTextRangeCollection requestedRanges, bool recalculateAll, CancellationToken cancellationToken)
        {
            if (language == null || requestedRanges.Count == 0) return Task.CompletedTask;
            latest = requestedRanges.First().Document;
            return UpdateAllAsync(cancellationToken);
        }

        protected override Task OnTextViewChangedAsync(TextViewChangedArgs args, CancellationToken cancellationToken) => Task.CompletedTask;

        private void OnInvalidated() => _ = Task.Run(async () =>
        {
            try { await UpdateAllAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { } // a closed view or a failed request keeps the last tags
        });

        private async Task UpdateAllAsync(CancellationToken cancellationToken)
        {
            var document = latest;
            if (language == null || document == null) return;
            var run = Interlocked.Increment(ref version);
            var tags = new List<TaggedTrackingTextRange<CodeLensTag>>();
            if (language.CodeLensEnabled)
            {
                var client = await language.ClientAsync().ConfigureAwait(false);
                if (!client.IsServerReady)
                {
                    language.Refresh.WaitForServer(language.ClientAsync);
                    return;
                }
                var text = document.Text.CopyToString();
                JToken? result;
                try
                {
                    result = await client.RequestAsync("textDocument/documentSymbol", new { textDocument = new { uri = new LensDocument(uri).Uri } }, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { return; }
                foreach (var element in LspSymbolLens.FromDocumentSymbols(result, language.Id, text))
                {
                    var kind = element.Kind == "Function" || element.Kind == "Method" ? CodeElementKind.KnownValues.Method : CodeElementKind.KnownValues.Type;
                    var tag = new CodeLensTag(kind) { Description = element.QualifiedName, UniqueIdentifier = element.Identifier };
                    // The range ends at the name: the reference lens queries the server at the name's last character.
                    tags.Add(new TaggedTrackingTextRange<CodeLensTag>(
                        new TextRange(document, element.Start, element.NameEnd - element.Start), TextRangeTrackingMode.ExtendForwardAndBackward, tag));
                }
            }
            if (run != Volatile.Read(ref version)) return; // a newer update owns the tags
            await UpdateTagsAsync(new[] { new TextRange(document, 0, document.Length) }, tags, cancellationToken).ConfigureAwait(false);
        }

        public override void Dispose()
        {
            if (language != null)
            {
                language.Refresh.Invalidated -= OnInvalidated;
                lock (Gate)
                {
                    if (Open.TryGetValue(uri, out var list))
                    {
                        list.Remove(this);
                        if (list.Count == 0) Open.Remove(uri);
                    }
                }
            }
            base.Dispose();
        }
    }

    /// <summary>File path and the URI form Visual Studio's LSP client uses for it.</summary>
    internal readonly struct LensDocument
    {
        internal LensDocument(Uri document)
        {
            Path = LspLensLanguage.PathOf(document);
            Uri = document.IsFile ? new Uri(Path).AbsoluteUri : document.ToString();
        }

        internal string Path { get; }
        internal string Uri { get; }
    }

    [VisualStudioContribution]
    internal sealed class LspReferencesCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new() { AppliesTo = new[] { DocumentFilter.FromDocumentType("text") } };
        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("Rust and WebAssembly references") { Priority = 110 };

        public Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token) =>
            Task.FromResult<CodeLens?>(LspLensLanguage.For(codeElement) is LspLensLanguage language ? new LspReferencesCodeLens(language) : null);
    }

    [VisualStudioContribution]
    internal sealed class RustRunCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new() { AppliesTo = new[] { DocumentFilter.FromDocumentType("text") } };
        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("Rust run") { Priority = 100 };

        public Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token) =>
            Task.FromResult<CodeLens?>(LspSymbolLens.IsMain(codeElement.UniqueIdentifier) ? new RustLaunchCodeLens(debug: false) : null);
    }

    [VisualStudioContribution]
    internal sealed class RustDebugCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new() { AppliesTo = new[] { DocumentFilter.FromDocumentType("text") } };
        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("Rust debug") { Priority = 101 };

        public Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token) =>
            Task.FromResult<CodeLens?>(LspSymbolLens.IsMain(codeElement.UniqueIdentifier) ? new RustLaunchCodeLens(debug: true) : null);
    }

    /// <summary>"N references" from the language server; a click runs Find All References at the name.</summary>
    internal sealed class LspReferencesCodeLens : InvokableCodeLens, IRefreshableLens
    {
        private readonly LspLensLanguage language;

        internal LspReferencesCodeLens(LspLensLanguage language)
        {
            this.language = language;
            language.Refresh.Register(this);
        }

        public override async Task<CodeLensLabel> GetLabelAsync(CodeElementContext codeElementContext, CancellationToken token)
        {
            var position = LensPosition.Of(codeElementContext.Range);
            if (!language.ServerFound)
                return new CodeLensLabel { Text = "- references", Tooltip = "Install " + language.Server + " or set Tools > Options > " + language.OptionsPage + " to count references." };
            var client = await language.ClientAsync();
            if (!client.IsServerReady)
            {
                language.Refresh.WaitForServer(language.ClientAsync);
                return new CodeLensLabel { Text = "- references", Tooltip = "Waiting for " + language.Server + "." };
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
            catch (Exception e) { return new CodeLensLabel { Text = "? references", Tooltip = language.Server + " failed: " + e.Message }; }
            var count = result is JArray array ? array.Count : 0;
            var text = CodeLensElement.CountLabel(count, "reference");
            return new CodeLensLabel { Text = text, Tooltip = text + " (" + language.Server + "). Click to find all references." };
        }

        public override Task ExecuteAsync(CodeElementContext codeElementContext, IClientContext clientContext, CancellationToken cancelToken) =>
            LensCommands.ExecuteAtAsync(LensPosition.Of(codeElementContext.Range), "Edit.FindAllReferences", cancelToken);

        public void Refresh() => Invalidate();

        public override void Dispose() => language.Refresh.Unregister(this);
    }

    /// <summary>
    /// "Run" / "Debug" above a Rust <c>fn main</c>. In a <c>.rustproj</c> the project becomes the startup project and
    /// Visual Studio's own Debug.Start / Debug.StartWithoutDebugging build and launch it (native engine, see
    /// LanguageProjectConfig); elsewhere <c>cargo run</c> runs in the Rust Output pane, which cannot debug.
    /// </summary>
    internal sealed class RustLaunchCodeLens : InvokableCodeLens
    {
        private static int cargoRunning;
        private readonly bool debug;

        internal RustLaunchCodeLens(bool debug) => this.debug = debug;

        public override void Dispose() { }

        public override async Task<CodeLensLabel> GetLabelAsync(CodeElementContext codeElementContext, CancellationToken token)
        {
            var path = LspLensLanguage.PathOf(codeElementContext.Range.Document.Uri);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            var project = ProjectFor(path);
            var text = debug ? "Debug" : "Run";
            var tooltip = project != null
                ? (debug ? "Build and debug " : "Build and run ") + project.Name + " (Visual Studio " + (debug ? "Debug > Start Debugging" : "Debug > Start Without Debugging") + ")."
                : debug ? "This file is not in a .rustproj: runs cargo run in the Rust pane without the debugger. Add a Rust project to debug."
                : "cargo run in the Rust Output pane.";
            return new CodeLensLabel { Text = text, Tooltip = tooltip };
        }

        public override async Task ExecuteAsync(CodeElementContext codeElementContext, IClientContext clientContext, CancellationToken cancelToken)
        {
            var path = LspLensLanguage.PathOf(codeElementContext.Range.Document.Uri);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancelToken);
            var dte = await AsyncServiceProvider.GlobalProvider.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancelToken);
            var project = ProjectFor(path);
            if (project != null && await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsSolutionBuildManager)) is IVsSolutionBuildManager builds)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancelToken);
                ErrorHandler.ThrowOnFailure(builds.set_StartupProject(project));
                dte.ExecuteCommand(debug ? "Debug.Start" : "Debug.StartWithoutDebugging");
                return;
            }
            dte.ExecuteCommand("File.SaveAll");
            var client = (RustLanguageClient)await LspLensLanguage.Rust.ClientAsync();
            // The program outlives the click: its run is not tied to the lens command's token.
            await CargoRunAsync(client.Output, path, CancellationToken.None);
        }

        /// <summary>The open .rustproj whose folder contains <paramref name="path"/> (the innermost one), or null.</summary>
        private static LanguageProjectNode? ProjectFor(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(ServiceProvider.GlobalProvider.GetService(typeof(SVsShell)) is IVsShell shell)) return null;
            var guid = new Guid(LanguagesPackage.PackageGuid);
            // Loaded whenever a language project is open: it owns the project factory.
            if (shell.IsPackageLoaded(ref guid, out var loaded) != VSConstants.S_OK || !(loaded is LanguagesPackage package)) return null;
            return package.Projects.All
                .Where(p => p.Language.Id == LanguageCatalog.Rust.Id && IsInside(path, p.Metadata.DirectoryPath))
                .OrderByDescending(p => p.Metadata.DirectoryPath.Length)
                .FirstOrDefault();
        }

        private static bool IsInside(string path, string folder)
        {
            var prefix = folder.TrimEnd('\\', '/') + "\\";
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private async Task CargoRunAsync(LanguageEnvironment output, string path, CancellationToken token)
        {
            await output.ShowOutputAsync();
            if (debug) output.Log("Debugging needs a Rust project (.rustproj) around " + path + "; running without the debugger.");
            var command = RustCargo.RunCommand(path);
            if (command == null) { output.Log("No Cargo.toml was found above " + path + "."); return; }
            var cargo = ToolLocator.Find(new[] { "cargo" }) ?? ToolLocator.Find(new[] { "cargo" }, LanguageProjectDefaults.InstallFolders(LanguageCatalog.Rust));
            if (cargo == null) { output.Log("cargo was not found on PATH or in %USERPROFILE%\\.cargo\\bin."); return; }
            if (Interlocked.Exchange(ref cargoRunning, 1) == 1) { output.Log("cargo run is already running; wait for it to finish."); return; }
            try
            {
                var arguments = ZigArguments.Join(command.Value.Arguments);
                output.Log("> cargo " + arguments + "  (in " + command.Value.Folder + ")");
                var exit = await OwnedProcess.RunAsync(ProcessCommands.CreateRedirected(cargo, arguments, command.Value.Folder), output.Log, token).ConfigureAwait(false);
                output.Log("cargo exited with code " + exit + ".");
            }
            catch (OperationCanceledException) { output.Log("cargo run canceled."); }
            catch (Exception e) { output.Log("cargo run: " + e.Message); }
            finally { Volatile.Write(ref cargoRunning, 0); }
        }
    }
}
