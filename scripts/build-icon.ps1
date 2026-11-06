param(
    [string]$Source = (Join-Path $PSScriptRoot '../src/Reflection.Desktop/Assets/Reflection.png'),
    [string]$Output = (Join-Path $PSScriptRoot '../src/Reflection.Desktop/Assets/Reflection.ico')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$bitmap = New-Object System.Windows.Media.Imaging.BitmapImage
$bitmap.BeginInit()
$bitmap.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
$bitmap.UriSource = [Uri](Resolve-Path -LiteralPath $Source).Path
$bitmap.EndInit()
$bitmap.Freeze()
if ($bitmap.PixelWidth -ne $bitmap.PixelHeight) { throw 'The icon master must be square.' }
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$frames = @(foreach ($size in $sizes) {
    $scale = New-Object System.Windows.Media.ScaleTransform ($size / $bitmap.PixelWidth), ($size / $bitmap.PixelHeight)
    $resized = New-Object System.Windows.Media.Imaging.TransformedBitmap $bitmap, $scale
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($resized))
    $stream = New-Object System.IO.MemoryStream
    try { $encoder.Save($stream); ,$stream.ToArray() } finally { $stream.Dispose() }
})
$target = [System.IO.Path]::GetFullPath($Output)
$file = [IO.File]::Create($target)
$writer = New-Object System.IO.BinaryWriter $file
try {
    $writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = $sizes[$i] % 256
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([UInt16]1); $writer.Write([UInt16]32)
        $writer.Write([UInt32]$frames[$i].Length); $writer.Write([UInt32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose(); $file.Dispose() }
Write-Output "Created $target (10 sizes, 16-256 px)"
