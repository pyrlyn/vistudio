using System;
using System.ComponentModel.Design;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Text;
using Vistudio.Core.Zig;
using Task = System.Threading.Tasks.Task;

namespace Vistudio.Zig
{
    /// <summary>
    /// Zig package: Tools &gt; Options &gt; Zig, the Zig commands (Tools menu, Zig toolbar with the build step drop-down,
    /// editor context menu). Separate from <c>KotlinPackage</c> so the Kotlin package, its menus and settings are untouched.
    /// Editor features (coloring, ast-check, ZLS, CodeLens) are MEF/Extensibility parts and do not need this package loaded.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideMenuResource("ZigMenus.ctmenu", 1)]
    [ProvideOptionPage(typeof(ZigOptionsPage), "Zig", "General", 0, 0, true)]
    // Build File, Run File and ast-check are DefaultInvisible: without this context they stayed hidden until another
    // Zig command had loaded the package (seen in a fresh Visual Studio 18 instance).
    [ProvideUIContextRule(ZigEditorContext, "Zig editor active", "Zig", new[] { "Zig" }, new[] { "ActiveEditorContentType:" + ZigContentTypes.Zig })]
    [ProvideAutoLoad(ZigEditorContext, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuid)]
    public sealed class ZigPackage : AsyncPackage
    {
        internal const string PackageGuid = "a7d3c9e1-5b2f-4e6a-9c8d-1f2e3d4c5b6a";
        /// <summary>UI context "a Zig editor is active" (also in ZigCommands.vsct as guidZigEditorContext).</summary>
        internal const string ZigEditorContext = "6b0d2f4e-8a1c-4d3e-9f5b-7c2a1e0d3b48";
        internal static readonly Guid Commands = new Guid("e3a1d6f0-4b2c-4f7e-8d9a-5c6b7a8e9f10");
        private const int BuildProject = 0x0100, BuildFile = 0x0101, RunFile = 0x0102, Cancel = 0x0103, RefreshSteps = 0x0104,
            RestartServer = 0x0105, AstCheck = 0x0106, Settings = 0x0107, StepCombo = 0x0110, StepComboList = 0x0111;

        private ZigEnvironment environment = null!;
        private ZigBuildService build = null!;
        private ZigLanguageClient client = null!;
        private EnvDTE.DTE? dte;
        /// <summary>Commands whose action is in progress (UI thread only).</summary>
        private readonly System.Collections.Generic.HashSet<int> running = new System.Collections.Generic.HashSet<int>();

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            var model = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel ?? throw new InvalidOperationException("Visual Studio component model is unavailable.");
            environment = model.GetService<ZigEnvironment>();
            build = model.GetService<ZigBuildService>();
            client = model.GetService<ZigLanguageClient>();
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            dte = await GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            if (!(await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)) return;

            Add(commands, BuildProject, async () => { await SaveAllAsync(); await build.BuildProjectAsync(ActiveDocument()); }, whileIdle: true);
            Add(commands, BuildFile, async () => await build.BuildFileAsync(await SaveActiveZigDocumentAsync()), whileIdle: true, zigDocument: true);
            Add(commands, RunFile, async () => await build.RunFileAsync(await SaveActiveZigDocumentAsync()), whileIdle: true, zigDocument: true);
            Add(commands, Cancel, () => { build.Cancel(); return Task.CompletedTask; }, whileBusy: true);
            Add(commands, RefreshSteps, async () =>
            {
                var root = await build.FindBuildRootAsync(ActiveDocument()) ?? throw new InvalidOperationException("No build.zig was found in the open folder.");
                await environment.ShowOutputAsync();
                await build.RefreshStepsVerboseAsync(root);
            });
            Add(commands, RestartServer, async () => { environment.Log("Restarting ZLS..."); await client.RestartAsync(); });
            Add(commands, AstCheck, async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var path = ActiveDocument() ?? throw new InvalidOperationException("Open a .zig or .zon file first.");
                var session = FindSession(path) ?? throw new InvalidOperationException("ast-check runs on .zig and .zon files open in an editor.");
                session.CheckNow();
            }, zigDocument: true);
            Add(commands, Settings, () => { ShowOptionPage(typeof(ZigOptionsPage)); return Task.CompletedTask; });

