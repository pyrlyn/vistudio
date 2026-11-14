using System;
using System.IO;
using System.Runtime.InteropServices;
using EnvDTE;
using Microsoft.VisualStudio.Shell;

namespace Vistudio
{
    /// <summary>
    /// Minimal DTE automation object (VSHPROPID_ExtObject) of a Kotlin project. The New Project template engine requires it
    /// after the project factory created the project; it exposes identity only. Items, code model and configurations are
    /// not automated: the project tree is read-only and Gradle owns the build.
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class KotlinProjectAutomation : Project
    {
        private readonly string filePath;
        private readonly Func<string> name;

        internal KotlinProjectAutomation(string filePath, Func<string> name)
        {
            this.filePath = filePath;
            this.name = name;
        }

        public string Name { get => name(); set => throw new NotSupportedException("Rename the Kotlin project in its Gradle settings."); }
        public string FileName => filePath;
        public string FullName => filePath;
        public string UniqueName => filePath;
        public string Kind => "{" + KotlinProjectFactory.Id.ToUpperInvariant() + "}";
        public bool IsDirty { get => false; set { } }
        public bool Saved { get => true; set { } }
        public DTE DTE
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return (DTE)Package.GetGlobalService(typeof(DTE));
            }
        }
        public Projects Collection
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return DTE.Solution.Projects;
            }
        }
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
