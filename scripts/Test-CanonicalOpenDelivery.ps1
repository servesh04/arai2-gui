[CmdletBinding()]
param([string]$FixtureBuildDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'tests/AriaGui.OpenActivationFixture/bin/Debug/net10.0'))
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'OpenActivationFixtureAssertions.psm1') -Force
Assert-OpenCaptureBuild $FixtureBuildDirectory
# Explicit gate experiment only. Never replace an existing association or UserChoice.
foreach($surface in @([Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes\ariagui'),
 [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('Software\Classes\ariagui'),
 [Microsoft.Win32.Registry]::ClassesRoot.OpenSubKey('ariagui'),
 [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\Shell\Associations\UrlAssociations\ariagui'))){
 if($surface){$surface.Dispose();throw 'Canonical delivery experiment blocked: existing handler/association must be preserved.'}
}
foreach($package in Get-AppxPackage){
 $manifest=Get-AppxPackageManifest -Package $package.PackageFullName
 if($manifest.SelectNodes('//*[local-name()="Protocol" and @Name="ariagui"]').Count){throw 'Canonical experiment blocked: installed package owns ariagui.'}
}
$root=Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/phase-c/canonical-delivery-'+[guid]::NewGuid().ToString('N'))
$payload=Join-Path $root 'owned capture with spaces'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
foreach($name in @('AriaGui.App.exe','AriaGui.App.dll','AriaGui.App.deps.json','AriaGui.App.runtimeconfig.json')){Copy-Item -LiteralPath (Join-Path $FixtureBuildDirectory $name) -Destination $payload}
[IO.File]::WriteAllText((Join-Path $payload 'synthetic-open-fixture'),'Owned argument-capture fixture.')
[IO.File]::WriteAllText((Join-Path $payload 'synthetic-canonical-delivery'),'Explicit canonical boundary measurement; no GUI runtime.')
@{Scheme='ariagui';ExitCode=0} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payload 'fixture.json')
$exe=Join-Path $payload 'AriaGui.App.exe';$registration=Join-Path $PSScriptRoot 'Register-OpenProtocol.ps1'
$receipts=[Collections.Generic.List[object]]::new();$outcome='fail';$failure=$null;$registered=$false
try{
 & $registration -Action register -Executable $exe -Apply | Out-Null;$registered=$true
 foreach($value in @('ariagui://open','ARIAGUI://OPEN','ariagui://open/','ariagui://open?fixture=one','ariagui://open#fixture','ariagui://%6fpen','ariagui://open/extra','ariagui://open?fixture="one two"','ariagui://open ')){
  $receiptPath=Join-Path $payload 'receipt.json';if(Test-Path -LiteralPath $receiptPath){Remove-Item -LiteralPath $receiptPath}
  $start=[Diagnostics.ProcessStartInfo]::new($value);$start.UseShellExecute=$true;$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
  $child=[Diagnostics.Process]::Start($start)
  if(!$child){throw 'Canonical dispatch returned no owned process.'}
  try{
   if(!$child.WaitForExit(10000) -or $child.ExitCode -ne 0){throw 'Canonical capture child failed.'}
   $receipt=Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
   if($receipt.ProcessId -ne $child.Id -or $receipt.Executable -cne $exe -or $receipt.ExitCode -ne 0){throw 'Canonical receipt identity failed.'}
   $receipts.Add([pscustomobject]@{Submitted=$value;Delivered=$receipt.Arguments})
  }finally{if(!$child.HasExited){$child.Kill($true);$child.WaitForExit(5000)|Out-Null};$child.Dispose()}
 }
 $outcome='pass'
}catch{$failure=$_.Exception.Message;throw}finally{
 if($registered){& $registration -Action unregister -Executable $exe -Apply | Out-Null}
 $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes\ariagui');$cleaned=!$key;if($key){$key.Dispose()}
 if(!$cleaned){$outcome='fail'}
 [ordered]@{Outcome=$outcome;Failure=$failure;OSVersion=[Environment]::OSVersion.Version.ToString();Receipts=$receipts.ToArray();
  PreexistingHandler=$false;ExistingHandlerReplaced=$false;OwnedTemporaryHandlerRemoved=$cleaned;
  ExecutableSha256=(Get-FileHash -LiteralPath $exe).Hash;AssemblySha256=(Get-FileHash -LiteralPath (Join-Path $payload 'AriaGui.App.dll')).Hash;
  AppAcceptance='not tested; this is shell delivered-value capture only';IsolatedWindowsDeploymentAcceptance=$false} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'canonical-evidence.json') -Encoding utf8
}
if(!$cleaned){throw 'Owned canonical registration cleanup failed.'}
$receipts
