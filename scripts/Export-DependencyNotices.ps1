[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
$allowed = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/packages')) + [IO.Path]::DirectorySeparatorChar
if (!$output.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Notice output must stay in workspace package artifacts.' }
if (Test-Path -LiteralPath $output) { throw 'Use a fresh notice directory to avoid mixing dependency versions.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$assets = Get-Content -LiteralPath (Join-Path $projectRoot 'src/AriaGui.App/obj/project.assets.json') -Raw | ConvertFrom-Json
$roots = @($assets.packageFolders.PSObject.Properties.Name)
$inventory = [Collections.Generic.List[object]]::new()
function Copy-DependencyNotices([string]$id, [string]$version, [string]$kind) {
    $packageRoot = $null
    foreach ($root in $roots) {
        $candidate = Join-Path $root ($id.ToLowerInvariant() + '/' + $version)
        if (Test-Path -LiteralPath $candidate -PathType Container) { $packageRoot = [IO.Path]::GetFullPath($candidate); break }
    }
    if (!$packageRoot) { throw "Restored package not found: $id/$version" }
    $nuspec = Get-ChildItem -LiteralPath $packageRoot -File -Filter '*.nuspec' | Select-Object -First 1
    if (!$nuspec) { throw "Package metadata missing: $id/$version" }
    [xml]$xml = Get-Content -LiteralPath $nuspec.FullName -Raw
    $metadata = $xml.package.metadata
    $destination = Join-Path $output ($id + '-' + $version)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $copied = [Collections.Generic.List[string]]::new()
    $files = @(Get-ChildItem -LiteralPath $packageRoot -File | Where-Object { $_.Name -match '(?i)^(LICENSE|LICENCE|COPYING|NOTICE|THIRD[-._]?PARTY[-._]?NOTICES)(\..*)?$' })
    if ($metadata.license.type -eq 'file') {
        $declared = [IO.Path]::GetFullPath((Join-Path $packageRoot $metadata.license.InnerText))
        if (!$declared.StartsWith($packageRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe declared license path.' }
        if (!(Test-Path -LiteralPath $declared -PathType Leaf)) { throw "Declared license missing: $id/$version" }
        $files += Get-Item -LiteralPath $declared
    }
    foreach ($file in $files | Sort-Object FullName -Unique) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $file.Name)
        $copied.Add($id + '-' + $version + '/' + $file.Name)
    }
    $inventory.Add([ordered]@{ id = $id; version = $version; scope = $kind
        licenseType = [string]$metadata.license.type; license = [string]$metadata.license.InnerText
        licenseUrl = [string]$metadata.licenseUrl; projectUrl = [string]$metadata.projectUrl
        noticeFiles = @($copied); contentHashSha512 = [string]$assets.libraries.($id + '/' + $version).sha512
    })
}
foreach ($library in $assets.libraries.PSObject.Properties | Sort-Object Name) {
    if ($library.Value.type -ne 'package') { continue }
    $parts = $library.Name.Split('/')
    Copy-DependencyNotices $parts[0] $parts[1] 'Resolved NuGet build graph'
}
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src/AriaGui.App/AriaGui.App.csproj') -Raw
$runtimeVersion = @($project.Project.PropertyGroup | Where-Object { $_.Condition -like '*AriaGuiRelease*' -and $_.AriaGuiRuntimeVersion })[0].AriaGuiRuntimeVersion
Copy-DependencyNotices 'Microsoft.NETCore.App.Runtime.win-x64' $runtimeVersion 'Embedded .NET runtime'
Copy-DependencyNotices 'Microsoft.AspNetCore.App.Runtime.win-x64' $runtimeVersion 'Embedded authenticated gateway runtime'
[ordered]@{ generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); packages = @($inventory)
    reviewStatus = 'Inventory and declared notice files; full runtime/native redistribution review pending'
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'dependency-inventory.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/THIRD_PARTY.md') -Destination $output
Write-Host "Collected declared notices for $($inventory.Count) resolved packages/runtime."
