# Native Visual Studio acceptance for Flutter widget preview. Run with Windows PowerShell (STA, COM automation).
# Installs the VSIX into an isolated root suffix, opens a copy of samples/flutter-preview with
# Open Folder (the SDK comes from each copy's .vs\settings.json, so global settings stay untouched) and drives the
# preview commands, the editor and the Dart pane through DTE.
param(
    [string]$Vsix = (Join-Path $PSScriptRoot '../src/Vistudio.Vsix/bin/Debug/net472/Vistudio.Vsix.vsix'),
    [string]$RootSuffix = 'VistudioPreview',
    [string]$FlutterSdk = $env:VISTUDIO_FLUTTER_SDK,
    [switch]$SkipInstall,
    [switch]$KeepOpen
)
$ErrorActionPreference = 'Stop'
if (-not $FlutterSdk -or -not (Test-Path (Join-Path $FlutterSdk 'bin/flutter.bat'))) { Write-Host "SKIP: set -FlutterSdk or VISTUDIO_FLUTTER_SDK to a Flutter 3.35+ SDK."; exit 0 }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$instance = & $vswhere -latest -format json | ConvertFrom-Json | Select-Object -First 1
$devenv = Join-Path $instance.installationPath 'Common7/IDE/devenv.exe'
$installer = Join-Path $instance.installationPath 'Common7/IDE/VSIXInstaller.exe'

$evidence = Join-Path $env:TEMP ('vistudio-preview-' + [Guid]::NewGuid().ToString('N'))
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
public static class DartRot {
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
public interface IDartOleMessageFilter {
    [PreserveSig] int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo);
    [PreserveSig] int RetryRejectedCall(IntPtr callee, int tickCount, int rejectType);
    [PreserveSig] int MessagePending(IntPtr callee, int tickCount, int pendingType);
}
public sealed class DartMessageFilter : IDartOleMessageFilter {
    [DllImport("ole32.dll")] private static extern int CoRegisterMessageFilter(IDartOleMessageFilter filter, out IDartOleMessageFilter previous);
    public static void Register() { IDartOleMessageFilter previous; CoRegisterMessageFilter(new DartMessageFilter(), out previous); }
    public int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo) { return 0; }
    public int RetryRejectedCall(IntPtr callee, int tickCount, int rejectType) { return tickCount < 120000 ? 250 : -1; }
    public int MessagePending(IntPtr callee, int tickCount, int pendingType) { return 2; }
}
'@
[DartMessageFilter]::Register()

# The SDK comes from the copy's .vs\settings.json, so global settings stay untouched. pub get is left to the preview.
$sample = Join-Path $evidence 'flutter-preview'
New-Item -ItemType Directory -Path $sample | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repo 'samples/flutter-preview') -Force | Where-Object { $_.Name -notin '.dart_tool', 'build', '.widget_preview' } | Copy-Item -Destination $sample -Recurse
New-Item -ItemType Directory -Path (Join-Path $sample '.vs') | Out-Null
# Verbose previewer log: the Dart pane then shows each page that connects to the previewer.
@{ 'vistudio.dart.flutterSdkPath' = $FlutterSdk; 'vistudio.dart.widgetPreviewVerbose' = $true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $sample '.vs/settings.json') -Encoding UTF8
# A Dart package without Flutter inside the workspace: the preview commands must stay hidden for its files.
$plain = Join-Path $sample 'tools/plain_dart'
New-Item -ItemType Directory -Force -Path (Join-Path $plain 'lib') | Out-Null
# LF only: mixed line endings make Visual Studio ask about them in a modal dialog.
[IO.File]::WriteAllText((Join-Path $plain 'pubspec.yaml'), "name: plain_dart`nenvironment:`n  sdk: "">=3.9.0 <4.0.0""`n")
[IO.File]::WriteAllText((Join-Path $plain 'lib/plain.dart'), "int answer() => 42;`n")
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
$helper = Join-Path $evidence 'FlutterPreviewAcceptance.dll'
& $csc /nologo /target:library /langversion:latest /nullable:disable "/out:$helper" "/reference:$interop" /reference:System.Management.dll /reference:System.Drawing.dll "/reference:$wpfFolder\UIAutomationClient.dll" "/reference:$wpfFolder\UIAutomationTypes.dll" "/reference:$wpfFolder\WindowsBase.dll" (Join-Path $PSScriptRoot 'FlutterPreviewAcceptance.cs') | ForEach-Object { Say $_ }
Check ($LASTEXITCODE -eq 0) 'Acceptance helper compiled'
Add-Type -Path $helper

