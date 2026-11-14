# Real ETW session check for T43: traces both runtime providers with logman while the test executable
# emits every event, decodes the trace with tracerpt (TDH), and checks that event and field names were
# decoded from the self-describing metadata. Needs elevation or membership in Performance Log Users.
param(
    [string]$Exe = (Join-Path $PSScriptRoot 'Vistudio.Tests\bin\Debug\net472\Vistudio.Tests.exe'),
    [int]$Seconds = 2
)
$ErrorActionPreference = 'Stop'
$native = '{4f3c063f-5474-59d2-14b2-3d8687b788b9}'
$managed = '{e74c385b-8523-59ba-af56-0e6de7ae32b6}'
$session = 'VistudioEtwCheck'
$work = Join-Path ([IO.Path]::GetTempPath()) ('vistudio-etw-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
$etl = Join-Path $work 'runtime.etl'
$providers = Join-Path $work 'providers.txt'
Set-Content $providers -Encoding ascii -Value @("$native 0xFFFFFFFFFFFFFFFF 0x5", "$managed 0xFFFFFFFFFFFFFFFF 0x5")

logman stop $session -ets *> $null
logman start $session -pf $providers -o $etl -ets | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host 'SKIP: cannot start an ETW session (run elevated or as a Performance Log Users member).'; exit 0 }
try { & $Exe --etw-emit $Seconds; $emit = $LASTEXITCODE } finally { logman stop $session -ets | Out-Null }
if ($emit -ne 0) { Write-Host "FAIL: emitter exited with $emit"; exit 1 }

$xml = Join-Path $work 'events.xml'
tracerpt $etl -o $xml -of XML -y | Out-Null
$text = Get-Content $xml -Raw
$failures = @()
foreach ($guid in $native, $managed) {
    $count = ([regex]::Matches($text, [regex]::Escape($guid), 'IgnoreCase')).Count
    if ($count -lt 4) { $failures += "provider $guid has $count events" }
}
foreach ($field in 'SessionId', 'RuntimeName', 'RuntimeVersion', 'ExitCode', 'ThreadId', 'Frames', 'Address', 'Size', 'TypeName') {
    if ($text -notmatch "Name=`"$field`"") { $failures += "field $field was not decoded" }
}
foreach ($event in 'SessionStart', 'SessionStop', 'StackSample', 'Allocation') {
    if ($text -notmatch $event) { $failures += "event $event was not decoded" }
}
if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Host "FAIL: $_" }; Write-Host "Trace kept in $work"; exit 1 }
Remove-Item $work -Recurse -Force
Write-Host 'PASS: both providers traced; event and field names decoded by TDH without a manifest.'
