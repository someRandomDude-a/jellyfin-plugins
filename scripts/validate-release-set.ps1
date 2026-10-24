param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$Tag, [string]$Repository = 'someRandomDude-a/jellyfin-plugins')
$ErrorActionPreference = 'Stop'
if ($Tag -notmatch '^v(\d+\.\d+\.\d+)$') { throw 'Expected immutable vX.Y.Z tag' }
$version = $Matches[1] + '.0'
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid repository' }
$plugins = @{
    AccountHub = 'bc9c3d3e-6da3-4ee5-91cd-c429d58ad901'
    DiscordBridge = '4b047046-a550-4415-b204-4206f1f18201'
    PrivateSyncPlay = '09514a9e-f7a0-4c5a-91bc-3a46338298ca'
    DeletionQueue = '3de88ba1-6d5c-49f5-854b-65a72f473f21'
}
$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($runtime in @('linux-x64','linux-arm64','win-x64')) {
    $catalogName = "manifest-$runtime.json"
    [void]$expected.Add($catalogName)
    $catalog = @(Get-Content -LiteralPath (Join-Path $Directory $catalogName) -Raw | ConvertFrom-Json)
    if ($catalog.Count -ne 4) { throw "Expected four plugins in $catalogName" }
    foreach ($asset in $plugins.Keys) {
        $entries = @($catalog | Where-Object { $_.guid -eq $plugins[$asset] })
        if ($entries.Count -ne 1) { throw "Missing/duplicate stable plugin identity: $asset/$runtime" }
        $releases = @($entries[0].versions | Where-Object version -eq $version)
        if ($releases.Count -ne 1 -or $releases[0].targetAbi -ne '12.2.0.0') { throw "Version/ABI mismatch: $asset/$runtime" }
        $name = "$asset-$version-$runtime.zip"
        $zip = Join-Path $Directory $name
        foreach ($suffix in @('', '.sha256', '.cdx.json', '.licenses.json')) {
            [void]$expected.Add($name + $suffix)
            if (!(Test-Path -LiteralPath ($zip + $suffix) -PathType Leaf)) { throw "Missing release artifact: $name$suffix" }
        }
        if ($releases[0].sourceUrl -cne "https://github.com/$Repository/releases/download/$Tag/$name") { throw "Wrong release URL: $name" }
        if ($releases[0].checksum -cne (Get-FileHash -LiteralPath $zip -Algorithm MD5).Hash.ToLowerInvariant()) { throw "Catalog checksum mismatch: $name" }
        $sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ((Get-Content -LiteralPath "$zip.sha256" -Raw).Trim() -cne "$sha  $name") { throw "SHA256 mismatch: $name" }
        $bom = Get-Content -LiteralPath "$zip.cdx.json" -Raw | ConvertFrom-Json
        if ($bom.bomFormat -ne 'CycloneDX') { throw "Invalid SBOM: $name" }
        $null = Get-Content -LiteralPath "$zip.licenses.json" -Raw | ConvertFrom-Json
        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $names = @($archive.Entries | ForEach-Object FullName)
            if (@($names | Where-Object { $_ -match '[/\\]|(?i)(^\.env|\.(key|db|pfx|p12)$)' }).Count) { throw "Unsafe archive entry: $name" }
            foreach ($required in @('meta.json','LICENSE',"Jellyfin.Plugin.$asset.dll")) {
                if (@($names | Where-Object { $_ -ceq $required }).Count -ne 1) { throw "Missing/duplicate required ZIP entry: $name/$required" }
            }
            $reader = [IO.StreamReader]::new($archive.GetEntry('meta.json').Open())
            try { $meta = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            if ($meta.guid -ne $plugins[$asset] -or $meta.version -ne $version -or $meta.targetAbi -ne '12.2.0.0') { throw "Installed/catalog identity mismatch: $name" }
            if ($asset -eq 'DiscordBridge' -and ('Activity-THIRD-PARTY-NOTICES.txt' -notin $names -or 'activity-inventory.json' -notin $names)) { throw "Missing Activity distribution inventory: $name" }
            if ($asset -eq 'DeletionQueue') {
                $native = if ($runtime -eq 'win-x64') { 'e_sqlite3.dll' } else { 'libe_sqlite3.so' }
                if ($native -notin $names) { throw "Missing native SQLite runtime: $name" }
            }
        } finally { $archive.Dispose() }
    }
}
foreach ($file in Get-ChildItem -LiteralPath $Directory -Force) {
    if ($file.PSIsContainer -or !$expected.Contains($file.Name)) { throw "Unexpected release artifact: $($file.Name)" }
}
Write-Output 'PASS complete release set: 12 ZIPs, three catalogs, stable identities, version/ABI, URLs, MD5/SHA256, SBOM/license inventories and archive safety.'
