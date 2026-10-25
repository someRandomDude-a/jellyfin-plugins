import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import assert from 'node:assert/strict';

const require = createRequire(new URL('../plugins/private-syncplay/package.json', import.meta.url));
const { JSDOM } = require('jsdom');
const source = path => readFile(new URL('../' + path, import.meta.url), 'utf8');
const turn = () => new Promise(resolve => setImmediate(resolve));
const response = data => ({ ok: true, status: 200, json: async () => ({ apiVersion: '1', ok: true, data }) });
// The source may come from either Git checkout convention. JavaScript's dot
// does not match CR, so /^import .*\n/ leaves imports behind on Windows CRLF.
const workspaceScript = code => code.replace(/^import[^\r\n]*\r?\n/, 'const mountSecurityPanel = () => () => {};\n')
    .replace('export function mountWorkspace', 'window.mountWorkspace = function');
test('Notification settings use native fields and erase unsaved secrets on cleanup', async () => {
    const dom = new JSDOM('<main></main>', { runScripts: 'outside-only', url: 'https://example.test/web/' });
    const { window } = dom; window.eval(await source('modules/notifications/src/AccountHub.Notifications/Web/settings.js'));
    const cleanup = await window.AccountHubNotificationSettings.mount(window.document.querySelector('main'), async path => path.includes('deliveries') ? { deliveries: [] } : { revision: 0, notificationDays: 90, routes: [] });
    [...window.document.querySelectorAll('button')].find(n => n.textContent === 'Add route').click(); await turn();
    assert.equal(window.document.querySelectorAll('textarea').length, 0);
    assert(window.document.querySelector('select.emby-select'));
    const secret = window.document.querySelector('input[type=password]'); secret.value = 'unsaved-test-secret'; cleanup(); assert.equal(secret.value, ''); assert.equal(window.document.querySelector('main').children.length, 0); dom.window.close();
});
test('Registered notification layout editor saves only layout with the current source revision', async () => {
    const dom = new JSDOM('<main></main>', { runScripts: 'outside-only', url: 'https://example.test/web/' });
    const { window } = dom, writes = []; window.eval(await source('modules/notifications/src/AccountHub.Notifications/Web/settings.js'));
    const cleanup = await window.AccountHubNotificationSettings.mount(window.document.querySelector('main'), async (path, method, body) => {
        if (method === 'PUT') { writes.push(body); return { status: 'updated' }; }
        if (path.endsWith('/types')) return { catalogs: [{ source: 'example-plugin', revision: 4, version: '1', types: [{ type: 'example.changed', name: 'Example changes', trigger: 'An example changes', recipients: 'explicit-users', layout: { density: 'compact', summaryLength: 140, expandable: true }, actions: [] }] }] };
        return path.includes('deliveries') ? { deliveries: [] } : { revision: 0, notificationDays: 90, routes: [] };
    });
    const panel = window.document.querySelector('details'); panel.open = true; panel.dispatchEvent(new window.Event('toggle')); await turn();
    const save = [...panel.querySelectorAll('button')].find(button => button.textContent === 'Save layout'); save.click(); await turn(); save.click(); await turn();
    assert.equal(writes[0].revision, 4); assert.equal(writes[1].revision, 5);
    assert.deepEqual(Object.keys(writes[0]).sort(), ['layout', 'revision', 'source', 'type']);
    assert.equal(writes[0].layout.summaryLength, 140); cleanup(); dom.window.close();
});
test('Notifications page does not remount during initial viewshow or discard an active editor', async () => {
    const html = await source('plugins/notifications/src/Jellyfin.Plugin.Notifications/Web/admin.html');
    const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'https://example.test/web/' });
    const w = dom.window, page = w.document.querySelector('#notificationsAdmin'); let release, mounts = 0, cleanups = 0;
    const gate = new Promise(resolve => { release = resolve; });
    w.AccountHubNotificationSettings = { mount: async () => { mounts++; await gate; return () => { cleanups++; }; } };
    w.eval(w.document.querySelector('script').textContent);
    page.dispatchEvent(new w.Event('viewshow')); page.dispatchEvent(new w.Event('viewshow')); assert.equal(mounts, 1);
    release(); await turn(); page.dispatchEvent(new w.Event('viewshow')); assert.equal(mounts, 1);
    page.dispatchEvent(new w.Event('viewhide')); assert.equal(cleanups, 1);
    page.dispatchEvent(new w.Event('viewshow')); await turn(); assert.equal(mounts, 2);
    page.dispatchEvent(new w.Event('viewhide')); dom.window.close();
});
test('Notification catalog opening during initial fetch shares the request and preserves drafts', async () => {
    const dom = new JSDOM('<main></main>', { runScripts: 'outside-only' }); const w = dom.window; w.eval(await source('modules/notifications/src/AccountHub.Notifications/Web/settings.js'));
    let release, reads = 0; const gate = new Promise(resolve => { release = resolve; });
    const mounted = w.AccountHubNotificationSettings.mount(w.document.querySelector('main'), async path => {
        if (path.endsWith('/types')) { reads++; await gate; return { catalogs: [{ source: 'example', version: '1', revision: 1, types: [{ type: 'example.changed', name: 'Example', trigger: 'Change', recipients: 'explicit-users', layout: { density: 'compact', summaryLength: 140, expandable: true }, actions: [] }] }] }; }
        return path.includes('deliveries') ? { deliveries: [] } : { revision: 0, routes: [], notificationDays: 90 };
    });
    await turn(); const panel = w.document.querySelector('details'); panel.open = true; panel.dispatchEvent(new w.Event('toggle')); await turn(); assert.equal(reads, 1);
    release(); const cleanup = await mounted; const length = panel.querySelector('input[type=number]'); length.value = '220';
    panel.open = false; panel.open = true; panel.dispatchEvent(new w.Event('toggle')); await turn(); assert.equal(reads, 1); assert.equal(length.value, '220');
    cleanup(); dom.window.close();
});
test('Aborting a pending notification settings mount cannot clear the next editor', async () => {
    const dom = new JSDOM('<main></main>', { runScripts: 'outside-only' }); const w = dom.window; w.eval(await source('modules/notifications/src/AccountHub.Notifications/Web/settings.js'));
    const controller = new w.AbortController(), root = w.document.querySelector('main'); let release, reads = 0;
    const mounted = w.AccountHubNotificationSettings.mount(root, async () => { reads++; return await new Promise(resolve => { release = resolve; }); }, { signal: controller.signal });
    controller.abort(); assert.equal(root.children.length, 0); root.textContent = 'Replacement editor';
    release({ revision: 0, routes: [], notificationDays: 90 }); const cleanup = await mounted; cleanup();
    assert.equal(reads, 1); assert.equal(root.textContent, 'Replacement editor'); dom.window.close();
});
test('Workspace navigation clears an open fresh-proof dialog without issuing a write', async () => {
    const dom = new JSDOM('<div class="accountHubPage"><p class="hubMessage"></p><nav class="hubTabs"></nav><section class="hubContent"></section></div>', { runScripts: 'outside-only', url: 'https://example.test/web/' });
    const { window } = dom; const calls = [];
    window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    window.HTMLDialogElement.prototype.close = function () { this.open = false; };
    window.fetch = async (url, options) => { calls.push({ url, method: options.method }); return response(url.endsWith('/users?skip=0&limit=50&search=') ? { items: [], total: 0 } : { revision: 0, auditRetentionDays: 90, notificationRetentionDays: 90, stepUpLevelMirror: 'Destructive', securityAlertMode: 'native', passwordLoginPolicy: 'auto' }); };
    let code = await source('plugins/account-hub/src/AccountHub/Web/workspace.js');
    code = workspaceScript(code);
    window.eval(code);
    const root = window.document.querySelector('.accountHubPage'); window.mountWorkspace(root, { getUrl: path => 'https://example.test/' + path, accessToken: () => 'disposable-token' }); await turn(); await turn();
    [...root.querySelectorAll('[role=tab]')].find(n => n.textContent === 'Settings').click(); await turn(); await turn();
    [...root.querySelectorAll('button')].find(n => n.textContent === 'Save audit retention').click(); await turn();
    const proof = window.document.querySelector('dialog input[type=password]'); assert(proof); proof.value = '123456';
    root.dispatchEvent(new window.Event('viewhide')); await turn();
    assert.equal(proof.value, ''); assert.equal(window.document.querySelectorAll('dialog').length, 0); assert.equal(calls.filter(c => c.method === 'POST').length, 0); dom.window.close();
});
test('Public recovery erases bearer fragment and sends no Jellyfin credentials', async () => {
    const token = 'a'.repeat(43), calls = [];
    const dom = new JSDOM('<base href="https://example.test/suite/AccountHub/"><main class="accountHubPublic" data-page="reset"><p class="hubPublicStatus"></p><div class="hubPublicContent"></div></main>', { runScripts: 'outside-only', url: 'https://example.test/suite/AccountHub/reset#token=' + token });
    dom.window.localStorage.setItem('jellyfin_credentials', JSON.stringify({ Servers: [{ ManualAddress: 'https://example.test/suite', AccessToken: 'must-not-send' }] }));
    dom.window.fetch = async (url, options) => { calls.push({ url: String(url), options }); return response({ valid: false }); };
    dom.window.eval(await source('plugins/account-hub/src/AccountHub/Web/public.js')); await turn();
    assert.equal(dom.window.location.hash, ''); assert.equal(calls.length, 1);
    assert.equal(calls[0].options.credentials, 'omit'); assert.equal(calls[0].options.headers.Authorization, undefined);
    assert(!calls[0].url.includes(token)); assert.equal(JSON.parse(calls[0].options.body).token, token); dom.window.close();
});
test('Missing optional Hub integrations are greyed out with remediation, not hidden or fetched', async () => {
    const dom = new JSDOM('<div class="accountHubPage"><p class="hubMessage"></p><nav class="hubTabs"></nav><section class="hubContent"></section></div>', { runScripts: 'outside-only', url: 'https://example.test/web/' });
    const { window } = dom; const calls = [];
    const dependencies = ['security', 'discord', 'deletion-queue', 'private-syncplay'].map(id => ({ id, name: id, available: false, state: 'missing', reason: 'Optional provider is missing.', fix: 'Install and enable the compatible plugin, then restart Jellyfin.' }));
    window.fetch = async (url, options) => { calls.push(url); return response(url.endsWith('/dependencies') ? { items: dependencies } : url.includes('/users?') ? { items: [], total: 0 } : { revision: 0 }); };
    let code = await source('plugins/account-hub/src/AccountHub/Web/workspace.js'); code = workspaceScript(code); window.eval(code);
    const root = window.document.querySelector('.accountHubPage'); window.mountWorkspace(root, { getUrl: path => 'https://example.test/' + path, accessToken: () => 'test' }); await turn(); await turn();
    const security = [...root.querySelectorAll('[role=tab]')].find(n => n.textContent === 'Security'); assert(security.disabled); assert(!security.hidden); assert.match(security.title, /restart Jellyfin/);
    for (const name of ['Discord', 'DeletionQueue', 'Private SyncPlay', 'Seerr']) assert(![...root.querySelectorAll('[role=tab]')].some(n => n.textContent === name));
    assert(!root.querySelector('#hub-dependency-deletion-queue')); assert(!root.querySelector('#hub-dependency-private-syncplay'));
    assert.equal([...root.querySelectorAll('[role=tab]')].find(n => n.textContent === 'People').disabled, false);
    assert.match(root.querySelector('.hubDependencyHealth').textContent, /Install and enable/); assert(!calls.some(path => path.includes('/domains/'))); dom.window.close();
});
test('Discord transport settings stay enabled without Hub, while dependent command switches explain missing plugins', async () => {
    const html = await source('plugins/discord-bridge/src/Jellyfin.Plugin.DiscordBridge/Web/settings.html');
    const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'https://example.test/web/' }); const { window } = dom;
    window.ApiClient = { getUrl: path => 'https://example.test/' + path, accessToken: () => 'test' };
    window.fetch = async () => response({ revision: 0, settings: { enabled: false, publicServerUrl: '', guildId: '', allowedChannelIds: [], enableSelfServiceCommands: true, enableWatchPartyCommands: true }, tokenConfigured: false, state: 'disabled', authorization: 'Standalone native elevation', features: [
        { id: 'transport', name: 'Bot transport', available: true, reason: 'Independent', fix: '' },
        { id: 'identity', name: 'Account commands', available: false, reason: 'Account Hub is missing.', fix: 'Install Account Hub and restart Jellyfin.' },
        { id: 'watchparty', name: 'Party commands', available: false, reason: 'Account Hub and Private SyncPlay are required.', fix: 'Install the missing plugins and restart Jellyfin.' }
    ] });
    window.eval(window.document.querySelector('script').textContent); await turn(); await turn();
    assert.equal(window.document.querySelector('.bridgeEnabled').disabled, false); assert.equal(window.document.querySelector('.bridgeToken').disabled, false);
    assert.equal(window.document.querySelector('.bridgeSelfService').disabled, true); assert.equal(window.document.querySelector('.bridgeWatchParty').disabled, true);
    assert.match(window.document.querySelector('.bridgeIdentityReason').textContent, /Install Account Hub/); assert.match(window.document.querySelector('.bridgePartyReason').textContent, /Private SyncPlay/); dom.window.close();
});
test('Library inbox controls remain native and independent when Discord is missing', async () => {
    const dom = new JSDOM('<div class="accountHubPage"><p class="hubMessage"></p><nav class="hubTabs"></nav><section class="hubContent"></section></div>', { runScripts: 'outside-only', url: 'https://example.test/web/' });
    const { window } = dom;
    window.fetch = async url => response(url.endsWith('/dependencies') ? { items: [{ id: 'discord', available: false, reason: 'Bridge is missing.', fix: 'Install Discord Bridge and restart Jellyfin.' }] } : url.endsWith('/library/settings') ? { settings: { revision: 0, enabled: false, episodes: true, movies: true }, health: { retained: 0, pending: 0 } } : url.includes('/users?') ? { items: [], total: 0 } : { revision: 0 });
    let code = await source('plugins/account-hub/src/AccountHub/Web/workspace.js'); code = workspaceScript(code); window.eval(code);
    const root = window.document.querySelector('.accountHubPage'); window.mountWorkspace(root, { getUrl: path => 'https://example.test/' + path, accessToken: () => 'test' }); await turn(); await turn();
    const tab = [...root.querySelectorAll('[role=tab]')].find(n => n.textContent === 'Library'); assert.equal(tab.disabled, false); tab.click(); await turn(); await turn();
    assert.equal(root.querySelectorAll('.hubContent input.emby-checkbox').length, 3);
    const buttons = [...root.querySelectorAll('.hubContent button')]; assert.equal(buttons.find(n => n.textContent === 'Save library notifications').disabled, false);
    const discord = buttons.find(n => n.textContent === 'Open Discord library announcement settings'); assert(discord.disabled); assert.match(discord.title, /Install Discord Bridge/); dom.window.close();
});
test('Discord library controls load explicit scopes safely, without requiring public invitation URL', async () => {
    const html = await source('plugins/discord-bridge/src/Jellyfin.Plugin.DiscordBridge/Web/settings.html');
    const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'https://example.test/web/' }); const { window } = dom;
    const id = '9dbb15b7-7eea-445a-8e67-5bc8a85f0387';
    window.ApiClient = { getUrl: path => 'https://example.test/' + path, accessToken: () => 'test' };
    window.fetch = async () => response({ revision: 0, settings: { enabled: false, publicServerUrl: '', guildId: '', allowedChannelIds: ['1234567890'], libraryIds: [id], libraryChannelIds: ['1234567890'] }, features: [], state: 'disabled', libraryStatus: 'available', libraryFolders: [{ id, name: '<img onerror=alert(1)>' }], libraryCaptureOwner: 'Discord Bridge', libraryHealth: { pending: 2, uncertainDiscord: 1 } });
    window.eval(window.document.querySelector('script').textContent); await turn(); await turn();
    const folders = window.document.querySelector('.bridgeLibraryFolders'); assert.equal(folders.selectedOptions[0].value, id); assert.equal(folders.querySelector('img'), null);
    assert.equal(window.document.querySelector('.bridgeLibraryEnabled').disabled, false); assert.equal(window.document.querySelector('.bridgeUrl').required, false);
    assert.match(window.document.querySelector('.bridgeLibraryHealth').textContent, /uncertainDiscord/); dom.window.close();
});

