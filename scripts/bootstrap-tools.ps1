param([string]$Directory = '', [switch]$PlanOnly,
    [ValidateRange(1,900)][int]$MetadataTimeoutSeconds = 30,
    [ValidateRange(1,900)][int]$DownloadTimeoutSeconds = 300)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tool-http.ps1')
$repo = Split-Path $PSScriptRoot -Parent
$windows = $env:OS -eq 'Windows_NT'
$platform = if ($windows) { 'win' } elseif ((uname -s) -eq 'Linux') { 'linux' } else { throw 'Tool bootstrap supports Windows and Linux only' }
$machine = if ($windows) { $env:PROCESSOR_ARCHITECTURE } else { uname -m }
$arch = switch -Regex ($machine) { '^(AMD64|x86_64)$' { 'x64'; break } '^(ARM64|aarch64)$' { 'arm64'; break } default { throw 'Unsupported tool architecture' } }
$headers = @{ 'User-Agent' = 'JellyfinSuite-ToolBootstrap' }
if ($env:GH_TOKEN) { $headers.Authorization = "Bearer $env:GH_TOKEN" }
$nodeReleases = Invoke-ToolHttp -Name 'stable Node metadata' -Uri 'https://nodejs.org/dist/index.json' -TimeoutSeconds $MetadataTimeoutSeconds -Quiet:$PlanOnly | ConvertFrom-Json
$node = @($nodeReleases | Where-Object { $_.version -match '^v\d+\.\d+\.\d+$' } | Sort-Object { [version]$_.version.Substring(1) } -Descending)[0].version
$ps = Invoke-ToolHttp -Name 'stable PowerShell metadata' -Uri 'https://api.github.com/repos/PowerShell/PowerShell/releases/latest' -Headers $headers -TimeoutSeconds $MetadataTimeoutSeconds -Quiet:$PlanOnly | ConvertFrom-Json
if ($ps.draft -or $ps.prerelease -or $ps.tag_name -notmatch '^v\d+\.\d+\.\d+$') { throw 'Invalid stable PowerShell release metadata' }
$index = Invoke-ToolHttp -Name 'stable SDK metadata' -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json' -TimeoutSeconds $MetadataTimeoutSeconds -Quiet:$PlanOnly | ConvertFrom-Json
$channel = @($index.'releases-index' | Where-Object { $_.'support-phase' -in @('active','maintenance') -and $_.'latest-sdk' -match '^\d+\.\d+\.\d+$' } | Sort-Object { [version]$_.'latest-sdk' } -Descending)[0]
$sdk = $channel.'latest-sdk'
$declared = (Get-Content -LiteralPath (Join-Path $repo 'global.json') -Raw | ConvertFrom-Json).sdk.version
if ($sdk -ne $declared -and !$PlanOnly) { throw "Latest SDK is $sdk but global.json declares $declared. Run the dependency-bump workflow first; do not override the source SDK." }
$versions = [ordered]@{ node = $node.Substring(1); powershell = $ps.tag_name.Substring(1); dotnet = $sdk; platform = "$platform-$arch" }
if ($PlanOnly) { $versions | ConvertTo-Json; return }
if (!$Directory) { $Directory = Join-Path $repo ('artifacts/tool-bootstrap/' + [guid]::NewGuid().ToString('N')) }
$Directory = [IO.Path]::GetFullPath($Directory)
if (Test-Path -LiteralPath $Directory) { throw 'Preserving existing tool directory; choose a fresh destination' }
New-Item -ItemType Directory -Path $Directory -Force | Out-Null
function Install-Verified([string]$Name, [string]$Url, [string]$Expected, [string]$Algorithm, [string]$Extension) {
    if ($Expected -notmatch $(if ($Algorithm -eq 'SHA512') { '^[a-fA-F0-9]{128}$' } else { '^[a-fA-F0-9]{64}$' })) { throw "Missing official checksum for $Name" }
    $archive = Join-Path $Directory ($Name + $Extension)
    $destination = Join-Path $Directory $Name
    Invoke-ToolHttp -Name "$Name archive" -Uri $Url -Destination $archive -TimeoutSeconds $DownloadTimeoutSeconds -MaxBytes 1073741824
    if ((Get-FileHash -LiteralPath $archive -Algorithm $Algorithm).Hash.ToLowerInvariant() -ne $Expected.ToLowerInvariant()) { throw "$Name checksum mismatch; refusing extraction or execution" }
    New-Item -ItemType Directory -Path $destination | Out-Null
    if ($Extension -eq '.zip') { Expand-Archive -LiteralPath $archive -DestinationPath $destination }
    else { & tar -xf $archive -C $destination; if ($LASTEXITCODE) { throw "$Name extraction failed" } }
    return $destination
}
$nodeExt = if ($windows) { '.zip' } else { '.tar.xz' }
$nodeName = "node-$node-$platform-$arch"
$sums = Invoke-ToolHttp -Name 'official Node checksums' -Uri "https://nodejs.org/dist/$node/SHASUMS256.txt" -TimeoutSeconds $MetadataTimeoutSeconds
$nodeHash = [regex]::Match($sums, '(?m)^([a-f0-9]{64})\s+' + [regex]::Escape($nodeName + $nodeExt) + '\r?$').Groups[1].Value
$nodeRoot = Install-Verified node "https://nodejs.org/dist/$node/$nodeName$nodeExt" $nodeHash SHA256 $nodeExt
$nodeBin = Join-Path $nodeRoot $nodeName
if (!$windows) { $nodeBin = Join-Path $nodeBin 'bin' }
$psExt = if ($windows) { '.zip' } else { '.tar.gz' }
$psName = if ($windows) { "PowerShell-$($versions.powershell)-win-$arch.zip" } else { "powershell-$($versions.powershell)-linux-$arch.tar.gz" }
$asset = @($ps.assets | Where-Object name -eq $psName)
if ($asset.Count -ne 1 -or $asset[0].digest -notmatch '^sha256:([a-f0-9]{64})$') { throw 'Official PowerShell asset/checksum unavailable' }
$psBin = Install-Verified powershell $asset[0].browser_download_url $asset[0].digest.Substring(7) SHA256 $psExt
$releases = Invoke-ToolHttp -Name 'SDK release metadata' -Uri $channel.'releases.json' -TimeoutSeconds $MetadataTimeoutSeconds | ConvertFrom-Json
$sdkRelease = @($releases.releases | ForEach-Object { $_.sdks } | Where-Object version -eq $sdk)[0]
$sdkFile = @($sdkRelease.files | Where-Object { $_.rid -eq "$platform-$arch" -and $_.name.EndsWith($(if ($windows) { '.zip' } else { '.tar.gz' })) })[0]
if (!$sdkFile) { throw 'Latest SDK archive is unavailable for this platform' }
$dotnetBin = Install-Verified dotnet $sdkFile.url $sdkFile.hash SHA512 $(if ($windows) { '.zip' } else { '.tar.gz' })
if (!$windows) { & chmod +x (Join-Path $psBin 'pwsh'); if ($LASTEXITCODE) { throw 'Cannot enable verified PowerShell executable' } }
$env:PATH = (@($nodeBin,$psBin,$dotnetBin) -join [IO.Path]::PathSeparator) + [IO.Path]::PathSeparator + $env:PATH
$env:PWSH = Join-Path $psBin $(if ($windows) { 'pwsh.exe' } else { 'pwsh' })
$env:DOTNET_ROOT = $dotnetBin
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
foreach ($tool in @(@('node','--version',"v$($versions.node)"),@($env:PWSH,'-Version',"PowerShell $($versions.powershell)"),@('dotnet','--version',$sdk))) {
    $actual = & $tool[0] $tool[1]
    if ($LASTEXITCODE -or $actual.Trim() -ne $tool[2]) { throw 'Installed tool does not match the discovered stable release' }
}
$env:SUITE_TOOLCHAIN_READY = 'true'
if ($env:GITHUB_PATH) { foreach ($bin in @($nodeBin,$psBin,$dotnetBin)) { $bin | Out-File -LiteralPath $env:GITHUB_PATH -Append -Encoding utf8 } }
if ($env:GITHUB_ENV) { @("PWSH=$env:PWSH","DOTNET_ROOT=$dotnetBin",'DOTNET_MULTILEVEL_LOOKUP=0','SUITE_TOOLCHAIN_READY=true') | Out-File -LiteralPath $env:GITHUB_ENV -Append -Encoding utf8 }
$versions | ConvertTo-Json | Out-File -LiteralPath (Join-Path $Directory 'tools.json') -Encoding utf8
Write-Output "PASS official checksum-verified stable tools: Node $($versions.node), PowerShell $($versions.powershell), SDK $sdk"
