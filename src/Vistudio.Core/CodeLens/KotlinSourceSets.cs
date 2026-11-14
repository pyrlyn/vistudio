using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Vistudio.Core.CodeLens
{
    /// <summary>A declaration found in another file: 0-based LSP position of its name.</summary>
    public sealed class CodeLocation
    {
        public CodeLocation(string path, int line, int column, string qualifiedName)
        { Path = path; Line = line; Column = column; QualifiedName = qualifiedName; }
        public string Path { get; }
        public int Line { get; }
        public int Column { get; }
        public string QualifiedName { get; }
    }

    /// <summary>
    /// Kotlin Multiplatform source sets by the Gradle convention <c>&lt;module&gt;/src/&lt;sourceSet&gt;/kotlin</c>, and the
    /// <c>actual</c> → <c>expect</c> lookup the Kotlin language server does not answer.
    /// </summary>
    public static class KotlinSourceSets
    {
        /// <summary>The source set directory name of a file (<c>jvmMain</c>), or null outside <c>src/&lt;set&gt;/</c>.</summary>
        public static string? SourceSetOf(string path) => SplitSourceSet(path)?.SourceSet;

        /// <summary>A short platform name for labels: <c>jvmMain</c> → <c>jvm</c>, <c>commonMain</c> → <c>common</c>.</summary>
        public static string TargetName(string sourceSet)
        {
            foreach (var suffix in new[] { "Main", "Test" })
                if (sourceSet.Length > suffix.Length && sourceSet.EndsWith(suffix, StringComparison.Ordinal))
                    return sourceSet.Substring(0, sourceSet.Length - suffix.Length);
            return sourceSet;
        }

        private static (string SourceRoot, string SourceSet)? SplitSourceSet(string path)
        {
            var parts = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // The innermost `src/<set>/` wins: a module may live in a folder that is itself called `src`.
            for (var i = parts.Length - 3; i >= 0; i--)
                if (string.Equals(parts[i], "src", StringComparison.OrdinalIgnoreCase))
                    return (string.Join(Path.DirectorySeparatorChar.ToString(), parts, 0, i + 1), parts[i + 1]);
            return null;
        }

        /// <summary>
        /// The <c>expect</c> declaration of an <c>actual</c> one: same package, qualified name and kind, in another source
        /// set of the same module. Common source sets are searched first. Null when nothing matches.
        /// </summary>
        public static CodeLocation? FindExpect(string actualPath, string actualText, CodeDeclaration actual)
        {
            var split = SplitSourceSet(actualPath);
            if (split == null || !Directory.Exists(split.Value.SourceRoot)) return null;
            var package = CodeDeclarationScanner.PackageOf(actualPath, actualText);
            var sets = Directory.GetDirectories(split.Value.SourceRoot)
                .Where(d => !string.Equals(Path.GetFileName(d), split.Value.SourceSet, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => Path.GetFileName(d).StartsWith("common", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(d => d, StringComparer.OrdinalIgnoreCase);
            foreach (var set in sets)
                foreach (var file in Directory.EnumerateFiles(set, "*.kt", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string text;
                    try { text = File.ReadAllText(file); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    if (text.IndexOf("expect", StringComparison.Ordinal) < 0 || CodeDeclarationScanner.PackageOf(file, text) != package) continue;
                    var match = CodeDeclarationScanner.ScanKotlin(text)
                        .FirstOrDefault(d => d.IsExpect && d.Kind == actual.Kind && d.QualifiedName == actual.QualifiedName);
                    if (match != null) return new CodeLocation(file, match.NameLine, match.NameColumn, match.QualifiedName);
                }
            return null;
        }

        /// <summary>
        /// A member <paramref name="member"/> declared in type <paramref name="typeName"/> (simple name) of a Kotlin or
        /// Java source: used to find the base of an override in a supertype returned by the type hierarchy.
        /// </summary>
        public static CodeDeclaration? FindMember(string path, string text, string typeName, string member, CodeDeclarationKind kind)
        {
            foreach (var declaration in CodeDeclarationScanner.Scan(path, text))
            {
                if (declaration.Name != member || declaration.Container == null) continue;
                var container = declaration.Container.Substring(declaration.Container.LastIndexOf('.') + 1);
                if (container != typeName) continue;
                // A Kotlin property may override a Java getter and vice versa: accept either member kind.
                if (declaration.Kind == kind || declaration.Kind == CodeDeclarationKind.Function || declaration.Kind == CodeDeclarationKind.Property)
                    return declaration;
            }
            return null;
        }

        /// <summary>
        /// Text of a source the language server points at: a <c>file:</c> URI or a <c>jar:</c> URI into a sources jar
        /// (<c>jar:///c%3A/…/x-sources.jar!/kotlin/Any.kt</c>). Null when the source cannot be read.
        /// </summary>
        public static string? ReadSource(string uri)
        {
            try
            {
                if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    var path = new Uri(uri).LocalPath;
                    return File.Exists(path) ? File.ReadAllText(path) : null;
                }
                if (!uri.StartsWith("jar:", StringComparison.OrdinalIgnoreCase)) return null;
                var separator = uri.IndexOf("!/", StringComparison.Ordinal);
                if (separator < 0) return null;
                var archive = Uri.UnescapeDataString(uri.Substring(4, separator - 4)).TrimStart('/');
                if (archive.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) archive = new Uri(archive).LocalPath;
                var entry = Uri.UnescapeDataString(uri.Substring(separator + 2));
                if (!File.Exists(archive)) return null;
                using (var zip = ZipFile.OpenRead(archive))
                {
                    var item = zip.GetEntry(entry);
                    if (item == null) return null;
                    using (var reader = new StreamReader(item.Open())) return reader.ReadToEnd();
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is UriFormatException || e is InvalidDataException || e is ArgumentException)
            {
                return null;
            }
        }
    }
}
