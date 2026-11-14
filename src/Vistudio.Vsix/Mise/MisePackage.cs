using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Vistudio.Core.Mise;
using Task = System.Threading.Tasks.Task;

namespace Vistudio.Mise
{
    /// <summary>
    /// mise package: Tools &gt; Mise menu (also in a mise config's editor context menu), Tools &gt; Options &gt; Mise and the
    /// workspace info bar. Loads in the background when a solution or folder opens, so a workspace with a mise config is
    /// checked without opening the file. Separate from the Kotlin and Zig packages, whose menus and settings are untouched.
    /// Editor hints and CodeLens are MEF/Extensibility parts and work without this package.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideMenuResource("MiseMenus.ctmenu", 1)]
    [ProvideOptionPage(typeof(MiseOptionsPage), "Mise", "General", 0, 0, true)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.FolderOpened_string, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuid)]
    public sealed class MisePackage : AsyncPackage
    {
        internal const string PackageGuid = "c4e2a7d9-6b1f-4a3e-8d5c-2f9b0e7a1c63";
        internal static readonly Guid Commands = new Guid("f1b3c5d7-2e4a-4c6b-9d8e-0a1b2c3d4e6f");
        private const int Check = 0x0100, Install = 0x0101, Upgrade = 0x0102, UpgradeToLatest = 0x0103, Trust = 0x0104, OpenConfig = 0x0105, Settings = 0x0106,
            EditorCheck = 0x0200, EditorInstall = 0x0201, EditorUpgrade = 0x0202, EditorUpgradeToLatest = 0x0203;

        private MiseService service = null!;
        private MiseInfoBar infoBar = null!;
        private EnvDTE.DTE? dte;
        private string? root;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            var model = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel ?? throw new InvalidOperationException("Visual Studio component model is unavailable.");
            service = model.GetService<MiseService>();
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            dte = await GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            infoBar = new MiseInfoBar(this, service, () => ShowOptionPage(typeof(MiseOptionsPage)));
            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)
            {
                Add(commands, Check, async () =>
                {
                    var scope = await ScopeAsync();
                    await service.ShowOutputAsync();
                    var report = await service.GetReportAsync(scope.Folder, force: true);
                    service.LogReport(scope, report);
                });
                Add(commands, Install, async () => await service.RunAsync(MiseAction.Install, await ScopeAsync()), action: true);
                Add(commands, Upgrade, async () => await service.RunAsync(MiseAction.Upgrade, await ScopeAsync()), action: true);
                Add(commands, UpgradeToLatest, async () => await service.RunAsync(MiseAction.UpgradeToLatest, await ScopeAsync()), action: true);
                Add(commands, Trust, async () => await service.RunAsync(MiseAction.Trust, await ScopeAsync()), action: true);
                Add(commands, OpenConfig, async () =>
                {
                    var scope = await ScopeAsync();
                    foreach (var file in scope.Files) await service.OpenAsync(file);
                });
                Add(commands, Settings, () => { ShowOptionPage(typeof(MiseOptionsPage)); return Task.CompletedTask; });
                // The editor context menu has its own ids: shown only while a mise config is the active document.
                Add(commands, EditorCheck, async () =>
                {
                    var scope = await ScopeAsync();
                    await service.ShowOutputAsync();
                    service.LogReport(scope, await service.GetReportAsync(scope.Folder, force: true));
                }, editorOnly: true);
                Add(commands, EditorInstall, async () => await service.RunAsync(MiseAction.Install, await ScopeAsync()), action: true, editorOnly: true);
                Add(commands, EditorUpgrade, async () => await service.RunAsync(MiseAction.Upgrade, await ScopeAsync()), action: true, editorOnly: true);
                Add(commands, EditorUpgradeToLatest, async () => await service.RunAsync(MiseAction.UpgradeToLatest, await ScopeAsync()), action: true, editorOnly: true);
            }

            service.WorkspaceChanged += () => { infoBar.ResetDismissal(); return CheckWorkspaceAsync(); };
            service.ReportChanged += (_, folder) =>
            {
                if (string.Equals(folder, root, StringComparison.OrdinalIgnoreCase)) JoinableTaskFactory.RunAsync(UpdateInfoBarAsync).FileAndForget("Vistudio/Mise/InfoBar");
            };
            MiseService.SettingsChanged += (_, __) => JoinableTaskFactory.RunAsync(CheckWorkspaceAsync).FileAndForget("Vistudio/Mise/Settings");
            // A workspace may already be open when the package loads.
            JoinableTaskFactory.RunAsync(CheckWorkspaceAsync).FileAndForget("Vistudio/Mise/Startup");
        }

        /// <summary>Checks the workspace's own mise configs; the info bar follows from <see cref="MiseService.ReportChanged"/>.</summary>
        private async Task CheckWorkspaceAsync()
        {
            var scope = await service.GetWorkspaceScopeAsync();
            root = scope?.Folder;
            if (scope == null) { await infoBar.UpdateAsync(null, null); return; }
            service.Log("mise: found " + scope.Title + " in " + scope.Folder + ".");
            if (!MiseService.Settings.InfoBar) { await infoBar.UpdateAsync(null, null); return; }
            await service.GetReportAsync(scope.Folder);
            await UpdateInfoBarAsync();
        }

        private async Task UpdateInfoBarAsync()
        {
            var folder = root;
            if (folder == null) return;
            var files = MiseConfigFiles.Find(folder);
            var scope = files.Count == 0 ? null : new MiseScope(folder, files);
            await infoBar.UpdateAsync(scope, scope == null ? null : service.Cached(folder));
        }

        /// <summary>The active mise config when one is focused, else the workspace's configs.</summary>
        private async Task<MiseScope> ScopeAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var active = ActiveDocument();
            if (active != null && MiseConfigFiles.IsConfigFile(active)) return MiseScope.ForFile(active);
            return await service.GetWorkspaceScopeAsync()
                ?? throw new InvalidOperationException("No mise config (mise.toml, .mise.toml, .tool-versions, .config/mise.toml, ...) in the open folder or solution folder.");
        }

        private void Add(OleMenuCommandService commands, int id, Func<Task> run, bool action = false, bool editorOnly = false)
        {
            var command = new OleMenuCommand((_, __) => JoinableTaskFactory.RunAsync(async () =>
            {
                try { await run(); }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    service.Log("mise: " + e.Message);
                    await service.ShowOutputAsync();
                }
            }).FileAndForget("Vistudio/Mise/Command"), new CommandID(Commands, id));
            command.BeforeQueryStatus += (_, __) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (editorOnly) command.Visible = MiseConfigFiles.IsConfigFile(ActiveDocument());
                command.Enabled = (!action || !service.IsBusy) && (!editorOnly || command.Visible);
            };
            commands.AddCommand(command);
        }

        private string? ActiveDocument()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return dte?.ActiveDocument?.FullName; }
            catch (ArgumentException) { return null; }
            catch (COMException) { return null; }
        }
    }
}
