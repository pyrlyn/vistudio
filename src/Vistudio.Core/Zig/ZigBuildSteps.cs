using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Vistudio.Core.Zig
{
    public sealed class ZigBuildStep
    {
        public ZigBuildStep(string name, string description, bool isDefault)
        { Name = name; Description = description; IsDefault = isDefault; }
        public string Name { get; }
        public string Description { get; }
        public bool IsDefault { get; }
        public override string ToString() => Name + (IsDefault ? " (default)" : "");
    }

    public sealed class ZigBuildArtifact
    {
        public ZigBuildArtifact(string kind, string name) { Kind = kind; Name = name; }
        /// <summary><c>Executable</c>, <c>Library</c>, <c>Test</c>, <c>Object</c>, ...</summary>
        public string Kind { get; }
        public string Name { get; }
    }

    /// <summary>Build steps of a build.zig: from <c>zig build -l</c>, with a static scan of the script as fallback.</summary>
    public static class ZigBuildSteps
    {
        private static readonly Regex Listed = new Regex(@"^ {2}(?<name>[^\s(]+)(?<default> \(default\))?(?:\s{2,}(?<description>.*))?$", RegexOptions.CultureInvariant);
        private static readonly Regex Step = new Regex(@"\.step\(\s*""(?<name>(?:[^""\\]|\\.)+)""\s*,\s*""(?<description>(?:[^""\\]|\\.)*)""", RegexOptions.CultureInvariant);
        private static readonly Regex Artifact = new Regex(@"\.add(?<kind>Executable|Library|StaticLibrary|SharedLibrary|Test|Object)\(\s*\.\{(?<body>(?:[^{}]|\{[^{}]*\})*?)\.name\s*=\s*""(?<name>[^""]+)""", RegexOptions.CultureInvariant | RegexOptions.Singleline);

        /// <summary>Parses <c>zig build -l</c> output: two-space indented step name, optional "(default)", description.</summary>
        public static IReadOnlyList<ZigBuildStep> ParseList(string output)
        {
            var steps = new List<ZigBuildStep>();
            foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
            {
                var match = Listed.Match(raw.TrimEnd());
                if (!match.Success) continue;
                steps.Add(new ZigBuildStep(match.Groups["name"].Value, match.Groups["description"].Value.Trim(), match.Groups["default"].Success));
            }
            return steps;
        }

        /// <summary>
        /// Static scan of build.zig: Zig's built-in install/uninstall steps plus every <c>.step("name", "description")</c>.
        /// Used while <c>zig build -l</c> is unavailable (no zig.exe, or build.zig does not compile yet).
        /// </summary>
        public static IReadOnlyList<ZigBuildStep> Scan(string buildScript)
        {
            var text = StripComments(buildScript);
            var steps = new List<ZigBuildStep>
            {
                new ZigBuildStep("install", "Copy build artifacts to prefix path", true),
                new ZigBuildStep("uninstall", "Remove build artifacts from prefix path", false),
            };
            foreach (Match match in Step.Matches(text))
            {
                var name = match.Groups["name"].Value;
                if (steps.Any(s => s.Name == name)) continue;
                steps.Add(new ZigBuildStep(name, match.Groups["description"].Value, false));
            }
            return steps;
        }

        /// <summary>Artifacts declared with <c>b.addExecutable(.{ .name = "..." })</c> and friends.</summary>
        public static IReadOnlyList<ZigBuildArtifact> ScanArtifacts(string buildScript)
        {
            var result = new List<ZigBuildArtifact>();
            foreach (Match match in Artifact.Matches(StripComments(buildScript)))
                result.Add(new ZigBuildArtifact(match.Groups["kind"].Value, match.Groups["name"].Value));
            return result;
        }

        // Keeps string literals (which may contain "//") and blanks line comments.
        private static string StripComments(string text) =>
            Regex.Replace(text, @"""(?:[^""\\\n]|\\.)*""|//[^\n]*", m => m.Value[0] == '"' ? m.Value : "");
    }
}
