using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Vistudio.Core;
using Vistudio.Core.Dart;
using Vistudio.Core.Packages;
using Vistudio.Core.Zig;

namespace Vistudio.Packages
{
    /// <summary>What Solution Explorer shows for one manifest.</summary>
    internal sealed class PackageState
    {
        public PackageState(IReadOnlyList<PackageEntry> packages, string? status, bool checkedForUpdates)
        {
            Packages = packages;
            Status = status;
            CheckedForUpdates = checkedForUpdates;
        }

        public IReadOnlyList<PackageEntry> Packages { get; }

        /// <summary>One line above the packages: an error, "Checking for updates...", or null.</summary>
        public string? Status { get; }

        public bool CheckedForUpdates { get; }
    }

    /// <summary>
    /// Package data for every project in this Visual Studio window: declared packages read from the manifests, newer
    /// versions from each manager's own report (checked in the background once per manifest and on request), and the
    /// add / remove / update actions run one at a time through the managers, with their output in the "Packages" pane.
    /// </summary>
    [Export, PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class PackageService
    {
        private static readonly Guid PaneId = new Guid("8bbf8229-bb94-4562-bad6-60c5308b5392");
        private readonly OutputPane output;
        private readonly ConcurrentDictionary<string, Cached> cache = new ConcurrentDictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim actions = new SemaphoreSlim(1, 1);
        /// <summary>The project's own package manager setting per manifest (language projects' Package manager property).</summary>
        private readonly ConcurrentDictionary<string, string> tools = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        [ImportingConstructor]
        public PackageService([Import(typeof(SVsServiceProvider))] IServiceProvider services) => output = new OutputPane(services, PaneId, "Packages");

        /// <summary>Raised on any thread with the manifest path whose packages or versions changed.</summary>
        public event Action<string>? Changed;

        public void Log(string message) => output.Log(message);

        private sealed class Cached
        {
            public Cached(DateTime written, PackageState state) { Written = written; State = state; }
            public DateTime Written { get; }
            public PackageState State { get; set; }
            public int Checking;
        }

        /// <summary>The packages of <paramref name="manifest"/> as known now; rereads the manifest when it changed on disk. Never blocks on the network.</summary>
        public PackageState Get(PackageManifestRef manifest)
        {
            var written = WriteTime(manifest.ManifestPath);
            if (cache.TryGetValue(manifest.ManifestPath, out var cached) && cached.Written == written) return cached.State;
            PackageState state;
            try
            {
                var declared = manifest.Manager.ReadManifest(manifest.ManifestPath);
                // Versions from the previous check stay visible until the next check replaces them.
                var previous = cached?.State.Packages.ToDictionary(p => p.Name + "|" + p.Group, StringComparer.OrdinalIgnoreCase);
                var merged = declared.Select(p => previous != null && previous.TryGetValue(p.Name + "|" + p.Group, out var old) && p.Current == null
                    ? p.WithVersions(old.Current, old.Compatible, old.Latest) : p).ToList();
                state = new PackageState(merged, cached?.State.CheckedForUpdates == true ? null : "Not checked for updates yet.", cached?.State.CheckedForUpdates == true);
            }
            catch (Exception e) when (e is PackageManagerException || e is IOException || e is UnauthorizedAccessException || e is InvalidDataException || e is FormatException)
            {
                state = new PackageState(Array.Empty<PackageEntry>(), manifest.Manager.DisplayName + ": " + e.Message, false);
            }
            cache[manifest.ManifestPath] = new Cached(written, state);
            return state;
        }

        /// <summary>Starts a background version check unless one ran for this manifest already (or <paramref name="force"/>).</summary>
        public void CheckForUpdates(PackageManifestRef manifest, bool force = false, string? configuredTool = null)
        {
            if (configuredTool != null) tools[manifest.ManifestPath] = configuredTool;
            var state = Get(manifest);
            if (state.CheckedForUpdates && !force || !cache.TryGetValue(manifest.ManifestPath, out var cached)) return;
            if (Interlocked.Exchange(ref cached.Checking, 1) == 1) return;
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try { await CheckAsync(manifest, cached); }
                finally { Interlocked.Exchange(ref cached.Checking, 0); }
            }).FileAndForget("Vistudio/Packages/Check");
        }

