# Resolved Gradle import validation

## Build and automated tests

`./build.ps1` passes without warnings: 303 regression assertions, VSIX asset/registration checks (including `Build/ImportModel.gradle`) and vector icon rendering checks. `GradleModelTests` covers schema/root validation, successful wrapper output, nonzero-exit rejection, in-flight process cancellation, canceled commits, skipped/unresolved configuration handling, selected-version requirements and preservation of manually maintained XML.

`./tests/test-gradle-import.ps1` passed all 12 real integration checks. It creates an isolated workspace from `tests/fixtures/gradle-import`, reuses the existing checksum-pinned Gradle 8.14.7 wrapper and installed Microsoft JDK 11.0.31, and uses Kotlin JVM plugin 2.4.21. No pinned versions or extension package dependencies were changed.

Evidence workspace: `artifacts/gradle-import-ab5e34375f7745309a7b17db2e00dee3`. Its `integration.log` records the following:

- Imported root, `:app` and `:shared`, with 39 configuration records, 41 configuration-scoped library records and 10 framework records. The configuration count includes explicitly skipped tooling configurations; these are not claimed resolved.
- `:app / runtimeClasspath` requests Kotlin stdlib 2.4.20 but selects 2.4.21. Selection reasons include `forced`.
- Transitive `org.jetbrains:annotations:13.0` and project reference `:shared` are present.
- The applied Kotlin JVM plugin and Kotlin standard library markers carry selected version 2.4.21.
- Reimport does not duplicate generated entries. Removing `:shared` from settings/dependencies removes the stale module/reference on import; the fixture is then restored and imported again.
- A real missing dependency in `brokenRuntimeClasspath` causes exit 1 and leaves the prior `.kotlinproj` byte-for-byte intact. Pre-cancel also preserves it; the pure regression test additionally cancels an already-running wrapper.
- Gradle actually compiles both Kotlin modules and runs `:app:run`, printing `Resolved Gradle import works` and exiting 0.

## Native Visual Studio acceptance

Installed the built package with VSIXInstaller into isolated profile `VistudioImportTest`, completed `/UpdateConfiguration`, and opened the same real multi-module workspace in Visual Studio 2026 Community 18.12. No normal-profile extension installation or global settings changes were made.

Installed and built assemblies match:

| File | SHA-256 |
| --- | --- |
| Vistudio.Vsix.dll | `0CD20AA36FF4388CC018C734B39D6535411C47991E8EDF76CD747F8D865D0AE5` |
| Vistudio.Core.dll | `C15C6454C039FB6400955C44407524B32EE7D48F745F87C234E1CD6A260611AC` |
| Build/ImportModel.gradle | `71C650CF4A38423BC2D9AFAD6862178BE0F89D10BD80B4613F0E729ECC61E31F` |

Observed **Tools > Kotlin: Project Explorer** and **Kotlin: Import Gradle project** in the native menu. Clicked **Import from Gradle** inside the actual tool window. The UI disabled import/settings controls, exposed Cancel import, invoked Gradle in Kotlin Output and completed successfully. The generated XML and the window both showed `ImportedAtUtc=2026-11-12T17:29:21.1342923Z` after this IDE invocation.

Expanded and inspected:

- **Modules (3)**: root, `:app`, `:shared`.
- **Libraries (41) > Module :app > runtimeClasspath**: `org.jetbrains.kotlin:kotlin-stdlib — 2.4.21`, `org.jetbrains:annotations — 13.0`, and `project :shared`.
- The stdlib details expose `Version: 2.4.21`, requested 2.4.20/2.4.21, `Relationship: Direct`, `SelectionReason: requested; forced`, and `Origin: Gradle ResolutionResult`. Native accessibility evidence is saved in `artifacts/gradle-import-ui-library.txt`.
- **Frameworks (10) > Module :app > buildscript:classpath** displays `Kotlin/JVM Gradle plugin — 2.4.21`. Its expanded entry shows `PluginId: org.jetbrains.kotlin.jvm`, `Evidence: org.jetbrains.kotlin:kotlin-gradle-plugin`, `ResolutionModule: :` and the applied-plugin/resolution-result origin. Native accessibility evidence is in `artifacts/gradle-import-ui-framework.txt`.

Also introduced the local missing-dependency fixture condition and clicked Import inside the IDE. The explorer reported `Gradle model import failed (exit 1). Previous metadata is unchanged. See Kotlin Output.` The prior tree remained available. Before/after `.kotlinproj` SHA-256 was identical: `AE12923BCB046424B983C52BBF624BA61EB763D2A11746D330124B7F52C08D08`. The failure condition was removed before the final recovery import.

The recovery import also succeeded from the explorer button. Both native UI and XML report `ImportedAtUtc=2026-11-12T17:35:18.1554172Z`, with the same 3 modules, 41 library records and 10 framework records. Evidence: `artifacts/gradle-import-ui-recovery.txt`. The isolated IDE remains open for inspection. Task-specific package: `artifacts/Vistudio.Kotlin.GradleImport.vsix`, SHA-256 `63EA7BD74260C4FB64ADEB28E0DA32986BD17377ABFBCEB0DAE21B5EDBA094BC`.

The application log is `artifacts/gradle-import-ActivityLog.xml`. UI element addressing for floating tool windows was intermittent; screenshot-based coordinate clicks succeeded. Only observed native behavior above is claimed.

## Boundaries

This acceptance covers a real multi-module Kotlin/JVM project on Windows. Spring/Boot, Ktor, Compose, JUnit, Android and Multiplatform marker/scope support is implemented but not validated on separate real framework/platform projects in this task. Composite/included builds are referenced, not recursively imported. Gradle plugin versions unavailable through the applied project's/ancestor's buildscript resolution are shown as unavailable rather than guessed. Graph resolution does not imply artifact download verification or compiler/toolchain compatibility for every target.
