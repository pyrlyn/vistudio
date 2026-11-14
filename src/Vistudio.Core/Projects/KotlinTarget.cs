using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Vistudio.Core.Projects
{
    /// <summary>
    /// One Kotlin Multiplatform target from the generated GradleImport snapshot (jvm, js, wasmJs, mingwX64, ...).
    /// Task paths are the ones Gradle reported as existing; nothing here runs Gradle.
    /// </summary>
    public sealed class KotlinTarget
    {
        /// <summary>Display order of platforms in the tree and in project kinds.</summary>
        public static readonly string[] PlatformOrder = { "JVM", "Android", "JS", "Wasm", "Native" };

        private KotlinTarget(XElement item)
        {
            string Value(string name) => ((string?)item.Element(name) ?? "").Trim();
            IReadOnlyList<string> List(string name) => Value(name).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            Name = (string?)item.Attribute("Include") ?? "";
            Module = Value("Module");
            Platform = Value("Platform");
            NativeTarget = Value("NativeTarget");
            WasmTarget = Value("WasmTarget");
            Environments = List("Environments");
            Compilations = List("Compilations");
            SourceSets = List("SourceSets");
            MainSourceSets = List("MainSourceSets");
            Configurations = List("Configurations");
            BuildTasks = List("BuildTasks");
            TestTasks = List("TestTasks");
            RunTasks = List("RunTasks");
        }

        public string Name { get; }
        /// <summary>Gradle path of the owning module, for example ":" or ":app".</summary>
        public string Module { get; }
        /// <summary>JVM, Android, JS, Wasm or Native.</summary>
        public string Platform { get; }
        /// <summary>Kotlin/Native target such as mingw_x64; empty for other platforms.</summary>
        public string NativeTarget { get; }
        /// <summary>JS or WASI for Wasm targets.</summary>
        public string WasmTarget { get; }
        /// <summary>Configured JS/Wasm environments: Node.js, Browser, D8.</summary>
        public IReadOnlyList<string> Environments { get; }
        public IReadOnlyList<string> Compilations { get; }
        /// <summary>Default source sets of the target's compilations, for example jvmMain and jvmTest.</summary>
        public IReadOnlyList<string> SourceSets { get; }
        /// <summary>All source sets compiled into the main compilation, including shared ones such as commonMain.</summary>
        public IReadOnlyList<string> MainSourceSets { get; }
        /// <summary>Dependency configurations of the target's compilations.</summary>
        public IReadOnlyList<string> Configurations { get; }
        public IReadOnlyList<string> BuildTasks { get; }
        public IReadOnlyList<string> TestTasks { get; }
        public IReadOnlyList<string> RunTasks { get; }

        /// <summary>Project-wide identity: ":jvm" in the root module, ":app:jvm" in module :app.</summary>
        public string Id => Qualify(Module, Name);
        public bool IsJvm => Platform == "JVM";

        /// <summary>Platform with its variant, for example "Native (mingw_x64)" or "Wasm/JS, Node.js".</summary>
        public string Description
        {
            get
            {
                var text = Platform + (WasmTarget.Length != 0 ? "/" + WasmTarget : "") + (NativeTarget.Length != 0 ? " (" + NativeTarget + ")" : "");
                return Environments.Count == 0 ? text : text + ", " + string.Join(", ", Environments);
            }
        }

        public IReadOnlyList<string> Tasks(string operation) =>
            operation == "build" ? BuildTasks : operation == "test" ? TestTasks : operation == "run" ? RunTasks
                : throw new ArgumentException("Unknown target operation: " + operation);

        public static string Qualify(string module, string name) => (module == ":" ? ":" : module + ":") + name;

        public static KotlinTarget FromItem(XElement item) => new KotlinTarget(item);

        /// <summary>Imported targets of all modules, in module, platform and name order.</summary>
        public static IReadOnlyList<KotlinTarget> All(KotlinProjectFile project) => Sort(project.Items("Target", "GradleImport").Select(FromItem));

        public static IReadOnlyList<KotlinTarget> Sort(IEnumerable<KotlinTarget> targets) => targets
            .OrderBy(t => t.Module == ":" ? 0 : 1).ThenBy(t => t.Module, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => Array.IndexOf(PlatformOrder, t.Platform) is var i && i >= 0 ? i : PlatformOrder.Length)
            .ThenBy(t => t.Name, StringComparer.Ordinal).ToList();

        /// <summary>
        /// JVM, Android, JS, Wasm, Native, Multiplatform (several platforms) or Unknown. Multiplatform targets count by platform;
        /// applied Kotlin/JVM and Android plugins count as JVM and Android.
        /// </summary>
        public static string ProjectKind(IEnumerable<KotlinTarget> targets, IEnumerable<XElement> frameworks)
        {
            var platforms = new HashSet<string>(targets.Select(t => t.Platform).Where(p => p.Length != 0), StringComparer.Ordinal);
            foreach (var framework in frameworks)
                switch ((string?)framework.Element("PluginId"))
                {
                    case "org.jetbrains.kotlin.jvm": platforms.Add("JVM"); break;
                    case "org.jetbrains.kotlin.android": case "com.android.application": case "com.android.library": platforms.Add("Android"); break;
                }
            return platforms.Count == 0 ? "Unknown" : platforms.Count == 1 ? platforms.Single() : "Multiplatform";
        }

        /// <summary>Finds an imported target by id (":app:jvm") or by a unique plain name ("jvm"); null when absent or ambiguous.</summary>
        public static KotlinTarget? Find(IReadOnlyList<KotlinTarget> targets, string value)
        {
            value = (value ?? "").Trim();
            if (value.Length == 0) return null;
            var exact = targets.FirstOrDefault(t => t.Id == value);
            if (exact != null) return exact;
            var named = targets.Where(t => t.Name == value).ToArray();
            return named.Length == 1 ? named[0] : null;
        }

        /// <summary>
        /// The target F5/Ctrl+F5 acts on: the StartupTarget, otherwise the only target of a project without JVM targets.
        /// Null means the JVM launch as before (no targets, or a JVM target exists and none was chosen).
        /// </summary>
        public static KotlinTarget? ForLaunch(KotlinProjectFile project)
        {
            var targets = All(project);
            var startup = project.Get("StartupTarget").Trim();
            if (startup.Length != 0)
                return Find(targets, startup) ?? throw new InvalidDataException("StartupTarget " + startup + " is not an imported Kotlin target. Import the Gradle model or choose another startup target in the project properties.");
            if (targets.Count == 0 || targets.Any(t => t.IsJvm)) return null;
            if (targets.Count == 1) return targets[0];
            throw new InvalidDataException("The project has several non-JVM Kotlin targets (" + string.Join(", ", targets.Select(t => t.Id)) +
                "). Choose StartupTarget in the project properties.");
        }

        /// <summary>Message for F5 on a target that the JVM debug adapter cannot debug.</summary>
        public static string DebuggingNotSupported(KotlinTarget target) =>
            "Debugging is not supported for the " + target.Description + " target " + target.Id + ". F5 debugging is available for JVM targets only. " +
            (target.RunTasks.Count != 0
                ? "Use Debug > Start Without Debugging (Ctrl+F5) to run " + string.Join(" ", target.RunTasks) + " through Gradle."
                : "The target has no Gradle run task.");
    }
}
