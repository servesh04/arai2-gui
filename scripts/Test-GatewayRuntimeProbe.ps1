[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory, [Parameter(Mandatory)][string]$PackagePath, [Parameter(Mandatory)][string]$ReportPath)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$allowed = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts')) + [IO.Path]::DirectorySeparatorChar
$probeRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$output = [IO.Path]::GetFullPath($ReportPath)
foreach ($path in @($probeRoot,$package,$output)) { if (!$path.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime probe paths must stay within workspace artifacts.' } }
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $probeRoot 'AriaGui.EngineFixture.exe'))
$start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
$start.Environment['DOTNET_ROOT'] = Join-Path $probeRoot 'absent-global-runtime'; $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$start.ArgumentList.Add('--gateway-runtime-probe'); $start.ArgumentList.Add((Join-Path $probeRoot ('private-' + [guid]::NewGuid().ToString('N'))))
$process = [Diagnostics.Process]::Start($start)
try {
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(20000)) { $process.Kill(); throw 'Private runtime probe timed out.' }
    if ($process.ExitCode -ne 0) { throw $stderr.GetAwaiter().GetResult() }
    $result = $stdout.GetAwaiter().GetResult() | ConvertFrom-Json
    if ($result.bytes -ne 4 -or !$result.kestrel.StartsWith($probeRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Probe did not load its local gateway runtime.' }
    $archive = [IO.Compression.ZipFile]::OpenRead($package); $checks = [Collections.Generic.List[object]]::new()
    try {
        foreach ($component in @('coreclr.dll','Microsoft.AspNetCore.Server.Kestrel.Core.dll','Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.dll','Microsoft.AspNetCore.Http.dll')) {
            $entry = $archive.GetEntry($component); if (!$entry) { throw "Missing packaged runtime: $component" }
            $stream = $entry.Open()
            try { $packageHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            $probeHash = (Get-FileHash -LiteralPath (Join-Path $probeRoot $component) -Algorithm SHA256).Hash
            if ($packageHash -ne $probeHash) { throw "Runtime component differs: $component" }
            $checks.Add([ordered]@{ component=$component; sha256=$probeHash; matchesPackage=$true })
        }
    } finally { $archive.Dispose() }
    [ordered]@{ probe=$result; package=$package; globalRuntimeDisabled=$true; components=@($checks); cleanMachineWinUiAcceptance='Unverified' } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $output
    Get-Content -LiteralPath $output -Raw
} finally { if (!$process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }; $process.Dispose() }
