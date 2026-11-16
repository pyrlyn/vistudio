# Native Visual Studio acceptance for T71 (CPU and memory while debugging). Run with Windows PowerShell (STA, COM
# automation). Installs the VSIX into an isolated root suffix, creates Dart and C projects from the New Project templates,
# stops each at a breakpoint with F5 and reads the diagnostics windows through UI Automation:
# - Dart (Debug Adapter Host engine): the Vistudio Diagnostics window shows the Dart VM session, its samples and the break;
#   Visual Studio's Diagnostic Tools window is read for its "does not support" text (recorded, not required).
# - C (native engine): Visual Studio's Diagnostic Tools window is open and does not refuse the engine.
# - Kotlin (Kotlin/JVM Debug Adapter Host engine, through the Kotlin server and Gradle): Vistudio Diagnostics finds the
#   program's JVM and shows its CPU, memory and JVM heap. The server path goes into the test solution's .vs\settings.json,
#   so the global Tools > Options values stay untouched.
param(
    [string]$Vsix = (Join-Path $PSScriptRoot '../src/Vistudio.Vsix/bin/Debug/net472/Vistudio.Vsix.vsix'),
    [string]$RootSuffix = 'VistudioDiagnostics',
    [string]$FlutterSdk = 'C:/Users/listepo/tools/flutter',
    [string]$KotlinServer = (Join-Path $env:LOCALAPPDATA 'Vistudio/servers/kotlin-server-271.2085.0/bin/intellij-server.exe'),
    [ValidateSet('all', 'dart', 'c', 'kotlin')][string[]]$Part = @('all'),
    # Keep the Kotlin project one folder below the solution (New Project's layout), where F5 currently times out.
    [switch]$KotlinSubfolder,
    [switch]$SkipInstall,
    [switch]$KeepOpen
)
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$instance = & $vswhere -latest -format json | ConvertFrom-Json | Select-Object -First 1
$devenv = Join-Path $instance.installationPath 'Common7/IDE/devenv.exe'
$installer = Join-Path $instance.installationPath 'Common7/IDE/VSIXInstaller.exe'
$vistudioDiagnostics = '{c7e1f3a5-2d4b-4c6e-8f0a-9b1d3e5f7a26}'
$diagnosticTools = '{6d4078d1-5951-4ed1-ac0e-0a8099c1cce5}'

$evidence = Join-Path $env:TEMP ('vistudio-diagnostics-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidence | Out-Null
$log = Join-Path $evidence 'acceptance.log'
$script:passed = 0
function Want([string]$name) { $Part -contains 'all' -or $Part -contains $name }
function Say([string]$text) { [IO.File]::AppendAllText($log, $text + [Environment]::NewLine); Write-Host $text }
function Check([bool]$condition, [string]$message) {
    if (-not $condition) { Say "FAIL: $message"; throw $message }
    $script:passed++; Say "PASS: $message"
}
function WaitUntil([scriptblock]$condition, [int]$seconds) {
    for ($i = 0; $i -lt $seconds * 2; $i++) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 500 }
    return [bool](& $condition)
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class DiagnosticsRot {
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
public interface IDiagnosticsOleMessageFilter {
    [PreserveSig] int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo);
    [PreserveSig] int RetryRejectedCall(IntPtr callee, int tickCount, int rejectType);
    [PreserveSig] int MessagePending(IntPtr callee, int tickCount, int pendingType);
}
public sealed class DiagnosticsMessageFilter : IDiagnosticsOleMessageFilter {
    [DllImport("ole32.dll")] private static extern int CoRegisterMessageFilter(IDiagnosticsOleMessageFilter filter, out IDiagnosticsOleMessageFilter previous);
    public static void Register() { IDiagnosticsOleMessageFilter previous; CoRegisterMessageFilter(new DiagnosticsMessageFilter(), out previous); }
    public int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo) { return 0; }
    public int RetryRejectedCall(IntPtr callee, int tickCount, int rejectType) { return tickCount < 120000 ? 250 : -1; }
    public int MessagePending(IntPtr callee, int tickCount, int pendingType) { return 2; }
}
'@
[DiagnosticsMessageFilter]::Register()

