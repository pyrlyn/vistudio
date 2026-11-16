using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Vistudio.Core;
using Vistudio.Core.Dart;
using Vistudio.Core.Dart.Preview;
using Task = System.Threading.Tasks.Task;

namespace Vistudio.Dart.Preview
{
    /// <summary>
    /// Flutter Widget Preview tool window: the official widget previewer's page (<c>flutter widget-preview start</c>)
    /// embedded with WebView2, once per chosen configuration (device size, theme, text scale, locale). The toolbar
    /// choices are saved as soon as they change. It follows the active Dart document, shows previewer and build errors
    /// above the previews, and closing it stops the previewer.
    /// </summary>
    [Guid(WindowGuid)]
    internal sealed class FlutterPreviewWindow : ToolWindowPane, IVsSelectionEvents
    {
        internal const string WindowGuid = "3b7e9c15-6a2d-4f81-b4e0-9c5d7a1f2e63";
        private static readonly string UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vistudio", "WebView2");
        private static Task<CoreWebView2Environment>? sharedEnvironment;

        private readonly Grid root = new Grid();
        private readonly ComboBox device = Combo(140), theme = Combo(110), textScale = Combo(90), locale = Combo(100);
        private readonly TextBlock title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Border noticeBar = new Border { Padding = new Thickness(8, 4, 8, 4), Visibility = Visibility.Collapsed };
        private readonly TextBlock noticeText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        private readonly WrapPanel noticeActions = new WrapPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly Grid surface = new Grid { ClipToBounds = true };
        private readonly TextBlock placeholder = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16), Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock status = new TextBlock { Margin = new Thickness(8, 2, 8, 3), TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.85 };
        private readonly Button restart = ToolButton("Restart", "Hot restart the previews (their state is reset)");
        private readonly Button stop = ToolButton("Stop", "Stop the widget previewer");
        private readonly Button browser = ToolButton("Browser", "Open the previewer page in the default browser");
        private readonly Button output = ToolButton("Output", "Show the Dart Output pane");
        private readonly List<PreviewCard> cards = new List<PreviewCard>();
        private FlutterPreviewService? service;
        private DartEnvironment? environment;
        private PreviewWindowState state = new PreviewWindowState();
        private IReadOnlyList<DevicePreset> devices = Array.Empty<DevicePreset>();
        private IReadOnlyList<double> scales = new[] { 1.0 };
        private IReadOnlyList<string> locales = Array.Empty<string>();
        private string? url;
        private uint selectionCookie, frameCookie;
        private bool loadingChoices;
        private bool ideDark;

