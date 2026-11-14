using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vistudio.Core;
using Vistudio.Core.Dart;

#pragma warning disable VSTHRD003 // The process and pump tasks of this session have no UI-thread dependency.
namespace Vistudio.Dart
{
    /// <summary>
    /// One <c>flutter run --machine</c> process: follows the daemon events (app id, started, logs, progress, stop) and
    /// sends hot reload, hot restart and stop requests on stdin. Plain output lines go through the compiler error parser.
    /// No Visual Studio dependencies besides logging callbacks.
    /// </summary>
    internal sealed class FlutterAppSession : IDisposable
    {
        private readonly OwnedProcess process;
        private readonly StreamWriter input;
        private readonly Action<string> log;
        private readonly DartDiagnosticParser parser;
        private readonly object gate = new object();
        private readonly Dictionary<int, TaskCompletionSource<FlutterDaemonMessage>> pending = new Dictionary<int, TaskCompletionSource<FlutterDaemonMessage>>();
        private readonly TaskCompletionSource<string> started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task pump;
        private readonly Task errors;
        private int nextId;

        internal FlutterAppSession(DartProject project, ProcessStartInfo start, Action<string> log)
        {
            Project = project;
            this.log = log;
            parser = new DartDiagnosticParser(project.Root);
            process = new OwnedProcess(start);
            input = new StreamWriter(process.Input, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            pump = Task.Run(ReadAsync);
            errors = OwnedProcess.PumpAsync(process.Error, line => Accept(line));
        }

        internal DartProject Project { get; }
        internal string? AppId { get; private set; }
        internal string? DeviceId { get; private set; }
        internal bool SupportsRestart { get; private set; } = true;
        /// <summary>Completes with the app id when the app runs on the device (<c>app.started</c>); faults when the process exits first.</summary>
        internal Task<string> Started => started.Task;
        internal Task<int> Exit => process.Exit;
        internal bool HasExited => process.Exit.IsCompleted;
        internal IReadOnlyList<DartDiagnostic> Diagnostics { get { lock (gate) return new List<DartDiagnostic>(parser.Diagnostics); } }
        /// <summary>Raised when new compiler errors were parsed (for the Error List).</summary>
        internal event Action? DiagnosticsChanged;

        private async Task ReadAsync()
        {
            using (var reader = new StreamReader(process.Output, Encoding.UTF8))
            {
                string? line;
                try { while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) Handle(line); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
            var code = await process.Exit.ConfigureAwait(false);
            started.TrySetException(new InvalidOperationException("flutter run exited with code " + code + " before the app started."));
            lock (gate)
            {
                foreach (var request in pending.Values) request.TrySetException(new InvalidOperationException("flutter run has exited."));
                pending.Clear();
            }
        }

        private void Handle(string line)
        {
            var message = FlutterDaemonMessage.Parse(line);
            if (message == null) { Accept(line); return; }
            if (message.IsResponse)
            {
                TaskCompletionSource<FlutterDaemonMessage>? request;
                lock (gate) { if (pending.TryGetValue(message.Id!.Value, out request)) pending.Remove(message.Id.Value); }
                request?.TrySetResult(message);
                return;
            }
            switch (message.Event)
            {
                case "app.start":
                    AppId = message.AppId;
                    DeviceId = (string?)message.Params["deviceId"];
                    SupportsRestart = (bool?)message.Params["supportsRestart"] ?? true;
                    log("Flutter app " + AppId + " starting on " + DeviceId + ".");
                    break;
                case "app.started":
                    log("Flutter app started. Hot reload with Tools > Dart > Flutter: Hot Reload" + (DartSettings.Load().HotReloadOnSave ? " or by saving a .dart file." : "."));
                    started.TrySetResult(AppId ?? message.AppId ?? "");
                    break;
                case "app.debugPort":
                    log("Dart VM service: " + (string?)message.Params["wsUri"]);
                    break;
                case "app.log":
                    foreach (var text in ((string?)message.Params["log"] ?? "").Split('\n')) Accept(text.TrimEnd('\r'));
                    break;
                case "app.progress":
                    var progress = (string?)message.Params["message"];
                    if (!string.IsNullOrEmpty(progress) && (bool?)message.Params["finished"] != true) log(progress!);
                    break;
                case "daemon.logMessage":
                    var level = (string?)message.Params["level"];
                    log((level == "error" ? "error: " : "") + (string?)message.Params["message"]);
                    break;
                case "app.webLaunchUrl":
                    log("Web app: " + (string?)message.Params["url"]);
                    break;
                case "app.stop":
                    var error = (string?)message.Params["error"];
                    log("Flutter app stopped" + (string.IsNullOrEmpty(error) ? "." : ": " + error));
                    break;
            }
        }

        private void Accept(string line)
        {
            string shown;
            int before, after;
            lock (gate)
            {
                before = parser.Diagnostics.Count;
                shown = parser.Accept(line);
                after = parser.Diagnostics.Count;
            }
            log(shown);
            if (after != before) DiagnosticsChanged?.Invoke();
        }

        /// <summary>Hot reload (keeps state) or hot restart; returns the daemon's message, throws when it reports a failure.</summary>
        internal async Task<string> RestartAsync(bool fullRestart, string reason, CancellationToken token)
        {
            var appId = AppId ?? throw new InvalidOperationException("The Flutter app has not started yet.");
            if (!started.Task.IsCompleted) throw new InvalidOperationException("The Flutter app is still starting.");
            if (!SupportsRestart) throw new InvalidOperationException("This Flutter app (device " + DeviceId + ", mode) does not support hot reload.");
            lock (gate) parser.Clear();
            var response = await SendAsync(id => FlutterDaemon.Restart(id, appId, fullRestart, reason), token).ConfigureAwait(false);
            if (!response.Succeeded(out var message)) throw new InvalidOperationException((fullRestart ? "Hot restart" : "Hot reload") + " failed" + (message.Length == 0 ? "." : ": " + message));
            DiagnosticsChanged?.Invoke();
            return message;
        }

        private async Task<FlutterDaemonMessage> SendAsync(Func<int, string> create, CancellationToken token)
        {
            var completion = new TaskCompletionSource<FlutterDaemonMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            int id;
            lock (gate) { id = ++nextId; pending[id] = completion; }
            try
            {
                await input.WriteLineAsync(create(id)).ConfigureAwait(false);
            }
            catch (IOException e) { lock (gate) pending.Remove(id); throw new InvalidOperationException("flutter run is not accepting requests: " + e.Message); }
            using (token.Register(() => completion.TrySetCanceled()))
                return await completion.Task.ConfigureAwait(false);
        }

        /// <summary>Asks the app to stop (<c>app.stop</c>), then ends the process tree if it does not exit within a few seconds.</summary>
        internal async Task StopAsync()
        {
            if (HasExited) return;
            if (AppId != null)
            {
                try
                {
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                        await SendAsync(id => FlutterDaemon.Stop(id, AppId), timeout.Token).ConfigureAwait(false);
                }
                catch (Exception) { }
                await Task.WhenAny(process.Exit, Task.Delay(5000)).ConfigureAwait(false);
            }
            await process.StopAsync().ConfigureAwait(false);
            try { await Task.WhenAll(pump, errors).ConfigureAwait(false); }
            catch (Exception) { }
        }

        public void Dispose()
        {
            input.Dispose();
            if (process.Exit.IsCompleted) process.Dispose(); // see DartDapConnection.Dispose
        }
    }
}
