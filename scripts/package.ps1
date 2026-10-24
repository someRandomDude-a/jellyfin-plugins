param(
    [Parameter(Mandatory)][string]$Tag,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository = 'someRandomDude-a/jellyfin-plugins',
    [ValidateSet('linux-x64','linux-arm64','win-x64')][string]$Runtime = 'linux-x64',
    [string]$OutputDirectory = '',
    [string]$PreviousManifest = '',
    [ValidateSet('all','account-hub','discord-bridge','private-syncplay','deletion-queue','notifications')][string]$Plugin = 'all',
    [string]$ReleaseTag = ''
)
$ErrorActionPreference = 'Stop'
$jsonOptions = @{}
if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { $jsonOptions.DateKind = 'String' }
if ($Tag -notmatch '^v(\d+\.\d+\.\d+)$') { throw 'Use an immutable vX.Y.Z release tag.' }
$version = $Matches[1] + '.0'
if (!$ReleaseTag) { $ReleaseTag = $Tag }
if ($ReleaseTag -cne $Tag -and ($Plugin -eq 'all' -or $ReleaseTag -cne "$Plugin/$Tag")) { throw 'Release tag must match the selected plugin and version' }
$downloadTag = [Uri]::EscapeDataString($ReleaseTag)
$root = Split-Path $PSScriptRoot -Parent
$targetAbi = ([xml](Get-Content -LiteralPath (Join-Path $root 'Jellyfin.Target.props') -Raw)).Project.PropertyGroup.JellyfinVersion + '.0'
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root "artifacts/release/$Tag/$Runtime" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$plugins = @(
    @{ name='Notifications'; id='c18d78ec-4b70-47cf-a0d2-936d987c37ba'; project='plugins/notifications/src/Jellyfin.Plugin.Notifications/Jellyfin.Plugin.Notifications.csproj'; asset='Notifications'; files=@('Jellyfin.Plugin.Notifications.dll','Jellyfin.Plugin.Notifications.deps.json','AccountHub.Notifications.dll','AccountHub.Api.dll','DiscordBridge.Api.dll','Microsoft.Data.Sqlite.dll','SQLitePCLRaw.core.dll','SQLitePCLRaw.provider.e_sqlite3.dll','SQLitePCLRaw.batteries_v2.dll',$(if ($Runtime -eq 'win-x64') {'e_sqlite3.dll'} else {'libe_sqlite3.so'})); description='Independent shared notification inbox, settings and delivery with native Jellyfin authorization.' },
    @{ name='Account Hub'; id='bc9c3d3e-6da3-4ee5-91cd-c429d58ad901'; project='plugins/account-hub/src/AccountHub/AccountHub.csproj'; asset='AccountHub'; files=@('Jellyfin.Plugin.AccountHub.dll','Jellyfin.Plugin.AccountHub.deps.json','AccountHub.Core.dll','AccountHub.Api.dll','AccountHub.Security.dll','Jellyfin.Security.Api.dll','Jellyfin.LibraryNotifications.dll','AccountHub.Notifications.dll','AccountHub.Lifecycle.dll','DiscordBridge.Api.dll','Dapper.dll'); description='Integration preview. Unified account workspace with embedded Security and Notifications; see documented acceptance limits.' },
    @{ name='Discord Bridge'; id='4b047046-a550-4415-b204-4206f1f18201'; project='plugins/discord-bridge/src/Jellyfin.Plugin.DiscordBridge/Jellyfin.Plugin.DiscordBridge.csproj'; asset='DiscordBridge'; files=@('Jellyfin.Plugin.DiscordBridge.dll','Jellyfin.Plugin.DiscordBridge.deps.json','DiscordBridge.Api.dll','AccountHub.Api.dll','Jellyfin.Security.Api.dll','Jellyfin.LibraryNotifications.dll','Jellyfin.Plugin.PrivateSyncPlay.Api.dll','Discord.Net.Commands.dll','Discord.Net.Core.dll','Discord.Net.Dave.dll','Discord.Net.Interactions.dll','Discord.Net.Rest.dll','Discord.Net.Webhook.dll','Discord.Net.WebSocket.dll','Newtonsoft.Json.dll'); description='Independent Discord transport with optional Hub self-service and native watch-party invitations.' },
    @{ name='Private SyncPlay'; id='09514a9e-f7a0-4c5a-91bc-3a46338298ca'; project='plugins/private-syncplay/src/Jellyfin.Plugin.PrivateSyncPlay/Jellyfin.Plugin.PrivateSyncPlay.csproj'; asset='PrivateSyncPlay'; files=@('Jellyfin.Plugin.PrivateSyncPlay.dll','Jellyfin.Plugin.PrivateSyncPlay.deps.json','Jellyfin.Plugin.PrivateSyncPlay.Api.dll','PrivateSyncPlay.IntegrationStore.dll','AccountHub.Api.dll'); description='Private watch parties and isolated guest playback.' },
    @{ name='Deletion Queue'; id='3de88ba1-6d5c-49f5-854b-65a72f473f21'; project='plugins/deletion-queue/src/Jellyfin.Plugin.DeletionQueue/Jellyfin.Plugin.DeletionQueue.csproj'; asset='DeletionQueue'; files=@('Jellyfin.Plugin.DeletionQueue.dll','Jellyfin.Plugin.DeletionQueue.deps.json','DeletionQueue.Core.dll','DeletionQueue.Infrastructure.dll','AccountHub.Api.dll','Microsoft.Data.Sqlite.dll','SQLitePCLRaw.core.dll','SQLitePCLRaw.provider.e_sqlite3.dll','SQLitePCLRaw.batteries_v2.dll',$(if ($Runtime -eq 'win-x64') {'e_sqlite3.dll'} else {'libe_sqlite3.so'})); description='Standalone consent-driven deletion workflow with authenticated evidence.' }
)
$assets = @{ 'account-hub'='AccountHub'; 'discord-bridge'='DiscordBridge'; 'private-syncplay'='PrivateSyncPlay'; 'deletion-queue'='DeletionQueue'; 'notifications'='Notifications' }
if ($Plugin -ne 'all') { $plugins = @($plugins | Where-Object asset -eq $assets[$Plugin]) }
$history = if ($PreviousManifest) { @(Get-Content -LiteralPath $PreviousManifest -Raw | ConvertFrom-Json @jsonOptions) } else { @() }
$manifest = @()
foreach ($item in $plugins) {
    $zip = Join-Path $output "$($item.asset)-$version-$Runtime.zip"
    if (Test-Path -LiteralPath $zip) { throw "Refusing to replace immutable package $zip" }
    $work = Join-Path $output ([Guid]::NewGuid().ToString('N'))
    $publish = Join-Path $work 'publish'; $stage = Join-Path $work 'stage'
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    & dotnet publish (Join-Path $root $item.project) -c Release -r $Runtime --self-contained false -o $publish "-p:Version=$version" "-p:PluginVersion=$version" "-p:AssemblyVersion=$version" "-p:FileVersion=$version"
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($item.name)" }
    foreach ($file in $item.files) { Copy-Item -LiteralPath (Join-Path $publish $file) -Destination $stage }
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $stage
    if ($item.asset -eq 'PrivateSyncPlay') { Copy-Item -LiteralPath (Join-Path $root 'plugins/private-syncplay/node_modules/hls.js/LICENSE') -Destination (Join-Path $stage 'hls.js.LICENSE') }
    if ($item.asset -eq 'AccountHub') { Copy-Item -LiteralPath (Join-Path $root 'plugins/account-hub/THIRD-PARTY-NOTICES.md') -Destination $stage }
    if ($item.asset -eq 'DiscordBridge') { Copy-Item -LiteralPath (Join-Path $root 'plugins/discord-bridge/THIRD-PARTY-NOTICES.md') -Destination $stage }
    if ($item.asset -eq 'DiscordBridge') {
        Copy-Item -LiteralPath (Join-Path $root 'plugins/discord-bridge/activity/dist/embedded-app-sdk.LICENSE') -Destination (Join-Path $stage 'Activity-THIRD-PARTY-NOTICES.txt')
        Copy-Item -LiteralPath (Join-Path $root 'plugins/discord-bridge/activity/dist/activity-inventory.json') -Destination $stage
    }
    $assembly = Join-Path $stage $item.files[0]
    if ([Reflection.AssemblyName]::GetAssemblyName($assembly).Version.ToString() -ne $version) { throw 'Assembly/catalog version mismatch' }
    $meta = [ordered]@{ guid=$item.id; name=$item.name; version=$version; targetAbi=$targetAbi; description=$item.description; overview=$item.description; owner=$Repository.Split('/')[0]; category='General' }
    $meta | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'meta.json') -Encoding utf8NoBOM
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $names = @($archive.Entries | ForEach-Object FullName)
        foreach ($file in @($item.files) + @('LICENSE','meta.json')) {
            if ($file -notin $names) { throw "Missing ZIP root entry: $file" }
        }
        if (@($names | Where-Object { $_ -match '[/\\]|^(Jellyfin.Controller|Jellyfin.Model|MediaBrowser\.|Microsoft.EntityFrameworkCore).*\.dll$' }).Count) { throw 'Unsafe ZIP contents' }
    } finally { $archive.Dispose() }
    $md5 = (Get-FileHash -LiteralPath $zip -Algorithm MD5).Hash.ToLowerInvariant()
    $sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$sha  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath "$zip.sha256" -Encoding ascii
    $old = @($history | Where-Object guid -eq $item.id | ForEach-Object { $_.versions })
    if (@($old | Where-Object { [Version]$_.version -ge [Version]$version }).Count) { throw 'Catalog versions must increase; existing releases are immutable.' }
    $entry = @{ version=$version; targetAbi=$targetAbi; changelog='Preview monorepo release; see release notes and documented acceptance limits.'; sourceUrl="https://github.com/$Repository/releases/download/$downloadTag/$([IO.Path]::GetFileName($zip))"; checksum=$md5; timestamp=[DateTime]::UtcNow.ToString('o') }
    $manifest += [ordered]@{ guid=$item.id; name=$item.name; description=$item.description; overview=$item.description; owner=$Repository.Split('/')[0]; category='General'; versions=@($entry)+$old }
}
ConvertTo-Json -InputObject @($manifest) -Depth 15 | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding utf8NoBOM
& (Join-Path $PSScriptRoot 'inventory-distribution.ps1') -PackagesDirectory $output
Write-Output "Validated packages and $Runtime catalog: $output"
