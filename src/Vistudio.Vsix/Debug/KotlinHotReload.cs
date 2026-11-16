using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Vistudio.Core;
using Vistudio.Core.Diagnostics;
using Vistudio.Core.HotReload;

namespace Vistudio
{
    /// <summary>
    /// Kotlin/JVM hot reload: while a program started through <see cref="KotlinDebugCoordinator"/> (F5, Ctrl+F5, the Run
    /// and Debug lenses) runs, Tools &gt; Kotlin &gt; Hot Reload or saving a source file compiles its module with Gradle
    /// and swaps the changed classes into the program's JVM. The IDE's part only: the debug adapter and the language
    /// server are not involved, the JVM is reached through the JDK's attach mechanism.
    /// </summary>
    [Export, PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class KotlinHotReload
    {
        private sealed class RunningProgram
        {
            public RunningProgram(HotReloadSession session, string mainClass, DateTime startedUtc) { Session = session; MainClass = mainClass; StartedUtc = startedUtc; }
            public HotReloadSession Session { get; }
            public string MainClass { get; }
            public DateTime StartedUtc { get; }
            public CancellationTokenSource Ended { get; } = new CancellationTokenSource();
            public int? Pid;
            /// <summary>The last reload put compiler errors in the Error List; a successful one clears them.</summary>
            public bool ErrorsShown;
        }

        private readonly KotlinEnvironment environment;
        private readonly IServiceProvider services;
        private readonly object sync = new object();
        private RunningProgram? program;
        private bool reloading, again;
        // UI thread only: documents saved by the Hot Reload command itself.
        private bool savingForReload;
        private CancellationTokenSource? pendingSave;

        [ImportingConstructor]
        public KotlinHotReload(KotlinEnvironment environment, [Import(typeof(SVsServiceProvider))] IServiceProvider services)
        { this.environment = environment; this.services = services; }

        /// <summary>True while a Kotlin/JVM program that can take reloads runs.</summary>
        internal bool IsActive { get { lock (sync) return program != null; } }

        /// <summary>The launch is about to start the JVM (the Kotlin server runs it through Gradle).</summary>
        internal void ProgramStarting(string gradleRoot, string entryFile, string mainClass, string javaHome)
        {
            var build = Path.Combine(Path.GetDirectoryName(typeof(KotlinHotReload).Assembly.Location)!, "Build");
            var target = new HotReloadTarget(gradleRoot, javaHome, entryFile, Path.Combine(build, "HotReload.gradle"),
                Path.Combine(build, "VistudioHotReloadAgent.java"), HotReloadTarget.WorkDirectoryFor(gradleRoot));
            RunningProgram? previous;
            lock (sync)
            {
                previous = program;
                program = new RunningProgram(new HotReloadSession(target), mainClass, DateTime.UtcNow);
            }
            End(previous);
        }

        /// <summary>The program exited or debugging stopped: pending reloads end and the session's Gradle daemon with them.</summary>
        internal void ProgramEnded()
        {
            RunningProgram? ended;
            lock (sync) { ended = program; program = null; pendingSave?.Cancel(); pendingSave = null; }
            End(ended);
        }

        private static void End(RunningProgram? ended)
        {
            if (ended == null) return;
            ended.Ended.Cancel();
            // Ending the daemon's process tree is quick, but it never needs the UI thread.
            _ = Task.Run(() => ended.Session.Dispose());
        }

        /// <summary>A source file was saved: reload once the saves of one Save All have settled.</summary>
        internal void SourceSaved(string path)
        {
            if (savingForReload) return;
            CancellationTokenSource delay;
            lock (sync)
            {
                if (program == null || !IsUnder(path, program.Session.Target.GradleRoot)) return;
                pendingSave?.Cancel();
                delay = pendingSave = new CancellationTokenSource();
            }
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(400, delay.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                await ReloadAsync(fromSave: true).ConfigureAwait(false);
            });
        }

        private static bool IsUnder(string path, string root) =>
            Path.GetFullPath(path).StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        /// <summary>Compiles and reloads. Requests that arrive while a reload runs are merged into one more reload after it.</summary>
        internal async Task ReloadAsync(bool fromSave)
        {
            RunningProgram? current;
            lock (sync)
            {
                current = program;
                if (current != null)
                {
                    if (reloading) { again = true; return; }
                    reloading = true;
                }
            }
            if (current == null)
            {
                environment.Log("Hot reload: no Kotlin/JVM program is running. Start one with F5 or Ctrl+F5 first.");
                return;
            }
            try
            {
                while (true)
                {
                    lock (sync) again = false;
                    try { await ReloadOnceAsync(current, fromSave); }
                    catch (OperationCanceledException) { }
                    catch (Exception e)
                    {
                        await ReportAsync(HotReloadResult.Failure(e.Message), fromSave);
                    }
                    lock (sync) if (!again || program != current) break;
                }
            }
            finally { lock (sync) reloading = false; }
        }

        private async Task ReloadOnceAsync(RunningProgram current, bool fromSave)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(current.Ended.Token);
            var shell = services.GetService(typeof(SVsShell)) as IVsShell ?? throw new InvalidOperationException("Visual Studio shell unavailable.");
            var packageId = typeof(KotlinPackage).GUID;
            ErrorHandler.ThrowOnFailure(shell.LoadPackage(ref packageId, out var loaded));
            var package = (KotlinPackage)loaded;
            if (package.GradleBusy)
            {
                environment.Log("Hot reload skipped: a Gradle build is running. Run Tools > Kotlin > Hot Reload when it has finished.");
                return;
            }
            if (!fromSave)
            {
                // The command reloads what is in the editors; these saves must not ask for a second reload.
                savingForReload = true;
                try { await package.SaveModifiedDocumentsAsync(); }
                finally { savingForReload = false; }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(current.Ended.Token);
            }
            SetStatus("Hot reload: compiling " + current.MainClass + "...");
            var lines = new List<string>();
            var diagnostics = new BuildDiagnostics(current.Session.Target.GradleRoot);
            var startedUtc = DateTime.UtcNow;
            // The compile holds the shared Gradle gate (Cancel Gradle and workspace changes end it); handing the classes to
            // the JVM does not, because a program paused at a breakpoint takes them only when it continues.
            var compilation = await package.RunExclusiveGradleAsync(async gradle =>
            {
                using (var both = CancellationTokenSource.CreateLinkedTokenSource(gradle, current.Ended.Token))
                    // Off the UI thread from the first statement: the session touches files before its first await.
                    return await Task.Run(() => current.Session.CompileAsync(line => { lock (lines) lines.Add(line); diagnostics.Accept(line); }, both.Token)).ConfigureAwait(false);
            });
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(current.Ended.Token);
            if (compilation.Error != null)
            {
                // The Gradle output is only worth reading when the compile failed.
                lock (lines) foreach (var line in lines) environment.Log(line);
                package.PublishBuildDiagnostics(diagnostics.Complete(1), startedUtc);
                current.ErrorsShown = true;
            }
            else if (current.ErrorsShown)
            {
                package.PublishBuildDiagnostics(diagnostics.Complete(0), startedUtc);
                current.ErrorsShown = false;
            }
            await TaskScheduler.Default;
            var result = HotReloadResult.Failure(compilation.Error ?? "");
            if (compilation.Error == null)
            {
                var pid = await FindJvmAsync(current).ConfigureAwait(false);
                result = pid == null
                    ? HotReloadResult.Failure("The JVM running " + current.MainClass + " was not found (jps lists no JVM with that main class started by this session). " +
                        "If the program is still starting, reload again once it runs.")
                    : await current.Session.ApplyAsync(compilation, pid.Value, environment.Log, current.Ended.Token).ConfigureAwait(false);
            }
            await ReportAsync(result, fromSave);
        }

