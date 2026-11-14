using System;
using System.Collections.Generic;
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
    /// <summary>
    /// Solution configurations for a Kotlin project. Gradle has no Debug/Release split here: both names map to the
    /// project's Gradle build tasks so solution Build/Rebuild/Clean work with default solution configurations.
    /// </summary>
    internal sealed class KotlinConfigProvider : IVsCfgProvider2
    {
        private static readonly string[] Names = { "Debug", "Release" };
        private const string Platform = "Any CPU";
        private readonly Dictionary<string, KotlinProjectConfig> configs;

        public KotlinConfigProvider(KotlinProjectNode project, KotlinPackage package) =>
            configs = Names.ToDictionary(n => n, n => new KotlinProjectConfig(project, package, n, Platform), StringComparer.OrdinalIgnoreCase);

        public void CancelBuilds() { foreach (var config in configs.Values) config.Cancel(); }

        public int GetCfgs(uint celt, IVsCfg[] a, uint[] pcActual, uint[] prgfFlags)
        {
            if (a != null) for (var i = 0; i < Math.Min(celt, (uint)Names.Length); i++) a[i] = configs[Names[i]];
            if (pcActual != null && pcActual.Length != 0) pcActual[0] = (uint)Names.Length;
            if (prgfFlags != null && prgfFlags.Length != 0) prgfFlags[0] = 0;
            return VSConstants.S_OK;
        }

        public int GetCfgNames(uint celt, string[] rgbstr, uint[] pcActual) => Fill(Names, celt, rgbstr, pcActual);
        public int GetPlatformNames(uint celt, string[] rgbstr, uint[] pcActual) => Fill(new[] { Platform }, celt, rgbstr, pcActual);
        public int GetSupportedPlatformNames(uint celt, string[] rgbstr, uint[] pcActual) => Fill(new[] { Platform }, celt, rgbstr, pcActual);

        private static int Fill(string[] values, uint celt, string[] target, uint[] actual)
        {
            if (target != null) for (var i = 0; i < Math.Min(celt, (uint)values.Length); i++) target[i] = values[i];
            if (actual != null && actual.Length != 0) actual[0] = (uint)values.Length;
            return VSConstants.S_OK;
        }

        public int GetCfgOfName(string pszCfgName, string pszPlatformName, out IVsCfg? ppCfg)
        {
            ppCfg = configs.TryGetValue(pszCfgName, out var config) && (string.IsNullOrEmpty(pszPlatformName) || pszPlatformName == Platform) ? config : null;
            return ppCfg == null ? VSConstants.E_INVALIDARG : VSConstants.S_OK;
        }

        public int GetCfgProviderProperty(int propid, out object? var)
        {
            var = null;
            switch (propid)
            {
                case (int)__VSCFGPROPID.VSCFGPROPID_SupportsCfgAdd:
                case (int)__VSCFGPROPID.VSCFGPROPID_SupportsCfgDelete:
                case (int)__VSCFGPROPID.VSCFGPROPID_SupportsCfgRename:
                case (int)__VSCFGPROPID.VSCFGPROPID_SupportsPlatformAdd:
                case (int)__VSCFGPROPID.VSCFGPROPID_SupportsPlatformDelete:
                case (int)__VSCFGPROPID.VSCFGPROPID_SupportsPrivateCfgs:
                    var = false; return VSConstants.S_OK;
                default:
                    return VSConstants.E_NOTIMPL;
            }
        }

        public int AdviseCfgProviderEvents(IVsCfgProviderEvents pCPE, out uint pdwCookie) { pdwCookie = 1; return VSConstants.S_OK; }
        public int UnadviseCfgProviderEvents(uint dwCookie) => VSConstants.S_OK;
        public int AddCfgsOfCfgName(string pszCfgName, string pszCloneCfgName, int fPrivate) => VSConstants.E_NOTIMPL;
        public int DeleteCfgsOfCfgName(string pszCfgName) => VSConstants.E_NOTIMPL;
        public int RenameCfgsOfCfgName(string pszOldName, string pszNewName) => VSConstants.E_NOTIMPL;
        public int AddCfgsOfPlatformName(string pszPlatformName, string pszClonePlatformName) => VSConstants.E_NOTIMPL;
        public int DeleteCfgsOfPlatformName(string pszPlatformName) => VSConstants.E_NOTIMPL;
    }

    /// <summary>
    /// Buildable and debuggable configuration: Build/Rebuild/Clean use the package Gradle gate for this project only;
    /// F5/Ctrl+F5 resolve the JVM entry point from the .kotlinproj and reuse the Open Folder Kotlin/JVM debug launch.
    /// </summary>
    internal sealed class KotlinProjectConfig : IVsDebuggableProjectCfg, IVsBuildableProjectCfg
    {
        private const uint RebuildOption = 1; // VS_BUILDABLEPROJECTCFGOPTS_REBUILD
        private readonly KotlinProjectNode project;
        private readonly KotlinPackage package;
        private readonly string name, platform;
        private readonly Dictionary<uint, IVsBuildStatusCallback> callbacks = new Dictionary<uint, IVsBuildStatusCallback>();
        private uint nextCookie = 1;
        private CancellationTokenSource? running;

        public KotlinProjectConfig(KotlinProjectNode project, KotlinPackage package, string name, string platform)
        {
            this.project = project;
            this.package = package;
            this.name = name;
            this.platform = platform;
        }

        public void Cancel() => running?.Cancel();

        #region IVsProjectCfg

        public int get_DisplayName(out string pbstrDisplayName) { pbstrDisplayName = name + "|" + platform; return VSConstants.S_OK; }
        public int get_CanonicalName(out string pbstrCanonicalName) { pbstrCanonicalName = name; return VSConstants.S_OK; }
        public int get_IsDebugOnly(out int pfIsDebugOnly) { pfIsDebugOnly = 0; return VSConstants.S_OK; }
        public int get_IsReleaseOnly(out int pfIsReleaseOnly) { pfIsReleaseOnly = 0; return VSConstants.S_OK; }
        public int get_BuildableProjectCfg(out IVsBuildableProjectCfg ppIVsBuildableProjectCfg) { ppIVsBuildableProjectCfg = this; return VSConstants.S_OK; }
        public int get_ProjectCfgProvider(out IVsProjectCfgProvider? ppIVsProjectCfgProvider) { ppIVsProjectCfgProvider = null; return VSConstants.E_NOTIMPL; }
        public int EnumOutputs(out IVsEnumOutputs? ppIVsEnumOutputs) { ppIVsEnumOutputs = null; return VSConstants.E_NOTIMPL; }
        public int OpenOutput(string szOutputCanonicalName, out IVsOutput? ppIVsOutput) { ppIVsOutput = null; return VSConstants.E_NOTIMPL; }
        public int get_Platform(out Guid pguidPlatform) { pguidPlatform = Guid.Empty; return VSConstants.E_NOTIMPL; }
        public int get_IsPackaged(out int pfIsPackaged) { pfIsPackaged = 0; return VSConstants.S_OK; }
        public int get_IsSpecifyingOutputSupported(out int pfIsSpecifyingOutputSupported) { pfIsSpecifyingOutputSupported = 0; return VSConstants.S_OK; }
        public int get_TargetCodePage(out uint puiTargetCodePage) { puiTargetCodePage = 0; return VSConstants.E_NOTIMPL; }
        public int get_UpdateSequenceNumber(Microsoft.VisualStudio.OLE.Interop.ULARGE_INTEGER[] puliUSN) => VSConstants.E_NOTIMPL;
        public int get_RootURL(out string? pbstrRootURL) { pbstrRootURL = null; return VSConstants.E_NOTIMPL; }

        #endregion

        #region IVsBuildableProjectCfg

        public int get_ProjectCfg(out IVsProjectCfg ppIVsProjectCfg) { ppIVsProjectCfg = this; return VSConstants.S_OK; }

        public int AdviseBuildStatusCallback(IVsBuildStatusCallback pIVsBuildStatusCallback, out uint pdwCookie)
        {
            pdwCookie = nextCookie++;
            callbacks[pdwCookie] = pIVsBuildStatusCallback;
            return VSConstants.S_OK;
        }

        public int UnadviseBuildStatusCallback(uint dwCookie) { callbacks.Remove(dwCookie); return VSConstants.S_OK; }

        public int QueryStartBuild(uint dwOptions, int[] pfSupported, int[] pfReady) => Query(pfSupported, pfReady);
        public int QueryStartClean(uint dwOptions, int[] pfSupported, int[] pfReady) => Query(pfSupported, pfReady);

        public int QueryStartUpToDateCheck(uint dwOptions, int[] pfSupported, int[] pfReady) => Query(pfSupported, pfReady);

        private int Query(int[] supported, int[] ready)
        {
            if (supported != null && supported.Length != 0) supported[0] = 1;
            if (ready != null && ready.Length != 0) ready[0] = running == null ? 1 : 0;
            return VSConstants.S_OK;
        }

        public int StartBuild(IVsOutputWindowPane pIVsOutputWindowPane, uint dwOptions)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Start(pIVsOutputWindowPane, (dwOptions & RebuildOption) != 0 ? "rebuild" : "build");
        }

        public int StartClean(IVsOutputWindowPane pIVsOutputWindowPane, uint dwOptions)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Start(pIVsOutputWindowPane, "clean");
        }

        /// <summary>
        /// Fast up-to-date check used by solution Build and by F5: up to date only after a successful Build/Rebuild in this
        /// session with no input file or directory changed since it started. Gradle still decides what to recompile.
        /// </summary>
        public int StartUpToDateCheck(IVsOutputWindowPane pIVsOutputWindowPane, uint dwOptions)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Synchronous contract: the solution build manager reads the HRESULT (S_OK = up to date, E_FAIL = build needed)
            // and does not advise build status callbacks for this check. Only input timestamps are scanned.
            return running == null && project.IsUpToDate() ? VSConstants.S_OK : VSConstants.E_FAIL;
        }

        public int QueryDebugLaunch(uint grfLaunch, out int pfCanLaunch) { pfCanLaunch = 1; return VSConstants.S_OK; }

        public int DebugLaunch(uint grfLaunch)
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
                    // Non-JVM Kotlin targets (JS, Wasm, Native): F5 explains that only JVM debugging exists; Ctrl+F5 runs the
                    // target's Gradle run task. JVM targets and projects without targets continue to the JVM launch.
                    var kotlinTarget = await Task.Run(() => KotlinTarget.ForLaunch(KotlinProjectFile.Load(project.FilePath)));
                    if (kotlinTarget != null && !kotlinTarget.IsJvm)
                    {
                        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (!noDebug || kotlinTarget.RunTasks.Count == 0)
                        {
                            var message = noDebug ? "Kotlin target " + kotlinTarget.Id + " has no Gradle run task. Import the Gradle model again or choose another startup target."
                                : KotlinTarget.DebuggingNotSupported(kotlinTarget);
                            package.Environment.Log("Kotlin: " + message);
                            VsShellUtilities.ShowMessageBox(package, message, "Kotlin debugging", OLEMSGICON.OLEMSGICON_INFO,
                                OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                            return;
                        }
                        package.Environment.Log("Kotlin run target: " + kotlinTarget.Id + " (" + kotlinTarget.Description + ") with Gradle " + string.Join(" ", kotlinTarget.RunTasks));
                        try { await package.RunTargetAsync(project.FilePath, kotlinTarget, "run"); }
                        catch (InvalidOperationException e)
                        {
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

        public int QueryStatus(out int pfBuildDone) { pfBuildDone = running == null ? 1 : 0; return VSConstants.S_OK; }

        public int Stop(int fSync) { running?.Cancel(); return VSConstants.S_OK; }

        public int Wait(uint dwMilliseconds, int fTickWhenMessageQNotEmpty) => VSConstants.E_NOTIMPL;

        private int Start(IVsOutputWindowPane? pane, string operation)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (running != null) return VSConstants.E_UNEXPECTED;
            var proceed = 1;
            foreach (var callback in callbacks.Values.ToArray()) callback.BuildBegin(ref proceed);
            if (proceed == 0) { End(false); return VSConstants.S_OK; }
            var cancellation = running = new CancellationTokenSource();
            var inputsRoot = project.Metadata.GradleRoot;
            project.LastSuccessfulBuild = null;
            // The solution Build pane accepts text only on the UI thread (VS 18), so Gradle lines are queued and flushed in batches.
            var pending = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var flushScheduled = 0;
            void Flush()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Interlocked.Exchange(ref flushScheduled, 0);
                var text = new System.Text.StringBuilder();
                while (pending.TryDequeue(out var line)) text.Append(line).Append(Environment.NewLine);
                if (text.Length == 0 || pane == null) return;
                try { pane.OutputString(text.ToString()); }
                catch (Exception e) { package.Environment.Log("Kotlin: Build pane output failed: " + e.Message); }
            }
            void Mirror(string line)
            {
                pending.Enqueue(line);
                if (Interlocked.Exchange(ref flushScheduled, 1) == 0)
                    package.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                        Flush();
                    }).Task.Forget();
            }
            package.JoinableTaskFactory.RunAsync(async () =>
            {
                var success = false;
                GradleInputs.Snapshot? inputs = null;
                try { inputs = await Task.Run(() => GradleInputs.Capture(inputsRoot)); }
                catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException) { package.Environment.Log("Kotlin: input snapshot failed: " + e.Message); }
                try { success = await package.RunProjectBuildAsync(project.FilePath, operation, Mirror, cancellation.Token); }
                catch (Exception e) { Mirror("Kotlin: " + e.Message); package.Environment.Log("Kotlin: " + e.Message); }
                await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                Flush();
                if (success && operation != "clean") project.LastSuccessfulBuild = inputs;
                running = null;
                cancellation.Dispose();
                End(success);
            }).Task.Forget();
            return VSConstants.S_OK;
        }

        private void End(bool success)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var callback in callbacks.Values.ToArray())
            {
                try { callback.BuildEnd(success ? 1 : 0); }
                catch (Exception e) { package.Environment.Log("Kotlin: build status callback failed: " + e.Message); }
            }
        }

        #endregion
    }
}
