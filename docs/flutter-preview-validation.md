# Flutter widget preview: design and validation

What the Flutter widget preview does, how it is built, and which checks were run with which result. Only checks listed here were performed.

## Design

| Part | Where | Responsibility |
| --- | --- | --- |
| Project and SDK detection | `Core/Dart/Preview/FlutterProjects.cs`, `FlutterSdkLocator.cs` | Flutter packages (`dependencies:` contains `flutter: sdk: flutter`; a `flutter_test`-only dev dependency does not count), workspace scan (skips `build`, `.dart_tool`, `.widget_preview`, links), SDK search order setting > `FLUTTER_ROOT` > PATH > fvm > mise > asdf > standard folders, version check (3.35+). `DartToolchain` uses the same search for Run, Debug and tests. |
| Discovery | `WidgetPreviewScanner.cs`, `WidgetPreviewLsp.cs` | Lexical scanner for `@Preview` on functions, static methods, constructors, factories and same-file `MultiPreview` subclasses, with argument parsing (`name`, `group`, `size`, `textScaleFactor`, `brightness`, `theme`, `localizations`, `wrapper`) and problems (private, instance method, required parameters); widget classes (direct, same-file and well-known bases; other bases through the analysis server's type hierarchy) with a no-argument construction. Analysis server answers (`dart/textDocument/getFlutterWidgetPreviews`, document symbols, type hierarchy) are parsed and merged. |
| Previewer session | `FlutterPreviewSession.cs`, `DtdClient.cs` | `flutter pub get` when needed; `dart tooling-daemon --machine`; a dedicated analysis server (`dart language-server`) connected with `dart/connectToDtd`; waiting until it registered the widget preview methods on DTD; `flutter widget-preview start --machine --web-server --dtd-url=...`; the DTD `Editor` service (`getActiveLocation`, `navigateToCode`, `activeLocationChanged`, `themeChanged`); event parsing; hot restart fallback after a failed hot reload; preview error reports; process ownership through job objects. |
| Widgets without `@Preview` | `PreviewSourceGeneration.cs` | Temporary host project in `%TEMP%\Vistudio\flutter-preview` with a path dependency on the package and generated `@Preview` functions; `Touch()` after saves makes its previewer reload. The quick fix computes the insertions for a `@Preview` function and the import. |
| Configurations | `PreviewConfigurations.cs` | Device presets, text scales and locales from the settings; expansion of the toolbar choice into up to six configurations; the WebView2 document script (root font size, `navigator.languages`); the persisted window state. |
| IDE | `Vsix/Dart/Preview/*` | Tool window with WebView2 (one page per configuration, reused while the previewer runs; color scheme through DevTools media emulation, text scale and locale through a live script, zoom for device sizes), service (session lifetime, SDK and version messages, notices, status bar), CodeLens, light bulb, edit/save tracker, commands in `DartCommands.vsct`, settings in Tools > Options > Dart. |

The previewer, its analysis server and DTD are separate from the IDE's Dart language client and from the Dart debugger.

## Machine

Windows 11 Pro (ARM64 under Parallels; the test console ran as x64), Visual Studio Community 2026 18.12, Flutter 3.50.0 stable (Dart 3.14.0) in `C:\Users\listepo\tools\flutter` (not on PATH; found by the SDK search as a standard folder), Microsoft Edge for the headless page checks.

## Automated checks without Visual Studio (`tests/Vistudio.Tests`)

`Vistudio.Tests.exe --flutter-preview none` runs the unit checks (also part of the default run); `--flutter-preview` adds the live stages against a real SDK (`VISTUDIO_FLUTTER_SDK`, the Dart settings, or the SDK search; without a Flutter 3.35+ SDK or Edge/Chrome the live part prints `SKIP` and passes).

Unit checks (138 assertions, pass): pubspec Flutter dependency (dev-only, commented, flow mapping, CRLF, hosted `flutter`, top-level `flutter:`), workspace scan (skipped folders, depth), SDK search order with a fake environment (setting incl. `bin`, invalid setting not replaced, `FLUTTER_ROOT`, stale `FLUTTER_ROOT`, PATH, fvm `.fvmrc` / fvm 2 config / path-traversal pin rejected, mise pin in a parent `mise.toml` and newest install, asdf `.tool-versions`, standard folders incl. Scoop), versions and the 3.35 minimum, the scanner (top-level, static, named constructor, factory, instance method rejected, private, required parameters, builder, same-file MultiPreview, comments/doc comments/strings ignored, annotation arguments, prefixed imports, widget classes direct/indirect/known bases/unknown bases/abstract/private/generic, constructions), the "Preview Widget" choice for a caret, analysis server answers (previews, DTD wrapper, merge of server-only previews, document symbols, framework widget supertypes), configurations (presets, scales, locales, expansion, cap of six, custom clamp, fallbacks, document and live scripts), the window state round trip, the quick fix (import, placement, unique name, CRLF, prefixed import, no fix without a no-argument constructor), the host project (import URIs, pubspec, generated previews scanned back, touch revision, user files untouched), previewer events and their classification, command lines, LSP framing across 7-byte chunks, settings validation.

Live stages, Flutter 3.50.0, on copies of `samples/flutter-preview` (last run on the final code: `PASS: 182 assertions`, 138 unit + 44 live, stages `dtd,preview,host,cancel,twopages`):

| Stage | Checked |
| --- | --- |
| `dtd` | `dart tooling-daemon --machine` address; service calls between clients; Editor events; earlier registrations are replayed to a new `Service` listener (so a page that connects later sees the Editor service); the preview analysis server registers the widget preview methods on DTD (5-10 s after `dart/connectToDtd`, only after its first analysis: the reason the session waits before starting the previewer, whose own limit is 30 s) |
| `preview` | pub get by the session; `widget_preview.started` URL (166 s on the first, cold web build); page served over HTTP; "Done loading previews" with a headless Edge as the page; the session owns 11 processes; the analysis server's previews equal the scanner's (names and lines); Editor service `getActiveLocation` / `navigateToCode`; scaffold `Connected`; hot reload after a file change (about 5 s); regenerated scaffold; a code error marks both previews `hasError` (the previewer leaves the library out rather than failing) and is reported; the fix reloads; hot restart through the previewer's DTD service; Stop ends all 11 processes in 0.2 s |
| `host` | wrapper for `StatusBadge` (no `@Preview`) is a valid preview; a change in the user's package plus `Touch` reloads the host previewer; the user's `lib` is unchanged |
| `cancel` | Stop during a start cancels it at once; no process left |
| `twopages` | a second page of the same previewer starts its app (two connected) |
| all | none of the processes owned by any session is running at the end |

## Visual Studio end to end (`tests/verify-flutter-preview.ps1`)

The script installs the Debug VSIX into the isolated root suffix `VistudioDart` (uninstalling the earlier 0.1.0 build first), opens a copy of `samples/flutter-preview` (plus a Dart-only package `tools\plain_dart`) with Open Folder, sets the SDK and the verbose previewer log only in that copy's `.vs\settings.json`, saves the per-user window choice "Phone, Light | Dark, 1.3x, ar" (restoring the user's own afterwards) and drives the IDE through DTE and UI Automation (the window's controls have `FlutterPreview.*` automation ids). Screenshots go to the evidence folder.

Last run on the final code: `PASS: 50 native Visual Studio checks` (VSIX built from this tree; earlier runs of the growing script found the problems listed below). Checked:

| Area | Checks |
| --- | --- |
| Registration | the four commands exist (`Dart.FlutterPreviewWidget`, `Dart.PreviewWidgetInEditor`, `Dart.PreviewWidgetFile`, `Dart.FlutterWidgetPreviewWindow`); Tools > Options > Dart through DTE shows "Flutter SDK in use" (`C:\Users\listepo\tools\flutter - Flutter 3.50.0 (standard folder)`) and the widget preview settings |
| Visibility | available in a Flutter package's `.dart` file, hidden in the Dart-only package's file, available again |
| Start | the tool window opens; the SDK comes from the workspace setting; the page loads in WebView2 ("Done loading previews"); previewer, daemon and analysis server run |
| Configurations | both pages of "Light \| Dark" load and start the previewer app; the package name shows; choosing Dark is saved at once and switches the color scheme without a reload; text scale and locale changes reconfigure the running page in place; no page was reloaded (screenshots `01-light-dark-phone*.png`, `02-dark-fit*.png`) |
| Restart / reload | the Restart button hot restarts with every page answering; saving the file hot reloads; a code error shows "2 previews are not rendered because the code has errors ..." in the window and the Dart pane, the fix reloads and the notice goes away; with `Reload = AfterDelay` (this window's `.vs\settings.json`) typing alone saved the file and reloaded |
| Widgets without `@Preview` | Preview Widget in the editor with the caret in `StatusBadge` uses the temporary host project, its previewer loads, the window explains it and offers **Add @Preview to source**, `status_badge.dart` is unchanged; **Add @Preview to source** writes `@Preview(name: 'StatusBadge') Widget statusBadgePreview() => const StatusBadge();` and the import, saves, and the package's own previewer shows it |
| Solution Explorer | `lib\widgets\greeting_card.dart` selected through UI Automation in the Open Folder view: **Preview Widget** is available and previews the package |
| SDK errors | a wrong `vistudio.dart.flutterSdkPath` is reported in the window with **Set Flutter SDK path...** |
| Lifetime | closing the window stops the previewer and leaves no process; the window command opens it again; closing the folder (`File.CloseFolder` returned E_FAIL in Open Folder, `File.CloseSolution` closed it) stops the previewer and leaves no process; after reopening the folder a previewer runs, Visual Studio exits 0.5 s after `DTE.Quit` and no previewer process is left |
| Errors | no Vistudio/Dart/Flutter errors in the ActivityLog |
| CodeLens | rendered "Preview" above `class GreetingCard` and above both `@Preview` lines (screenshot `01b-codelens-on-previews.png`); not invokable through UI Automation, see below |

Earlier (shorter) versions of the script also showed, with Visual Studio killed while a previewer ran, that the job objects ended all previewer processes.

## Problems found by these runs and fixed

- The analysis server registers the widget preview DTD methods only after its first analysis; on a cold cache the previewer's 30 s wait ran out. The session now waits for them (up to 5 min) before starting the previewer.
- `OwnedProcess`: its `Exited` handler read `ExitCode` after disposal and threw on a thread-pool thread (it crashed the test process and would crash devenv).
- Closing the window was not detected: a view helper set on a `ToolWindowPane` frame is not called, and Visual Studio 18 reports closing as `WinHidden`. The window subscribes with `IVsWindowFrame2.Advise` and treats `WinHidden` (unless auto-hidden) and `WinClosed` as closing.
- Visual Studio needed 60 s and a kill to exit: the close-folder handler awaited the full stop, which switched to the UI thread during shutdown. It now starts the stop and returns (exit in about 1 s).
- Stop during a long first start waited for the start; Stop now cancels it.
- The previewer's debug service (DWDS) keeps counting every page instance that ever connected, including reloaded pages, and waits for all of them on hot reload and hot restart ("Hot reload timed out - received 0/3 responses"). The window therefore never replaces or reloads pages while a previewer runs: pages are reused, the color scheme changes through DevTools media emulation, text scale and locale through a live script (Flutter web follows the root font size and `languagechange`), unused pages are parked; a hot restart that still does not reach every page restarts the previewer. The Reload button was removed for the same reason.
- Pages opened at the same moment left one app not started; pages now open one after another.
- A page could stay unopened after a quick second rebuild, or when its initialization completed before it was in the page pool; both fixed.
- The acceptance script wrote a test file with mixed line endings, which made Visual Studio show a modal dialog and reject automation.

## Not covered, or known limitations

- The **Preview CodeLens** renders (screenshots) but Visual Studio does not expose it to UI Automation, so the end-to-end run uses the equivalent editor command; clicking a lens was not automated. The light bulb ("Add @Preview for ...") was not automated either; the same insertion is checked through the window's **Add @Preview to source** action and the unit checks.
- **Theme**: the window sets the page's `prefers-color-scheme`, i.e. the platform brightness of previews (the previewer's per-preview brightness toggle follows it). Widgets look dark only when their theme uses that brightness (for example `@Preview(theme: ...)` or a theme built from the platform brightness); the sample's cards stay light.
- **Locale**: it becomes the platform locales; previews without `localizations:` resolve to the previewer's default `en-US`, so a right-to-left locale shows no visible change for them.
- Text scale and size presets do not override values given in `@Preview(...)`.
- Assets referenced without a package prefix do not load in the temporary host project (the user's package is a dependency there).
- The official previewer writes `.widget_preview\` into the package and adds it to `.gitignore`.
- Seen in the IDE, not caused by the preview code: intermittent `Vistudio.Zig.ZigEnvironment` composition failures at startup (2 of 11 runs; reported as a warning by the script), "Unknown method workspace/didChangeWatchedFiles" banners from the IDE's Dart language client, and `ArgumentException` banners from Visual Studio's LSP structure tagger after automatic saves.
- Only Windows (ARM64 VM, x64 test process) with Flutter 3.50.0 was tested; older 3.35-3.4x SDKs only through the unit checks.
