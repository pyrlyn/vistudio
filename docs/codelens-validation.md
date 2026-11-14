# CodeLens validation (T35)

Checks actually performed for Kotlin and Java CodeLens. Visual Studio Community 2026 18.12.12405.243, Kotlin LSP 271.2085.0, Eclipse JDT LS from `redhat.java` 1.56.0, Gradle JDK 11.0.31.

## Server probe

A direct LSP session (`--stdio`) on `samples/kotlin-jvm`:

- `initialize` advertises `codeLensProvider` (`resolveProvider: false`), but `textDocument/codeLens` returns `[]` for `Greeting.kt` and `Main.kt` after the Gradle import. The lenses are therefore computed by the extension.
- `textDocument/references` at `Greeting` (0-based 2:11) returns 4 locations (`Main.kt`, `EvaluationMain.kt` ×3). `textDocument/implementation` returns `null` for a final data class.
- The classic Visual Studio LSP client does not render `textDocument/codeLens`.

## Unit checks

`Vistudio.Tests.exe --codelens`: 26 assertions on `CodeDeclarationScanner` (Kotlin and Java declarations, nesting, implementable flags, entry points, name positions and declaration starts, strings/templates/text blocks/comments, local declarations ignored, broken code). Full suite: 587 assertions, exit 0.

Follow-up review: 84 assertions (`--codelens` now lists every failing check). Added Kotlin edge cases (multi-line headers, expression bodies with lambdas and call chains, accessors and `init`, lambda defaults in the primary constructor, delegation, `where` clauses, named companions, Kotlin enums, semicolons, annotated and backticked members, soft modifiers as names, destructuring, char literals with quotes, nested templates and comments, nested blocks, abstract members, sealed hierarchies, `expect`/`actual`, CRLF), Java edge cases (initializers, `throws`, array/lambda initializers, annotation types, enums with no or annotated constants, compact record constructors, sealed hierarchies, nested generics, overloaded constructors, strings, varargs, CRLF), entry-point and implementable rules, and `CodeLensElement` (unique identifiers with overload ordinals and their round trip, foreign identifiers ignored, name position inside the name for every declaration, backticks, CRLF, clamping, count labels). Three of them failed before the fixes: a lambda default in a Kotlin primary constructor hid the class members, `fun String.main()` and Java `public static int main` were entry points. The `null`-reply refresh loop in the count lens was found by reading the code and has no automated check (VSIX code). After the fixes and the step 5 changes: `--codelens` 121 assertions, full suite 746 assertions, exit 0 (run without `NoDefaultCurrentDirectoryInExePath`, which otherwise fails the fake-wrapper check in `ProjectFileTests`); VSIX Debug build exit 0.

## Native Visual Studio

The VSIX was built (Release, no warnings) and installed with VSIXInstaller into the isolated root suffix `VistudioCodeLensTest` (exit 0, `/UpdateConfiguration` exit 0). Copies of `samples/kotlin-jvm` and `samples/kotlin-java-jvm` under `artifacts/codelens-native` were opened as folders. Lens labels were read from a temporary trace in the extension (removed afterwards) and checked on screen; clicks were made on the lenses in the editor.

Kotlin (`kotlin-jvm`):

| Declaration | Lens | Result |
| --- | --- | --- |
| `fun main` (`Main.kt`) | `Run \| Debug \| 0 references` | First `- references` while the server imported, then `0 references` without user action |
| `data class Greeting` | `4 references` | Same 4 locations as the server probe; click opened Find All References ("'Greeting' references") |
| `Greeting.message()` | `2 references` | |
| `interface Shape` (test file `Shapes.kt` in the copy) | `3 references \| 3 implementations` | Click opened "'Shape' implementations" with `Square`, `Cube`, `Circle` |
| `Shape.area()` | `1 reference \| 2 implementations` | |
| `open class Square` | `1 reference \| 1 implementation` | |
| `Run` | | Gradle build, then Debug Output: `BUILD SUCCESSFUL`, `Process finished with exit code 0` |
| `Debug` | | Breakpoint on `println` in `main` bound and stopped (break mode, current-line arrow); Continue finished the session |

