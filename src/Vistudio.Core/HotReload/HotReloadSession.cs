using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vistudio.Core.Diagnostics;

namespace Vistudio.Core.HotReload
{
    /// <summary>Where one program's reloads compile and keep their files.</summary>
    public sealed class HotReloadTarget
    {
        public HotReloadTarget(string gradleRoot, string javaHome, string entryFile, string script, string agentSource, string workDirectory)
        {
            GradleRoot = Path.GetFullPath(gradleRoot);
            JavaHome = javaHome ?? "";
            EntryFile = Path.GetFullPath(entryFile);
            Script = script;
            AgentSource = agentSource;
            WorkDirectory = Path.GetFullPath(workDirectory);
        }

        public string GradleRoot { get; }
        public string JavaHome { get; }
        /// <summary>The source file with the program's <c>main</c>; its Gradle module is the one compiled.</summary>
        public string EntryFile { get; }
        public string Script { get; }
        public string AgentSource { get; }
        /// <summary>Class lists, the agent jar, reload requests and the daemon registry. Never inside the project.</summary>
        public string WorkDirectory { get; }
        public string DaemonRegistry => Path.Combine(WorkDirectory, "daemon");

        /// <summary>
        /// The work directory of a Gradle root under <c>%LOCALAPPDATA%\Vistudio\hot-reload</c>: outside the project, so
        /// the agent jar the running JVM keeps open never blocks the project's <c>clean</c>.
        /// </summary>
        public static string WorkDirectoryFor(string gradleRoot)
        {
            var full = Path.GetFullPath(gradleRoot).TrimEnd('\\', '/');
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
                // Lower case like the hash: the same folder spelled differently is the same work directory.
                var name = new string(Path.GetFileName(full).ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
                return SettingsXml.PathFor(Path.Combine("hot-reload", (name.Length == 0 ? "root" : name) + "-" + string.Concat(hash.Take(6).Select(b => b.ToString("x2")))));
            }
        }
    }

    /// <summary>The Gradle part of a reload: compiled, with the class files listed before and after.</summary>
    public sealed class HotReloadCompilation
    {
        internal HotReloadCompilation(HotReloadSnapshot before, HotReloadSnapshot after, string? error)
        { Before = before; After = after; Error = error; }
        public HotReloadSnapshot Before { get; }
        public HotReloadSnapshot After { get; }
        /// <summary>Why nothing can be reloaded: a compiler error, a module without JVM classes, a Gradle failure.</summary>
        public string? Error { get; }
    }

    /// <summary>Finds the program a debug session started and the JDK it runs on.</summary>
    public static class HotReloadJvm
    {
        /// <summary>
        /// The program's JVM among <c>jps -l</c> rows: it runs <paramref name="mainClass"/> and started with this session
        /// (an older run of the same class may still be alive). The newest one wins.
        /// </summary>
        public static int? Find(string jpsOutput, string mainClass, DateTime sessionStartUtc, Func<int, DateTime?> startTimeUtc)
        {
            int? best = null;
            var bestStart = DateTime.MinValue;
            foreach (var jvm in JvmTools.ParseJps(jpsOutput))
            {
                if (!JvmTools.IsMain(jvm.Main, mainClass)) continue;
                var start = startTimeUtc(jvm.Pid);
                // Two seconds of slack: the session start is taken just before the launch request.
                if (start == null || start.Value < sessionStartUtc.AddSeconds(-2)) continue;
                if (best == null || start.Value > bestStart) { best = jvm.Pid; bestStart = start.Value; }
            }
            return best;
        }

        /// <summary>Null when the process is gone or belongs to another user.</summary>
        public static DateTime? StartTimeUtc(int pid)
        {
            try { using (var process = Process.GetProcessById(pid)) return process.StartTime.ToUniversalTime(); }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (System.ComponentModel.Win32Exception) { return null; }
        }

