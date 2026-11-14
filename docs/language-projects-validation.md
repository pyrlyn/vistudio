# Language projects and packages: validation (T58-T62)

What was run, on which machine, and what was seen. Machine: Windows 11 on ARM, Visual Studio Community 18.12 (ARM64), Flutter 3.50.0 / Dart 3.14.0, Conan 2.33.0, CMake 3.31.12 on PATH plus the CMake 4.3.1 bundled with Visual Studio, JDK 21.0.12 (Adoptium), Gradle 8.14.7 (wrapper). Not installed: Rust (cargo, rust-analyzer), Zig, ZLS, wkg, wasmtime, wasm-tools, wat_server.

## Unit and live checks

| Command | Result |
| --- | --- |
| `Vistudio.Tests.exe --packages none` | PASS, 500 assertions: language catalog against `Icons/Languages.imagemanifest` and `Registration/LanguageFiles.pkgdef`; project files (load, validation, DTD rejection, schema version, atomic `Set`), command parsing without a shell, batch tools through `cmd.exe`, search folders passed to child tools; templates for all seven languages; node identity across version changes; each manager's manifest reader, report parser and actions (pub, Cargo, Gradle, Conan, Zig, wkg). |
| `Vistudio.Tests.exe --packages` | PASS, 522 assertions with the live parts that could run here: pub (`path` 1.8.0 → 1.9.1 flagged, `pub add` incl. `dev:`, `pub upgrade`, `--major-versions`, `pub remove`), Conan (`fmt` 9.1.0 → 12.2.0, `zlib` range 1.2.13 → 1.4, edit and re-check with a private `CONAN_HOME`), Gradle (gson 2.8.0 → 2.15.0 through `Build/OutdatedDependencies.gradle`, 30.7 s; the build-file edit re-read), Zig's `git ls-remote` part (ziglang/zig newest tag 0.15.2; `zig fetch` not run). Cargo and wkg live parts skipped (not installed). |
| `Vistudio.Tests.exe --rust-wasm` | PASS, 125 assertions (settings, server discovery, initialization options, WAT grammar, document-symbol lens mapping); live parts skipped (no rust-analyzer, no wat_server). |
| `Vistudio.Tests.exe` (default run) | PASS, 1195 assertions, three runs after the changes. One earlier run by the Rust/Wasm session ended in an unhandled exception in a .NET timer callback that did not recur in six further runs; not explained. |
| `Vistudio.Tests.exe --dart lsp` | Adds a live check that the analysis server advertises inlay hints statically when the client declares no dynamic registration (as `DartLspCompatibility` now does) and returns type hints for `bin/dart_console.dart`. See the run below. |
| `tests/verify-vsix.ps1`, `tests/verify-icon-rendering.ps1` | PASS: both image manifests map to registered monikers; all 12 vector icons render non-blank at 100 % and 200 %. |

## Visual Studio (tests/verify-language-projects.ps1, DTE only)

Root suffix `VistudioDart` (a fresh suffix stopped at Visual Studio's first-run window, which automation cannot pass and screen access was declined). Verified:

- File > New > Project templates `DartConsoleApp` and `CppConsoleApp` are installed (`GetProjectTemplate(..., "VistudioLanguages")`), `AddFromTemplate` creates `DartApp.dartproj` / `CppApp.cppproj` with their sources, and the projects load in the solution.
- Solution Explorer: the Dart project shows `Packages (pub)` first, then `bin`, `lib`, `test`, the project file and `pubspec.yaml`; `test ^1.25.0` is listed from the manifest. Appending `path: 1.8.0` to `pubspec.yaml` (plus `dart pub get`) shows `path 1.8.0` without any command (file watcher).
- The background version check runs on project open (`dart pub outdated --json --show-all` in the Packages pane). **Check for Updates** on the Packages node: `path 1.8.0 → 1.9.1`, node caption `Packages (pub) – 2 outdated`. **Update to Latest** on the package: `pubspec.yaml` no longer pins 1.8.0 and the node reads `path 1.9.1`.
- Command availability follows the selected node: on the project node only Run / Run Tests; on the Packages node Add Package, Check for Updates, Update All (Update All to Latest only when something is outdated); **Run Tests** executed on the project node.
- Solution Build of the Dart project runs `dart analyze` and succeeds.
- C++: `Packages (Conan)`; adding `fmt/10.2.1` to `conanfile.txt` shows it; **Check for Updates** reports `fmt 10.2.1 → 12.2.0` from the real Conan (private `CONAN_HOME` with `conan profile detect`).
- C++: **Set as Startup Project**, then Solution Build runs the template's command chain: `conan install` (builds `fmt/10.2.1` from source with the CMake bundled with Visual Studio), `cmake -S . -B build` with the Conan toolchain, `cmake --build build --config Debug`; it succeeds. **F5** with a breakpoint at `src/main.cpp:6` stops in `main` (language C++) in the native engine.
- 17 checks in the final C++ run, plus the Dart run above (same script, `-Part dart`). The ActivityLog of the Dart runs has no Vistudio errors. The C++ run has two, both "Activating the Vistudio.JvmCodeLensTaggerProvider service failed ... after the IDE has begun shutting down": the existing JVM CodeLens part was activated while the script ended a Visual Studio that had not exited within 60 s of `DTE.Quit`. Neither the slow exit nor that activation is explained yet.

Fixed during this acceptance:
- Virtual nodes had canonical names that contained the manifest's drive path once node identity moved off the caption; the shell then failed to resolve them (`GetItem` by path: "Value does not fall within the expected range"). The virtual part of a canonical name now has no drive colon or backslash.
- Node identity no longer depends on the caption: an outdated count or newly arrived versions recreated the Packages and package nodes, which loses their expansion and selection.
- Commands had menu-path names; `CanonicalName`/`LocCanonicalName` with a leading dot give `Vistudio.CheckPackageUpdates` and the others.
- The project hierarchy answers `OLECMDERR_E_UNKNOWNGROUP` for command groups it does not own.
- CMake 3.31 on PATH cannot generate for Visual Studio 18 (Conan's package builds failed with "Could not create named generator Visual Studio 18 2026"); C and C++ commands now look in the running Visual Studio's bundled CMake first and pass their search folders to the tools they start.

Note for automation: `UIHierarchyItem.Select` left the previous selection when the target was a virtual node; selecting by path (`UIHierarchy.GetItem("Solution\Project\Packages (pub)")`) works, and so does a click in the UI.

## Not verified

- Visual rendering (icons in Solution Explorer and tabs, outdated icon, Add Package dialog, context menus as drawn); screen access was declined.
- Add Package and Remove through the IDE (both open a modal window); they are covered by the Core actions and the live pub/Conan/Gradle runs.
- Rust, Zig, WebAssembly and Java projects in the IDE; Kotlin projects' Gradle Packages node in the IDE.
- Rust and WebAssembly editor features (no servers installed); `.rs` coloring with the new `rust` content type; WAT coloring.
- x64 Visual Studio, Release build.
