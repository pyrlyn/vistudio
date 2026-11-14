param([switch]$UseWindowsTrustStore)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$runner = Join-Path $PSScriptRoot 'Vistudio.Tests/bin/Release/net472/Vistudio.Tests.exe'
if (-not (Test-Path -LiteralPath $runner)) { throw 'Run ./build.ps1 first.' }
$previousJavaOptions = $env:JAVA_TOOL_OPTIONS
$previousNodeSystemCa = $env:NODE_USE_SYSTEM_CA
try {
    if ($UseWindowsTrustStore) {
        $env:JAVA_TOOL_OPTIONS = "$previousJavaOptions -Djavax.net.ssl.trustStoreType=Windows-ROOT -Djavax.net.ssl.trustStore=NONE".Trim()
        $env:NODE_USE_SYSTEM_CA = '1'
    }
    & $runner --multiplatform (Join-Path $projectRoot 'samples/kotlin-multiplatform') (Join-Path $projectRoot 'src/Vistudio.Vsix/Build/DiscoverTasks.gradle') (Join-Path $projectRoot 'artifacts/multiplatform-tests')
    if ($LASTEXITCODE -ne 0) { throw 'Multiplatform integration tests failed. See artifacts/multiplatform-tests.' }
}
finally { $env:JAVA_TOOL_OPTIONS = $previousJavaOptions; $env:NODE_USE_SYSTEM_CA = $previousNodeSystemCa }
