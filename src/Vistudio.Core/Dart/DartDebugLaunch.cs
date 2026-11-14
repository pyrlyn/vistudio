using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Vistudio.Core.Dart
{
    /// <summary>What a Dart debug session starts: a program, the tests of a file (optionally one by name) or Flutter integration tests.</summary>
    public enum DartLaunchKind { Program, Test, IntegrationTest }

    /// <summary>
    /// DAP <c>launch</c> arguments for the SDK debug adapters (<c>dart debug_adapter</c>, <c>flutter debug_adapter</c>,
    /// each with <c>--test</c> for tests). The <c>vistudio</c> object is read by Vistudio's adapter launcher to start the
    /// right adapter and is ignored by the adapter itself.
    /// </summary>
    public static class DartDebugLaunch
    {
        public const string AdapterKey = "vistudio";

        public static JObject Create(DartProject project, string program, DartSettings settings, DartLaunchKind kind, bool noDebug, string? testName = null)
        {
            var test = kind != DartLaunchKind.Program;
            if (kind == DartLaunchKind.IntegrationTest && !project.IsFlutter)
                throw new InvalidOperationException("Integration tests on a device need a Flutter package.");
            var launch = new JObject
            {
                ["type"] = project.IsFlutter ? "flutter" : "dart",
                ["request"] = "launch",
                ["name"] = (project.IsFlutter ? "Flutter: " : "Dart: ") + (testName ?? project.Relative(program)),
                ["program"] = Path.GetFullPath(program),
                ["cwd"] = project.Root,
                ["noDebug"] = noDebug,
                ["console"] = "debugConsole",
                ["debugSdkLibraries"] = settings.DebugLibraries,
                ["debugExternalPackageLibraries"] = settings.DebugLibraries,
                ["sendLogsToClient"] = false,
            };
            var args = new JArray();
            var toolArgs = new JArray();
            if (!test) foreach (var argument in DartArguments.Split(settings.ProgramArguments)) args.Add(argument);
            if (test && !string.IsNullOrEmpty(testName)) { args.Add("--plain-name"); args.Add(testName); }
            if (test) foreach (var argument in DartArguments.Split(settings.TestArguments)) args.Add(argument);
            if (project.IsFlutter && (kind == DartLaunchKind.Program || kind == DartLaunchKind.IntegrationTest))
            {
                toolArgs.Add("-d");
                toolArgs.Add(DartArguments.ValidateDevice(settings.FlutterDevice.Length == 0 ? "windows" : settings.FlutterDevice));
                if (kind == DartLaunchKind.Program) foreach (var argument in DartArguments.Split(settings.FlutterRunArguments)) toolArgs.Add(argument);
            }
            if (args.Count > 0) launch["args"] = args;
            if (toolArgs.Count > 0) launch["toolArgs"] = toolArgs;
            launch[AdapterKey] = new JObject { ["flutter"] = project.IsFlutter, ["test"] = test, ["cwd"] = project.Root };
            return launch;
        }

        /// <summary>Applies launch.vs.json overrides (args, toolArgs, env, cwd, noDebug) to generated launch arguments.</summary>
        public static JObject Merge(JObject launch, JObject? configuration, string workspace)
        {
            if (configuration == null) return launch;
            foreach (var key in new[] { "args", "toolArgs", "env", "noDebug", "vmAdditionalArgs", "flutterMode" })
                if (configuration[key] != null && configuration[key]!.Type != JTokenType.Null) launch[key] = configuration[key]!.DeepClone();
            if (configuration["cwd"]?.Type == JTokenType.String)
                launch["cwd"] = Path.GetFullPath(Path.Combine(workspace, (string)configuration["cwd"]!));
            if (configuration["deviceId"]?.Type == JTokenType.String)
            {
                var device = DartArguments.ValidateDevice((string)configuration["deviceId"]!);
                var tools = launch["toolArgs"] as JArray ?? new JArray();
                var index = tools.Select(t => (string?)t).ToList().IndexOf("-d");
                if (index >= 0 && index + 1 < tools.Count) tools[index + 1] = device;
                else { tools.Insert(0, device); tools.Insert(0, "-d"); }
                launch["toolArgs"] = tools;
            }
            return launch;
        }

        /// <summary>Which adapter the launcher starts for these launch arguments.</summary>
        public static (bool Flutter, bool Test, string Directory) Adapter(JObject launch)
        {
            var info = launch[AdapterKey] as JObject ?? throw new InvalidOperationException("Missing Dart adapter information. Start Dart debugging with F5 or the Dart commands.");
            var directory = (string?)info["cwd"] ?? (string?)launch["cwd"] ?? Path.GetTempPath();
            return ((bool?)info["flutter"] == true, (bool?)info["test"] == true, directory);
        }
    }
}
