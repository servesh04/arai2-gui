[CmdletBinding()]
param([Parameter(Mandatory)][string]$HostPublishDirectory,[Parameter(Mandatory)][string]$OutputDirectory,
      [ValidateSet('development','synthetic')][string]$Channel='development',[string]$SyntheticHostName)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$output=[IO.Path]::GetFullPath($OutputDirectory)
$allowed=[IO.Path]::GetFullPath((Join-Path $workspace 'artifacts/browser-packages'))+[IO.Path]::DirectorySeparatorChar
if(!$output.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Use a NEW directory under artifacts/browser-packages.'}
$hostSource=(Resolve-Path -LiteralPath $HostPublishDirectory).Path
foreach($required in @('AriaGui.NativeHost.exe','AriaGui.NativeHost.dll','AriaGui.NativeHost.deps.json','coreclr.dll','System.Private.CoreLib.dll')){if(!(Test-Path -LiteralPath (Join-Path $hostSource $required) -PathType Leaf)){throw 'Self-contained x64 host publish required.'}}
if($Channel -eq 'synthetic' -and $SyntheticHostName -notmatch '^com\.ariagui\.verify_[a-f0-9]{32}$'){throw 'Unique synthetic host name required.'}
& node (Join-Path $workspace 'extensions/aria-gui/build.mjs');if($LASTEXITCODE -ne 0){throw 'Extension generation failed.'}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
$acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false);$acl.SetOwner($sid)
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,[Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
Set-Acl -LiteralPath $output -AclObject $acl
Copy-Item -LiteralPath $hostSource -Destination (Join-Path $output 'host') -Recurse
$extensionBuild=Join-Path $workspace 'extensions/aria-gui/dist'
$identities=Get-Content -LiteralPath (Join-Path $extensionBuild 'identities.json') -Raw | ConvertFrom-Json
$hostName=if($Channel -eq 'synthetic'){$SyntheticHostName}else{'com.ariagui.browser.development'}
New-Item -ItemType Directory -Path (Join-Path $output 'native'),(Join-Path $output 'extensions'),(Join-Path $output 'notices') | Out-Null
foreach($browser in @('chrome','edge','firefox')){
    $extension=Join-Path $output ('extensions/'+$browser)
    Copy-Item -LiteralPath (Join-Path $extensionBuild $browser) -Destination $extension -Recurse
    [IO.File]::WriteAllText((Join-Path $extension 'configuration.mjs'),('export const configuration='+(@{hostName=$hostName}|ConvertTo-Json -Compress)+';'))
    Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $extension | Select-Object -ExpandProperty FullName) -DestinationPath (Join-Path $output ('extensions/'+$browser+'.zip'))
    $native=[ordered]@{name=$hostName;description='AriaGUI development bridge — production disabled';path=(Join-Path $output 'host/AriaGui.NativeHost.exe');type='stdio'}
    if($browser -eq 'firefox'){$native.allowed_extensions=@($identities.firefox)}else{$native.allowed_origins=@('chrome-extension://'+$identities.$browser+'/')}
    $native | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output ('native/'+$browser+'.json')) -Encoding utf8
}
# No GUI/descriptor/runtime/profile is copied. Missing descriptor keeps this host fail-closed.
Copy-Item -LiteralPath (Join-Path $workspace 'extensions/aria-gui/THIRD_PARTY_NOTICES.md'),(Join-Path $workspace 'docs/THIRD_PARTY.md') -Destination (Join-Path $output 'notices')
$runtime=Get-Content -LiteralPath (Join-Path $hostSource 'AriaGui.NativeHost.runtimeconfig.json') -Raw|ConvertFrom-Json
$runtimeVersion=@($runtime.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App')[0].version
$runtimePackage=Join-Path $env:USERPROFILE ('.nuget/packages/microsoft.netcore.app.runtime.win-x64/'+$runtimeVersion)
if(!(Test-Path -LiteralPath $runtimePackage)){throw 'Matching restored .NET runtime package is needed for notices.'}
$runtimeNotices=Join-Path $output ('notices/dotnet-runtime-'+$runtimeVersion);New-Item -ItemType Directory -Path $runtimeNotices | Out-Null
$noticeFiles=@(Get-ChildItem -LiteralPath $runtimePackage -File | Where-Object {$_.Name -match '(?i)license|notice'})
if(!$noticeFiles.Count){throw 'Matching .NET runtime notices are missing.'}
foreach($notice in $noticeFiles){Copy-Item -LiteralPath $notice.FullName -Destination $runtimeNotices}
[ordered]@{id='Microsoft.NETCore.App.Runtime.win-x64';version=$runtimeVersion;scope='Embedded native host runtime';noticeFiles=@($noticeFiles.Name);reviewStatus='Declared notices copied; full redistribution review pending'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'notices/native-host-dependencies.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Register-BrowserHost.ps1'),(Join-Path $PSScriptRoot 'BrowserRegistration.psm1'),(Join-Path $PSScriptRoot 'Install-BrowserCompanion.ps1') -Destination $output
Copy-Item -LiteralPath (Join-Path $workspace 'docs/BROWSER_HANDOFF_INSTALLATION.md') -Destination $output
$files=@(Get-ChildItem -LiteralPath $output -File -Recurse | Sort-Object FullName | ForEach-Object {[ordered]@{path=[IO.Path]::GetRelativePath($output,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
[ordered]@{schemaVersion=1;channel=$Channel;hostName=$hostName;productionEnabled=$false;identities=$identities;files=$files;trustStatus='Unsigned development bundle; no production activation; runtime/native licensing review pending'} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'browser-bundle.json') -Encoding utf8
Import-Module (Join-Path $PSScriptRoot 'BrowserRegistration.psm1') -Force
$null=Test-BrowserBundle -InstallRoot $output
Write-Output $output
