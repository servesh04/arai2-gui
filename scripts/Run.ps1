param([switch]$Unpackaged)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $project = 'src/AriaGui.App/AriaGui.App.csproj'
    $buildOptions = @('-p:Platform=x64')
    $launchProfile = 'AriaGui.App (Package)'
    if ($Unpackaged) {
        $buildOptions += '-p:AriaGuiUnpackaged=true'
        $launchProfile = 'AriaGui.App (Unpackaged)'
    }
    & dotnet restore $project @buildOptions
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    & dotnet build $project --no-restore @buildOptions
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & dotnet run --project $project --no-build --no-restore @buildOptions --launch-profile $launchProfile
    if ($LASTEXITCODE -ne 0) { throw 'App launch failed. For an MSIX Developer Mode error, see docs/DEVELOPMENT.md.' }
}
finally { Pop-Location }
