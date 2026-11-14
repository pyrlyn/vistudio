using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Workspace.VSIntegration.Contracts;
using Vistudio.Core.Dart;

namespace Vistudio.Dart
{
    /// <summary>
    /// Shared Dart services for MEF parts and <see cref="DartPackage"/>: the "Dart" Output pane, the Open Folder root,
    /// settings change notifications and the resolved SDKs. Independent of the Kotlin and Zig parts.
    /// </summary>
    [Export, PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class DartEnvironment
    {
        private static readonly Guid PaneId = new Guid("8e2d4b71-3c5a-4f6e-9d1b-7a0c2e4f6b93");
        private readonly IServiceProvider services;
        private readonly WorkspaceSettingsMonitor workspaceSettings;
        private IVsOutputWindowPane? pane;
        private IVsFolderWorkspaceService? folders;
        private bool initialized;
        private string? loggedSdk;

        /// <summary>Raised after the folder workspace changes.</summary>
        public event Func<Task>? WorkspaceChanged;
        /// <summary>Raised after Tools &gt; Options &gt; Dart is saved.</summary>
        public static event EventHandler? SettingsChanged;

        internal static void OnSettingsChanged() => SettingsChanged?.Invoke(null, EventArgs.Empty);

        [ImportingConstructor]
        public DartEnvironment([Import(typeof(SVsServiceProvider))] IServiceProvider services, WorkspaceSettingsMonitor workspaceSettings)
        {
            this.services = services;
            this.workspaceSettings = workspaceSettings;
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
            ErrorHandler.ThrowOnFailure(output.CreatePane(ref id, "Dart", 1, 0));
            ErrorHandler.ThrowOnFailure(output.GetPane(ref id, out pane));
            if (services.GetService(typeof(SComponentModel)) is IComponentModel components)
            {
                folders = components.GetService<IVsFolderWorkspaceService>();
                if (folders != null) folders.OnActiveWorkspaceChanged += OnWorkspaceChangedAsync;
            }
            initialized = true;
        }

        private async Task OnWorkspaceChangedAsync(object? sender, EventArgs args)
        {
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

        /// <summary>Writes one line to the Dart Output pane from any thread (creating the pane on first use).</summary>
        public void Log(string message)
        {
            var target = pane;
            if (target == null)
            {
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

        /// <summary>The SDKs of this window's settings, or an exception telling the user how to configure them. Logs versions once per SDK.</summary>
        public async Task<DartSdk> RequireSdkAsync()
        {
            await GetFolderAsync(); // this window's .vs\settings.json first
            var sdk = DartToolchain.Require(DartSettings.Load());
            var key = sdk.Dart + "|" + sdk.FlutterRoot;
            if (!string.Equals(loggedSdk, key, StringComparison.OrdinalIgnoreCase))
            {
                loggedSdk = key;
                var flutter = sdk.FlutterRoot == null ? "" : ", Flutter " + (DartToolchain.ReadFlutterVersion(sdk.FlutterRoot) ?? "?") + " (" + sdk.FlutterRoot + ")";
                Log("Dart " + (DartToolchain.ReadDartVersion(sdk) ?? "?") + " (" + sdk.Dart + ")" + flutter);
            }
            return sdk;
        }

        /// <summary>The package of a file or the open folder; throws when there is no pubspec.yaml.</summary>
        public async Task<DartProject> RequireProjectAsync(string? file)
        {
            var folder = await GetFolderAsync();
            var project = (file == null ? null : DartProject.Find(file, folder)) ?? (file == null ? null : DartProject.Find(file))
                ?? (folder == null ? null : DartProject.Find(folder));
            return project ?? throw new InvalidOperationException("No pubspec.yaml was found. Open the folder of a Dart or Flutter package (File > Open > Folder).");
        }
    }
}
