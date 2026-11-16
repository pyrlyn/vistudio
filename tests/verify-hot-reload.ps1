# Native Visual Studio acceptance for Kotlin/JVM hot reload (T72). Run with Windows PowerShell (STA, COM automation).
# Installs the VSIX into an isolated root suffix, generates a Kotlin console project (the New Project template's generator),
# opens it, starts it through the Kotlin server (Ctrl+F5, or F5 with -Debugger) and reloads it: on save, by Tools > Kotlin > Hot Reload,
# with a compiler error, with a change the JVM refuses, with -Debugger also at a breakpoint, and checks that the
# session's Gradle daemon ends with the session.
# The checks are in HotReloadAcceptance.cs (typed EnvDTE calls: editor, Output panes, Error List, debugger).
# The server path goes into the test solution's .vs\settings.json, so the global Tools > Options values stay untouched.
# A root suffix that Visual Studio has never run with stops at its first-launch prompts (sign in, theme) and exposes no
# automation object until they are answered; the script skips both ("Skip and add accounts later.", "Start Visual Studio").
param(
    [string]$Vsix = (Join-Path $PSScriptRoot '../src/Vistudio.Vsix/bin/Debug/net472/Vistudio.Vsix.vsix'),
    [string]$RootSuffix = 'VistudioHotReload',
    [string]$KotlinServer = (Join-Path $env:LOCALAPPDATA 'Vistudio/servers/kotlin-server-271.2085.0/bin/intellij-server.exe'),
    [string]$Tests = (Join-Path $PSScriptRoot 'Vistudio.Tests/bin/Debug/net472/Vistudio.Tests.exe'),
    # F5 with the breakpoint checks instead of Ctrl+F5. The Kotlin server's debugger waits 30 s for the JVM; the launch
    # runs on the debug session's Gradle daemon to make that. Without this switch the program runs without a debugger.
    [switch]$Debugger,
    [switch]$SkipInstall,
    [switch]$KeepOpen
)
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$instance = & $vswhere -latest -format json | ConvertFrom-Json | Select-Object -First 1
$devenv = Join-Path $instance.installationPath 'Common7/IDE/devenv.exe'
$installer = Join-Path $instance.installationPath 'Common7/IDE/VSIXInstaller.exe'

$evidence = Join-Path $env:TEMP ('vistudio-hot-reload-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidence | Out-Null
$log = Join-Path $evidence 'acceptance.log'
$script:passed = 0
function Say([string]$text) { [IO.File]::AppendAllText($log, $text + [Environment]::NewLine); Write-Host $text }
function Check([bool]$condition, [string]$message) {
    if (-not $condition) { Say "FAIL: $message"; throw $message }
    $script:passed++; Say "PASS: $message"
}
function WaitUntil([scriptblock]$condition, [int]$seconds) {
    for ($i = 0; $i -lt $seconds * 2; $i++) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 500 }
    return [bool](& $condition)
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class HotReloadRot {
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
public interface IHotReloadOleMessageFilter {
    [PreserveSig] int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo);
    [PreserveSig] int RetryRejectedCall(IntPtr callee, int tickCount, int rejectType);
    [PreserveSig] int MessagePending(IntPtr callee, int tickCount, int pendingType);
}
public sealed class HotReloadMessageFilter : IHotReloadOleMessageFilter {
    [DllImport("ole32.dll")] private static extern int CoRegisterMessageFilter(IHotReloadOleMessageFilter filter, out IHotReloadOleMessageFilter previous);
    public static void Register() { IHotReloadOleMessageFilter previous; CoRegisterMessageFilter(new HotReloadMessageFilter(), out previous); }
    public int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo) { return 0; }
    public int RetryRejectedCall(IntPtr callee, int tickCount, int rejectType) { return tickCount < 120000 ? 250 : -1; }
    public int MessagePending(IntPtr callee, int tickCount, int pendingType) { return 2; }
}
'@
[HotReloadMessageFilter]::Register()

