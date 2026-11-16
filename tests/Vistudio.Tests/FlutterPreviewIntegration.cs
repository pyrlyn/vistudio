using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Vistudio.Core;
using Vistudio.Core.Dart;
using Vistudio.Core.Dart.Preview;

/// <summary>
/// Live widget preview check on a copy of samples/flutter-preview with a real Flutter SDK (VISTUDIO_FLUTTER_SDK, the Dart
/// settings, or the SDK search): pub get, Vistudio's DTD and analysis server, flutter widget-preview start, the page
/// served over HTTP and opened in a headless Edge/Chrome (the IDE embeds the same page in WebView2), the Editor service,
/// hot reload after a source change, hot restart, the temporary host project for a widget without @Preview, and that
/// stopping leaves no process. Prints SKIP and succeeds when no suitable SDK or browser exists.
/// </summary>
internal static class FlutterPreviewIntegration
{
    private static readonly TimeSpan Ready = TimeSpan.FromMinutes(12);
    private static readonly TimeSpan Reload = TimeSpan.FromMinutes(3);

    /// <returns>False when skipped.</returns>
    internal static async Task<bool> RunAsync(string root, Action<bool, string> check, string stages)
    {
        var configured = Environment.GetEnvironmentVariable("VISTUDIO_FLUTTER_SDK");
        var settings = new DartSettings { FlutterSdkPath = configured ?? DartSettings.LoadGlobal().FlutterSdkPath };
        var lookup = DartToolchain.LocateFlutter(settings, FlutterSdkEnvironment.Current, null);
        var sdk = lookup.Location == null ? null : DartToolchain.Find(settings);
        if (sdk?.FlutterRoot == null)
        {
            Console.WriteLine("SKIP: no Flutter SDK (" + (lookup.Error ?? "not found") + ") - set VISTUDIO_FLUTTER_SDK or Tools > Options > Dart > Flutter SDK folder to run the widget preview integration test.");
            return false;
        }
        var version = DartToolchain.ReadFlutterVersion(sdk.FlutterRoot);
        var unsupported = WidgetPreviewSupport.Check(version);
        if (unsupported != null) { Console.WriteLine("SKIP: " + unsupported); return false; }
        var browser = FindBrowser();
        if (browser == null) { Console.WriteLine("SKIP: no Edge or Chrome to load the previewer page."); return false; }
        Console.WriteLine("Live widget preview: Flutter " + version + " (" + lookup.Location + "), browser " + browser);
        // See DartLiveTests: a UTF-8 console would put a BOM into every redirected stdin, which the analysis server rejects.
        if (Console.InputEncoding.GetPreamble().Length > 0) Console.InputEncoding = new UTF8Encoding(false);
        bool Stage(string name) => stages == "all" || stages.Split(',').Contains(name);

        var app = Path.Combine(root, "flutter preview", "flutter-preview");
        DartTests.CopySample("flutter-preview", app);
        var owned = new List<int>();
        if (Stage("dtd")) await DaemonAsync(sdk, root, check);
        if (Stage("preview")) await PreviewAsync(sdk, app, browser, root, check, owned);
        if (Stage("host")) await HostAsync(sdk, app, browser, root, check, owned);
        if (Stage("cancel")) await CancelWhileStartingAsync(sdk, app, check, owned);
        if (Stage("twopages")) await TwoPagesAsync(sdk, app, browser, root, check, owned);
        await Task.Delay(1000);
        var alive = owned.Distinct().Where(IsRunning).ToList();
        check(alive.Count == 0, "No process of any session is left (" + owned.Distinct().Count() + " owned processes checked)" + (alive.Count == 0 ? "" : ": " + string.Join(", ", alive.Select(Describe))));
        return true;
    }

