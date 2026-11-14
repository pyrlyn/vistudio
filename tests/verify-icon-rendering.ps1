param([string]$IconDirectory = (Join-Path $PSScriptRoot '../src/Vistudio.Vsix/Icons'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
$names = Get-ChildItem $IconDirectory -Filter *.xaml | ForEach-Object BaseName
if ($names.Count -lt 12) { throw "Expected at least 12 icons, found $($names.Count)." }
foreach ($name in $names) {
    $element = [Windows.Markup.XamlReader]::Parse((Get-Content (Join-Path $IconDirectory "$name.xaml") -Raw))
    # The VS Image Service rejects DrawingImage roots: XAML must produce a UIElement or BitmapSource.
    if ($element -isnot [Windows.UIElement]) { throw "$name is not an image-service UIElement." }
    foreach ($size in @(16, 32)) {
        $element.Measure([Windows.Size]::new(16, 16))
        $element.Arrange([Windows.Rect]::new(0, 0, 16, 16))
        $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96 * $size / 16, 96 * $size / 16, [Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($element)
        $pixels = [byte[]]::new($size * $size * 4)
        $bitmap.CopyPixels($pixels, $size * 4, 0)
        $opaque = 0
        for ($i = 3; $i -lt $pixels.Length; $i += 4) { if ($pixels[$i] -gt 0) { $opaque++ } }
        if ($opaque -lt $size) { throw "$name rendered blank at $size pixels." }
    }
}
Write-Output 'PASS: image-service root contract and nonblank vector rendering at 100% and 200% DPI.'
