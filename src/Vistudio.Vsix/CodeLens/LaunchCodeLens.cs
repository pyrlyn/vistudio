using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Vistudio.Core.Languages;
using Vistudio.Languages;

namespace Vistudio
{
    /// <summary>
    /// "Run" / "Debug" above the entry point of a language whose programs debug in Visual Studio's native engine (Rust,
    /// Zig). In a language project (<c>.rustproj</c>, <c>.zigproj</c>) the project becomes the startup project and Visual
    /// Studio's own Debug.Start / Debug.StartWithoutDebugging build and launch it (see LanguageProjectConfig); elsewhere
    /// the language's own run command runs it in its Output pane, which cannot debug.
    /// </summary>
    internal abstract class LaunchCodeLens : InvokableCodeLens
    {
        private readonly LanguageInfo language;
        private readonly bool debug;

        protected LaunchCodeLens(LanguageInfo language, bool debug)
        {
            this.language = language;
            this.debug = debug;
        }

        protected bool Debug => debug;

        /// <summary>What a click does without a project, for example "cargo run in the Rust Output pane".</summary>
        protected abstract string RunWithoutProjectDescription { get; }

        /// <summary>Runs the program of <paramref name="path"/> without a project (documents are already saved). Called on the UI thread.</summary>
        protected abstract Task RunWithoutProjectAsync(string path, EnvDTE.DTE dte);

        public override void Dispose() { }

        public override async Task<CodeLensLabel> GetLabelAsync(CodeElementContext codeElementContext, CancellationToken token)
        {
            var path = LspLensLanguage.PathOf(codeElementContext.Range.Document.Uri);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            var project = ProjectFor(path);
            var text = debug ? "Debug" : "Run";
            var tooltip = project != null
                ? (debug ? "Build and debug " : "Build and run ") + project.Name + " (Visual Studio " + (debug ? "Debug > Start Debugging" : "Debug > Start Without Debugging") + ")."
                : debug ? "This file is not in a " + language.ProjectExtension + ": runs " + RunWithoutProjectDescription + " without the debugger. Add a " + language.DisplayName + " project to debug."
                : RunWithoutProjectDescription + ".";
            return new CodeLensLabel { Text = text, Tooltip = Capitalize(tooltip) };
        }

        public override async Task ExecuteAsync(CodeElementContext codeElementContext, IClientContext clientContext, CancellationToken cancelToken)
        {
            var path = LspLensLanguage.PathOf(codeElementContext.Range.Document.Uri);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancelToken);
            var dte = await AsyncServiceProvider.GlobalProvider.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancelToken);
            var project = ProjectFor(path);
            if (project != null && await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsSolutionBuildManager)) is IVsSolutionBuildManager builds)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancelToken);
                ErrorHandler.ThrowOnFailure(builds.set_StartupProject(project));
                dte.ExecuteCommand(debug ? "Debug.Start" : "Debug.StartWithoutDebugging");
                return;
            }
            dte.ExecuteCommand("File.SaveAll");
            await RunWithoutProjectAsync(path, dte);
        }

        /// <summary>The open language project of this language whose folder contains <paramref name="path"/> (the innermost one), or null.</summary>
        private LanguageProjectNode? ProjectFor(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(ServiceProvider.GlobalProvider.GetService(typeof(SVsShell)) is IVsShell shell)) return null;
            var guid = new Guid(LanguagesPackage.PackageGuid);
            // Loaded whenever a language project is open: it owns the project factory.
            if (shell.IsPackageLoaded(ref guid, out var loaded) != VSConstants.S_OK || !(loaded is LanguagesPackage package)) return null;
            return package.Projects.All
                .Where(p => p.Language.Id == language.Id && IsInside(path, p.Metadata.DirectoryPath))
                .OrderByDescending(p => p.Metadata.DirectoryPath.Length)
                .FirstOrDefault();
        }

        private static bool IsInside(string path, string folder)
        {
            var prefix = folder.TrimEnd('\\', '/') + "\\";
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}
