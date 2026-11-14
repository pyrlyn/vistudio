using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TextManager.Interop;
using Newtonsoft.Json.Linq;
using Vistudio.Core.CodeLens;

namespace Vistudio
{
    [VisualStudioContribution]
    internal sealed class JvmReferencesCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
        {
            AppliesTo = new[] { DocumentFilter.FromDocumentType("text") },
        };

        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("Kotlin and Java references") { Priority = 100 };

        public Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token)
        {
            var element = JvmCodeElement.TryParse(codeElement, codeElementContext);
            return Task.FromResult<CodeLens?>(element == null || element.Element.IsGradleTask ? null : new JvmCountCodeLens(element, JvmCountCodeLens.Query.References));
        }
    }

    [VisualStudioContribution]
    internal sealed class JvmImplementationsCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
        {
            AppliesTo = new[] { DocumentFilter.FromDocumentType("text") },
        };

        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("Kotlin and Java implementations") { Priority = 200 };

        public Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token)
        {
            var element = JvmCodeElement.TryParse(codeElement, codeElementContext);
            // Kotlin `open` members with a body are overridden rather than implemented: same query, other noun.
            return Task.FromResult<CodeLens?>(element == null || !element.CanBeImplemented ? null
                : new JvmCountCodeLens(element, element.Element.Has(CodeDeclarationTraits.Open) ? JvmCountCodeLens.Query.Overrides : JvmCountCodeLens.Query.Implementations));
        }
    }

    [VisualStudioContribution]
    internal sealed class JvmRunCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
        {
            AppliesTo = new[] { DocumentFilter.FromDocumentType("text") },
        };

        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("Run Kotlin or Java main") { Priority = 10 };

        public Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token)
        {
            var element = JvmCodeElement.TryParse(codeElement, codeElementContext);
            return Task.FromResult<CodeLens?>(element == null || !element.IsMain ? null : new JvmLaunchCodeLens(debug: false));
        }
    }

    [VisualStudioContribution]
    internal sealed class JvmDebugCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
        {
            AppliesTo = new[] { DocumentFilter.FromDocumentType("text") },
        };

        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("Debug Kotlin or Java main") { Priority = 20 };

        public Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token)
        {
            var element = JvmCodeElement.TryParse(codeElement, codeElementContext);
            return Task.FromResult<CodeLens?>(element == null || !element.IsMain ? null : new JvmLaunchCodeLens(debug: true));
        }
    }

    /// <summary>
    /// "N references" / "N implementations" from the language server that owns the file (Kotlin LSP for
    /// <c>.kt</c>, Eclipse JDT LS for <c>.java</c>). A click puts the caret on the name and runs Visual Studio's
    /// Find All References or Go To Implementation, which the same server answers.
    /// </summary>
    internal sealed class JvmCountCodeLens : InvokableCodeLens, IJvmServerLens
    {
        /// <summary>Overrides and Kotlin actuals use the same implementation request as implementations.</summary>
        internal enum Query { References, Implementations, Overrides, Actuals }

        private readonly JvmCodeElement element;
        private readonly Query query;

        internal JvmCountCodeLens(JvmCodeElement element, Query query)
        {
            this.element = element;
            this.query = query;
            JvmCodeLensRefresh.Register(this);
        }

        public override async Task<CodeLensLabel> GetLabelAsync(CodeElementContext codeElementContext, CancellationToken token)
        {
            var noun = query switch { Query.References => "reference", Query.Overrides => "override", Query.Actuals => "actual", _ => "implementation" };
            var server = element.Java ? "Java language server (Eclipse JDT LS)" : "Kotlin language server";
            var position = JvmCodeLensPosition.Of(codeElementContext.Range);
            JToken? result;
            try
            {
                // Readiness is checked first: a ready server answers `null` when there is nothing to list (Kotlin LSP
                // does so for implementations), which must read as 0, not as "waiting" (that re-queried every lens
                // in a loop).
                if (!await JvmCodeLensServers.IsReadyAsync(element.Java))
                {
                    JvmCodeLensRefresh.WaitForServer(element.Java);
                    return new CodeLensLabel { Text = "- " + noun + "s", Tooltip = "Waiting for the " + server + " (initialization and Gradle import)." };
                }
                result = await JvmCodeLensServers.RequestAsync(element.Java,
                    query == Query.References ? "textDocument/references" : "textDocument/implementation",
                    query == Query.References
                        ? (object)new { textDocument = new { uri = position.Uri }, position = new { line = position.Line, character = position.Character }, context = new { includeDeclaration = false } }
                        : new { textDocument = new { uri = position.Uri }, position = new { line = position.Line, character = position.Character } },
                    token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                return new CodeLensLabel { Text = "? " + noun + "s", Tooltip = "The " + server + " failed: " + e.Message };
            }
            var count = result is JArray array ? array.Count : result != null && result.Type == JTokenType.Object ? 1 : 0;
            var text = CodeLensElement.CountLabel(count, noun);
            if (query == Query.Actuals && count > 0) text += " (" + JvmCodeLensServers.TargetsOf(result!) + ")";
            return new CodeLensLabel
            {
                Text = text,
                Tooltip = text + " (" + server + "). Click to " + (query == Query.References ? "find all references." : "go to the " + noun + "s."),
            };
        }

        public override async Task ExecuteAsync(CodeElementContext codeElementContext, IClientContext clientContext, CancellationToken cancelToken)
        {
            var position = JvmCodeLensPosition.Of(codeElementContext.Range);
            await JvmCodeLensServers.ExecuteAtAsync(position, query == Query.References ? "Edit.FindAllReferences" : "Edit.GoToImplementation", cancelToken);
        }

        /// <summary>Asks Visual Studio for a new label (counts changed or the server became ready).</summary>
        public void Refresh() => Invalidate();

        public override void Dispose() => JvmCodeLensRefresh.Unregister(this);
    }

    /// <summary>"Run" / "Debug" above a JVM entry point: the same launch as F5 / Ctrl+F5 on the document.</summary>
    internal sealed class JvmLaunchCodeLens : InvokableCodeLens
    {
        private readonly bool debug;
        internal JvmLaunchCodeLens(bool debug) => this.debug = debug;

        public override void Dispose() { }

        public override Task<CodeLensLabel> GetLabelAsync(CodeElementContext codeElementContext, CancellationToken token) =>
            Task.FromResult(new CodeLensLabel
            {
                Text = debug ? "Debug" : "Run",
                Tooltip = debug ? "Build with Gradle and debug this entry point in the Kotlin/JVM debugger." : "Build with Gradle and run this entry point without debugging.",
            });

        public override async Task ExecuteAsync(CodeElementContext codeElementContext, IClientContext clientContext, CancellationToken cancelToken)
        {
            var uri = codeElementContext.Range.Document.Uri;
            if (!uri.IsFile) return;
            var model = await AsyncServiceProvider.GlobalProvider.GetServiceAsync<SComponentModel, IComponentModel>();
            var environment = model.GetService<KotlinEnvironment>();
            var coordinator = model.GetService<KotlinDebugCoordinator>();
            var workspace = await environment.GetFolderAsync();
            if (workspace == null)
            {
                environment.Log("Open the Gradle project folder or its .kotlinproj before running " + System.IO.Path.GetFileName(uri.LocalPath) + ".");
                await environment.ShowOutputAsync();
                return;
            }
            var config = new JObject { ["project"] = uri.LocalPath, ["noDebug"] = !debug };
            // The launch outlives the click: Visual Studio cancels lens invocations when the editor moves on.
            await coordinator.LaunchAsync(workspace, config, uri.LocalPath, CancellationToken.None);
        }
    }

    /// <summary>LSP position of a declaration name: the tagged range ends at the name.</summary>
    internal readonly struct JvmCodeLensPosition
    {
        private JvmCodeLensPosition(string path, string uri, int line, int character) { Path = path; Uri = uri; Line = line; Character = character; }
        internal string Path { get; }
        internal string Uri { get; }
        internal int Line { get; }
        internal int Character { get; }

        internal static JvmCodeLensPosition Of(TextRange range)
        {
            var document = range.Document;
            var (line, character) = CodeLensElement.NamePosition(document.Text.CopyToString(), range.Start, range.End);
            var path = document.Uri.IsFile ? document.Uri.LocalPath : document.Uri.ToString();
            var uri = document.Uri.IsFile ? new Uri(path).AbsoluteUri : document.Uri.ToString();
            return new JvmCodeLensPosition(path, uri, line, character);
        }
    }

    internal static class JvmCodeLensServers
    {
        /// <summary>Platform names of the source sets of LSP locations: <c>jvm, js, mingwX64</c>.</summary>
        internal static string TargetsOf(JToken locations)
        {
            var targets = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var location in locations is JArray array ? (IEnumerable<JToken>)array : new[] { locations })
            {
                var uri = (string?)location["uri"] ?? (string?)location["targetUri"];
                if (uri == null || !System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) continue;
                var set = KotlinSourceSets.SourceSetOf(parsed.LocalPath);
                if (set != null) targets.Add(KotlinSourceSets.TargetName(set));
            }
            return string.Join(", ", targets);
        }

        internal static async Task<JToken?> RequestAsync(bool java, string method, object args, CancellationToken token)
        {
            var model = await AsyncServiceProvider.GlobalProvider.GetServiceAsync<SComponentModel, IComponentModel>();
            return java
                ? await model.GetService<JavaLanguageClient>().RequestAsync(method, args, token)
                : await model.GetService<KotlinLanguageClient>().RequestAsync(method, args, token);
        }

        internal static async Task<bool> IsReadyAsync(bool java)
        {
            var model = await AsyncServiceProvider.GlobalProvider.GetServiceAsync<SComponentModel, IComponentModel>();
            return java ? model.GetService<JavaLanguageClient>().IsServerReady : model.GetService<KotlinLanguageClient>().IsServerReady;
        }

        /// <summary>Puts the caret on the declaration name and runs a Visual Studio command there.</summary>
        internal static async Task ExecuteAtAsync(JvmCodeLensPosition position, string command, CancellationToken token)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, position.Path, Guid.Empty, out _, out _, out var frame, out IVsTextView view);
            frame?.Show();
            if (view == null) return;
            view.SetCaretPos(position.Line, position.Character);
            view.SendExplicitFocus();
            var dte = await AsyncServiceProvider.GlobalProvider.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            dte.ExecuteCommand(command);
        }
    }

    /// <summary>
    /// Counts depend on the whole workspace: after edits (debounced) and once a waiting server becomes ready,
    /// every live count lens asks again.
    /// </summary>
    internal static class JvmCodeLensRefresh
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<IJvmServerLens> Lenses = new HashSet<IJvmServerLens>();
        private static CancellationTokenSource? pending;
        private static readonly bool[] waiting = new bool[2];

        internal static void Register(IJvmServerLens lens) { lock (Gate) Lenses.Add(lens); }
        internal static void Unregister(IJvmServerLens lens) { lock (Gate) Lenses.Remove(lens); }

        internal static void Schedule(int delayMilliseconds = 2000)
        {
            CancellationTokenSource source;
            lock (Gate)
            {
                pending?.Cancel();
                pending = source = new CancellationTokenSource();
            }
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(delayMilliseconds, source.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                InvalidateAll();
            });
        }

        internal static void WaitForServer(bool java)
        {
            lock (Gate)
            {
                if (waiting[java ? 1 : 0]) return;
                waiting[java ? 1 : 0] = true;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    // Gradle import of a large project takes minutes; give up after an hour.
                    for (var i = 0; i < 1800 && !await JvmCodeLensServers.IsReadyAsync(java).ConfigureAwait(false); i++)
                        await Task.Delay(2000).ConfigureAwait(false);
                }
                catch (Exception) { }
                finally { lock (Gate) waiting[java ? 1 : 0] = false; }
                InvalidateAll();
            });
        }

        private static void InvalidateAll()
        {
            IJvmServerLens[] current;
            lock (Gate) current = new List<IJvmServerLens>(Lenses).ToArray();
            foreach (var lens in current)
            {
                try { lens.Refresh(); }
                catch (ObjectDisposedException) { Unregister(lens); }
            }
        }
    }

    /// <summary>A lens whose label comes from a language server and is asked again after edits or server start.</summary>
    internal interface IJvmServerLens
    {
        void Refresh();
    }
}
