# Kotlin project metadata

`vistudio.kotlinproj` stores project settings and imported Gradle metadata. Visual Studio loads it as a Solution Explorer project (see [Solution Explorer project](#solution-explorer-project)); settings are edited in the Properties window and actions are project context-menu commands. Everything uses the existing classic Visual Studio SDK package and VSCT commands; no additional dependencies are required.

The file uses the familiar .NET project XML layout (`Project`, `PropertyGroup`, `ItemGroup`). It is an **IDE metadata format**, not a C# project or an MSBuild build definition: MSBuild never evaluates it, and Kotlin compilation remains exclusively on Gradle/JVM.

## Usage

1. Open the Kotlin project with **File > Open > Folder**. The Gradle root must contain `gradlew.bat`; the existing global Gradle root setting can point to a subdirectory.
2. Run **Tools > Kotlin > Create or Open .kotlinproj**. It creates `vistudio.kotlinproj` when the folder has none (never replacing an existing file), opens it as a Solution Explorer project (closing the folder view) and imports the Gradle model if it was never imported. With a Kotlin project already loaded it only reveals Solution Explorer. Existing metadata can also be opened with **File > Open > Project/Solution**. **Tools > Kotlin > Import Gradle project** still works for an open folder.
3. Select the project and press **F4**. **Gradle JDK** offers detected installations in its drop-down and accepts any typed JDK home; empty uses global settings. Its version and vendor are read from the JDK's `release` file; both `bin/java.exe` and `bin/javac.exe` are required, and saving also refreshes the Gradle Wrapper/JDK tool snapshot. The other editable values are Gradle root, Build/Test/Run tasks, Import configurations, Startup target (Kotlin Multiplatform) and the Debug group (main class, source file, arguments, working directory). Every change is validated by `KotlinProjectFile.Set`/`SetJdk` against the file as it is on disk and saved atomically; the Properties window rejects invalid input with the reason and leaves the file unchanged. Read-only values: project file, name, GUID, JDK version, and the last import's time, Gradle version and JVM.
4. Kotlin Build/Clean/Test/Run, task discovery and F5 read the project settings on their next invocation. Debug launch inherits the project JDK unless its launch environment already supplies `JAVA_HOME`. Running processes are not restarted on save.
5. Right-click the project: **Import Gradle Model** resolves dependencies and updates the generated inventory; **Cancel Gradle** stops the owned process; **Run Tests with Gradle**, **Run with Gradle** and **Gradle Tasks...** run on the selected project; **Edit Project File** opens the XML (saving it refreshes the tree). These commands appear only for Kotlin projects and share one Gradle gate with solution builds and the Tools menu commands. Modified documents are saved through the IDE first.
6. Import again after changing the JDK, build scripts, settings, version catalogs, lockfiles or dependency rules. Import does not run continuously or silently on every editor change.

An empty `JdkHome` uses global Kotlin Gradle settings, then the IDE's inherited `JAVA_HOME`. This selection controls the JVM that runs Gradle; Gradle toolchains, `org.gradle.java.home` and daemon JVM criteria may independently control compiler/daemon selection. It does not install a JDK, change machine environment variables, alter language-server runtime settings, or rewrite Gradle toolchain declarations.

## Version 1 document

The exact name `vistudio.kotlinproj` avoids choosing arbitrarily between files. Paths resolve relative to its directory; absolute paths are accepted. The JDK picker saves an absolute machine-local path. Leave it empty for a portable shared project, or edit a relative JDK path manually.

```xml
<?xml version="1.0" encoding="utf-8"?>
<Project VistudioSchemaVersion="1">
  <PropertyGroup Label="Vistudio">
    <ProjectName>demo</ProjectName>
    <ProjectGuid>{d5c643e6-8ef2-4c61-ad01-bd219bfa1a68}</ProjectGuid>
    <GradleRoot>.</GradleRoot>
    <BuildTasks>build</BuildTasks>
    <TestTasks>test</TestTasks>
    <RunTasks>run</RunTasks>
    <JdkHome />
    <JdkVersion />
  </PropertyGroup>
  <ItemGroup Label="Modules">
    <Module Include=":"><Path>.</Path></Module>
  </ItemGroup>
  <ItemGroup Label="Tools" />
  <ItemGroup Label="Libraries" />
  <ItemGroup Label="Frameworks" />
  <ItemGroup Label="DetectedTools">
    <Tool Include="Gradle Wrapper">
      <Version>8.14.7</Version>
      <Origin>Wrapper declaration</Origin>
      <Path>gradle/wrapper/gradle-wrapper.properties</Path>
      <DistributionUrl>https://services.gradle.org/distributions/gradle-8.14.7-bin.zip</DistributionUrl>
    </Tool>
  </ItemGroup>
</Project>
```

The version above illustrates the existing sample, not a new requirement or a wrapper upgrade. Generated snapshots record the absolute source path. `DetectedTools` and `GradleImport` are generator-owned; manual groups and unknown elements survive updates. Whitespace formatting may change on save. The loader rejects unsupported schema versions, DTDs, duplicate settings and conditional Vistudio property groups. There is no MSBuild property interpolation, condition evaluation or imports. Atomic replacement and an external-edit check protect normal edit/save cycles; a detected conflict requires Refresh.

| Item | Metadata | Current behavior |
| --- | --- | --- |
| `Module Include=":app"` | `Path`, `Name`, `BuildFile`, `Group`, optional project `Version`, `Origin` | Imports all projects in the root Gradle build |
| `Tool Include="name"` | `Version`, `Path`, `Origin`, optional `Vendor` / `DistributionUrl` | Wrapper declaration and selected JDK are detected; custom tools can be recorded manually |
| `Configuration Include="runtimeClasspath"` | `Module`, `Status`, `ComponentCount` or `Reason` | Distinguishes resolved classpaths from explicitly skipped tooling configurations |
| `Library Include="group:artifact"` | selected `Version`, `Requested`, `SelectionReason`, `Relationship`, `Kind`, `Configuration`, `Module`, `Origin` | Imports direct/transitive components through Gradle resolution; project references and local files have separate kinds |
| `Framework Include="name"` | `Version`, `Module`, `Configuration`, `Evidence`, `Kind`, `Origin` | Recognizes exact resolved component markers and applied plugins; never substitutes a requested version |
| `Target Include="jvm"` | `Module`, `Platform` (JVM, Android, JS, Wasm, Native), `NativeTarget`, `WasmTarget`, `Environments`, `Compilations`, `SourceSets`, `MainSourceSets`, `Configurations`, `BuildTasks`, `TestTasks`, `RunTasks` | Kotlin Multiplatform targets (generated; see [Kotlin targets](#kotlin-targets)) |
| `KotlinSourceSet Include="webMain"` | `Module`, `DependsOn`, `Targets` | The Kotlin source-set hierarchy and the targets that compile each set (generated) |

For example, a manually recorded library can use `<Library Include="org.example:example"><Version>1.0</Version><Configuration>runtimeClasspath</Configuration><Module>:app</Module><Origin>Declared</Origin></Library>`. Mark origins honestly; a declared version is not evidence of a resolved artifact. Gradle build scripts and lockfiles remain the source of truth. The document does not duplicate the source contents, full Gradle model, LSP settings or `.vs/launch.vs.json`; those continue to belong to their existing owners.

## Implementation boundaries

- `Core/Projects/KotlinProjectFile`: generation, preserving XML load/save, tool snapshots, project-to-build settings mapping.
- `Core/Projects/JdkInstallation`: metadata-only validation/discovery in `JAVA_HOME`, the selected location, common Program Files vendor folders and the user's `.jdks`. This is a bounded inventory, not a scan of every installed program or every drive.
- `KotlinSettings.LoadForFolder`: global defaults plus per-project overrides from `KotlinProjectFile.Find` (the default `vistudio.kotlinproj`, otherwise the only `*.kotlinproj`; several custom-named files are ambiguous). `LoadForProjectFile` applies one explicit file, as used by solution builds. Invalid metadata blocks the operation with an error instead of silently selecting another JDK.
- `Core/Projects/KotlinProjectTree`: pure, lazily expanded tree model for Solution Explorer (see below).
- `Project/KotlinProjectFactory`, `KotlinProjectNode`, `KotlinProjectConfig`: the VSSDK project system that loads a `.kotlinproj` into a solution.
- `Project/KotlinProjectProperties`: Properties window browse object of the project node (editable settings, JDK drop-down, read-only import details). Other nodes use the read-only `KotlinNodeProperties`.
- `KotlinPackage`: menu registration and Gradle execution. `KotlinDebugCoordinator`: project-aware fallback launch environment. LSP and DAP keep their existing responsibilities.

`Build/ImportModel.gradle` exports a versioned XML envelope through a temporary file. `GradleProjectImporter` runs it with the project's wrapper and JDK; `GradleProjectModel` validates the result and classifies known framework markers. Exit 0 and a complete valid envelope are required before `KotlinProjectFile.ApplyImport` replaces the generated groups.

## Solution Explorer project

**File > Open > Project/Solution** accepts `*.kotlinproj`, and a `.sln` can reference one with project type `{E7447D71-1121-42DC-8A4D-D3D78B3CC633}`. The project is a hand-written VSSDK hierarchy (`IVsUIHierarchy`, `IVsProject`, `IVsPersistHierarchyItem`, configuration provider and buildable configurations); it adds no package dependencies, and the file is never evaluated by MSBuild. Only existing metadata can be opened; create it from an open folder with **Tools > Kotlin > Create or Open .kotlinproj**.

Tree, built by `KotlinProjectTree` from the GradleImport snapshot:

- one node per Gradle module (the aggregate root of a multi-module build is omitted when it has neither sources nor resolved configurations);
- a Kotlin Multiplatform module shows its shared source sets (`commonMain`, `commonTest`, intermediate sets such as `webMain` or `nativeMain`, with `Depends on` and `Targets` in Properties), then `Targets` with one node per target (`jvm (JVM)`, `js (JS)`, `wasmJs (Wasm)`, `mingwX64 (Native)`, ordered JVM, Android, JS, Wasm, Native) holding the target's own source sets (`jvmMain`, `jvmTest`) and `Dependencies` with its compilation configurations; the module's `Dependencies` keep the remaining configurations. Target nodes have platform icons and Build/Run/Test context commands (see [Kotlin targets](#kotlin-targets)). JVM-only modules keep the layout below;
- under a module, its source sets (`main`, `test`, then others) with existing source/resource roots shown relative to the module, and their on-disk folders and files (dot-prefixed entries hidden unless Tools > Options > Kotlin > Solution Explorer > Show hidden files is on, at most 5000 entries per folder);
- `Dependencies` with each resolved configuration and its libraries: selected version, `(transitive)` marker, project references, unversioned file dependencies; Properties shows coordinates, requested selector, selection reason and origin;
- `Frameworks` under a module: recognized framework components and applied plugins with the evidenced version (one node per framework/version, with all configurations in Properties; missing plugin versions say `version unavailable`);
- `Toolchain`: the Gradle Wrapper declaration and selected Gradle JDK snapshot, plus the Gradle version and JVM that ran the last import;
- `Declared Metadata`: manually maintained `Tool`/`Library`/`Framework` items outside the generated groups, with their origin;
- `Gradle Scripts`: settings, per-module build files, `gradle.properties`, version catalog and wrapper properties.

Import exports `SourceRoot` items (Kotlin source sets first, then Java source sets; `Sources` or `Resources`; absolute path; `Exists`). Snapshots without source roots, or unimported projects, fall back to the `src/<set>/{kotlin,java,resources}` convention and say so in Properties; unimported projects show an import hint instead of dependencies. The tree is read-only: add, rename and delete files on disk or in the editor. File-system watchers on the project and Gradle roots (ignoring `build`, `.gradle`, `.kotlin`, `.idea`, `.vs`, `out`, `.git`) and on the `.kotlinproj` itself reconcile the tree in place, keeping item ids of unchanged nodes.

Documents open through the project (not Miscellaneous Files) and save through their own document data. Solution configurations `Debug|Any CPU` and `Release|Any CPU` both run the project's `BuildTasks`; Rebuild runs `clean` plus build tasks in one Gradle invocation and Clean runs `clean`. Solution builds share the Kotlin commands' single Gradle gate, use the project's own root/JDK settings, mirror Gradle output into the Build pane and publish compiler diagnostics to the Error List. While no folder workspace is open, the first loaded `.kotlinproj` directory is the IDE root for the language server and the Kotlin Tools menu commands.

The IDE's fast up-to-date check (`GradleInputs`) reports the project up to date only after a successful Build/Rebuild in the current session when the same input files still exist and none was written after that build started. Inputs are all files under the Gradle root except `build`, `out`, `.gradle`, `.kotlin`, `.idea`, `.vs`, `.git` folders and IDE files (`.kotlinproj`, `.sln`, `.slnx`, `.suo`, `.user`, `.tmp`). Up-to-date projects are skipped by Build ("1 up-to-date"); Gradle still decides what to recompile when it runs. The check is synchronous: Visual Studio reads its HRESULT.

### F5 and Ctrl+F5

The project's configurations are debuggable. F5 starts the existing Kotlin/JVM debug launch (Kotlin LSP build-tool launch resolution, DAP debug engine); Ctrl+F5 starts the same launch with `noDebug`. The entry point comes from optional properties in the Vistudio property group:

| Property | Meaning |
| --- | --- |
| `DebugMainClass` | JVM entry class, for example `sample.MainKt` |
| `DebugSourceFile` | Kotlin file with that `main`, relative to the `.kotlinproj` |
| `DebugArguments` | Program arguments separated by whitespace; `"…"` groups, `\"` is a literal quote |
| `DebugWorkingDirectory` | Working directory relative to the `.kotlinproj`; default is the Gradle root |

In a project with Kotlin targets, the launch follows `StartupTarget` (see [Kotlin targets](#kotlin-targets)): a non-JVM startup target, or the only target of a project without JVM targets, is not debugged: F5 and Ctrl+F5 run its Gradle run task through the Kotlin Gradle gate with output in the Kotlin pane, and F5 also opens Vistudio Diagnostics on the program that task starts (Node.js for JS and wasmJs, the executable for Native; Gradle's own processes are not counted). A target without a run task gets a message that debugging is supported for JVM targets only. JVM targets use the JVM launch below; entry points are then searched only in the source sets of the JVM target's main compilation (for example `commonMain` and `jvmMain`), never in `jsMain` or `mingwX64Main`.

When `DebugSourceFile` is empty, the single top-level `fun main` (optionally `public`, `internal` or `suspend`) in production source roots (`main` and `*Main` source sets) is used; the class is the package plus the file facade name (`Main.kt` → `MainKt`, or `@file:JvmName`). With `DebugMainClass` alone, the matching candidate is selected. No candidate or several candidates stop the launch with a message naming the properties to set. If the language server is not running yet, the entry source is opened so Visual Studio loads the Kotlin client. When the project is up to date (normally right after the solution build manager's build-before-run), the launcher does not run Gradle again; otherwise it builds once with the Kotlin build gate first. Debugging requires the Kotlin language server from Tools > Options > Kotlin, as in Open Folder.

Not provided: editing the project from Solution Explorer, DTE `Project` automation beyond identity (the root's `ExtObject` exposes name, file and kind for the template engine; items, properties, code model and configurations are not automated), parallel solution builds of several Kotlin projects (the shared gate builds one Gradle invocation at a time; a second project reports the busy gate), and debugging a Kotlin project other than the first one loaded in the solution (the IDE root and language server follow the first project). JVM arguments and environment variables are not yet project properties.

## New Project templates

**File > New > Project** lists five templates under the language **Kotlin** (platforms Windows, Linux, macOS):

| Template | Gradle project | Build / Test / Run tasks |
| --- | --- | --- |
| Kotlin Console App (JVM) | `kotlin("jvm")` + `application`, `src/main/kotlin/<package>/Main.kt`, kotlin.test on JUnit 5 | `build` / `test` / `run` |
| Kotlin Class Library (JVM) | `kotlin("jvm")` + `java-library`, `Library.kt`, kotlin.test | `build` / `test` / `build` (nothing to run) |
| Kotlin and Java App (JVM) | Kotlin `main` calling a Java class in `src/main/java`, kotlin.test | `build` / `test` / `run` |
| Kotlin Multiplatform App | `kotlin("multiplatform")` with the targets chosen in a dialog (jvm, js and wasmJs on Node.js, mingwX64), `expect fun platformName()` with one `actual` per target, kotlin.test in `commonTest` | `build` / `allTests` / run task of the first chosen target |
| Compose Multiplatform Desktop App | Compose Multiplatform `jvm("desktop")` with an `App` composable and a `@Preview`, kotlin.test in `commonTest` | `build` / `allTests` / `run` |

Every project gets the checksum-pinned Gradle 8.14.7 wrapper of the samples, Kotlin 2.4.21 (Compose 1.12.2), JVM bytecode 11 for the JVM templates, `.gitignore`, `gradle.properties` and `vistudio.kotlinproj` with the tasks above (and the global Gradle JDK, if one is set). The package name is the project name in lowercase ASCII letters and digits (`My App` → `myapp`); the Gradle root project name drops characters Gradle rejects. Existing files are never overwritten: a location that already contains any generated file is rejected.

How it works: each `.vstemplate` (`src/Vistudio.Vsix/Templates/Projects`, zipped into the VSIX `ProjectTemplates` folder at build time) has a single project file, a `VistudioTemplate` stub that names the template kind. The template engine passes it to `KotlinProjectFactory.CreateProject` with `CPF_CLONEFILE`; the factory asks for Multiplatform targets, generates the project with `KotlinProjectTemplate` (Core, no VS dependencies) into the chosen location and opens the real `vistudio.kotlinproj`. The package then imports the Gradle model as soon as the project has loaded, exactly as **Create or Open .kotlinproj** does. No `IWizard` assembly or extra package is involved. Opening or adding any other `.kotlinproj` with `CPF_CLONEFILE` is rejected.

The first build of a Multiplatform project downloads Node.js (js, wasmJs) and the Kotlin/Native toolchain (mingwX64); Gradle needs network access to Maven Central for the first resolution of each template's dependencies. Run a Multiplatform target's run task in its own Gradle invocation: Kotlin 2.4.21 reports an undeclared task dependency when `build` and `jsNodeDevelopmentRun` are requested together.

## Kotlin targets

For every module that applies the Kotlin Multiplatform plugin, import records each non-metadata target as a generated `Target` item and the source-set hierarchy as `KotlinSourceSet` items. Task paths are qualified with the module path and recorded only when the task exists in that project:

| Platform | Build tasks | Test task | Run task |
| --- | --- | --- | --- |
| JVM | main `compileAllTaskName` (`:jvmMainClasses`) | `:jvmTest` | `:jvmRun` |
| JS, Wasm | `:jsMainClasses`, `:wasmJsMainClasses` | `:jsTest`, `:wasmJsTest` | the first of `<target>NodeDevelopmentRun`, `BrowserDevelopmentRun`, `D8DevelopmentRun` |
| Native | `:mingwX64MainKlibrary` plus link tasks of debug executables | `:mingwX64Test` | run task of the first debug executable (`:runDebugExecutableMingwX64`) |

The GradleImport property group adds `ProjectKind` (JVM, Android, JS, Wasm, Native, Multiplatform when several platforms are present, or Unknown; the Kotlin/JVM and Android plugins count as JVM and Android) and `Targets` (target ids). A target id is the module path plus the target name: `:jvm` in the root module, `:app:jvm` in `:app`. Single-platform Kotlin/JVM modules have no `Target` items and keep their existing tree.

```xml
<Target Include="wasmJs">
  <Module>:</Module>
  <Platform>Wasm</Platform>
  <WasmTarget>JS</WasmTarget>
  <Environments>Node.js</Environments>
  <Compilations>main test</Compilations>
  <SourceSets>wasmJsMain wasmJsTest</SourceSets>
  <MainSourceSets>commonMain wasmJsMain webMain</MainSourceSets>
  <Configurations>wasmJsCompileClasspath wasmJsRuntimeClasspath wasmJsTestCompileClasspath wasmJsTestRuntimeClasspath</Configurations>
  <BuildTasks>:wasmJsMainClasses</BuildTasks>
  <TestTasks>:wasmJsTest</TestTasks>
  <RunTasks>:wasmJsNodeDevelopmentRun</RunTasks>
  <Origin>Kotlin Multiplatform target</Origin>
</Target>
```

`StartupTarget` (editable, Properties window drop-down under **Targets**) selects a target by id; a unique plain name such as `js` is stored as its id, and unknown names are rejected with the valid ids. When set:

- **Run with Gradle** (and Tools > Kotlin > Run with Gradle) runs the startup target's run task instead of `RunTasks`;
- F5 debugs JVM targets only (see [F5 and Ctrl+F5](#f5-and-ctrlf5)); for other targets F5 runs the target's run task with Vistudio Diagnostics, Ctrl+F5 runs it without;
- a startup target that no longer exists after a reimport stops F5 with its name; Run, import and solution builds ignore it.

Right-clicking a target node offers **Build &lt;target&gt;**, **Run &lt;target&gt;** and **Test &lt;target&gt;** with the task paths in the caption; commands without a task are hidden. They run through the same Gradle gate as the other Kotlin commands (Error List diagnostics, cancel with Cancel Gradle). Solution Build still runs the project's `BuildTasks` (`build` builds all targets).

## Resolved Gradle import

The importer uses Gradle's public [ResolutionResult](https://docs.gradle.org/current/javadoc/org/gradle/api/artifacts/result/ResolutionResult.html), including selected component identifiers, requested selectors and selection reasons. This is graph resolution, not a parse of build-script text or a claim that all artifact binaries have been downloaded. Versions selected by conflict resolution, constraints, BOMs, dynamic selectors or substitutions are read from the resulting graph. A project substitution is recorded as a project reference without a fabricated Maven version.

Default scope includes Java source-set compile/runtime classpaths, Kotlin compilation dependency configurations, names ending in `compileClasspath` / `runtimeClasspath` (including variant classpaths), and classpaths of recognized applied plugins, walking parent buildscript classpaths where needed. Kotlin compiler/ABI infrastructure configurations are not indiscriminately resolved. Other resolvable configurations remain visible as `Skipped`, with an explanation. Include additional exact names by adding `<ImportConfigurations>customConfiguration anotherConfiguration</ImportConfigurations>` inside the Vistudio property group; unknown or non-resolvable names fail the import. All included configurations must resolve successfully; failures preserve the previous snapshot byte-for-byte and are reported in Kotlin Output.

Generated `PropertyGroup Label="GradleImport"` records the successful import time, actual Gradle/JVM versions, JVM home and scope. Generated `ItemGroup Label="GradleImport"` contains modules, configurations, libraries and frameworks. Reimport replaces this group, removing stale entries. Manual groups, settings, GUID and unknown XML are preserved; only the initial unmodified root-module placeholder is migrated. Never hand-edit the generated import group.

```xml
<ItemGroup Label="GradleImport">
  <Library Include="org.jetbrains.kotlin:kotlin-stdlib">
    <Module>:app</Module>
    <Configuration>runtimeClasspath</Configuration>
    <Version>2.4.21</Version>
    <Requested>org.jetbrains.kotlin:kotlin-stdlib:2.4.20</Requested>
    <Kind>Module</Kind>
    <Relationship>Direct</Relationship>
    <SelectionReason>forced</SelectionReason>
    <Origin>Gradle ResolutionResult</Origin>
  </Library>
</ItemGroup>
```

Solution Explorer groups libraries by module and configuration and frameworks by module, shows the selected version in the node caption and exposes provenance, requested selectors and selection reasons in the Properties window. Local file dependencies are explicitly unversioned. Framework detection recognizes Kotlin standard library, Spring/Boot, Ktor client/server, Compose Runtime and JUnit component anchors, plus applied Kotlin JVM/Multiplatform/Android, Compose, Spring Boot and Android plugins. Each version belongs to the evidenced component/plugin, not an inferred global framework version. Unknown frameworks stay visible as libraries; an applied plugin whose artifact version cannot be found says `version unavailable`.

The module list covers the root build, not recursive imports of included/composite builds or buildSrc. References into included builds retain their build-tree identity. Android SDK-dependent and non-Windows target configurations can fail when prerequisites are missing; no cross-platform success is promised. Import executes normal Gradle configuration and resolution code and may contact the project's repositories. It schedules only its metadata task, not compilation or application tasks.

## Verification

`ProjectFileTests` exercises generation/round trips, preserving unknown XML and dependencies, relative paths, project/global precedence, actual wrapper process environment, changing/clearing JDK, invalid JDK rejection, unsupported XML, external-edit conflicts and duplicate generation. Fake JDK executables deliberately cannot run, demonstrating that metadata operations do not execute them.

`tests/verify-project-metadata.ps1` can additionally generate a project against an actual Gradle root and installed JDK, invoke its `classes` task through the production process-start configuration and retain the XML/build output under artifacts. This is a core/Gradle integration check; it does not establish native Visual Studio UI acceptance.

The first stage passed 283 assertions and a real Gradle `classes` check. The resolved-import stage adds model validation, failed-exit and in-flight cancellation tests; the full suite now passes 303 assertions plus packaging/rendering checks. `tests/test-gradle-import.ps1` exercises a real two-module Kotlin fixture using the existing wrapper/JDK/plugin versions: conflict resolution, transitive/project dependencies, plugin/runtime markers, reimport, failure preservation, removal and actual application compilation/execution. See [Gradle import validation](gradle-import-validation.md) for the recorded integration and native IDE acceptance evidence.
