# Completed work

## T42. Inlay hints for every kind in Kotlin and Java

Requested by the creator ("inline hints for all types"). IntelliJ-style inlay hints in `.kt`/`.kts` and `.java` editors for every kind the language servers provide: Kotlin property, local variable, function return and parameter types, lambda return expressions, implicit `it`/`this`, ranges, `kotlin.time` durations, parameter names (including compiled, context and excluded ones) and call-chain types; Java parameter names, `var` and lambda parameter types and format parameters. All kinds are on by default and each can be turned off in Tools > Options > Kotlin. Done when both servers return every kind on the sample (repeatable probe), unit checks pass, and the hints are visible in a real Visual Studio instance without holding Alt+F1.

Probe findings (`tests/integration/verify-inlay-hints.py`, `samples/kotlin-java-jvm`, unsaved edits only):

- Visual Studio's LSP client already renders `textDocument/inlayHint` (with resolve and `workspace/inlayHint/refresh`), gated by the editor option `DefaultTextViewOptions.EnableInlayHintsOptionId` (`InlayHintsEnableKind`), whose default is `WhilePressingHotKey`: hints only appear while Alt+F1 is held.
- Kotlin LSP 271 answers inlay hint requests only after asking `workspace/configuration` for section `jetbrains.kotlin`; a null answer yields no hints. The answer is a flat object of enabled option ids. Build 271 uses `hints.type.property`, `hints.type.variable`, `hints.type.function.return`, `hints.type.function.parameter`, `hints.lambda.return`, `hints.lambda.receivers.parameters`, `hints.value.ranges`, `hints.value.kotlin.time`, `hints.parameters`, `hints.parameters.context`, `hints.parameters.compiled`, `hints.parameters.excluded`, `hints.call.chains` (the VS Code extension 0.0.12 still sends older `hints.settings.*` names that 271 ignores). With all on, the sample returns all kinds (57 hints). Implicit `it`/`this` hints come wrapped in whitespace-only hints, and call-chain/range hints have no kind.
- Visual Studio answers `workspace/configuration` only from Open Folder `VSWorkspaceSettings`, and the LSP middle layer sees only client-to-server traffic, so Vistudio must answer `jetbrains.kotlin` itself on the server streams.
- JDT LS (redhat.java 1.56) returns parameter names, `var` types and lambda parameter types from `java.inlayHints` in `initializationOptions` alone; it omits the type when the initializer spells it, and its hints have no kind.

Execution plan:

1. Core (`KotlinSettings.cs`): display mode (always / while holding Alt+F1 / never, default always) and one switch per hint kind, all on.
2. VSIX `Language/InlayHintConfiguration.cs` (JSON only, linked into tests): the `jetbrains.kotlin` answer, the `java.inlayHints` settings, and result normalization (whitespace-only hints become padding, Java kinds inferred: `name:` is a parameter hint, the rest are type hints).
3. VSIX `Language/LspConfigurationStreams.cs`: a server-output reader that answers `workspace/configuration` requests for owned sections and a frame-aware writer (extends `KotlinInitializeStream`) that injects the response between client frames. Kotlin client uses both; middle layers normalize `textDocument/inlayHint` results; Java client adds `inlayHints` to its settings.
4. VSIX `Editor/InlayHintsViewOptions.cs`: sets `EnableInlayHintsOptionId` on Kotlin/Java views from the display mode, live on options save. Options page category "Inlay hints".
5. Tests: unit checks for configuration, normalization and the stream interception; the probe on both real servers; a real Visual Studio check. Update README, architecture and toolchain docs if affected.

Status: implemented and checked in a real Visual Studio instance (root suffix `VistudioInlayTest`, `artifacts/inlay-native/kotlin-java-jvm`, driven through EnvDTE with a temporary trace that was removed afterwards). On `.kt` and `.java` views the option went from `WhilePressingHotKey` to `Always`. Visual Studio's own LSP client then requested hints: `JavaHints.java` got 12, then 14 after an edit (settings survive Visual Studio's configuration messages); `HintCases.kt` got 46 after the Gradle import (50 raw, 4 whitespace hints folded into padding around `it: Int` and `this: Ledger`), covering every Kotlin kind with parameter names as kind 2. The server answered `jetbrains.kotlin` through `LspConfigurationReader` before each request. Visual Studio only requests the visible span, so a small window shows fewer hints. Outside Visual Studio, a harness built from the real stream classes got 34 hints from a real server. Unit checks pass (`InlayHintTests`, `LspCompatibilityTests`); the full regression run still stops in the unrelated `ProjectFileTests` fake-wrapper JDK check. The creator confirmed the hints on screen. Toolchain unchanged.

## T49. Own Kotlin server system path for each Visual Studio instance

Moved from ideas.md (creator request: "a separate system-path for each Visual Studio instance"). Kotlin LSP 271 lets only one server use a `--system-path` (it holds `<path>/.app.lock`), and the global settings give every instance the same path, so a second Visual Studio instance gets no Kotlin server (initialize fails with ConnectionLostException). Done when every running instance starts its own server on its own path, a restarted instance reuses an existing cache instead of starting cold, unit checks pass, and two instances with the same settings both get Kotlin features.

Execution plan:

