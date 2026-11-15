using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;

namespace Vistudio
{
    /// <summary>
    /// Project &gt; Properties of a Kotlin or language project: the project's settings (the Properties window's view of the
    /// project node) in a property grid on a property page. The shell shows the pages a project's browse object names through
    /// <see cref="ISpecifyPropertyPages"/>; without one the command did nothing. Edits stay pending until OK or Apply, so
    /// Cancel discards them; Apply writes them through the browse object, which validates and saves the project file.
    /// </summary>
    [ComVisible(true), Guid(Clsid), ClassInterface(ClassInterfaceType.None)]
    public sealed class ProjectPropertyPage : IPropertyPage
    {
        internal const string Clsid = "5c1d7e2a-8f43-4b6e-a915-3e7d0c2b8f64";
        private const uint PropPageStatusDirty = 0x1;

        private IPropertyPageSite? site;
        private PropertyGrid? grid;
        private PendingEdits[] pending = Array.Empty<PendingEdits>();

        /// <summary>Fills <see cref="ISpecifyPropertyPages.GetPages"/> with this page.</summary>
        internal static void Specify(CAUUID[] pages)
        {
            if (pages == null || pages.Length == 0) return;
            var memory = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(Guid)));
            Marshal.StructureToPtr(new Guid(Clsid), memory, false);
            pages[0].cElems = 1;
            pages[0].pElems = memory;
        }

        public void SetPageSite(IPropertyPageSite pPageSite) => site = pPageSite;

        public void Activate(IntPtr hWndParent, RECT[] pRect, int bModal)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            grid = new PropertyGrid { ToolbarVisible = false, HelpVisible = true, PropertySort = PropertySort.Categorized, Visible = false };
            grid.CreateControl();
            SetParent(grid.Handle, hWndParent);
            Move(pRect);
            grid.SelectedObjects = pending;
        }

        public void Deactivate()
        {
            grid?.Dispose();
            grid = null;
        }

        public void GetPageInfo(PROPPAGEINFO[] pPageInfo)
        {
            if (pPageInfo == null || pPageInfo.Length == 0) return;
            pPageInfo[0] = new PROPPAGEINFO
            {
                cb = (uint)Marshal.SizeOf(typeof(PROPPAGEINFO)),
                pszTitle = "General",
                SIZE = new SIZE { cx = 560, cy = 380 },
            };
        }

        public void SetObjects(uint cObjects, object[] ppunk)
        {
            pending = cObjects == 0 || ppunk == null ? Array.Empty<PendingEdits>()
                : ppunk.Take((int)cObjects).Where(o => o != null).Select(o => new PendingEdits(o, OnChanged)).ToArray();
            if (grid != null) grid.SelectedObjects = pending;
        }

        public void Show(uint nCmdShow)
        {
            if (grid == null) return;
            grid.Visible = nCmdShow != 0; // SW_HIDE
            if (grid.Visible) grid.Focus();
        }

        public void Move(RECT[] pRect)
        {
            if (grid == null || pRect == null || pRect.Length == 0) return;
            var r = pRect[0];
            grid.SetBounds(r.left, r.top, r.right - r.left, r.bottom - r.top);
        }

        public int IsPageDirty() => pending.Any(p => p.IsDirty) ? VSConstants.S_OK : VSConstants.S_FALSE;

        public int Apply()
        {
            try
            {
                foreach (var edits in pending) edits.Apply();
                grid?.Refresh();
                return VSConstants.S_OK;
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                // The browse object rejects invalid values with the reason; the rest stay pending and the page stays open.
                var message = (e as System.Reflection.TargetInvocationException)?.InnerException?.Message ?? e.Message;
                MessageBox.Show(grid, message, "Project Properties", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                grid?.Refresh();
                return VSConstants.E_FAIL;
            }
        }

        public void Help(string pszHelpDir) { }

        public int TranslateAccelerator(MSG[] pMsg) => VSConstants.S_FALSE;

        private void OnChanged()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            grid?.Refresh();
            site?.OnStatusChange(PropPageStatusDirty);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

        /// <summary>
        /// One browse object as the grid sees it: its own properties (categories, descriptions, read-only state), with
        /// values the user changed kept here until <see cref="Apply"/>. Changed values show in bold.
        /// </summary>
        private sealed class PendingEdits : CustomTypeDescriptor
        {
            private readonly object target;
            private readonly Action changed;
            private readonly Dictionary<string, object?> values = new Dictionary<string, object?>(StringComparer.Ordinal);

            public PendingEdits(object target, Action changed) : base(TypeDescriptor.GetProvider(target).GetTypeDescriptor(target))
            {
                this.target = target;
                this.changed = changed;
            }

            public bool IsDirty => values.Count != 0;

            public override PropertyDescriptorCollection GetProperties() => GetProperties(null);

            public override PropertyDescriptorCollection GetProperties(Attribute[]? attributes) =>
                new PropertyDescriptorCollection(TypeDescriptor.GetProperties(target, attributes).Cast<PropertyDescriptor>()
                    .Select(p => (PropertyDescriptor)new Pending(this, p)).ToArray(), true);

            public override object GetPropertyOwner(PropertyDescriptor? pd) => this;

            public void Apply()
            {
                // Fresh descriptors: the browse object re-reads the project file after each save.
                var properties = TypeDescriptor.GetProperties(target);
                foreach (var name in values.Keys.ToList())
                {
                    properties[name]?.SetValue(target, values[name]);
                    values.Remove(name);
                }
            }

            private sealed class Pending : PropertyDescriptor
            {
                private readonly PendingEdits owner;
                private readonly PropertyDescriptor inner;

                public Pending(PendingEdits owner, PropertyDescriptor inner) : base(inner)
                {
                    this.owner = owner;
                    this.inner = inner;
                }

                public override Type ComponentType => typeof(PendingEdits);
                public override bool IsReadOnly => inner.IsReadOnly;
                public override Type PropertyType => inner.PropertyType;
                public override TypeConverter Converter => inner.Converter;
                public override bool CanResetValue(object component) => owner.values.ContainsKey(Name);
                public override void ResetValue(object component) { owner.values.Remove(Name); owner.changed(); }
                public override bool ShouldSerializeValue(object component) => owner.values.ContainsKey(Name);

                public override object? GetValue(object? component) =>
                    owner.values.TryGetValue(Name, out var value) ? value : inner.GetValue(owner.target);

                public override void SetValue(object? component, object? value)
                {
                    if (IsReadOnly) throw new NotSupportedException(Name + " is read-only.");
                    if (Equals(value, inner.GetValue(owner.target))) owner.values.Remove(Name);
                    else owner.values[Name] = value;
                    OnValueChanged(component, EventArgs.Empty);
                    owner.changed();
                }
            }
        }
    }
}
