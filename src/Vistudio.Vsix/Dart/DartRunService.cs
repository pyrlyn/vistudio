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
using Microsoft.VisualStudio.Threading;
using Vistudio.Core;
using Vistudio.Core.Dart;

namespace Vistudio.Dart
{
    /// <summary>
    /// Runs Dart programs (<c>dart run</c>) and Flutter apps (<c>flutter run --machine</c> with hot reload, hot restart and
    /// reload on save), plus one-shot tool commands (pub get, analyze, devices, flutter create). One running app and one
    /// tool command at a time; output goes to the Dart pane, compiler errors to the Error List.
    /// </summary>
    [Export, PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class DartRunService
    {
        private readonly DartEnvironment environment;
        private readonly DartErrorList errorList;
        private readonly object gate = new object();
        private FlutterAppSession? app;
        private OwnedProcess? program;
        private CancellationTokenSource? tool;
        private CancellationTokenSource? reloadDelay;

        [ImportingConstructor]
        public DartRunService(DartEnvironment environment, DartErrorList errorList)
        {
            this.environment = environment;
            this.errorList = errorList;
        }

        public bool IsAppRunning { get { lock (gate) return (app != null && !app.HasExited) || (program != null && !program.Exit.IsCompleted); } }
        public bool IsFlutterAppRunning { get { lock (gate) return app != null && !app.HasExited; } }
        public bool IsToolBusy { get { lock (gate) return tool != null; } }

        /// <summary>Runs the program or app of the active document's package (Ctrl+F5 equivalent without the debugger).</summary>
        public async Task RunAsync(string? activeDocument)
        {
            var project = await environment.RequireProjectAsync(activeDocument);
            var sdk = await environment.RequireSdkAsync();
            var entry = EntryPoint(project, activeDocument);
            if (project.IsFlutter) await RunFlutterAsync(sdk, project, entry);
            else await RunDartAsync(sdk, project, entry ?? throw new InvalidOperationException("No entry point: open a .dart file with main, or add bin\\" + project.Name + ".dart."));
        }

        /// <summary>The active document when it declares <c>main</c>, otherwise the package's default entry point.</summary>
        internal static string? EntryPoint(DartProject project, string? activeDocument)
        {
            if (activeDocument != null && DartProject.IsDartFile(activeDocument) && File.Exists(activeDocument) &&
                !project.IsTestFile(activeDocument) && !project.IsIntegrationTest(activeDocument))
            {
                try
                {
                    if (DartSourceScanner.Scan(File.ReadAllText(activeDocument)).Any(d => d.Kind == DartDeclarationKind.Main)) return activeDocument;
                }
                catch (IOException) { }
            }
            return project.DefaultEntryPoint();
        }

        private async Task RunDartAsync(DartSdk sdk, DartProject project, string entry)
        {
            await StopAsync();
            var start = DartCommandFactory.Run(sdk, project, entry, DartSettings.Load().ProgramArguments);
            await PrepareOutputAsync(start, project.Root);
            var parser = new DartDiagnosticParser(project.Root);
            var watch = Stopwatch.StartNew();
            var process = new OwnedProcess(start);
            lock (gate) program = process;
            process.Input.Close();
            var output = PumpAsync(new StreamReader(process.Output, Encoding.UTF8), parser);
            var errors = PumpAsync(process.Error, parser);
            ObserveProgramAsync(process, output, errors, parser, watch, Path.GetFileName(entry)).Forget();
        }

        private async Task PumpAsync(StreamReader reader, DartDiagnosticParser parser)
        {
            try
            {
                await OwnedProcess.PumpAsync(reader, line =>
                {
                    string shown;
                    lock (parser) shown = parser.Accept(line);
                    environment.Log(shown);
                }).ConfigureAwait(false);
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task ObserveProgramAsync(OwnedProcess process, Task output, Task errors, DartDiagnosticParser parser, Stopwatch watch, string title)
        {
#pragma warning disable VSTHRD003 // Process tasks have no UI-thread dependency.
            var code = await process.Exit.ConfigureAwait(false);
            try { await Task.WhenAll(output, errors).ConfigureAwait(false); } catch (Exception) { }
#pragma warning restore VSTHRD003
            List<DartDiagnostic> diagnostics;
            lock (parser) diagnostics = parser.Diagnostics.ToList();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            errorList.Publish(DartErrorSource.Run, diagnostics);
            environment.Log("dart run " + title + " exited with code " + code + " after " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s.");
            lock (gate) if (program == process) program = null;
            process.Dispose();
        }

        private async Task RunFlutterAsync(DartSdk sdk, DartProject project, string? entry)
        {
            await StopAsync();
            var settings = DartSettings.Load();
            var start = DartCommandFactory.FlutterRun(sdk, project, entry, settings.FlutterDevice.Length == 0 ? "windows" : settings.FlutterDevice, settings.FlutterRunArguments);
            await PrepareOutputAsync(start, project.Root);
            var session = new FlutterAppSession(project, start, environment.Log);
            session.DiagnosticsChanged += () => PublishAppDiagnosticsAsync(session).Forget();
            lock (gate) app = session;
            ObserveAppAsync(session).Forget();
            try
            {
                await session.Started;
            }
            catch (InvalidOperationException e) { environment.Log(e.Message); }
        }

        private async Task PublishAppDiagnosticsAsync(FlutterAppSession session)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            errorList.Publish(DartErrorSource.Run, session.Diagnostics);
        }

        private async Task ObserveAppAsync(FlutterAppSession session)
        {
#pragma warning disable VSTHRD003 // Process task has no UI-thread dependency.
            var code = await session.Exit.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            environment.Log("flutter run exited with code " + code + ".");
            await PublishAppDiagnosticsAsync(session);
            lock (gate) if (app == session) app = null;
            session.Dispose();
        }

        private async Task PrepareOutputAsync(ProcessStartInfo start, string root)
        {
            await environment.ClearOutputAsync();
            await environment.ShowOutputAsync();
            environment.Log("> " + start.FileName + " " + start.Arguments);
            environment.Log("  in " + root);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            errorList.Publish(DartErrorSource.Run, Array.Empty<DartDiagnostic>());
        }

        /// <summary>Hot reload (<paramref name="fullRestart"/> false) or hot restart of the running Flutter app.</summary>
        public async Task ReloadAsync(bool fullRestart, string reason)
        {
            FlutterAppSession? session;
            lock (gate) session = app;
            if (session == null || session.HasExited) throw new InvalidOperationException("No Flutter app is running. Start one with Flutter: Run.");
            var watch = Stopwatch.StartNew();
            environment.Log((fullRestart ? "Hot restart" : "Hot reload") + " (" + reason + ")...");
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
            {
                var message = await session.RestartAsync(fullRestart, reason, timeout.Token);
                environment.Log((fullRestart ? "Hot restart" : "Hot reload") + " done in " + watch.ElapsedMilliseconds + " ms" + (message.Length == 0 ? "." : ": " + message));
            }
        }

        /// <summary>Called when a .dart file is saved: hot reloads a running app of the same package after a short delay (several saves, one reload).</summary>
        internal void OnDartFileSaved(string path)
        {
            if (!DartSettings.Load().HotReloadOnSave) return;
            FlutterAppSession? session;
            CancellationTokenSource delay;
            lock (gate)
            {
                session = app;
                if (session == null || session.HasExited || !session.Started.IsCompleted || session.Started.IsFaulted) return;
                if (DartProject.FindRoot(path) is string root && !string.Equals(root.TrimEnd('\\'), session.Project.Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
                reloadDelay?.Cancel();
                reloadDelay = delay = new CancellationTokenSource();
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(300, delay.Token).ConfigureAwait(false);
                    await ReloadAsync(false, "save").ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { environment.Log(e.Message); }
            });
        }

        /// <summary>Stops the running app or program.</summary>
        public async Task StopAsync()
        {
            FlutterAppSession? session;
            OwnedProcess? process;
            lock (gate) { session = app; process = program; app = null; program = null; }
            if (session != null) { environment.Log("Stopping the Flutter app..."); await session.StopAsync(); }
            if (process != null && !process.Exit.IsCompleted) { environment.Log("Stopping the Dart program..."); await process.StopAsync(); }
        }

        /// <summary><c>pub get</c> for the active document's package.</summary>
        public async Task PubGetAsync(string? activeDocument)
        {
            var project = await environment.RequireProjectAsync(activeDocument);
            var sdk = await environment.RequireSdkAsync();
            await RunToolAsync((project.IsFlutter ? "flutter" : "dart") + " pub get", DartCommandFactory.PubGet(sdk, project), project.Root, null);
        }

        /// <summary><c>dart analyze</c> of the whole package; results replace the "Dart analyze" Error List entries.</summary>
        public async Task AnalyzeAsync(string? activeDocument)
        {
            var project = await environment.RequireProjectAsync(activeDocument);
            var sdk = await environment.RequireSdkAsync();
            var diagnostics = new List<DartDiagnostic>();
            await RunToolAsync("dart analyze", DartCommandFactory.Analyze(sdk, project.Root), project.Root, line =>
            {
                var diagnostic = DartDiagnosticParser.ParseMachineLine(line);
                if (diagnostic == null) return line;
                lock (diagnostics) diagnostics.Add(diagnostic);
                return diagnostic.ToString();
            });
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            errorList.Publish(DartErrorSource.Analyze, diagnostics);
            environment.Log("dart analyze: " + diagnostics.Count(d => d.Severity == DartDiagnosticSeverity.Error) + " errors, " +
                diagnostics.Count(d => d.Severity == DartDiagnosticSeverity.Warning) + " warnings, " + diagnostics.Count(d => d.Severity == DartDiagnosticSeverity.Info) + " infos.");
        }

        /// <summary>The devices <c>flutter devices --machine</c> reports.</summary>
        public async Task<IReadOnlyList<FlutterDevice>> GetDevicesAsync()
        {
            var sdk = await environment.RequireSdkAsync();
            var output = new StringBuilder();
            var code = await OwnedProcess.RunAsync(DartCommandFactory.Devices(sdk, await environment.GetFolderAsync() ?? Path.GetTempPath()),
                line => { lock (output) output.AppendLine(line); }, CancellationToken.None);
            var devices = FlutterDaemon.ParseDevices(output.ToString());
            if (code != 0 && devices.Count == 0) throw new InvalidOperationException("flutter devices failed (exit code " + code + "): " + output.ToString().Trim());
            return devices;
        }

        /// <summary><c>flutter create</c> into a new folder.</summary>
        public async Task<bool> CreateFlutterAppAsync(string folder)
        {
            var sdk = await environment.RequireSdkAsync();
            sdk.RequireFlutter();
            return await RunToolAsync("flutter create", DartCommandFactory.FlutterCreate(sdk, folder, null), Path.GetDirectoryName(folder.TrimEnd('\\'))!, null);
        }

        public void CancelTool()
        {
            lock (gate) tool?.Cancel();
        }

        private async Task<bool> RunToolAsync(string title, ProcessStartInfo start, string root, Func<string, string>? transform)
        {
            CancellationTokenSource cancellation;
            lock (gate)
            {
                if (tool != null) throw new InvalidOperationException("A Dart command is already running. Use Dart: Cancel to stop it.");
                tool = cancellation = new CancellationTokenSource();
            }
            try
            {
                await environment.ShowOutputAsync();
                environment.Log("> " + start.FileName + " " + start.Arguments);
                environment.Log("  in " + root);
                var watch = Stopwatch.StartNew();
                int code;
                try
                {
                    code = await Task.Run(() => OwnedProcess.RunAsync(start, line => environment.Log(transform == null ? line : transform(line)), cancellation.Token));
                }
                catch (OperationCanceledException)
                {
                    environment.Log(title + ": canceled.");
                    return false;
                }
                environment.Log(title + (code == 0 ? " succeeded" : " finished with exit code " + code) + " in " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s.");
                return code == 0;
            }
            finally
            {
                lock (gate) tool = null;
                cancellation.Dispose();
            }
        }
    }
}
