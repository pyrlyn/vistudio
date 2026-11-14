param([switch]$UseWindowsTrustStore, [ValidateSet('Debug','Release')][string]$Configuration = 'Release')
# Imports samples/kotlin-multiplatform through the production importer, checks targets, .kotlinproj and tree,
# then builds and runs jvm, js, wasmJs and mingwX64 with their imported Gradle tasks.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$runner = Join-Path $PSScriptRoot "Vistudio.Tests/bin/$Configuration/net472/Vistudio.Tests.exe"
if (-not (Test-Path -LiteralPath $runner)) { throw 'Build the test project first (./build.ps1).' }
$previousJavaOptions = $env:JAVA_TOOL_OPTIONS
$previousNodeSystemCa = $env:NODE_USE_SYSTEM_CA
$previousNoDefault = $env:NoDefaultCurrentDirectoryInExePath
try {
    # gradlew.bat must be resolvable from its own directory.
    Remove-Item Env:NoDefaultCurrentDirectoryInExePath -ErrorAction SilentlyContinue
    if ($UseWindowsTrustStore) {
        $env:JAVA_TOOL_OPTIONS = "$previousJavaOptions -Djavax.net.ssl.trustStoreType=Windows-ROOT -Djavax.net.ssl.trustStore=NONE".Trim()
        $env:NODE_USE_SYSTEM_CA = '1'
    }
    & $runner --kotlin-targets (Join-Path $projectRoot 'samples/kotlin-multiplatform') (Join-Path $projectRoot 'src/Vistudio.Vsix/Build/ImportModel.gradle') (Join-Path $projectRoot 'artifacts/kotlin-targets')
    if ($LASTEXITCODE -ne 0) { throw 'Kotlin target integration tests failed. See artifacts/kotlin-targets.' }
}
finally {
    $env:JAVA_TOOL_OPTIONS = $previousJavaOptions
    $env:NODE_USE_SYSTEM_CA = $previousNodeSystemCa
    if ($previousNoDefault) { $env:NoDefaultCurrentDirectoryInExePath = $previousNoDefault }
}
