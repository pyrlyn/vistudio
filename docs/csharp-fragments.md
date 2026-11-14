# Key C# fragments

These historical design excerpts are separate from the compiled implementation under `src/`. See the root README for the currently shipped feature set.

These are integration excerpts for the architecture in [architecture.md](architecture.md), not a complete compilable VSIX. Types explicitly described as project contracts need implementation. SDK references, MEF exports for services, package registration, shutdown plumbing, and tests are not supplied. No fragments have been compiled or executed.

## 1. Register Kotlin files for the built-in LSP client

```csharp
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;

internal static class KotlinContentTypes
{
    public const string Name = "Kotlin";

    [Export]
    [Name(Name)]
    [BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
    internal static ContentTypeDefinition ContentType;

    [Export]
    [FileExtension(".kt")]
    [ContentType(Name)]
    internal static FileExtensionToContentTypeDefinition KotlinFile;

    [Export]
    [FileExtension(".kts")]
    [ContentType(Name)]
    internal static FileExtensionToContentTypeDefinition KotlinScript;
}
```

The class uses MEF metadata fields, which are intentionally not initialized by application code. Register the assembly as a `Microsoft.VisualStudio.MefComponent` asset in the VSIX manifest.

Register the separately supplied TextMate grammar through a packaged `.pkgdef`:

```text
[$RootKey$\TextMate\Repositories]
"Vistudio.Kotlin"="$PackageFolder$\Grammars"
```

The grammar's `fileTypes` must include `kt` and `kts`; it needs a real Kotlin rule set. This registration fragment does not supply a grammar.

## 2. Connect the language server

This is the activation portion of `KotlinLanguageClient`. The rest of the `ILanguageClient` implementation belongs in the same class. `_sessions` is a project-owned `LanguageServerSessionManager`, not a Visual Studio SDK service. It validates configuration, starts the selected server using its actual release-specific launcher, owns process lifetime, drains stderr, and exposes raw protocol streams.

```csharp
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

[Export(typeof(ILanguageClient))]
[ContentType(KotlinContentTypes.Name)]
public sealed partial class KotlinLanguageClient : ILanguageClient
{
    // Project-owned service; import and initialize in the complete class.
    private readonly LanguageServerSessionManager _sessions;

    public event AsyncEventHandler<EventArgs> StartAsync;
    public event AsyncEventHandler<EventArgs> StopAsync;

    public Task OnLoadedAsync()
    {
        return StartAsync?.InvokeAsync(this, EventArgs.Empty)
            ?? Task.CompletedTask;
    }

    public async Task<Connection> ActivateAsync(CancellationToken token)
    {
        var session = await _sessions.StartForActiveWorkspaceAsync(token)
            .ConfigureAwait(false);

        // Read from server stdout; write to server stdin. No log text here.
        return new Connection(session.ProtocolOutput, session.ProtocolInput);
    }

    public Task RequestStopAsync()
    {
        // Allow VS to send LSP shutdown before process cleanup.
        return StopAsync?.InvokeAsync(this, EventArgs.Empty)
            ?? Task.CompletedTask;
    }
}
```

Complete the SDK contract with `Name`, `ConfigurationSections`, `InitializationOptions`, `FilesToWatch`, `ShowNotificationOnInitializeFailed`, `OnServerInitializedAsync`, and the initialization-failure callback appropriate to the pinned SDK. Current SDK documentation includes `Task<InitializationFailureContext> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo)`; older samples use an `Exception` callback. Check the referenced assembly rather than copying an older sample unchanged.

Do not add a second JSON-RPC consumer to these streams. VS sends initialize and document synchronization. The manager must handle initialization failure, process exit, workspace switch, and bounded graceful shutdown followed by owned-process cleanup. Configure Gradle model watching and any server-specific initialization only according to the selected backend's protocol.

References: [ILanguageClient](https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.languageserver.client.ilanguageclient?view=visualstudiosdk-2022), [initialization failure callback](https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.languageserver.client.ilanguageclient.onserverinitializefailedasync?view=visualstudiosdk-2022).

