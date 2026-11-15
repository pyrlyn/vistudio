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

## Visual Studio acceptance of every native script (2026-11-15)

Same machine (Visual Studio Community 18.12.12405.243, root suffix `VistudioDart`; JDK 21.0.12 Adoptium for Gradle, since the scripts' default Microsoft JDK 11 is not installed). Each script installed the VSIX it names (Debug build for the language and Dart scripts, Release `artifacts/Vistudio.Kotlin.vsix` for the Kotlin scripts) and drove the IDE through DTE and UI Automation. No kotlin-lsp is installed, so the Kotlin F5/Ctrl+F5 parts were skipped with the scripts' own switches.

| Script | Result |
| --- | --- |
| `verify-language-projects.ps1` (all parts) | PASS, 91 checks with the final Debug VSIX: the Dart and C++ checks of the first acceptance plus the `c`, `java` and `missing-tools` parts below; ActivityLog without Vistudio errors. |
| `verify-dart.ps1` (console and Flutter) | PASS, 51 checks with install, then 50 with `-SkipInstall` and the stricter analyze checks (final Debug VSIX): analysis server and its Error List entries, Run, Run Tests, failing test in the Error List, Analyze (1 error, then 0 after the fix, and the entry leaves the Error List), Dart debugging and test debugging, Flutter run with hot reload/restart, Flutter debugging, widget and integration tests; ActivityLogs without Vistudio errors. DTE reports the opened `.dart` document's language as "Plain Text" (not checked further). |
| `verify-solution-project.ps1 -SkipDebug` | PASS, 66 checks: in-IDE Gradle import, tree with dependencies (the recorded tree also shows one `Packages (Gradle)` node per build file, `root`, `app` and `shared`, with `kotlin-stdlib` under `app`; Check for Updates was not run on a Kotlin project), Show hidden files through Tools > Options, open/edit/save/undo, Build, Clean, Rebuild, compile error in the Error List, file add/delete, up-to-date build, Properties window, Open Folder > Create or Open .kotlinproj. Skipped: F5/Ctrl+F5. |
| `verify-templates.ps1 -SkipMultiplatform -SkipDebug` | PASS, 25 checks: Kotlin Console, Class Library, Kotlin+Java and Compose Desktop templates create, import and build. Skipped: the Multiplatform template (its target dialog needs a person) and F5. |
| `verify-kotlin-targets-native.ps1 -SkipJvmRun -EvidenceRoot $env:TEMP` | PASS, 43 checks: in-IDE import of jvm/js/wasmJs/mingwX64, target nodes, Run js, Build and Run mingwX64, Run wasmJs, Test jvm, solution Build, F5 on the js startup target shows the "not supported" message, Ctrl+F5 runs `:jsNodeDevelopmentRun`. Skipped: JVM Ctrl+F5. |

New parts of `verify-language-projects.ps1` (`-Part` now takes several values: `dart`, `cpp`, `c`, `java`, `missing-tools`):

- `c`: C Console App template, `Packages (Conan)`, folder tree, solution Build (Conan, CMake configure, build) and F5 stopping at `src/main.c:6` in `main`.
- `java`: Java Console App template, `Packages (Gradle)`, folder tree with the wrapper; adding `gson 2.8.0` to `build.gradle.kts` shows it, Check for Updates reports `2.8.0 → 2.15.0`, solution Build runs the wrapper and writes `build/libs/JavaApp.jar`.
- `missing-tools`: Rust, Zig and WebAssembly templates create and load (Rust shows `Packages (Cargo)`); the DTE unique name is the solution's relative name and the DTE Kind is the language project type; `SolutionBuild.BuildProject` fails with "'cargo' / 'zig' / 'wasm-tools' was not found … Install it or set Tool folders"; `Solution.Remove` removes the project.

Fixed during this acceptance:

- DTE automation object of `.kotlinproj` and language projects (`KotlinProjectAutomation`): `UniqueName` returned the absolute project path, so every DTE call that finds a project by unique name failed with E_FAIL (`Solution.Remove`, `SolutionBuild.BuildProject`). It now returns `IVsSolution.GetUniqueNameOfProject`. `Kind` returned the Kotlin project type for language projects too; it now returns the node's own project type. `UniqueName`, `DTE` and `Collection` switch to the UI thread, because out-of-process automation calls arrive on an RPC thread.
- The 11 Kotlin commands (`Kotlin.Target.Run`, `Kotlin.Project.ImportGradleModel`, `Kotlin.ComposePreview`, ...) were declared with `<CommandName>`, which gives them no DTE name: `DTE.Commands.Item("Kotlin.Target.Run")` failed and `ExecuteCommand` returned E_FAIL, and keyboard bindings could not find them by name. They now use `CanonicalName`/`LocCanonicalName` with a leading dot, like the language project commands.
- Acceptance harness: UI Automation calls into a modal dialog from the scripts' STA thread did not return (`ValuePattern.SetValue` on the Tools > Options grid until the dialog closed; the same call from an MTA thread took 18 ms), so the Options and message box steps now run on an MTA thread and report their checks back to the script thread. The message box helper no longer matches text inside the main window, reads the main window handle before F5 (Visual Studio rejects automation calls while a message box is open) and watches before F5 starts. The Dart "Analyze again after the fix" check matched an older summary in the pane; both analyze checks now read only output written after their own run, and the error must leave the Error List. The language script closes the solution without saving before `DTE.Quit`.
- The language script re-reads the Packages node right before Check for Updates: the background version check that starts on project open can rename it ("Packages (pub) – N outdated") at any time, and DTE then drops the earlier item ("Value does not fall within the expected range" in one run).
- Test hive: after two force-ended instances and repeated same-version reinstalls, `VistudioDart` started with "PkgDef cache creation failed" and an incomplete MEF composition (`SetSite failed for package [MisePackage]`, no New Project templates, no Dart analysis server). Deleting the hive's `ComponentModelCache`, `MEFCacheBackup` and template caches and reinstalling restored it. Not a Vistudio defect.
- `verify-kotlin-targets-native.ps1 -EvidenceRoot`: Kotlin/JS `kotlinNpmInstall` failed with `EISDIR … symlink` when the copy was under `L:\` (a Parallels host folder without symlinks); the project copy can now live on a local NTFS folder. Not a Vistudio defect.

## Not verified

- Visual rendering (icons in Solution Explorer and tabs, outdated icon, Add Package dialog, context menus as drawn); screen access was not used.
- Add Package and Remove through the IDE (both open a modal window); they are covered by the Core actions and the live pub/Conan/Gradle runs.
- Rust, Zig and WebAssembly builds, runs, F5 and editor features (no cargo, rust-analyzer, zig, ZLS, wasm-tools, wasmtime, wkg or wat_server on the machine); `.rs` and WAT coloring. Their projects were only created, loaded and built without the tool.
- Zig and WebAssembly templates write no package manifest, so those projects show no Packages node until a `build.zig.zon`, `wkg.toml` or `world.wit` exists.
- Java F5 and every Kotlin F5/Ctrl+F5 on the JVM (no kotlin-lsp on the machine); the Kotlin Multiplatform template (interactive target dialog).
- x64 Visual Studio.
