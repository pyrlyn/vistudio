# Compose preview validation (T22)

Checks performed for the Kotlin Compose Preview window. Only what is listed here was run.

## Environment

- Windows 11, Visual Studio Community 2026 18.12, isolated profile `VistudioTest`, Debug VSIX built from this tree.
- JDK 11.0.31 (Microsoft) for Gradle and the renderer JVM; Gradle 8.14.7 wrapper.
- Sample `samples/compose-multiplatform`: Kotlin 2.4.21, Compose Multiplatform 1.12.2 (`org.jetbrains.compose`, `org.jetbrains.kotlin.plugin.compose`), skiko 0.150.2 resolved by Gradle. `:composeApp` has `desktop` (JVM) and `wasmJs` targets; `:core` is a plain Kotlin Multiplatform module without Compose.

## Unit checks

`tests/Vistudio.Tests` (all 480 assertions pass) includes the scanner, request and manifest checks:

- `@Preview` in line, block and nested comments, strings and raw strings is ignored; string templates with braces do not break the declaration depth.
- Several `@Preview` annotations on one function, positional and named arguments, quoted commas, underscores in numbers, hexadecimal `backgroundColor`, qualified annotation names, expression bodies.
- Unsupported cases are listed with a reason: parameters (`@PreviewParameter`), receivers, previews inside an `object`. `@PreviewParameter` alone and multi-preview annotation classes are not previews.
- Facade names (`Cards.kt` → `CardsKt`, `App.desktop.kt` → `App_desktopKt`, `1st-file.kt` → `_1st_fileKt`), `@file:JvmName`, default package.
- Manifest parsing: escaped error text, rejected unsafe image names, the Gradle `VISTUDIO_PREVIEW_ERROR` marker.

## Gradle and renderer

`Vistudio.Tests.exe --compose-preview <sample> <JDK> src/Vistudio.Vsix/Build` scans `Cards.kt`, writes the request, runs `ComposePreview.gradle` through `GradleCommand.CreatePreview` and the real wrapper, and checks the manifest:

```text
RESULT GreetingCardPreview: 416x207 px, 7653 bytes
RESULT Badge on white: 320x120 px, 3057 bytes
RESULT Failing preview: error java.lang.IllegalStateException: This preview fails on purpose
at sample.ui.CardsKt.FailingPreview(Cards.kt:61)
PASS: Compose previews rendered through Gradle.
```

The images were inspected. The wrap-content card is cropped to its rounded box. The 160×60 dp badge preview is 320×120 px on a white background (`showBackground = true`).

The same task run for `core/src/commonMain/kotlin/sample/core/Greeting.kt` fails with the reported reason `The desktop target of :core has no Compose UI for desktop on its runtime classpath...`. The window does not start Gradle for files without `@Preview`.

## Visual Studio

The VSIX was installed with VSIXInstaller into `VistudioTest` (exit 0), followed by `/UpdateConfiguration` (exit 0). The sample folder was opened with `/Log`. The IDE was driven only through EnvDTE automation of that instance: the `Tools.KotlinComposePreview` command, `ItemOperations.OpenFile`, text edits and `Document.Save`. No screen control was used, so the images were not inspected on screen. The evidence comes from the files written by the IDE process and from the window's status lines in ActivityLog (`Vistudio.ComposePreview`).

- Opening the window and `Cards.kt` rendered through Gradle (status `Cards.kt: 2 rendered, 1 failed`). After a restart, Visual Studio restored the window and the file and rendered again at startup.
- Changing `GreetingCard("Visual Studio")` to `GreetingCard("Saved in VS")` and saving the document re-rendered automatically. The new `p0.png` shows "Hello, Saved in VS!" (393 px wide instead of 416). Reverting and saving rendered the original 416 px image.
- The save renders above ran while another process had the output folder as its working directory. The first build deleted the whole folder and failed in this situation; the runner now replaces only its files.
- ActivityLog contained no Vistudio errors.

### On-screen check

A second session, with screen access approved by the creator, checked the window by plain clicks only. The Kotlin server of this instance ran with its own `--system-path` (`%LOCALAPPDATA%\Vistudio\kotlin-lsp-preview`). This was set temporarily in `settings.xml` for the start and restored byte-for-byte afterwards, so concurrent sessions kept the shared cache.

