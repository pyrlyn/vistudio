using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Vistudio.Core.Languages
{
    public enum FeatureStatus
    {
        /// <summary>Vistudio implements it for this language.</summary>
        Supported,
        /// <summary>Visual Studio itself provides it (C/C++ language service, built-in grammars); Vistudio leaves it alone.</summary>
        VisualStudio,
        /// <summary>Explicit stub: the language, its tools or the platform cannot support it. <see cref="FeatureCell.Note"/> says why.</summary>
        Unsupported,
        /// <summary>Possible but not implemented yet; <see cref="FeatureCell.Note"/> names the open task.</summary>
        Open,
    }

    public sealed class FeatureCell
    {
        internal FeatureCell(FeatureStatus status, string note) { Status = status; Note = note; }
        public FeatureStatus Status { get; }
        public string Note { get; }
    }

    /// <summary>
    /// Which IDE feature each language of <see cref="LanguageCatalog"/> has, with the reason for every cell that is not
    /// implemented. The explicit stubs (<see cref="FeatureStatus.Unsupported"/>) live here and next to the code they
    /// concern; <c>Vistudio.Tests.exe --feature-matrix</c> prints this table and checks it against the scanners.
    /// </summary>
    public static class LanguageFeatureMatrix
    {
        public const string Icons = "File icons";
        public const string Coloring = "Syntax coloring";
        public const string LanguageServer = "Completion, navigation (language server)";
        public const string InlayHints = "Inlay hints from the server";
        public const string InlayHintsDisplay = "Inlay hint display setting";
        public const string ErrorLens = "Error Lens";
        public const string StickyScroll = "Sticky Scroll / structure guides";
        public const string RainbowBraces = "Rainbow braces";
        public const string ScrollBarMap = "Scroll bar map setting";
        public const string ReferenceLens = "CodeLens: references";
        public const string LaunchLens = "CodeLens: Run | Debug";
        public const string TestLens = "CodeLens: run tests";
        public const string RestartCommand = "Restart language server command";
        public const string ProjectType = "Project type and template";
        public const string Packages = "Packages node";
        public const string Build = "Build / Run / Test";
        public const string BuildErrors = "Build errors in Error List";
        public const string Debugging = "F5 debugging";
        public const string Diagnostics = "CPU and memory while debugging";
        public const string HotReload = "Hot reload";
        public const string Preview = "UI preview";

        public static IReadOnlyList<string> Features { get; } = new[]
        {
            Icons, Coloring, LanguageServer, InlayHints, InlayHintsDisplay, ErrorLens, StickyScroll, RainbowBraces, ScrollBarMap,
            ReferenceLens, LaunchLens, TestLens, RestartCommand, ProjectType, Packages, Build, BuildErrors, Debugging, Diagnostics,
            HotReload, Preview,
        };

        /// <summary>Column order of the printed table (the catalog's own order starts with Dart).</summary>
        public static IReadOnlyList<LanguageInfo> Languages { get; } = new[]
        {
            LanguageCatalog.Kotlin, LanguageCatalog.Java, LanguageCatalog.Dart, LanguageCatalog.Zig, LanguageCatalog.Rust,
            LanguageCatalog.C, LanguageCatalog.Cpp, LanguageCatalog.Wasm,
        };

        private const string CppEditor = "Visual Studio's C/C++ language service owns the editor; the T58-T66 creator decision leaves its built-in editor features untouched";

        private static readonly Dictionary<(string Language, string Feature), FeatureCell> Cells = CreateCells();

        public static FeatureCell Get(LanguageInfo language, string feature) =>
            Cells.TryGetValue((language.Id, feature), out var cell) ? cell : throw new ArgumentException("No matrix cell for " + language.Id + " / " + feature);

        private static Dictionary<(string, string), FeatureCell> CreateCells()
        {
            var cells = new Dictionary<(string, string), FeatureCell>();
            void Row(string feature, params (LanguageInfo Language, FeatureStatus Status, string Note)[] entries)
            {
                foreach (var e in entries) cells[(e.Language.Id, feature)] = new FeatureCell(e.Status, e.Note);
            }
            (LanguageInfo, FeatureStatus, string) S(LanguageInfo l, string note = "") => (l, FeatureStatus.Supported, note);
            (LanguageInfo, FeatureStatus, string) V(LanguageInfo l, string note = "") => (l, FeatureStatus.VisualStudio, note);
            (LanguageInfo, FeatureStatus, string) U(LanguageInfo l, string why) => (l, FeatureStatus.Unsupported, why);
            (LanguageInfo, FeatureStatus, string) O(LanguageInfo l, string why) => (l, FeatureStatus.Open, why);
            LanguageInfo k = LanguageCatalog.Kotlin, j = LanguageCatalog.Java, d = LanguageCatalog.Dart, z = LanguageCatalog.Zig,
                r = LanguageCatalog.Rust, c = LanguageCatalog.C, p = LanguageCatalog.Cpp, w = LanguageCatalog.Wasm;

            Row(Icons, S(k), S(j), S(d), S(z), S(r), S(c), S(p), S(w));
            Row(Coloring, S(k, "Vistudio grammar"), V(j, "built-in Java grammar"), S(d, "Vistudio grammar"), S(z, "Vistudio grammar"),
                V(r, "built-in Rust grammar"), V(c), V(p), S(w, "Vistudio WAT grammar"));
            Row(LanguageServer, S(k, "Kotlin LSP"), S(j, "Eclipse JDT LS"), S(d, "analysis server"), S(z, "ZLS"), S(r, "rust-analyzer"), V(c), V(p), S(w, "wat_server"));
            Row(InlayHints, S(k), S(j), S(d), S(z), S(r), V(c), V(p), S(w));
            Row(InlayHintsDisplay, S(k), S(j), S(d), S(z), S(r), V(c, "Text Editor > C/C++ > Inlay Hints"), V(p, "Text Editor > C/C++ > Inlay Hints"), S(w));
            Row(ErrorLens, S(k), S(j), S(d), S(z), S(r), S(c), S(p), S(w));
            Row(StickyScroll, S(k), S(j), S(d), S(z), S(r), V(c, CppEditor), V(p, CppEditor), S(w, "folded forms and flat block/loop/if ... end"));
            Row(RainbowBraces, S(k), S(j), S(d), S(z), S(r),
                U(c, CppEditor), U(p, CppEditor),
                U(w, "the WebAssembly text format is S-expressions and has no curly braces"));
            Row(ScrollBarMap, S(k), S(j), S(d), S(z), S(r), V(c, "Text Editor > C/C++ > Scroll Bars"), V(p, "Text Editor > C/C++ > Scroll Bars"), S(w));
            Row(ReferenceLens, S(k), S(j), S(d), S(z), S(r),
                O(c, "T63: needs reference counts from Visual Studio's C/C++ language service, which offers no LSP connection to ask"),
                O(p, "T63: needs reference counts from Visual Studio's C/C++ language service, which offers no LSP connection to ask"), S(w));
            Row(LaunchLens, S(k), S(j), S(d), S(z, ".zigproj: native debugger; otherwise zig run"), S(r, ".rustproj: native debugger; otherwise cargo run"),
                O(c, "T63: C/C++ has no Vistudio CodeLens yet"), O(p, "T63: C/C++ has no Vistudio CodeLens yet"),
                U(w, "a module has no main declaration (the entry is a _start export) and there is no WebAssembly debugger; Ctrl+F5 on the project runs it"));
            Row(TestLens, O(k, "T34 (in progress): Kotlin and Java test run and debug from Test Explorer and editor lenses"), O(j, "T34 (in progress): Kotlin and Java test run and debug from Test Explorer and editor lenses"), S(d), O(z, "zig test --test-filter per test block"), O(r, "cargo test per #[test] function"),
                O(c, "T63: C/C++ has no Vistudio CodeLens yet"), O(p, "T63: C/C++ has no Vistudio CodeLens yet"), U(w, "the text format has no test declarations"));
            Row(RestartCommand, S(k), O(j, "JDT LS restarts on settings and workspace changes only; Tools > Kotlin > Restart Language Server restarts the Kotlin server alone"), S(d), S(z),
                O(r, "restarts on settings changes; no Tools menu for Rust yet"), V(c), V(p), O(w, "restarts on settings changes; no Tools menu for WebAssembly yet"));
            Row(ProjectType, S(k, ".kotlinproj"), S(j, ".javaproj"), S(d, ".dartproj"), S(z, ".zigproj"), S(r, ".rustproj"), S(c, ".cproj"), S(p, ".cppproj"), S(w, ".wasmproj"));
            Row(Packages, S(k, "Gradle"), S(j, "Gradle"), S(d, "pub"), S(z, "zig fetch"), S(r, "Cargo"), S(c, "Conan"), S(p, "Conan"), S(w, "wkg"));
            Row(Build, S(k, "Gradle"), S(j, "Gradle"), S(d, "dart / flutter"), S(z, "zig build"), S(r, "cargo"), S(c, "CMake + Conan"), S(p, "CMake + Conan"), S(w, "wasm-tools / wasmtime"));
            Row(BuildErrors, S(k, "Gradle parser"), S(j, ".javaproj (javac); a .kotlinproj lists Kotlin errors only"), S(d), S(z), S(r), S(c), S(p), S(w));
            Row(Debugging, S(k, "JVM targets; JS, Wasm and Native targets run with diagnostics (no debug adapter)"), S(j, "Kotlin/JVM session"), S(d, "Dart / Flutter adapter"),
                S(z, "native engine (PDB)"), S(r, "native engine (PDB)"), S(c, "native engine"), S(p, "native engine"),
                U(w, "no WebAssembly debug engine: F5 runs the Run command (LanguageProjectConfig)"));
            Row(Diagnostics, S(k), S(j), S(d), S(z, "Diagnostic Tools"), S(r, "Diagnostic Tools"), S(c, "Diagnostic Tools"), S(p, "Diagnostic Tools"), S(w, "process counters"));
            Row(HotReload, S(k, "JVM: method bodies"), S(j, "JVM: method bodies"), S(d, "Flutter apps"),
                U(z, "the native engine's Edit and Continue needs MSVC /ZI images; zig does not produce them"),
                U(r, "the native engine's Edit and Continue needs MSVC /ZI images; rustc does not produce them"),
                V(c, "Edit and Continue of the native engine, for MSVC /ZI builds only"), V(p, "Edit and Continue of the native engine, for MSVC /ZI builds only"),
                U(w, "wasmtime cannot replace a running module's code"));
            const string NoUi = "no UI toolkit of the language declares previews the IDE could render";
            Row(Preview, S(k, "Compose Multiplatform @Preview"), U(j, NoUi), S(d, "Flutter widget previews"), U(z, NoUi), U(r, NoUi), U(c, NoUi), U(p, NoUi), U(w, NoUi));
            return cells;
        }

        /// <summary>The table as Markdown: one row per feature, one column per language, then the notes.</summary>
        public static string ToMarkdown()
        {
            var text = new StringBuilder();
            text.AppendLine("| Feature | " + string.Join(" | ", Languages.Select(l => l.DisplayName)) + " |");
            text.AppendLine("| --- |" + string.Concat(Languages.Select(_ => " :---: |")));
            var notes = new List<string>();
            foreach (var feature in Features)
            {
                var row = new List<string>();
                foreach (var language in Languages)
                {
                    var cell = Get(language, feature);
                    var mark = cell.Status switch { FeatureStatus.Supported => "✓", FeatureStatus.VisualStudio => "VS", FeatureStatus.Unsupported => "⊘", _ => "–" };
                    if (cell.Note.Length > 0 && cell.Status != FeatureStatus.Supported)
                    {
                        var index = notes.IndexOf(cell.Note);
                        if (index < 0) { notes.Add(cell.Note); index = notes.Count - 1; }
                        mark += " [" + (index + 1) + "]";
                    }
                    row.Add(mark);
                }
                text.AppendLine("| " + feature.Replace("|", "\\|") + " | " + string.Join(" | ", row) + " |");
            }
            text.AppendLine();
            for (var i = 0; i < notes.Count; i++) text.AppendLine((i + 1) + ". " + notes[i]);
            return text.ToString();
        }
    }
}
