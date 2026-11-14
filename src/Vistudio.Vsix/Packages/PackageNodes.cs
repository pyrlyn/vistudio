using System;
using System.Linq;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Vistudio.Core.Projects;

namespace Vistudio.Packages
{
    /// <summary>Icons and context menus of the Packages nodes, shared by every project type's hierarchy.</summary>
    internal static class PackageNodes
    {
        /// <summary>Command set of LanguageCommands.vsct (guidLanguageCommands).</summary>
        internal static readonly Guid Commands = new Guid("c386fa11-6735-43f2-9cfe-2dec11a8d70a");
        internal const int PackagesMenu = 0x2000, PackageMenu = 0x2001;

        public static ImageMoniker? Moniker(ProjectTreeNode model, bool open)
        {
            switch (model.Kind)
            {
                case ProjectTreeKind.Packages:
                    var failed = model.Details.Any(d => d.Key == "Status" && d.Value.StartsWith("Version check failed", StringComparison.Ordinal));
                    return failed ? KnownMonikers.ReferenceGroupWarning : open ? KnownMonikers.PackageFolderOpened : KnownMonikers.PackageFolderClosed;
                case ProjectTreeKind.Package:
                    return model.Package?.IsOutdated == true ? KnownMonikers.StatusUpdateAvailable : KnownMonikers.Package;
                default:
                    return null;
            }
        }

        /// <summary>The context menu id for a package node; null for other nodes.</summary>
        public static int? Menu(ProjectTreeNode model) =>
            model.Kind == ProjectTreeKind.Packages && model.Manifest != null ? PackagesMenu
            : model.Kind == ProjectTreeKind.Package && model.Package != null ? PackageMenu
            : (int?)null;
    }
}
