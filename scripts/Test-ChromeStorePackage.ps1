[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'

$package = (Resolve-Path -LiteralPath $PackagePath).Path
$required = @('manifest.json', 'background.js', 'clicks.js', 'popup.html', 'popup.js',
    'icons/16.png', 'icons/32.png', 'icons/48.png', 'icons/128.png')
$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($name in $required) { $null = $expected.Add($name) }
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $archive.Entries) {
        if (!$expected.Contains($entry.FullName) -or !$seen.Add($entry.FullName) -or $entry.Length -eq 0) {
            throw "Unexpected, duplicate, or empty store package entry: $($entry.FullName)"
        }
    }
    if ($seen.Count -ne $expected.Count) { throw 'The Chrome Web Store package is missing an extension file.' }
    $reader = [IO.StreamReader]::new($archive.GetEntry('manifest.json').Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json -AsHashtable }
    finally { $reader.Dispose() }
    if ($manifest.manifest_version -ne 3 -or $manifest.Contains('key') -or
        $manifest.name -ne 'AriaGUI Download Handoff' -or
        $manifest.version -notmatch '^\d+(\.\d+){1,3}$') {
        throw 'Invalid Chrome Web Store manifest or development signing key included.'
    }
    $permissions = @('downloads', 'nativeMessaging', 'cookies', 'storage', 'contextMenus', 'webRequest')
    if (@(Compare-Object $permissions @($manifest.permissions)).Count -or
        @(Compare-Object @('http://*/*', 'https://*/*') @($manifest.host_permissions)).Count) {
        throw 'Store package permissions differ from the reviewed extension.'
    }
    [pscustomobject]@{ Package = $package; Version = $manifest.version; Files = $seen.Count; DevelopmentKeyExcluded = $true }
} finally { $archive.Dispose() }