- The window docks next to Solution Explorer. Restored at startup, it showed the wrap-content card and the 160×60 dp badge on white at their dp size, and the failing preview's exception in red under its name.
- **Refresh** followed by **Cancel**: the status changed to "Rendering canceled.", the previous images stayed, and Refresh was enabled again.
- `GreetingCard(42)` was written to the file on disk and **Refresh** clicked. The status line and the top of the list showed `Compilation failed: …Cards.kt:45:18 Argument type mismatch…`, and each preview showed "Not rendered." After restoring the file and clicking Refresh, the images came back.
- Clicking a preview's name opened `Cards.kt` with the caret on the `fun` line.

Fixed after this check: long error text was clipped because the list scrolled horizontally. The list now wraps text, and a preview wider than the window shrinks to fit. The caret line was at the bottom edge after navigation; it is now shown with eight lines of context above. All three fixes were confirmed on screen in a rebuilt package.

Still verified only at the Gradle level, not in the window: "no JVM target" and "no Compose UI" messages. They use the same error path in the window as the compile failure above.

## Interactive mode

Checks of the interactive preview (2026-11-16). Machine for this section: Windows 11 Pro (ARM64 under Parallels; JDK and test console run as x64), Visual Studio Community 2026 18.12, JDK 21.0.12 (Eclipse Adoptium) for Gradle and the host JVM, the same sample and versions as above. The sample gained `Interactive.kt` (a click counter, a text field, a square driven by a `LaunchedEffect`).

### Unit checks

`tests/Vistudio.Tests` (all 1598 assertions of the default run pass) includes `ComposeLivePreviewTests.Run`: manifest lines and the Java argument file, when a host is reused, the `show`/`pointer`/`key` command text, Windows virtual key to AWT key code and location, which keys the focused preview takes and which stay with the IDE, rejected tabs in fields, unescaped text messages, message framing, frame header and pixel order, rejected truncated frames.

### Gradle, host and session

`Vistudio.Tests.exe --compose-live <sample> <JDK> src/Vistudio.Vsix/Build` copies the sample to a temporary folder (the path contains a space), and runs `ComposeLiveSession` against the real wrapper, the shipped init script, renderer and host. All 35 checks pass:

