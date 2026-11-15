using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Vistudio.Core.Diagnostics
{
    /// <summary>A language runtime that reports its own heap and can list objects by class.</summary>
    public interface IRuntimeProbe : IDisposable
    {
        string Name { get; }
        /// <summary>The latest heap figures, or null when none are available yet.</summary>
        Task<RuntimeHeap?> ReadAsync(CancellationToken token);
        Task<HeapSnapshot> SnapshotAsync(CancellationToken token);
    }

    /// <summary>
    /// Output of the JDK's own monitoring tools. They read the JVM's performance data or use the attach mechanism, so
    /// they answer while JDWP holds every Java thread suspended at a breakpoint.
    /// </summary>
    public static class JvmTools
    {
        public const string Runtime = "JVM heap";

        /// <summary><c>jps -l</c> lines: "pid main-class-or-jar"; a JVM still starting (or another user's) shows a placeholder.</summary>
        public static IReadOnlyList<(int Pid, string Main)> ParseJps(string output)
        {
            var result = new List<(int, string)>();
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                var space = line.IndexOf(' ');
                var pidText = space < 0 ? line : line.Substring(0, space);
                if (!int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
                result.Add((pid, space < 0 ? "" : line.Substring(space + 1).Trim()));
            }
            return result;
        }

        /// <summary>True when a <c>jps -l</c> main entry names <paramref name="mainClass"/>.</summary>
        public static bool IsMain(string main, string mainClass) =>
            string.Equals(main, mainClass, StringComparison.Ordinal);

        /// <summary>
        /// One <c>jstat -gc</c> data row read against its header (columns are named, not positional: JDK 8 has no CGC
        /// columns). Capacities and usage are in KB; "-" stands for a time not measured yet.
        /// </summary>
        public static RuntimeHeap? ParseJstatGc(string header, string row)
        {
            var names = header.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var values = row.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (names.Length == 0 || names.Length != values.Length) return null;
            var map = new Dictionary<string, double>(StringComparer.Ordinal);
            for (var i = 0; i < names.Length; i++)
            {
                if (values[i] == "-") { map[names[i]] = 0; continue; }
                if (!double.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
                map[names[i]] = value;
            }
            double Get(string name) => map.TryGetValue(name, out var v) ? v : 0;
            if (!map.ContainsKey("EU") || !map.ContainsKey("OU")) return null;
            var used = Get("S0U") + Get("S1U") + Get("EU") + Get("OU");
            var capacity = Get("S0C") + Get("S1C") + Get("EC") + Get("OC");
            var collections = (long)(Get("YGC") + Get("FGC"));
            return new RuntimeHeap(Runtime, (long)(used * 1024), (long)(capacity * 1024), collections, Get("GCT"));
        }

        private static readonly Regex HistogramRow = new Regex(@"^\s*\d+:\s+(\d+)\s+(\d+)\s+(.+?)\s*$", RegexOptions.Compiled);
        private static readonly Regex HistogramTotal = new Regex(@"^\s*Total\s+(\d+)\s+(\d+)\s*$", RegexOptions.Compiled);
        private static readonly Regex ModuleSuffix = new Regex(@"\s+\([^()]*\)$", RegexOptions.Compiled);

        /// <summary><c>jcmd &lt;pid&gt; GC.class_histogram</c>: one row per class, module suffix removed.</summary>
        public static HeapSnapshot ParseClassHistogram(string output, DateTime time)
        {
            var classes = new List<HeapClassEntry>();
            long? totalBytes = null, totalInstances = null;
            foreach (var line in output.Split('\n'))
            {
                var row = HistogramRow.Match(line);
                if (row.Success)
                {
                    classes.Add(new HeapClassEntry(ModuleSuffix.Replace(row.Groups[3].Value, ""),
                        long.Parse(row.Groups[1].Value, CultureInfo.InvariantCulture), long.Parse(row.Groups[2].Value, CultureInfo.InvariantCulture)));
                    continue;
                }
                var total = HistogramTotal.Match(line);
                if (total.Success)
                {
                    totalInstances = long.Parse(total.Groups[1].Value, CultureInfo.InvariantCulture);
                    totalBytes = long.Parse(total.Groups[2].Value, CultureInfo.InvariantCulture);
                }
            }
            if (classes.Count == 0) throw new InvalidDataException("jcmd GC.class_histogram returned no classes: " + FirstLine(output));
            return new HeapSnapshot(time, "jcmd GC.class_histogram", classes, totalBytes, totalInstances);
        }

        /// <summary>
        /// A JDK tool (<c>jps</c>, <c>jstat</c>, <c>jcmd</c>): first next to the debuggee's <c>java.exe</c> (same JDK as the
        /// program), then in the configured JDK, JAVA_HOME and PATH. Null when no JDK has it (a JRE has none).
        /// </summary>
        public static string? FindTool(string tool, string? javaExecutable, string? javaHome, Func<string, string?>? environment = null, Func<string, bool>? exists = null)
        {
            environment ??= Environment.GetEnvironmentVariable;
            exists ??= File.Exists;
            var folders = new List<string>();
            if (!string.IsNullOrWhiteSpace(javaExecutable)) folders.Add(Path.GetDirectoryName(javaExecutable!)!);
            if (!string.IsNullOrWhiteSpace(javaHome)) folders.Add(Path.Combine(javaHome!, "bin"));
            var home = environment("JAVA_HOME");
            if (!string.IsNullOrWhiteSpace(home)) folders.Add(Path.Combine(home!, "bin"));
            folders.AddRange((environment("PATH") ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim().Trim('"')));
            foreach (var folder in folders)
            {
                try
                {
                    var candidate = Path.Combine(folder, tool + ".exe");
                    if (exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        /// <summary>
        /// Waits for the program's JVM: a process that <c>jps -l</c> lists with <paramref name="mainClass"/> and that was not
        /// running before the launch (Gradle and the Kotlin server are JVMs too, and so may be an earlier run).
        /// </summary>
        public static async Task<int> FindJvmAsync(Func<CancellationToken, Task<string>> jps, string mainClass, ISet<int> before, TimeSpan timeout, CancellationToken token)
        {
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                limit.CancelAfter(timeout);
                while (true)
                {
                    try
                    {
                        var found = ParseJps(await jps(limit.Token).ConfigureAwait(false)).Where(j => !before.Contains(j.Pid) && IsMain(j.Main, mainClass)).ToList();
                        if (found.Count != 0) return found.Max(j => j.Pid);
                        await Task.Delay(500, limit.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        throw new TimeoutException("No JVM running " + mainClass + " appeared within " + (int)timeout.TotalSeconds + " s.");
                    }
                }
            }
        }

        /// <summary>Runs a JDK tool to completion and returns its standard output.</summary>
        public static async Task<string> RunAsync(string tool, string arguments, CancellationToken token)
        {
            var output = new System.Text.StringBuilder();
            var errors = new System.Text.StringBuilder();
            var exit = await OwnedProcess.RunAsync(Start(tool, arguments), line => output.AppendLine(line), line => errors.AppendLine(line), token).ConfigureAwait(false);
            if (exit != 0) throw new InvalidOperationException(Path.GetFileName(tool) + " " + arguments + " failed (" + exit + "): " + FirstLine(errors.ToString() + output));
            return output.ToString();
        }

        internal static ProcessStartInfo Start(string tool, string arguments) => new ProcessStartInfo(tool, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };

        private static string FirstLine(string text) => text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length != 0) ?? "";
    }

    /// <summary>
    /// Heap and garbage collection figures of one JVM from a single long-running <c>jstat -gc &lt;pid&gt; 1000</c>
    /// (one process for the whole session instead of one per sample); snapshots run <c>jcmd GC.class_histogram</c>.
    /// </summary>
    public sealed class JvmProbe : IRuntimeProbe
    {
        private readonly int pid;
        private readonly string? jcmd;
        private readonly OwnedProcess? jstat;
        private string? header;
        private RuntimeHeap? latest;

        public JvmProbe(int pid, string? jstatPath, string? jcmdPath)
        {
            this.pid = pid;
            jcmd = jcmdPath;
            if (jstatPath == null) return;
            jstat = new OwnedProcess(JvmTools.Start(jstatPath, "-gc " + pid.ToString(CultureInfo.InvariantCulture) + " 1000"));
            jstat.Input.Close();
            _ = Task.Run(() => OwnedProcess.PumpAsync(new StreamReader(jstat.Output), Accept));
            _ = Task.Run(() => OwnedProcess.PumpAsync(jstat.Error, _ => { }));
        }

        public string Name => "JVM";

        private void Accept(string line)
        {
            if (line.TrimStart().StartsWith("S0C", StringComparison.Ordinal)) { header = line; return; }
            if (header != null && JvmTools.ParseJstatGc(header, line) is RuntimeHeap heap) latest = heap;
        }

        public Task<RuntimeHeap?> ReadAsync(CancellationToken token) => Task.FromResult(latest);

        public async Task<HeapSnapshot> SnapshotAsync(CancellationToken token)
        {
            if (jcmd == null) throw new InvalidOperationException("jcmd was not found next to the program's java.exe, in the configured JDK, JAVA_HOME or PATH.");
            var output = await JvmTools.RunAsync(jcmd, pid.ToString(CultureInfo.InvariantCulture) + " GC.class_histogram", token).ConfigureAwait(false);
            return JvmTools.ParseClassHistogram(output, DateTime.Now);
        }

        public void Dispose()
        {
            if (jstat == null) return;
            try { jstat.StopAsync().Wait(5000); } catch (Exception) { }
            jstat.Dispose();
        }
    }
}
