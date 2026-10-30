// One registry for suite and owner-scoped tests. Planning never runs commands.
import { pathToFileURL } from 'node:url';

export const plugins = ['account-hub','discord-bridge','private-syncplay','deletion-queue','notifications','automatic-deletion-requests'];
const rootTests = names => names.map(name => 'scripts/' + name + '.test.mjs');
export const commonTests = [
    ...rootTests(['admin-ui','diagnostics-ui','user-error-boundary','latest-releases','ci-fixture-regressions','docs',
        'dependency-maintenance','dependency-evidence','private-command','update-dependencies','fixture-tools','route-audit','browser-relay',
        'release-target-policy','workflows','test-plan']),
    'plugins/private-syncplay/tests/jellyfin-versions.test.mjs',
    'tests/tooling/tool-http.test.mjs'
];
export const ownerTests = {
    'account-hub': rootTests(['group-policy-ui','domain-panel','invite-public','public-link','link-return']),
    'discord-bridge': rootTests(['discord-acceptance','discord-tunnel','discord-permissions','discord-settings-acceptance']),
    'private-syncplay': [],
    'deletion-queue': rootTests(['removal-ui','presentation-ui']),
    notifications: rootTests(['push-settings','notification-studio','live-poster-measurement','notification-studio-snapping','notification-catalog']),
    'automatic-deletion-requests': []
};
export const mixedTests = rootTests(['web-ui','ui-safety-followup']);
const ownerUiTests = Object.fromEntries(plugins.map(owner => [owner,
    owner === 'private-syncplay' ? [mixedTests[1]] :
        ['account-hub','deletion-queue'].includes(owner) ? mixedTests : [mixedTests[0]]
]));
const security = 'modules/security/tests/AccountHub.Security.Tests/AccountHub.Security.Tests.csproj';
const notifications = 'modules/notifications/tests/AccountHub.Notifications.Tests/AccountHub.Notifications.Tests.csproj';
export const testProjects = {
    'account-hub': ['plugins/account-hub/tests/AccountHub.Tests/AccountHub.Tests.csproj',security,notifications,'modules/user-management/tests/Jellyfin.Plugin.UserManager.Tests/Jellyfin.Plugin.UserManager.Tests.csproj'],
    'discord-bridge': ['plugins/discord-bridge/tests/DiscordBridge.Tests/DiscordBridge.Tests.csproj',security,notifications],
    'private-syncplay': ['plugins/private-syncplay/tests/Jellyfin.Plugin.PrivateSyncPlay.Tests/Jellyfin.Plugin.PrivateSyncPlay.Tests.csproj'],
    'deletion-queue': ['plugins/deletion-queue/tests/DeletionQueue.Tests/DeletionQueue.Tests.csproj',notifications],
    notifications: ['plugins/notifications/tests/Notifications.Tests/Notifications.Tests.csproj',notifications],
    'automatic-deletion-requests': ['plugins/automatic-deletion-requests/tests/AutomaticDeletionRequests.Tests/AutomaticDeletionRequests.Tests.csproj']
};
export function testPlan(plugin = 'all', configuration = 'Release') {
    if (plugin !== 'all' && !plugins.includes(plugin)) throw Error('Unknown test owner: ' + plugin);
    const all = plugin === 'all', selected = all ? plugins : [plugin], commands = [], seen = new Set();
    const add = (directory, program, ...args) => {
        const command = { directory, program, arguments: args }, key = JSON.stringify(command);
        if (!seen.has(key)) { seen.add(key); commands.push(command); }
    };
    const ps = (directory, file, ...args) => add(directory,'pwsh','-NoProfile','-File',file,...args);
    for (const project of all ? ['Jellyfin.Plugins.slnx'] : testProjects[plugin]) add('.','dotnet','test',project,'-c',configuration,'--nologo','--logger','trx');
    if (all) {
        for (const path of ['plugins/account-hub/src/AccountHub/Web/workspace.js','plugins/account-hub/src/AccountHub/Web/public.js',
            'modules/notifications/src/AccountHub.Notifications/Web/settings.js','scripts/integration.mjs','scripts/theme-acceptance.cjs',
            'scripts/test-combinations.mjs','plugins/account-hub/src/AccountHub/Web/group-policy.mjs','plugins/account-hub/src/AccountHub/Web/seerr-panel.mjs',
            'plugins/account-hub/src/AccountHub/Web/invite-admin.js','plugins/account-hub/src/AccountHub/Web/invite-public.js','scripts/notification-category-acceptance.cjs']) add('.','node','--check',path);
        add('modules/security','node','--check','web/security-panel.mjs');
    }
    add('.','node','plugins/private-syncplay/scripts/jellyfin-versions.mjs');
    const frontend = [...new Set([...commonTests,...selected.flatMap(owner => ownerTests[owner]),...(all ? mixedTests : [])])];
    add('.','node','--test','--test-concurrency=4',...frontend);
    if (!all) add('.','node','--test','--test-name-pattern=^' + plugin + ': ',...ownerUiTests[plugin]);
    for (const file of ['inventory-distribution','validate-release-set','merge-plugin-catalog']) ps('.','scripts/' + file + '.test.ps1');
    if (!selected.includes('private-syncplay')) ps('plugins/private-syncplay','tests/catalog-publish.test.ps1','-ProtocolOnly');
    for (const owner of selected) {
        if (owner === 'account-hub') {
            add('modules/security','node','--test','web/security-client.test.mjs');
            add('modules/notifications','node','--test','tests/web/bell.test.cjs');
            add('modules/user-management','node','--test','tests/web/common.test.cjs');
        } else if (owner === 'notifications') add('modules/notifications','node','--test','tests/web/bell.test.cjs');
        else if (owner === 'private-syncplay') {
            const directory = 'plugins/private-syncplay';
            add(directory,'npm','run','check'); add(directory,'npm','run','test:ui');
            add(directory,'node','--test','tests/guest-state.test.mjs');
            ps(directory,'scripts/package.ps1'); add(directory,'node','scripts/validate-package.mjs');
            for (const file of ['scripts/test-sdk.ps1','tests/catalog-history.test.ps1','tests/catalog-publish.test.ps1']) ps(directory,file);
        } else if (owner === 'deletion-queue') {
            ps('plugins/deletion-queue','scripts/test-release-metadata.ps1');
            // This four-part version is an intentional upgrade fixture, not a software target.
            ps('plugins/deletion-queue','scripts/test-release.ps1','-Runtime','all','-Tag','v0.1.0.6');
        }
    }
    return commands;
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    if (process.argv.slice(2).some(arg => !/^--(?:plugin|configuration)=/.test(arg))) throw Error('Unknown test-plan option');
    const option = name => process.argv.find(arg => arg.startsWith('--' + name + '='))?.slice(name.length + 3);
    console.log(JSON.stringify(testPlan(option('plugin'),option('configuration'))));
}