async function workspaceFixture(t, lineEnding) {
    const dom = new JSDOM('<div class="accountHubPage"><p class="hubMessage"></p><nav class="hubTabs"></nav><section class="hubContent"></section></div>', { runScripts: 'outside-only', url: 'https://example.test/web/' });
    t.after(() => dom.window.close());
    const { window } = dom, calls = [];
    window.fetch = async (url, options) => {
        calls.push({ url, options });
        return response(url.endsWith('/dependencies') ? { items: [] } : url.includes('/users?') ? { items: [], total: 0 }
            : url.endsWith('/settings/notifications') ? { revision: 0, notificationDays: 90, routes: [] }
                : url.includes('/deliveries?') ? { deliveries: [] }
                    : { revision: 0, auditRetentionDays: 90, notificationRetentionDays: 90, stepUpLevelMirror: 'Off', securityAlertMode: 'native', passwordLoginPolicy: 'auto' });
    };
    const code = await source('plugins/account-hub/src/AccountHub/Web/workspace.js');
    window.eval(workspaceScript(lineEnding ? code.replace(/\r?\n/g, lineEnding) : code));
    const root = window.document.querySelector('.accountHubPage'), content = root.querySelector('.hubContent');
    window.mountWorkspace(root, { getUrl: path => 'https://example.test/' + path, accessToken: () => 'test' }); await turn(); await turn();
    const tab = async name => { [...root.querySelectorAll('[role=tab]')].find(n => n.textContent === name).click(); await turn(); await turn(); };
    return { window, root, content, calls, tab };
}

