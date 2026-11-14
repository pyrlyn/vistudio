using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Workspace.VSIntegration.Contracts;
using Vistudio.Core;
using Vistudio.Core.Mise;
using SolutionEvents = Microsoft.VisualStudio.Shell.Events.SolutionEvents;
using Task = System.Threading.Tasks.Task;

// Workspace notifications and log writes run detached: FileAndForget reports failures, nothing waits on them.
#pragma warning disable VSSDK007

namespace Vistudio.Mise
{
    /// <summary>What a mise action applies to: one project folder and the config files whose tools it touches.</summary>
    internal sealed class MiseScope
    {
        public MiseScope(string folder, IReadOnlyList<string> files) { Folder = folder; Files = files; }
        public string Folder { get; }
        public IReadOnlyList<string> Files { get; }
        public string Title => string.Join(", ", Files.Select(f => f.Length > Folder.Length + 1 && f.StartsWith(Folder, StringComparison.OrdinalIgnoreCase) ? f.Substring(Folder.Length + 1) : Path.GetFileName(f)));

        public static MiseScope ForFile(string path) => new MiseScope(MiseConfigFiles.ProjectFolder(path), new[] { MiseConfigFiles.Normalize(path) });
    }

    /// <summary>
    /// Shared mise state for the editor hints, CodeLens, info bar and commands: the "Mise" Output pane, the workspace root,
    /// one cached report per project folder (refreshed on save, workspace change and after every action) and one running
    /// mise command at a time. mise is user-installed; nothing here answers its prompts.
    /// </summary>
    [Export, PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class MiseService
    {
        private static readonly Guid PaneId = new Guid("8b0e5f7a-3c41-4d2e-9f60-7a1b2c3d4e5f");
        private readonly IServiceProvider services;
        private readonly WorkspaceSettingsMonitor workspace;
        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> reports = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> loggedErrors = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        private IVsOutputWindowPane? pane;
        private IVsFolderWorkspaceService? folders;
        private bool initialized;
        private int busy;

        private sealed class Entry
        {
            public Task<MiseReport> Task = null!;
            public MiseReport? Result;
        }

        /// <summary>Raised (on any thread) with the project folder whose report changed.</summary>
        public event EventHandler<string>? ReportChanged;
        /// <summary>Raised after a folder or solution opens or closes.</summary>
        public event Func<Task>? WorkspaceChanged;
        /// <summary>Raised after Tools &gt; Options &gt; Mise is saved.</summary>
        public static event EventHandler? SettingsChanged;
        internal static void OnSettingsChanged()
        {
            settings = null;
            SettingsChanged?.Invoke(null, EventArgs.Empty);
        }

        private static MiseSettings? settings;
        /// <summary>Tools &gt; Options &gt; Mise, read once and again after each save (editor redraws read it often).</summary>
        public static MiseSettings Settings => settings ??= MiseSettings.Load();

        public bool IsBusy => Volatile.Read(ref busy) != 0;

        [ImportingConstructor]
        public MiseService([Import(typeof(SVsServiceProvider))] IServiceProvider services, WorkspaceSettingsMonitor workspace)
        {
            this.services = services;
            this.workspace = workspace;
            SolutionEvents.OnAfterOpenSolution += (_, __) => RaiseWorkspaceChanged();
            SolutionEvents.OnAfterCloseSolution += (_, __) => RaiseWorkspaceChanged();
            SettingsChanged += (_, __) => InvalidateAll();
        }

        private async Task EnsureInitializedAsync()
        {
            if (initialized) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (initialized) return;
            var output = services.GetService(typeof(SVsOutputWindow)) as IVsOutputWindow
                ?? throw new InvalidOperationException("Visual Studio Output service is unavailable.");
            var id = PaneId;
            ErrorHandler.ThrowOnFailure(output.CreatePane(ref id, "Mise", 1, 0));
            ErrorHandler.ThrowOnFailure(output.GetPane(ref id, out pane));
            if (services.GetService(typeof(SComponentModel)) is IComponentModel components)
            {
                folders = components.GetService<IVsFolderWorkspaceService>();
                if (folders != null) folders.OnActiveWorkspaceChanged += (_, __) => { RaiseWorkspaceChanged(); return Task.CompletedTask; };
            }
            initialized = true;
        }

        private void RaiseWorkspaceChanged()
        {
            InvalidateAll();
            var handlers = WorkspaceChanged;
            if (handlers == null) return;
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                foreach (Func<Task> callback in handlers.GetInvocationList())
                {
                    try { await callback(); }
                    catch (Exception e) { Log("mise: " + e.Message); }
                }
            }).FileAndForget("Vistudio/Mise/WorkspaceChanged");
        }

