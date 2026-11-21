using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace Vistudio.Wasm
{
    /// <summary>
    /// Block structure for WebAssembly text editors (Sticky Scroll headers and structure guide lines) from
    /// <see cref="Core.Wasm.WatStructureScanner"/>: multi-line folded forms and flat block/loop/if ... end.
    /// Not collapsible: outlining comes from wat_server's folding ranges.
    /// </summary>
    [Export(typeof(ITaggerProvider)), TagType(typeof(IStructureTag)), ContentType(WasmContentTypes.Wat)]
    internal sealed class WatStructureTaggerProvider : StructureTaggerProvider { }
}
