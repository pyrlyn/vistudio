using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Vistudio.Core.Dart;
using Task = System.Threading.Tasks.Task;

namespace Vistudio.Dart
{
    /// <summary>
    /// Dart package: Tools &gt; Options &gt; Dart, the Dart commands (Tools &gt; Dart menu, Dart toolbar with the Flutter device
    /// drop-down, editor context menu). Separate from the Kotlin and Zig packages. Editor features (coloring, analysis
    /// server, CodeLens, hot reload on save) are MEF/Extensibility parts and do not need this package loaded.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideMenuResource("DartMenus.ctmenu", 1)]
    [ProvideOptionPage(typeof(DartOptionsPage), "Dart", "General", 0, 0, true)]
    [ProvideToolWindow(typeof(Preview.FlutterPreviewWindow), Style = VsDockStyle.Tabbed, Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057", Orientation = ToolWindowOrientation.Right)]
    [ProvideUIContextRule(DartEditorContext, "Dart editor active", "Dart", new[] { "Dart" }, new[] { "ActiveEditorContentType:" + DartContentTypes.Dart })]
    [ProvideAutoLoad(DartEditorContext, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideUIContextRule(DartFileSelectedContext, "Dart file selected", "DartFile", new[] { "DartFile" }, new[] { "HierSingleSelectionName:\\.dart$" })]
    [ProvideAutoLoad(DartFileSelectedContext, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuid)]
    public sealed class DartPackage : AsyncPackage
    {
        internal const string PackageGuid = "5e9a1c37-2b4d-4f6e-8a0c-6d8f0b2e4a91";
        /// <summary>UI context "a Dart editor is active" (also in DartCommands.vsct as guidDartEditorContext).</summary>
        internal const string DartEditorContext = "7f1b3d59-4c6e-4a80-9b2d-8e0a2c4f6b13";
        /// <summary>UI context "a .dart file is selected in Solution Explorer" (DartCommands.vsct guidDartFileSelectedContext).</summary>
        internal const string DartFileSelectedContext = "4c2a8e61-7d3b-4f95-a1e8-2b6f9d0c5e47";
        internal static readonly Guid Commands = new Guid("a2c4e6f8-0b1d-4e3f-9a5c-7e9b1d3f5a72");
        private const int Run = 0x0100, Debug = 0x0101, Stop = 0x0102, HotReload = 0x0103, HotRestart = 0x0104, ListDevices = 0x0105,
            RunTests = 0x0110, RunTestFile = 0x0111, DebugTestFile = 0x0112, RunIntegrationTests = 0x0113,
            PubGet = 0x0120, Analyze = 0x0121, Cancel = 0x0122, NewFlutterApp = 0x0123, RestartServer = 0x0124, Settings = 0x0125,
            DeviceCombo = 0x0130, DeviceComboList = 0x0131,
            PreviewWidget = 0x0140, PreviewWidgetEditor = 0x0141, PreviewWidgetFile = 0x0142, PreviewWindow = 0x0143;

        private DartEnvironment environment = null!;
        private DartRunService run = null!;
        private DartTestService tests = null!;
        private DartDebugCoordinator debug = null!;
        private DartLanguageClient client = null!;
        private Preview.FlutterPreviewService preview = null!;
        private EnvDTE.DTE? dte;
        private readonly HashSet<int> running = new HashSet<int>();
        private IReadOnlyList<FlutterDevice> devices = Array.Empty<FlutterDevice>();
        private Task? devicesRefresh;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            var model = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel ?? throw new InvalidOperationException("Visual Studio component model is unavailable.");
            environment = model.GetService<DartEnvironment>();
            run = model.GetService<DartRunService>();
            tests = model.GetService<DartTestService>();
            debug = model.GetService<DartDebugCoordinator>();
            client = model.GetService<DartLanguageClient>();
            preview = model.GetService<Preview.FlutterPreviewService>();
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            dte = await GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            if (!(await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)) return;

            Add(commands, Run, async () => { await SaveAllAsync(); await run.RunAsync(ActiveDocument()); });
            Add(commands, Debug, async () => await debug.DebugProgramAsync(ActiveDocument(), noDebug: false));
            Add(commands, Stop, () => run.StopAsync(), enabled: () => run.IsAppRunning);
            Add(commands, HotReload, () => ReloadAsync(false), enabled: CanReload);
            Add(commands, HotRestart, () => ReloadAsync(true), enabled: CanReload);
            Add(commands, ListDevices, ListDevicesAsync);
            Add(commands, RunTests, async () => { await SaveAllAsync(); await tests.RunUnitTestsAsync(ActiveDocument()); }, enabled: () => !tests.IsBusy);
            Add(commands, RunTestFile, async () => { await SaveAllAsync(); await tests.RunFileAsync(RequireActive(), null); }, enabled: () => !tests.IsBusy, testDocument: true);
            Add(commands, DebugTestFile, async () => await debug.DebugTestAsync(RequireActive(), null), testDocument: true);
            Add(commands, RunIntegrationTests, async () => { await SaveAllAsync(); await tests.RunIntegrationTestsAsync(ActiveDocument()); }, enabled: () => !tests.IsBusy);
            Add(commands, PubGet, () => run.PubGetAsync(ActiveDocument()), enabled: () => !run.IsToolBusy);
            Add(commands, Analyze, async () => { await SaveAllAsync(); await run.AnalyzeAsync(ActiveDocument()); }, enabled: () => !run.IsToolBusy);
            Add(commands, Cancel, () => { run.CancelTool(); tests.Cancel(); return Task.CompletedTask; }, enabled: () => run.IsToolBusy || tests.IsBusy);
            Add(commands, NewFlutterApp, NewFlutterAppAsync, enabled: () => !run.IsToolBusy);
            Add(commands, RestartServer, async () => { environment.Log("Restarting the Dart analysis server..."); await client.RestartAsync(); });
            Add(commands, Settings, () => { ShowOptionPage(typeof(DartOptionsPage)); return Task.CompletedTask; });

            // Flutter widget preview: the Tools > Dart command and the editor context menu use the active document and caret,
            // the Solution Explorer item uses the selected file. Visible only for .dart files of Flutter packages.
            Add(commands, PreviewWidget, () => PreviewActiveAsync(), previewTarget: PreviewTarget.ActiveDocument);
            Add(commands, PreviewWidgetEditor, () => PreviewActiveAsync(), previewTarget: PreviewTarget.ActiveDocument);
            Add(commands, PreviewWidgetFile, () => PreviewSelectedAsync(), previewTarget: PreviewTarget.Selection);
            Add(commands, PreviewWindow, async () =>
            {
                var window = await ShowToolWindowAsync(typeof(Preview.FlutterPreviewWindow), 0, true, DisposalToken);
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                (window?.Frame as Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame)?.Show();
            });

            commands.AddCommand(new OleMenuCommand(OnDeviceCombo, new CommandID(Commands, DeviceCombo)) { ParametersDescription = "$" });
            commands.AddCommand(new OleMenuCommand(OnDeviceList, new CommandID(Commands, DeviceComboList)));
        }

        private enum PreviewTarget { None, ActiveDocument, Selection }

        private void Add(OleMenuCommandService service, int id, Func<Task> action, Func<bool>? enabled = null, bool testDocument = false, PreviewTarget previewTarget = PreviewTarget.None)
        {
            var command = new OleMenuCommand((_, __) => JoinableTaskFactory.RunAsync(async () =>
            {
                // DTE.ExecuteCommand can reach Exec twice in quick succession (seen with the Zig commands); ignore the repeat.
                if (!running.Add(id)) return;
                try { await action(); }
                catch (Exception e)
                {
                    environment.Log("Dart: " + e.Message);
                    await environment.ShowOutputAsync();
                }
                finally { running.Remove(id); }
            }).Task.Forget(), new CommandID(Commands, id));
            command.BeforeQueryStatus += (_, __) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var on = enabled == null || enabled();
                if (testDocument)
                {
                    var active = ActiveDocument();
                    command.Visible = active != null && (active.EndsWith("_test.dart", StringComparison.OrdinalIgnoreCase) ||
                        active.IndexOf("\\integration_test\\", StringComparison.OrdinalIgnoreCase) >= 0 && DartProject.IsDartFile(active));
                    on &= command.Visible;
                }
                if (previewTarget != PreviewTarget.None)
                {
                    var file = previewTarget == PreviewTarget.ActiveDocument ? ActiveDocument() : SelectedFile();
                    command.Visible = file != null && DartProject.IsDartFile(file) && DartSettings.Load().WidgetPreview && FlutterProjectCache.IsFlutter(file);
                    on &= command.Visible;
                }
                command.Enabled = on;
            };
            service.AddCommand(command);
        }

        /// <summary>Whether a file belongs to a Flutter package, cached per package folder and pubspec.yaml time (command status runs often).</summary>
        private static class FlutterProjectCache
        {
            private static readonly Dictionary<string, (DateTime Time, bool Flutter)> Cache = new Dictionary<string, (DateTime, bool)>(StringComparer.OrdinalIgnoreCase);

            public static bool IsFlutter(string file)
            {
                var root = DartProject.FindRoot(file);
                if (root == null) return false;
                DateTime time;
                try { time = File.GetLastWriteTimeUtc(Path.Combine(root, "pubspec.yaml")); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return false; }
                lock (Cache)
                {
                    if (Cache.TryGetValue(root, out var cached) && cached.Time == time) return cached.Flutter;
                    var flutter = Vistudio.Core.Dart.Preview.FlutterWorkspace.Read(root) != null;
                    Cache[root] = (time, flutter);
                    return flutter;
                }
            }
        }

        private async Task PreviewActiveAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var file = RequireActive();
            string text;
            int? caret = null;
            var document = dte?.ActiveDocument?.Object("TextDocument") as EnvDTE.TextDocument;
            if (document != null)
            {
                var editPoint = document.StartPoint.CreateEditPoint();
                text = editPoint.GetText(document.EndPoint);
                // AbsoluteCharOffset is 1-based and counts a line break as one character.
                caret = OffsetIn(text, document.Selection.ActivePoint.Line, document.Selection.ActivePoint.LineCharOffset);
            }
            else text = File.ReadAllText(file);
            await SaveAllAsync();
            await ShowPreviewWindowAsync();
            await preview.PreviewAtAsync(file, text, caret);
        }

        private async Task PreviewSelectedAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var file = SelectedFile() ?? throw new InvalidOperationException("Select a .dart file in Solution Explorer.");
            await SaveAllAsync();
            await ShowPreviewWindowAsync();
            await preview.PreviewAtAsync(file, await Task.Run(() => File.ReadAllText(file)), null);
        }

