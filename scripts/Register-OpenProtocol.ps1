[CmdletBinding()]
param(
    [ValidateSet('register','unregister','readback')][string]$Action='readback',
    [Parameter(Mandatory)][string]$Executable,
    [string]$SyntheticScheme,
    [switch]$Apply
)
$ErrorActionPreference='Stop'
$scheme=if($SyntheticScheme){
    if($SyntheticScheme -cnotmatch '^ariagui-open-fixture-[a-f0-9]{32}$'){throw 'Synthetic scheme must be a fresh fixture UUID.'}
    $SyntheticScheme
}else{'ariagui'}
$target=[IO.Path]::GetFullPath($Executable)
if([IO.Path]::GetFileName($target) -cne 'AriaGui.App.exe' -or ($Action -eq 'register' -and !(Test-Path -LiteralPath $target -PathType Leaf)) -or $target -match '["\x00-\x1f]'){throw 'A stable AriaGui.App.exe path is required; registration requires an existing file.'}
$command='"'+$target+'" "%1"'
$registryPath='Software\Classes\'+$scheme
function Read-Owned {
    $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registryPath)
    if(!$key){return $false}
    try {
        if($key.GetValue('AriaGUI.Open.OwnerExe') -cne $target -or $key.GetValue('URL Protocol') -cne '' -or
            $key.GetValue('') -cne 'URL:Open AriaGUI' -or $key.ValueCount -ne 3 -or $key.SubKeyCount -ne 1){throw 'Existing protocol registration is not owned by this exact executable.'}
        foreach($name in @('','URL Protocol','AriaGUI.Open.OwnerExe')){
            if($key.GetValueKind($name) -ne [Microsoft.Win32.RegistryValueKind]::String){throw 'Existing protocol registration is not owned with exact string value types.'}
        }
        $shell=$key.OpenSubKey('shell');$open=$key.OpenSubKey('shell\open');$leaf=$key.OpenSubKey('shell\open\command')
        try {
            if(!$shell -or !$open -or !$leaf -or $shell.ValueCount -ne 0 -or $shell.SubKeyCount -ne 1 -or
                $open.ValueCount -ne 0 -or $open.SubKeyCount -ne 1 -or $leaf.ValueCount -ne 1 -or $leaf.SubKeyCount -ne 0 -or
                $leaf.GetValue('') -cne $command -or $leaf.GetValueKind('') -ne [Microsoft.Win32.RegistryValueKind]::String){throw 'Existing protocol command is not the owned exact quoted command.'}
        }finally{if($leaf){$leaf.Dispose()};if($open){$open.Dispose()};if($shell){$shell.Dispose()}}
        return $true
    }finally{$key.Dispose()}
}
$owned=Read-Owned
$machine=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($registryPath)
try{if($machine -and $Action -eq 'register'){throw 'Machine protocol registration exists; this script does not replace it.'}}finally{if($machine){$machine.Dispose()}}
if(!$Apply -or $Action -eq 'readback'){
    return [pscustomobject]@{DryRun=!$Apply;Action=$Action;Scheme=$scheme;Executable=$target;Command=$command;Registered=$owned;PackageModel='unpackaged';RegistryModified=$false;DefaultHandlerVerified=$false}
}
if($Action -eq 'unregister'){
    if($owned){$null=Read-Owned;[Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($registryPath)}
    return [pscustomobject]@{Scheme=$scheme;Removed=$owned;HistoryPreserved=$true}
}
if(!$owned){
    $key=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($registryPath)
    try {
        if($key.ValueCount -ne 0 -or $key.SubKeyCount -ne 0){throw 'Protocol registration changed during preflight; refusing replacement.'}
        $key.SetValue('AriaGUI.Open.OwnerExe',$target,[Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('','URL:Open AriaGUI',[Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('URL Protocol','',[Microsoft.Win32.RegistryValueKind]::String)
        $leaf=$key.CreateSubKey('shell\open\command')
        try{$leaf.SetValue('',$command,[Microsoft.Win32.RegistryValueKind]::String)}finally{$leaf.Dispose()}
    }finally{$key.Dispose()}
}
if(!(Read-Owned)){throw 'Protocol registration readback failed.'}
[pscustomobject]@{Scheme=$scheme;Registered=$true;Command=$command;RegistryModified=!$owned;HistoryPreserved=$true;DefaultHandlerVerified=$false}
