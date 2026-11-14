using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Vistudio.Core;
using Vistudio.Core.Templates;
using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace Vistudio
{
    /// <summary>
    /// Opens existing .kotlinproj metadata files as Solution Explorer projects and creates new ones from the New Project templates.
    /// Gradle remains the build system.
    /// </summary>
    [Guid(Id)]
    internal sealed class KotlinProjectFactory : IVsProjectFactory
    {
        public const string Id = "e7447d71-1121-42dc-8a4d-d3d78b3cc633";
        public const string Extension = ".kotlinproj";
        private readonly KotlinPackage package;

        public KotlinProjectFactory(KotlinPackage package) => this.package = package;

        public int CanCreateProject(string pszFilename, uint grfCreateFlags, out int pfCanCreate)
        {
            pfCanCreate = File.Exists(pszFilename) && string.Equals(Path.GetExtension(pszFilename), Extension, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
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
                // File > New > Project: the .vstemplate's project file is a stub naming the template; generate the Gradle project.
                var created = CreateFromTemplate(path, pszLocation, pszName);
                if (created == null)
                {
                    pfCanceled = 1;
                    return VSConstants.OLE_E_PROMPTSAVECANCELLED;
                }
                path = created;
            }
            else if ((grfCreateFlags & (uint)__VSCREATEPROJFLAGS.CPF_OPENFILE) == 0)
                throw new NotSupportedException("Open the Gradle folder and run Tools > Kotlin: Create or Open .kotlinproj.");
            var project = new KotlinProjectNode(package, path);
            if ((grfCreateFlags & (uint)__VSCREATEPROJFLAGS.CPF_CLONEFILE) != 0) package.ImportWhenLoaded(path);
            var unknown = Marshal.GetIUnknownForObject(project);
            try { return Marshal.QueryInterface(unknown, ref iidProject, out ppvProject); }
            finally { Marshal.Release(unknown); }
        }

        /// <summary>Generates the project of a template stub into <paramref name="location"/>; null when the user canceled.</summary>
        private string? CreateFromTemplate(string stubPath, string location, string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var stub = KotlinProjectTemplate.TryReadStub(stubPath)
                ?? throw new NotSupportedException("Only Vistudio Kotlin project templates can create .kotlinproj projects. Open existing projects with File > Open > Project/Solution.");
            var projectName = string.IsNullOrWhiteSpace(name) ? stub.ProjectName : Path.GetFileNameWithoutExtension(name);
            IReadOnlyList<string>? targets = null;
            if (stub.Kind == KotlinTemplateKind.Multiplatform && (targets = KotlinTemplateTargetsDialog.Choose(projectName)) == null) return null;
            var folder = Path.GetFullPath(string.IsNullOrWhiteSpace(location) ? Path.GetDirectoryName(stubPath)! : location);
            // A template engine that writes the stub straight into the destination leaves it where the real project file goes.
            if (string.Equals(Path.GetDirectoryName(stubPath), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) File.Delete(stubPath);
            var wrapper = Path.Combine(Path.GetDirectoryName(typeof(KotlinProjectFactory).Assembly.Location)!, "Templates", "Wrapper");
            var options = new KotlinTemplateOptions(stub.Kind, projectName, targets);
            var project = KotlinProjectTemplate.Create(folder, options, wrapper, KotlinSettings.Load().GradleJavaHome);
            package.Environment.Log("Created " + stub.Kind + " Kotlin project " + project.FilePath
                + (options.Targets.Count != 0 ? " (targets: " + string.Join(", ", options.Targets) + ")" : ""));
            return project.FilePath;
        }

        public int SetSite(IOleServiceProvider psp) => VSConstants.S_OK;

        public int Close() => VSConstants.S_OK;
    }
}
