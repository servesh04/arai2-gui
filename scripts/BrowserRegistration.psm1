Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-BrowserInstallRoot([string]$Path) {
    if (!$Path -or ![IO.Path]::IsPathFullyQualified($Path) -or $Path.StartsWith('\\') -or $Path.Length -gt 1024 -or $Path -match '[\x00-\x1f]') { throw 'Use an absolute local installation root.' }
    $root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    $directory = [IO.DirectoryInfo]::new($root)
    if (!$directory.Exists) { throw 'Installation root is missing.' }
    for ($ancestor = $directory; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse installation paths are not supported.' }
    }
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $allowed = @($sid, 'S-1-5-18', 'S-1-5-32-544')
    $security = Get-Acl -LiteralPath $root
    if ($security.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $allowed) { throw 'Installation root owner is not trusted.' }
    $write = [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
    foreach ($rule in $security.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and ($rule.FileSystemRights -band $write) -ne 0 -and $rule.IdentityReference.Value -notin $allowed) { throw 'Installation root is broadly writable.' }
    }
    return $root
}

function Test-BrowserBundle([Parameter(Mandatory)][string]$InstallRoot) {
    $root = Assert-BrowserInstallRoot $InstallRoot
    $inventoryPath = Join-Path $root 'browser-bundle.json'
    if (!(Test-Path -LiteralPath $inventoryPath -PathType Leaf) -or (Get-Item -LiteralPath $inventoryPath).Length -gt 1048576) { throw 'Browser bundle inventory is missing or oversized.' }
    $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    if ($inventory.schemaVersion -ne 1 -or $inventory.channel -notin @('development','synthetic') -or $inventory.productionEnabled -ne $false -or @($inventory.files).Count -eq 0) { throw 'Unsupported or enabled browser bundle.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $inventory.files) {
        if (!$file.path -or [IO.Path]::IsPathRooted($file.path) -or $file.path -match '[:\x00-\x1f]|(^|[\/])\.{1,2}([\/]|$)' -or !$seen.Add($file.path.Replace('/','\')) -or $file.sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Unsafe bundle inventory entry.' }
        $target = [IO.Path]::GetFullPath((Join-Path $root $file.path))
        if (!$target.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Bundle entry escaped root.' }
        $item = Get-Item -LiteralPath $target
        if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256) { throw 'Browser bundle integrity failed.' }
    }
    # Unknown files can introduce unintended private data or executable dependencies.
    $all = @(Get-ChildItem -LiteralPath $root -Recurse)
    if (@($all | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count) { throw 'Reparse bundle content is forbidden.' }
    $actual = @($all | Where-Object { !$_.PSIsContainer -and $_.FullName -ne $inventoryPath })
    if ($actual.Count -ne $seen.Count -or @($actual | Where-Object { !$seen.Contains([IO.Path]::GetRelativePath($root,$_.FullName)) }).Count) { throw 'Browser bundle contains untracked files.' }
    if (@($actual | Where-Object { $_.Name -match '(?i)\.(db|db-wal|db-shm|log|dmp|tmp)$|^(preferences\.json|aria2\.conf|browser-installation-v1\.txt|native-host\.v1\.json)$' }).Count) { throw 'Private/runtime configuration cannot be packaged.' }
    return $inventory
}

function New-BrowserRegistrationPlan {
    [CmdletBinding()]
    param([Parameter(Mandatory)][ValidateSet('development','synthetic')][string]$Channel,
          [Parameter(Mandatory)][ValidateSet('chrome','edge','firefox')][string[]]$Browser,
          [Parameter(Mandatory)][string]$InstallRoot,
          [string]$SyntheticRegistryRoot)
    $root = Assert-BrowserInstallRoot $InstallRoot
    $bundle = Test-BrowserBundle $root
    if ($bundle.channel -ne $Channel) { throw 'Channel differs from the selected bundle.' }
    if ($Channel -eq 'synthetic') {
        if ($SyntheticRegistryRoot -notmatch '^Software\\AriaGUI\\Verification\\[a-f0-9]{32}$' -or $bundle.hostName -notmatch '^com\.ariagui\.verify_[a-f0-9]{32}$') { throw 'Synthetic registry namespace/host required.' }
    } elseif ($SyntheticRegistryRoot -or $bundle.hostName -ne 'com.ariagui.browser.development') { throw 'Development identity differs.' }
    $roots = @{ chrome='Software\Google\Chrome\NativeMessagingHosts';edge='Software\Microsoft\Edge\NativeMessagingHosts';firefox='Software\Mozilla\NativeMessagingHosts' }
    $entries = [Collections.Generic.List[object]]::new()
    foreach ($name in $Browser | Select-Object -Unique) {
        $manifest = Join-Path $root ('native/' + $name + '.json')
        $json = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        $hostPath = Join-Path $root 'host/AriaGui.NativeHost.exe'
        if ($json.name -ne $bundle.hostName -or $json.type -ne 'stdio' -or $json.path -ne $hostPath -or !(Test-Path -LiteralPath $hostPath -PathType Leaf)) { throw 'Native manifest target/identity differs from bundle.' }
        if ($name -eq 'firefox') {
            if (@($json.allowed_extensions).Count -ne 1 -or $json.allowed_extensions[0] -ne $bundle.identities.firefox) { throw 'Firefox allowlist differs.' }
        } elseif (@($json.allowed_origins).Count -ne 1 -or $json.allowed_origins[0] -ne ('chrome-extension://' + $bundle.identities.$name + '/')) { throw 'Chromium allowlist differs.' }
        $key = if ($Channel -eq 'synthetic') { $SyntheticRegistryRoot + '\' + $name + '\' + $bundle.hostName } else { $roots[$name] + '\' + $bundle.hostName }
        foreach ($view in @('Registry32','Registry64')) { $entries.Add([pscustomobject]@{Browser=$name;View=$view;Key=$key;Value=$manifest}) }
    }
    return [pscustomobject]@{SchemaVersion=1;Channel=$Channel;InstallRoot=$root;HostName=$bundle.hostName;Entries=@($entries);ProductionEnabled=$false}
}

function Open-RegistrationBase([string]$View) { [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryView]::$View) }
function Read-Registration([object]$Entry) {
    $base = Open-RegistrationBase $Entry.View
    try { $key=$base.OpenSubKey($Entry.Key);if (!$key) { return [pscustomobject]@{Exists=$false;HasDefault=$false;Value=$null;Kind=$null} }
        try { $hasDefault='' -in $key.GetValueNames();return [pscustomobject]@{Exists=$true;HasDefault=$hasDefault;Value=if($hasDefault){$key.GetValue('', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}else{$null};Kind=if($hasDefault){$key.GetValueKind('')}else{$null}} } finally {$key.Dispose()}
    } finally {$base.Dispose()}
}
function Restore-Registration([object]$Entry,[object]$Before) {
    $base=Open-RegistrationBase $Entry.View
    try { $key=$base.OpenSubKey($Entry.Key,$true);if(!$key){return}
        try {
            if ('' -notin $key.GetValueNames() -and !$Before.HasDefault) { return }
            if ('' -notin $key.GetValueNames() -or $key.GetValue('', $null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -cne $Entry.Value -or $key.GetValueKind('') -ne [Microsoft.Win32.RegistryValueKind]::String) { throw 'Rollback refused: registration changed after write.' }
            if($Before.HasDefault){$key.SetValue('',$Before.Value,$Before.Kind)}else{$key.DeleteValue('',$false)}
            $empty=$key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0
        } finally {$key.Dispose()}
        if(!$Before.Exists -and $empty){$base.DeleteSubKey($Entry.Key,$false)}
    } finally {$base.Dispose()}
}

function Invoke-BrowserRegistration {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$Plan,[ValidateSet('install','repair','uninstall','readback')][string]$Action='readback',
          [switch]$Apply,[scriptblock]$HelloCheck,[int]$SyntheticFailAfterWrite=0)
    if($Plan.SchemaVersion -ne 1 -or $Plan.Channel -notin @('development','synthetic') -or $Plan.ProductionEnabled -ne $false){throw 'Invalid registration plan.'}
    if(@($Plan.Entries).Count -lt 1 -or @($Plan.Entries).Count -gt 6){throw 'Invalid registration entry count.'}
    foreach($entry in $Plan.Entries){
        if($entry.Browser -notin @('chrome','edge','firefox') -or $entry.View -notin @('Registry32','Registry64') -or $entry.Value -cne (Join-Path $Plan.InstallRoot ('native/' + $entry.Browser + '.json'))){throw 'Invalid registration entry.'}
        if($Plan.Channel -eq 'synthetic' -and $entry.Key -notmatch ('^Software\\AriaGUI\\Verification\\[a-f0-9]{32}\\'+$entry.Browser+'\\com\.ariagui\.verify_[a-f0-9]{32}$')){throw 'Synthetic registration escaped verification namespace.'}
    }
    $syntheticRoot=if($Plan.Channel -eq 'synthetic'){($Plan.Entries[0].Key -split '\\')[0..3] -join '\'}else{''}
    $fresh=New-BrowserRegistrationPlan -Channel $Plan.Channel -Browser @($Plan.Entries.Browser | Select-Object -Unique) -InstallRoot $Plan.InstallRoot -SyntheticRegistryRoot $syntheticRoot
    $provided=@($Plan.Entries | ForEach-Object {$_.Browser+'|'+$_.View+'|'+$_.Key+'|'+$_.Value} | Sort-Object)
    $expected=@($fresh.Entries | ForEach-Object {$_.Browser+'|'+$_.View+'|'+$_.Key+'|'+$_.Value} | Sort-Object)
    if($Plan.HostName -cne $fresh.HostName -or $provided.Count -ne $expected.Count -or (Compare-Object $provided $expected)){throw 'Registration plan no longer matches the verified bundle.'}
    $Plan=$fresh
    # Normal browser registration is intentionally blocked until release/trust gates are completed.
    if($Apply -and $Plan.Channel -ne 'synthetic'){throw 'Development apply is blocked pending browser/installer/DNS release gates. Dry-run only.'}
    if($SyntheticFailAfterWrite -and $Plan.Channel -ne 'synthetic'){throw 'Fault injection is synthetic only.'}
    $before=@{};foreach($entry in $Plan.Entries){$before[$entry.View+'|'+$entry.Key]=Read-Registration $entry}
    if($Action -eq 'readback'){return @($Plan.Entries | ForEach-Object {$value=$before[$_.View+'|'+$_.Key];[pscustomobject]@{Browser=$_.Browser;View=$_.View;Matches=$value.HasDefault -and $value.Kind -eq 'String' -and $value.Value -ceq $_.Value;Connected=$false}})}
    foreach($entry in $Plan.Entries){$old=$before[$entry.View+'|'+$entry.Key];if($old.HasDefault -and ($old.Kind -ne 'String' -or $old.Value -cne $entry.Value)){throw 'Foreign registration exists; refusing replacement.'}}
    if(!$Apply){return [pscustomobject]@{DryRun=$true;Action=$Action;Plan=$Plan;Connected=$false}}
    $written=[Collections.Generic.List[object]]::new();$removed=0
    try {
        foreach($entry in $Plan.Entries){
            $current=Read-Registration $entry
            if($current.HasDefault -and ($current.Kind -ne 'String' -or $current.Value -cne $entry.Value)){throw 'Registration changed during transaction.'}
            $base=Open-RegistrationBase $entry.View
            try {
                if($Action -eq 'uninstall'){
                    $key=$base.OpenSubKey($entry.Key,$true);if(!$key){continue}
                    try {if('' -notin $key.GetValueNames()){continue};if($key.GetValue('', $null) -cne $entry.Value){throw 'Uninstall ownership changed.'};$key.DeleteValue('',$false);$empty=$key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0}finally{$key.Dispose()}
                    if($empty){$base.DeleteSubKey($entry.Key,$false)}
                    $removed++;if($SyntheticFailAfterWrite -gt 0 -and $removed -eq $SyntheticFailAfterWrite){throw 'Synthetic postremove failure.'}
                }else{
                    $key=$base.CreateSubKey($entry.Key);try{$key.SetValue('',$entry.Value,[Microsoft.Win32.RegistryValueKind]::String)}finally{$key.Dispose()}
                    $written.Add($entry);$read=Read-Registration $entry;if(!$read.HasDefault -or $read.Value -cne $entry.Value -or $read.Kind -ne 'String'){throw 'Registration readback failed.'}
                    if($SyntheticFailAfterWrite -gt 0 -and $written.Count -eq $SyntheticFailAfterWrite){throw 'Synthetic postwrite failure.'}
                }
            }finally{$base.Dispose()}
        }
        if($Action -ne 'uninstall' -and $HelloCheck -and !(& $HelloCheck)){throw 'Hello failed; registration rollback required.'}
    }catch{
        if($Action -eq 'uninstall' -and $removed -gt 0){throw 'Uninstall partially completed; retry removes only remaining owned pointers. No files or foreign values were deleted.'}
        if($Action -ne 'uninstall'){
            $rollbackErrors=[Collections.Generic.List[string]]::new()
            for($i=$written.Count-1;$i -ge 0;$i--){$entry=$written[$i];try{Restore-Registration $entry $before[$entry.View+'|'+$entry.Key]}catch{$rollbackErrors.Add('changed_registration_preserved')}}
            if($rollbackErrors.Count){throw 'Registration failed; changed values preserved instead of unsafe rollback.'}
        }
        throw
    }
    return [pscustomobject]@{Action=$Action;Applied=$true;Connected=$false;HelloVerified=[bool]$HelloCheck;ProductionEnabled=$false}
}
Export-ModuleMember -Function New-BrowserRegistrationPlan,Invoke-BrowserRegistration,Test-BrowserBundle
