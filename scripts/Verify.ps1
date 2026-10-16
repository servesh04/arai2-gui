$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Node.js 22+ is required for browser contract fixture compatibility tests.' }
    $nodeVersion = & node --version
    if ($LASTEXITCODE -ne 0 -or $nodeVersion -notmatch '^v(\d+)\.' -or [int]$Matches[1] -lt 22) { throw 'Node.js 22+ is required for browser contract fixture compatibility tests.' }
    foreach ($browserTool in @('BrowserRegistration.psm1','Register-BrowserHost.ps1','New-BrowserBundle.ps1','Install-BrowserCompanion.ps1','Test-BrowserRegistration.ps1','Test-BrowserCompanionInstaller.ps1','Test-GatewayRuntimeProbe.ps1','Register-OpenProtocol.ps1','Test-OpenProtocolRegistration.ps1','Test-OpenActivationGui.ps1')) {
        $parseTokens = $null; $parseErrors = $null
        $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $browserTool), [ref]$parseTokens, [ref]$parseErrors)
        if ($parseErrors.Count) { throw "Browser tool syntax failed: $browserTool" }
    }
    $provenance = Get-Content vendor/aria2/provenance.json -Raw | ConvertFrom-Json
    & (Join-Path $PSScriptRoot 'Test-OpenActivationFixtureAssertions.ps1')
    foreach($fixtureTool in @('OpenActivationFixtureAssertions.psm1','Test-OpenActivationFixtureAssertions.ps1','Test-CanonicalOpenDelivery.ps1','OpenProtocolInstaller.psm1','Install-OpenProtocol.ps1','Test-OpenProtocolInstaller.ps1','New-PhaseCSandboxBundle.ps1','Test-PhaseCGuestReadiness.ps1')){
        $parseTokens=$null;$parseErrors=$null
        $null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $fixtureTool),[ref]$parseTokens,[ref]$parseErrors)
        if($parseErrors.Count){throw "Open activation fixture syntax failed: $fixtureTool"}
    }
    if ((Get-FileHash vendor/aria2/win-x64/aria2c.exe -Algorithm SHA256).Hash -ne $provenance.executableSha256) {
        throw 'Bundled aria2 integrity check failed.'
    }
    & dotnet restore AriaGui.slnx -p:Platform=x64 --locked-mode -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'Locked dependency restore failed.' }
    & dotnet build AriaGui.slnx --no-restore -p:Platform=x64 -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    foreach ($project in @('tests/AriaGui.Core.Tests/AriaGui.Core.Tests.csproj', 'tests/AriaGui.Application.Tests/AriaGui.Application.Tests.csproj', 'tests/AriaGui.Infrastructure.Tests/AriaGui.Infrastructure.Tests.csproj', 'tests/AriaGui.Browser.Contracts.Tests/AriaGui.Browser.Contracts.Tests.csproj')) {
        & dotnet test $project --no-build --no-restore --logger trx --results-directory artifacts/test-results
        if ($LASTEXITCODE -ne 0) { throw "Tests failed: $project" }
    }
    & node --test tests/AriaGui.Browser.Contracts.Tests/fixtures.test.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Browser contract JavaScript fixtures failed.' }
    & node extensions/aria-gui/build.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Browser extension generation failed.' }
    & node --test extensions/aria-gui/tests/*.test.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Browser extension tests failed.' }
}
finally { Pop-Location }
