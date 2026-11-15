using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Vistudio.Core.Projects;
using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace Vistudio
{
    /// <summary>
    /// Read-only Solution Explorer hierarchy over a <see cref="ProjectTreeNode"/> model: items are materialized lazily,
    /// reconciled on disk changes (ids of unchanged items survive, so expansion and selection do too), and documents open
    /// in their standard editors. Shared by the Kotlin project and the language projects; each supplies its metadata,
    /// tree, icons, browse objects and configurations. The project's own tool stays the build system.
    /// </summary>
    internal abstract class TreeProjectNode : IVsUIHierarchy, IVsProject, IVsPersistHierarchyItem, IVsGetCfgProvider
    {
        protected const uint Root = VSConstants.VSITEMID_ROOT;
        private const uint Nil = VSConstants.VSITEMID_NIL;
        private static readonly string[] IgnoredSegments = { "\\build\\", "\\.gradle\\", "\\.kotlin\\", "\\.idea\\", "\\.vs\\", "\\out\\", "\\.git\\",
            "\\target\\", "\\zig-out\\", "\\.zig-cache\\", "\\zig-cache\\", "\\.dart_tool\\", "\\node_modules\\" };

        protected readonly AsyncPackage Package;
        private readonly Dictionary<uint, Item> items = new Dictionary<uint, Item>();
        private readonly Dictionary<uint, IVsHierarchyEvents> sinks = new Dictionary<uint, IVsHierarchyEvents>();
        private readonly Dictionary<int, object> parentProperties = new Dictionary<int, object>();
        private readonly Dictionary<string, ImageMoniker> fileMonikers = new Dictionary<string, ImageMoniker>(StringComparer.OrdinalIgnoreCase);
        private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private readonly object refreshLock = new object();
        private Timer? refreshTimer;
        private Item root = null!;
        private uint nextItemId = 1, nextSinkCookie = 1;
        private bool reloadMetadataPending, closed;
        private IOleServiceProvider? site;
        private KotlinProjectAutomation? automation;
        protected Guid ProjectGuid;

        protected TreeProjectNode(AsyncPackage package, string filePath)
        {
            Package = package;
            FilePath = filePath;
        }

        /// <summary>Builds the first tree and starts watching; called by the derived constructor once its metadata is loaded.</summary>
        protected void Initialize(string? projectGuid)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!Guid.TryParse(projectGuid, out ProjectGuid)) ProjectGuid = Guid.NewGuid();
            root = new Item(Root, null, BuildTree());
            items[Root] = root;
            Open[FilePath] = this;
            refreshTimer = new Timer(_ => Package.JoinableTaskFactory.RunAsync(async () =>
            {
                await Package.JoinableTaskFactory.SwitchToMainThreadAsync();
                Refresh();
            }).Task.Forget(), null, Timeout.Infinite, Timeout.Infinite);
            Watch();
        }

        /// <summary>Open project nodes by project file, for commands that receive the shell's view of a selected hierarchy.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TreeProjectNode> Open =
            new System.Collections.Concurrent.ConcurrentDictionary<string, TreeProjectNode>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// This extension's node behind a selected hierarchy. The selection may hand out the shell's wrapper of the
        /// project rather than the node itself, so the wrapper's root canonical name (the project file) finds it.
        /// </summary>
        internal static TreeProjectNode? From(object? hierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (hierarchy is TreeProjectNode node) return node;
            if (hierarchy is IVsHierarchy other && ErrorHandler.Succeeded(other.GetCanonicalName(Root, out var name)) && name != null
                && Open.TryGetValue(Path.GetFullPath(name), out var found)) return found;
            return null;
        }

        public string FilePath { get; }
        public string ProjectDirectory => Path.GetDirectoryName(FilePath)!;
        public string Name => root.Model.Caption;

        protected abstract ProjectTreeNode BuildTree();

        /// <summary>Reloads the project file; throws IO/XML/data exceptions, which keep the last valid tree.</summary>
        protected abstract void ReloadMetadata();

        /// <summary>Folders whose file additions and removals change the tree.</summary>
        protected abstract IEnumerable<string> WatchedDirectories { get; }

        protected abstract Guid ProjectTypeGuid { get; }
        protected abstract string TypeName { get; }
        protected abstract IVsCfgProvider2 ConfigurationProvider { get; }

        /// <summary>The Properties window object of an item; the project node usually edits the project file.</summary>
        protected abstract object CreateBrowseObject(uint itemid, ProjectTreeNode model);

        /// <summary>The icon of a non-document node; null falls back to the image service's icon for the file name.</summary>
        protected abstract ImageMoniker? NodeMoniker(ProjectTreeNode model, bool open);

        protected abstract void Log(string message);

        /// <summary>Shows a node-specific context menu; false uses the standard project/item/folder/reference menus.</summary>
        protected virtual bool TryShowContextMenu(uint itemid, ProjectTreeNode model, IVsUIShell shell, POINTS[] points, out int result)
        {
            result = VSConstants.S_OK;
            return false;
        }

        protected virtual void OnClosed() { }

        protected object? GetService(Type service)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return ((System.IServiceProvider)Package).GetService(service);
        }

        protected ProjectTreeNode? ModelAt(uint itemid)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return items.TryGetValue(itemid, out var item) ? item.Model : null;
        }

        private sealed class Item
        {
            public Item(uint id, Item? parent, ProjectTreeNode model) { Id = id; Parent = parent; Model = model; }
            public uint Id { get; }
            public Item? Parent { get; }
            public ProjectTreeNode Model { get; set; }
            public List<Item>? Children { get; set; }
        }

        #region Tree

        private List<Item> ChildrenOf(Item item)
        {
            if (item.Children == null) item.Children = item.Model.Children.Select(m => Create(item, m)).ToList();
            return item.Children;
        }

        private Item Create(Item parent, ProjectTreeNode model)
        {
            var item = new Item(nextItemId++, parent, model);
            items[item.Id] = item;
            return item;
        }

        private void Forget(Item item)
        {
            items.Remove(item.Id);
            if (item.Children != null) foreach (var child in item.Children) Forget(child);
        }

        private Item? FindByPath(string path)
        {
            try { path = Path.GetFullPath(path); } catch (ArgumentException) { return null; } catch (NotSupportedException) { return null; }
            if (KotlinProjectTree.SamePath(path, FilePath)) return root;
            return Find(root);

            Item? Find(Item item)
            {
                foreach (var child in ChildrenOf(item))
                {
                    if (child.Model.Path != null && (child.Model.IsDocument || child.Model.IsFileSystemContainer)
                        && KotlinProjectTree.SamePath(child.Model.Path, path)) return child;
                    if (!child.Model.IsDocument && child.Model.MayContain(path) && Find(child) is Item found) return found;
                }
                return null;
            }
        }

        // Virtual nodes append their key; a key may hold a file path (package manifests), and the shell resolves
        // canonical names that look like paths as files, so drive colons and backslashes are taken out of that part.
        private string CanonicalName(Item item) =>
            item.Model.Path ?? (item.Parent == null ? FilePath : CanonicalName(item.Parent) + "|" + item.Model.Key.Replace(":", "").Replace('\\', '/'));

        private void NotifyPropertyChanged(uint itemid, __VSHPROPID property)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Notify(itemid, (int)property);
        }

        private void NotifyInvalidated(uint itemid)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Notify(itemid, null);
        }

        private void Notify(uint itemid, int? property)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var sink in sinks.Values.ToArray())
            {
                try
                {
                    if (property is int propid) sink.OnPropertyChanged(itemid, propid, 0);
                    else sink.OnInvalidateItems(itemid);
                }
                catch (Exception e) { Log("Project: hierarchy event failed: " + e.Message); }
            }
        }

        #endregion

        #region Refresh

        private void Watch()
        {
            foreach (var directory in WatchedDirectories.Prepend(ProjectDirectory).Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                };
                watcher.Created += OnFileSystemChange;
                watcher.Deleted += OnFileSystemChange;
                watcher.Changed += OnFileSystemChange;
                watcher.Renamed += (s, e) => { OnFileSystemChange(s, e); OnFileSystemChange(s, new FileSystemEventArgs(WatcherChangeTypes.Deleted, directory, e.OldName)); };
                watcher.Error += (_, __) => ScheduleRefresh(true);
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
        }

        private void OnFileSystemChange(object sender, FileSystemEventArgs e)
        {
            if (KotlinProjectTree.SamePath(e.FullPath, FilePath)) { ScheduleRefresh(true); return; }
            if (IsWatchedContent(e.FullPath)) { ScheduleRefresh(false); return; }
            // Content edits never change the tree; build output and tool caches are not project items.
            if (e.ChangeType == WatcherChangeTypes.Changed) return;
            var path = e.FullPath + "\\";
            if (IgnoredSegments.Any(s => path.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)) return;
            ScheduleRefresh(false);
        }

        /// <summary>Files whose content shapes the tree (package manifests and lock files): their edits refresh it too.</summary>
        protected virtual bool IsWatchedContent(string path) => false;

        protected void ScheduleRefresh(bool reloadMetadata)
        {
            lock (refreshLock)
            {
                if (closed) return;
                reloadMetadataPending |= reloadMetadata;
                refreshTimer?.Change(400, Timeout.Infinite);
            }
        }

        private void Refresh()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            bool reload;
            lock (refreshLock)
            {
                if (closed) return;
                reload = reloadMetadataPending;
                reloadMetadataPending = false;
            }
            if (reload)
            {
                try { ReloadMetadata(); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException || e is System.Xml.XmlException)
                {
                    // A partially written or invalid file keeps the last valid tree; the next change retries.
                    Log("Project: " + FilePath + " was not reloaded: " + e.Message);
                    return;
                }
            }
            Reconcile(root, BuildTree());
            if (reload) RefreshPropertyBrowser();
        }

        /// <summary>The package node under the selection: the Packages node of a manifest or one declared package.</summary>
        internal (Core.Packages.PackageManifestRef Manifest, Core.Packages.PackageEntry? Package)? PackageAt(uint itemid)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!items.TryGetValue(itemid, out var item)) return null;
            if (item.Model.Manifest != null) return (item.Model.Manifest, null);
            if (item.Model.Package != null && item.Parent?.Model.Manifest is Core.Packages.PackageManifestRef owner) return (owner, item.Model.Package);
            return null;
        }

        /// <summary>Rebuilds the tree now, e.g. after package versions arrived; the metadata file is not reread.</summary>
        internal void RefreshTree()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!closed) Reconcile(root, BuildTree());
        }

        /// <summary>Called after the Properties window saved a setting: reload at once instead of waiting for the watcher.</summary>
        internal void MetadataSaved()
        {
            lock (refreshLock) reloadMetadataPending = true;
            // Runs after the property grid finished its SetValue call, so the browser refresh is not re-entrant.
            Package.JoinableTaskFactory.RunAsync(async () =>
            {
                await Task.Yield();
                await Package.JoinableTaskFactory.SwitchToMainThreadAsync();
                Refresh();
            }).Task.Forget();
        }

        // The Properties window caches the browse object; ask it to re-query after the metadata changed.
        private void RefreshPropertyBrowser()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            (GetService(typeof(SVsUIShell)) as IVsUIShell)?.RefreshPropertyBrowser(0);
        }

        private void Reconcile(Item item, ProjectTreeNode model)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var captionChanged = item.Model.Caption != model.Caption;
            var iconChanged = !captionChanged && !Equals(Packages.PackageNodes.Moniker(item.Model, false), Packages.PackageNodes.Moniker(model, false));
            item.Model = model;
            if (captionChanged) NotifyPropertyChanged(item.Id, __VSHPROPID.VSHPROPID_Caption);
            // Package nodes change icon when a version check marks them outdated.
            if (iconChanged) Notify(item.Id, (int)__VSHPROPID8.VSHPROPID_IconMonikerId);
            if (item.Children == null)
            {
                if (model.IsFileSystemContainer || model.Kind == ProjectTreeKind.Module || model.Kind == ProjectTreeKind.Packages)
                    NotifyPropertyChanged(item.Id, __VSHPROPID.VSHPROPID_Expandable);
                return;
            }
            var previous = item.Children.GroupBy(c => c.Model.Key).ToDictionary(g => g.Key, g => new Queue<Item>(g));
            var next = new List<Item>();
            var structural = false;
            foreach (var child in model.Children)
            {
                if (previous.TryGetValue(child.Key, out var matches) && matches.Count != 0)
                {
                    var existing = matches.Dequeue();
                    next.Add(existing);
                    Reconcile(existing, child);
                }
                else { next.Add(Create(item, child)); structural = true; }
            }
            foreach (var removed in previous.Values.SelectMany(q => q)) { Forget(removed); structural = true; }
            structural |= !next.Select(c => c.Id).SequenceEqual(item.Children.Where(c => items.ContainsKey(c.Id)).Select(c => c.Id));
            item.Children = next;
            if (structural) NotifyInvalidated(item.Id);
        }

        #endregion

        #region IVsHierarchy

        public int SetSite(IOleServiceProvider psp) { site = psp; return VSConstants.S_OK; }

        public int GetSite(out IOleServiceProvider? ppSP) { ppSP = site; return VSConstants.S_OK; }

        public int QueryClose(out int pfCanClose) { pfCanClose = 1; return VSConstants.S_OK; }

        public int Close()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            lock (refreshLock)
            {
                if (closed) return VSConstants.S_OK;
                closed = true;
            }
            Open.TryRemove(FilePath, out _);
            foreach (var watcher in watchers) watcher.Dispose();
            watchers.Clear();
            refreshTimer?.Dispose();
            OnClosed();
            return VSConstants.S_OK;
        }

        public int GetGuidProperty(uint itemid, int propid, out Guid pguid)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            pguid = Guid.Empty;
            if (!items.TryGetValue(itemid, out var item)) return VSConstants.E_INVALIDARG;
            switch (propid)
            {
                case (int)__VSHPROPID.VSHPROPID_ProjectIDGuid when itemid == Root:
                    pguid = ProjectGuid; return VSConstants.S_OK;
                case (int)__VSHPROPID.VSHPROPID_TypeGuid:
                    pguid = TypeGuid(item); return VSConstants.S_OK;
                case (int)__VSHPROPID8.VSHPROPID_IconMonikerGuid:
                    pguid = Moniker(item, false).Guid; return VSConstants.S_OK;
                case (int)__VSHPROPID8.VSHPROPID_OpenFolderIconMonikerGuid:
                    pguid = Moniker(item, true).Guid; return VSConstants.S_OK;
                default:
                    return VSConstants.DISP_E_MEMBERNOTFOUND;
            }
        }

        public int SetGuidProperty(uint itemid, int propid, ref Guid rguid)
        {
            if (itemid != Root || propid != (int)__VSHPROPID.VSHPROPID_ProjectIDGuid) return VSConstants.DISP_E_MEMBERNOTFOUND;
            ProjectGuid = rguid;
            return VSConstants.S_OK;
        }

        public int GetProperty(uint itemid, int propid, out object? pvar)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            pvar = null;
            if (!items.TryGetValue(itemid, out var item)) return VSConstants.E_INVALIDARG;
            var model = item.Model;
            switch (propid)
            {
                case (int)__VSHPROPID.VSHPROPID_Parent:
                    pvar = unchecked((int)(item.Parent?.Id ?? Nil)); break;
                case (int)__VSHPROPID.VSHPROPID_FirstChild:
                case (int)__VSHPROPID.VSHPROPID_FirstVisibleChild:
                    var children = ChildrenOf(item);
                    pvar = unchecked((int)(children.Count == 0 ? Nil : children[0].Id)); break;
                case (int)__VSHPROPID.VSHPROPID_NextSibling:
                case (int)__VSHPROPID.VSHPROPID_NextVisibleSibling:
                    var siblings = item.Parent?.Children;
                    var index = siblings?.IndexOf(item) ?? -1;
                    pvar = unchecked((int)(index < 0 || index + 1 >= siblings!.Count ? Nil : siblings[index + 1].Id)); break;
                case (int)__VSHPROPID.VSHPROPID_Caption:
                case (int)__VSHPROPID.VSHPROPID_Name:
                    pvar = model.Caption; break;
                case (int)__VSHPROPID.VSHPROPID_SaveName:
                    pvar = itemid == Root ? Path.GetFileName(FilePath) : model.Path != null ? Path.GetFileName(model.Path) : model.Caption; break;
                case (int)__VSHPROPID.VSHPROPID_TypeName:
                    pvar = TypeName; break;
                case (int)__VSHPROPID.VSHPROPID_ProjectDir:
                    pvar = ProjectDirectory; break;
                case (int)__VSHPROPID.VSHPROPID_ExtObject when itemid == Root:
                    // Required by the New Project template engine (Solution.AddFromTemplate) once the factory created the project.
                    pvar = automation ??= new KotlinProjectAutomation(this, FilePath, ProjectTypeGuid, () => items[Root].Model.Caption); break;
                case (int)__VSHPROPID.VSHPROPID_Expandable:
                    pvar = model.HasChildren; break;
                case (int)__VSHPROPID.VSHPROPID_ExpandByDefault:
                    pvar = itemid == Root; break;
                case (int)__VSHPROPID.VSHPROPID_IsHiddenItem:
                case (int)__VSHPROPID.VSHPROPID_IsNonMemberItem:
                    pvar = false; break;
                case (int)__VSHPROPID.VSHPROPID_StateIconIndex:
                    pvar = (int)VsStateIcon.STATEICON_NOSTATEICON; break;
                case (int)__VSHPROPID.VSHPROPID_BrowseObject:
                    pvar = CreateBrowseObject(itemid, model); break;
                case (int)__VSHPROPID.VSHPROPID_ConfigurationProvider when itemid == Root:
                    pvar = ConfigurationProvider; break;
                case (int)__VSHPROPID.VSHPROPID_DefaultEnableBuildProjectCfg when itemid == Root:
                    pvar = true; break;
                case (int)__VSHPROPID.VSHPROPID_DefaultEnableDeployProjectCfg when itemid == Root:
                    pvar = false; break;
                case (int)__VSHPROPID.VSHPROPID_HandlesOwnReload when itemid == Root:
                    // Tools rewrite the project file; the watcher reconciles the tree, so the shell must not prompt to reload.
                    pvar = true; break;
                case (int)__VSHPROPID.VSHPROPID_ProjectIDGuid when itemid == Root:
                    pvar = ProjectGuid; break;
                case (int)__VSHPROPID.VSHPROPID_TypeGuid:
                    pvar = TypeGuid(item); break;
                case (int)__VSHPROPID.VSHPROPID_ParentHierarchy when itemid == Root:
                case (int)__VSHPROPID.VSHPROPID_ParentHierarchyItemid when itemid == Root:
                    return parentProperties.TryGetValue(propid, out pvar) ? VSConstants.S_OK : VSConstants.DISP_E_MEMBERNOTFOUND;
                case (int)__VSHPROPID8.VSHPROPID_SupportsIconMonikers:
                    pvar = true; break;
                case (int)__VSHPROPID8.VSHPROPID_IconMonikerGuid:
                    pvar = Moniker(item, false).Guid; break;
                case (int)__VSHPROPID8.VSHPROPID_IconMonikerId:
                    pvar = Moniker(item, false).Id; break;
                case (int)__VSHPROPID8.VSHPROPID_OpenFolderIconMonikerGuid:
                    pvar = Moniker(item, true).Guid; break;
                case (int)__VSHPROPID8.VSHPROPID_OpenFolderIconMonikerId:
                    pvar = Moniker(item, true).Id; break;
                default:
                    return VSConstants.DISP_E_MEMBERNOTFOUND;
            }
            return VSConstants.S_OK;
        }

        public int SetProperty(uint itemid, int propid, object var)
        {
            if (itemid == Root && (propid == (int)__VSHPROPID.VSHPROPID_ParentHierarchy || propid == (int)__VSHPROPID.VSHPROPID_ParentHierarchyItemid))
            {
                parentProperties[propid] = var;
                return VSConstants.S_OK;
            }
            return VSConstants.DISP_E_MEMBERNOTFOUND;
        }

        public int GetNestedHierarchy(uint itemid, ref Guid iidHierarchyNested, out IntPtr ppHierarchyNested, out uint pitemidNested)
        {
            ppHierarchyNested = IntPtr.Zero;
            pitemidNested = 0;
            return VSConstants.E_FAIL;
        }

        public int GetCanonicalName(uint itemid, out string? pbstrName)
        {
            pbstrName = items.TryGetValue(itemid, out var item) ? CanonicalName(item) : null;
            return pbstrName == null ? VSConstants.E_INVALIDARG : VSConstants.S_OK;
        }

        public int ParseCanonicalName(string pszName, out uint pitemid)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            pitemid = Nil;
            var found = pszName.IndexOf('|') < 0 ? FindByPath(pszName) : FindVirtual(root);
            if (found == null) return VSConstants.E_FAIL;
            pitemid = found.Id;
            return VSConstants.S_OK;

            Item? FindVirtual(Item item)
            {
                var name = CanonicalName(item);
                if (string.Equals(name, pszName, StringComparison.OrdinalIgnoreCase)) return item;
                if (!pszName.StartsWith(name + "|", StringComparison.OrdinalIgnoreCase)) return null;
                return ChildrenOf(item).Where(c => c.Model.Path == null).Select(FindVirtual).FirstOrDefault(f => f != null);
            }
        }

        public int AdviseHierarchyEvents(IVsHierarchyEvents pEventSink, out uint pdwCookie)
        {
            pdwCookie = nextSinkCookie++;
            sinks[pdwCookie] = pEventSink;
            return VSConstants.S_OK;
        }

        public int UnadviseHierarchyEvents(uint dwCookie) { sinks.Remove(dwCookie); return VSConstants.S_OK; }

        public int Unused0() => VSConstants.E_NOTIMPL;
        public int Unused1() => VSConstants.E_NOTIMPL;
        public int Unused2() => VSConstants.E_NOTIMPL;
        public int Unused3() => VSConstants.E_NOTIMPL;
        public int Unused4() => VSConstants.E_NOTIMPL;

        #endregion

        #region IVsUIHierarchy

        // UNKNOWNGROUP, not NOTSUPPORTED: NOTSUPPORTED ends command routing at the hierarchy, so the packages' own
        // context-menu commands (package nodes, Kotlin targets) never got their status queried and stayed disabled.
        public int QueryStatusCommand(uint itemid, ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText) =>
            (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_UNKNOWNGROUP;

        public int ExecCommand(uint itemid, ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (pguidCmdGroup != VSConstants.GUID_VsUIHierarchyWindowCmds)
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_UNKNOWNGROUP;
            if (!items.TryGetValue(itemid, out var item))
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            switch ((VSConstants.VsUIHierarchyWindowCmdIds)nCmdID)
            {
                case VSConstants.VsUIHierarchyWindowCmdIds.UIHWCMDID_DoubleClick:
                case VSConstants.VsUIHierarchyWindowCmdIds.UIHWCMDID_EnterKey:
                    if (!item.Model.IsDocument) return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
                    VsShellUtilities.OpenDocument(Package, item.Model.Path!, VSConstants.LOGVIEWID_Primary, out _, out _, out var frame);
                    frame?.Show();
                    return VSConstants.S_OK;
                case VSConstants.VsUIHierarchyWindowCmdIds.UIHWCMDID_RightClick:
                    var position = (uint)(int)Marshal.GetObjectForNativeVariant(pvaIn)!;
                    var points = new[] { new POINTS { x = (short)(position & 0xffff), y = (short)(position >> 16) } };
                    var shell = GetService(typeof(SVsUIShell)) as IVsUIShell ?? throw new InvalidOperationException("Visual Studio shell is unavailable.");
                    if (Packages.PackageNodes.Menu(item.Model) is int packageMenu)
                    {
                        var packageCommands = Packages.PackageNodes.Commands;
                        return shell.ShowContextMenu(0, ref packageCommands, packageMenu, points, null);
                    }
                    if (TryShowContextMenu(itemid, item.Model, shell, points, out var shown)) return shown;
                    var menu = itemid == Root ? VsMenus.IDM_VS_CTXT_PROJNODE
                        : item.Model.IsDocument ? VsMenus.IDM_VS_CTXT_ITEMNODE
                        : item.Model.Kind == ProjectTreeKind.Library || item.Model.Kind == ProjectTreeKind.ProjectReference || item.Model.Kind == ProjectTreeKind.FileReference
                            ? VsMenus.IDM_VS_CTXT_REFERENCE : VsMenus.IDM_VS_CTXT_FOLDERNODE;
                    var menuGroup = VsMenus.guidSHLMainMenu;
                    return shell.ShowContextMenu(0, ref menuGroup, menu, points, null);
                default:
                    return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            }
        }

        #endregion

        #region IVsProject

        public int IsDocumentInProject(string pszMkDocument, out int pfFound, VSDOCUMENTPRIORITY[] pdwPriority, out uint pitemid)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var item = FindByPath(pszMkDocument);
            var found = item != null && item.Model.IsDocument;
            pfFound = found ? 1 : 0;
            pitemid = found ? item!.Id : Nil;
            if (pdwPriority != null && pdwPriority.Length != 0)
                pdwPriority[0] = found ? VSDOCUMENTPRIORITY.DP_Standard : VSDOCUMENTPRIORITY.DP_Unsupported;
            return VSConstants.S_OK;
        }

        public int GetMkDocument(uint itemid, out string? pbstrMkDocument)
        {
            pbstrMkDocument = null;
            if (!items.TryGetValue(itemid, out var item)) return VSConstants.E_INVALIDARG;
            pbstrMkDocument = itemid == Root ? FilePath : item.Model.Path;
            return pbstrMkDocument == null ? VSConstants.E_FAIL : VSConstants.S_OK;
        }

        public int OpenItem(uint itemid, ref Guid rguidLogicalView, IntPtr punkDocDataExisting, out IVsWindowFrame? ppWindowFrame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ppWindowFrame = null;
            if (!items.TryGetValue(itemid, out var item) || !item.Model.IsDocument) return VSConstants.E_INVALIDARG;
            var opener = GetService(typeof(SVsUIShellOpenDocument)) as IVsUIShellOpenDocument
                ?? throw new InvalidOperationException("Visual Studio document service is unavailable.");
            var services = site ?? GetService(typeof(IOleServiceProvider)) as IOleServiceProvider
                ?? throw new InvalidOperationException("Visual Studio service provider is unavailable.");
            return opener.OpenStandardEditor((uint)__VSOSEFLAGS.OSE_ChooseBestStdEditor, item.Model.Path, ref rguidLogicalView, "%3",
                this, itemid, punkDocDataExisting, services, out ppWindowFrame);
        }

        public int GetItemContext(uint itemid, out IOleServiceProvider? ppSP) { ppSP = null; return VSConstants.S_OK; }

        public int GenerateUniqueItemName(uint itemidLoc, string pszExt, string pszSuggestedRoot, out string? pbstrItemName)
        { pbstrItemName = null; return VSConstants.E_NOTIMPL; }

        public int AddItem(uint itemidLoc, VSADDITEMOPERATION dwAddItemOperation, string pszItemName, uint cFilesToOpen, string[] rgpszFilesToOpen, IntPtr hwndDlgOwner, VSADDRESULT[] pResult) =>
            VSConstants.E_NOTIMPL;

        #endregion

        #region IVsPersistHierarchyItem

        public int IsItemDirty(uint itemid, IntPtr punkDocData, out int pfDirty)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            pfDirty = 0;
            if (punkDocData == IntPtr.Zero) return VSConstants.S_OK;
            return Marshal.GetObjectForIUnknown(punkDocData) is IVsPersistDocData document ? document.IsDocDataDirty(out pfDirty) : VSConstants.S_OK;
        }

        public int SaveItem(VSSAVEFLAGS dwSave, string pszSilentSaveAsName, uint itemid, IntPtr punkDocData, out int pfCanceled)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            pfCanceled = 0;
            // Project metadata is written only by explicit commands; documents save through their own doc data.
            if (punkDocData == IntPtr.Zero) return VSConstants.S_OK;
            var document = Marshal.GetObjectForIUnknown(punkDocData);
            if (dwSave == VSSAVEFLAGS.VSSAVE_SilentSave && document is IPersistFileFormat format)
                return format.Save(string.IsNullOrEmpty(pszSilentSaveAsName) ? null : pszSilentSaveAsName, 1, 0);
            return document is IVsPersistDocData data ? data.SaveDocData(dwSave, out _, out pfCanceled) : VSConstants.E_NOINTERFACE;
        }

        #endregion

        public int GetCfgProvider(out IVsCfgProvider ppCfgProvider) { ppCfgProvider = ConfigurationProvider; return VSConstants.S_OK; }

        private Guid TypeGuid(Item item)
        {
            if (item.Id == Root) return ProjectTypeGuid;
            if (item.Model.IsDocument) return VSConstants.GUID_ItemType_PhysicalFile;
            return item.Model.IsFileSystemContainer ? VSConstants.GUID_ItemType_PhysicalFolder : VSConstants.GUID_ItemType_VirtualFolder;
        }

        private ImageMoniker Moniker(Item item, bool open)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if ((Packages.PackageNodes.Moniker(item.Model, open) ?? NodeMoniker(item.Model, open)) is ImageMoniker own) return own;
            var name = item.Model.Path ?? item.Model.Caption;
            var extension = Path.GetExtension(name);
            if (!fileMonikers.TryGetValue(extension, out var moniker))
            {
                var images = GetService(typeof(SVsImageService)) as IVsImageService2;
                moniker = images?.GetImageMonikerForFile(name) ?? KnownMonikers.Document;
                if (moniker.Guid == Guid.Empty) moniker = KnownMonikers.Document;
                fileMonikers[extension] = moniker;
            }
            return moniker;
        }
    }
}
