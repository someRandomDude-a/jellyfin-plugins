param([Parameter(Mandatory)][string]$Incoming, [Parameter(Mandatory)][string]$Previous, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$next = @(Get-Content -LiteralPath $Incoming -Raw | ConvertFrom-Json)
$old = @(Get-Content -LiteralPath $Previous -Raw | ConvertFrom-Json)
if ($next.Count -ne 1 -or !$next[0].versions.Count) { throw 'Expected one released plugin' }
foreach ($entry in @($old) + @($next)) {
    if (![guid]::TryParse([string]$entry.guid, [ref]([guid]::Empty)) -or !$entry.versions.Count) { throw 'Invalid catalog identity/history' }
    $seen = @{}
    foreach ($version in $entry.versions) {
        if ($version.version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $seen.ContainsKey($version.version)) { throw 'Invalid/duplicate catalog version' }
        $seen[$version.version] = $true
    }
}
if (@($old.guid | Select-Object -Unique).Count -ne $old.Count) { throw 'Duplicate catalog identity' }
$current = @($old | Where-Object guid -eq $next[0].guid)
$versions = @{}
if ($current.Count) { foreach ($version in $current[0].versions) { $versions[$version.version] = $version } }
foreach ($version in $next[0].versions) {
    if ($versions.ContainsKey($version.version)) {
        $existing = $versions[$version.version]
        foreach ($field in @('checksum','sourceUrl','targetAbi')) {
            if ($existing.$field -cne $version.$field) { throw 'Published version metadata is immutable' }
        }
    } else { $versions[$version.version] = $version }
}
$next[0].versions = @($versions.Values | Sort-Object { [version]$_.version } -Descending)
$merged = @($old | Where-Object guid -ne $next[0].guid) + @($next[0])
ConvertTo-Json -InputObject @($merged) -Depth 30 | Set-Content -LiteralPath $Output -Encoding utf8NoBOM