Java (`kotlin-java-jvm`, JDT LS):

| Declaration | Lens |
| --- | --- |
| `interface Payment` (test file `Payment.java` in the copy) | `2 references \| 2 implementations`; click opened "'Payment' implementations" with `Cash`, `Card` |
| `Payment.amount()` | `1 reference \| 2 implementations` |
| `class Cash` / `Cash.amount()` | `1 reference` / `1 reference` |
| `JavaEntry.describe` | `1 reference` (the Kotlin call in `MixedMain.kt` is not counted: JDT LS does not see Kotlin) |
| `JavaEntry.main`, `JavaEntry()` | `0 references`; `main` also has `Run \| Debug` |

## Further lens kinds (overrides, expect/actual, Compose preview, Gradle tasks)

Server probe (Kotlin LSP 271, direct `--stdio` session):

- `textDocument/implementation` on `expect fun platformName` (`samples/kotlin-multiplatform` copy) returns the four `actual`s (`jvmMain`, `jsMain`, `wasmJsMain`, `mingwX64Main`); on an `actual` it returns `null`, and `textDocument/definition` on either points at itself. No `declarationProvider`.
- On `override fun area()` in `Square`, `textDocument/definition` points at itself and `implementation` is `null`; `textDocument/prepareTypeHierarchy` at the member returns `Square`, and `typeHierarchy/supertypes` returns its supertypes (`Any` from the stdlib sources jar as a `jar:` URI, and `Shape`). The lens therefore walks the hierarchy and scans each supertype source.

Unit checks: `CodeLensKindsTests` (traits, identifiers with traits, Gradle script tasks and project paths, `actual` → `expect` lookup across source sets, reading a `jar:` source, base member lookup). `--codelens`: 121 assertions; full suite: 848 assertions, exit 0.

Native Visual Studio (`VistudioCodeLensTest`, files opened through the instance's DTE; labels read from a temporary trace, removed afterwards; clicks on the lenses):

| File | Lens | Result |
| --- | --- | --- |
| `kotlin-multiplatform/build.gradle.kts` | `Run task` above each `tasks.register("verify…")` | Tooltips `Run Gradle task :verifyJvm` … `:verifyTargets`; click on `verifyJvm` started the copy's Gradle wrapper, Output: `BUILD SUCCESSFUL` |
| `kotlin-multiplatform/src/jvmMain/kotlin/Platform.jvm.kt` | `expect in commonMain` above `actual fun platformName` | Click opened `commonMain/kotlin/Platform.kt` with the caret on `platformName` (line 4, column 12) |
| `compose-multiplatform/.../Cards.kt` | `Show preview` above the three `@Preview` functions | Click opened the Kotlin Compose Preview window listing the file's previews |

Not verified natively yet: `overrides Base.member`, `N overrides` and `N actuals (…)`, which need the Kotlin server in Visual Studio. During this check another test IDE held the shared `--system-path` cache of the global settings (kotlin-lsp 271 allows one server per path), so the Kotlin client of the test instance could not initialize. The server requests they use were checked directly (above).

## Findings during validation

- VisualStudio.Extensibility parts filtered by the `kotlin` document type or by a `**/*.kt` glob were not attached reliably: Visual Studio assigns the content type after the view opens. The parts apply to the `text` document type and filter by file extension in code.
- The IDE must not inherit `NoDefaultCurrentDirectoryInExePath` (as in the other native scripts); with it, the Gradle build behind Run failed with exit 1. Since 2026-11-14 `GradleCommand` starts the wrapper as `.\gradlew.bat`, so the variable no longer breaks Gradle commands; the native scripts still clear it and were not re-run after the fix.

## Not verified

- Refresh of counts after edits in the IDE: the IDE was driven by clicks only, without typing.
- Run | Debug above tests (coordinated with T34).
- `overrides`, `N overrides`, `N actuals` lenses in Visual Studio (see above), and the Java `@Override` base through JDT LS type hierarchy.