        /// <summary>This window's workspace folder (Open Folder location, else the solution folder), or null.</summary>
        public async Task<string?> GetRootAsync()
        {
            await EnsureInitializedAsync();
            await workspace.RefreshAsync();
            return WorkspaceSettings.CurrentRoot;
        }

        /// <summary>The workspace's own mise configs, or null when it has none.</summary>
        public async Task<MiseScope?> GetWorkspaceScopeAsync()
        {
            var root = await GetRootAsync();
            if (root == null) return null;
            var files = MiseConfigFiles.Find(root);
            return files.Count == 0 ? null : new MiseScope(root, files);
        }

        public MiseReport? Cached(string folder)
        {
            lock (gate) return reports.TryGetValue(folder, out var entry) ? entry.Result : null;
        }

        /// <summary>The report of a project folder; reused until it is older than the cache time, a save, or an action.</summary>
        public Task<MiseReport> GetReportAsync(string folder, bool force = false)
        {
            lock (gate)
            {
                if (!force && reports.TryGetValue(folder, out var existing)
                    && (existing.Result == null || DateTime.UtcNow - existing.Result.CreatedUtc < TimeSpan.FromMinutes(Math.Max(1, Settings.CacheMinutes))))
                    return existing.Task;
                var entry = new Entry();
                reports[folder] = entry;
                entry.Task = CollectAsync(folder, entry);
                return entry.Task;
            }
        }

        public void InvalidateAll()
        {
            lock (gate)
            {
                // Keep the last result visible until the new one arrives; only the freshness check changes.
                foreach (var key in reports.Keys.ToList())
                {
                    var old = reports[key].Result;
                    if (old == null) continue;
                    var stale = old.WithCreated(DateTime.MinValue);
                    reports[key] = new Entry { Task = Task.FromResult(stale), Result = stale };
                }
            }
        }

        private async Task<MiseReport> CollectAsync(string folder, Entry entry)
        {
            await TaskScheduler.Default;
            MiseReport report;
            var mise = MiseCommandFactory.FindExecutable(Settings);
            if (mise == null) report = MiseReport.Failed(NotFound);
            else
            {
                try { report = await MiseCommandFactory.CollectAsync(mise, folder, CancellationToken.None).ConfigureAwait(false); }
                catch (OperationCanceledException) { report = MiseReport.Failed("mise did not answer within two minutes."); }
                catch (Exception e) when (e is IOException || e is InvalidOperationException || e is System.ComponentModel.Win32Exception || e is UnauthorizedAccessException)
                { report = MiseReport.Failed("Could not run mise: " + e.Message); }
            }
            lock (gate)
            {
                entry.Result = report;
                var changed = !loggedErrors.TryGetValue(folder, out var last) || last != report.Error;
                loggedErrors[folder] = report.Error;
                if (changed && report.Error != null) Log("mise (" + folder + "): " + report.Error);
            }
            ReportChanged?.Invoke(this, folder);
            return report;
        }

        internal const string NotFound = "mise.exe was not found. Install mise (https://mise.jdx.dev) or set Tools > Options > Mise > mise.exe path.";

