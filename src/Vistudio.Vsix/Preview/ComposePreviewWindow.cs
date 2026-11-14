using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Vistudio.Core.Preview;

namespace Vistudio.Preview
{
    /// <summary>
    /// Kotlin Compose Preview: shows the @Preview functions of the active Kotlin file rendered through the module's JVM target.
    /// It follows the active document, renders on first display and after each save of the shown file (when visible), and
    /// keeps the last rendered images per file for the session.
    /// </summary>
    [Guid("5c0f2a7e-3d41-4b8e-9a63-2f1d6c8b7e40")]
    internal sealed class ComposePreviewWindow : ToolWindowPane, IVsSelectionEvents, IVsRunningDocTableEvents3, IVsWindowFrameNotify
    {
        private readonly Grid root = new Grid();
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 4) };
        private readonly StackPanel items = new StackPanel { Margin = new Thickness(8) };
        private readonly Button refresh = new Button { Content = "Refresh", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 6, 4, 2) };
        private readonly Button cancel = new Button { Content = "Cancel", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(4, 6, 4, 2), IsEnabled = false };
        private readonly CheckBox auto = new CheckBox { Content = "Refresh on save", IsChecked = true, Margin = new Thickness(8, 9, 4, 2), VerticalAlignment = VerticalAlignment.Center };
        private readonly Dictionary<string, Rendered> cache = new Dictionary<string, Rendered>(StringComparer.OrdinalIgnoreCase);
        private KotlinPackage? package;
        private uint selectionCookie, documentCookie;
        private string? file;
        private CancellationTokenSource? running;
        private string? pending;

        private sealed class Rendered
        {
            public Rendered(DateTime written, ComposePreviewFile previews, ComposePreviewRun run) { Written = written; Previews = previews; Run = run; }
            public DateTime Written { get; }
            public ComposePreviewFile Previews { get; }
            public ComposePreviewRun Run { get; }
        }

        public ComposePreviewWindow() : base(null)
        {
            Caption = "Kotlin Compose Preview";
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
            toolbar.Children.Add(refresh);
            toolbar.Children.Add(cancel);
            toolbar.Children.Add(auto);
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            Grid.SetRow(status, 1);
            var scroll = new ScrollViewer { Content = items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 2);
            root.Children.Add(toolbar);
            root.Children.Add(status);
            root.Children.Add(scroll);
            root.SetResourceReference(Panel.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            root.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            status.Text = "Open a Kotlin file with @Preview functions.";
            refresh.Click += (_, __) => Run(() => RenderAsync(force: true));
            cancel.Click += (_, __) => running?.Cancel();
            Content = root;
        }

        public override void OnToolWindowCreated()
        {
            base.OnToolWindowCreated();
            ThreadHelper.ThrowIfNotOnUIThread();
            package = (KotlinPackage)Package;
            if (GetService(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection selection)
            {
                selection.AdviseSelectionEvents(this, out selectionCookie);
                if (ErrorHandler.Succeeded(selection.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_DocumentFrame, out var frame)))
                    OnDocumentFrame(frame);
            }
            if (GetService(typeof(SVsRunningDocumentTable)) is IVsRunningDocumentTable table) table.AdviseRunningDocTableEvents(this, out documentCookie);
            if (Frame is IVsWindowFrame window) window.SetProperty((int)__VSFPROPID.VSFPROPID_ViewHelper, this);
        }

        protected override void Dispose(bool disposing)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposing)
            {
                running?.Cancel();
                if (selectionCookie != 0 && GetService(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection selection) selection.UnadviseSelectionEvents(selectionCookie);
                if (documentCookie != 0 && GetService(typeof(SVsRunningDocumentTable)) is IVsRunningDocumentTable table) table.UnadviseRunningDocTableEvents(documentCookie);
                selectionCookie = documentCookie = 0;
            }
            base.Dispose(disposing);
        }

        private bool Visible
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return Frame is IVsWindowFrame window && window.IsVisible() == VSConstants.S_OK;
            }
        }

        // Each action reports its own failure in the window and the Kotlin pane, so it can run detached.
#pragma warning disable VSSDK007
        private void Run(Func<Task> action) =>
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try { await action(); }
                catch (Exception e)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    status.Text = "Compose preview: " + e.Message;
                    package?.Environment.Log("Compose preview: " + e);
                    ActivityLog.TryLogError("Vistudio.ComposePreview", e.ToString());
                }
            }).Task.Forget();