for (const navigation of ['tab switch', 'viewhide', 'viewdestroy']) {
    test('Delayed Notifications script cannot mount after ' + navigation, async t => {
        const f = await workspaceFixture(t); await f.tab('Notifications');
        const script = f.window.document.querySelector('script[src$="/notifications/settings.js"]'); assert(script);
        if (navigation === 'tab switch') await f.tab('Settings'); else f.root.dispatchEvent(new f.window.Event(navigation));
        const expected = f.content.innerHTML;
        f.window.eval(await source('modules/notifications/src/AccountHub.Notifications/Web/settings.js'));
        script.dispatchEvent(new f.window.Event('load')); await turn(); await turn();
        assert.equal(f.content.innerHTML, expected);
        assert.equal(f.calls.filter(c => c.url.includes('/settings/notifications')).length, 0);
        if (navigation === 'tab switch') assert.equal(f.root.querySelector('[aria-selected=true]').textContent, 'Settings');
    });
}

test('Late Notifications mount cleanup and stale requests cannot affect a newer tab', async t => {
    const f = await workspaceFixture(t);
    let release, panel, panelRequest, disposed = 0;
    const pending = new Promise(resolve => { release = resolve; });
    f.window.AccountHubNotificationSettings = { async mount(root, request) {
        panel = root; panelRequest = request; root.textContent = 'Pending Notifications mount';
        await pending; root.textContent = 'Late Notifications mount';
        return () => { disposed++; root.replaceChildren(); };
    } };
    await f.tab('Notifications'); await f.tab('Settings');
    assert.equal(panel.isConnected, false);
    const count = f.calls.length;
    await assert.rejects(panelRequest('settings/notifications', 'GET'), { name: 'AbortError' });
    assert.equal(f.calls.length, count);
    const secret = f.content.querySelector('input[type=password]'); secret.value = 'new-view-secret';
    release(); await turn(); await turn();
    assert.equal(disposed, 1);
    assert.equal(f.content.querySelector('h2').textContent, 'Account Hub settings');
    assert.equal(secret.value, 'new-view-secret');
    await f.tab('People'); assert.equal(disposed, 1);
    assert.equal(secret.value, '');
});

