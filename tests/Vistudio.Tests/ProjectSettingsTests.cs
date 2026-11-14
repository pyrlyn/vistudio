using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Vistudio.Core;
using Vistudio.Core.Projects;

/// <summary>Project settings edited from the Properties window and the Solution Explorer nodes that replaced the tool window.</summary>
internal static class ProjectSettingsTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "settings project");
        void Write(string relative, string text) { var path = Path.Combine(folder, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        Write("gradlew.bat", "@echo off\r\n");
        Write("gradle/wrapper/gradle-wrapper.properties", "distributionUrl=https\\://services.gradle.org/distributions/gradle-8.14.7-bin.zip\n");
        Write("app/src/main/kotlin/Main.kt", "fun main() {}\n");
        Write("tools/gradlew.bat", "@echo off\r\n");
        var project = KotlinProjectFile.Create(folder, new KotlinSettings());
        project.Save();

        project = KotlinProjectFile.Load(project.FilePath);
        project.Set("BuildTasks", "  :app:build   check ");
        project.Set("ImportConfigurations", "integrationTestRuntimeClasspath, extra\tother");
        project.Set("DebugMainClass", "sample.MainKt");
        project.Set("DebugSourceFile", "app/src/main/kotlin/Main.kt");
        project.Set("DebugArguments", "one \"two words\"");
        project.Set("DebugWorkingDirectory", "app");
        project.Set("GradleRoot", "tools");
        project.Save();
        var saved = KotlinProjectFile.Load(project.FilePath);
        check(saved.Get("BuildTasks") == ":app:build check" && saved.Get("ImportConfigurations") == "integrationTestRuntimeClasspath extra other",
            "Task lists and import configurations are normalized when saved");
        check(saved.Get("DebugMainClass") == "sample.MainKt" && saved.Get("DebugArguments") == "one \"two words\"" && saved.GradleRoot == Path.Combine(folder, "tools"),
            "Debug settings and a relative Gradle root round trip");
        var snapshot = File.ReadAllText(project.FilePath);
        void Rejected(string name, string value, string message)
        {
            var target = KotlinProjectFile.Load(project.FilePath);
            try { target.Set(name, value); throw new Exception(message); }
            catch (ArgumentException) { check(target.Get(name) == saved.Get(name) && File.ReadAllText(project.FilePath) == snapshot, message); }
        }
        Rejected("BuildTasks", "build & whoami", "Shell syntax in task lists is rejected");
        Rejected("TestTasks", "", "Empty task lists are rejected");
        Rejected("GradleRoot", "missing", "A Gradle root without gradlew.bat is rejected");
        Rejected("ImportConfigurations", "bad;name", "Invalid configuration names are rejected");
        Rejected("DebugMainClass", "sample.Main Kt", "Invalid JVM class names are rejected");
        Rejected("DebugSourceFile", "app/missing.kt", "A missing debug source file is rejected");
        Rejected("DebugArguments", "\"unterminated", "Unterminated argument quotes are rejected");
        Rejected("DebugWorkingDirectory", "nowhere", "A missing working directory is rejected");
        Rejected("ProjectGuid", "{00000000-0000-0000-0000-000000000000}", "Read-only and unknown properties are rejected");
        check(KotlinProjectFile.EditableProperties.All(name => saved.Get(name) != null), "Every editable property is readable from the project file");

        // Nodes that replace the tool window inventory: toolchain, frameworks and declared metadata.
        project = KotlinProjectFile.Load(project.FilePath);
        project.Set("GradleRoot", ".");
        project.RefreshTools();
        project.Save();
        var document = XDocument.Load(project.FilePath);
        document.Root!.Add(new XElement("PropertyGroup", new XAttribute("Label", "GradleImport"),
            new XElement("ImportedAtUtc", "2026-11-13T00:00:00Z"), new XElement("GradleVersion", "8.14.7"),
            new XElement("JavaVersion", "11.0.31"), new XElement("JavaHome", "C:\\jdk")));
        XElement Item(string kind, string include, params (string, string)[] values) =>
            new XElement(kind, new XAttribute("Include", include), values.Select(v => new XElement(v.Item1, v.Item2)));
        document.Root.Add(new XElement("ItemGroup", new XAttribute("Label", "GradleImport"),
            Item("Module", ":", ("Name", "settings-project"), ("Path", folder)),
            Item("SourceRoot", Path.Combine(folder, "app", "src", "main", "kotlin"), ("Module", ":"), ("SourceSet", "main"), ("Kind", "Sources"), ("Exists", "true")),
            Item("Framework", "Kotlin standard library", ("Module", ":"), ("Configuration", "compileClasspath"), ("Version", "2.4.21"), ("Kind", "Resolved runtime component")),
            Item("Framework", "Kotlin standard library", ("Module", ":"), ("Configuration", "runtimeClasspath"), ("Version", "2.4.21"), ("Kind", "Resolved runtime component")),
            Item("Framework", "Kotlin/JVM Gradle plugin", ("Module", ":"), ("Configuration", "buildscript:classpath"), ("Version", ""), ("Kind", "Applied plugin"))));
        document.Root.Elements("ItemGroup").Single(e => (string?)e.Attribute("Label") == "Libraries")
            .Add(Item("Library", "org.example:declared", ("Version", "1.0"), ("Origin", "Declared")));
        document.Save(project.FilePath);
        var tree = KotlinProjectTree.Build(KotlinProjectFile.Load(project.FilePath));
        check(tree.Children.Select(n => n.Caption).SequenceEqual(new[] { "settings-project", "Toolchain", "Declared Metadata", "Gradle Scripts" }),
            "Toolchain and declared metadata nodes sit between modules and Gradle Scripts");
        var toolchain = tree.Children[1].Children.Select(n => n.Caption).ToArray();
        check(toolchain.SequenceEqual(new[] { "Gradle Wrapper 8.14.7", "Gradle 8.14.7 (last import)", "Gradle JVM 11.0.31 (last import)" }),
            "Toolchain shows the wrapper declaration and the Gradle/JVM that ran the last import");
        var frameworks = tree.Children[0].Children.Single(n => n.Kind == ProjectTreeKind.Frameworks).Children;
        check(frameworks.Select(n => n.Caption).SequenceEqual(new[] { "Kotlin standard library 2.4.21", "Kotlin/JVM Gradle plugin (version unavailable)" })
            && frameworks[0].Details.Any(d => d.Key == "Configurations" && d.Value == "compileClasspath, runtimeClasspath"),
            "Module frameworks are deduplicated across configurations and missing versions are explicit");
        var declared = tree.Children[2].Children.Single();
        check(declared.Caption == "Library: org.example:declared 1.0" && declared.Details.Any(d => d.Key == "Origin" && d.Value == "Declared"),
            "Manually declared libraries stay visible with their origin");
        check(KotlinProjectFile.Load(project.FilePath).Imported("GradleVersion") == "8.14.7", "Imported values are readable for the Properties window");
    }
}
