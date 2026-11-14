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
using Vistudio.Core;
using Vistudio.Core.Projects;
using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace Vistudio
{
    /// <summary>
    /// Read-only Solution Explorer hierarchy for one .kotlinproj: Gradle modules, source sets with on-disk files,
    /// resolved dependencies and Gradle scripts. Items are materialized lazily and reconciled on disk changes.
    /// </summary>
    internal sealed class KotlinProjectNode : IVsUIHierarchy, IVsProject, IVsPersistHierarchyItem, IVsGetCfgProvider
    {
        private const uint Root = VSConstants.VSITEMID_ROOT;
        private const uint Nil = VSConstants.VSITEMID_NIL;
        private static readonly ImageMoniker KotlinMoniker = new ImageMoniker { Guid = new Guid("6cd0eb83-fd71-470c-9717-dfa55d167c7b"), Id = 1 };
        private static readonly string[] IgnoredSegments = { "\\build\\", "\\.gradle\\", "\\.kotlin\\", "\\.idea\\", "\\.vs\\", "\\out\\", "\\.git\\" };

        private readonly KotlinPackage package;
        private readonly Dictionary<uint, Item> items = new Dictionary<uint, Item>();
        private readonly Dictionary<uint, IVsHierarchyEvents> sinks = new Dictionary<uint, IVsHierarchyEvents>();
        private readonly Dictionary<int, object> parentProperties = new Dictionary<int, object>();
        private readonly Dictionary<string, ImageMoniker> fileMonikers = new Dictionary<string, ImageMoniker>(StringComparer.OrdinalIgnoreCase);
        private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private readonly object refreshLock = new object();
        private readonly Timer refreshTimer;
        private readonly Item root;
        private uint nextItemId = 1, nextSinkCookie = 1;
        private bool reloadMetadataPending, closed;
        private IOleServiceProvider? site;
        private Guid projectGuid;

        public KotlinProjectNode(KotlinPackage package, string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.package = package;
            FilePath = filePath;
            Metadata = KotlinProjectFile.Load(filePath);
            if (!Guid.TryParse(Metadata.Get("ProjectGuid"), out projectGuid)) projectGuid = Guid.NewGuid();
            Configurations = new KotlinConfigProvider(this, package);
            root = new Item(Root, null, BuildTree());
            items[Root] = root;
            refreshTimer = new Timer(_ => package.JoinableTaskFactory.RunAsync(async () =>
            {
                await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                Refresh();
            }).Task.Forget(), null, Timeout.Infinite, Timeout.Infinite);
            Watch();
            TreeSettingsChanged += OnTreeSettingsChangedHandler;
            package.Environment.ProjectOpened(FilePath);
        }

        /// <summary>Raised on the UI thread when a global option that shapes project trees changes, e.g. Show hidden files.</summary>
        private static event Action? TreeSettingsChanged;

        internal static void OnTreeSettingsChanged() => TreeSettingsChanged?.Invoke();

        private void OnTreeSettingsChangedHandler() => ScheduleRefresh(false);

        private ProjectTreeNode BuildTree()
        {
            bool showHidden;
            try { showHidden = KotlinSettings.Load().ShowHiddenFiles; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException || e is System.Xml.XmlException)
            {
                package.Environment.Log("Kotlin project: settings were not read, hidden files stay hidden: " + e.Message);
                showHidden = false;
            }
            return KotlinProjectTree.Build(Metadata, showHidden);
        }

        public string FilePath { get; }
        private KotlinProjectAutomation? automation;
        public string ProjectDirectory => Path.GetDirectoryName(FilePath)!;

        /// <summary>The Kotlin target of a Targets/&lt;target&gt; node, for the target context commands.</summary>
        internal KotlinTarget? TargetAt(uint itemid)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return items.TryGetValue(itemid, out var item) ? item.Model.Target : null;
        }
        public KotlinProjectFile Metadata { get; private set; }
        public string Name => root.Model.Caption;
        internal KotlinConfigProvider Configurations { get; }

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

        private string CanonicalName(Item item) =>
            item.Model.Path ?? (item.Parent == null ? FilePath : CanonicalName(item.Parent) + "|" + item.Model.Key);

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
                catch (Exception e) { package.Environment.Log("Kotlin project: hierarchy event failed: " + e.Message); }
            }
        }

        #endregion

        #region Refresh

        private void Watch()
        {
            foreach (var directory in new[] { ProjectDirectory, Metadata.GradleRoot }.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
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
            // Content edits never change the tree; build output and tool caches are not project items.
            if (e.ChangeType == WatcherChangeTypes.Changed) return;
            var path = e.FullPath + "\\";
            if (IgnoredSegments.Any(s => path.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)) return;
            ScheduleRefresh(false);
        }

        private void ScheduleRefresh(bool reloadMetadata)
        {
            lock (refreshLock)
            {
                if (closed) return;
                reloadMetadataPending |= reloadMetadata;
                refreshTimer.Change(400, Timeout.Infinite);
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
                try { Metadata = KotlinProjectFile.Load(FilePath); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException || e is System.Xml.XmlException)
                {
                    // A partially written or invalid file keeps the last valid tree; the next change retries.
                    package.Environment.Log("Kotlin project: " + FilePath + " was not reloaded: " + e.Message);
                    return;
                }
            }
            Reconcile(root, BuildTree());
            if (reload) RefreshPropertyBrowser();
        }

        /// <summary>Called after the Properties window saved a setting: reload at once instead of waiting for the watcher.</summary>
        internal void MetadataSaved()
        {
            lock (refreshLock) reloadMetadataPending = true;
            // Runs after the property grid finished its SetValue call, so the browser refresh is not re-entrant.
            package.JoinableTaskFactory.RunAsync(async () =>
            {
                await Task.Yield();
                await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                Refresh();
            }).Task.Forget();
        }

        // The Properties window caches the browse object; ask it to re-query after the metadata changed.
        private void RefreshPropertyBrowser()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            (package.GetServiceOnMainThread(typeof(SVsUIShell)) as IVsUIShell)?.RefreshPropertyBrowser(0);
        }

        /// <summary>Applies a fresh model while keeping ids of unchanged items, so expansion and selection survive refreshes.</summary>
        private void Reconcile(Item item, ProjectTreeNode model)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var captionChanged = item.Model.Caption != model.Caption;
            item.Model = model;
            if (captionChanged) NotifyPropertyChanged(item.Id, __VSHPROPID.VSHPROPID_Caption);
            if (item.Children == null)
            {
                if (model.IsFileSystemContainer || model.Kind == ProjectTreeKind.Module)
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
            TreeSettingsChanged -= OnTreeSettingsChangedHandler;
            foreach (var watcher in watchers) watcher.Dispose();
            watchers.Clear();
            refreshTimer.Dispose();
            Configurations.CancelBuilds();
            package.Environment.ProjectClosed(FilePath);
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
                    pguid = projectGuid; return VSConstants.S_OK;
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
            projectGuid = rguid;
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
                    pvar = "Kotlin"; break;
                case (int)__VSHPROPID.VSHPROPID_ProjectDir:
                    pvar = ProjectDirectory; break;
                case (int)__VSHPROPID.VSHPROPID_ExtObject when itemid == Root:
                    // Required by the New Project template engine (Solution.AddFromTemplate) once the factory created the project.
                    pvar = automation ??= new KotlinProjectAutomation(FilePath, () => items[Root].Model.Caption); break;
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
                    // The project node edits .kotlinproj settings in the Properties window; other nodes show read-only details.
                    pvar = itemid == Root ? new KotlinProjectProperties(Metadata, package.Environment.Log, MetadataSaved) : (object)new KotlinNodeProperties(model); break;
                case (int)__VSHPROPID.VSHPROPID_ConfigurationProvider when itemid == Root:
                    pvar = Configurations; break;
                case (int)__VSHPROPID.VSHPROPID_DefaultEnableBuildProjectCfg when itemid == Root:
                    pvar = true; break;
                case (int)__VSHPROPID.VSHPROPID_DefaultEnableDeployProjectCfg when itemid == Root:
                    pvar = false; break;
                case (int)__VSHPROPID.VSHPROPID_HandlesOwnReload when itemid == Root:
                    // Imports rewrite the .kotlinproj; the watcher reconciles the tree, so the shell must not prompt to reload.
                    pvar = true; break;
                case (int)__VSHPROPID.VSHPROPID_ProjectIDGuid when itemid == Root:
                    pvar = projectGuid; break;
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

        public int QueryStatusCommand(uint itemid, ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText) =>
            (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;

        public int ExecCommand(uint itemid, ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (pguidCmdGroup != VSConstants.GUID_VsUIHierarchyWindowCmds || !items.TryGetValue(itemid, out var item))
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            switch ((VSConstants.VsUIHierarchyWindowCmdIds)nCmdID)
            {
                case VSConstants.VsUIHierarchyWindowCmdIds.UIHWCMDID_DoubleClick:
                case VSConstants.VsUIHierarchyWindowCmdIds.UIHWCMDID_EnterKey:
                    if (!item.Model.IsDocument) return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
                    VsShellUtilities.OpenDocument(package, item.Model.Path!, VSConstants.LOGVIEWID_Primary, out _, out _, out var frame);
                    frame?.Show();
                    return VSConstants.S_OK;
                case VSConstants.VsUIHierarchyWindowCmdIds.UIHWCMDID_RightClick:
                    var position = (uint)(int)Marshal.GetObjectForNativeVariant(pvaIn)!;
                    var points = new[] { new POINTS { x = (short)(position & 0xffff), y = (short)(position >> 16) } };
                    var menu = itemid == Root ? VsMenus.IDM_VS_CTXT_PROJNODE
                        : item.Model.IsDocument ? VsMenus.IDM_VS_CTXT_ITEMNODE
                        : item.Model.Kind == ProjectTreeKind.Library || item.Model.Kind == ProjectTreeKind.ProjectReference || item.Model.Kind == ProjectTreeKind.FileReference
                            ? VsMenus.IDM_VS_CTXT_REFERENCE : VsMenus.IDM_VS_CTXT_FOLDERNODE;
                    var shell = package.GetServiceOnMainThread(typeof(SVsUIShell)) as IVsUIShell ?? throw new InvalidOperationException("Visual Studio shell is unavailable.");
                    if (item.Model.Target != null)
                    {
                        var targetMenu = KotlinPackage.Commands;
                        return shell.ShowContextMenu(0, ref targetMenu, KotlinPackage.TargetMenu, points, null);
                    }
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
            var opener = package.GetServiceOnMainThread(typeof(SVsUIShellOpenDocument)) as IVsUIShellOpenDocument
                ?? throw new InvalidOperationException("Visual Studio document service is unavailable.");
            var services = site ?? package.GetServiceOnMainThread(typeof(IOleServiceProvider)) as IOleServiceProvider
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
            // Project metadata is written only by explicit Kotlin commands; documents save through their own doc data.
            if (punkDocData == IntPtr.Zero) return VSConstants.S_OK;
            var document = Marshal.GetObjectForIUnknown(punkDocData);
            if (dwSave == VSSAVEFLAGS.VSSAVE_SilentSave && document is IPersistFileFormat format)
                return format.Save(string.IsNullOrEmpty(pszSilentSaveAsName) ? null : pszSilentSaveAsName, 1, 0);
            return document is IVsPersistDocData data ? data.SaveDocData(dwSave, out _, out pfCanceled) : VSConstants.E_NOINTERFACE;
        }

        #endregion

        public int GetCfgProvider(out IVsCfgProvider ppCfgProvider) { ppCfgProvider = Configurations; return VSConstants.S_OK; }

        private Guid TypeGuid(Item item)
        {
            if (item.Id == Root) return new Guid(KotlinProjectFactory.Id);
            if (item.Model.IsDocument) return VSConstants.GUID_ItemType_PhysicalFile;
            return item.Model.IsFileSystemContainer ? VSConstants.GUID_ItemType_PhysicalFolder : VSConstants.GUID_ItemType_VirtualFolder;
        }

        private ImageMoniker Moniker(Item item, bool open)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            switch (item.Model.Kind)
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
                    switch (item.Model.Target?.Platform)
                    {
                        case "JVM": return KnownMonikers.JavaSource;
                        case "Android": return KnownMonikers.Android;
                        case "JS": return KnownMonikers.JSScript;
                        case "Wasm": return KnownMonikers.WebApplication;
                        case "Native": return KnownMonikers.Processor;
                        default: return KnownMonikers.Application;
                    }
                default:
                    var extension = Path.GetExtension(item.Model.Path ?? item.Model.Caption);
                    if (!fileMonikers.TryGetValue(extension, out var moniker))
                    {
                        var images = package.GetServiceOnMainThread(typeof(SVsImageService)) as IVsImageService2;
                        moniker = images?.GetImageMonikerForFile(item.Model.Path ?? item.Model.Caption) ?? KnownMonikers.Document;
                        if (moniker.Guid == Guid.Empty) moniker = KnownMonikers.Document;
                        fileMonikers[extension] = moniker;
                    }
                    return moniker;
            }
        }
    }
}
