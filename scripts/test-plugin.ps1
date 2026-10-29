param([Parameter(Mandatory)][ValidateSet('account-hub','discord-bridge','private-syncplay','deletion-queue','notifications','automatic-deletion-requests')][string]$Plugin,
    [string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'test-targets.ps1')
Invoke-TestEntryPoint -Plugin $Plugin -Configuration $Configuration
