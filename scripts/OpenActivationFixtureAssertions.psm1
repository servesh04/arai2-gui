Set-StrictMode -Version Latest
function Assert-OpenDispatchReceipt {
    param([int]$ExitCode,[object]$Receipt,[int]$ProcessId,[string]$Executable,[string[]]$ExpectedArguments)
    if($ExitCode -ne 0){throw "Dispatch child failed with exit code $ExitCode."}
    if(!$Receipt -or $Receipt.SchemaVersion -ne 1 -or $Receipt.ExitCode -ne 0 -or $Receipt.ProcessId -ne $ProcessId -or
        $Receipt.Executable -cne $Executable){throw 'Dispatch receipt identity failed.'}
    if($Receipt.Arguments.Count -ne $ExpectedArguments.Count){throw 'Dispatch receipt exact delivered arguments failed.'}
    for($i=0;$i -lt $ExpectedArguments.Count;$i++){
        if($Receipt.Arguments[$i] -cne $ExpectedArguments[$i]){throw 'Dispatch receipt exact delivered arguments failed.'}
    }
}
function Assert-OpenRejectedWarm {
    param([bool]$OwnerAlive,[bool]$UnchangedMinimized)
    if(!$OwnerAlive -or !$UnchangedMinimized){throw 'Malformed warm activation was admitted or changed the owner state.'}
}
function Assert-OpenExitCompleted {
    param([bool]$Completed)
    if(!$Completed){throw 'Invoked File/Exit did not complete within deadline.'}
}
function Assert-OpenCanonicalAssociationAbsent {
    foreach($surface in @([Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes\ariagui'),
        [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('Software\Classes\ariagui'),[Microsoft.Win32.Registry]::ClassesRoot.OpenSubKey('ariagui'),
        [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\Shell\Associations\UrlAssociations\ariagui'))){
        if($surface){$surface.Dispose();throw 'Canonical fixture blocked: existing handler/association must be preserved.'}
    }
    foreach($package in Get-AppxPackage){
        $manifest=Get-AppxPackageManifest -Package $package.PackageFullName
        if($manifest.SelectNodes('//*[local-name()="Protocol" and @Name="ariagui"]').Count){throw 'Canonical fixture blocked: installed package owns ariagui.'}
    }
}
function Assert-OpenCaptureBuild {
    param([string]$Directory)
    $bytes=[IO.File]::ReadAllBytes((Join-Path $Directory 'AriaGui.App.dll'))
    $marker=[Text.Encoding]::Unicode.GetBytes('synthetic-open-fixture')
    if(!([BitConverter]::ToString($bytes).Contains([BitConverter]::ToString($marker)))){throw 'Argument-capture fixture build is required; refusing a GUI/production payload.'}
}
Export-ModuleMember -Function Assert-OpenDispatchReceipt,Assert-OpenRejectedWarm,Assert-OpenExitCompleted,Assert-OpenCanonicalAssociationAbsent,Assert-OpenCaptureBuild
