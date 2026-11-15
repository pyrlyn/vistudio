using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using EnvDTE;

namespace Vistudio
{
    /// <summary>
    /// DTE <see cref="ProjectItems"/> of a Kotlin or language project: the files and folders on disk under the project folder,
    /// without build output and tool caches (the folders the project tree ignores) and without the project file. Project
    /// &gt; Export Template walks it to put the project's files into the template (the project factory copies them back when a
    /// project is created from it, see <see cref="ExportedProjectTemplate"/>). <see cref="AddFromTemplate"/> and
    /// <see cref="AddFolder"/> copy into the folder (Add New Item from item templates); the project tree follows the disk.
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class ProjectFolderItems : ProjectItems
    {
        private readonly KotlinProjectAutomation project;
        private readonly string? folder;
        private readonly object parent;

        internal ProjectFolderItems(KotlinProjectAutomation project, string? folder, object parent)
        {
            this.project = project;
            this.folder = folder;
            this.parent = parent;
        }

        private List<ProjectFolderItem> Items()
        {
            var list = new List<ProjectFolderItem>();
            if (folder == null || !Directory.Exists(folder)) return list;
            foreach (var directory in Directory.EnumerateDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                if (!Skipped(directory, true)) list.Add(new ProjectFolderItem(project, directory, true, this));
            foreach (var file in Directory.EnumerateFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                if (!Skipped(file, false) && !string.Equals(file, project.FullName, StringComparison.OrdinalIgnoreCase))
                    list.Add(new ProjectFolderItem(project, file, false, this));
            return list;
        }

        private bool Skipped(string path, bool directory)
        {
            // Only the part below the project folder counts: a project may itself live under a folder named build or out.
            var root = Path.GetDirectoryName(project.FullName)!.TrimEnd('\\');
            var relative = path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length) : "\\" + Path.GetFileName(path);
            if (TreeProjectNode.IsBuildOutput(relative + (directory ? "\\" : ""))) return true;
            try { return (File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System)) != 0; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        /// <summary>A 1-based index or an item name, as in every DTE collection.</summary>
        public ProjectItem Item(object index)
        {
            var items = Items();
            if (index is string name)
                return items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException("No project item is named " + name + ".", nameof(index));
            var position = Convert.ToInt32(index);
            if (position < 1 || position > items.Count) throw new ArgumentOutOfRangeException(nameof(index));
            return items[position - 1];
        }

        public object Parent => parent;
        public int Count => Items().Count;
        public IEnumerator GetEnumerator() => Items().GetEnumerator();
#pragma warning disable VSTHRD010 // KotlinProjectAutomation.DTE switches to the UI thread itself (automation calls arrive on RPC threads).
        public DTE DTE => project.DTE;
#pragma warning restore VSTHRD010
        public string Kind => project.Kind;
        public Project ContainingProject => project;

        /// <summary>Copies a file into this folder as <paramref name="Name"/> (Add New Item from an item template).</summary>
        public ProjectItem AddFromTemplate(string FileName, string Name) => CopyIn(FileName, Name);

        private ProjectFolderItem CopyIn(string source, string name)
        {
            var target = Target(name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: false);
            return new ProjectFolderItem(project, target, false, this);
        }

        public ProjectItem AddFolder(string Name, string Kind = Constants.vsProjectItemKindPhysicalFolder)
        {
            var target = Target(Name);
            Directory.CreateDirectory(target);
            return new ProjectFolderItem(project, target, true, this);
        }

        public ProjectItem AddFromFileCopy(string FilePath) => CopyIn(FilePath, Path.GetFileName(FilePath));

        /// <summary>Files already in the project folder are project items; others would be links, which these projects do not have.</summary>
        public ProjectItem AddFromFile(string FileName)
        {
            var path = Path.GetFullPath(FileName);
            if (folder == null || !File.Exists(path) || !path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Only files inside the project folder belong to the project; copy the file there instead.");
            return new ProjectFolderItem(project, path, false, this);
        }

        public ProjectItem AddFromDirectory(string Directory) =>
            throw new NotSupportedException("Copy the folder into the project folder; the project shows what is on disk.");

        private string Target(string name)
        {
            if (folder == null) throw new NotSupportedException("A file has no project items.");
            var root = folder.TrimEnd('\\') + "\\";
            var target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException(name + " is outside the project folder.", nameof(name));
            return target;
        }
    }

    /// <summary>A file or folder on disk under the project folder, as a DTE <see cref="ProjectItem"/>.</summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class ProjectFolderItem : ProjectItem
    {
        private readonly KotlinProjectAutomation project;
        private readonly string path;
        private readonly bool directory;
        private readonly ProjectItems collection;

        internal ProjectFolderItem(KotlinProjectAutomation project, string path, bool directory, ProjectItems collection)
        {
            this.project = project;
            this.path = path;
            this.directory = directory;
            this.collection = collection;
        }

        public string Name { get => Path.GetFileName(path); set => throw new NotSupportedException("Rename the file on disk; the project follows the folder."); }
        // As in Visual Studio's own projects, a folder's path ends with a separator.
        public string get_FileNames(short index) => directory ? path.TrimEnd('\\') + "\\" : path;
        public short FileCount => 1;
        public string Kind => directory ? Constants.vsProjectItemKindPhysicalFolder : Constants.vsProjectItemKindPhysicalFile;
        public ProjectItems ProjectItems => new ProjectFolderItems(project, directory ? path : null, this);
        public ProjectItems Collection => collection;
        public Project ContainingProject => project;
#pragma warning disable VSTHRD010 // KotlinProjectAutomation.DTE switches to the UI thread itself (automation calls arrive on RPC threads).
        public DTE DTE => project.DTE;
#pragma warning restore VSTHRD010
        public Project SubProject => null!;
        public Properties Properties => null!;
        public object Object => null!;
        public Document Document => null!;
        public FileCodeModel FileCodeModel => null!;
        public ConfigurationManager ConfigurationManager => null!;
        public object get_Extender(string ExtenderName) => null!;
        public object ExtenderNames => Array.Empty<string>();
        public string ExtenderCATID => "";
        public bool IsDirty { get => false; set { } }
        public bool Saved { get => true; set { } }
        public bool get_IsOpen(string ViewKind = Constants.vsViewKindAny) => false;
        public Window Open(string ViewKind = Constants.vsViewKindPrimary)
        {
            if (directory) throw new NotSupportedException("A folder cannot be opened.");
            return Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                return DTE.ItemOperations.OpenFile(path, ViewKind);
            });
        }
        public void ExpandView() { }
        public void Save(string FileName = "") { }
        public bool SaveAs(string NewFileName) => throw new NotSupportedException("Save the document from its editor.");
        public void Remove() => throw new NotSupportedException("Delete the file on disk; the project follows the folder.");
        public void Delete() => throw new NotSupportedException("Delete the file on disk; the project follows the folder.");

        public override string ToString() => Name;
    }
}
