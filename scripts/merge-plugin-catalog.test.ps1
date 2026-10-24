$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('opencode/catalog-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $root | Out-Null
try {
    $old = Join-Path $root 'old.json'; $next = Join-Path $root 'next.json'; $output = Join-Path $root 'merged.json'
    function Entry($id, $version) { @{ guid=$id; name=$id; versions=@(@{ version=$version; checksum='abc'; targetAbi='12.3.0.0'; sourceUrl="https://example.invalid/$version.zip" }) } }
    $a = '09514a9e-f7a0-4c5a-91bc-3a46338298ca'; $b = '4b047046-a550-4415-b204-4206f1f18201'
    $unchanged = Entry $b '2.0.0.0'
    ConvertTo-Json -InputObject @((Entry $a '1.0.0.0'), $unchanged) -Depth 10 | Set-Content $old
    ConvertTo-Json -InputObject @((Entry $a '1.1.0.0')) -Depth 10 | Set-Content $next
    & "$PSScriptRoot/merge-plugin-catalog.ps1" -Incoming $next -Previous $old -Output $output
    $result = @(Get-Content $output -Raw | ConvertFrom-Json)
    if ($result.Count -ne 2 -or @($result | Where-Object guid -eq $a)[0].versions.Count -ne 2) { throw 'Plugin/history lost' }
    if ((@($result | Where-Object guid -eq $b)[0] | ConvertTo-Json -Depth 10 -Compress) -ne ($unchanged | ConvertTo-Json -Depth 10 -Compress)) { throw 'Other plugin changed' }
    Copy-Item $output $old -Force
    & "$PSScriptRoot/merge-plugin-catalog.ps1" -Incoming $next -Previous $old -Output $output
    $tampered = @(Get-Content $next -Raw | ConvertFrom-Json); $tampered[0].versions[0].checksum = 'changed'
    ConvertTo-Json -InputObject $tampered -Depth 10 | Set-Content $next
    $rejected = $false
    try { & "$PSScriptRoot/merge-plugin-catalog.ps1" -Incoming $next -Previous $old -Output $output } catch { $rejected = $true }
    if (!$rejected) { throw 'Immutable version replacement accepted' }
    $calls = [Collections.Generic.List[object]]::new()
    function gh {
        $arguments = @($args); $raw = $input | Out-String
        $body = if ($raw.Trim()) { $raw | ConvertFrom-Json } else { $null }
        $calls.Add(@{ arguments=$arguments; body=$body }); $global:LASTEXITCODE = 0
        if (($arguments -join ' ') -match 'matching-refs') { return '[]' }
        return '{"sha":"mock-sha"}'
    }
    foreach ($runtime in @('linux-x64','linux-arm64','win-x64')) {
        ConvertTo-Json -InputObject @((Entry $a '1.1.0.0')) -Depth 10 | Set-Content (Join-Path $root "manifest-$runtime.json")
    }
    & "$PSScriptRoot/publish-combined-catalog.ps1" -Repository test/repository -Directory $root
    $tree = @($calls | Where-Object { $_.arguments -contains 'repos/test/repository/git/trees' })
    $refs = @($calls | Where-Object { $_.arguments -contains 'repos/test/repository/git/refs' })
    if ($tree.Count -ne 1 -or $tree[0].body.tree.Count -ne 3 -or $refs.Count -ne 1 -or $refs[0].body.ref -ne 'refs/heads/catalog') { throw 'Platform feeds must publish atomically to the combined branch' }
    Write-Output 'PASS combined catalog: independent entry merge, preserved history/other plugins, idempotence and immutable version rejection'
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
