[CmdletBinding()]
param([string]$FixtureDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'tests/AriaGui.OpenActivationFixture/bin/Debug/net10.0'))
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'OpenProtocolInstaller.psm1') -Force
$workspace=Split-Path -Parent $PSScriptRoot
$testsRoot=Join-Path $workspace 'artifacts/open-installer-tests'
$run=Join-Path $workspace ('artifacts/phase-c/installer-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force|Out-Null
$source1=Join-Path $run 'source one';$source2=Join-Path $run 'source two'
foreach($source in @($source1,$source2)){
 New-Item -ItemType Directory -Path $source|Out-Null
 Get-ChildItem -LiteralPath $FixtureDirectory|Copy-Item -Destination $source -Recurse
 [IO.File]::WriteAllText((Join-Path $source 'synthetic-open-fixture'),'owned')
}
[IO.File]::WriteAllText((Join-Path $source1 'version.txt'),'one')
[IO.File]::WriteAllText((Join-Path $source2 'version.txt'),'two')
$checks=[Collections.Generic.List[string]]::new();$scopes=[Collections.Generic.List[object]]::new();$outcome='fail';$failure=$null;$process=$null;$worker=$null
function Scope {
 $scope=[pscustomobject]@{Root=(Join-Path $testsRoot ([guid]::NewGuid().ToString('N')));Scheme=('ariagui-open-fixture-'+[guid]::NewGuid().ToString('N'))}
 $scopes.Add($scope);return $scope
}
function Invoke($scope,[string]$action,[string]$source=$null,[string]$fault='None'){
 Invoke-OpenProtocolInstall -Action $action -SourceDirectory $source -SyntheticWorkspaceRoot $scope.Root -SyntheticScheme $scope.Scheme -FaultPoint $fault -Apply
}
function Check([bool]$value,[string]$name){if(!$value){throw "Installer assertion: $name"};$checks.Add($name)}
function Refuse([scriptblock]$operation,[string]$pattern,[string]$name){$refused=$false;try{& $operation|Out-Null}catch{if($_.Exception.Message -notmatch $pattern){throw};$refused=$true};Check $refused $name}
function Child([string]$code){$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code));Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList @('-NoProfile','-EncodedCommand',$encoded) -WindowStyle Hidden -PassThru}
try{
 $scope=Scope
 $dry=Invoke-OpenProtocolInstall -Action install -SourceDirectory $source1 -SyntheticWorkspaceRoot $scope.Root -SyntheticScheme $scope.Scheme
 Check ($dry.DryRun -and !(Test-Path -LiteralPath $scope.Root)) 'dry run performs no filesystem/registration mutation'
 Invoke $scope install $source1|Out-Null
 $identity=(Get-Content (Join-Path $scope.Root 'receipt.json') -Raw|ConvertFrom-Json).InstallationId
 $stable=Join-Path $scope.Root 'app/AriaGui.App.exe'
 $choicePath='Software\Microsoft\Windows\Shell\Associations\UrlAssociations\'+$scope.Scheme+'\UserChoice'
 $choice=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($choicePath)
 try{$choice.SetValue('ProgId','Synthetic.Foreign');$choice.SetValue('Hash','synthetic-preserve')}finally{$choice.Dispose()}
 Invoke $scope update $source2|Out-Null
 Check ((Get-Content (Join-Path $scope.Root 'app/version.txt') -Raw) -ceq 'two') 'update switches version at stable executable path'
 Check (((Get-Content (Join-Path $scope.Root 'receipt.json') -Raw|ConvertFrom-Json).InstallationId) -ceq $identity) 'update preserves installer identity'
 Invoke $scope rollback|Out-Null
 Check ((Get-Content (Join-Path $scope.Root 'app/version.txt') -Raw) -ceq 'one') 'rollback restores previous payload'
 Invoke $scope update $source1|Out-Null
 Check ((Invoke $scope readback).Owned) 'identical-payload update is an idempotent owned operation'
 $choice=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($choicePath)
 try{Check ($choice.GetValue('ProgId') -ceq 'Synthetic.Foreign' -and $choice.GetValue('Hash') -ceq 'synthetic-preserve') 'foreign synthetic UserChoice remains byte/value unchanged'}finally{$choice.Dispose()}
 $status=Invoke $scope readback
 Check (!$status.DefaultSelected -and $status.Default -ceq 'present-preserved') 'readback never claims default selection'
 $changed=Scope;$changed.Scheme=$scope.Scheme
 Refuse {Invoke $changed install $source1} 'Foreign|changed' 'changed install root cannot steal existing registration'
 Check (!(Test-Path -LiteralPath $changed.Root)) 'changed-path refusal creates no destination'
 $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(('Software\Classes\'+$scope.Scheme),$true)
 try{
  $key.SetValue('Foreign','keep')
  Refuse {Invoke $scope uninstall} 'Foreign|changed' 'foreign registration value blocks uninstall'
  Check ($key.GetValue('Foreign') -ceq 'keep') 'foreign value preserved'
  $key.DeleteValue('Foreign')
  $key.SetValue('AriaGUI.Open.OwnerExe',$stable,[Microsoft.Win32.RegistryValueKind]::ExpandString)
  Refuse {Invoke $scope repair $source1} 'Foreign|changed' 'changed registry type blocks repair'
  Check ($key.GetValueKind('AriaGUI.Open.OwnerExe') -eq [Microsoft.Win32.RegistryValueKind]::ExpandString) 'foreign value type preserved'
  $key.SetValue('AriaGUI.Open.OwnerExe',$stable,[Microsoft.Win32.RegistryValueKind]::String)
 }finally{$key.Dispose()}
 Remove-Item -LiteralPath $stable
 Invoke $scope repair $source1|Out-Null
 Check (Test-Path -LiteralPath $stable) 'repair restores missing executable from exact inventoried source'
 Refuse {Invoke $scope repair $source2} 'Repair source differs' 'repair rejects silently changing version'
 $process=Start-Process -FilePath $stable -ArgumentList '--synthetic-hold' -WindowStyle Hidden -PassThru
 Start-Sleep -Milliseconds 300
 Check (!$process.HasExited) 'bounded owned payload process is running'
 Refuse {Invoke $scope update $source2} 'Owned application is running' 'active installed process blocks update'
 $process.Kill();Check ($process.WaitForExit(5000)) 'owned fixture process cleanup verified';$process.Dispose();$process=$null
 $lockPath=(Join-Path $scope.Root 'installer.lock').Replace("'","''")
 $ready=Join-Path $run 'lock-ready';$readyLiteral=$ready.Replace("'","''")
 $worker=Child "`$lock=[IO.File]::Open('$lockPath',[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);[IO.File]::WriteAllText('$readyLiteral','ready');Start-Sleep -Seconds 30;`$lock.Dispose()"
 for($i=0;$i -lt 50 -and !(Test-Path -LiteralPath $ready);$i++){Start-Sleep -Milliseconds 100}
 Check (Test-Path -LiteralPath $ready) 'separate process holds installer serialization lock'
 Refuse {Invoke $scope update $source2} 'being used by another process' 'concurrent installer cannot mutate owned payload'
 $worker.Kill();Check ($worker.WaitForExit(5000)) 'owned lock worker cleanup verified';$worker.Dispose();$worker=$null
 $lockLink=Join-Path $scope.Root 'installer.lock';$outside=Join-Path $run 'owned junction target'
 New-Item -ItemType Directory -Path $outside|Out-Null
 [IO.File]::WriteAllText((Join-Path $outside 'preserve.txt'),'outside')
 Remove-Item -LiteralPath $lockLink
 New-Item -ItemType Junction -Path $lockLink -Target $outside|Out-Null
 try{
  Refuse {Invoke $scope update $source2} 'Reparse installer' 'installer lock reparse target is refused'
  Check ((Get-Content (Join-Path $outside 'preserve.txt') -Raw) -ceq 'outside') 'lock reparse refusal preserves external target'
 }finally{Remove-Item -LiteralPath $lockLink -Force}
 [IO.File]::WriteAllText((Join-Path $scope.Root 'synthetic-package-conflict'),'fixture')
 Refuse {Invoke $scope update $source2} 'Packaged handler coexistence' 'package conflict fixture refuses shadowing/default changes'
 Remove-Item -LiteralPath (Join-Path $scope.Root 'synthetic-package-conflict')
 $foreignFile=Join-Path $scope.Root 'app/foreign-user-file.txt';[IO.File]::WriteAllText($foreignFile,'preserve')
 Refuse {Invoke $scope uninstall} 'foreign files' 'uninstall refuses unknown payload content'
 Check ((Get-Content $foreignFile -Raw) -ceq 'preserve') 'unknown payload content survives refused uninstall'
 Remove-Item -LiteralPath $foreignFile
 $history=Join-Path $run 'outside-app-history';[IO.File]::WriteAllText($history,'preserve')
 Remove-Item -LiteralPath $stable
 Invoke $scope uninstall|Out-Null
 Check (!(Test-Path -LiteralPath (Join-Path $scope.Root 'app')) -and !(Test-Path -LiteralPath ('HKCU:\Software\Classes\'+$scope.Scheme))) 'uninstall works after executable removal and removes owned association'
 Check ((Get-Content -LiteralPath $history -Raw) -ceq 'preserve') 'outside app data remains unchanged'
 $choice=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($choicePath)
 try{Check ($choice.GetValue('ProgId') -ceq 'Synthetic.Foreign' -and $choice.GetValue('Hash') -ceq 'synthetic-preserve') 'uninstall preserves foreign synthetic default choice'}finally{$choice.Dispose()}
 # Only this fixture's exact UUID choice is removed by test cleanup, never installer code.
 [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree(('Software\Microsoft\Windows\Shell\Associations\UrlAssociations\'+$scope.Scheme))
 foreach($point in @('Journal','Stage','Retiring','Current','RegistryOwner','RegistryTitle','RegistryProtocol','RegistryCommand','Receipt')){
  $faultScope=Scope
  Refuse {Invoke $faultScope install $source1 $point} ('Synthetic interruption: '+$point) ("interruption at $point is surfaced")
  Check ((Invoke $faultScope readback).JournalPending) ("durable intent survives $point interruption")
  Invoke $faultScope recover|Out-Null
  Check (!(Invoke $faultScope readback).JournalPending -and (Test-Path -LiteralPath (Join-Path $faultScope.Root 'app/AriaGui.App.exe'))) ("explicit recovery completes $point")
  Invoke $faultScope uninstall|Out-Null
 }
 foreach($action in @('update','rollback')){
  $faultScope=Scope;Invoke $faultScope install $source1|Out-Null
  if($action -eq 'rollback'){Invoke $faultScope update $source2|Out-Null}
  Refuse {Invoke $faultScope $action $source2 'Retiring'} 'Synthetic interruption' ("$action interrupted after old payload rename")
  Invoke $faultScope recover|Out-Null
  $expected=if($action -eq 'update'){'two'}else{'one'}
  Check ((Get-Content (Join-Path $faultScope.Root 'app/version.txt') -Raw) -ceq $expected) ("$action recovery selects intended payload")
  Invoke $faultScope uninstall|Out-Null
 }
 foreach($point in @('Unregistered','PayloadRemoved')){
  $faultScope=Scope;Invoke $faultScope install $source1|Out-Null
  Refuse {Invoke $faultScope uninstall $null $point} 'Synthetic interruption' ("uninstall interruption at $point")
  Invoke $faultScope recover|Out-Null
  Check (!(Test-Path -LiteralPath (Join-Path $faultScope.Root 'receipt.json')) -and !(Test-Path -LiteralPath ('HKCU:\Software\Classes\'+$faultScope.Scheme))) ("uninstall recovery after $point removes only owned resources")
 }
 $faultScope=Scope
 Refuse {Invoke $faultScope install $source1 'RegistryOwner'} 'Synthetic interruption' 'recovery conflict seed has owned partial registry state'
 $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(('Software\Classes\'+$faultScope.Scheme),$true)
 try{
  $key.SetValue('Foreign','preserve')
  Refuse {Invoke $faultScope recover} 'Foreign|changed' 'recovery refuses foreign additions instead of overwriting'
  Check ($key.GetValue('Foreign') -ceq 'preserve') 'recovery preserves intervening foreign data'
  $key.DeleteValue('Foreign')
 }finally{$key.Dispose()}
 Invoke $faultScope recover|Out-Null;Invoke $faultScope uninstall|Out-Null
 $faultScope=Scope;Invoke $faultScope install $source1|Out-Null
 Remove-Item -LiteralPath (Join-Path $faultScope.Root 'app/AriaGui.App.exe')
 Refuse {Invoke $faultScope repair $source1 'Retiring'} 'Synthetic interruption' 'missing-target repair interrupted after rename'
 Invoke $faultScope recover|Out-Null
 Check (Test-Path -LiteralPath (Join-Path $faultScope.Root 'app/AriaGui.App.exe')) 'repair recovery restores exact target and discards damaged retired copy'
 Invoke $faultScope uninstall|Out-Null
 $crash=Scope;$installer=(Join-Path $PSScriptRoot 'Install-OpenProtocol.ps1').Replace("'","''")
 $worker=Child ("& '$installer' -Action install -SourceDirectory '"+$source1.Replace("'","''")+"' -SyntheticWorkspaceRoot '"+$crash.Root.Replace("'","''")+"' -SyntheticScheme '"+$crash.Scheme+"' -FaultPoint RegistryTitle -FaultMode Exit -Apply")
 Check ($worker.WaitForExit(15000) -and $worker.ExitCode -eq 73) 'actual installer child exits abruptly after flushed partial registry write'
 $worker.Dispose();$worker=$null
 Invoke $crash recover|Out-Null
 Check (!(Invoke $crash readback).JournalPending) 'recovery completes after actual process exit without finally'
 Invoke $crash uninstall|Out-Null
 $outcome='pass'
}catch{$failure=$_.Exception.Message;throw}finally{
 foreach($child in @($process,$worker)){if($child){if(!$child.HasExited){$child.Kill();$child.WaitForExit(5000)|Out-Null};$child.Dispose()}}
 $cleaned=$true
 foreach($scope in $scopes){
  try{
   if(Test-Path -LiteralPath (Join-Path $scope.Root 'journal.json')){Invoke $scope recover|Out-Null}
   if(Test-Path -LiteralPath (Join-Path $scope.Root 'receipt.json')){Invoke $scope uninstall|Out-Null}
   if(Test-Path -LiteralPath ('HKCU:\Software\Classes\'+$scope.Scheme)){$cleaned=$false}
   $association='Software\Microsoft\Windows\Shell\Associations\UrlAssociations\'+$scope.Scheme
   $choice=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($association+'\UserChoice')
   $removeChoice=$false
   try{
    if($choice){
     if($choice.ValueCount -ne 2 -or $choice.SubKeyCount -ne 0 -or $choice.GetValueKind('ProgId') -ne [Microsoft.Win32.RegistryValueKind]::String -or $choice.GetValueKind('Hash') -ne [Microsoft.Win32.RegistryValueKind]::String -or $choice.GetValue('ProgId') -cne 'Synthetic.Foreign' -or $choice.GetValue('Hash') -cne 'synthetic-preserve'){throw 'Synthetic choice changed; refusing fixture cleanup.'}
     $removeChoice=$true
    }
   }finally{if($choice){$choice.Dispose()}}
   if($removeChoice){
    $associationKey=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($association)
    try{if($associationKey.ValueCount -ne 0 -or $associationKey.SubKeyCount -ne 1 -or $associationKey.GetSubKeyNames()[0] -cne 'UserChoice'){throw 'Synthetic association changed; refusing fixture cleanup.'}}finally{$associationKey.Dispose()}
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($association)
   }
  }catch{$cleaned=$false}
 }
 [ordered]@{Outcome=$outcome;Failure=$failure;Checks=$checks.ToArray();OwnedRegistrationsCleaned=$cleaned;CanonicalHandlerTouched=$false
 TestRoot=$run;InstalledReleaseAcceptance='pending'}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $run 'installer-evidence.json') -Encoding utf8
 if(!$cleaned){throw 'Synthetic owned cleanup incomplete; inspect installer evidence.'}
}
