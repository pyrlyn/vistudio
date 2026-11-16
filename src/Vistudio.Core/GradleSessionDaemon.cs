using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Vistudio.Core
{
    /// <summary>
    /// The Gradle daemon of one Kotlin/JVM debug session. The Kotlin server's debugger stops waiting for the program's
    /// JVM 30 s after the launch request (a constant of the server), and a Gradle client that first has to start a JVM
    /// for the build can need longer than that. So the session warms a daemon up before the launch request, and the
    /// server's Gradle client finds it through <see cref="ClientArguments"/>. The daemon is registered in a folder of
    /// its own (no other Gradle client shares it), lives in a process job owned by this object, and ends with it,
    /// together with the program it started.
    /// </summary>
    public sealed class GradleSessionDaemon : IDisposable
    {
        public const int IdleTimeoutMilliseconds = 600000;
        private readonly ProcessJob job = new ProcessJob();
        private readonly object sync = new object();
        private string? warmJavaHome;
        private bool disposed;

        public GradleSessionDaemon(string registry)
        {
            Registry = Path.GetFullPath(registry);
            if (Regex.IsMatch(Registry, "[\"%!\r\n]")) throw new ArgumentException("The Gradle daemon registry path contains shell expansion characters.");
        }

        /// <summary>The folder the daemon registers in (<c>-Dorg.gradle.daemon.registry.base</c>).</summary>
        public string Registry { get; }

        /// <summary>
        /// The registry of a Gradle root under <c>%LOCALAPPDATA%\Vistudio\debug-daemon</c>, one per debug session: a
        /// session never meets a daemon another session is about to end.
        /// </summary>
        public static string RegistryFor(string gradleRoot)
        {
            var full = Path.GetFullPath(gradleRoot).TrimEnd('\\', '/');
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
                var name = new string(Path.GetFileName(full).ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
                return SettingsXml.PathFor(Path.Combine("debug-daemon", (name.Length == 0 ? "root" : name) + "-" + string.Concat(hash.Take(6).Select(b => b.ToString("x2"))),
                    Guid.NewGuid().ToString("N").Substring(0, 8)));
            }
        }

        /// <summary>
        /// Arguments for a Gradle client that is not started here and has to use this daemon: the Kotlin server's
        /// launch (<c>toolArgs</c>). One argument per element, unquoted.
        /// </summary>
        public IReadOnlyList<string> ClientArguments => new[]
        {
            "--daemon", "-Dorg.gradle.daemon.registry.base=" + Registry, "-Dorg.gradle.daemon.idletimeout=" + IdleTimeoutMilliseconds
        };

        /// <summary>
        /// Runs a Gradle command made for this daemon (<see cref="GradleCommand.Create"/> with the registry, or
        /// <see cref="GradleCommand.CreateDaemonWarmUp"/>) in the session's job, so the daemon it starts stays alive
        /// after the client has exited and ends with the session.
        /// </summary>
        public async Task<int> RunAsync(ProcessStartInfo start, Action<string> onOutput, Action<string> onError, CancellationToken token, Action<int>? started = null)
        {
            lock (sync) if (disposed) throw new ObjectDisposedException(nameof(GradleSessionDaemon));
            Directory.CreateDirectory(Registry);
            var code = await OwnedProcess.RunAsync(start, onOutput, onError, token, started, job).ConfigureAwait(false);
            // A failed build still leaves its daemon running; only a client that never reached Gradle does not.
            lock (sync) warmJavaHome = RunningDaemons().Count != 0 ? JavaHomeOf(start) : null;
            return code;
        }

        /// <summary>
        /// True when a daemon started here still runs and was started with this <c>JAVA_HOME</c> (empty: the one the
        /// process inherits). A Gradle client with another JDK would start a daemon of its own instead of using it.
        /// </summary>
        public bool IsWarmFor(string? javaHome)
        {
            // No JAVA_HOME of its own: the client inherits the one of this process, as the runs started here do.
            var effective = string.IsNullOrWhiteSpace(javaHome) ? Environment.GetEnvironmentVariable("JAVA_HOME") ?? "" : javaHome!;
            lock (sync)
                return !disposed && warmJavaHome != null && SameJavaHome(warmJavaHome, effective) && RunningDaemons().Count != 0;
        }

        /// <summary>
        /// Starts the daemon, or makes sure the one a build of this session started is the one the launch will get:
        /// runs Gradle's <c>help</c> task with the launch's environment unless <see cref="IsWarmFor"/> already holds.
        /// </summary>
        /// <param name="environment">The launch's environment variables (<c>JAVA_HOME</c> among them), applied to the client.</param>
        /// <returns>Gradle's exit code; 0 also when nothing had to run.</returns>
        public async Task<int> WarmUpAsync(string gradleRoot, IEnumerable<KeyValuePair<string, string>>? environment, Action<string> onLine, CancellationToken token)
        {
            var variables = (environment ?? Enumerable.Empty<KeyValuePair<string, string>>()).ToList();
            var javaHome = variables.LastOrDefault(v => string.Equals(v.Key, "JAVA_HOME", StringComparison.OrdinalIgnoreCase)).Value ?? "";
            if (IsWarmFor(javaHome)) return 0;
            var start = GradleCommand.CreateDaemonWarmUp(gradleRoot, javaHome, Registry);
            foreach (var variable in variables)
                if (!string.Equals(variable.Key, "JAVA_HOME", StringComparison.OrdinalIgnoreCase)) start.EnvironmentVariables[variable.Key] = variable.Value;
            return await RunAsync(start, onLine, onLine, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Process ids of the Gradle daemons registered in <see cref="Registry"/> that still run. Each daemon writes
        /// <c>daemon-&lt;pid&gt;.out.log</c> under <c>&lt;registry&gt;\&lt;gradle version&gt;</c> when it starts.
        /// </summary>
        public IReadOnlyList<int> RunningDaemons() => RunningDaemons(Registry);

        public static IReadOnlyList<int> RunningDaemons(string registry)
        {
            var running = new List<int>();
            string[] logs;
            try { logs = Directory.Exists(registry) ? Directory.GetFiles(registry, "daemon-*.out.log", SearchOption.AllDirectories) : new string[0]; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return running; }
            foreach (var log in logs)
            {
                var pid = DaemonPid(Path.GetFileName(log));
                if (pid == null) continue;
                try
                {
                    using (var process = Process.GetProcessById(pid.Value))
                        // The id of a daemon that is gone can belong to another process by now: the daemon is a JVM
                        // that started before it created its log.
                        if (string.Equals(process.ProcessName, "java", StringComparison.OrdinalIgnoreCase) &&
                            process.StartTime.ToUniversalTime() <= File.GetCreationTimeUtc(log).AddSeconds(5)) running.Add(pid.Value);
                }
                catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is System.ComponentModel.Win32Exception || e is IOException) { }
            }
            return running;
        }

        /// <summary>The process id in a daemon log's file name (<c>daemon-1234.out.log</c>); null for any other name.</summary>
        public static int? DaemonPid(string logFileName)
        {
            var match = Regex.Match(logFileName, @"\Adaemon-(\d{1,9})\.out\.log\z", RegexOptions.CultureInvariant);
            return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : (int?)null;
        }

        private static string JavaHomeOf(ProcessStartInfo start) =>
            start.EnvironmentVariables.ContainsKey("JAVA_HOME") ? start.EnvironmentVariables["JAVA_HOME"] ?? "" : "";

        private static bool SameJavaHome(string first, string second)
        {
            string Normal(string path) => path.Trim().Replace('/', '\\').TrimEnd('\\');
            return string.Equals(Normal(first), Normal(second), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Ends the session's Gradle daemons and everything they started (the program, the Kotlin compiler daemon).
        /// A daemon the Kotlin server's own Gradle client started in the registry is not in the job; it is ended with
        /// its process tree. The registry folder is removed.
        /// </summary>
        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
            }
            var members = new HashSet<int>(job.ProcessIds());
            try { job.Terminate(); }
            catch (System.ComponentModel.Win32Exception) { }
            job.Dispose();
            foreach (var pid in RunningDaemons().Where(p => !members.Contains(p))) EndTree(pid);
            for (var attempt = 0; attempt < 10 && Directory.Exists(Registry); attempt++)
            {
                // The daemon's log and registry files are released a moment after its process has ended.
                try { Directory.Delete(Registry, true); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { Thread.Sleep(200); }
            }
            // The folder above holds one registry per session of this Gradle root; it goes with the last of them.
            try { var parent = Path.GetDirectoryName(Registry); if (parent != null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        /// <summary>
        /// The JVMs among <c>jps -l</c> rows that run <paramref name="mainClass"/> and started while a debug session
        /// lasted: what the session left behind when they still run after it (a launch the debugger never attached to
        /// leaves its JVM waiting for one). A JVM of an earlier or a later session of the same class is not listed.
        /// </summary>
        public static IReadOnlyList<int> ProgramJvms(string jpsOutput, string mainClass, DateTime sessionStartUtc, DateTime sessionEndUtc, Func<int, DateTime?> startTimeUtc)
        {
            var found = new List<int>();
            foreach (var jvm in Diagnostics.JvmTools.ParseJps(jpsOutput))
            {
                if (!Diagnostics.JvmTools.IsMain(jvm.Main, mainClass)) continue;
                var start = startTimeUtc(jvm.Pid);
                // Two seconds of slack before, as for hot reload: the session start is taken just before the launch request.
                if (start != null && start.Value >= sessionStartUtc.AddSeconds(-2) && start.Value <= sessionEndUtc) found.Add(jvm.Pid);
            }
            return found;
        }

        /// <summary>Ends a process and its descendants (<c>taskkill /T /F</c>); a process that is already gone is not an error.</summary>
        public static void EndTree(int pid)
        {
            try
            {
                using (var killer = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), "/PID " + pid + " /T /F")
                { UseShellExecute = false, CreateNoWindow = true }))
                    killer?.WaitForExit(10000);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException) { }
        }
    }
}
