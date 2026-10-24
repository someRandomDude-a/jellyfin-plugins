param([Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
    [Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
function Api([string]$Path, $Body = $null, [string]$Method = 'POST') {
    if ($null -eq $Body) { $text = gh api "repos/$Repository/$Path" }
    else { $text = ($Body | ConvertTo-Json -Depth 30 -Compress) | gh api --method $Method "repos/$Repository/$Path" --input - }
    if ($LASTEXITCODE) { throw "Catalog API failed: $Path" }
    return $text | ConvertFrom-Json
}
$work = Join-Path ([IO.Path]::GetTempPath()) ('opencode/catalog-merge-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $identity = $null
    foreach ($runtime in @('linux-x64','linux-arm64','win-x64')) {
        $incoming = @(Get-Content (Join-Path $Directory "manifest-$runtime.json") -Raw | ConvertFrom-Json)
        if ($incoming.Count -ne 1) { throw 'Expected selected-plugin platform catalog' }
        if ($identity -and $identity -ne $incoming[0].guid) { throw 'Platform catalog identities disagree' }
        $identity = $incoming[0].guid
    }
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        $refs = @(Api 'git/matching-refs/heads/catalog' | Where-Object ref -eq 'refs/heads/catalog')
        $parent = if ($refs.Count) { $refs[0].object.sha } else { '' }
        $tree = $null; $files = @()
        if ($parent) {
            $tree = (Api "git/commits/$parent").tree.sha
            $listing = Api "git/trees/${tree}?recursive=1"
            if ($listing.truncated) { throw 'Catalog tree listing truncated' }
            $files = @($listing.tree)
        }
        $updates = @()
        foreach ($runtime in @('linux-x64','linux-arm64','win-x64')) {
            $path = "manifest-$runtime.json"
            $previous = Join-Path $work "previous-$runtime.json"
            $file = @($files | Where-Object path -eq $path)
            if ($file.Count) {
                $blob = Api "git/blobs/$($file[0].sha)"
                if ($blob.encoding -ne 'base64') { throw 'Unsupported catalog blob encoding' }
                [IO.File]::WriteAllBytes($previous, [Convert]::FromBase64String($blob.content))
            } else {
                '[]' | Set-Content $previous
                # Bootstrap a new combined feed from already published independent
                # feeds, rather than hiding other plugins until their next release.
                foreach ($plugin in @('account-hub','discord-bridge','private-syncplay','deletion-queue')) {
                    $branch = "catalog-$plugin"
                    $legacyRefs = @(Api "git/matching-refs/heads/$branch" | Where-Object ref -eq "refs/heads/$branch")
                    if (!$legacyRefs.Count) { continue }
                    $legacyTree = Api "git/trees/$($legacyRefs[0].object.sha)?recursive=1"
                    if ($legacyTree.truncated) { throw 'Independent catalog tree listing truncated' }
                    $legacyFile = @($legacyTree.tree | Where-Object path -eq $path)
                    if (!$legacyFile.Count) { continue }
                    $legacyBlob = Api "git/blobs/$($legacyFile[0].sha)"
                    if ($legacyBlob.encoding -ne 'base64') { throw 'Unsupported independent blob encoding' }
                    $seed = Join-Path $work 'seed.json'; $seedMerged = Join-Path $work 'seed-merged.json'
                    [IO.File]::WriteAllBytes($seed, [Convert]::FromBase64String($legacyBlob.content))
                    & (Join-Path $PSScriptRoot 'merge-plugin-catalog.ps1') -Incoming $seed -Previous $previous -Output $seedMerged
                    Copy-Item $seedMerged $previous -Force
                }
            }
            $merged = Join-Path $work $path
            & (Join-Path $PSScriptRoot 'merge-plugin-catalog.ps1') -Incoming (Join-Path $Directory $path) -Previous $previous -Output $merged
            $blob = Api 'git/blobs' @{ content=[Convert]::ToBase64String([IO.File]::ReadAllBytes($merged)); encoding='base64' }
            $updates += @{ path=$path; mode='100644'; type='blob'; sha=$blob.sha }
        }
        $body = @{ tree=$updates }; if ($tree) { $body.base_tree = $tree }
        $newTree = Api 'git/trees' $body
        $parents = @(); if ($parent) { $parents = @($parent) }
        $commit = Api 'git/commits' @{ message="Update combined plugin catalogs: $identity"; tree=$newTree.sha; parents=$parents }
        try {
            if ($parent) { $null = Api 'git/refs/heads/catalog' @{ sha=$commit.sha; force=$false } 'PATCH' }
            else { $null = Api 'git/refs' @{ ref='refs/heads/catalog'; sha=$commit.sha } }
            Write-Output "Published combined architecture catalogs: https://raw.githubusercontent.com/$Repository/catalog/manifest-linux-x64.json"
            return
        } catch {
            # Re-read and merge the new parent after a race; never force a ref update.
            if ($attempt -eq 3) { throw }
        }
    }
} finally { Remove-Item -LiteralPath $work -Recurse -Force }
