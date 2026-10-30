param([ValidateSet('all','account-hub','discord-bridge','private-syncplay','deletion-queue','notifications','automatic-deletion-requests')][string]$Plugin = 'all',
    [string]$Configuration = 'Release',
    [switch]$List)
$ErrorActionPreference = 'Stop'
if ($List) { & (Join-Path $PSScriptRoot 'run-tests.ps1') -Plugin $Plugin -Configuration $Configuration -List; return }
. (Join-Path $PSScriptRoot 'test-targets.ps1')
Invoke-TestEntryPoint -Plugin $Plugin -Configuration $Configuration
