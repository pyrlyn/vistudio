using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace Vistudio.Zig
{
    /// <summary>
    /// Block structure for Zig editors (Sticky Scroll headers and structure guide lines), from the lexical
    /// <see cref="Core.Zig.ZigSourceScanner"/>. Not collapsible: outlining comes from ZLS folding ranges, as for Kotlin/Java.
    /// </summary>
    [Export(typeof(ITaggerProvider)), TagType(typeof(IStructureTag)), ContentType(ZigContentTypes.Zig)]
    internal sealed class ZigStructureTaggerProvider : StructureTaggerProvider { }
}