test('Navigation aborts a real pending Notifications mount and immediately erases its unsaved secret', async t => {
    const f = await workspaceFixture(t);
    f.window.eval(await source('modules/notifications/src/AccountHub.Notifications/Web/settings.js'));
    const fetch = f.window.fetch; let release, signal;
    const pending = new Promise(resolve => { release = resolve; });
    f.window.fetch = async (url, options) => {
        if (url.includes('/deliveries?')) { signal = options.signal; await pending; }
        return fetch(url, options); // Deliberately finishes even after abort, like a late transport completion.
    };
    await f.tab('Notifications'); assert(signal);
    [...f.content.querySelectorAll('button')].find(n => n.textContent === 'Add route').click(); await turn();
    const secret = f.content.querySelector('input[type=password]'); assert(secret); secret.value = 'pending-notifications-secret';
    await f.tab('Settings'); assert.equal(signal.aborted, true); assert.equal(secret.value, ''); assert.equal(secret.isConnected, false);
    const expected = f.content.innerHTML;
    release(); await turn(); await turn();
    assert.equal(f.content.innerHTML, expected); assert.equal(secret.value, '');
    assert.equal(f.calls.filter(c => c.options.method !== 'GET').length, 0);
});

for (const lineEnding of ['\n', '\r\n']) for (const navigation of ['tab switch', 'viewhide', 'viewdestroy']) {
    test('Unsaved Settings webhook secret is erased through retained reference on ' + navigation + (lineEnding === '\n' ? ' (LF)' : ' (CRLF)'), async t => {
        const f = await workspaceFixture(t, lineEnding); await f.tab('Settings');
        const secret = f.content.querySelector('input[type=password]'); assert(secret); secret.value = 'unsaved-webhook-secret';
        if (navigation === 'tab switch') await f.tab('People'); else f.root.dispatchEvent(new f.window.Event(navigation));
        assert.equal(secret.value, ''); assert.equal(secret.isConnected, false);
        assert.equal(f.calls.filter(c => c.options.method !== 'GET').length, 0);
    });
}

