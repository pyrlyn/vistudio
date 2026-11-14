# Native Visual Studio acceptance of Kotlin Multiplatform targets (T21). Run with Windows PowerShell (STA, COM automation).
# Installs the VSIX into an isolated root suffix, opens a copy of samples/kotlin-multiplatform (jvm, js, wasmJs, mingwX64)
# through a .sln, imports the Gradle model in the IDE and drives Solution Explorer, target commands, Build, F5 and Ctrl+F5.
param(
    [string]$Vsix = (Join-Path $PSScriptRoot '../artifacts/Vistudio.Kotlin.vsix'),
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$RootSuffix = 'VistudioTargetsTest',
    [string]$JdkHome = 'C:/Program Files/Microsoft/jdk-11.0.31.11-hotspot',
    [switch]$SkipInstall,
    [switch]$SkipJvmRun,
    [switch]$UseWindowsTrustStore,
    [switch]$KeepOpen
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# cmd.exe must be able to start gradlew.bat from the Gradle root inside the launched IDE.
Remove-Item Env:NoDefaultCurrentDirectoryInExePath -ErrorAction SilentlyContinue
if ($UseWindowsTrustStore) {
    # Inherited by the launched IDE and its Gradle/Node processes only.
    $env:JAVA_TOOL_OPTIONS = "$env:JAVA_TOOL_OPTIONS -Djavax.net.ssl.trustStoreType=Windows-ROOT -Djavax.net.ssl.trustStore=NONE".Trim()
    $env:NODE_USE_SYSTEM_CA = '1'
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$instance = & $vswhere -latest -format json | ConvertFrom-Json | Select-Object -First 1
$devenv = Join-Path $instance.installationPath 'Common7/IDE/devenv.exe'
$installer = Join-Path $instance.installationPath 'Common7/IDE/VSIXInstaller.exe'

$evidence = Join-Path $repo ('artifacts/kotlin-targets-native-' + [Guid]::NewGuid().ToString('N'))
$workspace = Join-Path $evidence 'project'
New-Item -ItemType Directory -Path $workspace | Out-Null
$log = Join-Path $evidence 'acceptance.log'
$script:passed = 0
function Say([string]$text) { [IO.File]::AppendAllText($log, $text + [Environment]::NewLine); Write-Output $text }
function Check([bool]$condition, [string]$message) {
    if (-not $condition) { Say "FAIL: $message"; throw $message }
    $script:passed++; Say "PASS: $message"
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class VistudioRot {
    [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx context);
    public static object FindDte(int processId) {
        IRunningObjectTable table; GetRunningObjectTable(0, out table);
        IEnumMoniker monikers; table.EnumRunning(out monikers);
        var current = new IMoniker[1];
        while (monikers.Next(1, current, IntPtr.Zero) == 0) {
            IBindCtx context; CreateBindCtx(0, out context);
            string name; current[0].GetDisplayName(context, null, out name);
            if (name.StartsWith("!VisualStudio.DTE.") && name.EndsWith(":" + processId)) { object dte; table.GetObject(current[0], out dte); return dte; }
        }
        return null;
    }
}
[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IVistudioOleMessageFilter {
    [PreserveSig] int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo);
    [PreserveSig] int RetryRejectedCall(IntPtr callee, int tickCount, int rejectType);
    [PreserveSig] int MessagePending(IntPtr callee, int tickCount, int pendingType);
}
public sealed class VistudioMessageFilter : IVistudioOleMessageFilter {
    [DllImport("ole32.dll")] private static extern int CoRegisterMessageFilter(IVistudioOleMessageFilter filter, out IVistudioOleMessageFilter previous);
    public static void Register() { IVistudioOleMessageFilter previous; CoRegisterMessageFilter(new VistudioMessageFilter(), out previous); }
    public int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo) { return 0; }
    // The IDE rejects automation calls while it is busy (for example during solution load); retry instead of failing.
    public int RetryRejectedCall(IntPtr callee, int tickCount, int rejectType) { return tickCount < 120000 ? 250 : -1; }
    public int MessagePending(IntPtr callee, int tickCount, int pendingType) { return 2; }
}
public static class VistudioWindow {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    public struct Rect { public int Left, Top, Right, Bottom; }
}
'@
[VistudioMessageFilter]::Register()

# Copy of the Multiplatform sample without build outputs or caches.
$sample = Join-Path $repo 'samples/kotlin-multiplatform'
foreach ($entry in 'src', 'gradle', 'kotlin-js-store', 'build.gradle.kts', 'settings.gradle.kts', 'gradlew.bat') {
    Copy-Item -LiteralPath (Join-Path $sample $entry) -Destination $workspace -Recurse
}
Add-Type -Path (Join-Path $repo "src/Vistudio.Core/bin/$Configuration/net472/Vistudio.Core.dll")
$defaults = New-Object Vistudio.Core.KotlinSettings
$defaults.GradleJavaHome = $JdkHome
[Vistudio.Core.Projects.KotlinProjectFile]::Create($workspace, $defaults).Save()
$projectFile = Join-Path $workspace 'vistudio.kotlinproj'
$projectGuid = ([xml](Get-Content -Raw $projectFile)).Project.PropertyGroup.ProjectGuid.Trim('{}').ToUpperInvariant()
$solution = Join-Path $workspace 'KotlinMultiplatform.sln'
$tab = "`t"
@(
    '', 'Microsoft Visual Studio Solution File, Format Version 12.00', '# Visual Studio Version 18', 'VisualStudioVersion = 18.0.0.0', 'MinimumVisualStudioVersion = 10.0.40219.1',
    "Project(`"{E7447D71-1121-42DC-8A4D-D3D78B3CC633}`") = `"kotlin-multiplatform`", `"vistudio.kotlinproj`", `"{$projectGuid}`"", 'EndProject',
    'Global', "${tab}GlobalSection(SolutionConfigurationPlatforms) = preSolution", "${tab}${tab}Debug|Any CPU = Debug|Any CPU", "${tab}${tab}Release|Any CPU = Release|Any CPU", "${tab}EndGlobalSection",
    "${tab}GlobalSection(ProjectConfigurationPlatforms) = postSolution",
    "${tab}${tab}{$projectGuid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU", "${tab}${tab}{$projectGuid}.Debug|Any CPU.Build.0 = Debug|Any CPU",
    "${tab}${tab}{$projectGuid}.Release|Any CPU.ActiveCfg = Release|Any CPU", "${tab}${tab}{$projectGuid}.Release|Any CPU.Build.0 = Release|Any CPU",
    "${tab}EndGlobalSection", 'EndGlobal'
) | Set-Content -LiteralPath $solution -Encoding UTF8
Say "Workspace: $workspace"

if (-not $SkipInstall) {
    $vsixPath = (Resolve-Path $Vsix).Path
    Say ("VSIX: $vsixPath SHA-256: " + (Get-FileHash -Algorithm SHA256 $vsixPath).Hash)
    $p = Start-Process -FilePath $installer -ArgumentList @('/quiet', "/rootSuffix:$RootSuffix", "/instanceIds:$($instance.instanceId)", "`"$vsixPath`"") -PassThru -Wait
    Check ($p.ExitCode -eq 0) "VSIXInstaller installed the package into root suffix $RootSuffix (exit $($p.ExitCode))"
    $p = Start-Process -FilePath $devenv -ArgumentList @('/RootSuffix', $RootSuffix, '/UpdateConfiguration') -PassThru -Wait
    Check ($p.ExitCode -eq 0) 'Visual Studio configuration refreshed'
}

$activityLog = Join-Path $evidence 'ActivityLog.xml'
$process = Start-Process -FilePath $devenv -ArgumentList @('/RootSuffix', $RootSuffix, "`"$solution`"", '/Log', "`"$activityLog`"") -PassThru
$dte = $null
for ($i = 0; $i -lt 180 -and -not $dte; $i++) { Start-Sleep -Seconds 1; $dte = [VistudioRot]::FindDte($process.Id) }
Check ($dte -ne $null) "Automation object of devenv $($process.Id) is available"
for ($i = 0; $i -lt 180 -and -not ($dte.Solution.IsOpen -and $dte.Solution.FullName -eq $solution); $i++) { Start-Sleep -Seconds 1 }
Check ($dte.Solution.FullName -eq $solution) 'Solution opened'
$interop = Join-Path $instance.installationPath 'Common7/IDE/PublicAssemblies/Microsoft.VisualStudio.Interop.dll'
[Reflection.Assembly]::LoadFrom($interop) | Out-Null
# Windows PowerShell's Add-Type is limited to C# 5; compile the helper with the IDE's Roslyn compiler instead.
$csc = Join-Path $instance.installationPath 'MSBuild/Current/Bin/Roslyn/csc.exe'
$helper = Join-Path $evidence 'KotlinTargetsAcceptance.dll'
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
& $csc /nologo /target:library /langversion:latest /nullable:disable "/out:$helper" "/reference:$interop" "/reference:$framework/System.Drawing.dll" "/reference:$framework/System.Xml.Linq.dll" "/reference:$framework/WPF/UIAutomationClient.dll" "/reference:$framework/WPF/UIAutomationTypes.dll" (Join-Path $PSScriptRoot 'KotlinTargetsAcceptance.cs') | ForEach-Object { Say $_ }
Check ($LASTEXITCODE -eq 0) 'Acceptance helper compiled'
Add-Type -Path $helper
try {
    $acceptance = New-Object KotlinTargetsAcceptance $dte, $process.Id, $workspace, $projectFile, ([Action[string]]{ param($line) Say $line })
    $acceptance.SkipJvmRun = [bool]$SkipJvmRun
    $count = $acceptance.Run()
    Say "PASS: $($script:passed + $count) native Visual Studio checks. Evidence: $evidence"
}
finally {
    if (-not $KeepOpen -and $dte) { try { $dte.Solution.Close($false); $dte.Quit() } catch { } }
}