            var combo = new OleMenuCommand(OnStepCombo, new CommandID(Commands, StepCombo)) { ParametersDescription = "$" };
            commands.AddCommand(combo);
            commands.AddCommand(new OleMenuCommand(OnStepList, new CommandID(Commands, StepComboList)));
        }

        private void Add(OleMenuCommandService service, int id, Func<Task> action, bool whileIdle = false, bool whileBusy = false, bool zigDocument = false)
        {
            var command = new OleMenuCommand((_, __) => JoinableTaskFactory.RunAsync(async () =>
            {
                // DTE.ExecuteCommand was seen to reach Exec twice ~100 ms apart (Visual Studio 18, checked in a test
                // instance); a command still running ignores the repeat instead of running twice.
                if (!running.Add(id)) return;
                try { await action(); }
                catch (Exception e)
                {
                    environment.Log("Zig: " + e.Message);
                    await environment.ShowOutputAsync();
                }
                finally { running.Remove(id); }
            }).Task.Forget(), new CommandID(Commands, id));
            command.BeforeQueryStatus += (_, __) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var busy = build.IsBusy;
                var enabled = (!whileIdle || !busy) && (!whileBusy || busy);
                if (zigDocument)
                {
                    // Also placed in the code editor context menu: shown only while a Zig document is active.
                    var active = ActiveDocument();
                    command.Visible = active != null && ZigCommandFactory.IsZigFile(active);
                    enabled &= command.Visible;
                }
                command.Enabled = enabled;
            };
            service.AddCommand(command);
        }

        private string? ActiveDocument()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return dte?.ActiveDocument?.FullName; }
            catch (ArgumentException) { return null; }
            catch (COMException) { return null; }
        }

        private async Task SaveAllAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            try { dte?.ExecuteCommand("File.SaveAll"); }
            catch (COMException) { }
        }

        private async Task<string> SaveActiveZigDocumentAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var document = dte?.ActiveDocument ?? throw new InvalidOperationException("Open a .zig file first.");
            if (!document.Saved) document.Save();
            return document.FullName;
        }

        private ZigAstCheckSession? FindSession(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var model = GetService(typeof(SComponentModel)) as IComponentModel;
            var documents = model?.GetService<ITextDocumentFactoryService>();
            var editors = model?.GetService<Microsoft.VisualStudio.Editor.IVsEditorAdaptersFactoryService>();
            if (documents == null || editors == null) return null;
            VsShellUtilities.IsDocumentOpen(this, path, Guid.Empty, out _, out _, out var frame);
            var view = frame == null ? null : VsShellUtilities.GetTextView(frame);
            var buffer = view == null ? null : editors.GetWpfTextView(view)?.TextDataModel.DocumentBuffer;
            return buffer == null ? null : ZigAstCheckSession.Get(buffer);
        }

        /// <summary>The build step drop-down on the Zig toolbar: shows and sets <see cref="ZigSettings.BuildStep"/>.</summary>
        private void OnStepCombo(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(e is OleMenuCmdEventArgs args)) return;
            if (args.OutValue != IntPtr.Zero)
            {
                var step = ZigSettings.Load().BuildStep;
                Marshal.GetNativeVariantForObject(step.Length == 0 ? "install (default)" : step, args.OutValue);
                return;
            }
            if (!(args.InValue is string chosen)) return;
            var name = chosen.Replace(" (default)", "").Trim();
            var root = ThreadHelper.JoinableTaskFactory.Run(() => build.FindBuildRootAsync(ActiveDocument()));
            var isDefault = build.GetSteps(root).Any(s => s.Name == name && s.IsDefault) || chosen.EndsWith(" (default)", StringComparison.Ordinal);
            try
            {
                var settings = ZigSettings.LoadGlobal();
                settings.BuildStep = isDefault ? "" : ZigArguments.ValidateStep(name);
                settings.Save();
                environment.Log("Zig build step: " + (isDefault ? name + " (default)" : name));
                if (ZigSettings.Load().BuildStep != settings.BuildStep)
                    environment.Log("Zig: this workspace's .vs\\settings.json sets vistudio.zig.buildStep, which overrides the chosen step in this window.");
            }
            catch (ArgumentException error) { environment.Log("Zig: " + error.Message); }
        }

        private void OnStepList(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(e is OleMenuCmdEventArgs args) || args.OutValue == IntPtr.Zero) return;
            var root = ThreadHelper.JoinableTaskFactory.Run(() => build.FindBuildRootAsync(ActiveDocument()));
            var steps = build.GetSteps(root).Select(s => s.ToString()).ToArray();
            Marshal.GetNativeVariantForObject(steps.Length == 0 ? new[] { "install (default)" } : steps, args.OutValue);
        }
    }
}
