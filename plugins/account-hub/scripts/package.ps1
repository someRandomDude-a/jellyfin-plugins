param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    dotnet build AccountHub.slnx -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    dotnet test AccountHub.slnx -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    $output = Join-Path $root "src/AccountHub/bin/$Configuration/net10.0"
    $stage = Join-Path $root ('artifacts/stage-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    $allowlist = @('Jellyfin.Plugin.AccountHub.dll', 'AccountHub.Core.dll', 'AccountHub.Api.dll', 'Jellyfin.Plugin.AccountHub.deps.json')
    foreach ($file in $allowlist) {
        Copy-Item -LiteralPath (Join-Path $output $file) -Destination $stage
    }
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $stage
    $zip = Join-Path $root ('artifacts/AccountHub-0.1.0.0-preview-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff') + '.zip')
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ($zip + '.sha256') -Value ($hash + '  ' + [IO.Path]::GetFileName($zip)) -Encoding ascii
    Write-Output "Preview only; not certified or published: $zip"
    Write-Output "SHA256: $hash"
} finally { Pop-Location }
