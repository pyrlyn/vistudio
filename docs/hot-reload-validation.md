# Kotlin/JVM hot reload: validation (T72)

What was checked, how, and what was not. Machine: Windows 11 on ARM64 (Parallels), Visual Studio 18.12 Community (ARM64), Eclipse Temurin JDK 21.0.12 (x64, emulated), Gradle 8.14.7, Kotlin 2.4.21, Kotlin language server 271.2085.0. Date: 2026-11-16. This machine is slow for JVM work (x64 Java under emulation); the times below are from here.

## 1. Probes before the implementation

Done by hand with the JDK tools and the agent source that ships (`Build/VistudioHotReloadAgent.java`), compiled with `javac --release 11` and packed with `jar` (manifest `Agent-Class`, `Can-Redefine-Classes`):

| Question | Result |
| --- | --- |
| Does a dynamically loaded agent swap a running class? | Yes. A program printing `one` every 500 ms printed `two` after `redefineClasses` with the recompiled class; the result file said `reloaded Spin`. Paths with spaces work for the jar and the request file. |
| What happens to a class whose structure changed? | Refused: `UnsupportedOperationException: class redefinition failed: attempted to add a method`; the program kept printing the previous text. A class the JVM had not loaded was reported as `notloaded`. |
| A process that does not exist | The attach client exits 1 with `IOException: no such process`. |
| Does the JVM say anything? | JDK 21 prints "WARNING: A Java agent has been loaded dynamically ..." (four lines) to the program's standard error at every load. |
| A JVM with every thread suspended by a debugger (`jdb`, `suspend`), as at a breakpoint | The attach request is not served: the client was still waiting after 13 s, the program printed nothing, and the swap happened right after `resume`. So a reload at a breakpoint can only be applied when the program continues. |
| Gradle: how long does the compile take? | Copy of `samples/kotlin-jvm`, one-line change: 57 s with the extension's usual `--no-daemon` run; with a daemon 37-40 s for the run that starts it, then 5-6 s. |
| Can the daemon be kept apart from the user's own Gradle? | Yes: with `-Dorg.gradle.daemon.registry.base=<folder>` (a folder with a space in its name) the daemon registered there (`registry.bin` and its `daemon-<pid>.out.log` in that folder) and a second run reused it (5 s). |

## 2. Unit checks

`Vistudio.Tests.exe --hot-reload none` (also part of the default run): class lists (directories, binary names, module descriptors and multi-release copies left out, optional hash column), the reload plan (unchanged classes, rewritten classes, the same bytes written again, a class another build compiled after the JVM started, new class files, classes the JVM refused offered again, a reload that never reached the JVM, the hashed first list), agent results and the texts shown to the user, the compiler message shortened to `File.kt:line`, the JVM chosen among `jps -l` rows, the Gradle command (daemon, private registry, idle timeout, environment variables, rejected paths), the work directory, the setting's default, and that the shipped script and agent agree on the names they share.

## 3. Live: Gradle and a JVM

`Vistudio.Tests.exe --hot-reload`. A copy of `samples/kotlin-jvm` (in a folder with a space in its name) gets a Kotlin `main` that prints a text from a Kotlin object and a Java class every 250 ms, and runs under `gradlew run` for the whole test, as a debuggee runs under the Kotlin server's Gradle launch. Reloads go through `HotReloadSession` with the shipped `HotReload.gradle` and agent source.

**PASS, 76 assertions** (55 unit, 21 in this part). In order:

| Step | Result | Compile + swap |
| --- | --- | --- |
| The program starts; `jps -l` finds its JVM by main class; `java.exe` of its JDK is found | ok | |
| Reload without edits | "no changed classes"; the agent jar is under the work directory, nothing under the project's `build\vistudio` | 27.2 s (starts the daemon, compiles the agent) |
| Kotlin body change | `sample.HotText` reloaded, nothing else; the program printed the new text | 9.5 s |
| Java body change | `sample.JavaText` reloaded, nothing else (the Kotlin `main` that calls it was recompiled to the same bytes and left alone); the program printed the new text | 5.5 s |
| A method added to the Kotlin object | refused: "class redefinition failed: attempted to add a method", with the restart advice; the program kept printing the old text | 5.2 s |
| Back to the old shape with a new body | reloaded (the refused class is offered again); new text printed | 4.2 s |
| Compiler error | "Hot reload failed: Compilation failed: HotText.kt:4:27 Unresolved reference 'missing'."; the `e:` line reaches the caller (the Error List input) | 4.4 s |
| Fix, plus a new class nothing uses yet | 1 class reloaded, 1 class not loaded yet; new text printed | 4.5 s |
| End of the session | exactly one Gradle daemon served the reloads (private registry); it ended when the session was disposed; the program kept running | |

