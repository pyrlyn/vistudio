# Kotlin Multiplatform acceptance sample

Uses the same Kotlin 2.4.21 and checksum-pinned Gradle 8.14.7 as the JVM sample. Gradle runs on the project JDK; the C# extension is built separately by MSBuild. No test framework is added: the shared executable contains assertions and prints `VISTUDIO_KMP_PASS` only after they succeed.

Open this directory with Visual Studio's **File > Open > Folder**. Use **Tools > Kotlin > Gradle targets and tasks...**, filter by target or task name, select a task, then **Run selected**. The task list comes from Gradle, including qualified paths for subprojects. The same workflow works with custom target names and user-defined tasks. Discovery configures the project but does not compile or run it.

| Target | Build | Run and self-test |
| --- | --- | --- |
| JVM | `jvmJar` | `verifyJvm` / `jvmRun` |
| Native / Windows x64 | `linkDebugExecutableMingwX64` | `verifyNative` / `runDebugExecutableMingwX64` |
| JS / Node | `jsJar` | `verifyJs` / `jsNodeDevelopmentRun` |
| Wasm / Node | `wasmJsJar` | `verifyWasm` / `wasmJsNodeDevelopmentRun` |

`commonMain` declares `expect fun platformName()`. Its `actual` implementations live in `jvmMain`, `mingwX64Main`, and in `jsMain`/`wasmJsMain` through the shared `webMain` source set of Kotlin's default hierarchy, so each target has its own source set and the program prints `Platform: JVM …`, `Native mingwX64`, `Web/JS` or `Web/Wasm`.

As a `.kotlinproj` (Tools > Kotlin > Create or Open .kotlinproj, then Import Gradle Model), Solution Explorer shows `commonMain` and `webMain`, a `Targets` node with `jvm`, `js`, `wasmJs` and `mingwX64`, each with its own source set, dependencies and Build/Run/Test commands, and a **Startup target** project property for Run with Gradle and Ctrl+F5. See [Kotlin targets](../../docs/project-file.md#kotlin-targets).

`build` builds all configured targets; `verifyTargets` executes the assertions on all four runtimes. Run these in separate invocations: Kotlin's development and production JS/Wasm tasks can share output paths, and combining them in one invocation triggers Gradle's implicit-dependency validation. `allTests` is the KMP aggregate for projects with ordinary test source sets; this sample uses executable self-tests instead.

For repeat use, put the desired task path in **Tools > Kotlin > Settings > Gradle > Run tasks**, then use **Tools > Kotlin > Run with Gradle**. Build and Test have independent task settings. **Tools > Kotlin > Cancel Gradle** cancels a build, discovery, run or development server. Output appears in the Kotlin pane. Standard input is closed; interactive console programs need an external terminal.

Native support is constrained by Kotlin's host toolchains. This Windows fixture builds and runs `mingwX64`; Apple targets require their supported macOS/Xcode host. The extension does not emulate another host. Browser, Android and other project tasks can be selected when present, but those environments are not covered by this fixture. F5 continues to use the existing **JVM** debug adapter; Native/JS/Wasm launches here are runs without that debugger.

## Automated acceptance

From the extension root:

```powershell
./build.ps1
./tests/test-multiplatform.ps1
./tests/test-kotlin-targets.ps1
```

`test-kotlin-targets.ps1` imports this sample through the extension's importer into a `.kotlinproj` under `artifacts/kotlin-targets/`, checks the recorded targets, source-set hierarchy, tree and startup-target behavior, then builds and runs each target with its imported Gradle tasks.

The integration executable uses the same `GradleCommand`, `OwnedProcess`, task catalog and diagnostic parser as the extension. It discovers actual tasks, builds the project, runs four runtime self-tests, inserts a uniquely named temporary erroneous common source, verifies each compiler's nonzero exit and located diagnostic, removes that file in `finally`, then checks successful recovery on all runtimes. Do not edit this sample during the test. Logs are written to `artifacts/multiplatform-tests/`.

The first run downloads Kotlin/Native, LLVM, Node and Kotlin's generated npm tool dependencies. No dependency versions in the existing JVM sample are changed. On Windows machines whose HTTPS interception certificate is trusted by Windows but not by Java/Node, the optional `-UseWindowsTrustStore` flag uses the system CA store for that test process only. It restores the original environment and does not disable certificate checks.

Sources: [Kotlin Multiplatform Gradle DSL](https://kotlinlang.org/docs/multiplatform/multiplatform-dsl-reference.html), [build and run KMP applications](https://kotlinlang.org/docs/multiplatform/build-and-run-kmp.html).
