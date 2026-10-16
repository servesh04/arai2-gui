[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$BuildDirectory,
 [string]$FixtureDll=(Join-Path (Split-Path -Parent $PSScriptRoot) 'tests/AriaGui.UiFixture/bin/Debug/net10.0/AriaGui.UiFixture.dll'),
 [switch]$RequiredAcceptance,
 [switch]$CanonicalShell,
 [switch]$WithPinnedEngine,
 [ValidateSet('None','PermissiveClassifier','ExitTimeout')][string]$FaultInjection='None'
)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'OpenActivationFixtureAssertions.psm1') -Force
$workspace=Split-Path -Parent $PSScriptRoot
$build=(Resolve-Path -LiteralPath $BuildDirectory).Path
# Release builds ignore the profile marker and must never be launched by this fixture.
# Require the reviewed DEBUG/verification marker literal in the actual app bytes before copying.
$appBytes=[IO.File]::ReadAllBytes((Join-Path $build 'AriaGui.App.dll'))
$markerBytes=[Text.Encoding]::Unicode.GetBytes('r1-fixture-profile.txt')
if(!([BitConverter]::ToString($appBytes).Contains([BitConverter]::ToString($markerBytes)))){throw 'GUI fixture requires a DEBUG/verification build with synthetic profile marker support.'}
$root=Join-Path $workspace ('artifacts/phase-c/gui-'+[guid]::NewGuid().ToString('N'))
$payload=Join-Path $root 'payload with spaces';$profile=Join-Path $root 'synthetic profile'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
Get-ChildItem -LiteralPath $build | Copy-Item -Destination $payload -Recurse
if($WithPinnedEngine){& dotnet $FixtureDll $profile '--paused-engine-fixture'}else{& dotnet $FixtureDll $profile}
if($LASTEXITCODE -ne 0){throw 'Synthetic profile seed failed.'}
[IO.File]::WriteAllText((Join-Path $payload 'r1-fixture-profile.txt'),$profile)
$exe=Join-Path $payload 'AriaGui.App.exe'
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class OpenGuiNative {
 public delegate bool EnumProc(IntPtr hwnd, IntPtr data);
 [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr data);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr w,IntPtr l);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd,int command);
 [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 public static IntPtr Find(int pid) {IntPtr found=IntPtr.Zero;EnumWindows((h,d)=>{uint p;GetWindowThreadProcessId(h,out p);if(p==pid && IsWindowVisible(h)){found=h;return false;}return true;},IntPtr.Zero);return found;}
}
'@
$checks=[Collections.Generic.List[string]]::new();$process=$null;$otherProcess=$null;$secondary=@();$closingUiVerified=$false;$actualCloseToTray=$false;$foregroundAfterWarm=$false
$outcome='fail';$failure=$null;$closingLimitation=$null;$incomplete=[Collections.Generic.List[string]]::new()
$canonicalRegistered=$false
$engineProcess=$null;$engineEvidence=$null
$engineSnapshots=[Collections.Generic.List[object]]::new()
function Launch-Owned([string]$argument){
 if($CanonicalShell -and $argument.StartsWith('ariagui:',[StringComparison]::OrdinalIgnoreCase)){
  $start=[Diagnostics.ProcessStartInfo]::new($argument);$start.UseShellExecute=$true;$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
  return [Diagnostics.Process]::Start($start)
 }
 return Start-Process -FilePath $exe -ArgumentList $argument -WindowStyle Hidden -PassThru
}
function Check([bool]$value,[string]$name){if(!$value){throw "GUI check failed: $name"};$checks.Add($name)}
function Wait-State([scriptblock]$predicate,[string]$name){for($i=0;$i -lt 100;$i++){if(& $predicate){return};Start-Sleep -Milliseconds 100};throw "GUI deadline: $name"}
function Assert-PausedEngine {
 $owner=Get-Content -LiteralPath (Join-Path $profile 'engine.owner.json') -Raw | ConvertFrom-Json
 $actual=Get-Process -Id $owner.ProcessId -ErrorAction Stop
 if($actual.MainModule.FileName -cne (Join-Path $profile 'disabled-engine/aria2c.exe')){throw 'Engine ownership does not identify the owned fixture binary.'}
 if($owner.AppProcessId -ne $process.Id){throw 'Engine owner changed to a secondary GUI.'}
 if($owner.RpcPort -ne $script:privateRpcPort -or $owner.RpcPort -eq 16800){throw 'Engine did not use the private fixture RPC port.'}
 if($script:engineProcess -and $script:engineProcess.Id -ne $actual.Id){$script:engineProcess.Dispose()}
 $script:engineProcess=$actual
 $matching=@(Get-Process -Name aria2c -ErrorAction SilentlyContinue | Where-Object {
  try{$_.MainModule.FileName -ceq (Join-Path $profile 'disabled-engine/aria2c.exe')}catch{$false}
 })
 if($matching.Count -ne 1){throw 'Expected exactly one owned fixture engine.'}
 $config=[IO.File]::ReadAllText((Join-Path $profile 'aria2.conf'))
 $secret=[regex]::Match($config,'(?m)^rpc-secret=([^\r\n]+)').Groups[1].Value
 if(!$secret){throw 'Owned fixture RPC configuration unavailable.'}
 $body=@{jsonrpc='2.0';id='phase-c';method='aria2.tellStatus';params=@(('token:'+$secret),'abcdef1234567890',@('gid','status'))} | ConvertTo-Json -Depth 5
 $status=Invoke-RestMethod -Uri ('http://127.0.0.1:'+$owner.RpcPort+'/jsonrpc') -Method Post -ContentType 'application/json' -Body $body
 if($status.result.gid -cne 'abcdef1234567890' -or $status.result.status -cne 'paused'){throw 'Synthetic pending identity/pause state changed.'}
 $script:engineEvidence=[ordered]@{ProcessId=$owner.ProcessId;AppProcessId=$owner.AppProcessId;RpcPort=$owner.RpcPort;Gid=$status.result.gid;Status=$status.result.status;PinnedHash=(Get-FileHash -LiteralPath $actual.MainModule.FileName).Hash}
 $engineSnapshots.Add([pscustomobject]$script:engineEvidence)
}
function Launch-Secondary([string]$argument='ariagui://open'){
 $child=Launch-Owned $argument
 $script:secondary+=,$child
 if(!$child.WaitForExit(10000)){throw 'Secondary did not exit within deadline.'}
 if($child.ExitCode -ne 0){throw 'Secondary activation failed with a nonzero exit.'}
}
try{
 if($CanonicalShell){
  Assert-OpenCanonicalAssociationAbsent
  & (Join-Path $PSScriptRoot 'Register-OpenProtocol.ps1') -Action register -Executable $exe -Apply | Out-Null
  $canonicalRegistered=$true
 }
 # Malformed cold input must not initialize runtime or create a window. The pre-seeded
 # synthetic runtime's exact files/hashes provide a before/after mutation comparison.
 $before=@(Get-ChildItem -LiteralPath $profile -Recurse -File | ForEach-Object {$_.FullName+'|'+(Get-FileHash -LiteralPath $_.FullName).Hash})
 foreach($coldArgument in @('ariagui://open?synthetic=reject','ariagui://open#fixture','ariagui://%6fpen','ariagui://open/extra','ariagui://open//','ariagui://user@open','ariagui://open?fixture="one two"')){
 $cold=Launch-Owned $coldArgument
 $secondary+=,$cold
 Check ($cold.WaitForExit(10000) -and $cold.ExitCode -eq 0) 'malformed cold activation exits cleanly'
 $activationReceipt=Get-Content -LiteralPath (Join-Path $payload 'activation-fixture.receipt.json') -Raw | ConvertFrom-Json
 Check ($activationReceipt.ProcessId -eq $cold.Id -and !$activationReceipt.Supported -and $activationReceipt.Kind -eq 'Launch') 'malformed cold delivered activation rejected by Launch adapter'
 $after=@(Get-ChildItem -LiteralPath $profile -Recurse -File | ForEach-Object {$_.FullName+'|'+(Get-FileHash -LiteralPath $_.FullName).Hash})
 Check (!(Compare-Object $before $after)) 'malformed cold activation leaves synthetic runtime unchanged'
 }
 $wake=Start-Process -FilePath $exe -ArgumentList '--browser-wakeup' -WindowStyle Hidden -PassThru
 $process=$wake
 $wakeWindow=[IntPtr]::Zero
 Wait-State {$script:wakeWindow=[OpenGuiNative]::Find($wake.Id);$script:wakeWindow -ne [IntPtr]::Zero} 'native-host wake cold window'
 Launch-Secondary '--browser-wakeup'
 Check (!$wake.HasExited -and [OpenGuiNative]::IsWindowVisible($wakeWindow)) 'exact native-host wake cold and warm launch accepted'
 $wake.Kill($true);$wake.WaitForExit(5000)|Out-Null;$wake.Dispose();$process=$null
 if($WithPinnedEngine){
  $probe=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
  try{$probe.Start();$script:privateRpcPort=$probe.LocalEndpoint.Port}finally{$probe.Stop()}
  if($privateRpcPort -lt 49152 -or $privateRpcPort -gt 65535){throw 'OS-selected fixture port is outside the private ephemeral range.'}
  [IO.File]::WriteAllText((Join-Path $profile 'phase-c-private-rpc-port.txt'),$privateRpcPort.ToString([Globalization.CultureInfo]::InvariantCulture))
  $engine=Join-Path $workspace 'vendor/aria2/win-x64/aria2c.exe'
  if((Get-FileHash -LiteralPath $engine).Hash -cne 'BE2099C214F63A3CB4954B09A0BECD6E2E34660B886D4C898D260FEBFE9D70C2'){throw 'Pinned engine integrity failed.'}
  New-Item -ItemType Directory -Path (Join-Path $profile 'disabled-engine') | Out-Null
  Copy-Item -LiteralPath $engine -Destination (Join-Path $profile 'disabled-engine/aria2c.exe')
  & dotnet $FixtureDll $profile '--prepare-guarded-pending'
  if($LASTEXITCODE -ne 0){throw 'Guarded pending fixture preparation failed.'}
 }
 $process=Launch-Owned 'ariagui://open'
 $window=[IntPtr]::Zero
 Wait-State {$script:window=[OpenGuiNative]::Find($process.Id);$script:window -ne [IntPtr]::Zero} 'cold URI window'
 Check (!$process.HasExited) 'cold open creates one owned GUI in synthetic profile'
 if($WithPinnedEngine){Wait-State {Test-Path -LiteralPath (Join-Path $profile 'engine.owner.json')} 'pinned engine ownership';Assert-PausedEngine;$checks.Add('owned pinned engine restores same paused synthetic pending GID')}
 $activationReceipt=Get-Content -LiteralPath (Join-Path $payload 'activation-fixture.receipt.json') -Raw | ConvertFrom-Json
 Check ($activationReceipt.ProcessId -eq $process.Id -and $activationReceipt.Supported -and $activationReceipt.Kind -eq 'Launch') 'canonical cold delivered activation accepted by Launch adapter'
 Launch-Secondary
 Check ([OpenGuiNative]::IsWindowVisible($window) -and !$process.HasExited) 'warm open reuses primary; secondary exits'
 $foregroundAfterWarm=[OpenGuiNative]::GetForegroundWindow() -eq $window
 $null=[OpenGuiNative]::PostMessage($window,0x10,[IntPtr]::Zero,[IntPtr]::Zero)
 for($i=0;$i -lt 30;$i++){if(![OpenGuiNative]::IsWindowVisible($window)){$actualCloseToTray=$true;break};Start-Sleep -Milliseconds 100}
 if(!$actualCloseToTray){
  $incomplete.Add('actual Explorer tray close/restore unavailable')
  Check (!$process.HasExited -and [OpenGuiNative]::IsWindowVisible($window)) 'tray-unavailable close keeps GUI accessible'
  $null=[OpenGuiNative]::ShowWindow($window,0) # Inject hidden state only when tray is unavailable.
 }
 Start-Sleep -Milliseconds 600
 Launch-Secondary
 Wait-State {[OpenGuiNative]::IsWindowVisible($window)} 'URI opens hidden tray window'
 Check (!$process.HasExited) 'hidden-window open preserves primary PID and ownership'
 $null=[OpenGuiNative]::ShowWindow($window,6)
 Start-Sleep -Milliseconds 600
 Launch-Secondary
 Wait-State {![OpenGuiNative]::IsIconic($window)} 'URI restores minimized window'
 $children=1..8 | ForEach-Object {Launch-Owned 'ariagui://open'}
 $secondary+=@($children)
 foreach($child in $children){Check ($child.WaitForExit(10000)) 'burst secondary exits without another GUI'}
 $null=[OpenGuiNative]::ShowWindow($window,6)
 Start-Sleep -Milliseconds 600
 Launch-Secondary 'ariagui://open?url=https://example.invalid/synthetic'
 if($FaultInjection -eq 'PermissiveClassifier'){$null=[OpenGuiNative]::ShowWindow($window,9)}
 Start-Sleep -Milliseconds 500
 Assert-OpenRejectedWarm (!$process.HasExited) ([OpenGuiNative]::IsIconic($window))
 $checks.Add('malformed warm activation leaves minimized owner unchanged')
 Launch-Secondary
 Wait-State {![OpenGuiNative]::IsIconic($window)} 'valid open after rejected malformed input'
 if($WithPinnedEngine){Assert-PausedEngine;$checks.Add('protocol burst retains GUI engine ownership and paused GID')}else{Check (!(Test-Path -LiteralPath (Join-Path $profile 'disabled-engine/aria2c.exe'))) 'fixture has no engine executable; no real downloads started'}
 # A second independently marked runtime must not receive this runtime's wake broadcast.
 $otherPayload=Join-Path $root 'other payload';$otherProfile=Join-Path $root 'other synthetic profile'
 New-Item -ItemType Directory -Path $otherPayload | Out-Null
 Get-ChildItem -LiteralPath $payload | Copy-Item -Destination $otherPayload -Recurse
 & dotnet $FixtureDll $otherProfile
 if($LASTEXITCODE -ne 0){throw 'Second synthetic profile seed failed.'}
 [IO.File]::WriteAllText((Join-Path $otherPayload 'r1-fixture-profile.txt'),$otherProfile)
 $otherProcess=Start-Process -FilePath (Join-Path $otherPayload 'AriaGui.App.exe') -WindowStyle Hidden -PassThru
 $otherWindow=[IntPtr]::Zero
 Wait-State {$script:otherWindow=[OpenGuiNative]::Find($otherProcess.Id);$script:otherWindow -ne [IntPtr]::Zero} 'other isolated GUI'
 $null=[OpenGuiNative]::ShowWindow($otherWindow,6);$null=[OpenGuiNative]::ShowWindow($window,6)
 Start-Sleep -Milliseconds 600;Launch-Secondary
 Wait-State {![OpenGuiNative]::IsIconic($window)} 'scoped URI restores correct runtime'
 Check ([OpenGuiNative]::IsIconic($otherWindow)) 'runtime-scoped activation leaves other synthetic profile minimized'
 # Discover capability separately. After invocation, timeout is always a real failure.
 $invoke=$null
 try{
  Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
  $element=[System.Windows.Automation.AutomationElement]::FromHandle($window)
  $file=$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'File'))
  if(!$file){throw 'Owned File menu unavailable through UI Automation.'}
  $expand=$file.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern);$expand.Expand()
  Start-Sleep -Milliseconds 200
  $exit=$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Exit'))
  if(!$exit){throw 'Owned Exit menu unavailable through UI Automation.'}
  $invoke=$exit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
 }catch{$closingLimitation=$_.Exception.Message;$incomplete.Add('owned File/Exit UI Automation discovery unavailable')}
 if($invoke){
  $hold=Join-Path $profile 'phase-c-hold-exit'
  [IO.File]::WriteAllText($hold,'Bounded fixture shutdown interleave.')
  $invoke.Invoke()
  Wait-State {Test-Path -LiteralPath (Join-Path $profile 'phase-c-exit-started')} 'actual GUI shutdown admission stop'
  Launch-Secondary
  Check (!$process.HasExited) 'activation during held actual shutdown exits secondary while primary retains lease'
  if($WithPinnedEngine){Assert-PausedEngine;$checks.Add('shutdown-held activation preserves sole GUI/engine and pending GID')}
  Remove-Item -LiteralPath $hold
  $completed=$process.WaitForExit(10000)
  if($FaultInjection -eq 'ExitTimeout'){$completed=$false}
  Assert-OpenExitCompleted $completed
  $checks.Add('actual invoked File/Exit completes shutdown');$closingUiVerified=$true
  if($WithPinnedEngine){Check ($engineProcess.WaitForExit(10000)) 'owned pinned engine exits with GUI';$engineEvidence['ExitVerified']=$true}
  $previousPid=$process.Id;$process.Dispose();$process=Launch-Owned 'ariagui://open'
  $relaunchWindow=[IntPtr]::Zero
  Wait-State {$script:relaunchWindow=[OpenGuiNative]::Find($process.Id);$script:relaunchWindow -ne [IntPtr]::Zero} 'deliberate relaunch after actual shutdown'
  Check ($process.Id -ne $previousPid) 'deliberate post-shutdown launch obtains fresh primary GUI'
  if($WithPinnedEngine){Wait-State {Test-Path -LiteralPath (Join-Path $profile 'engine.owner.json')} 'relaunch pinned engine ownership';Assert-PausedEngine;$checks.Add('deliberate relaunch retains synthetic paused GID')}
  if($WithPinnedEngine){
   & dotnet $FixtureDll $profile '--verify-guarded-pending'
   if($LASTEXITCODE -ne 0){throw 'Durable pending identity verification failed.'}
   $checks.Add('durable pending job/attempt/GID unchanged and no payload publication after recovery')
  }
 }
 $outcome=if($incomplete.Count){'incomplete'}else{'pass'}
 if($RequiredAcceptance -and $outcome -ne 'pass'){throw 'Required acceptance is incomplete: real tray or shutdown capability missing.'}
}catch{$failure=$_.Exception.Message;if($failure -notlike 'Required acceptance is incomplete:*'){$outcome='fail'};throw
}finally{
 foreach($child in $secondary){if(!$child.HasExited){$child.Kill($true);$child.WaitForExit(5000)|Out-Null};$child.Dispose()}
 if($otherProcess){if(!$otherProcess.HasExited){$otherProcess.Kill($true);$otherProcess.WaitForExit(5000)|Out-Null};$otherProcess.Dispose()}
 if($process){if(!$process.HasExited){$process.Kill($true);$process.WaitForExit(5000)|Out-Null};$process.Dispose()}
 if($engineProcess){if(!$engineProcess.HasExited){$engineProcess.Kill($true);$engineProcess.WaitForExit(5000)|Out-Null};$engineProcess.Dispose()}
 $cleanupFailure=$null
 if($canonicalRegistered){
  try{& (Join-Path $PSScriptRoot 'Register-OpenProtocol.ps1') -Action unregister -Executable $exe -Apply | Out-Null}
  catch{$outcome='fail';$failure=$_.Exception.Message;$cleanupFailure=$_}
 }
 [ordered]@{Outcome=$outcome;Failure=$failure;Incomplete=$incomplete.ToArray();FaultInjection=$FaultInjection;Checks=$checks.ToArray();SyntheticRoot=$root;PersonalProfileUsed=$false;CanonicalShell=$CanonicalShell.IsPresent;OwnedTemporaryRegistrationRemoved=$canonicalRegistered;ExistingHandlerReplaced=$false;
  ActualExitUiVerified=$closingUiVerified;ClosingLimitation=$closingLimitation;ClosingRace=if($closingUiVerified){'actual GUI with bounded synthetic shutdown hold'}else{'not observed'};
  ActualCloseToTray=$actualCloseToTray;HiddenStateInjectedForUnavailableTray=!$actualCloseToTray;
  ForegroundAfterWarm=$foregroundAfterWarm;
  EngineEvidence=$engineEvidence;EngineSnapshots=$engineSnapshots.ToArray();ProductionRpcPortUsed=$false;AppSha256=(Get-FileHash -LiteralPath (Join-Path $payload 'AriaGui.App.dll')).Hash} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'gui-evidence.json') -Encoding utf8
 if($cleanupFailure){throw $cleanupFailure}
}
$checks
