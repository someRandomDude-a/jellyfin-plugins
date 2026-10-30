# Shared automatic bootstrap. Nested package/release fixtures inherit one snapshot.
function Initialize-TestTargets {
    $repo = Split-Path $PSScriptRoot -Parent
    if (!$env:SUITE_TEST_TARGETS -or $env:SUITE_TOOLCHAIN_READY -ne 'true' -or !$env:PWSH -or !(Test-Path -LiteralPath $env:PWSH)) { & (Join-Path $PSScriptRoot 'bootstrap-tools.ps1') }
    if (!$env:PWSH -and $PSVersionTable.PSEdition -eq 'Core') { $env:PWSH = Join-Path $PSHOME $(if ([Environment]::OSVersion.Platform -eq 'Win32NT') { 'pwsh.exe' } else { 'pwsh' }) }
    if (!$env:SUITE_TEST_TARGETS) {
        $directory = Join-Path $repo ('artifacts/test-runs/' + [Guid]::NewGuid().ToString('N'))
        $file = Join-Path $directory 'targets.json'
        & node (Join-Path $repo 'scripts/prepare-test-targets.mjs') "--output=$file" --services=jellyfin,security
        if ($LASTEXITCODE) { throw 'Latest release bootstrap failed; refusing an older target' }
        $env:SUITE_TEST_TARGETS = $file
    } else {
        & node (Join-Path $repo 'scripts/prepare-test-targets.mjs') "--output=$env:SUITE_TEST_TARGETS" --services=jellyfin
        if ($LASTEXITCODE) { throw 'Current-run target validation failed' }
    }
    $snapshot = Get-Content -LiteralPath $env:SUITE_TEST_TARGETS -Raw | ConvertFrom-Json
    $env:SUITE_TEST_RUN_ID = $snapshot.runId
    $env:SUITE_JELLYFIN_PROPS = Join-Path (Split-Path $env:SUITE_TEST_TARGETS -Parent) 'Jellyfin.Latest.props'
    Push-Location $repo
    try {
        $audit = $null
        $arguments = @()
        if (!$env:SUITE_DEPENDENCY_AUDIT) {
            $audit = Join-Path (Split-Path $env:SUITE_TEST_TARGETS -Parent) ('dependency-audit-' + [Guid]::NewGuid().ToString('N') + '.json')
            $arguments = @("--output=$audit")
        }
        & node (Join-Path $repo 'scripts/check-dependency-freshness.mjs') @arguments
        if ($LASTEXITCODE) { throw 'Update dependencies with scripts/update-dependencies.mjs --apply before tests/packages; no stale fallback' }
        if ($audit) { $env:SUITE_DEPENDENCY_AUDIT = $audit }
    } finally { Pop-Location }
}

function Invoke-TestEntryPoint([string]$Plugin, [string]$Configuration) {
    $saved = @{}
    # Keep verified process-local tool paths aligned; restoring an old PWSH
    # while leaving the newly bootstrapped PATH would break subsequent fixtures.
    $names = @('SUITE_TEST_TARGETS','SUITE_TEST_RUN_ID','SUITE_JELLYFIN_PROPS','SUITE_DEPENDENCY_AUDIT')
    foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
    try { & (Join-Path $PSScriptRoot 'run-tests.ps1') -Plugin $Plugin -Configuration $Configuration }
    finally { foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') } }
}
