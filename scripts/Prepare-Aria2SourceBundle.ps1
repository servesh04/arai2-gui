[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory, [string]$NoticeDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
$allowed = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/packages')) + [IO.Path]::DirectorySeparatorChar
if (!$output.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Aria2 source output must stay inside workspace package artifacts.'
}
if (Test-Path -LiteralPath $output) { throw 'Use a fresh aria2 source directory.' }
$provenance = Get-Content -LiteralPath (Join-Path $projectRoot 'vendor/aria2/provenance.json') -Raw | ConvertFrom-Json
if (@($provenance.sourceArchives).Count -ne 7 -or @($provenance.buildFiles).Count -ne 2) {
    throw 'Pinned aria2 source inputs are incomplete.'
}
$null = New-Item -ItemType Directory -Path (Join-Path $output 'archives') -Force
$null = New-Item -ItemType Directory -Path (Join-Path $output 'build') -Force
$client = [Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromMinutes(3)
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Download-PinnedSource($item, [string]$directory, [switch]$Archive) {
    if ($item.name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]+$' -or !$seen.Add($item.name)) {
        throw "Invalid or duplicate source name: $($item.name)"
    }
    $uri = [uri]$item.url
    if ($uri.Scheme -ne 'https' -or $item.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
        throw "Invalid pinned source URL or hash: $($item.name)"
    }
    $path = Join-Path $directory $item.name
    $response = $client.GetAsync($uri, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    try {
        $response.EnsureSuccessStatusCode() | Out-Null
        $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        try {
            $file = [IO.File]::Create($path)
            try { $inputStream.CopyTo($file) } finally { $file.Dispose() }
        } finally { $inputStream.Dispose() }
    } finally { $response.Dispose() }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($actual -ne $item.sha256) { throw "Source hash mismatch: $($item.name)" }
    if ($Archive) {
        $null = & tar -tf $path
        if ($LASTEXITCODE -ne 0) { throw "Source archive cannot be read: $($item.name)" }
    }
    Write-Host "Verified aria2 source input: $($item.name)"
}
try {
    foreach ($item in $provenance.sourceArchives) {
        Download-PinnedSource $item (Join-Path $output 'archives') -Archive
    }
    foreach ($item in $provenance.buildFiles) {
        Download-PinnedSource $item (Join-Path $output 'build')
    }
} finally { $client.Dispose() }
$linkedNotices = [Collections.Generic.List[object]]::new()
if ($NoticeDirectory) {
    $noticeRoot = [IO.Path]::GetFullPath($NoticeDirectory)
    if (!$noticeRoot.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -or
        (Test-Path -LiteralPath $noticeRoot)) { throw 'Use a fresh linked-library notice directory inside package artifacts.' }
    $null = New-Item -ItemType Directory -Path $noticeRoot -Force
    $extractRoot = New-Item -ItemType Directory -Path (Join-Path $output 'extracted-notices') -Force
    $noticeInputs = @(
        @{ component = 'GMP'; archive = 'gmp-6.3.0.tar.xz'; root = 'gmp-6.3.0'; names = @('COPYING', 'COPYING.LESSERv3') },
        @{ component = 'Expat'; archive = 'expat-2.5.0.tar.bz2'; root = 'expat-2.5.0'; names = @('COPYING') },
        @{ component = 'SQLite'; archive = 'sqlite-autoconf-3430100.tar.gz'; root = 'sqlite-autoconf-3430100'; names = @('README.txt') },
        @{ component = 'zlib'; archive = 'zlib-1.3.tar.gz'; root = 'zlib-1.3'; names = @('LICENSE') },
        @{ component = 'c-ares'; archive = 'c-ares-1.19.1.tar.gz'; root = 'c-ares-1.19.1'; names = @('LICENSE.md') },
        @{ component = 'libssh2'; archive = 'libssh2-1.11.0.tar.bz2'; root = 'libssh2-1.11.0'; names = @('COPYING') },
        @{ component = 'wslay'; archive = 'aria2-1.37.0.tar.xz'; root = 'aria2-1.37.0/deps/wslay'; names = @('COPYING') }
    )
    foreach ($input in $noticeInputs) {
        $destination = New-Item -ItemType Directory -Path (Join-Path $noticeRoot $input.component) -Force
        foreach ($name in $input.names) {
            $member = $input.root + '/' + $name
            $null = & tar -xf (Join-Path $output ('archives/' + $input.archive)) -C $extractRoot.FullName $member
            if ($LASTEXITCODE -ne 0) { throw "Linked-library notice missing from source: $member" }
            $extracted = Join-Path $extractRoot.FullName $member
            $target = Join-Path $destination.FullName $name
            Copy-Item -LiteralPath $extracted -Destination $target
            $linkedNotices.Add([ordered]@{ component = $input.component; name = $name
                sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash })
        }
    }
}
[ordered]@{
    aria2Version = $provenance.version
    bundledExecutableSha256 = $provenance.executableSha256
    sourceArchives = $provenance.sourceArchives
    buildFiles = $provenance.buildFiles
    linkedLibraryNotices = @($linkedNotices)
    sourceVerification = 'SHA-256 and archive readability; binary rebuild equivalence not asserted'
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'source-manifest.json') -Encoding utf8
