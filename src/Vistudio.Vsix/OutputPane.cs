using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Vistudio
{
    /// <summary>
    /// One custom Output window pane, created on first use and written from any thread. Long lines are cut so one
    /// tool's runaway output cannot stall the Output window.
    /// </summary>
    internal sealed class OutputPane
    {
        private readonly IServiceProvider services;
        private readonly Guid id;
        private readonly string title;
        private IVsOutputWindowPane? pane;

        public OutputPane(IServiceProvider services, Guid id, string title)
        {
            this.services = services;
            this.id = id;
            this.title = title;
        }

        public void Log(string message)
        {
            var target = pane;
            if (target != null) { Write(target, message); return; }
#pragma warning disable VSSDK007 // Fire-and-forget by design; failures only lose this log line.
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try { Write(await GetAsync(), message); }
                catch (Exception) { }
            });
#pragma warning restore VSSDK007
        }

        public async Task ShowAsync()
        {
            var target = await GetAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            target.Activate();
            (services.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ExecuteCommand("View.Output");
        }

        private async Task<IVsOutputWindowPane> GetAsync()
        {
            if (pane != null) return pane;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (pane != null) return pane;
            var output = services.GetService(typeof(SVsOutputWindow)) as IVsOutputWindow
                ?? throw new InvalidOperationException("Visual Studio Output service is unavailable.");
            var paneId = id;
            ErrorHandler.ThrowOnFailure(output.CreatePane(ref paneId, title, 1, 0));
            ErrorHandler.ThrowOnFailure(output.GetPane(ref paneId, out var created));
            return pane = created;
        }

        private static void Write(IVsOutputWindowPane target, string message)
        {
#pragma warning disable VSTHRD010 // This SDK method explicitly supports background threads.
            target.OutputStringThreadSafe((message.Length > 16384 ? message.Substring(0, 16384) + " [truncated]" : message) + Environment.NewLine);
#pragma warning restore VSTHRD010
        }
    }
}
