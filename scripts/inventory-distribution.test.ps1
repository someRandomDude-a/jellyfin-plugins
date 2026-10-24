$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('opencode/distribution-inventory-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$fixture/packages","$fixture/stage","$fixture/cache/example.package/1.2.3" -Force | Out-Null
try {
    [IO.File]::WriteAllBytes("$fixture/stage/Example.dll", [byte[]]@(1,2,3,4,5))
    '{"libraries":{"Example.Package/1.2.3":{"type":"package"},"Server.SDK/12.2.0":{"type":"package"}},"targets":{"net10.0":{"Example.Package/1.2.3":{"runtime":{"lib/net10.0/Example.dll":{}}},"Server.SDK/12.2.0":{"runtime":{"lib/net10.0/Server.dll":{}}}}}}' | Set-Content "$fixture/stage/Example.deps.json"
    '<package><metadata><id>Example.Package</id><version>1.2.3</version><license type="expression">MIT</license></metadata></package>' | Set-Content "$fixture/cache/example.package/1.2.3/example.package.nuspec"
    @(@{name='Example'; versions=@(@{version='1.0.0.0';sourceUrl='https://example.invalid/Example.zip'})}) | ConvertTo-Json -Depth 10 -AsArray | Set-Content "$fixture/packages/manifest.json"
    Compress-Archive -Path "$fixture/stage/*" -DestinationPath "$fixture/packages/Example.zip"
    & "$PSScriptRoot/inventory-distribution.ps1" -PackagesDirectory "$fixture/packages" -NugetCache "$fixture/cache"
    $bom = Get-Content "$fixture/packages/Example.zip.cdx.json" -Raw | ConvertFrom-Json
    if ($bom.bomFormat -ne 'CycloneDX' -or $bom.specVersion -ne '1.6' -or $bom.components.Count -ne 2) { throw 'Incorrect binary/package inventory' }
    if (@($bom.components | Where-Object name -eq 'Server.SDK').Count) { throw 'Unshipped SDK must not be advertised as bundled' }
    $binary = $bom.components | Where-Object type -eq 'file'
    if ($binary.hashes[0].content -ne (Get-FileHash "$fixture/stage/Example.dll" -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Binary hash mismatch' }
    $licenses = Get-Content "$fixture/packages/Example.zip.licenses.json" -Raw | ConvertFrom-Json
    if ($licenses.packages.Count -ne 1 -or $licenses.packages[0].expression -ne 'MIT' -or !$licenses.packages[0].reviewRequired) { throw 'Pinned metadata and explicit manual review are required' }
    Remove-Item -LiteralPath "$fixture/cache/example.package/1.2.3/example.package.nuspec"
    $rejected = $false
    try { & "$PSScriptRoot/inventory-distribution.ps1" -PackagesDirectory "$fixture/packages" -NugetCache "$fixture/cache" } catch { $rejected = $true }
    if (!$rejected) { throw 'Missing pinned metadata must fail closed' }
    Write-Output 'PASS shipped-binary SBOM, hashes, SDK exclusion, pinned license metadata and missing-metadata rejection.'
} finally { Remove-Item -LiteralPath $fixture -Recurse -Force }
