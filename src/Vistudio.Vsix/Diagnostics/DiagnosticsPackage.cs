using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace Vistudio.Diagnostics
{
    /// <summary>
    /// Vistudio Diagnostics (T71): the tool window that stands in for Visual Studio's Diagnostic Tools in debug sessions
    /// the Diagnostics Hub does not accept (the Kotlin/Java and Dart Debug Adapter Host engines) and for WebAssembly F5,
    /// which runs without a debugger. Loaded by the first such session or by View &gt; Other Windows.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideMenuResource("DiagnosticsMenus.ctmenu", 1)]
    // Docked as a tab of Visual Studio's own Diagnostic Tools window, where users look for this data.
    [ProvideToolWindow(typeof(DiagnosticsWindow), Style = VsDockStyle.Tabbed, Window = "6d4078d1-5951-4ed1-ac0e-0a8099c1cce5", Orientation = ToolWindowOrientation.Right)]
    [Guid(PackageGuid)]
    public sealed class DiagnosticsPackage : AsyncPackage, IVsDebuggerEvents
    {
        internal const string PackageGuid = "4b8d2f61-9c3e-4a7d-b5e2-1f6a8c0d3e94";
        private static readonly Guid Commands = new Guid("8e2a6c14-3f5b-4d9e-a1c7-5b0d2e4f6a83");
        private IVsDebugger? debugger;
        private uint debuggerCookie;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)
                commands.AddCommand(new MenuCommand((_, __) => JoinableTaskFactory.RunAsync(() => ShowAsync(activate: true)).FileAndForget("Vistudio/Diagnostics/Show"),
                    new CommandID(Commands, 0x0100)));
            debugger = await GetServiceAsync(typeof(SVsShellDebugger)) as IVsDebugger;
            if (debugger != null) ErrorHandler.ThrowOnFailure(debugger.AdviseDebuggerEvents(this, out debuggerCookie));
        }

        internal async Task ShowAsync(bool activate)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            var window = await FindToolWindowAsync(typeof(DiagnosticsWindow), 0, true, DisposalToken);
            if (window?.Frame is IVsWindowFrame frame)
                // Opening at session start must not take focus from the editor that is about to stop at a breakpoint.
                ErrorHandler.ThrowOnFailure(activate ? frame.Show() : frame.ShowNoActivate());
        }

        public int OnModeChange(DBGMODE mode)
        {
            DiagnosticsSessions.OnDebuggerMode(mode & ~DBGMODE.DBGMODE_EncMask);
            return VSConstants.S_OK;
        }

        protected override void Dispose(bool disposing)
        {
            // The shell disposes packages on the UI thread; Unadvise asserts it.
#pragma warning disable VSTHRD010
            if (disposing) Unadvise();
#pragma warning restore VSTHRD010
            base.Dispose(disposing);
        }

        private void Unadvise()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (debugger == null || debuggerCookie == 0) return;
            debugger.UnadviseDebuggerEvents(debuggerCookie);
            debuggerCookie = 0;
        }
    }
}
