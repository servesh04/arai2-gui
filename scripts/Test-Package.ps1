[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [string]$ReportPath,
    [switch]$RequireSignature
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$provenance = Get-Content -LiteralPath (Join-Path $projectRoot 'vendor/aria2/provenance.json') -Raw | ConvertFrom-Json
$zip = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $entries = @{}
    foreach ($entry in $zip.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($entries.ContainsKey($name)) { throw "Duplicate package entry: $name" }
        $entries[$name] = $entry
    }
    function Read-PackageText([string]$name) {
        if (!$entries.ContainsKey($name)) { throw "Missing package entry: $name" }
        $reader = [IO.StreamReader]::new($entries[$name].Open())
        try { $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    function Get-PackageHash([string]$name) {
        if (!$entries.ContainsKey($name)) { throw "Missing package entry: $name" }
        $stream = $entries[$name].Open(); $sha = [Security.Cryptography.SHA256]::Create()
        try { ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
        finally { $stream.Dispose(); $sha.Dispose() }
    }
    [xml]$manifest = Read-PackageText 'AppxManifest.xml'
    [xml]$sourceManifest = Get-Content -LiteralPath (Join-Path $projectRoot 'src/AriaGui.App/Package.appxmanifest') -Raw
    $identity = $manifest.Package.Identity
    if ($identity.Name -ne $sourceManifest.Package.Identity.Name -or $identity.Publisher -ne $sourceManifest.Package.Identity.Publisher -or
        $identity.Version -ne $sourceManifest.Package.Identity.Version -or $identity.ProcessorArchitecture -ne 'x64') { throw 'Package identity/version/architecture differs from the reviewed manifest.' }
    if (@($manifest.Package.Dependencies.PackageDependency).Where({ $null -ne $_ }).Count -gt 0) { throw 'Self-contained release unexpectedly requires a framework package.' }
    $family = $manifest.Package.Dependencies.TargetDeviceFamily
    if ($family.Name -ne 'Windows.Desktop' -or $family.MinVersion -ne '10.0.22000.0') { throw 'Unexpected supported OS declaration.' }
    foreach ($name in @('AriaGui.App.exe','AriaGui.App.dll','AriaGui.Application.dll','AriaGui.Infrastructure.dll','resources.pri',
        'coreclr.dll','hostfxr.dll','hostpolicy.dll','Microsoft.ui.xaml.dll','e_sqlite3.dll','Engine/aria2c.exe',
        'Engine/Notices/provenance.json','AppxBlockMap.xml')) {
        if (!$entries.ContainsKey($name) -or $entries[$name].Length -eq 0) { throw "Missing/empty release dependency: $name" }
    }
    $inventoryPath = 'Notices/dependency-inventory.json'
    if (!$entries.ContainsKey($inventoryPath)) { throw 'Release dependency inventory missing.' }
    $inventory = Read-PackageText $inventoryPath | ConvertFrom-Json
    $runtimeNotices = @($inventory.packages | Where-Object id -eq 'Microsoft.NETCore.App.Runtime.win-x64')
    if ($runtimeNotices.Count -ne 1) { throw 'Embedded runtime notice record missing.' }
    foreach ($name in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')) {
        if (!$entries.ContainsKey('Notices/Microsoft.NETCore.App.Runtime.win-x64-' + $runtimeNotices[0].version + '/' + $name)) { throw "Embedded runtime notice missing: $name" }
    }
    $runtime = (Read-PackageText 'AriaGui.App.runtimeconfig.json' | ConvertFrom-Json).runtimeOptions
    if ($runtime.framework -or $runtime.frameworks -or !@($runtime.includedFrameworks).Where({ $_.name -eq 'Microsoft.NETCore.App' }).Count) { throw '.NET runtime is not self-contained.' }
    if (@($runtime.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App')[0].version -ne $runtimeNotices[0].version) { throw 'Runtime and corresponding notice versions differ.' }
    if ((Get-PackageHash 'Engine/aria2c.exe') -ne $provenance.executableSha256) { throw 'Packaged engine hash differs from the pinned binary.' }
    foreach ($notice in @($provenance.notices) + @('provenance.json')) {
        $expected = (Get-FileHash -LiteralPath (Join-Path $projectRoot "vendor/aria2/$notice") -Algorithm SHA256).Hash
        if ((Get-PackageHash "Engine/Notices/$notice") -ne $expected) { throw "Packaged notice/provenance differs: $notice" }
    }
    foreach ($name in $entries.Keys) {
        if ($name -match '(?i)(^|/)(downloads\.db.*|aria2\.conf|engine\.owner\.json|engine\.checkpoint\.json|preferences\.json|.*\.pfx|.*\.pdb|.*\.log)$' -or
            $name -match '(?i)(AriaGui\.EngineFixture|testhost|/sessions/|\.Tests\.)') { throw "Private or development file in package: $name" }
    }
    # Verify the bytes of every block-mapped file, including assemblies and XAML resources.
    [xml]$blockMap = Read-PackageText 'AppxBlockMap.xml'
    if ($blockMap.BlockMap.HashMethod -ne 'http://www.w3.org/2001/04/xmlenc#sha256') { throw 'Unsupported package block-map hash method.' }
    $mapped = @{}
    $blockBuffer = [byte[]]::new(65536); $blockSha = [Security.Cryptography.SHA256]::Create()
    try {
        foreach ($file in $blockMap.BlockMap.File) {
            $name = $file.Name.Replace('\','/')
            if (!$entries.ContainsKey($name) -or $mapped.ContainsKey($name) -or $entries[$name].Length -ne [long]$file.Size) { throw "Package block-map file/size mismatch: $name" }
            $mapped[$name] = $true
            $stream = $entries[$name].Open(); $remaining = [long]$file.Size
            try {
                foreach ($block in @($file.Block | Where-Object { $null -ne $_ })) {
                    $length = [int][Math]::Min(65536L, $remaining)
                    $stream.ReadExactly($blockBuffer, 0, $length)
                    if ([Convert]::ToBase64String($blockSha.ComputeHash($blockBuffer, 0, $length)) -ne $block.Hash) { throw "Package block-map hash mismatch: $name" }
                    $remaining -= $length
                }
                if ($remaining -ne 0 -or $stream.ReadByte() -ne -1) { throw "Package block-map length mismatch: $name" }
            } finally { $stream.Dispose() }
        }
    } finally { $blockSha.Dispose() }
    foreach ($name in $entries.Keys) {
        # CodeIntegrity.cat is added by SignTool and covered by the package signature, not its block map.
        if (!$name.EndsWith('/') -and $name -notin @('AppxBlockMap.xml','[Content_Types].xml','AppxSignature.p7x','AppxMetadata/CodeIntegrity.cat') -and !$mapped.ContainsKey($name)) { throw "Unmapped package content: $name" }
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $package
    $signed = $entries.ContainsKey('AppxSignature.p7x')
    $nativeTrust = $null; $signatureStatus = $signature.Status.ToString()
    if ($signed) {
        . (Join-Path $PSScriptRoot 'PackageSignature.ps1')
        $nativeTrust = Get-WindowsPackageTrust $package
        # CERT_E_UNTRUSTEDROOT is the single expected development-trust failure.
        if ($nativeTrust -notin @('00000000','800B0109') -or $signature.SignerCertificate.Subject -ne $identity.Publisher) { throw "Package signature failed native verification: 0x$nativeTrust" }
        $signatureStatus = $(if ($nativeTrust -eq '00000000') { 'Valid' } else { 'UntrustedRoot' })
        Add-Type -AssemblyName System.Security.Cryptography.Pkcs
        $signatureStream = $entries['AppxSignature.p7x'].Open(); $memory = [IO.MemoryStream]::new()
        try {
            $signatureStream.CopyTo($memory); $signatureBytes = $memory.ToArray()
            if ($signatureBytes.Length -lt 5 -or [Text.Encoding]::ASCII.GetString($signatureBytes,0,4) -ne 'PKCX') { throw 'Unrecognized package signature header.' }
            $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
            $cms.Decode([byte[]]$signatureBytes[4..($signatureBytes.Length - 1)])
            $cms.CheckSignature($true) # Cryptographic validity; trust remains a separate recorded result.
        } finally { $signatureStream.Dispose(); $memory.Dispose() }
    }
    if ($RequireSignature -and !$signed) { throw 'A matching signature is required; modified/unrecognized packages are rejected.' }
    $report = [ordered]@{
        checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); package = $package
        sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash; bytes = (Get-Item -LiteralPath $package).Length
        name = $identity.Name; publisher = $identity.Publisher; version = $identity.Version; architecture = 'x64'
        minimumWindowsBuild = $family.MinVersion; entries = $entries.Count
        selfContainedDotNet = $true; selfContainedWindowsAppSdk = $true; frameworkDependencies = @()
        dotNetRuntime = @($runtime.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App')[0].version
        engineVersion = $provenance.version; engineSha256 = $provenance.executableSha256
        bundledNotices = @($provenance.notices); signaturePresent = $signed; signatureStatus = $signatureStatus
        windowsAuthenticodeStatus = $signature.Status.ToString(); nativeTrustResult = $nativeTrust; blockMapVerified = $true
        dependencyInventoryPackages = @($inventory.packages).Count
        signerThumbprint = $signature.SignerCertificate.Thumbprint
        cleanMachineAcceptance = 'Pending'; publicRedistribution = 'Pending corresponding-source/license review'
    }
    if ($ReportPath) {
        $output = [IO.Path]::GetFullPath($ReportPath)
        $allowed = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
        if (!$output.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package reports must stay inside workspace artifacts.' }
        New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
        $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $output -Encoding utf8
    }
    [pscustomobject]$report
}
finally { $zip.Dispose() }
