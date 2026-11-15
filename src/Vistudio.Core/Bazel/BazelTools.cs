using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Vistudio.Core.Languages;
using Vistudio.Core.Zig;

namespace Vistudio.Core.Bazel
{
    /// <summary>
    /// Bazel settings (Tools &gt; Options &gt; Bazel), overridden per window by <c>vistudio.bazel.*</c> keys in the workspace's
    /// <c>.vs\settings.json</c>. Bazel, buildifier and the Starlark language server are user-installed.
    /// </summary>
    public sealed class BazelSettings : StoredSettings<BazelSettings>
    {
        /// <summary>starpls.exe or bazel-lsp.exe (or the folder). Empty searches PATH for starpls, then bazel-lsp.</summary>
        public string LanguageServerPath { get; set; } = "";
        /// <summary>Extra server arguments. starpls gets its <c>server</c> subcommand automatically.</summary>
        public string LanguageServerArguments { get; set; } = "";
        /// <summary>buildifier.exe (or its folder). Empty searches PATH, then <c>%USERPROFILE%\go\bin</c>.</summary>
        public string BuildifierPath { get; set; } = "";
        /// <summary>Vistudio's own checks (syntax, load, duplicate targets, labels, missing files) on open, save and while typing.</summary>
        public bool Validation { get; set; } = true;
        public bool ValidateWhileTyping { get; set; } = true;
        /// <summary><c>buildifier --lint=warn</c> on open and save.</summary>
        public bool BuildifierLint { get; set; } = true;
        /// <summary>Reports files buildifier would reformat.</summary>
        public bool ReportFormatting { get; set; } = true;
        /// <summary>File, folder, label, repository, rule and attribute completion from the workspace on disk.</summary>
        public bool Completion { get; set; } = true;
        /// <summary>Folder for <c>--disk_cache</c> when a project's build cache is on. Empty uses <c>%LOCALAPPDATA%\Vistudio\bazel-disk-cache</c>.</summary>
        public string DiskCachePath { get; set; } = "";

        public BazelSettings() : base("bazel-settings.xml", WorkspaceSettings.BazelPrefix) { }

        public override void Validate()
        {
            ZigArguments.Split(LanguageServerArguments, "Starlark language server arguments");
            if (DiskCachePath.Trim().Length != 0 && DiskCachePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                throw new ArgumentException("The Bazel disk cache folder is not a valid path.");
        }

        public string EffectiveDiskCache => DiskCachePath.Trim().Length != 0
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(DiskCachePath.Trim().Trim('"')))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vistudio", "bazel-disk-cache");
    }

    /// <summary>Discovery and launch of a user-installed Starlark language server: starpls or bazel-lsp.</summary>
    public static class BazelLanguageServer
    {
        public static readonly string[] ExecutableNames = { "starpls", "bazel-lsp" };

        public static IEnumerable<string> InstallFolders
        {
            get
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return new[] { Path.Combine(home, "go", "bin"), Path.Combine(home, ".cargo", "bin") };
            }
        }