        /// <summary><c>java.exe</c> of the JDK the program runs on (the program itself may be <c>javaw.exe</c>).</summary>
        public static string? JavaExecutable(int pid, string? javaHome)
        {
            var image = Win32ProcessCounters.ImagePath(pid);
            if (image != null)
            {
                var java = Path.Combine(Path.GetDirectoryName(image)!, "java.exe");
                if (File.Exists(java)) return java;
            }
            return JvmTools.FindTool("java", null, javaHome);
        }
    }

    /// <summary>
    /// Hot reload for one running Kotlin/JVM program: Gradle compiles the module (a daemon kept for the session makes
    /// that take seconds), and the reload agent swaps the changed classes into the JVM. Disposing the session ends the
    /// daemon. One reload at a time.
    /// </summary>
    public sealed class HotReloadSession : IDisposable
    {
        private readonly ProcessJob job = new ProcessJob();
        private readonly HotReloadState state = new HotReloadState();
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly string id = Guid.NewGuid().ToString("N").Substring(0, 8);
        private int requests;
        private bool disposed;

        public HotReloadSession(HotReloadTarget target) => Target = target;

        public HotReloadTarget Target { get; }

        /// <summary>Compiles, then applies. Use the two steps separately to hold a build lock only while Gradle runs.</summary>
        public async Task<HotReloadResult> ReloadAsync(int pid, Action<string> log, CancellationToken token)
        {
            var compilation = await CompileAsync(log, token).ConfigureAwait(false);
            return await ApplyAsync(compilation, pid, log, token).ConfigureAwait(false);
        }

        /// <param name="onGradleLine">Every line Gradle prints (compiler diagnostics included).</param>
        public async Task<HotReloadCompilation> CompileAsync(Action<string> onGradleLine, CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(Target.WorkDirectory);
                var before = Path.Combine(Target.WorkDirectory, "before.tsv");
                var after = Path.Combine(Target.WorkDirectory, "after.tsv");
                File.Delete(before);
                File.Delete(after);
                var start = GradleCommand.CreateHotReload(Target.GradleRoot, Target.JavaHome, Target.Script, Target.AgentSource, Target.EntryFile,
                    Target.WorkDirectory, Target.DaemonRegistry, hashBefore: state.KnownCount == 0);
                string? scriptError = null, compileError = null;
                Action<string> accept = line =>
                {
                    onGradleLine(line);
                    if (scriptError == null && line.StartsWith("VISTUDIO_HOTRELOAD_ERROR\t", StringComparison.Ordinal)) scriptError = line.Substring(line.IndexOf('\t') + 1);
                    if (compileError == null && (line.StartsWith("e: ", StringComparison.Ordinal) || line.Contains(": error: ")))
                        compileError = CompilerMessage(line.StartsWith("e: ", StringComparison.Ordinal) ? line.Substring(3) : line);
                };
                var code = await OwnedProcess.RunAsync(start, accept, accept, token, null, job).ConfigureAwait(false);
                if (code != 0)
                    return new HotReloadCompilation(HotReloadSnapshot.Empty, HotReloadSnapshot.Empty,
                        scriptError ?? (compileError != null ? "Compilation failed: " + compileError : "Gradle failed (exit " + code + ")."));
                if (!File.Exists(after))
                    return new HotReloadCompilation(HotReloadSnapshot.Empty, HotReloadSnapshot.Empty, "Gradle did not write the class list (" + after + ").");
                return new HotReloadCompilation(
                    File.Exists(before) ? HotReloadSnapshot.Parse(File.ReadAllText(before, Encoding.UTF8)) : HotReloadSnapshot.Empty,
                    HotReloadSnapshot.Parse(File.ReadAllText(after, Encoding.UTF8)), null);
            }
            finally { gate.Release(); }
        }

        /// <summary>
        /// Hands the classes that changed to the JVM. A JVM whose threads a debugger holds at a breakpoint takes them when
        /// it continues, so this can wait; <paramref name="log"/> says so once.
        /// </summary>
        public async Task<HotReloadResult> ApplyAsync(HotReloadCompilation compilation, int pid, Action<string> log, CancellationToken token)
        {
            if (compilation.Error != null) return HotReloadResult.Failure(compilation.Error);
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var started = HotReloadJvm.StartTimeUtc(pid);
                if (started == null) return HotReloadResult.Failure("The program's JVM (process " + pid + ") is no longer running.");
                var jvmStart = (long)(started.Value - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                var candidates = state.Plan(compilation.Before, compilation.After, jvmStart, Hash);
                if (candidates.Count == 0) return HotReloadResult.NoChanges;
                var result = await RunAgentAsync(compilation, candidates, pid, log, token).ConfigureAwait(false);
                state.Apply(candidates, result);
                return result;
            }
            finally { gate.Release(); }
        }

