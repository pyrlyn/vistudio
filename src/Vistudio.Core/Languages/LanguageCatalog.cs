using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vistudio.Core.Packages;

namespace Vistudio.Core.Languages
{
    /// <summary>
    /// One language the extension knows: the file extensions it claims, its project file extension, the icon of
    /// <c>Icons/Languages.imagemanifest</c> and the package manager its projects use. Facts only; the services that
    /// act on a language (servers, debuggers, builds) live with that language.
    /// </summary>
    public sealed class LanguageInfo
    {
        internal LanguageInfo(string id, string displayName, string[] extensions, string projectExtension, int iconId,
            PackageEcosystem packages, bool editorFromVisualStudio)
        {
            Id = id;
            DisplayName = displayName;
            Extensions = extensions;
            ProjectExtension = projectExtension;
            IconId = iconId;
            Packages = packages;
            EditorFromVisualStudio = editorFromVisualStudio;
        }

        /// <summary>Stable lower-case id, also the prefix of its workspace settings (<c>vistudio.&lt;id&gt;.*</c>).</summary>
        public string Id { get; }
        public string DisplayName { get; }

        /// <summary>Source extensions with the dot, lower case.</summary>
        public IReadOnlyList<string> Extensions { get; }

        /// <summary>The project file extension with the dot (<c>.rustproj</c>).</summary>
        public string ProjectExtension { get; }

        /// <summary>Image id in <see cref="LanguageCatalog.IconGuid"/>; 0 when Visual Studio's own icon is used.</summary>
        public int IconId { get; }

        public PackageEcosystem Packages { get; }

        /// <summary>
        /// Visual Studio itself provides completion, inline hints and debugging (Visual C++); Vistudio must not
        /// start a second language server for these files.
        /// </summary>
        public bool EditorFromVisualStudio { get; }

        public bool Claims(string path) => Extensions.Contains(Path.GetExtension(path ?? "").ToLowerInvariant());
    }

    public static class LanguageCatalog
    {
        /// <summary>Guid of <c>Icons/Languages.imagemanifest</c>.</summary>
        public static readonly Guid IconGuid = new Guid("2d0f6a15-591d-4131-b160-d834f3945b8f");

        public static readonly LanguageInfo Kotlin = new LanguageInfo("kotlin", "Kotlin", new[] { ".kt", ".kts" }, ".kotlinproj", 0, PackageEcosystem.Gradle, false);
        public static readonly LanguageInfo Java = new LanguageInfo("java", "Java", new[] { ".java" }, ".javaproj", 4, PackageEcosystem.Gradle, false);
        public static readonly LanguageInfo Dart = new LanguageInfo("dart", "Dart", new[] { ".dart" }, ".dartproj", 1, PackageEcosystem.Pub, false);
        public static readonly LanguageInfo Zig = new LanguageInfo("zig", "Zig", new[] { ".zig", ".zon" }, ".zigproj", 2, PackageEcosystem.Zig, false);
        public static readonly LanguageInfo Rust = new LanguageInfo("rust", "Rust", new[] { ".rs" }, ".rustproj", 5, PackageEcosystem.Cargo, false);
        public static readonly LanguageInfo Wasm = new LanguageInfo("wasm", "WebAssembly", new[] { ".wat", ".wast", ".wasm" }, ".wasmproj", 7, PackageEcosystem.Wkg, false);
        // Headers are shared by C and C++; they belong to C++ so a mixed folder is not split by extension.
        public static readonly LanguageInfo C = new LanguageInfo("c", "C", new[] { ".c" }, ".cproj", 8, PackageEcosystem.Conan, true);
        public static readonly LanguageInfo Cpp = new LanguageInfo("cpp", "C++", new[] { ".cpp", ".cc", ".cxx", ".c++", ".h", ".hh", ".hpp", ".hxx", ".ixx", ".cppm" }, ".cppproj", 9, PackageEcosystem.Conan, true);

        public static IReadOnlyList<LanguageInfo> All { get; } = new[] { Dart, Kotlin, Zig, Java, C, Cpp, Wasm, Rust };

        public static LanguageInfo? ForFile(string path) => All.FirstOrDefault(l => l.Claims(path));

        public static LanguageInfo? ForProjectFile(string path) =>
            All.FirstOrDefault(l => string.Equals(Path.GetExtension(path ?? ""), l.ProjectExtension, StringComparison.OrdinalIgnoreCase));

        public static LanguageInfo? ById(string id) => All.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Icons of <c>Icons/Languages.imagemanifest</c> beyond the one per language.</summary>
    public static class LanguageIcons
    {
        /// <summary>A .dartproj whose pubspec depends on the Flutter SDK.</summary>
        public const int FlutterProject = 10;
        /// <summary>BUILD, BUILD.bazel, MODULE.bazel, WORKSPACE, .bzl and other Starlark files.</summary>
        public const int BazelFile = 11;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Time, bool Flutter)> flutter =
            new System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime, bool)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The project node's icon: Flutter for Flutter apps and packages, otherwise the language's.</summary>
        public static int ForProject(LanguageProjectFile project) =>
            project.Language == LanguageCatalog.Dart && IsFlutter(project.DirectoryPath) ? FlutterProject : project.Language.IconId;

        /// <summary>The icon of a file Visual Studio cannot map by extension; 0 for none.</summary>
        public static int ForFileName(string? path) => Bazel.BazelWorkspace.IsStarlarkFile(path) ? BazelFile : 0;

        private static bool IsFlutter(string folder)
        {
            var pubspec = Path.Combine(folder, "pubspec.yaml");
            DateTime time;
            try { time = File.Exists(pubspec) ? File.GetLastWriteTimeUtc(pubspec) : DateTime.MinValue; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException) { return false; }
            if (flutter.TryGetValue(folder, out var cached) && cached.Time == time) return cached.Flutter;
            var value = time != DateTime.MinValue && LanguageProjectDefaults.IsFlutter(folder);
            flutter[folder] = (time, value);
            return value;
        }
    }
}