        private sealed class PreviewCard
        {
            public PreviewCard(PreviewConfiguration configuration, Border frame, WebView2 view, TextBlock label)
            { Configuration = configuration; Frame = frame; View = view; Label = label; }
            public PreviewConfiguration Configuration { get; set; }
            /// <summary>The id of the document-created script (root font size, languages), replaced when the configuration changes.</summary>
            public string? ScriptId { get; set; }
            /// <summary>The previewer page was opened in it (later changes reload it in place).</summary>
            public bool Opened { get; set; }
            public Border Frame { get; }
            public WebView2 View { get; }
            public TextBlock Label { get; }
            public bool Ready { get; set; }
            /// <summary>Completed once WebView2 is set up (profile, script, zoom) and the page can be opened.</summary>
            public TaskCompletionSource<bool> Initialized { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public FlutterPreviewWindow() : base(null)
        {
            Caption = "Flutter Widget Preview";
            var toolbar = new WrapPanel { Margin = new Thickness(4, 4, 4, 2) };
            toolbar.Children.Add(title);
            toolbar.Children.Add(Labeled("Device", device));
            toolbar.Children.Add(Labeled("Theme", theme));
            toolbar.Children.Add(Labeled("Text", textScale));
            toolbar.Children.Add(Labeled("Locale", locale));
            foreach (var button in new[] { restart, stop, browser, output }) toolbar.Children.Add(button);

            var notice = new DockPanel();
            DockPanel.SetDock(noticeActions, Dock.Right);
            notice.Children.Add(noticeActions);
            notice.Children.Add(noticeText);
            noticeBar.Child = notice;

            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(noticeBar, 1);
            Grid.SetRow(surface, 2);
            Grid.SetRow(status, 3);
            root.Children.Add(toolbar);
            root.Children.Add(noticeBar);
            root.Children.Add(surface);
            root.Children.Add(status);
            surface.Children.Add(placeholder);
            root.SetResourceReference(Panel.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            root.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            noticeBar.SetResourceReference(Border.BackgroundProperty, VsBrushes.InfoBackgroundKey);
            noticeText.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.InfoTextKey);
            placeholder.Text = "Use Preview above a @Preview function or widget class, Preview Widget in the editor's context menu, or Tools > Dart > Flutter: Preview Widget.";
            status.Text = "Not running.";

            restart.Click += (_, __) => service?.Run(() => service.HotRestartAsync());
            stop.Click += (_, __) => service?.Run(() => service.StopAsync("stopped from the preview window"));
            browser.Click += (_, __) => { if (url != null) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); };
            output.Click += (_, __) => service?.Run(() => environment!.ShowOutputAsync());
            foreach (var combo in new[] { device, theme, textScale, locale }) combo.SelectionChanged += (_, __) => { ThreadHelper.ThrowIfNotOnUIThread(); OnChoiceChanged(); };
            surface.SizeChanged += (_, __) => LayoutCards();
            // Automation ids for UI Automation (accessibility tools and tests/verify-flutter-preview.ps1).
            foreach (var (element, id) in new (FrameworkElement, string)[] { (device, "Device"), (theme, "Theme"), (textScale, "TextScale"), (locale, "Locale"), (noticeText, "NoticeText"), (status, "Status"), (title, "Project") })
                System.Windows.Automation.AutomationProperties.SetAutomationId(element, "FlutterPreview." + id);
            Content = root;
        }

        public override void OnToolWindowCreated()
        {
            base.OnToolWindowCreated();
            ThreadHelper.ThrowIfNotOnUIThread();
            var model = (IComponentModel)GetService(typeof(SComponentModel));
            service = model.GetService<FlutterPreviewService>();
            environment = model.GetService<DartEnvironment>();
            service.StateChanged += (s, m) => OnUi(() => ShowState(s, m));
            service.UrlChanged += u => OnUi(() => ShowUrl(u));
            service.NoticeChanged += n => OnUi(() => ShowNotice(n));
            service.PreviewerStatusChanged += (s, m) => OnUi(() => ShowPreviewerStatus(s, m));
            service.ReloadPagesRequested += all => OnUi(() => ReloadPages(all));
            service.PageConnected += () => OnUi(() => connectedSignal?.TrySetResult(true));
            DartEnvironment.SettingsChanged += (_, __) => OnUi(LoadChoices);
            VSColorTheme.ThemeChanged += _ => OnUi(OnIdeThemeChanged);
            if (GetService(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection selection) selection.AdviseSelectionEvents(this, out selectionCookie);
            // IVsWindowFrame2.Advise, not VSFPROPID_ViewHelper: a ToolWindowPane's frame does not call a view helper set here.
            if (Frame is IVsWindowFrame2 frame) frame.Advise(new FrameEvents(this), out frameCookie);
            LoadChoices();
            OnIdeThemeChanged();
            ShowState(service.State, service.LastMessage ?? "Not running.");
            ShowNotice(service.Notice);
            ShowUrl(service.Url);
        }

        protected override void Dispose(bool disposing)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposing)
            {
                if (selectionCookie != 0 && GetService(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection selection) selection.UnadviseSelectionEvents(selectionCookie);
                selectionCookie = 0;
                if (frameCookie != 0 && Frame is IVsWindowFrame2 frame) frame.Unadvise(frameCookie);
                frameCookie = 0;
                ClearCards();
            }
            base.Dispose(disposing);
        }

