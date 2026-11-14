using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Vistudio.Core;
using Vistudio.Core.Projects;

internal static class ProjectTreeTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "tree project");
        var app = Path.Combine(folder, "app");
        void Write(string path, string text = "") { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        Write(Path.Combine(folder, "gradlew.bat"), "@echo off\r\n");
        Write(Path.Combine(folder, "settings.gradle.kts"));
        Write(Path.Combine(folder, "build.gradle.kts"));
        Write(Path.Combine(folder, "gradle", "libs.versions.toml"));
        Write(Path.Combine(app, "build.gradle.kts"));
        Write(Path.Combine(app, "src", "main", "kotlin", "demo", "Main.kt"), "fun main() {}");
        Write(Path.Combine(app, "src", "main", "kotlin", ".hidden", "Secret.kt"));
        Write(Path.Combine(app, "src", "main", "kotlin", ".editorconfig"));
        Write(Path.Combine(app, "src", "main", "resources", "app.properties"));
        Write(Path.Combine(app, "src", "main", "resources", "empty", ".gitkeep"));
        Write(Path.Combine(app, "src", "test", "kotlin", "MainTest.kt"));
        Write(Path.Combine(app, "src", "extra", "kotlin", "Extra.kt"));

        // Unimported metadata: conventional layout, explicit hint, no fake dependency data.
        var project = KotlinProjectFile.Create(folder, new KotlinSettings());
        project.Save();
        var tree = KotlinProjectTree.Build(KotlinProjectFile.Load(project.FilePath));
        check(tree.Kind == ProjectTreeKind.Project && tree.Caption == "tree project", "Project node uses the metadata project name");
        check(tree.Children[0].Kind == ProjectTreeKind.Message && tree.Children[0].Caption.Contains("not imported"), "Unimported projects explain how to import the Gradle model");
        var placeholder = tree.Children.Single(n => n.Kind == ProjectTreeKind.Module);
        check(placeholder.Children.Where(n => n.Kind == ProjectTreeKind.SourceSet).Count() == 0, "Root placeholder without src shows no invented source sets");
        check(placeholder.Children.Single(n => n.Kind == ProjectTreeKind.Dependencies).Children.Single().Kind == ProjectTreeKind.Message,
            "Unimported dependencies are a hint, not an empty resolved list");

        // Imported snapshot: one aggregate root, :app with configured roots and resolved configurations.
        var document = XDocument.Load(project.FilePath);
        document.Root!.Add(new XElement("PropertyGroup", new XAttribute("Label", "GradleImport"), new XElement("ImportedAtUtc", "2026-11-12T00:00:00Z")));
        XElement Item(string kind, string include, params (string, string)[] values) =>
            new XElement(kind, new XAttribute("Include", include), values.Select(v => new XElement(v.Item1, v.Item2)));
        var generated = new XElement("ItemGroup", new XAttribute("Label", "GradleImport"),
            Item("Module", ":", ("Name", "tree-root"), ("Path", folder), ("BuildFile", Path.Combine(folder, "build.gradle.kts"))),
            Item("Module", ":app", ("Name", "app"), ("Path", app), ("BuildFile", Path.Combine(app, "build.gradle.kts"))),
            Item("Configuration", "runtimeClasspath", ("Module", ":app"), ("Status", "Resolved")),
            Item("Configuration", "compileClasspath", ("Module", ":app"), ("Status", "Resolved")),
            Item("Configuration", "tooling", ("Module", ":app"), ("Status", "Skipped")),
            Item("Library", "org.jetbrains:annotations", ("Module", ":app"), ("Configuration", "runtimeClasspath"), ("Version", "13.0"),
                ("Kind", "Module"), ("Relationship", "Transitive or constraint")),
            Item("Library", "org.jetbrains.kotlin:kotlin-stdlib", ("Module", ":app"), ("Configuration", "runtimeClasspath"), ("Version", "2.4.21"),
                ("Requested", "org.jetbrains.kotlin:kotlin-stdlib:2.4.20"), ("Kind", "Module"), ("Relationship", "Direct")),
            Item("Library", "project :shared", ("Module", ":app"), ("Configuration", "runtimeClasspath"), ("Kind", "Project"),
                ("ProjectPath", ":shared"), ("BuildTreePath", ":shared"), ("Relationship", "Direct")),
            Item("SourceRoot", Path.Combine(app, "src", "main", "kotlin"), ("Module", ":app"), ("SourceSet", "main"), ("Kind", "Sources"), ("Exists", "true")),
            Item("SourceRoot", Path.Combine(app, "src", "main", "java"), ("Module", ":app"), ("SourceSet", "main"), ("Kind", "Sources"), ("Exists", "false")),
            Item("SourceRoot", Path.Combine(app, "src", "main", "resources"), ("Module", ":app"), ("SourceSet", "main"), ("Kind", "Resources"), ("Exists", "true")),
            Item("SourceRoot", Path.Combine(app, "src", "test", "kotlin"), ("Module", ":app"), ("SourceSet", "test"), ("Kind", "Sources"), ("Exists", "true")));
        document.Root.Add(generated);
        document.Root.Elements("ItemGroup").Where(e => (string?)e.Attribute("Label") == "Modules").Remove();
        document.Save(project.FilePath);
        tree = KotlinProjectTree.Build(KotlinProjectFile.Load(project.FilePath));
        check(tree.Children.Select(n => n.Caption).SequenceEqual(new[] { "app", "Gradle Scripts" }),
            "Aggregate root without sources is folded into Gradle Scripts; modules come first");
        var module = tree.Children[0];
        check(module.Children.Select(n => n.Caption).SequenceEqual(new[] { "main", "test", "Dependencies" }),
            "Configured source sets are ordered main/test and the unconfigured extra directory is not invented");
        var main = module.Children[0];
        check(main.Children.Select(n => n.Caption).SequenceEqual(new[] { "src/main/kotlin", "src/main/resources" }),
            "Only existing roots are shown, sources before resources, relative to the module");
        var kotlinRoot = main.Children[0];
        check(kotlinRoot.HasChildren && kotlinRoot.Children.Single().Caption == "demo", "Hidden folders are skipped and visible folders are lazy containers");
        var mainFile = kotlinRoot.Children[0].Children.Single();
        check(mainFile.IsDocument && mainFile.Path == Path.Combine(app, "src", "main", "kotlin", "demo", "Main.kt"), "Source files map to absolute document paths");
        check(tree.MayContain(mainFile.Path!) && kotlinRoot.MayContain(mainFile.Path!) && !main.Children[1].MayContain(mainFile.Path!),
            "Path lookup only descends into the matching source root");
        var emptyFolder = main.Children[1].Children.Single(n => n.Caption == "empty");
        check(!emptyFolder.HasChildren, "A folder with only dot entries is not expandable by default");
        check(!new KotlinSettings().ShowHiddenFiles, "Hidden files are hidden by default");

        // Show hidden files option: dot files and folders appear, including in lazy children and expandability.
        var shown = KotlinProjectTree.Build(KotlinProjectFile.Load(project.FilePath), showHidden: true);
        var shownMain = shown.Children[0].Children[0];
        check(shownMain.Children[0].Children.Select(n => n.Caption).SequenceEqual(new[] { ".hidden", "demo", ".editorconfig" }),
            "Show hidden files lists dot folders and dot files in normal folder/file order");
        check(shownMain.Children[0].Children[0].Children.Single().Caption == "Secret.kt", "Dot folders expand when hidden files are shown");
        var shownEmpty = shownMain.Children[1].Children.Single(n => n.Caption == "empty");
        check(shownEmpty.HasChildren && shownEmpty.Children.Single().Caption == ".gitkeep", "Folders with only dot entries expand when hidden files are shown");
        var configurations = module.Children[2].Children;
        check(configurations.Select(n => n.Caption).SequenceEqual(new[] { "compileClasspath (0)", "runtimeClasspath (3)" }),
            "Only resolved configurations are listed, in classpath order, with counts");
        var runtime = configurations[1].Children;
        check(runtime[1].Kind == ProjectTreeKind.ProjectReference && runtime[1].Caption == ":shared", "Project dependencies are references, not versioned libraries");
        check(runtime[0].Caption == "org.jetbrains.kotlin:kotlin-stdlib:2.4.21" && runtime[0].Details.Any(d => d.Key == "Requested" && d.Value.EndsWith("2.4.20")),
            "Libraries show the selected version and keep the requested selector as provenance");
        check(runtime[2].Caption == "org.jetbrains:annotations:13.0  (transitive)", "Transitive libraries are marked");
        var scripts = tree.Children[1].Children.Select(n => n.Caption).ToArray();
        check(scripts.SequenceEqual(new[] { "settings.gradle.kts", "build.gradle.kts (root)", "build.gradle.kts (app)", "libs.versions.toml" }),
            "Gradle Scripts lists settings, per-module build files and the version catalog");
        var keys = tree.Children.Concat(module.Children).Select(n => n.Key).ToArray();
        check(keys.Distinct().Count() == keys.Length, "Sibling keys are unique for stable item ids");

        // Older snapshots without SourceRoot items fall back to the conventional layout.
        generated.Elements("SourceRoot").Remove();
        document.Save(project.FilePath);
        tree = KotlinProjectTree.Build(KotlinProjectFile.Load(project.FilePath));
        check(tree.Children[0].Children.Select(n => n.Caption).SequenceEqual(new[] { "main", "test", "extra", "Dependencies" }),
            "Snapshots without source roots use src/<set>/<language> directories");

        // Project lookup in a folder.
        check(KotlinProjectFile.Find(folder) == project.FilePath, "The default vistudio.kotlinproj is found");
        var other = Path.Combine(root, "named project");
        Directory.CreateDirectory(other);
        check(KotlinProjectFile.Find(other) == null, "No metadata means no project");
        File.Copy(project.FilePath, Path.Combine(other, "Demo.kotlinproj"));
        check(KotlinProjectFile.Find(other) == Path.Combine(other, "Demo.kotlinproj"), "A single custom-named .kotlinproj is found");
        File.Copy(project.FilePath, Path.Combine(other, "Second.kotlinproj"));
        try { KotlinProjectFile.Find(other); throw new Exception("Ambiguous project files accepted"); }
        catch (InvalidDataException) { check(true, "Several custom-named .kotlinproj files are ambiguous"); }
        var settings = KotlinSettings.LoadForProjectFile(Path.Combine(other, "Demo.kotlinproj"));
        check(settings.GradleRoot == other, "Explicit project file settings resolve the Gradle root relative to the loaded file");
    }
}
