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

`Vistudio.Tests.exe --hot-reload debugger`: **PASS, 74 assertions** (55 unit, 19 in this part). The program runs through the installed Kotlin server's DAP endpoint with the launch arguments of `KotlinJvmLaunch` and frames through `KotlinDapSession`, the code Visual Studio's F5 uses; reloads go through `HotReloadSession`.

- Debug launch: stopped at the breakpoint in `HotText.kt:6`; `jps -l` lists the JVM the server started through Gradle under the program's main class, so the JVM is found the way the IDE looks for it.
- A body change while the debuggee runs: `1 class reloaded`, and the program printed the new text. The JVM's dynamic-agent warning appeared in the program output.
- A reload asked for while the program is paused at a breakpoint: the class compiled, the swap did not happen for the 8 s it was watched, the "A program paused in the debugger takes them when it continues" line was logged, and after `continue` the result was `1 class reloaded`.
- Breakpoints after a reload: the breakpoint set in the class before it was reloaded stopped again by itself, and the stopped frame showed the new value (`text = "kotlin-3"`). Nothing has to be sent to the adapter again.
- Run without debugging (`noDebug`, Ctrl+F5) with `"vmArgs": ["-XX:+EnableDynamicAgentLoading"]`: the JVM was found, the class reloaded, the program printed the new text, and the agent warning did not appear, so `vmArgs` reaches the JVM.

Found on the way (not part of hot reload): the server's debugger stops waiting for the JVM 30 s after the launch (its log: "Debugger is waiting for application to start", then "Disconnected from the target VM" exactly 30 s later, then "DAP session ended; the debuggee is kept running"). On this machine a launch with the extension's `toolArgs` (`--no-daemon`) needs longer than that to reach the JVM, so the debug session ended before the program started, and the JVM it left behind kept waiting for a debugger. The probe therefore launches on a Gradle daemon warmed up beforehand (own registry, ended afterwards) and waits 45 s after the import; with that the first launch attached. F5 in Visual Studio uses `--no-daemon` and was not run here.

## 5. Visual Studio

`tests/verify-hot-reload.ps1 -RootSuffix VistudioDiagnostics` (Windows PowerShell, STA; the Debug VSIX installed with VSIXInstaller; driven through EnvDTE only, no mouse or keyboard): **PASS, 27 checks**, no Vistudio entries of type Error in the ActivityLog. The project is a Kotlin console project from the New Project template's generator with a `main` that prints a text from a second class every 250 ms, opened from a solution in its own folder and started with **Ctrl+F5** (`Debug.StartWithoutDebugging`).

- The command is registered as `Kotlin.HotReload`; it is available while the program runs and unavailable after Stop.
- The program started 140 s after Ctrl+F5 (first start of the Kotlin server, Gradle import and build) and printed in the Debug pane.
- Reload on save: the text of `HotText.kt` was replaced in the editor and saved; the Kotlin pane showed "Hot reload: 1 class reloaded." 39 s later (the reload that starts the daemon) and the program printed the new text.
- Reload by command from an unsaved edit: `Kotlin.HotReload` saved the document, reloaded in 9 s, the program printed the new text, and the command's own save did not start a second reload.
- Compiler error: "Hot reload failed: Compilation failed: HotText.kt:6:16 Unresolved reference 'missing'." in the Kotlin pane, an Error List entry for `HotText.kt`, the program still on the previous code; after the fix the class reloaded and the Error List entry was gone.
- A method added to the class: "Hot reload: 1 class could not be reloaded." with "jvmconsole.HotText: class redefinition failed: attempted to add a method" and the restart advice; with the old shape again the class reloaded and the program printed the new text.
- Stop Debugging ended the session, and no Gradle daemon of the hot reload registry was left running.

With `-Debugger` (F5 and the breakpoint checks) the run stopped at its first check: the debug session ended 82 s after F5 without reaching the breakpoint, and the Debug pane ended with "The program '' has exited with code 4294967295" right after Gradle's first two lines. That is the 30 s attach limit of section 4 with the extension's `--no-daemon` launch on this machine, not a reload step; no reload was attempted in that run.

Notes on running the script here: Windows PowerShell started from PowerShell 7 needs its own `PSModulePath`; a root suffix that never ran shows Visual Studio's sign-in and theme prompts before any automation object exists (the script now skips them through UI Automation; this run used an existing suffix after answering them once); and creating the project through `AddFromTemplate` followed by `Solution.Close`/`Open` stalled in the script's `Open` call while Visual Studio stayed idle, so the script generates the project up front and passes its solution on the command line.

## 6. Not verified

- Hot reload of a debug session started with F5 in Visual Studio (the launch does not get that far on this machine, see above): reloading at a breakpoint, breakpoints after a reload and the debugger state were checked only at the DAP level in section 4.
- The option page entry, `.vs\settings.json` (`vistudio.kotlin.hotReloadOnSave`), turning reload on save off, several saves merged into one reload, the status bar text, the Run | Debug lenses, `.javaproj` projects, Open Folder with `launch.vs.json`.
- Nothing was looked at on screen: every Visual Studio result above was read through automation.
- Multiplatform modules (`jvm()`, `jvm("desktop")`): the script handles their compilations, but no reload was run on `samples/kotlin-multiplatform` or `samples/compose-multiplatform`. A Compose UI is not recomposed by a reload; that was not tried either.
- Multi-module builds where the edited class lives in a dependency module packaged as a jar.
- JDKs other than Temurin 21; a JRE without `jdk.attach`; a JDK that forbids dynamic agent loading.
- Projects on a network share with a clock that differs from the local one (class files newer than the JVM are compared by time on the first reload).
