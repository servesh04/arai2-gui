$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'OpenActivationFixtureAssertions.psm1') -Force
$failed=0
foreach($negative in @({Assert-OpenExitCompleted $false},{Assert-OpenRejectedWarm $true $false},
    {Assert-OpenDispatchReceipt -ExitCode 23 -Receipt $null -ProcessId 1 -Executable 'synthetic' -ExpectedArguments @('synthetic:open')},
    {Assert-OpenDispatchReceipt -ExitCode 0 -Receipt ([pscustomobject]@{SchemaVersion=1;ExitCode=0;ProcessId=1;Executable='synthetic';Arguments=@('wrong:open')}) -ProcessId 1 -Executable 'synthetic' -ExpectedArguments @('synthetic:open')})){
    $refused=$false;try{& $negative}catch{$refused=$true}
    if(!$refused){throw 'Negative fixture assertion was silently accepted.'};$failed++
}
Assert-OpenRejectedWarm $true $true
Assert-OpenExitCompleted $true
"$failed negative fixture assertion checks passed."
