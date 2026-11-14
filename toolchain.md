# Toolchain

The user authorized implementation with the classic Visual Studio SDK. The VSIX host/build dependencies below implement that choice. Kotlin build dependencies belong only to the sample Gradle project.

| Program | How to install | Why here | Source |
| --- | --- | --- | --- |
| PowerShell | Windows / official distribution | Local document and link checks | https://github.com/PowerShell/PowerShell |
| Visual Studio 2026 and VSSDK workload | Visual Studio Installer | Host, VSIX packaging, full-framework MSBuild | https://visualstudio.microsoft.com/ |
| .NET Framework 4.7.2 targeting tools | Visual Studio Installer | Compile the extension and regression executable | https://learn.microsoft.com/dotnet/framework/ |
| JDK 11 | Microsoft OpenJDK distribution | Run the sample Gradle/Kotlin build | https://learn.microsoft.com/java/openjdk/ |
| Gradle 8.14.7 | Sample's checksum-pinned wrapper | Kotlin project build, compatible with the installed JDK 11 | https://github.com/gradle/gradle |
| Python 3.12 | Python distribution, optional | Standard-library-only LSP fixture, transparent acceptance recorder and navigation-log verification | https://www.python.org/ |
| Kotlin language server 263.7084.0 (expired 2026-11-13, kept only for the fail-fast check) | Official Windows x64 standalone archive; includes JetBrains Runtime 25 | Real LSP navigation acceptance testing; runs separately from the project JDK | https://github.com/Kotlin/kotlin-lsp/releases/tag/kotlin-lsp%2Fv263.7084.0 |
| Kotlin language server 271.2085.0 | Official Windows x64 standalone archive, user-installed (263.7084.0 has expired) | Kotlin LSP and the JVM debugger; F5 uses its launch protocol | https://github.com/Kotlin/kotlin-lsp |
| Eclipse JDT Language Server 1.61.0 | User-installed and configured by folder in Tools > Options (creator decision, not bundled); tested with the copy shipped in the VS Code Java extension | Completion, navigation, hover and diagnostics in `.java` files | https://github.com/eclipse-jdtls/eclipse.jdt.ls |
| Zig 0.16.0 | mise (user's machine); the extension finds it through Tools > Options > Zig or PATH, not bundled | `zig ast-check` diagnostics, `zig build`/`build-obj`/`run`, `zig env`; tests run against it | https://github.com/ziglang/zig |
| mise 2026.9.5 | User-installed (winget on this machine); the extension finds it through Tools > Options > Mise, PATH or the default install folders, not bundled | Tool install checks, update checks (`mise ls` / `mise outdated` JSON), `mise install` / `upgrade` / `upgrade --bump` / `trust`; live tests run against it | https://github.com/jdx/mise |
| ZLS (Zig Language Server) matching the Zig version; tested with 0.16.0 | User-installed and configured by path in Tools > Options > Zig (creator decision, not bundled); on this machine `mise install zls@0.16.0` (not activated globally) | Zig completion, signature help, navigation, references, symbols | https://github.com/zigtools/zls |
| Flutter SDK 3.50.0 (includes Dart SDK 3.14.0) | User-installed and configured by folder in Tools > Options > Dart or `vistudio.dart.flutterSdkPath` (not bundled); on this machine the official stable zip (SHA-256 checked) unpacked to `C:\Users\listepo\tools\flutter` (a local disk: git inside the SDK rejects the network share) | Dart analysis server (`dart language-server`), `dart run` / `test` / `analyze` / `pub`, `flutter run --machine` (hot reload), `flutter test`, the `dart` and `flutter debug_adapter` DAP servers; live tests run against it | https://docs.flutter.dev/install/archive |
| Conan 2 (tested with 2.33.0) | User-installed (on this machine in `C:\Users\Public\tools\conan`, on PATH), not bundled | C and C++ packages in Solution Explorer: `conan graph outdated --format=json` for newer versions, `conan install` after `conanfile.txt` edits; the C/C++ project template's build (`conan install` + CMake toolchain) | https://github.com/conan-io/conan |
| CMake (tested with 3.31.12) | User-installed (on this machine in `C:\Users\Public\tools\cmake`, on PATH), not bundled | Build commands of the C and C++ project templates (`cmake -S . -B build`, `cmake --build`, `ctest`) | https://github.com/Kitware/CMake |
| Rust toolchain: cargo, rustc (MSVC target) | User-installed (rustup), not bundled; not installed on this machine | Rust project builds (`cargo build/run/test`), Rust packages (`cargo add/remove/update`, `cargo update --dry-run --verbose` for newer versions; needs Cargo 1.78+ for the "available" notes) | https://github.com/rust-lang/cargo |
| rust-analyzer | User-installed (`rustup component add rust-analyzer` or release binary), found through Tools > Options > Rust, PATH or `%USERPROFILE%\.cargo\bin`; not bundled, not installed on this machine | Rust completion, navigation, hover, diagnostics, inlay hints and CodeLens data (document symbols, references) | https://github.com/rust-lang/rust-analyzer |
| wat_server (wasm-language-tools) | User-installed (release binary or `cargo install`), found through Tools > Options > WebAssembly, PATH or `%USERPROFILE%\.cargo\bin`; not bundled, not installed on this machine | WebAssembly text (`.wat`) completion, navigation, hover, diagnostics, inlay hints and CodeLens data | https://github.com/g-plane/wasm-language-tools |
| wkg (wasm-pkg-tools) | User-installed (`cargo install wkg` or release binary), not bundled; not installed on this machine | WebAssembly component packages: `wkg wit fetch` after WIT version edits | https://github.com/bytecodealliance/wasm-pkg-tools |
| wasmtime, wasm-tools | User-installed, not bundled; not installed on this machine | Run and build commands of the WebAssembly project template (`wasmtime run`, `wasm-tools parse`) | https://github.com/bytecodealliance/wasmtime, https://github.com/bytecodealliance/wasm-tools |
| Visual Studio workload "Desktop development with C++" | Visual Studio Installer (`--add Microsoft.VisualStudio.Workload.NativeDesktop --includeRecommended`, added on this machine for Flutter) | Flutter Windows desktop builds (CMake, MSVC) for Run, Debug and integration tests on the `windows` device; not needed by the VSIX itself | https://docs.flutter.dev/platform-integration/windows/setup |

## NuGet

| Package | Where | Source | Why here |
| --- | --- | --- | --- |
| Microsoft.VisualStudio.SDK 17.14.40265 | local | https://www.nuget.org/packages/Microsoft.VisualStudio.SDK/17.14.40265 | Classic VSSDK contracts |
| Microsoft.VisualStudio.LanguageServer.Client 17.14.60 | local | https://www.nuget.org/packages/Microsoft.VisualStudio.LanguageServer.Client/17.14.60 | Host-provided LSP client |
| Microsoft.VisualStudio.Workspace.VSIntegration 17.12.19 | local | https://www.nuget.org/packages/Microsoft.VisualStudio.Workspace.VSIntegration/17.12.19 | Active Open Folder workspace |
| Microsoft.VisualStudio.Debugger.DebugAdapterHost.Interfaces 16.6.40406.1 | local, compile reference only | https://www.nuget.org/packages/Microsoft.VisualStudio.Debugger.DebugAdapterHost.Interfaces/16.6.40406.1 | Connect Kotlin DAP to the native Visual Studio debugger |
| Microsoft.VisualStudio.Extensibility.Sdk 18.12.1603 | local, compile reference only | https://www.nuget.org/packages/Microsoft.VisualStudio.Extensibility.Sdk/18.12.1603 | In-process VisualStudio.Extensibility parts: CodeLens tagger and lens providers for `.kt`/`.java` |
| Microsoft.VisualStudio.Extensibility.Build 18.12.1606 | local | https://www.nuget.org/packages/Microsoft.VisualStudio.Extensibility.Build/18.12.1606 | Generates `.vsextension/extension.json` for the CodeLens parts |
| Microsoft.VSSDK.BuildTools 18.12.1606 | local | https://www.nuget.org/packages/Microsoft.VSSDK.BuildTools/18.12.1606 | VSCT compilation, pkgdef generation, VSIX packaging (18.12 is required by Extensibility.Build 18.12) |
| Newtonsoft.Json 13.0.3 | local tests; compile-only in Core; SDK transitive dependency in VSIX | https://www.nuget.org/packages/Newtonsoft.Json/13.0.3 | Normalize LSP initialization and DAP frames using the SDK's existing serializer version; read workspace `.vs\settings.json` (Core references it without runtime assets, Visual Studio supplies the assembly) |

SDK runtime assets are excluded from the VSIX; the IDE supplies them. Only Vistudio's own assemblies are shipped. The SDK's transitive package versions are resolved by NuGet. The 18.12 SDK/build meta-packages were evaluated but could not restore due to unpublished transitive dependencies, so the implementation uses the available 17.14 contracts with the VS 2026 host.

## Gradle sample

The completion UI uses the SDK's existing editor/IntelliSense and EnvDTE contracts and the .NET Framework `PresentationCore` reference. Method/extension suggestions use the existing Kotlin LSP connection with in-memory lexical source snapshots. It adds no NuGet package, JVM compiler dependency, or bundled completion server.

The enclosing-owner fallback uses the same server's standard `documentSymbol`, `rename` and `signatureHelp` requests. Semantic rename results change only in-memory analysis strings; no workspace edit is applied and no evaluator is invoked. The optional integration harness uses the existing C# toolchain and Python standard library.

| Package | Where | Source | Why here |
| --- | --- | --- | --- |
| org.jetbrains.kotlin.jvm 2.4.21 | local sample | https://kotlinlang.org/ | Compile the sample Kotlin/JVM application |
| org.jetbrains.kotlin.multiplatform 2.4.21 | local Multiplatform sample | https://kotlinlang.org/ | Compile shared Kotlin for JVM, Windows Native, JS and Wasm using the existing Kotlin version |
| org.jetbrains.kotlin.plugin.compose 2.4.21 | local Compose sample | https://github.com/JetBrains/kotlin | Compose compiler plugin matching the sample's Kotlin version |
| org.jetbrains.compose 1.12.2 | local Compose sample | https://github.com/JetBrains/compose-multiplatform | Compose Multiplatform Gradle plugin; supplies `compose.desktop.currentOs` |
| org.jetbrains.compose.runtime:runtime, foundation:foundation, ui:ui, ui:ui-tooling-preview 1.12.2 | local Compose sample | https://github.com/JetBrains/compose-multiplatform | UI and the `@Preview` annotation rendered by the preview window |
| org.jetbrains.kotlin:kotlin-test 2.4.21 (`kotlin("test")`) | projects generated by the New Project templates | https://github.com/JetBrains/kotlin/tree/master/libraries/kotlin.test | The templates' unit tests; Gradle selects the JUnit 5/JUnit 4/JS/Native variant per target |

The New Project templates (`Vistudio.Core/Templates`) generate Gradle projects with exactly the versions above (Gradle 8.14.7 wrapper copied from `samples/kotlin-jvm`, Kotlin 2.4.21, Compose Multiplatform 1.12.2); they add no extension dependency. The `.vstemplate` zips are built by MSBuild's `ZipDirectory` task, and the template engine's DTE `Project` contract comes from the EnvDTE reference already in the SDK package.

The Compose preview renderer (`Build/VistudioPreviewRenderer.java`) is compiled by the user's Gradle build with the JDK it already uses and calls Compose reflectively; the extension adds no Compose, Skia or JVM dependency.

The Multiplatform plugin provisions Kotlin/Native/LLVM and Node 24.16.0 with Yarn 1.22.22 for its target tasks. Its generated npm dependencies are recorded in the sample's `kotlin-js-store/yarn.lock`. Runtime assertions use Kotlin's standard library, without adding a test framework. File icons use embedded WPF vector resources and the VSSDK image service; brace classifiers and diagnostic adornments use existing SDK contracts, without new NuGet dependencies.

Zig support (T44-T48) adds no NuGet package: the grammar is a TextMate JSON file in the existing grammar folder, ZLS goes through the LSP client and CodeLens parts already listed, and zig.exe/ZLS are the user's installs. The sample `samples/zig-hello` uses only the Zig standard library.

Dart and Flutter support (T52-T57) adds no NuGet package or program to the VSIX: the grammar is a TextMate JSON file written for this project, the analysis server and the debug adapters are the SDK's own (`dart language-server`, `dart`/`flutter debug_adapter`), reached through the LSP client, the Debug Adapter Host and the CodeLens parts already listed. The samples resolve their pub dependencies from pub.dev with `pub get`: `samples/dart-console` uses `test` (^1.25.0, dev); `samples/flutter-app` (generated by `flutter create`, Flutter 3.50.0) uses `material_ui` and `cupertino_icons`, plus `flutter_test`, `integration_test` (both from the Flutter SDK) and `flutter_lints` for development. Their exact versions are pinned in each sample's `pubspec.lock`.

The language project types and Packages nodes (T58-T60) add no NuGet package or bundled program: each package manager is the user's install, driven through its own commands and machine-readable output (`dart pub outdated --json`, `conan graph outdated --format=json`, Cargo's `update --dry-run --verbose` notes, `git ls-remote --tags` for Zig dependencies hosted on GitHub/Codeberg/GitLab, a Gradle init script `Build/OutdatedDependencies.gradle` that resolves `latest.release` with Gradle's own dependency resolution). Manifests are read by small readers for exactly the subsets needed (pubspec YAML, Cargo/wkg TOML, `build.zig.zon`, Gradle Kotlin/Groovy DSL and `libs.versions.toml`), so no YAML or TOML library is added. File and project icons are new vector XAML resources in `Icons/Languages.imagemanifest`.

The runtime ETW library (`src/Vistudio.Etw`, T43) uses only Windows advapi32 and the .NET Framework `EventSource`; its real-session check uses the Windows `logman` and `tracerpt` tools. It adds no program or package.

No third-party test framework or bundled language server is used. The SDK already supplies Newtonsoft.Json; the regression project references that same version to exercise the connection adapter. A user's separately installed LSP server may have a different JDK requirement.

Project model import uses the Gradle distribution's bundled Groovy XML support and public dependency-resolution APIs, plus .NET Framework XML APIs in Core. The multi-module import fixture reuses the existing Kotlin plugin and wrapper; its deliberately lower stdlib request is forced to the already-used 2.4.21 to verify selected-versus-requested versions. It adds no extension dependency or external test framework.
