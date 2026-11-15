# Native Visual Studio acceptance for the language project types and the Packages nodes (T59, T60). Run with Windows
# PowerShell (STA, COM automation). Installs the VSIX into an isolated root suffix, creates Dart and C++ projects from the
# New Project templates in a local temp folder, and drives Solution Explorer, the package commands, solution Build and F5
# through DTE only. The tools come from PATH of the started Visual Studio: the Flutter SDK's dart for Dart; cmake, conan and
# MSVC for C++. Conan runs with its own CONAN_HOME under the evidence folder, so the user's Conan configuration is untouched.
param(
    [string]$Vsix = (Join-Path $PSScriptRoot '../src/Vistudio.Vsix/bin/Debug/net472/Vistudio.Vsix.vsix'),
    [string]$RootSuffix = 'VistudioLanguages',
    [string]$FlutterSdk = 'C:/Users/listepo/tools/flutter',
    [ValidateSet('all', 'dart', 'cpp', 'c', 'java', 'missing-tools')][string[]]$Part = @('all'),
    [switch]$SkipInstall,
    [switch]$KeepOpen
)
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$instance = & $vswhere -latest -format json | ConvertFrom-Json | Select-Object -First 1
$devenv = Join-Path $instance.installationPath 'Common7/IDE/devenv.exe'
$installer = Join-Path $instance.installationPath 'Common7/IDE/VSIXInstaller.exe'

