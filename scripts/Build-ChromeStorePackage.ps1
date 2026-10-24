[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'

$extensionRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'extension'
$package = [IO.Path]::GetFullPath($OutputPath)
if ([IO.Path]::GetExtension($package) -ne '.zip' -or (Test-Path -LiteralPath $package)) {
    throw 'Choose a new .zip output path for the Chrome Web Store upload.'
}
$parent = [IO.Path]::GetDirectoryName($package)
if (!(Test-Path -LiteralPath $parent -PathType Container)) { throw 'The output directory must already exist.' }

$files = @('manifest.json', 'background.js', 'clicks.js', 'popup.html', 'popup.js',
    'icons/16.png', 'icons/32.png', 'icons/48.png', 'icons/128.png')
foreach ($name in $files) {
    if (!(Test-Path -LiteralPath (Join-Path $extensionRoot $name) -PathType Leaf)) {
        throw "Missing extension source: $name"
    }
}
$manifest = Get-Content -LiteralPath (Join-Path $extensionRoot 'manifest.json') -Raw | ConvertFrom-Json -AsHashtable
if ($manifest.manifest_version -ne 3 -or !$manifest.key) { throw 'Expected the pinned development manifest.' }
$manifest.Remove('key')
$manifestBytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 20) + "`n")

try {
    $stream = [IO.File]::Open($package, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($name in $files) {
                $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $target = $entry.Open()
                try {
                    if ($name -eq 'manifest.json') { $target.Write($manifestBytes) }
                    else {
                        $source = [IO.File]::OpenRead((Join-Path $extensionRoot $name))
                        try { $source.CopyTo($target) } finally { $source.Dispose() }
                    }
                } finally { $target.Dispose() }
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
    & (Join-Path $PSScriptRoot 'Test-ChromeStorePackage.ps1') -PackagePath $package | Out-Null
    Write-Output $package
} catch {
    if (Test-Path -LiteralPath $package -PathType Leaf) { Remove-Item -LiteralPath $package -Force }
    throw
}