test('Native creation override uses explicit fresh confirmation and exposes five-minute status', async t => {
    const f = await workspaceFixture(t), original = f.window.fetch; let enabled = false, sent;
    f.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    f.window.HTMLDialogElement.prototype.close = function () { this.open = false; };
    f.window.fetch = async (url, options) => {
        if (url.endsWith('/user-creation/native-override')) { sent = JSON.parse(options.body); enabled = sent.enabled; return response({ owner: 'native' }); }
        if (url.endsWith('/user-creation')) return response({ owner: enabled ? 'native' : 'hub', nativeOverrideUntil: enabled ? Date.now() + 300000 : null });
        return original(url, options);
    };
    await f.tab('Settings');
    [...f.content.querySelectorAll('button')].find(b => b.textContent === 'Enable native creation for five minutes').click(); await turn();
    assert.equal(sent, undefined);
    const dialog = f.window.document.querySelector('dialog'); assert(dialog);
    dialog.querySelector('input[type=text]').value = 'CONFIRM ACCOUNT HUB CHANGE';
    dialog.querySelector('form').dispatchEvent(new f.window.Event('submit', { bubbles: true, cancelable: true })); await turn(); await turn();
    assert.equal(sent.enabled, true); assert.equal(sent.confirmation, 'CONFIRM ACCOUNT HUB CHANGE'); assert(sent.commandId);
    assert.match(f.content.textContent, /Hub onboarding is paused/);
    assert([...f.content.querySelectorAll('button')].some(b => b.textContent === 'End native override and resume Hub'));
});

