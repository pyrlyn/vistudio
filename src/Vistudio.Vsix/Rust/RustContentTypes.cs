using System.ComponentModel.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;

namespace Vistudio.Rust
{
    internal static class RustContentTypes
    {
#pragma warning disable CS0649 // MEF metadata declarations are intentionally unassigned.
        // The content type name is the LSP languageId rust-analyzer expects. Visual Studio's own Rust TextMate grammar
        // (Starterkit "rust", scope source.rust, file type rs) colors documents of this remote-code content type.
        internal const string Rust = "rust";
        [Export, Name(Rust), BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
        internal static ContentTypeDefinition? Definition;
        [Export, FileExtension(".rs"), ContentType(Rust)]
        internal static FileExtensionToContentTypeDefinition? Source;
#pragma warning restore CS0649
    }
}
