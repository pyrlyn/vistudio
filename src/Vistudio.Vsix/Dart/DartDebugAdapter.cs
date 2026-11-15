using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Debugger.DebugAdapterHost.Interfaces;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vistudio.Core;
using Vistudio.Core.Dart;

namespace Vistudio.Dart
{
    /// <summary>
    /// Starts the SDK's DAP server for a Dart debug session (<c>dart debug_adapter</c> or <c>flutter debug_adapter</c>,
    /// with <c>--test</c> for tests) as the Debug Adapter Host's adapter process. Registered in
    /// <c>Registration/DartDebugAdapter.pkgdef</c>; the host maps DAP into breakpoints, call stack, locals, watch and stepping.
    /// </summary>
    [ComVisible(true), Guid("3d6f8a12-7b4c-4e59-9f0a-2c8e1b5d7a63")]
    public sealed class DartAdapterLauncher : IAdapterLauncher
    {
        public void Initialize(IDebugAdapterHostContext context) { }
        public void UpdateLaunchOptions(IAdapterLaunchInfo launchInfo) { }

        public ITargetHostProcess LaunchAdapter(IAdapterLaunchInfo launchInfo, ITargetHostInterop targetInterop)
        {
            var launch = JObject.Parse(launchInfo.LaunchJson);
            var (flutter, test, directory) = DartDebugLaunch.Adapter(launch);
            var sdk = DartToolchain.Require(DartSettings.Load());
            var start = DartCommandFactory.DebugAdapter(sdk, flutter, test, Directory.Exists(directory) ? directory : Path.GetTempPath());
            DartDebugSessions.Log?.Invoke("Starting the " + (flutter ? "Flutter" : "Dart") + (test ? " test" : "") + " debug adapter: " + start.FileName + " " + start.Arguments);
            return new DartAdapterProcess(start, flutter && !test, directory);
        }
    }

    /// <summary>The adapter process as the Debug Adapter Host sees it: a <see cref="DartDapConnection"/> plus the host contract.</summary>
    internal sealed class DartAdapterProcess : ITargetHostProcess
    {
        private readonly DartDapConnection connection;

        internal DartAdapterProcess(ProcessStartInfo start, bool hotReload, string root)
        {
            SupportsHotReload = hotReload;
            Root = root;
            connection = new DartDapConnection(start, ErrorLine);
            connection.EventReceived += WatchVmService;
            connection.Exited += () =>
            {
                DartDebugSessions.Remove(this);
                Exited?.Invoke(this, EventArgs.Empty);
            };
            DartDebugSessions.Add(this);
        }

        internal bool SupportsHotReload { get; }
        /// <summary>Package root of the debugged program (hot reload on save only for files of this package).</summary>
        internal string Root { get; }
        internal bool AppStarted => connection.AppStarted;

        /// <summary>
        /// Visual Studio's Diagnostic Tools refuses Debug Adapter Host engines (T71); the VM service the adapter announces
        /// gives Vistudio Diagnostics the program's process and heap instead.
        /// </summary>
        private void WatchVmService(JObject message)
        {
            if ((string?)message["event"] != "dart.debuggerUris" || !((string?)message["body"]?["vmServiceUri"] is string uri) || uri.Length == 0) return;
            DartDebugSessions.Log?.Invoke("Dart VM service: " + uri);
            Diagnostics.DiagnosticsSessions.WatchDartVm(uri, Path.GetFileName(Root.TrimEnd('\\', '/')));
        }

        public IntPtr Handle => IntPtr.Zero;
        public Stream StandardInput => connection.HostInput;
        public Stream StandardOutput => connection.HostOutput;
        public bool HasExited => connection.HasExited;
        public event EventHandler? Exited;
        public event DataReceivedEventHandler? ErrorDataReceived;

        private void ErrorLine(string line)
        {
            DartDebugSessions.Log?.Invoke("[debug adapter] " + line);
            if (ErrorDataReceived == null) return;
            try
            {
                // DataReceivedEventArgs has no public constructor; the Dart pane has the line either way.
                var args = (DataReceivedEventArgs)Activator.CreateInstance(typeof(DataReceivedEventArgs),
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new object[] { line }, null);
                ErrorDataReceived?.Invoke(this, args);
            }
            catch (Exception) { }
        }

        internal Task<JObject> RequestAsync(string command, JObject arguments, CancellationToken token) => connection.RequestAsync(command, arguments, token);

        public void Terminate()
        {
            if (connection.HasExited) return;
            Task.Run(connection.StopAsync).Forget();
        }
    }

    /// <summary>Running Dart debug sessions, for hot reload commands and hot reload on save.</summary>
    internal static class DartDebugSessions
    {
        private static readonly object Gate = new object();
        private static readonly List<DartAdapterProcess> Sessions = new List<DartAdapterProcess>();

        /// <summary>Writes to the Dart Output pane (set by <see cref="DartDebugCoordinator"/>).</summary>
        internal static Action<string>? Log { get; set; }

        internal static void Add(DartAdapterProcess session) { lock (Gate) Sessions.Add(session); }
        internal static void Remove(DartAdapterProcess session) { lock (Gate) Sessions.Remove(session); }

        internal static IReadOnlyList<DartAdapterProcess> Reloadable
        {
            get { lock (Gate) return Sessions.Where(s => s.SupportsHotReload && !s.HasExited).ToList(); }
        }

        /// <summary>Hot reload or hot restart in every Flutter debug session (of <paramref name="root"/>'s package when given); returns how many were reloaded.</summary>
        internal static async Task<int> ReloadAsync(bool fullRestart, string reason, string? root)
        {
            var targets = Reloadable.Where(s => s.AppStarted && (root == null || string.Equals(s.Root.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (var session in targets)
            {
                var watch = Stopwatch.StartNew();
                using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
                {
                    var response = await session.RequestAsync(fullRestart ? "hotRestart" : "hotReload", new JObject { ["reason"] = reason }, timeout.Token).ConfigureAwait(false);
                    var ok = (bool?)response["success"] == true;
                    Log?.Invoke((fullRestart ? "Hot restart" : "Hot reload") + " (debug session) " + (ok ? "done in " + watch.ElapsedMilliseconds + " ms." : "failed: " + (string?)response["message"]));
                }
            }
            return targets.Count;
        }
    }

}
