using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Vistudio.Core.Dart;
using Vistudio.Core.Packages;
using Vistudio.Core.Zig;

namespace Vistudio.Core.Languages
{
    /// <summary>
    /// The project file of a language project (<c>.rustproj</c>, <c>.zigproj</c>, <c>.dartproj</c>, <c>.cproj</c>,
    /// <c>.cppproj</c>, <c>.wasmproj</c>, <c>.javaproj</c>). Like <c>.kotlinproj</c> it is IDE metadata in the familiar
    /// project XML layout, never evaluated by MSBuild: the language's own tool builds, and the file only records which
    /// commands Visual Studio runs for Build, Run, Test, Clean and F5. The project folder is the file's folder.
    /// </summary>
    public sealed class LanguageProjectFile
    {
        public const int SchemaVersion = 1;
        private static readonly XmlReaderSettings ReaderSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

        /// <summary>Settings shown in the Properties window, in display order.</summary>
        public static readonly string[] EditableProperties =
        {
            "BuildCommand", "RunCommand", "TestCommand", "CleanCommand", "DebugProgram", "DebugArguments", "WorkingDirectory", "ToolFolders", "PackageTool",
            "BuildSystem", "BuildCache", "RemoteCache",
        };

        /// <summary>Settings a build system switch replaces when they still hold the previous system's defaults.</summary>
        private static readonly string[] BuildSystemDefaults = { "BuildCommand", "RunCommand", "TestCommand", "CleanCommand", "DebugProgram" };

        private readonly XDocument document;

        private LanguageProjectFile(string path, XDocument document, LanguageInfo language)
        {
            FilePath = path;
            this.document = document;
            Language = language;
        }

        public string FilePath { get; }
        public string DirectoryPath => Path.GetDirectoryName(FilePath)!;
        public LanguageInfo Language { get; }
        public string Name => Get("ProjectName", Path.GetFileNameWithoutExtension(FilePath));
        public string ProjectGuid => Get("ProjectGuid");
        private XElement Properties => document.Root!.Elements("PropertyGroup").First(g => (string?)g.Attribute("Label") == "Vistudio");

        public string Get(string name, string fallback = "")
        {
            var value = (string?)Properties.Element(name);
            return string.IsNullOrEmpty(value) ? fallback : value!;
        }

        /// <summary>Folders searched for the commands' programs before PATH; relative entries are under the project folder.</summary>
        public IReadOnlyList<string> ToolFolders =>
            Get("ToolFolders").Split(';').Select(f => f.Trim()).Where(f => f.Length != 0).Select(ResolvePath).ToList();

        public string WorkingDirectory => ResolvePath(Get("WorkingDirectory", "."));

