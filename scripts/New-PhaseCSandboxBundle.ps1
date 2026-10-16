[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$package=(Resolve-Path -LiteralPath $PackagePath).Path
$allowed=[IO.Path]::GetFullPath((Join-Path $workspace 'artifacts/packages'))+[IO.Path]::DirectorySeparatorChar
if(!$package.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Use a workspace release candidate.'}
$certificate=Join-Path (Split-Path -Parent $package) 'AriaGUI.Development.cer'
if(!(Test-Path -LiteralPath $certificate)){throw 'File-only development certificate required.'}
$report=& (Join-Path $PSScriptRoot 'Test-Package.ps1') -PackagePath $package -RequireSignature
$publicCertificate=[Security.Cryptography.X509Certificates.X509Certificate2]::new($certificate)
try {
 if($publicCertificate.Thumbprint -ne $report.signerThumbprint -or $publicCertificate.Subject -ne $report.publisher){throw 'Public certificate does not match the candidate signer/publisher.'}
} finally {$publicCertificate.Dispose()}
$root=Join-Path $workspace ('artifacts/phase-c/sandbox-'+[guid]::NewGuid().ToString('N'))
$inputDirectory=Join-Path $root 'input';$outputDirectory=Join-Path $root 'output'
New-Item -ItemType Directory -Path $inputDirectory,$outputDirectory | Out-Null
Copy-Item -LiteralPath $package -Destination (Join-Path $inputDirectory 'candidate.msix')
Copy-Item -LiteralPath $certificate -Destination (Join-Path $inputDirectory 'candidate.cer')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-PhaseCGuestReadiness.ps1') -Destination $inputDirectory
Copy-Item -LiteralPath (Join-Path $workspace 'docs/BROWSER_HANDOFF_PHASE_C_ISOLATED_RUNBOOK.md') -Destination $inputDirectory
[ordered]@{HostComputerName=$env:COMPUTERNAME;PackageName=$report.name;PackageSha256=(Get-FileHash -LiteralPath $package).Hash
 CertificateSha256=(Get-FileHash -LiteralPath $certificate).Hash;Acceptance='pending';HostTrustChanged=$false} |
 ConvertTo-Json | Set-Content -LiteralPath (Join-Path $inputDirectory 'isolated-sandbox.json') -Encoding utf8
$inputXml=[Security.SecurityElement]::Escape($inputDirectory)
$outputXml=[Security.SecurityElement]::Escape($outputDirectory)
@"
<Configuration>
 <Networking>Disable</Networking><ClipboardRedirection>Disable</ClipboardRedirection>
 <PrinterRedirection>Disable</PrinterRedirection><AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput>
 <MappedFolders>
  <MappedFolder><HostFolder>$inputXml</HostFolder><SandboxFolder>C:\PhaseCInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
  <MappedFolder><HostFolder>$outputXml</HostFolder><SandboxFolder>C:\PhaseCOutput</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
 </MappedFolders>
 <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\PhaseCInput\Test-PhaseCGuestReadiness.ps1</Command></LogonCommand>
</Configuration>
"@ | Set-Content -LiteralPath (Join-Path $root 'PhaseC.wsb') -Encoding utf8
Write-Output $root
# Preparation only: never launch Sandbox, import trust, install, or modify a handler here.
