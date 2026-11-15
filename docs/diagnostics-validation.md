# CPU and memory while debugging: validation (T71)

What was checked, how, and what was not. Machine: Windows 11 on ARM64 (Parallels), Visual Studio 18 Community (ARM64), Eclipse Temurin JDK 21.0.12, Flutter 3.50.0 with Dart 3.14.0. Not installed here: cargo, zig, wasmtime, wasm-tools. Kotlin/Java F5 through the Kotlin server was not run in Visual Studio (section 5).

## 1. Probe: which sessions Visual Studio's Diagnostic Tools accepts

Method: IL of `Common7\IDE\CommonExtensions\Platform\DiagnosticsHub\Microsoft.DiagnosticsHub.VisualStudio.Package.dll` and `...\Platform\Debugger\VsDebugPresentationPackage.dll` (disassembled with the Windows SDK's `ildasm`), plus the package's string resources.

- `PerformanceDebuggerSessionManager.OnDebugProcessCreated` starts the Diagnostic Tools session only if `PerformanceDebuggerUtilities.IsPerfDebuggerScenario(engineIds)` is true. That method returns false for a "bad-listed" engine (COM+ legacy, Script, SQL, Silverlight) and otherwise requires one engine from `IsDebugEngineGoodlisted`: `NativeOnly`, `ManagedOnly` (v2, v4), `COMPlusNewArch`, `ManagedAndNative`, `CoreSystemClr`, and the CLR native compilation engine. The list is code, not registry or MEF data.
- Any other engine gets the resource text "The Diagnostic Tools window does not support debugging using the '{0}' debug engine." (`PerformanceDebuggerDebugEngineNotSupported`), filled with the engine name.
- The tools inside the window are MEF exports whose metadata can only narrow the engines (`SupportedDebugEngineGuids`; the Events tool lists native and the managed engines; tools without the key accept whatever the session accepted).

Consequences per language:

| Language | F5 path in Vistudio | Engine | Diagnostic Tools |
| --- | --- | --- | --- |
| Rust, Zig, C, C++ | `LanguageProjectConfig.LaunchNative` (`DebugProgram`) | `NativeOnly` | Accepted (on the list) |
| Kotlin (JVM), Java | `KotlinDebugCoordinator` → Debug Adapter Host | `7b672d65-...` "Kotlin/JVM (Gradle)" | Refused |
| Dart, Flutter | `DartDebugCoordinator` → Debug Adapter Host | `c4a2e6f8-...` "Dart / Flutter" | Refused |
| WebAssembly | F5 runs the Run command (no debugger) | none | No session |
| Kotlin JS/Wasm/Native targets | F5 runs the target's Gradle run task (creator decision 2026-11-15, replacing T21's message) | none | No session; Vistudio Diagnostics watches the program |

## 2. Unit checks

