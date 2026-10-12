[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceBundle,[Parameter(Mandatory)][string]$ReportPath)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$scope=Join-Path $workspace ('artifacts/browser-installer-tests/'+[guid]::NewGuid().ToString('N'))
$root=Join-Path $scope 'stable installation 日本語'
$installer=Join-Path $PSScriptRoot 'Install-BrowserCompanion.ps1'
$checks=[Collections.Generic.List[string]]::new()
function Check([bool]$Condition,[string]$Name){if(!$Condition){throw ('Check failed: '+$Name)};$checks.Add($Name)}
function Run([string]$Action,[switch]$Fail){& $installer -Action $Action -Channel synthetic -SyntheticWorkspaceRoot $scope -InstallRoot $root -SourceBundle $SourceBundle -Apply -SyntheticFailAfterSwitch:$Fail}
function Reject([scriptblock]$Work,[string]$Pattern,[string]$Name){$rejected=$false;try{& $Work|Out-Null}catch{if($_.Exception.Message -notmatch $Pattern){throw};$rejected=$true};Check $rejected $Name}
$process=$null
try{
    $dry=& $installer -Action install -Channel synthetic -SyntheticWorkspaceRoot $scope -InstallRoot $root -SourceBundle $SourceBundle
    Check ($dry.DryRun -and !(Test-Path -LiteralPath $scope)) 'Install dry-run has no file side effects'
    $sourceManifest=Join-Path $SourceBundle 'native/chrome.json';$sourceBytes=[IO.File]::ReadAllBytes($sourceManifest)
    [IO.File]::AppendAllText($sourceManifest,' ')
    try{Reject {& $installer -Action install -Channel synthetic -SyntheticWorkspaceRoot $scope -InstallRoot $root -SourceBundle $SourceBundle} 'integrity failed' 'Dry-run rejects corrupted source without creating workspace'}finally{[IO.File]::WriteAllBytes($sourceManifest,$sourceBytes)}
    Check (!(Test-Path -LiteralPath $scope)) 'Failed dry-run remains read-only'
    $first=Run install;Check ($first.Installed -and !$first.ProductionEnabled -and !$first.RegistryModified) 'Verified synthetic bundle installed without registration'
    $history=Join-Path $scope 'downloads.db';[IO.File]::WriteAllText($history,'synthetic history outside payload')
    $native=Get-Content -LiteralPath (Join-Path $root 'native/chrome.json') -Raw|ConvertFrom-Json
    Check ($native.path -eq (Join-Path $root 'host/AriaGui.NativeHost.exe')) 'Spaces/non-ASCII native manifest uses final stable path'
    $receiptBefore=[IO.File]::ReadAllBytes((Join-Path $scope 'installation-receipt.json'));$inventoryBefore=(Get-FileHash -LiteralPath (Join-Path $root 'browser-bundle.json')).Hash
    Reject {Run repair -Fail} 'Synthetic switch failure' 'Post-switch failure rolls back visibly'
    Check ((Get-FileHash -LiteralPath (Join-Path $root 'browser-bundle.json')).Hash -eq $inventoryBefore -and [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $scope 'installation-receipt.json'))) -eq [Convert]::ToBase64String($receiptBefore)) 'Failed repair restores exact payload and receipt'
    $repaired=Run repair;Check ($repaired.CompanionInstallationId -eq $first.CompanionInstallationId) 'Successful repair preserves installation identity and stable root'
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $root 'host/AriaGui.NativeHost.exe'));$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($start)
    Reject {Run repair} 'Active companion' 'Active host refuses update before switch'
    $process.StandardInput.Close();if(!$process.WaitForExit(5000)){$process.Kill($true);$process.WaitForExit(5000)|Out-Null};$process.Dispose();$process=$null
    $extra=Join-Path $root 'unowned.txt';[IO.File]::WriteAllText($extra,'preserve')
    Reject {Run uninstall} 'untracked files' 'Uninstall refuses unowned extra payload data'
    Check ([IO.File]::ReadAllText($extra) -eq 'preserve') 'Unowned payload data preserved'
    Remove-Item -LiteralPath $extra
    $null=Run uninstall
    Check (!(Test-Path -LiteralPath $root) -and [IO.File]::ReadAllText($history) -eq 'synthetic history outside payload') 'Owned uninstall leaves history/runtime outside payload untouched'
    $second=Run install;Check ($second.Installed -and [IO.File]::ReadAllText($history) -eq 'synthetic history outside payload') 'Reinstall preserves outside history'
    $null=Run uninstall
    [ordered]@{passed=$checks.Count;checks=@($checks);syntheticWorkspace=$scope;productionEnabled=$false;registryModified=$false;cleanMachineVerified=$false} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding utf8
    Write-Output ('Passed '+$checks.Count+' synthetic staged-installer checks.')
}finally{
    if($process){if(!$process.HasExited){$process.Kill($true);$process.WaitForExit(5000)|Out-Null};$process.Dispose()}
    # Keep the synthetic workspace/evidence/history; no broad cleanup is needed.
}
