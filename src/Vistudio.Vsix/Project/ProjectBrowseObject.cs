using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell.Interop;

namespace Vistudio
{
    /// <summary>
    /// Base of a project node's browse object (Properties window, Project &gt; Properties). Implements
    /// <see cref="ICustomTypeDescriptor"/> itself rather than deriving from <see cref="CustomTypeDescriptor"/>: that class is
    /// not COM-visible, and a COM-invisible base makes the CLR refuse IDispatch for the whole object, so the shell could not
    /// read <see cref="Name"/> (the property page frame then titled itself with the .NET class name).
    /// </summary>
    [ComVisible(true)]
    public abstract class ProjectBrowseObject : ICustomTypeDescriptor, ISpecifyPropertyPages, IVsBrowseObject
    {
        /// <summary>The project node these settings describe.</summary>
        internal IVsHierarchy? Hierarchy { get; set; }

        /// <summary>Read through IDispatch by the property page frame for its title ("DartApp Property Pages").</summary>
        [Browsable(false)]
        public string Name => GetComponentName() ?? "";

        /// <summary>Project &gt; Properties shows these settings on <see cref="ProjectPropertyPage"/>.</summary>
        public void GetPages(CAUUID[] pPages) => ProjectPropertyPage.Specify(pPages);

        public int GetProjectItem(out IVsHierarchy? pHier, out uint pItemid)
        {
            pHier = Hierarchy;
            pItemid = VSConstants.VSITEMID_ROOT;
            return pHier == null ? VSConstants.E_NOTIMPL : VSConstants.S_OK;
        }

        public virtual string? GetClassName() => null;
        public virtual string? GetComponentName() => null;
        public virtual PropertyDescriptorCollection GetProperties() => GetProperties(null);
        public virtual PropertyDescriptorCollection GetProperties(Attribute[]? attributes) => PropertyDescriptorCollection.Empty;
        public virtual object GetPropertyOwner(PropertyDescriptor? pd) => this;
        public AttributeCollection GetAttributes() => AttributeCollection.Empty;
        public TypeConverter GetConverter() => new TypeConverter();
        public EventDescriptor? GetDefaultEvent() => null;
        public PropertyDescriptor? GetDefaultProperty() => null;
        public object? GetEditor(Type editorBaseType) => null;
        public EventDescriptorCollection GetEvents() => EventDescriptorCollection.Empty;
        public EventDescriptorCollection GetEvents(Attribute[]? attributes) => EventDescriptorCollection.Empty;
    }
}
