using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Workspace.VSIntegration.Contracts;

namespace Vistudio
{
    [Export, PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class KotlinEnvironment
    {
        private readonly IServiceProvider services;
        private IVsOutputWindowPane? pane;
        private IVsFolderWorkspaceService? folders;
        private bool initialized;
        private readonly List<string> loadedProjects = new List<string>();
        public event Func<Task>? WorkspaceChanged;

        /// <summary>The first .kotlinproj loaded in the solution; it is the IDE root when no folder workspace is open.</summary>
        public string? LoadedProjectFile
        {
            get { ThreadHelper.ThrowIfNotOnUIThread(); return loadedProjects.Count == 0 ? null : loadedProjects[0]; }
        }

        public string? ProjectFileFor(string root)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return loadedProjects.Find(p => string.Equals(Path.GetDirectoryName(p), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
        }

        public void ProjectOpened(string projectFile)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (loadedProjects.Exists(p => string.Equals(p, projectFile, StringComparison.OrdinalIgnoreCase))) return;
            loadedProjects.Add(projectFile);
            if (loadedProjects.Count == 1 && folders?.CurrentWorkspace == null) RaiseWorkspaceChanged();
        }

        public void ProjectClosed(string projectFile)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var index = loadedProjects.FindIndex(p => string.Equals(p, projectFile, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return;
            loadedProjects.RemoveAt(index);
            if (index == 0 && folders?.CurrentWorkspace == null) RaiseWorkspaceChanged();
        }

        // OnWorkspaceChangedAsync logs each handler failure itself, so the notification can run detached.
        private void RaiseWorkspaceChanged() =>
            Microsoft.VisualStudio.Threading.TplExtensions.Forget(
                ThreadHelper.JoinableTaskContext.Factory.RunAsync(() => OnWorkspaceChangedAsync(this, EventArgs.Empty)).Task);

        private readonly WorkspaceSettingsMonitor workspaceSettings;

        [ImportingConstructor]
        public KotlinEnvironment([Import(typeof(SVsServiceProvider))] IServiceProvider services, WorkspaceSettingsMonitor workspaceSettings)
        {
            this.services = services;
            this.workspaceSettings = workspaceSettings;
            // Point settings at this window's workspace early; GetFolderAsync awaits the same refresh.
#pragma warning disable VSSDK007 // Detached on purpose: GetFolderAsync awaits the same refresh before settings are used.
            ThreadHelper.JoinableTaskFactory.RunAsync(workspaceSettings.RefreshAsync).FileAndForget("Vistudio/WorkspaceSettings");
#pragma warning restore VSSDK007
        }

        public async Task<string?> GetFolderAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!initialized)
            {
                var output = services.GetService(typeof(SVsOutputWindow)) as IVsOutputWindow
                    ?? throw new InvalidOperationException("Visual Studio Output service is unavailable.");
                var id = new Guid("c10f736b-ddcb-40bb-a347-550bc215f460");
                ErrorHandler.ThrowOnFailure(output.CreatePane(ref id, "Kotlin", 1, 0));
                ErrorHandler.ThrowOnFailure(output.GetPane(ref id, out pane));
                var components = services.GetService(typeof(SComponentModel)) as IComponentModel
                    ?? throw new InvalidOperationException("Visual Studio component model is unavailable.");
                folders = components.GetService<IVsFolderWorkspaceService>();
                if (folders != null) folders.OnActiveWorkspaceChanged += OnWorkspaceChangedAsync;
                workspaceSettings.Attach(Log);
                initialized = true;
            }
            await workspaceSettings.RefreshAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return folders?.CurrentWorkspace?.Location
                ?? (loadedProjects.Count == 0 ? null : Path.GetDirectoryName(loadedProjects[0]));
        }

        private async Task OnWorkspaceChangedAsync(object? sender, EventArgs args)
        {
            // The new workspace's .vs\settings.json must be in effect before the handlers restart the servers.
            try { await workspaceSettings.RefreshAsync(); }
            catch (Exception e) { Log("Workspace settings: " + e.Message); }
            var handlers = WorkspaceChanged;
            if (handlers == null) return;
            foreach (Func<Task> callback in handlers.GetInvocationList())
            {
                try { await callback().ConfigureAwait(false); }
                catch (Exception e) { Log("Workspace change: " + e.Message); }
            }
        }

        public void Log(string message)
        {
            var target = pane;
            // OutputStringThreadSafe avoids per-line UI dispatch during builds.
#pragma warning disable VSTHRD010 // This SDK method explicitly supports background threads.
            target?.OutputStringThreadSafe((message.Length > 16384 ? message.Substring(0, 16384) + " [truncated]" : message) + Environment.NewLine);
#pragma warning restore VSTHRD010
        }

        public async Task ShowOutputAsync()
        {
            await GetFolderAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            pane?.Activate();
            var dte = services.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            dte?.ExecuteCommand("View.Output");
        }

        internal async Task StopDebuggingAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = services.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            if (dte?.Debugger.CurrentMode != EnvDTE.dbgDebugMode.dbgDesignMode)
                dte?.ExecuteCommand("Debug.StopDebugging");
        }

        public static string ResolveGradleRoot(string folder, string setting) =>
            Path.GetFullPath(string.IsNullOrWhiteSpace(setting) ? folder : Path.Combine(folder, setting));
    }
}
