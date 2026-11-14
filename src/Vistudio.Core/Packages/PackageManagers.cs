using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vistudio.Core.Projects;

namespace Vistudio.Core.Packages
{
    /// <summary>A manifest found in a project and the manager that owns it.</summary>
    public sealed class PackageManifestRef
    {
        public PackageManifestRef(IPackageManager manager, string manifestPath)
        {
            Manager = manager ?? throw new ArgumentNullException(nameof(manager));
            ManifestPath = Path.GetFullPath(manifestPath);
        }

        public IPackageManager Manager { get; }
        public string ManifestPath { get; }
        public string Directory => Path.GetDirectoryName(ManifestPath)!;
    }

    public static class PackageManagers
    {
        public static IReadOnlyList<IPackageManager> All { get; } = new IPackageManager[]
        {
            new PubPackageManager(), new CargoPackageManager(), new GradlePackageManager(),
            new ConanPackageManager(), new ZigPackageManager(), new WkgPackageManager(),
        };

        public static IPackageManager For(PackageEcosystem ecosystem) => All.First(m => m.Ecosystem == ecosystem);

        /// <summary>The manifest of <paramref name="manager"/> in <paramref name="folder"/> itself (no search below or above).</summary>
        public static PackageManifestRef? Find(IPackageManager manager, string folder)
        {
            foreach (var name in manager.ManifestNames)
            {
                var path = Path.Combine(folder, name);
                if (File.Exists(path)) return new PackageManifestRef(manager, path);
            }
            return null;
        }

        /// <summary>
        /// The executable of <paramref name="manager"/>: an explicit setting when it exists, else the first of its tool
        /// names in <paramref name="extraFolders"/> (project or SDK folders), then on PATH with PATHEXT.
        /// </summary>
        public static string? FindTool(IPackageManager manager, string? configured, IEnumerable<string>? extraFolders = null)
        {
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.IsPathRooted(configured) && File.Exists(configured) ? configured : null;
            return ToolLocator.Find(manager.ToolNames, extraFolders);
        }

        /// <summary>The Solution Explorer node for one manifest: packages with versions, outdated marks and a status line.</summary>
        public static ProjectTreeNode Node(PackageManifestRef manifest, IReadOnlyList<PackageEntry>? packages, string? status)
        {
            var list = packages ?? Array.Empty<PackageEntry>();
            var outdated = list.Count(p => p.IsOutdated);
            var caption = "Packages (" + manifest.Manager.DisplayName + ")" + (outdated == 0 ? "" : " – " + outdated + " outdated");
            var details = new[]
            {
                new KeyValuePair<string, string>("Manager", manifest.Manager.DisplayName),
                new KeyValuePair<string, string>("Manifest", manifest.ManifestPath),
                new KeyValuePair<string, string>("Status", status ?? ""),
            };
            return new ProjectTreeNode(ProjectTreeKind.Packages, caption, null, () =>
            {
                var children = new List<ProjectTreeNode>();
                if (!string.IsNullOrEmpty(status)) children.Add(new ProjectTreeNode(ProjectTreeKind.Message, status!));
                children.AddRange(list.OrderBy(p => p.Scope).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(PackageNode));
                return children;
            }, details) { Manifest = manifest, Identity = manifest.ManifestPath };
        }

        public static ProjectTreeNode PackageNode(PackageEntry package) =>
            new ProjectTreeNode(ProjectTreeKind.Package, package.Caption, null, null, new[]
            {
                new KeyValuePair<string, string>("Name", package.Name),
                new KeyValuePair<string, string>("Requested", package.Requested),
                new KeyValuePair<string, string>("Current", package.Current ?? ""),
                new KeyValuePair<string, string>("Compatible", package.Compatible ?? ""),
                new KeyValuePair<string, string>("Latest", package.Latest ?? ""),
                new KeyValuePair<string, string>("Scope", package.Scope.ToString()),
                new KeyValuePair<string, string>("Section", package.Group),
                new KeyValuePair<string, string>("Source", package.Source ?? ""),
                new KeyValuePair<string, string>("Declared in", package.ManifestPath + (package.Line > 0 ? "(" + package.Line + ")" : "")),
            }) { Package = package, Identity = package.Name + "|" + package.Group };
    }

    /// <summary>
    /// Finds executables the way cmd.exe would (PATH and PATHEXT), without starting a shell. Shared by the package
    /// managers and the language project commands; the older Zig, Dart and mise lookups keep their own rules for a
    /// configured folder.
    /// </summary>
    public static class ToolLocator
    {
        public static string? Find(IEnumerable<string> names, IEnumerable<string>? extraFolders = null)
        {
            var folders = (extraFolders ?? Enumerable.Empty<string>())
                .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                .Select(f => f.Trim().Trim('"')).Where(f => f.Length != 0 && Path.IsPathRooted(f)).ToList();
            var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';')
                .Where(e => e.Length != 0).ToList();
            foreach (var name in names)
                foreach (var folder in folders)
                {
                    if (Path.HasExtension(name) && TryFile(Path.Combine(folder, name)) is string direct) return direct;
                    foreach (var extension in extensions)
                        if (TryFile(Path.Combine(folder, name + extension)) is string found) return found;
                }
            return null;
        }

        private static string? TryFile(string path)
        {
            try { return File.Exists(path) ? Path.GetFullPath(path) : null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }
    }
}
