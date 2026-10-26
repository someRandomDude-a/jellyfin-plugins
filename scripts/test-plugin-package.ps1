param([Parameter(Mandatory)][ValidateSet('account-hub','discord-bridge','private-syncplay','deletion-queue','notifications','automatic-deletion-requests')][string]$Plugin,
    [string]$Tag = 'v0.2.0', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
if (!$OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot "../artifacts/independent-package-$Plugin-$([guid]::NewGuid().ToString('N'))" }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Package verification requires a fresh directory' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$merged = Join-Path $output 'merged'
New-Item -ItemType Directory $merged -Force | Out-Null
foreach ($runtime in @('linux-x64','linux-arm64','win-x64')) {
    $folder = Join-Path $output $runtime
    & (Join-Path $PSScriptRoot 'package.ps1') -Tag $Tag -ReleaseTag "$Plugin/$Tag" -Plugin $Plugin -Runtime $runtime -OutputDirectory $folder
    $catalog = @(Get-Content (Join-Path $folder 'manifest.json') -Raw | ConvertFrom-Json)
    if ($catalog.Count -ne 1) { throw 'Independent packaging built unrelated plugins' }
    Get-ChildItem $folder -File | Where-Object Name -ne 'manifest.json' | Copy-Item -Destination $merged
    Copy-Item (Join-Path $folder 'manifest.json') (Join-Path $merged "manifest-$runtime.json")
}
& (Join-Path $PSScriptRoot 'validate-release-set.ps1') -Directory $merged -Tag $Tag -ReleaseTag "$Plugin/$Tag" -Plugin $Plugin
Write-Output "PASS independent $Plugin packages: $output"
