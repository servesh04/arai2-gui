[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackagePath, [string]$SignedPackagePath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$inputPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$allowed = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/release-tool-tests')) + [IO.Path]::DirectorySeparatorChar
$root = Join-Path $allowed ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$checks = [Collections.Generic.List[string]]::new()
function Expect-Rejection([scriptblock]$action, [string]$pattern, [string]$name) {
    $rejected = $false
    try { & $action | Out-Null }
    catch { if ($_.Exception.Message -notmatch $pattern) { throw }; $rejected = $true }
    if (!$rejected) { throw "Check failed: $name did not reject the fixture." }
    $checks.Add($name)
}
function Edit-Package([string]$name, [string]$entryName, [string]$content, [switch]$Delete, [string]$Source = $inputPackage) {
    $path = Join-Path $root ($name + '.msix')
    Copy-Item -LiteralPath $Source -Destination $path
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $zip.GetEntry($entryName); if ($entry) { $entry.Delete() }
        if (!$Delete) {
            $entry = $zip.CreateEntry($entryName)
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write($content) } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
    $path
}
try {
    $null = & (Join-Path $PSScriptRoot 'Test-Package.ps1') -PackagePath $inputPackage
    $checks.Add('Original package content accepted')
    Expect-Rejection { & (Join-Path $PSScriptRoot 'Test-Package.ps1') -PackagePath $inputPackage -RequireSignature } 'signature is required' 'Unsigned package rejected when signature required'
    $altered = Edit-Package 'engine' 'Engine/aria2c.exe' 'tampered'
    Expect-Rejection { & (Join-Path $PSScriptRoot 'Test-Package.ps1') -PackagePath $altered } 'engine hash differs' 'Changed engine rejected'
    $altered = Edit-Package 'missing-runtime' 'coreclr.dll' '' -Delete
    Expect-Rejection { & (Join-Path $PSScriptRoot 'Test-Package.ps1') -PackagePath $altered } 'Missing/empty release dependency' 'Missing runtime rejected'
    $altered = Edit-Package 'private-data' 'downloads.db' 'private-history'
    Expect-Rejection { & (Join-Path $PSScriptRoot 'Test-Package.ps1') -PackagePath $altered } 'Private or development file' 'Private data rejected'
    if ($SignedPackagePath) {
        $signed = (Resolve-Path -LiteralPath $SignedPackagePath).Path
        $null = & (Join-Path $PSScriptRoot 'Test-Package.ps1') -PackagePath $signed -RequireSignature
        $checks.Add('Original development-signed package accepted with trust reported separately')
        $altered = Edit-Package 'signed-assembly' 'AriaGui.App.dll' 'changed application code' -Source $signed
        Expect-Rejection { & (Join-Path $PSScriptRoot 'Test-Package.ps1') -PackagePath $altered -RequireSignature } 'block-map' 'Changed signed assembly rejected'
        # All file hashes still match; only the signed block-map document changes.
        $zip = [IO.Compression.ZipFile]::OpenRead($signed)
        try {
            $reader = [IO.StreamReader]::new($zip.GetEntry('AppxBlockMap.xml').Open())
            try { $changedMap = $reader.ReadToEnd() + [Environment]::NewLine + '<!-- changed after signing -->' }
            finally { $reader.Dispose() }
        } finally { $zip.Dispose() }
        $altered = Edit-Package 'signed-block-map' 'AppxBlockMap.xml' $changedMap -Source $signed
        Expect-Rejection { & (Join-Path $PSScriptRoot 'Test-Package.ps1') -PackagePath $altered -RequireSignature } 'signature failed native verification' 'Changed signed block map rejected'
    }
    $runtime = Join-Path $root 'runtime'; New-Item -ItemType Directory -Path $runtime | Out-Null
    $secret = 'fixture-secret-123456'
    [IO.File]::WriteAllText((Join-Path $runtime 'aria2.conf'), "rpc-secret=$secret")
    [IO.File]::WriteAllText((Join-Path $runtime 'downloads.db'), 'do-not-export-history')
    [IO.File]::WriteAllText((Join-Path $runtime 'preferences.json'), 'do-not-export-preferences')
    [IO.File]::WriteAllText((Join-Path $runtime 'engine.log'), "problem $secret at https://name:password@example.invalid/file?access_token=private-query" + [Environment]::NewLine +
        'token:another-secret' + [Environment]::NewLine + 'rpc-secret=third-secret' + [Environment]::NewLine + 'C:\Users\PrivateUser\Downloads\private.bin')
    $before = Get-FileHash -LiteralPath (Join-Path $runtime 'aria2.conf') -Algorithm SHA256
    $export = & (Join-Path $PSScriptRoot 'Export-Diagnostics.ps1') -RuntimeDirectory $runtime
    $names = @(Get-ChildItem -LiteralPath $export -File | Select-Object -ExpandProperty Name)
    if ($names.Count -ne 2 -or 'diagnostics.json' -notin $names -or 'engine.log' -notin $names) { throw 'Diagnostics export allowlist failed.' }
    $text = [IO.File]::ReadAllText((Join-Path $export 'engine.log'))
    foreach ($sensitive in @($secret,'password','private-query','another-secret','third-secret','PrivateUser','private.bin')) {
        if ($text.Contains($sensitive)) { throw "Diagnostics redaction failed: $sensitive" }
    }
    if ((Get-FileHash -LiteralPath (Join-Path $runtime 'aria2.conf') -Algorithm SHA256).Hash -ne $before.Hash) { throw 'Diagnostics changed the runtime configuration.' }
    $checks.Add('Diagnostics allowlist, secret/URL/path redaction, and source preservation')
    [ordered]@{ checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); passed = $checks.Count; checks = @($checks)
        package = $inputPackage; diagnosticsFixtureExport = $export
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $projectRoot 'artifacts/release-tools-results.json') -Encoding utf8
    Write-Host "$($checks.Count) release-tool checks passed."
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if (!$resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup.' }
    if (Test-Path -LiteralPath $resolved -PathType Container) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
