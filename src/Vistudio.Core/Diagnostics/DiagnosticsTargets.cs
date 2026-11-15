using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Vistudio.Core.Diagnostics
{
    /// <summary>Builds a <see cref="DiagnosticsMonitor"/> for each kind of debuggee Vistudio starts.</summary>
    public static class DiagnosticsTargets
    {
        /// <summary>JVMs running now, so that the program's JVM can be told apart from them once it starts.</summary>
        public static async Task<ISet<int>> RunningJvmsAsync(string jps, CancellationToken token) =>
            new HashSet<int>(JvmTools.ParseJps(await JvmTools.RunAsync(jps, "-l", token).ConfigureAwait(false)).Select(j => j.Pid));

        /// <summary>
        /// Waits for the JVM running <paramref name="mainClass"/> (the Kotlin server starts it through Gradle, so the first
        /// launch can include a build) and watches it with jstat; jstat and jcmd come from the program's own JDK.
        /// </summary>
        public static async Task<DiagnosticsMonitor> ForJvmAsync(string jps, string mainClass, ISet<int> before, string? javaHome, TimeSpan timeout, CancellationToken token)
        {
            var pid = await JvmTools.FindJvmAsync(t => JvmTools.RunAsync(jps, "-l", t), mainClass, before, timeout, token).ConfigureAwait(false);
            var java = Win32ProcessCounters.ImagePath(pid);
            var jstat = JvmTools.FindTool("jstat", java, javaHome) ?? JvmTools.FindTool("jstat", jps, null);
            var jcmd = JvmTools.FindTool("jcmd", java, javaHome) ?? JvmTools.FindTool("jcmd", jps, null);
            var monitor = new DiagnosticsMonitor(mainClass + " (JVM, pid " + pid + ")", () => new[] { pid }, new JvmProbe(pid, jstat, jcmd));
            if (jstat == null) monitor.Note(DiagnosticsEventKind.Note, "jstat was not found (a JRE has none); only process counters are shown.");
            monitor.Start("JVM " + pid + " running " + mainClass + (java == null ? "" : " (" + java + ")"));
            return monitor;
        }

        /// <summary>Watches the program behind a Dart VM service: its process from <c>getVM</c>, its heap from <c>getMemoryUsage</c>.</summary>
        public static async Task<DiagnosticsMonitor> ForDartVmAsync(string vmServiceUri, string title, CancellationToken token)
        {
            var probe = new DartVmProbe(vmServiceUri);
            try
            {
                int pid;
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    limit.CancelAfter(TimeSpan.FromSeconds(20));
                    pid = await probe.ProcessIdAsync(limit.Token).ConfigureAwait(false);
                }
                var image = Win32ProcessCounters.ImagePath(pid);
                var monitor = new DiagnosticsMonitor(title + " (Dart VM, pid " + pid + ")", () => new[] { pid }, probe);
                monitor.Start("Dart VM " + pid + (image == null ? "" : " (" + Path.GetFileName(image) + ")") + ", VM service " + vmServiceUri);
                return monitor;
            }
            catch
            {
                probe.Dispose();
                throw;
            }
        }

        /// <summary>
        /// A program that a Gradle run task starts (Kotlin JS, wasmJs or Native target): sampling begins once it appears
        /// under <paramref name="gradle"/>, after the build part of the run.
        /// </summary>
        public static DiagnosticsMonitor ForProgramUnderGradle(int gradle, string title)
        {
            var monitor = new DiagnosticsMonitor(title + " (under Gradle pid " + gradle + ")", () => Win32ProcessCounters.Instance.ProgramUnderGradle(gradle), null);
            monitor.Start("Waiting for the program that Gradle " + gradle + " builds and starts (Gradle's own processes are not counted)");
            return monitor;
        }

        /// <summary>A program without a runtime protocol (WebAssembly under wasmtime): the started process and its children.</summary>
        public static DiagnosticsMonitor ForProcessTree(int root, string title)
        {
            var monitor = new DiagnosticsMonitor(title + " (pid " + root + ")", () => Win32ProcessCounters.Instance.Tree(root), null);
            monitor.Start("Process " + root + " and the processes it starts");
            return monitor;
        }
    }
}
