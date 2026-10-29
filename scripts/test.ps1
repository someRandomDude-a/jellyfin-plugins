param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'test-targets.ps1')
Invoke-TestEntryPoint -Plugin all -Configuration $Configuration
