using System;
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
using Vistudio.Core.Projects;

[assembly: ProvideCodeBase]

namespace Vistudio
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideBindingPath]
    [ProvideProjectFactory(typeof(KotlinProjectFactory), "Kotlin", "Kotlin Project Files (*.kotlinproj);*.kotlinproj", "kotlinproj", "kotlinproj", null, LanguageVsTemplate = "Kotlin")]
    [ProvideOptionPage(typeof(KotlinOptionsPage), "Kotlin", "General", 0, 0, true)]
    [ProvideToolWindow(typeof(Preview.ComposePreviewWindow), Style = VsDockStyle.Tabbed, Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057", Orientation = ToolWindowOrientation.Right)]
    [Guid("6cc1f5c0-99c8-43c6-b605-8dac96970119")]
    public sealed class KotlinPackage : AsyncPackage
    {
        internal static readonly Guid Commands = new Guid("75625a1a-f6e1-42aa-bf3f-90432c756124");
        /// <summary>Context menu of Kotlin target nodes (Commands.vsct TargetMenu).</summary>
        internal const int TargetMenu = 0x2000;
        private KotlinEnvironment environment = null!;
        private KotlinLanguageClient language = null!;
        private CancellationTokenSource? buildCancellation;
        private JoinableTask<bool>? buildTask;
        private BuildErrorList? buildErrors;

        internal KotlinEnvironment Environment => environment;

        internal object? GetServiceOnMainThread(Type service)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return GetService(service);
        }

        /// <summary>Solution Build/Rebuild/Clean for one loaded .kotlinproj, mirrored into the solution Build pane.</summary>
        internal Task<bool> RunProjectBuildAsync(string projectFile, string operation, Action<string> mirror, CancellationToken token) =>
            BeginBuildAsync(operation, token, new ProjectBuildRequest(projectFile, mirror, solutionBuild: true));

        /// <summary>An operation for one loaded .kotlinproj: a solution build (already saved, Build pane) or a project context-menu command.</summary>
        private sealed class ProjectBuildRequest
        {
            public ProjectBuildRequest(string? projectFile, Action<string>? mirror, bool solutionBuild)
            { ProjectFile = projectFile; Mirror = mirror; SolutionBuild = solutionBuild; }
            /// <summary>Null for tasks of a build script (CodeLens): the active workspace supplies the settings.</summary>
            public string? ProjectFile { get; }
            public string? Directory => ProjectFile == null ? null : System.IO.Path.GetDirectoryName(ProjectFile)!;
            /// <summary>Gradle build to run in instead of the one resolved from the settings (the script's own build).</summary>
            public string? GradleRoot { get; set; }
            public Action<string>? Mirror { get; }
            public bool SolutionBuild { get; }
            /// <summary>Explicit Gradle tasks (a Kotlin target's build/test/run tasks) instead of the project settings.</summary>
            public System.Collections.Generic.IReadOnlyList<string>? Tasks { get; set; }
        }

        /// <summary>Runs tasks declared in a Kotlin build script (CodeLens "Run task") in that script's Gradle build through the shared gate.</summary>
        internal Task<bool> RunScriptTasksAsync(string gradleRoot, System.Collections.Generic.IReadOnlyList<string> tasks) =>
            BeginBuildAsync("tasks", default, new ProjectBuildRequest(null, null, solutionBuild: false) { Tasks = tasks, GradleRoot = gradleRoot });

        /// <summary>Runs a Kotlin target's imported build, test or run tasks for one loaded .kotlinproj through the shared Gradle gate.</summary>
        internal Task<bool> RunTargetAsync(string projectFile, KotlinTarget target, string operation)
        {
            var tasks = target.Tasks(operation);
            if (tasks.Count == 0) throw new InvalidOperationException("Kotlin target " + target.Id + " has no Gradle " + operation + " task. Import the Gradle model again or use Gradle Tasks...");
            return BeginBuildAsync(operation, default, new ProjectBuildRequest(projectFile, null, solutionBuild: false) { Tasks = tasks });
        }

        /// <summary>The selected Targets/&lt;target&gt; node of a Kotlin project, if exactly one is selected.</summary>
        private (KotlinProjectNode Project, KotlinTarget Target)? SelectedTarget()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(GetService(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection selection)) return null;
            if (ErrorHandler.Failed(selection.GetCurrentSelection(out var hierarchy, out var itemid, out _, out var container))) return null;
            try
            {
                if (hierarchy == IntPtr.Zero || itemid == VSConstants.VSITEMID_SELECTION) return null;
                var project = System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(hierarchy) as KotlinProjectNode;
                var target = project?.TargetAt(itemid);
                return project != null && target != null ? (project, target) : ((KotlinProjectNode, KotlinTarget)?)null;
            }
            finally
            {
                if (hierarchy != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(hierarchy);
                if (container != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(container);
            }
        }

        private void AddTargetCommand(OleMenuCommandService service, int id, string operation, string verb)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = new OleMenuCommand((_, __) => JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    var selected = SelectedTarget() ?? throw new InvalidOperationException("Select a Kotlin target under Targets in Solution Explorer.");
                    await RunTargetAsync(selected.Project.FilePath, selected.Target, operation);
                }
                catch (Exception e)
                {
                    environment.Log("Kotlin: " + e.Message);
                    await environment.ShowOutputAsync();
                }
            }).Task.Forget(), new CommandID(Commands, id));
            command.BeforeQueryStatus += (_, __) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var target = SelectedTarget()?.Target;
                command.Visible = target != null && target.Tasks(operation).Count != 0;
                command.Enabled = command.Visible && buildCancellation == null;
                if (target != null) command.Text = verb + " " + target.Name + " (" + string.Join(" ", target.Tasks(operation)) + ")";
            };
            service.AddCommand(command);
        }

        /// <summary>The Kotlin project selected in Solution Explorer (project node or one of its items), if any.</summary>
        private KotlinProjectNode? SelectedProject()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(GetService(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection selection)) return null;
            if (ErrorHandler.Failed(selection.GetCurrentSelection(out var hierarchy, out _, out var multi, out var container))) return null;
            try
            {
                if (hierarchy == IntPtr.Zero) return null;
                return System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(hierarchy) as KotlinProjectNode;
            }
            finally
            {
                if (hierarchy != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(hierarchy);
                if (container != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(container);
            }
        }

        private async Task<bool> RunForSelectedAsync(string operation)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var project = SelectedProject() ?? throw new InvalidOperationException("Select a Kotlin project in Solution Explorer.");
            return await BeginBuildAsync(operation, default, new ProjectBuildRequest(project.FilePath, null, solutionBuild: false));
        }

        /// <summary>
        /// Open Folder: creates vistudio.kotlinproj when missing, opens it as a Solution Explorer project and imports the Gradle model
        /// when it was never imported. With a Kotlin project already loaded, it only reveals that project.
        /// </summary>
        private async Task CreateOrOpenProjectAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var loaded = environment.LoadedProjectFile;
            if (loaded != null)
            {
                environment.Log("Kotlin project already open: " + loaded);
                (await GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ExecuteCommand("View.SolutionExplorer");
                return;
            }
            var folder = await environment.GetFolderAsync()
                ?? throw new InvalidOperationException("Open the Gradle project folder first (File > Open > Folder), or open an existing .kotlinproj with File > Open > Project/Solution.");
            var created = false;
            var path = await Task.Run(() =>
            {
                var existing = KotlinProjectFile.Find(folder);
                if (existing != null) return existing;
                var metadata = KotlinProjectFile.Create(folder, KotlinSettings.Load());
                metadata.Save();
                created = true;
                return metadata.FilePath;
            });
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            environment.Log((created ? "Created " : "Opening ") + path);
            var solution = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution ?? throw new InvalidOperationException("Visual Studio solution service is unavailable.");
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            // A temporary solution (closing the folder view) that holds the project; IVsSolution.OpenSolutionFile rejects
            // project files in Visual Studio 18, and File.OpenProject ignores a path argument and shows its dialog.
            // The open folder is the current solution; close it first (the shell prompts for unsaved documents).
            if (ErrorHandler.Succeeded(solution.GetProperty((int)__VSPROPID.VSPROPID_IsSolutionOpen, out var isOpen)) && isOpen is bool open && open)
            {
                if (solution.CloseSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_PromptSave, null, 0) != VSConstants.S_OK)
                    throw new OperationCanceledException("Closing the open folder was canceled.");
            }
            ErrorHandler.ThrowOnFailure(solution.CreateSolution(System.IO.Path.GetDirectoryName(path), System.IO.Path.GetFileNameWithoutExtension(path),
                (uint)__VSCREATESOLUTIONFLAGS.CSF_TEMPORARY));
            var anyType = Guid.Empty;
            var hierarchyId = typeof(IVsHierarchy).GUID;
            ErrorHandler.ThrowOnFailure(solution.CreateProject(ref anyType, path, null, null, (uint)__VSCREATEPROJFLAGS.CPF_OPENFILE, ref hierarchyId, out var projectHierarchy));
            if (projectHierarchy != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(projectHierarchy);
            await ImportWhenLoadedAsync(path);
        }

        /// <summary>A project created from a New Project template: imports its Gradle model once the project has loaded.</summary>
        internal void ImportWhenLoaded(string path) => JoinableTaskFactory.RunAsync(async () =>
        {
            try { await ImportWhenLoadedAsync(path); }
            catch (Exception e)
            {
                environment.Log("Kotlin: " + e.Message);
                await environment.ShowOutputAsync();
            }
        }).Task.Forget();

        /// <summary>Waits until the .kotlinproj is loaded in the solution, then imports the Gradle model if it was never imported.</summary>
        private async Task ImportWhenLoadedAsync(string path)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var directory = System.IO.Path.GetDirectoryName(path)!;
            for (var waited = 0; environment.ProjectFileFor(directory) == null; waited += 250)
            {
                if (waited >= 60000) throw new InvalidOperationException("The Kotlin project did not load: " + path);
                await Task.Delay(250);
                await JoinableTaskFactory.SwitchToMainThreadAsync();
            }
            var imported = await Task.Run(() => KotlinProjectFile.Load(path).ImportedAtUtc.Length != 0);
            if (!imported) await BeginBuildAsync("import", default, new ProjectBuildRequest(path, null, solutionBuild: false));
        }

        /// <summary>Opens the .kotlinproj XML in the XML editor (text editor fallback); saving it refreshes the project tree.</summary>
        private void EditProjectFile()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var project = SelectedProject() ?? throw new InvalidOperationException("Select a Kotlin project in Solution Explorer.");
            try { VsShellUtilities.OpenDocumentWithSpecificEditor(this, project.FilePath, new Guid("fa3cd31e-987b-443a-9b81-186104e8dac1"), VSConstants.LOGVIEWID_Primary); }
            catch (System.Runtime.InteropServices.COMException)
            {
                VsShellUtilities.OpenDocumentWithSpecificEditor(this, project.FilePath, VSConstants.VsEditorFactoryGuid.TextEditor_guid, VSConstants.LOGVIEWID_Primary);
            }
        }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var components = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel
                ?? throw new InvalidOperationException("Visual Studio component model is unavailable.");
            environment = components.GetService<KotlinEnvironment>();
            language = components.GetService<KotlinLanguageClient>();
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            RegisterProjectFactory(new KotlinProjectFactory(this));
            await environment.GetFolderAsync();
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            buildErrors = new BuildErrorList(this, environment.Log);
            environment.WorkspaceChanged += CancelForWorkspaceChangeAsync;
            var commands = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService
                ?? throw new InvalidOperationException("Visual Studio command service is unavailable.");
            Add(commands, 0x100, () => BeginBuildAsync("build"), true);
            Add(commands, 0x101, () => BeginBuildAsync("clean"), true);
            Add(commands, 0x102, () => BeginBuildAsync("test"), true);
            Add(commands, 0x103, () => { buildCancellation?.Cancel(); return Task.CompletedTask; }, false, true);
            Add(commands, 0x104, () => language.RestartAsync());
            Add(commands, 0x105, () => { ShowOptionPage(typeof(KotlinOptionsPage)); return Task.CompletedTask; });
            Add(commands, 0x106, () => BeginBuildAsync("run"), true);
            Add(commands, 0x107, () => BeginBuildAsync("discover"), true);
            Add(commands, 0x109, () => BeginBuildAsync("import"), true);
            Add(commands, 0x10A, CreateOrOpenProjectAsync, true);
            Add(commands, 0x108, async () =>
            {
                var window = await ShowToolWindowAsync(typeof(Preview.ComposePreviewWindow), 0, true, DisposalToken);
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!(window?.Frame is IVsWindowFrame frame)) throw new InvalidOperationException("Cannot open the Compose preview window.");
                ErrorHandler.ThrowOnFailure(frame.Show());
            });
            // Solution Explorer project context menu: visible only for Kotlin projects, acting on the selected project.
            AddProjectCommand(commands, 0x200, () => RunForSelectedAsync("import"), ProjectCommandKind.Gradle);
            AddProjectCommand(commands, 0x201, () => RunForSelectedAsync("test"), ProjectCommandKind.Gradle);
            AddProjectCommand(commands, 0x202, () => RunForSelectedAsync("run"), ProjectCommandKind.Gradle);
            AddProjectCommand(commands, 0x203, () => RunForSelectedAsync("discover"), ProjectCommandKind.Gradle);
            AddProjectCommand(commands, 0x204, () => { buildCancellation?.Cancel(); return Task.CompletedTask; }, ProjectCommandKind.Cancel);
            AddProjectCommand(commands, 0x205, () => { EditProjectFile(); return Task.CompletedTask; }, ProjectCommandKind.Always);
            // Kotlin target nodes (Targets/<target>): the target's imported Gradle tasks.
            AddTargetCommand(commands, 0x210, "build", "Build");
            AddTargetCommand(commands, 0x211, "run", "Run");
            AddTargetCommand(commands, 0x212, "test", "Test");
        }

        private enum ProjectCommandKind { Gradle, Cancel, Always }

        private void AddProjectCommand(OleMenuCommandService service, int id, Func<Task> action, ProjectCommandKind kind)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = new OleMenuCommand((_, __) => JoinableTaskFactory.RunAsync(async () =>
            {
                try { await action(); }
                catch (Exception e)
                {
                    environment.Log("Kotlin: " + e.Message);
                    await environment.ShowOutputAsync();
                }
            }).Task.Forget(), new CommandID(Commands, id));
            command.BeforeQueryStatus += (_, __) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                command.Visible = SelectedProject() != null;
                command.Enabled = command.Visible && (kind == ProjectCommandKind.Always
                    || (kind == ProjectCommandKind.Gradle ? buildCancellation == null : buildCancellation != null));
            };
            service.AddCommand(command);
        }

        private void Add(OleMenuCommandService service, int id, Func<Task> action, bool build = false, bool cancel = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = new OleMenuCommand((_, __) => JoinableTaskFactory.RunAsync(async () =>
            {
                try { await action(); }
                catch (Exception e)
                {
                    environment.Log("Kotlin: " + e.Message);
                    await environment.ShowOutputAsync();
                }
            }).Task.Forget(), new CommandID(Commands, id));
            command.BeforeQueryStatus += (_, __) => command.Enabled = build ? buildCancellation == null : !cancel || buildCancellation != null;
            service.AddCommand(command);
        }

        internal Task<bool> BuildForDebugAsync(CancellationToken token) => BeginBuildAsync("build", token);

        /// <summary>True while a Gradle operation holds the shared gate (UI thread).</summary>
        internal bool GradleBusy { get { ThreadHelper.ThrowIfNotOnUIThread(); return buildCancellation != null; } }

        /// <summary>
        /// Runs other Gradle work (Compose preview) through the same gate as builds: one Gradle process at a time,
        /// canceled by Cancel Gradle and by workspace changes.
        /// </summary>
        internal async Task<T> RunExclusiveGradleAsync<T>(Func<CancellationToken, Task<T>> work)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (buildCancellation != null) throw new InvalidOperationException("A Gradle build is already running. Wait for it or cancel it first.");
            var cancellation = buildCancellation = new CancellationTokenSource();
            var result = default(T)!;
            buildTask = JoinableTaskFactory.RunAsync(async () =>
            {
                try { result = await work(cancellation.Token); return true; }
                finally
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    cancellation.Dispose();
                    if (buildCancellation == cancellation) buildCancellation = null;
                }
            });
            await buildTask.JoinAsync();
            return result;
        }

        private async Task<bool> BeginBuildAsync(string operation, CancellationToken token = default, ProjectBuildRequest? request = null)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (buildCancellation != null) throw new InvalidOperationException("A Gradle build is already running. Wait for it or cancel it before launching.");
            buildCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            buildTask = JoinableTaskFactory.RunAsync(() => PrepareAndRunBuildAsync(operation, buildCancellation.Token, request));
            return await buildTask.JoinAsync();
        }

        private async Task<bool> PrepareAndRunBuildAsync(string operation, CancellationToken token, ProjectBuildRequest? request)
        {
            void Log(string line)
            {
                environment.Log(line);
                request?.Mirror?.Invoke(line);
            }
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(token);
                var root = request?.Directory ?? await environment.GetFolderAsync() ?? request?.GradleRoot;
                if (root == null) throw new InvalidOperationException("Open a Kotlin folder (File > Open > Folder) or a .kotlinproj project.");
                var projectFile = request?.ProjectFile ?? environment.ProjectFileFor(root);
                var settings = await Task.Run(() => projectFile != null ? KotlinSettings.LoadForProjectFile(projectFile) : KotlinSettings.LoadForFolder(root), token);
                await JoinableTaskFactory.SwitchToMainThreadAsync(token);
                var tasks = GradleCommand.ParseTasks(operation == "build" ? settings.BuildTasks : operation == "test" ? settings.TestTasks : operation == "run" ? settings.RunTasks : "clean");
                if (operation == "rebuild") tasks = new[] { "clean" }.Concat(GradleCommand.ParseTasks(settings.BuildTasks)).ToArray();
                if (request?.Tasks != null) tasks = GradleCommand.ParseTasks(string.Join(" ", request.Tasks));
                var gradleRoot = request?.GradleRoot ?? KotlinEnvironment.ResolveGradleRoot(root, settings.GradleRoot);
                var dte = await GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE
                    ?? throw new InvalidOperationException("Visual Studio automation service is unavailable.");
                // The solution build manager has already saved documents; Kotlin commands save here to keep the IDE prompts.
                if (request == null || !request.SolutionBuild)
                {
                    // Save modified documents only: File.SaveAll would also prompt "Save As" for a temporary solution that
                    // wraps a .kotlinproj, and Gradle does not need solution files. Unnamed documents still get their prompt.
                    if (dte.Documents != null)
                        foreach (EnvDTE.Document document in dte.Documents)
                        {
                            if (!document.Saved) document.Save();
                            if (!document.Saved) throw new InvalidOperationException("Build canceled: save modified documents first.");
                        }
                    await environment.ShowOutputAsync();
                }
                if (operation == "import")
                {
                    var metadata = await Task.Run(() =>
                    {
                        var path = projectFile ?? KotlinProjectFile.Find(root);
                        return path != null ? KotlinProjectFile.Load(path) : KotlinProjectFile.Create(root, settings);
                    }, token);
                    var script = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(KotlinPackage).Assembly.Location)!, "Build", "ImportModel.gradle");
                    environment.Log("Importing resolved Gradle model from " + metadata.GradleRoot + "...");
                    var model = await GradleProjectImporter.ResolveAsync(metadata, settings, script, environment.Log, token);
                    await JoinableTaskFactory.SwitchToMainThreadAsync(token);
                    if (request != null ? environment.ProjectFileFor(root) == null
                        : !string.Equals(root, await environment.GetFolderAsync(), StringComparison.OrdinalIgnoreCase))
                        throw new OperationCanceledException("The active workspace changed during import.");
                    await Task.Run(() => { metadata.ApplyImport(model); metadata.RefreshTools(); metadata.Save(token); }, token);
                    environment.Log("Gradle model imported successfully into " + metadata.FilePath);
                    return true;
                }
                if (operation == "discover")
                {
                    environment.Log("Discovering Gradle targets and tasks...");
                    var catalog = new GradleTaskCatalog();
                    var script = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(KotlinPackage).Assembly.Location)!, "Build", "DiscoverTasks.gradle");
                    var discoveryExit = await OwnedProcess.RunAsync(GradleCommand.CreateDiscovery(gradleRoot, settings.GradleJavaHome, script), line =>
                    {
                        catalog.Accept(line);
                        if (!line.StartsWith("VISTUDIO_TASK\t", StringComparison.Ordinal)) environment.Log(line);
                    }, token);
                    var available = catalog.Complete(discoveryExit);
                    await JoinableTaskFactory.SwitchToMainThreadAsync(token);
                    if (available.Length == 0) throw new InvalidOperationException("Gradle reported no selectable tasks.");
                    using (var dialog = new GradleTaskDialog(available))
                    {
                        var owner = new System.Windows.Forms.NativeWindow();
                        owner.AssignHandle(dte.MainWindow.HWnd);
                        try
                        {
                            if (dialog.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK) return false;
                            tasks = dialog.SelectedTasks;
                        }
                        finally { owner.ReleaseHandle(); }
                    }
                }
                var start = GradleCommand.Create(gradleRoot, settings.GradleJavaHome, tasks);
                Log("Gradle " + string.Join(" ", tasks) + " in " + start.WorkingDirectory);
                token.ThrowIfCancellationRequested();
                await JoinableTaskFactory.SwitchToMainThreadAsync(token);
                buildErrors!.Clear();
                var diagnostics = new BuildDiagnostics(start.WorkingDirectory);
                var buildStartedUtc = DateTime.UtcNow;
                var code = await OwnedProcess.RunAsync(start, line =>
                {
                    Log(line);
                    diagnostics.Accept(line);
                }, token).ConfigureAwait(false);
                Log(code == 0 ? "Gradle tasks succeeded." : "Gradle tasks FAILED (exit " + code + "). See output above.");
                await JoinableTaskFactory.SwitchToMainThreadAsync(token);
                token.ThrowIfCancellationRequested();
                buildErrors.Publish(diagnostics.Complete(code), buildStartedUtc);
                return code == 0;
            }
            catch (OperationCanceledException) { Log("Gradle " + operation + " canceled. Any previous imported model is unchanged."); return false; }
            finally
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                buildCancellation?.Dispose();
                buildCancellation = null;
            }
        }

        private async Task CancelForWorkspaceChangeAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            buildCancellation?.Cancel();
            if (buildTask != null) await buildTask.JoinAsync();
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            buildErrors?.Clear();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && environment != null)
            {
                environment.WorkspaceChanged -= CancelForWorkspaceChangeAsync;
                JoinableTaskFactory.Run(async () =>
                {
                    await CancelForWorkspaceChangeAsync();
                    await language.StopProcessAsync();
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    buildErrors?.Dispose();
                    buildErrors = null;
                });
            }
            base.Dispose(disposing);
        }
    }
}