The program ran under one Gradle build while every reload compiled in a second one in the same project folder; neither blocked the other.

An earlier run of the same test found one defect, fixed before the run above: after the Java change two classes were reloaded, because the Kotlin file that calls the Java class had been recompiled to identical bytes and the state had no hash to compare with. The list written before a session's first compilation now carries the hashes.

## 4. Live: under the Kotlin server's debugger

`Vistudio.Tests.exe --hot-reload debugger`: **PASS, 80 assertions** (55 unit, 25 in this part; rerun after T73, section 7). The program runs through the installed Kotlin server's DAP endpoint with the launch arguments of `KotlinJvmLaunch`, on the session's Gradle daemon (`GradleSessionDaemon`, warmed up with the launch's environment before the launch request), and frames through `KotlinDapSession`, the code Visual Studio's F5 uses; reloads go through `HotReloadSession`.

- Debug launch: stopped at the breakpoint in `HotText.kt:6` on the first attempt, 10 s after the launch request; `jps -l` lists the JVM the server started through Gradle under the program's main class, so the JVM is found the way the IDE looks for it.
- A body change while the debuggee runs: `1 class reloaded`, and the program printed the new text. The JVM's dynamic-agent warning appeared in the program output.
- A reload asked for while the program is paused at a breakpoint: the class compiled, the swap did not happen for the 8 s it was watched, the "A program paused in the debugger takes them when it continues" line was logged, and after `continue` the result was `1 class reloaded`.
- Breakpoints after a reload: the breakpoint set in the class before it was reloaded stopped again by itself, and the stopped frame showed the new value (`text = "kotlin-3"`). Nothing has to be sent to the adapter again.
- Run without debugging (`noDebug`, Ctrl+F5) with `"vmArgs": ["-XX:+EnableDynamicAgentLoading"]`: the JVM was found, the class reloaded, the program printed the new text, and the agent warning did not appear, so `vmArgs` reaches the JVM.

Found on the way (not part of hot reload; fixed as T73, section 7): the server's debugger stops waiting for the JVM 30 s after the launch (its log: "Debugger is waiting for application to start", then "Disconnected from the target VM" exactly 30 s later, then "DAP session ended; the debuggee is kept running"). On this machine a launch with `--no-daemon` in `toolArgs` needs longer than that to reach the JVM, so the debug session ended before the program started, and the JVM it left behind kept waiting for a debugger. The first version of this probe therefore launched on a Gradle daemon it warmed up itself and waited 45 s after the import; it now uses the extension's own session daemon and does not wait.

## 5. Visual Studio

`tests/verify-hot-reload.ps1 -RootSuffix VistudioDiagnostics` (Windows PowerShell, STA; the Debug VSIX installed with VSIXInstaller; driven through EnvDTE only, no mouse or keyboard): **PASS, 27 checks**, no Vistudio entries of type Error in the ActivityLog. The project is a Kotlin console project from the New Project template's generator with a `main` that prints a text from a second class every 250 ms, opened from a solution in its own folder and started with **Ctrl+F5** (`Debug.StartWithoutDebugging`).

- The command is registered as `Kotlin.HotReload`; it is available while the program runs and unavailable after Stop.
- The program started 140 s after Ctrl+F5 (first start of the Kotlin server, Gradle import and build) and printed in the Debug pane.
- Reload on save: the text of `HotText.kt` was replaced in the editor and saved; the Kotlin pane showed "Hot reload: 1 class reloaded." 39 s later (the reload that starts the daemon) and the program printed the new text.
- Reload by command from an unsaved edit: `Kotlin.HotReload` saved the document, reloaded in 9 s, the program printed the new text, and the command's own save did not start a second reload.
- Compiler error: "Hot reload failed: Compilation failed: HotText.kt:6:16 Unresolved reference 'missing'." in the Kotlin pane, an Error List entry for `HotText.kt`, the program still on the previous code; after the fix the class reloaded and the Error List entry was gone.
- A method added to the class: "Hot reload: 1 class could not be reloaded." with "jvmconsole.HotText: class redefinition failed: attempted to add a method" and the restart advice; with the old shape again the class reloaded and the program printed the new text.
- Stop Debugging ended the session, and no Gradle daemon of the hot reload registry was left running.

With `-Debugger` (F5 and the breakpoint checks) the first runs stopped at their first check: the debug session ended 82 s after F5 (128 s in a second run) without reaching the breakpoint, and the Debug pane ended with "The program '' has exited with code 4294967295" right after Gradle's first two lines. That is the 30 s attach limit of section 4 with the `--no-daemon` launch on this machine, not a reload step. After T73 the same run passes; see section 7.

Notes on running the script here: Windows PowerShell started from PowerShell 7 needs its own `PSModulePath`; a root suffix that never ran shows Visual Studio's sign-in and theme prompts before any automation object exists (the script now skips them through UI Automation; this run used an existing suffix after answering them once); and creating the project through `AddFromTemplate` followed by `Solution.Close`/`Open` stalled in the script's `Open` call while Visual Studio stayed idle, so the script generates the project up front and passes its solution on the command line.

## 6. Not verified

- The option page entry, `.vs\settings.json` (`vistudio.kotlin.hotReloadOnSave`), turning reload on save off, several saves merged into one reload, the status bar text, the Run | Debug lenses, `.javaproj` projects, Open Folder with `launch.vs.json`.
- Nothing was looked at on screen: every Visual Studio result above was read through automation.
- Multiplatform modules (`jvm()`, `jvm("desktop")`): the script handles their compilations, but no reload was run on `samples/kotlin-multiplatform` or `samples/compose-multiplatform`. A Compose UI is not recomposed by a reload; that was not tried either.
- Multi-module builds where the edited class lives in a dependency module packaged as a jar.
- JDKs other than Temurin 21; a JRE without `jdk.attach`; a JDK that forbids dynamic agent loading.
- Projects on a network share with a clock that differs from the local one (class files newer than the JVM are compared by time on the first reload).

## 7. F5 within the Kotlin server's attach wait (T73)

Same machine and date. The change: F5 and Ctrl+F5 run on a Gradle daemon that belongs to the debug session (`GradleSessionDaemon`), started before the launch request; see T73 in [plan.md](../plan.md) for the choices.

**Before the change.** `tests/verify-hot-reload.ps1 -RootSuffix VistudioDiagnostics -Debugger` failed at "F5 stopped at the breakpoint in Main.kt:6 (128 s after F5)". The server's log of that run: launch at 12:10:37, "Debugger is waiting for application to start" at 12:10:38, "Connecting to the target VM" every 500 ms, "Disconnected from the target VM" at 12:11:08, the disconnect request from Visual Studio five seconds later (the transport holds `terminated` back that long for an exit code), and no second attach attempt in between.

**Is the wait configurable?** No. Read from the server's own classes with `javap` (jars under `plugins\dap-jvm` and `lib\language-server.dap.platform.jar` of 271.2085.0): `JvmDebuggerAdapter.createDebugEnvironment` passes `pollConnection = true` to IntelliJ's `DefaultDebugEnvironment`, whose constructor loads the constant `30000` (`DebugEnvironment.LOCAL_START_TIMEOUT`); `JvmLaunchArguments` has no timeout field; the ten minutes of `BUILD_TOOL_ATTACH_TIMEOUT` bound the server's own retry loop, which did not try again in the log above.

**Outside Visual Studio.** `Vistudio.Tests.exe --hot-reload debugger-cold` (the probe of section 4, first with a launch without a daemon): **PASS, 78 assertions** (55 unit, 23 live).

| Step | Result |
| --- | --- |
| Launch without a daemon (`--no-daemon` in `toolArgs`, as before) | No stop. DAP events: `initialized` at 0.8 s, `terminated` at 38.1 s (the server's 33 s plus the transport's 5 s), nothing else. After the client closed its connection no JVM of the program appeared within 90 s in this run (the earlier Visual Studio run of section 5 was reported to have left one waiting for a debugger; the reproduction above did not look for it). |
| Warm-up of the session daemon (Gradle `help`, launch environment, registry path with a space) | 19 s |
| Launch on the session daemon | Stopped at the breakpoint on the first attempt, 13 s after the launch request, with no pause after the server's import. DAP events: `initialized`, then `stopped`; an attached debugger sends nothing before its first stop. One daemon ran in the session's registry. |
| Second launch (run without debugging) | "daemon already warm": no warm-up, the same daemon |
| End of the session without a disconnect request (connection closed while the program runs) | Disposing the session daemon ended the daemon and the program's JVM; the registry folder was removed |

`Vistudio.Tests.exe --hot-reload debugger` (the final probe, with the Gradle build before the launch run on the session daemon as F5 from Open Folder does): **PASS, 80 assertions** (55 unit, 25 live). The build took 59 s and left one daemon running; the launch needed no warm-up ("daemon already warm") and stopped at the breakpoint 10 s after the launch request; the run without debugging reused the daemon; the end of the session left no daemon, no JVM and no registry folder.

One run of the earlier probe version (warm-up by `help`, 38 s) took 29 s from the launch request to the breakpoint, 33 s from the DAP connection, while another session was building on the same machine: it attached, with almost no margin. The 30 s still count from the launch request; the daemon shortens what happens in them, it does not lift the limit.

**Visual Studio, F5.** `tests/verify-hot-reload.ps1 -RootSuffix VistudioDiagnostics -Debugger` (Debug VSIX installed by the script, EnvDTE only): **PASS, 41 checks**, no Vistudio entries of type Error in the ActivityLog.

- F5 stopped at the breakpoint in `Main.kt:6` 74 s after F5 (first start of the Kotlin server, Gradle import, the daemon's warm-up, the launch). The Kotlin pane: "Starting the Gradle daemon of this debug session ..." and "Gradle daemon ready (27 s)." The solution build had built the project, so the launch's own build was skipped and Gradle's `help` task started the daemon.
- Every reload check of section 5 passed in the F5 session (save, command, compiler error, refused change), the first reload 41 s (it starts the hot reload daemon), the next 9 s.
- At a breakpoint: the stopped frame showed the reloaded value (`text = "kotlin-6"`); a save while paused logged "A program paused in the debugger takes them when it continues." and reloaded nothing; after Continue the class reloaded and the program printed the new text.
- A breakpoint that was set before a reload of its class: the class reloaded while the breakpoint was set and the program printed the new text. Informational, not a check: after that reload the breakpoint did not stop again within 30 s in Visual Studio, while at the DAP level (section 4) it did. Not looked into here.
- Stop Debugging: the session ended, no Gradle daemon of the launch registry (`%LOCALAPPDATA%\Vistudio\debug-daemon`) or the hot reload registry was running, and no `java.exe` whose command line names the project folder was left.

**Visual Studio, Ctrl+F5.** The same script without `-Debugger` (`-SkipInstall`, the VSIX of the F5 run): **PASS, 28 checks**, no Vistudio errors in the ActivityLog. The program printed 96 s after Ctrl+F5 (140 s before the change, section 5), the daemon's warm-up took 31 s, and after Stop no daemon and no JVM of the project was left.

**Unit checks** (default run): the launch's `toolArgs` with and without a session daemon, the launch environment handed to the warm-up, the build and warm-up command lines, the registry naming, the daemon's process id from its log name, the JVMs a session left (main class and start time within the session), `KotlinDebugMonitor`'s reading of the DAP traffic (the events recorded above, a stop, an exit code, Stop Debugging during the start, ends outside the attach wait) and its wiring through `KotlinDapSession`.

Not verified:

- The message for a session that ended unattached, and the search for a leftover JVM, in Visual Studio: with the session daemon the attach no longer failed here, so both ran only in the unit checks (the classification of the recorded DAP events, the selection among `jps -l` rows). Ending a leftover JVM's process tree was not exercised: the launch without a daemon left none in the probe run.
- F5 from Open Folder (`launch.vs.json`, Current Document) and from the Run | Debug lenses in Visual Studio, where the build before the launch runs on the session daemon: that order was run in the probe only. The Visual Studio runs used a `.kotlinproj` in a solution.
- A build whose configuration takes longer than 30 s on a warm daemon, a multi-module or Multiplatform build, a fast machine, a JDK other than Temurin 21, a `gradle.properties` that turns the daemon off (`--daemon` on the command line overrides it by Gradle's rules; not run).
- Cancel Gradle during the warm-up, Stop Debugging during the warm-up or while Gradle starts the program, Visual Studio closed during a session (the job object ends the daemon by design; not run).
- Nothing was looked at on screen.