        public string ResolvePath(string path) =>
            Path.GetFullPath(Path.Combine(DirectoryPath, Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))));

        public static LanguageProjectFile Load(string path)
        {
            path = Path.GetFullPath(path);
            var language = LanguageCatalog.ForProjectFile(path) ?? throw new InvalidDataException("Not a language project file: " + path);
            if (language == LanguageCatalog.Kotlin) throw new InvalidDataException(".kotlinproj files are Kotlin projects.");
            XDocument document;
            using (var reader = XmlReader.Create(path, ReaderSettings)) document = XDocument.Load(reader);
            Validate(document, language, path);
            return new LanguageProjectFile(path, document, language);
        }

        private static void Validate(XDocument document, LanguageInfo language, string path)
        {
            var root = document.Root;
            if (root == null || root.Name.LocalName != "Project" || root.Name.Namespace != XNamespace.None)
                throw new InvalidDataException(path + ": the root element must be <Project>.");
            if ((string?)root.Attribute("VistudioSchemaVersion") != SchemaVersion.ToString())
                throw new InvalidDataException(path + ": unsupported VistudioSchemaVersion (expected " + SchemaVersion + ").");
            var groups = root.Elements("PropertyGroup").Where(g => (string?)g.Attribute("Label") == "Vistudio").ToList();
            if (groups.Count != 1) throw new InvalidDataException(path + ": exactly one <PropertyGroup Label=\"Vistudio\"> is required.");
            if (groups[0].Attribute("Condition") != null) throw new InvalidDataException(path + ": the Vistudio property group cannot be conditional.");
            var duplicate = groups[0].Elements().GroupBy(e => e.Name.LocalName).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null) throw new InvalidDataException(path + ": " + duplicate.Key + " is set twice.");
            var declared = (string?)groups[0].Element("Language");
            if (declared != null && !string.Equals(declared, language.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(path + ": Language '" + declared + "' does not match the " + language.ProjectExtension + " extension.");
            foreach (var name in new[] { "BuildCommand", "RunCommand", "TestCommand", "CleanCommand" })
            {
                try { LanguageCommandLine.Parse((string?)groups[0].Element(name) ?? ""); }
                catch (ArgumentException e) { throw new InvalidDataException(path + ": " + name + ": " + e.Message); }
            }
        }

        /// <summary>Validates and stores one editable setting, then saves atomically. Throws <see cref="ArgumentException"/> for invalid values.</summary>
        public void Set(string name, string value, CancellationToken token = default)
        {
            if (!EditableProperties.Contains(name)) throw new ArgumentException(name + " cannot be edited.");
            value = (value ?? "").Trim();
            if (value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new ArgumentException(name + " cannot contain line breaks.");
            switch (name)
            {
                case "BuildCommand": case "RunCommand": case "TestCommand": case "CleanCommand":
                    LanguageCommandLine.Parse(value);
                    break;
                case "DebugArguments":
                    ZigArguments.Split(value, "Debug arguments");
                    break;
                case "DebugProgram": case "WorkingDirectory": case "PackageTool":
                    if (value.Length != 0 && value.IndexOfAny(Path.GetInvalidPathChars().Concat(new[] { '*', '?' }).ToArray()) >= 0)
                        throw new ArgumentException(name + " is not a valid path: " + value);
                    break;
                case "ToolFolders":
                    if (value.Split(';').Any(f => f.Trim().IndexOfAny(Path.GetInvalidPathChars()) >= 0))
                        throw new ArgumentException("Tool folders must be paths separated by ';'.");
                    break;
                case "BuildSystem":
                    if (!Enum.TryParse<LanguageBuildSystem>(value.Length == 0 ? "Native" : value, true, out var system) || !Enum.IsDefined(typeof(LanguageBuildSystem), system))
                        throw new ArgumentException("Build system must be Native or Bazel.");
                    value = system.ToString();
                    SwitchBuildSystem(system);
                    break;
                case "BuildCache":
                    if (value.Length == 0) value = "true";
                    if (!bool.TryParse(value, out var cache)) throw new ArgumentException("Build cache must be true or false.");
                    value = cache ? "true" : "false";
                    break;
                case "RemoteCache":
                    if (value.Length != 0 && (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                        || !new[] { "http", "https", "grpc", "grpcs" }.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase)))
                        throw new ArgumentException("Remote cache must be an http, https, grpc or grpcs URL.");
                    break;
            }
            var element = Properties.Element(name);
            if (element == null) Properties.Add(element = new XElement(name));
            element.Value = value;
            Save(token);
        }

        /// <summary>Native (the language's own tool) or Bazel; files without the setting are Native.</summary>
        public LanguageBuildSystem BuildSystem =>
            string.Equals(Get("BuildSystem"), "Bazel", StringComparison.OrdinalIgnoreCase) ? LanguageBuildSystem.Bazel : LanguageBuildSystem.Native;

        /// <summary>The tool-native build cache (<see cref="BuildCache"/>); on unless the file says false.</summary>
        public bool BuildCacheEnabled => !string.Equals(Get("BuildCache"), "false", StringComparison.OrdinalIgnoreCase);

        public string RemoteCache => Get("RemoteCache");

        /// <summary>
        /// Replaces the command settings that still hold the current build system's defaults with the new system's
        /// defaults; settings the user changed stay as they are.
        /// </summary>
        private void SwitchBuildSystem(LanguageBuildSystem system)
        {
            var current = BuildSystem;
            if (current == system) return;
            var before = LanguageProjectDefaults.For(Language, DirectoryPath, ProgramName(current), current);
            var after = LanguageProjectDefaults.For(Language, DirectoryPath, ProgramName(current), system);
            foreach (var name in BuildSystemDefaults)
            {
                var value = Get(name);
                if (value != (before.TryGetValue(name, out var old) ? old : "")) continue;
                var element = Properties.Element(name);
                if (element == null) Properties.Add(element = new XElement(name));
                element.Value = after.TryGetValue(name, out var replacement) ? replacement : "";
            }
        }

        /// <summary>The program name the defaults were written with: the debug program's file name, else the project name.</summary>
        private string ProgramName(LanguageBuildSystem system)
        {
            var program = Get("DebugProgram");
            return program.Length != 0 ? Path.GetFileNameWithoutExtension(program) : Name;
        }

        public void Save(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var temporary = FilePath + ".tmp";
            var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), NewLineChars = "\r\n" };
            using (var writer = XmlWriter.Create(temporary, settings)) document.Save(writer);
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
            else File.Move(temporary, FilePath);
        }

        /// <summary>The package manifests of this project: the language's own manager, in the project folder.</summary>
        public IReadOnlyList<PackageManifestRef> FindManifests()
        {
            var manager = PackageManagers.For(Language.Packages);
            var found = PackageManagers.Find(manager, DirectoryPath);
            return found == null ? Array.Empty<PackageManifestRef>() : new[] { found };
        }

        /// <summary>
        /// Writes a new project file for <paramref name="language"/> into <paramref name="folder"/> with the defaults of
        /// <see cref="LanguageProjectDefaults"/>; an existing file is never replaced. <paramref name="programName"/> is the
        /// built program's name when the tool derives it from the package rather than the project (Cargo crate names).
        /// </summary>
        public static LanguageProjectFile Create(string folder, LanguageInfo language, string name, string? programName = null)
        {
            if (language == LanguageCatalog.Kotlin) throw new ArgumentException("Kotlin projects use .kotlinproj and the Gradle import.");
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Invalid project name: " + name);
            var path = Path.Combine(Path.GetFullPath(folder), name + language.ProjectExtension);
            if (File.Exists(path)) throw new IOException(path + " already exists.");
            var defaults = LanguageProjectDefaults.For(language, folder, programName ?? name);
            var group = new XElement("PropertyGroup", new XAttribute("Label", "Vistudio"),
                new XElement("Language", language.Id),
                new XElement("ProjectName", name),
                new XElement("ProjectGuid", Guid.NewGuid().ToString("B")));
            foreach (var property in EditableProperties)
                group.Add(new XElement(property, defaults.TryGetValue(property, out var value) ? value : ""));
            var document = new XDocument(new XElement("Project", new XAttribute("VistudioSchemaVersion", SchemaVersion), group));
            var file = new LanguageProjectFile(path, document, language);
            file.Save();
            return file;
        }

        /// <summary>The only language project file of <paramref name="folder"/> for any language; null when none or several.</summary>
        public static string? Find(string folder)
        {
            if (!Directory.Exists(folder)) return null;
            var files = LanguageCatalog.All.Where(l => l != LanguageCatalog.Kotlin)
                .SelectMany(l => Directory.EnumerateFiles(folder, "*" + l.ProjectExtension)).ToList();
            return files.Count == 1 ? files[0] : null;
        }

        /// <summary>The commands of <paramref name="operation"/> (build, rebuild, run, test, clean) ready to start in order.</summary>
        public IReadOnlyList<ProcessStartInfo> Commands(string operation) => CommandsWithNotes(operation).Select(c => c.Start).ToList();

        /// <summary><see cref="Commands"/> with a note for each (for the output pane): why it was added, what the build cache did.</summary>
        public IReadOnlyList<(ProcessStartInfo Start, string? Note)> CommandsWithNotes(string operation)
        {
            IReadOnlyList<LanguageCommand> lines;
            if (operation == "rebuild")
            {
                var clean = CommandLines("clean");
                lines = clean.Concat(WithDependencies(CommandLines("build"), cleaned: clean.Count != 0)).ToList();
            }
            else lines = operation == "build" ? WithDependencies(CommandLines("build"), cleaned: false) : CommandLines(operation);
            return lines.Select(c =>
            {
                var start = c.ToStartInfo(this);
                var note = c.Note != null && c.CacheNote != null ? c.Note + " " + c.CacheNote : c.Note ?? c.CacheNote;
                return (start, note);
            }).ToList();
        }

        /// <summary>
        /// Dart packages built by their own tool: <c>dart pub get</c> (<c>flutter pub get</c> for Flutter packages) in the
        /// project folder before the build commands when the dependencies are unresolved
        /// (<see cref="DartProject.UnresolvedReason"/>), or after a clean step, which may delete .dart_tool. Analyze and
        /// the other build commands do not resolve by themselves. Nothing is added when the build commands already run
        /// pub get, there are none, or Bazel builds.
        /// </summary>
        private IReadOnlyList<LanguageCommand> WithDependencies(IReadOnlyList<LanguageCommand> build, bool cleaned)
        {
            if (build.Count == 0 || Language != LanguageCatalog.Dart || BuildSystem != LanguageBuildSystem.Native) return build;
            if (!File.Exists(Path.Combine(DirectoryPath, "pubspec.yaml"))) return build;
            if (build.Any(IsPubGet)) return build;
            var reason = cleaned ? "the clean step may have removed .dart_tool" : DartProject.UnresolvedReason(DirectoryPath);
            if (reason == null) return build;
            var tool = LanguageProjectDefaults.IsFlutter(DirectoryPath) ? "flutter" : "dart";
            var pubGet = new LanguageCommand(tool, new[] { "pub", "get" }, DirectoryPath, "Dependencies are not resolved (" + reason + "); running " + tool + " pub get first.");
            return new[] { pubGet }.Concat(build).ToList();

            static bool IsPubGet(LanguageCommand c)
            {
                var tool = BuildCache.Tool(c.Program);
                return (tool == "dart" || tool == "flutter") && c.Arguments.Count >= 2 && c.Arguments[0] == "pub" && (c.Arguments[1] == "get" || c.Arguments[1] == "upgrade");
            }
        }

        private IReadOnlyList<LanguageCommand> CommandLines(string operation)
        {
            string text;
            switch (operation)
            {
                case "build": text = Get("BuildCommand"); break;
                case "run": text = Get("RunCommand"); break;
                case "test": text = Get("TestCommand"); break;
                case "clean": text = Get("CleanCommand"); break;
                default: throw new ArgumentException("Unknown operation: " + operation);
            }
            return LanguageCommandLine.Parse(text);
        }

        /// <summary>The program F5 starts under the native debugger; null when the project sets none.</summary>
        public string? DebugProgram
        {
            get
            {
                var value = Get("DebugProgram");
                return value.Length == 0 ? null : ResolvePath(value);
            }
        }

        public IReadOnlyList<string> DebugArguments => ZigArguments.Split(Get("DebugArguments"), "Debug arguments");

        /// <summary>Folders searched for programs: the project folder, <see cref="ToolFolders"/>, then the language's usual install folders.</summary>
        public IEnumerable<string> SearchFolders => new[] { DirectoryPath }.Concat(ToolFolders).Concat(LanguageProjectDefaults.InstallFolders(Language))
            .Concat(BuildSystem == LanguageBuildSystem.Bazel ? LanguageProjectDefaults.BazelInstallFolders() : Enumerable.Empty<string>());

        /// <summary>The project's cache options for <see cref="BuildCache.Apply"/>.</summary>
        public BuildCacheOptions CacheOptions() => new BuildCacheOptions
        {
            Enabled = BuildCacheEnabled,
            RemoteCache = RemoteCache,
            WorkingDirectory = WorkingDirectory,
            BazelDiskCache = Bazel.BazelSettings.Load().EffectiveDiskCache,
            FindTool = name => ToolLocator.Find(new[] { name }, SearchFolders.Where(Directory.Exists)),
        };
    }

    /// <summary>One program and its arguments from a command setting.</summary>
    public sealed class LanguageCommand
    {
        /// <param name="directory">Where the command runs instead of the project's working directory.</param>
        /// <param name="note">Why the command was added, for the output pane.</param>
        public LanguageCommand(string program, IReadOnlyList<string> arguments, string? directory = null, string? note = null)
        {
            Program = program;
            Arguments = arguments;
            WorkingDirectory = directory;
            Note = note;
        }

        public string Program { get; }
        public IReadOnlyList<string> Arguments { get; }
        public string? WorkingDirectory { get; }
        public string? Note { get; }

        public override string ToString() => ZigArguments.Join(new[] { Program }.Concat(Arguments));

        /// <summary>
        /// Resolves the program (absolute; relative to the project folder when it contains a separator; else searched in
        /// <see cref="LanguageProjectFile.SearchFolders"/> and PATH) and starts it directly, or through cmd.exe with
        /// every argument quoted when it is a batch file (gradlew.bat, flutter.bat).
        /// </summary>
        public ProcessStartInfo ToStartInfo(LanguageProjectFile project)
        {
            var executable = Resolve(project) ?? throw new FileNotFoundException(
                "'" + Program + "' was not found in the project folder, its tool folders or PATH. Install it or set Tool folders in the project properties.");
            var directory = WorkingDirectory ?? project.WorkingDirectory;
            var extension = Path.GetExtension(executable);
            var cache = BuildCache.Apply(executable, Arguments, project.CacheOptions());
            CacheNote = cache.Note;
            var start = extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                ? ProcessCommands.CreateRedirected(Path.Combine(Environment.SystemDirectory, "cmd.exe"), DartArguments.BatchCommandLine(executable, cache.Arguments), directory)
                : ProcessCommands.CreateRedirected(executable, ZigArguments.Join(cache.Arguments), directory);
            foreach (var variable in cache.Environment) start.EnvironmentVariables[variable.Key] = variable.Value;
            // Tools start tools (Conan runs CMake to build packages): they find the same programs first.
            var folders = project.SearchFolders.Where(Directory.Exists).ToList();
            if (folders.Count != 0) start.EnvironmentVariables["PATH"] = string.Join(";", folders) + ";" + start.EnvironmentVariables["PATH"];
            return start;
        }

        public string? Resolve(LanguageProjectFile project)
        {
            if (Path.IsPathRooted(Program)) return File.Exists(Program) ? Program : null;
            if (Program.IndexOfAny(new[] { '\\', '/' }) >= 0)
            {
                var relative = project.ResolvePath(Program);
                return File.Exists(relative) ? relative : ToolLocator.Find(new[] { relative });
            }
            // bazelisk is the usual way to get Bazel and picks the version .bazelversion asks for.
            var names = BuildCache.Tool(Program) == "bazel" && Program.Equals("bazel", StringComparison.OrdinalIgnoreCase) ? new[] { "bazel", "bazelisk" } : new[] { Program };
            return ToolLocator.Find(names, project.SearchFolders);
        }

        /// <summary>What the build cache did for the last <see cref="ToStartInfo"/>; null when the tool has no cache to add.</summary>
        public string? CacheNote { get; private set; }
    }

    /// <summary>
    /// Command settings: arguments split like a shell (double quotes group), commands chained with a standalone
    /// <c>&amp;&amp;</c> and run in order until one fails. Nothing goes through a shell, so other shell syntax is
    /// rejected instead of being passed on literally.
    /// </summary>
    public static class LanguageCommandLine
    {
        public static IReadOnlyList<LanguageCommand> Parse(string text)
        {
            var result = new List<LanguageCommand>();
            var current = new List<string>();
            foreach (var token in ZigArguments.Split(text, "Commands"))
            {
                if (token == "&&") { Close(); continue; }
                if (token.IndexOfAny(new[] { '|', '<', '>' }) >= 0 || token == "&" || token == ";")
                    throw new ArgumentException("Commands run without a shell: pipes, redirection and '&' are not supported ('" + token + "').");
                current.Add(token);
            }
            Close();
            return result;

            void Close()
            {
                if (current.Count == 0)
                {
                    if (result.Count != 0 || text.Contains("&&")) throw new ArgumentException("'&&' needs a command on both sides.");
                    return;
                }
                result.Add(new LanguageCommand(current[0], current.Skip(1).ToList()));
                current = new List<string>();
            }
        }
    }
}
