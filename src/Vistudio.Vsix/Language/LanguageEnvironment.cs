using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Workspace.VSIntegration.Contracts;

namespace Vistudio
{
    /// <summary>What a <see cref="StdioLanguageClient"/> needs from its language: the window's folder, logging, workspace changes.</summary>
    internal interface ILanguageEnvironment
    {
        /// <summary>The Open Folder root (null without one), after this window's <c>.vs\settings.json</c> is in effect.</summary>
        Task<string?> GetFolderAsync();
        /// <summary>One line in the language's Output pane, from any thread.</summary>
        void Log(string message);
        /// <summary>Raised after the folder workspace changes, once the new workspace's settings apply.</summary>
        event Func<Task>? WorkspaceChanged;
    }

    /// <summary>
    /// Folder, workspace-settings and Output pane services for a language that needs nothing else (Rust, WebAssembly).
    /// Zig and Dart have their own environments because they also manage toolchains.
    /// </summary>
    internal sealed class LanguageEnvironment : ILanguageEnvironment
    {
        private readonly IServiceProvider services;
        private readonly WorkspaceSettingsMonitor workspaceSettings;
        private readonly OutputPane output;
        private IVsFolderWorkspaceService? folders;
        private bool initialized;

        public LanguageEnvironment(IServiceProvider services, WorkspaceSettingsMonitor workspaceSettings, Guid paneId, string paneTitle)
        {
            this.services = services;
            this.workspaceSettings = workspaceSettings;
            output = new OutputPane(services, paneId, paneTitle);
        }

        public event Func<Task>? WorkspaceChanged;

        public void Log(string message) => output.Log(message);

        public Task ShowOutputAsync() => output.ShowAsync();

        public async Task<string?> GetFolderAsync()
        {
            await EnsureInitializedAsync();
            await workspaceSettings.RefreshAsync();
            return folders?.CurrentWorkspace?.Location;
        }

        private async Task EnsureInitializedAsync()
        {
            if (initialized) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (initialized) return;
            if (services.GetService(typeof(SComponentModel)) is IComponentModel components)
            {
                folders = components.GetService<IVsFolderWorkspaceService>();
                if (folders != null) folders.OnActiveWorkspaceChanged += OnWorkspaceChangedAsync;
            }
            workspaceSettings.Attach(Log);
            initialized = true;
        }

        private async Task OnWorkspaceChangedAsync(object? sender, EventArgs args)
        {
            // The new workspace's .vs\settings.json must be in effect before the server restarts.
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
    }
}
