using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Vistudio.Core;
using Vistudio.Core.Projects;

internal static class GradleModelTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "import tests"); Directory.CreateDirectory(folder);
        var modelPath = Path.Combine(folder, "model.xml");
        var wrapper = Path.Combine(folder, "gradlew.bat");
        var script = Path.Combine(folder, "model init.gradle"); File.WriteAllText(script, "// Process fixture only");
        var modelXml = new XElement("GradleModel", new XAttribute("SchemaVersion", "1"), new XAttribute("Complete", "true"),
            new XAttribute("RootDirectory", folder), new XAttribute("GradleVersion", "8.14.7"), new XAttribute("JavaVersion", "11"), new XAttribute("JavaHome", "test-jdk"),
            new XElement("Module", new XAttribute("Include", ":"), new XElement("Path", folder)),
            new XElement("Module", new XAttribute("Include", ":app"), new XElement("Path", Path.Combine(folder, "app"))),
            new XElement("Configuration", new XAttribute("Include", "runtimeClasspath"), new XElement("Module", ":app"), new XElement("Status", "Resolved")),
            new XElement("Configuration", new XAttribute("Include", "tooling"), new XElement("Module", ":app"), new XElement("Status", "Skipped")),
            new XElement("Library", new XAttribute("Include", "org.jetbrains.kotlin:kotlin-stdlib"), new XElement("Module", ":app"),
                new XElement("Configuration", "runtimeClasspath"), new XElement("Version", "2.4.21"), new XElement("Requested", "2.4.20"), new XElement("Kind", "Module")),
            new XElement("Library", new XAttribute("Include", "file.jar"), new XElement("Module", ":app"),
                new XElement("Configuration", "runtimeClasspath"), new XElement("Version", ""), new XElement("Kind", "File")),
            new XElement("SourceRoot", new XAttribute("Include", Path.Combine(folder, "app", "src", "main", "kotlin")), new XElement("Module", ":app"),
                new XElement("SourceSet", "main"), new XElement("Kind", "Sources"), new XElement("Exists", "false")));
        modelXml.Save(modelPath);
        var model = GradleProjectModel.Load(modelPath, folder);
        check(model.Items("Framework").Single().Element("Version")!.Value == "2.4.21", "Framework derives its version from the resolved component, not the requested version");
        check(model.Items("Configuration").Count() == 2, "Skipped configurations remain visible in the snapshot");
        check(model.Items("SourceRoot").Single().Element("SourceSet")!.Value == "main", "Configured source roots are part of the validated model");
        check(model.Items("Library").Single(e => (string?)e.Element("Kind") == "File").Element("Version")!.Value == "", "Unversioned files do not get guessed versions");
        File.WriteAllText(wrapper, "@echo off\r\ntype model.xml > \"%VISTUDIO_MODEL_OUTPUT%\"\r\nexit /b 0\r\n");
        var project = KotlinProjectFile.Create(folder, new KotlinSettings()); project.Save();
        var doc = XDocument.Load(project.FilePath);
        doc.Root!.Add(new XElement("ItemGroup", new XAttribute("Label", "Manual"), new XElement("Library", new XAttribute("Include", "manual:kept"))));
        doc.Save(project.FilePath);
        project = KotlinProjectFile.Load(project.FilePath);
        var resolved = await GradleProjectImporter.ResolveAsync(project, new KotlinSettings(), script, _ => { }, CancellationToken.None);
        project.ApplyImport(resolved); project.Save();
        check(project.Items("Module").Count() == 2 && project.ImportedAtUtc.Length != 0, "Import replaces only the generated root placeholder and records time");
        check(project.Items("Library").Any(e => (string?)e.Attribute("Include") == "manual:kept"), "Import preserves manually maintained entries");
        project.ApplyImport(resolved); project.Save();
        check(project.Items("Library").Count() == 3, "Repeated snapshots do not accumulate library entries");
        var snapshot = File.ReadAllText(project.FilePath);
        File.WriteAllText(wrapper, "@echo off\r\ntype model.xml > \"%VISTUDIO_MODEL_OUTPUT%\"\r\nexit /b 7\r\n");
        try { await GradleProjectImporter.ResolveAsync(project, new KotlinSettings(), script, _ => { }, CancellationToken.None); throw new Exception("Failed process accepted"); }
        catch (InvalidOperationException) { check(File.ReadAllText(project.FilePath) == snapshot, "Nonzero Gradle exit rejects even a complete-looking output"); }
        File.WriteAllText(wrapper, "@echo off\r\necho started\r\nping -n 30 127.0.0.1 >nul\r\necho late>late-model.txt\r\n");
        using (var cancellation = new CancellationTokenSource())
        {
            try { await GradleProjectImporter.ResolveAsync(project, new KotlinSettings(), script, line => { if (line == "started") cancellation.Cancel(); }, cancellation.Token); throw new Exception("Canceled process accepted"); }
            catch (OperationCanceledException) { check(File.ReadAllText(project.FilePath) == snapshot && !File.Exists(Path.Combine(folder, "late-model.txt")), "Cancellation stops the owned import process and preserves metadata"); }
        }
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { project.Save(canceled.Token); throw new Exception("Canceled commit accepted"); }
            catch (OperationCanceledException) { check(File.ReadAllText(project.FilePath) == snapshot, "Canceled commit cannot replace the XML file"); }
        }
        void Invalid(Action<XElement> mutate, string reason)
        {
            var changed = new XElement(modelXml); mutate(changed); changed.Save(modelPath);
            try { GradleProjectModel.Load(modelPath, folder); throw new Exception(reason); }
            catch (InvalidDataException) { check(true, reason); }
        }
        Invalid(e => e.SetAttributeValue("Complete", "false"), "Reject incomplete models");
        Invalid(e => e.SetAttributeValue("SchemaVersion", "9"), "Reject unsupported model schemas");
        Invalid(e => e.SetAttributeValue("RootDirectory", Path.Combine(folder, "other")), "Reject models from another workspace");
        Invalid(e => e.Elements("Configuration").First().SetElementValue("Status", "Failed"), "Reject failed resolution status");
        Invalid(e => e.Elements("Library").First().SetElementValue("Configuration", "tooling"), "A library cannot claim to be resolved in a skipped configuration");
        Invalid(e => e.Elements("Library").First().SetElementValue("Version", ""), "Maven components must have a selected version");
        Invalid(e => e.Elements("Library").First().SetElementValue("Module", ":unknown"), "Reject unknown module references");
        Invalid(e => e.Add(new XElement(e.Elements("Module").First())), "Reject duplicate module identities");
        Invalid(e => e.Elements("SourceRoot").Single().SetAttributeValue("Include", "src/main/kotlin"), "Reject relative source roots");
        Invalid(e => e.Elements("SourceRoot").Single().SetElementValue("Kind", "Generated"), "Reject unknown source root kinds");
        Invalid(e => e.Elements("SourceRoot").Single().SetElementValue("Module", ":missing"), "Reject source roots of unknown modules");
        File.WriteAllText(modelPath, "<!DOCTYPE GradleModel [<!ENTITY x SYSTEM 'file:///not-read'>]><GradleModel>&x;</GradleModel>");
        try { GradleProjectModel.Load(modelPath, folder); throw new Exception("DTD accepted"); }
        catch (XmlException) { check(true, "Model XML does not expand entities"); }
        var start = GradleCommand.CreateModelImport(folder, "", script, Path.Combine(folder, "output file.xml"));
        check(start.Arguments.Contains("--no-configure-on-demand") && start.Arguments.EndsWith(":vistudioImportModel6cc1f5c0\""), "Importer configures all modules and invokes only the root metadata task");
        check(start.EnvironmentVariables["VISTUDIO_MODEL_OUTPUT"]!.EndsWith("output file.xml"), "Output path is passed through the environment, not shell interpolation");
    }
}
