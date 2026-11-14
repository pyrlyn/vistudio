# Solution Explorer project validation

Scope: T16, opening `.kotlinproj` as a native Solution Explorer project with Gradle builds; T17, F5/Ctrl+F5 and the fast up-to-date check; T18, the native commands, Properties window and nodes that replaced Kotlin Project Explorer. Only the checks below were performed.

## T20: F5 fails fast when the Kotlin language server exited; F5 on server 271

`./build.ps1`: 495 regression assertions (shared suite), package and rendering checks; one VSTHRD010 warning comes from another task's `KotlinDebugCompletion.cs`. `ServerLifecycleTests`: an unexpected exit records the exit code and the last stderr lines and fails a pending debug wait at once (under 5 s instead of the 3-minute timeout) and every later wait; a new server clears the failure; exits of replaced processes and deliberate stops are not failures; initialization and server-side Gradle import failures keep their messages.

Kotlin language server: with creator approval the expired 263.7084.0 was replaced by 271.2085.0 (official `kotlin-server-271.2085.0.win.zip` from download.jetbrains.com, 367,152,145 bytes, published SHA-256 `da210a7d84a06f855b8b7b04c5bd54936d270e79f3ea13a9c6ddc2f1046b2a98` matched) under `%LOCALAPPDATA%\Vistudio\servers\kotlin-lsp-271.2085.0`; the global setting points to it, the 263 folder and `settings.xml.before-lsp-271.bak` were kept. Server 271 removed `intellij.java.resolveBuildToolLaunch` (first run: "Unknown command"); T30 migrated the launch to the 271 protocol.

Native run of `tests/verify-solution-project.ps1 -SkipHiddenFilesOption -FailingServerPath <263 intellij-server.exe>` on Visual Studio 2026 18.12 (Release build of the shared tree, cleanly reinstalled: package SHA-256 `1F7F134274A6DD744C5CC1B072F9C55C3E534C8DA0550C0F804128DC27352807`, installed `Vistudio.Vsix.dll` `21189F59D2B865FAF7030A52CCCFC00201CFDC6A123850081EF90EF1020AE720`, `Vistudio.Core.dll` `D09587FEAE7DF7BAD647A5EAECF36EF1C1A5D31ADA680FE781B22EBD01583730`): **59 of 59** checks passed, the T19 Options step skipped. Besides the T16-T18 checks:

- F5 on server 271 stopped at `sample.MainKt.main(Main.kt:3)` 10 s after F5; continue ran to completion with `Resolved Gradle import works` in the Debug pane; Ctrl+F5 ran the program and returned to design mode.
- With the server switched to the expired 263 build and restarted, the Kotlin pane recorded "The Kotlin language server exited with code 7: … This build of kotlin-server has expired. The IDE will now close. … Fix the server in Tools > Options > Kotlin, then run Kotlin: Restart Language Server."
- F5 then showed that reason in the "Kotlin debugging" message box **0 s** after F5 (previously a 3-minute wait), and no debug session was left behind. The setting was restored byte-for-byte and the server restarted.

Evidence: `artifacts/solution-project-4c0b15138fab48f48355b1566cd559ee/` (`acceptance.log`, `fail-fast-message.txt`, `debug-break.txt`, `debug-output.txt`, `run-output.txt`). An earlier run (`artifacts/solution-project-842fb3d6f86242328a5d007cabecc06d/fail-fast-dialog-observed.txt`) shows the same dialog text.

Found during acceptance:

