using System.ComponentModel.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;

namespace Vistudio.Bazel
{
    internal static class BazelContentTypes
    {
#pragma warning disable CS0649 // MEF metadata declarations are intentionally unassigned.
        // The content type name is the LSP languageId starpls and bazel-lsp accept. Vistudio's grammar
        // (Grammars/Starlark.tmLanguage.json) colors it; BUILD and WORKSPACE have no extension, so they map by file name.
        internal const string Starlark = "starlark";
        [Export, Name(Starlark), BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
        internal static ContentTypeDefinition? Definition;
        [Export, FileExtension(".bzl"), ContentType(Starlark)]
        internal static FileExtensionToContentTypeDefinition? Extension;
        // BUILD.bazel, MODULE.bazel, WORKSPACE.bazel, REPO.bazel.
        [Export, FileExtension(".bazel"), ContentType(Starlark)]
        internal static FileExtensionToContentTypeDefinition? BazelFiles;
        [Export, FileExtension(".bzlmod"), ContentType(Starlark)]
        internal static FileExtensionToContentTypeDefinition? Bzlmod;
        [Export, FileExtension(".star"), ContentType(Starlark)]
        internal static FileExtensionToContentTypeDefinition? Star;
        [Export, FileName("BUILD"), ContentType(Starlark)]
        internal static FileExtensionToContentTypeDefinition? Build;
        [Export, FileName("WORKSPACE"), ContentType(Starlark)]
        internal static FileExtensionToContentTypeDefinition? Workspace;
#pragma warning restore CS0649
    }
}
