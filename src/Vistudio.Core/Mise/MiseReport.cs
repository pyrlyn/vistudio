using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Vistudio.Core.Mise
{
    public enum MiseToolState
    {
        UpToDate,
        UpdateAvailable,
        Missing,
    }

    /// <summary>One configured tool as mise reports it.</summary>
    public sealed class MiseToolStatus
    {
        public string Name { get; set; } = "";
        /// <summary>Version as requested in the config (<c>22</c>, <c>latest</c>, <c>1.13.2</c>).</summary>
        public string Requested { get; set; } = "";
        /// <summary>Version the config resolves to (installed or to be installed); several for <c>["20", "22"]</c>.</summary>
        public string Version { get; set; } = "";
        public bool Installed { get; set; }
        /// <summary>Newest version inside the requested range when newer than the installed one (<c>mise upgrade</c>).</summary>
        public string? LatestInRange { get; set; }
        /// <summary>Newest version overall when newer than the installed one and the in-range one (<c>mise upgrade --bump</c>).</summary>
        public string? Latest { get; set; }
        /// <summary>The requested value <c>--bump</c> writes to the config (<c>24</c> for a requested <c>22</c>).</summary>
        public string? Bump { get; set; }
        public string? SourcePath { get; set; }
        public string? ReleaseUrl { get; set; }

        public MiseToolState State => !Installed ? MiseToolState.Missing
            : LatestInRange != null || Latest != null ? MiseToolState.UpdateAvailable : MiseToolState.UpToDate;

        /// <summary>The end-of-line hint for this tool's line.</summary>
        public string Hint()
        {
            switch (State)
            {
                case MiseToolState.Missing:
                    return "not installed" + (Version.Length > 0 ? " (" + Version + ")" : "") + (Latest != null ? " · latest " + Latest : "");
                case MiseToolState.UpdateAvailable:
                    var text = LatestInRange != null ? Version + " → " + LatestInRange : Version;
                    return text + (Latest != null ? " · latest " + Latest : "");
                default:
                    return Version;
            }
        }

        public string Tooltip()
        {
            var lines = new List<string> { Name + " (mise)", "Requested: " + Requested, Installed ? "Installed: " + Version : "Not installed; mise install gets " + Version };
            if (LatestInRange != null) lines.Add("Update in range: " + LatestInRange + " (Upgrade all / mise upgrade)");
            if (Latest != null) lines.Add("Latest: " + Latest + (Bump != null ? ", writes \"" + Bump + "\" to the config" : "") + " (Upgrade all to latest / mise upgrade --bump)");
            if (State == MiseToolState.UpToDate) lines.Add("Up to date.");
            if (SourcePath != null) lines.Add("Config: " + MiseConfigFiles.Normalize(SourcePath));
            if (ReleaseUrl != null) lines.Add("Release: " + ReleaseUrl);
            return string.Join("\n", lines);
        }
    }

    /// <summary>Tools of one folder merged from <c>mise ls --current --json</c>, <c>mise outdated --json</c> and <c>mise outdated --bump --json</c>.</summary>
    public sealed class MiseReport
    {
        private static readonly Regex Untrusted = new Regex(@"Config files? in (?<path>.+?) (?:are|is) not trusted", RegexOptions.CultureInvariant);

        public IReadOnlyList<MiseToolStatus> Tools { get; private set; } = Array.Empty<MiseToolStatus>();
        /// <summary>Why the report is empty or partial (mise missing, config error, update check failed).</summary>
        public string? Error { get; private set; }
        /// <summary>The config that mise refuses until <c>mise trust</c>.</summary>
        public string? UntrustedPath { get; private set; }
        /// <summary>True when the update columns are known (the outdated check ran).</summary>
        public bool UpdatesChecked { get; private set; }
        public DateTime CreatedUtc { get; private set; } = DateTime.UtcNow;

        /// <summary>The same report with another creation time (<see cref="DateTime.MinValue"/> marks it stale but still shown).</summary>
        public MiseReport WithCreated(DateTime created) =>
            new MiseReport { Tools = Tools, Error = Error, UntrustedPath = UntrustedPath, UpdatesChecked = UpdatesChecked, CreatedUtc = created };

        public static MiseReport Failed(string error, string? untrustedPath = null) => new MiseReport { Error = error, UntrustedPath = untrustedPath };

        /// <summary>Report from mise's stderr when a command failed; recognizes untrusted configs.</summary>
        public static MiseReport FromFailure(string stderr, int exitCode)
        {
            var untrusted = Untrusted.Match(stderr);
            if (untrusted.Success)
            {
                var path = untrusted.Groups["path"].Value.Trim();
                return Failed("The mise config " + path + " is not trusted. Run Trust (mise trust) to let mise read it.", path);
            }
            var message = string.Join(" ", stderr.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.Contains("Run with --verbose") && !l.Contains("ERROR Version:"))
                .Select(l => l.StartsWith("mise ERROR ", StringComparison.Ordinal) ? l.Substring(11) : l));
            return Failed(message.Length == 0 ? "mise exited with code " + exitCode + "." : message);
        }

        public static MiseReport Parse(string listJson, string? outdatedJson, string? bumpJson, string? updateError = null)
        {
            var tools = new List<MiseToolStatus>();
            if (JToken.Parse(listJson) is JObject list)
            {
                foreach (var property in list.Properties())
                {
                    var entries = property.Value as JArray;
                    if (entries == null || entries.Count == 0) continue;
                    var objects = entries.OfType<JObject>().ToList();
                    tools.Add(new MiseToolStatus
                    {
                        Name = property.Name,
                        Requested = string.Join(", ", objects.Select(o => (string?)o["requested_version"] ?? "").Where(v => v.Length > 0).Distinct()),
                        Version = string.Join(", ", objects.Select(o => (string?)o["version"] ?? "").Where(v => v.Length > 0).Distinct()),
                        Installed = objects.All(o => (bool?)o["installed"] == true),
                        SourcePath = (string?)objects[0]["source"]?["path"],
                    });
                }
            }
            var inRange = Outdated(outdatedJson);
            var overall = Outdated(bumpJson);
            foreach (var tool in tools)
            {
                var current = tool.Installed ? tool.Version : null;
                if (inRange.TryGetValue(tool.Name, out var range))
                {
                    var latest = (string?)range["latest"];
                    if (current != null && latest != null && latest != current) tool.LatestInRange = latest;
                    tool.ReleaseUrl = (string?)range["release_url"];
                }
                if (overall.TryGetValue(tool.Name, out var bump))
                {
                    var latest = (string?)bump["latest"];
                    // Shown only when it adds something: newer than what is (or will be) installed and than the in-range update.
                    if (latest != null && latest != tool.Version && latest != tool.LatestInRange) tool.Latest = latest;
                    tool.Bump = (string?)bump["bump"];
                    tool.ReleaseUrl = (string?)bump["release_url"] ?? tool.ReleaseUrl;
                }
            }
            return new MiseReport
            {
                Tools = tools,
                UpdatesChecked = outdatedJson != null,
                Error = updateError == null ? null : "Update check failed: " + updateError,
            };
        }

        private static Dictionary<string, JObject> Outdated(string? json)
        {
            var result = new Dictionary<string, JObject>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(json)) return result;
            var token = JToken.Parse(json!);
            // Object keyed by tool (current mise); older releases print an array of rows with "name".
            if (token is JObject map) foreach (var property in map.Properties()) { if (property.Value is JObject row) result[property.Name] = row; }
            else if (token is JArray rows) foreach (var row in rows.OfType<JObject>()) { var name = (string?)row["name"]; if (name != null) result[name] = row; }
            return result;
        }

        /// <summary>Tools whose winning definition is in <paramref name="file"/>.</summary>
        public IReadOnlyList<MiseToolStatus> ForFile(string file) => Tools.Where(t => MiseConfigFiles.SamePath(t.SourcePath, file)).ToList();

        /// <summary>Tools defined in any of <paramref name="files"/> (a project's own configs, not the global one).</summary>
        public IReadOnlyList<MiseToolStatus> ForFiles(IEnumerable<string> files)
        {
            var set = new HashSet<string>(files.Select(MiseConfigFiles.Normalize), StringComparer.OrdinalIgnoreCase);
            return Tools.Where(t => t.SourcePath != null && set.Contains(MiseConfigFiles.Normalize(t.SourcePath))).ToList();
        }

        /// <summary>The reported status for a tool name as written in a config (exact name first).</summary>
        public static MiseToolStatus? Match(IReadOnlyList<MiseToolStatus> tools, string written) =>
            tools.FirstOrDefault(t => string.Equals(t.Name, written, StringComparison.Ordinal))
            ?? tools.FirstOrDefault(t => MiseToolLocator.SameTool(written, t.Name));

        public static string Summary(IReadOnlyList<MiseToolStatus> tools)
        {
            var missing = tools.Count(t => t.State == MiseToolState.Missing);
            var updates = tools.Count(t => t.State == MiseToolState.UpdateAvailable);
            if (missing == 0 && updates == 0) return tools.Count == 0 ? "no tools" : "all " + Count(tools.Count, "tool") + " up to date";
            var parts = new List<string>();
            if (missing > 0) parts.Add(missing + " not installed");
            if (updates > 0) parts.Add(Count(updates, "update"));
            return string.Join(" · ", parts);
        }

        private static string Count(int count, string noun) => count + " " + noun + (count == 1 ? "" : "s");
    }
}
