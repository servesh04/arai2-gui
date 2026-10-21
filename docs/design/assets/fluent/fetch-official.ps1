$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$assetRoot = Join-Path $PSScriptRoot '.'
$revision = 'a347af95478f989038cccbf085978865264c048a'
$map = [ordered]@{
'add'='Add'; 'download'='Arrow Download'; 'play'='Play'; 'pause'='Pause'; 'retry'='Arrow Clockwise'; 'delete'='Delete'; 'folder'='Folder'; 'file'='Document'; 'copy'='Copy'; 'more'='More Horizontal'; 'search'='Search'; 'filter'='Filter'; 'settings'='Settings'; 'info'='Info'; 'warning'='Warning'; 'check'='Checkmark'; 'close'='Dismiss'; 'chevron-down'='Chevron Down'; 'arrow-up'='Arrow Up'; 'arrow-down'='Arrow Down'; 'arrow-left'='Arrow Left'; 'arrow-right'='Arrow Right'; 'columns'='Column Triple'; 'resize'='Arrow Autofit Width'; 'document'='Document Text'; 'archive'='Archive'; 'video'='Video Clip'; 'audio'='Music Note 2'; 'image'='Image'; 'code'='Code'; 'data'='Table'; 'plugins'='Plug Connected'; 'notifications'='Alert'; 'network'='Globe'; 'history'='History'; 'stop'='Stop'; 'speed'='Gauge'; 'calendar'='Calendar LTR'; 'panel'='Panel Right'; 'open'='Open'; 'link'='Link'; 'upload'='Arrow Upload'; 'chevron-right'='Chevron Right'
}
$symbols = [System.Collections.Generic.List[string]]::new()
$manifest = [System.Collections.Generic.List[object]]::new()
foreach ($key in $map.Keys) {
    $name = $map[$key]
    $upstreamFile = 'ic_fluent_' + $name.ToLower().Replace(' ','_') + '_24_regular.svg'
    $url = 'https://raw.githubusercontent.com/microsoft/fluentui-system-icons/' + $revision + '/assets/' + [uri]::EscapeDataString($name) + '/SVG/' + $upstreamFile
    $localSource = Join-Path $assetRoot $upstreamFile; if (Test-Path $localSource) { $raw = Get-Content -LiteralPath $localSource -Raw } else { $raw = (Invoke-WebRequest -Uri $url).Content }
    if ($raw -is [byte[]]) { $raw = [Text.Encoding]::UTF8.GetString($raw) }
    [xml]$xml = $raw
    if ($xml.svg.viewBox -ne '0 0 24 24') { throw "Unexpected viewBox: $key" }
    [IO.File]::WriteAllText((Join-Path $assetRoot $upstreamFile),$raw)
    $inner = $xml.svg.InnerXml.Replace('fill="#212121"','fill="currentColor"').Replace('fill="#000000"','fill="currentColor"').Replace('fill="black"','fill="currentColor"')
    $symbols.Add('<symbol id="fluent-' + $key + '" viewBox="0 0 24 24">' + $inner + '</symbol>')
    $manifest.Add([pscustomobject]@{id="fluent-$key";upstreamName=$name;source=$upstreamFile;url=$url})
    Write-Output "Fetched fluent-$key"
}
$licenseUrl = 'https://raw.githubusercontent.com/microsoft/fluentui-system-icons/' + $revision + '/LICENSE'
Invoke-WebRequest -Uri $licenseUrl -OutFile (Join-Path $assetRoot 'LICENSE')
Invoke-WebRequest -Uri ('https://raw.githubusercontent.com/microsoft/fluentui-system-icons/' + $revision + '/NOTICE') -OutFile (Join-Path $assetRoot 'NOTICE')
$sprite = '<svg xmlns="http://www.w3.org/2000/svg"><!-- Microsoft Fluent System Icons, MIT; see LICENSE and NOTICE. Source revision ' + $revision + '. Path geometry unchanged; paint uses currentColor. -->' + "`n" + ($symbols -join "`n") + "`n</svg>"
[IO.File]::WriteAllText((Join-Path $assetRoot 'fluent-sprite.svg'),$sprite)
$manifest | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $assetRoot 'sources.json')
Write-Output ('Generated sprite with ' + $symbols.Count + ' symbols')
