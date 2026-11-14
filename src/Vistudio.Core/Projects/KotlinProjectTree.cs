using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Vistudio.Core.Projects
{
    public enum ProjectTreeKind
    {
        Project, Module, SourceSet, SourceRoot, Folder, File, Dependencies, Configuration,
        Library, ProjectReference, FileReference, ScriptsFolder, Message,
        Toolchain, Tool, Frameworks, Framework, Declared, Targets, Target,
    }

    /// <summary>One node of the IDE project tree. Container children load lazily, so unexpanded folders are never enumerated.</summary>
    public sealed class ProjectTreeNode
    {
        private static readonly IReadOnlyList<ProjectTreeNode> None = Array.Empty<ProjectTreeNode>();
        private readonly Func<IReadOnlyList<ProjectTreeNode>>? load;
        private readonly bool showHidden;
        private IReadOnlyList<ProjectTreeNode>? children;

        public ProjectTreeNode(ProjectTreeKind kind, string caption, string? path = null,
            Func<IReadOnlyList<ProjectTreeNode>>? children = null, IEnumerable<KeyValuePair<string, string>>? details = null, bool showHidden = false)
        {
            Kind = kind;
            Caption = caption;
            Path = path;
            load = children;
            this.showHidden = showHidden;
            Details = (details ?? Enumerable.Empty<KeyValuePair<string, string>>()).Where(d => d.Value.Length != 0).ToArray();
        }

        public ProjectTreeKind Kind { get; }
        public string Caption { get; }
        /// <summary>Absolute file-system path for files, folders, source roots, build scripts and file dependencies.</summary>
        public string? Path { get; }
        public IReadOnlyList<KeyValuePair<string, string>> Details { get; }
        /// <summary>For <see cref="ProjectTreeKind.Target"/> nodes: the imported target, used by per-target Gradle commands.</summary>
        public KotlinTarget? Target { get; internal set; }
        /// <summary>Sibling identity used to keep IDE item ids stable across refreshes.</summary>
        public string Key => Kind + "|" + (Path ?? Caption);
        public bool IsFileSystemContainer => Kind == ProjectTreeKind.Folder || Kind == ProjectTreeKind.SourceRoot;
        public bool IsDocument => Kind == ProjectTreeKind.File;
        public IReadOnlyList<ProjectTreeNode> Children => children ?? (children = load?.Invoke() ?? None);

        public bool HasChildren
        {
            get
            {
                if (children != null || load == null) return Children.Count != 0;
                if (!IsFileSystemContainer) return Children.Count != 0;
                try { return Directory.EnumerateFileSystemEntries(Path!).Any(e => KotlinProjectTree.IsVisibleEntry(System.IO.Path.GetFileName(e), showHidden)); }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }
        }

        /// <summary>True when the node is the path or may contain it further down; used to locate documents without a full scan.</summary>
        public bool MayContain(string fullPath)
        {
            switch (Kind)
            {
                case ProjectTreeKind.Project: case ProjectTreeKind.Module: case ProjectTreeKind.SourceSet: case ProjectTreeKind.ScriptsFolder:
                case ProjectTreeKind.Targets: case ProjectTreeKind.Target:
                    return true;
                case ProjectTreeKind.Folder: case ProjectTreeKind.SourceRoot:
                    return KotlinProjectTree.SamePath(Path!, fullPath) || fullPath.StartsWith(Path!.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
                case ProjectTreeKind.File:
                    return KotlinProjectTree.SamePath(Path!, fullPath);
                default:
                    return false;
            }
        }
    }

    /// <summary>Builds the Solution Explorer view of a .kotlinproj from its Gradle import snapshot. Gradle stays the build source of truth.</summary>
    public static class KotlinProjectTree
    {
        public const int MaxEntriesPerFolder = 5000;
        private static readonly string[] SourceSetOrder = { "main", "test", "commonMain", "commonTest" };
        private static readonly string[] ConfigurationOrder = { "compileClasspath", "runtimeClasspath", "testCompileClasspath", "testRuntimeClasspath" };

        /// <param name="showHidden">Include files and folders whose name starts with '.'; hidden by default.</param>
        public static ProjectTreeNode Build(KotlinProjectFile project, bool showHidden = false)
        {
            var imported = project.ImportedAtUtc.Length != 0;
            var modules = Modules(project, imported);
            var sourceRoots = project.Items("SourceRoot", "GradleImport").ToArray();
            var configurations = project.Items("Configuration", "GradleImport").Where(c => (string?)c.Element("Status") == "Resolved").ToArray();
            var libraries = project.Items("Library", "GradleImport").ToArray();
            var top = new List<ProjectTreeNode>();
            if (!imported)
                top.Add(new ProjectTreeNode(ProjectTreeKind.Message, "Gradle model not imported. Right-click the project > Import Gradle Model."));
            var frameworks = project.Items("Framework", "GradleImport").ToArray();
            var targets = imported ? KotlinTarget.All(project) : Array.Empty<KotlinTarget>();
            var kotlinSets = project.Items("KotlinSourceSet", "GradleImport").ToArray();
            var startup = KotlinTarget.Find(targets, project.Get("StartupTarget"));
            foreach (var module in modules)
            {
                var sets = SourceSets(module, sourceRoots.Where(r => (string?)r.Element("Module") == module.GradlePath).ToArray(), showHidden,
                    kotlinSets.Where(s => (string?)s.Element("Module") == module.GradlePath).ToArray());
                var moduleConfigurations = configurations.Where(c => (string?)c.Element("Module") == module.GradlePath).ToArray();
                var moduleTargets = targets.Where(t => t.Module == module.GradlePath).ToList();
                // The aggregate root of a multi-module build usually has neither sources nor dependencies; its script stays under Gradle Scripts.
                if (module.GradlePath == ":" && modules.Count > 1 && sets.Count == 0 && moduleConfigurations.Length == 0 && moduleTargets.Count == 0) continue;
                ProjectTreeNode[] targetsNode = Array.Empty<ProjectTreeNode>();
                if (moduleTargets.Count != 0)
                {
                    // Multiplatform: each target owns its compilations' default source sets and dependency configurations;
                    // shared source sets (commonMain, nativeMain, webMain, ...) and other configurations stay on the module.
                    var allSets = sets;
                    var allConfigurations = moduleConfigurations;
                    var targetNodes = moduleTargets.Select(t => TargetNode(t, allSets.Where(s => t.SourceSets.Contains(s.Caption)).ToArray(),
                        allConfigurations.Where(c => t.Configurations.Contains((string)c.Attribute("Include")!)).ToArray(), libraries, t == startup)).ToList();
                    targetsNode = new[] { new ProjectTreeNode(ProjectTreeKind.Targets, "Targets", null, () => targetNodes, new[]
                    {
                        Detail("Targets", string.Join(", ", moduleTargets.Select(t => t.Name))), Detail("Origin", "Kotlin Multiplatform targets (Gradle import)"),
                    }) };
                    var owned = new HashSet<string>(moduleTargets.SelectMany(t => t.SourceSets), StringComparer.Ordinal);
                    var targetConfigurations = new HashSet<string>(moduleTargets.SelectMany(t => t.Configurations), StringComparer.Ordinal);
                    sets = sets.Where(s => !owned.Contains(s.Caption)).ToList();
                    moduleConfigurations = moduleConfigurations.Where(c => !targetConfigurations.Contains((string)c.Attribute("Include")!)).ToArray();
                }
                var dependencies = imported
                    ? new ProjectTreeNode(ProjectTreeKind.Dependencies, "Dependencies", null, () => Configurations(moduleConfigurations, libraries, module.GradlePath))
                    : new ProjectTreeNode(ProjectTreeKind.Dependencies, "Dependencies", null, () => new[]
                        { new ProjectTreeNode(ProjectTreeKind.Message, "Import the Gradle model to list resolved dependencies.") });
                var moduleFrameworks = Frameworks(frameworks.Where(f => (string?)f.Element("Module") == module.GradlePath));
                var content = sets.Concat(targetsNode).Concat(new[] { dependencies }).Concat(moduleFrameworks.Count == 0 ? Array.Empty<ProjectTreeNode>()
                    : new[] { new ProjectTreeNode(ProjectTreeKind.Frameworks, "Frameworks", null, () => moduleFrameworks) }).ToArray();
                top.Add(new ProjectTreeNode(ProjectTreeKind.Module, module.Caption, null, () => content, new[]
                {
                    Detail("Gradle path", module.GradlePath), Detail("Directory", module.Directory), Detail("Build file", module.BuildFile ?? ""),
                }));
            }
            var toolchain = Toolchain(project);
            if (toolchain.Count != 0) top.Add(new ProjectTreeNode(ProjectTreeKind.Toolchain, "Toolchain", null, () => toolchain));
            var declared = Declared(project);
            if (declared.Count != 0) top.Add(new ProjectTreeNode(ProjectTreeKind.Declared, "Declared Metadata", null, () => declared));
            var scripts = Scripts(project, modules);
            if (scripts.Count != 0) top.Add(new ProjectTreeNode(ProjectTreeKind.ScriptsFolder, "Gradle Scripts", null, () => scripts));
            return new ProjectTreeNode(ProjectTreeKind.Project, project.Name.Length != 0 ? project.Name : System.IO.Path.GetFileNameWithoutExtension(project.FilePath),
                project.FilePath, () => top, new[]
                {
                    Detail("Gradle root", project.GradleRoot), Detail("Imported", project.ImportedAtUtc), Detail("JDK", project.JdkVersion),
                    Detail("Project kind", project.ProjectKind), Detail("Startup target", startup?.Id ?? ""),
                });
        }

        private static ProjectTreeNode TargetNode(KotlinTarget target, ProjectTreeNode[] sets, XElement[] configurations, XElement[] libraries, bool isStartup)
        {
            var order = target.Configurations.ToList();
            var dependencies = new ProjectTreeNode(ProjectTreeKind.Dependencies, "Dependencies", null,
                () => Configurations(configurations, libraries, target.Module, order));
            var children = sets.Concat(new[] { dependencies }).ToArray();
            return new ProjectTreeNode(ProjectTreeKind.Target, target.Name + " (" + target.Platform + ")", null, () => children, new[]
            {
                Detail("Target", target.Id), Detail("Platform", target.Description), Detail("Compilations", string.Join(", ", target.Compilations)),
                Detail("Source sets", string.Join(", ", target.SourceSets)), Detail("Main compilation source sets", string.Join(", ", target.MainSourceSets)),
                Detail("Build tasks", string.Join(" ", target.BuildTasks)), Detail("Test tasks", string.Join(" ", target.TestTasks)),
                Detail("Run tasks", string.Join(" ", target.RunTasks)), Detail("Debugging", target.IsJvm ? "JVM debug adapter (F5)" : "Not supported; Ctrl+F5 runs through Gradle"),
                Detail("Startup target", isStartup ? "Yes" : ""), Detail("Origin", "Kotlin Multiplatform target (Gradle import)"),
            }) { Target = target };
        }

        private static string Value(XElement item, string name) => (string?)item.Element(name) ?? "";

        private static IReadOnlyList<ProjectTreeNode> Frameworks(IEnumerable<XElement> items) => items
            .GroupBy(f => (string)f.Attribute("Include")! + "" + Value(f, "Version"))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                var version = Value(first, "Version");
                return new ProjectTreeNode(ProjectTreeKind.Framework, (string)first.Attribute("Include")! + (version.Length != 0 ? " " + version : " (version unavailable)"), null, null, new[]
                {
                    Detail("Version", version), Detail("Kind", Value(first, "Kind")), Detail("Plugin id", Value(first, "PluginId")),
                    Detail("Configurations", string.Join(", ", g.Select(f => Value(f, "Configuration")).Where(c => c.Length != 0).Distinct())),
                    Detail("Evidence", Value(first, "Evidence")), Detail("Origin", Value(first, "Origin")),
                });
            }).ToList();

        private static IReadOnlyList<ProjectTreeNode> Toolchain(KotlinProjectFile project)
        {
            var result = project.Items("Tool", "DetectedTools").Select(t => new ProjectTreeNode(ProjectTreeKind.Tool,
                (string)t.Attribute("Include")! + " " + Value(t, "Version") + (Value(t, "Vendor").Length != 0 ? " (" + Value(t, "Vendor") + ")" : ""), null, null,
                t.Elements().Select(e => Detail(e.Name.LocalName, e.Value)))).ToList();
            if (project.Imported("GradleVersion").Length != 0)
                result.Add(new ProjectTreeNode(ProjectTreeKind.Tool, "Gradle " + project.Imported("GradleVersion") + " (last import)", null, null, new[]
                {
                    Detail("Imported", project.ImportedAtUtc), Detail("Origin", "Gradle model import"),
                }));
            if (project.Imported("JavaVersion").Length != 0)
                result.Add(new ProjectTreeNode(ProjectTreeKind.Tool, "Gradle JVM " + project.Imported("JavaVersion") + " (last import)", null, null, new[]
                {
                    Detail("Java home", project.Imported("JavaHome")), Detail("Origin", "Gradle model import"),
                }));
            return result;
        }

        /// <summary>Manually maintained Tool/Library/Framework entries; generated and detected groups are shown elsewhere.</summary>
        private static IReadOnlyList<ProjectTreeNode> Declared(KotlinProjectFile project) => project.ManualItems()
            .Where(i => i.Name == "Tool" || i.Name == "Library" || i.Name == "Framework")
            .Select(i => new ProjectTreeNode(ProjectTreeKind.Declared, i.Name.LocalName + ": " + (string?)i.Attribute("Include")
                + (Value(i, "Version").Length != 0 ? " " + Value(i, "Version") : ""), null, null,
                new[] { Detail("Origin", Value(i, "Origin").Length != 0 ? Value(i, "Origin") : "Manual metadata") }
                    .Concat(i.Elements().Where(e => e.Name != "Origin").Select(e => Detail(e.Name.LocalName, e.Value))))).ToList();

        public static IReadOnlyList<ProjectTreeNode> ListDirectory(string directory, bool showHidden = false)
        {
            string[] folders, files;
            try
            {
                folders = Directory.GetDirectories(directory).Where(d => IsVisibleEntry(System.IO.Path.GetFileName(d), showHidden)).ToArray();
                files = Directory.GetFiles(directory).Where(f => IsVisibleEntry(System.IO.Path.GetFileName(f), showHidden)).ToArray();
            }
            catch (IOException) { return Array.Empty<ProjectTreeNode>(); }
            catch (UnauthorizedAccessException) { return Array.Empty<ProjectTreeNode>(); }
            Array.Sort(folders, StringComparer.OrdinalIgnoreCase);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            var result = folders.Select(d => new ProjectTreeNode(ProjectTreeKind.Folder, System.IO.Path.GetFileName(d), d, () => ListDirectory(d, showHidden), null, showHidden))
                .Concat(files.Select(f => new ProjectTreeNode(ProjectTreeKind.File, System.IO.Path.GetFileName(f), f)))
                .Take(MaxEntriesPerFolder).ToList();
            var hidden = folders.Length + files.Length - result.Count;
            if (hidden > 0) result.Add(new ProjectTreeNode(ProjectTreeKind.Message, hidden + " more entries are not shown."));
            return result;
        }

        /// <summary>Names starting with '.' (.gradle, .idea, .editorconfig, ...) are hidden unless <paramref name="showHidden"/> is set.</summary>
        public static bool IsVisibleEntry(string name, bool showHidden = false) => name.Length != 0 && (showHidden || name[0] != '.');

        public static bool SamePath(string left, string right) =>
            string.Equals(left.TrimEnd('\\', '/'), right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        private static KeyValuePair<string, string> Detail(string name, string value) => new KeyValuePair<string, string>(name, value ?? "");

        private sealed class ModuleInfo
        {
            public string GradlePath = "", Caption = "", Directory = "";
            public string? BuildFile;
        }

        private static List<ModuleInfo> Modules(KotlinProjectFile project, bool imported)
        {
            var result = new List<ModuleInfo>();
            var items = imported ? project.Items("Module", "GradleImport").ToArray() : Array.Empty<XElement>();
            if (items.Length == 0) items = project.Items("Module", "Modules").ToArray();
            foreach (var item in items)
            {
                var gradlePath = (string?)item.Attribute("Include") ?? "";
                if (!gradlePath.StartsWith(":", StringComparison.Ordinal) || result.Any(m => m.GradlePath == gradlePath)) continue;
                var directory = project.ResolvePath((string?)item.Element("Path") ?? (gradlePath == ":" ? project.Get("GradleRoot", ".") : gradlePath.Substring(1).Replace(':', '\\')));
                var buildFile = (string?)item.Element("BuildFile");
                if (string.IsNullOrEmpty(buildFile) || !File.Exists(buildFile))
                    buildFile = new[] { "build.gradle.kts", "build.gradle" }.Select(n => System.IO.Path.Combine(directory, n)).FirstOrDefault(File.Exists);
                var name = (string?)item.Element("Name");
                result.Add(new ModuleInfo
                {
                    GradlePath = gradlePath, Directory = directory, BuildFile = buildFile,
                    Caption = gradlePath == ":" ? (string.IsNullOrEmpty(name) ? project.Name : name!) : gradlePath.Substring(1),
                });
            }
            return result.OrderBy(m => m.GradlePath == ":" ? 0 : 1).ThenBy(m => m.GradlePath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<ProjectTreeNode> SourceSets(ModuleInfo module, XElement[] roots, bool showHidden, XElement[] kotlinSets)
        {
            KeyValuePair<string, string>[] Hierarchy(string set)
            {
                var info = kotlinSets.FirstOrDefault(s => (string?)s.Attribute("Include") == set);
                return info == null ? Array.Empty<KeyValuePair<string, string>>() : new[]
                {
                    Detail("Depends on", ((string?)info.Element("DependsOn") ?? "").Replace(" ", ", ")),
                    Detail("Targets", ((string?)info.Element("Targets") ?? "").Replace(" ", ", ")),
                };
            }
            var origin = "Gradle source set";
            var entries = roots.Where(r => (string?)r.Element("Exists") != "false" && Directory.Exists((string)r.Attribute("Include")!))
                .Select(r => (Set: (string)r.Element("SourceSet")!, Path: (string)r.Attribute("Include")!, Kind: (string?)r.Element("Kind") ?? "Sources")).ToList();
            if (roots.Length == 0)
            {
                // Older snapshots and unimported projects: show the conventional src/<set>/<language> layout.
                origin = "Directory convention; import the Gradle model for configured source sets";
                var src = System.IO.Path.Combine(module.Directory, "src");
                if (Directory.Exists(src))
                    foreach (var set in Directory.GetDirectories(src).Where(d => IsVisibleEntry(System.IO.Path.GetFileName(d))))
                        foreach (var language in new[] { ("kotlin", "Sources"), ("java", "Sources"), ("resources", "Resources") })
                        {
                            var path = System.IO.Path.Combine(set, language.Item1);
                            if (Directory.Exists(path)) entries.Add((System.IO.Path.GetFileName(set), path, language.Item2));
                        }
            }
            return entries.GroupBy(e => e.Set, StringComparer.Ordinal)
                .OrderBy(g => Array.IndexOf(SourceSetOrder, g.Key) is var i && i >= 0 ? i : SourceSetOrder.Length).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var children = g.OrderBy(e => e.Kind == "Sources" ? 0 : 1).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                        .Select(e => (ProjectTreeNode)new ProjectTreeNode(ProjectTreeKind.SourceRoot, Relative(module.Directory, e.Path), e.Path,
                            () => ListDirectory(e.Path, showHidden), new[] { Detail("Content", e.Kind), Detail("Source set", g.Key), Detail("Origin", origin) }, showHidden)).ToArray();
                    return new ProjectTreeNode(ProjectTreeKind.SourceSet, g.Key, null, () => children, Hierarchy(g.Key).Concat(new[] { Detail("Origin", origin) }));
                }).ToList();
        }

        private static IReadOnlyList<ProjectTreeNode> Configurations(XElement[] configurations, XElement[] libraries, string module, IList<string>? order = null)
        {
            var preferred = order ?? ConfigurationOrder;
            var nodes = configurations
                .OrderBy(c => preferred.IndexOf((string)c.Attribute("Include")!) is var i && i >= 0 ? i : preferred.Count)
                .ThenBy(c => ((string)c.Attribute("Include")!).StartsWith("buildscript:", StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(c => (string)c.Attribute("Include")!, StringComparer.OrdinalIgnoreCase)
                .Select(c =>
                {
                    var name = (string)c.Attribute("Include")!;
                    var members = libraries.Where(l => (string?)l.Element("Module") == module && (string?)l.Element("Configuration") == name)
                        .OrderBy(l => (string?)l.Element("Relationship") == "Direct" ? 0 : 1)
                        .ThenBy(l => (string)l.Attribute("Include")!, StringComparer.OrdinalIgnoreCase).Select(Library).ToArray();
                    return new ProjectTreeNode(ProjectTreeKind.Configuration, name + " (" + members.Length + ")", null, () => members,
                        new[] { Detail("Module", module), Detail("Status", (string?)c.Element("Status") ?? ""), Detail("Origin", (string?)c.Element("Origin") ?? "") });
                }).ToList();
            if (nodes.Count == 0) nodes.Add(new ProjectTreeNode(ProjectTreeKind.Message, "No resolved configurations."));
            return nodes;
        }

        private static ProjectTreeNode Library(XElement library)
        {
            string Value(string name) => (string?)library.Element(name) ?? "";
            var include = (string)library.Attribute("Include")!;
            var transitive = Value("Relationship") != "Direct" && Value("Relationship") != "File" ? "  (transitive)" : "";
            var details = new[]
            {
                Detail("Coordinates", include), Detail("Selected version", Value("Version")), Detail("Requested", Value("Requested")),
                Detail("Relationship", Value("Relationship")), Detail("Selection reason", Value("SelectionReason")),
                Detail("Configuration", Value("Configuration")), Detail("Origin", Value("Origin")),
            };
            switch (Value("Kind"))
            {
                case "Project":
                    var path = Value("ProjectPath").Length != 0 ? Value("ProjectPath") : include;
                    var build = Value("BuildTreePath").Length != 0 && Value("BuildTreePath") != path ? " [" + Value("BuildTreePath") + "]" : "";
                    return new ProjectTreeNode(ProjectTreeKind.ProjectReference, path + build + transitive, null, null, details);
                case "File":
                    var file = Value("Path").Length != 0 ? Value("Path") : include;
                    return new ProjectTreeNode(ProjectTreeKind.FileReference, System.IO.Path.GetFileName(file), file, null, details);
                default:
                    return new ProjectTreeNode(ProjectTreeKind.Library, include + (Value("Version").Length != 0 ? ":" + Value("Version") : "") + transitive, null, null, details);
            }
        }

        private static IReadOnlyList<ProjectTreeNode> Scripts(KotlinProjectFile project, List<ModuleInfo> modules)
        {
            var root = project.GradleRoot;
            var result = new List<ProjectTreeNode>();
            void Add(string path, string caption)
            {
                if (File.Exists(path) && !result.Any(n => SamePath(n.Path!, path)))
                    result.Add(new ProjectTreeNode(ProjectTreeKind.File, caption, System.IO.Path.GetFullPath(path)));
            }
            foreach (var name in new[] { "settings.gradle.kts", "settings.gradle" }) Add(System.IO.Path.Combine(root, name), name);
            foreach (var module in modules.Where(m => m.BuildFile != null))
                Add(module.BuildFile!, System.IO.Path.GetFileName(module.BuildFile) + " (" + (module.GradlePath == ":" ? "root" : module.Caption) + ")");
            Add(System.IO.Path.Combine(root, "gradle.properties"), "gradle.properties");
            Add(System.IO.Path.Combine(root, "gradle", "libs.versions.toml"), "libs.versions.toml");
            Add(System.IO.Path.Combine(root, "gradle", "wrapper", "gradle-wrapper.properties"), "gradle-wrapper.properties");
            return result;
        }

        private static string Relative(string baseDirectory, string path)
        {
            var prefix = baseDirectory.TrimEnd('\\', '/') + "\\";
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path.Substring(prefix.Length).Replace('\\', '/') : path;
        }
    }
}