$evidence = Join-Path $env:TEMP ('vistudio-languages-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidence | Out-Null
$log = Join-Path $evidence 'acceptance.log'
$script:passed = 0
function Want([string]$name) { $Part -contains 'all' -or $Part -contains $name }
function Say([string]$text) { [IO.File]::AppendAllText($log, $text + [Environment]::NewLine); Write-Host $text }
function Check([bool]$condition, [string]$message) {
    if (-not $condition) { Say "FAIL: $message"; throw $message }
    $script:passed++; Say "PASS: $message"
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class LanguagesRot {
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
public interface ILanguagesOleMessageFilter {
    [PreserveSig] int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo);
    [PreserveSig] int RetryRejectedCall(IntPtr callee, int tickCount, int rejectType);
    [PreserveSig] int MessagePending(IntPtr callee, int tickCount, int pendingType);
}
public sealed class LanguagesMessageFilter : ILanguagesOleMessageFilter {
    [DllImport("ole32.dll")] private static extern int CoRegisterMessageFilter(ILanguagesOleMessageFilter filter, out ILanguagesOleMessageFilter previous);
    public static void Register() { ILanguagesOleMessageFilter previous; CoRegisterMessageFilter(new LanguagesMessageFilter(), out previous); }
    public int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo) { return 0; }
    public int RetryRejectedCall(IntPtr callee, int tickCount, int rejectType) { return tickCount < 120000 ? 250 : -1; }
    public int MessagePending(IntPtr callee, int tickCount, int pendingType) { return 2; }
}
'@
[LanguagesMessageFilter]::Register()

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

# The started Visual Studio inherits this environment: the tools a user would have on PATH.
$env:PATH = (Join-Path $FlutterSdk 'bin/cache/dart-sdk/bin') + ';' + (Join-Path $FlutterSdk 'bin') + ';' + $env:PATH
$env:CONAN_HOME = Join-Path $evidence 'conan-home'
# Through cmd.exe: Windows PowerShell turns the progress Conan writes to stderr into errors under ErrorActionPreference Stop.
cmd.exe /d /c "conan profile detect >nul 2>&1"
Check ($LASTEXITCODE -eq 0) 'Conan default profile detected in the private CONAN_HOME'

$activityLog = Join-Path $evidence 'ActivityLog.xml'
# Started without a file, Visual Studio waits in its start window and registers no automation object; an empty
# solution opens straight into the IDE.
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
$process = Start-Process -FilePath $devenv -ArgumentList @('/RootSuffix', $RootSuffix, "`"$solutionFile`"", '/Log', "`"$activityLog`"") -PassThru
$dte = $null
for ($i = 0; $i -lt 240 -and -not $dte; $i++) { Start-Sleep -Seconds 1; $dte = [LanguagesRot]::FindDte($process.Id) }
if (-not $dte) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
Check ($dte -ne $null) "Automation object of devenv $($process.Id) is available"
try {
    $a = New-Object LanguageProjectsAcceptance $dte, ([Action[string]]{ param($line) Say $line })

    if (Want 'dart') {
        $file = $a.Create('DartConsoleApp', $solutionFolder, 'DartApp', '.dartproj')
        $project = $a.ProjectItem('DartApp')
        $packages = $a.WaitChild($project, { param($n) $n.StartsWith('Packages (pub)') }, 30, 'Dart project shows its pub Packages node first')
        $children = $a.Children($project)
        Check ($children -contains 'bin' -and $children -contains 'lib' -and $children -contains 'pubspec.yaml') ('Dart project lists its folder: ' + ($children -join ', '))
        $null = $a.WaitChild($packages, { param($n) $n.StartsWith('test ') }, 120, 'pub dev dependency test is listed with a version')
        # An old pin makes pub report an outdated package; the watcher picks up the manifest edit.
        $pubspec = Join-Path (Split-Path $file) 'pubspec.yaml'
        [IO.File]::AppendAllText($pubspec, "`r`ndependencies:`r`n  path: 1.8.0`r`n")
        Push-Location (Split-Path $file); cmd.exe /d /c "dart pub get >nul 2>&1"; Pop-Location
        Check ($LASTEXITCODE -eq 0) 'dart pub get with path 1.8.0'
        $old = $a.WaitChild($packages, { param($n) $n.StartsWith('path 1.8.0') }, 60, 'The new dependency appears after the manifest edit')
        try { $a.Execute($project, 'Vistudio.TestLanguageProject') } catch { Say "Run Tests on the project node: $($_.Exception.Message)" }
        # The background version check can rename the node ("... – N outdated") at any time, which drops earlier DTE items.
        $packages = $a.WaitChild($project, { param($n) $n.StartsWith('Packages (pub)') }, 30, 'Packages (pub) node before Check for Updates')
        $a.Execute($packages, 'Vistudio.CheckPackageUpdates')
        $old = $a.WaitNested($project, 'Packages (pub)', { param($n) $n -match '^path 1\.8\.0 \u2192 \d' }, 180, 'Check for Updates marks path 1.8.0 as outdated with the newer version')
        $packages = $a.WaitChild($project, { param($n) $n -match '^Packages \(pub\) \u2013 \d+ outdated' }, 30, 'The Packages node counts outdated packages')
        $a.Execute($old, 'Vistudio.UpdatePackageToLatest')
        $null = $a.WaitNested($project, 'Packages (pub)', { param($n) $n -match '^path 1\.(9|[1-9]\d)\.' -and $n -notmatch '\u2192' }, 240, 'Update to Latest moved path to the newest version')
        Check ((Get-Content $pubspec -Raw) -notmatch 'path: 1\.8\.0') 'pubspec.yaml no longer pins path 1.8.0'
        Say ('Packages pane:' + [Environment]::NewLine + $a.Pane('Packages'))
        Check ($a.Build(300)) 'Solution build runs dart analyze for the Dart project and succeeds'
    }

    if (Want 'cpp') {
        $file = $a.Create('CppConsoleApp', $solutionFolder, 'CppApp', '.cppproj')
        $project = $a.ProjectItem('CppApp')
        $packages = $a.WaitChild($project, { param($n) $n.StartsWith('Packages (Conan)') }, 30, 'C++ project shows its Conan Packages node')
        $conanfile = Join-Path (Split-Path $file) 'conanfile.txt'
        $text = [IO.File]::ReadAllText($conanfile).Replace("[requires]`r`n", "[requires]`r`nfmt/10.2.1`r`n")
        [IO.File]::WriteAllText($conanfile, $text)
        $fmt = $a.WaitChild($packages, { param($n) $n.StartsWith('fmt ') }, 60, 'fmt appears after the conanfile edit')
        $packages = $a.WaitChild($project, { param($n) $n.StartsWith('Packages (Conan)') }, 30, 'Packages (Conan) node before Check for Updates')
        $a.Execute($packages, 'Vistudio.CheckPackageUpdates')
        $fmt = $a.WaitNested($project, 'Packages (Conan)', { param($n) $n -match '^fmt 10\.2\.1 \u2192 \d' }, 180, 'Conan reports a newer fmt')
        $a.SetStartup('CppApp')
        Check ($a.Build(900)) 'Solution build runs conan install, CMake configure and build for the C++ project'
        $main = Join-Path (Split-Path $file) 'src/main.cpp'
        $function = $a.DebugToBreakpoint($main, 6, 180)
        Check ($function -match 'main') "F5 debugs the CMake program in the native engine (stopped in $function)"
    }

    if (Want 'c') {
        $file = $a.Create('CConsoleApp', $solutionFolder, 'CApp', '.cproj')
        $project = $a.ProjectItem('CApp')
        $null = $a.WaitChild($project, { param($n) $n.StartsWith('Packages (Conan)') }, 30, 'C project shows its Conan Packages node')
        $children = $a.Children($project)
        Check ($children -contains 'src' -and $children -contains 'CMakeLists.txt' -and $children -contains 'conanfile.txt') ('C project lists its folder: ' + ($children -join ', '))
        $a.SetStartup('CApp')
        Check ($a.Build(900)) 'Solution build runs conan install, CMake configure and build for the C project'
        $main = Join-Path (Split-Path $file) 'src/main.c'
        $function = $a.DebugToBreakpoint($main, 6, 180)
        Check ($function -match 'main') "F5 debugs the C program in the native engine (stopped in $function)"
    }

    if (Want 'java') {
        $file = $a.Create('JavaConsoleApp', $solutionFolder, 'JavaApp', '.javaproj')
        $folder = Split-Path $file
        $project = $a.ProjectItem('JavaApp')
        $packages = $a.WaitChild($project, { param($n) $n.StartsWith('Packages (Gradle)') }, 60, 'Java project shows its Gradle Packages node')
        $children = $a.Children($project)
        Check ($children -contains 'src' -and $children -contains 'build.gradle.kts' -and $children -contains 'gradlew.bat') ('Java project lists its folder: ' + ($children -join ', '))
        $gradleFile = Join-Path $folder 'build.gradle.kts'
        $text = [IO.File]::ReadAllText($gradleFile).Replace("dependencies {`r`n}", "dependencies {`r`n    implementation(`"com.google.code.gson:gson:2.8.0`")`r`n}")
        [IO.File]::WriteAllText($gradleFile, $text)
        Check ($text.Contains('gson:2.8.0')) 'gson 2.8.0 added to build.gradle.kts'
        $null = $a.WaitNested($project, 'Packages (Gradle)', { param($n) $n.StartsWith('com.google.code.gson:gson') -or $n.StartsWith('gson') }, 90, 'gson appears after the build file edit')
        $packages = $a.WaitChild($project, { param($n) $n.StartsWith('Packages (Gradle)') }, 10, 'Gradle Packages node after the edit')
        $packages = $a.WaitChild($project, { param($n) $n.StartsWith('Packages (Gradle)') }, 30, 'Packages (Gradle) node before Check for Updates')
        $a.Execute($packages, 'Vistudio.CheckPackageUpdates')
        $null = $a.WaitNested($project, 'Packages (Gradle)', { param($n) $n -match 'gson 2\.8\.0 \u2192 \d' }, 300, 'Gradle reports a newer gson')
        Check ($a.Build(600)) 'Solution build runs the Gradle wrapper for the Java project and succeeds'
        Check (Test-Path (Join-Path $folder 'build/libs/JavaApp.jar')) 'Gradle build produced build/libs/JavaApp.jar'
    }

    if (Want 'missing-tools') {
        # Rust, Zig and WebAssembly projects load without their tools; a build must fail with a message that names the
        # missing tool instead of hanging or failing silently. Skipped per language when the tool is on PATH.
        foreach ($case in @(
                @{ Template = 'RustConsoleApp'; Name = 'RustApp'; Extension = '.rustproj'; Tool = 'cargo'; Files = @('src', 'Cargo.toml'); Packages = 'Packages (Cargo)' },
                @{ Template = 'ZigConsoleApp'; Name = 'ZigApp'; Extension = '.zigproj'; Tool = 'zig'; Files = @('src', 'build.zig'); Packages = $null },
                @{ Template = 'WasmModule'; Name = 'WasmApp'; Extension = '.wasmproj'; Tool = 'wasm-tools'; Files = @('src'); Packages = $null })) {
            $null = $a.Create($case.Template, $solutionFolder, $case.Name, $case.Extension)
            $project = $a.ProjectItem($case.Name)
            if ($case.Packages) { $null = $a.WaitChild($project, { param($n) $n.StartsWith($case.Packages) }, 30, "$($case.Name) shows $($case.Packages)") }
            $children = $a.Children($project)
            Check (@($case.Files | Where-Object { $children -notcontains $_ }).Count -eq 0) ("$($case.Name) lists its folder: " + ($children -join ', '))
            if (Get-Command $case.Tool -ErrorAction SilentlyContinue) { Say "SKIP: $($case.Tool) is installed; the missing-tool build is not checked"; continue }
            Check (-not $a.BuildProject($case.Name, 120)) "$($case.Name) build fails without $($case.Tool)"
            $pane = $a.Pane('Build') + $a.Pane('Vistudio Projects')
            Check ($pane -match [regex]::Escape($case.Tool)) "$($case.Name) build without $($case.Tool) names the missing tool"
            $a.Unload($case.Name)
        }
    }
}
finally {
    try { foreach ($pane in 'Vistudio Projects', 'Packages') { Say ("--- $pane pane:" + [Environment]::NewLine + $a.Pane($pane)) } } catch { }
    if (-not $KeepOpen) {
        try { $dte.Documents.CloseAll(2) } catch { }
        # Added or removed projects make the solution dirty; its save prompt would hold DTE.Quit.
        try { $dte.Solution.Close($false) } catch { }
        try { $dte.Quit() } catch { }
        if (-not $process.WaitForExit(60000)) { Say 'WARN: devenv did not exit in 60 s; ending it'; Stop-Process -Id $process.Id -Force }
        $errors = Select-String -LiteralPath $activityLog -Pattern '<type>Error</type>' -Context 0, 4 | Where-Object { $_.Context.PostContext -match 'Vistudio' }
        Say ("ActivityLog Vistudio errors: " + @($errors).Count)
        $errors | ForEach-Object { Say $_.ToString() }
    }
}
Say "PASS: $($script:passed + $a.Passed) native Visual Studio checks. Evidence: $evidence"
