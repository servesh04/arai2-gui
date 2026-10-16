[CmdletBinding()]
param([string]$InputDirectory='C:\PhaseCInput',[string]$OutputDirectory='C:\PhaseCOutput')
$ErrorActionPreference='Stop'
$marker=Get-Content -LiteralPath (Join-Path $InputDirectory 'isolated-sandbox.json') -Raw | ConvertFrom-Json
# Fail closed on accidental execution on the host. This guard is not a security boundary.
if($env:USERNAME -cne 'WDAGUtilityAccount' -or $env:COMPUTERNAME -eq $marker.HostComputerName){throw 'Requires the owned Windows Sandbox guest; host execution refused.'}
if((Get-FileHash -LiteralPath (Join-Path $InputDirectory 'candidate.msix')).Hash -cne $marker.PackageSha256 -or
   (Get-FileHash -LiteralPath (Join-Path $InputDirectory 'candidate.cer')).Hash -cne $marker.CertificateSha256){throw 'Candidate hash mismatch.'}
$package=@(Get-AppxPackage -Name $marker.PackageName)
$handlers=@('HKCU:\Software\Classes\ariagui','HKLM:\Software\Classes\ariagui','Registry::HKEY_CLASSES_ROOT\ariagui',
 'HKCU:\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\ariagui' | Where-Object {Test-Path -LiteralPath $_})
if($package.Count -or $handlers.Count){throw 'Guest is not clean for canonical acceptance.'}
foreach($installed in Get-AppxPackage){
 $manifest=$installed | Get-AppxPackageManifest
 if(@($manifest.SelectNodes("//*[local-name()='Protocol' and @Name='ariagui']")).Count){throw 'Existing packaged canonical handler; guest is not clean.'}
}
[ordered]@{Outcome='ready-for-manual-isolated-install';Acceptance='pending';OS=[Environment]::OSVersion.VersionString
 PackageSha256=$marker.PackageSha256;CertificateSha256=$marker.CertificateSha256
 ExplorerPresent=(@(Get-Process explorer -ErrorAction SilentlyContinue).Count -gt 0)
 CertificateImported=$false;PackageInstalled=$false;HandlerChanged=$false;NetworkDisabledByConfiguration=$true} |
 ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'guest-readiness.json') -Encoding utf8
# No installation or acceptance assertion. Follow the mapped runbook in this guest.
