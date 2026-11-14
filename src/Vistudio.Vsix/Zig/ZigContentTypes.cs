using System.ComponentModel.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;

namespace Vistudio.Zig
{
    internal static class ZigContentTypes
    {
#pragma warning disable CS0649 // MEF metadata declarations are intentionally unassigned.
        // The content type name is the LSP languageId ZLS expects. The Zig TextMate grammar (Grammars/Zig.tmLanguage.json)
        // colors documents of this remote-code content type, as Kotlin's grammar does for `kotlin`.
        internal const string Zig = "zig";
        [Export, Name(Zig), BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
        internal static ContentTypeDefinition? Definition;
        [Export, FileExtension(".zig"), ContentType(Zig)]
        internal static FileExtensionToContentTypeDefinition? Source;
        // ZON (Zig Object Notation, build.zig.zon) is Zig expression syntax; ZLS and ast-check (--zon) both accept it.
        [Export, FileExtension(".zon"), ContentType(Zig)]
        internal static FileExtensionToContentTypeDefinition? ObjectNotation;
#pragma warning restore CS0649
    }
}
