param([Parameter(Mandatory)][string]$Incoming, [Parameter(Mandatory)][string]$Previous, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$next = @(Get-Content -LiteralPath $Incoming -Raw | ConvertFrom-Json)
$old = @(Get-Content -LiteralPath $Previous -Raw | ConvertFrom-Json)
if (!$next.Count) { throw 'Expected released plugins' }
foreach ($entry in @($old) + @($next)) {
    if (![guid]::TryParse([string]$entry.guid, [ref]([guid]::Empty)) -or !$entry.versions.Count) { throw 'Invalid catalog identity/history' }
    $seen = @{}
    foreach ($version in $entry.versions) {
        if ($version.version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $seen.ContainsKey($version.version)) { throw 'Invalid/duplicate catalog version' }
        $seen[$version.version] = $true
    }
}
if (@($old.guid | Select-Object -Unique).Count -ne $old.Count) { throw 'Duplicate catalog identity' }
if (@($next.guid | Select-Object -Unique).Count -ne $next.Count) { throw 'Duplicate incoming catalog identity' }
$merged = @($old)
foreach ($entry in $next) {
    $current = @($merged | Where-Object guid -eq $entry.guid)
    $versions = @{}
    if ($current.Count) { foreach ($version in $current[0].versions) { $versions[$version.version] = $version } }
    foreach ($version in $entry.versions) {
        if ($versions.ContainsKey($version.version)) {
            $existing = $versions[$version.version]
            foreach ($field in @('checksum','sourceUrl','targetAbi')) {
                if ($existing.$field -cne $version.$field) { throw 'Published version metadata is immutable' }
            }
        } else { $versions[$version.version] = $version }
    }
    $entry.versions = @($versions.Values | Sort-Object { [version]$_.version } -Descending)
    $merged = @($merged | Where-Object guid -ne $entry.guid) + @($entry)
}
ConvertTo-Json -InputObject @($merged) -Depth 30 | Set-Content -LiteralPath $Output -Encoding utf8NoBOM
