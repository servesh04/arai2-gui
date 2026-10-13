[CmdletBinding()]
param([string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$ArtifactDirectory) { $ArtifactDirectory = Join-Path $root ('artifacts/browser-phase-b/' + [guid]::NewGuid().ToString('N')) }
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
if ((Test-Path -LiteralPath $ArtifactDirectory) -and @(Get-ChildItem -LiteralPath $ArtifactDirectory -Force).Count) { throw 'A fresh empty artifact directory is required.' }
$project = Join-Path $root 'tests/AriaGui.Browser.Spikes/AriaGui.Browser.Spikes.csproj'
& dotnet build $project --no-restore -m:1 -p:UseSharedCompilation=false
if ($LASTEXITCODE) { throw 'Credential harness build failed.' }
$harness = Join-Path $root 'tests/AriaGui.Browser.Spikes/bin/Debug/net10.0/AriaGui.Browser.Spikes.dll'
& dotnet $harness credential-probe (Join-Path $root 'vendor/aria2/win-x64/aria2c.exe') $ArtifactDirectory | Out-Null
if ($LASTEXITCODE) { throw 'Credential harness execution failed; no feature gate result is claimed.' }
$report = Get-Content -LiteralPath (Join-Path $ArtifactDirectory 'evidence.json') -Raw | ConvertFrom-Json
# Exercise the actual allowlisted exporter against measured engine logs. The values are
# synthetic unique markers found in retained session evidence; they are never printed.
$markerText = [IO.File]::ReadAllText((Join-Path $ArtifactDirectory 'lifecycle/session.txt'))
$markers = @([regex]::Matches($markerText, 'PHASEB_[A-Z]+_[a-f0-9]{32}') | ForEach-Object { $_.Value } | Sort-Object -Unique)
if ($markers.Count -ne 5) { throw 'Five synthetic marker values must be independently recoverable from the session.' }
$exports = @()
foreach ($stage in @('lifecycle', 'recovery', 'crash-recovery', 'redirects')) {
    $stageRoot = Join-Path $ArtifactDirectory $stage
    # A retained copy exercises the rotated filename exporter path; engine rotation itself is not claimed.
    Copy-Item -LiteralPath (Join-Path $stageRoot 'engine.log') -Destination (Join-Path $stageRoot 'engine.log.previous')
    [IO.File]::WriteAllText((Join-Path $stageRoot 'exception-transport.log'), ($markers -join "`n"))
    $export = & (Join-Path $PSScriptRoot 'Export-Diagnostics.ps1') -RuntimeDirectory (Join-Path $ArtifactDirectory $stage)
    $exportNames = @(Get-ChildItem -LiteralPath $export -File | ForEach-Object Name)
    if ($exportNames.Count -ne 3 -or 'diagnostics.json' -notin $exportNames -or 'engine.log' -notin $exportNames -or 'engine.log.previous' -notin $exportNames) { throw 'Diagnostics allowlist changed.' }
    $files = @()
    foreach ($file in Get-ChildItem -LiteralPath $export -File) {
        $text = [IO.File]::ReadAllText($file.FullName)
        $fields = @($markers | Where-Object { $text.Contains($_) } | ForEach-Object { ($_ -split '_')[1] })
        $files += [ordered]@{ name = $file.Name; bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash; markerFields = $fields }
    }
    $exports += [ordered]@{ boundary = $stage; path = $export; files = $files }
}
[ordered]@{ syntheticOnly = $true; persistenceGate = $report.persistenceGate; redirectGate = $report.redirectGate
    unknownTransportLogExcluded = $true; exports = $exports; scope = 'Actual exporter over current engine log and a retained rotated-name copy; unknown exception transport log excluded. Actual engine rotation/production exception paths are not claimed. Production does not enable verbose aria2 logs or credential forwarding.' } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'diagnostics-evidence.json') -Encoding utf8
foreach ($item in $exports | Where-Object boundary -in @('lifecycle', 'recovery', 'redirects')) {
    foreach ($file in $item.files | Where-Object name -in @('engine.log', 'engine.log.previous')) {
        if ('HEADER' -notin $file.markerFields -or 'UA' -notin $file.markerFields) { throw 'Diagnostics evidence changed or is inconclusive; review the recorded field observations.' }
    }
}
Write-Output "Credential feasibility measured: persistence=$($report.persistenceGate); redirect=$($report.redirectGate). Evidence: $ArtifactDirectory"
# Exit zero means the experiment completed, never that credential support passed S01/S02.
