[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackagePath, [string]$ReportPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$provenance = Get-Content -LiteralPath (Join-Path $projectRoot 'vendor/aria2/provenance.json') -Raw | ConvertFrom-Json
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $entries = @{}
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($entries.ContainsKey($name) -or !$name.StartsWith('AriaGUI/', [StringComparison]::Ordinal) -or
            $name.Contains('../') -or $name.Contains('/./') -or $name.Contains(':')) {
            throw "Invalid or duplicate portable ZIP entry: $name"
        }
        $entries[$name] = $entry
    }
    function Require-Entry([string]$name) {
        $path = 'AriaGUI/' + $name
        if (!$entries.ContainsKey($path) -or $entries[$path].Length -eq 0) { throw "Missing/empty portable dependency: $path" }
    }
    function Read-Entry([string]$name) {
        Require-Entry $name
        $reader = [IO.StreamReader]::new($entries['AriaGUI/' + $name].Open())
        try { $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    function Entry-Hash([string]$name) {
        Require-Entry $name
        $stream = $entries['AriaGUI/' + $name].Open()
        try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
    }
    foreach ($name in @('AriaGui.App.exe', 'AriaGui.App.dll', 'AriaGui.Application.dll',
        'AriaGui.Infrastructure.dll', 'AriaGui.App.pri', 'App.xbf', 'coreclr.dll', 'hostfxr.dll',
        'hostpolicy.dll', 'Microsoft.ui.xaml.dll', 'e_sqlite3.dll', 'Engine/aria2c.exe',
        'Engine/Notices/provenance.json', 'Notices/dependency-inventory.json', 'RELEASE_NOTES.md',
        'host/AriaGui.NativeHost.exe', 'host/hostfxr.dll', 'host/coreclr.dll',
        'Microsoft.AspNetCore.Server.Kestrel.Core.dll')) {
        Require-Entry $name
    }
    if ((Entry-Hash 'Engine/aria2c.exe') -ne $provenance.executableSha256) {
        throw 'Portable engine hash differs from the pinned binary.'
    }
    foreach ($notice in @($provenance.notices) + @('provenance.json')) {
        $expected = (Get-FileHash -LiteralPath (Join-Path $projectRoot "vendor/aria2/$notice") -Algorithm SHA256).Hash
        if ((Entry-Hash "Engine/Notices/$notice") -ne $expected) { throw "Portable engine notice differs: $notice" }
    }
    $inventory = Read-Entry 'Notices/dependency-inventory.json' | ConvertFrom-Json
    $runtime = (Read-Entry 'AriaGui.App.runtimeconfig.json' | ConvertFrom-Json).runtimeOptions
    foreach ($id in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.AspNetCore.App.Runtime.win-x64')) {
        $notice = @($inventory.packages | Where-Object id -eq $id)
        if ($notice.Count -ne 1) { throw "Embedded runtime notice record missing: $id" }
        foreach ($name in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
            Require-Entry "Notices/$id-$($notice[0].version)/$name"
        }
        $framework = $id.Replace('.Runtime.win-x64', '')
        if (@($runtime.includedFrameworks | Where-Object name -eq $framework)[0].version -ne $notice[0].version) {
            throw "Embedded runtime and notice version differ: $id"
        }
    }
    if ($runtime.framework -or $runtime.frameworks) { throw 'Portable app unexpectedly requires an external .NET runtime.' }
    foreach ($name in $entries.Keys) {
        if ($name -match '(?i)(^|/)(downloads\.db.*|aria2\.conf|engine\.owner\.json|preferences\.json|.*\.pfx|.*\.pdb|.*\.log)$' -or
            $name -match '(?i)(AriaGui\.EngineFixture|testhost|/sessions/|\.Tests\.)') {
            throw "Private or development file in portable ZIP: $name"
        }
    }
    $report = [ordered]@{
        checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        package = $package
        sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
        bytes = (Get-Item -LiteralPath $package).Length
        entries = $entries.Count
        architecture = 'x64'
        engineVersion = $provenance.version
        engineSha256 = $provenance.executableSha256
        selfContainedDotNet = $true
        selfContainedWindowsAppSdk = $true
        cleanMachineAcceptance = 'Pending'
    }
    if ($ReportPath) {
        $output = [IO.Path]::GetFullPath($ReportPath)
        $allowed = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
        if (!$output.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Portable reports must stay inside workspace artifacts.' }
        $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $output -Encoding utf8
    }
    [pscustomobject]$report
}
finally { $archive.Dispose() }
