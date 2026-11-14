using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Workspace;
using Microsoft.VisualStudio.Workspace.Debug;

namespace Vistudio
{
    [ExportLaunchDebugTarget(ProviderId, new[] { ".kt", ".java" }, true)]
    [PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class KotlinLaunchProvider : ILaunchDebugTargetProvider5
    {
        internal const string ProviderId = "9b60fbae-dc22-464a-aea3-df7a134c1f2c";
        internal const string LaunchType = "kotlin-jvm";
        // Used by Visual Studio's Debug and Launch Settings editor, not VS Code.
        internal const string Schema = @"{""definitions"":{""kotlin-jvm"":{""type"":""object"",""required"":[""mainClass""],""properties"":{
            ""type"":{""enum"":[""kotlin-jvm""]},""mainClass"":{""type"":""string""},
            ""project"":{""type"":""string""},""name"":{""type"":""string""},
            ""args"":{""type"":""array"",""items"":{""type"":""string""}},
            ""vmArgs"":{""type"":""array"",""items"":{""type"":""string""}},
            ""cwd"":{""type"":""string""},""env"":{""type"":""object"",""additionalProperties"":{""type"":""string""}},
            ""gradleProject"":{""type"":""string""},""sourceSet"":{""type"":""string""}}},
            ""kotlinFile"":{""allOf"":[{""$ref"":""#/definitions/default""},{""$ref"":""#/definitions/kotlin-jvm""}]}},
            ""defaults"":{"".kt"":{""$ref"":""#/definitions/kotlin-jvm""},"".java"":{""$ref"":""#/definitions/kotlin-jvm""}},""configuration"":""#/definitions/kotlinFile""}";
        private readonly KotlinDebugCoordinator coordinator;
        [ImportingConstructor]
        public KotlinLaunchProvider(KotlinDebugCoordinator coordinator) => this.coordinator = coordinator;

        // Kotlin and Java entry points share one JVM debug session.
        public bool SupportsContext(IWorkspace workspace, string targetFilePath) => KotlinJvmLaunch.IsSourceFile(targetFilePath);
        public bool SupportsTargetPath(IWorkspace workspace, string targetFilePath) => SupportsContext(workspace, targetFilePath);
        public bool SupportsProjectConfiguration(IWorkspace workspace, ProjectConfiguration configuration) => IsOurs(configuration.LaunchSettings);
        public bool SupportsContext(IWorkspace workspace, ProjectConfiguration? configuration, string path) =>
            configuration != null ? SupportsProjectConfiguration(workspace, configuration) : SupportsContext(workspace, path);
        private static bool IsOurs(IPropertySettings settings) => settings.GetValue("type", "") == LaunchType;
        public void LaunchDebugTarget(IWorkspace workspace, IServiceProvider services, DebugLaunchActionContext context) =>
            ThreadHelper.JoinableTaskFactory.Run(() => LaunchDebugTargetAsync(workspace, services, context, CancellationToken.None));
        public Task LaunchDebugTargetAsync(IWorkspace workspace, IServiceProvider services, DebugLaunchActionContext context, CancellationToken token) =>
            coordinator.LaunchAsync(workspace.Location, context, token);
    }

    [ExportLaunchConfigurationProvider("9a6531ca-79a3-4ab5-ad90-f4221c9fc3fe", new[] { ".kt", ".java" }, KotlinLaunchProvider.LaunchType, KotlinLaunchProvider.Schema)]
    internal sealed class KotlinLaunchConfigurationProvider : ILaunchConfigurationProvider
    {
        public bool IsDebugLaunchActionSupported(DebugLaunchActionContext context) => true;
        public void CustomizeLaunchConfiguration(DebugLaunchActionContext context, IPropertySettings settings)
        {
            settings["mainClass"] = "your.package.MainKt";
            settings["args"] = Array.Empty<string>();
        }
    }
}
