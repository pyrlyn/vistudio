using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Vistudio.Core.Projects
{
    /// <summary>
    /// JVM entry point for F5 from a solution-loaded .kotlinproj. Explicit DebugMainClass / DebugSourceFile win;
    /// otherwise the single top-level <c>fun main</c> in production source roots is used. Nothing is executed here.
    /// </summary>
    public sealed class KotlinLaunchTarget
    {
        private const int MaxScannedFiles = 5000;
        private const long MaxScannedBytes = 1024 * 1024;
        private static readonly Regex TopLevelMain = new Regex(@"^(?:(?:public|internal|suspend)\s+)*fun\s+main\s*\(",RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex Package = new Regex(@"^\s*package\s+([A-Za-z_][\w.]*)", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex JvmName = new Regex(@"^\s*@file\s*:\s*JvmName\s*\(\s*""([^""]+)""\s*\)", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private KotlinLaunchTarget(string mainClass, string sourceFile, string[] arguments, string workingDirectory)
        {
            MainClass = mainClass;
            SourceFile = sourceFile;
            Arguments = arguments;
            WorkingDirectory = workingDirectory;
        }

        public string MainClass { get; }
        public string SourceFile { get; }
        public IReadOnlyList<string> Arguments { get; }
        public string WorkingDirectory { get; }

        public static KotlinLaunchTarget Resolve(KotlinProjectFile project)
        {
            var mainClass = project.Get("DebugMainClass").Trim();
            var source = project.Get("DebugSourceFile").Trim();
            var arguments = SplitArguments(project.Get("DebugArguments"));
            var cwd = project.Get("DebugWorkingDirectory").Trim();
            var workingDirectory = cwd.Length == 0 ? project.GradleRoot : project.ResolvePath(cwd);
            if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException("DebugWorkingDirectory does not exist: " + workingDirectory);
            if (source.Length != 0)
            {
                source = project.ResolvePath(source);
                if (!File.Exists(source)) throw new FileNotFoundException("DebugSourceFile does not exist.", source);
                if (mainClass.Length == 0) mainClass = EntryPoint(source) ?? throw new InvalidDataException(
                    "DebugSourceFile has no top-level fun main; also set DebugMainClass.");
                return new KotlinLaunchTarget(mainClass, source, arguments, workingDirectory);
            }
            var candidates = Candidates(project);
            if (mainClass.Length != 0) candidates = candidates.Where(c => c.MainClass == mainClass).ToList();
            if (candidates.Count == 1) return new KotlinLaunchTarget(candidates[0].MainClass, candidates[0].Source, arguments, workingDirectory);
            if (candidates.Count == 0)
                throw new InvalidDataException(mainClass.Length == 0
                    ? "No top-level fun main was found in the project's main source roots. Set DebugMainClass and DebugSourceFile in " + Path.GetFileName(project.FilePath) + "."
                    : "DebugMainClass " + mainClass + " has no matching top-level fun main; also set DebugSourceFile.");
            throw new InvalidDataException("Several entry points exist (" + string.Join(", ", candidates.Select(c => c.MainClass)) +
                "). Set DebugMainClass in " + Path.GetFileName(project.FilePath) + ".");
        }

        /// <summary>The JVM class of a file's top-level main, or null when the file declares none.</summary>
        public static string? EntryPoint(string sourceFile)
        {
            var text = File.ReadAllText(sourceFile);
            if (!TopLevelMain.IsMatch(text)) return null;
            var package = Package.Match(text);
            var jvmName = JvmName.Match(text);
            var name = jvmName.Success ? jvmName.Groups[1].Value : FacadeName(Path.GetFileNameWithoutExtension(sourceFile));
            return package.Success ? package.Groups[1].Value + "." + name : name;
        }

        /// <summary>Kotlin's file facade name: capitalized file name, non-identifier characters replaced, plus "Kt".</summary>
        public static string FacadeName(string fileName)
        {
            var builder = new StringBuilder();
            foreach (var c in fileName) builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            if (builder.Length == 0 || char.IsDigit(builder[0])) builder.Insert(0, '_');
            builder[0] = char.ToUpperInvariant(builder[0]);
            return builder + "Kt";
        }

        /// <summary>Whitespace-separated arguments; double quotes group spaces and \" escapes a quote.</summary>
        public static string[] SplitArguments(string value)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            bool quoted = false, any = false;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '\\' && i + 1 < value.Length && value[i + 1] == '"') { current.Append('"'); i++; any = true; }
                else if (c == '"') { quoted = !quoted; any = true; }
                else if (char.IsWhiteSpace(c) && !quoted) { if (any) result.Add(current.ToString()); current.Clear(); any = false; }
                else { current.Append(c); any = true; }
            }
            if (quoted) throw new InvalidDataException("DebugArguments has an unterminated quote.");
            if (any) result.Add(current.ToString());
            return result.ToArray();
        }

        private static List<(string MainClass, string Source)> Candidates(KotlinProjectFile project)
        {
            // Production roots only: the "main" source set and Kotlin targets' "*Main" sets (for example jvmMain).
            var roots = new List<string>();
            // Multiplatform: only source sets compiled into a JVM target's main compilation (jvmMain, commonMain, ...), so
            // a main in jsMain or mingwX64Main is never launched on the JVM. The startup target wins when it is a JVM target.
            var targets = KotlinTarget.All(project);
            var startup = KotlinTarget.Find(targets, project.Get("StartupTarget"));
            var jvm = startup != null && startup.IsJvm ? new[] { startup } : targets.Where(t => t.IsJvm).ToArray();
            if (jvm.Length != 0)
                roots.AddRange(project.Items("SourceRoot", "GradleImport").Where(r => (string?)r.Element("Kind") == "Sources"
                    && jvm.Any(t => t.Module == (string?)r.Element("Module") && t.MainSourceSets.Contains((string?)r.Element("SourceSet") ?? "")))
                    .Select(r => (string)r.Attribute("Include")!).Where(Directory.Exists));
            else Collect(KotlinProjectTree.Build(project));

            void Collect(ProjectTreeNode node)
            {
                if (node.Kind == ProjectTreeKind.SourceSet && !(node.Caption == "main" || node.Caption.EndsWith("Main", StringComparison.Ordinal))) return;
                if (node.Kind == ProjectTreeKind.SourceRoot)
                {
                    if (node.Details.Any(d => d.Key == "Content" && d.Value == "Sources")) roots.Add(node.Path!);
                    return;
                }
                if (node.Kind == ProjectTreeKind.Project || node.Kind == ProjectTreeKind.Module || node.Kind == ProjectTreeKind.SourceSet)
                    foreach (var child in node.Children) Collect(child);
            }
            var result = new List<(string, string)>();
            var scanned = 0;
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var file in Directory.EnumerateFiles(root, "*.kt", SearchOption.AllDirectories))
                {
                    if (++scanned > MaxScannedFiles) throw new InvalidDataException("Too many Kotlin files to detect an entry point. Set DebugMainClass and DebugSourceFile.");
                    if (new FileInfo(file).Length > MaxScannedBytes) continue;
                    var entry = EntryPoint(file);
                    if (entry != null) result.Add((entry, file));
                }
            return result;
        }
    }
}