        private static void OnUi(Action action)
        {
#pragma warning disable VSSDK007 // UI updates only; failures are logged by the action itself.
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try { action(); }
                catch (Exception e) { ActivityLog.TryLogError("Vistudio.FlutterPreview", e.ToString()); }
            });
#pragma warning restore VSSDK007
        }

        // ---- Toolbar choices (saved immediately, outside Tools > Options) ----

        private void LoadChoices()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var settings = DartSettings.Load();
            try { devices = PreviewConfigurations.ParseDevices(settings.WidgetPreviewDevices); } catch (ArgumentException) { devices = PreviewConfigurations.ParseDevices(PreviewConfigurations.DefaultDevices); }
            try { scales = PreviewConfigurations.ParseTextScales(settings.WidgetPreviewTextScales); } catch (ArgumentException) { scales = new[] { 1.0 }; }
            try { locales = PreviewConfigurations.ParseLocales(settings.WidgetPreviewLocales); } catch (ArgumentException) { locales = Array.Empty<string>(); }
            state = PreviewWindowState.Load(PreviewWindowState.FilePath);
            loadingChoices = true;
            try
            {
                Fill(device, new[] { ("Fit window", PreviewSelection.Fit) }.Concat(devices.Select(d => (d.ToString(), d.Name)))
                    .Concat(new[] { ("Custom (" + state.CustomWidth + "×" + state.CustomHeight + ")", PreviewSelection.Custom), ("All devices side by side", PreviewSelection.Compare) }), state.Device);
                Fill(theme, new[] { ("Visual Studio", "ide"), ("Light", "light"), ("Dark", "dark"), ("Light | Dark", PreviewSelection.BothThemes) }, state.Theme);
                var savedScale = state.TextScale == PreviewSelection.Compare ? PreviewSelection.Compare
                    : PreviewConfigurations.FormatScale(double.TryParse(state.TextScale, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 1.0);
                Fill(textScale, scales.Select(s => (PreviewConfigurations.FormatScale(s) + "×", PreviewConfigurations.FormatScale(s))).Concat(new[] { ("All side by side", PreviewSelection.Compare) }), savedScale);
                Fill(locale, new[] { ("System", PreviewSelection.SystemLocale) }.Concat(locales.Select(l => (l, l))).Concat(new[] { ("All side by side", PreviewSelection.Compare) }), state.Locale);
            }
            finally { loadingChoices = false; }
            RebuildCards();
        }

        private void OnChoiceChanged()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (loadingChoices) return;
            var selection = state.ToSelection();
            selection.Device = Value(device) ?? PreviewSelection.Fit;
            selection.Theme = Value(theme) ?? "ide";
            selection.TextScale = Value(textScale) ?? "1.0";
            selection.Locale = Value(locale) ?? PreviewSelection.SystemLocale;
            if (selection.Device == PreviewSelection.Custom && !AskCustomSize(selection)) { LoadChoices(); return; }
            state.Apply(selection);
            try { state.Save(); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { environment?.Log("Widget preview: the window choices could not be saved: " + e.Message); }
            RebuildCards();
        }

        private bool AskCustomSize(PreviewSelection selection)
        {
            var answer = Microsoft.VisualBasic.Interaction.InputBox("Preview surface size in logical pixels (WIDTHxHEIGHT):", "Custom preview size", selection.CustomWidth + "x" + selection.CustomHeight);
            if (string.IsNullOrWhiteSpace(answer)) return false;
            try
            {
                var preset = PreviewConfigurations.ParseDevices("Custom size=" + answer.Trim()).Single();
                selection.CustomWidth = preset.Width;
                selection.CustomHeight = preset.Height;
                return true;
            }
            catch (ArgumentException e)
            {
                VsShellUtilities.ShowMessageBox(this, e.Message, "Custom preview size", OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return false;
            }
        }

        // ---- Previewer state ----

        private void ShowState(PreviewSessionState value, string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var project = service?.Project;
            title.Text = project == null ? "" : project.Name + (service?.Host != null ? " (temporary host)" : "");
            status.Text = value switch
            {
                PreviewSessionState.Stopped => "Not running.",
                PreviewSessionState.Ready => "Ready. " + (state.Device == PreviewSelection.Fit ? "" : "Values set in @Preview (size, brightness, textScaleFactor, localizations) take precedence over the window's configuration."),
                _ => FirstLine(message),
            };
            var running = value != PreviewSessionState.Stopped && value != PreviewSessionState.Failed;
            restart.IsEnabled = value == PreviewSessionState.Ready || value == PreviewSessionState.Serving;
            stop.IsEnabled = running;
            if (!running && cards.Count == 0) placeholder.Text = value == PreviewSessionState.Failed ? "The previewer is not running. See the message above and Output > Dart." : placeholder.Text;
        }

        private void ShowPreviewerStatus(PreviewerStatus value, string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            switch (value)
            {
                case PreviewerStatus.ReloadStarted: status.Text = "Hot reload..."; break;
                case PreviewerStatus.RestartStarted: status.Text = "Hot restart..."; break;
                case PreviewerStatus.Reloaded: status.Text = "Hot reloaded at " + DateTime.Now.ToString("T") + "."; break;
                case PreviewerStatus.Restarted: status.Text = "Restarted at " + DateTime.Now.ToString("T") + "."; break;
                case PreviewerStatus.Error: status.Text = "Build error - see the message above."; break;
            }
        }

        private void ShowNotice(PreviewNotice? notice)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            noticeActions.Children.Clear();
            if (notice == null) { noticeBar.Visibility = Visibility.Collapsed; return; }
            noticeText.Text = (notice.Kind == PreviewNoticeKind.Error ? "⛔ " : notice.Kind == PreviewNoticeKind.Warning ? "⚠ " : "ℹ ") + notice.Text;
            foreach (var (label, run) in notice.Actions)
            {
                var button = new Button { Content = label, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(4, 0, 0, 0) };
                System.Windows.Automation.AutomationProperties.SetAutomationId(button, "FlutterPreview.Notice." + label.TrimEnd('.'));
                button.Click += (_, __) => run();
                noticeActions.Children.Add(button);
            }
            noticeBar.Visibility = Visibility.Visible;
        }

        private void ShowUrl(string? value)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (value == url) return;
            url = value;
            browser.IsEnabled = url != null;
            RebuildCards();
        }

        // ---- Preview pages ----

        /// <summary>
        /// Shows the configurations of the toolbar choice. The previewer's debug service keeps counting every page that
        /// ever connected and waits for all of them on hot reload and hot restart (seen in Visual Studio: "Hot reload
        /// timed out - received 0/3 responses" after pages were replaced, even after a clean unload). So while the
        /// previewer runs, pages are never replaced: they are reconfigured and reloaded in place (a reload keeps the
        /// page's connection), and pages no longer needed are parked hidden but stay connected. Only a new previewer
        /// (another URL) gets new pages.
        /// </summary>
        private void RebuildCards()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (url == null)
            {
                ClearCards();
                placeholder.Visibility = Visibility.Visible;
                if (service?.State == PreviewSessionState.Starting || service?.State == PreviewSessionState.ResolvingDependencies) placeholder.Text = "Starting the widget previewer...";
                return;
            }
            if (pagesUrl != url) ClearCards();
            pagesUrl = url;
            placeholder.Visibility = Visibility.Collapsed;
            var configurations = PreviewConfigurations.Expand(state.ToSelection(), devices, scales, locales, out var truncated);
            if (truncated) environment?.Log("Widget preview: only the first " + PreviewConfigurations.MaximumSideBySide + " configurations are shown side by side.");
            var reload = new List<PreviewCard>();
            for (var i = 0; i < configurations.Count; i++)
            {
                if (i < pool.Count)
                {
                    var card = pool[i];
                    card.Frame.Visibility = Visibility.Visible;
                    if (Reconfigure(card, configurations[i])) reload.Add(card);
                }
                else CreateCard(configurations[i]);
            }
            // Parked: hidden, still loaded and connected (the debug service keeps waiting for them otherwise).
            for (var i = configurations.Count; i < pool.Count; i++) pool[i].Frame.Visibility = Visibility.Collapsed;
            cards.Clear();
            cards.AddRange(pool.Take(configurations.Count));
            RunPages(reload);
            LayoutCards();
        }

        private PreviewCard CreateCard(PreviewConfiguration configuration)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var label = new TextBlock { Text = configuration.Label, Margin = new Thickness(2, 0, 2, 2), Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis };
            var view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
            var panel = new DockPanel();
            DockPanel.SetDock(label, Dock.Top);
            panel.Children.Add(label);
            panel.Children.Add(view);
            var frame = new Border { Child = panel, Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            var card = new PreviewCard(configuration, frame, view, label);
            surface.Children.Add(frame);
            // In the pool before InitializeAsync runs: its first await can complete synchronously, and it gives up on cards outside the pool.
            pool.Add(card);
            _ = InitializeAsync(card);
            return card;
        }

        /// <summary>Applies a configuration to a loaded page in place (no reload, see <see cref="PreviewConfiguration.LiveScript"/>); always false.</summary>
        private bool Reconfigure(PreviewCard card, PreviewConfiguration configuration)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var previous = card.Configuration;
            card.Configuration = configuration;
            card.Label.Text = configuration.Label;
            if (!card.Ready) return false; // InitializeAsync applies the new configuration
            _ = ApplyColorSchemeAsync(card);
            if (previous.DocumentScript() == configuration.DocumentScript()) return false;
            _ = ReplaceScriptAsync(card);
            return false;
        }

        private async Task ReplaceScriptAsync(PreviewCard card)
        {
            try
            {
                var core = card.View.CoreWebView2;
                if (card.ScriptId != null) core.RemoveScriptToExecuteOnDocumentCreated(card.ScriptId);
                card.ScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(card.Configuration.DocumentScript());
                if (card.Opened) await core.ExecuteScriptAsync(card.Configuration.LiveScript());
                if (DartSettings.Load().WidgetPreviewVerbose) environment?.Log("Widget preview: page '" + card.Configuration.Label + "' reconfigured in place.");
            }
            catch (Exception e) when (e is InvalidOperationException || e is COMException || e is ObjectDisposedException) { }
        }

        /// <summary>
        /// The page's <c>prefers-color-scheme</c> through DevTools media emulation: per page, applied live (Flutter web
        /// follows the media query and updates the platform brightness without a reload), kept across reloads.
        /// </summary>
        private async Task ApplyColorSchemeAsync(PreviewCard card)
        {
            try
            {
                var dark = card.Configuration.ProfileKey(ideDark) == "dark";
                var call = card.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia",
                    "{\"features\":[{\"name\":\"prefers-color-scheme\",\"value\":\"" + (dark ? "dark" : "light") + "\"}]}");
                if (await Task.WhenAny(call, Task.Delay(10000)) != call)
                    environment?.Log("Widget preview: the color scheme of '" + card.Configuration.Label + "' was not applied within 10 s.");
                else await call;
            }
            catch (Exception e) when (e is InvalidOperationException || e is COMException || e is ArgumentException || e is ObjectDisposedException)
            {
                environment?.Log("Widget preview: could not set the color scheme of '" + card.Configuration.Label + "': " + e.Message);
            }
        }

        /// <summary>Removes every page (a new previewer, the window closing, the previewer stopping).</summary>
        private void ClearCards()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var card in pool)
            {
                surface.Children.Remove(card.Frame);
                try { card.View.Dispose(); }
                catch (Exception e) when (e is InvalidOperationException || e is COMException) { }
            }
            pool.Clear();
            cards.Clear();
            pagesUrl = null;
            ++pagesGeneration;
        }

        private async Task InitializeAsync(PreviewCard card)
        {
            try
            {
                var webEnvironment = await (sharedEnvironment ??= CreateEnvironmentAsync());
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!pool.Contains(card)) return;
                await card.View.EnsureCoreWebView2Async(webEnvironment);
                if (!pool.Contains(card)) return;
                var core = card.View.CoreWebView2;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = true;
                core.NewWindowRequested += (_, e) => { e.Handled = true; Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); };
                core.ProcessFailed += (_, e) => OnUi(() => ShowNotice(new PreviewNotice(PreviewNoticeKind.Warning, "A preview page stopped (" + e.ProcessFailedKind + "). Restart the previewer to show it again.", ("Restart previewer", () => service?.Run(() => service.RestartAsync())))));
                core.NavigationCompleted += (_, e) =>
                {
                    // Media emulation needs a document; Flutter follows prefers-color-scheme changes live.
                    if (e.IsSuccess) _ = ApplyColorSchemeAsync(card);
                    if (!e.IsSuccess) environment?.Log("Widget preview: page '" + card.Configuration.Label + "' failed to load (" + e.WebErrorStatus + ").");
                    else if (DartSettings.Load().WidgetPreviewVerbose) environment?.Log("Widget preview: page '" + card.Configuration.Label + "' loaded in WebView2.");
                };
                card.ScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(card.Configuration.DocumentScript());
                _ = ApplyColorSchemeAsync(card); // not awaited: it must never hold up opening the page
                card.Ready = true;
                ApplyZoom(card);
                card.Initialized.TrySetResult(true); // RunPages opens the pages one at a time
            }
            catch (WebView2RuntimeNotFoundException)
            {
                ShowNotice(new PreviewNotice(PreviewNoticeKind.Error, "The Microsoft Edge WebView2 Runtime is not installed, so previews cannot be shown in Visual Studio. Install it, or open the page in a browser.",
                    ("Get WebView2", () => Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true })),
                    ("Open in browser", () => { if (url != null) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); })));
            }
            catch (Exception e)
            {
                // Started detached: every failure must be reported here, or it would be lost.
                environment?.Log("Widget preview: WebView2 failed for '" + card.Configuration.Label + "': " + e);
                ShowNotice(new PreviewNotice(PreviewNoticeKind.Error, "The preview page could not be shown (WebView2: " + e.Message + ").", ("Open in browser", () => { if (url != null) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); })));
            }
        }

        private static Task<CoreWebView2Environment> CreateEnvironmentAsync()
        {
            Directory.CreateDirectory(UserDataFolder);
            return CoreWebView2Environment.CreateAsync(null, UserDataFolder);
        }

        /// <summary>
        /// Places the cards in a grid that fits the window without scrolling (WebView2 is a native child window; a WPF
        /// ScrollViewer cannot clip it). Fixed device sizes are scaled down with WebView2's zoom, so the page still sees
        /// the device size in CSS pixels; "Fit window" cards share the space.
        /// </summary>
        private void LayoutCards()
        {
            if (cards.Count == 0) return;
            double availableWidth = Math.Max(100, surface.ActualWidth - 8), availableHeight = Math.Max(100, surface.ActualHeight - 8);
            const double labelHeight = 22, margin = 8;
            var best = (Columns: 1, Scale: 0.0);
            for (var columns = 1; columns <= cards.Count; columns++)
            {
                var rows = (cards.Count + columns - 1) / columns;
                double cellWidth = availableWidth / columns - margin, cellHeight = availableHeight / rows - margin - labelHeight;
                if (cellWidth <= 20 || cellHeight <= 20) continue;
                var scale = cards.Where(c => c.Configuration.Width != null).Select(c => Math.Min(cellWidth / c.Configuration.Width!.Value, cellHeight / c.Configuration.Height!.Value)).DefaultIfEmpty(cellWidth * cellHeight / 1e6).Min();
                if (scale > best.Scale) best = (columns, scale);
            }
            var bestRows = (cards.Count + best.Columns - 1) / best.Columns;
            double cellW = availableWidth / best.Columns, cellH = availableHeight / bestRows;
            var zoom = Math.Min(1.0, Math.Max(0.25, best.Scale));
            for (var i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                var config = card.Configuration;
                double width, height;
                if (config.Width == null) { width = cellW - margin; height = cellH - margin - labelHeight; card.Label.Text = config.Label; }
                else
                {
                    width = config.Width.Value * zoom;
                    height = config.Height!.Value * zoom;
                    card.Label.Text = config.Label + (zoom < 0.999 ? " - " + Math.Round(zoom * 100) + "%" : "");
                }
                card.View.Width = Math.Max(20, width);
                card.View.Height = Math.Max(20, height);
                card.Frame.Margin = new Thickness(4 + (i % best.Columns) * cellW, 4 + (i / best.Columns) * cellH, 0, 0);
                card.Frame.Tag = config.Width == null ? 1.0 : zoom;
                ApplyZoom(card);
            }
        }

        private static void ApplyZoom(PreviewCard card)
        {
            if (!card.Ready || !(card.Frame.Tag is double zoom)) return;
            try { card.View.ZoomFactor = zoom; }
            catch (Exception e) when (e is InvalidOperationException || e is COMException) { }
        }

        /// <summary>Reloads every page, parked ones too (the Reload button, a page refresh request, a hot restart that did not reach every page).</summary>
        private void ReloadPages(bool all)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!all) return; // a hot reload reaches every connected page through the debug service
            RunPages(pool.Where(c => c.Opened).ToList());
        }

        private readonly List<PreviewCard> pool = new List<PreviewCard>();
        private string? pagesUrl;
        private int pagesGeneration;
        private TaskCompletionSource<bool>? connectedSignal;

        /// <summary>
        /// Opens or reloads pages one at a time: the next page starts once the previous one started the previewer app (or
        /// after 45 s). Pages that connect at the same moment were seen to leave one app not started in Visual Studio.
        /// A newer call (rebuild, reload) supersedes a running one.
        /// </summary>
        private void RunPages(IReadOnlyList<PreviewCard> reload)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var generation = ++pagesGeneration;
            // A newer call supersedes this one, so every call also opens the pages that were not opened yet.
            var pages = reload.Concat(pool.Where(c => !c.Opened && !reload.Contains(c))).ToList();
            var page = url;
