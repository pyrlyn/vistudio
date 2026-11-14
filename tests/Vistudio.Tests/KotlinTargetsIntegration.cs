using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vistudio.Core;
using Vistudio.Core.Projects;

/// <summary>
/// Real Gradle check of Kotlin Multiplatform targets: imports samples/kotlin-multiplatform through the production importer and
/// ImportModel.gradle, checks the .kotlinproj and tree, then builds and runs every target with its imported tasks.
/// </summary>
internal static class KotlinTargetsIntegration
{
    public static async Task<int> RunAsync(string sample, string script, string output)
    {
        Directory.CreateDirectory(output);
        var assertions = 0;
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new Exception(message);
            Console.WriteLine("PASS: " + message);
        }
        async Task<(int Exit, string Text)> Gradle(string name, IEnumerable<string> tasks)
        {
            var lines = new List<string>();
            var start = GradleCommand.Create(sample, "", tasks.ToArray());
            var exit = await OwnedProcess.RunAsync(start, line => { lock (lines) lines.Add(line); }, CancellationToken.None);
            var text = string.Join(Environment.NewLine, lines);
            File.WriteAllText(Path.Combine(output, name + ".log"), "gradlew " + string.Join(" ", tasks) + Environment.NewLine + text + Environment.NewLine + "EXIT " + exit);
            return (exit, text);
        }
        try
        {
            var folder = Path.Combine(output, "project");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
            var project = KotlinProjectFile.Create(folder, new KotlinSettings { GradleRoot = Path.GetFullPath(sample) });
            project.Save();
            var importLog = new List<string>();
            var model = await GradleProjectImporter.ResolveAsync(project, new KotlinSettings(), Path.GetFullPath(script), l => { lock (importLog) importLog.Add(l); }, CancellationToken.None);
            File.WriteAllLines(Path.Combine(output, "import.log"), importLog);
            project.ApplyImport(model);
            project.RefreshTools();
            project.Save();
            File.Copy(project.FilePath, Path.Combine(output, "vistudio.kotlinproj"), true);
            project = KotlinProjectFile.Load(project.FilePath);

            Check(project.ProjectKind == "Multiplatform" && project.Imported("Targets") == ":jvm :js :wasmJs :mingwX64",
                "Real import records a Multiplatform project with jvm, js, wasmJs and mingwX64");
            var targets = KotlinTarget.All(project).ToDictionary(t => t.Name);
            Check(targets["jvm"].RunTasks.SequenceEqual(new[] { ":jvmRun" }) && targets["js"].RunTasks.SequenceEqual(new[] { ":jsNodeDevelopmentRun" })
                && targets["wasmJs"].RunTasks.SequenceEqual(new[] { ":wasmJsNodeDevelopmentRun" }) && targets["mingwX64"].RunTasks.SequenceEqual(new[] { ":runDebugExecutableMingwX64" }),
                "Run tasks are the targets' real Gradle run tasks");
            Check(targets["mingwX64"].NativeTarget == "mingw_x64" && targets["wasmJs"].WasmTarget == "JS" && targets["js"].Environments.SequenceEqual(new[] { "Node.js" }),
                "Native, Wasm and JS variants are imported");
            Check(targets["mingwX64"].MainSourceSets.SequenceEqual(new[] { "commonMain", "mingwMain", "mingwX64Main", "nativeMain" })
                && targets["js"].MainSourceSets.Contains("webMain") && targets["jvm"].Configurations.Contains("jvmRuntimeClasspath"),
                "Targets record their compilations' source-set hierarchy and configurations");
            var hierarchy = project.Items("KotlinSourceSet", "GradleImport").ToDictionary(s => (string)s.Attribute("Include")!, s => (string?)s.Element("DependsOn") ?? "");
            Check(hierarchy["mingwX64Main"] == "mingwMain" && hierarchy["nativeMain"] == "commonMain" && hierarchy["wasmJsMain"] == "webMain",
                "The dependsOn hierarchy of the default template is imported");

            var tree = KotlinProjectTree.Build(project);
            var module = tree.Children.Single(n => n.Kind == ProjectTreeKind.Module);
            var captions = module.Children.Select(n => n.Caption).ToArray();
            File.WriteAllText(Path.Combine(output, "tree.txt"), Dump(tree, 0, 4));
            Check(captions.Take(3).SequenceEqual(new[] { "commonMain", "webMain", "Targets" }) && captions.Contains("Dependencies"),
                "The module shows existing shared source sets, then Targets and the remaining dependencies");
            var targetNodes = module.Children.Single(n => n.Kind == ProjectTreeKind.Targets).Children;
            Check(targetNodes.Select(n => n.Caption).SequenceEqual(new[] { "jvm (JVM)", "js (JS)", "wasmJs (Wasm)", "mingwX64 (Native)" }), "Targets are listed by platform");
            foreach (var node in targetNodes)
                Check(node.Children.First().Caption == node.Target!.Name + "Main" && node.Children.Last().Caption == "Dependencies",
                    node.Target.Name + " shows its own source set and dependencies");
            var jvmRuntime = targetNodes[0].Children.Last().Children.Single(n => n.Caption.StartsWith("jvmRuntimeClasspath", StringComparison.Ordinal));
            Check(jvmRuntime.Children.Any(n => n.Caption.StartsWith("org.jetbrains.kotlin:kotlin-stdlib:", StringComparison.Ordinal)),
                "The JVM target's runtime classpath lists the resolved Kotlin standard library");
            var launch = KotlinLaunchTarget.Resolve(project);
            Check(launch.MainClass == "sample.MainKt" && launch.SourceFile.EndsWith(Path.Combine("commonMain", "kotlin", "Main.kt"), StringComparison.OrdinalIgnoreCase),
                "F5 resolves the shared main of the JVM target's compilation");
            project.Set("StartupTarget", "wasmJs");
            var settings = new KotlinSettings();
            project.ApplyTo(settings);
            Check(settings.RunTasks == ":wasmJsNodeDevelopmentRun" && KotlinTarget.ForLaunch(project)!.Name == "wasmJs"
                && KotlinTarget.DebuggingNotSupported(KotlinTarget.ForLaunch(project)!).Contains(":wasmJsNodeDevelopmentRun"),
                "A Wasm startup target runs its Gradle task and explains that F5 debugging is not supported");

            // Build and run every target through its imported tasks, each in a separate Gradle invocation.
            var expected = new Dictionary<string, string> { ["jvm"] = "Platform: JVM ", ["js"] = "Platform: Web/JS", ["wasmJs"] = "Platform: Web/Wasm", ["mingwX64"] = "Platform: Native mingwX64" };
            foreach (var target in KotlinTarget.All(project))
            {
                var build = await Gradle(target.Name + "-build", target.BuildTasks);
                Check(build.Exit == 0, "Build " + target.Id + " (" + string.Join(" ", target.BuildTasks) + ") succeeds through Gradle");
                var run = await Gradle(target.Name + "-run", target.RunTasks);
                Check(run.Exit == 0 && run.Text.Contains(expected[target.Name]) && run.Text.Contains("VISTUDIO_KMP_PASS"),
                    "Run " + target.Id + " (" + string.Join(" ", target.RunTasks) + ") executes the target's actual implementation");
            }
            Console.WriteLine("PASS: " + assertions + " Kotlin target integration assertions. Logs: " + output);
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("FAIL: " + e.Message);
            File.WriteAllText(Path.Combine(output, "failure.log"), e.ToString());
            return 1;
        }
    }

    private static string Dump(ProjectTreeNode node, int depth, int maxDepth)
    {
        var text = new string(' ', depth * 2) + node.Caption + Environment.NewLine;
        if (depth >= maxDepth || node.Kind == ProjectTreeKind.SourceRoot || node.Kind == ProjectTreeKind.Configuration || node.Kind == ProjectTreeKind.Toolchain) return text;
        return text + string.Concat(node.Children.Select(c => Dump(c, depth + 1, maxDepth)));
    }
}