## 3. Prepare a Gradle invocation without MSBuild

This fragment supports **task paths only**, not arbitrary command-line options or build properties. Its restricted token grammar makes the command safe to pass through `cmd.exe`; a richer CLI needs a separate tested argument encoder. The workspace wrapper is expected to be trusted project code.

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

internal static class GradleCommand
{
    private static readonly Regex TaskPath = new Regex(
        @"\A:?[A-Za-z_][A-Za-z0-9_.-]*(?::[A-Za-z_][A-Za-z0-9_.-]*)*\z",
        RegexOptions.CultureInvariant);

    public static ProcessStartInfo Create(
        string root, string javaHome, IReadOnlyList<string> tasks)
    {
        root = Path.GetFullPath(root);
        javaHome = Path.GetFullPath(javaHome);

        if (!File.Exists(Path.Combine(root, "gradlew.bat")))
            throw new FileNotFoundException("Gradle Wrapper is missing.");
        if (!File.Exists(Path.Combine(javaHome, "bin", "java.exe")))
            throw new DirectoryNotFoundException("Configured JDK is missing.");
        if (tasks == null || tasks.Count == 0 ||
            tasks.Any(t => t == null || !TaskPath.IsMatch(t)))
            throw new ArgumentException("Expected Gradle task paths only.");

        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            // /d disables AutoRun; /v:off disables delayed expansion.
            // Paths are passed through WorkingDirectory and environment.
            Arguments = "/d /v:off /s /c \"\"gradlew.bat\" --console=plain "
                + string.Join(" ", tasks) + "\"",
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.EnvironmentVariables["JAVA_HOME"] = javaHome;
        return start;
    }
}
```

The UTF-8 decoding policy must match the selected Gradle/JDK output configuration and be tested with non-ASCII diagnostics; setting the reader's encoding does not force a child JVM to emit that encoding.

The build service consumes this command through a project-owned process supervisor:

```csharp
// Excerpt: _processes, _output, _errors and BuildResult are project contracts.
public async Task<BuildResult> BuildAsync(
    GradleWorkspace workspace, CancellationToken cancellationToken)
{
    var generation = _errors.BeginBuild(workspace.Id);
    var command = GradleCommand.Create(
        workspace.Root, workspace.JavaHome, workspace.BuildTasks);

    var result = await _processes.RunToExitAsync(
        command,
        onLine: line =>
        {
            _output.Enqueue(line.Text);
            _errors.AcceptCompilerLine(workspace.Id, generation, line);
        },
        cancellationToken).ConfigureAwait(false);

    // Complete replaces only this build's rows and rejects stale generations.
    // A nonzero exit without parsed diagnostics adds a build-failure summary.
    return _errors.Complete(workspace.Id, generation, result);
}
```

`RunToExitAsync` must concurrently drain both streams, close unused stdin, await process exit and stream completion, report launch failure distinctly from nonzero exit, and cancel only the owned process tree. It must not call synchronous `WaitForExit` on the UI thread. `AcceptCompilerLine` must serialize or synchronize state because stdout and stderr can arrive concurrently. Cancellation also completes/invalidate the build generation through a `finally`/cancellation path in the full service.

This abstraction is intentionally not presented as an already implemented process supervisor.

## 4. Publish one build error in Error List

This small presenter demonstrates the SDK boundary. The full design uses snapshots and workspace/build generations for replacement and deduplication. Both ErrorTask and the underlying text view use zero-based coordinates here; the parser normalizes compiler coordinates before this call.

```csharp
using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;

internal sealed class BuildErrorListPresenter : IDisposable
{
    private readonly ErrorListProvider _provider;