- Gradle compiles the desktop target and the host; the manifest names `:composeApp`/`desktop`, 45 libraries from the Gradle cache and three reloadable entries (the module's classes and resources, `core-desktop.jar`).
- The host starts and loads the classes (0.6 s); `CounterPreview` is shown 2.3 s later (the first composition loads Compose and Skia) at 240×120 px at scale 1 and 480×240 px at scale 2, transparent outside its content.
- Clicks change state and the next frame shows it (first click to frame: 159 ms); the hover tint appears under the pointer and goes when it leaves; a click outside the button changes nothing; pointer positions are frame pixels at scale 2.
- A click focuses `TextFieldPreview`'s field; typed `abc` and Backspace change its text (seen through the color of a bar bound to the text).
- `TimerPreview` changes by itself: a `LaunchedEffect` with `delay` produced all three colors without input.
- A preview without a size is cropped to what it drew: 416×207 px at scale 2, the size of the static render.
- `FailingPreview` reports its exception with `Cards.kt:61`; the host then shows another preview.
- After an edit, Gradle compiles on the session's daemon and `load` shows the new code in the same host process (94 ms), starting from the initial state; the reloaded preview reacts to clicks.
- A compiler error is returned as `Compilation failed: …Interactive.kt:82:17 Initializer type mismatch…`, and the preview from before the error stays interactive. (Kotlin removes classes from the build directory on a failed compilation; the host runs from its own copy.)
- `:core` (no Compose) is reported with the "no Compose UI for desktop" message.
- After `Dispose` the host and the Gradle daemon processes are gone, the session folder under `%LOCALAPPDATA%\Vistudio\compose-live` is removed, and the project copy can be deleted (nothing holds its files).

Gradle times in that run: 64 s first compile of the copy, 22 s after the edit, 16 s with the compiler error, 7 s for the module without Compose. They are dominated by the Kotlin compilation on this machine.

Found and fixed while running it: .NET Framework starts a redirected standard input with a UTF-8 byte order mark, which made the host reject its first command; the host now ignores the mark, and a command the host does not understand fails the waiting request instead of timing out.

`Vistudio.Tests.exe --compose-preview` was run again after the renderer's helpers were made shared with the host: same three results as above (416×207 px/7653 bytes, 320×120 px/3057 bytes, the reported exception).

### Visual Studio

`tests/verify-compose-live.ps1` (Windows PowerShell, STA) installs the Debug VSIX into its own root suffix `VistudioComposeLive`, opens a copy of the sample with Open Folder and runs `tests/ComposeLiveAcceptance.cs`: EnvDTE for documents and commands, UI Automation for the window's controls (`ComposePreview.*` automation ids), and a `PrintWindow` capture of the IDE to read the color the window shows at a position inside the preview. The click and the typing are real input: the script moves the pointer over the preview and sends key presses to the foreground Visual Studio. The run against VSIX SHA-256 `735AC2A7…0073A65C` passed all 33 checks without a skipped step:

- **Kotlin.ComposePreview** opens the window; the three previews of `Interactive.kt` render as images without a Refresh, each with an **Interactive** link.
- **Interactive** on `CounterPreview`: the status line reads "Counter: interactive since …", one host JVM runs for this Visual Studio, and the window shows the red button (the capture shows it at 480×240 px, display scale 2).
- A mouse click on the button turns it green ("Clicked 1 times" in the capture, drawn with the hover tint under the pointer).
- **Back to previews** ends the host and shows the list again.
- `TextFieldPreview`: after a click into the field, typing `abc` turns its bar green, and `Interactive.kt` in the editor is unchanged, so the keys went to the preview and not to the editor.
- With the counter running, changing a color in `Interactive.kt` and saving shows the new color 27 s later in the same host process.
- A saved compiler error puts `Compilation failed: …Interactive.kt:82:17 Initializer type mismatch…` in the status line and above the preview, which keeps running; removing the error and saving makes it interactive again.
- **Back** leaves no host process and no session folder; the list renders again.
- Visual Studio is asked to exit while a preview runs: it exits (0.7 s) and no host process is left.
- The ActivityLog has no Compose preview errors.

The fresh root suffix first stopped at Visual Studio's "Sign in" and settings pages, before the automation object exists; the script skips sign-in and keeps the defaults. A first complete run needed a manual **Refresh**: the window had been asked to render before Visual Studio finished opening the folder, stayed at "Open the Gradle project folder…" and logged that as an error. The window now renders again when the workspace becomes known, and such states are logged as information; the run listed above is the one after that fix and has no manual step.

Seen in the capture and not changed: the **Refresh on save** label is drawn dark on the dark theme (it was before this work).

Not covered by any check: mouse wheel scrolling, the secondary and middle buttons, Ctrl chords (clipboard), typed characters other than a, b, c and Backspace (Shift, other layouts, dead keys), display scales other than 2, a preview that throws after it is shown (its error display in the window), **Restart**, **Cancel** during the live compile, closing the tool window or the folder while a preview runs, a module with a declared Java toolchain, a restart of the host after a library change, a floating or auto-hidden tool window.

## Notes

- Gradle downloads on this machine need `-Djavax.net.ssl.trustStoreType=Windows-ROOT` (through `JAVA_TOOL_OPTIONS`) because a TLS-inspecting antivirus certificate is only in the Windows store. This applied only to the first download of the sample's dependencies and is not part of the project.
- The agent shell sets `NoDefaultCurrentDirectoryInExePath=1`, which makes `cmd` ignore `gradlew.bat` in the current directory. Tests and IDE runs in this validation unset it; Visual Studio started normally does not have it. Since 2026-11-14 `GradleCommand` starts the wrapper as `.\gradlew.bat`, so the variable no longer breaks Gradle commands; the native scripts still clear it and were not re-run after the fix.

## Repeat

```powershell
$env:JAVA_HOME = 'C:\Program Files\Microsoft\jdk-11.0.31.11-hotspot'
./build.ps1 -Configuration Debug
tests/Vistudio.Tests/bin/Debug/net472/Vistudio.Tests.exe --compose-preview "$PWD/samples/compose-multiplatform" $env:JAVA_HOME "$PWD/src/Vistudio.Vsix/Build"
```

In Visual Studio, open `samples/compose-multiplatform`, run **Tools > Kotlin: Compose Preview**, open `composeApp/src/commonMain/kotlin/sample/ui/Cards.kt`, edit a preview and save.

Interactive mode (any JDK 11 or newer in `JAVA_HOME`; the acceptance needs Windows PowerShell and an unlocked desktop, because it clicks and types in the Visual Studio window):

```powershell
tests/Vistudio.Tests/bin/Debug/net472/Vistudio.Tests.exe --compose-live "$PWD/samples/compose-multiplatform" $env:JAVA_HOME "$PWD/src/Vistudio.Vsix/Build"
powershell.exe -NoProfile -STA -File tests/verify-compose-live.ps1
```

By hand: open `composeApp/src/commonMain/kotlin/sample/ui/Interactive.kt`, click **Interactive** next to **Counter** in the window, click the button, edit a color in the file and save.