1. Core `KotlinSystemPath`: read `--system-path=X` / `--system-path X` from the configured arguments (official `intellij-server.exe` / `kotlin-lsp.cmd` without the argument get `%LOCALAPPDATA%\Vistudio\kotlin-lsp`); pick the first free slot `X`, `X-2`, `X-3`, ...: free means Vistudio's `<slot>\.vistudio.lock` can be held exclusively and the server's own `<slot>\.app.lock` is not held (covers servers started without Vistudio's lock). Return the rewritten arguments and a lease that keeps the lock until the server stops. Other servers' arguments stay unchanged.
2. VSIX `KotlinLanguageClient`: take the lease under the process gate after the old server is cleaned up, start the server with the rewritten arguments, release the lease when the server stops; log the chosen path.
3. Tests: argument parsing and rewriting (quoted, `=` and space forms, absent), slot choice with a held Vistudio lock and with a held `.app.lock`, lease release; real check with two Visual Studio instances. Update README (language server settings).

Implemented: `Vistudio.Core/KotlinSystemPath.cs` finds `--system-path` in the configured arguments (quoted, `=` or space form; the official `intellij-server.exe`/`kotlin-lsp.cmd` without it start from `%LOCALAPPDATA%\Vistudio\kotlin-lsp`) and takes the first free slot `X`, `X-2`, ... A slot is free when Vistudio's `<slot>\.vistudio.lock` can be opened exclusively and no server holds `<slot>\.app.lock`; the lease keeps the lock until the server stops. `KotlinLanguageClient` takes the lease under its process gate after the previous server is cleaned up, starts the server with the rewritten arguments, logs the path, and releases the lease in `CleanupAsync`. Other servers keep their arguments.

Validation performed: `KotlinSystemPathTests` (13 assertions: parsing and rewriting, slots 1/2/3, reuse after release, a path held by a foreign `.app.lock` is skipped, a stale unheld `.app.lock` does not block, other servers unchanged, default path for the official launcher). In Visual Studio: while another agent's test IDE (older build, no Vistudio lock) held `kotlin-lsp-cache`, the `VistudioInlayTest` instance started its server on `kotlin-lsp-cache-2`, both servers ran at the same time, and Kotlin features (inlay hints, T42) worked; after restarts the instance reused `kotlin-lsp-cache-2`. README documents the slots. Toolchain unchanged.

## T39. Sticky Scroll for Kotlin and Java

Requested by the creator. Visual Studio's built-in Sticky Scroll (Text Editor > General) pins the headers of the enclosing scopes (class, function, `if`, loop, lambda) at the top of the editor, but it builds them only from `IStructureTag` block structure, which Roslyn provides for C# and the LSP client does not provide for Kotlin or Java (it renders LSP folding ranges as outlining only). Done when `.kt`, `.kts` and `.java` editors show sticky headers for nested blocks (multi-line headers start at the declaration, not at the line with the brace; leading annotations excluded), outlining from the language servers is not duplicated, unit tests cover the block scanner, and the strict build passes.

Implemented: `Vistudio.Core/Structure/CodeStructureScanner.cs` finds every multi-line brace block with the statement header that owns it (multi-line headers start at the declaration, leading annotations are skipped, call chains continue after lambda arguments, a statement after lambda parameters starts its own header) and classifies it (type, member, conditional, loop, statement, expression); it reuses the tokenizer of `CodeDeclarationScanner`, now internal. `Editor/JvmStructureTagger.cs` turns the blocks into non-collapsible `StructureTag`s for the `kotlin` and `java` content types, so folding stays with the language servers. Two Sticky Scroll constraints found in the IDE: a multi-line primary header makes the margin throw "Attempted to introduce overlapping source spans", so the primary header is the first header line only; and a block whose header shares lines with an enclosing header (`run { items.map {`) is dropped for the same reason.

Validation performed: `Vistudio.Tests --structure` passes (41 assertions, Kotlin and Java headers, kinds, comments, raw strings, single-line and broken blocks). The VSIX builds. In a Visual Studio 18 experimental instance (root suffix `VistudioTest`, Sticky Scroll on), captured with `PrintWindow`: a `.kt` file pins `data class Inventory(` … `) : Iterable<Int> {`, `fun summarize(...)`, `for`, `if`; a `.java` file pins `public class Ledger`, `public void run()`, `for`, `if`; structure guide lines show; the ActivityLog has no overlapping-span errors after the fix. Not performed: a fully green `build.ps1`, because `ProjectFileTests` ("Selected JDK reaches the wrapper process") fails independently of this change. Follow-up check: a 79-line Gradle-style `build.gradle.kts` in the same instance pins `kotlin {`, `sourceSets {`, `val commonMain by getting {`, `dependencies {` while scrolled inside `dependencies`; the ActivityLog of that session has no editor or Vistudio errors.

## T20. Fail Kotlin debug launch immediately when the language server exited

Moved from ideas.md with creator approval. When the Kotlin language server process exits unexpectedly (for example an expired build), F5/Ctrl+F5 must stop at once with the reason instead of waiting for the 3-minute initialization timeout. Deliberate stops and restarts are not failures; a restart clears the failure.

Execution plan:

1. `KotlinServerLifecycle` (no Visual Studio dependencies, linked into the test project): owns the initialization/import signals, records an unexpected exit with its exit code and the last stderr lines, fails pending and future waits immediately, ignores expected exits and exits of replaced processes.
2. `KotlinLanguageClient` delegates to it: start, stderr lines, expected stop in cleanup, observed exit, initialization and import notifications; the debug wait throws the recorded reason. The coordinator already reports such errors in a message box and the Kotlin pane.
3. Tests: unit tests for immediate failure (pending and new waits), expected/obsolete exits, restart reset, import failure; full build. Native check: F5 with a server that exits at startup reports the reason within seconds.

Delivered `KotlinServerLifecycle` (linked into the tests) and its use in `KotlinLanguageClient`, including servers that die at startup (exit code and stderr instead of a generic initialization failure). With creator approval the expired Kotlin LSP 263.7084.0 was replaced by the checksum-verified 271.2085.0; T30 owns the 271 launch protocol.

Validation: 495 assertions; 59 of 59 native checks in Visual Studio 2026 18.12, including F5/Ctrl+F5 on server 271 and F5 with the expired server reporting the exit reason 0 s after F5. Evidence: docs/solution-project-validation.md.

## T18. Move Kotlin Project Explorer features into the native Solution Explorer

Replace the custom Kotlin Project Explorer tool window with native Solution Explorer features: project context-menu commands, editable project settings in the Properties window (F4), and tree nodes for the information the tool window showed. Remove the tool window. Creator decisions: delete the tool window; edit settings in the Properties window.

Execution plan:

1. Core: validated `KotlinProjectFile.Set` for Vistudio properties (task lists, Gradle root with wrapper, debug settings, import configurations); tree adds a project-level `Toolchain` node (wrapper, Gradle JDK, imported Gradle/JVM) and per-module `Frameworks`, and shows manually declared metadata.
2. VSIX: the project root browse object becomes editable (Gradle JDK with a dropdown of discovered JDKs plus free input, Build/Test/Run tasks, Gradle root, import configurations, debug main class/source/arguments/working directory; read-only file, GUID, versions and last import). Changes are validated, saved atomically and picked up by the watcher.
3. Project context menu (visible only for Kotlin projects): Import Gradle Model, Run Tests, Run, Gradle Tasks..., Cancel Gradle, Edit Project File, acting on the selected project. Tools menu: replace "Kotlin: Project Explorer" with "Kotlin: Create or Open .kotlinproj", which generates metadata for an open folder, opens it in Solution Explorer and imports it.
4. Remove the tool window, its registration, command and code; update the package checks.
5. Tests and acceptance: unit tests for `Set` and the new tree nodes; native script steps for the context-menu import command, editing properties through the Properties window and the new nodes. Update README, docs/project-file.md and the validation document.

Delivered: the Kotlin Project Explorer tool window, its registration and command are removed. Native replacements: project context-menu commands (Import Gradle Model, Run Tests with Gradle, Run with Gradle, Gradle Tasks..., Cancel Gradle, Edit Project File) visible only for Kotlin projects and acting on the selected project; **Tools > Kotlin: Create or Open .kotlinproj** for an open folder; an editable Properties window view of the project (Gradle JDK with detected installations, Gradle root, task lists, import configurations, debug entry point; read-only file, GUID, JDK version and last import) validated by `KotlinProjectFile.Set`/`SetJdk`; `Toolchain`, per-module `Frameworks` and `Declared Metadata` tree nodes. Kotlin commands save modified documents instead of `File.SaveAll`.

Validation: warning-free VSIX, 371 assertions, package/rendering checks; 52 of 52 native checks in Visual Studio 2026 18.12 including the context-menu import, Properties window edits through UI Automation, new nodes and the Open Folder Create or Open flow. F5/Ctrl+F5 not re-verified: the installed Kotlin LSP build expired on 2026-11-13. Evidence: docs/solution-project-validation.md.

## T19. Hide dot files and folders in the project tree by default

Entries whose name starts with `.` stay hidden in the `.kotlinproj` Solution Explorer tree by default. A global option (Tools > Options > Kotlin > Solution Explorer > Show hidden files) shows them. Changing the option refreshes loaded Kotlin projects without reopening.

Execution plan:

1. Core: `KotlinSettings.ShowHiddenFiles` (default `false`); `KotlinProjectTree.Build`, `ListDirectory`, `IsVisibleEntry` and lazy `HasChildren` take the flag.
2. VSIX: option on `KotlinOptionsPage`; `KotlinProjectNode` builds with the saved flag and refreshes when the option changes.
3. Tests: tree hides dot entries by default and shows them with the flag; legacy settings default to hidden. Build, run tests, update README.

Validation: Core, test and VSIX projects compile (Debug). `ProjectTreeTests` pass (27 assertions, 6 new: default hiding, non-expandable dot-only folder, dot folders/files listed and expandable with the option). The full test executable stopped earlier in `ProjectFileTests` ("Selected JDK reaches the wrapper process"), unrelated to this change, while T18 was editing the same tree. Not verified natively in Visual Studio: no VSIX was installed and `artifacts/` was not touched. Known limit: with the option on, changes inside `.gradle`, `.kotlin`, `.idea`, `.vs` and `.git` still do not trigger an automatic tree refresh.

## T17. F5 debugging for solution-loaded .kotlinproj projects

Start Kotlin/JVM debugging (F5) and running without debugging (Ctrl+F5) from a `.kotlinproj` loaded in a solution, reusing the existing Gradle build, Kotlin LSP launch resolution and DAP debug engine. Done when a breakpoint in a real Gradle/Kotlin project is hit from F5 in Visual Studio, the program continues to completion with its output visible, and the launch target is configurable in the project file.

Execution plan:

1. Core: `KotlinLaunchTarget` resolves the launch from `.kotlinproj` properties (`DebugMainClass`, `DebugSourceFile`, `DebugArguments`, `DebugWorkingDirectory`) or, when unset, from the single top-level `main` in `main`-type source roots (package + JVM facade name, `@file:JvmName`); ambiguity and absence are explicit errors.
2. VSIX: `KotlinProjectConfig` implements `IVsDebuggableProjectCfg`; `KotlinDebugCoordinator` gets a configuration-object entry point shared with Open Folder, opens the source so the language server starts, then builds with Gradle once and launches the DAP engine. Report the project up to date to the run-time check so F5 does not prompt; Gradle stays incremental.
3. Tests: resolver unit tests; rerun all regression and package checks.
4. Native acceptance: extend `tests/verify-solution-project.ps1` with a breakpoint hit through F5, continue to exit with program output, and Ctrl+F5; record evidence in `docs/solution-project-validation.md`.

Delivered `KotlinLaunchTarget` (project properties or automatic single top-level `main` detection), `IVsDebuggableProjectCfg` on the project configurations sharing the Open Folder Kotlin/JVM launch path (LSP launch resolution, DAP engine), automatic opening of the entry source so the language client loads, and a session-scoped fast up-to-date check (`GradleInputs`, synchronous HRESULT contract) so Build skips unchanged projects and F5 does not rebuild right after the solution pre-build.

Validation: warning-free VSIX, 349 assertions, package/rendering checks; 42 of 42 native checks in Visual Studio 2026 18.12 on the real multi-module Kotlin fixture, including an up-to-date Build, an F5 breakpoint hit at `Main.kt:3`, continue to completion with program output, and Ctrl+F5. Evidence: docs/solution-project-validation.md.

## T16. Load .kotlinproj as a Solution Explorer project

Open a `.kotlinproj` as a native project in Solution Explorer with a tree of Gradle modules, source sets/source roots with their files, and resolved dependencies per configuration. Solution Build/Rebuild/Clean must run Gradle through the existing pipeline; no MSBuild evaluation of the project file. Verify on a real multi-module Kotlin/Gradle project inside Visual Studio.

Execution plan:

1. Core: export `SourceRoot` items (Kotlin and Java source sets, sources/resources) from `Build/ImportModel.gradle`; accept them in `GradleProjectModel` and `ApplyImport`; add `KotlinProjectFile.Find` (default or single `*.kotlinproj`) and group-scoped item queries; add a pure lazy `KotlinProjectTree` model (Gradle scripts, modules, source sets, source roots and files with conventional fallback, dependencies by resolved configuration).
2. VSIX: hand-written VSSDK project system with no new packages: `KotlinProjectFactory` (`IVsProjectFactory`, registered for `.kotlinproj`), `KotlinProjectNode` (`IVsUIHierarchy`, `IVsProject`, `IVsPersistHierarchyItem`, configuration provider), `KotlinProjectConfig` (`IVsProjectCfg`, `IVsBuildableProjectCfg`). Document opening through the project, context menus, image monikers, file-system/metadata watchers with reconciling refresh.
3. Integration: a loaded project becomes the IDE root for LSP, Kotlin commands and the explorer when no folder workspace is open; solution builds call the package Gradle gate with the project's own root/settings and mirror output into the Build pane.
4. Tests: tree/source-root/model validation unit tests; extend the real Gradle import integration with source roots.
5. Native acceptance: build/install the VSIX in an isolated profile, open the real `tests/fixtures/gradle-import` copy through a `.sln`, import in the IDE, dump the actual Solution Explorer tree, open/save documents, Build/Rebuild/Clean and a failing build with Error List, file add refresh. Record evidence in `docs/solution-project-validation.md`.

Delivered a hand-written VSSDK project system with no new dependencies: `.kotlinproj` project factory, read-only hierarchy (modules, source sets with on-disk files, resolved dependencies per configuration, Gradle scripts), documents opened and saved through the project, image monikers, a Properties view with Gradle provenance, watchers that reconcile the tree in place, and `Debug`/`Release` configurations whose Build/Rebuild/Clean run Gradle through the existing gate with Build pane output and Error List diagnostics. Gradle import now exports `SourceRoot` items. A loaded project is the IDE root for LSP, Kotlin commands and the explorer when no folder is open. F5 debugging from a solution project is not implemented.

Validation: warning-free VSIX, 329 assertions, package/rendering checks; 16 real Gradle import checks; 36 of 36 native checks in Visual Studio 2026 18.12 (isolated profile `VistudioProjectTest`) on the real multi-module Kotlin fixture: tree after in-IDE import, open/save, Build/Clean/Rebuild, failing build with Error List entry, file add/delete refresh. Two defects found in the IDE were fixed (reload prompt after import, Build pane threading). Evidence: docs/solution-project-validation.md; package: artifacts/Vistudio.Kotlin.SolutionProject.vsix.

## T15. Import the resolved Gradle project model

Import Gradle modules, configuration-scoped resolved libraries and recognized frameworks into .kotlinproj and the native Kotlin Project Explorer. Preserve user settings and the previous snapshot on failed/canceled imports. Show resolution provenance and errors rather than requested versions as resolved ones.

Execution plan: reuse the existing wrapper/process pipeline and framework XML support, add a bundled Gradle init script and bounded model parser, atomic generated-group replacement and cancellable explorer import. Verify real multi-module Kotlin dependency conflict resolution, repeated import/removal, and failure preservation; build/install the VSIX in an isolated IDE profile and verify the actual explorer tree and generated XML in Visual Studio. No new runtime package dependencies or pinned version changes.

Delivered a public-API Gradle model exporter, validated XML envelope/import service, generated-group replacement, direct/transitive/file/project dependency metadata, recognized framework/applied-plugin versions, configuration scope/status and optional extra configuration selection. Explorer imports automatically after generation and exposes Import/Cancel commands, module/configuration grouping, selected versions, requested selectors and selection reasons. Builds/imports share the existing operation gate and workspace cancellation.

Validation: warning-free VSIX build, 303 assertions, package and rendering checks; 12 real multi-module Gradle/Kotlin integration checks including requested 2.4.20 versus selected 2.4.21, stale removal, failed import preservation and actual compile/run. Installed the matching final package into VistudioImportTest. In native Visual Studio, imported the real fixture, inspected 3 modules, resolved library and framework versions/provenance, induced a missing-dependency failure with byte-identical snapshot preservation, then restored and successfully reimported. Evidence and explicit platform/framework/composite-build limits: docs/gradle-import-validation.md. Package: artifacts/Vistudio.Kotlin.GradleImport.vsix. T12 remains separate and unchanged.

## T14. Project metadata and Kotlin Project Explorer skeleton

Create a versioned .kotlinproj XML document using Project/PropertyGroup/ItemGroup conventions, a classic VSSDK tool window, installed JDK discovery and project-specific JDK selection. Keep Kotlin builds on Gradle and preserve existing debugging work.

Execution plan: reuse framework XML/WPF and existing VSSDK packages; implement a preserving XML store and inventory model in Core, register an explorer command/tool window, apply project Gradle settings to existing commands, document the schema and initial dependency inventory limits. Verify round trips, JDK validation, settings precedence, and the complete VSIX build/package checks. Native IDE acceptance is separate from automated validation.

Delivered preserving/atomic metadata storage, schema-version validation, project build settings, JDK metadata discovery and path/version selection, wrapper version snapshots, and a registered WPF explorer with module/tool/library/framework sections. The project JDK reaches Gradle commands and fallback debug launch environment; language-server settings remain separate. No new dependencies or version changes. Documentation: docs/project-file.md; package: artifacts/Vistudio.Kotlin.ProjectExplorer.vsix.

Validation: warning-free VSIX build; 283 regression assertions; package registrations/assets and icon rendering checks. Generated metadata against the real Kotlin/JVM sample, selected installed Microsoft JDK 11.0.31, and ran Gradle 8.14.7 classes successfully with exit 0 (outputs up to date). Evidence is under artifacts/project-metadata-77708efb217347a4bdabc1db3d45780e. This completes the requested skeleton. Automatic dependency/framework resolution and native Solution Explorer project loading are not implemented. The new tool window has not been tested interactively or installed in an IDE profile in this task; T12's separate pending UI acceptance is preserved.

## T11. Preserve lexical Kotlin debugger completion context

Probe source-context LSP completion for local/private extensions and generic types. Replace nominal runtime type projection with a bounded isolated source snapshot at the selected frame position, preserving lexical visibility without evaluating user code or modifying source. Add scope/generic regression fixtures and tests, build/install the VSIX, and verify both native debugger windows, frame switching and zero side effects on a real Gradle/Kotlin project. Document tested boundaries.

Implemented lexical in-memory source snapshots at the selected frame line, stable per-file virtual identities and serialized document lifetimes. Preserved local/file-private/member-private function declarations and generic source types instead of projecting erased JVM types. Added operator signatures, private member origin filtering, bounded incomplete-list requery, explicit no-auto-evaluation local enumeration, optional diagnostic tracing, regression tests and sample fixtures. No new packages or version changes; Kotlin builds remain Gradle/JVM.

Release build passes 173 assertions without warnings. Installed/restarted VistudioTest and verified real Kotlin server 263.7084.0 with the Gradle sample: both native windows showed local/private extensions and String?/Int? substitutions for selected inner/caller frames; the final binary also showed private function-generic member extensions returning List<String> in both windows. DAP evaluate counts stayed unchanged around suggestion lists (3 -> 3 and 13 -> 13), counter stayed 0, and JVM exited with 0. Preserved saved Watches, disabled logging and closed the test IDE. Evidence and reproduction steps are in docs/validation.md and the sample README.

Known backend boundary: member extensions whose receiver uses the enclosing generic class's type parameter T or List<T> can be omitted in the copied context. This was reproduced and is explicitly documented as unreliable; function-level inferred generic parameters and the tested concrete/nested generic cases work. Source mappings inside multiline/generated/inline/coroutine code and other host/server versions remain unvalidated.

## T10. Complete Kotlin methods and extensions in debugger expressions

Inspect the pinned Kotlin server's semantic completion contract and connect it to native Watch/Immediate using the selected suspended frame. Keep suggestion requests separate from expression execution, cancel stale results on context changes, and preserve insertion behavior. Add regression coverage and a real Kotlin method/extension fixture with a side-effect counter, build/install the VSIX, and verify both windows plus frame switching in Visual Studio without executing user code when showing suggestions. Record supported scope and evidence.

Implemented type-only in-memory LSP documents using the selected frame's source imports and explicit receiver type. Added asynchronous request cancellation/deadlines, scratch diagnostics filtering, callable/extension-origin filtering, overload descriptions and name-only insertion. Native Watch custom commit preserves signatures in the popup without inserting them into expressions. No new dependencies or version updates; Kotlin builds remain on Gradle/JVM.

The final VSIX builds without warnings and 162 C# assertions pass. Reinstalled and verified in a restarted VS 2026 18.12 VistudioTest instance on the real Kotlin project. Both Watch and Immediate offered the expected methods and imported extensions, switched correctly between same-named receivers of different frame types, and inserted names only. Immediate also showed overloads and String stdlib extensions. DAP evaluation counts stayed unchanged around showing/accepting lists. The counter stayed 0 and the JVM exited with 0; Error List was empty and no scratch file was written. Preserved original saved Watches, disabled logging and closed the owned IDE. Documented evidence and the limits of projecting runtime types rather than reproducing full evaluator lexical scope.

## T9. Complete Kotlin expressions in debugger windows

Inspect native Watch/Immediate editor extension points and real Kotlin backend completion support. Implement completion using the selected suspended stack frame, with safe lifecycle/cancellation and no expression evaluation for suggestions. Add relevant regression coverage, build/install the VSIX and verify suggestions and insertion in both native windows on the real Gradle/JVM project, including switching frames. Document the actual supported scope.

Implemented SDK completion for frame locals, arguments and debugger-exposed field chains. Native Watch uses IVsCompletionSet and a temporary code content type; Immediate uses the MEF completion broker and a scoped custom commit that restores debugger-buffer flags. Context changes dismiss suggestions; closed views detach handlers. Added bounded Kotlin chain parsing, identifier escaping, tests and a two-frame sample fixture. No dependency versions or NuGet packages changed.

Built the Release VSIX without warnings; 152 C# assertions passed. Reinstalled the final artifact, restarted Visual Studio 2026 18.12 in VistudioTest and verified both native windows on the real Gradle/Kotlin project. Watch completed greeting.name to "Kotlin"; Immediate inserted and evaluated the same expression. Both lists changed from counter/frameOnly/value in the nested function to args/counter/greeting/nullableName after selecting main in Call Stack. Final JVM output confirmed counter 0 and exit code 0. The installed assembly hash matches the build. Removed the added test Watch, disabled logging, closed the owned IDE and documented evidence and limits: semantic method/extension/import completion is not implemented.

## T8. Fix cold automatic Watch evaluation

Trace the pinned Kotlin backend's hover timeout and preserve single execution of expressions. Implement a bounded compatibility fix without promoting restricted evaluations or retrying user code. Add regression coverage, rebuild/install the VSIX, restart Visual Studio and verify saved Watch expressions plus a side-effect counter on the real Gradle/JVM sample. Record the actual results and remaining limits.

Added native-server startup compatibility with a child-only 4000 ms hover deadline, preserving existing JVM options, explicit overrides and the original restricted DAP requests. No expression retries or context promotion were added. Added delayed-hover regression coverage and a slow side-effect counter in the Kotlin sample. No dependency or version changes.

Built and installed the VSIX with 137 passing C# assertions. Fully restarted Visual Studio and Kotlin server, then verified the first automatic refresh of saved Watch expressions without manual refresh. The real host log showed one request per expression and successful initial results after 1675–1735 ms. Both Immediate and final JVM output confirmed exactly one counter increment; normal exit code was 0. Removed the side-effecting test Watch entry, disabled diagnostic logging and closed the test IDE. Updated instructions, architecture and validation evidence, including the bounded timeout and native-launcher scope.

## T7. Support Kotlin expression evaluation in Watch and Immediate

Inspect the SDK evaluation contract and real Kotlin backend responses. Connect native Watch and Immediate evaluation through the existing DAP session, preserving frame context, errors and single execution of expressions. Add meaningful protocol coverage, verify values and Kotlin expressions in both windows against the real Gradle sample, rebuild the VSIX and document supported behavior and limitations.

Implemented KotlinEvaluation in the existing SDK Debug Adapter Host transport. Unrestricted interactive requests, including the context omitted by native Immediate, use the pinned backend final-presentation path and a unique trailing comment to avoid stale cached results. Frame IDs, options, errors and object handles remain intact; evaluations are not retried. Added protocol regression coverage and EvaluationMain.kt with a counter plus a second launch configuration. No dependencies or versions changed.

Built and installed the final VSIX in Visual Studio 2026 18.12 VistudioTest; all 129 C# assertions passed. Verified real Kotlin/Gradle Watch values, null handling, arithmetic, object creation and expansion; Immediate stdlib calls, repeated counter calls returning 1 then 2, and recovery after an unresolved expression. F10 refreshed Watch in the new frame; F5 completed with Evaluation calls: 2 and exit code 0. Documented the backend cold automatic Watch hover timeout and unvalidated advanced evaluator behavior. Saved local evidence, updated setup/architecture/validation documentation, and closed the owned test IDE.

## T6. Fix debugger object previews and exit status

Investigate the real backend DAP sequence for pending object values and termination. Adapt the existing debug transport using the current SDK and JSON dependencies, add protocol regression coverage, and verify resolved object values and correct normal/nonzero exit status on the real Kotlin sample in Visual Studio. Rebuild the installable VSIX and update documented limitations.

Added a per-connection DAP session and bounded framed input adapter. Temporary object presentations are resolved with variables-only retries, preserving request correlation and expansion handles without repeating expressions or caching across suspension generations. Early termination waits for the backend's exit status while forwarding remaining output, with bounded timeout and immediate Stop/EOF handling. No dependencies or pinned versions changed.

All 95 C# assertions pass and the Release VSIX builds without warnings. Installed and tested the final artifact in Visual Studio 2026 18.12's VistudioTest profile with the real Kotlin 263.7084.0 backend and Gradle sample. Locals displayed Greeting(name=Kotlin), its expanded name field, and Greeting(name=Updated) after stepping across reassignment. Native Debug Output showed exit code 1 (0x1) for an intentional failure and 0 (0x0) after restoring the original sample. Main.kt was restored byte-for-byte. Documented retry/deadline behavior and the distinction between Gradle runner and child JVM exit status; retained local acceptance evidence and closed the test IDE.

## T5. Implement Kotlin/JVM debugging in Visual Studio

Integrate native F5 startup with Gradle build and a real Kotlin-aware JVM debug adapter through Visual Studio Debug Adapter Host. Reuse the installed official Kotlin server where its DAP support fits. Implement launch configuration, owned-session cleanup and SDK startup integration, then verify line breakpoints, stepping and variables on a real Kotlin/Gradle project. Keep Kotlin compilation outside MSBuild and document supported configurations and untested advanced features.

Added separate Open Folder launch configuration and debug target providers, a Gradle build/import/launch coordinator, COM adapter launcher, loopback DAP transport, file-URI normalization, and debug lifecycle ownership. Registered the engine through pkgdef and retained native debugger commands and windows. Added the compile-only Debug Adapter Host interfaces package; runtime SDK assemblies and the external server are not bundled.

Verified inside Visual Studio 2026 18.12's isolated VistudioTest profile with the official Kotlin server 263.7084.0 and the real Kotlin 2.4.21 / Gradle 8.14.7 sample. F5 reached the Main.kt breakpoint; F11 entered Greeting.message; Shift+F11 returned; F10 stepped over; Locals exposed name = "Kotlin" and Call Stack showed both source frames. Continue printed the greeting. Stop Debugging terminated the target JVM and owned Gradle Wrapper. A temporary compiler error blocked F5 startup and navigated through Error List; restoring the source allowed another successful breakpoint stop. Manual server restart worked after ending the debug session. All 69 C# assertions passed, and the installable VSIX was rebuilt. Documented backend object-summary and exit-code presentation limitations, supported launch configuration, and advanced features not yet validated.

## T4. Validate and complete Kotlin LSP navigation

Enable F12 definition navigation and Shift+F12 references through the Visual Studio SDK LSP integration and verify against a real Kotlin language server.

Retained native SDK command routing, reference results and cancellation. Added KotlinInitializeStream and KotlinLspCompatibility to remove VS-private enum values that prevented the official Kotlin server from initializing. The adapter changes only initialize; subsequent frames pass through unchanged. Added framing/compatibility regression tests, a transparent acceptance recorder, a real-response verifier, and cross-file sample symbols including an unrelated same-name method.

Verified the official Kotlin language server 263.7084.0 Windows x64 archive against its published SHA-256, installed it locally outside the VSIX, and configured the native launcher. With the real imported Gradle project, F12 opened Greeting.message, Shift+F12 displayed its declaration and two uses while excluding OtherGreeting.message, and reference double-click opened Preview.kt 3:45. Verified recorded requests/streamed responses and repeated both shortcuts without the recorder. All 60 C# assertions and the sample Gradle build/run passed. Rebuilt the installable VSIX and documented setup and the untested virtual/library-source boundary.

## T3. Publish Gradle compiler diagnostics in Error List

Parse Kotlin compiler errors and warnings from Gradle output, publish build-scoped Error List entries, and navigate to the source location on double-click using the classic Visual Studio SDK.

Added GradleDiagnosticParser, BuildDiagnostic and a thread-safe BuildDiagnostics collector in Core. BuildErrorList uses the existing SDK's ErrorListProvider/ErrorTask and explicit OpenDocument/IVsTextView navigation. Connected start/completion/cancellation/workspace/disposal lifecycle without clearing LSP or other providers. No dependencies or dependency versions changed.

Rebuilt the installable VSIX; all 43 regression assertions passed. Installed in VistudioTest and compiled a separate broken Kotlin sample. Error List showed Main.kt line 4; double-click opened line 4, column 13. Corrected the fixture and rebuilt successfully; Error List then showed zero errors and warnings with no remaining rows. Documented recognized formats, batch publication, generic failure behavior and multiline-output limitations in README.md and docs/validation.md.

## T2. Implement the initial installable VSIX

Implement Kotlin syntax highlighting, configurable external LSP connectivity, and Gradle Wrapper build commands using the classic Visual Studio SDK.

Delivered Vistudio.sln, a C# VSIX project and reusable process/settings core, TextMate registration, LSP lifecycle/options, Open Folder resolution, asynchronous Gradle Build/Clean/Test/Cancel and Output integration, regression tests, and a real Kotlin/JVM sample. Updated installation instructions and toolchain inventory. Debugging and Gradle compiler Error List integration remain outside this initial implementation.

Built artifacts/Vistudio.Kotlin.vsix for Visual Studio 2026 18.12+ x64. All 19 regression assertions passed. Installed in the isolated VistudioTest profile, observed syntax coloring and package commands, built the Kotlin sample from the IDE, and verified LSP initialization, document synchronization, completion insertion, and shutdown with a protocol fixture. Removed temporary fixture settings and closed the test IDE. Production Kotlin server semantic behavior is not claimed tested; see docs/validation.md for evidence and limitations.

## T1. Design the Kotlin/JVM extension

Described syntax highlighting, IntelliSense, Gradle builds, navigation, Error List integration, and JVM debugging. Delivered README.md, docs/architecture.md, and docs/csharp-fragments.md. The user selected architecture and key C# fragments and explicitly required Visual Studio SDK; the design uses the classic in-process SDK throughout.

Verified relevant API names and integration contracts against Microsoft, Kotlin, and Gradle primary documentation. Checked all 10 Markdown files for local link targets, paired code fences, and English project text. No SDK packages were installed, no code was compiled, and no Visual Studio runtime tests were run. The documents distinguish the proposed JVM debugger/backend work from implemented functionality.



### T13. Kotlin file integration and multiplatform workflows

Add Kotlin file icons and default editor associations, discover real Gradle tasks for JVM, Native, JS, Wasm and Multiplatform build/test/run workflows, and add regression and real compiler checks. Add optional brace pair colors and default editor diagnostics when feasible. Keep compilation on the project's Gradle wrapper and debugging on the existing JVM adapter.

Execution plan: reuse the installed VSSDK, existing process runner and Kotlin/Gradle versions; add task discovery and a task picker, editor registrations and image assets, editor taggers and pure parsing tests. Add a small Multiplatform fixture with target-specific build/run/test acceptance scripts, build the VSIX, run regressions and available host compiler checks, and document host/backend limitations and actual verification. Preserve T12 work.

Implemented: VS file associations and vector icons, actual Gradle task discovery/picker and configurable Run command, six-color brace matching, default Gradle squiggles and inline Gradle/LSP error messages. Added a four-target sample, focused regressions, real compiler/run/failure/recovery checks and VSIX asset checks. The 72-assertion target/diagnostic suite and real JVM, mingwX64, JS/Node and Wasm/Node integration checks pass. The VSIX builds without warnings and its assets validate.

Completed acceptance: verified distinct icons, default click-to-open code editor behavior, matching and neighboring/nested brace colors, live LSP squiggles and inline messages after comments, Gradle Error List failures, and recovery after editing and rebuilding in actual Visual Studio. Fixed the Image Service rejection of bare DrawingImage roots; added UIElement rendering regressions at 100% and 200% DPI, root-level manifest deployment and codebase registration. The full strict build passes 262 assertions and asset/rendering checks. Installed and enabled the final package in the normal profile using VSSDK extension-manager tasks, refreshed configuration, verified installed/built DLL hashes, and repeated editor checks in that normal profile. T12 work and its running IDE session are preserved. Evidence and remaining platform/UI-matrix limits are recorded in docs/multiplatform-validation.md.

### T37. Java .properties files: highlighting and icon

Requested by the creator. `.properties` files (`gradle.properties`, `local.properties`, `gradle-wrapper.properties`, resource bundles) open in the code editor with syntax highlighting and show their own icon in Solution Explorer and editor tabs. Visual Studio ships no `.properties` grammar (its built-in INI grammar covers `.ini`/`.conf`/`.inf` only). Done when the VSIX ships the grammar, icon, and file association, packaging and icon-rendering checks cover them, and the strict build passes.

Execution plan:
1. `src/Vistudio.Vsix/Grammars/Properties.tmLanguage.json` (picked up by the existing `Vistudio.Kotlin` TextMate repository): `#`/`!` comments, keys with escapes, `=`/`:`/whitespace separators, values with `\uXXXX` and escape sequences, backslash line continuation.
2. `src/Vistudio.Vsix/Icons/PropertiesFile.xaml` (image-service `Image` root), image ID 3 in `Kotlin.imagemanifest`; `.properties` default editor and `DefaultIconMoniker` in `Registration/KotlinFiles.pkgdef`; csproj items.
3. Extend `tests/verify-vsix.ps1` (association, grammar entry, valid JSON) and `tests/verify-icon-rendering.ps1` (new icon); run `build.ps1`; update README.

Delivered: `Grammars/Properties.tmLanguage.json` (scope `source.java-properties`) in the existing TextMate repository, the `.properties` default code-editor association, and a key/value page icon (image ID 3, `DefaultIconMoniker`) in the Kotlin palette. Line ends use the newline `end` plus backslash-newline continuation pattern of Visual Studio's bundled make grammar; key/separator whitespace is limited to Java's space/tab/form feed so a key-only line does not swallow the next line.

Validation: VSIX builds; `verify-vsix.ps1` checks the association, the packaged grammar (parses, claims `properties`) and the new image moniker; `verify-icon-rendering.ps1` renders the icon at 100% and 200% DPI; a scripted tokenizer over the grammar confirmed comments, separators, booleans/numbers, escapes, continuation (a continued line starting with `#` stays value), placeholders, escaped spaces in keys and key-only lines. Not verified inside a running Visual Studio instance. The unit suite run by `build.ps1` fails on a pre-existing, unrelated check (`ProjectFileTests`: "Selected JDK reaches the wrapper process").
