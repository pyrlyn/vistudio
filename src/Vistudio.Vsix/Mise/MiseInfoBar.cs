using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Vistudio.Core.Mise;
using Task = System.Threading.Tasks.Task;

// Info bar buttons start mise detached: FileAndForget reports failures, the bar does not wait.
#pragma warning disable VSSDK007

namespace Vistudio.Mise
{
    /// <summary>
    /// The main-window info bar for the workspace's mise tools: shown when tools are missing, updates exist, the config is
    /// not trusted, or mise is not installed; its buttons run the same actions as Tools &gt; Mise. Closing it hides it until
    /// the workspace changes.
    /// </summary>
    internal sealed class MiseInfoBar : IVsInfoBarUIEvents
    {
        private const string Open = "open", Settings = "settings";
        private readonly IServiceProvider services;
        private readonly MiseService service;
        private readonly Action showSettings;
        private IVsInfoBarUIElement? element;
        private uint cookie;
        private MiseScope? shownFor;
        private string? shownText;
        private string? dismissedFolder;

        internal MiseInfoBar(IServiceProvider services, MiseService service, Action showSettings)
        {
            this.services = services; this.service = service; this.showSettings = showSettings;
        }

        /// <summary>A new workspace shows the bar again even if it was closed for the previous one.</summary>
        internal void ResetDismissal() => dismissedFolder = null;

        internal async Task UpdateAsync(MiseScope? scope, MiseReport? report)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (scope == null || report == null || !MiseService.Settings.InfoBar || string.Equals(dismissedFolder, scope.Folder, StringComparison.OrdinalIgnoreCase))
            {
                Close();
                return;
            }
            var (text, actions, warning) = Content(scope, report);
            if (text == null) { Close(); return; }
            if (element != null && text == shownText && shownFor?.Folder == scope.Folder) return;
            Close();

            if (!(services.GetService(typeof(SVsInfoBarUIFactory)) is IVsInfoBarUIFactory factory)) return;
            if (!(services.GetService(typeof(SVsShell)) is IVsShell shell)) return;
            if (shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out var hostObject) != 0 || !(hostObject is IVsInfoBarHost host)) return;
            var model = new InfoBarModel(new[] { new InfoBarTextSpan(text) }, actions, warning ? KnownMonikers.StatusWarning : KnownMonikers.StatusInformation, isCloseButtonVisible: true);
            element = factory.CreateInfoBar(model);
            element.Advise(this, out cookie);
            host.AddInfoBar(element);
            shownFor = scope;
            shownText = text;
        }

        private static (string? Text, IEnumerable<IVsInfoBarActionItem> Actions, bool Warning) Content(MiseScope scope, MiseReport report)
        {
            var actions = new List<IVsInfoBarActionItem>();
            var openFile = scope.Files.FirstOrDefault();
            if (report.UntrustedPath != null)
            {
                actions.Add(new InfoBarButton("Trust", MiseAction.Trust));
                actions.Add(new InfoBarHyperlink("Open " + Path.GetFileName(report.UntrustedPath), Open));
                return ("mise: " + Path.GetFileName(report.UntrustedPath) + " is not trusted, so its tools cannot be checked. Trust it only if you trust this repository.", actions, true);
            }
            if (report.Tools.Count == 0 && report.Error == MiseService.NotFound)
            {
                actions.Add(new InfoBarHyperlink("Settings", Settings));
                return ("This workspace has a mise config (" + scope.Title + "), but mise.exe was not found.", actions, true);
            }
            if (report.Tools.Count == 0) return (null, actions, false);
            var tools = report.ForFiles(scope.Files);
            var missing = tools.Count(t => t.State == MiseToolState.Missing);
            var updates = MiseService.Targets(tools, MiseAction.UpgradeToLatest).Count - missing;
            if (missing == 0 && updates == 0) return (null, actions, false);
            if (missing > 0) actions.Add(new InfoBarButton("Install missing", MiseAction.Install));
            if (MiseService.Targets(tools, MiseAction.Upgrade).Count > 0) actions.Add(new InfoBarButton("Upgrade all", MiseAction.Upgrade));
            if (updates > 0) actions.Add(new InfoBarButton("Upgrade all to latest", MiseAction.UpgradeToLatest));
            if (openFile != null) actions.Add(new InfoBarHyperlink("Open " + Path.GetFileName(openFile), Open));
            var names = string.Join(", ", tools.Where(t => t.State != MiseToolState.UpToDate).Take(4).Select(t => t.Name + " " + (t.State == MiseToolState.Missing ? "(not installed)" : "→ " + (t.LatestInRange ?? t.Latest))));
            return ("mise (" + scope.Title + "): " + MiseReport.Summary(tools) + ". " + names + (tools.Count(t => t.State != MiseToolState.UpToDate) > 4 ? ", ..." : ""), actions, missing > 0);
        }

        public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var scope = shownFor;
            if (scope == null) return;
            if (actionItem.ActionContext is MiseAction action)
            {
                Close();
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    try { await service.RunAsync(action, scope); }
                    catch (Exception e) { service.Log("mise: " + e.Message); await service.ShowOutputAsync(); }
                }).FileAndForget("Vistudio/Mise/InfoBar");
            }
            else if (Open.Equals(actionItem.ActionContext))
            {
                var file = service.Cached(scope.Folder)?.UntrustedPath ?? scope.Files.FirstOrDefault();
                if (file != null) service.OpenAsync(file).FileAndForget("Vistudio/Mise/Open");
            }
            else if (Settings.Equals(actionItem.ActionContext)) showSettings();
        }

        public void OnClosed(IVsInfoBarUIElement infoBarUIElement)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Closed by the user (the close button), not by Close() below.
            if (element == infoBarUIElement) dismissedFolder = shownFor?.Folder;
            Detach();
        }

        private void Close()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var current = element;
            Detach();
            current?.Close();
        }

        private void Detach()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (element != null && cookie != 0) element.Unadvise(cookie);
            element = null;
            cookie = 0;
            shownText = null;
        }
    }
}
