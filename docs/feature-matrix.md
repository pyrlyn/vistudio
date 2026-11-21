# Features of each language (T74)

Creator request (2026-11-21): build a language × feature matrix; where a feature exists for some languages and not others, carry it over with the pattern of the existing implementation; where it exists for one language only, first extract a shared interface or base class, then connect every language; leave what the platform cannot support as an explicit stub with the reason; run the tests of every language and attach the updated matrix.

Scope: the eight languages of T58-T66 (Kotlin, Java, Dart, Zig, Rust, C, C++, WebAssembly). Bazel/Starlark files are a build-file editor (completion, validation) and are not a column. The matrix is data in `src/Vistudio.Core/Languages/LanguageFeatureMatrix.cs`; `Vistudio.Tests.exe --feature-matrix` checks every cell exists, that every stub and open cell has a reason, and that the Sticky Scroll, rainbow brace, scroll bar and inlay hint cells match what the scanners and settings actually do, then prints the table below.

Legend: ✓ implemented by Vistudio · VS supplied by Visual Studio itself, left alone · ⊘ explicit stub (the language, its tools or the platform cannot support it; reason in the note) · – open, not implemented (note names why or the task).

## Matrix after this change

| Feature | Kotlin | Java | Dart | Zig | Rust | C | C++ | WebAssembly |
| --- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| File icons | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Syntax coloring | ✓ | VS [1] | ✓ | ✓ | VS [2] | VS | VS | ✓ |
| Completion, navigation (language server) | ✓ | ✓ | ✓ | ✓ | ✓ | VS | VS | ✓ |
| Inlay hints from the server | ✓ | ✓ | ✓ | ✓ | ✓ | VS | VS | ✓ |
| Inlay hint display setting | ✓ | ✓ | **✓ new** | **✓ new** | **✓ new** | VS [3] | VS [3] | **✓ new** |
| Error Lens | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Sticky Scroll / structure guides | ✓ | ✓ | **✓ new** | ✓ | **✓ new** | VS [4] | VS [4] | **✓ new** |
| Rainbow braces | ✓ | **✓ new** | **✓ new** | **✓ new** | **✓ new** | ⊘ [4] | ⊘ [4] | ⊘ [5] |
| Scroll bar map setting | ✓ | ✓ | **✓ new** | **✓ new** | **✓ new** | VS [6] | VS [6] | **✓ new** |
| CodeLens: references | ✓ | ✓ | ✓ | ✓ | ✓ | – [7] | – [7] | ✓ |
| CodeLens: Run \| Debug | ✓ | ✓ | ✓ | **✓ new** | ✓ | – [8] | – [8] | ⊘ [9] |
| CodeLens: run tests | – [10] | – [10] | ✓ | **✓ new** [11] | **✓ new** [12] | – [8] | – [8] | ⊘ [13] |
| Restart language server command | ✓ | – [14] | ✓ | ✓ | – [15] | VS | VS | – [16] |
| Project type and template | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Packages node | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Build / Run / Test | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Build errors in Error List | ✓ | **✓ new** [17] | ✓ (+ project builds) | ✓ (+ project builds) | **✓ new** | **✓ new** | **✓ new** | **✓ new** |
| F5 debugging | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ⊘ [18] |
| CPU and memory while debugging | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Hot reload | ✓ | ✓ | ✓ | ⊘ [19] | ⊘ [20] | VS [21] | VS [21] | ⊘ [22] |
| UI preview | ✓ | ⊘ [23] | ✓ | ⊘ [23] | ⊘ [23] | ⊘ [23] | ⊘ [23] | ⊘ [23] |

