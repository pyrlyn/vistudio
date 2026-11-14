using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Vistudio.Core;
using Vistudio.Core.Projects;

internal static class GradleImportIntegration
{
    private static System.Collections.Generic.IEnumerable<ProjectTreeNode> Enumerate(ProjectTreeNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Enumerate));

    public static async Task<int> RunAsync(string folder, string jdk, string script)
    {
        folder = Path.GetFullPath(folder);
        script = Path.GetFullPath(script);
        var count = 0;
        void Check(bool success, string message)
        { if (!success) throw new Exception(message); count++; Console.WriteLine("PASS: " + message); }
        var settings = new KotlinSettings { GradleJavaHome = jdk };
        var path = Path.Combine(folder, KotlinProjectFile.FileName);
        var properties = Path.Combine(folder, "gradle.properties");
        var originalProperties = File.Exists(properties) ? File.ReadAllText(properties) : null;
        var settingsFile = Path.Combine(folder, "settings.gradle.kts");
        var appFile = Path.Combine(folder, "app", "build.gradle.kts");
        var originalSettings = File.ReadAllText(settingsFile);
        var originalApp = File.ReadAllText(appFile);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8)))
        {
            try
            {
                async Task<KotlinProjectFile> ImportAsync()
                {
                    var project = File.Exists(path) ? KotlinProjectFile.Load(path) : KotlinProjectFile.Create(folder, settings);
                    var model = await GradleProjectImporter.ResolveAsync(project, new KotlinSettings { GradleJavaHome = jdk }, script, Console.WriteLine, timeout.Token);
                    project.ApplyImport(model);
                    project.Save(timeout.Token);
                    return KotlinProjectFile.Load(path);
                }
                var imported = await ImportAsync();
                Check(imported.Items("Module").Select(e => (string?)e.Attribute("Include")).OrderBy(s => s)
                    .SequenceEqual(new[] { ":", ":app", ":shared" }), "Root and both submodules imported without placeholders");
                var selected = imported.Items("Library").Single(e => (string?)e.Attribute("Include") == "org.jetbrains.kotlin:kotlin-stdlib"
                    && (string?)e.Element("Module") == ":app" && (string?)e.Element("Configuration") == "runtimeClasspath");
                Check((string?)selected.Element("Version") == "2.4.21" && selected.Element("Requested")!.Value.Contains("2.4.20"), "Real requested 2.4.20 resolves to selected 2.4.21");
                Check(selected.Element("SelectionReason")!.Value.IndexOf("forced", StringComparison.OrdinalIgnoreCase) >= 0, "Gradle selection reason records force");
                Check(imported.Items("Library").Any(e => (string?)e.Attribute("Include") == "org.jetbrains:annotations"
                    && (string?)e.Element("Module") == ":app" && (string?)e.Element("Configuration") == "runtimeClasspath"), "Transitive annotations dependency imported");
                Check(imported.Items("Library").Any(e => (string?)e.Element("Kind") == "Project" && (string?)e.Element("ProjectPath") == ":shared"), "Project dependency is not misrepresented as a Maven library version");
                Check(imported.Items("Framework").Any(e => (string?)e.Attribute("Include") == "Kotlin/JVM Gradle plugin"
                    && (string?)e.Element("Version") == "2.4.21"), "Applied Kotlin JVM plugin has a resolved version");
                Check(imported.Items("Framework").Any(e => (string?)e.Attribute("Include") == "Kotlin standard library"
                    && (string?)e.Element("Version") == "2.4.21" && (string?)e.Element("Configuration") == "runtimeClasspath"), "Runtime framework marker uses selected component version");
                foreach (var module in new[] { "app", "shared" })
                    Check(imported.Items("SourceRoot", "GradleImport").Any(e => (string?)e.Element("Module") == ":" + module && (string?)e.Element("SourceSet") == "main"
                        && (string?)e.Element("Kind") == "Sources" && (string?)e.Element("Exists") == "true"
                        && string.Equals((string)e.Attribute("Include")!, Path.Combine(folder, module, "src", "main", "kotlin"), StringComparison.OrdinalIgnoreCase)),
                        "Gradle reports the configured Kotlin source root of :" + module);
                var tree = KotlinProjectTree.Build(imported);
                var appNode = tree.Children.Single(n => n.Kind == ProjectTreeKind.Module && n.Caption == "app");
                var sources = appNode.Children.Single(n => n.Caption == "main").Children.Single(n => n.Caption == "src/main/kotlin");
                Check(Enumerate(sources).Any(n => n.IsDocument && n.Path!.EndsWith(".kt", StringComparison.OrdinalIgnoreCase)), "Project tree lists real Kotlin files of :app");
                Check(appNode.Children.Single(n => n.Kind == ProjectTreeKind.Dependencies).Children.Any(c => c.Caption.StartsWith("runtimeClasspath")
                    && c.Children.Any(l => l.Caption.StartsWith("org.jetbrains.kotlin:kotlin-stdlib:2.4.21"))), "Project tree lists the selected stdlib version under runtimeClasspath");
                var originalCount = imported.Items("Library").Count();
                imported = await ImportAsync();
                Check(imported.Items("Library").Count() == originalCount, "Repeated import replaces the generated snapshot without duplicates");
                var snapshot = File.ReadAllText(path);
                File.WriteAllText(properties, (originalProperties ?? "") + "\nvistudioBroken=true\n");
                var failed = false;
                try { await ImportAsync(); } catch (InvalidOperationException) { failed = true; }
                Check(failed && File.ReadAllText(path) == snapshot, "Real unresolved configuration preserves the last successful XML byte-for-byte");
                if (originalProperties == null) File.Delete(properties); else File.WriteAllText(properties, originalProperties);
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel();
                    try { await GradleProjectImporter.ResolveAsync(imported, settings, script, Console.WriteLine, canceled.Token); }
                    catch (OperationCanceledException) { Check(File.ReadAllText(path) == snapshot, "Canceled import preserves the snapshot"); }
                }
                File.WriteAllText(settingsFile, originalSettings.Replace("include(\":app\", \":shared\")", "include(\":app\")"));
                File.WriteAllText(appFile, originalApp.Replace("implementation(project(\":shared\"))", ""));
                imported = await ImportAsync();
                Check(!imported.Items("Module").Any(e => (string?)e.Attribute("Include") == ":shared")
                    && !imported.Items("Library").Any(e => (string?)e.Element("ProjectPath") == ":shared"), "Removed module and project dependency disappear on reimport");
                File.WriteAllText(settingsFile, originalSettings); File.WriteAllText(appFile, originalApp);
                await ImportAsync();
                var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
                var code = await OwnedProcess.RunAsync(GradleCommand.Create(folder, jdk, new[] { ":app:run" }), line => { output.Enqueue(line); Console.WriteLine(line); }, timeout.Token);
                Check(code == 0 && output.Any(l => l.Contains("Resolved Gradle import works")), "Actual multi-module Kotlin program compiles and runs through Gradle");
                Console.WriteLine("PASS: " + count + " real Gradle model integration checks; " + path);
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
            finally
            {
                if (originalProperties == null) { if (File.Exists(properties)) File.Delete(properties); } else File.WriteAllText(properties, originalProperties);
                File.WriteAllText(settingsFile, originalSettings); File.WriteAllText(appFile, originalApp);
            }
        }
    }
}
