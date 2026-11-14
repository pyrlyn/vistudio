using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace Vistudio
{
    /// <summary>
    /// Debug and Release solution configurations on one "Any CPU" platform. The projects' own tools have no MSBuild
    /// configuration split; both names exist so solution Build/Rebuild/Clean work with default solution configurations.
    /// </summary>
    internal sealed class ProjectConfigProvider : IVsCfgProvider2
    {
        private static readonly string[] Names = { "Debug", "Release" };
        private const string Platform = "Any CPU";
        private readonly Dictionary<string, BuildableProjectConfig> configs;

        public ProjectConfigProvider(Func<string, string, BuildableProjectConfig> create) =>
            configs = Names.ToDictionary(n => n, n => create(n, Platform), StringComparer.OrdinalIgnoreCase);

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
    /// A buildable, debuggable configuration whose Build/Rebuild/Clean run the project's own tool, mirrored into the
    /// solution Build pane. Derived classes run the operation and start F5/Ctrl+F5.
    /// </summary>
    internal abstract class BuildableProjectConfig : IVsDebuggableProjectCfg, IVsBuildableProjectCfg
    {
        private const uint RebuildOption = 1; // VS_BUILDABLEPROJECTCFGOPTS_REBUILD
        protected readonly AsyncPackage Package;
        private readonly string name, platform;
        private readonly Dictionary<uint, IVsBuildStatusCallback> callbacks = new Dictionary<uint, IVsBuildStatusCallback>();
        private uint nextCookie = 1;
        private CancellationTokenSource? running;

        protected BuildableProjectConfig(AsyncPackage package, string name, string platform)
        {
            Package = package;
            this.name = name;
            this.platform = platform;
        }

        public void Cancel() => running?.Cancel();

        protected bool IsRunning => running != null;

        /// <summary>Runs "build", "rebuild" or "clean"; lines go to <paramref name="mirror"/> from any thread.</summary>
        protected abstract Task<bool> RunAsync(string operation, Action<string> mirror, CancellationToken token);

        /// <summary>Up to date for the solution build manager and F5; false makes Visual Studio build first.</summary>
        protected abstract bool IsUpToDate();

        protected abstract void Log(string message);

        public abstract int DebugLaunch(uint grfLaunch);

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

        public int StartUpToDateCheck(IVsOutputWindowPane pIVsOutputWindowPane, uint dwOptions)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Synchronous contract: the solution build manager reads the HRESULT (S_OK = up to date, E_FAIL = build needed)
            // and does not advise build status callbacks for this check.
            return running == null && IsUpToDate() ? VSConstants.S_OK : VSConstants.E_FAIL;
        }

        public int QueryDebugLaunch(uint grfLaunch, out int pfCanLaunch) { pfCanLaunch = 1; return VSConstants.S_OK; }

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
            // The solution Build pane accepts text only on the UI thread (VS 18), so tool lines are queued and flushed in batches.
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
                catch (Exception e) { Log("Build pane output failed: " + e.Message); }
            }
            void Mirror(string line)
            {
                pending.Enqueue(line);
                if (Interlocked.Exchange(ref flushScheduled, 1) == 0)
                    Package.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await Package.JoinableTaskFactory.SwitchToMainThreadAsync();
                        Flush();
                    }).Task.Forget();
            }
            Package.JoinableTaskFactory.RunAsync(async () =>
            {
                var success = false;
                try { success = await RunAsync(operation, Mirror, cancellation.Token); }
                catch (Exception e) { Mirror(e.Message); Log(e.Message); }
                await Package.JoinableTaskFactory.SwitchToMainThreadAsync();
                Flush();
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
                catch (Exception e) { Log("Build status callback failed: " + e.Message); }
            }
        }

        #endregion
    }
}
