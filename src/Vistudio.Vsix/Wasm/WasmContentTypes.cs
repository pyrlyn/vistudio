using System.ComponentModel.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;

namespace Vistudio.Wasm
{
    internal static class WasmContentTypes
    {
#pragma warning disable CS0649 // MEF metadata declarations are intentionally unassigned.
        // The content type name is the LSP languageId wat_server expects. Vistudio's grammar (Grammars/Wat.tmLanguage.json)
        // colors both content types, as Kotlin's grammar does for `kotlin`.
        internal const string Wat = "wat";
        [Export, Name(Wat), BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
        internal static ContentTypeDefinition? Definition;
        [Export, FileExtension(".wat"), ContentType(Wat)]
        internal static FileExtensionToContentTypeDefinition? Text;

        // WAST scripts (spec test files) extend the text format with assertions; wat_server does not parse them, so they
        // get their own content type that no language client is registered for.
        internal const string Wast = "wast";
        [Export, Name(Wast), BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
        internal static ContentTypeDefinition? ScriptDefinition;
        [Export, FileExtension(".wast"), ContentType(Wast)]
        internal static FileExtensionToContentTypeDefinition? Script;
#pragma warning restore CS0649
    }
}
