using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Threading;
using Vistudio.Core.Packages;
using Vistudio.Packages;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Vistudio.Core;
using Vistudio.Core.Projects;

namespace Vistudio
{
    /// <summary>
    /// Read-only Solution Explorer hierarchy for one .kotlinproj: Gradle modules, source sets with on-disk files,
    /// resolved dependencies and Gradle scripts. The hierarchy mechanics are in <see cref="TreeProjectNode"/>.
    /// </summary>
    internal sealed class KotlinProjectNode : TreeProjectNode
    {
        private static readonly ImageMoniker KotlinMoniker = new ImageMoniker { Guid = new Guid("6cd0eb83-fd71-470c-9717-dfa55d167c7b"), Id = 1 };
        private readonly KotlinPackage package;
        private readonly PackageService packages;

        public KotlinProjectNode(KotlinPackage package, string filePath) : base(package, filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.package = package;
            packages = (package.GetServiceOnMainThread(typeof(SComponentModel)) as IComponentModel
                ?? throw new InvalidOperationException("Visual Studio component model is unavailable.")).GetService<PackageService>();
            Metadata = KotlinProjectFile.Load(filePath);
            Configurations = KotlinConfigProvider.Create(this, package);
            Initialize(Metadata.Get("ProjectGuid"));
            TreeSettingsChanged += OnTreeSettingsChangedHandler;
            packages.Changed += OnPackagesChanged;
            // Gradle version checks start a Gradle run each; they wait for the user's Check for Updates instead of every project open.
            package.Environment.ProjectOpened(FilePath);
        }

        /// <summary>Raised on the UI thread when a global option that shapes project trees changes, e.g. Show hidden files.</summary>
        private static event Action? TreeSettingsChanged;

        internal static void OnTreeSettingsChanged() => TreeSettingsChanged?.Invoke();

        private void OnTreeSettingsChangedHandler() => ScheduleRefresh(false);

        protected override ProjectTreeNode BuildTree()
        {
            bool showHidden;
            try { showHidden = KotlinSettings.Load().ShowHiddenFiles; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException || e is System.Xml.XmlException)
            {
                package.Environment.Log("Kotlin project: settings were not read, hidden files stay hidden: " + e.Message);
                showHidden = false;
            }
            var tree = KotlinProjectTree.Build(Metadata, showHidden);
            // Each Gradle build file of the project gets a Packages node with its declared dependencies and newer versions.
            manifests = GradleManifests();
            if (manifests.Count == 0) return tree;
            var packageNodes = manifests.Select(m =>
            {
                var state = packages.Get(m);
                var node = PackageManagers.Node(m, state.Packages, state.Status);
                return manifests.Count == 1 ? node : Rename(node, node.Caption + " – " + ModuleName(m));
            }).ToList();
            return new ProjectTreeNode(tree.Kind, tree.Caption, tree.Path, () => packageNodes.Concat(tree.Children).ToList(), tree.Details);
        }

        private IReadOnlyList<PackageManifestRef> manifests = Array.Empty<PackageManifestRef>();