# The window's toolbar choice lives in a per-user file: save the user's, use two configurations side by side, restore it at the end.
$windowState = Join-Path $env:LOCALAPPDATA 'Vistudio/flutter-preview-window.xml'
$windowStateBackup = $null
if (Test-Path $windowState) { $windowStateBackup = [IO.File]::ReadAllBytes($windowState) }
New-Item -ItemType Directory -Force -Path (Split-Path $windowState) | Out-Null
@'
<?xml version="1.0" encoding="utf-8"?>
<PreviewWindowState xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Device>Phone</Device>
  <CustomWidth>400</CustomWidth>
  <CustomHeight>800</CustomHeight>
  <Theme>both</Theme>
  <TextScale>1.3</TextScale>
  <Locale>ar</Locale>
  <LastProject />
</PreviewWindowState>
'@ | Set-Content -LiteralPath $windowState -Encoding UTF8

$activityLog = Join-Path $evidence 'ActivityLog.xml'
$process = $null
$dte = $null
try {
    $process = Start-Process -FilePath $devenv -ArgumentList @('/RootSuffix', $RootSuffix, "`"$sample`"", '/Log', "`"$activityLog`"") -PassThru
    for ($i = 0; $i -lt 240 -and -not $dte; $i++) { Start-Sleep -Seconds 1; $dte = [DartRot]::FindDte($process.Id) }
    Check ($dte -ne $null) "Automation object of devenv $($process.Id) is available"
    $acceptance = New-Object FlutterPreviewAcceptance $dte, ([Action[string]]{ param($line) Say $line })
    $script:passed += $acceptance.Run($sample, $evidence, $FlutterSdk)
}
finally {
    if ($windowStateBackup) { [IO.File]::WriteAllBytes($windowState, $windowStateBackup) } else { Remove-Item -LiteralPath $windowState -ErrorAction SilentlyContinue }
    if (-not $KeepOpen -and $process) {
        if ($dte) { try { $dte.Documents.CloseAll(2) } catch { } }
        $quit = [Diagnostics.Stopwatch]::StartNew()
        if ($dte) { try { $dte.Quit() } catch { Say "WARN: DTE.Quit failed: $($_.Exception.Message)" } }
        if (-not $process.WaitForExit(60000)) { Say "WARN: devenv did not exit in 60 s; ending it"; Stop-Process -Id $process.Id -Force }
        else { Say ("Visual Studio exited " + [Math]::Round($quit.Elapsed.TotalSeconds, 1) + " s after DTE.Quit") }
        Start-Sleep -Seconds 3
        $left = @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'widget-preview|tooling-daemon' -and $_.Name -ne 'powershell.exe' })
        Check ($left.Count -eq 0) "No previewer process is left after Visual Studio exited with a previewer running ($($left.Count))"
        $errors = @(Select-String -LiteralPath $activityLog -Pattern '<type>Error</type>' -Context 0, 4 | Where-Object { $_.Context.PostContext -match 'Vistudio|Dart|Flutter' })
        # Known, separate from the preview: Vistudio.Zig.ZigEnvironment sometimes fails to compose at startup (seen in 2 of 11 runs).
        $zig = @($errors | Where-Object { ($_.Context.PostContext -join ' ') -match 'Vistudio\.Zig\.' -and ($_.Context.PostContext -join ' ') -notmatch 'Dart|Flutter|Preview' })
        if ($zig.Count -gt 0) { Say "WARN: $($zig.Count) ActivityLog error(s) from Vistudio.Zig.ZigEnvironment composition (not the widget preview)" }
        Check (($errors.Count - $zig.Count) -eq 0) "No other Vistudio/Dart/Flutter errors in the ActivityLog ($($errors.Count - $zig.Count))"
    }
}
Say "PASS: $($script:passed) native Visual Studio checks. Evidence: $evidence"