        private async Task CheckAsync(PackageManifestRef manifest, Cached cached)
        {
            var declared = cached.State.Packages;
            Publish(manifest, cached, new PackageState(declared, "Checking for updates...", cached.State.CheckedForUpdates));
            PackageState result;
            try
            {
                var tool = FindTool(manifest, Configured(manifest)) ?? throw new PackageManagerException(Missing(manifest.Manager));
                var commands = manifest.Manager.CreateOutdated(tool, manifest.ManifestPath, declared);
                var outputs = new List<PackageToolOutput>();
                foreach (var command in commands)
                {
                    Log("> " + Describe(command));
                    outputs.Add(await RunCapturedAsync(command, CancellationToken.None));
                }
                var updated = manifest.Manager.ApplyOutdated(declared, outputs);
                var outdated = updated.Count(p => p.IsOutdated);
                Log(manifest.Manager.DisplayName + " " + manifest.ManifestPath + ": " + updated.Count + " packages, " + outdated + " outdated.");
                result = new PackageState(updated, commands.Count == 0 ? manifest.Manager.DisplayName + " reports no newer versions." : null, true);
            }
            catch (Exception e) when (e is PackageManagerException || e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException
                || e is System.ComponentModel.Win32Exception || e is FormatException || e is ArgumentException)
            {
                Log(manifest.Manager.DisplayName + ": version check failed: " + e.Message);
                result = new PackageState(declared, "Version check failed: " + FirstLine(e.Message), true);
            }
            Publish(manifest, cached, result);
        }

        private void Publish(PackageManifestRef manifest, Cached cached, PackageState state)
        {
            cached.State = state;
            Changed?.Invoke(manifest.ManifestPath);
        }

