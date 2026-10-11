[CmdletBinding()]
param([Parameter(Mandatory)][string]$RuntimeDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$runtime = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
if (!(Test-Path -LiteralPath $runtime -PathType Container)) { throw 'Choose the app runtime folder explicitly.' }
$output = Join-Path $projectRoot ('artifacts/diagnostics/' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$secret = ''
$configuration = Join-Path $runtime 'aria2.conf'
if ((Test-Path -LiteralPath $configuration -PathType Leaf) -and (Get-Item -LiteralPath $configuration).Length -le 16384) {
    $match = [regex]::Match([IO.File]::ReadAllText($configuration), '(?m)^rpc-secret=(.+)\r?$')
    if ($match.Success) { $secret = $match.Groups[1].Value.Trim() }
}
$metadata = [ordered]@{ capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    os = [Environment]::OSVersion.VersionString; architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    engineVersion = (Get-Content -LiteralPath (Join-Path $projectRoot 'vendor/aria2/provenance.json') -Raw | ConvertFrom-Json).version
    files = @(); excluded = @('configuration','database','preferences','ownership records','sessions','downloaded files','backups')
}
foreach ($name in @('downloads.db','downloads.db-wal','engine.log','engine.log.previous','engine.checkpoint.json','preferences.json')) {
    $path = Join-Path $runtime $name
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $file = Get-Item -LiteralPath $path
        $metadata.files += [ordered]@{ name = $name; bytes = $file.Length; modifiedUtc = $file.LastWriteTimeUtc.ToString('O') }
    }
}
foreach ($name in @('engine.log','engine.log.previous')) {
    $path = Join-Path $runtime $name
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    $file = Get-Item -LiteralPath $path
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint -or $file.Length -gt 1049600) { continue }
    $reader = [IO.StreamReader]::new($path); $characters = [char[]]::new(1049600)
    try { $count = $reader.ReadBlock($characters, 0, $characters.Length); $text = [string]::new($characters, 0, $count) }
    finally { $reader.Dispose() }
    if ($secret) { $text = $text.Replace($secret, '[redacted]') }
    $text = [regex]::Replace($text, '(?i)(?:https?|ftp|sftp)://[^\s]+', '[URL redacted]')
    $text = [regex]::Replace($text, '(?im)(rpc-secret\s*[=:]\s*|token:|authorization\s*:\s*)[^\r\n]+', '$1[redacted]')
    $text = [regex]::Replace($text, '(?i)[A-Z]:\\[^\r\n]+', '[path redacted]')
    [IO.File]::WriteAllText((Join-Path $output $name), $text)
}
$metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'diagnostics.json') -Encoding utf8
Write-Host "Diagnostics saved to $output. Review these files before sharing."
$output
