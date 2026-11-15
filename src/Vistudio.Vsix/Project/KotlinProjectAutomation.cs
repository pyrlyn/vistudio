using System;
using System.IO;
using System.Runtime.InteropServices;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Vistudio
{
    /// <summary>
    /// Minimal DTE automation object (VSHPROPID_ExtObject) of a Kotlin or language project. The New Project template engine
    /// requires it after the project factory created the project; it exposes identity only. Items, code model and
    /// configurations are not automated: the project tree is read-only and each project's own tool owns the build.
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class KotlinProjectAutomation : Project
    {
        private readonly IVsHierarchy hierarchy;
        private readonly string filePath;
        private readonly Guid projectType;
        private readonly Func<string> name;

        internal KotlinProjectAutomation(IVsHierarchy hierarchy, string filePath, Guid projectType, Func<string> name)
        {
            this.hierarchy = hierarchy;
            this.filePath = filePath;
            this.projectType = projectType;
            this.name = name;
        }

        public string Name { get => name(); set => throw new NotSupportedException("Rename the project through its own tool's settings."); }
        public string FileName => filePath;
        public string FullName => filePath;

        /// <summary>
        /// The solution's own unique name (a path relative to the solution for projects below it). DTE finds projects by
        /// it: Solution.Remove, SolutionBuild.BuildProject and the solution contexts fail with E_FAIL for any other value.
        /// </summary>
        public string UniqueName =>
            // Out-of-process automation clients call this object on an RPC thread; the solution answers on the UI thread.
            OnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return Package.GetGlobalService(typeof(SVsSolution)) is IVsSolution solution
                    && ErrorHandler.Succeeded(solution.GetUniqueNameOfProject(hierarchy, out var unique)) && !string.IsNullOrEmpty(unique)
                    ? unique : filePath;
            });

        public string Kind => projectType.ToString("B").ToUpperInvariant();
        public bool IsDirty { get => false; set { } }
        public bool Saved { get => true; set { } }
        public DTE DTE => OnMainThread(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return (DTE)Package.GetGlobalService(typeof(DTE));
        });

        public Projects Collection => OnMainThread(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return ((DTE)Package.GetGlobalService(typeof(DTE))).Solution.Projects;
        });

        private static T OnMainThread<T>(Func<T> read) =>
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                return read();
            });
        // The .kotlinproj is saved by the Properties window and Gradle import as it changes; there is nothing pending to save.
        public void Save(string FileName = "") { }
        public void SaveAs(string NewFileName) => throw new NotSupportedException("Kotlin project files cannot be saved under another name.");
        public void Delete() => throw new NotSupportedException("Remove the Kotlin project from the solution instead.");
        public ProjectItems ProjectItems => null!;
        public Properties Properties => null!;
        public object Object => null!;
        public object get_Extender(string ExtenderName) => null!;
        public object ExtenderNames => Array.Empty<string>();
        public string ExtenderCATID => "";
        public ConfigurationManager ConfigurationManager => null!;
        public Globals Globals => null!;
        public ProjectItem ParentProjectItem => null!;
        public CodeModel CodeModel => null!;

        public override string ToString() => Path.GetFileName(filePath);
    }
}
