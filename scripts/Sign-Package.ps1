[CmdletBinding(DefaultParameterSetName = 'Development')]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory, ParameterSetName = 'Development')][switch]$Development,
    [Parameter(Mandatory, ParameterSetName = 'Certificate')][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$CertificateThumbprint,
    [Parameter(ParameterSetName = 'Certificate')][uri]$TimestampUrl
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$allowed = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/packages')) + [IO.Path]::DirectorySeparatorChar
if (!$package.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Sign only workspace release candidates in artifacts/packages.' }
$null = & (Join-Path $PSScriptRoot 'Test-Package.ps1') -PackagePath $package
[xml]$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'src/AriaGui.App/Package.appxmanifest') -Raw
$publisher = $manifest.Package.Identity.Publisher
$assets = Get-Content -LiteralPath (Join-Path $projectRoot 'src/AriaGui.App/obj/project.assets.json') -Raw | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
$sdkVersion = @($assets.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'Microsoft.Windows.SDK.BuildTools/*' })[0].Split('/')[1]
$signTool = $null
foreach ($root in $packageRoots) {
    $sdk = Join-Path $root ('microsoft.windows.sdk.buildtools/' + $sdkVersion + '/bin')
    if (Test-Path -LiteralPath $sdk) {
        $signTool = Get-ChildItem -LiteralPath $sdk -Recurse -File -Filter signtool.exe | Where-Object { $_.Directory.Name -eq 'x64' } | Select-Object -First 1 -ExpandProperty FullName
        if ($signTool) { break }
    }
}
if (!$signTool) { throw 'Pinned Windows SDK SignTool not found. Restore/build the app first.' }
$runRoot = Split-Path -Parent $package
$signedPath = Join-Path $runRoot ('AriaGUI_' + $manifest.Package.Identity.Version + '_x64_' + $(if ($Development) { 'development' } else { 'signed' }) + '.msix')
if (Test-Path -LiteralPath $signedPath) { throw 'Signed output already exists; build a fresh candidate rather than replacing it.' }
$signArgs = @('sign','/fd','SHA256')
$rsa = $null; $certificate = $null
try {
    if ($Development) {
        if ($publisher -ne 'CN=AriaGUI.Development') { throw 'Development signing is limited to the development publisher.' }
        # File-only key creation: no certificate-store or machine trust changes.
        $keyRoot = Join-Path $projectRoot ('.tools/signing/' + [guid]::NewGuid().ToString('N'))
        $directory = New-Item -ItemType Directory -Path $keyRoot -Force
        $acl = [Security.AccessControl.DirectorySecurity]::new()
        $acl.SetAccessRuleProtection($true, $false)
        $user = [Security.Principal.WindowsIdentity]::GetCurrent().User
        foreach ($sid in @($user, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
        }
        Set-Acl -LiteralPath $directory.FullName -AclObject $acl
        $rsa = [Security.Cryptography.RSA]::Create(3072)
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new($publisher, $rsa,
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false,$false,0,$true))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
        $oids = [Security.Cryptography.OidCollection]::new()
        $null = $oids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids,$true))
        $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddYears(1))
        $pfx = Join-Path $keyRoot 'development.pfx'
        [IO.File]::WriteAllBytes($pfx, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, ''))
        $cer = Join-Path $runRoot 'AriaGUI.Development.cer'
        [IO.File]::WriteAllBytes($cer, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        $signArgs += @('/f',$pfx)
    } else {
        $certificate = Get-Item -LiteralPath ('Cert:\CurrentUser\My\' + $CertificateThumbprint)
        if (!$certificate.HasPrivateKey -or $certificate.Subject -ne $publisher -or $certificate.NotAfter -le [DateTime]::UtcNow) { throw 'Signing certificate must match the manifest publisher, have a private key, and be unexpired.' }
        $signArgs += @('/sha1',$CertificateThumbprint,'/s','My')
        if ($TimestampUrl) {
            if ($TimestampUrl.Scheme -ne 'https') { throw 'Use an HTTPS RFC 3161 timestamp service.' }
            $signArgs += @('/tr',$TimestampUrl.AbsoluteUri,'/td','SHA256')
        }
    }
    Copy-Item -LiteralPath $package -Destination $signedPath
    $signArgs += $signedPath
    & $signTool @signArgs
    if ($LASTEXITCODE -ne 0) { throw 'Package signing failed. Preserve the unsigned candidate and inspect the signing output.' }
    $report = & (Join-Path $PSScriptRoot 'Test-Package.ps1') -PackagePath $signedPath -RequireSignature -ReportPath (Join-Path $runRoot 'signed-package-report.json')
    [ordered]@{ mode = $(if ($Development) { 'Development only' } else { 'Provided certificate' })
        package = $signedPath; publisher = $publisher; thumbprint = $certificate.Thumbprint
        certificateStoreChanged = $false; trustInstalled = $false; timestampUrl = $TimestampUrl.AbsoluteUri
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'signing-report.json') -Encoding utf8
    Write-Host 'Signed candidate created. Certificate trust and installation have not been changed.'
    $report
}
finally { if ($certificate) { $certificate.Dispose() }; if ($rsa) { $rsa.Dispose() } }
