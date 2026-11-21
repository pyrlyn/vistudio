using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace Vistudio.Rust
{
    /// <summary>
    /// Block structure for Rust editors (Sticky Scroll headers and structure guide lines): the header logic of
    /// <see cref="Core.Structure.CodeStructureScanner"/> on the text masked by <see cref="Core.Rust.RustSourceScanner.Mask"/>.
    /// Not collapsible: outlining comes from rust-analyzer's folding ranges.
    /// </summary>
    [Export(typeof(ITaggerProvider)), TagType(typeof(IStructureTag)), ContentType(RustContentTypes.Rust)]
    internal sealed class RustStructureTaggerProvider : StructureTaggerProvider { }
}
