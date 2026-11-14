param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild/Current/Bin/MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Install Visual Studio with the extension development workload.' }
& $msbuild (Join-Path $PSScriptRoot 'src/Vistudio.Vsix/Vistudio.Vsix.csproj') /restore /p:Configuration=$Configuration /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'VSIX build failed.' }
if (-not $SkipTests) {
    & $msbuild (Join-Path $PSScriptRoot 'tests/Vistudio.Tests/Vistudio.Tests.csproj') /restore /p:Configuration=$Configuration /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & (Join-Path $PSScriptRoot "tests/Vistudio.Tests/bin/$Configuration/net472/Vistudio.Tests.exe")
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
$destination = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "src/Vistudio.Vsix/bin/$Configuration/net472/Vistudio.Vsix.vsix") -Destination (Join-Path $destination 'Vistudio.Kotlin.vsix')
if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'tests/verify-vsix.ps1') -Path (Join-Path $destination 'Vistudio.Kotlin.vsix')
    & powershell.exe -NoProfile -STA -File (Join-Path $PSScriptRoot 'tests/verify-icon-rendering.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Icon rendering tests failed.' }
}
Write-Output "VSIX: $destination/Vistudio.Kotlin.vsix"