        private IReadOnlyList<PackageManifestRef> GradleManifests()
        {
            var gradle = PackageManagers.For(PackageEcosystem.Gradle);
            var folders = Metadata.Items("Module").Select(m => (string?)m.Element("Path")).Where(p => !string.IsNullOrEmpty(p))
                .Select(p => Metadata.ResolvePath(Path.Combine(Metadata.GradleRoot, p!))).Append(Metadata.GradleRoot)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            return folders.Select(f => PackageManagers.Find(gradle, f)).Where(m => m != null).Select(m => m!)
                .GroupBy(m => m.ManifestPath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        }

        private string ModuleName(PackageManifestRef manifest)
        {
            var relative = manifest.Directory.Length > Metadata.GradleRoot.Length ? manifest.Directory.Substring(Metadata.GradleRoot.TrimEnd('\\').Length).Trim('\\') : "";
            return relative.Length == 0 ? "root" : relative.Replace('\\', '/');
        }

        private static ProjectTreeNode Rename(ProjectTreeNode node, string caption) =>
            new ProjectTreeNode(node.Kind, caption, node.Path, () => node.Children, node.Details) { Manifest = node.Manifest, Identity = node.Identity };

        private void OnPackagesChanged(string manifestPath)
        {
            if (!manifests.Any(m => KotlinProjectTree.SamePath(m.ManifestPath, manifestPath))) return;
            package.JoinableTaskFactory.RunAsync(async () =>
            {
                await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                RefreshTree();
            }).FileAndForget("Vistudio/Kotlin/PackagesChanged");
        }

        // Build file edits change the declared packages.
        protected override bool IsWatchedContent(string path) =>
            manifests.Any(m => KotlinProjectTree.SamePath(m.ManifestPath, path)) || Path.GetFileName(path).Equals("libs.versions.toml", StringComparison.OrdinalIgnoreCase);

        protected override void ReloadMetadata() => Metadata = KotlinProjectFile.Load(FilePath);

        protected override IEnumerable<string> WatchedDirectories => new[] { Metadata.GradleRoot };

        protected override Guid ProjectTypeGuid => new Guid(KotlinProjectFactory.Id);

        protected override string TypeName => "Kotlin";

        protected override IVsCfgProvider2 ConfigurationProvider => Configurations;

        protected override void Log(string message) => package.Environment.Log(message);

        // The project node edits .kotlinproj settings in the Properties window; other nodes show read-only details.
        protected override object CreateBrowseObject(uint itemid, ProjectTreeNode model) =>
            itemid == Root ? new KotlinProjectProperties(Metadata, package.Environment.Log, MetadataSaved) { Hierarchy = this } : (object)new KotlinNodeProperties(model);

        protected override void OnClosed()
        {
            TreeSettingsChanged -= OnTreeSettingsChangedHandler;
            packages.Changed -= OnPackagesChanged;
            Configurations.CancelBuilds();
            package.Environment.ProjectClosed(FilePath);
        }

        protected override bool TryShowContextMenu(uint itemid, ProjectTreeNode model, IVsUIShell shell, POINTS[] points, out int result)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            result = VSConstants.S_OK;
            if (model.Target == null) return false;
            var targetMenu = KotlinPackage.Commands;
            result = shell.ShowContextMenu(0, ref targetMenu, KotlinPackage.TargetMenu, points, null);
            return true;
        }

        /// <summary>The Kotlin target of a Targets/&lt;target&gt; node, for the target context commands.</summary>
        internal KotlinTarget? TargetAt(uint itemid) => ModelAt(itemid)?.Target;

        public KotlinProjectFile Metadata { get; private set; } = null!;
        internal ProjectConfigProvider Configurations { get; }

        /// <summary>Input snapshot taken when the last successful solution Build/Rebuild started; cleared by Clean or failure.</summary>
        internal GradleInputs.Snapshot? LastSuccessfulBuild { get; set; }

        /// <summary>Fast IDE check used by solution builds and F5: input timestamps only, never file contents.</summary>
        internal bool IsUpToDate()
        {
            var root = Metadata.GradleRoot;
            var started = LastSuccessfulBuild;
            try { return GradleInputs.IsUpToDate(root, started); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return false; }
        }

        protected override ImageMoniker? NodeMoniker(ProjectTreeNode model, bool open)
        {
            switch (model.Kind)
            {
                case ProjectTreeKind.Project: return KotlinMoniker;
                case ProjectTreeKind.Module: return KnownMonikers.Module;
                case ProjectTreeKind.SourceSet: case ProjectTreeKind.SourceRoot: case ProjectTreeKind.Folder:
                    return open ? KnownMonikers.FolderOpened : KnownMonikers.FolderClosed;
                case ProjectTreeKind.Dependencies: return KnownMonikers.ReferenceGroup;
                case ProjectTreeKind.Configuration: return KnownMonikers.Library;
                case ProjectTreeKind.Library: return KnownMonikers.Package;
                case ProjectTreeKind.ProjectReference: return KnownMonikers.Application;
                case ProjectTreeKind.FileReference: return KnownMonikers.Reference;
                case ProjectTreeKind.ScriptsFolder: return KnownMonikers.BuildDefinition;
                case ProjectTreeKind.Message: return KnownMonikers.StatusInformation;
                case ProjectTreeKind.Toolchain: case ProjectTreeKind.Tool: return KnownMonikers.Settings;
                case ProjectTreeKind.Frameworks: case ProjectTreeKind.Framework: return KnownMonikers.Component;
                case ProjectTreeKind.Declared: return KnownMonikers.ProjectFilterFile;
                case ProjectTreeKind.Targets: return KnownMonikers.ApplicationGroup;
                case ProjectTreeKind.Target:
                    switch (model.Target?.Platform)
                    {
                        case "JVM": return KnownMonikers.JavaSource;
                        case "Android": return KnownMonikers.Android;
                        case "JS": return KnownMonikers.JSScript;
                        case "Wasm": return KnownMonikers.WebApplication;
                        case "Native": return KnownMonikers.Processor;
                        default: return KnownMonikers.Application;
                    }
                default:
                    return null;
            }
        }
    }
}