#pragma warning restore VSSDK007

        /// <summary>Shows the file's previews (cached images when the file is unchanged) and renders when needed.</summary>
        private void ShowFile(string path, bool saved)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var changed = !string.Equals(path, file, StringComparison.OrdinalIgnoreCase);
            if (!changed && !saved) return;
            file = path;
            Run(() => RenderAsync(force: saved));
        }

        private async Task RenderAsync(bool force)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var path = file;
            if (path == null || package == null) return;
            if (running != null)
            {
                // One render at a time; the latest request runs when the current one finishes.
                pending = path;
                status.Text = "Rendering… the preview of " + Path.GetFileName(path) + " follows.";
                return;
            }
            if (force) SaveIfModified(path);
            var (written, previews) = await Task.Run(() =>
            {
                var text = File.ReadAllText(path);
                return (File.GetLastWriteTimeUtc(path), ComposePreviewScanner.Scan(path, text));
            });
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!string.Equals(path, file, StringComparison.OrdinalIgnoreCase)) return;
            if (previews.Previews.Count == 0)
            {
                Show(previews, null, "No Compose @Preview functions in " + Path.GetFileName(path) + ".");
                return;
            }
            if (cache.TryGetValue(path, out var cached) && cached.Written == written && !force)
            {
                Show(cached.Previews, cached.Run, Summary(path, cached.Run));
                return;
            }
            if (!previews.Renderable.Any())
            {
                Show(previews, null, "None of the previews in " + Path.GetFileName(path) + " can be rendered.");
                return;
            }
            if (!force && !Visible)
            {
                Show(previews, null, "Not rendered yet. Click Refresh.");
                return;
            }
            if (package.GradleBusy)
            {
                // Another Gradle operation (build, import) holds the shared gate: render as soon as it finishes,
                // unless the user switches to another file meanwhile (that file then gets its own request).
                Show(previews, cached?.Run, "Waiting for the running Gradle operation to finish…");
                while (package.GradleBusy)
                {
                    await Task.Delay(1000);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (!string.Equals(path, file, StringComparison.OrdinalIgnoreCase) || running != null) return;
                }
            }
            Show(previews, cached?.Run, "Rendering " + Path.GetFileName(path) + " with Gradle…");
            running = new CancellationTokenSource();
            refresh.IsEnabled = false;
            cancel.IsEnabled = true;
            try
            {
                var token = running.Token;
                var run = await package.RunExclusiveGradleAsync(async gradleToken =>
                {
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, gradleToken))
                        return await ComposePreviewRunner.RenderAsync(package.Environment, path, previews, linked.Token);
                });
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (run.Canceled) Show(previews, cached?.Run, "Rendering canceled.");
                else
                {
                    if (run.Error == null) cache[path] = new Rendered(written, previews, run);
                    if (string.Equals(path, file, StringComparison.OrdinalIgnoreCase)) Show(previews, run, Summary(path, run));
                }
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                running.Dispose();
                running = null;
                refresh.IsEnabled = true;
                cancel.IsEnabled = false;
                var next = pending;
                pending = null;
                if (next != null && string.Equals(next, file, StringComparison.OrdinalIgnoreCase) && !string.Equals(next, path, StringComparison.OrdinalIgnoreCase))
                    Run(() => RenderAsync(force: false));
            }
        }

        private static string Summary(string path, ComposePreviewRun run)
        {
            if (run.Error != null) return Path.GetFileName(path) + ": " + run.Error;
            var failed = run.Results.Values.Count(r => r.Error != null);
            return Path.GetFileName(path) + ": " + run.Results.Values.Count(r => r.File != null) + " rendered" + (failed == 0 ? "" : ", " + failed + " failed")
                + " at " + DateTime.Now.ToString("T") + ".";
        }

        private void SaveIfModified(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(GetService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte) || dte.Documents == null) return;
            foreach (EnvDTE.Document document in dte.Documents)
                if (string.Equals(document.FullName, path, StringComparison.OrdinalIgnoreCase) && !document.Saved) document.Save();
        }

        private void Show(ComposePreviewFile previews, ComposePreviewRun? run, string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            status.Text = message;
            ActivityLog.TryLogInformation("Vistudio.ComposePreview", message); // recorded only when Visual Studio runs with /Log
            items.Children.Clear();
            if (run?.Error != null) items.Children.Add(ErrorText(run.Error));
            foreach (var preview in previews.Previews)
            {
                var card = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
                var title = new TextBlock { Text = preview.DisplayName, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand,
                    ToolTip = preview.Function + "() — line " + preview.Line + ". Click to open.", Margin = new Thickness(0, 0, 0, 4) };
                var line = preview.Line;
                var source = previews.Path;
                title.MouseLeftButtonUp += (_, __) => OpenAt(source, line);
                card.Children.Add(title);
                ComposePreviewResult? result = null;
                if (preview.Unsupported != null) card.Children.Add(Note(preview.Unsupported));
                else if (run == null || run.Error != null || !run.Results.TryGetValue(preview.Id, out result)) card.Children.Add(Note("Not rendered."));
                else if (result.Error != null) card.Children.Add(ErrorText(result.Error));
                else card.Children.Add(ImageOf(Path.Combine(run.OutputDirectory, result.File!), result));
                items.Children.Add(card);
            }
        }

        private static UIElement ImageOf(string path, ComposePreviewResult result)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad; // keep the file unlocked for the next render
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                var image = new Image { Source = bitmap, Width = result.Width / result.Scale, Height = result.Height / result.Scale,
                    Stretch = Stretch.Fill, SnapsToDevicePixels = true };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                // Shown at its dp size; a preview wider than the window shrinks to fit instead of being clipped.
                var fit = new Viewbox { Child = image, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left };
                return new Border { Child = fit, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left,
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)) };
            }
            catch (Exception e) { return ErrorText("Cannot load " + path + ": " + e.Message); }
        }

        private static TextBlock Note(string text) => new TextBlock { Text = text, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

        private static TextBox ErrorText(string text) => new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 0, 0, 8)
        };

        private void OpenAt(string path, int line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (package == null) return;
            VsShellUtilities.OpenDocument(package, path, VSConstants.LOGVIEWID_Primary, out _, out _, out var frame);
            var view = VsShellUtilities.GetTextView(frame);
            if (view == null) return;
            view.SetCaretPos(line - 1, 0);
            // CenterLines leaves a freshly activated view at the bottom edge; put a few lines of context above instead.
            view.SetTopLine(Math.Max(0, line - 1 - 8));
        }

        private void OnDocumentFrame(object? value)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (value is IVsWindowFrame frame && ErrorHandler.Succeeded(frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out var moniker))
                && moniker is string path && path.EndsWith(".kt", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                ShowFile(path, saved: false);
        }

        // IVsSelectionEvents: follow the active document.
        public int OnElementValueChanged(uint elementid, object varValueOld, object varValueNew)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (elementid == (uint)VSConstants.VSSELELEMID.SEID_DocumentFrame) OnDocumentFrame(varValueNew);
            return VSConstants.S_OK;
        }
        public int OnSelectionChanged(IVsHierarchy pHierOld, uint itemidOld, IVsMultiItemSelect pMISOld, ISelectionContainer pSCOld,
            IVsHierarchy pHierNew, uint itemidNew, IVsMultiItemSelect pMISNew, ISelectionContainer pSCNew) => VSConstants.S_OK;
        public int OnCmdUIContextChanged(uint dwCmdUICookie, int fActive) => VSConstants.S_OK;

        // IVsRunningDocTableEvents3: re-render after the shown file is saved.
        public int OnAfterSave(uint docCookie)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (auto.IsChecked != true || file == null || !Visible) return VSConstants.S_OK;
            if (GetService(typeof(SVsRunningDocumentTable)) is IVsRunningDocumentTable table
                && ErrorHandler.Succeeded(table.GetDocumentInfo(docCookie, out _, out _, out _, out var moniker, out _, out _, out _))
                && string.Equals(moniker, file, StringComparison.OrdinalIgnoreCase))
                ShowFile(moniker, saved: true);
            return VSConstants.S_OK;
        }
        public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
        public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
        public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;
        public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame) => VSConstants.S_OK;
        public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;
        public int OnAfterAttributeChangeEx(uint docCookie, uint grfAttribs, IVsHierarchy pHierOld, uint itemidOld, string pszMkDocumentOld,
            IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew) => VSConstants.S_OK;
        public int OnBeforeSave(uint docCookie) => VSConstants.S_OK;

        // IVsWindowFrameNotify: render the current file when the window becomes visible and has nothing rendered yet.
        public int OnShow(int fShow)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if ((fShow == (int)__FRAMESHOW.FRAMESHOW_WinShown || fShow == (int)__FRAMESHOW.FRAMESHOW_TabActivated) && file != null
                && running == null && !cache.ContainsKey(file))
                Run(() => RenderAsync(force: false));
            return VSConstants.S_OK;
        }
        public int OnMove() => VSConstants.S_OK;
        public int OnSize() => VSConstants.S_OK;
        public int OnDockableChange(int fDockable) => VSConstants.S_OK;
    }
}
