using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Vistudio.Core;
using Vistudio.Core.Languages;
using Vistudio.Core.Packages;
using Vistudio.Core.Projects;
using Vistudio.Packages;

namespace Vistudio.Languages
{
    /// <summary>
    /// Solution Explorer hierarchy of one language project (.rustproj, .zigproj, .dartproj, .cproj, .cppproj, .wasmproj,
    /// .javaproj): the language's Packages node, then the project folder. The language's own tool builds and runs.
    /// </summary>
    internal sealed class LanguageProjectNode : TreeProjectNode
    {
        private readonly LanguagesPackage package;
        private readonly PackageService packages;
        private IReadOnlyList<PackageManifestRef> manifests = Array.Empty<PackageManifestRef>();

        public LanguageProjectNode(LanguagesPackage package, PackageService packages, string filePath) : base(package, filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.package = package;
            this.packages = packages;
            Metadata = LanguageProjectFile.Load(filePath);
            Configurations = new ProjectConfigProvider((name, platform) => new LanguageProjectConfig(this, package, name, platform));
            manifests = Metadata.FindManifests();
            Initialize(Metadata.ProjectGuid);
            packages.Changed += OnPackagesChanged;
            // Outdated marks are part of what the tree shows, so each manifest is checked once in the background.
            foreach (var manifest in manifests) packages.CheckForUpdates(manifest, configuredTool: Metadata.Get("PackageTool"));
        }

        public LanguageProjectFile Metadata { get; private set; }
        internal ProjectConfigProvider Configurations { get; }
        internal LanguageInfo Language => Metadata.Language;

        /// <summary>Set by a successful Build/Rebuild, cleared by Clean or a failure; edits are the tool's business.</summary>
        internal DateTime? LastSuccessfulBuildUtc { get; set; }

        private void OnPackagesChanged(string manifestPath)
        {
            if (!manifests.Any(m => KotlinProjectTree.SamePath(m.ManifestPath, manifestPath))) return;
            package.JoinableTaskFactory.RunAsync(async () =>
            {
                await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                RefreshTree();
            }).FileAndForget("Vistudio/Languages/PackagesChanged");
        }

        protected override ProjectTreeNode BuildTree()
        {
            manifests = Metadata.FindManifests();
            var nodes = manifests.Select(m =>
            {
                var state = packages.Get(m);
                return PackageManagers.Node(m, state.Packages, state.Status);
            });
            return LanguageProjectTree.Build(Metadata, nodes);
        }

        protected override void ReloadMetadata() => Metadata = LanguageProjectFile.Load(FilePath);

        protected override IEnumerable<string> WatchedDirectories => Enumerable.Empty<string>();

        // Manifest and lock edits by the managers or by hand change the packages shown.
        protected override bool IsWatchedContent(string path)
        {
            var name = Path.GetFileName(path);
            return manifests.Any(m => KotlinProjectTree.SamePath(m.ManifestPath, path))
                || name.Equals("Cargo.lock", StringComparison.OrdinalIgnoreCase) || name.Equals("pubspec.lock", StringComparison.OrdinalIgnoreCase)
                || name.Equals("conan.lock", StringComparison.OrdinalIgnoreCase) || name.Equals("wkg.lock", StringComparison.OrdinalIgnoreCase)
                || PackageManagers.For(Language.Packages).ManifestNames.Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        protected override Guid ProjectTypeGuid => new Guid(LanguageProjectFactory.Id);

        protected override string TypeName => Language.DisplayName;

        protected override IVsCfgProvider2 ConfigurationProvider => Configurations;

        protected override void Log(string message) => package.Log(Language.DisplayName + " project: " + message);

        protected override object CreateBrowseObject(uint itemid, ProjectTreeNode model) =>
            itemid == Root ? new LanguageProjectProperties(Metadata, Log, MetadataSaved) : (object)new KotlinNodeProperties(model);

        protected override ImageMoniker? NodeMoniker(ProjectTreeNode model, bool open)
        {
            switch (model.Kind)
            {
                case ProjectTreeKind.Project:
                    return Language.IconId == 0 ? KnownMonikers.Application : new ImageMoniker { Guid = LanguageCatalog.IconGuid, Id = Language.IconId };
                case ProjectTreeKind.Folder:
                    return open ? KnownMonikers.FolderOpened : KnownMonikers.FolderClosed;
                case ProjectTreeKind.Message:
                    return KnownMonikers.StatusInformation;
                default:
                    return null;
            }
        }

        protected override void OnClosed()
        {
            packages.Changed -= OnPackagesChanged;
            Configurations.CancelBuilds();
            package.Projects.Closed(this);
        }
    }
}
