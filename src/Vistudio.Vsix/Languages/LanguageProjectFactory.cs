using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Vistudio.Core.Languages;
using Vistudio.Packages;
using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace Vistudio.Languages
{
    /// <summary>
    /// Opens language project files as Solution Explorer projects and creates new ones from the New Project templates.
    /// One factory serves every language: the extension of the file names the language.
    /// </summary>
    [Guid(Id)]
    internal sealed class LanguageProjectFactory : IVsProjectFactory
    {
        public const string Id = "eaec17e2-15db-4dc7-9979-dbaa46b8fdd1";
        private readonly LanguagesPackage package;

        public LanguageProjectFactory(LanguagesPackage package) => this.package = package;

        public int CanCreateProject(string pszFilename, uint grfCreateFlags, out int pfCanCreate)
        {
            var language = LanguageCatalog.ForProjectFile(pszFilename);
            pfCanCreate = File.Exists(pszFilename) && language != null && language != LanguageCatalog.Kotlin ? 1 : 0;
            return VSConstants.S_OK;
        }

        public int CreateProject(string pszFilename, string pszLocation, string pszName, uint grfCreateFlags, ref Guid iidProject, out IntPtr ppvProject, out int pfCanceled)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ppvProject = IntPtr.Zero;
            pfCanceled = 0;
            var path = Path.GetFullPath(pszFilename);
            if ((grfCreateFlags & (uint)__VSCREATEPROJFLAGS.CPF_CLONEFILE) != 0)
            {
                // File > New > Project: the .vstemplate's project file is a stub naming the language; generate the project.
                // A template made with Project > Export Template carries a real project file instead: copy it.
                var template = LanguageProjectTemplate.TryReadStub(path);
                var folder = Path.GetFullPath(string.IsNullOrWhiteSpace(pszLocation) ? Path.GetDirectoryName(path)! : pszLocation);
                if (template == null)
                {
                    var exportedName = string.IsNullOrWhiteSpace(pszName) ? Path.GetFileNameWithoutExtension(path) : Path.GetFileNameWithoutExtension(pszName);
                    path = ExportedProjectTemplate.Clone(path, folder, exportedName);
                    package.Log("Created project " + path + " from an exported template");
                }
                else
                {
                    var stub = template.Value;
                    var name = string.IsNullOrWhiteSpace(pszName) ? stub.ProjectName : Path.GetFileNameWithoutExtension(pszName);
                    // A template engine that writes the stub straight into the destination leaves it where the real project file goes.
                    if (string.Equals(Path.GetDirectoryName(path), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) File.Delete(path);
                    var wrapper = Path.Combine(Path.GetDirectoryName(typeof(LanguageProjectFactory).Assembly.Location)!, "Templates", "Wrapper");
                    path = LanguageProjectTemplate.Create(folder, stub.Language, name, wrapper).FilePath;
                    package.Log("Created " + stub.Language.DisplayName + " project " + path);
                }
            }
            else if ((grfCreateFlags & (uint)__VSCREATEPROJFLAGS.CPF_OPENFILE) == 0)
                throw new NotSupportedException("Create language projects with File > New > Project.");
            var project = new LanguageProjectNode(package, package.Packages, path);
            package.Projects.Opened(project);
            var unknown = Marshal.GetIUnknownForObject(project);
            try { return Marshal.QueryInterface(unknown, ref iidProject, out ppvProject); }
            finally { Marshal.Release(unknown); }
        }

        public int SetSite(IOleServiceProvider psp) => VSConstants.S_OK;

        public int Close() => VSConstants.S_OK;
    }
}
