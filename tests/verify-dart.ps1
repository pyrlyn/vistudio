# Native Visual Studio acceptance for Dart and Flutter. Run with Windows PowerShell (STA, COM automation).
# Installs the VSIX into an isolated root suffix, opens copies of samples/dart-console and samples/flutter-app with
# Open Folder (the SDK comes from each copy's .vs\settings.json, so global settings stay untouched) and drives the
# Dart commands, editor, Error List and debugger through DTE.
param(
    [string]$Vsix = (Join-Path $PSScriptRoot '../src/Vistudio.Vsix/bin/Debug/net472/Vistudio.Vsix.vsix'),
    [string]$RootSuffix = 'VistudioDart',
    [string]$FlutterSdk = 'C:/Users/listepo/tools/flutter',
    [ValidateSet('all', 'console', 'flutter')][string]$Part = 'all',
    [switch]$SkipInstall,
    [switch]$KeepOpen
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$instance = & $vswhere -latest -format json | ConvertFrom-Json | Select-Object -First 1
$devenv = Join-Path $instance.installationPath 'Common7/IDE/devenv.exe'
$installer = Join-Path $instance.installationPath 'Common7/IDE/VSIXInstaller.exe'

# Flutter's Windows build and the tools' symlinks work on a local disk; the repository may be on a network share.
$evidence = Join-Path $env:TEMP ('vistudio-dart-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidence | Out-Null
$log = Join-Path $evidence 'acceptance.log'
$script:passed = 0
# Write-Host, not output: functions below return values, and log lines must not mix into them.
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

function Copy-Sample([string]$name, [string]$target) {
    $source = Join-Path $repo "samples/$name"
    New-Item -ItemType Directory -Path $target | Out-Null
    Get-ChildItem -LiteralPath $source -Force | Where-Object { $_.Name -notin '.dart_tool', 'build' } | Copy-Item -Destination $target -Recurse
    New-Item -ItemType Directory -Path (Join-Path $target '.vs') | Out-Null
    @{ 'vistudio.dart.flutterSdkPath' = $FlutterSdk; 'vistudio.dart.flutterDevice' = 'windows' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $target '.vs/settings.json') -Encoding UTF8
}
$console = Join-Path $evidence 'dart-console'
$flutter = Join-Path $evidence 'flutter-app'
Copy-Sample 'dart-console' $console
Copy-Sample 'flutter-app' $flutter
# A print in build() shows in the output which title the running code has: proof that a hot reload took effect.
$main = Join-Path $flutter 'lib/main.dart'
$text = [IO.File]::ReadAllText($main)
$newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" } # keep the file's line endings (VS asks about mixed ones)
[IO.File]::WriteAllText($main, $text.Replace('    return Scaffold(', "    print('build title=`${widget.title}');$newline    return Scaffold("), (New-Object Text.UTF8Encoding $false))
# Visual Studio offers CMake support for windows/CMakeLists.txt in a modal dialog; this is what answering 'Do not enable' stores.
'{ "enableCMake": false }' | Set-Content -LiteralPath (Join-Path $flutter '.vs/CMakeWorkspaceSettings.json') -Encoding ASCII
foreach ($folder in $console, $flutter) {
    $tool = if ($folder -eq $flutter) { Join-Path $FlutterSdk 'bin/flutter.bat' } else { Join-Path $FlutterSdk 'bin/cache/dart-sdk/bin/dart.exe' }
    Push-Location $folder; & $tool pub get | Out-Null; Pop-Location
    Check ($LASTEXITCODE -eq 0) "pub get in $(Split-Path -Leaf $folder)"
}
Say "Workspaces: $console, $flutter"

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
$helper = Join-Path $evidence 'DartAcceptance.dll'
& $csc /nologo /target:library /langversion:latest /nullable:disable "/out:$helper" "/reference:$interop" (Join-Path $PSScriptRoot 'DartAcceptance.cs') | ForEach-Object { Say $_ }
Check ($LASTEXITCODE -eq 0) 'Acceptance helper compiled'
Add-Type -Path $helper

function Start-Ide([string]$folder, [string]$name) {
    $activityLog = Join-Path $evidence "ActivityLog-$name.xml"
    $process = Start-Process -FilePath $devenv -ArgumentList @('/RootSuffix', $RootSuffix, "`"$folder`"", '/Log', "`"$activityLog`"") -PassThru
    $dte = $null
    for ($i = 0; $i -lt 240 -and -not $dte; $i++) { Start-Sleep -Seconds 1; $dte = [DartRot]::FindDte($process.Id) }
    Check ($dte -ne $null) "Automation object of devenv $($process.Id) is available ($name)"
    return @($dte, $process, $activityLog)
}

function Finish-Ide($dte, $process, [string]$activityLog) {
    if ($KeepOpen) { return }
    # Unsaved documents (after a failed step) would stop Quit with a save prompt.
    try { $dte.Documents.CloseAll(2) } catch { }
    try { $dte.Quit() } catch { }
    if (-not $process.WaitForExit(60000)) { Say "WARN: devenv did not exit in 60 s; ending it"; Stop-Process -Id $process.Id -Force }
    Check ($true) "Visual Studio exited ($([IO.Path]::GetFileName($activityLog)))"
    $errors = Select-String -LiteralPath $activityLog -Pattern '<type>Error</type>' -Context 0, 4 | Where-Object { $_.Context.PostContext -match 'Vistudio|Dart' }
    Check (@($errors).Count -eq 0) "No Vistudio/Dart errors in the ActivityLog ($(@($errors).Count))"
}

if ($Part -in 'all', 'console') {
    $dte, $process, $activityLog = Start-Ide $console 'console'
    try {
        $acceptance = New-Object DartAcceptance $dte, ([Action[string]]{ param($line) Say $line })
        $count = $acceptance.RunConsole($console)
        $script:passed += $count
    }
    finally { Finish-Ide $dte $process $activityLog }
}
if ($Part -in 'all', 'flutter') {
    $dte, $process, $activityLog = Start-Ide $flutter 'flutter'
    try {
        $acceptance = New-Object DartAcceptance $dte, ([Action[string]]{ param($line) Say $line })
        $count = $acceptance.RunFlutter($flutter)
        $script:passed += $count
    }
    finally { Finish-Ide $dte $process $activityLog }
}
Say "PASS: $($script:passed) native Visual Studio checks. Evidence: $evidence"
