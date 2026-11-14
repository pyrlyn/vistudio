using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Vistudio.Core.CodeLens;
using Vistudio.Core.Dart;

namespace Vistudio.Core.Packages
{
    /// <summary>
    /// Gradle dependencies of <c>build.gradle.kts</c> / <c>build.gradle</c>. Gradle has no add/remove/update command, so
    /// changes are manifest edits (the IDE re-imports the model afterwards); the outdated report is one wrapper run with
    /// the <c>OutdatedDependencies.gradle</c> init script, which asks Gradle's own dynamic-version resolution for
    /// <c>latest.release</c> of each declared module.
    /// </summary>
    public sealed class GradlePackageManager : IPackageManager
    {
        public const string ReportTask = ":vistudioOutdatedReport6cc1f5c0";
        public const string BeginMarker = "VISTUDIO-OUTDATED-BEGIN";
        public const string EndMarker = "VISTUDIO-OUTDATED-END";
        internal const string RecordPrefix = "VISTUDIO-OUTDATED\t";
        internal const string ErrorPrefix = "VISTUDIO-OUTDATED-ERROR\t";
        internal const string KotlinSource = "kotlin plugin";
        internal const string CatalogSource = "version catalog";
        internal const string VariableSource = "variable";

        private readonly string? javaHome;

        /// <param name="outdatedScript">Path of <c>OutdatedDependencies.gradle</c>; defaults to <c>Build\</c> next to this assembly, where the VSIX installs it.</param>
        /// <param name="javaHome">JAVA_HOME for the wrapper (Kotlin settings' Gradle JDK); the inherited environment when empty.</param>
        public GradlePackageManager(string? outdatedScript = null, string? javaHome = null)
        {
            OutdatedScript = string.IsNullOrWhiteSpace(outdatedScript)
                ? Path.Combine(Path.GetDirectoryName(typeof(GradlePackageManager).Assembly.Location) ?? "", "Build", "OutdatedDependencies.gradle")
                : Path.GetFullPath(outdatedScript);
            this.javaHome = string.IsNullOrWhiteSpace(javaHome) ? null : javaHome;
        }

        public string OutdatedScript { get; }

        public PackageEcosystem Ecosystem => PackageEcosystem.Gradle;
        public string DisplayName => "Gradle";
        public IReadOnlyList<string> ToolNames { get; } = new[] { "gradlew" };
        public IReadOnlyList<string> ManifestNames { get; } = new[] { "build.gradle.kts", "build.gradle" };

        /// <summary>The nearest folder upwards with <c>settings.gradle(.kts)</c>, or the build file's own folder.</summary>
        public static string SettingsRoot(string buildFile) =>
            GradleScriptScanner.ProjectOf(buildFile)?.Root ?? Path.GetDirectoryName(Path.GetFullPath(buildFile))!;

        public IReadOnlyList<PackageEntry> ReadManifest(string manifestPath)
        {
            manifestPath = Path.GetFullPath(manifestPath);
            if (!File.Exists(manifestPath)) throw new PackageManagerException("Build file not found: " + manifestPath);
            return Scan(manifestPath, File.ReadAllText(manifestPath)).Declarations.Select(ToEntry).ToList();
        }

        private static GradleDependencyScanner Scan(string buildFile, string text) =>
            GradleDependencyScanner.Scan(buildFile, text, GradleVersionCatalog.Load(SettingsRoot(buildFile)));

        private static PackageEntry ToEntry(GradleDeclaration d)
        {
            switch (d.Kind)
            {
                case GradleDeclarationKind.Kotlin:
                    return new PackageEntry(d.Name, d.Requested, d.Scope, d.Configuration, d.VersionFile, d.Line, KotlinSource);
                case GradleDeclarationKind.Variable:
                    return new PackageEntry(d.Name, d.Requested, d.Scope, d.Configuration, d.VersionFile, d.Line, VariableSource);
                case GradleDeclarationKind.Catalog:
                case GradleDeclarationKind.Bundle:
                    return new PackageEntry(d.Name, d.Requested, d.Scope, d.Configuration, d.VersionFile, d.VersionLine, CatalogSource);
                default:
                    return new PackageEntry(d.Name, d.Requested, d.Scope, d.Configuration, d.VersionFile, d.Line);
            }
        }

