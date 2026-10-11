$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $provenance = Get-Content vendor/aria2/provenance.json -Raw | ConvertFrom-Json
    if ((Get-FileHash vendor/aria2/win-x64/aria2c.exe -Algorithm SHA256).Hash -ne $provenance.executableSha256) {
        throw 'Bundled aria2 integrity check failed.'
    }
    & dotnet restore AriaGui.slnx -p:Platform=x64 --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked dependency restore failed.' }
    & dotnet build AriaGui.slnx --no-restore -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    foreach ($project in @('tests/AriaGui.Core.Tests/AriaGui.Core.Tests.csproj', 'tests/AriaGui.Application.Tests/AriaGui.Application.Tests.csproj', 'tests/AriaGui.Infrastructure.Tests/AriaGui.Infrastructure.Tests.csproj')) {
        & dotnet test $project --no-build --no-restore --logger trx --results-directory artifacts/test-results
        if ($LASTEXITCODE -ne 0) { throw "Tests failed: $project" }
    }
}
finally { Pop-Location }
