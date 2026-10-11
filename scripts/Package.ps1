[CmdletBinding()]
param([switch]$SkipVerification)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    # Default: the exact source is checked before we produce a release candidate.
    if (!$SkipVerification) { & (Join-Path $PSScriptRoot 'Verify.ps1') }
    [xml]$manifest = Get-Content -LiteralPath 'src/AriaGui.App/Package.appxmanifest' -Raw
    $version = $manifest.Package.Identity.Version
    $runRoot = Join-Path $projectRoot ('artifacts/packages/' + $version + '/' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    $buildRoot = Join-Path $runRoot 'build'
    & dotnet restore src/AriaGui.App/AriaGui.App.csproj -p:Platform=x64 -p:AriaGuiRelease=true --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Release dependency restore failed.' }
    $notices = Join-Path $runRoot 'notices'
    & (Join-Path $PSScriptRoot 'Export-DependencyNotices.ps1') -OutputDirectory $notices
    $buildArgs = @('publish','src/AriaGui.App/AriaGui.App.csproj','--no-restore','-c','Release','-p:Platform=x64',
        '-p:AriaGuiRelease=true', ('-p:AriaGuiReleaseNoticesDir=' + $notices), ('-p:AppxPackageDir=' + $buildRoot + [IO.Path]::DirectorySeparatorChar))
    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) { throw 'Release package build failed.' }
    $packages = @(Get-ChildItem -LiteralPath $buildRoot -Recurse -File -Filter '*.msix')
    if ($packages.Count -ne 1) { throw 'Expected exactly one x64 MSIX.' }
    $packagePath = Join-Path $runRoot ('AriaGUI_' + $version + '_x64_unsigned.msix')
    Copy-Item -LiteralPath $packages[0].FullName -Destination $packagePath
    $report = & (Join-Path $PSScriptRoot 'Test-Package.ps1') -PackagePath $packagePath -ReportPath (Join-Path $runRoot 'package-report.json')
    foreach ($document in @('RELEASE_NOTES.md','PHASE_7.md','RELEASE_ACCEPTANCE.md','THIRD_PARTY.md')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot ('docs/' + $document)) -Destination $runRoot
    }
    Write-Host 'Unsigned local candidate created. Signing and clean-machine acceptance are separate steps.'
    $report
}
finally { Pop-Location }
