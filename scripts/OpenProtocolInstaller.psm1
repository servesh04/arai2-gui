Set-StrictMode -Version Latest
function Invoke-OpenProtocolInstall {
 [CmdletBinding()]
 param([ValidateSet('install','update','repair','rollback','recover','uninstall','readback')][string]$Action='readback',
 [string]$SourceDirectory,[string]$SyntheticWorkspaceRoot,[string]$SyntheticScheme,[switch]$Apply,
 [ValidateSet('None','Journal','Stage','Retiring','Current','RegistryOwner','RegistryTitle','RegistryProtocol','RegistryCommand','Receipt','Unregistered','PayloadRemoved')][string]$FaultPoint='None',
 [ValidateSet('Throw','Exit')][string]$FaultMode='Throw')
 $ErrorActionPreference='Stop'
 $workspace=Split-Path -Parent $PSScriptRoot
 $synthetic=!!$SyntheticWorkspaceRoot
 if($synthetic){
  $root=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($SyntheticWorkspaceRoot))
  $allowed=[IO.Path]::GetFullPath((Join-Path $workspace 'artifacts/open-installer-tests'))
  if([IO.Path]::GetDirectoryName($root) -cne $allowed -or [IO.Path]::GetFileName($root) -cnotmatch '^[a-f0-9]{32}$' -or
   $SyntheticScheme -cnotmatch '^ariagui-open-fixture-[a-f0-9]{32}$'){throw 'Synthetic root and UUID scheme required.'}
  $scheme=$SyntheticScheme
 }else{
  if($SyntheticScheme -or $FaultPoint -ne 'None' -or $FaultMode -ne 'Throw'){throw 'Fixture options require synthetic mode.'}
  $root=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AriaGUI\OpenOnly'
  $scheme='ariagui'
 }
 $current=Join-Path $root 'app';$stage=Join-Path $root 'stage';$retiring=Join-Path $root 'retiring';$previous=Join-Path $root 'previous'
 $receiptPath=Join-Path $root 'receipt.json';$previousPath=Join-Path $root 'previous.json';$journalPath=Join-Path $root 'journal.json'
 $target=Join-Path $current 'AriaGui.App.exe';$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
 $keyPath='Software\Classes\'+$scheme;$command='"'+$target+'" "%1"'
 function Assert-Path([string]$path){
  $absolute=[IO.Path]::GetFullPath($path)
  if($absolute -cne $root -and !$absolute.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Installer path escaped owned root.'}
  for($item=[IO.DirectoryInfo]::new($absolute);$item;$item=$item.Parent){if($item.Exists -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reparse installer path forbidden.'}}
  if(Test-Path -LiteralPath $absolute){if((Get-Item -LiteralPath $absolute).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse installer target forbidden.'}}
  return $absolute
 }
 function Read-Json([string]$path){
  $null=Assert-Path $path
  if(!(Test-Path -LiteralPath $path)){return $null}
  if((Get-Item -LiteralPath $path).Length -gt 2097152){throw 'Oversized installer metadata.'}
  return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
 }
 function Write-Json([string]$path,$value){
  $temporary=Assert-Path ($path+'.tmp');$null=Assert-Path $path
  $bytes=[Text.Encoding]::UTF8.GetBytes(($value|ConvertTo-Json -Depth 12 -Compress))
  $file=[IO.File]::Open($temporary,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
  try{$file.Write($bytes,0,$bytes.Length);$file.Flush($true)}finally{$file.Dispose()}
  [IO.File]::Move($temporary,$path,$true)
 }
 function Inventory([string]$directory){
  $full=[IO.Path]::GetFullPath($directory)
  if(!(Test-Path -LiteralPath $full -PathType Container)){throw 'Payload directory missing.'}
  if((Get-Item -LiteralPath $full).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse source forbidden.'}
  $files=@(Get-ChildItem -LiteralPath $full -Recurse -Force)
  if($files.Count -gt 4096 -or @($files|Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count){throw 'Oversized or reparse payload forbidden.'}
  $total=0L;$result=@(foreach($file in $files|Where-Object {!$_.PSIsContainer}|Sort-Object FullName){
   $total+=$file.Length;if($total -gt 1073741824){throw 'Payload exceeds one GiB.'}
   [pscustomobject]@{Path=[IO.Path]::GetRelativePath($full,$file.FullName);Sha256=(Get-FileHash -LiteralPath $file.FullName).Hash}
  })
  if(!@($result|Where-Object Path -CEQ 'AriaGui.App.exe').Count){throw 'Payload requires AriaGui.App.exe.'}
  return ,$result
 }
 function Validate-Receipt($value){
  if(!$value){return}
  if($value.Schema -ne 1 -or $value.OwnerSid -cne $sid -or $value.Root -cne $root -or $value.Scheme -cne $scheme -or !$value.InstallationId){throw 'Foreign installer receipt.'}
  $names=@{}
  foreach($entry in $value.Inventory){
   if(!$entry.Path -or [IO.Path]::IsPathRooted($entry.Path) -or $entry.Path -match '[":\x00-\x1f]' -or ($entry.Path -split '[\\/]') -contains '..' -or $entry.Sha256 -cnotmatch '^[A-F0-9]{64}$' -or $names.ContainsKey($entry.Path)){throw 'Invalid receipt inventory.'}
   $names[$entry.Path]=$true
  }
  if(!$names.ContainsKey('AriaGui.App.exe')){throw 'Receipt lacks GUI executable.'}
 }
 function Assert-Payload([string]$directory,$value,[bool]$subset=$false){
  $null=Assert-Path $directory;Validate-Receipt $value
  if(!$value){if(Test-Path -LiteralPath $directory){throw 'Unowned payload directory.'};return}
  if(!(Test-Path -LiteralPath $directory)){if($subset){return};throw 'Owned payload missing.'}
  $expected=@{};$directories=@{};foreach($entry in $value.Inventory){$expected[$entry.Path]=$entry.Sha256;$parent=[IO.Path]::GetDirectoryName($entry.Path);while($parent){$directories[$parent]=$true;$parent=[IO.Path]::GetDirectoryName($parent)}}
  $seen=0
  foreach($file in Get-ChildItem -LiteralPath $directory -Recurse -Force){
   if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse payload forbidden.'}
   $relative=[IO.Path]::GetRelativePath($directory,$file.FullName)
   if($file.PSIsContainer){if(!$directories.ContainsKey($relative)){throw 'Foreign payload directory.'};continue}
   if(!$expected.ContainsKey($relative) -or (Get-FileHash -LiteralPath $file.FullName).Hash -cne $expected[$relative]){throw 'Payload changed or contains foreign files.'};$seen++
  }
  if(!$subset -and $seen -ne $expected.Count){throw 'Owned payload incomplete.'}
 }
 function Remove-Payload([string]$directory,$value){Assert-Payload $directory $value $true;if(Test-Path -LiteralPath $directory){Remove-Item -LiteralPath (Assert-Path $directory) -Recurse -Force}}
 function Assert-Inactive {
  foreach($process in [Diagnostics.Process]::GetProcesses()){
   try{if($process.MainModule.FileName.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw [InvalidOperationException]::new('Owned application is running; exit before installer changes.')}}
   catch [InvalidOperationException]{if($_.Exception.Message -like 'Owned application*'){throw}}
   catch{}finally{$process.Dispose()}
  }
 }
 function Registry-State([bool]$partial=$false){
  $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($keyPath)
  if(!$key){return 'absent'}
  try{
   $expected=@{'AriaGUI.Open.OwnerExe'=$target;''='URL:Open AriaGUI';'URL Protocol'=''}
   foreach($name in $key.GetValueNames()){if(!$expected.ContainsKey($name) -or $key.GetValueKind($name) -ne [Microsoft.Win32.RegistryValueKind]::String -or $key.GetValue($name) -cne $expected[$name]){throw 'Foreign or changed protocol registration.'}}
   function Check-Branch($branch,[string]$path){
    if(!$branch){return}
    try{
     if($path -eq 'command'){
      if($branch.SubKeyCount -or $branch.ValueCount -gt 1 -or ($branch.ValueCount -and ($branch.GetValueNames()[0] -cne '' -or $branch.GetValueKind('') -ne [Microsoft.Win32.RegistryValueKind]::String -or $branch.GetValue('') -cne $command))){throw 'Foreign protocol command.'}
     }else{
      $child=if($path -eq 'shell'){'open'}else{'command'}
      if($branch.ValueCount -or @($branch.GetSubKeyNames()|Where-Object {$_ -cne $child}).Count){throw 'Foreign protocol branch.'}
      Check-Branch ($branch.OpenSubKey($child)) $child
     }
    }finally{$branch.Dispose()}
   }
   if(@($key.GetSubKeyNames()|Where-Object {$_ -cne 'shell'}).Count){throw 'Foreign protocol subkey.'}
   Check-Branch ($key.OpenSubKey('shell')) 'shell'
   if($partial){return 'compatible-partial'}
   $leaf=$key.OpenSubKey('shell\open\command')
   try{if($key.ValueCount -ne 3 -or !$leaf -or $leaf.ValueCount -ne 1){throw 'Protocol write incomplete; journaled recovery required.'}}finally{if($leaf){$leaf.Dispose()}}
   return 'owned'
  }finally{$key.Dispose()}
 }
 function Default-State {
  $choice=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\Shell\Associations\UrlAssociations\'+$scheme+'\UserChoice')
  try{return $(if($choice){'present-preserved'}else{'absent-not-selected'})}finally{if($choice){$choice.Dispose()}}
 }
 function Assert-NoConflict {
  $machine=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($keyPath)
  try{if($machine){throw 'Machine handler conflict; no replacement.'}}finally{if($machine){$machine.Dispose()}}
  if($synthetic){if(Test-Path -LiteralPath (Join-Path $root 'synthetic-package-conflict')){throw 'Packaged handler coexistence requires explicit Windows default choice; installer refuses shadowing.'}}
  else{foreach($package in Get-AppxPackage){$manifest=$package|Get-AppxPackageManifest;if(@($manifest.SelectNodes("//*[local-name()='Protocol' and @Name='ariagui']")).Count){throw 'Packaged handler coexistence requires explicit Windows default choice; installer refuses shadowing.'}}}
 }
 function Fault([string]$point){if($synthetic -and $FaultPoint -eq $point){if($FaultMode -eq 'Exit'){[Environment]::Exit(73)};throw "Synthetic interruption: $point"}}
 function Set-Registration([bool]$present){
  $null=Registry-State $true
  if(!$present){[Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($keyPath,$false);return}
  Assert-NoConflict
  $key=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($keyPath)
  try{
   $key.SetValue('AriaGUI.Open.OwnerExe',$target,[Microsoft.Win32.RegistryValueKind]::String);$key.Flush();Fault 'RegistryOwner'
   $key.SetValue('','URL:Open AriaGUI',[Microsoft.Win32.RegistryValueKind]::String);$key.Flush();Fault 'RegistryTitle'
   $key.SetValue('URL Protocol','',[Microsoft.Win32.RegistryValueKind]::String);$key.Flush();Fault 'RegistryProtocol'
   $leaf=$key.CreateSubKey('shell\open\command');try{$leaf.SetValue('',$command,[Microsoft.Win32.RegistryValueKind]::String);$leaf.Flush()}finally{$leaf.Dispose()};Fault 'RegistryCommand'
  }finally{$key.Dispose()}
  $null=Registry-State
 }
 $null=Assert-Path $root
 $before=Read-Json $receiptPath;$backup=Read-Json $previousPath;$journal=Read-Json $journalPath
 Validate-Receipt $before;Validate-Receipt $backup
 if((Test-Path -LiteralPath $root) -and !$before -and !$journal){
  if(@(Get-ChildItem -LiteralPath $root -Force|Where-Object Name -NotIn @('installer.lock','synthetic-package-conflict')).Count){throw 'Existing root has no ownership receipt/journal.'}
 }
 if($journal -and $Action -ne 'recover' -and $Action -ne 'readback'){throw 'Pending journal requires explicit recover.'}
 if(!$journal){$registry=Registry-State;Assert-Payload $current $before ($Action -in @('repair','uninstall'));Assert-Payload $previous $backup}
 else{$registry=Registry-State $true}
 if($Action -eq 'readback'){return [pscustomobject]@{Root=$root;Scheme=$scheme;Owned=!!$before;JournalPending=!!$journal;Registration=$registry;Default=Default-State;DefaultSelected=$false;PackageModel='unpackaged';ReviewCandidate=$true}}
 if(!$journal -and $Action -eq 'recover'){throw 'No pending operation.'}
 if(!$journal){
  if($Action -eq 'install' -and $before){throw 'Already installed; use update or repair.'}
  if(!$before -and $registry -ne 'absent'){throw 'Registration has no installer ownership receipt.'}
  if($Action -in @('update','repair','rollback') -and !$before){throw 'No owned installation.'}
  $after=$null
  if($Action -ne 'uninstall'){
   Assert-NoConflict
   if($Action -eq 'rollback'){if(!$backup){throw 'No previous version.'};$source=$previous;$after=$backup}
   else{
    if(!$SourceDirectory){throw 'Source directory required.'};$source=(Resolve-Path -LiteralPath $SourceDirectory).Path
    if($source -ieq $root -or $source.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Use an external immutable source bundle.'}
    $inventory=Inventory $source
    $after=[pscustomobject]@{Schema=1;OwnerSid=$sid;Root=$root;Scheme=$scheme;InstallationId=$(if($before){$before.InstallationId}else{[guid]::NewGuid().ToString('N')});Inventory=$inventory}
    if($Action -eq 'repair' -and (($before.Inventory|ConvertTo-Json -Compress) -cne ($inventory|ConvertTo-Json -Compress))){throw 'Repair source differs; use update.'}
   }
  }
  $journal=[pscustomobject]@{Schema=1;OwnerSid=$sid;Root=$root;Scheme=$scheme;Action=$Action;Source=$(if($after){$source}else{$null});Before=$before;BeforeBackup=$backup;After=$after}
 }
 if($journal.Schema -ne 1 -or $journal.OwnerSid -cne $sid -or $journal.Root -cne $root -or $journal.Scheme -cne $scheme -or $journal.Action -notin @('install','update','repair','rollback','uninstall')){throw 'Invalid operation journal.'}
 Validate-Receipt $journal.Before;Validate-Receipt $journal.BeforeBackup;Validate-Receipt $journal.After
 Assert-Inactive
 if(!$Apply){return [pscustomobject]@{DryRun=$true;Root=$root;Scheme=$scheme;Action=$journal.Action;Default=Default-State;DefaultSelected=$false}}
 if(!(Test-Path -LiteralPath $root)){
  New-Item -ItemType Directory -Path $root|Out-Null
  $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
  foreach($owner in @($sid,'S-1-5-18')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($owner),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
  Set-Acl -LiteralPath $root -AclObject $acl
 }
 $lock=[IO.File]::Open((Assert-Path (Join-Path $root 'installer.lock')),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
 try{
  # Recheck state after serialization: cooperating installers may have completed meanwhile.
  $fresh=Read-Json $journalPath
  if($fresh){if($Action -ne 'recover'){throw 'Pending journal requires explicit recover.'};$journal=$fresh}
  elseif($Action -eq 'recover'){throw 'Pending operation changed.'}
  elseif((($before|ConvertTo-Json -Depth 12 -Compress) -cne ((Read-Json $receiptPath)|ConvertTo-Json -Depth 12 -Compress))){throw 'Installation changed during preflight.'}
  if(!$fresh){Write-Json $journalPath $journal};Fault 'Journal'
  if($journal.Action -eq 'uninstall'){
   Set-Registration $false;Fault 'Unregistered'
   Remove-Payload $current $journal.Before;Remove-Payload $previous $journal.BeforeBackup;Fault 'PayloadRemoved'
   foreach($path in @($receiptPath,$previousPath)){if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath (Assert-Path $path)}}
  }else{
   Assert-NoConflict
   $after=$journal.After;$old=$journal.Before
   $newCurrent=$false
   try{Assert-Payload $current $after;$newCurrent=$true}catch{}
   if(!$newCurrent){
    Assert-Payload $stage $after $true
    if(!(Test-Path -LiteralPath $stage)){New-Item -ItemType Directory -Path $stage|Out-Null}
   }
   if(!$newCurrent){
    $sourceInventory=Inventory $journal.Source
    if(($sourceInventory|ConvertTo-Json -Compress) -cne ($after.Inventory|ConvertTo-Json -Compress)){throw 'Recovery source changed; preserve journal for review.'}
    foreach($entry in $after.Inventory){$destination=Assert-Path (Join-Path $stage $entry.Path);[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))|Out-Null;[IO.File]::Copy((Join-Path $journal.Source $entry.Path),$destination,$true)}
    Assert-Payload $stage $after;Fault 'Stage'
    if(Test-Path -LiteralPath $retiring){Assert-Payload $retiring $old ($journal.Action -eq 'repair')}else{
     Assert-Payload $current $old ($journal.Action -eq 'repair')
     if(Test-Path -LiteralPath $current){Move-Item -LiteralPath (Assert-Path $current) -Destination (Assert-Path $retiring)}
    };Fault 'Retiring'
    Move-Item -LiteralPath (Assert-Path $stage) -Destination (Assert-Path $current);Fault 'Current'
   }
   if(Test-Path -LiteralPath $retiring){
    Assert-Payload $retiring $old ($journal.Action -eq 'repair')
    if($journal.Action -eq 'repair'){
     # A damaged old payload is not a usable rollback candidate.
     Remove-Payload $retiring $old
    }else{
     Remove-Payload $previous $journal.BeforeBackup
     Move-Item -LiteralPath (Assert-Path $retiring) -Destination (Assert-Path $previous)
     Write-Json $previousPath $old
    }
   }elseif($journal.Action -in @('update','rollback') -and $old -and
    (($old.Inventory|ConvertTo-Json -Compress) -cne ($after.Inventory|ConvertTo-Json -Compress))){
    Assert-Payload $previous $old;Write-Json $previousPath $old
   }
   Set-Registration $true
   Write-Json $receiptPath $after;Fault 'Receipt'
  }
  Remove-Item -LiteralPath (Assert-Path $journalPath)
  return [pscustomobject]@{Action=$journal.Action;Root=$root;Scheme=$scheme;Committed=$true;Default=Default-State;DefaultSelected=$false;ReviewCandidate=$true}
 }finally{$lock.Dispose()}
}
Export-ModuleMember -Function Invoke-OpenProtocolInstall
