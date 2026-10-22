[CmdletBinding()]
param([switch]$SkipVerification)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    if (!$SkipVerification) { & (Join-Path $PSScriptRoot 'Verify.ps1') }
    [xml]$project = Get-Content -LiteralPath 'src/AriaGui.App/AriaGui.App.csproj' -Raw
    $version = @($project.Project.PropertyGroup | Where-Object { $_.Version })[0].Version
    $runRoot = Join-Path $projectRoot ('artifacts/packages/' + $version + '/' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N'))
    $appRoot = Join-Path $runRoot 'AriaGUI'
    New-Item -ItemType Directory -Path $appRoot -Force | Out-Null
    $options = @('-p:Platform=x64', '-p:AriaGuiRelease=true', '-p:AriaGuiUnpackaged=true', '-p:DebugType=None',
        '-p:GenerateAppxPackageOnBuild=false', '-m:1', '-nr:false')
    & dotnet restore src/AriaGui.App/AriaGui.App.csproj @options --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Portable release dependency restore failed.' }
    $notices = Join-Path $runRoot 'notices'
    & (Join-Path $PSScriptRoot 'Export-DependencyNotices.ps1') -OutputDirectory $notices
    $publishArgs = @('publish', 'src/AriaGui.App/AriaGui.App.csproj', '--no-restore', '-c', 'Release') +
        $options + @(('-p:AriaGuiReleaseNoticesDir=' + $notices), '-o', $appRoot)
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw 'Portable release publish failed.' }
    $hostArgs = @('publish', 'src/AriaGui.NativeHost/AriaGui.NativeHost.csproj', '--no-restore', '-c', 'Release') +
        $options + @('-o', (Join-Path $appRoot 'host'))
    & dotnet @hostArgs
    if ($LASTEXITCODE -ne 0) { throw 'Portable native host publish failed.' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/RELEASE_NOTES.md') -Destination $appRoot
    $zipPath = Join-Path $runRoot ('AriaGUI-' + $version + '-win-x64.zip')
    [IO.Compression.ZipFile]::CreateFromDirectory($appRoot, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $true)
    $report = & (Join-Path $PSScriptRoot 'Test-PortablePackage.ps1') -PackagePath $zipPath -ReportPath (Join-Path $runRoot 'package-report.json')
    @(
        ('{0}  {1}' -f (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash, (Split-Path -Leaf $zipPath))
        ('{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $appRoot 'AriaGui.App.exe') -Algorithm SHA256).Hash, 'AriaGUI/AriaGui.App.exe')
    ) | Set-Content -LiteralPath (Join-Path $runRoot 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "Verified portable ZIP created: $zipPath"
    $report
}
finally { Pop-Location }
