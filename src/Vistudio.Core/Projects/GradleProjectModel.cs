using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Vistudio.Core.Projects
{
    public sealed class GradleProjectModel
    {
        private readonly XElement root;
        /// <summary>Generated item kinds, in the order they are written into the GradleImport item group.</summary>
        public static readonly string[] ItemNames = { "Module", "Configuration", "Library", "Framework", "SourceRoot", "Target", "KotlinSourceSet" };
        private GradleProjectModel(XElement root) => this.root = root;
        public string GradleVersion => (string)root.Attribute("GradleVersion")!;
        public string JavaVersion => (string)root.Attribute("JavaVersion")!;
        public string JavaHome => (string)root.Attribute("JavaHome")!;
        public string RootDirectory => (string)root.Attribute("RootDirectory")!;
        public IEnumerable<XElement> Items(string name) => root.Elements(name).Select(e => new XElement(e));

        public static GradleProjectModel Load(string path, string expectedRoot)
        {
            XElement root;
            using (var reader = XmlReader.Create(path, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 }))
                root = XElement.Load(reader);
            if (root.Name != "GradleModel" || (string?)root.Attribute("SchemaVersion") != "1" || (string?)root.Attribute("Complete") != "true")
                throw new InvalidDataException("Gradle did not produce a complete supported project model.");
            foreach (var name in new[] { "RootDirectory", "GradleVersion", "JavaVersion", "JavaHome" })
                if (string.IsNullOrWhiteSpace((string?)root.Attribute(name))) throw new InvalidDataException("Missing Gradle model field: " + name);
            if (!string.Equals(Path.GetFullPath((string)root.Attribute("RootDirectory")!).TrimEnd('\\', '/'),
                Path.GetFullPath(expectedRoot).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Gradle returned a model for another root directory.");
            var modules = root.Elements("Module").Select(e => (string?)e.Attribute("Include") ?? "").ToArray();
            if (!modules.Contains(":") || modules.Distinct(StringComparer.Ordinal).Count() != modules.Length || modules.Any(m => !m.StartsWith(":", StringComparison.Ordinal)))
                throw new InvalidDataException("The imported Gradle module list is invalid.");
            var configurations = new HashSet<string>(StringComparer.Ordinal);
            var allConfigurations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in root.Elements("Configuration"))
            {
                var status = (string?)item.Element("Status");
                var key = (string?)item.Element("Module") + "\t" + (string?)item.Attribute("Include");
                if ((status != "Resolved" && status != "Skipped") || !allConfigurations.Add(key))
                    throw new InvalidDataException("An imported configuration is unresolved or duplicated.");
                if (status == "Resolved") configurations.Add(key);
            }
            foreach (var item in root.Elements())
            {
                if (!ItemNames.Contains(item.Name.LocalName)
                    || item.Name.NamespaceName.Length != 0 || string.IsNullOrWhiteSpace((string?)item.Attribute("Include"))
                    || item.Elements().Any(e => e.HasElements) || item.Elements().GroupBy(e => e.Name).Any(g => g.Count() > 1))
                    throw new InvalidDataException("Invalid imported item.");
                if (item.Name == "Module") continue;
                if (!modules.Contains((string?)item.Element("Module"))) throw new InvalidDataException("Imported item references an unknown module.");
                if (item.Name == "SourceRoot" && (!Path.IsPathRooted((string)item.Attribute("Include")!) || string.IsNullOrWhiteSpace((string?)item.Element("SourceSet"))
                    || ((string?)item.Element("Kind") != "Sources" && (string?)item.Element("Kind") != "Resources")))
                    throw new InvalidDataException("Imported source root must be an absolute Sources/Resources directory of a named source set.");
                if (item.Name == "Library" && (!configurations.Contains((string?)item.Element("Module") + "\t" + (string?)item.Element("Configuration"))
                    || ((string?)item.Element("Kind") == "Module" && string.IsNullOrWhiteSpace((string?)item.Element("Version")))))
                    throw new InvalidDataException("Imported library has no resolved configuration/version.");
                if (item.Name == "Target" && string.IsNullOrWhiteSpace((string?)item.Element("Platform")))
                    throw new InvalidDataException("Imported Kotlin target has no platform.");
            }
            foreach (var kind in new[] { "Target", "KotlinSourceSet" })
                if (root.Elements(kind).GroupBy(e => (string?)e.Element("Module") + "\t" + (string?)e.Attribute("Include")).Any(g => g.Count() > 1))
                    throw new InvalidDataException("Imported Kotlin " + (kind == "Target" ? "target" : "source set") + " is duplicated.");
            // Only exact component anchors are classified. Every other library remains visible as a library.
            foreach (var library in root.Elements("Library").ToArray())
            {
                var configuration = (string?)library.Element("Configuration") ?? "";
                if (configuration == "buildscript:classpath" || configuration.IndexOf("compiler", StringComparison.OrdinalIgnoreCase) >= 0
                    || configuration.IndexOf("plugin", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                var label = FrameworkName((string)library.Attribute("Include")!);
                if (label == null || (string?)library.Element("Kind") != "Module") continue;
                root.Add(new XElement("Framework", new XAttribute("Include", label),
                    new XElement("Version", (string?)library.Element("Version")), new XElement("Module", (string?)library.Element("Module")),
                    new XElement("Configuration", configuration), new XElement("Kind", "Resolved runtime component"),
                    new XElement("Evidence", (string)library.Attribute("Include")!), new XElement("Origin", "Gradle ResolutionResult component marker")));
            }
            return new GradleProjectModel(root);
        }

        private static string? FrameworkName(string coordinate)
        {
            switch (coordinate)
            {
                case "org.jetbrains.kotlin:kotlin-stdlib": return "Kotlin standard library";
                case "org.springframework.boot:spring-boot": return "Spring Boot";
                case "org.springframework:spring-core": return "Spring Framework";
                case "io.ktor:ktor-server-core": case "io.ktor:ktor-server-core-jvm": return "Ktor Server";
                case "io.ktor:ktor-client-core": case "io.ktor:ktor-client-core-jvm": return "Ktor Client";
                case "org.jetbrains.compose.runtime:runtime": case "org.jetbrains.compose.runtime:runtime-desktop":
                case "androidx.compose.runtime:runtime": case "androidx.compose.runtime:runtime-android": return "Compose Runtime";
                case "org.junit.jupiter:junit-jupiter-api": return "JUnit Jupiter";
                case "junit:junit": return "JUnit";
                default: return null;
            }
        }
    }
}
