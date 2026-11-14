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

## Notes

- Gradle downloads on this machine need `-Djavax.net.ssl.trustStoreType=Windows-ROOT` (through `JAVA_TOOL_OPTIONS`) because a TLS-inspecting antivirus certificate is only in the Windows store. This applied only to the first download of the sample's dependencies and is not part of the project.
- The agent shell sets `NoDefaultCurrentDirectoryInExePath=1`, which makes `cmd` ignore `gradlew.bat` in the current directory. Tests and IDE runs in this validation unset it; Visual Studio started normally does not have it.

## Repeat

```powershell
$env:JAVA_HOME = 'C:\Program Files\Microsoft\jdk-11.0.31.11-hotspot'
./build.ps1 -Configuration Debug
tests/Vistudio.Tests/bin/Debug/net472/Vistudio.Tests.exe --compose-preview "$PWD/samples/compose-multiplatform" $env:JAVA_HOME "$PWD/src/Vistudio.Vsix/Build"
```

In Visual Studio, open `samples/compose-multiplatform`, run **Tools > Kotlin: Compose Preview**, open `composeApp/src/commonMain/kotlin/sample/ui/Cards.kt`, edit a preview and save.