    /// <summary>The DTD behavior the session relies on: service calls between clients, events, and whether registrations are replayed.</summary>
    private static async Task DaemonAsync(DartSdk sdk, string root, Action<bool, string> check)
    {
        using (var daemon = new OwnedProcess(DartCommandFactory.ToolingDaemon(sdk, root)))
        {
            var line = await daemon.Output.ReadLineAsync(TimeSpan.FromSeconds(60));
            var uri = new Uri((string)JObject.Parse(line!)["tooling_daemon_details"]!["uri"]!);
            check(uri.Scheme == "ws", "dart tooling-daemon reports its WebSocket address");
            using (var editor = await DtdClient.ConnectAsync(uri, CancellationToken.None))
            using (var page = await DtdClient.ConnectAsync(uri, CancellationToken.None))
            {
                await editor.RegisterServiceAsync("Editor", "getActiveLocation", _ => Task.FromResult(new JObject { ["type"] = "ActiveLocation", ["selections"] = new JArray() }), null, CancellationToken.None);
                var events = new ConcurrentQueue<string>();
                page.EventReceived += (stream, kind, data) => events.Enqueue(stream + "/" + kind + "/" + (string?)data["service"] + "." + (string?)data["method"]);
                await page.StreamListenAsync("Service", CancellationToken.None);
                await page.StreamListenAsync("Editor", CancellationToken.None);
                var answer = await page.CallAsync("Editor.getActiveLocation", new JObject(), CancellationToken.None);
                check((string?)answer["type"] == "ActiveLocation", "A DTD client calls a service registered by another client");
                await editor.PostEventAsync("Editor", "activeLocationChanged", new JObject { ["selections"] = new JArray() }, CancellationToken.None);
                await Until(() => events.Any(e => e.StartsWith("Editor/activeLocationChanged")), TimeSpan.FromSeconds(10));
                check(events.Any(e => e.StartsWith("Editor/activeLocationChanged")), "Editor events reach listeners");
                var replayed = events.Any(e => e.StartsWith("Service/ServiceRegistered/Editor.getActiveLocation"));
                check(replayed, "DTD replays earlier service registrations to a new Service listener (a previewer page that connects later still sees the Editor service)");
                await editor.RegisterServiceAsync("Editor", "navigateToCode", _ => Task.FromResult(new JObject { ["type"] = "Success" }), null, CancellationToken.None);
                await Until(() => events.Any(e => e.StartsWith("Service/ServiceRegistered/Editor.navigateToCode")), TimeSpan.FromSeconds(10));
                check(events.Any(e => e.StartsWith("Service/ServiceRegistered/Editor.navigateToCode")), "A registration after subscribing is announced on the Service stream");
                var services = await page.GetRegisteredServicesAsync(CancellationToken.None);
                check(services.TryGetValue("Editor", out var methods) && methods.Contains("getActiveLocation") && methods.Contains("navigateToCode"), "getRegisteredServices lists the Editor methods");
            }
            await AnalysisServerOnDaemonAsync(sdk, uri, root, check);
            await daemon.StopAsync();
        }
    }

