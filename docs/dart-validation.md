# Dart and Flutter validation

What was run for T52-T57, on which machine, and what it showed. Only checks listed here were performed.

## Machine

- Windows 11 Pro on ARM64 (Apple Silicon under Parallels); Visual Studio Community 2026 18.12.12405.243, native ARM64 (`devenv.exe` machine type AA64).
- Flutter 3.50.0 stable (Dart 3.14.0), the official `flutter_windows_3.50.0-stable.zip` (SHA-256 `35FB8434...59AAB8` checked), unpacked to `C:\Users\listepo\tools\flutter`. A first copy on `L:` (a Parallels shared folder) did not work: git inside the SDK rejects the network share ("dubious ownership"), so the SDK lives on the local disk. No global git or system setting was changed.
- Visual Studio workload "Desktop development with C++" added through the Visual Studio Installer (needed by Flutter's Windows device).
- The VSIX manifest had only an x64 installation target, so VSIXInstaller refused this ARM64 Visual Studio (`NoApplicableSKUsException`). With the creator's approval a second `InstallationTarget` with `ProductArchitecture arm64` was added; the extension code is managed AnyCPU.

## Automated checks (`tests/Vistudio.Tests`)

`Vistudio.Tests.exe --dart` runs the unit checks (also part of the default run) and then live checks against the SDK (`VISTUDIO_FLUTTER_SDK=C:\Users\listepo\tools\flutter`). Last full run after all fixes: `PASS: 144 assertions (Dart, live SDK)` — 96 unit and 48 live checks (`--dart none` alone: 96). The default run (1195 assertions) and `--zig` 125, `--codelens` 121, `--structure` 41, `--jvm-languages` 147, `--etw` 78, `--mise` 74 also pass; their live Zig and mise parts were skipped because this machine has no zig.exe or mise.

Unit checks: argument splitting and the `cmd.exe` command line for `flutter.bat` (every argument quoted; `%`, `"` and line breaks rejected), SDK resolution (Flutter folder or its `bin`, Dart SDK precedence, PATH with `flutter.bat` / `dart.bat` / `dart.exe`, versions from `version` / `flutter.version.json`), package detection (`sdk: flutter`, a commented line does not count), test and integration test folders, entry points, command lines for run / test / integration test / analyze / language server / debug adapters / create, the analyzer machine format (escaped backslashes and pipes) and compiler lines (relative, absolute, `file:///`, `Context:` skipped, Output pane rewrite), Flutter daemon events and responses, device listing, the JSON test protocol (hidden loading test, print, failure with location, suite compile error, flutter's daemon-style lines hidden), the source scanner (types, members, `main`, nested groups, adjacent and interpolated test names, comments/strings/raw/triple strings masked), DAP launch arguments and `launch.vs.json` merging, settings round trip and `vistudio.dart.*` overrides, the grammar (scopes, every regex compiles) and DAP framing (partial writes, frame queue).

Live checks, on copies of `samples/dart-console` and `samples/flutter-app` in `%TEMP%`:

| Area | Checked against the real SDK |
| --- | --- |
| Toolchain | `dart --version` = SDK version file (3.14.0); `dart pub get`, `flutter pub get` |
| Run | `dart run` output (`Total area: 9.14`); a compile error parsed at `broken.dart(2,9)` |
| Error reporting | `dart analyze --format=machine`: `undefined_identifier` error and `unused_local_variable` warning with exact locations |
| Analysis server (LSP) | capabilities; an unsaved error published as a diagnostic at the right line; references to `add()` (2, from `bin`); member completion after `Rectangle(2, 3).`; go to definition into `lib/`; hover with the signature |
| Unit tests | `dart test --reporter json`: 3 passed with names and zero-based locations; one test by full name (`--plain-name`); a failing test reported at its `test(` line with `Expected: <3>`; a test file compile error reported with its location |
| Dart debugging | `dart debug_adapter` through `DartDapConnection` with every frame written in two pieces: breakpoint hit (after the adapter's own `stopped(entry)` + `continued`), top frame `main` at the line, Locals with `shapes`, watch `shapes.length + 40 = 42`, a Vistudio-own `threads` request answered privately (the client never sees that response), step over, output events, exit |
| Test debugging | `dart debug_adapter --test` with `--plain-name "shapes total area"`: stopped in that test; exactly one visible `testDone`, success |
| Widget test | `flutter test --machine`: `Counter increments smoke test` passed |
| Flutter run + hot reload | `flutter run --machine -d windows` (`FlutterAppSession`): app started (52 s first build); after editing the title, hot reload applied it (`Reloaded 1 of 1051 libraries`, the app printed the new title); hot restart; a hot reload with a compile error reported the error at `main.dart(78,11)`; Stop ended flutter run and the app |
| Flutter debugging | `flutter debug_adapter`: stopped in `_MyHomePageState.build` on Windows, `widget.title` evaluated; `flutter.appStarted`; a `hotReload` request sent between the client's frames rebuilt with the edited title (seen by the debugger at the breakpoint), answered with success; `hotRestart`; disconnect ended the session; the adapter exited and no app process remained |
| Integration test | `flutter test integration_test -d windows --machine`: `counter app tapping the button increments the counter` passed (34 s) |

## Visual Studio (`tests/verify-dart.ps1`)

The script installs the Debug VSIX into the isolated root suffix `VistudioDart` (VSIXInstaller exit 0), opens each sample copy with Open Folder, takes the SDK from that copy's `.vs\settings.json` (global settings untouched) and drives the IDE through DTE: commands by canonical name, the editor's text, the Output panes, the Error List and the debugger. No mouse, keyboard or screen capture was used (screen access was declined); the first-run "Sign in" dialog of the new profile was dismissed by the creator.

Dart console sample, 27 checks passed: the code editor opened `bin\dart_console.dart`; the analysis server started from the IDE and the Dart pane logged Dart 3.14.0 / Flutter 3.50.0 from the workspace settings; an unsaved `print(missingThing);` appeared in the Error List (level High, `dart_console.dart` line 10) and disappeared after the fix; **Dart.Run** printed `Total area: 9.14` and exit code 0; **Dart.RunTests** 3 passed; **Dart.RunTestFile** on a failing test showed `FAIL  wrong sum` and put `Test failed: wrong sum: Expected: <3>` in the Error List at `failing_test.dart` line 4; **Dart.Analyze** listed the `undefinedThing` error in the Error List and 0 errors after the fix; **Dart.Debug** with a breakpoint stopped in break mode in `main` (frame language Dart), Watch `shapes.length + 40` = 42, Locals `arguments`, `shapes`, Continue ended the session with the output in Debug Output; **Dart.DebugTestFile** stopped in the test closure and ended; Visual Studio exited; no Vistudio/Dart errors in the ActivityLog.

Flutter sample, 25 checks passed: the analysis server ran for the Flutter package; **Dart.Run** built and started the app on Windows (`flutter run --machine`), its output reached the Dart pane; editing the title in the editor and saving hot reloaded the running app (`Reloaded 1 of 1051 libraries`, the app printed the new title); **Dart.HotRestart** and **Dart.HotReload**; **Dart.Stop** ended flutter run and no `flutter_counter.exe` remained; **Dart.Debug** with a breakpoint in `build()` stopped in `_MyHomePageState.build`, Watch `widget.title` = "Reloaded On Save", Continue, app output in Debug Output; editing and saving during the debug session hot reloaded it (Dart pane "Hot reload (debug session) done", Debug Output shows the new title); **Stop Debugging** ended the session and no app process remained; **Dart.RunTests** passed the widget test; **Dart.RunIntegrationTests** passed `counter app tapping the button increments the counter` on Windows (36.7 s) with no app left afterwards; Visual Studio exited; no Vistudio/Dart errors in the ActivityLog.

Two prompts of the environment blocked an earlier run and are now answered in the script's workspace copy: Visual Studio's "Inconsistent Line Endings" (the script itself had inserted an LF line into a CRLF file) and "CMake Integration" (with the C++ workload installed, Visual Studio offers CMake support for the Flutter project's `windows\CMakeLists.txt`; the copy gets `.vs\CMakeWorkspaceSettings.json` with `enableCMake: false`, which is what "Do not enable" stores). A user opening a Flutter folder sees the CMake question once.

## Found and fixed while validating

- The analysis server rejected the first frame (`FormatException: Invalid value in input: 239`) when started from the test console: .NET Framework writes `Console.InputEncoding`'s preamble into a redirected stdin at `Process.Start`, a UTF-8 BOM under `chcp 65001`. Visual Studio has no console (ANSI code page, no preamble) and the IDE checks above show the server working there; the test runner now switches its console input encoding to UTF-8 without BOM. Not fixed in the product: on a system whose ANSI code page is UTF-8 (the Windows "Beta: Use Unicode UTF-8" option) every stdio server started by the extension would get a BOM.
- In Visual Studio, **Stop Debugging** of a Flutter session left `flutter_counter.exe` and the Dart development service running: the Flutter adapter exited after `disconnect` without stopping them, and nobody closed the adapter's job object. The next Windows build then hung on the locked executable. `DartDapConnection` now ends the adapter's whole process tree as soon as the adapter exits; the live Flutter debugging check and the IDE script now assert that no app process remains.
- The Dart adapter reports `stopped` with reason `entry` while it configures breakpoints and resumes by itself (`continued` follows), so Visual Studio leaves break mode again on its own; the live checks wait for the breakpoint stop.

## Not verified

- Visual rendering in the IDE: TextMate coloring, squiggle look, CodeLens rendering and clicking a lens (no screen access). The CodeLens commands themselves were exercised through the same services (`Dart.Run`, `Dart.Debug`, `Dart.RunTestFile`, `Dart.DebugTestFile`), and the reference counts through the analysis server's `textDocument/references` in the live checks.
- Completion popups, Go To Definition and Find All References inside the IDE editor (checked against the analysis server over LSP in the live checks, not through Visual Studio's UI).
- F5 on a `.dart` startup item and `launch.vs.json` targets (the debug path was driven through **Dart.Debug**, which uses the same coordinator), **New Flutter App...**, **Pub Get**, **List Devices** and the device drop-down in the IDE, devices other than Windows (Chrome, emulators), Dart-only SDK installs, x64 Visual Studio.
- The Release build and `build.ps1` end to end (Debug builds were used).
