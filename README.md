# Vistudio: Kotlin for classic Visual Studio

An installable C# VSIX for **Visual Studio 2026 18.12+ (x64 or ARM64)**, using the classic Visual Studio SDK. It provides Kotlin file icons and editor associations, syntax and brace coloring, LSP navigation, Gradle build/run/test workflows for Multiplatform targets, editor diagnostics, and Kotlin/JVM debugging, plus Zig and Dart/Flutter support.

## Install and use

1. Build with `./build.ps1` from PowerShell, or use the generated `artifacts/Vistudio.Kotlin.vsix`.
2. Close Visual Studio and open the VSIX installer. Restart Visual Studio after installation.
3. Use **File > Open > Folder** to open the directory containing your project's `gradlew.bat`.
4. Click a `.kt` or `.kts` file in Solution Explorer to preview it, or double-click to keep it open. Both extensions are registered with Visual Studio's code editor and Kotlin content type. Syntax and brace coloring work without a language server. This VSIX registers Visual Studio associations, not Windows Explorer default applications. Java `.properties` files (`gradle.properties`, `local.properties`, `gradle-wrapper.properties`, resource bundles) also open in the code editor with their own icon and a Vistudio TextMate grammar, since Visual Studio ships none: `#`/`!` comments, keys, `=`/`:` separators, `\uXXXX` and backslash escapes, line continuations, `true`/`false` and numeric values, and `{0}`/`${name}` placeholders.
5. Use **Tools > Kotlin > Build with Gradle**, **Clean with Gradle**, **Test with Gradle**, or **Cancel Gradle**. Output and exit status appear in the **Kotlin** Output pane.
6. After compilation, errors and warnings appear in **View > Error List**. Double-click a located diagnostic to open its file at the reported line and column. Use the **Entire Solution** scope and **Build + IntelliSense** source filter if rows are hidden.

If an experimental profile has not initialized its package registry yet, close that profile and run `devenv /RootSuffix VistudioTest /UpdateConfiguration` before reopening it. The test installation procedure is in [validation.md](docs/validation.md).

This development package retains version 0.1.0. To replace an earlier 0.1.0 installation, uninstall Vistudio Kotlin from that profile first, then install the newly built VSIX.

## Kotlin project in Solution Explorer

With the Gradle folder open (**File > Open > Folder**), **Tools > Kotlin > Create or Open .kotlinproj** creates `vistudio.kotlinproj` (a versioned XML metadata file with the familiar .NET `Project / PropertyGroup / ItemGroup` layout), opens it as a Solution Explorer project and imports the Gradle model. Existing metadata opens directly with **File > Open > Project/Solution**.

Right-click the project for **Import Gradle Model**, **Run Tests with Gradle**, **Run with Gradle**, **Gradle Tasks...**, **Cancel Gradle** and **Edit Project File**. Select the project and press **F4** to edit its settings in the Properties window: Gradle JDK (detected installations or any JDK home), Gradle root, Build/Test/Run tasks, extra import configurations and the F5 entry point; the last import's Gradle and JVM versions are shown read-only. Invalid values are rejected with the reason.

Import resolves modules, direct/transitive libraries, project references, recognized framework/plugin versions and source roots. The tree shows them per module (`Dependencies`, `Frameworks`), plus `Toolchain` (wrapper, Gradle JDK, last-import Gradle/JVM) and manually `Declared Metadata`; Properties shows requested versions and Gradle selection reasons. Failed or canceled imports preserve the last successful snapshot. Kotlin builds stay on Gradle, and the language-server JDK remains independent. See [the file structure, import scope and usage](docs/project-file.md).

**File > Open > Project/Solution** opens a `.kotlinproj` (or a `.sln` that contains one) as a Solution Explorer project: Gradle modules, source sets with their files, resolved dependencies per configuration and Gradle scripts. Solution Build, Rebuild and Clean run Gradle; output goes to the Build pane and compiler errors to the Error List. F5 and Ctrl+F5 debug or run the project's `main` with the Kotlin/JVM debugger (detected automatically or set with `DebugMainClass`). See [Solution Explorer project](docs/project-file.md#solution-explorer-project) and [its validation](docs/solution-project-validation.md).

## New Kotlin projects

**File > New > Project** (language filter **Kotlin**) creates a Gradle project with the wrapper and `vistudio.kotlinproj` from one of five templates: Kotlin Console App (JVM), Kotlin Class Library (JVM), Kotlin and Java App (JVM), Kotlin Multiplatform App (choose jvm, js, wasmJs and mingwX64 targets) and Compose Multiplatform Desktop App. The new project opens in Solution Explorer and its Gradle model is imported; Build runs Gradle and F5 debugs the console app's `main`. Versions match the samples (Gradle 8.14.7, Kotlin 2.4.21, Compose 1.12.2). See [New Project templates](docs/project-file.md#new-project-templates) and [their validation](docs/template-validation.md).

## Configure the language server