- When the server dies at startup, Visual Studio calls `OnServerInitializeFailedAsync` before the exit is observed, and its cleanup marked the exit as expected, so only a generic "initialization failed" was reported. The handler now waits up to 1 s for the process and records its exit code and stderr.
- kotlin-lsp 271 allows one server per `--system-path`. A second Visual Studio instance with the same Kotlin settings (another session's IDE on the shared cache) made the server here fail to initialize; T20 then reported that immediately. Concurrent IDEs need separate system paths (see ideas.md).
- Reinstalling the same 0.1.0 VSIX while another instance of the profile was open left two extension folders, and the IDE kept loading the old one. Uninstall, install and `/UpdateConfiguration` with no instance open fixed it.

## T18: native Solution Explorer replaces Kotlin Project Explorer

`./build.ps1`: warning-free VSIX, 371 regression assertions (shared suite, including T19's), package and rendering checks. `verify-vsix.ps1` now fails if the removed tool window is still registered. `ProjectSettingsTests` covers `KotlinProjectFile.Set` normalization and rejection (shell syntax and empty task lists, Gradle root without wrapper, configuration names, JVM class names, missing debug source/working directory, unterminated quotes, read-only properties; a rejected value changes neither the object nor the file), the `Toolchain`, per-module `Frameworks` (deduplicated across configurations, explicit missing versions) and `Declared Metadata` nodes, and imported values.

Native run of `tests/verify-solution-project.ps1 -SkipDebug` (package SHA-256 `CAC18010D9A042824201E77BBFAE10296B6A21973E519105A3ED8C67564FB5A8`; installed `Vistudio.Vsix.dll` `CE8F05E8D0E8CB4FEC9668560A698D2856D4C6DE9B774A5FBEA00041301E11B8` and `Vistudio.Core.dll` `035BCE6763E122AFA4BB699508F0D6BF282ADC7A06B68E053A7BF552863D7E81` match the build): **52 of 52** checks passed, F5/Ctrl+F5 skipped (see below). New checks:

- The Project Explorer command (`0x108`) no longer exists; **Tools > Kotlin: Create or Open .kotlinproj** and the project context-menu commands are registered.
- With the project selected, the context-menu command `ProjectandSolutionContextMenus.Project.ImportGradleModel` imported the model (the T16 tree checks then passed on that import).
- The tree contains `Toolchain` with `Gradle Wrapper 8.14.7` and the module `Frameworks` node with `Kotlin/JVM Gradle plugin 2.4.21`.
- In the real Properties window (WinForms property grid, driven through UI Automation), setting **Arguments** to `alpha "beta gamma"` and **Test tasks** to `:app:test` saved `DebugArguments` and `TestTasks` in the `.kotlinproj`; the grid then showed the saved values; **Gradle JDK** and the last import's **Gradle version** `8.14.7` were displayed; clearing **Arguments** wrote an empty value. Categories: Debug, Gradle, Last import, Project, with descriptions.
- A copy of the project without metadata opened with **File > Open > Folder**; **Create or Open .kotlinproj** generated `vistudio.kotlinproj`, closed the folder view, opened the project in a temporary solution, imported it (`SourceRoot` items written) and showed `folder-project` with `app/main/src/main/kotlin/sample/Main.kt` in Solution Explorer.

Evidence: `artifacts/solution-project-d827ebabac7f44b7ae31645048436f35/` (`acceptance.log`, `tree-after-import.txt`, `properties-window.png`, `properties-panel.png`, `create-or-open.png`). Package: `artifacts/Vistudio.Kotlin.SolutionProject.vsix`.

Defects found and fixed during acceptance:

- `IVsSolution.OpenSolutionFile` rejects a project file in Visual Studio 18 ("not a valid solution file"), and `File.OpenProject` ignores a path argument and shows its dialog. Create or Open now closes the folder view (`CloseSolutionElement` with the normal save prompt), creates a temporary solution and opens the project with `CreateProject(CPF_OPENFILE)`.
- `File.SaveAll` before Kotlin commands prompted "Save File As" for that temporary solution. Kotlin commands now save modified documents only.
- The Properties window kept showing old values after a save because it re-created the browse object from not-yet-reloaded metadata. Saving now reloads the project metadata immediately and refreshes the property browser; external edits of the `.kotlinproj` also refresh it.

Not re-verified: F5 and Ctrl+F5. On 2026-11-13 the installed Kotlin language server (`kotlin-lsp-263.7084.0`) refused to start ("This build of kotlin-server has expired", exit 7), so every Kotlin debug launch waits for the server and times out after 3 minutes, in solution and Open Folder mode alike. T18 does not change the debug path; T17's evidence below predates the expiry. Also not verified: the context-menu commands' visibility in the right-click menu itself (they were executed through DTE with the project selected), the JDK drop-down list, and an invalid value typed into the Properties window (covered by unit tests only).

## T17: F5, Ctrl+F5 and up-to-date check

`./build.ps1`: warning-free VSIX, 349 regression assertions, package and rendering checks. `LaunchTargetTests` covers automatic detection of the single top-level `main` in production roots (member `main` and test sources ignored), ambiguity, `DebugMainClass` selection with `@file:JvmName` and `suspend main`, explicit `DebugSourceFile`, quoted `DebugArguments`, relative working directory, missing files, unterminated quotes, facade names, and `GradleInputs` (outputs, tool folders, IDE metadata and directory timestamps ignored; edited, added and deleted sources detected).

The native run of `tests/verify-solution-project.ps1` (package SHA-256 `E63A82F67A642C0F5D03F5B17F1A8663E587C0FE33962E357358250CB1E803D7`; installed `Vistudio.Vsix.dll` `48E6A8D2F6851E534A82E1FEE26AC6C97A7639969C0BFC2B62CD1D6D89F02D1C` and `Vistudio.Core.dll` `8BE9FAE9874275EB82393041D5DF26D9CFA631AE6F7D06F9A6D52C91C71C41C1` match the build) passed **42 of 42** checks. The 36 T16 checks below passed again, plus:

- A second solution Build without input changes is reported `1 up-to-date` and does not start Gradle.
- With a breakpoint on `Main.kt` line 3, `Debug.Start` (F5) on the Kotlin project stopped in break mode at `sample.MainKt.main(Main.kt:3)` 11 seconds after F5. The language server was already running because `Main.kt` had been opened earlier in the session. The Kotlin pane shows `Kotlin debug target: sample.MainKt` and that the up-to-date project launched without another Gradle run.
- Continue ran the program to completion (design mode); `Resolved Gradle import works` appears in the Debug pane.
- `Debug.StartWithoutDebugging` (Ctrl+F5) logged `Kotlin run target: sample.MainKt`, ran the program with the same output and returned to design mode.

Evidence: `artifacts/solution-project-8fd982623d2a4416873c0334f2f37875/` (`acceptance.log`, `debug-break.txt`, `debug-break.png`, `debug-break-editor.png`, `kotlin-output.txt`, `debug-output.txt`, `run-output.txt`, `up-to-date-build.txt`; the Gradle project is in `project/` so evidence files are never build inputs). Package: `artifacts/Vistudio.Kotlin.SolutionProject.vsix`.

Found during acceptance: the solution build manager takes the up-to-date result from the `StartUpToDateCheck` HRESULT and does not advise status callbacks for it; directory timestamps were unusable because Gradle creates output folders inside input folders. Both are reflected in the implementation.

Not validated for T17: F5 when the language server has not started yet in the session, the Visual Studio "project is out of date" prompt path, F5 for a second Kotlin project in the same solution, and Multiplatform `*Main` entry points.

## Build and automated tests

`./build.ps1` builds the VSIX without warnings and passes 329 regression assertions plus package and icon-rendering checks. `verify-vsix.ps1` now also requires the `.kotlinproj` project factory registration (`Projects\{e7447d71-1121-42dc-8a4d-d3d78b3cc633}`, `PossibleProjectExtensions=kotlinproj`).

- `ProjectTreeTests`: unimported hint and no invented dependencies; aggregate root folding; configured source-set order; only existing roots; hidden folders; lazy folders and document paths; path lookup scoped to the matching root; resolved configurations only, with counts; project references; selected version with requested selector as provenance; transitive marker; Gradle Scripts content; unique sibling keys; conventional fallback for snapshots without source roots; `KotlinProjectFile.Find` (default, single custom name, ambiguity) and `LoadForProjectFile`.
- `GradleModelTests`: `SourceRoot` items are validated (absolute path, `Sources`/`Resources`, known module).
- `tests/test-gradle-import.ps1` (real Gradle 8.14.7 wrapper, Microsoft JDK 11.0.31, Kotlin JVM plugin 2.4.21, two modules): 16 checks pass, including the new ones: Gradle reports `src/main/kotlin` source roots for `:app` and `:shared`, the tree lists real `.kt` files of `:app`, and `runtimeClasspath` shows `kotlin-stdlib:2.4.21`.

The agent shell sets `NoDefaultCurrentDirectoryInExePath=1`, which stops `cmd.exe` from starting `gradlew.bat` from the Gradle root; the existing wrapper tests then fail with `"gradlew.bat" is not recognized`. All runs above clear this variable first. A normal Visual Studio session does not set it.

## Native Visual Studio acceptance

`tests/verify-solution-project.ps1` (Windows PowerShell, STA; typed DTE steps in `tests/SolutionProjectAcceptance.cs`, compiled with the IDE's Roslyn compiler):

1. Installs the VSIX with VSIXInstaller into the isolated root suffix `VistudioProjectTest` of Visual Studio Community 2026 18.12 and runs `/UpdateConfiguration`. The normal profile is untouched.
2. Copies `tests/fixtures/gradle-import` (real three-project Kotlin/JVM build) with the sample wrapper, generates `vistudio.kotlinproj` through the production Core API and writes a `.sln` that references it with the Kotlin project type.
3. Opens the solution in the IDE and drives the real Solution Explorer, editor, solution build manager and Error List.

Result of the final run: **36 of 36 checks passed** with package SHA-256 `9922AE6BDBFE311F29FADB4067F8590733644BA78F6E280BBB16E684C3EAAF9A`. Installed and built files match:

| File | SHA-256 |
| --- | --- |
| Vistudio.Vsix.dll | `3A910722E80BD68F39CF8F5BAAD0A1B1DE8AEB14BBECB6236290B62E6DEB8EDF` |
| Vistudio.Core.dll | `DD7948E7B6695865A61D5C4101FEF8C6F4662BFF42344ADA195166EB1AD1A322` |
| Build/ImportModel.gradle | `AA5037003139655F2B5AAFA0C8D5FA0E881602CAB0C02FC82BCF9706BB4397F6` |

Observed behavior:

- The `.kotlinproj` loads as a project; before import, the tree shows the import hint, the placeholder module and Gradle Scripts.
- `Tools.KotlinImportGradleproject` run inside the IDE imports the model in project mode (no folder workspace) and writes `SourceRoot` items. The tree refreshes from the file watcher without a reload prompt: modules `app` and `shared`, source set `main`, `src/main/kotlin`, `sample/Main.kt` and `sample/Greeting.kt`, per-module `Dependencies` with `runtimeClasspath (n)`, `org.jetbrains.kotlin:kotlin-stdlib:2.4.21`, project reference `:shared`, `org.jetbrains:annotations:13.0  (transitive)`, and Gradle Scripts with `settings.gradle.kts` and `build.gradle.kts (app)`. The aggregate root module stays visible because it has a resolved `buildscript:classpath` (Kotlin Gradle plugin).
- Double-clicking `Main.kt` in Solution Explorer opens it in the editor; it is not in Miscellaneous Files. An edit saved through the IDE reaches disk; undo plus save restores the original bytes.
- Solution Build runs `gradlew build` (`BUILD SUCCESSFUL`, Kotlin classes for both modules), Clean runs `clean` and removes `app/build`, Rebuild runs `clean build`. Gradle output appears in the Build pane.
- A real type error appended to `Greeting.kt` fails the solution build (`LastBuildInfo = 1`, `BUILD FAILED`) and the Error List shows `Initializer type mismatch: expected 'Int', actual 'String'.` at line 5 of `Greeting.kt`. After the fix the build succeeds again.
- A Kotlin file created on disk appears under `shared/main/src/main/kotlin/sample` without reloading; deleting it removes the node.

Evidence (not versioned): `artifacts/solution-project-938f01eee1fc48a19d6c68917afc4b6b/` contains `acceptance.log`, `tree-before-import.txt`, `tree-after-import.txt`, `build-output.txt`, `clean-output.txt`, `rebuild-output.txt`, `failed-build-output.txt`, `error-list.txt`, `solution-explorer.png` / `solution-explorer-panel.png`, `error-list.png` and `ActivityLog.xml`. Package: `artifacts/Vistudio.Kotlin.SolutionProject.vsix`.

Defects found and fixed during acceptance:

- The shell showed "File Modification Detected — Reload?" after each import rewrote the `.kotlinproj`. The project root now reports `VSHPROPID_HandlesOwnReload` and refreshes itself.
- The solution Build pane in VS 18 rejects writes from background threads (`OutputStringNoPump must be called on the UI thread`), which failed the first build. Gradle lines are now queued and flushed on the UI thread.

Environment notes: the first launch of the new profile showed the GitHub Copilot promotion and the personalization page; they were dismissed with "Maybe later" and the default settings. In one run the Gradle client hung in the JDK while creating its loopback selector pipe before the forked daemon connected; the script now cancels through `Kotlin: Cancel Gradle` and retries once (the final run did not need it). PowerShell's late-bound COM adapter cannot reach DTE2 tool windows in VS 18, so the steps use typed EnvDTE calls.

## Not validated

F5 debugging from a solution project (not implemented), Properties window contents of tree nodes, context menus, multiple Kotlin projects in one solution, Multiplatform or Android source-set layouts in Solution Explorer, and large projects near the 5000-entries-per-folder limit.
