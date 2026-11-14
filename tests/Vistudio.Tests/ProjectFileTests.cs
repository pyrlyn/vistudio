using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Vistudio.Core;
using Vistudio.Core.Projects;

internal static class ProjectFileTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "project metadata");
        var gradle = Path.Combine(folder, "app");
        Directory.CreateDirectory(Path.Combine(gradle, "gradle", "wrapper"));
        File.WriteAllText(Path.Combine(gradle, "gradlew.bat"), "@echo off\r\necho JAVA_HOME=%JAVA_HOME%\r\necho ARGS:%*\r\nexit /b 0\r\n");
        File.WriteAllText(Path.Combine(gradle, "gradle", "wrapper", "gradle-wrapper.properties"),
            "distributionUrl=https\\://services.gradle.org/distributions/gradle-8.14.7-bin.zip\n");
        string FakeJdk(string name, string version)
        {
            var home = Path.Combine(folder, name);
            Directory.CreateDirectory(Path.Combine(home, "bin"));
            File.WriteAllText(Path.Combine(home, "bin", "java.exe"), "not executable");
            File.WriteAllText(Path.Combine(home, "bin", "javac.exe"), "not executable");
            File.WriteAllText(Path.Combine(home, "release"), "JAVA_VERSION=\"" + version + "\"\nIMPLEMENTOR=\"Test vendor\"\n");
            return home;
        }
        var first = FakeJdk("jdk 11", "11.0.31");
        var second = FakeJdk("jdk 21", "21.0.9");
        void Rejected<T>(Action action, string message) where T : Exception
        {
            try { action(); } catch (T) { check(true, message); return; }
            throw new Exception(message);
        }
        var defaults = new KotlinSettings { GradleRoot = "app", BuildTasks = ":app:build", GradleJavaHome = first };
        var project = KotlinProjectFile.Create(folder, defaults);
        project.Save();
        var file = project.FilePath;
        var xml = XDocument.Load(file);
        check(xml.Root!.Name == "Project" && (string?)xml.Root.Attribute("Sdk") == null && !xml.Descendants("Import").Any(), "Project has .NET XML shape without MSBuild targets");
        check(project.GradleRoot == gradle && project.JdkVersion == "11.0.31", "Relative Gradle root and initial JDK metadata");
        check(project.Items("Tool").Any(t => (string?)t.Attribute("Include") == "Gradle Wrapper" && (string?)t.Element("Version") == "8.14.7"), "Declared wrapper version is detected without launching Gradle");
        check(!project.Items("Library").Any() && !project.Items("Framework").Any(), "Generation does not invent resolved dependencies");
        Rejected<IOException>(() => KotlinProjectFile.Create(folder, defaults).Save(), "Generate refuses to replace existing metadata");
        xml.Root.Add(new XElement("ItemGroup", new XAttribute("Label", "Libraries"), new XElement("Library",
            new XAttribute("Include", "example:library"), new XElement("Version", "1.2.3"), new XElement("Origin", "Declared"))));
        xml.Root.Add(new XElement("FutureExtension", new XAttribute("custom", "kept"), "value & text"));
        xml.Save(file);
        project = KotlinProjectFile.Load(file);
        project.SetJdk("jdk 21");
        project.RefreshTools();
        project.Save();
        var reloaded = KotlinProjectFile.Load(file);
        check(reloaded.JdkHome == second && reloaded.JdkVersion == "21.0.9", "JDK selection round trips with version derived from release");
        check((string?)reloaded.Items("Library").Single().Element("Version") == "1.2.3", "Dependency metadata survives JDK save");
        check((string?)XDocument.Load(file).Root!.Element("FutureExtension")?.Attribute("custom") == "kept", "Unknown XML survives updates");
        var effective = new KotlinSettings { GradleJavaHome = first, LanguageServerJavaHome = "server-jdk" };
        KotlinSettings.ApplyProject(folder, effective);
        check(effective.GradleJavaHome == second && effective.GradleRoot == gradle && effective.BuildTasks == ":app:build", "Project settings override global build defaults");
        check(effective.LanguageServerJavaHome == "server-jdk", "Project JDK does not modify language server JDK");
        var lines = new List<string>();
        var command = GradleCommand.Create(effective.GradleRoot, effective.GradleJavaHome, GradleCommand.ParseTasks(effective.BuildTasks));
        var exit = await OwnedProcess.RunAsync(command, line => { lock (lines) lines.Add(line); }, CancellationToken.None);
        check(exit == 0 && lines.Contains("JAVA_HOME=" + second) && lines.Any(l => l.EndsWith(":app:build")), "Selected JDK reaches the wrapper process, without executing a JDK during inventory");
        reloaded.SetJdk(""); reloaded.RefreshTools(); reloaded.Save();
        effective.GradleJavaHome = first;
        reloaded.ApplyTo(effective);
        check(effective.GradleJavaHome == first && reloaded.JdkVersion == "", "Empty project JDK restores global fallback");
        check(!reloaded.Items("Tool").Any(t => (string?)t.Attribute("Include") == "Gradle JDK"), "Clearing selection removes stale JDK snapshot");
        Rejected<ArgumentException>(() => reloaded.SetJdk(Path.Combine(second, "bin")), "Reject bin directory");
        File.Delete(Path.Combine(second, "bin", "javac.exe"));
        Rejected<ArgumentException>(() => reloaded.SetJdk(second), "Reject JRE without compiler");
        File.WriteAllText(Path.Combine(first, "release"), "IMPLEMENTOR=\"Missing version\"");
        Rejected<ArgumentException>(() => JdkInstallation.Read(first), "Reject unidentified version");
        File.AppendAllText(file, "\n<!-- external edit -->");
        Rejected<IOException>(() => reloaded.Save(), "Do not overwrite concurrent external edits");
        var malformed = Path.Combine(folder, "invalid.kotlinproj");
        File.WriteAllText(malformed, "<Project VistudioSchemaVersion=\"2\" />");
        Rejected<InvalidDataException>(() => KotlinProjectFile.Load(malformed), "Reject future schema instead of rewriting it");
        File.WriteAllText(malformed, "<!DOCTYPE Project [<!ENTITY x SYSTEM 'file:///not-read'>]><Project VistudioSchemaVersion=\"1\">&x;</Project>");
        Rejected<XmlException>(() => KotlinProjectFile.Load(malformed), "Reject XML entities");
        File.WriteAllText(malformed, "<Project VistudioSchemaVersion=\"1\"><PropertyGroup Label=\"Vistudio\"><JdkHome>a</JdkHome><JdkHome>b</JdkHome></PropertyGroup></Project>");
        Rejected<InvalidDataException>(() => KotlinProjectFile.Load(malformed), "Reject ambiguous duplicate settings");
        var untouched = new KotlinSettings { GradleRoot = "unchanged" };
        KotlinSettings.ApplyProject(root, untouched);
        check(untouched.GradleRoot == "unchanged", "Folders without metadata preserve existing behavior");
    }
}
