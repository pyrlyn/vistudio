using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace Vistudio.Core.Projects
{
    /// <summary>IDE metadata only: never evaluated by MSBuild or used to replace Gradle build scripts.</summary>
    public sealed class KotlinProjectFile
    {
        public const string FileName = "vistudio.kotlinproj";
        private readonly XDocument document;
        private string? savedText;
        public string FilePath { get; }
        public string DirectoryPath => Path.GetDirectoryName(FilePath)!;
        private XElement Properties => document.Root!.Elements("PropertyGroup")
            .Single(e => (string?)e.Attribute("Label") == "Vistudio");

        private KotlinProjectFile(string path, XDocument document, string? savedText)
        {
            FilePath = Path.GetFullPath(path);
            this.document = document;
            this.savedText = savedText;
        }

        public string Name => Get("ProjectName");
        public string JdkHome => Get("JdkHome");
        public string JdkVersion => Get("JdkVersion");
        public string GradleRoot => ResolvePath(Get("GradleRoot", "."));
        public string Get(string name, string fallback = "") => (string?)Properties.Element(name) ?? fallback;
        public string ResolvePath(string path) => Path.GetFullPath(Path.Combine(DirectoryPath, path));

        public static KotlinProjectFile Create(string folder, KotlinSettings defaults)
        {
            folder = Path.GetFullPath(folder);
            var root = new XElement("Project", new XAttribute("VistudioSchemaVersion", "1"),
                new XComment("IDE metadata. Gradle remains the source of truth for Kotlin builds and dependencies."),
                new XElement("PropertyGroup", new XAttribute("Label", "Vistudio"),
                    new XElement("ProjectName", Path.GetFileName(folder)),
                    new XElement("ProjectGuid", Guid.NewGuid().ToString("B")),
                    new XElement("GradleRoot", string.IsNullOrWhiteSpace(defaults.GradleRoot) ? "." : defaults.GradleRoot),
                    new XElement("BuildTasks", defaults.BuildTasks), new XElement("TestTasks", defaults.TestTasks),
                    new XElement("RunTasks", defaults.RunTasks), new XElement("JdkHome", ""), new XElement("JdkVersion", "")),
                new XElement("ItemGroup", new XAttribute("Label", "Modules")),
                new XElement("ItemGroup", new XAttribute("Label", "Tools")),
                new XElement("ItemGroup", new XAttribute("Label", "Libraries")),
                new XElement("ItemGroup", new XAttribute("Label", "Frameworks")));
            var project = new KotlinProjectFile(Path.Combine(folder, FileName), new XDocument(root), null);
            if (!File.Exists(Path.Combine(project.GradleRoot, "gradlew.bat")))
                throw new FileNotFoundException("The selected Gradle root must contain gradlew.bat.");
            root.Elements("ItemGroup").First().Add(new XElement("Module", new XAttribute("Include", ":"),
                new XElement("Path", project.Get("GradleRoot")), new XElement("Origin", "Generated placeholder")));
            if (!string.IsNullOrWhiteSpace(defaults.GradleJavaHome)) project.SetJdk(defaults.GradleJavaHome);
            project.RefreshTools();
            return project;
        }

        public static KotlinProjectFile Load(string path)
        {
            var text = File.ReadAllText(path);
            XDocument document;
            using (var input = new StringReader(text))
            using (var reader = XmlReader.Create(input, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 }))
                document = XDocument.Load(reader);
            if (document.Root?.Name != "Project" || (string?)document.Root.Attribute("VistudioSchemaVersion") != "1")
                throw new InvalidDataException("Unsupported .kotlinproj format; expected VistudioSchemaVersion=1.");
            var groups = document.Root.Elements("PropertyGroup").Where(e => (string?)e.Attribute("Label") == "Vistudio").ToArray();
            if (groups.Length != 1 || groups[0].Elements().GroupBy(e => e.Name).Any(g => g.Count() != 1)
                || groups[0].Attribute("Condition") != null || groups[0].Elements().Any(e => e.Attribute("Condition") != null))
                throw new InvalidDataException("Use one unconditional Vistudio PropertyGroup with unique properties.");
            return new KotlinProjectFile(path, document, text);
        }

        public void SetJdk(string path)
        {
            path = path.Trim();
            var jdk = path.Length == 0 ? null : JdkInstallation.Read(ResolvePath(path));
            Properties.SetElementValue("JdkHome", path.Length == 0 ? "" : jdk!.Home);
            Properties.SetElementValue("JdkVersion", jdk?.Version ?? "");
        }

        public IEnumerable<XElement> Items(string name) => document.Root!.Elements("ItemGroup").Elements(name)
            .Select(e => new XElement(e));

        /// <summary>Items outside the generated (GradleImport) and detected (DetectedTools) groups.</summary>
        public IEnumerable<XElement> ManualItems() => document.Root!.Elements("ItemGroup")
            .Where(e => (string?)e.Attribute("Label") != "GradleImport" && (string?)e.Attribute("Label") != "DetectedTools")
            .Elements().Select(e => new XElement(e));

        /// <summary>Items of one labeled group only, for example the generated GradleImport snapshot.</summary>
        public IEnumerable<XElement> Items(string name, string groupLabel) => document.Root!.Elements("ItemGroup")
            .Where(e => (string?)e.Attribute("Label") == groupLabel).Elements(name).Select(e => new XElement(e));

        /// <summary>The default vistudio.kotlinproj, otherwise the only *.kotlinproj in the folder; null when none exists.</summary>
        public static string? Find(string folder)
        {
            var preferred = Path.Combine(folder, FileName);
            if (File.Exists(preferred)) return Path.GetFullPath(preferred);
            if (!Directory.Exists(folder)) return null;
            var candidates = Directory.GetFiles(folder, "*.kotlinproj").Where(p => p.EndsWith(".kotlinproj", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length > 1) throw new InvalidDataException("Several .kotlinproj files exist in " + folder + "; keep one or name it " + FileName + ".");
            return candidates.Length == 1 ? Path.GetFullPath(candidates[0]) : null;
        }

        public string ImportedAtUtc => Imported("ImportedAtUtc");

        /// <summary>A value of the generated GradleImport property group (ImportedAtUtc, GradleVersion, JavaVersion, JavaHome...).</summary>
        public string Imported(string name) => (string?)document.Root!.Elements("PropertyGroup")
            .FirstOrDefault(e => (string?)e.Attribute("Label") == "GradleImport")?.Element(name) ?? "";

        /// <summary>Vistudio properties that the IDE edits through <see cref="Set"/>; the JDK uses <see cref="SetJdk"/>.</summary>
        public static readonly string[] EditableProperties =
        {
            "GradleRoot", "BuildTasks", "TestTasks", "RunTasks", "ImportConfigurations",
            "DebugMainClass", "DebugSourceFile", "DebugArguments", "DebugWorkingDirectory", "StartupTarget",
        };

        /// <summary>JVM, Android, JS, Wasm, Native, Multiplatform or Unknown, from the last import; empty before the first import.</summary>
        public string ProjectKind => Imported("ProjectKind");

        /// <summary>Validates and stores one editable property. Invalid values throw <see cref="ArgumentException"/> and change nothing.</summary>
        public void Set(string name, string value)
        {
            value = (value ?? "").Trim();
            switch (name)
            {
                case "BuildTasks": case "TestTasks": case "RunTasks":
                    value = string.Join(" ", GradleCommand.ParseTasks(value));
                    break;
                case "GradleRoot":
                    if (value.Length == 0) value = ".";
                    if (!File.Exists(Path.Combine(ResolvePath(value), "gradlew.bat")))
                        throw new ArgumentException("The Gradle root must contain gradlew.bat: " + ResolvePath(value));
                    break;
                case "ImportConfigurations":
                    var names = value.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (names.Any(n => !System.Text.RegularExpressions.Regex.IsMatch(n, @"\A[A-Za-z_][A-Za-z0-9_.-]*\z")))
                        throw new ArgumentException("Enter Gradle configuration names separated by spaces, for example integrationTestRuntimeClasspath.");
                    value = string.Join(" ", names);
                    break;
                case "DebugMainClass":
                    if (value.Length != 0 && !System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[A-Za-z_$][\w$]*(\.[A-Za-z_$][\w$]*)*\z"))
                        throw new ArgumentException("Enter a JVM class name, for example sample.MainKt.");
                    break;
                case "DebugSourceFile":
                    if (value.Length != 0 && !File.Exists(ResolvePath(value))) throw new ArgumentException("The debug source file does not exist: " + ResolvePath(value));
                    break;
                case "DebugArguments":
                    try { KotlinLaunchTarget.SplitArguments(value); }
                    catch (InvalidDataException e) { throw new ArgumentException(e.Message); }
                    break;
                case "DebugWorkingDirectory":
                    if (value.Length != 0 && !Directory.Exists(ResolvePath(value))) throw new ArgumentException("The working directory does not exist: " + ResolvePath(value));
                    break;
                case "StartupTarget":
                    if (value.Length != 0)
                    {
                        var targets = KotlinTarget.All(this);
                        value = (KotlinTarget.Find(targets, value) ?? throw new ArgumentException(targets.Count == 0
                            ? "The project has no imported Kotlin Multiplatform targets. Import the Gradle model first."
                            : "Unknown or ambiguous Kotlin target " + value + ". Choose one of: " + string.Join(", ", targets.Select(t => t.Id)) + ".")).Id;
                    }
                    break;
                default:
                    throw new ArgumentException("Unknown or read-only project property: " + name);
            }
            Properties.SetElementValue(name, value);
        }

        public void ApplyImport(GradleProjectModel model)
        {
            if (!string.Equals(model.RootDirectory.TrimEnd('\\', '/'), GradleRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The imported model belongs to another Gradle root.");
            var root = document.Root!;
            root.Elements().Where(e => (e.Name == "PropertyGroup" || e.Name == "ItemGroup") &&
                (string?)e.Attribute("Label") == "GradleImport").Remove();
            // Migrate only the exact original root placeholder; manually enriched modules remain intact.
            root.Elements("ItemGroup").Where(e => (string?)e.Attribute("Label") == "Modules").Elements("Module")
                .Where(e => (string?)e.Attribute("Include") == ":" && (string?)e.Element("Path") == Get("GradleRoot") &&
                    ((string?)e.Element("Origin") == "Generated placeholder" || e.Elements().All(p => p.Name == "Path"))).Remove();
            root.Add(new XElement("PropertyGroup", new XAttribute("Label", "GradleImport"),
                new XElement("ImportedAtUtc", DateTime.UtcNow.ToString("O")), new XElement("RootDirectory", model.RootDirectory),
                new XElement("GradleVersion", model.GradleVersion), new XElement("JavaVersion", model.JavaVersion),
                new XElement("JavaHome", model.JavaHome), new XElement("Scope", "Compile/runtime and Kotlin compilation configurations; explicit ImportConfigurations; recognized applied plugin classpaths"),
                new XElement("ProjectKind", KotlinTarget.ProjectKind(model.Items("Target").Select(KotlinTarget.FromItem), model.Items("Framework"))),
                new XElement("Targets", string.Join(" ", KotlinTarget.Sort(model.Items("Target").Select(KotlinTarget.FromItem)).Select(t => t.Id)))));
            root.Add(new XElement("ItemGroup", new XAttribute("Label", "GradleImport"),
                GradleProjectModel.ItemNames.SelectMany(model.Items)));
        }

        public void RefreshTools()
        {
            // Replace only our own snapshot, preserving manually maintained item groups and metadata.
            document.Root!.Elements("ItemGroup").Where(e => (string?)e.Attribute("Label") == "DetectedTools").Remove();
            var tools = new XElement("ItemGroup", new XAttribute("Label", "DetectedTools"));
            var wrapper = Path.Combine(GradleRoot, "gradle", "wrapper", "gradle-wrapper.properties");
            if (File.Exists(wrapper))
            {
                var distribution = File.ReadLines(wrapper).Select(l => l.Trim())
                    .FirstOrDefault(l => l.StartsWith("distributionUrl=", StringComparison.Ordinal));
                var url = distribution?.Substring("distributionUrl=".Length).Replace("\\:", ":") ?? "";
                var match = System.Text.RegularExpressions.Regex.Match(url, @"/gradle-(?<version>[0-9][A-Za-z0-9.+-]*?)-(?:bin|all)\.zip(?:\?.*)?$");
                tools.Add(new XElement("Tool", new XAttribute("Include", "Gradle Wrapper"),
                    new XElement("Version", match.Success ? match.Groups["version"].Value : "Unknown"),
                    new XElement("Origin", "Wrapper declaration"), new XElement("Path", wrapper),
                    new XElement("DistributionUrl", url)));
            }
            if (JdkHome.Length != 0)
            {
                var jdk = JdkInstallation.Read(ResolvePath(JdkHome));
                Properties.SetElementValue("JdkVersion", jdk.Version);
                tools.Add(new XElement("Tool", new XAttribute("Include", "Gradle JDK"),
                    new XElement("Version", jdk.Version), new XElement("Vendor", jdk.Vendor),
                    new XElement("Path", jdk.Home), new XElement("Origin", "Local release file")));
            }
            document.Root.Add(tools);
        }

        public void ApplyTo(KotlinSettings settings)
        {
            settings.GradleRoot = GradleRoot;
            settings.BuildTasks = Get("BuildTasks", settings.BuildTasks);
            settings.TestTasks = Get("TestTasks", settings.TestTasks);
            settings.RunTasks = Get("RunTasks", settings.RunTasks);
            // The startup target's run task replaces RunTasks; a stale StartupTarget is reported by F5, never by import or build.
            var startup = KotlinTarget.Find(KotlinTarget.All(this), Get("StartupTarget"));
            if (startup != null && startup.RunTasks.Count != 0) settings.RunTasks = string.Join(" ", startup.RunTasks);
            if (JdkHome.Length != 0)
                settings.GradleJavaHome = JdkInstallation.Read(ResolvePath(JdkHome)).Home;
        }

        public void Save(CancellationToken token = default)
        {
            GradleCommand.ParseTasks(Get("BuildTasks", "build"));
            GradleCommand.ParseTasks(Get("TestTasks", "test"));
            GradleCommand.ParseTasks(Get("RunTasks", "run"));
            if (savedText != null && (!File.Exists(FilePath) || File.ReadAllText(FilePath) != savedText))
                throw new IOException("The project file changed on disk. Refresh before saving.");
            var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                document.Save(temp);
                var newText = File.ReadAllText(temp);
                token.ThrowIfCancellationRequested();
                if (savedText == null) File.Move(temp, FilePath); // Never overwrite an existing project on Generate.
                else File.Replace(temp, FilePath, null);
                savedText = newText;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
