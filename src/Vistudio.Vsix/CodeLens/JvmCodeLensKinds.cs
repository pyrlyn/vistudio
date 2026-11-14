using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Newtonsoft.Json.Linq;
using Vistudio.Core.CodeLens;

namespace Vistudio
{
    // Further lens kinds requested by the creator: overrides, Kotlin expect/actual, Compose preview, Gradle script tasks.

    [VisualStudioContribution]
    internal sealed class JvmOverrideCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
        {
            AppliesTo = new[] { DocumentFilter.FromDocumentType("text") },
        };

        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("Kotlin and Java overridden member") { Priority = 150 };

        public Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token)
        {
            var element = JvmCodeElement.TryParse(codeElement, codeElementContext);
            return Task.FromResult<CodeLens?>(element == null || !element.Element.Has(CodeDeclarationTraits.Override) ? null : new JvmOverrideBaseCodeLens(element));
        }
    }

    [VisualStudioContribution]
    internal sealed class KotlinExpectActualCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
        {
            AppliesTo = new[] { DocumentFilter.FromDocumentType("text") },
        };

        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("Kotlin expect and actual declarations") { Priority = 120 };

        public Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token)
        {
            var element = JvmCodeElement.TryParse(codeElement, codeElementContext);
            if (element == null || element.Java) return Task.FromResult<CodeLens?>(null);
            if (element.Element.Has(CodeDeclarationTraits.Expect)) return Task.FromResult<CodeLens?>(new JvmCountCodeLens(element, JvmCountCodeLens.Query.Actuals));
            if (element.Element.Has(CodeDeclarationTraits.Actual)) return Task.FromResult<CodeLens?>(new KotlinExpectCodeLens(element));
            return Task.FromResult<CodeLens?>(null);
        }
    }

    [VisualStudioContribution]
    internal sealed class ComposePreviewCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
        {
            AppliesTo = new[] { DocumentFilter.FromDocumentType("text") },
        };

        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("Compose preview") { Priority = 30 };

        public Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token)
        {
            var element = JvmCodeElement.TryParse(codeElement, codeElementContext);
            return Task.FromResult<CodeLens?>(element == null || !element.Element.Has(CodeDeclarationTraits.Preview) ? null : new ComposePreviewCodeLens());
        }
    }

    [VisualStudioContribution]
    internal sealed class GradleTaskCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
        {
            AppliesTo = new[] { DocumentFilter.FromDocumentType("text") },
        };

        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("Run Gradle task") { Priority = 10 };

        public Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token)
        {
            var element = JvmCodeElement.TryParse(codeElement, codeElementContext);
            return Task.FromResult<CodeLens?>(element == null || !element.Element.IsGradleTask ? null : new GradleTaskCodeLens(element));
        }
    }

    /// <summary>
    /// "overrides Base.member" above an override. No server answers "go to base" for Kotlin, so the lens walks the type
    /// hierarchy of the member's class (<c>textDocument/prepareTypeHierarchy</c> at the member, then
    /// <c>typeHierarchy/supertypes</c>) and looks for the member in each supertype's source, nearest first.
    /// </summary>
    internal sealed class JvmOverrideBaseCodeLens : InvokableCodeLens, IJvmServerLens
    {
        private const int MaxSupertypes = 64;
        private readonly JvmCodeElement element;
        private JvmBaseMember? found;

        internal JvmOverrideBaseCodeLens(JvmCodeElement element)
        {
            this.element = element;
            JvmCodeLensRefresh.Register(this);
        }

        private sealed class JvmBaseMember
        {
            public JvmBaseMember(string display, string? path, int line, int column, bool library)
            { Display = display; Path = path; Line = line; Column = column; Library = library; }
            public string Display { get; }
            public string? Path { get; }
            public int Line { get; }
            public int Column { get; }
            public bool Library { get; }
        }

        public override async Task<CodeLensLabel> GetLabelAsync(CodeElementContext codeElementContext, CancellationToken token)
        {
            var server = element.Java ? "Java language server (Eclipse JDT LS)" : "Kotlin language server";
            if (!await JvmCodeLensServers.IsReadyAsync(element.Java))
            {
                JvmCodeLensRefresh.WaitForServer(element.Java);
                return new CodeLensLabel { Text = "overrides -", Tooltip = "Waiting for the " + server + " (initialization and Gradle import)." };
            }
            try
            {
                found = await FindBaseAsync(JvmCodeLensPosition.Of(codeElementContext.Range), token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                found = null;
                return new CodeLensLabel { Text = "overrides ?", Tooltip = "The " + server + " failed: " + e.Message };
            }
            if (found == null) return new CodeLensLabel { Text = "overrides", Tooltip = "The overridden member was not found in the supertypes reported by the " + server + "." };
            return new CodeLensLabel
            {
                Text = "overrides " + found.Display,
                Tooltip = found.Path != null ? "Overrides " + found.Display + ". Click to go to it."
                    : "Overrides " + found.Display + " (library" + (found.Library ? " without readable source" : "") + ").",
            };
        }

        private async Task<JvmBaseMember?> FindBaseAsync(JvmCodeLensPosition position, CancellationToken token)
        {
            var member = element.Element.Name.Substring(element.Element.Name.LastIndexOf('.') + 1);
            Enum.TryParse<CodeDeclarationKind>(element.Element.Kind, out var kind);
            var start = await JvmCodeLensServers.RequestAsync(element.Java, "textDocument/prepareTypeHierarchy",
                new { textDocument = new { uri = position.Uri }, position = new { line = position.Line, character = position.Character } }, token);
            if (!(start is JArray roots) || roots.Count == 0) return null;
            var queue = new Queue<JToken>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            queue.Enqueue(roots[0]);
            string? library = null;
            var visited = 0;
            while (queue.Count > 0 && visited++ < MaxSupertypes)
            {
                var supertypes = await JvmCodeLensServers.RequestAsync(element.Java, "typeHierarchy/supertypes", new { item = queue.Dequeue() }, token);
                if (!(supertypes is JArray items)) continue;
                foreach (var item in items)
                {
                    var uri = (string?)item["uri"] ?? "";
                    var name = TypeName((string?)item["name"] ?? "");
                    if (!seen.Add(uri + "#" + name + "#" + item["selectionRange"])) continue;
                    var text = KotlinSourceSets.ReadSource(uri);
                    if (text != null)
                    {
                        var declaration = KotlinSourceSets.FindMember(SourceName(uri), text, name, member, kind);
                        if (declaration != null)
                        {
                            var file = uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(uri).LocalPath : null;
                            return new JvmBaseMember(name + "." + member, file, declaration.NameLine, declaration.NameColumn, false);
                        }
                    }
                    else if (library == null && name != "Any" && name != "Object") library = name;
                    queue.Enqueue(item);
                }
            }
            // A Kotlin override always has a base; without a readable source the nearest library supertype is the best guess,
            // and Any/Object for the members every class inherits.
            if (member == "toString" || member == "equals" || member == "hashCode") library ??= element.Java ? "Object" : "Any";
            return library == null ? null : new JvmBaseMember(library + "." + member, null, 0, 0, true);
        }

        /// <summary>Supertype names may carry type arguments (<c>Comparable&lt;T&gt;</c>).</summary>
        private static string TypeName(string name)
        {
            var angle = name.IndexOf('<');
            return (angle < 0 ? name : name.Substring(0, angle)).Trim();
        }

        /// <summary>File name used to pick the Kotlin or Java scanner for a <c>file:</c> or <c>jar:</c> source.</summary>
        private static string SourceName(string uri)
        {
            var path = uri.Split('?', '#')[0];
            return path.Substring(path.LastIndexOfAny(new[] { '/', '!' }) + 1);
        }

        public override async Task ExecuteAsync(CodeElementContext codeElementContext, IClientContext clientContext, CancellationToken cancelToken)
        {
            var target = found;
            if (target?.Path == null) return;
            await JvmCodeLensNavigation.OpenAsync(target.Path, target.Line, target.Column);
        }

        public void Refresh() => Invalidate();

        public override void Dispose() => JvmCodeLensRefresh.Unregister(this);
    }

    /// <summary>"expect" above a Kotlin <c>actual</c> declaration: the matching <c>expect</c> in another source set of the module.</summary>
    internal sealed class KotlinExpectCodeLens : InvokableCodeLens
    {
        private readonly JvmCodeElement element;
        private CodeLocation? found;

        internal KotlinExpectCodeLens(JvmCodeElement element) => this.element = element;

        public override void Dispose() { }

        public override async Task<CodeLensLabel> GetLabelAsync(CodeElementContext codeElementContext, CancellationToken token)
        {
            var text = codeElementContext.Range.Document.Text.CopyToString();
            var path = element.Path;
            var start = codeElementContext.Range.Start;
            found = await Task.Run(() =>
            {
                // The declaration under the lens: the tagged range starts where the scanner's declaration starts.
                foreach (var declaration in CodeDeclarationScanner.ScanKotlin(text))
                    if (declaration.StartOffset == start && declaration.IsActual) return KotlinSourceSets.FindExpect(path, text, declaration);
                return null;
            }, token);
            if (found == null) return new CodeLensLabel { Text = "expect ?", Tooltip = "No matching expect declaration in the other source sets of this module." };
            var set = KotlinSourceSets.SourceSetOf(found.Path);
            return new CodeLensLabel
            {
                Text = "expect" + (set != null ? " in " + set : ""),
                Tooltip = "expect " + found.QualifiedName + " in " + System.IO.Path.GetFileName(found.Path) + ". Click to go to it.",
            };
        }

        public override async Task ExecuteAsync(CodeElementContext codeElementContext, IClientContext clientContext, CancellationToken cancelToken)
        {
            var target = found;
            if (target != null) await JvmCodeLensNavigation.OpenAsync(target.Path, target.Line, target.Column);
        }
    }

    /// <summary>"Show preview" above a Compose <c>@Preview</c> function: opens the Kotlin Compose Preview window, which follows the active file.</summary>
    internal sealed class ComposePreviewCodeLens : InvokableCodeLens
    {
        public override void Dispose() { }

        public override Task<CodeLensLabel> GetLabelAsync(CodeElementContext codeElementContext, CancellationToken token) =>
            Task.FromResult(new CodeLensLabel { Text = "Show preview", Tooltip = "Open the Kotlin Compose Preview window for this file." });

        public override async Task ExecuteAsync(CodeElementContext codeElementContext, IClientContext clientContext, CancellationToken cancelToken)
        {
            var package = await JvmCodeLensNavigation.PackageAsync();
            var window = await package.ShowToolWindowAsync(typeof(Preview.ComposePreviewWindow), 0, true, package.DisposalToken);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (window?.Frame is IVsWindowFrame frame) ErrorHandler.ThrowOnFailure(frame.Show());
        }
    }

    /// <summary>"Run task" above a task in a Kotlin Gradle script: runs <c>:project:task</c> through the shared Gradle gate.</summary>
    internal sealed class GradleTaskCodeLens : InvokableCodeLens
    {
        private readonly JvmCodeElement element;

        internal GradleTaskCodeLens(JvmCodeElement element) => this.element = element;

        public override void Dispose() { }

        private string? TaskPath()
        {
            var project = element.Path.Length == 0 ? null : GradleScriptScanner.ProjectOf(element.Path);
            return project == null ? null : GradleScriptScanner.TaskPath(project.Value.ProjectPath, element.Element.Name);
        }

        public override Task<CodeLensLabel> GetLabelAsync(CodeElementContext codeElementContext, CancellationToken token)
        {
            var path = TaskPath();
            var application = element.Element.Name == "run" && codeElementContext.Range.Document.Text.CopyToString()
                .Substring(codeElementContext.Range.Start, codeElementContext.Range.End - codeElementContext.Range.Start).StartsWith("application", StringComparison.Ordinal);
            return Task.FromResult(new CodeLensLabel
            {
                Text = application ? "Run application" : "Run task",
                Tooltip = path == null ? "No settings.gradle(.kts) above this script: it is not part of a Gradle build." : "Run Gradle task " + path + ".",
            });
        }

        public override async Task ExecuteAsync(CodeElementContext codeElementContext, IClientContext clientContext, CancellationToken cancelToken)
        {
            var project = element.Path.Length == 0 ? null : GradleScriptScanner.ProjectOf(element.Path);
            var package = await JvmCodeLensNavigation.PackageAsync();
            if (project == null)
            {
                package.Environment.Log("Kotlin: " + System.IO.Path.GetFileName(element.Path) + " is not part of a Gradle build (no settings.gradle(.kts) above it).");
                await package.Environment.ShowOutputAsync();
                return;
            }
            var task = GradleScriptScanner.TaskPath(project.Value.ProjectPath, element.Element.Name);
            // The build outlives the click: Visual Studio cancels lens invocations when the editor moves on.
            _ = package.JoinableTaskFactory.RunAsync(async () =>
            {
                try { await package.RunScriptTasksAsync(project.Value.Root, new[] { task }); }
                catch (Exception e)
                {
                    package.Environment.Log("Kotlin: " + e.Message);
                    await package.Environment.ShowOutputAsync();
                }
            });
        }
    }

    internal static class JvmCodeLensNavigation
    {
        /// <summary>Opens a file with the caret at a 0-based LSP position.</summary>
        internal static async Task OpenAsync(string path, int line, int column)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path, Guid.Empty, out _, out _, out var frame, out IVsTextView view);
            frame?.Show();
            if (view == null) return;
            view.SetCaretPos(line, column);
            view.CenterLines(line, 1);
            view.SendExplicitFocus();
        }

        /// <summary>The VSSDK package of this extension (loaded on demand).</summary>
        internal static async Task<KotlinPackage> PackageAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var shell = await AsyncServiceProvider.GlobalProvider.GetServiceAsync<SVsShell, IVsShell>();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var guid = typeof(KotlinPackage).GUID;
            ErrorHandler.ThrowOnFailure(shell.LoadPackage(ref guid, out var loaded));
            return (KotlinPackage)loaded;
        }
    }
}
