[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackagePath, [string]$ReportPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$provenance = Get-Content -LiteralPath (Join-Path $projectRoot 'vendor/aria2/provenance.json') -Raw | ConvertFrom-Json
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $entries = @{}
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($entries.ContainsKey($name) -or !$name.StartsWith('AriaGUI/', [StringComparison]::Ordinal) -or
            $name.Contains('../') -or $name.Contains('/./') -or $name.Contains(':')) {
            throw "Invalid or duplicate portable ZIP entry: $name"
        }
        $entries[$name] = $entry
    }
    function Require-Entry([string]$name) {
        $path = 'AriaGUI/' + $name
        if (!$entries.ContainsKey($path) -or $entries[$path].Length -eq 0) { throw "Missing/empty portable dependency: $path" }
    }
    function Read-Entry([string]$name) {
        Require-Entry $name
        $reader = [IO.StreamReader]::new($entries['AriaGUI/' + $name].Open())
        try { $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    function Entry-Hash([string]$name) {
        Require-Entry $name
        $stream = $entries['AriaGUI/' + $name].Open()
        try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
    }
    foreach ($name in @('AriaGui.App.exe', 'AriaGui.App.dll', 'AriaGui.Application.dll',
        'AriaGui.Infrastructure.dll', 'AriaGui.App.pri', 'App.xbf', 'coreclr.dll', 'hostfxr.dll',
        'hostpolicy.dll', 'Microsoft.ui.xaml.dll', 'e_sqlite3.dll', 'Engine/aria2c.exe',
        'Engine/Notices/provenance.json', 'Notices/dependency-inventory.json', 'LICENSE', 'RELEASE_NOTES.md',
        'host/AriaGui.NativeHost.exe', 'host/hostfxr.dll', 'host/coreclr.dll',
        'Microsoft.AspNetCore.Server.Kestrel.Core.dll')) {
        Require-Entry $name
    }
    $licenseHash = (Get-FileHash -LiteralPath (Join-Path $projectRoot 'LICENSE') -Algorithm SHA256).Hash
    if ((Entry-Hash 'LICENSE') -ne $licenseHash) { throw 'Portable AriaGUI license differs from the repository license.' }
    if ((Entry-Hash 'Engine/aria2c.exe') -ne $provenance.executableSha256) {
        throw 'Portable engine hash differs from the pinned binary.'
    }
    foreach ($notice in @($provenance.notices) + @('provenance.json')) {
        $expected = (Get-FileHash -LiteralPath (Join-Path $projectRoot "vendor/aria2/$notice") -Algorithm SHA256).Hash
        if ((Entry-Hash "Engine/Notices/$notice") -ne $expected) { throw "Portable engine notice differs: $notice" }
    }
    $source = Read-Entry 'Source/aria2/source-manifest.json' | ConvertFrom-Json
    if ($source.aria2Version -ne $provenance.version -or
        $source.bundledExecutableSha256 -ne $provenance.executableSha256) {
        throw 'Portable aria2 source manifest does not match the bundled engine.'
    }
    foreach ($group in @(@('sourceArchives', 'archives'), @('buildFiles', 'build'))) {
        $pinned = @($provenance.($group[0]))
        $listed = @($source.($group[0]))
        if ($pinned.Count -ne $listed.Count) { throw "Portable aria2 $($group[0]) count differs." }
        foreach ($item in $pinned) {
            $matches = @($listed | Where-Object name -eq $item.name)
            if ($matches.Count -ne 1 -or $matches[0].sha256 -ne $item.sha256 -or
                $matches[0].url -ne $item.url -or
                (Entry-Hash "Source/aria2/$($group[1])/$($item.name)") -ne $item.sha256) {
                throw "Portable aria2 source differs: $($item.name)"
            }
        }
    }
    if (@($source.linkedLibraryNotices).Count -ne 8) { throw 'Portable linked-library notices are incomplete.' }
    foreach ($notice in $source.linkedLibraryNotices) {
        if ($notice.component -notmatch '^[A-Za-z0-9-]+$' -or $notice.name -notmatch '^[A-Za-z0-9._-]+$' -or
            (Entry-Hash "Notices/EngineStatic/$($notice.component)/$($notice.name)") -ne $notice.sha256) {
            throw "Portable linked-library notice differs: $($notice.component)/$($notice.name)"
        }
    }
    $inventory = Read-Entry 'Notices/dependency-inventory.json' | ConvertFrom-Json
    foreach ($name in @('Notices/LicenseTexts/MIT.txt', 'Notices/LicenseTexts/Apache-2.0.txt',
        'Notices/Fluent-UI-System-Icons/LICENSE', 'Notices/Fluent-UI-System-Icons/NOTICE',
        'Notices/Microsoft.WindowsAppSDK-2.5.1/license.txt', 'Notices/Microsoft.WindowsAppSDK-2.5.1/NOTICE.txt',
        'Notices/Microsoft.WindowsAppSDK.Runtime-2.5.1/NOTICE.txt',
        'Notices/Microsoft.Windows.AI.MachineLearning-2.1.74/ThirdPartyNotices.txt',
        'Notices/Microsoft.Web.WebView2-1.0.3719.77/LICENSE.txt', 'Notices/Microsoft.Web.WebView2-1.0.3719.77/NOTICE.txt',
        'Notices/SQLite-3.53.4/LICENSE.txt', 'Notices/CommunityToolkit.Mvvm-8.4.0/License.md')) {
        Require-Entry $name
    }
    foreach ($dependency in $inventory.packages) {
        if ($dependency.licenseType -eq 'expression') { Require-Entry ('Notices/' + $dependency.licenseTextFile) }
    }
    $runtime = (Read-Entry 'AriaGui.App.runtimeconfig.json' | ConvertFrom-Json).runtimeOptions
    foreach ($id in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.AspNetCore.App.Runtime.win-x64')) {
        $notice = @($inventory.packages | Where-Object id -eq $id)
        if ($notice.Count -ne 1) { throw "Embedded runtime notice record missing: $id" }
        foreach ($name in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
            Require-Entry "Notices/$id-$($notice[0].version)/$name"
        }
        $framework = $id.Replace('.Runtime.win-x64', '')
        if (@($runtime.includedFrameworks | Where-Object name -eq $framework)[0].version -ne $notice[0].version) {
            throw "Embedded runtime and notice version differ: $id"
        }
    }
    if ($runtime.framework -or $runtime.frameworks) { throw 'Portable app unexpectedly requires an external .NET runtime.' }
    $families = [ordered]@{ FirstParty = 0; Aria2 = 0; DotNet = 0; WindowsAppSdk = 0
        WebView2 = 0; SQLite = 0; EntityFramework = 0; CommunityToolkit = 0 }
    foreach ($name in $entries.Keys | Where-Object { $_ -match '^AriaGUI/.+\.(dll|exe)$' }) {
        $file = Split-Path $name -Leaf
        $family = switch -Regex ($file) {
            '^aria2c\.exe$' { 'Aria2'; break }
            '^AriaGui\.' { 'FirstParty'; break }
            '^(System\.|Microsoft\.AspNetCore\.|Microsoft\.Extensions\.|Microsoft\.CSharp|Microsoft\.VisualBasic|Microsoft\.Win32|Microsoft\.JSInterop|Microsoft\.Net\.Http\.Headers|Microsoft\.DiaSymReader|WindowsBase|netstandard|mscor|coreclr|clr|hostfxr|hostpolicy|createdump|dcompi|marshal|msquic|aspnetcorev2_inprocess)' { 'DotNet'; break }
            '^(Microsoft\.Windows|Microsoft\.UI\.|Microsoft\.WinUI|Microsoft\.Graphics|Microsoft\.InputStateManager|Microsoft\.InteractiveExperiences|Microsoft\.Internal\.FrameworkUdk|Microsoft\.Security\.Authentication\.OAuth|Microsoft\.Asg\.|Microsoft\.DirectManipulation|WinRT\.|CoreMessagingXP|dwmcorei|DwmSceneI|DWriteCore|MRM|NPUDetect|PerceptiveStreaming|PushNotifications|RestartAgent|SessionHandle|wuceffectsi|DirectML|onnxruntime|Microsoft\.ML\.OnnxRuntime|WinUIEdit)' { 'WindowsAppSdk'; break }
            '^(Microsoft\.Web|WebView2)' { 'WebView2'; break }
            '^(SQLitePCLRaw\.|e_sqlite3)' { 'SQLite'; break }
            '^(Microsoft\.Data\.Sqlite|Microsoft\.EntityFrameworkCore)' { 'EntityFramework'; break }
            '^CommunityToolkit\.' { 'CommunityToolkit'; break }
            default { throw "Unreviewed shipped binary: $name" }
        }
        $families[$family]++
    }
    if (@($families.Values | Where-Object { $_ -eq 0 }).Count -ne 0) { throw 'A reviewed binary family is absent from the portable ZIP.' }
    foreach ($name in $entries.Keys) {
        if ($name -match '(?i)(^|/)(downloads\.db.*|aria2\.conf|engine\.owner\.json|preferences\.json|.*\.pfx|.*\.pdb|.*\.log)$' -or
            $name -match '(?i)(AriaGui\.EngineFixture|testhost|/sessions/|\.Tests\.)') {
            throw "Private or development file in portable ZIP: $name"
        }
    }
    $report = [ordered]@{
        checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        package = $package
        sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
        bytes = (Get-Item -LiteralPath $package).Length
        entries = $entries.Count
        architecture = 'x64'
        engineVersion = $provenance.version
        engineSha256 = $provenance.executableSha256
        selfContainedDotNet = $true
        selfContainedWindowsAppSdk = $true
        shippedBinaryFamilies = $families
        bundledAria2SourceInputs = @($source.sourceArchives).Count
        cleanMachineAcceptance = 'Pending'
    }
    if ($ReportPath) {
        $output = [IO.Path]::GetFullPath($ReportPath)
        $allowed = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
        if (!$output.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Portable reports must stay inside workspace artifacts.' }
        $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $output -Encoding utf8
    }
    [pscustomobject]$report
}
finally { $archive.Dispose() }