        private async Task ShowPreviewWindowAsync()
        {
            var window = await ShowToolWindowAsync(typeof(Preview.FlutterPreviewWindow), 0, true, DisposalToken);
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            (window?.Frame as Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame)?.ShowNoActivate();
        }

        /// <summary>The 0-based offset of a 1-based line and column in <paramref name="text"/> (any line break style).</summary>
        internal static int OffsetIn(string text, int line, int column)
        {
            var offset = 0;
            for (var current = 1; current < line && offset < text.Length; offset++)
            {
                if (text[offset] == '\n') current++;
                else if (text[offset] == '\r' && (offset + 1 >= text.Length || text[offset + 1] != '\n')) current++;
            }
            return Math.Min(text.Length, offset + Math.Max(0, column - 1));
        }

        /// <summary>The file selected in Solution Explorer (a .kotlinproj/.dartproj tree or the Open Folder view).</summary>
        private string? SelectedFile()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(GetService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsShellMonitorSelection)) is Microsoft.VisualStudio.Shell.Interop.IVsMonitorSelection selection)) return null;
            IntPtr hierarchyPointer = IntPtr.Zero, containerPointer = IntPtr.Zero;
            try
            {
                if (Microsoft.VisualStudio.ErrorHandler.Failed(selection.GetCurrentSelection(out hierarchyPointer, out var item, out var multiple, out containerPointer))
                    || hierarchyPointer == IntPtr.Zero || multiple != null) return null;
                if (!(Marshal.GetObjectForIUnknown(hierarchyPointer) is Microsoft.VisualStudio.Shell.Interop.IVsHierarchy hierarchy)) return null;
                return Microsoft.VisualStudio.ErrorHandler.Succeeded(hierarchy.GetCanonicalName(item, out var name)) && name != null && File.Exists(name) ? name : null;
            }
            catch (Exception e) when (e is COMException || e is ArgumentException || e is InvalidCastException) { return null; }
            finally
            {
                if (hierarchyPointer != IntPtr.Zero) Marshal.Release(hierarchyPointer);
                if (containerPointer != IntPtr.Zero) Marshal.Release(containerPointer);
            }
        }

        protected override void Dispose(bool disposing)
        {
            // Visual Studio is exiting: stop the widget previewer (its job objects end the processes even if this is cut short).
            if (disposing) preview?.Dispose();
            base.Dispose(disposing);
        }

        private bool CanReload() => run.IsFlutterAppRunning || DartDebugSessions.Reloadable.Count > 0;

        private async Task ReloadAsync(bool fullRestart)
        {
            await SaveAllAsync();
            var reloaded = await DartDebugSessions.ReloadAsync(fullRestart, "manual", null);
            if (run.IsFlutterAppRunning) await run.ReloadAsync(fullRestart, "manual");
            else if (reloaded == 0) throw new InvalidOperationException("No Flutter app is running (or it is still starting). Start one with Run or Debug.");
        }

        private async Task ListDevicesAsync()
        {
            await environment.ShowOutputAsync();
            environment.Log("Flutter devices:");
            devices = await run.GetDevicesAsync();
            foreach (var device in devices) environment.Log("  " + device + (device.IsSupported ? "" : " - not supported by this project"));
            environment.Log("Current device: " + DartSettings.Load().FlutterDevice + " (change it in the Dart toolbar or Tools > Options > Dart > Device).");
        }

        private async Task NewFlutterAppAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            string folder;
            using (var dialog = new System.Windows.Forms.SaveFileDialog
            {
                Title = "New Flutter app: choose the parent folder and type the app folder name (a Dart package name)",
                FileName = "my_app",
                CheckPathExists = true,
                OverwritePrompt = false,
                Filter = "Folder|*.",
            })
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                folder = dialog.FileName.TrimEnd('.');
            }
            if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
                throw new InvalidOperationException("The folder is not empty: " + folder);
            if (await run.CreateFlutterAppAsync(folder))
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                dte?.ExecuteCommand("File.OpenFolder", "\"" + folder + "\"");
            }
        }

        private string? ActiveDocument()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return dte?.ActiveDocument?.FullName; }
            catch (ArgumentException) { return null; }
            catch (COMException) { return null; }
        }

        private string RequireActive()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return ActiveDocument() ?? throw new InvalidOperationException("Open a .dart file first.");
        }

        private async Task SaveAllAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            try { dte?.ExecuteCommand("File.SaveAll"); }
            catch (COMException) { }
        }

        /// <summary>The device drop-down on the Dart toolbar: shows and sets <see cref="DartSettings.FlutterDevice"/>.</summary>
        private void OnDeviceCombo(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(e is OleMenuCmdEventArgs args)) return;
            if (args.OutValue != IntPtr.Zero)
            {
                Marshal.GetNativeVariantForObject(DartSettings.Load().FlutterDevice, args.OutValue);
                return;
            }
            if (!(args.InValue is string chosen)) return;
            // Entries read "Name (id, platform)"; typing an id works too.
            var id = devices.FirstOrDefault(d => d.ToString() == chosen)?.Id ?? chosen.Trim();
            try
            {
                var settings = DartSettings.LoadGlobal();
                settings.FlutterDevice = DartArguments.ValidateDevice(id);
                settings.Save();
                DartEnvironment.OnSettingsChanged();
                environment.Log("Flutter device: " + settings.FlutterDevice);
                if (DartSettings.Load().FlutterDevice != settings.FlutterDevice)
                    environment.Log("Dart: this workspace's .vs\\settings.json sets vistudio.dart.flutterDevice, which overrides the chosen device in this window.");
            }
            catch (ArgumentException error) { environment.Log("Dart: " + error.Message); }
        }

        private void OnDeviceList(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(e is OleMenuCmdEventArgs args) || args.OutValue == IntPtr.Zero) return;
            if (devices.Count == 0 && (devicesRefresh == null || devicesRefresh.IsCompleted))
                devicesRefresh = Task.Run(async () =>
                {
                    try { devices = await run.GetDevicesAsync(); }
                    catch (Exception error) { environment.Log("flutter devices: " + error.Message); }
                });
            var items = devices.Where(d => d.IsSupported).Select(d => d.ToString()).ToArray();
            Marshal.GetNativeVariantForObject(items.Length == 0 ? new[] { "windows", "chrome", "web-server" } : items, args.OutValue);
        }
    }
}