    /// <summary>The previewer needs an analysis server that registered the "Lsp" service (widget preview requests) on the daemon.</summary>
    private static async Task AnalysisServerOnDaemonAsync(DartSdk sdk, Uri dtd, string root, Action<bool, string> check)
    {
        var app = Path.Combine(root, "flutter preview", "lsp-probe");
        DartTests.CopySample("flutter-preview", app);
        check(await OwnedProcess.RunAsync(DartCommandFactory.PubGet(sdk, DartProject.Find(app)!), _ => { }, CancellationToken.None) == 0, "flutter pub get for the analysis server probe");
        var errors = new ConcurrentQueue<string>();
        using (var server = new OwnedProcess(DartCommandFactory.PreviewAnalysisServer(sdk, app)))
        {
            _ = OwnedProcess.PumpAsync(server.Error, errors.Enqueue);
            var lsp = new LspStdioClient(server.Input, server.Output, message => errors.Enqueue(message));
            lsp.Start();
            var rootUri = new Uri(app + "\\").AbsoluteUri;
            var init = await lsp.RequestAsync("initialize", new JObject
            {
                ["processId"] = Process.GetCurrentProcess().Id, ["rootUri"] = rootUri,
                ["workspaceFolders"] = new JArray(new JObject { ["name"] = "app", ["uri"] = rootUri }),
                ["capabilities"] = new JObject { ["window"] = new JObject { ["workDoneProgress"] = true } },
            }, CancellationToken.None);
            check(init?["capabilities"] != null, "Preview analysis server initialized");
            await lsp.NotifyAsync("initialized", new JObject(), CancellationToken.None);
            var connected = await lsp.RequestAsync("dart/connectToDtd", new JObject { ["uri"] = dtd.ToString() }, CancellationToken.None);
            Console.WriteLine("  info dart/connectToDtd answered: " + (connected?.ToString(Newtonsoft.Json.Formatting.None) ?? "null"));
            using (var observer = await DtdClient.ConnectAsync(dtd, CancellationToken.None))
            {
                IReadOnlyDictionary<string, IReadOnlyList<string>> services = new Dictionary<string, IReadOnlyList<string>>();
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(60))
                {
                    services = await observer.GetRegisteredServicesAsync(CancellationToken.None);
                    if (services.TryGetValue("Lsp", out var registered) && registered.Contains(WidgetPreviewLsp.FilePreviewsMethod)) break;
                    await Task.Delay(500);
                }
                check(services.TryGetValue("Lsp", out var lsps) && lsps.Contains(WidgetPreviewLsp.FilePreviewsMethod),
                    "The analysis server registered the Lsp service on the daemon after " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s: " + string.Join("; ", services.Select(s => s.Key + "(" + string.Join(",", s.Value) + ")")));
                if (!(services.TryGetValue("Lsp", out var final) && final.Contains(WidgetPreviewLsp.FilePreviewsMethod)))
                {
                    Console.WriteLine("  info Lsp methods: " + (services.TryGetValue("Lsp", out var all) ? string.Join(", ", all) : "none"));
                    foreach (var line in errors.Take(40)) Console.WriteLine("  info server: " + line);
                }
            }
            await server.StopAsync();
        }
    }

