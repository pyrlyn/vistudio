# Bazel, build caches and Dart/Flutter icons: validation (T67-T70)

What was run and what was not. Machine: Windows 11 on ARM, Visual Studio 18.12 build tools (MSBuild). Not installed: Bazel, bazelisk, buildifier, starpls, bazel-lsp, sccache, cargo, zig.

## Checks that ran

| Command | Result |
| --- | --- |
| `Vistudio.Tests.exe --bazel` | PASS, 120 assertions. Starlark lexer (raw, triple-quoted and unterminated strings, comments, line breaks inside brackets) and top-level calls, defs, globals, loads; labels (repositories, `//pkg` shorthand, local, invalid forms); a sample Bzlmod workspace on disk: root, packages, targets and genrule outputs, repositories with `repo_name` and `use_repo`. Checks: private load symbols, non-.bzl loads, missing files, duplicate targets, missing packages, unknown repositories, unknown targets as suggestions, `..` in labels, folders in `srcs`, `//visibility` values, `def` in BUILD, Python statements, mismatched brackets, unterminated strings, glob patterns left alone, subpackage boundaries, `load()` in MODULE.bazel, toolchain patterns. Completion: files, folders and local targets in `srcs`, subfolders, `glob()`, `//` packages and folders (no `bazel-*`), package targets, subpackages, `:targets`, `@repositories`, `.bzl` labels and public symbols in `load()`, visibility, no completion in `name`, attributes minus used ones, rules per file kind, MODULE.bazel functions, nothing after a dot or a number, names left to a running server. buildifier JSON and syntax-error parsing, its command line, starpls' `server` subcommand. Build caches for Bazel (position after the command, `.bazelrc` and explicit flags win, `clean` untouched, cache off), Gradle (`--build-cache`, `gradle.properties`, `--no-build-cache`, remote init script), Cargo (sccache found / missing / existing `RUSTC_WRAPPER` / `clean`), CMake (configure only), Zig/Dart/Flutter/wasm-tools untouched, Kotlin `GradleCommand`. Bazel build system: detection from `MODULE.bazel`, `bazel` → `bazelisk` fallback with the disk cache, cache off, switching Native ⇄ Bazel keeps edited commands, invalid values rejected. Icons: Flutter vs Dart project icon, Bazel file names, image manifest and `BazelFiles.pkgdef`. |
| `Vistudio.Tests.exe --packages none` | PASS, 500 assertions (unchanged count; language project files now also carry `BuildSystem`, `BuildCache`, `RemoteCache`). |
| `Vistudio.Tests.exe` (default run) | PASS, 1315 assertions (1195 before + the 120 Bazel ones). |
| `tests/verify-vsix.ps1` (Debug VSIX) | PASS. It now also requires `BazelFiles.pkgdef` in the VSIX manifest (it was missing at first, so the Bazel registrations would not have been installed), the Starlark grammar's file types and the `.bzl` editor association; image 10 (Flutter) is listed as used from code only. |
| `tests/verify-icon-rendering.ps1` | PASS: every vector icon, including `FlutterProject` and `BazelFile`, renders non-blank at 100 % and 200 %. The three new drawings were also looked at as 128 px renders. |

## Not verified

- Nothing ran against real Bazel, bazelisk, buildifier, starpls, bazel-lsp or sccache: the buildifier JSON format and the starpls `server` subcommand follow their documentation, not a run here.
- Nothing was checked in Visual Studio: the `starlark` content type for extensionless `BUILD`/`WORKSPACE` (MEF `FileName` mapping), TextMate coloring of extensionless files, the completion list and its commit behavior (typed characters never commit), squiggles, Error List entries and navigation, the Bazel options page, the Flutter and Bazel icons in Solution Explorer, and the new Properties window rows.
- The Gradle remote cache init script (`gradle.beforeSettings`, `HttpBuildCache`) was not run by Gradle.
- sccache with CMake only takes effect with Ninja or Makefile generators; the C/C++ template's default (Visual Studio generator) ignores compiler launchers.
- Unsaved targets in the open BUILD file appear in `:target` completion only after a save (completion reads BUILD files from disk); targets created inside macros are unknown, so missing ones are suggestions, not warnings.
- The Flutter project icon follows `pubspec.yaml` when the project loads; a later switch to Flutter shows after the project reloads.
