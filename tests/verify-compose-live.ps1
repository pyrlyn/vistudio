# Native Visual Studio acceptance for the interactive mode of the Kotlin Compose Preview window.
# Run with Windows PowerShell (STA, COM automation). Installs the VSIX into an isolated root suffix, opens a copy of
# samples/compose-multiplatform with Open Folder and drives the window through DTE and UI Automation
# (tests/ComposeLiveAcceptance.cs). Two steps use the real mouse and keyboard on the Visual Studio window; they are
# skipped and counted when Visual Studio cannot be brought to the foreground.
param(
    [string]$Vsix = (Join-Path $PSScriptRoot '../src/Vistudio.Vsix/bin/Debug/net472/Vistudio.Vsix.vsix'),
    [string]$RootSuffix = 'VistudioComposeLive',
    [switch]$SkipInstall,
    [switch]$KeepOpen
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$instance = & $vswhere -latest -format json | ConvertFrom-Json | Select-Object -First 1
$devenv = Join-Path $instance.installationPath 'Common7/IDE/devenv.exe'
$installer = Join-Path $instance.installationPath 'Common7/IDE/VSIXInstaller.exe'

$evidence = Join-Path $env:TEMP ('vistudio-compose-live-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidence | Out-Null
$log = Join-Path $evidence 'acceptance.log'
$script:passed = 0
function Say([string]$text) { [IO.File]::AppendAllText($log, $text + [Environment]::NewLine); Write-Host $text }
function Check([bool]$condition, [string]$message) {
    if (-not $condition) { Say "FAIL: $message"; throw $message }
    $script:passed++; Say "PASS: $message"
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class ComposeRot {
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
public interface IComposeOleMessageFilter {
    [PreserveSig] int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo);
    [PreserveSig] int RetryRejectedCall(IntPtr callee, int tickCount, int rejectType);
    [PreserveSig] int MessagePending(IntPtr callee, int tickCount, int pendingType);
}
public sealed class ComposeMessageFilter : IComposeOleMessageFilter {
    [DllImport("ole32.dll")] private static extern int CoRegisterMessageFilter(IComposeOleMessageFilter filter, out IComposeOleMessageFilter previous);
    public static void Register() { IComposeOleMessageFilter previous; CoRegisterMessageFilter(new ComposeMessageFilter(), out previous); }
    public int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo) { return 0; }
    public int RetryRejectedCall(IntPtr callee, int tickCount, int rejectType) { return tickCount < 120000 ? 250 : -1; }
    public int MessagePending(IntPtr callee, int tickCount, int pendingType) { return 2; }
}
'@
[ComposeMessageFilter]::Register()

# A copy: the acceptance edits Interactive.kt, and the first build must not depend on what the repository's sample has built.
$sample = Join-Path $evidence 'compose-multiplatform'
New-Item -ItemType Directory -Path $sample | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repo 'samples/compose-multiplatform') -Force | Where-Object { $_.Name -notin 'build', '.gradle', '.kotlin', '.vs', '.idea' } | ForEach-Object {
    if ($_.PSIsContainer) { robocopy $_.FullName (Join-Path $sample $_.Name) /E /XD build .gradle .kotlin /NFL /NDL /NJH /NJS /NP | Out-Null } else { Copy-Item -LiteralPath $_.FullName -Destination $sample }
}
Say "Workspace: $sample"

if (-not $SkipInstall) {
    $vsixPath = (Resolve-Path $Vsix).Path
    Say ("VSIX SHA-256: " + (Get-FileHash -Algorithm SHA256 $vsixPath).Hash)
    # The development package keeps version 0.1.0: remove an earlier build from this profile first (exit code ignored when none is installed).
    $p = Start-Process -FilePath $installer -ArgumentList @('/quiet', "/rootSuffix:$RootSuffix", "/instanceIds:$($instance.instanceId)", '/uninstall:Vistudio.Kotlin.6cc1f5c0-99c8-43c6-b605-8dac96970119') -PassThru -Wait
    Say "VSIXInstaller /uninstall exit code: $($p.ExitCode)"
    $p = Start-Process -FilePath $installer -ArgumentList @('/quiet', "/rootSuffix:$RootSuffix", "/instanceIds:$($instance.instanceId)", "`"$vsixPath`"") -PassThru -Wait
    Check ($p.ExitCode -eq 0) "VSIXInstaller installed the package into root suffix $RootSuffix (exit $($p.ExitCode))"
    $p = Start-Process -FilePath $devenv -ArgumentList @('/RootSuffix', $RootSuffix, '/UpdateConfiguration') -PassThru -Wait
    Check ($p.ExitCode -eq 0) 'Visual Studio configuration refreshed'
}

$interop = Join-Path $instance.installationPath 'Common7/IDE/PublicAssemblies/Microsoft.VisualStudio.Interop.dll'
[Reflection.Assembly]::LoadFrom($interop) | Out-Null
$wpfFolder = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\WPF'
$csc = Join-Path $instance.installationPath 'MSBuild/Current/Bin/Roslyn/csc.exe'
$helper = Join-Path $evidence 'ComposeLiveAcceptance.dll'
& $csc /nologo /target:library /langversion:latest /nullable:disable "/out:$helper" "/reference:$interop" /reference:System.Management.dll /reference:System.Drawing.dll "/reference:$wpfFolder\UIAutomationClient.dll" "/reference:$wpfFolder\UIAutomationTypes.dll" "/reference:$wpfFolder\WindowsBase.dll" (Join-Path $PSScriptRoot 'ComposeLiveAcceptance.cs')
Check ($LASTEXITCODE -eq 0) 'Acceptance helper compiled'
Add-Type -Path $helper

# The first start of a new root suffix stops at "Sign in to Visual Studio" and then at the settings page, before the
# automation object exists. Neither is answered with an account or a choice: sign-in is skipped, the defaults are kept.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
function Skip-FirstRun([int]$processId) {
    try {
        $own = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
        foreach ($window in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $own)) {
            foreach ($name in 'Skip and add accounts later.', 'Start Visual Studio') {
                $element = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name))) |
                    Where-Object { $_.Current.ControlType -ne [System.Windows.Automation.ControlType]::Text } | Select-Object -First 1
                if ($element) { Say "First start of this profile: $name"; $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return }
            }
        }
    } catch { }
}

$activityLog = Join-Path $evidence 'ActivityLog.xml'
$process = $null
$dte = $null
$hostsQuery = { @(Get-CimInstance Win32_Process -Filter "Name='java.exe'" | Where-Object { $_.CommandLine -match 'compose-live' -and $_.CommandLine -match ('s-' + $process.Id + '-') }) }
try {
    $process = Start-Process -FilePath $devenv -ArgumentList @('/RootSuffix', $RootSuffix, "`"$sample`"", '/Log', "`"$activityLog`"") -PassThru
    for ($i = 0; $i -lt 360 -and -not $dte; $i++) {
        Start-Sleep -Seconds 1
        $dte = [ComposeRot]::FindDte($process.Id)
        if (-not $dte -and $i % 5 -eq 4) { Skip-FirstRun $process.Id }
    }
    Check ($dte -ne $null) "Automation object of devenv $($process.Id) is available"
    $acceptance = New-Object ComposeLiveAcceptance $dte, ([Action[string]]{ param($line) Say $line })
    $script:passed += $acceptance.Run($sample, $evidence)
    if ($acceptance.Skipped -gt 0) { Say "SKIPPED: $($acceptance.Skipped) real-input step(s)" }
}
finally {
    if (-not $KeepOpen -and $process) {
        if ($dte) { try { $dte.Documents.CloseAll(2) } catch { } }
        $quit = [Diagnostics.Stopwatch]::StartNew()
        if ($dte) { try { $dte.Quit() } catch { Say "WARN: DTE.Quit failed: $($_.Exception.Message)" } }
        if (-not $process.WaitForExit(60000)) { Say "WARN: devenv did not exit in 60 s; ending it"; Stop-Process -Id $process.Id -Force }
        else { Say ("Visual Studio exited " + [Math]::Round($quit.Elapsed.TotalSeconds, 1) + " s after DTE.Quit") }
        Start-Sleep -Seconds 3
        $left = & $hostsQuery
        Check ($left.Count -eq 0) "No preview host is left after Visual Studio exited with a preview running ($($left.Count))"
        if (Test-Path $activityLog) {
            $errors = @(Select-String -LiteralPath $activityLog -Pattern '<type>Error</type>' -Context 0, 4 | Where-Object { ($_.Context.PostContext -join ' ') -match 'Vistudio' })
            $preview = @($errors | Where-Object { ($_.Context.PostContext -join ' ') -match 'ComposePreview|Compose' })
            if ($errors.Count -gt $preview.Count) { Say "WARN: $($errors.Count - $preview.Count) ActivityLog error(s) from other Vistudio components (not the Compose preview)" }
            Check ($preview.Count -eq 0) "No Compose preview errors in the ActivityLog ($($preview.Count))"
        }
    }
}
Say "PASS: $($script:passed) native Visual Studio checks. Evidence: $evidence"
