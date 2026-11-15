using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Vistudio.Core;
using Vistudio.Core.Languages;
using Vistudio.Core.Packages;
using Vistudio.Packages;
using Task = System.Threading.Tasks.Task;

namespace Vistudio.Languages
{
    /// <summary>
    /// Language projects (Dart, Zig, Java, C, C++, WebAssembly, Rust) and the package commands of every project type's
    /// Packages nodes. Separate from <c>KotlinPackage</c>, which keeps the .kotlinproj project type and Gradle import.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideMenuResource("LanguageMenus.ctmenu", 1)]
    // Saving either page raises its language client's settings event, which restarts that server.
    // The shell creates property pages by CLSID: Project > Properties of Kotlin and language projects.
    [ProvideObject(typeof(ProjectPropertyPage), RegisterUsing = RegistrationMethod.CodeBase)]
    [ProvideOptionPage(typeof(Rust.RustOptionsPage), "Rust", "General", 0, 0, true)]
    [ProvideOptionPage(typeof(Wasm.WasmOptionsPage), "WebAssembly", "General", 0, 0, true)]
    [ProvideOptionPage(typeof(Bazel.BazelOptionsPage), "Bazel", "General", 0, 0, true)]
    [ProvideProjectFactory(typeof(LanguageProjectFactory), "Vistudio Languages",
        "Vistudio Language Projects (*.rustproj;*.zigproj;*.dartproj;*.cproj;*.cppproj;*.wasmproj;*.javaproj);*.rustproj;*.zigproj;*.dartproj;*.cproj;*.cppproj;*.wasmproj;*.javaproj",
        "rustproj", "rustproj;zigproj;dartproj;cproj;cppproj;wasmproj;javaproj", null, LanguageVsTemplate = "VistudioLanguages")]
    [Guid(PackageGuid)]
    public sealed class LanguagesPackage : AsyncPackage
    {
        internal const string PackageGuid = "486c739c-7327-4a2b-9c0c-383595f5649c";
        private static readonly Guid PaneId = new Guid("17fed4e4-3975-4e2f-83d0-369eac95576b");
        private const int AddPackage = 0x0100, CheckForUpdates = 0x0101, UpdateAll = 0x0102, UpdateAllLatest = 0x0103, OpenManifest = 0x0104,
            UpdatePackage = 0x0110, UpdatePackageLatest = 0x0111, RemovePackage = 0x0112, GoToDeclaration = 0x0113,
            RunProject = 0x0120, TestProject = 0x0121, CancelProject = 0x0122;

        private OutputPane output = null!;
        private CancellationTokenSource? running;

        internal PackageService Packages { get; private set; } = null!;
        internal LanguageProjects Projects { get; } = new LanguageProjects();

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            var components = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel
                ?? throw new InvalidOperationException("Visual Studio component model is unavailable.");
            Packages = components.GetService<PackageService>();
            output = new OutputPane(this, PaneId, "Vistudio Projects");
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            RegisterProjectFactory(new LanguageProjectFactory(this));
            if (!(await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands))
            {
                Log("The command service is unavailable; package and project commands are off.");
                return;
            }

