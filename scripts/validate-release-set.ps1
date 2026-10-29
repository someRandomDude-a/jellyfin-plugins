param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$Tag, [string]$Repository = 'someRandomDude-a/jellyfin-plugins',
    [ValidateSet('all','account-hub','discord-bridge','private-syncplay','deletion-queue','notifications','automatic-deletion-requests')][string]$Plugin = 'all', [string]$ReleaseTag = '')
$ErrorActionPreference = 'Stop'
if ($Tag -notmatch '^v(\d+\.\d+\.\d+)$') { throw 'Expected immutable vX.Y.Z tag' }
$version = $Matches[1] + '.0'
if (!$ReleaseTag) { $ReleaseTag = $Tag }
if ($ReleaseTag -cne $Tag -and ($Plugin -eq 'all' -or $ReleaseTag -cne "$Plugin/$Tag")) { throw 'Release tag must match the selected plugin and version' }
$downloadTag = [Uri]::EscapeDataString($ReleaseTag)
. (Join-Path $PSScriptRoot 'test-targets.ps1')
Initialize-TestTargets
$targetAbi = ([xml](Get-Content -LiteralPath $env:SUITE_JELLYFIN_PROPS -Raw)).Project.PropertyGroup.JellyfinTargetAbi
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid repository' }
$plugins = @{
    AutomaticDeletionRequests = 'a6429fb2-c67c-459d-b6a7-579c4a05dace'
    Notifications = 'c18d78ec-4b70-47cf-a0d2-936d987c37ba'
    AccountHub = 'bc9c3d3e-6da3-4ee5-91cd-c429d58ad901'
    DiscordBridge = '4b047046-a550-4415-b204-4206f1f18201'
    PrivateSyncPlay = '09514a9e-f7a0-4c5a-91bc-3a46338298ca'
    DeletionQueue = '3de88ba1-6d5c-49f5-854b-65a72f473f21'
}
$assets = @{ 'account-hub'='AccountHub'; 'discord-bridge'='DiscordBridge'; 'private-syncplay'='PrivateSyncPlay'; 'deletion-queue'='DeletionQueue'; 'notifications'='Notifications'; 'automatic-deletion-requests'='AutomaticDeletionRequests' }
if ($Plugin -ne 'all') { $asset = $assets[$Plugin]; $plugins = @{ $asset = $plugins[$asset] } }
$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($runtime in @('linux-x64','linux-arm64','win-x64')) {
    $catalogName = "manifest-$runtime.json"
    [void]$expected.Add($catalogName)
    $catalog = @(Get-Content -LiteralPath (Join-Path $Directory $catalogName) -Raw | ConvertFrom-Json)
    if ($catalog.Count -ne $plugins.Count) { throw "Wrong plugin count in $catalogName" }
    foreach ($asset in $plugins.Keys) {
        $entries = @($catalog | Where-Object { $_.guid -eq $plugins[$asset] })
        if ($entries.Count -ne 1) { throw "Missing/duplicate stable plugin identity: $asset/$runtime" }
        $releases = @($entries[0].versions | Where-Object version -eq $version)
        if ($releases.Count -ne 1 -or $releases[0].targetAbi -ne $targetAbi) { throw "Version/ABI mismatch: $asset/$runtime" }
        $name = "$asset-$version-$runtime.zip"
        $zip = Join-Path $Directory $name
        foreach ($suffix in @('', '.sha256', '.cdx.json', '.licenses.json')) {
            [void]$expected.Add($name + $suffix)
            if (!(Test-Path -LiteralPath ($zip + $suffix) -PathType Leaf)) { throw "Missing release artifact: $name$suffix" }
        }
        if ($releases[0].sourceUrl -cne "https://github.com/$Repository/releases/download/$downloadTag/$name") { throw "Wrong release URL: $name" }
        if ($releases[0].checksum -cne (Get-FileHash -LiteralPath $zip -Algorithm MD5).Hash.ToLowerInvariant()) { throw "Catalog checksum mismatch: $name" }
        $sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ((Get-Content -LiteralPath "$zip.sha256" -Raw).Trim() -cne "$sha  $name") { throw "SHA256 mismatch: $name" }
        $bom = Get-Content -LiteralPath "$zip.cdx.json" -Raw | ConvertFrom-Json
        if ($bom.bomFormat -ne 'CycloneDX') { throw "Invalid SBOM: $name" }
        $null = Get-Content -LiteralPath "$zip.licenses.json" -Raw | ConvertFrom-Json
        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $names = @($archive.Entries | ForEach-Object FullName)
            if ($asset -eq 'DeletionQueue' -and @($names | Where-Object { $_ -match '(?i)skia' }).Count) { throw "Host Skia runtime must not be shipped: $name" }
            if (@($names | Where-Object { $_ -match '[/\\]|(?i)(^\.env|\.(key|db|pfx|p12)$)' }).Count) { throw "Unsafe archive entry: $name" }
            foreach ($required in @('meta.json','LICENSE',"Jellyfin.Plugin.$asset.dll")) {
                if (@($names | Where-Object { $_ -ceq $required }).Count -ne 1) { throw "Missing/duplicate required ZIP entry: $name/$required" }
            }
            $reader = [IO.StreamReader]::new($archive.GetEntry('meta.json').Open())
            try { $meta = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            if ($meta.guid -ne $plugins[$asset] -or $meta.version -ne $version -or $meta.targetAbi -ne $targetAbi) { throw "Installed/catalog identity mismatch: $name" }
            if ($asset -in @('DeletionQueue','Notifications')) {
                $native = if ($runtime -eq 'win-x64') { 'e_sqlite3.dll' } else { 'libe_sqlite3.so' }
                if ($native -notin $names) { throw "Missing native SQLite runtime: $name" }
            }
            if ($asset -eq 'Notifications' -and 'Jellyfin.LibraryNotifications.dll' -notin $names) { throw "Missing Notifications library observer dependency: $name" }
        } finally { $archive.Dispose() }
    }
}
foreach ($file in Get-ChildItem -LiteralPath $Directory -Force) {
    if ($file.PSIsContainer -or !$expected.Contains($file.Name)) { throw "Unexpected release artifact: $($file.Name)" }
}
Write-Output "PASS complete $Plugin release set: $($plugins.Count * 3) ZIPs, three catalogs, stable identities, version/ABI, URLs, MD5/SHA256, SBOM/license inventories and archive safety."
