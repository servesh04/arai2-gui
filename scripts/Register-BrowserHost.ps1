[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('development','synthetic')][string]$Channel,
      [Parameter(Mandatory)][ValidateSet('chrome','edge','firefox')][string[]]$Browser,
      [Parameter(Mandatory)][string]$InstallRoot,
      [ValidateSet('install','repair','uninstall','readback')][string]$Action='readback',
      [string]$SyntheticRegistryRoot,[switch]$Apply)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'BrowserRegistration.psm1') -Force
$plan=New-BrowserRegistrationPlan -Channel $Channel -Browser $Browser -InstallRoot $InstallRoot -SyntheticRegistryRoot $SyntheticRegistryRoot
Invoke-BrowserRegistration -Plan $plan -Action $Action -Apply:$Apply
