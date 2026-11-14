# Multiplatform and editor feature validation

## Performed checks

- Release VSIX build with the existing VSSDK/NuGet versions. The final feature build has no compiler or analyzer warnings.
- Focused regression executable: `Vistudio.Tests.exe --target-features` passes **72 assertions**, covering task discovery records, qualified target forwarding, wrapper invocation with spaced paths, Unicode metadata, failed discovery, settings migration, brace matching, nesting, strings/templates/multi-dollar interpolation, comments, incomplete input, deep nesting and existing diagnostic parsing.
- VSIX archive verification passes: editor registrations for `.kt`/`.kts`, registration asset installation, two resolvable embedded vector icons with matching monikers, packaged discovery script, and exclusion of host SDK DLLs.
- `tests/test-multiplatform.ps1 -UseWindowsTrustStore` passes against the real sample and compilers. It discovers **192 Gradle tasks** through the same command/process/catalog implementation used by the extension.
- Real `build` succeeds for JVM, Windows Native (`mingwX64`), Kotlin/JS and Kotlin/Wasm. Separate `verifyTargets` executes all four runtimes and produces four successful assertion markers.
- An intentionally invalid temporary common source fails each of `compileKotlinJvm`, `compileKotlinMingwX64`, `compileKotlinJs`, and `compileKotlinWasmJs`. Each compiler's output is parsed into the expected file, zero-based line 1, and error severity. Each process returns nonzero.
- The temporary source is removed and `verifyTargets` succeeds again on all four runtimes. Only the original `Main.kt` remains in `src/commonMain/kotlin`.
- The full Release build passes **262 assertions**, VSIX asset checks, and vector rendering checks at 100% and 200% DPI, without compiler/analyzer warnings.
- Actual Visual Studio acceptance in `VistudioTargets` found blank icons. The Image Service rejected bare `DrawingImage` roots with "Unsupported element type. The element must be a UIElement or a BitmapSource." Both assets now wrap the vector in an `Image`; the manifest ships at the extension root, with assembly codebase registration. Distinct source/script icons are visible after restarting the host. Packaging and rendering regressions cover this defect.
- `.kts` opens by double-click in the standard code editor with Kotlin coloring, without Open With. With **Preview Selected Items** enabled, one click opens `.kt` in preview. The extension respects the host's preview preference.
- The dark-theme editor visibly gives matching colors to brace partners, different colors to neighboring lambdas and nested blocks, and preserves string/comment coloring.
- The configured real Kotlin LSP publishes two intentional errors on lines 9 and 10 of the disposable `artifacts/editor-acceptance/src/EditorAcceptance.kt`. Squiggles and inline messages are visible before building; messages follow the complete trailing comments without overlap. Gradle Build from the Tools menu also publishes both failures in Error List.
- Editing the source in Visual Studio removes both live errors. A subsequent **Kotlin: Build with Gradle** completes with `BUILD SUCCESSFUL` and zero errors. The remaining redundant-explicit-type inspection is a server warning, not a build failure.

Logs: `artifacts/multiplatform-tests/discovery.log`, `build.log`, `run.log`, four `*-failure.log` files, and `recovery.log`. Earlier failed exploratory logs are retained separately and are not successful acceptance evidence.

The earlier concurrent T12 regression failure is resolved in the current shared tree. The final package was built with the complete strict `build.ps1`, without `-SkipTests`; T12 changes are preserved.

## Kotlin targets in the project file and Solution Explorer (T21)

The sample now has `expect`/`actual` sources (`commonMain/Platform.kt`, `jvmMain`, `jsMain`, `wasmJsMain`, `mingwX64Main`, shared `webMain`), so the T13 statement above that only `Main.kt` remains describes the sample at that time.

