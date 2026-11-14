using System;
using System.IO;

namespace Vistudio.Core
{
    public sealed class KotlinSettings
    {
        public string LanguageServerPath { get; set; } = "";
        public string LanguageServerArguments { get; set; } = "--stdio";
        public string LanguageServerJavaHome { get; set; } = "";
        /// <summary>Folder of a user-installed Eclipse JDT Language Server for .java editing features. Empty disables it.</summary>
        public string JavaLanguageServerPath { get; set; } = "";
        /// <summary>Java 21+ runtime for JDT LS. Empty uses LanguageServerJavaHome or the Kotlin server's bundled runtime.</summary>
        public string JavaLanguageServerJavaHome { get; set; } = "";
        public string GradleRoot { get; set; } = "";
        public string GradleJavaHome { get; set; } = "";
        public string BuildTasks { get; set; } = "build";
        public string TestTasks { get; set; } = "test";
        public string RunTasks { get; set; } = "run";
        public bool RainbowBraces { get; set; } = true;
        public bool EditorDiagnostics { get; set; } = true;
        /// <summary>Vertical scroll bar mode in Kotlin and Java editors. Inherit keeps the Visual Studio setting.</summary>
        public ScrollBarMode ScrollBarMode { get; set; } = ScrollBarMode.Inherit;
        /// <summary>Source overview width in map mode.</summary>
        public ScrollBarMapWidth ScrollBarMapWidth { get; set; } = ScrollBarMapWidth.Medium;
        /// <summary>Show the code preview tooltip when hovering the map.</summary>
        public bool ScrollBarMapPreview { get; set; } = true;
        /// <summary>Show files and folders whose name starts with '.' in the .kotlinproj Solution Explorer tree.</summary>
        public bool ShowHiddenFiles { get; set; }
        /// <summary>When Kotlin and Java editors show inlay hints. Visual Studio's own default only shows them while Alt+F1 is held.</summary>
        public InlayHintsDisplay InlayHints { get; set; } = InlayHintsDisplay.Always;
        // Kotlin inlay hint kinds (see InlayHints.KotlinOptions). All on.
        public bool InlayHintsPropertyTypes { get; set; } = true;
        public bool InlayHintsLocalVariableTypes { get; set; } = true;
        public bool InlayHintsFunctionReturnTypes { get; set; } = true;
        public bool InlayHintsParameterTypes { get; set; } = true;
        public bool InlayHintsLambdaReturns { get; set; } = true;
        public bool InlayHintsImplicitReceivers { get; set; } = true;
        public bool InlayHintsRanges { get; set; } = true;
        public bool InlayHintsTimeDurations { get; set; } = true;
        public bool InlayHintsParameterNames { get; set; } = true;
        public bool InlayHintsContextParameters { get; set; } = true;
        public bool InlayHintsCompiledParameterNames { get; set; } = true;
        public bool InlayHintsExcludedParameterNames { get; set; } = true;
        public bool InlayHintsCallChains { get; set; } = true;
        // Java inlay hint kinds (JDT LS java.inlayHints). All on.
        public JavaParameterNameHints JavaInlayHintsParameterNames { get; set; } = JavaParameterNameHints.All;
        public bool JavaInlayHintsVariableTypes { get; set; } = true;
        public bool JavaInlayHintsParameterTypes { get; set; } = true;
        public bool JavaInlayHintsFormatParameters { get; set; } = true;

        public static string FilePath => SettingsXml.PathFor("settings.xml");

        /// <summary>Tools &gt; Options values only. Use this to edit and <see cref="Save"/> the global defaults.</summary>
        /// <remarks>Unlike the other languages, a damaged file throws: callers report it instead of silently resetting Kotlin.</remarks>
        public static KotlinSettings LoadGlobal() => SettingsXml.Read<KotlinSettings>(FilePath);

        /// <summary>Effective settings of this Visual Studio window: global values overridden by its workspace's <c>.vs\settings.json</c>.</summary>
        public static KotlinSettings Load()
        {
            var settings = LoadGlobal();
            WorkspaceSettings.ApplyTo(settings, WorkspaceSettings.CurrentRoot);
            return settings;
        }

        public static KotlinSettings LoadForFolder(string folder)
        {
            var settings = LoadGlobal();
            ApplyProject(folder, settings);
            WorkspaceSettings.ApplyTo(settings, WorkspaceSettings.RootFor(folder));
            return settings;
        }

        public static void ApplyProject(string folder, KotlinSettings settings)
        {
            var path = Projects.KotlinProjectFile.Find(folder);
            if (path != null) Projects.KotlinProjectFile.Load(path).ApplyTo(settings);
        }

        /// <summary>Global defaults overridden by one explicit project file, for example a project loaded in a solution.</summary>
        public static KotlinSettings LoadForProjectFile(string projectFile)
        {
            var settings = LoadGlobal();
            Projects.KotlinProjectFile.Load(projectFile).ApplyTo(settings);
            WorkspaceSettings.ApplyTo(settings, WorkspaceSettings.RootFor(Path.GetDirectoryName(Path.GetFullPath(projectFile))));
            return settings;
        }

        public void Save()
        {
            GradleCommand.ParseTasks(BuildTasks);
            GradleCommand.ParseTasks(TestTasks);
            GradleCommand.ParseTasks(RunTasks);
            SettingsXml.Write(this, FilePath);
        }
    }
}
