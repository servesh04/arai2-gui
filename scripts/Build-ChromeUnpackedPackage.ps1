[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'

$extensionRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'extension'
$package = [IO.Path]::GetFullPath($OutputPath)
if ([IO.Path]::GetExtension($package) -ne '.zip' -or (Test-Path -LiteralPath $package)) {
    throw 'Choose a new .zip output path for the unpacked Chrome extension.'
}
$parent = [IO.Path]::GetDirectoryName($package)
if (!(Test-Path -LiteralPath $parent -PathType Container)) { throw 'The output directory must already exist.' }

$files = @('manifest.json', 'background.js', 'clicks.js', 'popup.html', 'popup.js',
    'icons/16.png', 'icons/32.png', 'icons/48.png', 'icons/128.png')
$entries = @('INSTALL.txt') + @($files | ForEach-Object { 'AriaGUI-Chrome-Extension/' + $_ })
foreach ($name in $files + @('INSTALL-unpacked.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $extensionRoot $name) -PathType Leaf)) {
        throw "Missing extension source: $name"
    }
}

try {
    $stream = [IO.File]::Open($package, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($entryName in $entries) {
                $sourceName = if ($entryName -eq 'INSTALL.txt') { 'INSTALL-unpacked.txt' }
                    else { $entryName.Substring('AriaGUI-Chrome-Extension/'.Length) }
                $entry = $archive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $target = $entry.Open()
                try {
                    $source = [IO.File]::OpenRead((Join-Path $extensionRoot $sourceName))
                    try { $source.CopyTo($target) } finally { $source.Dispose() }
                } finally { $target.Dispose() }
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
    & (Join-Path $PSScriptRoot 'Test-ChromeUnpackedPackage.ps1') -PackagePath $package | Out-Null
    Write-Output $package
} catch {
    if (Test-Path -LiteralPath $package -PathType Leaf) { Remove-Item -LiteralPath $package -Force }
    throw
}
