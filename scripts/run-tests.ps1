param([ValidateSet('all','account-hub','discord-bridge','private-syncplay','deletion-queue','notifications','automatic-deletion-requests')][string]$Plugin = 'all',
    [string]$Configuration = 'Release',
    [switch]$List)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
# Listing is offline and does not bootstrap tools or start test fixtures.
if (!$List) {
    . (Join-Path $PSScriptRoot 'test-targets.ps1')
    Initialize-TestTargets
    foreach ($name in @('GITHUB_REF','GITHUB_REF_TYPE','GITHUB_REF_NAME')) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
}
$planJson = & node (Join-Path $PSScriptRoot 'test-plan.mjs') "--plugin=$Plugin" "--configuration=$Configuration"
if ($LASTEXITCODE) { throw 'Cannot resolve the test plan' }
if ($List) { $planJson; return }
$failures = [System.Collections.Generic.List[string]]::new()
foreach ($step in ($planJson | ConvertFrom-Json)) {
    $label = "$($step.directory): $($step.program) $($step.arguments -join ' ')"
    Push-Location (Join-Path $root $step.directory)
    try {
        $program = $step.program
        $arguments = [string[]]$step.arguments
        & $program @arguments
        if ($LASTEXITCODE) { throw "$program failed in $($step.directory)" }
    } catch {
        $failures.Add($label)
        Write-Warning "FAIL $label (see command output above); continuing independent test entry points."
    } finally { Pop-Location }
}
if ($failures.Count) {
    Write-Output "FAILED $($failures.Count) test entry points:"
    foreach ($failure in $failures) { Write-Output "- $failure" }
    throw 'Test suite failed; all independent test entry points were attempted.'
}
Write-Output "PASS $Plugin and its common/dependency tests"
