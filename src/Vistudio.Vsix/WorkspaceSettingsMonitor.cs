using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Workspace.VSIntegration.Contracts;
using Vistudio.Core;
using Vistudio.Core.Dart;
using Vistudio.Core.Rust;
using Vistudio.Core.Wasm;
using Vistudio.Core.Zig;
using Vistudio.Dart;
using Vistudio.Zig;
using SolutionEvents = Microsoft.VisualStudio.Shell.Events.SolutionEvents;

// Settings notifications and server restarts run detached: FileAndForget reports failures, nothing waits on them.
#pragma warning disable VSSDK007

namespace Vistudio
{
    /// <summary>
    /// Keeps <see cref="WorkspaceSettings.CurrentRoot"/> on this Visual Studio window's workspace (Open Folder location, else
    /// the solution folder) and watches its <c>.vs\settings.json</c>. Every Visual Studio instance is its own process, so each
    /// window has exactly one monitor and its own effective Kotlin, Java and Zig settings.
    /// </summary>
    [Export, PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class WorkspaceSettingsMonitor
    {
        private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
        private readonly IServiceProvider services;
        private readonly List<Action<string>> logs = new List<Action<string>>();
        private readonly Timer changeTimer;
        private IVsFolderWorkspaceService? folders;
        private FileSystemWatcher? watcher;
        private bool initialized, tracking;
        private string? root;
        private string? kotlinSnapshot, kotlinServer, javaServer, zigSnapshot, dartSnapshot, rustSnapshot, wasmSnapshot;

        [ImportingConstructor]
        public WorkspaceSettingsMonitor([Import(typeof(SVsServiceProvider))] IServiceProvider services)
        {
            this.services = services;
            changeTimer = new Timer(_ => ThreadHelper.JoinableTaskFactory.RunAsync(OnFileChangedAsync).FileAndForget("Vistudio/WorkspaceSettings"));
        }

        /// <summary>Adds an Output pane writer for the file in use and its warnings.</summary>
        public void Attach(Action<string> log)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!logs.Contains(log)) logs.Add(log);
        }

