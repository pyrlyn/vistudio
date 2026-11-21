using System.Collections.Generic;
using Vistudio.Core.Editor;
using Vistudio.Core.Wasm;
using Vistudio.Core.Zig;

namespace Vistudio.Core.Structure
{
    /// <summary>Editor block structure (Sticky Scroll headers, structure guides) of each <see cref="EditorLanguage"/>, from its lexical scanner.</summary>
    public static class SourceStructure
    {
        public static IReadOnlyList<CodeBlock> Scan(EditorLanguage language, string text)
        {
            switch (language)
            {
                case EditorLanguage.Zig:
                    return ZigSourceScanner.Scan(text).Blocks;
                case EditorLanguage.Wat:
                    return WatStructureScanner.Scan(text);
                default:
                    return CodeStructureScanner.Scan(text, language); // Kotlin, Java, Dart, Rust
            }
        }
    }
}
