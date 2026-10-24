<#
.SYNOPSIS
    Builds and packages the Jellyfin User Manager plugin.

.DESCRIPTION
    Produces:
      dist/plugin/                              extracted plugin (meta.json + assemblies)
      dist/Jellyfin.Plugin.UserManager_<ver>.zip
      dist/manifest.json                        manifest pointing at the GitHub release asset
      manifest.json                             repository manifest (version entry upserted)

.PARAMETER Version
    Plugin version. Defaults to the Version property in Directory.Build.props.
    Missing components are padded, so 1.0.0 becomes 1.0.0.0.

.PARAMETER ReleaseTag
    Git tag that will host the package. Defaults to v<Version>. Pass the actual
    tag from CI so manifest source URLs stay valid for tags such as v1.0.0.

.PARAMETER RepositoryUrl
    GitHub repository used in manifest source URLs. Defaults to the origin remote.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.EXAMPLE
    ./scripts/package.ps1 -Version 1.0.0.0

.EXAMPLE
    ./scripts/package.ps1 -Version 1.0.0 -ReleaseTag v1.0.0
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$ReleaseTag,
    [string]$RepositoryUrl,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/Jellyfin.Plugin.UserManager/Jellyfin.Plugin.UserManager.csproj'
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$dist = Join-Path $repoRoot 'dist'
$pluginDir = Join-Path $dist 'plugin'
$pluginName = 'Jellyfin.Plugin.UserManager'
$pluginGuid = 'ba6561b1-5df1-445a-9f8d-e6127f143c97'
$targetAbi = '12.2.0.0'
$owner = 'someRandomDude-a'
$name = 'User Manager'
$description = 'Identity and permission manager for Jellyfin and Seerr with invite links, Discord password resets, audit logs and admin notifications.'
$overview = 'Manage Jellyfin and Seerr users from one place: permission presets, self-service invite links, Discord account linking and password resets, a searchable audit log and admin notifications.'
$category = 'Administration'

if (-not $Version) {
    [xml]$props = Get-Content $propsPath
    $Version = $props.Project.PropertyGroup.Version
}

$parts = @($Version.TrimStart('v').Split('.'))
while ($parts.Count -lt 4) { $parts += '0' }
$Version = ($parts[0..3] -join '.')

if (-not $ReleaseTag) {
    $ReleaseTag = "v$Version"
}
else {
    $ReleaseTag = $ReleaseTag.Trim()
    if ($ReleaseTag -notmatch '^v?[0-9]+(\.[0-9]+){0,3}$') {
        throw "ReleaseTag must look like v1.2.3 or v1.2.3.4 (got '$ReleaseTag')."
    }

    if (-not $ReleaseTag.StartsWith('v')) {
        $ReleaseTag = "v$ReleaseTag"
    }
}

if (-not $RepositoryUrl) {
    try {
        $remote = (git -C $repoRoot remote get-url origin).Trim()
        $RepositoryUrl = $remote -replace '^git@github\.com:', 'https://github.com/' -replace '\.git$', ''
    }
    catch {
        $RepositoryUrl = 'https://github.com/someRandomDude-a/jellyfin-user-manager'
    }
}
$RepositoryUrl = $RepositoryUrl.TrimEnd('/')

Write-Host "Building $pluginName $Version ($Configuration)" -ForegroundColor Cyan
& dotnet build $project -c $Configuration -p:Version=$Version -p:AssemblyVersion=$Version -p:FileVersion=$Version -p:InformationalVersion=$Version --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

$output = Join-Path $repoRoot "src/Jellyfin.Plugin.UserManager/bin/$Configuration/net10.0"
if (-not (Test-Path $output)) {
    throw "Build output was not found at $output"
}

if (Test-Path $pluginDir) {
    Remove-Item $pluginDir -Recurse -Force
}
New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null

# Ship only assemblies Jellyfin does not already provide. Server and shared-framework
# assemblies are resolved from the Jellyfin host at runtime.
$excluded = @(
    'Jellyfin.*.dll',
    'MediaBrowser.*.dll',
    'Microsoft.*.dll',
    'System.*.dll',
    'netstandard.dll',
    'WindowsBase.dll'
)

Get-ChildItem $output -File -Filter '*.dll' | ForEach-Object {
    $skip = $false
    foreach ($pattern in $excluded) {
        if ($_.Name -like $pattern) { $skip = $true; break }
    }

    if (-not $skip) {
        Copy-Item $_.FullName (Join-Path $pluginDir $_.Name)
    }
}