#pragma warning disable VSSDK007 // Detached on purpose: superseded by the next call; failures are reported per page.
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                foreach (var card in pages)
                {
                    await Task.WhenAny(card.Initialized.Task, Task.Delay(60000));
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (generation != pagesGeneration || !pool.Contains(card) || !card.Ready) continue;
                    var signal = connectedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    try
                    {
                        if (card.Opened) card.View.CoreWebView2.Reload();
                        else if (page != null) { card.View.CoreWebView2.Navigate(page); card.Opened = true; }
                    }
                    catch (Exception e) when (e is InvalidOperationException || e is COMException || e is NullReferenceException) { continue; }
                    await Task.WhenAny(signal.Task, Task.Delay(45000));
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                }
            });
#pragma warning restore VSSDK007
        }

        private void OnIdeThemeChanged()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var background = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
            var foreground = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey);
            var dark = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) < 128;
            var changed = dark != ideDark;
            ideDark = dark;
            service?.Run(() => service.SetThemeAsync(dark, Hex(background), Hex(foreground)));
            if (changed) foreach (var card in pool.Where(c => c.Ready && c.Configuration.Theme == PreviewTheme.Ide)) _ = ApplyColorSchemeAsync(card);
        }

        private static string Hex(System.Drawing.Color color) => "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");

        // ---- Active document ----

        private void OnDocumentFrame(object? value)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (service == null || !(value is IVsWindowFrame frame) || ErrorHandler.Failed(frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out var moniker))
                || !(moniker is string path) || !DartProject.IsDartFile(path)) return;
            var line = 0;
            var view = VsShellUtilities.GetTextView(frame);
            if (view != null && ErrorHandler.Succeeded(view.GetCaretPos(out var caretLine, out _))) line = caretLine;
            service.Run(() => service.OnActiveDocumentAsync(path, line));
        }

        public int OnElementValueChanged(uint elementid, object varValueOld, object varValueNew)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (elementid == (uint)VSConstants.VSSELELEMID.SEID_DocumentFrame) OnDocumentFrame(varValueNew);
            return VSConstants.S_OK;
        }
        public int OnSelectionChanged(IVsHierarchy pHierOld, uint itemidOld, IVsMultiItemSelect pMISOld, ISelectionContainer pSCOld,
            IVsHierarchy pHierNew, uint itemidNew, IVsMultiItemSelect pMISNew, ISelectionContainer pSCNew) => VSConstants.S_OK;
        public int OnCmdUIContextChanged(uint dwCmdUICookie, int fActive) => VSConstants.S_OK;

        // ---- Frame notifications: closing the window stops the previewer ----

        /// <summary>
        /// Frame events. Closing the tool window (X button, Window.Close, Shift+Esc) hides its frame: Visual Studio 18
        /// reports WinHidden (seen in the acceptance run), older versions WinClosed. An auto-hidden window sliding back
        /// in is hidden too, but must keep the previewer running; switching tabs is TabDeactivated.
        /// </summary>
        private sealed class FrameEvents : IVsWindowFrameNotify
        {
            private readonly FlutterPreviewWindow owner;
            public FrameEvents(FlutterPreviewWindow owner) => this.owner = owner;
            public int OnShow(int fShow)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (fShow == (int)__FRAMESHOW.FRAMESHOW_WinClosed) owner.StopFromWindow();
                else if (fShow == (int)__FRAMESHOW.FRAMESHOW_WinHidden && !owner.IsAutoHidden()) owner.StopFromWindow();
                return VSConstants.S_OK;
            }
            public int OnMove() => VSConstants.S_OK;
            public int OnSize() => VSConstants.S_OK;
            public int OnDockableChange(int fDockable) => VSConstants.S_OK;
        }

        private bool IsAutoHidden()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Frame is IVsWindowFrame frame && ErrorHandler.Succeeded(frame.GetProperty((int)__VSFPROPID.VSFPROPID_FrameMode, out var mode))
                && mode is int value && value == (int)VSFRAMEMODE2.VSFM_AutoHide;
        }

        private void StopFromWindow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ClearCards(); // the previewer stops anyway
            url = null;
            if (service != null && service.State != PreviewSessionState.Stopped) service.Run(() => service.StopAsync("the preview window was closed"));
        }
        // ---- Small helpers ----

        private static ComboBox Combo(double width) => new ComboBox { Width = width, Margin = new Thickness(2, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };

        private static FrameworkElement Labeled(string text, ComboBox combo)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 2, 0) });
            panel.Children.Add(combo);
            return panel;
        }

        private static Button ToolButton(string text, string tooltip)
        {
            var button = new Button { Content = text, ToolTip = tooltip, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(2), IsEnabled = text == "Output" };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "FlutterPreview." + text);
            return button;
        }

        private static void Fill(ComboBox combo, IEnumerable<(string Label, string Value)> items, string selected)
        {
            combo.Items.Clear();
            foreach (var (label, value) in items) combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
            combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, selected, StringComparison.OrdinalIgnoreCase)) ?? combo.Items[0];
        }

        private static string? Value(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

        private static string FirstLine(string message)
        {
            var line = message.Replace("\r", "").Split('\n')[0];
            return line.Length > 200 ? line.Substring(0, 197) + "..." : line;
        }
    }
}