    public BuildErrorListPresenter(IServiceProvider services)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _provider = new ErrorListProvider(services)
        {
            ProviderName = "Kotlin Gradle",
            ProviderGuid = new Guid("A6072E87-509E-4A70-8462-2CBECA51F543")
        };
    }

    public void Add(string absoluteFile, int line0, int column0,
                    string message, bool warning)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var task = new ErrorTask
        {
            Category = TaskCategory.BuildCompile,
            ErrorCategory = warning
                ? TaskErrorCategory.Warning : TaskErrorCategory.Error,
            Document = absoluteFile,
            Line = Math.Max(0, line0),
            Column = Math.Max(0, column0),
            Text = message
        };
        task.Navigate += (sender, args) =>
            _provider.Navigate(task, VSConstants.LOGVIEWID_Code);
        _provider.Tasks.Add(task);
    }

    public void ClearOwnedBuildErrors()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _provider.Tasks.Clear();
    }

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _provider.Dispose();
    }
}
```

Call the presenter after `await package.JoinableTaskFactory.SwitchToMainThreadAsync(token)`. The provider owns only its own build errors. For large builds batch updates; do not switch threads for every log line. Verify double-click line/column navigation against the chosen SDK and add explicit text-view positioning if required by its helper behavior.

Reference: [ErrorListProvider](https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.errorlistprovider?view=visualstudiosdk-2022).

## 5. Express the F5 orchestration boundary

The following is **orchestration pseudocode in C#**. Every lower-case service and result type is a project abstraction. In particular, `debugHost.StartAttachSessionAsync` is not a Visual Studio SDK method and does not itself implement a debugger. Its implementation must bridge the Open Folder launch provider to the registered Debug Adapter Host engine.

```csharp
public async Task StartDebuggingAsync(
    DebugLaunchProfile profile, CancellationToken token)
{
    await documents.SaveForLaunchAsync(profile.Workspace, token);

    var build = await gradle.BuildAsync(profile.Workspace, token);
    if (!build.Succeeded)
        return;

    // An ownership lease cleans up this launch if any following step fails.
    using (var launch = await gradleDebug.StartSuspendedAsync(profile, token))
    {
        // Await readiness, not completion of Gradle's still-running run task.
        // Readiness comes from launch output/protocol, not a probing JDWP client.
        var endpoint = await launch.WaitForDebuggerEndpointAsync(token);

        // Starts a DAP session. The adapter then connects to endpoint via JDWP.
        var session = await debugHost.StartAttachSessionAsync(
            profile.Adapter, endpoint, profile.SourceRoots, token);

        // Hand over target lifetime only after the debugger owns the session.
        // The adapter resumes the VM after initial DAP configuration completes.
        launch.TransferOwnershipTo(session);
    }
}
```

Keep `Preparing`, `Building`, `StartingTarget`, `ConnectingAdapter`, `Configuring`, `Running`, `Stopped`, `Terminating`, and `Finished` states distinct. A rejected attach or canceled startup releases the launch lease. Detaching from an externally owned JVM leaves it running; terminating a plugin-owned launch cleans it up. The session owns adapter cleanup even when its target exits first.

The adapter must implement DAP requests and Kotlin/JVM source mapping described in [architecture.md](architecture.md). A launch profile with `hostName` and `port` is insufficient to provide this functionality.

For comparison, the SDK's actual custom transport hook is:

```csharp
// Signatures from IAdapterLauncher, not an implementation:
void UpdateLaunchOptions(IAdapterLaunchInfo launchInfo);
ITargetHostProcess LaunchAdapter(
    IAdapterLaunchInfo launchInfo, ITargetHostInterop targetInterop);
```

`IAdapterLauncher` also inherits `IDebugAdapterHostComponent.Initialize`. Return a process abstraction exposing **DAP** input/output. Register the custom launcher and engine using the Host's documented registration mechanism; merely defining this class is insufficient. [Custom transport contract](https://github.com/microsoft/VSDebugAdapterHost/wiki/Providing-a-Custom-Transport).

## What remains implementation work

Full package and MEF composition; TextMate grammar; language-server process management and backend compatibility; Gradle model import and process supervision; compiler parser and snapshot store; native build/F5 providers; JVM DAP adapter with Kotlin source mapping; deployment and the acceptance tests in the architecture document. These are intentionally not hidden behind a claim that the fragments form a working plugin.