$pluginAssembly = Join-Path $output "$pluginName.dll"
if (-not (Test-Path $pluginAssembly)) {
    throw "Plugin assembly $pluginAssembly was not found."
}
Copy-Item $pluginAssembly (Join-Path $pluginDir "$pluginName.dll")

$packagedAssemblies = @(Get-ChildItem $pluginDir -File -Filter '*.dll')
if ($packagedAssemblies.Count -eq 0) {
    throw "No assemblies were copied into $pluginDir."
}

$timestamp = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffzzz')
$meta = [ordered]@{
    guid        = $pluginGuid
    name        = $name
    description = $description
    overview    = $overview
    owner       = $owner
    category    = $category
    imageUrl    = ''
    version     = $Version
    targetAbi   = $targetAbi
    changelog   = "Release $Version."
    status      = 'Active'
    autoUpdate  = $true
    assemblies  = @()
    timestamp   = $timestamp
}

$metaJson = $meta | ConvertTo-Json -Depth 5
$metaPath = Join-Path $pluginDir 'meta.json'
[System.IO.File]::WriteAllText($metaPath, $metaJson, [System.Text.UTF8Encoding]::new($false))

$zipName = "${pluginName}_$Version.zip"
$zipPath = Join-Path $dist $zipName
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $pluginDir,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

$md5 = [System.Security.Cryptography.MD5]::Create()
try {
    $hashBytes = $md5.ComputeHash([System.IO.File]::ReadAllBytes($zipPath))
}
finally {
    $md5.Dispose()
}
$checksum = ([System.BitConverter]::ToString($hashBytes)).Replace('-', '').ToLowerInvariant()

$checksumPath = "$zipPath.md5"
[System.IO.File]::WriteAllText(
    $checksumPath,
    "$checksum  $zipName`n",
    [System.Text.UTF8Encoding]::new($false))

$sourceUrl = "$RepositoryUrl/releases/download/$ReleaseTag/$zipName"

$manifestEntry = [ordered]@{
    guid        = $pluginGuid
    name        = $name
    description = $description
    overview    = $overview
    owner       = $owner
    category    = $category
    imageUrl    = ''
    versions    = @(
        [ordered]@{
            version    = $Version
            changelog  = "Release $Version."
            targetAbi  = $targetAbi
            sourceUrl  = $sourceUrl
            checksum   = $checksum
            timestamp  = $timestamp
        }
    )
}

$manifest = @($manifestEntry)
$manifestJson = ConvertTo-Json -InputObject $manifest -Depth 8
$distManifestPath = Join-Path $dist 'manifest.json'
[System.IO.File]::WriteAllText($distManifestPath, $manifestJson, [System.Text.UTF8Encoding]::new($false))

# Upsert the same version into the repository manifest so it can be used directly.
$rootManifestPath = Join-Path $repoRoot 'manifest.json'
$versions = @()
if (Test-Path $rootManifestPath) {
    try {
        $existing = Get-Content $rootManifestPath -Raw | ConvertFrom-Json
        if ($existing -and $existing.Count -gt 0 -and $existing[0].versions) {
            $versions = @($existing[0].versions | Where-Object { $_.version -ne $Version })
        }
    }
    catch {
        Write-Warning "Could not parse the existing manifest.json; it will be replaced."
    }
}

$mergedVersions = @($manifestEntry.versions[0]) + $versions
$merged = @(
    [ordered]@{
        guid        = $pluginGuid
        name        = $name
        description = $description
        overview    = $overview
        owner       = $owner
        category    = $category
        imageUrl    = ''
        versions    = $mergedVersions
    }
)
[System.IO.File]::WriteAllText(
    $rootManifestPath,
    (ConvertTo-Json -InputObject $merged -Depth 8),
    [System.Text.UTF8Encoding]::new($false))

Write-Host ''
Write-Host 'Package created:' -ForegroundColor Green
Write-Host "  Zip:        $zipPath"
Write-Host "  Checksum:   $checksumPath"
Write-Host "  Plugin dir: $pluginDir"
Write-Host "  Manifest:   $distManifestPath"
Write-Host "  Version:    $Version"
Write-Host "  Release:    $ReleaseTag"
Write-Host "  Checksum:   $checksum (MD5)"
Write-Host "  Source URL: $sourceUrl"