        /// <summary>
        /// Runs one action built by <paramref name="build"/> (built inside the action gate, so it sees the manifest as the
        /// previous action left it): manifest edits first, then the manager's commands in order, stopping at the first
        /// failure. The manifest is reread and, when it was checked before, checked again.
        /// </summary>
        public async Task<bool> RunAsync(PackageManifestRef manifest, Func<string, PackageAction> build, CancellationToken token = default)
        {
            await actions.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var tool = FindTool(manifest, Configured(manifest));
                PackageAction action;
                try { action = build(tool ?? throw new PackageManagerException(Missing(manifest.Manager))); }
                catch (Exception e) when (e is PackageManagerException || e is ArgumentException || e is IOException)
                {
                    Log(manifest.Manager.DisplayName + ": " + e.Message);
                    await output.ShowAsync();
                    return false;
                }
                Log("== " + action.Description);
                await output.ShowAsync();
                foreach (var edit in action.Edits)
                {
                    WritePreservingEncoding(edit.Path, edit.Text);
                    Log("Edited " + edit.Path);
                }
                var success = true;
                foreach (var command in action.Commands)
                {
                    Log("> " + Describe(command));
                    var exit = await OwnedProcess.RunAsync(command, Log, token).ConfigureAwait(false);
                    if (exit != 0) { Log(manifest.Manager.DisplayName + " exited with code " + exit + "."); success = false; break; }
                }
                Log(success ? "Done." : "Failed.");
                cache.TryRemove(manifest.ManifestPath, out var previous);
                var checkedBefore = previous?.State.CheckedForUpdates == true;
                if (previous != null) cache[manifest.ManifestPath] = new Cached(DateTime.MinValue, previous.State);
                Get(manifest);
                if (checkedBefore) CheckForUpdates(manifest, force: true);
                Changed?.Invoke(manifest.ManifestPath);
                return success;
            }
            finally { actions.Release(); }
        }

        /// <summary>
        /// Updates every outdated package to its latest version, one action per package so each one is built from the
        /// manifest as the previous update left it.
        /// </summary>
        public async Task UpdateAllToLatestAsync(PackageManifestRef manifest)
        {
            var outdated = Get(manifest).Packages.Where(p => p.IsOutdated).Select(p => p.Name + "|" + p.Group).ToList();
            if (outdated.Count == 0) { Log(manifest.Manager.DisplayName + ": nothing to update; run Check for Updates first."); await output.ShowAsync(); return; }
            foreach (var key in outdated)
            {
                var ok = await RunAsync(manifest, tool =>
                {
                    var current = Get(manifest).Packages.FirstOrDefault(p => string.Equals(p.Name + "|" + p.Group, key, StringComparison.OrdinalIgnoreCase))
                        ?? throw new PackageManagerException(key.Split('|')[0] + " is no longer declared.");
                    return manifest.Manager.Update(tool, manifest.ManifestPath, current, true);
                });
                if (!ok) break;
            }
        }

        /// <summary>The manager's executable: the project's own setting, the language's configured SDK, then PATH.</summary>
        public static string? FindTool(PackageManifestRef manifest, string? configured = null)
        {
            if (!string.IsNullOrWhiteSpace(configured)) return PackageManagers.FindTool(manifest.Manager, configured);
            switch (manifest.Manager.Ecosystem)
            {
                case PackageEcosystem.Pub:
                    return DartToolchain.Find(DartSettings.Load())?.Dart;
                case PackageEcosystem.Zig:
                    return ZigToolchain.FindExecutable(ZigSettings.Load());
                case PackageEcosystem.Gradle:
                    var wrapper = Path.Combine(GradlePackageManager.SettingsRoot(manifest.ManifestPath), "gradlew.bat");
                    return File.Exists(wrapper) ? wrapper : null;
                case PackageEcosystem.Cargo:
                case PackageEcosystem.Wkg:
                    var cargoBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo", "bin");
                    return PackageManagers.FindTool(manifest.Manager, null, new[] { cargoBin });
                default:
                    return PackageManagers.FindTool(manifest.Manager, null);
            }
        }

        private string? Configured(PackageManifestRef manifest) => tools.TryGetValue(manifest.ManifestPath, out var tool) && tool.Length != 0 ? tool : null;

        private static string Missing(IPackageManager manager) =>
            manager.Ecosystem == PackageEcosystem.Gradle ? "The Gradle wrapper (gradlew.bat) was not found next to settings.gradle(.kts)."
            : manager.DisplayName + " (" + string.Join(", ", manager.ToolNames) + ") was not found. Install it, add it to PATH, or set its folder in the language's options.";

        private static async Task<PackageToolOutput> RunCapturedAsync(ProcessStartInfo start, CancellationToken token)
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var exit = await OwnedProcess.RunAsync(start, line => stdout.AppendLine(line), line => stderr.AppendLine(line), token).ConfigureAwait(false);
            return new PackageToolOutput(exit, stdout.ToString(), stderr.ToString());
        }

        /// <summary>
        /// Writes a manifest through a temporary file with the original file's encoding and BOM, so a manager that reads
        /// the file next sees either the old or the new text, and editors keep their encoding.
        /// </summary>
        internal static void WritePreservingEncoding(string path, string text)
        {
            Encoding encoding = new UTF8Encoding(false);
            if (File.Exists(path))
            {
                var head = new byte[4];
                int count;
                using (var stream = File.OpenRead(path)) count = stream.Read(head, 0, 4);
                if (count >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) encoding = new UTF8Encoding(true);
                else if (count >= 2 && head[0] == 0xFF && head[1] == 0xFE) encoding = Encoding.Unicode;
                else if (count >= 2 && head[0] == 0xFE && head[1] == 0xFF) encoding = Encoding.BigEndianUnicode;
            }
            var temporary = path + ".vistudio.tmp";
            File.WriteAllText(temporary, text, encoding);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        private static string Describe(ProcessStartInfo start) => Path.GetFileName(start.FileName) + " " + start.Arguments;

        private static string FirstLine(string text)
        {
            var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length != 0) ?? text;
            return line.Length > 200 ? line.Substring(0, 200) + "..." : line;
        }

        private static DateTime WriteTime(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return DateTime.MinValue; }
        }
    }
}
