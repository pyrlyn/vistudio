param([string]$JdkHome = 'C:/Program Files/Microsoft/jdk-11.0.31.11-hotspot')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$workspace = Join-Path $repo ('artifacts/gradle-import-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workspace | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot 'fixtures/gradle-import/*') -Destination $workspace -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'samples/kotlin-jvm/gradlew.bat') -Destination $workspace
New-Item -ItemType Directory -Path (Join-Path $workspace 'gradle') | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'samples/kotlin-jvm/gradle/wrapper') -Destination (Join-Path $workspace 'gradle') -Recurse
$log = Join-Path $workspace 'integration.log'
& (Join-Path $repo 'tests/Vistudio.Tests/bin/Release/net472/Vistudio.Tests.exe') --gradle-import $workspace $JdkHome (Join-Path $repo 'src/Vistudio.Vsix/Build/ImportModel.gradle') 2>&1 | Tee-Object -FilePath $log
if ($LASTEXITCODE -ne 0) { throw "Gradle import integration failed. See $log" }
Write-Output "UI acceptance workspace: $workspace"