test('Deletion Queue integrated CSS leaves presentation to native and custom themes', async t => {
    const css = await source('plugins/deletion-queue/src/Jellyfin.Plugin.DeletionQueue/Web/integrated.css');
    assert.doesNotMatch(css, /#[\da-f]{3,8}\b|rgba?\(|hsla?\(|(?:^|[;{])\s*(?:color|background(?:-[\w-]+)?|border(?:-[\w-]+)?|box-shadow|font(?:-[\w-]+)?|line-height|backdrop-filter)\s*:|::backdrop|prefers-color-scheme/mi);
    const dom = new JSDOM('<style>.dialog { background: rgb(10, 20, 30); color: rgb(230, 220, 210); border-radius: 9px; } .emby-button { background: rgb(40, 50, 60); color: rgb(210, 200, 190); border-radius: 7px; }</style><dialog class="dialog dq-panel" open></dialog><button class="emby-button dq-card-action">Remove</button>', { url: 'https://example.test/web/' });
    t.after(() => dom.window.close());
    const style = dom.window.document.createElement('style'); style.textContent = css; dom.window.document.head.append(style);
    const panel = dom.window.getComputedStyle(dom.window.document.querySelector('dialog')), card = dom.window.getComputedStyle(dom.window.document.querySelector('button'));
    assert.equal(panel.backgroundColor, 'rgb(10, 20, 30)'); assert.equal(panel.color, 'rgb(230, 220, 210)'); assert.equal(panel.borderRadius, '9px');
    assert.equal(card.backgroundColor, 'rgb(40, 50, 60)'); assert.equal(card.color, 'rgb(210, 200, 190)'); assert.equal(card.borderRadius, '7px');
    assert.equal(panel.position, 'fixed'); assert.equal(card.position, 'absolute');
});