        public IReadOnlyList<ProcessStartInfo> CreateOutdated(string tool, string manifestPath, IReadOnlyList<PackageEntry> declared)
        {
            if (declared.Count == 0) return Array.Empty<ProcessStartInfo>();
            var root = WrapperRoot(tool);
            var start = GradleCommand.CreateInitScript(root, javaHome ?? "", OutdatedScript, ReportTask);
            start.EnvironmentVariables["VISTUDIO_OUTDATED_ROOT"] = SettingsRoot(manifestPath);
            start.EnvironmentVariables["VISTUDIO_OUTDATED_BUILD_FILE"] = Path.GetFullPath(manifestPath);
            start.EnvironmentVariables["VISTUDIO_OUTDATED_OUTPUT"] = ResultFile(manifestPath);
            return new[] { start };
        }

        /// <summary>
        /// Where the init script also writes its report. One fixed file per build file under %TEMP%\Vistudio, so repeated
        /// checks overwrite instead of accumulating; <see cref="ApplyOutdated"/> reads the copy printed to stdout.
        /// </summary>
        public static string ResultFile(string manifestPath)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(manifestPath).ToUpperInvariant()));
                return Path.Combine(Path.GetTempPath(), "Vistudio", "gradle-outdated-" + BitConverter.ToString(hash, 0, 8).Replace("-", "") + ".tsv");
            }
        }

        private static string WrapperRoot(string tool)
        {
            if (string.IsNullOrWhiteSpace(tool) || !Path.IsPathRooted(tool) || !string.Equals(Path.GetFileName(tool), "gradlew.bat", StringComparison.OrdinalIgnoreCase))
                throw new PackageManagerException("The Gradle tool must be the absolute path of gradlew.bat: " + tool);
            if (!File.Exists(tool)) throw new PackageManagerException("No gradlew.bat at " + tool + ". Open the wrapper's folder or set Gradle root in Kotlin settings.");
            return Path.GetDirectoryName(Path.GetFullPath(tool))!;
        }

        public IReadOnlyList<PackageEntry> ApplyOutdated(IReadOnlyList<PackageEntry> declared, IReadOnlyList<PackageToolOutput> outputs)
        {
            if (outputs.Count == 0) return declared;
            var output = outputs[0];
            var lines = output.StandardOutput.Replace("\r\n", "\n").Split('\n');
            var begin = Array.FindLastIndex(lines, l => l.Trim() == BeginMarker);
            var end = begin < 0 ? -1 : Array.FindIndex(lines, begin + 1, l => l.Trim() == EndMarker);
            if (begin < 0 || end < 0) throw new PackageManagerException(Failure(output));
            var records = new List<OutdatedRecord>();
            var errors = new List<string>();
            for (var i = begin + 1; i < end; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.StartsWith(ErrorPrefix, StringComparison.Ordinal)) { errors.Add(line.Substring(ErrorPrefix.Length).Replace('\t', ' ').Trim()); continue; }
                if (!line.StartsWith(RecordPrefix, StringComparison.Ordinal)) continue;
                var fields = line.Split('\t');
                if (fields.Length >= 7) records.Add(new OutdatedRecord(fields[2], fields[3], fields[4], fields[5], fields[6]));
            }
            if (records.Count == 0 && errors.Count > 0) throw new PackageManagerException("Gradle could not check for updates: " + string.Join("; ", errors));
            return declared.Select(entry =>
            {
                var matches = records.Where(r => r.Name == entry.Name && r.Configuration == entry.Group).ToList();
                if (matches.Count == 0) matches = records.Where(r => r.Name == entry.Name).ToList();
                if (matches.Count == 0) return entry;
                var current = matches.Select(r => r.Current).FirstOrDefault(v => v.Length != 0);
                var latest = PackageVersion.Newest(matches.Select(r => r.Latest), includePreRelease: true);
                return entry.WithVersions(current ?? entry.Current, entry.Compatible, latest ?? entry.Latest);
            }).ToList();
        }

        private sealed class OutdatedRecord
        {
            public OutdatedRecord(string configuration, string name, string declared, string current, string latest)
            { Configuration = configuration; Name = name; Declared = declared; Current = current; Latest = latest; }
            public string Configuration { get; }
            public string Name { get; }
            public string Declared { get; }
            public string Current { get; }
            public string Latest { get; }
        }

        /// <summary>Gradle's own "What went wrong" section, or the last lines it printed.</summary>
        private static string Failure(PackageToolOutput output)
        {
            var all = (output.StandardError + "\n" + output.StandardOutput).Replace("\r\n", "\n").Split('\n');
            var wrong = Array.FindIndex(all, l => l.StartsWith("* What went wrong:", StringComparison.Ordinal));
            IEnumerable<string> message = wrong >= 0
                ? all.Skip(wrong + 1).TakeWhile(l => !l.StartsWith("* ", StringComparison.Ordinal))
                : all.Where(l => l.Trim().Length != 0).Reverse().Take(8).Reverse();
            var text = string.Join(Environment.NewLine, message.Select(l => l.TrimEnd()).Where(l => l.Length != 0));
            return "Gradle could not check for updates (exit code " + output.ExitCode + ")" + (text.Length == 0 ? "." : ":" + Environment.NewLine + text);
        }

        public PackageAction Add(string tool, string manifestPath, PackageRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Source != null) throw new PackageManagerException("Gradle takes packages from the repositories the build declares; a package source cannot be given here.");
            if (request.Version == null) throw new PackageManagerException("Gradle needs a version to add " + request.Name + ".");
            var parts = request.Name.Split(':');
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0 || parts[0].IndexOf('/') >= 0 || parts[1].IndexOf('/') >= 0)
                throw new PackageManagerException("A Gradle package is named group:artifact, for example com.google.code.gson:gson: " + request.Name);
            manifestPath = Path.GetFullPath(manifestPath);
            var text = File.ReadAllText(manifestPath);
            var scan = Scan(manifestPath, text);
            var configuration = request.Scope == PackageScope.Development ? "testImplementation" : request.Scope == PackageScope.Build ? "compileOnly" : "implementation";
            if (scan.Declarations.Any(d => d.Name == request.Name && d.Configuration == configuration))
                throw new PackageManagerException(request.Name + " is already declared in " + configuration + "; update it instead.");
            var groovy = !manifestPath.EndsWith(".kts", StringComparison.OrdinalIgnoreCase);
            var coordinate = request.Name + ":" + request.Version;
            var statement = groovy ? configuration + " '" + coordinate + "'" : configuration + "(\"" + coordinate + "\")";
            var eol = text.Contains("\r\n") ? "\r\n" : "\n";
            string edited;
            if (scan.TopLevelOpen < 0 || scan.TopLevelClose < 0)
            {
                var prefix = text.Length == 0 ? "" : text.EndsWith("\n", StringComparison.Ordinal) ? eol : eol + eol;
                edited = text + prefix + "dependencies {" + eol + "    " + statement + eol + "}" + eol;
            }
            else
            {
                var inside = scan.Declarations.Where(d => d.Start > scan.TopLevelOpen && d.Start < scan.TopLevelClose).ToList();
                var indent = inside.Count > 0 ? Indentation(text, inside[inside.Count - 1].Start) : "    ";
                var lineStart = LineStart(text, scan.TopLevelClose);
                edited = text.Substring(lineStart, scan.TopLevelClose - lineStart).Trim().Length == 0
                    ? text.Insert(lineStart, indent + statement + eol)
                    : text.Insert(scan.TopLevelClose, eol + indent + statement + eol);
            }
            return new PackageAction("Add " + statement + " to " + Path.GetFileName(manifestPath), null, new[] { new ManifestEdit(manifestPath, edited) });
        }

        public PackageAction Remove(string tool, string manifestPath, PackageEntry package)
        {
            if (package == null) throw new ArgumentNullException(nameof(package));
            if (package.Source == KotlinSource)
                throw new PackageManagerException(package.Name + " comes from the Kotlin Gradle plugin's kotlin(...) shorthand; remove that line in the build file by hand.");
            manifestPath = Path.GetFullPath(manifestPath);
            var text = File.ReadAllText(manifestPath);
            var declaration = Locate(manifestPath, text, package);
            if (declaration.Kind == GradleDeclarationKind.Bundle)
                throw new PackageManagerException(package.Name + " is part of the catalog bundle " + declaration.Accessor + "; remove it from the bundle in " + declaration.VersionFile + ".");
            var (start, end) = StatementLines(text, declaration.Start, declaration.End);
            var edited = text.Remove(start, end - start);
            return new PackageAction("Remove " + package.Name + " (" + declaration.Configuration + ") from " + Path.GetFileName(manifestPath), null,
                new[] { new ManifestEdit(manifestPath, edited) });
        }

        /// <summary>
        /// <paramref name="toLatest"/> rewrites the version to <see cref="PackageEntry.Latest"/> where it is written (the
        /// build file or the catalog). Without it nothing changes: a plain Gradle version is not a range, and a dynamic one
        /// (<c>1.+</c>, <c>[1,2)</c>) already moves on the next resolution, so the action only explains that.
        /// </summary>
        public PackageAction Update(string tool, string manifestPath, PackageEntry? package, bool toLatest)
        {
            if (package == null)
                throw new PackageManagerException("Check for updates first: Gradle has no update command, so each declared version is rewritten and the newest versions must be known.");
            Editable(package);
            if (!toLatest)
                return new PackageAction(IsDynamic(package.Requested)
                    ? package.Name + " " + package.Requested + " is a dynamic version; Gradle picks the newest match on the next import."
                    : package.Name + " " + package.Requested + " is an exact Gradle version; update to latest to change it.");
            if (package.Latest == null) throw new PackageManagerException("Check for updates first: the newest version of " + package.Name + " is not known.");
            if (PackageVersion.Same(package.Latest, package.Requested))
                return new PackageAction(package.Name + " already declares " + package.Requested + ".");
            return UpdateAll(manifestPath, new[] { package });
        }

        /// <summary>
        /// Rewrites every entry of <paramref name="outdated"/> (entries of one build file after an outdated check) to its
        /// <see cref="PackageEntry.Latest"/>, as one action with one edit per changed file. Entries without a newer latest,
        /// with a <c>kotlin(...)</c>, variable or dynamic version, or without a written version are skipped and named in the description.
        /// </summary>
        public PackageAction UpdateAll(string manifestPath, IReadOnlyList<PackageEntry> outdated)
        {
            manifestPath = Path.GetFullPath(manifestPath);
            var buildText = File.ReadAllText(manifestPath);
            var replacements = new Dictionary<string, Dictionary<int, (int Length, string Version)>>(StringComparer.OrdinalIgnoreCase);
            var changed = new List<string>();
            var skipped = new List<string>();
            foreach (var package in outdated)
            {
                // The selected version is the baseline when known: a declared 1.0 may already resolve to 1.2 through conflict resolution.
                var baseline = package.Current ?? package.Requested;
                if (package.Latest == null || (baseline.Length != 0 && !IsDynamic(baseline) && PackageVersion.Compare(package.Latest, baseline) <= 0)) continue;
                if (package.Source == KotlinSource || package.Source == VariableSource || IsDynamic(package.Requested)) { skipped.Add(package.Name); continue; }
                var latest = PackageArguments.ValidateVersion(package.Latest);
                var declaration = Locate(manifestPath, buildText, package);
                if (declaration.VersionStart < 0) { skipped.Add(package.Name); continue; }
                if (!replacements.TryGetValue(declaration.VersionFile, out var file)) replacements[declaration.VersionFile] = file = new Dictionary<int, (int, string)>();
                // Libraries sharing one [versions] entry share the span; the highest requested version wins.
                if (!file.TryGetValue(declaration.VersionStart, out var existing) || PackageVersion.Compare(latest, existing.Version) > 0)
                    file[declaration.VersionStart] = (declaration.VersionLength, latest);
                changed.Add(package.Name + " " + package.Requested + " -> " + latest);
            }
            var edits = new List<ManifestEdit>();
            foreach (var file in replacements)
            {
                var text = string.Equals(file.Key, manifestPath, StringComparison.OrdinalIgnoreCase) ? buildText : File.ReadAllText(file.Key);
                var builder = new StringBuilder(text);
                foreach (var span in file.Value.OrderByDescending(p => p.Key))
                    builder.Remove(span.Key, span.Value.Length).Insert(span.Key, span.Value.Version);
                edits.Add(new ManifestEdit(file.Key, builder.ToString()));
            }
            var description = changed.Count == 0 ? "No Gradle dependency of " + Path.GetFileName(manifestPath) + " has a newer version to write."
                : "Update " + string.Join(", ", changed);
            if (skipped.Count > 0) description += " (not changed: " + string.Join(", ", skipped.Distinct()) + ")";
            return new PackageAction(description, null, edits);
        }

        private static void Editable(PackageEntry package)
        {
            if (package.Source == KotlinSource)
                throw new PackageManagerException(package.Name + " takes its version from the Kotlin Gradle plugin; change the plugin version instead.");
            if (package.Source == VariableSource)
                throw new PackageManagerException(package.Name + " takes its version from the variable " + package.Requested + "; change that variable instead.");
        }

        private static bool IsDynamic(string version) =>
            version.IndexOf('+') >= 0 || version.IndexOfAny(new[] { '[', ']', '(', ')' }) >= 0 || version.StartsWith("latest.", StringComparison.Ordinal);

        /// <summary>Finds the declaration behind <paramref name="package"/> again by rescanning the build file.</summary>
        private static GradleDeclaration Locate(string buildFile, string text, PackageEntry package)
        {
            var candidates = Scan(buildFile, text).Declarations.Where(d => d.Name == package.Name && d.Configuration == package.Group).ToList();
            var inBuildFile = string.Equals(Path.GetFullPath(package.ManifestPath), buildFile, StringComparison.OrdinalIgnoreCase);
            var exact = candidates.FirstOrDefault(d => inBuildFile ? d.Line == package.Line
                : string.Equals(d.VersionFile, Path.GetFullPath(package.ManifestPath), StringComparison.OrdinalIgnoreCase) && d.VersionLine == package.Line);
            var found = exact ?? (candidates.Count == 1 ? candidates[0] : null);
            if (found == null)
                throw new PackageManagerException(candidates.Count == 0
                    ? package.Name + " (" + package.Group + ") is no longer declared in " + Path.GetFileName(buildFile) + "; refresh the dependencies."
                    : package.Name + " (" + package.Group + ") is declared several times in " + Path.GetFileName(buildFile) + "; refresh the dependencies.");
            return found;
        }

        /// <summary>
        /// The whole lines of a statement when nothing else shares them (a trailing <c>//</c> comment goes with it);
        /// otherwise the statement alone with one adjacent space.
        /// </summary>
        private static (int Start, int End) StatementLines(string text, int start, int end)
        {
            var lineStart = LineStart(text, start);
            var lineEnd = text.IndexOf('\n', end);
            var after = (lineEnd < 0 ? text.Substring(end) : text.Substring(end, lineEnd - end)).Trim();
            if (text.Substring(lineStart, start - lineStart).Trim().Length == 0 && (after.Length == 0 || after.StartsWith("//", StringComparison.Ordinal)))
                return (lineStart, lineEnd < 0 ? text.Length : lineEnd + 1);
            if (end < text.Length && text[end] == ' ') return (start, end + 1);
            if (start > 0 && text[start - 1] == ' ') return (start - 1, end);
            return (start, end);
        }

        private static int LineStart(string text, int offset)
        {
            var newline = offset == 0 ? -1 : text.LastIndexOf('\n', offset - 1);
            return newline + 1;
        }

        private static string Indentation(string text, int offset)
        {
            var start = LineStart(text, offset);
            var i = start;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
            return text.Substring(start, i - start);
        }
    }
}