# Texts of the UI Automation subtree of the devenv element named $name with the most content (the tab header has the
# same name as the pane, but almost no descendants).
function PaneTexts([int]$processId, [string]$name) {
    $auto = [Windows.Automation.AutomationElement]
    $byProcess = New-Object Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $processId)
    $byName = New-Object Windows.Automation.PropertyCondition($auto::NameProperty, $name)
    $best = @()
    foreach ($window in $auto::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $byProcess)) {
        foreach ($pane in $window.FindAll([Windows.Automation.TreeScope]::Descendants, $byName)) {
            $texts = @($pane.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) |
                ForEach-Object { $_.Current.Name } | Where-Object { $_ })
            if ($texts.Count -gt $best.Count) { $best = $texts }
        }
    }
    return , $best
}

# True when devenv has a UI Automation element with this name (a tool window's tab or pane, even in the background).
function HasElement([int]$processId, [string]$name) {
    $auto = [Windows.Automation.AutomationElement]
    $byProcess = New-Object Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $processId)
    $byName = New-Object Windows.Automation.PropertyCondition($auto::NameProperty, $name)
    foreach ($window in $auto::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $byProcess)) {
        if ($window.FindFirst([Windows.Automation.TreeScope]::Descendants, $byName)) { return $true }
    }
    return $false
}
# Brings a tool window to the front through its command (late-bound DTE Window objects show no captions to PowerShell).
function ShowWindow([string[]]$commands) {
    foreach ($command in $commands) { try { $dte.ExecuteCommand($command); Say "Command $command ran"; return } catch { Say "Command $command failed: $($_.Exception.Message)" } }
}

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
$helper = Join-Path $evidence 'LanguageProjectsAcceptance.dll'
& $csc /nologo /target:library /langversion:latest /nullable:disable "/out:$helper" "/reference:$interop" (Join-Path $PSScriptRoot 'LanguageProjectsAcceptance.cs') | ForEach-Object { Say $_ }
Check ($LASTEXITCODE -eq 0) 'Acceptance helper compiled'
Add-Type -Path $helper

$env:PATH = (Join-Path $FlutterSdk 'bin/cache/dart-sdk/bin') + ';' + (Join-Path $FlutterSdk 'bin') + ';' + $env:PATH
$env:CONAN_HOME = Join-Path $evidence 'conan-home'
if (Want 'c') {
    cmd.exe /d /c "conan profile detect >nul 2>&1"
    Check ($LASTEXITCODE -eq 0) 'Conan default profile detected in the private CONAN_HOME'
}

$activityLog = Join-Path $evidence 'ActivityLog.xml'
$solutionFolder = Join-Path $evidence 'solution'
New-Item -ItemType Directory -Path $solutionFolder | Out-Null
$solutionFile = Join-Path $solutionFolder 'Acceptance.sln'
@"

Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 18
VisualStudioVersion = 18.0.0.0
MinimumVisualStudioVersion = 10.0.40219.1
Global
	GlobalSection(SolutionProperties) = preSolution
		HideSolutionNode = FALSE
	EndGlobalSection