        /// <summary>The Kotlin server starts the JVM through Gradle, so the program is found by its main class once it runs.</summary>
        private static async Task<int?> FindJvmAsync(RunningProgram current)
        {
            if (current.Pid is int known && HotReloadJvm.StartTimeUtc(known) != null) return known;
            var jps = JvmTools.FindTool("jps", null, current.Session.Target.JavaHome)
                ?? throw new InvalidOperationException("jps was not found in the configured JDK, JAVA_HOME or PATH, so the program's JVM cannot be found.");
            var output = await JvmTools.RunAsync(jps, "-l", current.Ended.Token).ConfigureAwait(false);
            return current.Pid = HotReloadJvm.Find(output, current.MainClass, current.StartedUtc, HotReloadJvm.StartTimeUtc);
        }

        private async Task ReportAsync(HotReloadResult result, bool fromSave)
        {
            environment.Log(result.Summary);
            foreach (var line in result.Details) environment.Log(line);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var problem = result.Error != null || result.Failed.Count != 0;
            SetStatus(problem ? result.Summary + " See Output > Kotlin." : result.Summary);
            // A reload asked for by command shows why it failed; a save must not move the Output window in front of the editor.
            if (problem && !fromSave) await environment.ShowOutputAsync();
        }

        private void SetStatus(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (services.GetService(typeof(SVsStatusbar)) is IVsStatusbar bar) bar.SetText(text.Length > 200 ? text.Substring(0, 200) + "..." : text);
        }
    }

    /// <summary>Hot reload on save: a saved .kt or .java file reloads the running Kotlin/JVM program of its Gradle build.</summary>
    // Matched on "text" plus the file name, like the Dart listener: the content type can be assigned after the view opens.
    [Export(typeof(IWpfTextViewCreationListener)), ContentType("text"), TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class KotlinHotReloadSaveListener : IWpfTextViewCreationListener
    {
        [Import] internal ITextDocumentFactoryService Documents = null!;
        [Import] internal KotlinHotReload HotReload = null!;

        public void TextViewCreated(IWpfTextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var buffer = view.TextDataModel.DocumentBuffer;
            if (!Documents.TryGetTextDocument(buffer, out var document) || !KotlinJvmLaunch.IsSourceFile(document.FilePath)) return;
            // One subscription per document, however many views show it; removed with the last view.
            var views = buffer.Properties.GetOrCreateSingletonProperty(typeof(KotlinHotReloadSaveListener), () =>
            {
                document.FileActionOccurred += Saved;
                return new ViewCount();
            });
            views.Count++;
            view.Closed += (_, __) =>
            {
                if (--views.Count > 0) return;
                document.FileActionOccurred -= Saved;
                buffer.Properties.RemoveProperty(typeof(KotlinHotReloadSaveListener));
            };
        }

        private sealed class ViewCount { internal int Count; }

        private void Saved(object? sender, TextDocumentFileActionEventArgs e)
        {
            if ((e.FileActionType & FileActionTypes.ContentSavedToDisk) == 0 || !KotlinJvmLaunch.IsSourceFile(e.FilePath) || !HotReload.IsActive) return;
            bool enabled;
            // A damaged settings file must not break saving; the option page reports it.
            try { enabled = KotlinSettings.Load().HotReloadOnSave; }
            catch (Exception error) when (error is InvalidOperationException || error is System.Xml.XmlException || error is IOException) { enabled = true; }
            if (enabled) HotReload.SourceSaved(e.FilePath);
        }
    }
}
