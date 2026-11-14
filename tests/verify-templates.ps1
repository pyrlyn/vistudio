# Native Visual Studio acceptance for the Kotlin New Project templates. Run with Windows PowerShell (STA, COM automation).
# Installs the VSIX into an isolated root suffix, checks that every template is registered for the Kotlin language, creates
# each project through the Visual Studio template engine (the same path as File > New > Project), waits for the Gradle
# import, builds the solution through the solution build manager and, for the console app, starts it with F5.
# The Multiplatform template opens its target dialog; answer it in the IDE (or pass -SkipMultiplatform).
param(
    [string]$Vsix = (Join-Path $PSScriptRoot '../artifacts/Vistudio.Kotlin.vsix'),
    # Use an initialized profile: a new root suffix shows its first-run dialog, which blocks automation.
    [string]$RootSuffix = 'VistudioImportTest',
    [string[]]$Templates = @('KotlinConsoleApp', 'KotlinClassLibrary', 'KotlinJavaApp', 'ComposeDesktopApp', 'KotlinMultiplatformApp'),
    [switch]$SkipInstall,
    [switch]$SkipMultiplatform,
    [switch]$SkipDebug,
    # Lets Gradle's JVM trust the Windows certificate store (needed where the JDK's cacerts lack the network's root CA).
    [switch]$WindowsTrustStore,
    [switch]$KeepOpen
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Remove-Item Env:NoDefaultCurrentDirectoryInExePath -ErrorAction SilentlyContinue
if ($WindowsTrustStore) { $env:JAVA_TOOL_OPTIONS = '-Djavax.net.ssl.trustStoreType=WINDOWS-ROOT' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$instance = & $vswhere -latest -format json | ConvertFrom-Json | Select-Object -First 1
$devenv = Join-Path $instance.installationPath 'Common7/IDE/devenv.exe'
$installer = Join-Path $instance.installationPath 'Common7/IDE/VSIXInstaller.exe'

$evidence = Join-Path $repo ('artifacts/templates-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidence | Out-Null
$log = Join-Path $evidence 'acceptance.log'
$script:passed = 0
function Say([string]$text) { [IO.File]::AppendAllText($log, $text + [Environment]::NewLine); Write-Output $text }
function Check([bool]$condition, [string]$message) {
    if (-not $condition) { Say "FAIL: $message"; throw $message }
    $script:passed++; Say "PASS: $message"
}
function Wait([scriptblock]$condition, [int]$seconds) {
    for ($i = 0; $i -lt $seconds; $i++) { if (& $condition) { return $true }; Start-Sleep -Seconds 1 }
    return [bool](& $condition)
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class TemplateRot {
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
public interface ITemplateOleMessageFilter {
    [PreserveSig] int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo);
    [PreserveSig] int RetryRejectedCall(IntPtr callee, int tickCount, int rejectType);
    [PreserveSig] int MessagePending(IntPtr callee, int tickCount, int pendingType);
}
public sealed class TemplateMessageFilter : ITemplateOleMessageFilter {
    [DllImport("ole32.dll")] private static extern int CoRegisterMessageFilter(ITemplateOleMessageFilter filter, out ITemplateOleMessageFilter previous);
    public static void Register() { ITemplateOleMessageFilter previous; CoRegisterMessageFilter(new TemplateMessageFilter(), out previous); }
    public int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo) { return 0; }
    // The IDE rejects automation calls while it is busy (solution load, modal target dialog); keep retrying.
    public int RetryRejectedCall(IntPtr callee, int tickCount, int rejectType) { return tickCount < 900000 ? 250 : -1; }
    public int MessagePending(IntPtr callee, int tickCount, int pendingType) { return 2; }
}
'@
[TemplateMessageFilter]::Register()

if (-not $SkipInstall) {
    $vsixPath = (Resolve-Path $Vsix).Path
    Say ("VSIX SHA-256: " + (Get-FileHash -Algorithm SHA256 $vsixPath).Hash)
    # The development package keeps version 0.1.0, so remove an earlier installation from this profile first.
    Start-Process -FilePath $installer -ArgumentList @('/quiet', "/rootSuffix:$RootSuffix", "/instanceIds:$($instance.instanceId)", '/uninstall:Vistudio.Kotlin.6cc1f5c0-99c8-43c6-b605-8dac96970119') -Wait
    $p = Start-Process -FilePath $installer -ArgumentList @('/quiet', "/rootSuffix:$RootSuffix", "/instanceIds:$($instance.instanceId)", "`"$vsixPath`"") -PassThru -Wait
    Check ($p.ExitCode -eq 0) "VSIXInstaller installed the package into root suffix $RootSuffix (exit $($p.ExitCode))"
    $p = Start-Process -FilePath $devenv -ArgumentList @('/RootSuffix', $RootSuffix, '/UpdateConfiguration') -PassThru -Wait
    Check ($p.ExitCode -eq 0) 'Visual Studio configuration refreshed'
}

$activityLog = Join-Path $evidence 'ActivityLog.xml'
# Opening a solution skips the start window, which otherwise delays the automation object.
$startSolution = Join-Path $evidence 'Start.sln'
[IO.File]::WriteAllText($startSolution, "`r`nMicrosoft Visual Studio Solution File, Format Version 12.00`r`n# Visual Studio Version 18`r`nGlobal`r`nEndGlobal`r`n")
$process = Start-Process -FilePath $devenv -ArgumentList @('/RootSuffix', $RootSuffix, "`"$startSolution`"", '/Log', "`"$activityLog`"") -PassThru
$dte = $null
for ($i = 0; $i -lt 180 -and -not $dte; $i++) { Start-Sleep -Seconds 1; $dte = [TemplateRot]::FindDte($process.Id) }
Check ($dte -ne $null) "Automation object of devenv $($process.Id) is available"
Wait { try { $dte.Solution -ne $null } catch { $false } } 120 | Out-Null
# Output panes are reachable through DTE only after the Output window exists.
$dte.ExecuteCommand('View.Output')

$interop = Join-Path $instance.installationPath 'Common7/IDE/PublicAssemblies/Microsoft.VisualStudio.Interop.dll'
$solution2 = [Reflection.Assembly]::LoadFrom($interop).GetType('EnvDTE80.Solution2', $true)
# Late binding does not reach the Output window panes; read them through the typed DTE2 interface.
Add-Type -ReferencedAssemblies $interop -TypeDefinition @'
public static class TemplatePanes {
    public static string Text(object dte, string name) {
        foreach (EnvDTE.OutputWindowPane pane in ((EnvDTE80.DTE2)dte).ToolWindows.OutputWindow.OutputWindowPanes) {
            if (pane.Name != name) continue;
            var document = pane.TextDocument;
            return document.StartPoint.CreateEditPoint().GetText(document.EndPoint);
        }
        return "";
    }
}
'@
function PaneText([string]$name) { [TemplatePanes]::Text($dte, $name) }
try {
    foreach ($template in $Templates) {
        if ($SkipMultiplatform -and $template -eq 'KotlinMultiplatformApp') { Say "SKIP: $template"; continue }
        # GetProjectTemplate belongs to Solution2, which late binding does not expose; call it through the interop type.
        $path = $solution2.GetMethod('GetProjectTemplate').Invoke($dte.Solution, @("$template.vstemplate", 'Kotlin'))
        Check ([IO.File]::Exists($path)) "$template is registered for the Kotlin language: $path"
        $solutionDir = Join-Path $evidence $template
        $name = $template.Replace('Kotlin', 'Demo')
        $projectDir = Join-Path $solutionDir $name
        New-Item -ItemType Directory -Path $solutionDir | Out-Null
        if ($dte.Solution.IsOpen) { $dte.Solution.Close($false) }
        $dte.Solution.Create($solutionDir, $name)
        if ($template -eq 'KotlinMultiplatformApp') { Say 'Answer the Kotlin Multiplatform targets dialog in Visual Studio.' }
        $added = $true
        try { $dte.Solution.AddFromTemplate($path, $projectDir, $name, $false) | Out-Null }
        catch { $added = $false; Say "AddFromTemplate: $($_.Exception.Message)" }
        Check $added "$template was created by the Visual Studio template engine without errors"
        $projectFile = Join-Path $projectDir 'vistudio.kotlinproj'
        Check (Wait { [IO.File]::Exists($projectFile) -and [IO.File]::Exists((Join-Path $projectDir 'gradlew.bat')) } 30) "$template generated $projectFile with the Gradle wrapper"
        $loaded = Wait { @($dte.Solution.Projects | Where-Object { try { $_.FullName -eq $projectFile } catch { $false } }).Count -eq 1 } 60
        Check $loaded "$template project is in the solution (DTE project $projectFile)"
        $dte.Solution.SaveAs((Join-Path $solutionDir "$name.sln"))
        Check (Wait { ([IO.File]::ReadAllText($projectFile)) -match '<ImportedAtUtc>' } 900) "$template Gradle model was imported automatically"
        Copy-Item -LiteralPath $projectFile -Destination (Join-Path $evidence "$template.kotlinproj")
        # The import holds the shared Kotlin Gradle gate until its process exits; let it finish before building.
        Wait { (PaneText 'Kotlin') -match 'Gradle import (finished|in)|Imported' } 60 | Out-Null
        Start-Sleep -Seconds 5
        $build = $dte.Solution.SolutionBuild
        $build.Build($true)
        $buildPane = PaneText 'Build'
        [IO.File]::WriteAllText((Join-Path $evidence "$template-build.txt"), $buildPane + "`r`n--- LastBuildInfo: $($build.LastBuildInfo)`r`n--- Kotlin pane:`r`n" + (PaneText 'Kotlin'))
        Check ($build.LastBuildInfo -eq 0 -and $buildPane.Contains('BUILD SUCCESSFUL')) "$template builds through the solution build manager (Gradle)"
        if ($template -eq 'KotlinConsoleApp' -and -not $SkipDebug) {
            $dte.Debugger.Go($false)
            Wait { $dte.Debugger.CurrentMode -ne 1 } 120 | Out-Null
            Wait { $dte.Debugger.CurrentMode -eq 1 } 600 | Out-Null
            $all = (@('Debug', 'Kotlin') | ForEach-Object { PaneText $_ }) -join "`n"
            [IO.File]::WriteAllText((Join-Path $evidence "$template-run.txt"), $all)
            Check ($all.Contains('Hello, Kotlin!')) "$template runs with F5 and prints its greeting"
        }
        [IO.File]::WriteAllText((Join-Path $evidence "$template-kotlin.txt"), (PaneText 'Kotlin'))
    }
    Say "PASS: $($script:passed) native Visual Studio checks. Evidence: $evidence"
}
finally {
    if (-not $KeepOpen) { try { $dte.Quit() } catch { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue } }
}
