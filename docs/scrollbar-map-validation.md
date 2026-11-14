# Scroll bar map validation (T40)

Checks actually performed for the Kotlin/Java scroll bar map mode settings. Visual Studio Community 2026 18.12.

## Option names

The editor option ids come from the installed editor (`Microsoft.VisualStudio.Editor.Implementation.dll`, `UnifiedSettings/registration.json`) and the public `DefaultTextViewHostOptions` names. Visual Studio's own "Source overview" values are Off 0, Narrow 33, Medium 58, Wide 83 pixels.

A first mapping assumed `OverviewMargin/ShowSourceImageMargin` was map mode. A live trace showed its global value is `True` even in bar mode, while `OverviewMargin/ShowEnhancedScrollBar` is `False`; the latter is "Use map mode". The mapping was corrected before the checks below.

## Unit checks

`ScrollBarMapTests` (in `Vistudio.Tests.exe`): defaults inherit, Inherit sets nothing, Bar only turns map mode off, Map sets code image/width/preview, width Off keeps map marks without the code image, pixel widths, settings XML round trip and old settings files. They passed. The full suite stopped later on an unrelated project-file test (`Selected JDK reaches the wrapper process...`) from concurrent work.

## Native Visual Studio

The Debug VSIX was installed with VSIXInstaller into the isolated root suffix `VistudioMapTest` (exit 0, `/UpdateConfiguration` exit 0). A copy of `samples/kotlin-java-jvm` sources was opened. Values were read by a temporary trace in the extension (environment variable, removed afterwards) 8 seconds after each view was created: the view's local and global option values and the actual widths of the overview margin elements. The screen was not inspected (another session held computer control).

| Settings | File | Local options (global) | Margin widths |
| --- | --- | --- | --- |
| Map, Narrow | `MixedMain.kt` (active) | map mode `True` (`False`), code image `True`, width 33 (100), preview `True` | overview 50, code image 33 |
| Map, Wide (first mapping) | `Ledger.java` (active) | width 83 (100) | overview 101, code image 83 |
| Bar | `MixedMain.kt` (active) | map mode `False` (`False`); others inherited | overview 19, code image collapsed |

The global values stayed at the profile defaults (bar mode), so other languages keep the Visual Studio setting. The temporary `settings.xml` entries were removed afterwards.

## Not verified

- Reapplying to open editors after OK in Tools > Options (`ScrollBarMapOptions.SettingsChanged`): needs UI input, not driven.
- Hover preview and click/drag on the map: built-in Visual Studio behavior, not exercised.
- Gradle build errors as map marks: they are the existing `IErrorTag` squiggles, which Visual Studio marks in the scroll bar; not rechecked here.
