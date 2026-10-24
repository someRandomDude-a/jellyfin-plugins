import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import assert from 'node:assert/strict';
const require = createRequire(new URL('../plugins/private-syncplay/package.json', import.meta.url));
const { JSDOM } = require('jsdom');
const code = (await readFile(new URL('../plugins/account-hub/src/AccountHub/Web/domain-panel.js', import.meta.url), 'utf8')).replace('export function mountDomainPanel', 'window.mountDomainPanel = function');
const turn = async () => { for (let i = 0; i < 6; i++) await new Promise(resolve => setImmediate(resolve)); };
const id = n => '10000000-0000-4000-8000-' + String(n).padStart(12, '0');
const success = data => ({ ok: true, status: 200, json: async () => ({ apiVersion: '1', ok: true, data }) });
const failure = (code, status = 409) => ({ ok: false, status, json: async () => ({ apiVersion: '1', ok: false, error: { code } }) });
const defaults = { defaultMode: 'Public', createPublic: 'Inherit', createPrivate: 'Inherit', guestInvites: 'Inherit' };
const user = (n, admin = false, sync = true) => ({ userId: id(n), name: 'User ' + n, administrator: admin, policy: sync ? { ...defaults, defaultMode: null } : { authority: null, notificationPreferences: { delayHours: 48, notifications: true } }, defaults: sync ? { defaultMode: 'Public', createPublic: true } : { authority: 'OwnRequests' }, effective: sync ? { defaultMode: 'Public', createPublic: true } : { authority: 'OwnRequests', source: 'default' }, sources: { defaultMode: 'default' } });
async function fixture({ sync = true, total = 51, dependency, respond } = {}) {
    const dom = new JSDOM('<main></main>', { runScripts: 'outside-only', url: 'https://example.test/suite/web/' });
    const { window } = dom; window.structuredClone = structuredClone;
    window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    window.HTMLDialogElement.prototype.close = function () { this.open = false; };
    const root = window.document.querySelector('main'), calls = [], outer = new window.AbortController();
    const caps = sync ? { settingsFields: ['allowPublicGroups', 'defaultMode', 'guestPlayer'], globalPolicyFields: ['allowPublicGroups', 'defaultMode'], enums: { defaultMode: ['Public', 'Private'], permission: ['Inherit', 'Allow', 'Deny'] }, limits: {}, guestPlayer: { features: ['title', 'mute'], maxControls: 24, controlIdPattern: '^[a-z][a-z0-9.-]{0,63}$' } } : { fields: { authority: { values: ['Denied', 'OwnRequests', 'AnyAccessibleItem'] }, notificationMode: { values: ['Auto', 'Local', 'Hub', 'Disabled'] }, hubFallbackAfterMinutes: { min: 1, max: 1440 }, defaultAuthority: { values: ['Denied', 'OwnRequests', 'AnyAccessibleItem'] }, activityDays: { min: 1, max: 365 }, delayHours: { min: 48, max: 8760 } } };
    const settings = sync ? { revision: 'r1', settings: { allowPublicGroups: true, defaultMode: 'Public', guestPlayer: { features: ['title'], controls: ['mute'] } } } : { revision: 'r1', notificationMode: 'Auto', hubIntegrationEnabled: true, hubFallbackAfterMinutes: 15, publishAudit: true, deviceBell: true, seerr: { baseUrl: 'https://seerr.test', keyConfigured: true } };
    window.fetch = async (url, options) => {
        const payload = options.body ? JSON.parse(options.body) : undefined; calls.push({ url, options, payload });
        const override = await respond?.(url, options, payload); if (override) return override;
        if (url.endsWith('/capabilities')) return success(caps);
        if (url.endsWith('/settings')) return success(settings);
        if (url.endsWith('/workspace')) return success({ revision: 8 });
        if (url.includes('/effective-policy')) { const n = Number(url.split('/users/')[1].split('/')[0].slice(-12)); return success({ revision: 'r1', user: user(n, false, sync) }); }
        if (url.includes('/policy?')) { const skip = Number(new URL(url).searchParams.get('skip')); return success({ revision: 'r1', defaults: sync ? { allowPublicGroups: true, defaultMode: 'Public' } : { defaultAuthority: 'OwnRequests', activityDays: 14, delayHours: 48, automaticDeletion: false }, total, users: Array.from({ length: Math.min(50, total - skip) }, (_, n) => user(skip + n + 1, skip + n === 0, sync)) }); }
        if (payload?.route === 'policy/preview') return success({ revision: 'r1', users: [{ userId: id(2), before: user(2, false, sync), after: user(2, false, sync) }], skippedAdmins: [], warnings: ['Global safety applies.'] });
        if (payload) return success({ revision: 'r2', skippedAdmins: [], results: [{ userId: id(2), status: 'applied' }, { userId: id(3), code: 'revision_changed' }] });
        throw Error('Unexpected URL ' + url);
    };
    window.eval(code);
    const cleanup = window.mountDomainPanel(root, { getUrl: path => 'https://example.test/suite/' + path, accessToken: () => 'test-session' }, { key: sync ? 'private-syncplay' : 'deletion-queue', signal: outer.signal, dependency });
    await turn();
    const click = async (name, scope = root) => { const button = [...scope.querySelectorAll('button')].find(b => b.textContent === name); assert(button, 'Missing button ' + name); button.click(); await turn(); };
    const select = (label, value, scope = root) => { const caption = [...scope.querySelectorAll('label')].find(n => n.textContent === label); assert(caption, 'Missing field ' + label); const input = window.document.getElementById(caption.htmlFor); if (input.type === 'checkbox') input.checked = value; else input.value = value; input.dispatchEvent(new window.Event('change', { bubbles: true })); return input; };
    async function grant() { const dialog = window.document.querySelector('dialog'); assert(dialog); select('Type CONFIRM ACCOUNT HUB CHANGE', 'CONFIRM ACCOUNT HUB CHANGE', dialog); const proof = dialog.querySelector('input[type=password]'); proof.value = '123456'; dialog.querySelector('form').dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true })); await turn(); assert.equal(proof.value, ''); }
    return { dom, window, root, calls, outer, cleanup, click, select, grant, close: () => { cleanup(); dom.window.close(); } };
}
test('Missing domain disables only its own controls and gives a repair without network calls', async () => {
    const f = await fixture({ dependency: { available: false, reason: 'Domain is missing.', fix: 'Install and enable, then restart Jellyfin.' } });
    assert.match(f.root.textContent, /restart Jellyfin/); assert(f.root.querySelector('fieldset').disabled); assert.equal(f.calls.length, 0); f.close();
});
test('Pagination retains explicit selection, protects administrators, and sends only selected native IDs', async () => {
    const f = await fixture(); const admin = f.root.querySelector('input[type=checkbox]'); assert(admin.disabled);
    f.select('Select User 2', true); await f.click('Next page'); assert.match(f.root.textContent, /Users 51–51 of 51/); f.select('Select User 51', true);
    await f.click('Edit selected overrides'); await f.click('Preview user changes');
    const preview = f.calls.find(c => c.payload?.route === 'policy/preview').payload;
    assert.deepEqual(preview.payload.userIds, [id(2), id(51)]); assert.deepEqual(preview.payload.changes.policy, { defaultMode: null, createPublic: 'Inherit', createPrivate: 'Inherit', guestInvites: 'Inherit' });
    assert.equal(f.calls.filter(c => c.payload && c.payload.route !== 'policy/preview').length, 0); f.close();
});
test('Deletion Queue uses authority null and native nested notificationPreferences, not SyncPlay permissions', async () => {
    const f = await fixture({ sync: false, total: 3 }); f.select('Select User 2', true); await f.click('Edit selected overrides');
    f.select('Authority', 'Denied'); f.select('Notification delay hours', '200'); f.select('Notifications', false); await f.click('Preview user changes');
    const preview = f.calls.find(c => c.payload?.route === 'policy/preview').payload.payload;
    assert.deepEqual(preview.users, [{ userId: id(2), policy: { authority: 'Denied', notificationPreferences: { delayHours: 200, notifications: false } } }]);
    assert.equal(preview.changes, undefined); assert.equal(preview.revision, 'r1'); f.close();
});
test('Edits after preview invalidate apply and require a new preview', async () => {
    const f = await fixture({ total: 3 }); f.select('Select User 2', true); await f.click('Edit selected overrides'); await f.click('Preview user changes'); assert.match(f.root.textContent, /Apply reviewed change/);
    f.select('Create Public', 'Deny'); assert(!f.root.textContent.includes('Apply reviewed change')); f.close();
});
test('Explicit same-command retry does not reissue uncertain effects and shows partial results per user', async () => {
    let attempts = 0;
    const f = await fixture({ total: 3, respond: async (_url, _options, payload) => { if (payload && payload.route !== 'policy/preview' && ++attempts === 1) throw TypeError('Connection lost'); } });
    f.select('Select User 2', true); await f.click('Edit selected overrides'); await f.click('Preview user changes');
    await f.click('Apply reviewed change'); await f.grant(); assert.match(f.root.textContent, /Use Retry to check this change/);
    await f.click('Retry exact command'); await f.grant(); const writes = f.calls.filter(c => c.payload && c.payload.route !== 'policy/preview');
    assert.equal(writes.length, 2); assert.equal(writes[0].payload.commandId, writes[1].payload.commandId); assert.deepEqual(writes[0].payload.payload, writes[1].payload.payload);
    assert.match(f.root.querySelector('[aria-label="Command result"]').textContent, /applied/); assert.match(f.root.querySelector('[aria-label="Command result"]').textContent, /revision_changed/); f.close();
});
test('Revision conflict clears retry/apply and never silently rebases', async () => {
    const f = await fixture({ total: 3, respond: async (_url, _options, payload) => payload?.route === 'policy/preview' ? failure('revision_changed') : undefined });
    f.select('Select User 2', true); await f.click('Edit selected overrides'); await f.click('Preview user changes');
    assert.match(f.root.textContent, /Reload and preview again/); assert(!f.root.textContent.includes('Apply reviewed change')); f.close();
});
test('Abort erases open proof dialog and inputs, cancels requests, and performs no write', async () => {
    const f = await fixture({ total: 3 }); f.select('Select User 2', true); await f.click('Edit selected overrides'); await f.click('Preview user changes'); await f.click('Apply reviewed change');
    const proof = f.window.document.querySelector('dialog input[type=password]'); proof.value = 'do-not-retain'; f.outer.abort(); await turn();
    assert.equal(proof.value, ''); assert.equal(f.window.document.querySelector('dialog'), null); assert.equal(f.root.children.length, 0); assert(f.calls.every(c => c.options.signal.aborted)); assert.equal(f.calls.filter(c => c.payload && c.payload.route !== 'policy/preview').length, 0); f.close();
});
test('Domain settings preview uses native nested settings and player allowlists', async () => {
    const f = await fixture({ total: 3 }); await f.click('Edit settings'); f.select('Default Mode', 'Private'); await f.click('Preview settings');
    const payload = f.calls.find(c => c.payload?.route === 'policy/preview').payload.payload; assert.equal(payload.changes.settings.defaultMode, 'Private'); assert.deepEqual(payload.changes.settings.guestPlayer.features, ['title']); assert.deepEqual(payload.changes.settings.guestPlayer.controls, ['mute']); f.close();
});
test('All-domain reset uses explicit resetScope, bounded preview disclosure and no party participants', async () => {
    const f = await fixture({ respond: async (_url, _options, payload) => payload?.route === 'policy/preview' ? success({ revision: 'r1', usersTruncated: true, totalAffectedUsers: 500, users: [], skippedAdmins: [id(1)], warnings: ['Native safety applies.'] }) : undefined });
    await f.click('Preview reset all users and globals'); const payload = f.calls.find(c => c.payload?.route === 'policy/preview').payload.payload;
    assert.equal(payload.resetScope, 'all'); assert.deepEqual(payload.userIds, []); assert.match(f.root.textContent, /first 0 users of 500/); assert.match(f.root.textContent, /Excluded administrators: 1/); assert(!f.root.textContent.includes('participant')); f.close();
});
test('Deletion Queue secret replacement never reads back a key and is erased on unmount', async () => {
    const f = await fixture({ sync: false, total: 3 }); await f.click('Edit settings'); const secret = f.root.querySelector('input[type=password]'); assert.equal(secret.value, ''); secret.value = 'transient-replacement';
    f.cleanup(); assert.equal(secret.value, ''); assert(!f.root.textContent.includes('transient-replacement')); assert.equal(f.calls.filter(c => c.payload).length, 0); f.close();
});
