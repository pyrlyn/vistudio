using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Vistudio.Core.Dart;
using Vistudio.Core.Dart.Preview;
using Task = System.Threading.Tasks.Task;

namespace Vistudio.Dart.Preview
{
    /// <summary>What the preview window shows above the previews: a message and optional actions.</summary>
    internal enum PreviewNoticeKind { Info, Warning, Error }

    internal sealed class PreviewNotice
    {
        public PreviewNotice(PreviewNoticeKind kind, string text, params (string Label, Action Run)[] actions)
        { Kind = kind; Text = text; Actions = actions; }
        public PreviewNoticeKind Kind { get; }
        public string Text { get; }
        public (string Label, Action Run)[] Actions { get; }
    }

    /// <summary>
    /// The IDE side of Flutter widget previews: decides what to preview (the package's own <c>@Preview</c> declarations,
    /// or a temporary host project for a widget without one), finds and checks the Flutter SDK, runs one
    /// <see cref="FlutterPreviewSession"/> at a time, forwards the active file and theme, reloads host projects after
    /// saves, and stops everything when the window, the solution/folder or Visual Studio closes. The language server,
    /// the debugger and this previewer stay separate: the previewer has its own analysis server and DTD.
    /// </summary>
    [Export, PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class FlutterPreviewService : IVsSolutionEvents, IDisposable
    {
        private readonly DartEnvironment environment;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private FlutterPreviewSession? session;
        private string? sessionRoot;
        private FlutterProjectInfo? project;
        private PreviewHostProject? host;
        private string? activeFile;
        private bool themeDark;
        private (string? Background, string? Foreground) themeColors;
        private int restartsAfterCrash;
        private uint solutionCookie;
        private uint statusCookie;

        /// <summary>The window shows these; raised on any thread.</summary>
        public event Action<PreviewSessionState, string>? StateChanged;
        public event Action<string?>? UrlChanged;
        public event Action<PreviewNotice?>? NoticeChanged;
        public event Action<PreviewerStatus, string>? PreviewerStatusChanged;
        /// <summary>The window must reload its pages (secondary configurations after a reload, or a page refresh request).</summary>
        public event Action<bool>? ReloadPagesRequested;
        /// <summary>A page started the previewer app (the window starts the next configuration's page then).</summary>
        public event Action? PageConnected;

        [ImportingConstructor]
        public FlutterPreviewService(DartEnvironment environment)
        {
            this.environment = environment;
            // The close-folder handler also runs while Visual Studio shuts down: start the stop and return at once (awaiting
            // it there could wait for the UI thread that is waiting for this handler).
            environment.WorkspaceChanged += () =>
            {
                if (session != null) _ = Task.Run(() => StopAsync("the folder or solution changed"));
                return Task.CompletedTask;
            };
            DartEnvironment.SettingsChanged += (_, __) => OnSettingsChanged();
        }

        public PreviewSessionState State => session?.State ?? PreviewSessionState.Stopped;
        public string? Url => session?.Url;
        public FlutterProjectInfo? Project => project;
        public PreviewHostProject? Host => host;
        public PreviewNotice? Notice { get; private set; }
        public string? LastMessage { get; private set; }

        /// <summary>
        /// Shows the preview for <paramref name="file"/>. With <paramref name="widgetClass"/> (a widget without
        /// <c>@Preview</c>) the widget is previewed through the temporary host project; otherwise the package's own previews.
        /// </summary>
        public async Task PreviewAsync(string file, string? widgetClass)
        {
            await EnsureSubscriptionsAsync();
            var settings = await LoadSettingsAsync();
            if (!settings.WidgetPreview)
            {
                ShowNotice(new PreviewNotice(PreviewNoticeKind.Info, "Flutter widget previews are turned off (Tools > Options > Dart > Widget preview).", ("Open settings", OpenSettings)));
                return;
            }
            var flutter = FlutterWorkspace.ProjectOf(file);
            if (flutter == null)
            {
                var message = DartProject.FindRoot(file) == null
                    ? Path.GetFileName(file) + " is not part of a Dart package (no pubspec.yaml above it)."
                    : Path.GetFileName(file) + " belongs to a Dart package that does not depend on Flutter (pubspec.yaml has no 'flutter: sdk: flutter' dependency), so it has no widget previews.";
                environment.Log("Widget preview: " + message);
                ShowNotice(new PreviewNotice(PreviewNoticeKind.Info, message));
                return;
            }

            var lookup = DartToolchain.LocateFlutter(settings, FlutterSdkEnvironment.Current, flutter.Root);
            if (lookup.Location == null)
            {
                environment.Log("Widget preview: " + lookup.Error);
                ShowNotice(new PreviewNotice(PreviewNoticeKind.Error, (lookup.Error ?? "No Flutter SDK was found.") + " Set the Flutter SDK folder to preview widgets.",
                    ("Set Flutter SDK path...", () => ChooseSdkFolder(flutter.Root)), ("Open settings", OpenSettings)));
                return;
            }
            var version = DartToolchain.ReadFlutterVersion(lookup.Location.Root);
            var unsupported = WidgetPreviewSupport.Check(version);
            if (unsupported != null)
            {
                environment.Log("Widget preview: " + unsupported + " (" + lookup.Location + ")");
                ShowNotice(new PreviewNotice(PreviewNoticeKind.Error, unsupported + " SDK: " + lookup.Location.Root,
                    ("Choose another SDK...", () => ChooseSdkFolder(flutter.Root)), ("Open settings", OpenSettings)));
                return;
            }
            var sdk = DartToolchain.Find(settings, FlutterSdkEnvironment.Current, flutter.Root)
                ?? throw new FileNotFoundException("The Flutter SDK has no Dart SDK yet (bin\\cache\\dart-sdk). Run 'flutter --version' once in " + lookup.Location.Root + ".");

            string root;
            PreviewNotice? notice = null;
            if (widgetClass != null)
            {
                var text = await Task.Run(() => File.ReadAllText(file));
                var widget = WidgetPreviewScanner.Scan(text).Widgets.FirstOrDefault(w => w.Name == widgetClass);
                if (widget == null) throw new InvalidOperationException("No widget class '" + widgetClass + "' in " + Path.GetFileName(file) + ".");
                if (widget.Problem != null || widget.Construction == null)
                {
                    ShowNotice(new PreviewNotice(PreviewNoticeKind.Warning, widget.Problem ?? "'" + widgetClass + "' cannot be created without arguments; add a @Preview function that builds it."));
                    return;
                }
                if (host == null || !string.Equals(host.PackageRoot, flutter.Root, StringComparison.OrdinalIgnoreCase))
                    host = new PreviewHostProject(flutter.Root, flutter.Name, PreviewHostProject.DefaultFolder(flutter.Root, flutter.Name));
                host.Add(new WrappedWidget(file, widget.Name, widget.Construction));
                var pubspec = await Task.Run(() => File.ReadAllText(Path.Combine(flutter.Root, "pubspec.yaml")));
                await Task.Run(() => host.Write(pubspec));
                root = host.Folder;
                notice = new PreviewNotice(PreviewNoticeKind.Info,
                    widgetClass + " has no @Preview, so it is shown through a temporary project (" + host.Folder + "); your sources are not changed.",
                    ("Add @Preview to source", () => Run(() => AddPreviewToSourceAsync(file, widgetClass))));
            }
            else
            {
                if (host != null && string.Equals(sessionRoot, host.Folder, StringComparison.OrdinalIgnoreCase)) host = null;
                root = flutter.Root;
            }

            project = flutter;
            activeFile = widgetClass != null && host != null ? host.PreviewsPath : file;
            ShowNotice(notice);
            await EnsureSessionAsync(sdk, root, settings, lookup.Location);
        }

        /// <summary>"Preview Widget" for a file and caret offset (null: file selected in Solution Explorer): see <see cref="PreviewChoice.For"/>.</summary>
        public async Task PreviewAtAsync(string file, string text, int? caret)
        {
            var scanned = WidgetPreviewScanner.Scan(text);
            var choice = PreviewChoice.For(scanned, caret ?? -1);
            if (!choice.HasPreview && FlutterWorkspace.ProjectOf(file) != null)
            {
                environment.Log("Widget preview: " + choice.Message);
                ShowNotice(new PreviewNotice(PreviewNoticeKind.Info, Path.GetFileName(file) + ": " + choice.Message));
                return;
            }
            await PreviewAsync(file, choice.WrapWidget);
        }

        /// <summary>The active document changed: the previewer shows that file's previews (package mode only).</summary>
        public async Task OnActiveDocumentAsync(string path, int line)
        {
            var current = session;
            if (current == null || project == null || !DartProject.IsDartFile(path)) return;
            if (host != null && string.Equals(sessionRoot, host.Folder, StringComparison.OrdinalIgnoreCase)) return; // host mode keeps its generated file
            if (!IsUnder(path, project.Root)) return;
            activeFile = path;
            await current.SetActiveFileAsync(path, line, 0);
        }

        /// <summary>A Dart file was saved: a host project is touched so its previewer reloads (package mode reloads by itself).</summary>
        public void OnDartFileSaved(string path)
        {
            var current = host;
            if (session == null || current == null || !string.Equals(sessionRoot, current.Folder, StringComparison.OrdinalIgnoreCase)) return;
            if (!IsUnder(path, current.PackageRoot)) return;
            try { current.Touch(); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { environment.Log("Widget preview: " + e.Message); }
        }

        /// <summary>True when <paramref name="path"/> belongs to the package being previewed (for the reload-after-typing option).</summary>
        public bool IsPreviewing(string path) => session != null && project != null && IsUnder(path, project.Root);

        public async Task SetThemeAsync(bool dark, string? background, string? foreground)
        {
            themeDark = dark;
            themeColors = (background, foreground);
            var current = session;
            if (current != null) await current.SetThemeAsync(dark, background, foreground, null);
        }

        public async Task HotRestartAsync()
        {
            var current = session ?? throw new InvalidOperationException("The widget previewer is not running.");
            environment.Log("Widget preview: hot restart requested.");
            await current.HotRestartAsync();
        }

        /// <summary>Starts the previewer again for the current package or host project.</summary>
        public async Task RestartAsync()
        {
            var file = activeFile;
            var root = sessionRoot;
            await StopAsync(null);
            if (root != null && file != null)
            {
                var settings = await LoadSettingsAsync();
                var lookup = DartToolchain.LocateFlutter(settings, FlutterSdkEnvironment.Current, project?.Root ?? root);
                var sdk = DartToolchain.Find(settings, FlutterSdkEnvironment.Current, project?.Root ?? root);
                if (lookup.Location == null || sdk == null) throw new FileNotFoundException(lookup.Error ?? "No Flutter SDK was found.");
                await EnsureSessionAsync(sdk, root, settings, lookup.Location);
            }
        }

        private async Task EnsureSessionAsync(DartSdk sdk, string root, DartSettings settings, FlutterSdkLocation location)
        {
            await gate.WaitAsync();
            try
            {
                var current = session;
                if (current != null && string.Equals(sessionRoot, root, StringComparison.OrdinalIgnoreCase) && string.Equals(current.Options.Sdk.FlutterRoot, sdk.FlutterRoot, StringComparison.OrdinalIgnoreCase)
                    && current.State != PreviewSessionState.Failed && current.State != PreviewSessionState.Stopped)
                {
                    if (activeFile != null) await current.SetActiveFileAsync(activeFile, 0, 0);
                    UrlChanged?.Invoke(current.Url);
                    return;
                }
                await StopSessionAsync();
                restartsAfterCrash = 0;
                await StartSessionAsync(sdk, root, settings, location);
            }
            finally { gate.Release(); }
        }

        private async Task StartSessionAsync(DartSdk sdk, string root, DartSettings settings, FlutterSdkLocation location)
        {
            var created = new FlutterPreviewSession(new FlutterPreviewSessionOptions(sdk, root) { RunPubGet = settings.WidgetPreviewPubGet, Verbose = settings.WidgetPreviewVerbose });
            var verbose = settings.WidgetPreviewVerbose;
            created.Log += (level, message) => { if (level != "trace" || verbose) environment.Log("Widget preview" + (level == "error" ? " error" : level == "warning" ? " warning" : "") + ": " + message); };
            created.StateChanged += (state, message) => OnStateChanged(created, state, message);
            created.Started += url => { if (session == created) UrlChanged?.Invoke(url); };
            created.Status += (status, message) => OnPreviewerStatus(created, status, message);
            created.NavigateRequested += (path, line, column) => NavigateTo(path, line, column);
            created.Exited += (code, expected) => OnExited(created, code, expected);
            created.PreviewProblems += problems => OnPreviewProblems(created, problems);
            created.PageConnected += () => { if (session == created) PageConnected?.Invoke(); };
            session = created;
            sessionRoot = root;
            environment.Log("Widget preview: Flutter " + (DartToolchain.ReadFlutterVersion(location.Root) ?? "?") + " (" + location + ") in " + root);
            UrlChanged?.Invoke(null);
            try
            {
                if (activeFile != null) await created.SetActiveFileAsync(activeFile, 0, 0);
                await created.SetThemeAsync(themeDark, themeColors.Background, themeColors.Foreground, null);
                await created.StartAsync(CancellationToken.None);
            }
            catch (Exception e)
            {
                environment.Log("Widget preview: " + e.Message);
                if (session == created && !created.IsStopping)
                    ShowNotice(new PreviewNotice(PreviewNoticeKind.Error, "The widget previewer could not start: " + e.Message, ("Restart", () => Run(RestartAsync)), ("Show output", ShowOutput)));
            }
        }

        private void OnStateChanged(FlutterPreviewSession source, PreviewSessionState state, string message)
        {
            if (session != source) return;
            LastMessage = message;
            StateChanged?.Invoke(state, message);
            Run(() => UpdateStatusBarAsync(state, message));
        }

        private void OnPreviewerStatus(FlutterPreviewSession source, PreviewerStatus status, string message)
        {
            if (session != source) return;
            PreviewerStatusChanged?.Invoke(status, message);
            switch (status)
            {
                case PreviewerStatus.Reloaded:
                case PreviewerStatus.Restarted:
                    if (Notice?.Kind == PreviewNoticeKind.Error) ShowNotice(null);
                    ReloadPagesRequested?.Invoke(false);
                    break;
                case PreviewerStatus.PageRefreshRequired:
                    ReloadPagesRequested?.Invoke(true);
                    break;
                case PreviewerStatus.RestartFailed:
                    // The previewer's debug service waits for pages that are gone; only a fresh previewer recovers.
                    if (DateTime.UtcNow - lastProcessRestart < TimeSpan.FromMinutes(2)) break;
                    lastProcessRestart = DateTime.UtcNow;
                    environment.Log("Widget preview: the hot restart did not reach every page; restarting the previewer.");
                    ShowNotice(new PreviewNotice(PreviewNoticeKind.Warning, "Hot reload and hot restart did not reach every preview page, so the previewer was restarted. Previews lose their state.", ("Show output", ShowOutput)));
                    Run(RestartAsync);
                    break;
                case PreviewerStatus.ReloadFailed:
                    ShowNotice(new PreviewNotice(PreviewNoticeKind.Warning, "Hot reload was not possible, so the previews were restarted (their state was reset): " + message.Trim()));
                    break;
                case PreviewerStatus.Error:
                    ShowNotice(new PreviewNotice(PreviewNoticeKind.Error, message.Trim(), ("Show output", ShowOutput)));
                    break;
                case PreviewerStatus.ConnectionLost:
                    ShowNotice(new PreviewNotice(PreviewNoticeKind.Warning, message.Trim(), ("Restart previewer", () => Run(RestartAsync))));
                    break;
            }
        }

        private PreviewNotice? problemsNotice;
        private DateTime lastProcessRestart = DateTime.MinValue;

        /// <summary>Previews left out because of code errors (they appear as error cards in the page; the details are in the Error List).</summary>
        private void OnPreviewProblems(FlutterPreviewSession source, IReadOnlyList<LspWidgetPreview> problems)
        {
            if (session != source) return;
            if (problems.Count == 0)
            {
                if (Notice != null && Notice == problemsNotice) ShowNotice(null);
                return;
            }
            var names = string.Join(", ", problems.Take(5).Select(p => p.FunctionName + " (" + FileName(p.ScriptUri) + ":" + (p.Line + 1) + ")")) + (problems.Count > 5 ? ", ..." : "");
            problemsNotice = new PreviewNotice(PreviewNoticeKind.Error,
                problems.Count + (problems.Count == 1 ? " preview is" : " previews are") + " not rendered because the code has errors: " + names + ". Fix them (see the Error List) and save.",
                ("Show output", ShowOutput));
            ShowNotice(problemsNotice);
        }

        private static string FileName(string? uri) =>
            uri != null && Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? Path.GetFileName(parsed.LocalPath) : uri ?? "?";

        private void OnExited(FlutterPreviewSession source, int code, bool expected)
        {
            if (expected || session != source) return;
            if (source.State == PreviewSessionState.Ready && restartsAfterCrash == 0)
            {
                // It ran fine before: start it once more automatically and say so.
                restartsAfterCrash++;
                environment.Log("Widget preview: the previewer stopped unexpectedly (exit code " + code + "); restarting it.");
                ShowNotice(new PreviewNotice(PreviewNoticeKind.Warning, "The previewer stopped unexpectedly (exit code " + code + ") and is being restarted. Previews lose their state.", ("Show output", ShowOutput)));
                Run(async () =>
                {
                    var settings = await LoadSettingsAsync();
                    await gate.WaitAsync();
                    try { if (session == source) await StartSessionAsync(source.Options.Sdk, source.Options.ProjectRoot, settings, new FlutterSdkLocation(source.Options.Sdk.FlutterRoot!, FlutterSdkSource.Setting, "previous session")); }
                    finally { gate.Release(); }
                });
                return;
            }
            ShowNotice(new PreviewNotice(PreviewNoticeKind.Error, source.LastError ?? "The previewer stopped (exit code " + code + ").", ("Restart", () => Run(RestartAsync)), ("Show output", ShowOutput)));
        }

        /// <summary>Stops the running previewer (window closed, folder or solution closed, Visual Studio exiting).</summary>
        public async Task StopAsync(string? reason)
        {
            // Stop first without the gate: a start in progress holds it, and stopping the session cancels that start.
            var running = session;
            if (running != null)
            {
                if (reason != null) environment.Log("Widget preview: stopping (" + reason + ")");
                reason = null;
                try { await running.StopAsync(); }
                catch (Exception e) { environment.Log("Widget preview: " + e.Message); }
            }
            await gate.WaitAsync();
            try
            {
                if (session != null && reason != null) environment.Log("Widget preview: stopping (" + reason + ")");
                await StopSessionAsync();
            }
            finally { gate.Release(); }
        }

        private async Task StopSessionAsync()
        {
            var current = session;
            session = null;
            sessionRoot = null;
            if (current == null) return;
            UrlChanged?.Invoke(null);
            try { await current.StopAsync(); }
            catch (Exception e) { environment.Log("Widget preview: " + e.Message); }
            current.Dispose();
            StateChanged?.Invoke(PreviewSessionState.Stopped, "Stopped.");
            Run(() => UpdateStatusBarAsync(PreviewSessionState.Stopped, "")); // not awaited: stopping must not wait for the UI thread
        }

        private void OnSettingsChanged()
        {
            var settings = DartSettings.Load();
            if (!settings.WidgetPreview && session != null) Run(() => StopAsync("widget previews were turned off"));
        }

        private void ShowNotice(PreviewNotice? notice)
        {
            Notice = notice;
            NoticeChanged?.Invoke(notice);
        }

        private static async Task<DartSettings> LoadSettingsAsync() => await Task.Run(() => DartSettings.Load());

        private async Task AddPreviewToSourceAsync(string file, string widgetClass)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            FlutterPreviewQuickFix.Apply(file, widgetClass);
            (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ExecuteCommand("File.SaveAll");
            await PreviewAsync(file, null);
        }

        private void ChooseSdkFolder(string projectRoot) => Run(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose the Flutter SDK folder (it contains bin\\flutter.bat)", ShowNewFolderButton = false })
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                var chosen = dialog.SelectedPath;
                if (!FlutterSdkLocator.IsSdk(FlutterSdkLocatorNormalize(chosen)))
                {
                    ShowNotice(new PreviewNotice(PreviewNoticeKind.Error, chosen + " is not a Flutter SDK folder (no bin\\flutter.bat).", ("Set Flutter SDK path...", () => ChooseSdkFolder(projectRoot))));
                    return;
                }
                var settings = DartSettings.LoadGlobal();
                settings.FlutterSdkPath = FlutterSdkLocatorNormalize(chosen)!;
                settings.Save();
                DartEnvironment.OnSettingsChanged();
                environment.Log("Widget preview: Flutter SDK folder set to " + settings.FlutterSdkPath + " (Tools > Options > Dart > Flutter SDK folder).");
                if (!string.Equals(DartSettings.Load().FlutterSdkPath, settings.FlutterSdkPath, StringComparison.OrdinalIgnoreCase))
                    environment.Log("Widget preview: this workspace's .vs\\settings.json sets vistudio.dart.flutterSdkPath, which overrides the chosen folder in this window.");
            }
            ShowNotice(null);
            if (activeFile != null) await PreviewAsync(activeFile, null);
        });

        private static string? FlutterSdkLocatorNormalize(string folder)
        {
            var full = Path.GetFullPath(folder).TrimEnd('\\');
            return string.Equals(Path.GetFileName(full), "bin", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(full) : full;
        }

        private void OpenSettings() => Run(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ExecuteCommand("Tools.Options", DartOptionsPage.PageGuidString);
        });

        private void ShowOutput() => Run(environment.ShowOutputAsync);

        private void NavigateTo(string path, int line, int column) => Run(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!File.Exists(path)) return;
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path, VSConstants.LOGVIEWID_Primary, out _, out _, out var frame);
            frame?.Show();
            var view = VsShellUtilities.GetTextView(frame);
            if (view == null || line <= 0) return;
            view.SetCaretPos(line - 1, Math.Max(0, column - 1));
            view.CenterLines(line - 1, 1);
        });

        private async Task UpdateStatusBarAsync(PreviewSessionState state, string message)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!(ServiceProvider.GlobalProvider.GetService(typeof(SVsStatusbar)) is IVsStatusbar bar)) return;
            object icon = (short)Constants.SBAI_General;
            var busy = state == PreviewSessionState.ResolvingDependencies || state == PreviewSessionState.Starting || state == PreviewSessionState.Serving;
            bar.Animation(busy ? 1 : 0, ref icon);
            if (busy)
            {
                bar.Progress(ref statusCookie, 1, "Flutter widget preview", 0, 0);
                bar.SetText("Flutter widget preview: " + Shorten(message));
            }
            else
            {
                bar.Progress(ref statusCookie, 0, "", 0, 0);
                statusCookie = 0;
                if (state == PreviewSessionState.Ready) bar.SetText("Flutter widget preview ready");
                else if (state == PreviewSessionState.Failed) bar.SetText("Flutter widget preview failed - see Output > Dart");
            }
        }

        private static string Shorten(string message)
        {
            var line = message.Split('\n')[0].Trim();
            return line.Length > 120 ? line.Substring(0, 117) + "..." : line;
        }

        private async Task EnsureSubscriptionsAsync()
        {
            if (solutionCookie != 0) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (solutionCookie == 0 && ServiceProvider.GlobalProvider.GetService(typeof(SVsSolution)) is IVsSolution solution)
                solution.AdviseSolutionEvents(this, out solutionCookie);
        }

        internal static bool IsUnder(string path, string root)
        {
            try { return Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { return false; }
        }

        // Each action reports its own failures in the Dart pane and the window, so it can run detached.
#pragma warning disable VSSDK007
        internal void Run(Func<Task> action) => ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try { await action(); }
            catch (Exception e)
            {
                environment.Log("Widget preview: " + e.Message);
                ShowNotice(new PreviewNotice(PreviewNoticeKind.Error, e.Message, ("Show output", ShowOutput)));
            }
        }).Task.Forget();
#pragma warning restore VSSDK007

        // IVsSolutionEvents: stop with the solution.
        public int OnAfterCloseSolution(object pUnkReserved)
        {
            Run(() => StopAsync("the solution was closed"));
            return VSConstants.S_OK;
        }
        public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
        public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
        public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;
        public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution) => VSConstants.S_OK;
        public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;

        /// <summary>Visual Studio is exiting: stop synchronously (the job objects end the processes even if this is cut short).</summary>
        public void Dispose()
        {
            var current = session;
            session = null;
            if (current == null) return;
#pragma warning disable VSTHRD002 // Package disposal is synchronous; StopAsync runs on the thread pool and never needs the UI thread.
            try { Task.Run(() => current.StopAsync()).Wait(TimeSpan.FromSeconds(10)); }
#pragma warning restore VSTHRD002
            catch (AggregateException) { }
            current.Dispose();
        }
    }
}