        /// <summary>
        /// Points the settings at the current workspace. The Kotlin and Zig environments call this before their own
        /// workspace-change handlers, which restart the language servers with the new settings.
        /// </summary>
        public async Task RefreshAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Initialize();
            Update(ResolveRoot(), false);
        }

        private void Initialize()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (initialized) return;
            initialized = true;
            if (services.GetService(typeof(SComponentModel)) is IComponentModel components)
                folders = components.GetService<IVsFolderWorkspaceService>();
            // Solutions without Open Folder raise no workspace event; Vistudio's environments only follow .kotlinproj files.
            SolutionEvents.OnAfterOpenSolution += (s, e) => OnSolutionChanged();
            SolutionEvents.OnAfterCloseSolution += (s, e) => OnSolutionChanged();
        }

        private void OnSolutionChanged()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (folders?.CurrentWorkspace != null) return; // The folder workspace event covers it.
            Update(ResolveRoot(), true);
        }

        private string? ResolveRoot()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var folder = folders?.CurrentWorkspace?.Location;
            if (!string.IsNullOrEmpty(folder)) return folder;
            if (services.GetService(typeof(SVsSolution)) is IVsSolution solution
                && solution.GetSolutionInfo(out var directory, out _, out _) == 0 && !string.IsNullOrEmpty(directory)) return directory;
            return null;
        }

        private void Update(string? candidate, bool notify)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            WorkspaceSettings.CurrentRoot = candidate;
            var current = WorkspaceSettings.CurrentRoot;
            if (tracking && string.Equals(root, current, StringComparison.OrdinalIgnoreCase)) return;
            var first = !tracking;
            tracking = true;
            root = current;
            Watch(current);
            Report(current);
            var changed = TakeSnapshot();
            if (!first) ClearZigBuildErrors();
            // Workspace events need no notification: their handlers restart the servers with the new settings.
            if (notify && changed.Any) Notify(changed);
        }

        private void Report(string? folder)
        {
            if (folder == null) return;
            var file = WorkspaceSettings.Read(folder);
            if (file.Path == null) return;
            Log("Workspace settings: " + file.Path);
            foreach (var warning in WorkspaceSettings.Check(folder)) Log("Workspace settings: " + warning);
        }

        private void Watch(string? folder)
        {
            watcher?.Dispose();
            watcher = null;
            if (folder == null) return;
            try
            {
                var settingsFolder = Path.Combine(folder, ".vs");
                // Visual Studio normally creates .vs itself; until then watch the root for it (not recursively).
                var target = Directory.Exists(settingsFolder)
                    ? new FileSystemWatcher(settingsFolder, "settings.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime }
                    : new FileSystemWatcher(folder, ".vs") { NotifyFilter = NotifyFilters.DirectoryName };
                FileSystemEventHandler changed = (s, e) => changeTimer.Change(Debounce, Timeout.InfiniteTimeSpan);
                target.Changed += changed;
                target.Created += changed;
                target.Deleted += changed;
                target.Renamed += (s, e) => changeTimer.Change(Debounce, Timeout.InfiniteTimeSpan);
                target.Error += (s, e) => changeTimer.Change(Debounce, Timeout.InfiniteTimeSpan);
                target.EnableRaisingEvents = true;
                watcher = target;
            }
            catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException)
            {
                Log("Workspace settings: cannot watch " + folder + ": " + e.Message);
            }
        }

        private async Task OnFileChangedAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var folder = root;
            if (folder == null || !string.Equals(folder, WorkspaceSettings.CurrentRoot, StringComparison.OrdinalIgnoreCase)) return;
            // .vs appeared: move the watcher onto it.
            if (watcher != null && !string.Equals(watcher.Path, Path.Combine(folder, ".vs"), StringComparison.OrdinalIgnoreCase)) Watch(folder);
            var changed = TakeSnapshot();
            if (!changed.Any) return;
            Report(folder);
            Notify(changed);
        }

        private readonly struct Changes
        {
            public Changes(bool kotlin, bool kotlinServer, bool javaServer, bool zig, bool dart, bool rust, bool wasm)
            {
                Kotlin = kotlin; KotlinServer = kotlinServer; JavaServer = javaServer; Zig = zig; Dart = dart; Rust = rust; Wasm = wasm;
            }

            public bool Kotlin { get; }
            public bool KotlinServer { get; }
            public bool JavaServer { get; }
            public bool Zig { get; }
            public bool Dart { get; }
            public bool Rust { get; }
            public bool Wasm { get; }
            public bool Any => Kotlin || Zig || Dart || Rust || Wasm;
        }

        // Compares the effective settings with the last ones applied, so unrelated saves of the file restart nothing.
        private Changes TakeSnapshot()
        {
            KotlinSettings kotlin;
            ZigSettings zig;
            try { kotlin = KotlinSettings.Load(); }
            catch (InvalidOperationException e) { Log("Kotlin settings: " + e.Message); kotlin = new KotlinSettings(); }
            zig = ZigSettings.Load();
            var nextKotlin = Serialize(kotlin);
            var nextKotlinServer = string.Join("\n", kotlin.LanguageServerPath, kotlin.LanguageServerArguments, kotlin.LanguageServerJavaHome, kotlin.GradleRoot, kotlin.GradleJavaHome);
            var nextJavaServer = string.Join("\n", kotlin.JavaLanguageServerPath, kotlin.JavaLanguageServerJavaHome, kotlin.LanguageServerJavaHome, kotlin.GradleRoot, kotlin.GradleJavaHome);
            var nextZig = Serialize(zig);
            var nextDart = Serialize(DartSettings.Load());
            var nextRust = Serialize(RustSettings.Load());
            var nextWasm = Serialize(WasmSettings.Load());
            var changes = new Changes(nextKotlin != kotlinSnapshot, nextKotlinServer != kotlinServer, nextJavaServer != javaServer, nextZig != zigSnapshot, nextDart != dartSnapshot,
                nextRust != rustSnapshot, nextWasm != wasmSnapshot);
            kotlinSnapshot = nextKotlin; kotlinServer = nextKotlinServer; javaServer = nextJavaServer; zigSnapshot = nextZig; dartSnapshot = nextDart;
            rustSnapshot = nextRust; wasmSnapshot = nextWasm;
            return changes;
        }

        private static string Serialize<T>(T value)
        {
            using (var writer = new StringWriter())
            {
                new XmlSerializer(typeof(T)).Serialize(writer, value);
                return writer.ToString();
            }
        }

        // The same notifications Tools > Options raises, plus a server restart when the server settings changed.
        private void Notify(Changes changes)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (changes.Zig) ZigEnvironment.OnSettingsChanged();
            if (changes.Dart) DartEnvironment.OnSettingsChanged();
            if (changes.Rust) Rust.RustLanguageClient.OnSettingsChanged();
            if (changes.Wasm) Wasm.WasmLanguageClient.OnSettingsChanged();
            if (!changes.Kotlin) return;
            KotlinProjectNode.OnTreeSettingsChanged();
            ScrollBarMapOptions.OnSettingsChanged();
            InlayHintsViewOptions.OnSettingsChanged();
            if (!(services.GetService(typeof(SComponentModel)) is IComponentModel components)) return;
            if (changes.KotlinServer)
            {
                var kotlin = components.GetService<KotlinLanguageClient>();
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    try { await kotlin.RestartAsync(); }
                    catch (InvalidOperationException e) { Log("Workspace settings: Kotlin language server not restarted: " + e.Message); }
                }).FileAndForget("Vistudio/WorkspaceSettings");
            }
            if (changes.JavaServer)
            {
                var java = components.GetService<JavaLanguageClient>();
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    try { await java.RestartAsync(); }
                    catch (InvalidOperationException e) { Log("Workspace settings: Java language server not restarted: " + e.Message); }
                }).FileAndForget("Vistudio/WorkspaceSettings");
            }
        }

        // Build errors belong to the workspace that produced them; ast-check and LSP errors follow their documents.
        private void ClearZigBuildErrors()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (services.GetService(typeof(SComponentModel)) is IComponentModel components)
                components.GetService<ZigErrorList>().PublishBuild(Array.Empty<ZigDiagnostic>());
        }

        private void Log(string message)
        {
            foreach (var log in logs.ToArray())
            {
                try { log(message); }
                catch (Exception) { }
            }
        }
    }
}