            AddPackageCommand(commands, AddPackage, (manifest, _) =>
            {
                var request = AddPackageDialog.Ask(manifest.Manager, manifest.ManifestPath);
                if (request != null) Run(manifest, tool => manifest.Manager.Add(tool, manifest.ManifestPath, request));
            }, needsPackage: false);
            AddPackageCommand(commands, CheckForUpdates, (manifest, _) => Packages.CheckForUpdates(manifest, force: true), needsPackage: false);
            AddPackageCommand(commands, UpdateAll, (manifest, _) => Run(manifest, tool => manifest.Manager.Update(tool, manifest.ManifestPath, null, false)),
                needsPackage: false, text: manifest => "Update All (within constraints)");
            AddPackageCommand(commands, UpdateAllLatest, (manifest, _) => JoinableTaskFactory.RunAsync(() => Packages.UpdateAllToLatestAsync(manifest)).FileAndForget("Vistudio/Packages/UpdateAll"),
                needsPackage: false, text: manifest =>
                {
                    var outdated = Packages.Get(manifest).Packages.Count(p => p.IsOutdated);
                    return outdated == 0 ? null : "Update All to Latest (" + outdated + ")";
                });
            AddPackageCommand(commands, OpenManifest, (manifest, _) => VsShellUtilities.OpenDocument(this, manifest.ManifestPath),
                needsPackage: false, text: manifest => "Open " + System.IO.Path.GetFileName(manifest.ManifestPath));
            AddPackageCommand(commands, UpdatePackage, (manifest, package) => Run(manifest, tool => manifest.Manager.Update(tool, manifest.ManifestPath, package, false)),
                needsPackage: true, packageText: p => p.HasCompatibleUpdate ? "Update to " + p.Compatible : "Update (within " + (p.Requested.Length == 0 ? "constraints" : p.Requested) + ")");
            AddPackageCommand(commands, UpdatePackageLatest, (manifest, package) => Run(manifest, tool => manifest.Manager.Update(tool, manifest.ManifestPath, package, true)),
                needsPackage: true, packageText: p => p.Latest != null && p.IsOutdated ? "Update to Latest (" + p.Latest + ")" : null);
            AddPackageCommand(commands, GoToDeclaration, (manifest, package) =>
            {
                VsShellUtilities.OpenDocument(this, package!.ManifestPath, VSConstants.LOGVIEWID_Primary, out _, out _, out var frame);
                if (package.Line > 0 && frame != null && VsShellUtilities.GetTextView(frame) is Microsoft.VisualStudio.TextManager.Interop.IVsTextView view)
                {
                    view.SetCaretPos(package.Line - 1, 0);
                    view.CenterLines(package.Line - 1, 1);
                }
            }, needsPackage: true);
            AddPackageCommand(commands, RemovePackage, (manifest, package) =>
            {
                if (VsShellUtilities.ShowMessageBox(this, "Remove " + package!.Name + " from " + manifest.ManifestPath + "?", "Remove Package",
                        OLEMSGICON.OLEMSGICON_QUERY, OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND) == (int)VSConstants.MessageBoxResult.IDOK)
                    Run(manifest, tool => manifest.Manager.Remove(tool, manifest.ManifestPath, package));
            }, needsPackage: true, packageText: p => "Remove " + p.Name);
            AddProjectCommand(commands, RunProject, "run");
            AddProjectCommand(commands, TestProject, "test");
            var cancel = new OleMenuCommand((_, __) => running?.Cancel(), new CommandID(PackageNodes.Commands, CancelProject));
            cancel.BeforeQueryStatus += (_, __) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                cancel.Visible = SelectedLanguageProject() != null;
                cancel.Enabled = running != null;
            };
            commands.AddCommand(cancel);
            Log("Package and project commands are ready.");
        }

        internal void Log(string message) => output.Log(message);

        internal TInterface GetService<TService, TInterface>() where TInterface : class
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return GetService(typeof(TService)) as TInterface ?? throw new InvalidOperationException(typeof(TService).Name + " is unavailable.");
        }

        internal T GetMefService<T>() where T : class
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var components = GetService(typeof(SComponentModel)) as IComponentModel ?? throw new InvalidOperationException("Visual Studio component model is unavailable.");
            return components.GetService<T>();
        }

        internal async Task SaveAllAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            (GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.Documents.SaveAll();
        }

        /// <summary>Runs a project's commands for <paramref name="operation"/> in order, stopping at the first failure.</summary>
        /// <param name="problem">Receives the reason when the commands cannot start (a missing tool, no command set): the
        /// tool's own output says nothing then, so a build shows the reason in the Error List too.</param>
        /// <param name="started">Gets the process id of the last command (the program itself) once it runs.</param>
        internal async Task<bool> RunCommandsAsync(LanguageProjectFile project, string operation, Action<string> mirror, CancellationToken token,
            Action<string>? problem = null, Action<int>? started = null)
        {
            IReadOnlyList<(System.Diagnostics.ProcessStartInfo Start, string? Note)> commands;
            try { commands = await Task.Run(() => project.CommandsWithNotes(operation), token); }
            catch (Exception e) when (e is System.IO.FileNotFoundException || e is ArgumentException)
            {
                mirror(project.Name + ": " + e.Message);
                problem?.Invoke(e.Message);
                return false;
            }
            if (commands.Count == 0)
            {
                mirror(project.Name + ": no " + operation + " command is set in the project properties.");
                if (operation != "clean") problem?.Invoke("No " + operation + " command is set in the project properties.");
                return operation == "clean";
            }
            for (var i = 0; i < commands.Count; i++)
            {
                var (command, note) = commands[i];
                if (note != null) mirror(note);
                mirror("> " + System.IO.Path.GetFileName(command.FileName) + " " + command.Arguments);
                var exit = await OwnedProcess.RunAsync(command, mirror, mirror, token, i == commands.Count - 1 ? started : null).ConfigureAwait(false);
                if (exit != 0) { mirror(project.Name + ": exited with code " + exit + "."); return false; }
            }
            return true;
        }

        private ErrorListProvider? buildProblems;
        private readonly Dictionary<string, ErrorTask> buildProblemByProject = new Dictionary<string, ErrorTask>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Shows why a project's build could not start as one Error List entry for that project, or removes the entry
        /// (<paramref name="message"/> null) when its next build starts.
        /// </summary>
        internal void ReportBuildProblem(IVsHierarchy hierarchy, LanguageProjectFile project, string? message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            buildProblems ??= new ErrorListProvider(this) { ProviderGuid = new Guid("0b5f6b84-6a3e-4f5c-9a43-7d2c1e8f5b61"), ProviderName = "Vistudio Projects" };
            if (buildProblemByProject.TryGetValue(project.FilePath, out var previous))
            {
                buildProblems.Tasks.Remove(previous);
                buildProblemByProject.Remove(project.FilePath);
            }
            if (message == null) return;
            var task = new ErrorTask
            {
                Category = TaskCategory.BuildCompile, ErrorCategory = TaskErrorCategory.Error, Text = message,
                Document = project.FilePath, HierarchyItem = hierarchy, CanDelete = false
            };
            buildProblems.Tasks.Add(task);
            buildProblemByProject[project.FilePath] = task;
        }

        /// <summary>Run / Run Tests from the project context menu, Ctrl+F5 without a debug program: output in the "Vistudio Projects" pane.</summary>
        internal void RunProjectCommand(LanguageProjectFile project, string operation, Action<int>? started = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (running != null) { Log("Another run is in progress; cancel it first."); return; }
            var cancellation = running = new CancellationTokenSource();
            JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await SaveAllAsync();
                    await output.ShowAsync();
                    Log("== " + project.Name + ": " + operation);
                    var ok = await RunCommandsAsync(project, operation, Log, cancellation.Token, started: started);
                    Log(ok ? "Done." : "Failed.");
                }
                catch (OperationCanceledException) { Log("Canceled."); }
                finally
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    running = null;
                    cancellation.Dispose();
                }
            }).FileAndForget("Vistudio/Languages/Run");
        }

        private void Run(PackageManifestRef manifest, Func<string, PackageAction> build) =>
            JoinableTaskFactory.RunAsync(() => Packages.RunAsync(manifest, build)).FileAndForget("Vistudio/Packages/Action");

        private void AddPackageCommand(OleMenuCommandService service, int id, Action<PackageManifestRef, PackageEntry?> execute, bool needsPackage,
            Func<PackageManifestRef, string?>? text = null, Func<PackageEntry, string?>? packageText = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = new OleMenuCommand((_, __) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var selected = SelectedPackage();
                if (selected == null || needsPackage && selected.Value.Package == null) return;
                try { execute(selected.Value.Manifest, selected.Value.Package); }
                catch (Exception e) when (e is PackageManagerException || e is ArgumentException || e is System.IO.IOException || e is COMException)
                {
                    Packages.Log(e.Message);
                    VsShellUtilities.ShowMessageBox(this, e.Message, "Packages", OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                }
            }, new CommandID(PackageNodes.Commands, id));
            command.BeforeQueryStatus += (_, __) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                // Visual Studio swallows exceptions from here and shows the command disabled, so they are logged.
                try
                {
                    var selected = SelectedPackage();
                    var visible = selected != null && (!needsPackage || selected.Value.Package != null);
                    string? caption = null;
                    if (visible && packageText != null && selected!.Value.Package != null) visible = (caption = packageText(selected.Value.Package)) != null;
                    else if (visible && text != null) visible = (caption = text(selected!.Value.Manifest)) != null;
                    command.Visible = visible;
                    command.Enabled = visible;
                    if (caption != null) command.Text = caption;
                }
                catch (Exception e)
                {
                    Note("package command status failed: " + e.GetType().Name + ": " + e.Message);
                    command.Visible = command.Enabled = false;
                }
            };
            service.AddCommand(command);
        }

        private void AddProjectCommand(OleMenuCommandService service, int id, string operation)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = new OleMenuCommand((_, __) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (SelectedLanguageProject() is LanguageProjectNode project) RunProjectCommand(project.Metadata, operation);
            }, new CommandID(PackageNodes.Commands, id));
            command.BeforeQueryStatus += (_, __) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var project = SelectedLanguageProject();
                var configured = project != null && project.Metadata.Get(operation == "run" ? "RunCommand" : "TestCommand").Length != 0;
                command.Visible = project != null;
                command.Enabled = configured && running == null;
                if (project != null) command.Text = (operation == "run" ? "Run " : "Run Tests in ") + project.Name;
            };
            service.AddCommand(command);
        }

        private string? lastUnknownSelection;

        /// <summary>
        /// Says once per distinct reason why the selection gives the package or project commands nothing to act on;
        /// Visual Studio only shows such commands disabled.
        /// </summary>
        private void Note(string reason)
        {
            if (reason == lastUnknownSelection) return;
            lastUnknownSelection = reason;
            Log("Commands: " + reason);
        }

        private (TreeProjectNode Node, uint ItemId)? Selection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(GetService(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection selection)) { Note("selection service unavailable"); return null; }
            var hr = selection.GetCurrentSelection(out var hierarchy, out var itemid, out _, out var container);
            if (ErrorHandler.Failed(hr)) { Note("GetCurrentSelection failed: 0x" + hr.ToString("x8")); return null; }
            try
            {
                var selected = hierarchy == IntPtr.Zero ? null : Marshal.GetObjectForIUnknown(hierarchy);
                var node = itemid == VSConstants.VSITEMID_SELECTION ? null : TreeProjectNode.From(selected);
                // One line per distinct selection the commands cannot use: why a package command stays disabled.
                var state = node != null ? null : (selected?.GetType().FullName ?? "no hierarchy") + ", item " + itemid;
                if (state != null) Note("selection is not a Vistudio project item: " + state);
                return node == null ? ((TreeProjectNode, uint)?)null : (node, itemid);
            }
            finally
            {
                if (hierarchy != IntPtr.Zero) Marshal.Release(hierarchy);
                if (container != IntPtr.Zero) Marshal.Release(container);
            }
        }

        private (PackageManifestRef Manifest, PackageEntry? Package)? SelectedPackage()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var selection = Selection();
            if (selection == null) return null;
            var package = selection.Value.Node.PackageAt(selection.Value.ItemId);
            if (package == null) Note("no package node at item " + selection.Value.ItemId + " of " + selection.Value.Node.Name);
            return package;
        }

        private LanguageProjectNode? SelectedLanguageProject()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Selection()?.Node as LanguageProjectNode;
        }
    }

    /// <summary>The language projects open in this window.</summary>
    internal sealed class LanguageProjects
    {
        private readonly List<LanguageProjectNode> open = new List<LanguageProjectNode>();

        public void Opened(LanguageProjectNode project) { lock (open) open.Add(project); }

        public void Closed(LanguageProjectNode project) { lock (open) open.Remove(project); }

        public IReadOnlyList<LanguageProjectNode> All { get { lock (open) return open.ToList(); } }
    }
}
