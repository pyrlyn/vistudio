<h1><picture><source media="(prefers-color-scheme: dark)" srcset="brand/logo-dark.svg"><img src="brand/logo.svg" width="32" height="32" alt="" align="absmiddle"></picture> Vistudio: Kotlin for classic Visual Studio</h1>

An installable C# VSIX for **Visual Studio 2026 18.12+ (x64 or ARM64)**, using the classic Visual Studio SDK. It provides Kotlin file icons and editor associations, syntax and brace coloring, LSP navigation, Gradle build/run/test workflows for Multiplatform targets, editor diagnostics, and Kotlin/JVM debugging, plus Zig and Dart/Flutter support.

## Install and use

1. Build with `./build.ps1` from PowerShell, or use the generated `artifacts/Vistudio.Kotlin.vsix`.
2. Close Visual Studio and open the VSIX installer. Restart Visual Studio after installation.
3. Use **File > Open > Folder** to open the directory containing your project's `gradlew.bat`.
4. Click a `.kt` or `.kts` file in Solution Explorer to preview it, or double-click to keep it open. Both extensions are registered with Visual Studio's code editor and Kotlin content type. Syntax and brace coloring work without a language server. This VSIX registers Visual Studio associations, not Windows Explorer default applications. Java `.properties` files (`gradle.properties`, `local.properties`, `gradle-wrapper.properties`, resource bundles) also open in the code editor with their own icon and a Vistudio TextMate grammar, since Visual Studio ships none: `#`/`!` comments, keys, `=`/`:` separators, `\uXXXX` and backslash escapes, line continuations, `true`/`false` and numeric values, and `{0}`/`${name}` placeholders.
5. Use **Tools > Kotlin > Build with Gradle**, **Clean with Gradle**, **Test with Gradle**, or **Cancel Gradle**. Output and exit status appear in the **Kotlin** Output pane.
6. After compilation, errors and warnings appear in **View > Error List**. Double-click a located diagnostic to open its file at the reported line and column. Use the **Entire Solution** scope and **Build + IntelliSense** source filter if rows are hidden.

If an experimental profile has not initialized its package registry yet, close that profile and run `devenv /RootSuffix VistudioTest /UpdateConfiguration` before reopening it. The test installation procedure is in [validation.md](docs/validation.md).

This development package retains version 0.1.0. To replace an earlier 0.1.0 installation, uninstall Vistudio Kotlin from that profile first, then install the newly built VSIX.

## Build the extension

Requires Windows, Visual Studio with **Visual Studio extension development**, .NET Framework 4.7.2 targeting tools, and NuGet access. Open [Vistudio.sln](Vistudio.sln), or run:

```powershell
./build.ps1
```

The script builds the VSIX, runs the dependency-free C# regression executable, and copies the package to `artifacts/Vistudio.Kotlin.vsix`. Use Visual Studio's full-framework MSBuild for VSIX packaging. MSBuild builds the C# extension only; it is never called for Kotlin builds.

The implementation references the published SDK 17.14 contracts because the available 18.12 NuGet meta-package currently refers to unpublished dependencies. The installation target is the tested VS 2026 host. VS 2022 compatibility is not declared.

## Current scope

Implemented: `.kt`/`.kts` TextMate grammar (nested comments, strings/interpolation, declarations, keywords, numbers), shared MEF LSP client, settings/restart, asynchronous Gradle Build/Clean/Test/Cancel, output, validated task arguments, owned Windows process jobs, and a [real Kotlin/JVM sample](samples/kotlin-jvm/README.md).

Gradle commands use the project's wrapper with `--console=plain --no-daemon`. Two kinds of run use a Gradle daemon instead, each one of its own that Vistudio ends: the build and launch of a Kotlin/JVM debug session (see [Kotlin/JVM debugging](docs/user-guide.md#kotlinjvm-debugging)) and hot reload compiles. CLI options are deliberately not accepted in task fields. The wrapper's exit code determines success. Logs use UTF-8 decoding; configure the selected JDK/Gradle output accordingly for non-ASCII text. Cancellation terminates the owned invocation rather than all Java processes.

Not yet implemented: Native/JS/Wasm debug adapters, Android device management, and automatic server installation. LSP diagnostics depend on the configured server and are handled by Visual Studio.

## Documentation

- [User guide](docs/user-guide.md): language features, debugging, previews, Java, Zig, Dart/Flutter, Bazel and mise.
- [Feature matrix](docs/feature-matrix.md)
- [Architecture](docs/architecture.md)
- Validation notes for individual features live under [docs/](docs/).

## Design and validation

- [Architecture, SDK extension points, and acceptance criteria](docs/architecture.md)
- [Illustrative C# integration fragments](docs/csharp-fragments.md)
- [Validation and test installation](docs/validation.md)

The architecture documents describe the larger intended feature set. This README describes the implementation shipped in version 0.1.0.
