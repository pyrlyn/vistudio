using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Vistudio.Core.Projects;

namespace Vistudio
{
    /// <summary>Read-only Properties window view of a project tree node, including Gradle resolution provenance.</summary>
    [ComVisible(true)]
    public sealed class KotlinNodeProperties : CustomTypeDescriptor
    {
        private readonly ProjectTreeNode node;

        internal KotlinNodeProperties(ProjectTreeNode node) => this.node = node;

        public override string GetClassName() => "Kotlin " + node.Kind;
        public override string GetComponentName() => node.Caption;
        public override PropertyDescriptorCollection GetProperties() => GetProperties(null);

        public override PropertyDescriptorCollection GetProperties(Attribute[]? attributes)
        {
            var values = new[] { ("Name", node.Caption), ("Kind", node.Kind.ToString()), ("Full Path", node.Path ?? "") }
                .Where(v => v.Item2.Length != 0).Concat(node.Details.Select(d => (d.Key, d.Value)));
            return new PropertyDescriptorCollection(values.Select(v => (PropertyDescriptor)new Value(v.Item1, v.Item2)).ToArray(), true);
        }

        public override object GetPropertyOwner(PropertyDescriptor? pd) => this;

        private sealed class Value : PropertyDescriptor
        {
            private readonly string value;
            public Value(string name, string value) : base(name, new Attribute[] { new CategoryAttribute("Kotlin") }) => this.value = value;
            public override Type ComponentType => typeof(KotlinNodeProperties);
            public override bool IsReadOnly => true;
            public override Type PropertyType => typeof(string);
            public override bool CanResetValue(object component) => false;
            public override object GetValue(object? component) => value;
            public override void ResetValue(object component) { }
            public override void SetValue(object? component, object? value) => throw new NotSupportedException();
            public override bool ShouldSerializeValue(object component) => false;
        }
    }
}
