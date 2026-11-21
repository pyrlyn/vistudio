using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace Vistudio.Dart
{
    /// <summary>
    /// Block structure for Dart editors (Sticky Scroll headers and structure guide lines): the Kotlin/Java header logic of
    /// <see cref="Core.Structure.CodeStructureScanner"/> on the text masked by <see cref="Core.Dart.DartSourceScanner.Mask"/>.
    /// Not collapsible: outlining comes from the analysis server's folding ranges.
    /// </summary>
    [Export(typeof(ITaggerProvider)), TagType(typeof(IStructureTag)), ContentType(DartContentTypes.Dart)]
    internal sealed class DartStructureTaggerProvider : StructureTaggerProvider { }
}
