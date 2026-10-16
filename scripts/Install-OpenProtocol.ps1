[CmdletBinding()]
param([ValidateSet('install','update','repair','rollback','recover','uninstall','readback')][string]$Action='readback',
 [string]$SourceDirectory,[string]$SyntheticWorkspaceRoot,[string]$SyntheticScheme,[switch]$Apply,
 [ValidateSet('None','Journal','Stage','Retiring','Current','RegistryOwner','RegistryTitle','RegistryProtocol','RegistryCommand','Receipt','Unregistered','PayloadRemoved')][string]$FaultPoint='None',
 [ValidateSet('Throw','Exit')][string]$FaultMode='Throw')
Import-Module (Join-Path $PSScriptRoot 'OpenProtocolInstaller.psm1') -Force
Invoke-OpenProtocolInstall @PSBoundParameters