Check (Test-Path -LiteralPath $KotlinServer) "Kotlin language server found: $KotlinServer"
if (-not $SkipInstall) {
    $vsixPath = (Resolve-Path $Vsix).Path
    Say ("VSIX SHA-256: " + (Get-FileHash -Algorithm SHA256 $vsixPath).Hash)
    $p = Start-Process -FilePath $installer -ArgumentList @('/quiet', "/rootSuffix:$RootSuffix", "/instanceIds:$($instance.instanceId)", "`"$vsixPath`"") -PassThru -Wait
    Check ($p.ExitCode -eq 0) "VSIXInstaller installed the package into root suffix $RootSuffix (exit $($p.ExitCode))"
    $p = Start-Process -FilePath $devenv -ArgumentList @('/RootSuffix', $RootSuffix, '/UpdateConfiguration') -PassThru -Wait
    Check ($p.ExitCode -eq 0) 'Visual Studio configuration refreshed'
}

$interop = Join-Path $instance.installationPath 'Common7/IDE/PublicAssemblies/Microsoft.VisualStudio.Interop.dll'
[Reflection.Assembly]::LoadFrom($interop) | Out-Null
$csc = Join-Path $instance.installationPath 'MSBuild/Current/Bin/Roslyn/csc.exe'
$helper = Join-Path $evidence 'HotReloadAcceptance.dll'
& $csc /nologo /target:library /langversion:latest /nullable:disable "/out:$helper" "/reference:$interop" (Join-Path $PSScriptRoot 'HotReloadAcceptance.cs') | ForEach-Object { Say $_ }
Check ($LASTEXITCODE -eq 0) 'Acceptance helper compiled'
Add-Type -Path $helper

$activityLog = Join-Path $evidence 'ActivityLog.xml'
# The project comes from the generator behind the New Project template (Vistudio.Core KotlinProjectTemplate, run by the
# test executable), in a folder of its own with a solution beside it: Visual Studio passes the solution folder to the
# Kotlin server as its root, and the server imports only a Gradle build found there.
$projects = Join-Path $evidence 'projects'
& $Tests --templates (Join-Path $PSScriptRoot '../samples/kotlin-jvm') $projects | ForEach-Object { Say $_ }
Check ($LASTEXITCODE -eq 0) 'The Kotlin console project was generated'
$solutionFolder = Join-Path $projects 'JvmConsole'
$file = Join-Path $solutionFolder 'vistudio.kotlinproj'
$projectGuid = ([xml](Get-Content -Raw $file)).Project.PropertyGroup.ProjectGuid.Trim('{}').ToUpperInvariant()
$tab = "`t"
$solutionFile = Join-Path $solutionFolder 'HotApp.sln'
@(
    '', 'Microsoft Visual Studio Solution File, Format Version 12.00', '# Visual Studio Version 18', 'VisualStudioVersion = 18.0.0.0', 'MinimumVisualStudioVersion = 10.0.40219.1',
    "Project(`"{E7447D71-1121-42DC-8A4D-D3D78B3CC633}`") = `"HotApp`", `"vistudio.kotlinproj`", `"{$projectGuid}`"", 'EndProject',
    'Global', "${tab}GlobalSection(SolutionConfigurationPlatforms) = preSolution", "${tab}${tab}Debug|Any CPU = Debug|Any CPU", "${tab}EndGlobalSection",
    "${tab}GlobalSection(ProjectConfigurationPlatforms) = postSolution",
    "${tab}${tab}{$projectGuid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU", "${tab}${tab}{$projectGuid}.Debug|Any CPU.Build.0 = Debug|Any CPU",
    "${tab}EndGlobalSection", 'EndGlobal'
) | Set-Content -LiteralPath $solutionFile -Encoding UTF8
New-Item -ItemType Directory -Force -Path (Join-Path $solutionFolder '.vs') | Out-Null
@{ 'vistudio.kotlin.languageServerPath' = $KotlinServer } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $solutionFolder '.vs/settings.json') -Encoding UTF8

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
# Answers a first-launch prompt of this devenv, if one is showing: the sign-in page is skipped, the theme page accepted.
function SkipFirstLaunch([int]$processId) {
    $auto = [Windows.Automation.AutomationElement]
    $byProcess = New-Object Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $processId)
    foreach ($window in $auto::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $byProcess)) {
        foreach ($name in 'Skip and add accounts later.', 'Start Visual Studio') {
            $byName = New-Object Windows.Automation.PropertyCondition($auto::NameProperty, $name)
            foreach ($element in $window.FindAll([Windows.Automation.TreeScope]::Descendants, $byName)) {
                $pattern = $null
                if ($element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); Say "First-launch prompt answered: $name"; return }
            }
        }
    }
}

$a = New-Object HotReloadAcceptance -ArgumentList (, [Action[string]]{ param($line) Say $line })
$a.WriteProgram($file)
$process = Start-Process -FilePath $devenv -ArgumentList @('/RootSuffix', $RootSuffix, "`"$solutionFile`"", '/Log', "`"$activityLog`"") -PassThru
$dte = $null
for ($i = 0; $i -lt 480 -and -not $dte; $i++) {
    Start-Sleep -Seconds 1
    $dte = [HotReloadRot]::FindDte($process.Id)
    if (-not $dte -and $i % 10 -eq 9) { try { SkipFirstLaunch $process.Id } catch { } }
}
if (-not $dte) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
Check ($dte -ne $null) "Automation object of devenv $($process.Id) is available"
try {
    $a.Attach($dte)
    Check (WaitUntil { $dte.Solution.IsOpen -and $dte.Solution.Projects.Count -ge 1 } 180) "Visual Studio opened the Kotlin project from $solutionFile"
    $a.Run([bool]$Debugger)
    # Stop Debugging ended the session: the program ran on the session's Gradle daemon and ends with it.
    $projectJvms = { @(Get-CimInstance Win32_Process -Filter "Name='java.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($solutionFolder) }) }
    Check (WaitUntil { (& $projectJvms).Count -eq 0 } 30) "No JVM running from the project is left after Stop Debugging ($((& $projectJvms).Count) found)"
}
catch {
    Say ("ERROR: " + $_.Exception.Message + ' at ' + $_.InvocationInfo.PositionMessage)
    throw
}
finally {
    Say 'Cleaning up'
    foreach ($pane in 'Kotlin', 'Debug') { try { [IO.File]::WriteAllText((Join-Path $evidence "$pane-pane.txt"), $a.Pane($pane)) } catch { } }
    # A program started without a debugger outlives Stop when the launch leaves it running; end what runs from this project.
    Get-CimInstance Win32_Process -Filter "Name='java.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($solutionFolder) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    if (-not $KeepOpen) {
        try { if ($dte.Debugger.CurrentMode -ne 1) { $dte.Debugger.Stop($true) } } catch { }
        try { $dte.Documents.CloseAll(2) } catch { }
        try { $dte.Solution.Close($false) } catch { }
        try { $dte.Quit() } catch { }
        if (-not $process.WaitForExit(60000)) { Say 'WARN: devenv did not exit in 60 s; ending it'; Stop-Process -Id $process.Id -Force }
        $errors = Select-String -LiteralPath $activityLog -Pattern '<type>Error</type>' -Context 0, 4 -ErrorAction SilentlyContinue | Where-Object { $_.Context.PostContext -match 'Vistudio' }
        Say ("ActivityLog Vistudio errors: " + @($errors).Count)
        $errors | ForEach-Object { Say $_.ToString() }
    }
}
$total = $script:passed + $(if ($a) { $a.Passed } else { 0 })
Say "PASS: $total native Visual Studio checks. Evidence: $evidence"
