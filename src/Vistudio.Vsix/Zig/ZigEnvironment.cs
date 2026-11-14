using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Workspace.VSIntegration.Contracts;
using Vistudio.Core.Zig;

namespace Vistudio.Zig
{
    /// <summary>
    /// Shared Zig services for MEF parts and <see cref="ZigPackage"/>: the "Zig" Output pane, the Open Folder root,
    /// settings change notifications and the toolchain environment (<c>zig env</c>). Independent of the Kotlin parts.
    /// </summary>
    [Export, PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class ZigEnvironment : ILanguageEnvironment
    {
        private static readonly Guid PaneId = new Guid("5f1c4c63-2d6f-4f43-9a0c-3b2d6a9f7e21");
        private readonly IServiceProvider services;
        private readonly SemaphoreSlim environmentGate = new SemaphoreSlim(1, 1);
        private IVsOutputWindowPane? pane;
        private IVsFolderWorkspaceService? folders;
        private bool initialized;
        private string? environmentFor;
        private ZigEnvironmentInfo? environment;

        /// <summary>Raised after the folder workspace changes.</summary>
        public event Func<Task>? WorkspaceChanged;
        /// <summary>Raised after Tools &gt; Options &gt; Zig is saved.</summary>
        public static event EventHandler? SettingsChanged;

        internal static void OnSettingsChanged() => SettingsChanged?.Invoke(null, EventArgs.Empty);

        private readonly WorkspaceSettingsMonitor workspaceSettings;

        [ImportingConstructor]
        public ZigEnvironment([Import(typeof(SVsServiceProvider))] IServiceProvider services, WorkspaceSettingsMonitor workspaceSettings)
        {
            this.services = services;
            this.workspaceSettings = workspaceSettings;
            // Point settings at this window's workspace early; GetFolderAsync awaits the same refresh.
#pragma warning disable VSSDK007 // Detached on purpose: GetFolderAsync awaits the same refresh before settings are used.
            ThreadHelper.JoinableTaskFactory.RunAsync(workspaceSettings.RefreshAsync).FileAndForget("Vistudio/WorkspaceSettings");
#pragma warning restore VSSDK007
        }

        /// <summary>The Open Folder root, or null without a folder workspace.</summary>
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
            var output = services.GetService(typeof(SVsOutputWindow)) as IVsOutputWindow
                ?? throw new InvalidOperationException("Visual Studio Output service is unavailable.");
            var id = PaneId;
            ErrorHandler.ThrowOnFailure(output.CreatePane(ref id, "Zig", 1, 0));
            ErrorHandler.ThrowOnFailure(output.GetPane(ref id, out pane));
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
            // The new workspace's .vs\settings.json must be in effect before ZLS restarts.
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

        /// <summary>Writes one line to the Zig Output pane from any thread (creating the pane on first use).</summary>
        public void Log(string message)
        {
            var target = pane;
            if (target == null)
            {
                // First message before the pane exists: create it without blocking the caller.
#pragma warning disable VSSDK007 // Fire-and-forget by design; failures only lose this log line.
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    try { await EnsureInitializedAsync(); Write(pane, message); }
                    catch (Exception) { }
                });
#pragma warning restore VSSDK007
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

        public async Task ClearOutputAsync()
        {
            await EnsureInitializedAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            pane?.Clear();
        }

        public async Task ShowOutputAsync()
        {
            await EnsureInitializedAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            pane?.Activate();
            (services.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ExecuteCommand("View.Output");
        }

        /// <summary><c>zig env</c> of the configured toolchain, cached per zig.exe path; logs the version once.</summary>
        public async Task<ZigEnvironmentInfo?> GetToolchainAsync(CancellationToken token)
        {
            var zig = ZigToolchain.FindExecutable(ZigSettings.Load());
            if (zig == null) return null;
            await environmentGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (environment != null && string.Equals(environmentFor, zig, StringComparison.OrdinalIgnoreCase)) return environment;
                environment = await ZigToolchain.ReadEnvironmentAsync(zig, token).ConfigureAwait(false);
                environmentFor = zig;
                Log("Zig " + environment.Version + " (" + zig + "), std: " + environment.StdDirectory);
                return environment;
            }
            finally { environmentGate.Release(); }
        }
    }
}
