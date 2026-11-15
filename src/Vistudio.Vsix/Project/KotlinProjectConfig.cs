using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Vistudio.Core.Projects;

namespace Vistudio
{
    /// <summary>Kotlin solution configurations: both names map to the project's Gradle build tasks.</summary>
    internal static class KotlinConfigProvider
    {
        public static ProjectConfigProvider Create(KotlinProjectNode project, KotlinPackage package) =>
            new ProjectConfigProvider((name, platform) => new KotlinProjectConfig(project, package, name, platform));
    }

    /// <summary>
    /// Build/Rebuild/Clean use the package Gradle gate for this project only; F5/Ctrl+F5 resolve the JVM entry point
    /// from the .kotlinproj and reuse the Open Folder Kotlin/JVM debug launch.
    /// </summary>
    internal sealed class KotlinProjectConfig : BuildableProjectConfig
    {
        private readonly KotlinProjectNode project;
        private readonly KotlinPackage package;

        public KotlinProjectConfig(KotlinProjectNode project, KotlinPackage package, string name, string platform) : base(package, name, platform)
        {
            this.project = project;
            this.package = package;
        }

        protected override void Log(string message) => package.Environment.Log("Kotlin: " + message);

        /// <summary>
        /// Up to date only after a successful Build/Rebuild in this session with no input file or directory changed since
        /// it started. Gradle still decides what to recompile.
        /// </summary>
        protected override bool IsUpToDate() => project.IsUpToDate();

        protected override async Task<bool> RunAsync(string operation, Action<string> mirror, CancellationToken token)
        {
            var inputsRoot = project.Metadata.GradleRoot;
            project.LastSuccessfulBuild = null;
            GradleInputs.Snapshot? inputs = null;
            try { inputs = await Task.Run(() => GradleInputs.Capture(inputsRoot)); }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException) { Log("input snapshot failed: " + e.Message); }
            var success = await package.RunProjectBuildAsync(project.FilePath, operation, mirror, token);
            if (success && operation != "clean") project.LastSuccessfulBuild = inputs;
            return success;
        }

        public override int DebugLaunch(uint grfLaunch)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var noDebug = (grfLaunch & (uint)__VSDBGLAUNCHFLAGS.DBGLAUNCH_NoDebug) != 0;
            var components = package.GetServiceOnMainThread(typeof(SComponentModel)) as IComponentModel
                ?? throw new InvalidOperationException("Visual Studio component model is unavailable.");
            var coordinator = components.GetService<KotlinDebugCoordinator>();
            package.JoinableTaskFactory.RunAsync(async () =>
            {
                KotlinLaunchTarget target;
                try
                {
                    // Non-JVM Kotlin targets (JS, Wasm, Native) have no debugger: F5 and Ctrl+F5 run the target's Gradle run
                    // task, and F5 adds Vistudio Diagnostics on the program it starts (creator decision, T71). JVM targets
                    // and projects without targets continue to the JVM launch.
                    var kotlinTarget = await Task.Run(() => KotlinTarget.ForLaunch(KotlinProjectFile.Load(project.FilePath)));
                    if (kotlinTarget != null && !kotlinTarget.IsJvm)
                    {
                        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (kotlinTarget.RunTasks.Count == 0)
                        {
                            var message = noDebug ? "Kotlin target " + kotlinTarget.Id + " has no Gradle run task. Import the Gradle model again or choose another startup target."
                                : KotlinTarget.DebuggingNotSupported(kotlinTarget);
                            package.Environment.Log("Kotlin: " + message);
                            VsShellUtilities.ShowMessageBox(package, message, "Kotlin debugging", OLEMSGICON.OLEMSGICON_INFO,
                                OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                            return;
                        }
                        package.Environment.Log("Kotlin run target: " + kotlinTarget.Id + " (" + kotlinTarget.Description + ") with Gradle " + string.Join(" ", kotlinTarget.RunTasks) +
                            (noDebug ? "" : ". The " + kotlinTarget.Description + " target has no debugger; it runs without debugging and Vistudio Diagnostics shows its CPU and memory."));
                        var title = project.Name + " " + kotlinTarget.Id + " (" + kotlinTarget.Description + ")";
                        Action<int>? started = noDebug ? null : (Action<int>)(gradle => Diagnostics.DiagnosticsSessions.WatchProgramUnderGradle(gradle, title));
                        try
                        {
                            var ok = await package.RunTargetAsync(project.FilePath, kotlinTarget, "run", started);
                            if (!noDebug) Diagnostics.DiagnosticsSessions.EndRun(ok ? "The Gradle run finished." : "The Gradle run failed.");
                        }
                        catch (InvalidOperationException e)
                        {
                            if (!noDebug) Diagnostics.DiagnosticsSessions.EndRun("The Gradle run could not start.");
                            package.Environment.Log("Kotlin: " + e.Message);
                            await package.Environment.ShowOutputAsync();
                        }
                        return;
                    }
                    target = await Task.Run(() => KotlinLaunchTarget.Resolve(KotlinProjectFile.Load(project.FilePath)));
                }
                catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException || e is System.IO.InvalidDataException || e is System.Xml.XmlException)
                {
                    await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    package.Environment.Log("Cannot start Kotlin debugging: " + e.Message);
                    VsShellUtilities.ShowMessageBox(package, e.Message, "Kotlin debugging", OLEMSGICON.OLEMSGICON_WARNING,
                        OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                    return;
                }
                package.Environment.Log("Kotlin " + (noDebug ? "run" : "debug") + " target: " + target.MainClass + " (" + target.SourceFile + ")");
                var config = new JObject
                {
                    ["type"] = KotlinLaunchProvider.LaunchType, ["mainClass"] = target.MainClass, ["project"] = target.SourceFile,
                    ["cwd"] = target.WorkingDirectory, ["args"] = new JArray(target.Arguments.ToArray()),
                };
                if (noDebug) config["noDebug"] = true;
                // Normally the solution build manager has just built the startup project; build again only if inputs changed
                // (for example when the run-time build was declined or disabled in Tools > Options).
                var buildFirst = !await Task.Run(project.IsUpToDate);
                if (!buildFirst) package.Environment.Log("Kotlin project is up to date; launching without another Gradle build.");
                await coordinator.LaunchAsync(project.ProjectDirectory, config, null, CancellationToken.None, buildFirst);
            }).Task.Forget();
            return VSConstants.S_OK;
        }
    }
}
