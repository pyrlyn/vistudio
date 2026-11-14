using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Vistudio.Core;
using Vistudio.Core.Dart;

namespace Vistudio.Dart
{
    /// <summary>
    /// Runs unit/widget tests (<c>dart test</c> / <c>flutter test</c>) and Flutter integration tests on a device
    /// (<c>flutter test integration_test -d &lt;device&gt;</c>) with the JSON reporter. Results go to the Dart pane
    /// (PASS/FAIL per test, errors with stack traces, summary) and failures to the "Dart tests" Error List entries.
    /// </summary>
    [Export, PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class DartTestService
    {
        private readonly DartEnvironment environment;
        private readonly DartErrorList errorList;
        private readonly object gate = new object();
        private CancellationTokenSource? running;

        [ImportingConstructor]
        public DartTestService(DartEnvironment environment, DartErrorList errorList)
        {
            this.environment = environment;
            this.errorList = errorList;
        }

        public bool IsBusy { get { lock (gate) return running != null; } }

        public void Cancel()
        {
            lock (gate) running?.Cancel();
        }

        /// <summary>All unit tests of the package (<c>test\</c>).</summary>
        public async Task<DartTestRun?> RunUnitTestsAsync(string? activeDocument)
        {
            var project = await environment.RequireProjectAsync(activeDocument);
            return await RunAsync(project, Array.Empty<string>(), null, integration: false);
        }

        /// <summary>The tests of one file, or one test or group by its full name.</summary>
        public async Task<DartTestRun?> RunFileAsync(string file, string? plainName)
        {
            var project = await environment.RequireProjectAsync(file);
            var integration = project.IsIntegrationTest(file);
            if (!integration && !project.IsTestFile(file) && !file.EndsWith("_test.dart", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Open a test file first (*_test.dart under test\\, or a file under integration_test\\).");
            return await RunAsync(project, new[] { file }, plainName, integration);
        }

        /// <summary>All Flutter integration tests (<c>integration_test\</c>) on the configured device.</summary>
        public async Task<DartTestRun?> RunIntegrationTestsAsync(string? activeDocument)
        {
            var project = await environment.RequireProjectAsync(activeDocument);
            if (!project.HasIntegrationTests) throw new InvalidOperationException("The package has no integration_test folder with .dart files.");
            return await RunAsync(project, Array.Empty<string>(), null, integration: true);
        }

        private async Task<DartTestRun?> RunAsync(DartProject project, IReadOnlyList<string> paths, string? plainName, bool integration)
        {
            var sdk = await environment.RequireSdkAsync();
            var settings = DartSettings.Load();
            var device = settings.FlutterDevice.Length == 0 ? "windows" : settings.FlutterDevice;
            var start = integration
                ? DartCommandFactory.IntegrationTest(sdk, project, paths, plainName, device, settings.TestArguments)
                : DartCommandFactory.Test(sdk, project, paths, plainName, settings.TestArguments);
            CancellationTokenSource cancellation;
            lock (gate)
            {
                if (running != null) throw new InvalidOperationException("Tests are already running. Use Dart: Cancel to stop them.");
                running = cancellation = new CancellationTokenSource();
            }
            try
            {
                await environment.ClearOutputAsync();
                await environment.ShowOutputAsync();
                var title = (integration ? "Integration tests on " + device : "Tests") + (paths.Count == 0 ? "" : " in " + string.Join(", ", paths.Select(project.Relative))) +
                    (string.IsNullOrEmpty(plainName) ? "" : " matching '" + plainName + "'");
                environment.Log(title);
                environment.Log("> " + start.FileName + " " + start.Arguments);
                var run = new DartTestRun(project.Root);
                var watch = Stopwatch.StartNew();
                int code;
                try
                {
                    code = await Task.Run(() => OwnedProcess.RunAsync(start, line =>
                    {
                        string? shown;
                        lock (run) shown = run.Accept(line);
                        if (shown != null) environment.Log(shown);
                    }, cancellation.Token));
                }
                catch (OperationCanceledException)
                {
                    environment.Log("Tests canceled.");
                    return null;
                }
                IReadOnlyList<DartDiagnostic> diagnostics;
                string summary;
                lock (run) { diagnostics = run.Diagnostics(); summary = run.Summary(); }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                errorList.Publish(DartErrorSource.Test, diagnostics);
                environment.Log(summary + " in " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s (exit code " + code + ").");
                if (run.Tests.Count == 0 && code != 0) environment.Log("No test results: see the output above (compile errors are also in the Error List).");
                return run;
            }
            finally
            {
                lock (gate) running = null;
                cancellation.Dispose();
            }
        }
    }
}
