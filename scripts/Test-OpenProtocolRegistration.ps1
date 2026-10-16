[CmdletBinding()]
param([string]$FixtureBuildDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'tests/AriaGui.OpenActivationFixture/bin/Debug/net10.0'))
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'OpenActivationFixtureAssertions.psm1') -Force
Assert-OpenCaptureBuild $FixtureBuildDirectory
$scheme='ariagui-open-fixture-'+[guid]::NewGuid().ToString('N')
$root=Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/phase-c/registration-'+[guid]::NewGuid().ToString('N'))
$payload=Join-Path $root 'capture payload with spaces'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
foreach($name in @('AriaGui.App.exe','AriaGui.App.dll','AriaGui.App.deps.json','AriaGui.App.runtimeconfig.json')){
 Copy-Item -LiteralPath (Join-Path $FixtureBuildDirectory $name) -Destination $payload
}
$Executable=Join-Path $payload 'AriaGui.App.exe'
[IO.File]::WriteAllText((Join-Path $payload 'synthetic-open-fixture'),'Owned argument-capture fixture only.')
$script=Join-Path $PSScriptRoot 'Register-OpenProtocol.ps1'
$checks=[Collections.Generic.List[string]]::new();$dispatches=[Collections.Generic.List[object]]::new();$outcome='fail';$failure=$null
function Dispatch([string]$value,[int]$exitCode=0){
 @{Scheme=$scheme;ExitCode=$exitCode} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payload 'fixture.json')
 $receiptPath=Join-Path $payload 'receipt.json'
 if(Test-Path -LiteralPath $receiptPath){Remove-Item -LiteralPath $receiptPath}
 $start=[Diagnostics.ProcessStartInfo]::new($value);$start.UseShellExecute=$true;$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
 $child=[Diagnostics.Process]::Start($start)
 if(!$child){throw 'Shell returned no process; dispatch outcome is unconfirmed.'}
 try{
  if(!$child.WaitForExit(10000)){throw 'Synthetic dispatch exceeded deadline.'}
  $receipt=if(Test-Path -LiteralPath $receiptPath){Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json}else{$null}
  $dispatches.Add([pscustomobject]@{Submitted=$value;ActualExitCode=$child.ExitCode;Receipt=$receipt})
  Assert-OpenDispatchReceipt -ExitCode $child.ExitCode -Receipt $receipt -ProcessId $child.Id -Executable $Executable -ExpectedArguments @($value)
 }finally{if(!$child.HasExited){$child.Kill($true);$child.WaitForExit(5000)|Out-Null};$child.Dispose()}
}
try{
 $plan=& $script -Action register -Executable $Executable -SyntheticScheme $scheme
 if(!$plan.DryRun -or $plan.RegistryModified -or $plan.Registered){throw 'Dry-run isolation failed.'};$checks.Add('dry run writes nothing')
 $result=& $script -Action register -Executable $Executable -SyntheticScheme $scheme -Apply
 if(!$result.Registered){throw 'Synthetic registration failed.'}
 $read=& $script -Action readback -Executable $Executable -SyntheticScheme $scheme -Apply
 if(!$read.Registered -or $read.Command -cne ('"'+$Executable+'" "%1"')){throw 'Exact quoted command readback failed.'}
 $checks.Add('per-user registration and exact quoted readback')
 $duplicate=& $script -Action register -Executable $Executable -SyntheticScheme $scheme -Apply
 if($duplicate.RegistryModified){throw 'Duplicate registration rewrote state.'};$checks.Add('duplicate unchanged')
 foreach($suffix in @('://open','://open/','://open?synthetic=one%20two','://open#fixture','://%6fpen')){
  $value=$scheme+$suffix
  $changed=$false
  try{Dispatch $value}catch{if($_.Exception.Message -cne 'Dispatch receipt exact delivered arguments failed.'){throw};$changed=$true}
  if($changed){$checks.Add('OS changed synthetic vector; exact assertion rejected it: '+$suffix)}else{$checks.Add('exact synthetic shell argument delivery: '+$suffix)}
 }
 # Quotes can split a raw registered command line. Measure the actual boundary; never
 # count a changed vector as successful quoting. A mismatch is an expected rejection.
 $quoted=$scheme+'://open?synthetic="one two"';$refused=$false
 try{Dispatch $quoted}catch{if($_.Exception.Message -cne 'Dispatch receipt exact delivered arguments failed.'){throw};$refused=$true}
 if($refused){$checks.Add('quoted synthetic payload changed argument vector and exact receipt assertion rejected it')}else{$checks.Add('quoted synthetic payload delivered exactly')}
 $refused=$false
 try{Dispatch ($scheme+'://open') 23}catch{if($_.Exception.Message -notmatch 'exit code 23'){throw};$refused=$true}
 if(!$refused){throw 'Deliberately failing child was counted as success.'};$checks.Add('actual nonzero child rejected')
 $path='Software\Classes\'+$scheme
 $leaf=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($path+'\shell\open\command',$true)
 try{$leaf.SetValue('',$read.Command,[Microsoft.Win32.RegistryValueKind]::ExpandString)}finally{$leaf.Dispose()}
 $refused=$false
 try{& $script -Action unregister -Executable $Executable -SyntheticScheme $scheme -Apply | Out-Null}catch{if($_.Exception.Message -notmatch 'not the owned'){throw};$refused=$true}
 if(!$refused){throw 'Changed command type was not rejected.'}
 $leaf=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($path+'\shell\open\command',$true)
 try{if($leaf.GetValueKind('') -ne [Microsoft.Win32.RegistryValueKind]::ExpandString){throw 'Changed type was not preserved.'};$leaf.SetValue('',$read.Command,[Microsoft.Win32.RegistryValueKind]::String)}finally{$leaf.Dispose()}
 $checks.Add('changed value type refused and preserved')
 $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($path,$true)
 try{$key.SetValue('unrelated','preserve')}finally{$key.Dispose()}
 $refused=$false
 try{& $script -Action unregister -Executable $Executable -SyntheticScheme $scheme -Apply | Out-Null}catch{if($_.Exception.Message -notmatch 'not owned'){throw};$refused=$true}
 if(!$refused){throw 'Changed ownership was not rejected.'}
 $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($path,$true)
 try{if($key.GetValue('unrelated') -ne 'preserve'){throw 'Foreign value removed.'};$key.DeleteValue('unrelated')}finally{$key.Dispose()}
 $checks.Add('foreign value refused and preserved')
 $missing=Join-Path $root 'missing target/AriaGui.App.exe';$refused=$false
 try{& $script -Action register -Executable $missing -SyntheticScheme $scheme -Apply | Out-Null}catch{if($_.Exception.Message -notmatch 'existing file'){throw};$refused=$true}
 if(!$refused){throw 'Missing target was accepted.'};$checks.Add('missing registration target refused')
 $newDirectory=Join-Path $root 'changed install path';New-Item -ItemType Directory -Path $newDirectory | Out-Null
 $newExecutable=Join-Path $newDirectory 'AriaGui.App.exe';Copy-Item -LiteralPath $Executable -Destination $newExecutable
 $refused=$false
 try{& $script -Action register -Executable $newExecutable -SyntheticScheme $scheme -Apply | Out-Null}catch{if($_.Exception.Message -notmatch 'not owned'){throw};$refused=$true}
 if(!$refused){throw 'Changed install path overwrote old ownership.'};$checks.Add('old path owner preserved when changed-path update is refused')
 $partialScheme='ariagui-open-fixture-'+[guid]::NewGuid().ToString('N');$partialPath='Software\Classes\'+$partialScheme
 $partial=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($partialPath)
 try{$partial.SetValue('AriaGUI.Open.OwnerExe',$Executable)}finally{$partial.Dispose()}
 try{
  $refused=$false
  try{& $script -Action register -Executable $Executable -SyntheticScheme $partialScheme -Apply | Out-Null}catch{if($_.Exception.Message -notmatch 'not owned'){throw};$refused=$true}
  if(!$refused){throw 'Partial write was blindly repaired.'};$checks.Add('partial write refused and preserved for explicit repair review')
 }finally{
  $partial=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($partialPath)
  try{if($partial.ValueCount -ne 1 -or $partial.SubKeyCount -ne 0 -or $partial.GetValue('AriaGUI.Open.OwnerExe') -cne $Executable){throw 'Partial fixture changed; refusing cleanup.'}}finally{$partial.Dispose()}
  [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($partialPath)
 }
 $backup=$Executable+'.owned-fixture-backup'
 Move-Item -LiteralPath $Executable -Destination $backup
 try{& $script -Action unregister -Executable $Executable -SyntheticScheme $scheme -Apply | Out-Null;$checks.Add('owned registration removed after executable removal')}finally{Move-Item -LiteralPath $backup -Destination $Executable}
 $outcome='pass'
}catch{$failure=$_.Exception.Message;throw}finally{
 try{& $script -Action unregister -Executable $Executable -SyntheticScheme $scheme -Apply | Out-Null}catch{$outcome='fail';$failure=$_.Exception.Message;throw}finally{
  $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes\'+$scheme)
  $cleaned=!$key;if($key){$key.Dispose()}
  if(!$cleaned){$outcome='fail'}
  [ordered]@{Outcome=$outcome;Failure=$failure;Checks=$checks.ToArray();Dispatches=$dispatches.ToArray();Scheme=$scheme;
   ExecutableSha256=(Get-FileHash -LiteralPath $Executable).Hash;AssemblySha256=(Get-FileHash -LiteralPath (Join-Path $payload 'AriaGui.App.dll')).Hash;
   ProductionSchemeModified=$false;RegistryCleaned=$cleaned;AppAcceptance='not tested by capture fixture'} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'registration-evidence.json') -Encoding utf8
 }
}
if(!$cleaned){throw 'Owned synthetic cleanup failed.'}
$checks
