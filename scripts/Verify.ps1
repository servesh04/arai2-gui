$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Node.js 22+ is required for browser contract fixture compatibility tests.' }
    $nodeVersion = & node --version
    if ($LASTEXITCODE -ne 0 -or $nodeVersion -notmatch '^v(\d+)\.' -or [int]$Matches[1] -lt 22) { throw 'Node.js 22+ is required for browser contract fixture compatibility tests.' }
    $provenance = Get-Content vendor/aria2/provenance.json -Raw | ConvertFrom-Json
    if ((Get-FileHash vendor/aria2/win-x64/aria2c.exe -Algorithm SHA256).Hash -ne $provenance.executableSha256) {
        throw 'Bundled aria2 integrity check failed.'
    }
    & dotnet restore AriaGui.slnx -p:Platform=x64 --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked dependency restore failed.' }
    & dotnet build AriaGui.slnx --no-restore -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    foreach ($project in @('tests/AriaGui.Core.Tests/AriaGui.Core.Tests.csproj', 'tests/AriaGui.Application.Tests/AriaGui.Application.Tests.csproj', 'tests/AriaGui.Infrastructure.Tests/AriaGui.Infrastructure.Tests.csproj', 'tests/AriaGui.Browser.Contracts.Tests/AriaGui.Browser.Contracts.Tests.csproj')) {
        & dotnet test $project --no-build --no-restore --logger trx --results-directory artifacts/test-results
        if ($LASTEXITCODE -ne 0) { throw "Tests failed: $project" }
    }
    & node --test tests/AriaGui.Browser.Contracts.Tests/fixtures.test.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Browser contract JavaScript fixtures failed.' }
}
finally { Pop-Location }
