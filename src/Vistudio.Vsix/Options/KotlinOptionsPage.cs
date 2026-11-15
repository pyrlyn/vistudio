using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Vistudio.Core;

namespace Vistudio
{
    [ComVisible(true)]
    [Guid("9661d77a-63ae-494d-9699-9e6df41fc601")]
    public sealed class KotlinOptionsPage : DialogPage
    {
        // Global defaults only; a workspace's .vs\settings.json overrides them per window and is never written here.
        private KotlinSettings settings = KotlinSettings.LoadGlobal();
        [Category("Language server"), DisplayName("Executable / launcher"), Description("Absolute path to an installed Kotlin language server .exe, .cmd or .bat. Stdio transport only. Empty disables LSP.")]
        public string LanguageServerPath { get => settings.LanguageServerPath; set => settings.LanguageServerPath = value; }
        [Category("Language server"), DisplayName("Arguments"), Description("Arguments for the selected server release. Its stdout must contain only LSP messages. Restart the language server after changing settings.")]
        public string LanguageServerArguments { get => settings.LanguageServerArguments; set => settings.LanguageServerArguments = value; }
        [Category("Language server"), DisplayName("JAVA_HOME"), Description("Optional JDK for the language server, independent of the project JDK.")]
        public string LanguageServerJavaHome { get => settings.LanguageServerJavaHome; set => settings.LanguageServerJavaHome = value; }
        [Category("Java language server"), DisplayName("JDT LS folder"), Description("Absolute path to an extracted Eclipse JDT Language Server (contains plugins and config_win). Provides completion and navigation in .java files. Empty disables it; .java keeps coloring and debugging. Reopen the folder after changing.")]
        public string JavaLanguageServerPath { get => settings.JavaLanguageServerPath; set => settings.JavaLanguageServerPath = value; }
        [Category("Java language server"), DisplayName("JAVA_HOME"), Description("Optional Java 21+ runtime for JDT LS. Empty uses the Kotlin language server JAVA_HOME, then the runtime bundled with the Kotlin server.")]
        public string JavaLanguageServerJavaHome { get => settings.JavaLanguageServerJavaHome; set => settings.JavaLanguageServerJavaHome = value; }
        [Category("Gradle"), DisplayName("Root folder"), Description("Empty uses the Open Folder root. Otherwise an absolute path or a path relative to that root. Must contain gradlew.bat.")]
        public string GradleRoot { get => settings.GradleRoot; set => settings.GradleRoot = value; }
        [Category("Gradle"), DisplayName("JAVA_HOME"), Description("Optional JDK for Gradle. Empty inherits the Visual Studio environment.")]
        public string GradleJavaHome { get => settings.GradleJavaHome; set => settings.GradleJavaHome = value; }
        [Category("Gradle"), DisplayName("Build tasks"), Description("Task paths separated by spaces, for example :app:build. No CLI options.")]
        public string BuildTasks { get => settings.BuildTasks; set => settings.BuildTasks = value; }
        [Category("Gradle"), DisplayName("Test tasks")]
        public string TestTasks { get => settings.TestTasks; set => settings.TestTasks = value; }
        [Category("Gradle"), DisplayName("Run tasks"), Description("Run task paths, e.g. :app:jvmRun, :app:jsNodeDevelopmentRun or :app:runDebugExecutableMingwX64. Discover actual names with Tools > Kotlin > Gradle targets and tasks.")]
        public string RunTasks { get => settings.RunTasks; set => settings.RunTasks = value; }
        [Category("Editor"), DisplayName("Rainbow braces"), Description("Color matching curly braces. Reopen editors after changing this setting.")]
        public bool RainbowBraces { get => settings.RainbowBraces; set => settings.RainbowBraces = value; }
        [Category("Editor"), DisplayName("Error Lens (inline diagnostics)"), Description("Show severity-colored diagnostic messages after the end of the line with a line tint in every editor (Kotlin, Java and other languages), plus Gradle squiggles in Kotlin files. Colors: Fonts and Colors > Error Lens. Reopen editors after changing this setting.")]
        public bool EditorDiagnostics { get => settings.EditorDiagnostics; set => settings.EditorDiagnostics = value; }
        [Category("Editor"), DisplayName("Scroll bar mode"), Description("Vertical scroll bar in Kotlin and Java editors. Map shows a minimap of the file colored like the editor, with the visible area, changes, find results, bookmarks, breakpoints and errors. Inherit keeps Text Editor > All Languages > Scroll Bars. Applies to open editors.")]
        public ScrollBarMode ScrollBarMode { get => settings.ScrollBarMode; set => settings.ScrollBarMode = value; }
        [Category("Editor"), DisplayName("Scroll bar map width"), Description("Source overview width when Scroll bar mode is Map. Off keeps map mode marks without the code image.")]
        public ScrollBarMapWidth ScrollBarMapWidth { get => settings.ScrollBarMapWidth; set => settings.ScrollBarMapWidth = value; }
        [Category("Editor"), DisplayName("Scroll bar map preview"), Description("Show a code preview tooltip when hovering the map. Used when Scroll bar mode is Map.")]
        public bool ScrollBarMapPreview { get => settings.ScrollBarMapPreview; set => settings.ScrollBarMapPreview = value; }
        [Category("Solution Explorer"), DisplayName("Show hidden files"), Description("Show files and folders whose name starts with '.' in Kotlin project trees. Off by default.")]
        public bool ShowHiddenFiles { get => settings.ShowHiddenFiles; set => settings.ShowHiddenFiles = value; }
        [Category("Inlay hints"), DisplayName("Show inlay hints"), Description("When Kotlin and Java editors show inlay hints (types, parameter names and more, from the language servers). Visual Studio's own default shows them only while Alt+F1 is held. Applies to open editors.")]
        public InlayHintsDisplay InlayHints { get => settings.InlayHints; set => settings.InlayHints = value; }
        [Category("Inlay hints (Kotlin)"), DisplayName("Property types"), Description("Types of properties without an explicit type, e.g. val derived: Map<Int, String>.")]
        public bool InlayHintsPropertyTypes { get => settings.InlayHintsPropertyTypes; set => settings.InlayHintsPropertyTypes = value; }
        [Category("Inlay hints (Kotlin)"), DisplayName("Local variable types"), Description("Types of local variables and loop variables without an explicit type.")]
        public bool InlayHintsLocalVariableTypes { get => settings.InlayHintsLocalVariableTypes; set => settings.InlayHintsLocalVariableTypes = value; }
        [Category("Inlay hints (Kotlin)"), DisplayName("Function return types"), Description("Return types of expression-body functions, e.g. fun twice() = seed * 2: Int.")]
        public bool InlayHintsFunctionReturnTypes { get => settings.InlayHintsFunctionReturnTypes; set => settings.InlayHintsFunctionReturnTypes = value; }
        [Category("Inlay hints (Kotlin)"), DisplayName("Parameter types"), Description("Types of lambda parameters without an explicit type, e.g. { acc: Int, v: Int -> }.")]
        public bool InlayHintsParameterTypes { get => settings.InlayHintsParameterTypes; set => settings.InlayHintsParameterTypes = value; }
        [Category("Inlay hints (Kotlin)"), DisplayName("Lambda return expressions"), Description("Marks the value a multi-line lambda returns, e.g. ^map.")]
        public bool InlayHintsLambdaReturns { get => settings.InlayHintsLambdaReturns; set => settings.InlayHintsLambdaReturns = value; }
        [Category("Inlay hints (Kotlin)"), DisplayName("Implicit receivers and parameters"), Description("it: Int and this: Ledger at the start of multi-line lambdas.")]
        public bool InlayHintsImplicitReceivers { get => settings.InlayHintsImplicitReceivers; set => settings.InlayHintsImplicitReceivers = value; }
        [Category("Inlay hints (Kotlin)"), DisplayName("Ranges"), Description("Bounds of ranges such as 0 ≤ .. ≤ 10, until, downTo and ..<.")]
        public bool InlayHintsRanges { get => settings.InlayHintsRanges; set => settings.InlayHintsRanges = value; }
        [Category("Inlay hints (Kotlin)"), DisplayName("kotlin.time durations"), Description("Readable values of day-based kotlin.time.Duration expressions, e.g. 24h.")]
        public bool InlayHintsTimeDurations { get => settings.InlayHintsTimeDurations; set => settings.InlayHintsTimeDurations = value; }
        [Category("Inlay hints (Kotlin)"), DisplayName("Parameter names"), Description("Parameter names at call arguments, e.g. describe(count = 42). Turns off the three settings below too.")]
        public bool InlayHintsParameterNames { get => settings.InlayHintsParameterNames; set => settings.InlayHintsParameterNames = value; }
        [Category("Inlay hints (Kotlin)"), DisplayName("Parameter names: context parameters"), Description("Names of implicitly passed context parameters.")]
        public bool InlayHintsContextParameters { get => settings.InlayHintsContextParameters; set => settings.InlayHintsContextParameters = value; }
        [Category("Inlay hints (Kotlin)"), DisplayName("Parameter names: compiled code"), Description("Parameter names for calls into compiled libraries.")]
        public bool InlayHintsCompiledParameterNames { get => settings.InlayHintsCompiledParameterNames; set => settings.InlayHintsCompiledParameterNames = value; }
        [Category("Inlay hints (Kotlin)"), DisplayName("Parameter names: excluded methods"), Description("Parameter names also for methods IntelliJ excludes as obvious, e.g. println(message = ...).")]
        public bool InlayHintsExcludedParameterNames { get => settings.InlayHintsExcludedParameterNames; set => settings.InlayHintsExcludedParameterNames = value; }
        [Category("Inlay hints (Kotlin)"), DisplayName("Call chain types"), Description("Result type after each line of a multi-line call chain.")]
        public bool InlayHintsCallChains { get => settings.InlayHintsCallChains; set => settings.InlayHintsCallChains = value; }
        [Category("Inlay hints (Java)"), DisplayName("Parameter names"), Description("Parameter names at call arguments: All, Literals (only literal arguments) or None. Reopen the folder after changing Java settings.")]
        public JavaParameterNameHints JavaInlayHintsParameterNames { get => settings.JavaInlayHintsParameterNames; set => settings.JavaInlayHintsParameterNames = value; }
        [Category("Inlay hints (Java)"), DisplayName("var types"), Description("Types of var declarations whose initializer does not spell the type.")]
        public bool JavaInlayHintsVariableTypes { get => settings.JavaInlayHintsVariableTypes; set => settings.JavaInlayHintsVariableTypes = value; }
        [Category("Inlay hints (Java)"), DisplayName("Lambda parameter types"), Description("Types of lambda parameters without an explicit type.")]
        public bool JavaInlayHintsParameterTypes { get => settings.JavaInlayHintsParameterTypes; set => settings.JavaInlayHintsParameterTypes = value; }
        [Category("Inlay hints (Java)"), DisplayName("Format parameters"), Description("Argument hints for format strings such as String.format.")]
        public bool JavaInlayHintsFormatParameters { get => settings.JavaInlayHintsFormatParameters; set => settings.JavaInlayHintsFormatParameters = value; }
        public override void LoadSettingsFromStorage() => settings = KotlinSettings.LoadGlobal();
        public override void SaveSettingsToStorage()
        {
            var previous = KotlinSettings.LoadGlobal().ShowHiddenFiles;
            settings.Save();
            if (previous != settings.ShowHiddenFiles) KotlinProjectNode.OnTreeSettingsChanged();
            ScrollBarMapOptions.OnSettingsChanged();
            InlayHintsViewOptions.OnSettingsChanged();
        }
        public override void ResetSettings() => settings = new KotlinSettings();
    }
}
