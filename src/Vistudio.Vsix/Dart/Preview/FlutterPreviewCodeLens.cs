using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using Vistudio.Core.CodeLens;
using Vistudio.Core.Dart;
using Vistudio.Core.Dart.Preview;
using Task = System.Threading.Tasks.Task;

namespace Vistudio.Dart.Preview
{
    /// <summary>Opening the preview window and starting a preview, shared by the commands, CodeLens and light bulb.</summary>
    internal static class FlutterPreviewCommands
    {
        /// <summary>Shows the Flutter Widget Preview window and previews <paramref name="file"/> (or <paramref name="widgetClass"/> through the host project).</summary>
        public static async Task ShowAsync(string file, string? widgetClass)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var model = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel ?? throw new InvalidOperationException("Visual Studio component model is unavailable.");
            var service = model.GetService<FlutterPreviewService>();
            // The window belongs to DartPackage; load it (it may not be loaded yet when a CodeLens is clicked).
            if (ServiceProvider.GlobalProvider.GetService(typeof(SVsShell)) is IVsShell shell)
            {
                var package = new Guid(DartPackage.PackageGuid);
                shell.LoadPackage(ref package, out _);
            }
            if (ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell)) is IVsUIShell ui)
            {
                var window = new Guid(FlutterPreviewWindow.WindowGuid);
                if (Microsoft.VisualStudio.ErrorHandler.Succeeded(ui.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fForceCreate, ref window, out var frame)) && frame != null)
                    frame.ShowNoActivate();
            }
            await service.PreviewAsync(file, widgetClass);
        }
    }

    /// <summary>
    /// What gets a "Preview" CodeLens: <c>@Preview</c> declarations (an element on the annotation line, kind Preview),
    /// and widget classes (the existing Type element of the class line). Previews come from the analysis server
    /// (<c>dart/textDocument/getFlutterWidgetPreviews</c>, which also knows imported MultiPreview annotations) merged with
    /// Vistudio's scanner; indirect widget subclasses are confirmed through the server's type hierarchy.
    /// </summary>
    internal static class FlutterPreviewLensData
    {
        internal const string PreviewPrefix = "vistudiodart|Preview|";
        private static readonly ConcurrentDictionary<string, (int Version, bool IsWidget)> hierarchy = new ConcurrentDictionary<string, (int, bool)>();

        internal static bool Enabled(string path)
        {
            var settings = DartSettings.Load();
            return settings.CodeLens && settings.WidgetPreview && settings.WidgetPreviewCodeLens && FlutterWorkspace.ProjectOf(path) != null;
        }

        /// <summary>Preview targets for tagging: the scanner's, plus the analysis server's when it answers quickly.</summary>
        internal static async Task<IReadOnlyList<WidgetPreviewTarget>> TargetsAsync(string path, string uri, string text, CancellationToken token)
        {
            var scanned = WidgetPreviewScanner.Scan(text).Targets;
            try
            {
                var model = await AsyncServiceProvider.GlobalProvider.GetServiceAsync<SComponentModel, IComponentModel>();
                var client = model.GetService<DartLanguageClient>();
                if (!client.IsServerReady) return scanned;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    var answer = await client.RequestAsync(WidgetPreviewLsp.FilePreviewsMethod, new { uri }, timeout.Token);
                    return WidgetPreviewLsp.Merge(scanned, WidgetPreviewLsp.ParsePreviews(answer), text);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return scanned; }
            catch (Exception e) when (!(e is OperationCanceledException)) { return scanned; } // older servers: method not found
        }

        /// <summary>The widget class declared at a Type element, when it is (or the server confirms it is) a widget.</summary>
        internal static async Task<WidgetClassInfo?> WidgetAsync(TextRange range, CancellationToken token)
        {
            var text = range.Document.Text.CopyToString();
            var widgets = WidgetPreviewScanner.Scan(text).Widgets;
            var widget = widgets.FirstOrDefault(w => w.NameEnd == range.End) ?? widgets.FirstOrDefault(w => w.StartOffset >= range.Start && w.NameEnd <= range.End);
            if (widget == null || widget.IsWidget == true) return widget;
            if (widget.IsAbstract || widget.IsPrivate) return null;
            return await IsWidgetByServerAsync(range, widget, text, token) ? widget : null;
        }

        private static async Task<bool> IsWidgetByServerAsync(TextRange range, WidgetClassInfo widget, string text, CancellationToken token)
        {
            var uri = range.Document.Uri.IsFile ? new Uri(range.Document.Uri.LocalPath).AbsoluteUri : range.Document.Uri.ToString();
            var key = uri + "|" + widget.Name + "|" + widget.SuperClass;
            if (hierarchy.TryGetValue(key, out var cached) && cached.Version == text.Length) return cached.IsWidget;
            try
            {
                var model = await AsyncServiceProvider.GlobalProvider.GetServiceAsync<SComponentModel, IComponentModel>();
                var client = model.GetService<DartLanguageClient>();
                if (!client.IsServerReady) return false;
                var (line, character) = CodeLensElement.NamePosition(text, widget.NameStart, widget.NameEnd);
                var items = await client.RequestAsync("textDocument/prepareTypeHierarchy", WidgetPreviewLsp.Position(uri, line, character), token) as JArray;
                var queue = new Queue<JToken>(items ?? new JArray());
                var visited = 0;
                while (queue.Count > 0 && visited++ < 12)
                {
                    var supertypes = await client.RequestAsync("typeHierarchy/supertypes", new { item = queue.Dequeue() }, token) as JArray;
                    if (supertypes == null) continue;
                    if (supertypes.Any(WidgetPreviewLsp.IsFrameworkWidget)) { hierarchy[key] = (text.Length, true); return true; }
                    foreach (var super in supertypes) queue.Enqueue(super);
                }
                hierarchy[key] = (text.Length, false);
            }
            catch (Exception e) when (!(e is OperationCanceledException)) { }
            return false;
        }
    }

    [VisualStudioContribution]
    internal sealed class FlutterPreviewCodeLensProvider : ExtensionPart, ICodeLensProvider
    {
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new() { AppliesTo = new[] { DocumentFilter.FromDocumentType("text") } };
        public CodeLensProviderConfiguration CodeLensProviderConfiguration => new("Flutter widget preview") { Priority = 90 };

        public async Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token)
        {
            var id = codeElement.UniqueIdentifier;
            if (id == null) return null;
            var path = DartCodeElement.PathOf(codeElementContext.Range);
            if (id.StartsWith(FlutterPreviewLensData.PreviewPrefix, StringComparison.Ordinal))
                return FlutterPreviewLensData.Enabled(path) ? new FlutterPreviewCodeLens(null) : null;
            if (DartCodeElement.Kind(codeElement) != DartDeclarationKind.Type || !FlutterPreviewLensData.Enabled(path)) return null;
            var widget = await FlutterPreviewLensData.WidgetAsync(codeElementContext.Range, token);
            return widget == null ? null : new FlutterPreviewCodeLens(widget.Name);
        }
    }

    /// <summary>"Preview" above a <c>@Preview</c> declaration or a widget class.</summary>
    internal sealed class FlutterPreviewCodeLens : InvokableCodeLens
    {
        private readonly string? widgetClass;

        internal FlutterPreviewCodeLens(string? widgetClass) => this.widgetClass = widgetClass;

        public override void Dispose() { }

        public override Task<CodeLensLabel> GetLabelAsync(CodeElementContext codeElementContext, CancellationToken token)
        {
            if (widgetClass == null)
            {
                var text = codeElementContext.Range.Document.Text.CopyToString();
                var target = WidgetPreviewScanner.Scan(text).Targets.FirstOrDefault(t => t.AnnotationStart >= codeElementContext.Range.Start && t.AnnotationStart <= codeElementContext.Range.End);
                var tooltip = target == null ? "Show the widget previews of this file." : target.Problem != null
                    ? "The previewer will report: " + target.Problem
                    : "Show the previews of this file in the Flutter Widget Preview window (" + target.Name + ": " + target.Annotation.Describe() + ").";
                return Task.FromResult(new CodeLensLabel { Text = target?.Problem != null ? "Preview (invalid)" : "Preview", Tooltip = tooltip });
            }
            var widget = WidgetPreviewScanner.Scan(codeElementContext.Range.Document.Text.CopyToString()).Widgets.FirstOrDefault(w => w.Name == widgetClass);
            var has = widget?.HasPreview == true;
            return Task.FromResult(new CodeLensLabel
            {
                Text = "Preview",
                Tooltip = has ? "Show the @Preview declarations of " + widgetClass + "."
                    : widget?.Construction == null ? (widget?.Problem ?? widgetClass + " cannot be created without arguments.") + " Add a @Preview function that builds it."
                    : widgetClass + " has no @Preview: preview it through a temporary project (no source change). The light bulb on the class adds a @Preview function.",
            });
        }

        public override async Task ExecuteAsync(CodeElementContext codeElementContext, IClientContext clientContext, CancellationToken cancelToken)
        {
            var path = DartCodeElement.PathOf(codeElementContext.Range);
            var model = await AsyncServiceProvider.GlobalProvider.GetServiceAsync<SComponentModel, IComponentModel>();
            var service = model.GetService<FlutterPreviewService>();
            string? wrap = null;
            if (widgetClass != null)
            {
                var widget = WidgetPreviewScanner.Scan(codeElementContext.Range.Document.Text.CopyToString()).Widgets.FirstOrDefault(w => w.Name == widgetClass);
                if (widget != null && !widget.HasPreview) wrap = widgetClass;
            }
            service.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                (await AsyncServiceProvider.GlobalProvider.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>())?.ExecuteCommand("File.SaveAll");
                await FlutterPreviewCommands.ShowAsync(path, wrap);
            });
        }
    }
}
