param(
    [Parameter(Mandatory = $true)][string]$GradleRoot,
    [Parameter(Mandatory = $true)][string]$JdkHome
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$core = Join-Path $repo 'src/Vistudio.Core/bin/Release/net472/Vistudio.Core.dll'
Add-Type -Path $core
$workspace = Join-Path $repo ('artifacts/project-metadata-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workspace | Out-Null
$defaults = [Vistudio.Core.KotlinSettings]::new()
$defaults.GradleRoot = (Resolve-Path -LiteralPath $GradleRoot).Path
$defaults.GradleJavaHome = (Resolve-Path -LiteralPath $JdkHome).Path
$defaults.BuildTasks = 'classes'
$project = [Vistudio.Core.Projects.KotlinProjectFile]::Create($workspace, $defaults)
$project.Save()
$effective = [Vistudio.Core.KotlinSettings]::new()
[Vistudio.Core.KotlinSettings]::ApplyProject($workspace, $effective)
if ($effective.GradleJavaHome -ne $defaults.GradleJavaHome -or $effective.GradleRoot -ne $defaults.GradleRoot) {
    throw 'Project settings did not round trip.'
}
Add-Type -ReferencedAssemblies $core -TypeDefinition @'
using System;
using System.IO;
using System.Threading;
using Vistudio.Core;
public static class ProjectMetadataBuildCheck
{
    public static int Run(KotlinSettings settings, string log)
    {
        using (var writer = new StreamWriter(log))
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
        {
            var start = GradleCommand.Create(settings.GradleRoot, settings.GradleJavaHome, GradleCommand.ParseTasks(settings.BuildTasks));
            return OwnedProcess.RunAsync(start, line => { lock (writer) writer.WriteLine(line); }, cancellation.Token).GetAwaiter().GetResult();
        }
    }
}
'@
$log = Join-Path $workspace 'gradle-build.log'
$code = [ProjectMetadataBuildCheck]::Run($effective, $log)
Get-Content -LiteralPath $log
if ($code -ne 0) { throw "Gradle classes failed with exit $code; see $log" }
Write-Output "PASS: generated $($project.FilePath), selected JDK $($project.JdkVersion), Gradle classes exit 0."
