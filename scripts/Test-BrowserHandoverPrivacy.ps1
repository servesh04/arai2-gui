[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $root ('artifacts/browser-wp7/privacy-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$markers = @('WP7_RPC_SECRET_91A2', 'WP7_URL_SECRET_91A2', 'WP7_HISTORY_SECRET_91A2', 'WP7_SESSION_SECRET_91A2', 'WP7_PREF_SECRET_91A2')
[IO.File]::WriteAllText((Join-Path $fixture 'aria2.conf'), "rpc-secret=$($markers[0])`n")
[IO.File]::WriteAllText((Join-Path $fixture 'engine.log'), "https://example.invalid/file?token=$($markers[1])`nRPC $($markers[0])`n")
[IO.File]::WriteAllText((Join-Path $fixture 'downloads.db'), $markers[2])
[IO.File]::WriteAllText((Join-Path $fixture 'aria2.session'), $markers[3])
[IO.File]::WriteAllText((Join-Path $fixture 'preferences.json'), $markers[4])
$export = & (Join-Path $PSScriptRoot 'Export-Diagnostics.ps1') -RuntimeDirectory $fixture
$files = @(Get-ChildItem -LiteralPath $export -File)
if ($files.Count -ne 2 -or 'diagnostics.json' -notin $files.Name -or 'engine.log' -notin $files.Name) { throw 'Export allowlist changed.' }
foreach ($file in $files) {
    $text = [IO.File]::ReadAllText($file.FullName)
    foreach ($marker in $markers) { if ($text.Contains($marker)) { throw "Synthetic marker escaped into $($file.Name)." } }
}
[ordered]@{ passed = $true; markersScanned = $markers.Count; fixture = $fixture; export = $export
    scope = 'Diagnostics export only; history/session fixtures are excluded, not tested as secret-free production storage.' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'artifacts/browser-wp7/privacy-evidence.json')
Write-Output 'Privacy diagnostics probe passed: five markers absent from allowlisted export.'
