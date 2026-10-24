$ErrorActionPreference = 'Stop'
$headers = @{ 'User-Agent' = 'AccountHub-Security-baseline-check' }
if ($env:GH_TOKEN) { $headers.Authorization = "Bearer $env:GH_TOKEN" }
$api = 'https://api.github.com/repos/ZL154/JellyfinSecurity'
$release = Invoke-RestMethod "$api/releases/latest" -Headers $headers
if ($release.tag_name -ne 'v2.6.4') { throw "Security release changed: $($release.tag_name). Revalidate before updating compiled support." }
$commit = Invoke-RestMethod "$api/commits/v2.6.4" -Headers $headers
if ($commit.sha -ne '2c02686fcec8b15ea56235de8d196460711befaa') { throw 'Security tag moved.' }
$asset = @($release.assets | Where-Object name -eq 'Jellyfin.Plugin.TwoFactorAuthv2.6.4.0-jf12.zip')
if ($asset.Count -ne 1 -or $asset[0].digest -ne 'sha256:6c1ae5e41cc0bc7f168025d9b941578f13115cd73cac7a72496888c46f64a998') {
    throw 'Supported jf12 asset metadata changed or unavailable.'
}
Write-Output 'Latest release/tag/jf12 asset metadata match the compiled baseline. This is NOT live-server certification.'
