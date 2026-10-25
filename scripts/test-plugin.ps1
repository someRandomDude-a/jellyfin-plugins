param([Parameter(Mandatory)][ValidateSet('account-hub','discord-bridge','private-syncplay','deletion-queue','notifications')][string]$Plugin,
    [string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
foreach ($name in @('GITHUB_REF','GITHUB_REF_TYPE','GITHUB_REF_NAME')) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
function Run([string]$Directory, [string]$Program, [string[]]$Arguments) {
    Push-Location (Join-Path $root $Directory)
    try { & $Program @Arguments; if ($LASTEXITCODE) { throw "$Program failed in $Directory" } }
    finally { Pop-Location }
}
function DotnetTest([string]$Project) { Run '.' dotnet @('test',$Project,'-c',$Configuration,'--nologo','--logger','trx') }
# Common target and packaging contracts never compile another plugin implementation.
Run '.' node @('plugins/private-syncplay/scripts/jellyfin-versions.mjs')
Run '.' node @('--test','plugins/private-syncplay/tests/jellyfin-versions.test.mjs','scripts/plugin-isolation.test.mjs')
Run '.' node @('--test','scripts/admin-ui.test.mjs')
Run '.' pwsh @('-NoProfile','-File','scripts/inventory-distribution.test.ps1')
Run '.' pwsh @('-NoProfile','-File','scripts/validate-release-set.test.ps1')
Run '.' pwsh @('-NoProfile','-File','scripts/merge-plugin-catalog.test.ps1')
Run 'plugins/private-syncplay' pwsh @('-NoProfile','-File','tests/catalog-publish.test.ps1','-ProtocolOnly')
switch ($Plugin) {
    'notifications' {
        DotnetTest 'plugins/notifications/tests/Notifications.Tests/Notifications.Tests.csproj'
        DotnetTest 'modules/notifications/tests/AccountHub.Notifications.Tests/AccountHub.Notifications.Tests.csproj'
        Run 'modules/notifications' node @('--test','tests/web/bell.test.cjs')
        Run '.' node @('--test','--test-name-pattern=[Nn]otification','scripts/web-ui.test.mjs')
    }
    'private-syncplay' {
        DotnetTest 'plugins/private-syncplay/tests/Jellyfin.Plugin.PrivateSyncPlay.Tests/Jellyfin.Plugin.PrivateSyncPlay.Tests.csproj'
        Run 'plugins/private-syncplay' npm @('run','check')
        Run 'plugins/private-syncplay' npm @('run','test:ui')
        Run 'plugins/private-syncplay' node @('--test','tests/guest-state.test.mjs')
        Run 'plugins/private-syncplay' pwsh @('-NoProfile','-File','scripts/package.ps1')
        Run 'plugins/private-syncplay' node @('scripts/validate-package.mjs')
        Run 'plugins/private-syncplay' pwsh @('-NoProfile','-File','scripts/test-sdk.ps1')
        Run 'plugins/private-syncplay' pwsh @('-NoProfile','-File','tests/catalog-history.test.ps1')
        Run 'plugins/private-syncplay' pwsh @('-NoProfile','-File','tests/catalog-publish.test.ps1')
    }
    'deletion-queue' {
        DotnetTest 'plugins/deletion-queue/tests/DeletionQueue.Tests/DeletionQueue.Tests.csproj'
        DotnetTest 'modules/notifications/tests/AccountHub.Notifications.Tests/AccountHub.Notifications.Tests.csproj'
        Run '.' node @('--test','scripts/removal-ui.test.mjs')
        Run 'plugins/deletion-queue' pwsh @('-NoProfile','-File','scripts/test-release-metadata.ps1')
        Run 'plugins/deletion-queue' pwsh @('-NoProfile','-File','scripts/test-release.ps1','-Runtime','all','-Tag','v0.1.0.6')
    }
    'discord-bridge' {
        DotnetTest 'plugins/discord-bridge/tests/DiscordBridge.Tests/DiscordBridge.Tests.csproj'
        DotnetTest 'modules/security/tests/AccountHub.Security.Tests/AccountHub.Security.Tests.csproj'
        DotnetTest 'modules/notifications/tests/AccountHub.Notifications.Tests/AccountHub.Notifications.Tests.csproj'
        Run 'plugins/discord-bridge/activity' npm @('test')
        Run '.' node @('--test','scripts/discord-acceptance.test.mjs','scripts/discord-tunnel.test.mjs')
        Run '.' node @('--test','--test-name-pattern=Discord','scripts/web-ui.test.mjs')
    }
    'account-hub' {
        DotnetTest 'plugins/account-hub/tests/AccountHub.Tests/AccountHub.Tests.csproj'
        DotnetTest 'modules/security/tests/AccountHub.Security.Tests/AccountHub.Security.Tests.csproj'
        DotnetTest 'modules/notifications/tests/AccountHub.Notifications.Tests/AccountHub.Notifications.Tests.csproj'
        DotnetTest 'modules/user-management/tests/Jellyfin.Plugin.UserManager.Tests/Jellyfin.Plugin.UserManager.Tests.csproj'
        Run 'modules/security' node @('--test','web/security-client.test.mjs')
        Run 'modules/notifications' node @('--test','tests/web/bell.test.cjs')
        Run 'modules/user-management' node @('--test','tests/web/common.test.cjs')
        Run '.' node @('--test','--test-name-pattern=^(?!.*(?:Discord|SyncPlay|Deletion Queue)).*$','scripts/web-ui.test.mjs','scripts/group-policy-ui.test.mjs','scripts/domain-panel.test.mjs','scripts/invite-public.test.mjs','scripts/ui-safety-followup.test.mjs')
    }
}
Write-Output "PASS $Plugin and its common/dependency tests"
