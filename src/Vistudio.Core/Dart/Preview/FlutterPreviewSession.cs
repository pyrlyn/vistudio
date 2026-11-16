using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Vistudio.Core.Dart.Preview
{
    public enum PreviewSessionState { Stopped, ResolvingDependencies, Starting, Serving, Ready, Failed }

    /// <summary>What a session runs; see <see cref="FlutterPreviewSession"/>.</summary>
    public sealed class FlutterPreviewSessionOptions
    {
        public FlutterPreviewSessionOptions(DartSdk sdk, string projectRoot) { Sdk = sdk; ProjectRoot = projectRoot; }
        public DartSdk Sdk { get; }
        /// <summary>The Flutter package the previewer runs in (the user's package or a <see cref="PreviewHostProject"/>).</summary>
        public string ProjectRoot { get; }
        public bool RunPubGet { get; set; } = true;
        public bool Verbose { get; set; }
        /// <summary>How long the previewer may take to serve its page (the first web build compiles the SDK).</summary>
        public TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(10);
    }

    /// <summary>
    /// One running widget previewer: Vistudio's own Dart Tooling Daemon, an analysis server connected to it (the previewer
    /// asks it for the <c>@Preview</c> declarations), and <c>flutter widget-preview start --machine --web-server</c>,
    /// whose page the IDE embeds. The session provides the DTD <c>Editor</c> service (active file, theme, navigation),
    /// reports the previewer's state, falls back to a hot restart when a hot reload fails, and owns every process in job
    /// objects, so <see cref="StopAsync"/> (or the IDE exiting) leaves no process behind.
    /// </summary>
    public sealed class FlutterPreviewSession : IDisposable
    {
        private const string EditorService = "Editor";
        private readonly FlutterPreviewSessionOptions options;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly object gate = new object();
        private readonly List<OwnedProcess> processes = new List<OwnedProcess>();
        private OwnedProcess? daemon, analysisServer, previewer;
        private LspStdioClient? analysis;
        private DtdClient? control, editor;
        private string? scaffoldStream, previewService;
        private JObject? activeLocation;
        private JObject? theme;
        private int documentVersion;
        private volatile bool stopping;
        private PreviewSessionState state;
        private Task? previewerExit;
        private DateTime lastFallbackRestart = DateTime.MinValue;

        public FlutterPreviewSession(FlutterPreviewSessionOptions options) { this.options = options; }

        public FlutterPreviewSessionOptions Options => options;
        public PreviewSessionState State => state;
        /// <summary>True once <see cref="StopAsync"/> was called (also while a start is being cancelled).</summary>
        public bool IsStopping => stopping;
        /// <summary>The previewer page (set when <see cref="Started"/> fires).</summary>
        public string? Url { get; private set; }
        public Uri? DtdUri { get; private set; }
        /// <summary>The last error shown in the window (build failure, previewer exit), or null.</summary>
        public string? LastError { get; private set; }

        /// <summary>(level, message): level is <c>status</c>, <c>warning</c>, <c>error</c> or <c>trace</c>.</summary>
        public event Action<string, string>? Log;
        public event Action<PreviewSessionState, string>? StateChanged;
        /// <summary>The page URL is known; the window can navigate.</summary>
        public event Action<string>? Started;
        /// <summary>A classified previewer message (reload started, done, failed, errors).</summary>
        public event Action<PreviewerStatus, string>? Status;
        /// <summary>The scaffold asked to show code: file path, 1-based line and column (0 when absent).</summary>
        public event Action<string, int, int>? NavigateRequested;
        /// <summary>The previewer process ended: exit code and whether <see cref="StopAsync"/> caused it.</summary>
        public event Action<int, bool>? Exited;
        /// <summary>
        /// After each load or reload: the previews whose code (or a dependency's) has errors. The previewer does not
        /// compile those libraries (it shows an error card instead), so compile errors never appear as build output.
        /// </summary>
        public event Action<IReadOnlyList<LspWidgetPreview>>? PreviewProblems;
        /// <summary>A previewer page started its app and connected to DTD (one per configuration shown, again after each page reload).</summary>
        public event Action? PageConnected;

        /// <summary>The ids of every process the session owns (daemon, analysis server, flutter and their children).</summary>
        public IReadOnlyList<int> ProcessIds()
        {
            lock (gate) return processes.SelectMany(p => p.TreeProcessIds()).Distinct().ToList();
        }

        public async Task StartAsync(CancellationToken token)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token))
            {
                var cancel = linked.Token;
                try
                {
                    await ResolveDependenciesAsync(cancel).ConfigureAwait(false);
                    SetState(PreviewSessionState.Starting, "Starting the Dart Tooling Daemon and the analysis server...");
                    DtdUri = await StartDaemonAsync(cancel).ConfigureAwait(false);
                    await StartAnalysisServerAsync(DtdUri, cancel).ConfigureAwait(false);
                    control = await DtdClient.ConnectAsync(DtdUri, cancel).ConfigureAwait(false);
                    control.EventReceived += OnControlEvent;
                    await control.StreamListenAsync("Service", cancel).ConfigureAwait(false);
                    await RegisterEditorAsync(cancel).ConfigureAwait(false);
                    await WaitForAnalysisServiceAsync(control, cancel).ConfigureAwait(false);
                    SetState(PreviewSessionState.Starting, "Starting flutter widget-preview (the first start compiles the web SDK and can take a few minutes)...");
                    await StartPreviewerAsync(DtdUri, cancel).ConfigureAwait(false);
                }
                catch (Exception e) when (!(e is OperationCanceledException) || !token.IsCancellationRequested)
                {
                    await StopProcessesAsync().ConfigureAwait(false);
                    if (stopping)
                    {
                        // StopAsync during the start (window or solution closed): not a failure.
                        SetState(PreviewSessionState.Stopped, "The widget previewer was stopped while starting.");
                        throw new OperationCanceledException("The widget previewer was stopped while starting.", e);
                    }
                    LastError = e.Message;
                    SetState(PreviewSessionState.Failed, e.Message);
                    throw;
                }
            }
        }

        private async Task ResolveDependenciesAsync(CancellationToken token)
        {
            var reason = DartProject.UnresolvedReason(options.ProjectRoot);
            if (reason == null) return;
            if (!options.RunPubGet)
                throw new InvalidOperationException("The package's dependencies are not resolved (" + reason + "). Run Dart > Pub Get, or turn on Tools > Options > Dart > Widget preview: Run pub get.");
            SetState(PreviewSessionState.ResolvingDependencies, "Running flutter pub get (" + reason + ")...");
            var output = new StringBuilder();
            var project = DartProject.Find(options.ProjectRoot) ?? throw new InvalidOperationException("No pubspec.yaml in " + options.ProjectRoot);
            var code = await OwnedProcess.RunAsync(DartCommandFactory.PubGet(options.Sdk, project), line =>
            {
                lock (output) output.AppendLine(line);
                Emit("status", line);
            }, token).ConfigureAwait(false);
            if (code != 0) throw new InvalidOperationException("flutter pub get failed (exit code " + code + "):" + Environment.NewLine + Tail(output.ToString(), 20));
        }

        private async Task<Uri> StartDaemonAsync(CancellationToken token)
        {
            var process = Own(new OwnedProcess(DartCommandFactory.ToolingDaemon(options.Sdk, Path.GetTempPath())));
            daemon = process;
            process.Input.Close();
            var uri = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reader = new StreamReader(process.Output, Encoding.UTF8);
            _ = Task.Run(async () =>
            {
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (!uri.Task.IsCompleted && line.TrimStart().StartsWith("{", StringComparison.Ordinal))
                    {
                        try
                        {
                            var value = (string?)JObject.Parse(line)["tooling_daemon_details"]?["uri"];
                            if (value != null) { uri.TrySetResult(new Uri(value)); continue; }
                        }
                        catch (Exception e) when (e is JsonException || e is UriFormatException) { }
                    }
                    Emit("trace", "DTD: " + line);
                }
                uri.TrySetException(new InvalidOperationException("dart tooling-daemon exited before reporting its address."));
            });
            _ = PumpErrorsAsync(process, "DTD");
            using (token.Register(() => uri.TrySetCanceled()))
            {
                var finished = await Task.WhenAny(uri.Task, Task.Delay(TimeSpan.FromSeconds(60), token)).ConfigureAwait(false);
                if (finished != uri.Task) throw new TimeoutException("dart tooling-daemon did not report its address within 60 seconds.");
                var address = await uri.Task.ConfigureAwait(false);
                Emit("trace", "Dart Tooling Daemon: " + address.GetLeftPart(UriPartial.Authority));
                return address;
            }
        }

        private async Task StartAnalysisServerAsync(Uri dtd, CancellationToken token)
        {
            var process = Own(new OwnedProcess(DartCommandFactory.PreviewAnalysisServer(options.Sdk, options.ProjectRoot)));
            analysisServer = process;
            _ = PumpErrorsAsync(process, "Analysis server");
            analysis = new LspStdioClient(process.Input, process.Output, message => Emit("trace", message));
            analysis.Start();
            var root = new Uri(Path.GetFullPath(options.ProjectRoot).TrimEnd('\\') + "\\").AbsoluteUri.TrimEnd('/');
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                await analysis.RequestAsync("initialize", new JObject
                {
                    ["processId"] = System.Diagnostics.Process.GetCurrentProcess().Id,
                    ["clientInfo"] = new JObject { ["name"] = "Vistudio widget preview" },
                    ["rootUri"] = root,
                    ["workspaceFolders"] = new JArray(new JObject { ["name"] = Path.GetFileName(options.ProjectRoot.TrimEnd('\\', '/')), ["uri"] = root }),
                    ["capabilities"] = new JObject { ["window"] = new JObject { ["workDoneProgress"] = true } },
                }, timeout.Token).ConfigureAwait(false);
                await analysis.NotifyAsync("initialized", new JObject(), timeout.Token).ConfigureAwait(false);
                try { await analysis.RequestAsync("dart/connectToDtd", new JObject { ["uri"] = dtd.ToString() }, timeout.Token).ConfigureAwait(false); }
                catch (InvalidOperationException e)
                {
                    throw new InvalidOperationException("The Dart analysis server of this SDK cannot connect to the Dart Tooling Daemon (" + e.Message + "). Update Flutter ('flutter upgrade').", e);
                }
            }
        }

        /// <summary>The DTD methods the previewer calls on the analysis server ("Lsp" service).</summary>
        public static readonly IReadOnlyList<string> RequiredLspMethods = new[]
        {
            WidgetPreviewLsp.WorkspacePreviewsMethod, WidgetPreviewLsp.FilePreviewsMethod, "dart/workspace/analysis/complete",
        };

        /// <summary>
        /// Waits until the analysis server has registered the widget preview methods on the daemon. The server registers
        /// its "Lsp" methods in steps, the preview ones only after its first analysis (about 10 s on a warm cache, longer
        /// on a cold one), while the previewer gives up after 30 s; waiting here avoids that race.
        /// </summary>
        private async Task WaitForAnalysisServiceAsync(DtdClient client, CancellationToken token)
        {
            SetState(PreviewSessionState.Starting, "Waiting for the analysis server to analyze the package...");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<string> methods = Array.Empty<string>();
            while (watch.Elapsed < TimeSpan.FromMinutes(5))
            {
                var services = await client.GetRegisteredServicesAsync(token).ConfigureAwait(false);
                methods = services.TryGetValue("Lsp", out var registered) ? registered : Array.Empty<string>();
                if (RequiredLspMethods.All(methods.Contains))
                {
                    Emit("trace", "Analysis server ready on DTD after " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s.");
                    return;
                }
                if (analysisServer != null && analysisServer.Exit.IsCompleted)
                    throw new InvalidOperationException("The analysis server for the widget previewer exited (code " + analysisServer.Exit.Result + "). See Output > Dart.");
                await Task.Delay(500, token).ConfigureAwait(false);
            }
            // Older SDKs detect previews without the analysis server; let the previewer decide.
            Emit("warning", "The analysis server did not register the widget preview requests on DTD within 5 minutes (registered: "
                + (methods.Count == 0 ? "none" : string.Join(", ", methods.Where(m => m.StartsWith("dart/", StringComparison.Ordinal)))) + "). Starting the previewer anyway.");
        }

        private async Task StartPreviewerAsync(Uri dtd, CancellationToken token)
        {
            var process = Own(new OwnedProcess(DartCommandFactory.WidgetPreviewStart(options.Sdk, options.ProjectRoot, dtd.ToString(), options.Verbose)));
            previewer = process;
            process.Input.Close();
            var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var errors = new StringBuilder();
            var reader = new StreamReader(process.Output, Encoding.UTF8);
            var stdout = Task.Run(async () =>
            {
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) OnPreviewerLine(line, started, errors);
            });
            var stderr = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.Error.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    lock (errors) errors.AppendLine(line);
                    Emit("error", line);
                }
            });
            previewerExit = Task.Run(async () =>
            {
                var code = await process.Exit.ConfigureAwait(false);
                await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(2000)).ConfigureAwait(false);
                var expected = stopping;
                string message;
                lock (errors) message = errors.Length == 0 ? "" : Tail(errors.ToString(), 25);
                if (!started.Task.IsCompleted)
                    started.TrySetException(new InvalidOperationException(UnsupportedMessage(message) ?? ("flutter widget-preview start exited with code " + code + "." + (message.Length == 0 ? "" : Environment.NewLine + message))));
                else if (!expected)
                {
                    LastError = "The widget previewer exited (code " + code + ")." + (message.Length == 0 ? "" : Environment.NewLine + message);
                    SetState(PreviewSessionState.Failed, LastError);
                }
                Exited?.Invoke(code, expected);
            });
            using (token.Register(() => started.TrySetCanceled()))
            {
                var finished = await Task.WhenAny(started.Task, Task.Delay(options.StartTimeout, token)).ConfigureAwait(false);
                if (finished != started.Task) throw new TimeoutException("The widget previewer did not start within " + options.StartTimeout.TotalMinutes + " minutes. See Output > Dart.");
                Url = await started.Task.ConfigureAwait(false);
            }
            SetState(PreviewSessionState.Serving, "Previewer page: " + Url + " (connecting)...");
            Started?.Invoke(Url);
            _ = DiscoverPreviewServiceAsync();
        }

        /// <summary>Known previewer failures with a clearer explanation (older SDKs without the options, web disabled).</summary>
        public static string? UnsupportedMessage(string output)
        {
            if (output.IndexOf("Could not find a subcommand named \"widget-preview\"", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("Could not find a command named \"widget-preview\"", StringComparison.OrdinalIgnoreCase) >= 0)
                return "This Flutter SDK has no widget previewer (flutter widget-preview). Run 'flutter upgrade' to get Flutter " + WidgetPreviewSupport.Minimum.Major + "." + WidgetPreviewSupport.Minimum.Minor + " or newer.";
            if (output.IndexOf("Could not find an option named", StringComparison.OrdinalIgnoreCase) >= 0)
                return "This Flutter SDK's widget previewer does not support the options Vistudio needs (--machine, --web-server, --dtd-url). Run 'flutter upgrade'." + Environment.NewLine + output;
            if (output.IndexOf("requires Flutter Web to be enabled", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Widget previews need Flutter web support. Run 'flutter config --enable-web', then restart the preview.";
            return null;
        }

        private void OnPreviewerLine(string line, TaskCompletionSource<string> started, StringBuilder errors)
        {
            var e = PreviewerEvent.Parse(line);
            if (e == null)
            {
                if (line.Trim().Length > 0) Emit("status", line);
                return;
            }
            switch (e.Kind)
            {
                case PreviewerEventKind.Started:
                    if (!string.IsNullOrEmpty(e.Url)) started.TrySetResult(e.Url!);
                    return;
                case PreviewerEventKind.Initializing:
                    Emit("trace", "Widget previewer process " + e.Pid);
                    return;
                case PreviewerEventKind.Log:
                    var message = e.Message ?? "";
                    if (e.Level == "error") lock (errors) errors.AppendLine(message);
                    Emit(e.Level ?? "status", message);
                    var status = e.Status;
                    if (status == PreviewerStatus.None) return;
                    if (status == PreviewerStatus.Ready) SetState(PreviewSessionState.Ready, "Previews loaded.");
                    // One failed reload prints several lines (each log level, the proxy service): restart once per burst.
                    if (status == PreviewerStatus.ReloadFailed && DateTime.UtcNow - lastFallbackRestart > TimeSpan.FromSeconds(20))
                    {
                        lastFallbackRestart = DateTime.UtcNow;
                        _ = RestartAfterFailedReloadAsync(message);
                    }
                    Status?.Invoke(status, message);
                    if (status == PreviewerStatus.Ready || status == PreviewerStatus.Reloaded || status == PreviewerStatus.Restarted) _ = ReportPreviewProblemsAsync();
                    return;
                default:
                    Emit("trace", "Widget previewer event " + e.Name + ": " + e.Message);
                    return;
            }
        }

        private async Task ReportPreviewProblemsAsync()
        {
            try
            {
                var previews = await GetPreviewsAsync(null, lifetime.Token).ConfigureAwait(false);
                var problems = previews.Where(p => p.HasError || p.DependencyHasErrors).ToList();
                foreach (var problem in problems)
                    Emit("warning", "Preview " + problem.FunctionName + " (" + (problem.ScriptUri ?? "?") + ":" + (problem.Line + 1) + ") is not rendered: "
                        + (problem.HasError ? "its code has errors." : "a library it depends on has errors.") + " See the Error List.");
                PreviewProblems?.Invoke(problems);
            }
            catch (Exception e) when (e is DtdException || e is IOException || e is InvalidOperationException || e is OperationCanceledException) { }
        }

        private async Task RestartAfterFailedReloadAsync(string reason)
        {
            Emit("warning", "Hot reload was not possible (" + reason.Trim() + "); restarting the previews.");
            try { await HotRestartAsync().ConfigureAwait(false); }
            catch (Exception e) { Emit("error", "Hot restart failed: " + e.Message); }
        }

        /// <summary>Asks the previewer to hot restart (state is lost, all code reloaded) through its DTD service.</summary>
        public async Task HotRestartAsync()
        {
            var client = control ?? throw new InvalidOperationException("The widget previewer is not running.");
            var service = previewService ?? await DiscoverPreviewServiceAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("The widget previewer has not registered its DTD service yet.");
            await client.CallAsync(service + ".hotRestartPreviewer", new JObject(), lifetime.Token).ConfigureAwait(false);
        }

        /// <summary>The previews the analysis server finds (for a file, or the whole package), through the DTD Lsp service.</summary>
        public async Task<IReadOnlyList<LspWidgetPreview>> GetPreviewsAsync(string? file, CancellationToken token)
        {
            var client = control ?? throw new InvalidOperationException("The widget previewer is not running.");
            var result = file == null
                ? await client.CallAsync("Lsp." + WidgetPreviewLsp.WorkspacePreviewsMethod, new JObject(), token).ConfigureAwait(false)
                : await client.CallAsync("Lsp." + WidgetPreviewLsp.FilePreviewsMethod, new JObject { ["uri"] = new Uri(Path.GetFullPath(file)).AbsoluteUri }, token).ConfigureAwait(false);
            return WidgetPreviewLsp.ParsePreviews(result);
        }

        // The previewer registers "widget-preview[-uuid]" and posts on "WidgetPreviewScaffold[-uuid]" when a page connects.
        private async Task<string?> DiscoverPreviewServiceAsync()
        {
            var client = control;
            if (client == null) return null;
            for (var attempt = 0; attempt < 120 && !lifetime.IsCancellationRequested && previewService == null; attempt++)
            {
                try
                {
                    var services = await client.GetRegisteredServicesAsync(lifetime.Token).ConfigureAwait(false);
                    var name = services.Keys.FirstOrDefault(n => n == "widget-preview" || n.StartsWith("widget-preview-", StringComparison.Ordinal));
                    if (name != null)
                    {
                        previewService = name;
                        scaffoldStream = "WidgetPreviewScaffold" + name.Substring("widget-preview".Length);
                        await client.StreamListenAsync(scaffoldStream, lifetime.Token).ConfigureAwait(false);
                        Emit("trace", "Widget previewer DTD service: " + name);
                        break;
                    }
                }
                catch (Exception e) when (e is DtdException || e is IOException || e is InvalidOperationException) { }
                catch (OperationCanceledException) { return null; }
                try { await Task.Delay(500, lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
            }
            return previewService;
        }

        private void OnControlEvent(string stream, string kind, JObject data)
        {
            if (stream == scaffoldStream && kind == "Connected")
            {
                // A previewer page (one per configuration shown) connected. The daemon replays the Editor service
                // registration to it (checked by the live test), but events are not replayed: once the page has
                // subscribed to the Editor stream, post the current theme and active file again.
                Emit("trace", "Previewer page connected to DTD.");
                PageConnected?.Invoke();
                _ = RepostEditorStateAsync();
            }
        }

        private async Task RepostEditorStateAsync()
        {
            try
            {
                await Task.Delay(1500, lifetime.Token).ConfigureAwait(false);
                var client = editor;
                if (client != null && client.IsOpen) await PostEditorStateAsync(client, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Emit("trace", "Editor state: " + e.Message); }
        }

        /// <summary>(Re)registers the Editor service on a fresh connection, then posts the current file and theme.</summary>
        private async Task RegisterEditorAsync(CancellationToken token)
        {
            var uri = DtdUri ?? throw new InvalidOperationException("No DTD.");
            var client = await DtdClient.ConnectAsync(uri, token).ConfigureAwait(false);
            await client.RegisterServiceAsync(EditorService, "getActiveLocation", _ => Task.FromResult(ActiveLocationResponse()), null, token).ConfigureAwait(false);
            await client.RegisterServiceAsync(EditorService, "navigateToCode", parameters =>
            {
                var target = (string?)parameters["uri"];
                if (target != null && Uri.TryCreate(target, UriKind.Absolute, out var parsed) && parsed.IsFile)
                    NavigateRequested?.Invoke(parsed.LocalPath, (int?)parameters["line"] ?? 0, (int?)parameters["column"] ?? 0);
                return Task.FromResult(new JObject { ["type"] = "Success" });
            }, new JObject { ["supportedSchemes"] = new JArray("file") }, token).ConfigureAwait(false);
            DtdClient? previous;
            lock (gate) { previous = editor; editor = client; }
            previous?.Dispose();
            await PostEditorStateAsync(client, token).ConfigureAwait(false);
        }

        private JObject ActiveLocationResponse()
        {
            var location = activeLocation;
            var response = new JObject { ["type"] = "ActiveLocation", ["selections"] = location?["selections"] ?? new JArray() };
            if (location?["textDocument"] != null) response["textDocument"] = location["textDocument"];
            return response;
        }

        private async Task PostEditorStateAsync(DtdClient client, CancellationToken token)
        {
            if (theme != null) await client.PostEventAsync(EditorService, "themeChanged", new JObject { ["theme"] = theme }, token).ConfigureAwait(false);
            if (activeLocation != null) await client.PostEventAsync(EditorService, "activeLocationChanged", activeLocation, token).ConfigureAwait(false);
        }

        /// <summary>Tells the previewer which file is active, so it shows that file's previews (when its filter is on).</summary>
        public async Task SetActiveFileAsync(string path, int line, int character)
        {
            var position = new JObject { ["line"] = Math.Max(0, line), ["character"] = Math.Max(0, character) };
            activeLocation = new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = new Uri(Path.GetFullPath(path)).AbsoluteUri, ["version"] = Interlocked.Increment(ref documentVersion) },
                ["selections"] = new JArray(new JObject { ["active"] = position, ["anchor"] = position.DeepClone() }),
            };
            var client = editor;
            if (client == null || !client.IsOpen) return;
            try { await client.PostEventAsync(EditorService, "activeLocationChanged", activeLocation, lifetime.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is DtdException || e is IOException || e is OperationCanceledException || e is System.Net.WebSockets.WebSocketException) { }
        }

        /// <summary>Tells the previewer the IDE theme (its own UI follows it; previews follow the page's color scheme).</summary>
        public async Task SetThemeAsync(bool dark, string? background, string? foreground, int? fontSize)
        {
            theme = new JObject { ["isDarkMode"] = dark, ["backgroundColor"] = background, ["foregroundColor"] = foreground, ["fontSize"] = fontSize };
            var client = editor;
            if (client == null || !client.IsOpen) return;
            try { await client.PostEventAsync(EditorService, "themeChanged", new JObject { ["theme"] = theme }, lifetime.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is DtdException || e is IOException || e is OperationCanceledException || e is System.Net.WebSockets.WebSocketException) { }
        }

        /// <summary>Ends the previewer, the analysis server and the daemon, and waits until their process trees are gone.</summary>
        public async Task StopAsync()
        {
            if (stopping) { await StopProcessesAsync().ConfigureAwait(false); return; }
            stopping = true;
            lifetime.Cancel();
            await StopProcessesAsync().ConfigureAwait(false);
            if (previewerExit != null) await Task.WhenAny(previewerExit, Task.Delay(5000)).ConfigureAwait(false);
            if (state != PreviewSessionState.Failed) SetState(PreviewSessionState.Stopped, "Stopped.");
        }

        private async Task StopProcessesAsync()
        {
            DtdClient? a, b;
            lock (gate) { a = editor; b = control; editor = null; control = null; }
            a?.Dispose();
            b?.Dispose();
            analysis?.Dispose();
            OwnedProcess[] owned;
            lock (gate) owned = processes.ToArray();
            // The previewer first (it talks to the others), then the analysis server and the daemon.
            foreach (var process in owned.Reverse())
            {
                try { await process.StopAsync().ConfigureAwait(false); }
                catch (Exception e) when (e is IOException || e is InvalidOperationException || e is System.ComponentModel.Win32Exception) { Emit("warning", e.Message); }
            }
        }

        private OwnedProcess Own(OwnedProcess process)
        {
            lock (gate) processes.Add(process);
            return process;
        }

        private async Task PumpErrorsAsync(OwnedProcess process, string name)
        {
            try
            {
                string? line;
                while ((line = await process.Error.ReadLineAsync().ConfigureAwait(false)) != null)
                    if (line.Trim().Length > 0) Emit("trace", name + ": " + line);
            }
            catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is InvalidOperationException) { }
        }

        private void SetState(PreviewSessionState value, string message)
        {
            state = value;
            Emit(value == PreviewSessionState.Failed ? "error" : "status", message);
            StateChanged?.Invoke(value, message);
        }

        private void Emit(string level, string message)
        {
            try { Log?.Invoke(level, message); }
            catch (Exception) { }
        }

        private static string Tail(string text, int lines)
        {
            var all = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
        }

        public void Dispose()
        {
            if (!stopping) StopAsync().Wait(15000);
            lock (gate)
            {
                foreach (var process in processes) process.Dispose();
                processes.Clear();
            }
            lifetime.Dispose();
        }
    }
}
