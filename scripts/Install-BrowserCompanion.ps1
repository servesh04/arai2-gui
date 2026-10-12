[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('install','repair','uninstall','readback')][string]$Action,
      [Parameter(Mandatory)][ValidateSet('synthetic')][string]$Channel,
      [Parameter(Mandatory)][string]$SyntheticWorkspaceRoot,[Parameter(Mandatory)][string]$InstallRoot,
      [string]$SourceBundle,[switch]$Apply,[switch]$SyntheticFailAfterSwitch)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'BrowserRegistration.psm1') -Force
$workspace=Split-Path -Parent $PSScriptRoot
if(!(Test-Path -LiteralPath (Join-Path $workspace 'AriaGui.slnx') -PathType Leaf)){throw 'Synthetic installer must run from the source checkout scripts directory.'}
$allowed=[IO.Path]::GetFullPath((Join-Path $workspace 'artifacts/browser-installer-tests'))
$scope=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($SyntheticWorkspaceRoot))
if([IO.Path]::GetDirectoryName($scope) -ne $allowed -or [IO.Path]::GetFileName($scope) -notmatch '^[a-f0-9]{32}$'){throw 'Use an explicitly selected UUID workspace under artifacts/browser-installer-tests.'}
$root=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($InstallRoot))
if([IO.Path]::GetDirectoryName($root) -ne $scope -or [IO.Path]::GetFileName($root) -in @('', '.', '..') -or $root -match '[\x00-\x1f]'){throw 'Install root must be one named payload directory inside the selected synthetic workspace.'}
function Assert-Target([string]$Path){
    $absolute=[IO.Path]::GetFullPath($Path)
    if(!$absolute.StartsWith($scope+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Mutation target escaped synthetic installation workspace.'}
    for($item=[IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($absolute));$item;$item=$item.Parent){if($item.Exists -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Reparse mutation ancestor is forbidden.'}}
    if(Test-Path -LiteralPath $absolute){$item=Get-Item -LiteralPath $absolute;if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Reparse mutation target is forbidden.'}}
    return $absolute
}
function Set-Private([string]$Path){$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User;$acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false);$acl.SetOwner($sid);$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,[Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow));Set-Acl -LiteralPath $Path -AclObject $acl}
function Assert-Inactive{
    foreach($process in [Diagnostics.Process]::GetProcesses()){
        try{$path=$process.MainModule.FileName;if($path -and $path.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw [InvalidOperationException]::new('Active companion process: close it before repair/uninstall.')}}
        catch [InvalidOperationException]{if($_.Exception.Message -like 'Active companion*'){throw}}
        catch{}finally{$process.Dispose()}
    }
}
function Remove-Verified([string]$Path){$target=Assert-Target $Path;$null=Test-BrowserBundle -InstallRoot $target;Remove-Item -LiteralPath $target -Recurse -Force}
$receipt=Join-Path $scope 'installation-receipt.json'
function Read-CurrentInstallation {
    $exists=Test-Path -LiteralPath $root;$bytes=$null;$metadata=$null
    $null=Assert-Target $root;$null=Assert-Target $receipt
    if($exists){
        if(!(Test-Path -LiteralPath $receipt -PathType Leaf) -or (Get-Item -LiteralPath $receipt).Length -gt 4096){throw 'Existing payload has no bounded ownership receipt.'}
        $bytes=[IO.File]::ReadAllBytes($receipt);$metadata=[Text.Encoding]::UTF8.GetString($bytes)|ConvertFrom-Json
        if($metadata.schemaVersion -ne 1 -or $metadata.installRoot -cne $root -or $metadata.ownerSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -or $metadata.channel -ne $Channel -or $metadata.inventorySha256 -ne (Get-FileHash -LiteralPath (Join-Path $root 'browser-bundle.json')).Hash){throw 'Installation receipt does not own this payload.'}
        $null=Test-BrowserBundle -InstallRoot $root
    }elseif(Test-Path -LiteralPath $receipt){throw 'Receipt exists without payload; manual verified repair required.'}
    return [pscustomobject]@{Exists=$exists;Bytes=$bytes;Metadata=$metadata}
}
function Read-Source {
    if(!$SourceBundle){throw 'Install/repair requires a verified source bundle.'}
    $source=(Resolve-Path -LiteralPath $SourceBundle).Path;$inventory=Test-BrowserBundle -InstallRoot $source
    if($inventory.channel -ne 'synthetic'){throw 'Applied companion staging accepts synthetic bundles only.'}
    return [pscustomobject]@{Path=$source;Inventory=$inventory}
}
$preflight=Read-CurrentInstallation
$sourcePreflight=if($Action -in @('install','repair')){Read-Source}else{$null}
if(!$Apply){Assert-Inactive;return [pscustomobject]@{DryRun=$true;Action=$Action;InstallRoot=$root;ExistingOwnedPayload=$preflight.Exists;SourceVerified=$null -ne $sourcePreflight;ProductionEnabled=$false;RegistryModified=$false}}
if(!(Test-Path -LiteralPath $scope)){New-Item -ItemType Directory -Path $scope -Force | Out-Null;Set-Private $scope}
$null=Assert-Target $root
$lockPath=Assert-Target (Join-Path $scope 'operation.lock')
$lock=[IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$stage=$null;$backup=$null;$switched=$false;$oldReceipt=$null;$oldExists=Test-Path -LiteralPath $root
try{
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $current=Read-CurrentInstallation;$oldExists=$current.Exists;$oldReceipt=$current.Bytes;$owned=$current.Metadata
    if($Action -eq 'readback'){return [pscustomobject]@{Installed=$oldExists;InstallRoot=$root;ProductionEnabled=$false;Connected=$false}}
    Assert-Inactive
    if($Action -eq 'uninstall'){
        if($oldExists){Remove-Verified $root;$null=Assert-Target $receipt;Remove-Item -LiteralPath $receipt}
        return [pscustomobject]@{Uninstalled=$true;RegistryModified=$false;HistoryPreserved=$true}
    }
    $verifiedSource=Read-Source;$source=$verifiedSource.Path;$inventory=$verifiedSource.Inventory
    $stage=Assert-Target (Join-Path $scope ('stage-'+[guid]::NewGuid().ToString('N')))
    Copy-Item -LiteralPath $source -Destination $stage -Recurse
    Set-Private $stage
    $null=Test-BrowserBundle -InstallRoot $stage # catches source mutation during copying before permitted rewrites
    foreach($browser in @('chrome','edge','firefox')){
        $manifest=Join-Path $stage ('native/'+$browser+'.json');$native=Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        $native.path=Join-Path $root 'host/AriaGui.NativeHost.exe';$native | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifest -Encoding utf8
    }
    $inventory.files=@(Get-ChildItem -LiteralPath $stage -File -Recurse | Where-Object {$_.FullName -ne (Join-Path $stage 'browser-bundle.json')} | Sort-Object FullName | ForEach-Object {[ordered]@{path=[IO.Path]::GetRelativePath($stage,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
    $inventory | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage 'browser-bundle.json') -Encoding utf8
    $null=Test-BrowserBundle -InstallRoot $stage
    Assert-Inactive
    if($oldExists){$backup=Assert-Target (Join-Path $scope ('backup-'+[guid]::NewGuid().ToString('N')));$null=Assert-Target $root;Move-Item -LiteralPath $root -Destination $backup}
    $null=Assert-Target $stage;$null=Assert-Target $root;Move-Item -LiteralPath $stage -Destination $root;$stage=$null;$switched=$true
    if($SyntheticFailAfterSwitch){throw 'Synthetic switch failure.'}
    $newReceipt=[ordered]@{schemaVersion=1;installRoot=$root;ownerSid=$sid;channel=$Channel;companionInstallationId=if($oldExists){$owned.companionInstallationId}else{[guid]::NewGuid().ToString('D')};inventorySha256=(Get-FileHash -LiteralPath (Join-Path $root 'browser-bundle.json')).Hash}
    $temporary=Assert-Target (Join-Path $scope ('receipt-'+[guid]::NewGuid().ToString('N')+'.tmp'))
    try{$newReceipt|ConvertTo-Json|Set-Content -LiteralPath $temporary -Encoding utf8;$null=Assert-Target $receipt;Move-Item -LiteralPath $temporary -Destination $receipt -Force}finally{if(Test-Path -LiteralPath $temporary){$null=Assert-Target $temporary;Remove-Item -LiteralPath $temporary}}
    if($backup){Remove-Verified $backup;$backup=$null}
    return [pscustomobject]@{Installed=$true;InstallRoot=$root;CompanionInstallationId=$newReceipt.companionInstallationId;ProductionEnabled=$false;RegistryModified=$false}
}catch{
    if($switched){Remove-Verified $root}
    if($backup){$null=Assert-Target $backup;$null=Assert-Target $root;Move-Item -LiteralPath $backup -Destination $root;$backup=$null}
    if($oldReceipt){$null=Assert-Target $receipt;[IO.File]::WriteAllBytes($receipt,$oldReceipt)}elseif($switched -and (Test-Path -LiteralPath $receipt)){$null=Assert-Target $receipt;Remove-Item -LiteralPath $receipt}
    throw
}finally{
    if($stage -and (Test-Path -LiteralPath $stage)){Remove-Verified $stage}
    $lock.Dispose()
}
