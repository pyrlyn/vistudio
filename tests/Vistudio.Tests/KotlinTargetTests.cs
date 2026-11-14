using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Vistudio.Core;
using Vistudio.Core.Projects;

/// <summary>Kotlin Multiplatform targets: import validation, project kind, StartupTarget, tree layout and launch selection.</summary>
internal static class KotlinTargetTests
{
    private static XElement Item(string kind, string include, params (string, string)[] values) =>
        new XElement(kind, new XAttribute("Include", include), values.Select(v => new XElement(v.Item1, v.Item2)));

    private static XElement Target(string name, string platform, string run, params (string, string)[] extra) => Item("Target", name,
        new[] { ("Module", ":"), ("Platform", platform), ("Compilations", "main test"), ("SourceSets", name + "Main " + name + "Test"),
            ("MainSourceSets", "commonMain " + name + "Main" + (platform == "JS" || platform == "Wasm" ? " webMain" : "")),
            ("Configurations", name + "CompileClasspath " + name + "RuntimeClasspath " + name + "TestCompileClasspath"),
            ("BuildTasks", ":" + name + "MainClasses"), ("TestTasks", ":" + name + "Test"), ("RunTasks", run) }.Concat(extra).ToArray());

    public static void Run(string root, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "kmp project");
        void Write(string path, string text = "") { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        string Src(string set) => Path.Combine(folder, "src", set, "kotlin");
        Write(Path.Combine(folder, "gradlew.bat"), "@echo off\r\n");
        Write(Path.Combine(folder, "build.gradle.kts"));
        Write(Path.Combine(Src("commonMain"), "Main.kt"), "package sample\n\nfun main() {}\n");
        Write(Path.Combine(Src("jvmMain"), "Platform.jvm.kt"), "package sample\n");
        Write(Path.Combine(Src("jsMain"), "Tool.kt"), "package tool\n\nfun main() {}\n");
        Write(Path.Combine(Src("webMain"), "Web.kt"), "package sample\n");
        Write(Path.Combine(Src("mingwX64Main"), "Platform.kt"), "package sample\n");

        var sets = new[] { ("commonMain", "", "js jvm mingwX64 wasmJs"), ("commonTest", "", "js jvm mingwX64 wasmJs"), ("jvmMain", "commonMain", "jvm"),
            ("jsMain", "webMain", "js"), ("wasmJsMain", "webMain", "wasmJs"), ("webMain", "commonMain", "js wasmJs"),
            ("nativeMain", "commonMain", "mingwX64"), ("mingwX64Main", "nativeMain", "mingwX64") };
        var configurationNames = new[] { "jvmCompileClasspath", "jvmRuntimeClasspath", "jvmTestCompileClasspath", "jsCompileClasspath", "jsRuntimeClasspath",
            "jsTestCompileClasspath", "wasmJsCompileClasspath", "wasmJsRuntimeClasspath", "wasmJsTestCompileClasspath", "mingwX64CompileClasspath",
            "mingwX64RuntimeClasspath", "mingwX64TestCompileClasspath", "metadataCommonMainCompileClasspath" };
        var model = new XElement("GradleModel", new XAttribute("SchemaVersion", "1"), new XAttribute("Complete", "true"),
            new XAttribute("RootDirectory", folder), new XAttribute("GradleVersion", "8.14.7"), new XAttribute("JavaVersion", "11"), new XAttribute("JavaHome", "jdk"),
            Item("Module", ":", ("Name", "kmp"), ("Path", folder), ("BuildFile", Path.Combine(folder, "build.gradle.kts"))),
            configurationNames.Select(c => Item("Configuration", c, ("Module", ":"), ("Status", "Resolved"))),
            Item("Library", "org.jetbrains.kotlin:kotlin-stdlib", ("Module", ":"), ("Configuration", "jvmRuntimeClasspath"), ("Version", "2.4.21"), ("Kind", "Module"), ("Relationship", "Direct")),
            Item("Framework", "Kotlin Multiplatform Gradle plugin", ("Module", ":"), ("PluginId", "org.jetbrains.kotlin.multiplatform"), ("Version", "2.4.21")),
            sets.SelectMany(s => new[]
            {
                Item("SourceRoot", Src(s.Item1), ("Module", ":"), ("SourceSet", s.Item1), ("Kind", "Sources"), ("Exists", Directory.Exists(Src(s.Item1)) ? "true" : "false")),
                Item("KotlinSourceSet", s.Item1, ("Module", ":"), ("DependsOn", s.Item2), ("Targets", s.Item3)),
            }),
            Target("wasmJs", "Wasm", ":wasmJsNodeDevelopmentRun", ("WasmTarget", "JS"), ("Environments", "Node.js")),
            Target("mingwX64", "Native", ":runDebugExecutableMingwX64", ("NativeTarget", "mingw_x64")),
            Target("jvm", "JVM", ":jvmRun"),
            Target("js", "JS", ":jsNodeDevelopmentRun", ("Environments", "Node.js Browser")));
        var modelPath = Path.Combine(root, "kmp-model.xml");
        model.Save(modelPath);

        // Import validation.
        var loaded = GradleProjectModel.Load(modelPath, folder);
        check(loaded.Items("Target").Count() == 4 && loaded.Items("KotlinSourceSet").Count() == sets.Length, "Targets and the source-set hierarchy are part of the validated model");
        void Invalid(Action<XElement> change, string message)
        {
            var copy = new XElement(model);
            change(copy);
            var path = Path.Combine(root, "kmp-invalid.xml");
            copy.Save(path);
            try { GradleProjectModel.Load(path, folder); check(false, message); }
            catch (InvalidDataException) { check(true, message); }
        }
        Invalid(m => m.Add(Target("jvm", "JVM", ":jvmRun")), "Duplicated targets of one module are rejected");
        Invalid(m => m.Elements("Target").First().Element("Platform")!.Value = "", "Targets without a platform are rejected");
        Invalid(m => m.Elements("Target").First().Element("Module")!.Value = ":missing", "Targets of unknown modules are rejected");
        Invalid(m => m.Add(Item("KotlinSourceSet", "commonMain", ("Module", ":"))), "Duplicated source sets are rejected");

        // Project kind.
        var targets = loaded.Items("Target").Select(KotlinTarget.FromItem).ToArray();
        check(KotlinTarget.ProjectKind(targets, loaded.Items("Framework")) == "Multiplatform", "Several platforms make a Multiplatform project");
        check(KotlinTarget.ProjectKind(targets.Where(t => t.Platform == "JS"), Array.Empty<XElement>()) == "JS", "A single JS target makes a JS project");
        check(KotlinTarget.ProjectKind(targets.Where(t => t.Platform == "Native"), Array.Empty<XElement>()) == "Native", "A single Native target makes a Native project");
        check(KotlinTarget.ProjectKind(Array.Empty<KotlinTarget>(), new[] { Item("Framework", "Kotlin/JVM Gradle plugin", ("PluginId", "org.jetbrains.kotlin.jvm")) }) == "JVM",
            "The Kotlin/JVM plugin without Multiplatform targets is a JVM project");
        check(KotlinTarget.ProjectKind(Array.Empty<KotlinTarget>(), Array.Empty<XElement>()) == "Unknown", "Without evidence the kind is Unknown");
        var wasm = targets.Single(t => t.Name == "wasmJs");
        check(wasm.Id == ":wasmJs" && wasm.Description == "Wasm/JS, Node.js" && targets.Single(t => t.Name == "mingwX64").Description == "Native (mingw_x64)",
            "Targets describe platform variants and environments");
        check(KotlinTarget.Qualify(":app", "jvm") == ":app:jvm", "Targets of submodules are qualified by the module path");

        // Project file: generated kind and targets, StartupTarget validation, run task selection.
        var project = KotlinProjectFile.Create(folder, new KotlinSettings { RunTasks = "run" });
        project.ApplyImport(loaded);
        project.Save();
        project = KotlinProjectFile.Load(project.FilePath);
        check(project.ProjectKind == "Multiplatform" && project.Imported("Targets") == ":jvm :js :wasmJs :mingwX64",
            "The import records the project kind and target ids in platform order");
        check(KotlinTarget.All(project).Select(t => t.Name).SequenceEqual(new[] { "jvm", "js", "wasmJs", "mingwX64" }), "Targets are listed JVM, JS, Wasm, Native");
        check(KotlinTarget.ForLaunch(project) == null, "Without a startup target a project with a JVM target keeps the JVM launch");
        var settings = new KotlinSettings();
        project.ApplyTo(settings);
        check(settings.RunTasks == "run", "Without a startup target Run uses RunTasks");
        project.Set("StartupTarget", "js");
        check(project.Get("StartupTarget") == ":js", "A unique target name is stored as its id");
        project.ApplyTo(settings);
        check(settings.RunTasks == ":jsNodeDevelopmentRun", "The startup target's run task replaces RunTasks");
        var js = KotlinTarget.ForLaunch(project)!;
        check(js.Name == "js" && !js.IsJvm, "F5/Ctrl+F5 act on the startup target");
        var message = KotlinTarget.DebuggingNotSupported(js);
        check(message.Contains("not supported") && message.Contains("Ctrl+F5") && message.Contains(":jsNodeDevelopmentRun") && message.Contains("JS, Node.js, Browser"),
            "The F5 message names the target and the Gradle run task for Ctrl+F5");
        try { project.Set("StartupTarget", "ios"); check(false, "Unknown startup targets are rejected"); }
        catch (ArgumentException e) { check(e.Message.Contains(":jvm, :js, :wasmJs, :mingwX64"), "Unknown startup targets are rejected with the valid ids"); }
        check(project.Get("StartupTarget") == ":js", "A rejected startup target leaves the stored value unchanged");
        project.Set("StartupTarget", ":jvm");
        check(KotlinTarget.ForLaunch(project)!.IsJvm, "A JVM startup target uses the JVM launch");
        project.Save();

        // Tree: shared source sets, a Targets node with per-target source sets and dependencies, remaining module dependencies.
        var tree = KotlinProjectTree.Build(KotlinProjectFile.Load(project.FilePath));
        var module = tree.Children.Single(n => n.Kind == ProjectTreeKind.Module);
        check(module.Children.Select(n => n.Caption).SequenceEqual(new[] { "commonMain", "webMain", "Targets", "Dependencies", "Frameworks" }),
            "Shared source sets come first, then Targets, the module dependencies and frameworks");
        check(module.Children[1].Details.Any(d => d.Key == "Depends on" && d.Value == "commonMain") && module.Children[1].Details.Any(d => d.Key == "Targets" && d.Value == "js, wasmJs"),
            "Shared source sets show their dependsOn parents and targets");
        var targetNodes = module.Children[2].Children;
        check(targetNodes.Select(n => n.Caption).SequenceEqual(new[] { "jvm (JVM)", "js (JS)", "wasmJs (Wasm)", "mingwX64 (Native)" }),
            "Targets are ordered by platform with the platform in the caption");
        check(targetNodes.All(n => n.Kind == ProjectTreeKind.Target && n.Target != null) && targetNodes[0].Target!.Id == ":jvm", "Target nodes carry their target");
        check(targetNodes[0].Details.Any(d => d.Key == "Startup target" && d.Value == "Yes") && targetNodes[1].Details.All(d => d.Key != "Startup target"),
            "Only the startup target is marked");
        check(targetNodes[0].Children.Select(n => n.Caption).SequenceEqual(new[] { "jvmMain", "Dependencies" }), "A target shows its own existing source sets and dependencies");
        check(targetNodes[2].Children.Select(n => n.Caption).SequenceEqual(new[] { "Dependencies" }), "A target without source directories still lists its dependencies");
        check(targetNodes[0].Children[1].Children.Select(n => n.Caption).SequenceEqual(new[] { "jvmCompileClasspath (0)", "jvmRuntimeClasspath (1)", "jvmTestCompileClasspath (0)" }),
            "Target dependencies are its compilation configurations in compilation order");
        check(module.Children[3].Children.Select(n => n.Caption).SequenceEqual(new[] { "metadataCommonMainCompileClasspath (0)" }),
            "Module dependencies keep only configurations that belong to no target");
        var platformFile = Path.Combine(Src("jvmMain"), "Platform.jvm.kt");
        check(module.Children[2].MayContain(platformFile) && targetNodes[0].MayContain(platformFile), "Document lookup descends into Targets");
        check(tree.Details.Any(d => d.Key == "Project kind" && d.Value == "Multiplatform") && tree.Details.Any(d => d.Key == "Startup target" && d.Value == ":jvm"),
            "The project node shows the kind and startup target");

        // F5 entry point: only the JVM target's main compilation (commonMain + jvmMain), never jsMain.
        var launch = KotlinLaunchTarget.Resolve(KotlinProjectFile.Load(project.FilePath));
        check(launch.MainClass == "sample.MainKt" && launch.SourceFile == Path.Combine(Src("commonMain"), "Main.kt"),
            "A main in a JS-only source set is not a JVM entry point candidate");

        // A stale StartupTarget is reported by F5 and ignored by Run, import and build.
        var document = XDocument.Load(project.FilePath);
        document.Root!.Elements("PropertyGroup").Single(e => (string?)e.Attribute("Label") == "Vistudio").SetElementValue("StartupTarget", ":iosArm64");
        document.Save(project.FilePath);
        project = KotlinProjectFile.Load(project.FilePath);
        try { KotlinTarget.ForLaunch(project); check(false, "Stale startup targets stop F5"); }
        catch (InvalidDataException e) { check(e.Message.Contains(":iosArm64"), "Stale startup targets stop F5 with the name"); }
        settings = new KotlinSettings();
        project.ApplyTo(settings);
        check(settings.RunTasks == "run", "A stale startup target does not block Run, import or build settings");

        // Projects without JVM targets: the only target is used, several need a startup target.
        document.Root.Elements("PropertyGroup").Single(e => (string?)e.Attribute("Label") == "Vistudio").SetElementValue("StartupTarget", "");
        document.Root.Elements("ItemGroup").Single(e => (string?)e.Attribute("Label") == "GradleImport").Elements("Target")
            .Where(t => (string?)t.Element("Platform") != "Native").Remove();
        document.Save(project.FilePath);
        check(KotlinTarget.ForLaunch(KotlinProjectFile.Load(project.FilePath))!.Name == "mingwX64", "A single non-JVM target is the launch target");
        document.Root.Elements("ItemGroup").Single(e => (string?)e.Attribute("Label") == "GradleImport").Add(Target("js", "JS", ":jsNodeDevelopmentRun"));
        document.Save(project.FilePath);
        try { KotlinTarget.ForLaunch(KotlinProjectFile.Load(project.FilePath)); check(false, "Several non-JVM targets need a startup target"); }
        catch (InvalidDataException e) { check(e.Message.Contains("StartupTarget"), "Several non-JVM targets ask for StartupTarget"); }

        // JVM-only and unimported projects keep the previous tree: no Targets node.
        var jvmOnly = Path.Combine(root, "jvm only");
        Write(Path.Combine(jvmOnly, "gradlew.bat"), "@echo off\r\n");
        Write(Path.Combine(jvmOnly, "src", "main", "kotlin", "Main.kt"), "fun main() {}\n");
        var jvmProject = KotlinProjectFile.Create(jvmOnly, new KotlinSettings());
        jvmProject.Save();
        var plain = KotlinProjectTree.Build(KotlinProjectFile.Load(jvmProject.FilePath));
        check(!plain.Children.Where(n => n.Kind == ProjectTreeKind.Module).SelectMany(n => n.Children).Any(n => n.Kind == ProjectTreeKind.Targets),
            "Projects without imported targets have no Targets node");
    }
}