1. Visual Studio's built-in Java grammar.
2. Visual Studio's built-in Rust grammar.
3. Text Editor > C/C++ > Inlay Hints.
4. Visual Studio's C/C++ language service owns the editor; the T58-T66 creator decision leaves its built-in editor features untouched (no Vistudio taggers on C/C++ buffers).
5. The WebAssembly text format is S-expressions and has no curly braces (stub in `RainbowBraces.Scan`; `WasmSettings` answers `false` through an explicit interface member, so it is neither stored nor a workspace key).
6. Text Editor > C/C++ > Scroll Bars.
7. T63: needs reference counts from Visual Studio's C/C++ language service, which offers no LSP connection to ask.
8. T63: C/C++ has no Vistudio CodeLens yet.
9. A module has no `main` declaration (its entry is a `_start` export) and there is no WebAssembly debugger; Ctrl+F5 on the `.wasmproj` runs it.
10. T34 (in progress): Kotlin and Java test run and debug from Test Explorer and editor lenses.
11. T75: **Run test** (`zig test <file> --test-filter <name>`, Zig pane, Error List) and **Debug test** (`--test-no-exec -femit-bin=zig-out\vistudio\test-<file>.exe`, then the native debugger) above each `test` block. Zig matches the filter as a substring of the full test name; an unnamed `test { }` runs the file's tests.
12. T75: **Run test** (`cargo test [--lib|--bin x|--test x] -- <crate path> --exact`, Rust pane) and **Debug test** (`cargo test --no-run --message-format=json`, then the test program of the file's crate under the native debugger with `<crate path> --exact --nocapture`) above `#[test]` / `#[x::test]` functions; the crate path adds the file's module path (`src/net/http.rs` → `net::http::`).
13. The text format has no test declarations.
14. JDT LS restarts on settings and workspace changes only; Tools > Kotlin > Restart Language Server restarts the Kotlin server alone.
15. rust-analyzer restarts on settings changes; there is no Tools menu for Rust to put the command in yet.
16. wat_server restarts on settings changes; there is no Tools menu for WebAssembly yet.
17. `.javaproj` builds (javac lines through Gradle). A `.kotlinproj` still lists Kotlin compiler errors only (Gradle parser, unchanged).
18. No WebAssembly debug engine: F5 runs the Run command (existing stub in `LanguageProjectConfig`).
19. The native engine's Edit and Continue needs MSVC `/ZI` images; zig does not produce them.
20. The native engine's Edit and Continue needs MSVC `/ZI` images; rustc does not produce them.
21. Edit and Continue of Visual Studio's native engine, for MSVC `/ZI` builds only (Vistudio does not change the CMake flags).
22. wasmtime cannot replace a running module's code.
23. No UI toolkit of the language declares previews the IDE could render (Compose `@Preview` and Flutter `@Preview` are the two that do).

## What changed, feature by feature

| Feature | Before | Shared part (extracted or reused) | Connected now | Stub |
| --- | --- | --- | --- | --- |
| Sticky Scroll / structure guides | Kotlin, Java (`JvmStructureTagger`), Zig (`ZigStructureTagger`, a near copy) | Base `StructureTaggerProvider` + one `SourceStructureTagger`; Core `SourceStructure.Scan(EditorLanguage, text)`; `CodeStructureScanner` takes a language (Dart and Rust keyword tables, Rust attributes and `match` arms) | Dart (`DartSourceScanner.Mask`), Rust (new `RustSourceScanner.Mask`), WebAssembly text (new `WatStructureScanner`) | C/C++: comment in `EditorLanguage` and `EditorLanguages` |
| Rainbow braces | Kotlin only (`KotlinBraceScanner`, `KotlinBraceTagger`) | Core `BracePalette` (color assignment taken out of the Kotlin scanner, same colors) and `RainbowBraces.Scan(EditorLanguage, text)`; one `RainbowBraceTagger`; setting in `IEditorSettings` | Java (its tokenizer: no `${` templates), Dart, Zig (`ZigSourceScanner.BracePositions`), Rust | WAT (`RainbowBraces.Scan`, `WasmSettings`), C/C++ |
| Scroll bar map setting | Kotlin, Java (`ScrollBarMapOptions` read `KotlinSettings`) | Core `IEditorSettings`; `ScrollBarMap.Resolve(IEditorSettings)`; VSIX `EditorLanguages` (content type → language → settings) | Dart, Zig, Rust, WAT: settings, Tools > Options rows, `vistudio.<language>.scrollBar*` keys | – |
| Inlay hint display | Kotlin, Java (`InlayHintsViewOptions`) | same interface (`InlayHintsDisplay`; Kotlin keeps its stored `InlayHints` name) | Dart, Zig, Rust, WAT (`vistudio.<language>.inlayHintsDisplay`) | – |
| CodeLens Run \| Debug | Kotlin, Java, Dart, Rust (`RustLaunchCodeLens`) | Base `LaunchCodeLens` (project lookup by language, startup project + Debug.Start / StartWithoutDebugging, fallback run) taken out of the Rust lens | Zig: `ZigLaunchCodeLens` above the top-level `main` (fallback `ZigBuildService.RunFileAsync`, the Run File command) | WAT ⊘ [9]; C/C++ open (T63) |
| Build errors in Error List | Kotlin (Gradle), Zig and Dart (their own Tools commands only) | Core `ToolDiagnosticParser` (MSVC, GCC/Clang, javac, rustc/cargo, zig, Dart compiler, `dart analyze`, wasm-tools); `LanguagesPackage.ReportBuildDiagnostics`; `DocumentNavigation` taken out of `ZigNavigation` | Solution Build of every language project: Rust, C, C++, Java, WebAssembly, and Zig/Dart project builds | – |

Settings added to each language's Tools > Options page (category Editor) and `.vs\settings.json` keys: Dart, Zig and Rust get Rainbow braces, Scroll bar mode, Scroll bar map width, Scroll bar map preview and Show inlay hints; WebAssembly gets the same without Rainbow braces. Defaults are Kotlin's (braces on, scroll bar inherited, inlay hints always shown). Fonts and Colors items "Kotlin brace 1–6" are now displayed as "Rainbow brace 1–6"; their internal names did not change, so colors already set are kept.

Not changed: the Kotlin brace lexer (template braces), every existing Kotlin/Java structure result (the 41 structure assertions are unchanged and pass), the Kotlin Gradle diagnostics, Dart/Zig Tools command diagnostics.

## Open after this change

Real work that remains, not stubs: Restart Language Server for Java, Rust and WebAssembly (needs a menu decision: no Tools submenu exists for Rust or WebAssembly), Java errors in `.kotlinproj` builds, and C/C++ CodeLens (T63).

## Checks performed (2026-11-21, this machine)

Unit checks only; nothing below was run in Visual Studio.

| Check | Result |
| --- | --- |
| `build.ps1` (Release: VSIX build, default unit run, `verify-vsix.ps1`, icon rendering) | PASS: 1928 assertions in the default run (1602 before this change), packaged file associations, icon rendering |
| `--structure` (structure, `EditorLanguageTests`, `FeatureMatrixTests`) | PASS, 352 assertions; the 41 earlier structure assertions unchanged |
| `--feature-matrix` | PASS, 225 assertions; prints the table above |
| `--packages none` (languages, projects, packages, `ToolDiagnosticTests`) | PASS, 530 |
| `--rust-wasm` | PASS, 125 (first run failed once: the settings-file test expected exactly four Rust properties; it now expects the four plus the five editor ones). Live rust-analyzer / wat_server parts skipped: not installed |
| `--zig` | PASS, 125; live part skipped (no zig.exe) |
| `--dart none`, `--codelens`, `--jvm-languages`, `--bazel`, `--mise`, `--diagnostics none`, `--hot-reload none` | PASS: 96, 121, 177, 120, 74 (live mise skipped), 46, 55 |
Not verified: the editor parts in Visual Studio (Sticky Scroll headers, brace colors, map mode and inlay hint display in Dart, Zig, Rust and WAT editors, the Zig Run/Debug lens, Error List entries from a real cargo/CMake/javac/wasm-tools build). zig.exe, cargo, rust-analyzer, wat_server and wasm-tools are not installed here, so the parser was checked against recorded output lines, not live builds.
