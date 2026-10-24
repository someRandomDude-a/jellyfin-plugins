param([Parameter(Mandatory)][string]$PackagesDirectory, [string]$NugetCache = '')
$ErrorActionPreference = 'Stop'
if (!$NugetCache) { $NugetCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' } }
$root = (Resolve-Path -LiteralPath $PackagesDirectory).Path
$manifest = @(Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw | ConvertFrom-Json)
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($plugin in $manifest) {
    $asset = [IO.Path]::GetFileName(([Uri]$plugin.versions[0].sourceUrl).AbsolutePath)
    if ($asset -notmatch '^[A-Za-z0-9_.-]+\.zip$') { throw 'Invalid manifest asset name' }
    $zipPath = Join-Path $root $asset
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        if (@($archive.Entries | Where-Object { $_.FullName -ne [IO.Path]::GetFileName($_.FullName) }).Count) { throw 'Expected allowlisted ZIP root files only' }
        $files = @($archive.Entries | Where-Object { $_.Name -match '\.(dll|so)$' })
        $names = @($files | ForEach-Object Name)
        $deps = @($archive.Entries | Where-Object Name -like '*.deps.json')
        if ($deps.Count -ne 1) { throw 'Expected exactly one plugin dependency manifest' }
        $reader = [IO.StreamReader]::new($deps[0].Open())
        try { $graph = $reader.ReadToEnd() | ConvertFrom-Json -AsHashtable } finally { $reader.Dispose() }
        $components = [Collections.Generic.List[object]]::new()
        $licenses = [Collections.Generic.List[object]]::new()
        foreach ($key in $graph.libraries.Keys | Sort-Object) {
            if ($graph.libraries[$key].type -ne 'package') { continue }
            $shipped = @()
            foreach ($target in $graph.targets.Values) {
                if (!$target.ContainsKey($key)) { continue }
                foreach ($kind in @('runtime','native','runtimeTargets')) {
                    if (!$target[$key].ContainsKey($kind)) { continue }
                    $shipped += @($target[$key][$kind].Keys | ForEach-Object { [IO.Path]::GetFileName($_) } | Where-Object { $_ -in $names })
                }
            }
            $shipped = @($shipped | Sort-Object -Unique)
            if (!$shipped.Count) { continue } # SDK/server dependencies not distributed are excluded.
            $id, $version = $key.Split('/')
            $nuspec = Join-Path $NugetCache ($id.ToLowerInvariant() + '/' + $version.ToLowerInvariant() + '/' + $id.ToLowerInvariant() + '.nuspec')
            if (!(Test-Path -LiteralPath $nuspec)) { throw "Restore pinned package metadata before inventory: $key" }
            $xmlSettings = [Xml.XmlReaderSettings]::new(); $xmlSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit; $xmlSettings.XmlResolver = $null
            $xmlReader = [Xml.XmlReader]::Create($nuspec, $xmlSettings)
            try { $xml = [Xml.XmlDocument]::new(); $xml.XmlResolver = $null; $xml.Load($xmlReader) } finally { $xmlReader.Dispose() }
            $license = $xml.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='license']")
            $expression = if ($license -and $license.GetAttribute('type') -eq 'expression') { $license.InnerText } else { $null }
            $licenseFile = if ($license -and $license.GetAttribute('type') -eq 'file') { $license.InnerText } else { $null }
            $purl = 'pkg:nuget/' + $id + '@' + $version
            $component = [ordered]@{ type='library'; 'bom-ref'=$purl; name=$id; version=$version; purl=$purl }
            if ($expression) { $component.licenses = @(@{ expression=$expression }) }
            $components.Add($component)
            $licenses.Add([ordered]@{ package=$id; version=$version; expression=$expression; packageLicenseFile=$licenseFile; shippedFiles=$shipped; reviewRequired=$true })
        }
        foreach ($file in $files) {
            $stream = $file.Open()
            try { $hash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            $components.Add([ordered]@{ type='file'; 'bom-ref'=('file:' + $file.Name); name=$file.Name; hashes=@(@{ alg='SHA-256'; content=$hash }) })
        }
        $bom = [ordered]@{ bomFormat='CycloneDX'; specVersion='1.6'; serialNumber=('urn:uuid:' + [Guid]::NewGuid()); version=1; metadata=@{ component=@{ type='application'; name=$plugin.name; version=$plugin.versions[0].version; hashes=@(@{ alg='SHA-256'; content=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant() }) } }; components=@($components.ToArray()) }
        $bom | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath "$zipPath.cdx.json" -Encoding utf8NoBOM
        [ordered]@{ plugin=$plugin.name; archive=$asset; scope='Actual shipped binaries mapped to pinned NuGet metadata; not legal approval or a complete source provenance attestation'; packages=@($licenses.ToArray()) } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath "$zipPath.licenses.json" -Encoding utf8NoBOM
        Write-Output "Inventoried shipped binaries and pinned package license metadata: $asset"
    } finally { $archive.Dispose() }
}
