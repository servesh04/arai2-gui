$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$vendorRoot = Join-Path $projectRoot 'vendor/aria2'
$provenance = Get-Content -LiteralPath (Join-Path $vendorRoot 'provenance.json') -Raw | ConvertFrom-Json
$acquisitionRoot = Join-Path $projectRoot ('.tools/aria2-acquisition/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $acquisitionRoot -Force | Out-Null
try {
    $archivePath = Join-Path $acquisitionRoot $provenance.assetName
    Invoke-WebRequest -Uri $provenance.assetUrl -OutFile $archivePath
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $provenance.archiveSha256) {
        throw 'aria2 release archive checksum does not match the reviewed pin.'
    }
    $extractedPath = Join-Path $acquisitionRoot 'extracted'
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractedPath
    $releaseRoot = Join-Path $extractedPath 'aria2-1.37.0-win-64bit-build1'
    $executablePath = Join-Path $releaseRoot 'aria2c.exe'
    if ((Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash -ne $provenance.executableSha256) {
        throw 'aria2 executable checksum does not match the reviewed pin.'
    }
    foreach ($notice in $provenance.notices) {
        if (!(Test-Path -LiteralPath (Join-Path $releaseRoot $notice) -PathType Leaf)) { throw "Missing release notice: $notice" }
    }
    New-Item -ItemType Directory -Path (Join-Path $vendorRoot 'win-x64') -Force | Out-Null
    Copy-Item -LiteralPath $executablePath -Destination (Join-Path $vendorRoot 'win-x64/aria2c.exe')
    foreach ($notice in $provenance.notices) {
        Copy-Item -LiteralPath (Join-Path $releaseRoot $notice) -Destination (Join-Path $vendorRoot $notice)
    }
    Write-Host "Verified aria2 $($provenance.version) for Windows x64."
}
finally {
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '.tools/aria2-acquisition'))
    $resolvedAcquisitionRoot = [IO.Path]::GetFullPath($acquisitionRoot)
    if (!$resolvedAcquisitionRoot.StartsWith($allowedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing cleanup outside the aria2 acquisition folder.'
    }
    Remove-Item -LiteralPath $resolvedAcquisitionRoot -Recurse -Force
}
