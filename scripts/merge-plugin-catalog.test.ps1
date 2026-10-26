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
    $suite = @((Entry $a '1.2.0.0'), (Entry $b '2.1.0.0'))
    foreach ($id in @('e0cb0eb1-93ef-4335-bae4-c729771cbbe7','85679095-3232-4b24-be55-08055a0562cd','c18d78ec-4b70-47cf-a0d2-936d987c37ba','a6429fb2-c67c-459d-b6a7-579c4a05dace')) { $suite += Entry $id '1.0.0.0' }
    ConvertTo-Json -InputObject $suite -Depth 10 | Set-Content $next
    & "$PSScriptRoot/merge-plugin-catalog.ps1" -Incoming $next -Previous $old -Output $output
    $result = @(Get-Content $output -Raw | ConvertFrom-Json)
    if ($result.Count -ne 6 -or @($result | Where-Object guid -eq $a)[0].versions.Count -ne 3 -or @($result | Where-Object guid -eq $b)[0].versions.Count -ne 2) { throw 'Suite merge lost plugin history' }
    Copy-Item $output $old -Force
    & "$PSScriptRoot/merge-plugin-catalog.ps1" -Incoming $next -Previous $old -Output $output
    if ((Get-Content $old -Raw) -cne (Get-Content $output -Raw)) { throw 'Suite merge not idempotent' }
    $suite[1].versions[0].checksum = 'changed'
    ConvertTo-Json -InputObject $suite -Depth 10 | Set-Content $next
    $rejected = $false
    try { & "$PSScriptRoot/merge-plugin-catalog.ps1" -Incoming $next -Previous $old -Output $output } catch { $rejected = $true }
    if (!$rejected) { throw 'Suite immutable version replacement accepted' }
    $suite[1].versions[0].checksum = 'abc'
    foreach ($runtime in @('linux-x64','linux-arm64','win-x64')) {
        ConvertTo-Json -InputObject $suite -Depth 10 | Set-Content (Join-Path $root "manifest-$runtime.json")
    }
    $calls.Clear()
    & "$PSScriptRoot/publish-combined-catalog.ps1" -Repository test/repository -Directory $root -AllPlugins
    $tree = @($calls | Where-Object { $_.arguments -contains 'repos/test/repository/git/trees' })
    $refs = @($calls | Where-Object { $_.arguments -contains 'repos/test/repository/git/refs' })
    $blobs = @($calls | Where-Object { $_.arguments -contains 'repos/test/repository/git/blobs' })
    if ($tree.Count -ne 1 -or $tree[0].body.tree.Count -ne 3 -or $refs.Count -ne 1 -or $blobs.Count -ne 3) { throw 'Suite catalogs must publish atomically' }
    foreach ($blob in $blobs) {
        $entries = @([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($blob.body.content)) | ConvertFrom-Json)
        if ($entries.Count -ne 6) { throw 'Published suite catalog lost entries' }
    }
    $calls.Clear(); $rejected = $false
    try { & "$PSScriptRoot/publish-combined-catalog.ps1" -Repository test/repository -Directory $root } catch { $rejected = $true }
    if (!$rejected -or $calls.Count) { throw 'Selected mode accepted a suite or wrote before validation' }
    # An old five-plugin artifact must fail before any GitHub API call.
    foreach ($runtime in @('linux-x64','linux-arm64','win-x64')) {
        ConvertTo-Json -InputObject @($suite[0..4]) -Depth 10 | Set-Content (Join-Path $root "manifest-$runtime.json")
    }
    $rejected = $false
    try { & "$PSScriptRoot/publish-combined-catalog.ps1" -Repository test/repository -Directory $root -AllPlugins } catch { $rejected = $true }
    if (!$rejected -or $calls.Count) { throw 'Incomplete five-plugin suite accepted or contacted GitHub' }
    foreach ($runtime in @('linux-x64','linux-arm64','win-x64')) {
        ConvertTo-Json -InputObject $suite -Depth 10 | Set-Content (Join-Path $root "manifest-$runtime.json")
    }
    $mismatched = @($suite[0..4]) + @((Entry '1aa06e5f-4760-4397-a547-ae037cd4b722' '1.0.0.0'))
    ConvertTo-Json -InputObject $mismatched -Depth 10 | Set-Content (Join-Path $root 'manifest-win-x64.json')
    $rejected = $false
    try { & "$PSScriptRoot/publish-combined-catalog.ps1" -Repository test/repository -Directory $root -AllPlugins } catch { $rejected = $true }
    if (!$rejected -or $calls.Count) { throw 'Mismatched suite identities accepted' }
    $suite[4] = $suite[0]
    ConvertTo-Json -InputObject $suite -Depth 10 | Set-Content $next
    $rejected = $false
    try { & "$PSScriptRoot/merge-plugin-catalog.ps1" -Incoming $next -Previous $old -Output $output } catch { $rejected = $true }
    if (!$rejected) { throw 'Duplicate incoming identities accepted' }
    Write-Output 'PASS combined catalog: single/suite publication, preserved history, idempotence, immutable versions and cross-platform identity validation (mocked GitHub only)'
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
