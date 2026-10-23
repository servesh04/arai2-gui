[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'

$package = (Resolve-Path -LiteralPath $PackagePath).Path
$files = @('manifest.json', 'background.js', 'clicks.js', 'popup.html', 'popup.js',
    'icons/16.png', 'icons/32.png', 'icons/48.png', 'icons/128.png')
$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$null = $expected.Add('INSTALL.txt')
foreach ($name in $files) { $null = $expected.Add('AriaGUI-Chrome-Extension/' + $name) }
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $archive.Entries) {
        if (!$expected.Contains($entry.FullName) -or !$seen.Add($entry.FullName) -or $entry.Length -eq 0) {
            throw "Unexpected, duplicate, or empty unpacked package entry: $($entry.FullName)"
        }
    }
    if ($seen.Count -ne $expected.Count) { throw 'The unpacked package is missing an extension file.' }
    $reader = [IO.StreamReader]::new($archive.GetEntry('AriaGUI-Chrome-Extension/manifest.json').Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json -AsHashtable }
    finally { $reader.Dispose() }
    if ($manifest.manifest_version -ne 3 -or $manifest.name -ne 'AriaGUI Download Handoff' -or
        $manifest.version -notmatch '^\d+(\.\d+){1,3}$' -or !$manifest.key) {
        throw 'Invalid unpacked Chrome extension manifest.'
    }
    $keyBytes = [Convert]::FromBase64String($manifest.key)
    $id = -join ([Security.Cryptography.SHA256]::HashData($keyBytes)[0..15] | ForEach-Object {
        [char]([int][char]'a' + ($_ -shr 4)); [char]([int][char]'a' + ($_ -band 15))
    })
    if ($id -cne 'fipdcdbkjacoejdioahbjajjpcadiepm') {
        throw 'Unpacked extension ID does not match the registered native host.'
    }
    $permissions = @('downloads', 'nativeMessaging', 'cookies', 'storage', 'contextMenus', 'webRequest')
    if (@(Compare-Object $permissions @($manifest.permissions)).Count -or
        @(Compare-Object @('http://*/*', 'https://*/*') @($manifest.host_permissions)).Count) {
        throw 'Unpacked package permissions differ from the reviewed extension.'
    }
    [pscustomobject]@{ Package = $package; Version = $manifest.version; Files = $seen.Count; ExtensionId = $id }
} finally { $archive.Dispose() }
