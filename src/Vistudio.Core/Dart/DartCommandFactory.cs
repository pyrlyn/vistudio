using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Vistudio.Core.Dart
{
    /// <summary>
    /// Process start information for every dart.exe / flutter.bat invocation the extension makes. dart.exe runs
    /// directly; flutter.bat runs through cmd.exe with each argument quoted (<see cref="DartArguments.BatchCommandLine"/>).
    /// </summary>
    public static class DartCommandFactory
    {
        public const string ClientId = "vistudio";
        public const string ClientVersion = "0.1.0";

        public static ProcessStartInfo Dart(DartSdk sdk, IEnumerable<string> arguments, string directory)
        {
            var start = ProcessCommands.CreateRedirected(sdk.Dart, DartArguments.Join(arguments), directory);
            Prepare(start, sdk);
            return start;
        }

        public static ProcessStartInfo Flutter(DartSdk sdk, IEnumerable<string> arguments, string directory)
        {
            var batch = sdk.RequireFlutter();
            var start = ProcessCommands.CreateRedirected(Path.Combine(Environment.SystemDirectory, "cmd.exe"), DartArguments.BatchCommandLine(batch, arguments), directory);
            Prepare(start, sdk);
            return start;
        }

        /// <summary>The tool a package's run/test/debug commands go through: flutter.bat for Flutter packages, dart.exe otherwise.</summary>
        public static ProcessStartInfo Tool(DartSdk sdk, DartProject project, IEnumerable<string> arguments) =>
            project.IsFlutter ? Flutter(sdk, arguments, project.Root) : Dart(sdk, arguments, project.Root);

        /// <summary>The Dart analysis server over stdio (LSP): completion, navigation, diagnostics, references.</summary>
        public static ProcessStartInfo LanguageServer(DartSdk sdk, string directory, string extraArguments)
        {
            var arguments = new List<string> { "language-server", "--protocol=lsp", "--client-id=" + ClientId, "--client-version=" + ClientVersion };
            arguments.AddRange(DartArguments.Split(extraArguments));
            return Dart(sdk, arguments, directory);
        }

        /// <summary><c>dart run &lt;file&gt; [args]</c> for console programs.</summary>
        public static ProcessStartInfo Run(DartSdk sdk, DartProject project, string file, string programArguments)
        {
            var arguments = new List<string> { "run", project.Relative(file) };
            arguments.AddRange(DartArguments.Split(programArguments));
            return Dart(sdk, arguments, project.Root);
        }

        /// <summary>
        /// <c>flutter run --machine</c>: the app runs on <paramref name="device"/> and stdin/stdout speak the Flutter
        /// daemon protocol, which carries hot reload (<c>app.restart</c>) and stop requests.
        /// </summary>
        public static ProcessStartInfo FlutterRun(DartSdk sdk, DartProject project, string? target, string device, string extraArguments)
        {
            var arguments = new List<string> { "run", "--machine", "-d", DartArguments.ValidateDevice(device) };
            if (!string.IsNullOrEmpty(target)) { arguments.Add("--target"); arguments.Add(project.Relative(target!)); }
            arguments.AddRange(DartArguments.Split(extraArguments));
            return Flutter(sdk, arguments, project.Root);
        }

        /// <summary>
        /// Unit/widget tests with the JSON reporter: <c>dart test --reporter json</c>, or <c>flutter test --machine</c>
        /// for Flutter packages (same event protocol). <paramref name="paths"/> may be files or folders; empty runs <c>test\</c>.
        /// </summary>
        public static ProcessStartInfo Test(DartSdk sdk, DartProject project, IEnumerable<string> paths, string? plainName, string extraArguments)
        {
            var arguments = new List<string> { "test" };
            arguments.AddRange(project.IsFlutter ? new[] { "--machine" } : new[] { "--reporter", "json" });
            AddTestFilter(arguments, plainName);
            arguments.AddRange(DartArguments.Split(extraArguments));
            arguments.AddRange(paths.Select(project.Relative));
            return Tool(sdk, project, arguments);
        }

        /// <summary>Flutter integration tests (<c>integration_test\</c>) on a device: <c>flutter test integration_test -d &lt;device&gt; --machine</c>.</summary>
        public static ProcessStartInfo IntegrationTest(DartSdk sdk, DartProject project, IEnumerable<string> paths, string? plainName, string device, string extraArguments)
        {
            if (!project.IsFlutter) throw new InvalidOperationException("Integration tests on a device need a Flutter package (pubspec.yaml depends on sdk: flutter).");
            var arguments = new List<string> { "test", "--machine", "-d", DartArguments.ValidateDevice(device) };
            AddTestFilter(arguments, plainName);
            arguments.AddRange(DartArguments.Split(extraArguments));
            var list = paths.Select(project.Relative).ToList();
            arguments.AddRange(list.Count == 0 ? new[] { "integration_test" } : list.ToArray());
            return Flutter(sdk, arguments, project.Root);
        }

        private static void AddTestFilter(List<string> arguments, string? plainName)
        {
            if (string.IsNullOrEmpty(plainName)) return;
            arguments.Add("--plain-name");
            arguments.Add(plainName!);
        }

        /// <summary><c>dart tooling-daemon --machine</c>: prints <c>{"tooling_daemon_details":{"uri":...}}</c> and serves DTD on a local port.</summary>
        public static ProcessStartInfo ToolingDaemon(DartSdk sdk, string directory) => Dart(sdk, new[] { "tooling-daemon", "--machine" }, directory);

        /// <summary>
        /// The analysis server that serves the widget previewer through DTD (the same options <c>flutter widget-preview</c>
        /// uses for its own server): completion and search are off, it only analyzes and reports previews.
        /// </summary>
        public static ProcessStartInfo PreviewAnalysisServer(DartSdk sdk, string directory) => Dart(sdk, new[]
        {
            "language-server", "--protocol=lsp", "--client-id=" + ClientId + ".widget-preview", "--client-version=" + ClientVersion,
            "--disable-server-feature-completion", "--disable-server-feature-search", "--suppress-analytics",
        }, directory);

        /// <summary>
        /// <c>flutter widget-preview start --machine --web-server --dtd-url=...</c>: the official widget previewer, served
        /// over HTTP for embedding (no browser launch), using Vistudio's DTD (the Editor service, the analysis server).
        /// </summary>
        public static ProcessStartInfo WidgetPreviewStart(DartSdk sdk, string projectRoot, string dtdUrl, bool verbose)
        {
            var arguments = new List<string> { "widget-preview", "start", "--machine", "--web-server", "--dtd-url=" + dtdUrl };
            if (verbose) arguments.Add("--verbose");
            return Flutter(sdk, arguments, projectRoot);
        }

        /// <summary><c>flutter devices --machine</c>: JSON array of connected devices.</summary>
        public static ProcessStartInfo Devices(DartSdk sdk, string directory) => Flutter(sdk, new[] { "devices", "--machine" }, directory);

        /// <summary><c>dart pub get</c> or <c>flutter pub get</c>.</summary>
        public static ProcessStartInfo PubGet(DartSdk sdk, DartProject project) => Tool(sdk, project, new[] { "pub", "get" });

        /// <summary><c>dart analyze --format=machine</c> over a package: one diagnostic per line.</summary>
        public static ProcessStartInfo Analyze(DartSdk sdk, string root) => Dart(sdk, new[] { "analyze", "--format=machine", "." }, root);

        /// <summary>The DAP server of the Dart SDK (<c>dart debug_adapter</c>) or of Flutter (<c>flutter debug_adapter</c>); <c>--test</c> debugs tests.</summary>
        public static ProcessStartInfo DebugAdapter(DartSdk sdk, bool flutter, bool test, string directory)
        {
            var arguments = new List<string> { "debug_adapter" };
            if (test) arguments.Add("--test");
            return flutter ? Flutter(sdk, arguments, directory) : Dart(sdk, arguments, directory);
        }

        /// <summary><c>flutter create</c> for a new app in <paramref name="folder"/> (created when missing).</summary>
        public static ProcessStartInfo FlutterCreate(DartSdk sdk, string folder, string? organization)
        {
            var name = Path.GetFileName(folder.TrimEnd('\\', '/')).ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') || name.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
                throw new ArgumentException("The folder name must be a valid Dart package name (letters, digits, underscores): " + folder);
            var arguments = new List<string> { "create", "--project-name", name };
            if (!string.IsNullOrWhiteSpace(organization)) { arguments.Add("--org"); arguments.Add(organization!.Trim()); }
            arguments.Add(Path.GetFullPath(folder));
            return Flutter(sdk, arguments, Path.GetDirectoryName(Path.GetFullPath(folder).TrimEnd('\\'))!);
        }

        private static void Prepare(ProcessStartInfo start, DartSdk sdk)
        {
            // Tells pub and the tools which client runs them (analytics and messages); no color codes in captured output.
            start.EnvironmentVariables["PUB_ENVIRONMENT"] = ClientId;
            start.EnvironmentVariables["NO_COLOR"] = "1";
            start.EnvironmentVariables.Remove("CLICOLOR_FORCE");
            if (sdk.FlutterRoot != null) start.EnvironmentVariables["FLUTTER_ROOT"] = sdk.FlutterRoot;
        }
    }
}
