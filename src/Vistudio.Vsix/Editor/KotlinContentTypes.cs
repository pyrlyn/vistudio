using System.ComponentModel.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;

namespace Vistudio
{
    internal static class KotlinContentTypes
    {
#pragma warning disable CS0649 // MEF metadata declarations are intentionally unassigned.
        // VS uses the content type name as the LSP textDocument.languageId.
        internal const string Kotlin = "kotlin";
        [Export, Name(Kotlin), BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
        internal static ContentTypeDefinition? Definition;
        [Export, FileExtension(".kt"), ContentType(Kotlin)]
        internal static FileExtensionToContentTypeDefinition? File;
        [Export, FileExtension(".kts"), ContentType(Kotlin)]
        internal static FileExtensionToContentTypeDefinition? Script;
#pragma warning restore CS0649
    }
}
