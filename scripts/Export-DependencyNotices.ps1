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
        copyright = [string]$metadata.copyright
        licenseUrl = [string]$metadata.licenseUrl; projectUrl = [string]$metadata.projectUrl
        licenseTextFile = $(if ($metadata.license.type -eq 'expression') { 'LicenseTexts/' + $metadata.license.InnerText + '.txt' } else { $null })
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
$licenseTexts = @(
    @{ name = 'MIT'; url = 'https://raw.githubusercontent.com/spdx/license-list-data/v3.26.0/text/MIT.txt'; sha256 = '6D7C9E9D7D12C8D3753BDB24417B84819BA4D6C2F025C37948113D86DC81EEF9' },
    @{ name = 'Apache-2.0'; url = 'https://raw.githubusercontent.com/spdx/license-list-data/v3.26.0/text/Apache-2.0.txt'; sha256 = '074E6E32C86A4C0EF8B3ED25B721CA23ACA83DF277CD88106EF7177C354615FF' }
)
$declaredExpressions = @($inventory | Where-Object { $_['licenseType'] -eq 'expression' } | ForEach-Object { $_['license'] } | Sort-Object -Unique)
foreach ($expression in $declaredExpressions) {
    if ($expression -notin @($licenseTexts | ForEach-Object { $_['name'] })) { throw "Unreviewed dependency license expression: $expression" }
}
$textRoot = New-Item -ItemType Directory -Path (Join-Path $output 'LicenseTexts') -Force
$client = [Net.Http.HttpClient]::new()
try {
    foreach ($license in $licenseTexts) {
        if ($license.name -notin $declaredExpressions) { continue }
        $path = Join-Path $textRoot.FullName ($license.name + '.txt')
        $response = $client.GetAsync($license.url, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            $response.EnsureSuccessStatusCode() | Out-Null
            $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            try {
                $file = [IO.File]::Create($path)
                try { $inputStream.CopyTo($file) } finally { $file.Dispose() }
            } finally { $inputStream.Dispose() }
        } finally { $response.Dispose() }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $license.sha256) {
            throw "Dependency license text differs: $($license.name)"
        }
    }
} finally { $client.Dispose() }
$fluent = New-Item -ItemType Directory -Path (Join-Path $output 'Fluent-UI-System-Icons') -Force
foreach ($name in @('LICENSE', 'NOTICE')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "docs/design/assets/fluent/$name") -Destination $fluent.FullName
}
[ordered]@{ generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); packages = @($inventory)
    reviewStatus = 'Pinned license texts and package notices collected; shipped binaries are classified by Test-PortablePackage.ps1'
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'dependency-inventory.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/THIRD_PARTY.md') -Destination $output
Write-Host "Collected declared notices for $($inventory.Count) resolved packages/runtime."