        /// <summary>
        /// Runs install / upgrade / upgrade --bump for the scope's tools only (never a bare <c>mise upgrade</c>, which would
        /// touch every global tool too), or trusts its config; then refreshes the report.
        /// </summary>
        public async Task RunAsync(MiseAction action, MiseScope scope)
        {
            if (Interlocked.Exchange(ref busy, 1) == 1)
            {
                Log("mise: another mise command is still running.");
                await ShowOutputAsync();
                return;
            }
            try
            {
                var mise = MiseCommandFactory.FindExecutable(Settings) ?? throw new InvalidOperationException(NotFound);
                System.Diagnostics.ProcessStartInfo start;
                if (action == MiseAction.Trust)
                {
                    var report = Cached(scope.Folder);
                    var file = report?.UntrustedPath ?? scope.Files.FirstOrDefault() ?? throw new InvalidOperationException("No mise config to trust.");
                    start = MiseCommandFactory.Action(mise, scope.Folder, action, Array.Empty<string>(), file);
                }
                else
                {
                    var report = await GetReportAsync(scope.Folder);
                    if (report.UntrustedPath != null) throw new InvalidOperationException(report.Error + " Use Tools > Mise > Trust Config first.");
                    if (report.Tools.Count == 0 && report.Error != null) throw new InvalidOperationException(report.Error);
                    var names = Targets(report.ForFiles(scope.Files), action);
                    if (names.Count == 0)
                    {
                        Log("mise: " + Nothing(action) + " (" + scope.Title + ").");
                        await ShowOutputAsync();
                        return;
                    }
                    start = MiseCommandFactory.Action(mise, scope.Folder, action, names);
                }
                await ShowOutputAsync();
                Log("> mise " + start.Arguments + "    (" + scope.Folder + ")");
                await TaskScheduler.Default;
                var code = await OwnedProcess.RunAsync(start, Log, CancellationToken.None).ConfigureAwait(false);
                Log(code == 0 ? "mise: " + MiseCommandFactory.Describe(action) + " finished." : "mise: exited with code " + code + ".");
            }
            finally { Volatile.Write(ref busy, 0); }
            InvalidateAll();
            var after = await GetReportAsync(scope.Folder, force: true);
            Log("mise (" + scope.Title + "): " + (after.Tools.Count == 0 && after.Error != null ? after.Error : MiseReport.Summary(after.ForFiles(scope.Files))));
        }

        /// <summary>Tool names an action needs: missing ones for install, outdated (or missing) ones for the upgrades.</summary>
        internal static IReadOnlyList<string> Targets(IReadOnlyList<MiseToolStatus> tools, MiseAction action) => action switch
        {
            MiseAction.Install => tools.Where(t => t.State == MiseToolState.Missing).Select(t => t.Name).ToList(),
            MiseAction.Upgrade => tools.Where(t => t.State == MiseToolState.Missing || t.LatestInRange != null).Select(t => t.Name).ToList(),
            MiseAction.UpgradeToLatest => tools.Where(t => t.State != MiseToolState.UpToDate).Select(t => t.Name).ToList(),
            _ => Array.Empty<string>(),
        };

        private static string Nothing(MiseAction action) => action switch
        {
            MiseAction.Install => "every tool is installed",
            MiseAction.Upgrade => "every tool is at the newest version its config allows",
            _ => "every tool is at the latest version",
        };

        /// <summary>Writes the per-tool state of a scope to the Output pane.</summary>
        public void LogReport(MiseScope scope, MiseReport report)
        {
            if (report.Tools.Count == 0 && report.Error != null) { Log("mise (" + scope.Title + "): " + report.Error); return; }
            var tools = report.ForFiles(scope.Files);
            Log("mise (" + scope.Title + "): " + MiseReport.Summary(tools) + (report.Error != null ? " - " + report.Error : ""));
            foreach (var tool in tools)
                Log("  " + (tool.State == MiseToolState.Missing ? "! " : tool.State == MiseToolState.UpdateAvailable ? "^ " : "  ") + tool.Name + " = \"" + tool.Requested + "\": " + tool.Hint());
        }

        /// <summary>Writes one line to the Mise Output pane from any thread (creating the pane on first use).</summary>
        public void Log(string message)
        {
            var target = pane;
            if (target == null)
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await EnsureInitializedAsync();
                    Write(pane, message);
                }).FileAndForget("Vistudio/Mise/Log");
                return;
            }
            Write(target, message);
        }

        private static void Write(IVsOutputWindowPane? target, string message)
        {
#pragma warning disable VSTHRD010 // This SDK method explicitly supports background threads.
            target?.OutputStringThreadSafe((message.Length > 16384 ? message.Substring(0, 16384) + " [truncated]" : message) + Environment.NewLine);
#pragma warning restore VSTHRD010
        }

        public async Task ShowOutputAsync()
        {
            await EnsureInitializedAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            pane?.Activate();
            (services.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ExecuteCommand("View.Output");
        }

        /// <summary>Opens a config file in the editor.</summary>
        public async Task OpenAsync(string path)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path);
        }
    }
}
