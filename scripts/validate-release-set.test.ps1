$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('opencode/release-set-' + [Guid]::NewGuid().ToString('N'))
$release = Join-Path $root 'release'
$stage = Join-Path $root 'stage'
$validator = Join-Path $PSScriptRoot 'validate-release-set.ps1'
$targetAbi = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Jellyfin.Target.props') -Raw)).Project.PropertyGroup.JellyfinVersion + '.0'
$plugins = @{
    AccountHub = 'bc9c3d3e-6da3-4ee5-91cd-c429d58ad901'; DiscordBridge = '4b047046-a550-4415-b204-4206f1f18201'
    PrivateSyncPlay = '09514a9e-f7a0-4c5a-91bc-3a46338298ca'; DeletionQueue = '3de88ba1-6d5c-49f5-854b-65a72f473f21'
}
function Validate { & $validator -Directory $release -Tag v1.2.3 | Out-Null }
function Reject([scriptblock]$Operation) {
    $rejected = $false
    try { & $Operation } catch { $rejected = $true }
    if (!$rejected) { throw 'Unsafe release set was accepted' }
}
try {
    New-Item -ItemType Directory -Path $release, $stage -Force | Out-Null
    foreach ($runtime in @('linux-x64','linux-arm64','win-x64')) {
        $catalog = @()
        foreach ($asset in $plugins.Keys) {
            $item = Join-Path $stage "$asset-$runtime"
            New-Item -ItemType Directory -Path $item | Out-Null
            @{ guid=$plugins[$asset]; version='1.2.3.0'; targetAbi=$targetAbi } | ConvertTo-Json | Set-Content (Join-Path $item 'meta.json')
            'synthetic fixture, not a real assembly' | Set-Content (Join-Path $item "Jellyfin.Plugin.$asset.dll")
            'MIT fixture' | Set-Content (Join-Path $item 'LICENSE')
            if ($asset -eq 'DiscordBridge') {
                'fixture notices' | Set-Content (Join-Path $item 'Activity-THIRD-PARTY-NOTICES.txt')
                '{}' | Set-Content (Join-Path $item 'activity-inventory.json')
            }
            if ($asset -eq 'DeletionQueue') { 'native fixture' | Set-Content (Join-Path $item $(if ($runtime -eq 'win-x64') { 'e_sqlite3.dll' } else { 'libe_sqlite3.so' })) }
            $name = "$asset-1.2.3.0-$runtime.zip"; $zip = Join-Path $release $name
            [IO.Compression.ZipFile]::CreateFromDirectory($item, $zip)
            "$((Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant())  $name" | Set-Content "$zip.sha256"
            '{"bomFormat":"CycloneDX"}' | Set-Content "$zip.cdx.json"
            '[]' | Set-Content "$zip.licenses.json"
            $catalog += @{ guid=$plugins[$asset]; versions=@(@{ version='1.2.3.0'; targetAbi=$targetAbi; checksum=(Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant(); sourceUrl="https://github.com/someRandomDude-a/jellyfin-plugins/releases/download/v1.2.3/$name" }) }
        }
        ConvertTo-Json -InputObject @($catalog) -Depth 8 | Set-Content (Join-Path $release "manifest-$runtime.json")
    }
    Validate
    $sha = Join-Path $release 'AccountHub-1.2.3.0-linux-x64.zip.sha256'
    $original = Get-Content $sha -Raw; 'tampered' | Set-Content $sha; Reject { Validate }; $original | Set-Content $sha
    $catalog = Join-Path $release 'manifest-win-x64.json'
    Move-Item $catalog "$catalog.missing"; Reject { Validate }; Move-Item "$catalog.missing" $catalog
    $original = Get-Content $catalog -Raw
    $original.Replace($targetAbi,'0.0.0.0') | Set-Content $catalog; Reject { Validate }; $original | Set-Content $catalog
    $original.Replace('github.com/', 'example.invalid/') | Set-Content $catalog; Reject { Validate }; $original | Set-Content $catalog
    'must never upload' | Set-Content (Join-Path $release 'unexpected.txt'); Reject { Validate }; Remove-Item (Join-Path $release 'unexpected.txt')
    Validate
    Write-Output 'PASS offline merged-release validation: complete three-platform fixture, checksum tamper, missing catalog, ABI/URL mismatch and unexpected-file rejection.'
    $single = Join-Path $root 'single'
    New-Item -ItemType Directory $single | Out-Null
    foreach ($runtime in @('linux-x64','linux-arm64','win-x64')) {
        Get-ChildItem $release -Filter "PrivateSyncPlay-*-$runtime.zip*" | Copy-Item -Destination $single
        $entry = @(Get-Content (Join-Path $release "manifest-$runtime.json") -Raw | ConvertFrom-Json | Where-Object guid -eq $plugins.PrivateSyncPlay)
        $entry[0].versions[0].sourceUrl = $entry[0].versions[0].sourceUrl.Replace('/download/v1.2.3/', '/download/private-syncplay%2Fv1.2.3/')
        ConvertTo-Json -InputObject @($entry) -Depth 8 | Set-Content (Join-Path $single "manifest-$runtime.json")
    }
    & $validator -Directory $single -Tag v1.2.3 -ReleaseTag private-syncplay/v1.2.3 -Plugin private-syncplay | Out-Null
    Reject { & $validator -Directory $single -Tag v1.2.3 -ReleaseTag discord-bridge/v1.2.3 -Plugin private-syncplay }
    Reject { & $validator -Directory $single -Tag v1.2.3 -ReleaseTag private-syncplay/v1.2.3 -Plugin discord-bridge }
    Reject { & $validator -Directory $single -Tag v1.2.3 }
    Write-Output 'PASS independent three-platform release, plugin-prefixed URLs and cross-plugin/tag rejection without unrelated assets.'
} finally { if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