        public static string? Find(BazelSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.LanguageServerPath))
            {
                // A folder may hold either server.
                foreach (var name in ExecutableNames)
                {
                    var found = LanguageServerLocator.Find(settings.LanguageServerPath, name, InstallFolders);
                    if (found != null) return found;
                }
                return null;
            }
            return ExecutableNames.Select(n => LanguageServerLocator.Find("", n, InstallFolders)).FirstOrDefault(f => f != null);
        }

        /// <summary>starpls serves LSP only through its <c>server</c> subcommand; bazel-lsp serves it without arguments.</summary>
        public static string Arguments(string executable, BazelSettings settings)
        {
            var arguments = settings.LanguageServerArguments.Trim();
            var isStarpls = Path.GetFileNameWithoutExtension(executable).StartsWith("starpls", StringComparison.OrdinalIgnoreCase);
            var first = ZigArguments.Split(arguments, "Starlark language server arguments").FirstOrDefault();
            if (isStarpls && first != "server") arguments = ("server " + arguments).Trim();
            return arguments;
        }

        public static ProcessStartInfo Create(string executable, BazelSettings settings, string workingDirectory) =>
            LanguageServerLocator.Create(executable, Arguments(executable, settings), workingDirectory, "Starlark language server arguments");
    }

    /// <summary>buildifier lint: <c>--mode=check --lint=warn --format=json</c> on the document text through stdin.</summary>
    public static class Buildifier
    {
        public const string ExecutableName = "buildifier";

        public static string? Find(BazelSettings settings) =>
            LanguageServerLocator.Find(settings.BuildifierPath, ExecutableName, BazelLanguageServer.InstallFolders);

        /// <param name="path">The document's path; buildifier picks the file type (BUILD, .bzl, MODULE.bazel) from it.</param>
        public static ProcessStartInfo Create(string executable, string path, string? root)
        {
            var directory = root ?? Path.GetDirectoryName(Path.GetFullPath(path))!;
            string relative;
            try { relative = root == null ? Path.GetFileName(path) : MakeRelative(root, path); }
            catch (ArgumentException) { relative = Path.GetFileName(path); }
            var arguments = ZigArguments.Join(new[] { "--mode=check", "--lint=warn", "--format=json", "--path=" + relative });
            var start = ProcessCommands.CreateRedirected(executable, arguments, directory);
            start.RedirectStandardInput = true;
            return start;
        }

        private static string MakeRelative(string root, string path)
        {
            var full = Path.GetFullPath(path);
            var rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full.Substring(rootFull.Length).Replace('\\', '/') : Path.GetFileName(full);
        }

        private static readonly Regex SyntaxError = new Regex(@"^(?<file>.*?):(?<line>\d+):(?<column>\d+):\s*(?<message>.+)$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Lint warnings and the formatting state from buildifier's JSON report, plus syntax errors from its error output
        /// (<c>file:line:column: message</c>). Unknown output gives no diagnostics.
        /// </summary>
        public static IReadOnlyList<BazelDiagnostic> Parse(string output, string errors, bool reportFormatting)
        {
            var result = new List<BazelDiagnostic>();
            var json = output.Trim();
            if (json.StartsWith("{", StringComparison.Ordinal))
            {
                JObject report;
                try { report = JObject.Parse(json); }
                catch (Newtonsoft.Json.JsonException) { report = new JObject(); }
                foreach (var file in report["files"] as JArray ?? new JArray())
                {
                    foreach (var warning in file["warnings"] as JArray ?? new JArray())
                    {
                        var line = (int?)warning["start"]?["line"] ?? 1;
                        var column = (int?)warning["start"]?["column"] ?? 1;
                        var endLine = (int?)warning["end"]?["line"] ?? line;
                        var endColumn = (int?)warning["end"]?["column"] ?? column + 1;
                        var category = (string?)warning["category"] ?? "lint";
                        var message = ((string?)warning["message"] ?? "").Trim();
                        var url = (string?)warning["url"];
                        if (!string.IsNullOrEmpty(url)) message += " (" + url + ")";
                        var length = endLine == line ? Math.Max(1, endColumn - column) : 1;
                        result.Add(new BazelDiagnostic(line - 1, Math.Max(0, column - 1), length, BazelSeverity.Warning, category, message, "buildifier"));
                    }
                    if (reportFormatting && (bool?)file["valid"] != false && (bool?)file["formatted"] == false)
                        result.Add(new BazelDiagnostic(0, 0, 1, BazelSeverity.Suggestion, "format", "buildifier would reformat this file.", "buildifier"));
                }
            }
            foreach (var line in errors.Split('\n').Select(l => l.TrimEnd('\r')))
            {
                var match = SyntaxError.Match(line);
                if (!match.Success) continue;
                result.Add(new BazelDiagnostic(Math.Max(0, int.Parse(match.Groups["line"].Value) - 1), Math.Max(0, int.Parse(match.Groups["column"].Value) - 1), 1,
                    BazelSeverity.Error, "syntax", match.Groups["message"].Value.Trim(), "buildifier"));
            }
            return result;
        }
    }
}
