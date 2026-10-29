param([ValidateSet('all','account-hub','discord-bridge','private-syncplay','deletion-queue','notifications','automatic-deletion-requests')][string]$Plugin = 'all',
    [string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'test-targets.ps1')
Initialize-TestTargets
$all = $Plugin -eq 'all'
$executed = [Collections.Generic.HashSet[string]]::new()
foreach ($name in @('GITHUB_REF','GITHUB_REF_TYPE','GITHUB_REF_NAME')) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
function Run([string]$Directory, [string]$Program, [string[]]$Arguments) {
    if ($all -and $Program -eq 'node' -and $Arguments[0] -eq '--test' -and $Arguments.Count -gt 2) {
        # Aggregate runs execute each complete file once. Selected release gates
        # retain their owner-specific name filters and never build other plugins.
        foreach ($file in @($Arguments | Select-Object -Skip 1 | Where-Object { $_ -notlike '--test-name-pattern=*' })) { Run $Directory node @('--test', $file) }
        return
    }
    $key = $Directory + '|' + $Program + '|' + ($Arguments -join '|')
    if (!$executed.Add($key)) { return }
    Push-Location (Join-Path $root $Directory)
    try { & $Program @Arguments; if ($LASTEXITCODE) { throw "$Program failed in $Directory" } }
    finally { Pop-Location }
}
function DotnetTest([string]$Project) { if (!$all) { Run '.' dotnet @('test',$Project,'-c',$Configuration,'--nologo','--logger','trx') } }
if ($all) {
    Run '.' dotnet @('test','Jellyfin.Plugins.slnx','-c',$Configuration,'--nologo','--logger','trx')
    foreach ($path in @('plugins/account-hub/src/AccountHub/Web/workspace.js','plugins/account-hub/src/AccountHub/Web/public.js','modules/notifications/src/AccountHub.Notifications/Web/settings.js','scripts/integration.mjs','scripts/theme-acceptance.cjs','scripts/test-combinations.mjs','plugins/account-hub/src/AccountHub/Web/group-policy.mjs','plugins/account-hub/src/AccountHub/Web/seerr-panel.mjs','plugins/account-hub/src/AccountHub/Web/invite-admin.js','plugins/account-hub/src/AccountHub/Web/invite-public.js')) { Run '.' node @('--check',$path) }
    Run 'modules/security' node @('--check','web/security-panel.mjs')
    Run '.' node @('--check','scripts/notification-category-acceptance.cjs')
}
# Common target and packaging contracts never compile another plugin implementation.
Run '.' node @('plugins/private-syncplay/scripts/jellyfin-versions.mjs')
Run '.' node @('--test','plugins/private-syncplay/tests/jellyfin-versions.test.mjs','scripts/plugin-isolation.test.mjs')
Run '.' node @('--test','scripts/admin-ui.test.mjs')
Run '.' node @('--test','scripts/diagnostics-ui.test.mjs')
Run '.' node @('--test','scripts/user-error-boundary.test.mjs')
Run '.' node @('--test','scripts/latest-releases.test.mjs')
Run '.' node @('--test','scripts/docs.test.mjs')
Run '.' node @('--test','scripts/dependency-maintenance.test.mjs')
Run '.' node @('--test','scripts/workflow-cleanup.test.mjs')
Run '.' node @('--test','scripts/update-dependencies.test.mjs')
Run '.' node @('--test','scripts/tool-bootstrap.test.mjs','scripts/fixture-tools.test.mjs','scripts/link-return.test.mjs')
Run '.' node @('--test','scripts/route-audit.test.mjs')
Run '.' node @('--test','scripts/browser-relay.test.mjs')
Run '.' node @('--test','scripts/release-target-policy.test.mjs')
Run '.' pwsh @('-NoProfile','-File','scripts/inventory-distribution.test.ps1')
Run '.' pwsh @('-NoProfile','-File','scripts/validate-release-set.test.ps1')
Run '.' pwsh @('-NoProfile','-File','scripts/merge-plugin-catalog.test.ps1')
Run 'plugins/private-syncplay' pwsh @('-NoProfile','-File','tests/catalog-publish.test.ps1','-ProtocolOnly')
foreach ($selectedPlugin in $(if ($all) { @('account-hub','discord-bridge','private-syncplay','deletion-queue','notifications','automatic-deletion-requests') } else { @($Plugin) })) {
switch ($selectedPlugin) {
    'automatic-deletion-requests' {
        DotnetTest 'plugins/automatic-deletion-requests/tests/AutomaticDeletionRequests.Tests/AutomaticDeletionRequests.Tests.csproj'
        Run '.' node @('--test','--test-name-pattern=[Aa]utomatic','scripts/web-ui.test.mjs')
    }
    'notifications' {
        DotnetTest 'plugins/notifications/tests/Notifications.Tests/Notifications.Tests.csproj'
        DotnetTest 'modules/notifications/tests/AccountHub.Notifications.Tests/AccountHub.Notifications.Tests.csproj'
        Run 'modules/notifications' node @('--test','tests/web/bell.test.cjs')
        Run '.' node @('--test','scripts/push-settings.test.mjs')
        Run '.' node @('--test','scripts/notification-studio.test.mjs')
        Run '.' node @('--test','scripts/notification-studio-snapping.test.mjs')
        Run '.' node @('--test','scripts/notification-catalog.test.mjs')
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
        Run '.' node @('--test','scripts/presentation-ui.test.mjs')
        Run 'plugins/deletion-queue' pwsh @('-NoProfile','-File','scripts/test-release-metadata.ps1')
        Run 'plugins/deletion-queue' pwsh @('-NoProfile','-File','scripts/test-release.ps1','-Runtime','all','-Tag','v0.1.0.6')
    }
    'discord-bridge' {
        DotnetTest 'plugins/discord-bridge/tests/DiscordBridge.Tests/DiscordBridge.Tests.csproj'
        DotnetTest 'modules/security/tests/AccountHub.Security.Tests/AccountHub.Security.Tests.csproj'
        DotnetTest 'modules/notifications/tests/AccountHub.Notifications.Tests/AccountHub.Notifications.Tests.csproj'
        Run '.' node @('--test','scripts/discord-acceptance.test.mjs','scripts/discord-tunnel.test.mjs')
        Run '.' node @('--test','scripts/discord-permissions.test.mjs')
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
        Run '.' node @('--test','scripts/public-link.test.mjs')
    }
}
}
Write-Output "PASS $Plugin and its common/dependency tests"