- Real `gradlew build` of the sample succeeds and `verifyTargets` prints `Platform: JVM 11`, `Platform: Native mingwX64`, `Platform: Web/JS`, `Platform: Web/Wasm`, each followed by `VISTUDIO_KMP_PASS` (JDK 11, Kotlin 2.4.21, Gradle 8.14.7, Windows trust store for the test process only).
- `ImportModel.gradle` run on the sample exports 4 `Target` items and 16 `KotlinSourceSet` items: jvm (`:jvmMainClasses`, `:jvmTest`, `:jvmRun`), js (Node.js; `:jsMainClasses`, `:jsTest`, `:jsNodeDevelopmentRun`), wasmJs (Wasm/JS, Node.js; `:wasmJsNodeDevelopmentRun`) and mingwX64 (`mingw_x64`; `:mingwX64MainKlibrary :linkDebugExecutableMingwX64`, `:mingwX64Test`, `:runDebugExecutableMingwX64`), with the default hierarchy (`mingwX64Main` → `mingwMain` → `nativeMain` → `commonMain`, `jsMain`/`wasmJsMain` → `webMain` → `commonMain`).
- Unit suite (`Vistudio.Tests.exe`, Debug build): **422 assertions** pass, including `KotlinTargetTests`: model validation (duplicate targets/source sets, missing platform, unknown module), project kinds, target ids and descriptions, `StartupTarget` normalization/rejection, run-task replacement, F5 target selection (JVM kept, startup target, stale target, single and several non-JVM targets), the F5 message, tree layout (shared sets, Targets order, per-target source sets and dependencies, remaining module configurations, startup marking, document lookup) and JVM-only entry-point candidates.
- `tests/test-kotlin-targets.ps1 -UseWindowsTrustStore` passes **22 assertions** against the real sample through the production importer: `ProjectKind` Multiplatform, `Targets` `:jvm :js :wasmJs :mingwX64`, real run tasks, Native/Wasm/JS variants, source-set hierarchy, the tree (`commonMain`, `webMain`, `Targets` with each target's own `*Main` and `Dependencies`, kotlin-stdlib in `jvmRuntimeClasspath`), F5 entry point `sample.MainKt` from `commonMain`, a Wasm startup target, and a Gradle build plus run of every target with its imported tasks, each printing its own platform.

- The existing `tests/test-multiplatform.ps1 -UseWindowsTrustStore` still passes on the changed sample: 192 discovered tasks, build and self-tests on all four runtimes, located compiler failures for `compileKotlinJvm`, `compileKotlinMingwX64`, `compileKotlinJs`, `compileKotlinWasmJs`, and recovery.

Logs: `artifacts/kotlin-targets/` (`import.log`, `vistudio.kotlinproj`, `tree.txt`, `<target>-build.log`, `<target>-run.log`).

## Environment findings

The installed antivirus intercepts HTTPS with a CA trusted by Windows but not Java/Node's bundled stores. Acceptance used Windows system roots only in the test process (`JAVA_TOOL_OPTIONS` and `NODE_USE_SYSTEM_CA`), with original variables restored. TLS verification was never disabled, and no global trust configuration was changed.

Combining Gradle `build` and development JS/Wasm run tasks in one invocation exposes the Kotlin plugin's shared development/production output paths and Gradle's implicit-dependency validation. The integration runner executes build and run as separate invocations. This is documented in the sample, without changing the project's Kotlin or Gradle versions.

## Final installation

The final package is installed and enabled in the normal `18.0_f09bc98c` profile at `%LocalAppData%/Microsoft/VisualStudio/18.0_f09bc98c/Extensions/Vistudio/Vistudio Kotlin/0.1.0`. Deployment used the installed VSSDK's deployment-path and extension-manager tasks, followed by `devenv /UpdateConfiguration`. It did not change the setting for loading all user extensions as administrator. The separate T12 IDE session stayed open and untouched. `artifacts/editor-normal-install.log` confirms the extension-manager registration.

The normal profile was launched after installation. Its editor visibly shows both Kotlin icons, source/script code-editor tabs, colored neighboring/nested brace pairs, and inline real-LSP inspection text after the comment. No experimental root suffix is needed.

- Final VSIX SHA-256: `5B4F6F4627EDB419E466A886F8B2BA1CAAFB9100BD332E42F6AA2B67E239CA8E`.
- Installed and built `Vistudio.Vsix.dll` SHA-256: `AC2FC7722EC89F769304C040E4671E84D6CE21954630BDCD5DDFEA9F56E77D9C`.
- `artifacts/Vistudio.Kotlin.vsix` and `artifacts/Vistudio.Kotlin.Multiplatform.vsix` are identical final packages.

Image manifest placement and assembly resolution follow the [Visual Studio Image Service guidance](https://learn.microsoft.com/en-us/visualstudio/extensibility/image-service-and-catalog?view=visualstudio#faq). The root-element incompatibility was reproduced against the installed Image Service, then verified by host rendering after the fix.

## Scope of host acceptance

The checks above used the actual Visual Studio editor and Tools build command in a disposable Gradle/JVM project. Light/high-contrast themes, word-wrap/zoom combinations, tooltip interaction, and target-picker cancellation/folder-switch UI flows were not exhaustively exercised. Multiplatform task discovery, target builds/runs and intentional failures were validated by the real integration runner described above. Live semantics require a compatible configured server.

Native acceptance covers Windows x64 only. Apple targets require their supported macOS/Xcode host. Browser, Android/device flows and Native/JS/Wasm debugging are not claimed tested or implemented as IDE debuggers. Run tasks use Gradle; F5 remains the JVM adapter's responsibility.