EndGlobal
"@ | Set-Content -LiteralPath $solutionFile -Encoding UTF8
if (Want 'kotlin') {
    Check (Test-Path -LiteralPath $KotlinServer) "Kotlin language server found: $KotlinServer"
    New-Item -ItemType Directory -Path (Join-Path $solutionFolder '.vs') | Out-Null
    @{ 'vistudio.kotlin.languageServerPath' = $KotlinServer } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $solutionFolder '.vs/settings.json') -Encoding UTF8
}
$process = Start-Process -FilePath $devenv -ArgumentList @('/RootSuffix', $RootSuffix, "`"$solutionFile`"", '/Log', "`"$activityLog`"") -PassThru
$dte = $null
for ($i = 0; $i -lt 240 -and -not $dte; $i++) { Start-Sleep -Seconds 1; $dte = [DiagnosticsRot]::FindDte($process.Id) }
if (-not $dte) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
Check ($dte -ne $null) "Automation object of devenv $($process.Id) is available"
$a = $null
try {
    $a = New-Object LanguageProjectsAcceptance $dte, ([Action[string]]{ param($line) Say $line })

    if (Want 'dart') {
        $file = $a.Create('DartConsoleApp', $solutionFolder, 'DartApp', '.dartproj')
        $folder = Split-Path $file
        Push-Location $folder; cmd.exe /d /c "dart pub get >nul 2>&1"; Pop-Location
        Check ($LASTEXITCODE -eq 0) 'dart pub get for the Dart console project'
        $a.SetStartup('DartApp')
        $program = Get-ChildItem (Join-Path $folder 'bin') -Filter '*.dart' | Select-Object -First 1
        $line = (Select-String -LiteralPath $program.FullName -Pattern 'print\(' | Select-Object -First 1).LineNumber
        $null = $dte.Debugger.Breakpoints.Add('', $program.FullName, $line)
        $dte.ExecuteCommand('Debug.Start')
        Check (WaitUntil { $dte.Debugger.CurrentMode -eq 2 } 180) "Dart F5 stopped at the breakpoint in $($program.Name):$line"
        $opened = WaitUntil { HasElement $process.Id 'Vistudio Diagnostics' } 30
        if (-not $opened) {
            foreach ($pane in 'Dart', 'Debug') { try { Say ("--- $pane pane:" + [Environment]::NewLine + $a.Pane($pane)) } catch { } }
        }
        Check $opened 'The Vistudio Diagnostics window opened with the Dart session'
        ShowWindow @('View.VistudioDiagnostics')
        $texts = @()
        $found = WaitUntil { $script:texts = PaneTexts $process.Id 'Vistudio Diagnostics'; ($script:texts -join "`n") -match 'Dart VM, pid \d+' -and ($script:texts -join "`n") -match 'Debugger break' -and ($script:texts -join "`n") -match 'Private bytes \d' } 60
        Say ("Vistudio Diagnostics texts: " + (($script:texts | Select-Object -Unique) -join ' | '))
        Check $found 'Vistudio Diagnostics shows the Dart VM session with private bytes and the debugger break'
        try {
            ShowWindow @('Debug.ShowDiagnosticTools', 'Debug.DiagnosticTools', 'View.DiagnosticTools')
            $tools = PaneTexts $process.Id 'Diagnostic Tools'
            Say ("Diagnostic Tools texts during the Dart session: " + (($tools | Select-Object -Unique | Select-Object -First 40) -join ' | '))
            if (($tools -join "`n") -match 'does not support debugging using') { Say "INFO: Diagnostic Tools refuses the Dart / Flutter engine, as the probe found." }
        } catch { Say "INFO: Diagnostic Tools window not read during the Dart session: $($_.Exception.Message)" }
        $dte.Debugger.Go($false)
        Check (WaitUntil { $dte.Debugger.CurrentMode -eq 1 } 120) 'The Dart program ran to the end'
        ShowWindow @('View.VistudioDiagnostics')
        $ended = WaitUntil { $script:texts = PaneTexts $process.Id 'Vistudio Diagnostics'; ($script:texts -join "`n") -match 'Debugging stopped\.|The program exited\.' } 30
        Say ("Vistudio Diagnostics texts after the session: " + (($script:texts | Select-Object -Unique) -join ' | '))
        Check $ended 'The Vistudio Diagnostics session ended with the debug session and kept its data'
        $dte.Debugger.Breakpoints | ForEach-Object { $_.Delete() }
    }

    if (Want 'kotlin') {
        $file = $a.Create('KotlinConsoleApp', $solutionFolder, 'KotlinApp', 'vistudio.kotlinproj', 'Kotlin')
        if (-not $KotlinSubfolder) {
            # Visual Studio passes the solution folder to the Kotlin server as its root, and the server imports only a
            # Gradle build found there; with the project one folder down (New Project's default) it imports nothing and F5
            # times out (-KotlinSubfolder reproduces it). This run reopens the project from a solution in its own folder.
            $projectFolder = Split-Path $file
            $projectGuid = ([xml](Get-Content -Raw $file)).Project.PropertyGroup.ProjectGuid.Trim('{}').ToUpperInvariant()
            $tab = "`t"
            $kotlinSolution = Join-Path $projectFolder 'KotlinApp.sln'
            @(
                '', 'Microsoft Visual Studio Solution File, Format Version 12.00', '# Visual Studio Version 18', 'VisualStudioVersion = 18.0.0.0', 'MinimumVisualStudioVersion = 10.0.40219.1',
                "Project(`"{E7447D71-1121-42DC-8A4D-D3D78B3CC633}`") = `"KotlinApp`", `"vistudio.kotlinproj`", `"{$projectGuid}`"", 'EndProject',
                'Global', "${tab}GlobalSection(SolutionConfigurationPlatforms) = preSolution", "${tab}${tab}Debug|Any CPU = Debug|Any CPU", "${tab}EndGlobalSection",
                "${tab}GlobalSection(ProjectConfigurationPlatforms) = postSolution",
                "${tab}${tab}{$projectGuid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU", "${tab}${tab}{$projectGuid}.Debug|Any CPU.Build.0 = Debug|Any CPU",
                "${tab}EndGlobalSection", 'EndGlobal'
            ) | Set-Content -LiteralPath $kotlinSolution -Encoding UTF8
            New-Item -ItemType Directory -Force -Path (Join-Path $projectFolder '.vs') | Out-Null
            Copy-Item -LiteralPath (Join-Path $solutionFolder '.vs/settings.json') -Destination (Join-Path $projectFolder '.vs/settings.json')
            $dte.Solution.Close($false)
            $dte.Solution.Open($kotlinSolution)
            Check (WaitUntil { $dte.Solution.IsOpen -and $dte.Solution.Projects.Count -ge 1 } 120) "Reopened the Kotlin project from $kotlinSolution"
        }
        # The Kotlin template leaves ProjectName empty, so the caption is not the name given here.
        $captions = @()
        $null = WaitUntil { $script:captions = @($a.SolutionChildren()); $script:captions.Count -gt 0 } 60
        Say ('Solution Explorer projects: ' + ($script:captions -join ', '))
        $a.SetStartup(($script:captions | Select-Object -First 1))
        $main = Get-ChildItem (Join-Path (Split-Path $file) 'src/main/kotlin') -Recurse -Filter 'Main.kt' | Select-Object -First 1
        $line = (Select-String -LiteralPath $main.FullName -Pattern 'println\(' | Select-Object -First 1).LineNumber
        $null = $dte.Debugger.Breakpoints.Add('', $main.FullName, $line)
        $started = Get-Date
        $dte.ExecuteCommand('Debug.Start')
        # The first F5 loads the Kotlin server, imports the Gradle project and builds it.
        $stopped = WaitUntil { $dte.Debugger.CurrentMode -eq 2 } 900
        if (-not $stopped) { foreach ($pane in 'Kotlin', 'Debug') { try { Say ("--- $pane pane:" + [Environment]::NewLine + $a.Pane($pane)) } catch { } } }
        Check $stopped ("Kotlin F5 stopped at the breakpoint in Main.kt:$line (" + [int]((Get-Date) - $started).TotalSeconds + " s after F5)")
        Check (WaitUntil { HasElement $process.Id 'Vistudio Diagnostics' } 60) 'The Vistudio Diagnostics window opened with the Kotlin session'
        ShowWindow @('View.VistudioDiagnostics')
        $found = WaitUntil { $script:texts = PaneTexts $process.Id 'Vistudio Diagnostics'; $joined = $script:texts -join "`n"; $joined -match '\(JVM, pid \d+\)' -and $joined -match 'JVM heap \d' -and $joined -match 'Private bytes \d' } 90
        Say ("Vistudio Diagnostics texts: " + (($script:texts | Select-Object -Unique) -join ' | '))
        if (-not $found) { try { Say ("--- Kotlin pane:" + [Environment]::NewLine + $a.Pane('Kotlin')) } catch { } }
        Check $found 'Vistudio Diagnostics shows the Kotlin program''s JVM with private bytes and the JVM heap'
        $dte.Debugger.Go($false)
        Check (WaitUntil { $dte.Debugger.CurrentMode -eq 1 } 180) 'The Kotlin program ran to the end'
        ShowWindow @('View.VistudioDiagnostics')
        $ended = WaitUntil { $script:texts = PaneTexts $process.Id 'Vistudio Diagnostics'; ($script:texts -join "`n") -match 'Debugging stopped\.|The program exited\.' } 30
        Say ("Vistudio Diagnostics texts after the session: " + (($script:texts | Select-Object -Unique) -join ' | '))
        Check $ended 'The Vistudio Diagnostics session of the Kotlin program ended with the debug session'
        try { Say ("--- Kotlin pane:" + [Environment]::NewLine + $a.Pane('Kotlin')) } catch { }
        $dte.Debugger.Breakpoints | ForEach-Object { $_.Delete() }
    }

    if (Want 'c') {
        $file = $a.Create('CConsoleApp', $solutionFolder, 'CApp', '.cproj')
        $a.SetStartup('CApp')
        Check ($a.Build(900)) 'Solution build runs conan install, CMake configure and build for the C project'
        $main = Join-Path (Split-Path $file) 'src/main.c'
        $null = $dte.Debugger.Breakpoints.Add('', $main, 6)
        $dte.ExecuteCommand('Debug.Start')
        Check (WaitUntil { $dte.Debugger.CurrentMode -eq 2 } 180) 'C F5 stopped at the breakpoint in main.c:6 (native engine)'
        Check (WaitUntil { HasElement $process.Id 'Diagnostic Tools' } 60) 'Visual Studio opened its Diagnostic Tools window for the native session'
        ShowWindow @('Debug.ShowDiagnosticTools', 'Debug.DiagnosticTools', 'View.DiagnosticTools')
        $tools = @()
        $null = WaitUntil { $script:tools = PaneTexts $process.Id 'Diagnostic Tools'; $script:tools.Count -gt 5 } 60
        Say ("Diagnostic Tools texts during the C session: " + (($script:tools | Select-Object -Unique | Select-Object -First 60) -join ' | '))
        Check (-not (($script:tools -join "`n") -match 'does not support debugging using')) 'Diagnostic Tools does not refuse the native engine'
        $dte.Debugger.Stop($true)
        $null = WaitUntil { $dte.Debugger.CurrentMode -eq 1 } 60
        $dte.Debugger.Breakpoints | ForEach-Object { $_.Delete() }
    }
}
finally {
    if (-not $KeepOpen) {
        try { $dte.Documents.CloseAll(2) } catch { }
        try { $dte.Solution.Close($false) } catch { }
        try { $dte.Quit() } catch { }
        if (-not $process.WaitForExit(60000)) { Say 'WARN: devenv did not exit in 60 s; ending it'; Stop-Process -Id $process.Id -Force }
        $errors = Select-String -LiteralPath $activityLog -Pattern '<type>Error</type>' -Context 0, 4 | Where-Object { $_.Context.PostContext -match 'Vistudio' }
        Say ("ActivityLog Vistudio errors: " + @($errors).Count)
        $errors | ForEach-Object { Say $_.ToString() }
    }
}
$total = $script:passed + $(if ($a) { $a.Passed } else { 0 })
Say "PASS: $total native Visual Studio checks. Evidence: $evidence"
