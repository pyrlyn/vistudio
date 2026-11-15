using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Vistudio
{
    /// <summary>
    /// The project file of a project template made with Project &gt; Export Template: a copy of a real Vistudio project file,
    /// not one of the extension's own template stubs. For a project template the template engine writes the project file and
    /// the template's files (parameters replaced) into a temporary folder, asks the project factory to create the project
    /// from that project file, then deletes the folder; like MSBuild project systems, the factory copies the files along.
    /// </summary>
    internal static class ExportedProjectTemplate
    {
        /// <summary>
        /// Writes <paramref name="folder"/>\<paramref name="name"/> plus the template file's extension with the new project
        /// name and a new project GUID, and returns its path. <paramref name="generatedLabels"/> name groups that describe the
        /// original folder (a Kotlin project's Gradle import), which are dropped so the new project imports its own.
        /// </summary>
        internal static string Clone(string templateFile, string folder, string name, params string[] generatedLabels)
        {
            var document = XDocument.Load(templateFile, LoadOptions.PreserveWhitespace);
            var root = document.Root;
            if (root == null || root.Name.LocalName != "Project" || root.Attribute("VistudioSchemaVersion") == null)
                throw new NotSupportedException(Path.GetFileName(templateFile) + " is not a Vistudio project file.");
            var properties = root.Elements("PropertyGroup").FirstOrDefault(e => (string?)e.Attribute("Label") == "Vistudio")
                ?? throw new NotSupportedException(Path.GetFileName(templateFile) + " has no Vistudio property group.");
            Set(properties, "ProjectName", name);
            // Always new: a template keeps the exported project's GUID when the wizard did not parameterize it.
            Set(properties, "ProjectGuid", Guid.NewGuid().ToString("B"));
            root.Elements().Where(e => generatedLabels.Contains((string?)e.Attribute("Label"))).Remove();

            var target = Path.Combine(Path.GetFullPath(folder), name + Path.GetExtension(templateFile));
            var source = Path.GetFullPath(templateFile);
            var inPlace = string.Equals(target, source, StringComparison.OrdinalIgnoreCase);
            if (!inPlace && File.Exists(target)) throw new IOException(target + " already exists.");
            var sourceFolder = Path.GetDirectoryName(source)!;
            var targetFolder = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(targetFolder);
            document.Save(target);
            if (string.Equals(sourceFolder, targetFolder, StringComparison.OrdinalIgnoreCase))
            {
                // A template engine that writes the project straight into the destination leaves the file under the exported name.
                if (!inPlace) File.Delete(source);
            }
            else CopyContent(sourceFolder, targetFolder, source);
            return target;
        }

        /// <summary>The template's files, as the template engine prepared them; existing files are kept.</summary>
        private static void CopyContent(string from, string to, string projectFile)
        {
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (string.Equals(file, projectFile, StringComparison.OrdinalIgnoreCase) || name.EndsWith(".vstemplate", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("__TemplateIcon.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("__PreviewImage.", StringComparison.OrdinalIgnoreCase))
                    continue;
                var target = Path.Combine(to, file.Substring(from.TrimEnd('\\').Length + 1));
                if (File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, directory.Substring(from.TrimEnd('\\').Length + 1)));
        }

        private static void Set(XElement group, string name, string value)
        {
            var element = group.Element(name);
            if (element == null) group.Add(element = new XElement(name));
            element.Value = value;
        }
    }
}
