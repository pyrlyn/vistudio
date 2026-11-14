using System.ComponentModel.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;

namespace Vistudio.Dart
{
    internal static class DartContentTypes
    {
#pragma warning disable CS0649 // MEF metadata declarations are intentionally unassigned.
        // The content type name is the LSP languageId the Dart analysis server expects. The Dart TextMate grammar
        // (Grammars/Dart.tmLanguage.json) colors documents of this remote-code content type, as for `kotlin` and `zig`.
        internal const string Dart = "dart";
        [Export, Name(Dart), BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
        internal static ContentTypeDefinition? Definition;
        [Export, FileExtension(".dart"), ContentType(Dart)]
        internal static FileExtensionToContentTypeDefinition? Source;
#pragma warning restore CS0649
    }
}
