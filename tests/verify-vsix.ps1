param([Parameter(Mandatory = $true)][string]$Path)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path))
function Read-Entry([string]$name) {
    $entry = $archive.GetEntry($name)
    if (-not $entry) { throw "Missing VSIX asset: $name" }
    $reader = [System.IO.StreamReader]::new($entry.Open())
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}
try {
    $packageRegistration = Read-Entry 'Vistudio.Vsix.pkgdef'
    if ($packageRegistration.Contains('ef1bc27e-3609-4f20-afc1-126c583a762d')) { throw 'The removed Kotlin Project Explorer tool window is still registered.' }
    if (-not $packageRegistration.Contains('[$RootKey$\Projects\{e7447d71-1121-42dc-8a4d-d3d78b3cc633}]') -or
        -not $packageRegistration.Contains('"PossibleProjectExtensions"="kotlinproj"')) { throw 'Missing .kotlinproj project factory registration.' }
    $registration = Read-Entry 'KotlinFiles.pkgdef'
    foreach ($extension in @('kt', 'kts', 'properties')) {
        if (-not $registration.Contains(('"{0}"=dword:00000064' -f $extension))) { throw "Missing default editor association for $extension" }
    }
    $manifest = [xml](Read-Entry 'extension.vsixmanifest')
    if ('KotlinFiles.pkgdef' -notin $manifest.PackageManifest.Assets.Asset.Path) { throw 'File registrations are not installed by the VSIX manifest.' }
    # The image service limits discovery depth; manifests must ship at the extension root.
    $images = [xml](Read-Entry 'Kotlin.imagemanifest')
    # Visual Studio ships no .properties grammar; ours must sit in the registered TextMate repository and parse.
    $grammar = (Read-Entry 'Grammars/Properties.tmLanguage.json') | ConvertFrom-Json
    if ('properties' -notin $grammar.fileTypes) { throw 'The .properties grammar does not claim the properties extension.' }
    $null = Read-Entry 'Grammars/Kotlin.tmLanguage.json'
    $null = Read-Entry 'Build/DiscoverTasks.gradle'
    $null = Read-Entry 'Build/ImportModel.gradle'
    $entry = $archive.GetEntry('Vistudio.Vsix.dll')
    $stream = $entry.Open(); $bytes = [System.IO.MemoryStream]::new()
    try { $stream.CopyTo($bytes); $assembly = [System.Reflection.Assembly]::Load($bytes.ToArray()) }
    finally { $stream.Dispose(); $bytes.Dispose() }
    $resourceName = $assembly.GetManifestResourceNames() | Where-Object { $_ -like '*.g.resources' } | Select-Object -First 1
    if (-not $resourceName) { throw 'WPF image resources are absent from the assembly.' }
    $resources = [System.Resources.ResourceReader]::new($assembly.GetManifestResourceStream($resourceName))
    try {
        $entries = @{}
        $iterator = $resources.GetEnumerator()
        while ($iterator.MoveNext()) { $entries[$iterator.Key] = $iterator.Value }
        foreach ($image in $images.ImageManifest.Images.Image) {
            $key = ($image.Source.Uri -split ';component/')[1].ToLowerInvariant()
            if (-not $entries.ContainsKey($key)) { throw "Unresolvable image resource: $key" }
            $reader = [System.IO.StreamReader]::new($entries[$key])
            # VS Image Service accepts UIElement or BitmapSource, not a bare DrawingImage.
            try { $drawing = [xml]$reader.ReadToEnd(); if ($drawing.DocumentElement.LocalName -ne 'Image') { throw "Invalid image service root: $key" } }
            finally { $reader.Dispose() }
            $moniker = $image.Guid.Trim('{}') + ':' + $image.ID
            if (-not $registration.Contains($moniker)) { throw "Unmapped image moniker: $moniker" }
        }
    }
    finally { $resources.Dispose() }
    if ($archive.Entries.FullName | Where-Object { $_ -match '^Microsoft\.VisualStudio\..*\.dll$' }) { throw 'Host SDK assemblies must not ship inside the VSIX.' }
    Write-Output 'PASS: packaged file associations, .properties grammar, vector icon resources, task discovery and host dependency isolation.'
}
finally { $archive.Dispose() }
