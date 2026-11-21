using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Vistudio.Core;
using Vistudio.Core.Zig;

namespace Vistudio.Zig
{
    /// <summary>
    /// Zig build commands: Build Project (<c>zig build [step]</c>), Build File (<c>zig build-obj</c>), Run (<c>zig run</c>),
    /// one at a time, cancellable. Output goes to the Zig pane with clickable locations; errors to the Error List.
    /// </summary>
    [Export, PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class ZigBuildService
    {
        private readonly ZigEnvironment environment;
        private readonly ZigErrorList errorList;
        private readonly object gate = new object();
        private CancellationTokenSource? running;
        private string? stepsRoot;
        private IReadOnlyList<ZigBuildStep> steps = Array.Empty<ZigBuildStep>();
        private Task? stepsRefresh;

        [ImportingConstructor]
        public ZigBuildService(ZigEnvironment environment, ZigErrorList errorList)
        {
            this.environment = environment;
            this.errorList = errorList;
        }

        public bool IsBusy { get { lock (gate) return running != null; } }

        public void Cancel()
        {
            lock (gate) running?.Cancel();
        }

        /// <summary>The folder with build.zig for the active document or the open folder; null when there is none.</summary>
        public async Task<string?> FindBuildRootAsync(string? activeDocument)
        {
            var folder = await environment.GetFolderAsync();
            if (activeDocument != null && ZigCommandFactory.IsZigFile(activeDocument))
            {
                var root = ZigCommandFactory.FindBuildRoot(activeDocument, folder);
                if (root != null) return root;
            }
            if (folder != null && File.Exists(Path.Combine(folder, "build.zig"))) return folder;
            return activeDocument == null ? null : ZigCommandFactory.FindBuildRoot(activeDocument);
        }

        public async Task<bool> BuildProjectAsync(string? activeDocument)
        {
            var root = await FindBuildRootAsync(activeDocument) ?? throw new InvalidOperationException("No build.zig was found. Open the folder that contains build.zig (File > Open > Folder).");
            var settings = ZigSettings.Load();
            var zig = ZigToolchain.RequireExecutable(settings);
            var step = settings.BuildStep.Trim();
            return await RunAsync("zig build" + (step.Length == 0 ? "" : " " + step), root,
                ZigCommandFactory.Build(zig, root, step, ZigToolchain.LibDirectory(settings), settings.BuildFlags));
        }

        public async Task<bool> BuildFileAsync(string file)
        {
            RequireZigFile(file);
            var settings = ZigSettings.Load();
            var zig = ZigToolchain.RequireExecutable(settings);
            var root = await FindBuildRootAsync(file) ?? Path.GetDirectoryName(file)!;
            var start = ZigCommandFactory.BuildObject(zig, file, root, ZigToolchain.LibDirectory(settings), settings.DefaultFlags, out var objectFile);
            Directory.CreateDirectory(Path.GetDirectoryName(objectFile)!);
            var succeeded = await RunAsync("zig build-obj " + Path.GetFileName(file), root, start);
            if (succeeded) environment.Log("Object file: " + objectFile);
            return succeeded;
        }

        public async Task<bool> RunFileAsync(string file)
        {
            RequireZigFile(file);
            var settings = ZigSettings.Load();
            var zig = ZigToolchain.RequireExecutable(settings);
            var root = await FindBuildRootAsync(file) ?? Path.GetDirectoryName(file)!;
            return await RunAsync("zig run " + Path.GetFileName(file), root, ZigCommandFactory.Run(zig, file, root, ZigToolchain.LibDirectory(settings), settings.DefaultFlags));
        }

        /// <summary>
        /// <c>zig test</c> of <paramref name="file"/>, only the tests whose name contains <paramref name="filter"/> (all of
        /// them when it is empty). With <paramref name="emitBinary"/> the test program is built, not run (Debug test).
        /// Output in the Zig pane, compiler errors in the Error List, as for Run File.
        /// </summary>
        public async Task<bool> TestFileAsync(string file, string? filter, string? emitBinary = null)
        {
            RequireZigFile(file);
            var settings = ZigSettings.Load();
            var zig = ZigToolchain.RequireExecutable(settings);
            var root = await FindBuildRootAsync(file) ?? Path.GetDirectoryName(file)!;
            if (emitBinary != null) Directory.CreateDirectory(Path.GetDirectoryName(emitBinary)!);
            var title = "zig test " + Path.GetFileName(file) + (string.IsNullOrEmpty(filter) ? "" : " (" + filter + ")");
            return await RunAsync(title, root, ZigCommandFactory.Test(zig, file, root, ZigToolchain.LibDirectory(settings), settings.DefaultFlags, filter, emitBinary));
        }

        private static void RequireZigFile(string file)
        {
            if (!file.EndsWith(".zig", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Open a .zig source file first (the active document is " + Path.GetFileName(file) + ").");
        }

        private async Task<bool> RunAsync(string title, string root, ProcessStartInfo start)
        {
            CancellationTokenSource cancellation;
            lock (gate)
            {
                if (running != null) throw new InvalidOperationException("A Zig command is already running. Use Zig: Cancel to stop it.");
                running = cancellation = new CancellationTokenSource();
            }
            try
            {
                await environment.ClearOutputAsync();
                await environment.ShowOutputAsync();
                environment.Log("> " + start.FileName + " " + start.Arguments);
                environment.Log("  in " + root);
                var parser = new ZigDiagnosticParser(start.WorkingDirectory);
                var watch = Stopwatch.StartNew();
                int code;
                try
                {
                    code = await Task.Run(() => OwnedProcess.RunAsync(start, line =>
                    {
                        string shown;
                        lock (parser) shown = parser.Accept(line);
                        environment.Log(shown);
                    }, cancellation.Token));
                }
                catch (OperationCanceledException)
                {
                    environment.Log(title + ": canceled.");
                    return false;
                }
                var diagnostics = ZigDiagnosticParser.Flatten(parser.Diagnostics, ZigSettings.Load().DiagnosticLevel);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                errorList.PublishBuild(diagnostics);
                var errors = diagnostics.Count(d => d.Severity == ZigDiagnosticSeverity.Error);
                environment.Log(title + (code == 0 ? " succeeded" : " failed (exit code " + code + ")") +
                    (errors > 0 ? ", " + errors + " error" + (errors == 1 ? "" : "s") : "") + " in " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s.");
                return code == 0;
            }
            finally
            {
                lock (gate) { running = null; }
                cancellation.Dispose();
            }
        }

        /// <summary>
        /// Build steps for the drop-down: the cached <c>zig build -l</c> result for <paramref name="root"/>, otherwise a
        /// static scan of build.zig while <c>zig build -l</c> runs in the background (it compiles build.zig).
        /// </summary>
        public IReadOnlyList<ZigBuildStep> GetSteps(string? root, Action? refreshed = null)
        {
            if (root == null) return Array.Empty<ZigBuildStep>();
            lock (gate)
            {
                if (string.Equals(stepsRoot, root, StringComparison.OrdinalIgnoreCase) && steps.Count > 0) return steps;
                stepsRoot = root;
                steps = ScanSteps(root);
                if (stepsRefresh == null || stepsRefresh.IsCompleted) stepsRefresh = Task.Run(() => RefreshStepsAsync(root, refreshed));
                return steps;
            }
        }

        public Task RefreshStepsAsync(string root, Action? refreshed = null) => RefreshStepsCoreAsync(root, refreshed, log: false);

        /// <summary>Re-reads the steps (Zig: Refresh Build Steps) and lists them in the Output pane.</summary>
        public Task RefreshStepsVerboseAsync(string root) => RefreshStepsCoreAsync(root, null, log: true);

        private async Task RefreshStepsCoreAsync(string root, Action? refreshed, bool log)
        {
            var settings = ZigSettings.Load();
            var zig = ZigToolchain.FindExecutable(settings);
            if (zig == null) { if (log) environment.Log("zig.exe not found; build steps come from a scan of build.zig."); return; }
            var output = new StringBuilder();
            int code;
            try
            {
                code = await OwnedProcess.RunAsync(ZigCommandFactory.ListSteps(zig, root, ZigToolchain.LibDirectory(settings)),
                    line => { lock (output) output.AppendLine(line); }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) { environment.Log("zig build -l: " + e.Message); return; }
            var listed = code == 0 ? ZigBuildSteps.ParseList(output.ToString()) : Array.Empty<ZigBuildStep>();
            if (listed.Count == 0)
            {
                environment.Log("zig build -l failed (exit code " + code + "); using the steps declared in build.zig." + Environment.NewLine + output.ToString().TrimEnd());
                listed = ScanSteps(root);
            }
            lock (gate)
            {
                if (string.Equals(stepsRoot, root, StringComparison.OrdinalIgnoreCase) || stepsRoot == null) { stepsRoot = root; steps = listed; }
            }
            if (log)
            {
                environment.Log("Build steps in " + root + ":");
                foreach (var step in listed) environment.Log("  " + step + (step.Description.Length == 0 ? "" : " - " + step.Description));
                var artifacts = ZigBuildSteps.ScanArtifacts(SafeRead(Path.Combine(root, "build.zig")));
                if (artifacts.Count > 0) environment.Log("Artifacts: " + string.Join(", ", artifacts.Select(a => a.Name + " (" + a.Kind + ")")));
            }
            refreshed?.Invoke();
        }

        private static IReadOnlyList<ZigBuildStep> ScanSteps(string root)
        {
            var script = SafeRead(Path.Combine(root, "build.zig"));
            return script.Length == 0 ? Array.Empty<ZigBuildStep>() : ZigBuildSteps.Scan(script);
        }

        private static string SafeRead(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }
    }
}
