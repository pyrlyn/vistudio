using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Workspace;
using Microsoft.VisualStudio.Workspace.Debug;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vistudio.Core;
using Vistudio.Core.Dart;

namespace Vistudio.Dart
{
    /// <summary>
    /// Starts Dart and Flutter debug sessions in the Visual Studio debugger: programs (F5 on a .dart file, launch.vs.json
    /// targets, Dart: Debug), single tests or test files (CodeLens Debug) and Flutter integration tests. The session runs
    /// the SDK's own debug adapter through <see cref="DartAdapterLauncher"/>; <c>noDebug</c> starts without debugging.
    /// </summary>
    [Export, PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class DartDebugCoordinator
    {
        internal const string EngineId = "c4a2e6f8-1b3d-4a5c-9e7f-0d2b4c6e8a17";
        private readonly DartEnvironment environment;
        private readonly IServiceProvider services;
        private readonly SemaphoreSlim launchGate = new SemaphoreSlim(1, 1);

        [ImportingConstructor]
        public DartDebugCoordinator(DartEnvironment environment, [Import(typeof(SVsServiceProvider))] IServiceProvider services)
        {
            this.environment = environment;
            this.services = services;
            DartDebugSessions.Log = environment.Log;
        }

        /// <summary>Debugs the program of <paramref name="file"/>'s package (the file itself when it has <c>main</c>).</summary>
        internal async Task DebugProgramAsync(string? file, bool noDebug, JObject? configuration = null)
        {
            var project = await environment.RequireProjectAsync(file);
            var entry = DartRunService.EntryPoint(project, file)
                ?? throw new InvalidOperationException(project.IsFlutter ? "lib\\main.dart was not found." : "No entry point: open a .dart file with main, or add bin\\" + project.Name + ".dart.");
            await LaunchAsync(project, entry, DartLaunchKind.Program, noDebug, null, configuration);
        }

        /// <summary>Debugs the tests of one file, or one test/group by full name.</summary>
        internal async Task DebugTestAsync(string file, string? plainName)
        {
            var project = await environment.RequireProjectAsync(file);
            await LaunchAsync(project, file, project.IsIntegrationTest(file) ? DartLaunchKind.IntegrationTest : DartLaunchKind.Test, false, plainName, null);
        }

        internal Task LaunchAsync(IWorkspace workspace, DebugLaunchActionContext context, CancellationToken token)
        {
            var configuration = JObject.FromObject(context.LaunchConfiguration);
            var target = (string?)configuration["program"] ?? (string?)configuration["project"] ?? context.ProjectFileContext?.FilePath;
            if (!string.IsNullOrEmpty(target)) target = Path.GetFullPath(Path.Combine(workspace.Location, target!));
            return DebugProgramAsync(target, (bool?)configuration["noDebug"] == true, configuration);
        }

        private async Task LaunchAsync(DartProject project, string program, DartLaunchKind kind, bool noDebug, string? testName, JObject? configuration)
        {
            if (!await launchGate.WaitAsync(0)) { environment.Log("A Dart debug launch is already starting."); return; }
            try
            {
                var sdk = await environment.RequireSdkAsync();
                if (project.IsFlutter) sdk.RequireFlutter();
                await SaveAllAsync();
                var unresolved = DartProject.UnresolvedReason(project.Root);
                if (unresolved != null)
                {
                    environment.Log("Resolving dependencies first (" + unresolved + "): pub get...");
                    var code = await OwnedProcess.RunAsync(DartCommandFactory.PubGet(sdk, project), environment.Log, CancellationToken.None);
                    if (code != 0) throw new InvalidOperationException("pub get failed (exit code " + code + "); see the Dart Output pane.");
                }
                var settings = DartSettings.Load();
                var launch = DartDebugLaunch.Merge(DartDebugLaunch.Create(project, program, settings, kind, noDebug, testName), configuration, project.Root);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var debugger = services.GetService(typeof(SVsShellDebugger)) as IVsDebugger4 ?? throw new InvalidOperationException("Visual Studio debugger unavailable.");
                var target = new VsDebugTargetInfo4
                {
                    dlo = (uint)DEBUG_LAUNCH_OPERATION.DLO_CreateProcess,
                    bstrExe = program,
                    bstrCurDir = project.Root,
                    bstrOptions = launch.ToString(Formatting.None),
                    guidLaunchDebugEngine = new Guid(EngineId),
                    fSendToOutputWindow = true,
                };
                environment.Log((noDebug ? "Starting without debugging: " : "Debugging: ") + (string?)launch["name"] +
                    (project.IsFlutter && kind != DartLaunchKind.Test ? " on " + settings.FlutterDevice : "") + ". Program output appears in Debug Output.");
                debugger.LaunchDebugTargets4(1, new[] { target }, new VsDebugTargetProcessInfo[1]);
            }
            catch (Exception e)
            {
                environment.Log("Cannot start Dart debugging: " + e.Message);
                await environment.ShowOutputAsync();
            }
            finally { launchGate.Release(); }
        }

        private async Task SaveAllAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try { (services.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ExecuteCommand("File.SaveAll"); }
            catch (System.Runtime.InteropServices.COMException) { }
        }
    }

    /// <summary>F5 / Ctrl+F5 on a .dart startup item or a launch.vs.json target of type <c>dart</c> in Open Folder.</summary>
    [ExportLaunchDebugTarget(ProviderId, new[] { ".dart" }, true)]
    [PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class DartLaunchProvider : ILaunchDebugTargetProvider5
    {
        internal const string ProviderId = "e7b1c3d5-2f4a-4b6c-8d9e-1a3c5e7f9b20";
        internal const string LaunchType = "dart";
        // Used by Visual Studio's Debug and Launch Settings editor.
        internal const string Schema = @"{""definitions"":{""dart"":{""type"":""object"",""properties"":{
            ""type"":{""enum"":[""dart""]},""program"":{""type"":""string""},""name"":{""type"":""string""},
            ""args"":{""type"":""array"",""items"":{""type"":""string""}},
            ""toolArgs"":{""type"":""array"",""items"":{""type"":""string""}},
            ""deviceId"":{""type"":""string""},""cwd"":{""type"":""string""},
            ""env"":{""type"":""object"",""additionalProperties"":{""type"":""string""}}}},
            ""dartFile"":{""allOf"":[{""$ref"":""#/definitions/default""},{""$ref"":""#/definitions/dart""}]}},
            ""defaults"":{"".dart"":{""$ref"":""#/definitions/dart""}},""configuration"":""#/definitions/dartFile""}";
        private readonly DartDebugCoordinator coordinator;

        [ImportingConstructor]
        public DartLaunchProvider(DartDebugCoordinator coordinator) => this.coordinator = coordinator;

        public bool SupportsContext(IWorkspace workspace, string targetFilePath) => DartProject.IsDartFile(targetFilePath);
        public bool SupportsTargetPath(IWorkspace workspace, string targetFilePath) => SupportsContext(workspace, targetFilePath);
        public bool SupportsProjectConfiguration(IWorkspace workspace, ProjectConfiguration configuration) => configuration.LaunchSettings.GetValue("type", "") == LaunchType;
        public bool SupportsContext(IWorkspace workspace, ProjectConfiguration? configuration, string path) =>
            configuration != null ? SupportsProjectConfiguration(workspace, configuration) : SupportsContext(workspace, path);
        public void LaunchDebugTarget(IWorkspace workspace, IServiceProvider services, DebugLaunchActionContext context) =>
            ThreadHelper.JoinableTaskFactory.Run(() => LaunchDebugTargetAsync(workspace, services, context, CancellationToken.None));
        public Task LaunchDebugTargetAsync(IWorkspace workspace, IServiceProvider services, DebugLaunchActionContext context, CancellationToken token) =>
            coordinator.LaunchAsync(workspace, context, token);
    }

    [ExportLaunchConfigurationProvider("f8c2d4e6-3a5b-4c7d-9e0f-2b4d6f8a0c31", new[] { ".dart" }, DartLaunchProvider.LaunchType, DartLaunchProvider.Schema)]
    internal sealed class DartLaunchConfigurationProvider : ILaunchConfigurationProvider
    {
        public bool IsDebugLaunchActionSupported(DebugLaunchActionContext context) => true;
        public void CustomizeLaunchConfiguration(DebugLaunchActionContext context, IPropertySettings settings)
        {
            settings["args"] = Array.Empty<string>();
        }
    }
}
