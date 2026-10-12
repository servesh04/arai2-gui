[CmdletBinding()]
param([Parameter(Mandatory)][string]$BundleRoot,[Parameter(Mandatory)][string]$ReportPath)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'BrowserRegistration.psm1') -Force
$registryRoot='Software\AriaGUI\Verification\'+[guid]::NewGuid().ToString('N')
$base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryView]::Registry64)
$existing=$base.OpenSubKey($registryRoot);if($existing){$existing.Dispose();throw 'Synthetic namespace already exists.'}
$checks=[Collections.Generic.List[string]]::new()
function Check([bool]$Condition,[string]$Name){if(!$Condition){throw ('Check failed: '+$Name)};$checks.Add($Name)}
function Reject([scriptblock]$Work,[string]$Pattern,[string]$Name){$rejected=$false;try{& $Work | Out-Null}catch{if($_.Exception.Message -notmatch $Pattern){throw};$rejected=$true};Check $rejected $Name}
$plan=New-BrowserRegistrationPlan -Channel synthetic -Browser chrome,edge,firefox -InstallRoot $BundleRoot -SyntheticRegistryRoot $registryRoot
try{
    $dry=Invoke-BrowserRegistration -Plan $plan -Action install
    Check ($dry.DryRun -and $null -eq $base.OpenSubKey($registryRoot)) 'Dry-run leaves registry absent'
    $manifestPath=Join-Path $BundleRoot 'native/chrome.json';$manifestBytes=[IO.File]::ReadAllBytes($manifestPath)
    [IO.File]::AppendAllText($manifestPath,' ')
    try{Reject {Invoke-BrowserRegistration -Plan $plan -Action install -Apply} 'integrity failed' 'Changed manifest between plan and apply fails revalidation'}finally{[IO.File]::WriteAllBytes($manifestPath,$manifestBytes)}
    Reject {Invoke-BrowserRegistration -Plan $plan -Action install -Apply -SyntheticFailAfterWrite 3} 'Synthetic postwrite' 'Injected partial install fails visibly'
    Check (@(Invoke-BrowserRegistration -Plan $plan -Action readback | Where-Object Matches).Count -eq 0) 'Partial install rolls back all owned pointers'
    Reject {Invoke-BrowserRegistration -Plan $plan -Action install -Apply -HelloCheck {$false}} 'Hello failed' 'Failed hello rolls back'
    Check (@(Invoke-BrowserRegistration -Plan $plan -Action readback | Where-Object Matches).Count -eq 0) 'Failed hello leaves no registration'
    $foreignKeyPath=$registryRoot+'\other-channel\com.ariagui.verify_'+[guid]::NewGuid().ToString('N')
    $foreignKey=$base.CreateSubKey($foreignKeyPath);try{$foreignKey.SetValue('','C:\OtherChannel\native.json',[Microsoft.Win32.RegistryValueKind]::String)}finally{$foreignKey.Dispose()}
    $null=Invoke-BrowserRegistration -Plan $plan -Action install -Apply
    $readback=@(Invoke-BrowserRegistration -Plan $plan -Action readback)
    Check ($readback.Count -eq 6 -and @($readback | Where-Object {!$_.Matches -or $_.Connected}).Count -eq 0) 'Exact browser/view readback never claims connected'
    $null=Invoke-BrowserRegistration -Plan $plan -Action repair -Apply
    Check (@(Invoke-BrowserRegistration -Plan $plan -Action readback | Where-Object Matches).Count -eq 6) 'Owned repair is idempotent'
    $entry=$plan.Entries[0];$key=$base.OpenSubKey($entry.Key,$true)
    try{$key.SetValue('Keep','unrelated',[Microsoft.Win32.RegistryValueKind]::String);$key.SetValue('','C:\Foreign\native.json',[Microsoft.Win32.RegistryValueKind]::String)}finally{$key.Dispose()}
    Reject {Invoke-BrowserRegistration -Plan $plan -Action repair -Apply} 'Foreign registration' 'Foreign pointer refuses repair'
    Reject {Invoke-BrowserRegistration -Plan $plan -Action uninstall -Apply} 'Foreign registration' 'Foreign pointer refuses uninstall'
    $key=$base.OpenSubKey($entry.Key,$true);try{Check ($key.GetValue('') -eq 'C:\Foreign\native.json') 'Foreign pointer preserved';$key.SetValue('',$entry.Value,[Microsoft.Win32.RegistryValueKind]::String)}finally{$key.Dispose()}
    $null=Invoke-BrowserRegistration -Plan $plan -Action uninstall -Apply
    $foreignKey=$base.OpenSubKey($foreignKeyPath);try{Check ($foreignKey.GetValue('') -eq 'C:\OtherChannel\native.json') 'Uninstall preserves another channel registration'}finally{$foreignKey.Dispose()}
    $key=$base.OpenSubKey($entry.Key);try{Check ($key.GetValue('Keep') -eq 'unrelated' -and '' -notin $key.GetValueNames()) 'Uninstall removes only owned default and preserves other values'}finally{$key.Dispose()}
    $null=Invoke-BrowserRegistration -Plan $plan -Action install -Apply
    Check (@(Invoke-BrowserRegistration -Plan $plan -Action readback | Where-Object Matches).Count -eq 6) 'Reinstall restores exact pointers without user-data access'
    Reject {Invoke-BrowserRegistration -Plan $plan -Action uninstall -Apply -SyntheticFailAfterWrite 1} 'partially completed' 'Partial uninstall failure is explicit and safely retryable'
    $null=Invoke-BrowserRegistration -Plan $plan -Action uninstall -Apply
    Check ((Test-Path -LiteralPath (Join-Path $BundleRoot 'browser-bundle.json'))) 'Uninstall preserves installation files'
    $badPlan=$plan | Select-Object *;$badPlan.Entries=@([pscustomobject]@{Browser='chrome';View='Registry64';Key='Software\Google\Chrome\NativeMessagingHosts\com.production';Value=$entry.Value})
    Reject {Invoke-BrowserRegistration -Plan $badPlan -Action install -Apply} 'escaped verification' 'Forged plan cannot escape synthetic root'
    $inventoryHash=(Get-FileHash -LiteralPath (Join-Path $BundleRoot 'browser-bundle.json')).Hash
    $extra=Join-Path $BundleRoot 'unexpected.txt';[IO.File]::WriteAllText($extra,'extra')
    try{Reject {Test-BrowserBundle -InstallRoot $BundleRoot} 'untracked files' 'Untracked bundle data rejected'}finally{Remove-Item -LiteralPath $extra}
    Check ((Get-FileHash -LiteralPath (Join-Path $BundleRoot 'browser-bundle.json')).Hash -eq $inventoryHash) 'Readback/repair/uninstall preserve bundle inventory'
    [ordered]@{passed=$checks.Count;checks=@($checks);syntheticNamespace=$registryRoot;personalBrowserKeysModified=$false;productionEnabled=$false;cleanMachineVerified=$false} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding utf8
    Write-Output ('Passed '+$checks.Count+' synthetic registration checks.')
}finally{
    # This UUID subtree was checked absent before this test. All its values were created by this test.
    if($registryRoot -notmatch '^Software\\AriaGUI\\Verification\\[a-f0-9]{32}$'){throw 'Unsafe registry cleanup.'}
    $base.DeleteSubKeyTree($registryRoot,$false);$check=$base.OpenSubKey($registryRoot);if($check){$check.Dispose();throw 'Synthetic cleanup failed.'};$base.Dispose()
}
