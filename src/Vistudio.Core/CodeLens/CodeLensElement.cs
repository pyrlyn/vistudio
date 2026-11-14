using System;
using System.Collections.Generic;
using System.Linq;

namespace Vistudio.Core.CodeLens
{
    /// <summary>
    /// Identity of a CodeLens code element shared by the editor tagger and the lens providers:
    /// <c>vistudio|IM|Kind|Qualified.Name[#ordinal]</c>, where <c>I</c> marks a declaration that can have
    /// implementations, <c>M</c> a JVM entry point, and the ordinal separates overloads in one file. Declarations
    /// with <see cref="CodeDeclarationTraits"/> carry them as a number after the kind (<c>Function/5</c>); Gradle
    /// script tasks use the kind <c>GradleTask</c> and the task name.
    /// </summary>
    public sealed class CodeLensElement
    {
        private const string Prefix = "vistudio|";

        internal const string GradleTaskKind = "GradleTask";

        private CodeLensElement(bool implementable, bool main, string kind, CodeDeclarationTraits traits, string name)
        { CanBeImplemented = implementable; IsMain = main; Kind = kind; Traits = traits; Name = name; }

        public bool CanBeImplemented { get; }
        public bool IsMain { get; }
        /// <summary><see cref="CodeDeclarationKind"/> name, or <c>GradleTask</c>.</summary>
        public string Kind { get; }
        public CodeDeclarationTraits Traits { get; }
        /// <summary>Qualified declaration name or Gradle task name, without the overload ordinal.</summary>
        public string Name { get; }
        public bool IsGradleTask => Kind == GradleTaskKind;
        public bool Has(CodeDeclarationTraits trait) => (Traits & trait) != 0;

        /// <summary>One identifier per declaration, in order; identifiers are unique within the file.</summary>
        public static IReadOnlyList<string> Identifiers(IReadOnlyList<CodeDeclaration> declarations) =>
            WithOrdinals(declarations.Select(declaration =>
            {
                var kind = declaration.Kind + (declaration.Traits == CodeDeclarationTraits.None ? "" : "/" + (int)declaration.Traits);
                return Prefix + (declaration.CanBeImplemented ? "I" : "-") + (declaration.IsMain ? "M" : "-") + "|" + kind + "|" + declaration.QualifiedName;
            }));

        /// <summary>Identifiers of Gradle script tasks; a task registered twice keeps an ordinal like overloads.</summary>
        public static IReadOnlyList<string> Identifiers(IReadOnlyList<GradleScriptTask> tasks) =>
            WithOrdinals(tasks.Select(task => Prefix + "--|" + GradleTaskKind + "|" + task.Name));

        /// <summary>
        /// Makes identifiers unique within one file: the second and later occurrences of an identifier get <c>#1</c>,
        /// <c>#2</c>, ... in document order, so overloads and duplicate names keep stable, distinct lenses.
        /// </summary>
        public static IReadOnlyList<string> WithOrdinals(IEnumerable<string> identifiers)
        {
            var result = new List<string>();
            var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var id in identifiers)
            {
                ordinals.TryGetValue(id, out var ordinal);
                ordinals[id] = ordinal + 1;
                result.Add(ordinal == 0 ? id : id + "#" + ordinal);
            }
            return result;
        }

        /// <summary>Null for code elements tagged by anyone else (for example Roslyn).</summary>
        public static CodeLensElement? TryParse(string? identifier)
        {
            if (identifier == null || identifier.Length < Prefix.Length + 3 || !identifier.StartsWith(Prefix, StringComparison.Ordinal) || identifier[Prefix.Length + 2] != '|') return null;
            var kind = "";
            var traits = CodeDeclarationTraits.None;
            var name = "";
            var rest = identifier.Substring(Prefix.Length + 3);
            var bar = rest.IndexOf('|');
            if (bar >= 0)
            {
                kind = rest.Substring(0, bar);
                name = rest.Substring(bar + 1);
                var hash = name.LastIndexOf('#');
                if (hash > 0) name = name.Substring(0, hash);
                var slash = kind.IndexOf('/');
                if (slash >= 0)
                {
                    if (int.TryParse(kind.Substring(slash + 1), out var bits)) traits = (CodeDeclarationTraits)bits;
                    kind = kind.Substring(0, slash);
                }
            }
            return new CodeLensElement(identifier[Prefix.Length] == 'I', identifier[Prefix.Length + 1] == 'M', kind, traits, name);
        }

        /// <summary>
        /// LSP position (0-based line, UTF-16 character) for the server queries of a lens whose tagged range is
        /// <paramref name="start"/>..<paramref name="end"/>: the range ends at the declaration name, so the position is
        /// the name's last character (inside the backticks of a Kotlin <c>`quoted name`</c>).
        /// </summary>
        public static (int Line, int Character) NamePosition(string text, int start, int end)
        {
            var offset = Math.Min(Math.Max(start, end - 1), text.Length);
            if (offset > start && offset < text.Length && text[offset] == '`') offset--;
            var line = 0;
            var lineStart = 0;
            for (var i = 0; i < offset; i++)
                if (text[i] == '\n') { line++; lineStart = i + 1; }
            return (line, offset - lineStart);
        }

        /// <summary>"1 reference", "3 implementations".</summary>
        public static string CountLabel(int count, string noun) => count + " " + noun + (count == 1 ? "" : "s");
    }
}