Install a compatible standalone Kotlin language server separately, for example a release from [Kotlin/kotlin-lsp](https://github.com/Kotlin/kotlin-lsp). The VS Code extension package is not the classic Visual Studio plugin.

Open **Tools > Kotlin > Settings** or **Tools > Options > Kotlin > General**:

| Setting | Meaning |
| --- | --- |
| Language server: Executable / launcher | Absolute path to the server's `.exe`, `.cmd`, or `.bat` launcher, or `java.exe` when that distribution documents direct Java invocation |
| Language server: Arguments | Actual stdio arguments for that release; default `--stdio`, verify against its help output. Kotlin LSP 271 allows one server per `--system-path`, so each running Visual Studio instance gets its own: the configured path for the first instance, then `<path>-2`, `<path>-3`, ... (the official launcher without the argument starts from `%LOCALAPPDATA%\Vistudio\kotlin-lsp`). A path counts as taken while another instance holds its `.vistudio.lock` or a server holds its `.app.lock`; a restarted instance reuses a free path and its cache. The chosen path is in the Kotlin Output pane |
| Language server: JAVA_HOME | Optional server-specific JDK; can differ from the project's JDK |
| Gradle: Root folder | Empty uses the opened folder; otherwise an absolute path or relative path from that folder |
| Gradle: JAVA_HOME | Optional project JDK; empty inherits Visual Studio's environment |
| Gradle: Build / Test tasks | Task paths separated by spaces, such as `:app:build` or `clean build` |
| Gradle: Run tasks | Task paths such as `:desktop:jvmRun`, `:native:runDebugExecutableMingwX64` or `:web:wasmJsNodeDevelopmentRun` |
| Editor: Rainbow braces / Error Lens (inline diagnostics) | Enabled by default, including for older settings files; reopen editors after changing these settings. Error Lens shows the most severe diagnostic of each line after its end (error/warning/info color, marker, `+N` for more, all messages in the tooltip) and tints the line, in every editor (Kotlin, Java, C#, ...). It is drawn on adornment layers and does not move text or the caret. Hinted suggestions and spelling are not shown. Colors: **Tools > Options > Environment > Fonts and Colors > Text Editor > Error Lens - Error/Warning/Info** |
| Editor: Scroll bar mode / map width / map preview | Minimap for `.kt`, `.kts` and `.java` editors using the Visual Studio scroll bar map mode: a code image colored like the editor, the visible area (click or drag to scroll), changes, find results, bookmarks, breakpoints, errors (including Gradle build errors in Kotlin files) and a hover preview. `Inherit` (default) keeps **Text Editor > All Languages > Scroll Bars**; `Bar` or `Map` override it only for Kotlin and Java. Width: Off, Narrow, Medium, Wide. Applies to open editors on OK |
| Inlay hints / Inlay hints (Kotlin) / Inlay hints (Java) | When `.kt`, `.kts` and `.java` editors show inlay hints (`Always` by default, applies to open editors on OK) and one switch per hint kind, all on. See [Inlay hints](#inlay-hints) |
| Solution Explorer: Show hidden files | Off by default: files and folders whose name starts with `.` are not shown in `.kotlinproj` trees. Turning it on refreshes loaded Kotlin projects without reopening them |

Apply changes, then run **Tools > Kotlin > Restart Language Server**. Opening a Kotlin file activates the client. It sends the `kotlin` language ID and the opened folder as the LSP root. Completion, hover, definitions, references, and diagnostics depend on the external server's capabilities and its compatibility with Visual Studio's LSP client. No language server is bundled or automatically downloaded.

Tested backend: the official **Kotlin language server 271.2085.0 Windows x64 standalone archive** (`kotlin-server-271.2085.0.win.zip`, published SHA-256 `da210a7d84a06f855b8b7b04c5bd54936d270e79f3ea13a9c6ddc2f1046b2a98`; build 263.7084.0 expired on 2026-11-13), using `bin/intellij-server.exe --stdio` and its bundled JetBrains Runtime 25. The deprecated `kotlin-lsp.cmd` launcher is unnecessary. Keep the project's Gradle JDK separate; the sample uses JDK 11. Download the archive and published SHA-256 from the [official release](https://github.com/Kotlin/kotlin-lsp/releases/tag/kotlin-lsp%2Fv263.7084.0), not the VS Code VSIX.

### Settings for each window (`.vs\settings.json`)

Tools > Options holds the global defaults. Each Visual Studio window can override them for the folder or solution it has open with `<workspace>\.vs\settings.json`, so two windows can use different compilers, versions, flags and paths at the same time. Every Visual Studio instance is its own process: its language servers, `zig env`, Error List entries and squiggles stay in that window.

```jsonc
{
  // Comments and trailing commas are allowed. Keys are setting property names in any case.
  "vistudio.zig.zigPath": "tools/zig-0.16/zig.exe",      // relative paths start at the workspace folder
  "vistudio.zig.libDirectory": "${workspaceFolder}/zig-lib",
  "vistudio.zig.defaultFlags": "-O ReleaseSafe -lc",
  "vistudio.zig.diagnosticLevel": "Errors",
  "vistudio.kotlin.gradleJavaHome": "%JAVA_21_HOME%",
  "vistudio.kotlin.buildTasks": ":app:assemble",
  "vistudio": { "java": { "languageServerPath": "C:/tools/jdtls" } }
}
```

- Sections: `vistudio.kotlin.*` (Tools > Options > Kotlin), `vistudio.java.*` (its Java settings, for example `languageServerPath`, `languageServerJavaHome`, `inlayHintsParameterNames`) and `vistudio.zig.*` (Tools > Options > Zig). Flat dotted keys and nested objects are equivalent.
- Path settings expand `${workspaceFolder}` and `%VARIABLES%`; relative paths are resolved against the workspace folder (`gradleRoot` stays workspace-relative as before).
- Values are checked like Tools > Options checks them (flags, build step, Gradle tasks). Unknown keys, wrong types and rejected values keep the global value and are reported in the Kotlin and Zig Output panes together with the file in use. Invalid JSON is reported and ignored.
- Precedence: Tools > Options < `.kotlinproj` < `.vs\settings.json`.
- The workspace is the Open Folder location, otherwise the solution folder; a `.kotlinproj` outside it uses the `.vs\settings.json` next to it.
- Saving the file applies it at once: Zig ast-check and ZLS, scroll bar, inlay hints and Solution Explorer refresh, and the Kotlin or Java language server restarts when one of its server settings changed. Switching workspace restarts the servers with the new workspace's settings and clears the previous workspace's Zig build errors (Kotlin build errors were already cleared).
- Tools > Options never writes workspace values; the Zig toolbar's build step drop-down saves the global step and says so when the workspace file overrides it. ZLS gets its own `zls.json` per workspace under `%LOCALAPPDATA%\Vistudio\zls\<hash>`.

Checks: `WorkspaceSettingsTests` in `Vistudio.Tests.exe` (parsing, mapping and types, relative paths, validation fallback, precedence, cache, window root, per-workspace `zls.json`).

### Definition and usages

Open the Gradle root with **File > Open > Folder**, open a Kotlin source file, and allow the server to import the project. With the standard Visual Studio keyboard bindings:

- **F12** (`Edit.GoToDefinition`) opens the symbol's definition, including definitions in another Kotlin file.
- **Shift+F12** (`Edit.FindAllReferences`) opens the native **Find All References** window. Double-click a result to open that usage. The host currently requests the declaration as well as references.

These commands use Visual Studio's existing LSP routing and result UI. The extension does not replace keyboard bindings or search text to infer usages. Custom keyboard profiles may require assigning these commands under Tools > Options > Environment > Keyboard. See the [sample acceptance steps](samples/kotlin-jvm/README.md).

The VSIX normalizes the first outgoing `initialize` frame to remove VS-private completion-kind and diagnostic-tag enum values that the tested server rejects. All later frames, including `definition`, `references`, cancellation, document synchronization and streamed reference results, pass through unchanged. The SDK middleware does not intercept initialization on this host, so the adapter is at the connection's write stream.

Verified scope: Gradle/JVM Kotlin source navigation across local files. Library/decompiled-source URIs, remote workspaces, other targets and other server versions are not claimed tested. The server must successfully import the project; a connection alone does not mean indexing/import has finished.

The connection uses **stdio**. Stdout must contain only LSP frames; server logs go to stderr and the Kotlin Output pane. TCP-only launchers are not supported in this version. Server exit is reported; use the restart command to reconnect. Switching folders stops the old server and cancels the current build.

Settings are saved per user at `%LOCALAPPDATA%/Vistudio/settings.xml`, shared across IDE profiles. An empty server path disables LSP while retaining syntax coloring and build commands. Only open trusted projects: Gradle executes their build scripts.

## Gradle compiler diagnostics

The extension recognizes Kotlin `.kt` and `.kts` diagnostics in modern `e: file:///.../Main.kt:4:13 Message` and legacy `e: C:\...\Main.kt: (4, 13): Message` forms, including `w:` warnings. Relative paths are resolved against the configured Gradle root; file URIs and encoded characters are normalized. Identical entries from stdout/stderr are deduplicated.

Entries are published as a batch after Gradle exits and both output streams have drained. Starting an accepted build/clean/test invocation clears the previous Vistudio build entries. Canceling leaves the build list empty; changing or closing the folder clears it after canceling the active build. LSP and other extensions' diagnostics are independent and are not cleared.

A failed Gradle invocation without a recognized Kotlin error creates a general error pointing to Kotlin Output, with no fabricated file or line. It cannot navigate to source. Multiline explanation lines, Java compiler output, arbitrary plugin messages, and unrecognized formats remain in Output; only the recognized Kotlin diagnostic headline is put in Error List. Build success still depends on the process exit code.

Navigation clamps the original location to the current document if edits shortened it. Removed files are reported in Kotlin Output. The publisher uses the SDK's [ErrorListProvider / ErrorTask](https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.errorlistprovider?view=visualstudiosdk-2022), with explicit [OpenDocument](https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.vsshellutilities.opendocument?view=visualstudiosdk-2022) navigation; a custom table provider for large result sets remains outside this implementation.

## Build the extension

Requires Windows, Visual Studio with **Visual Studio extension development**, .NET Framework 4.7.2 targeting tools, and NuGet access. Open [Vistudio.sln](Vistudio.sln), or run:

```powershell
./build.ps1
```

The script builds the VSIX, runs the dependency-free C# regression executable, and copies the package to `artifacts/Vistudio.Kotlin.vsix`. Use Visual Studio's full-framework MSBuild for VSIX packaging. MSBuild builds the C# extension only; it is never called for Kotlin builds.

The implementation references the published SDK 17.14 contracts because the available 18.12 NuGet meta-package currently refers to unpublished dependencies. The installation target is the tested VS 2026 host. VS 2022 compatibility is not declared.

## Current scope

Implemented: `.kt`/`.kts` TextMate grammar (nested comments, strings/interpolation, declarations, keywords, numbers), shared MEF LSP client, settings/restart, asynchronous Gradle Build/Clean/Test/Cancel, output, validated task arguments, owned Windows process jobs, and a [real Kotlin/JVM sample](samples/kotlin-jvm/README.md).

Gradle commands use the project's wrapper with `--console=plain --no-daemon`. CLI options are deliberately not accepted in task fields. The wrapper's exit code determines success. Logs use UTF-8 decoding; configure the selected JDK/Gradle output accordingly for non-ASCII text. Cancellation terminates the owned invocation rather than all Java processes.

Not yet implemented: Native/JS/Wasm debug adapters, Android device management, and automatic server installation. LSP diagnostics depend on the configured server and are handled by Visual Studio.

## Multiplatform build, run and test

Use **Tools > Kotlin > Gradle targets and tasks...** to discover the project's actual Gradle tasks. Search by target, name or description, select one or more tasks and choose **Run selected**. Qualified task paths preserve subproject identity and custom target names. JVM, Native, JS and Wasm use the project's Gradle Wrapper and target toolchains; no Kotlin build is delegated to MSBuild. Discovery runs the bundled init script with configuration caching disabled for that invocation only.

**Tools > Kotlin > Run with Gradle** executes the configured Run tasks. Build, Test and Run each have independent settings. For KMP test suites choose `allTests` or a target's test task through the picker or Test tasks setting; plain JVM projects retain the `test` default. Output, cancellation, save-before-run and compiler diagnostics use the same pipeline as Build. A nonzero exit remains a failure; unsupported host targets are reported by Gradle. Browser development servers remain active until canceled. Programs needing standard input require an external terminal.

See the [four-target sample and repeatable integration test](samples/kotlin-multiplatform/README.md). The tested runtime matrix is JVM, Windows Native (`mingwX64`), JS/Node and Wasm/Node. Other Native hosts and browser/device workflows require the corresponding toolchains and are not claimed tested. F5 debugging remains JVM-specific.

In a `.kotlinproj`, the Gradle import records each Multiplatform target (platform, source sets, configurations and its real build/test/run tasks) and the project kind (JVM, JS, Wasm, Native or Multiplatform). Solution Explorer shows shared source sets, a **Targets** node with one node per target (its own source sets and dependencies, platform icon, **Build/Run/Test &lt;target&gt;** context commands), and the **Startup target** project property selects the target for Run with Gradle, F5 and Ctrl+F5. JS, Wasm and Native targets have no debugger: F5 and Ctrl+F5 run the target's Gradle run task, and F5 also opens Vistudio Diagnostics on the program it starts (see [CPU and memory while debugging](#cpu-and-memory-while-debugging)); a target without a run task gets a message. See [Kotlin targets](docs/project-file.md#kotlin-targets).

## Braces and diagnostics in the editor

Curly braces share a color with their matching partner. Adjacent sibling pairs and immediate nesting levels use different colors from a six-color palette. Strings, character literals, backtick identifiers and nested comments are excluded; code inside string interpolation is included. Incomplete documents remain editable. Colors can be changed under **Fonts and Colors > Kotlin brace 1–6**.

Gradle errors and warnings are enabled by default in Error List and as editor squiggles. The inline layer also reads Visual Studio's LSP error tags. It places a compact, trimmed message after the entire source line (including its comment), with the full message on hover. Space-negotiating adornments reserve their own width and do not replace source text. Duplicate messages on a line are combined. Build markers disappear after an edit until the next build to avoid presenting outdated coordinates. Live semantic errors require a configured server that publishes diagnostics; build diagnostics appear after Gradle exits.

The editor additions have been checked in Visual Studio: distinct source/script icons, default code-editor opening, adjacent/nested brace colors, live LSP errors after trailing comments, Gradle Error List entries, and recovery after editing and rebuilding. Single-click preview follows Visual Studio's **Preview Selected Items** preference; double-click opens a normal editor tab. See [the performed checks and remaining platform limits](docs/multiplatform-validation.md).

## Kotlin/JVM debugging

Debugging requires the official Kotlin server configured above. Build 263.7084.0 has expired; F5 uses the launch protocol of **271.2085.0** and later, where the launch names the main class and its file and the server finds the Gradle module itself. The server supplies the Kotlin-aware DAP/JVM backend; the VSIX connects it to Visual Studio's native Debug Adapter Host. Other LSP implementations do not automatically provide this debug contract.

1. Open the Gradle root as a folder and open a Kotlin source file so the server imports the project.
2. Create `.vs/launch.vs.json` with the configuration below. The sample includes a `launch.vs.json` template to copy into its `.vs` directory.
3. Open the configured source file, set a line breakpoint with **F9**, and press **F5** using **Current Document**, or select its launch target from the startup dropdown.
4. Use **F10**, **F11**, **Shift+F11**, **F5**, and **Shift+F5** for step over, step into, step out, continue, and stop. Open **Debug > Windows > Locals**, **Watch**, and **Call Stack** to inspect the session. These are the standard Visual Studio debugger commands.

```json
{
  "version": "0.2.1",
  "configurations": [{
    "type": "kotlin-jvm",
    "name": "Kotlin JVM sample",
    "project": "src/main/kotlin/Main.kt",
    "mainClass": "sample.MainKt",
    "args": []
  }]
}
```

`project` names the Kotlin or Java source file whose imported module owns the entry point. `mainClass` is the JVM class name, including package and any `@JvmName` override. Without `mainClass` (for example **F5** on **Current Document**), the entry class is derived from the file: a Kotlin top-level `main` gives `package.FileKt` (or its `@file:JvmName`), a Java `public static void main` gives `package.ClassName`. Optional `args` and `vmArgs` are arrays of strings; `env` is an object of string values; `cwd` is relative to the opened folder or absolute. `gradleProject` and `sourceSet` select an alternate Gradle project/source set when needed. Start with the defaults resolved from the source file. The sample is a local classpath-based Gradle JVM application.

F5 saves documents and runs the configured **Gradle Build tasks** first. A failed or canceled build prevents debugger startup and retains compiler diagnostics in Error List. The server then resolves the Gradle launch target and runs the JVM through Gradle, with the project's dependencies and toolchain. This second Gradle invocation performs the debug run; its output goes to **Debug Output**. Neither step uses MSBuild for Kotlin. The extension's own C# build uses the VSSDK toolchain.

The DAP connection uses a server-selected loopback port; there is no fixed JDWP port to configure. Stop the session before manually restarting the language server. Changing folders stops the debug session before replacing the server. Terminal input, remote attach, test debugging, coroutines and inline-function stepping are outside the validated scope. Internal-console output and ordinary JVM source debugging are the supported workflow.

The transport resolves the backend's temporary object previews before returning them to Locals, retaining field expansion. It repeats only `variables`, never expression evaluations; unresolved previews become an explicit unavailable value after a two-second retry budget. Continuing, stepping, canceling or stopping ends retries for that suspension.

When the backend sends `terminated` early, the transport continues forwarding output and allows up to five seconds for `exited`, then delivers the actual reported exit code before ending the session. It never substitutes success for a missing exit code. Stop Debugging and connection closure bypass that wait. For Gradle launches the backend reports the Gradle runner's status; a child's distinct exit code may also appear in Gradle's failure output. See [validation evidence and limitations](docs/validation.md).

### Kotlin expressions in Watch and Immediate

While stopped at a Kotlin breakpoint, select the desired frame in **Call Stack**. In **Debug > Windows > Watch > Watch 1**, enter a Kotlin expression in the empty Name row. In **Immediate** (**Ctrl+Alt+I** with the standard keyboard profile), enter `? expression` and press Enter. Evaluation uses the suspended JVM and selected stack frame through the Kotlin backend.

Examples at the sample's `EvaluationMain.kt` line 8:

| Window | Expression | Expected result |
| --- | --- | --- |
| Watch | `greeting` | `Greeting(name=Kotlin)` |
| Watch | `greeting.name.length + 1` | `7` |
| Watch | `nullableName ?: "Guest"` | `"Guest"` |
| Watch | `greeting.copy(name="Watch")` | Expandable `Greeting(name=Watch)` |
| Immediate | `? greeting.name.uppercase()` | `"KOTLIN"` |
| Immediate | `? listOf(1, 2, 3).sum()` | `6` |

Function calls can change the target's state. Use Immediate for intentional one-off calls; Watch expressions may run again when Visual Studio refreshes them. Repeated explicit evaluations request a fresh result, without the transport retrying the expression. Invalid expressions display the backend diagnostic; some backend errors arrive as result text rather than failed DAP responses.

This is suspended-frame expression evaluation, not a persistent Kotlin REPL. Declarations shared between commands, advanced language constructs and full IntelliJ evaluator parity are not claimed. Visual Studio's restricted hover/no-function-evaluation requests are forwarded unchanged; enforcement of their flags depends on the backend. See the [repeatable sample checks](samples/kotlin-jvm/README.md).

Saved Watch expressions are also tested on the first stop after restarting Visual Studio. For the official `intellij-server.exe`, the extension sets the server's hover budget to 4000 ms instead of its 500 ms default, within Visual Studio's 5000 ms request budget. This waits for the original evaluation, without retrying it or relaxing its restriction flags. Existing `IJ_JAVA_OPTIONS` and an explicit timeout override are preserved; machine environment and Gradle options are unchanged. Slow evaluations can still reach the bounded deadline, and custom launchers require their own backend configuration.

### Completion in Watch and Immediate

While stopped in a Kotlin frame, edit a Watch expression or type `? expression` in Immediate. **Ctrl+Space** opens suggestions; typing an identifier or `.` also triggers completion. **Tab** or **Enter** accepts the selected suggestion; **Escape** closes the list. A second Enter submits the completed expression for evaluation.

Suggestions contain the selected frame's local variables and arguments, and the debugger-exposed fields of a simple receiver chain, such as `greeting.na` or `greeting?.na`. Choose another frame in Call Stack to change the available names. An open list is dismissed on a frame change, continue, stop, or loss of editor focus. Keyword and spaced identifiers are inserted with Kotlin backticks.

Member lists also include **methods and extension functions**, with overload signatures, from the real Kotlin language server. For example, `target.bu` offers `bumpMethod()` and an imported `bumpExtension()`. Accepting a callable inserts its **name only**; add parentheses/arguments and explicitly submit the expression when you intend to invoke it. Showing or accepting suggestions does not call the method.

The extension captures the selected frame's source file and line, then places the receiver chain at that position in a separate in-memory copy of the saved Kotlin source. This preserves **local extensions, file-private and class-private extensions, declared/inferred generic arguments and generic bounds** for Kotlin semantic analysis. For example, the same `items.lex` prefix in frames with `List<String>` and `List<Int>` offers different applicable extensions; a generic extension's result becomes `String?` or `Int?`. JVM type erasure does not determine these lists. A compiler-checked fallback now handles omitted member extensions on the enclosing class's `T` and `List<T>`. It isolates generic owner identities using semantic rename results in analysis-only text and resolves candidate calls with signature help. The real-server/JVM frame checks pass; native Watch/Immediate acceptance of this latest fix remains pending because desktop automation could not reliably target the test IDE. See [validation](docs/validation.md#enclosing-class-generic-extension-fix).

Every LSP analysis document is closed after its request, including on cancellation or error. Requests from both windows are serialized: at most the primary snapshot and one isolated call probe are open together. Empty incomplete LSP lists receive at most three bounded completion requeries; expression evaluation is never retried for suggestions. No scratch file is written, no original document is changed, and no DAP `evaluate`, backend completion-apply command, or Gradle invocation is used to obtain suggestions. Rename results are applied to a temporary string only, never to the workspace or editor. Native local/argument enumeration disables automatic property/function evaluation. Requests are debounced, have a five-second deadline and are canceled when the expression or debugger context changes. The backend's DAP completion capability remains false; semantic suggestions use its LSP connection.

Semantic completion requires a readable `.kt` source and a valid selected-frame line. The source copy supplies static Kotlin context, not runtime reification of type parameters. Source changes after compilation, generated/inline/coroutine frame mappings, ambiguous duplicated top-level declarations and a stop inside a multiline expression can limit results; these cases are not comprehensively validated. Call/index receivers and completion inside strings/comments remain excluded. Imports are not added automatically, and Immediate lines beginning with `>` remain IDE commands. Traversal is bounded to eight receiver components and 512 runtime properties per scope; source snapshots to 1 MiB and semantic lists to 256 entries.

## Java alongside Kotlin

Kotlin/JVM projects that also contain Java sources (`src/main/java`) are supported in the same workspace and the same debug session. The [mixed sample](samples/kotlin-java-jvm) has Kotlin calling Java and Java calling Kotlin.

| Feature | `.kt` files | `.java` files |
| --- | --- | --- |
| Coloring | Vistudio Kotlin grammar | Visual Studio's built-in Java grammar, plus JDT LS semantic tokens when configured |
| Completion, hover, Go To Definition | Kotlin server, including Java classes and members | Eclipse JDT LS (optional, see below), including Kotlin declarations compiled by Gradle |
| Diagnostics | Kotlin server and Gradle | JDT LS and Gradle |
| Debugging | One Kotlin/JVM session | Same session: breakpoints, stepping in both directions, mixed Call Stack, Locals, Watch |

**Java editing features** come from a separately installed [Eclipse JDT Language Server](https://github.com/eclipse-jdtls/eclipse.jdt.ls); the Kotlin server parses Java but answers no requests inside `.java` files. Set **Tools > Options > Kotlin > Java language server > JDT LS folder** to an extracted JDT LS (the folder with `plugins` and `config_win`), then reopen the folder. Its runtime must be Java 21+: the optional JAVA_HOME setting, else the Kotlin server JAVA_HOME, else the JetBrains Runtime bundled with the Kotlin server. Gradle is still run with the Gradle JDK setting. Without the folder, `.java` keeps coloring and debugging.

JDT LS imports the Gradle build itself. Vistudio passes it an init script that adds the Kotlin compiler's class output of each JVM source set to its classpath, so Java code that calls Kotlin resolves without false errors after a Gradle build. Eclipse metadata stays in `%LOCALAPPDATA%\Vistudio\jdtls-workspaces`, never in the project folder. Kotlin declarations seen from Java are compiled classes: F12 from Java into Kotlin source is not available; edits to Kotlin become visible to Java after the next build.

**Debugging** uses the existing F5 path. Set breakpoints in `.kt` and `.java` files, start from a Kotlin or Java `main` (launch.vs.json `project` may be a `.java` file, `mainClass` the Java class), and step across the language boundary. Call Stack shows each frame's language (Kotlin or Java). Watch and Immediate evaluate Java expressions in Java frames; their completion offers the frame's locals, arguments and fields as Java names (Kotlin-only semantic suggestions stay in Kotlin frames).

Kotlin/JS, Kotlin/Wasm and Kotlin/Native debugging are approved as later phases in [the roadmap](roadmap.md).

## Compose Multiplatform previews

**Tools > Kotlin > Compose Preview** opens the **Kotlin Compose Preview** window. It follows the active `.kt` file, lists its `@Preview` functions (`androidx.compose.ui.tooling.preview.Preview` or any other annotation named `Preview`) and shows them as images. The window renders when a file is first shown and after each save of that file (**Refresh on save**); **Refresh** saves and renders again, **Cancel** stops it. Click a preview's name to go to its function.

Rendering stays on Gradle and the module's own Compose version. A temporary init script compiles the module's JVM (desktop) target, then a small renderer runs in a separate JVM on that target's runtime classpath and draws each preview with Compose's `ImageComposeScene`. The extension ships no Compose or Skia binaries. Supported `@Preview` arguments: `name`, `widthDp`, `heightDp`, `showBackground`, `backgroundColor`; without a size the image wraps the content. Previews are drawn at 2× and shown at their dp size.

Requirements and limits:

- The file must be compiled by a JVM target of its module (`commonMain`, `jvmMain`, `desktopMain`, ...) with `compose.desktop.currentOs` on that target's runtime classpath. Previews only in `androidMain` or a module without a JVM target are reported in the window.
- Only top-level, parameterless preview functions are rendered. Previews nested in classes or objects, with a receiver, or with `@PreviewParameter` are listed as unsupported.
- An exception thrown by a preview is shown under its name; the other previews of the file still render. Compile errors and Gradle failures are shown at the top of the window, with the full log in **Output > Kotlin**.
- Preview runs share the Gradle gate with builds: the window waits while another Gradle operation runs. Images are written to `build/vistudio/compose-preview` under the Gradle root.
- Modules without Compose have no previews; the window says so.

The [Compose Multiplatform sample](samples/compose-multiplatform) has a Compose module (desktop JVM and Wasm targets) and a plain Kotlin Multiplatform module. See [the performed checks](docs/compose-preview-validation.md).

## CodeLens

Kotlin (`.kt`) and Java (`.java`) files show CodeLens above declarations, as C# does:

- **N references** above classes, interfaces, objects, functions, properties and constructors. Click to run **Find All References** on the name.
- **N implementations** above interfaces, abstract/open/sealed classes and abstract/open (or interface) members. Click to run **Go To Implementation**.
- **Run | Debug** above a JVM entry point (Kotlin top-level `fun main`, Java `public static void main`): the same Gradle build and launch as Ctrl+F5 / F5 on that file.
- **overrides Base.member** above a Kotlin `override` or a Java `@Override` member. Click to go to the overridden member. A base in a library shows its type only (it is not opened).
- **N overrides** instead of "N implementations" above a Kotlin `open` member with a body.
- **N actuals (js, jvm, …)** above a Kotlin Multiplatform `expect` declaration (click: **Go To Implementation**) and **expect in commonMain** above an `actual` one (click: open the `expect`).
- **Show preview** above a Compose `@Preview` function: opens the Kotlin Compose Preview window for the file.
- **Run task** above a task declared in a `build.gradle.kts` (`tasks.register("x")`, `tasks.named`, `tasks.create`, `task("x")`, `val x by tasks.registering`, `register("x")` in `tasks { }`) and **Run application** above `application { }`: runs `:project:task` with Gradle, like Kotlin > Gradle Tasks.

Counts and override bases come from the language server that owns the file: the Kotlin language server for `.kt`, Eclipse JDT LS for `.java` (Tools > Options > Kotlin). Until the server has finished initialization and Gradle import the lens shows `- references` and fills in by itself. Counts refresh a few seconds after edits. A Java declaration's count includes only Java usages, because JDT LS does not see Kotlin sources; a Kotlin declaration's count comes from the Kotlin server.

Lenses are VisualStudio.Extensibility parts hosted in-process next to the VSSDK package. Turn them on or off with **Tools > Options > Text Editor > All Languages > CodeLens**. See [the performed checks](docs/codelens-validation.md).

## Inlay hints

Kotlin (`.kt`, `.kts`) and Java (`.java`) editors show inlay hints from the language servers, drawn by Visual Studio's own LSP client:

- Kotlin: property, local variable, function return and lambda parameter types; lambda return expressions (`^map`); implicit `it: Int` / `this: Ledger` in multi-line lambdas; range bounds (`≤`, `<`, `≥`); day-based `kotlin.time` durations; parameter names (`count =`), including calls into compiled code, context parameters and methods IntelliJ excludes as obvious; result types after each line of a call chain.
- Java (needs the JDT LS folder): parameter names, `var` types (when the initializer does not spell the type) and lambda parameter types, plus format-string arguments.

All kinds are on by default. **Tools > Options > Kotlin > Inlay hints** sets when they show (`Always` by default; `WhileHoldingAltF1` is Visual Studio's own default; `Never`), and the **Inlay hints (Kotlin)** and **Inlay hints (Java)** categories turn single kinds off. Kotlin changes apply to the next hint request; Java changes need the folder reopened.

Colors: **Fonts and Colors > Text Editor > Inlay Hints - Types / Inlay Hints - Parameters** (Visual Studio's own items). Visual Studio 18 files its Dark-theme colors for them under the Output Window, so editors on Dark and Dark-based themes showed light-grey hint boxes; `Registration/InlayHintColors.pkgdef` adds them to the editor's category (`#A9A8A7` on `#3E3E3E`, the C# inline hint colors; `#C7C7C7` on `#333333` for Dark (Extra Contrast)). Colors you set yourself still win.

The Kotlin server reads its hint options through `workspace/configuration` (section `jetbrains.kotlin`) before each request and returns nothing without them. Visual Studio answers that request only from Open Folder workspace settings, so Vistudio answers it on the server stream (`LspConfigurationReader`) and leaves every other message untouched. `tests/integration/verify-inlay-hints.py` checks every kind against both real servers on `samples/kotlin-java-jvm` without writing to disk.

## Sticky Scroll

Kotlin (`.kt`, `.kts`) and Java (`.java`) editors support Visual Studio's Sticky Scroll: while you scroll, the headers of the enclosing scopes (class, function, accessor, `if`/`when`/`switch` branch, loop, `try`/`catch`, lambda) stay pinned at the top of the editor, and clicking one jumps to it. A header that spans lines starts at the declaration (`data class Box(` … `) : Base {`), not at the line with the brace, and leading annotations are left out. The same block structure draws the structure guide lines.

The scopes come from a lexical scanner in the extension (`CodeStructureScanner`), so they work before the language server starts and in broken code. Folding stays with the language servers' folding ranges. Turn Sticky Scroll on or off and set its line count with **Tools > Options > Text Editor > General > Sticky Scroll**.

## CPU and memory while debugging

Visual Studio's **Diagnostic Tools** window (Debug > Windows > Show Diagnostic Tools) accepts only native and .NET debug engines; Visual Studio 18 checks that against a fixed list, with no extension point. What each language gets on F5:

| Language | F5 session | CPU, memory, snapshots |
| --- | --- | --- |
| Rust, Zig, C, C++ | Native engine (`DebugProgram` of the project) | Visual Studio's Diagnostic Tools window |
| Kotlin (JVM target), Java | Kotlin/JVM Debug Adapter Host engine | **Vistudio Diagnostics** window; heap and GC from `jstat`, snapshots from `jcmd GC.class_histogram` |
| Dart, Flutter | Dart Debug Adapter Host engine | **Vistudio Diagnostics** window; heap and snapshots from the Dart VM service |
| WebAssembly | No debugger: F5 runs the Run command | **Vistudio Diagnostics** window; the started process and its children |
| Kotlin JS, Wasm, Native targets | No debugger: F5 runs the target's Gradle run task | **Vistudio Diagnostics** window; the program Gradle starts (Node.js, or the mingwX64 executable), without Gradle's own processes |

The **Vistudio Diagnostics** window (View > Other Windows > Vistudio Diagnostics; it opens by itself, as a tab next to Diagnostic Tools, when such a session starts) samples the program once a second: CPU as a share of all processors, private bytes and working set, and the runtime's own heap line (used and capacity). Break periods are shaded in the charts. Events lists the start, debugger breaks and continues, garbage collections (JVM collection counts; a large drop of the Dart heap), snapshots and the end. **Take Snapshot** lists the heap by class and compares it with the previous snapshot (largest size change first); for WebAssembly a snapshot records process memory only. The data stays in the window after Stop Debugging.

How the program is found: the Kotlin server starts the JVM through Gradle, so Vistudio waits (up to five minutes, for the first build) for a new JVM that `jps -l` lists with the launched main class; `jstat`/`jcmd` come from that JVM's own JDK, then the configured JDK, `JAVA_HOME` and PATH (a JRE has no `jstat`: only process counters are shown then). For Dart and Flutter the debug adapter's `dart.debuggerUris` event gives the VM service, whose `getVM` names the program's process (`dartvm.exe`, or the Flutter app). Both work while the program is stopped at a breakpoint.

Checks: `Vistudio.Tests.exe --diagnostics` (unit checks; live checks against a JVM and a Dart VM when a JDK and a Dart SDK are found; `--diagnostics none` for unit checks only); `tests/verify-diagnostics.ps1` (Visual Studio through DTE and UI Automation). Results: [docs/diagnostics-validation.md](docs/diagnostics-validation.md).

## Runtime ETW events for the Performance Profiler

`src/Vistudio.Etw` (net472, no package dependencies) lets a language runtime emit profiling events that the Visual Studio Performance Profiler shows in the Events Viewer.

| Provider | Name | GUID | API |
| --- | --- | --- | --- |
| `RuntimeEtwProvider` | `Vistudio-Runtime` | `4f3c063f-5474-59d2-14b2-3d8687b788b9` | `EventRegister` / `EventSetInformation` / `EventWrite` through advapi32 |
| `RuntimeEventSource.Log` | `Vistudio-Runtime-Managed` | `e74c385b-8523-59ba-af56-0e6de7ae32b6` | `EventSource`, for a runtime hosted on .NET |

Both implement `IRuntimeEvents` and write the same events in the self-describing (TraceLogging) format, so no manifest has to be installed. GUIDs are the EventSource hash of the names, so tools that take a provider name resolve to the same GUID.

| Id | Event | Level | Keyword | Fields |
| --- | --- | --- | --- | --- |
| 1 | `SessionStart` (opcode Start) | Informational | `0x1` Session | `SessionId`, `RuntimeName`, `RuntimeVersion` |
| 2 | `SessionStop` (opcode Stop) | Informational | `0x1` Session | `SessionId`, `ExitCode` |
| 3 | `StackSample` | Verbose | `0x2` Sampling | `SessionId`, `ThreadId`, `Frames` (hex array, innermost first, at most 4096) |
| 4 | `Allocation` | Verbose | `0x4` Allocation | `SessionId`, `Address` (hex), `Size`, `TypeName` |

```csharp
using var events = new RuntimeEtwProvider();          // or IRuntimeEvents events = RuntimeEventSource.Log;
events.SessionStart(sessionId, "MyLang", "1.0");
if (events.IsEnabled(EventLevel.Verbose, RuntimeEventIds.SamplingKeyword)) events.StackSample(sessionId, threadId, frames);
```

Writes are skipped while no session enables the level and keyword, and they never throw (`LastWriteStatus` keeps the last Win32 status). To see the events: Debug > Performance Profiler, select Events Viewer, open its settings (gear), add the GUID under Additional Providers (keywords `0xFFFFFFFFFFFFFFFF`, level Verbose), then start or attach to the runtime process. `Vistudio.Tests.exe --etw-emit 60` is a ready-made emitter to attach to.

Checks: `Vistudio.Tests.exe --etw` (registration, descriptors, metadata and payload decoding, enable filtering, failures, real advapi32 calls, EventListener for the EventSource); `tests/verify-etw.ps1` traces both GUIDs with `logman` and decodes them with `tracerpt` (needs elevation or Performance Log Users).

## Zig

Zig support lives in its own package (`Vistudio.Zig.ZigPackage`, `src/Vistudio.Vsix/Zig`, `src/Vistudio.Core/Zig`) with its own settings file, Output pane, Error List providers and menus, so Kotlin and Java behavior is unchanged. Open a Zig project with **File > Open > Folder** (the folder with `build.zig`); `samples/zig-hello` is a ready-made one. Nothing Zig-related is bundled: zig.exe and ZLS are the user's own installs.

**Tools > Options > Zig > General**: zig.exe path (empty searches PATH; the version from `zig env` is logged to the Zig Output pane), standard library folder (`--zig-lib-dir`, also ZLS `zig_lib_path`), default flags for Build File and Run, `zig build` flags and step, ast-check on save / while typing with the typing delay, diagnostic output (errors only, or errors plus each `note:` as its own Error List message), ZLS path and arguments, ZLS diagnostics, CodeLens.

| Feature | How it works |
| --- | --- |
| Coloring | `.zig` and `.zon` use the `zig` content type and `Grammars/Zig.tmLanguage.json`: keywords (`pub`, `fn`, `const`, `var`, `struct`, `enum`, `union`, `error`, `try`, `catch`, `comptime`, `defer`, ...), `@` builtins (`@import`, `@export`, ...), primitive and named types, numbers, strings, multiline `\\` strings, doc comments. |
| IntelliSense and navigation | A user-installed [ZLS](https://github.com/zigtools/zls) matching the Zig version, through the Visual Studio LSP client: completion (keywords, builtins, std and module members from the `@import` / `build.zig` graph ZLS resolves), member completion, signature help, Go To Definition, Find All References, Go To Symbol, hover. Vistudio writes `%LOCALAPPDATA%\Vistudio\zls\zls.json` (zig.exe, lib folder, no build-on-save, no snippets) and starts ZLS with `--config-path`. ZLS restarts when the settings are saved or with **Tools > Zig > Restart Language Server**. |
| Diagnostics | `zig ast-check --color off` on open, on save and after typing pauses, on the editor text through stdin (unsaved edits included). Zig has no JSON diagnostic format; `path:line:col: error|warning|note: message` lines are parsed, source and caret lines skipped, notes attached to their error. Squiggles track edits; the Error List shows "Zig ast-check" entries. `zig build` is never run for this. While ast-check is on, ZLS's own (duplicate) diagnostics are hidden unless **ZLS diagnostics** is set. |
| CodeLens | Above every function: "N references" (ZLS `textDocument/references`; click runs Find All References) and the signature (`pub fn area(self: Rect) u32`; click goes to the declaration). Counts refresh after edits, once ZLS is ready, and in steps after ZLS reports "Loaded build file" (references from importing files appear only a few seconds after that). |
| Sticky Scroll | Function, container, test and control-flow blocks from the lexical `ZigSourceScanner`. Visual Studio's Sticky Scroll must be on (**Tools > Options > Text Editor > General > Sticky Scroll**); a fresh profile has it off. |
| Build and run | **Tools > Zig** and the **Zig** toolbar: Build Project (`zig build <step>`, the step chosen in the toolbar drop-down), Build File (`zig build-obj` of the active file into `zig-out/vistudio`), Run File (`zig run`), Cancel, Refresh Build Steps. Steps come from `zig build -l`, with a static `build.zig` scan (`b.step(...)`, artifacts) while that is unavailable. Build File, Run File and Run ast-check are also in the code editor context menu of Zig files and appear as soon as a Zig editor is active (the Zig package then loads in the background). Output goes to the Zig pane with locations rewritten to `path(line,col): error: ...` (double-click navigates); errors go to the "Zig build" Error List provider (an error ast-check already lists is not repeated). Commands have canonical names for key bindings and the Command Window: `Zig.BuildProject`, `Zig.BuildFile`, `Zig.RunFile`, `Zig.Cancel`, `Zig.RefreshBuildSteps`, `Zig.AstCheck`, `Zig.RestartLanguageServer`, `Zig.Settings`, and `Zig.BuildStep <step>` sets the drop-down. |

Checks: `Vistudio.Tests.exe --zig` covers settings, `zig env` (ZON and JSON, relative paths), flag splitting and quoting, the diagnostic parser on real Zig 0.16 output, note levels, build step listing and the `build.zig` scan, the source scanner (signatures, containers, blocks), the grammar, command lines and the ZLS configuration, then runs live checks against a real zig.exe (`VISTUDIO_ZIG`, PATH or the mise install): `zig env`, ast-check on unsaved text, `zig build -l`, `zig run`, `zig build-obj` and a located `zig build` error in a copy of `samples/zig-hello`; with a ZLS install (`VISTUDIO_ZLS` or mise) it also checks completion, signature help, definition, references, symbols and hover against the real server.

## Dart and Flutter

Dart support lives in its own package (`Vistudio.Dart.DartPackage`, `src/Vistudio.Vsix/Dart`, `src/Vistudio.Core/Dart`) with its own settings file (`%LOCALAPPDATA%\Vistudio\dart-settings.xml`), Output pane ("Dart"), Error List providers, debug engine and menus, so Kotlin, Java and Zig behavior is unchanged. Open a Dart or Flutter package with **File > Open > Folder** (the folder with `pubspec.yaml`); `samples/dart-console` (pure Dart with `package:test` tests) and `samples/flutter-app` (counter app with a widget test and an integration test) are ready-made ones. Nothing is bundled: the Flutter SDK (which contains the Dart SDK) or a Dart SDK is the user's own install. A package whose `pubspec.yaml` depends on `sdk: flutter` runs, tests and debugs through `flutter.bat`; any other package through `dart.exe`. Flutter's Windows desktop device needs the Visual Studio workload "Desktop development with C++".

**Tools > Options > Dart > General** (or `vistudio.dart.<property>` in the workspace's `.vs\settings.json`, for example `"vistudio.dart.flutterSdkPath": "C:/tools/flutter"`): Flutter SDK folder, Dart SDK folder (both empty: PATH), Flutter device (`windows` by default; also the drop-down on the **Dart** toolbar), hot reload on save, `flutter run` arguments, program arguments, test arguments, debugging of SDK and package libraries, analysis server on/off and arguments, CodeLens. `flutter.bat` runs through `cmd.exe`, so its arguments are each quoted and `%`, `"` and line breaks are rejected.

| Feature | How it works |
| --- | --- |
| Editor | `.dart` uses the `dart` content type (code editor association in `Registration/DartFiles.pkgdef`) and `Grammars/Dart.tmLanguage.json`, written for this project: keywords, declarations (`class`, `mixin`, `enum`, `extension`, `extension type`, `typedef`), modifiers, annotations, built-in types and capitalized type names, numbers, single/double/triple-quoted and raw strings with `$name` / `${expr}` interpolation and escapes, line, block (nested) and documentation comments with `[references]`. |
| IntelliSense and navigation | The SDK's analysis server (`dart language-server --protocol=lsp`) through the Visual Studio LSP client: completion (including members and unimported libraries), signature help, hover, Go To Definition, Find All References, document symbols. The whole workspace is analyzed. `workspace/configuration` is not advertised (Visual Studio would answer it with nulls) and snippets are off (Visual Studio inserts snippet text literally). **Tools > Dart > Restart Analysis Server** restarts it; it also restarts when settings change. |
| Error reporting | Analysis server errors, warnings and lints for open and unopened files (unsaved edits included) as squiggles and Error List entries. Besides that: "Dart run" (compiler errors from `dart run`, `flutter run` and hot reload, `path:line:col: Error:` lines, rewritten in the Dart pane to `path(line,col): error:` for double-click navigation), "Dart tests" (each failed test at its `test(` call with the first failure line, plus compile errors of test files that did not load) and "Dart analyze" (**Tools > Dart > Analyze Package**, `dart analyze --format=machine`). |
| Run | **Tools > Dart > Run** (toolbar, editor context menu): Dart packages run the active file when it declares `main`, otherwise `bin\<package>.dart` / `bin\main.dart` (`dart run`); Flutter packages run `lib\main.dart` (or the active file with `main`) with `flutter run --machine -d <device>`. Output goes to the Dart pane. **Stop App** ends it (`app.stop`, then the process tree). |
| Flutter hot reload | While `flutter run` is running: **Flutter: Hot Reload** (keeps state) and **Flutter: Hot Restart** send `app.restart` over the Flutter daemon protocol on stdin; with **Hot reload on save** (default on) saving a `.dart` file of the app's package reloads it (several saves within 300 ms reload once). Reload failures and compile errors are reported in the Dart pane and the Error List. **Flutter: List Devices** runs `flutter devices --machine`. |
| Debugging | F5 / Ctrl+F5 on a `.dart` startup item, a `launch.vs.json` entry of type `dart` (`program`, `args`, `toolArgs`, `deviceId`, `cwd`, `env`), **Tools > Dart > Debug**, or the CodeLens. A dedicated engine ("Dart / Flutter", `Registration/DartDebugAdapter.pkgdef`) starts the SDK's own DAP server (`dart debug_adapter` or `flutter debug_adapter`, `--test` for tests) through the Debug Adapter Host: breakpoints, call stack, Locals, Watch/Immediate evaluation, stepping, program output in Debug Output. `pub get` runs first when `.dart_tool\package_config.json` is missing. In a Flutter debug session the hot reload commands and hot reload on save send the adapter's `hotReload` / `hotRestart` requests; Vistudio places them between the debugger's own frames and consumes their responses, so the debugger never sees them. |
| Tests | **Run Unit Tests** (`dart test --reporter json`, or `flutter test --machine` for Flutter packages), **Run Tests in File** and **Debug Tests in File** (on a `*_test.dart` or `integration_test` file), **Run Integration Tests** (`flutter test integration_test -d <device> --machine`). The Dart pane shows `PASS`/`FAIL` per test with errors, stack traces and printed output, then a summary; failures go to the Error List. |
| CodeLens | Above `main`: **Run**, **Debug** and "N references". Above `test`, `testWidgets` and `group` calls: **Run test(s)** / **Debug test(s)** for that test by its full name (`--plain-name`; a name with interpolation runs the whole file). Above other functions, methods and types: "N references" from the analysis server (click: Find All References), refreshed after edits and once the server is ready. Declarations come from a lexical scanner that masks comments and strings (`DartSourceScanner`). |
| Flutter projects | **Tools > Dart > New Flutter App...** (`flutter create` into a new folder, then opens it), **Pub Get**, Flutter package detection from `pubspec.yaml`, device selection, Windows/web/emulator devices as `flutter devices` reports them. |

Commands have canonical names for key bindings and the Command Window: `Dart.Run`, `Dart.Debug`, `Dart.Stop`, `Dart.HotReload`, `Dart.HotRestart`, `Dart.ListDevices`, `Dart.RunTests`, `Dart.RunTestFile`, `Dart.DebugTestFile`, `Dart.RunIntegrationTests`, `Dart.PubGet`, `Dart.Analyze`, `Dart.Cancel`, `Dart.NewFlutterApp`, `Dart.RestartAnalysisServer`, `Dart.Settings`, and `Dart.Device <id>` sets the device.

Checks: `Vistudio.Tests.exe --dart` (unit checks also in the default run) covers arguments and `cmd.exe` quoting, SDK resolution, package detection, command lines, the analyzer/compiler parsers, the Flutter daemon protocol, the JSON test protocol, the source scanner, launch arguments, settings and `.vs\settings.json` overrides, the grammar and DAP framing, then runs live checks against a real SDK (`VISTUDIO_FLUTTER_SDK`, Tools > Options or PATH). `tests/verify-dart.ps1` installs the VSIX into an isolated root suffix and drives the IDE through DTE. What was run on which machine is in [docs/dart-validation.md](docs/dart-validation.md).

## Language projects and packages

Besides `.kotlinproj`, Visual Studio loads one project type per language (`Vistudio.Languages.LanguagesPackage`, `src/Vistudio.Vsix/Languages`, `src/Vistudio.Core/Languages`): `.dartproj`, `.zigproj`, `.rustproj`, `.cproj`, `.cppproj`, `.wasmproj` and `.javaproj`. Like `.kotlinproj` the file is IDE metadata in the project XML layout, never evaluated by MSBuild; the language's own tool builds. Create one with **File > New > Project** (filter on the language: Dart Console App, Zig Console App, Rust Console App, C/C++ Console App (CMake + Conan), WebAssembly Module (WAT), Java Console App (Gradle)) or open an existing file with **File > Open > Project/Solution**. The project folder is the file's folder. Open Folder keeps working for Dart and Zig without a project file.

| Feature | How it works |
| --- | --- |
| Solution Explorer | The project node (language icon), a **Packages** node for the language's package manager, then the folder as it is on disk; build output and tool caches (`target`, `zig-out`, `.zig-cache`, `.dart_tool`, `build`, `out`, `.gradle`, `node_modules`) are not listed. File additions, removals and manifest edits refresh the tree. |
| Settings (F4 on the project) | Build, Run, Test and Clean commands, Debug program and arguments, working directory, tool folders searched before PATH, and the package manager's path. Commands run without a shell: `&&` chains several, double quotes group an argument; pipes, redirection and `&` are rejected. Batch tools (`gradlew.bat`, `flutter.bat`) run through `cmd.exe` with every argument quoted. Every value is validated before the file is saved. |
| Build | Solution Build/Rebuild/Clean run the commands with output in the Build pane: `cargo build`, `zig build`, `dart analyze` (Flutter: `flutter build windows --debug`), `conan install` + `cmake -S . -B build` + `cmake --build build`, `wasm-tools parse`, `gradlew.bat build`. The project context menu adds **Run** and **Run Tests** (output in the "Vistudio Projects" pane) and **Cancel Run**. |
| F5 / Ctrl+F5 | Rust, Zig, C and C++: the Debug program (`target\debug\<name>.exe`, `zig-out\bin\<name>.exe`, `build\Debug\<name>.exe`) starts under Visual Studio's native debugger, which reads the PDBs of MSVC-target builds. Dart: the Dart/Flutter debugger. Java: the Kotlin/JVM debug session, entry from `src\main\java`. WebAssembly has no debugger here yet: F5 runs the Run command. |

**Bazel and build caches.** Each language project has a **Build system** setting: Native (the language's own tool) or Bazel. A project created in a Bazel workspace root (`MODULE.bazel`, `REPO.bazel`, `WORKSPACE`) starts as Bazel with `bazel build //...`, `bazel run //:<name>`, `bazel test //...`, `bazel clean` and, for Rust, Zig, C and C++, `bazel-bin\<name>.exe` as the debug program; `bazel` falls back to `bazelisk`. Switching the setting replaces only the commands that still hold the other system's defaults. **Build cache** (on by default) turns on each tool's own cache: Bazel `--disk_cache` (folder in Tools > Options > Bazel) and `--remote_cache`, Gradle `--build-cache` (Kotlin projects: Tools > Options > Kotlin > Gradle > Build cache), sccache for Cargo and CMake configure when installed; Zig, Dart/Flutter and wasm-tools cache by themselves. **Remote cache** takes an http(s)/grpc(s) URL for Bazel and a read-only Gradle HTTP cache. Flags already set on the command line, in `.bazelrc`, `gradle.properties` or `RUSTC_WRAPPER` win; the "Vistudio Projects" pane says what was added.

Templates create no build tool state: Rust gets `Cargo.toml` and `src\main.rs` with a test; Zig `build.zig` and `src\main.zig` with run and test steps (no `build.zig.zon`, whose fingerprint `zig init` generates); Dart `pubspec.yaml`, `bin`, `lib`, `test`; C/C++ `CMakeLists.txt`, `conanfile.txt` (CMakeDeps, CMakeToolchain) and a CTest test; WebAssembly a WASI command module in `src\main.wat`; Java a Gradle `application` with the validated Gradle 8.14.7 wrapper.

### Packages in Solution Explorer

Every project gets a Packages node for its language's own package manager; Kotlin projects get one per Gradle build file, above the Gradle model's Dependencies.

| Language | Manager | Manifest | Newer versions from | Add / Remove / Update |
| --- | --- | --- | --- | --- |
| Dart, Flutter | pub (`dart`) | `pubspec.yaml` (+ `pubspec.lock`) | `dart pub outdated --json --show-all` | `dart pub add` (`dev:` for Development), `dart pub remove`, `dart pub upgrade` (`--major-versions` for latest) |
| Rust | Cargo | `Cargo.toml` (+ `Cargo.lock`) | `cargo update --dry-run --verbose` (Cargo 1.78+ notes "available") | `cargo add` (`--dev`, `--build`), `cargo remove`, `cargo update`, latest: `cargo add name@<latest>` |
| Kotlin, Java | Gradle (wrapper) | `build.gradle.kts` / `build.gradle`, `gradle/libs.versions.toml` | Init script `Build/OutdatedDependencies.gradle`: Gradle resolves the newest release of each declared module with the project's repositories | Edits of the build file or the version catalog (the Gradle model import picks them up) |
| C, C++ | Conan 2 | `conanfile.txt` (`conanfile.py` read-only) | `conan graph outdated --format=json` | `[requires]` edits, then `conan install --build=missing`; `--update` within ranges |
| Zig | `zig fetch` | `build.zig.zon` | `git ls-remote --tags` of dependencies on GitHub, Codeberg or GitLab pinned to a version tag | `zig fetch --save=<name> <url>`, removal by a manifest edit, latest by the tag in the URL |
| WebAssembly | wkg | WIT files (`import`/`use`/`include`), `wkg.lock` | wkg has no version listing: none | `@version` edits in the WIT files, then `wkg wit fetch`; Add and Remove are by hand |

A package node reads `name current → newer` when a newer version exists (icon: update available) and the Packages node counts them. Language projects check once in the background when they open; Kotlin projects check on **Check for Updates** (each check is a Gradle run). Context menu of a Packages node: **Add Package...**, **Check for Updates**, **Update All (within constraints)**, **Update All to Latest (N)**, **Open** the manifest. Of a package: **Update** / **Update to <version>**, **Update to Latest (<version>)**, **Go to Declaration**, **Remove** (asks first). Actions run one at a time with their commands and results in the "Packages" Output pane; edited manifests keep their encoding and BOM, and the versions are checked again afterwards. Names, versions and URLs are validated before they reach a command line or a manifest. Canonical command names: `Vistudio.AddPackage`, `Vistudio.CheckPackageUpdates`, `Vistudio.UpdateAllPackages`, `Vistudio.UpdateAllPackagesToLatest`, `Vistudio.OpenPackageManifest`, `Vistudio.UpdatePackage`, `Vistudio.UpdatePackageToLatest`, `Vistudio.GoToPackageDeclaration`, `Vistudio.RemovePackage`, `Vistudio.RunLanguageProject`, `Vistudio.TestLanguageProject`, `Vistudio.CancelLanguageProject`.

Checks: `Vistudio.Tests.exe --packages` (language catalog and icons, project files, commands, templates, every manager's manifest reader, report parser and actions; then live runs against the managers found on the machine; `--packages none` skips those) and `tests/verify-language-projects.ps1` (IDE through DTE). Results: [docs/language-projects-validation.md](docs/language-projects-validation.md).

## Bazel files

BUILD, BUILD.bazel, MODULE.bazel, WORKSPACE(.bazel), REPO.bazel and `.bzl` files open as `starlark` documents (`src/Vistudio.Vsix/Bazel`, `src/Vistudio.Core/Bazel`) with Vistudio's Starlark grammar and the Bazel icon.

| Feature | How it works |
| --- | --- |
| Completion | Vistudio's own, from the files on disk (no Bazel needed): files and folders of the package in `srcs`, `hdrs`, `data` and `glob()`; `//` packages and folders, `//pkg:` targets and files, `:targets`, `@repositories` from MODULE.bazel (`repo_name`, `use_repo`) or WORKSPACE; `.bzl` labels and their public symbols in `load()`; `//visibility:` values; rules and functions for the file kind (BUILD, MODULE.bazel, .bzl) and attribute names inside a call. Opens on `"`, `/`, `:`, `@` and letters; typed characters never commit, Tab/Enter do. |
| Validation | Vistudio's checks on open, save and while typing: unterminated strings, brackets, Python-only statements, `def` outside .bzl, `load()` (label, .bzl, private symbols), duplicate targets, invalid labels, missing files, packages and (in Bzlmod-only workspaces) repositories, files of subpackages. buildifier `--lint=warn` on open and save when installed. Squiggles plus "Bazel" Error List entries; unknown targets are suggestions because macros can create them. |
| Language server | starpls or bazel-lsp (user-installed, PATH or Tools > Options > Bazel) adds hover, Go To Definition and its own diagnostics; while it runs, rule and attribute names come from it. |
| Settings | Tools > Options > Bazel, overridden per window by `vistudio.bazel.*` keys in `.vs\settings.json`. |

Checks: `Vistudio.Tests.exe --bazel`. Results and what is not verified yet: [docs/bazel-validation.md](docs/bazel-validation.md).

## mise

[mise](https://mise.jdx.dev) tool versions live in their own package (`Vistudio.Mise.MisePackage`, `src/Vistudio.Vsix/Mise`, `src/Vistudio.Core/Mise`) with its own settings file, Output pane ("Mise") and menu. mise is the user's own install (PATH, the default install folders, or **Tools > Options > Mise > General**); nothing is bundled. All versions come from mise itself (`mise ls --current --json`, `mise outdated --json`, `mise outdated --bump --json`), run in the project folder; a line scanner only places them on the config's tool lines.

Recognized configs: `mise.toml`, `.mise.toml`, `mise.local.toml`, `mise.<env>.toml`, `.config/mise.toml`, `mise/config.toml`, `.mise/config.toml`, `.config/mise/config.toml`, `.config/mise/conf.d/*.toml` and `.tool-versions`. Workspace detection looks at the Open Folder location or the solution folder (not its sub-folders).

| Feature | How it works |
| --- | --- |
| Install check | When a folder or solution opens, the package (background auto-load) checks the workspace's configs. Tools whose configured version is not installed are reported as "not installed". |
| Inline hints | After each tool line of an open config: `⚠ not installed (1.12.1) · latest 1.13.2`, `↑ 26.10.0 → 26.13.1 · latest 27.0.2` (in-range update, then the latest version when it is newer), or `✓ 4.4.4`. A tool defined here but overridden elsewhere shows where it comes from. The tooltip lists requested, installed, in-range and latest versions, the value `--bump` writes, the config and the release page. Colors: **Fonts and Colors > Mise - Not installed / Update available / Up to date**. |
| CodeLens | Above `[tools]` (the first tool line of `.tool-versions`): status (`mise: 1 not installed · 2 updates`; click installs the missing tools, or checks again), **Upgrade all (N)** and **Upgrade all to latest (N)**. |
| Info bar | Shown when the workspace has missing tools, updates, an untrusted config, or no mise.exe: **Install missing**, **Upgrade all**, **Upgrade all to latest**, **Trust**, **Open mise.toml**. Closing it hides it until the workspace changes. |
| Commands | **Tools > Mise**: Check Tools, Install Missing (`mise install`), Upgrade All (`mise upgrade`: newest versions inside the requested ones, the config is unchanged), Upgrade All to Latest (`mise upgrade --bump`: newest versions overall, the requested versions in the config are rewritten), Trust Config (`mise trust`), Open Config, Settings. The first four are also in the code editor context menu of mise configs. With a mise config active they apply to that file, otherwise to the workspace's configs. |

Actions always name the tools of the file or workspace (`mise upgrade node ninja`), never a bare `mise upgrade`, which would also upgrade the user's global tools. One mise command runs at a time; output goes to the Mise pane and every hint, lens and the info bar refresh afterwards. Update checks query remote versions, so they are cached (**Update check cache**, 30 minutes by default); saving a config, Check Tools and every action check again. Vistudio never passes `--yes` or `MISE_YES`: mise would then answer its own prompts, including whether to trust a config. An untrusted config is reported, and only **Trust** runs `mise trust`.

Checks: `Vistudio.Tests.exe --mise` (also in the default run without the live part) covers config names and workspace detection, the tool locator on TOML (`[tools]`, quoted keys, inline tables, `[tools.<name>]`, dotted keys) and `.tool-versions`, the report merge (in-range and latest updates, missing tools, several versions, legacy array output, update-check failure), untrusted-config and error messages, command lines and quoting, and settings; then live checks against the installed mise.exe in a temp folder: the untrusted config is reported, versions are read with the folder trusted only through `MISE_TRUSTED_CONFIG_PATHS`, and the folder is still untrusted afterwards (no install runs: `mise install`, even `--dry-run`, records such configs in mise's trust store).

## Design and validation

- [Architecture, SDK extension points, and acceptance criteria](docs/architecture.md)
- [Illustrative C# integration fragments](docs/csharp-fragments.md)
- [Validation and test installation](docs/validation.md)

The architecture documents describe the larger intended feature set. This README describes the implementation shipped in version 0.1.0.