    private static async Task PreviewAsync(DartSdk sdk, string app, string browserPath, string root, Action<bool, string> check, List<int> owned)
    {
        var log = new ConcurrentQueue<string>();
        var statuses = new ConcurrentQueue<PreviewerStatus>();
        var navigations = new ConcurrentQueue<string>();
        var session = new FlutterPreviewSession(new FlutterPreviewSessionOptions(sdk, app) { RunPubGet = true, Verbose = false });
        session.Log += (level, message) => { log.Enqueue(level + ": " + message); if (level != "trace") Console.WriteLine("    [" + level + "] " + Short(message)); };
        session.Status += (status, _) => statuses.Enqueue(status);
        session.NavigateRequested += (path, line, column) => navigations.Enqueue(path + ":" + line);
        var problems = new ConcurrentQueue<IReadOnlyList<LspWidgetPreview>>();
        session.PreviewProblems += list => problems.Enqueue(list);
        var exited = new TaskCompletionSource<(int, bool)>();
        session.Exited += (code, expected) => exited.TrySetResult((code, expected));
        var watch = Stopwatch.StartNew();
        OwnedProcess? browser = null;
        try
        {
            check(DartProject.UnresolvedReason(app) != null, "The fresh copy needs pub get");
            await session.StartAsync(CancellationToken.None);
            check(DartProject.UnresolvedReason(app) == null, "The session ran flutter pub get first");
            check(session.Url != null && session.Url.StartsWith("http://", StringComparison.Ordinal), "widget_preview.started reported the page: " + session.Url + " after " + watch.Elapsed.TotalSeconds.ToString("0") + " s");
            var page = await Download(session.Url!);
            check(page.Contains("flutter") && page.Contains("<html"), "The page is served over HTTP (the IDE embeds it in WebView2): " + page.Length + " characters");
            await session.SetThemeAsync(true, "#1E1E1E", "#DCDCDC", 13);
            await session.SetActiveFileAsync(Path.Combine(app, "lib", "widgets", "greeting_card.dart"), 21, 0);

            browser = StartBrowser(browserPath, session.Url!, Path.Combine(root, "browser profile"));
            await Until(() => session.State == PreviewSessionState.Ready || session.State == PreviewSessionState.Failed, Ready);
            check(session.State == PreviewSessionState.Ready, "The previewer loaded its previews in the page (\"Done loading previews\") after " + watch.Elapsed.TotalSeconds.ToString("0") + " s; state " + session.State + " " + session.LastError);
            owned.AddRange(session.ProcessIds());
            owned.AddRange(browser.TreeProcessIds());
            check(session.ProcessIds().Count >= 3, "The session owns the daemon, the analysis server and the previewer process trees: " + session.ProcessIds().Count + " processes");

            var file = Path.Combine(app, "lib", "widgets", "greeting_card.dart");
            var previews = await session.GetPreviewsAsync(file, CancellationToken.None);
            check(previews.Select(p => p.FunctionName).OrderBy(n => n).SequenceEqual(new[] { "darkGreetingPreview", "greetingCardPreview" }),
                "The analysis server on Vistudio's DTD reports the file's @Preview functions: " + string.Join(", ", previews.Select(p => p.FunctionName + " @" + p.Line)));
            check(previews.Any(p => p.FunctionName == "greetingCardPreview" && (p.Annotation ?? "").Contains("Greeting card")), "Preview annotations come with the answer");
            var scanned = WidgetPreviewScanner.Scan(File.ReadAllText(file)).Targets;
            check(previews.All(p => scanned.Any(t => t.Name == p.FunctionName && WidgetPreviewScanner.LineOf(File.ReadAllText(file), t.NameStart) == p.Line)),
                "Vistudio's own scanner finds the same previews on the same lines as the analysis server");

            using (var observer = await DtdClient.ConnectAsync(session.DtdUri!, CancellationToken.None))
            {
                var services = await observer.GetRegisteredServicesAsync(CancellationToken.None);
                check(services.TryGetValue("Editor", out var editor) && editor.Contains("getActiveLocation") && editor.Contains("navigateToCode"), "The session provides the DTD Editor service");
                check(services.Keys.Any(k => k.StartsWith("widget-preview", StringComparison.Ordinal)) && services.TryGetValue("Lsp", out var lsp) && lsp.Contains(WidgetPreviewLsp.FilePreviewsMethod),
                    "The previewer and the analysis server registered their DTD services: " + string.Join(", ", services.Keys));
                var location = await observer.CallAsync("Editor.getActiveLocation", new JObject(), CancellationToken.None);
                check(((string?)location["textDocument"]?["uri"] ?? "").EndsWith("greeting_card.dart", StringComparison.Ordinal), "Editor.getActiveLocation answers the active file");
                await observer.CallAsync("Editor.navigateToCode", new JObject { ["uri"] = new Uri(file).AbsoluteUri, ["line"] = 22, ["column"] = 1 }, CancellationToken.None);
                await Until(() => navigations.Count > 0, TimeSpan.FromSeconds(10));
                check(navigations.Any(n => n.EndsWith("greeting_card.dart:22", StringComparison.OrdinalIgnoreCase)), "Editor.navigateToCode reaches the IDE callback");
            }
            await Until(() => log.Any(l => l.Contains("Previewer page connected")), TimeSpan.FromSeconds(30));
            check(log.Any(l => l.Contains("Previewer page connected")), "The page connected to the DTD (scaffold Connected event); theme and active file are posted again for it");

            // Hot reload: a source change on disk (what a save does) reloads the page.
            while (statuses.TryDequeue(out _)) { }
            File.WriteAllText(file, File.ReadAllText(file).Replace("'Greeting card'", "'Greeting card (edited)'").Replace("'Hello, $name!'", "'Hi, $name!'"));
            watch.Restart();
            await Until(() => statuses.Contains(PreviewerStatus.Reloaded) || statuses.Contains(PreviewerStatus.Restarted), Reload);
            check(statuses.Contains(PreviewerStatus.ReloadStarted) && (statuses.Contains(PreviewerStatus.Reloaded) || statuses.Contains(PreviewerStatus.Restarted)),
                "After the change the previewer hot reloaded (" + string.Join(" > ", statuses) + ") in " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s");
            previews = await session.GetPreviewsAsync(file, CancellationToken.None);
            check(previews.Any(p => (p.Annotation ?? "").Contains("Greeting card (edited)")), "The analysis server sees the edited annotation");
            var generated = Directory.GetFiles(Path.Combine(app, ".widget_preview"), "*.dart", SearchOption.AllDirectories)
                .FirstOrDefault(f => File.ReadAllText(f).Contains("Greeting card (edited)"));
            check(generated != null, "The previewer regenerated its scaffold with the edited preview" + (generated == null ? "" : ": " + generated.Substring(app.Length)));

            // A code error: the previewer keeps running and leaves the broken library out (error card in the page); the
            // session reports the affected previews from the analysis server. Fixing it reloads them again.
            while (statuses.TryDequeue(out _)) { }
            while (problems.TryDequeue(out _)) { }
            var good = File.ReadAllText(file);
            File.WriteAllText(file, good.Replace("final String name;", "final String name"));
            await Until(() => problems.Any(list => list.Count > 0), Reload);
            var reported = problems.LastOrDefault(list => list.Count > 0) ?? Array.Empty<LspWidgetPreview>();
            check(reported.Count == 2 && reported.All(p => p.HasError && (p.ScriptUri ?? "").EndsWith("greeting_card.dart", StringComparison.Ordinal)),
                "A code error after a change is reported: both previews of the file are marked as not rendered (" + string.Join(", ", reported.Select(p => p.FunctionName)) + ")");
            check(log.Any(l => l.StartsWith("warning: Preview greetingCardPreview") && l.Contains("errors")), "The problem is written to the Output log");
            while (statuses.TryDequeue(out _)) { }
            while (problems.TryDequeue(out _)) { }
            File.WriteAllText(file, good);
            await Until(() => problems.Any(list => list.Count == 0), Reload);
            check(problems.Any(list => list.Count == 0) && (statuses.Contains(PreviewerStatus.Reloaded) || statuses.Contains(PreviewerStatus.Restarted)),
                "After the fix the previewer reloads and no preview has errors (" + string.Join(" > ", statuses) + ")");

            while (statuses.TryDequeue(out _)) { }
            await session.HotRestartAsync();
            await Until(() => statuses.Contains(PreviewerStatus.Restarted), Reload);
            check(statuses.Contains(PreviewerStatus.RestartStarted) && statuses.Contains(PreviewerStatus.Restarted), "Hot restart through the previewer's DTD service (" + string.Join(" > ", statuses) + ")");
            owned.AddRange(session.ProcessIds());
        }
        finally
        {
            var ids = session.ProcessIds().ToList();
            owned.AddRange(ids);
            watch.Restart();
            await session.StopAsync();
            var result = await Task.WhenAny(exited.Task, Task.Delay(10000));
            check(result == exited.Task && exited.Task.Result.Item2, "Stop ended the previewer as an expected exit");
            check(session.ProcessIds().Count == 0 && ids.All(id => !IsRunning(id)), "Stop left none of the " + ids.Count + " session processes running (" + watch.Elapsed.TotalSeconds.ToString("0.0") + " s)");
            if (browser != null) { owned.AddRange(browser.TreeProcessIds()); await browser.StopAsync(); browser.Dispose(); }
            session.Dispose();
        }
    }

    /// <summary>A widget without @Preview, previewed through the temporary host project (the user's package is not modified).</summary>
    private static async Task HostAsync(DartSdk sdk, string app, string browserPath, string root, Action<bool, string> check, List<int> owned)
    {
        var badge = Path.Combine(app, "lib", "widgets", "status_badge.dart");
        var before = Directory.GetFiles(Path.Combine(app, "lib"), "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText);
        var widget = WidgetPreviewScanner.Scan(File.ReadAllText(badge)).Widgets.Single(w => w.Name == "StatusBadge");
        check(!widget.HasPreview && widget.Construction == "const StatusBadge()", "StatusBadge has no @Preview and a const constructor");
        var host = new PreviewHostProject(app, "flutter_preview_sample", Path.Combine(root, "flutter preview", "host"));
        host.Add(new WrappedWidget(badge, widget.Name, widget.Construction!));
        host.Write(File.ReadAllText(Path.Combine(app, "pubspec.yaml")));
        var statuses = new ConcurrentQueue<PreviewerStatus>();
        var session = new FlutterPreviewSession(new FlutterPreviewSessionOptions(sdk, host.Folder));
        session.Log += (level, message) => { if (level != "trace") Console.WriteLine("    [host " + level + "] " + Short(message)); };
        session.Status += (status, _) => statuses.Enqueue(status);
        OwnedProcess? browser = null;
        try
        {
            await session.StartAsync(CancellationToken.None);
            browser = StartBrowser(browserPath, session.Url!, Path.Combine(root, "browser profile host"));
            await Until(() => session.State == PreviewSessionState.Ready || session.State == PreviewSessionState.Failed, Ready);
            check(session.State == PreviewSessionState.Ready, "The host project's previewer loaded (state " + session.State + " " + session.LastError + ")");
            owned.AddRange(session.ProcessIds());
            owned.AddRange(browser.TreeProcessIds());
            var previews = await session.GetPreviewsAsync(host.PreviewsPath, CancellationToken.None);
            check(previews.Count == 1 && previews[0].FunctionName == "vistudioPreview0" && !previews[0].HasError, "The generated wrapper is a valid preview of StatusBadge: " + string.Join(", ", previews.Select(p => p.FunctionName + (p.HasError ? " (error)" : ""))));

            // A change in the user's package, then Touch (what the IDE does on save), reloads the host's previewer.
            while (statuses.TryDequeue(out _)) { }
            File.WriteAllText(badge, File.ReadAllText(badge).Replace("'Online'", "'Available'"));
            host.Touch();
            await Until(() => statuses.Contains(PreviewerStatus.Reloaded) || statuses.Contains(PreviewerStatus.Restarted), Reload);
            check(statuses.Contains(PreviewerStatus.Reloaded) || statuses.Contains(PreviewerStatus.Restarted), "Editing the wrapped widget reloads the host previewer (" + string.Join(" > ", statuses) + ")");
            File.WriteAllText(badge, before[badge]);
            var after = Directory.GetFiles(Path.Combine(app, "lib"), "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText);
            check(after.Count == before.Count && after.All(kv => before.TryGetValue(kv.Key, out var text) && text == kv.Value), "The host project left the user's lib folder unchanged");
        }
        finally
        {
            var ids = session.ProcessIds().ToList();
            owned.AddRange(ids);
            await session.StopAsync();
            check(ids.All(id => !IsRunning(id)), "Stopping the host session left none of its " + ids.Count + " processes");
            if (browser != null) { owned.AddRange(browser.TreeProcessIds()); await browser.StopAsync(); browser.Dispose(); }
            session.Dispose();
        }
    }

    /// <summary>Closing the window (or the solution) during a start stops it at once instead of waiting minutes for it.</summary>
    private static async Task CancelWhileStartingAsync(DartSdk sdk, string app, Action<bool, string> check, List<int> owned)
    {
        var session = new FlutterPreviewSession(new FlutterPreviewSessionOptions(sdk, app));
        var start = session.StartAsync(CancellationToken.None);
        await Until(() => session.State == PreviewSessionState.Starting && session.ProcessIds().Count > 0, TimeSpan.FromMinutes(2));
        var ids = session.ProcessIds().ToList();
        owned.AddRange(ids);
        check(ids.Count > 0 && !start.IsCompleted, "A start is in progress (" + ids.Count + " processes)");
        var watch = Stopwatch.StartNew();
        await session.StopAsync();
        var finished = await Task.WhenAny(start, Task.Delay(30000));
        var canceled = finished == start && start.IsCanceled || start.Exception?.InnerException is OperationCanceledException;
        check(canceled && session.State == PreviewSessionState.Stopped, "StopAsync cancelled the start in " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s (state " + session.State + ", " + (start.Exception?.InnerException?.GetType().Name ?? start.Status.ToString()) + ")");
        await Task.Delay(500);
        check(ids.All(id => !IsRunning(id)), "No process of the cancelled start is left");
        session.Dispose();
    }

    /// <summary>Side-by-side configurations open the same previewer page several times: each page must start and follow reloads.</summary>
    private static async Task TwoPagesAsync(DartSdk sdk, string app, string browserPath, string root, Action<bool, string> check, List<int> owned)
    {
        var log = new ConcurrentQueue<string>();
        var statuses = new ConcurrentQueue<PreviewerStatus>();
        var session = new FlutterPreviewSession(new FlutterPreviewSessionOptions(sdk, app) { Verbose = true });
        session.Log += (level, message) => log.Enqueue(level + ": " + message);
        session.Status += (status, _) => statuses.Enqueue(status);
        OwnedProcess? first = null, second = null;
        try
        {
            await session.StartAsync(CancellationToken.None);
            first = StartBrowser(browserPath, session.Url!, Path.Combine(root, "browser profile a"));
            await Until(() => session.State == PreviewSessionState.Ready, Ready);
            check(session.State == PreviewSessionState.Ready, "First page loaded");
            int Connected() => log.Count(l => l.Contains("Previewer page connected to DTD."));
            await Until(() => Connected() >= 1, TimeSpan.FromSeconds(30));
            second = StartBrowser(browserPath, session.Url!, Path.Combine(root, "browser profile b"));
            await Until(() => Connected() >= 2, TimeSpan.FromMinutes(2));
            Console.WriteLine("  info pages connected: " + Connected());
            foreach (var line in log.Where(l => l.IndexOf("connect", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("client", StringComparison.OrdinalIgnoreCase) >= 0).Skip(0).Take(40))
                Console.WriteLine("  info " + Short(line));
            check(Connected() >= 2, "A second page of the same previewer starts its app (" + Connected() + " pages connected)");
            owned.AddRange(session.ProcessIds());
            owned.AddRange(first.TreeProcessIds());
            owned.AddRange(second.TreeProcessIds());
        }
        finally
        {
            owned.AddRange(session.ProcessIds());
            await session.StopAsync();
            foreach (var browser in new[] { first, second }) if (browser != null) { await browser.StopAsync(); browser.Dispose(); }
            session.Dispose();
        }
    }

    private static string? FindBrowser()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static OwnedProcess StartBrowser(string browser, string url, string profile)
    {
        Directory.CreateDirectory(profile);
        var start = ProcessCommands.CreateRedirected(browser, string.Join(" ", new[]
        {
            "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions",
            "--user-data-dir=" + DartArguments.Quote(profile), "--window-size=1280,900", DartArguments.Quote(url),
        }), profile);
        var process = new OwnedProcess(start);
        process.Input.Close();
        _ = OwnedProcess.PumpAsync(new StreamReader(process.Output), _ => { });
        _ = OwnedProcess.PumpAsync(process.Error, _ => { });
        return process;
    }

    private static async Task<string> Download(string url)
    {
        using (var client = new WebClient())
            return await client.DownloadStringTaskAsync(url);
    }

    private static async Task Until(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < timeout) await Task.Delay(250);
    }

    private static bool IsRunning(int id)
    {
        try { using (var process = Process.GetProcessById(id)) return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    private static string Describe(int id)
    {
        try { using (var process = Process.GetProcessById(id)) return id + " " + process.ProcessName; }
        catch (Exception) { return id.ToString(); }
    }

    private static string Short(string message) => message.Length > 240 ? message.Substring(0, 240) + "..." : message.Replace("\r", "").Replace("\n", " | ");
}

internal static class StreamTimeouts
{
    public static async Task<string?> ReadLineAsync(this Stream stream, TimeSpan timeout)
    {
        var reader = new StreamReader(stream, Encoding.UTF8);
        var read = reader.ReadLineAsync();
        return await Task.WhenAny(read, Task.Delay(timeout)) == read ? read.Result : null;
    }
}
