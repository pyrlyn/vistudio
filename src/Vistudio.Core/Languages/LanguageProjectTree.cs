using System;
using System.Collections.Generic;
using System.Linq;
using Vistudio.Core.Projects;

namespace Vistudio.Core.Languages
{
    /// <summary>
    /// Solution Explorer view of a language project: one Packages node per manifest (from the language's own package
    /// manager), then the project folder as it is on disk, without the build output and tool caches.
    /// </summary>
    public static class LanguageProjectTree
    {
        /// <summary>Top-level folders that hold build output or downloaded dependencies, per the languages' own tools.</summary>
        public static readonly string[] OutputFolders = { "target", "zig-out", ".zig-cache", "zig-cache", ".dart_tool", "build", "out", "node_modules", ".gradle" };

        /// <param name="packages">The Packages node of each manifest, already holding the versions known so far.</param>
        public static ProjectTreeNode Build(LanguageProjectFile project, IEnumerable<ProjectTreeNode> packages, bool showHidden = false)
        {
            var packageNodes = packages.ToList();
            return new ProjectTreeNode(ProjectTreeKind.Project, project.Name, null, () =>
                packageNodes.Concat(KotlinProjectTree.ListDirectory(project.DirectoryPath, showHidden)
                    .Where(n => !(n.Kind == ProjectTreeKind.Folder && OutputFolders.Contains(n.Caption, StringComparer.OrdinalIgnoreCase)))).ToList(),
                new[]
                {
                    new KeyValuePair<string, string>("Language", project.Language.DisplayName),
                    new KeyValuePair<string, string>("Project file", project.FilePath),
                });
        }
    }
}