`Vistudio.Tests.exe --diagnostics none` and the default run (`Vistudio.Tests.exe` with no switch, which includes them). Covered: process tree with reused process ids, CPU % arithmetic (baseline per process id and creation time, cap at 100, processors), byte formatting, timeline capacity, `jps -l` lines, `jstat -gc` rows of JDK 21 (running and suspended, "-" values) and JDK 8 (no CGC columns), `jcmd GC.class_histogram` (module suffix, Total line, error output), JDK tool lookup order (debuggee's `java.exe`, configured JDK, `JAVA_HOME`, PATH), JVM discovery (old JVMs ignored, timeout), VM service address forms, `getVM` / `getMemoryUsage` / `getAllocationProfile` replies, snapshot differences, and the monitor (break marks, GC events from counts and from Dart heap drops, a failing runtime noted once, snapshots with and without a runtime, end on exit, single stop, the background loop).

## 3. Live checks outside Visual Studio

`Vistudio.Tests.exe --diagnostics` with `VISTUDIO_FLUTTER_SDK=C:\Users\listepo\tools\flutter`: **PASS, 66 assertions** (2026-11-15). Measured values from that run:

- Process tree (stand-in for `wasmtime run`: `cmd.exe` starting a busy PowerShell loop through `OwnedProcess.RunAsync` with the new started callback): 3 processes counted, max CPU 16.4 %, the session ended when the program exited.
- JVM (`java -agentlib:jdwp=...,suspend=n` running a small allocating program): found by `jps -l` in about 0.6 s, CPU and memory sampled, `jstat` heap 206.4 MB of 514.0 MB with 18 collections, garbage collections listed as events, `jcmd` snapshot lists `VistudioSpin$Shape` and `[B`, the session ended when the JVM was stopped.
- JVM held by JDWP (`suspend=y`, every Java thread suspended, as at a breakpoint): `jstat` still reports the heap and `jcmd GC.class_histogram` still lists 211 classes.
- Dart VM of `dart run --enable-vm-service=0 --pause-isolates-on-start` (isolate paused): heap 5.7 MB and process memory sampled, allocation profile with 74 classes, end on exit.
- Dart through the SDK's debug adapter (`dart debug_adapter`, as the Visual Studio F5 session runs it): the adapter sends `dart.debuggerUris` with `vmServiceUri` (`ws://127.0.0.1:<port>/<token>=/ws`); `getVM` names the program's process, which Dart 3.14 runs as `dartvm.exe`; CPU (max 14.0 %) and Dart heap (23.4 MB) sampled; the snapshot lists the program's `Shape` objects; the session ended with the adapter.

`Vistudio.Tests.exe --diagnostics flutter` (adds the Flutter sample on the Windows desktop device through `flutter debug_adapter`): **PASS, 72 assertions**. The Flutter adapter also sends `dart.debuggerUris`; `getVM` names the Windows app process (`flutter_counter.exe`, not the Flutter tool); private bytes 252.7 MB, Dart heap 81.4 MB, max CPU 19.2 % sampled; allocation profile with 416 classes (81.7 MB); the session ended when the app stopped.

After the creator chose that F5 runs non-JVM Kotlin targets with diagnostics, `--diagnostics` passes **70 assertions**: the new unit check of the Gradle-process filter plus a live stand-in (`cmd.exe` running a short `java -version`, then a busy PowerShell loop), where only the PowerShell process was counted (6 samples, 1 process, max CPU 23.7 %) and sampling started once it appeared.

`Vistudio.Tests.exe --diagnostics kotlin-targets` (a temp copy of `samples/kotlin-multiplatform` whose `main` keeps working for about 4 s; each target built, then its run task through `gradlew.bat --no-daemon` with the program-under-Gradle monitor F5 now uses; `JAVA_TOOL_OPTIONS` with the Windows trust store as in `test-kotlin-targets.ps1 -UseWindowsTrustStore`): **PASS, 79 assertions**. In every run only the program was counted, never Gradle's JVMs:

| Target | Run task | Samples | Processes | Max CPU | Private bytes |
| --- | --- | --- | --- | --- | --- |
| js | `:jsNodeDevelopmentRun` (Node.js) | 13 | 1 | 31.4 % | 142.2 MB |
| wasmJs | `:wasmJsNodeDevelopmentRun` (Node.js) | 2 | 1 | 19.2 % | 108.3 MB |
| mingwX64 | `:runDebugExecutableMingwX64` (x64 executable, emulated on ARM64) | 4 | 1 | 20.4 % | 10.2 MB |

The full Release build (`build.ps1`) passed after the first change (default unit run **1359 assertions**) and again after the Kotlin target change (**1361**), each time with packaged file/registration checks (`verify-vsix.ps1`) and icon rendering. The packaged `Vistudio.Vsix.pkgdef` registers `DiagnosticsPackage`, its menu resource and the tool window.

## 4. Visual Studio

`tests/verify-diagnostics.ps1 -RootSuffix VistudioLanguages` (Windows PowerShell, DTE plus UI Automation; Release VSIX from `build.ps1`, SHA-256 `102B5334...F9D1`, installed into the `VistudioLanguages` test hive): **PASS, 22 checks**, ActivityLog without Vistudio errors (2026-11-15).

- **Dart F5** (Dart Console App template, breakpoint on the `print` line): the session stopped at the breakpoint; the Dart pane logged the VM service the adapter announced; the Vistudio Diagnostics window opened by itself. Its UI Automation text at the breakpoint: `DartApp (Dart VM, pid 15504)`, `In break mode. 1 process.`, `CPU 0.4 %`, `Private bytes 235.6 MB`, `Working set 245.5 MB`, `Dart heap 5.9 MB of 6.9 MB`, events `Started Dart VM 15504 (dartvm.exe), VM service ws://127.0.0.1:.../ws` and `Break Debugger break`. After Continue the program ended and the window kept the data with `Ended.` and the event `Exited Debugging stopped.`
- **C F5** (C Console App template: Conan, CMake, MSVC; breakpoint in `main.c:6`): the native session stopped at the breakpoint and Visual Studio's Diagnostic Tools window appeared with "Starting Diagnostic Tools..." and no "does not support" text. Its charts are not exposed to UI Automation (only the frame and that status line were readable), so the CPU/memory figures inside Diagnostic Tools were not read.
- During the Dart session, Diagnostic Tools showed no readable content (the window was not realized; the script found no command name to bring it forward: `Debug.ShowDiagnosticTools`, `Debug.DiagnosticTools`, `View.DiagnosticTools` are not valid DTE commands), so its refusal message for the Dart engine was not observed live; it rests on the probe in section 1.
- Not done in Visual Studio: Take Snapshot (button not clicked; snapshots are checked live in section 3), the dark theme, Kotlin/Java, Flutter, WebAssembly, Rust, Zig, C++.

Two earlier attempts of the script failed for test-setup reasons, not product ones: a fresh `VistudioDiagnostics` hive never registered its DTE object within 240 s (first-run state of a new hive), and a check that read window captions through late-bound DTE objects got empty captions in PowerShell; the script now finds windows through UI Automation.

## 5. Not checked, and gaps

- **Kotlin and Java F5 in Visual Studio**: not run (Java F5 in Visual Studio has never been validated on this machine, see `language-projects-validation.md`). What is checked: the JVM discovery, `jstat` and `jcmd` paths against real JVMs (section 3) and that the hook compiles into `KotlinDebugCoordinator` right before `LaunchDebugTargets4`. The JVM that the Kotlin server starts through Gradle was not observed; the assumption is that `jps -l` lists it with the launched main class (Gradle's `JavaExec` sets the main class on the JVM).
- **Kotlin Multiplatform non-JVM targets** (JS, Wasm, Native): F5 now runs the run task with Vistudio Diagnostics (creator decision); checked live through Gradle (section 3), not in Visual Studio. The F5 path in `KotlinProjectConfig` and the `Started` hook in `KotlinPackage` were compiled, not clicked. Ctrl+F5 is unchanged and not watched. Kotlin/JS in a browser (`jsBrowser*` tasks) was not tried: the browser would be outside the Gradle process tree.
- **Rust and Zig**: their F5 uses the same native launch as C, so Diagnostic Tools applies by the probe; not run, because cargo and zig are not installed.
- **WebAssembly F5 in Visual Studio**: not run (wasmtime is not installed). The process-tree monitor and the started callback were checked live with a stand-in program (section 3).
- **Diagnostic Tools content for native sessions** beyond what UI Automation exposes (section 4): charts were not inspected visually; no screenshot was taken.
- **Vistudio Diagnostics drawing**: chart rendering, theme colors in the dark theme and the snapshot grid were not looked at on screen; only UI Automation text was read.
- **Events timeline integration**: Vistudio's events are in its own window; nothing is added to Diagnostic Tools' Events tab or to the Performance Profiler (T43's ETW provider is unchanged).