        private async Task<HotReloadResult> RunAgentAsync(HotReloadCompilation compilation, IReadOnlyList<HotReloadCandidate> candidates, int pid, Action<string> log, CancellationToken token)
        {
            var jar = compilation.After.AgentJar;
            if (jar == null || !File.Exists(jar)) return HotReloadResult.Failure("Gradle did not build the reload agent jar.");
            var java = HotReloadJvm.JavaExecutable(pid, Target.JavaHome);
            if (java == null) return HotReloadResult.Failure("java.exe was not found next to the program's JVM, in the configured JDK, JAVA_HOME or PATH.");

            // A request of its own per reload: an agent that answers late (after a canceled wait) cannot overwrite a newer result.
            var request = Path.Combine(Target.WorkDirectory, "request-" + id + "-" + Interlocked.Increment(ref requests).ToString(CultureInfo.InvariantCulture) + ".tsv");
            var response = request + ".result";
            File.WriteAllLines(request, candidates.Select(c => "class\t" + c.File.ClassName + "\t" + c.File.Path), new UTF8Encoding(false));
            try
            {
                string? clientError = null;
                var errors = new StringBuilder();
                var start = JvmTools.Start(java, "-cp \"" + jar + "\" VistudioHotReloadAgent " + pid.ToString(CultureInfo.InvariantCulture) + " \"" + jar + "\" \"" + request + "\"");
                var run = OwnedProcess.RunAsync(start, line =>
                {
                    if (clientError == null && line.StartsWith("VISTUDIO_HOTRELOAD_ERROR\t", StringComparison.Ordinal)) clientError = line.Substring(line.IndexOf('\t') + 1);
                }, line => errors.AppendLine(line), token);
                using (var notice = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(3), notice.Token)).ConfigureAwait(false) != run && !token.IsCancellationRequested)
                        log("Hot reload: waiting for the JVM to take " + (candidates.Count == 1 ? "1 class" : candidates.Count + " classes") +
                            ". A program paused in the debugger takes them when it continues.");
                    notice.Cancel();
                }
                var exit = await run.ConfigureAwait(false);
                if (exit != 0)
                {
                    var message = clientError ?? errors.ToString().Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length != 0) ?? "exit " + exit;
                    return HotReloadResult.Failure(message.IndexOf("no such process", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "The program's JVM (process " + pid + ") is no longer running."
                        : "The JVM did not load the reload agent: " + message);
                }
                if (!File.Exists(response)) return HotReloadResult.Failure("The reload agent wrote no result.");
                return HotReloadResult.ParseAgent(File.ReadAllText(response, Encoding.UTF8));
            }
            finally
            {
                try { File.Delete(request); File.Delete(response); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static readonly System.Text.RegularExpressions.Regex SourceLocation = new System.Text.RegularExpressions.Regex(
            @"^(?:file:///\S*/|[A-Za-z]:\\(?:[^\\:]+\\)*)(?<file>[^\\/:\s]+\.(?:kts?|java):\d+)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// The first compiler error as one short line: <c>HotText.kt:4:27 Unresolved reference 'missing'.</c> instead of the
        /// file URI (kotlinc) or full path (javac) it starts with. The Error List entry keeps the full location.
        /// </summary>
        public static string CompilerMessage(string line) => SourceLocation.Replace(line, "${file}");

        private static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
        }

        /// <summary>Ends the Gradle daemon (and the Kotlin compiler daemon it started) kept for this session.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { job.Terminate(); }
            catch (System.ComponentModel.Win32Exception) { }
            job.Dispose();
        }
    }
}
