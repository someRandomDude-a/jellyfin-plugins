import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import assert from 'node:assert/strict';
const require = createRequire(new URL('../plugins/private-syncplay/package.json', import.meta.url));
const { JSDOM } = require('jsdom');
const source = path => readFile(new URL('../' + path, import.meta.url), 'utf8');
const hub = 'plugins/account-hub/src/AccountHub/Web/';
const sync = 'plugins/private-syncplay/src/Jellyfin.Plugin.PrivateSyncPlay/Web/';
const dq = 'plugins/deletion-queue/src/Jellyfin.Plugin.DeletionQueue/Web/';
const turn = async () => { await new Promise(r => setImmediate(r)); await new Promise(r => setImmediate(r)); };
const deferred = () => { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; };
function environment(t, html = '<main></main>') {
    const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'https://example.test/web/' });
    t.after(() => dom.window.close());
    dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    dom.window.HTMLDialogElement.prototype.close = function () { this.open = false; this.dispatchEvent(new dom.window.Event('close')); };
    return dom.window;
}
const find = (w, label) => [...w.document.querySelectorAll('button')].find(b => b.textContent === label);
async function click(w, label) { const b = find(w, label); assert(b, label); b.click(); await turn(); }
async function seerr(t, request) {
    const w = environment(t), make = (tag, text) => { const n = w.document.createElement(tag); if (text) n.textContent = text; return n; };
    const button = (label, fn) => { const b = make('button', label); b.onclick = fn; return b; };
    w.eval((await source(hub + 'seerr-panel.mjs')).replace('export async function mountSeerrPanel', 'window.mountSeerrPanel = async function'));
    const signal = new w.AbortController();
    await w.mountSeerrPanel(w.document.querySelector('main'), { request, signal: signal.signal, make, button, table: (_, rows) => { const div = make('div'); for (const row of rows) for (const item of row) div.append(item instanceof w.Node ? item : make('span', String(item))); return div; }, field: (_, value) => { const input = make('input'); input.value = value; return { input, root: input, value: () => input.value }; } });
    return { w, signal };
}
const connection = { baseUrl: 'https://seerr.test', keyConfigured: true, warnings: [] };
const people = { items: [{ id: 'a', username: 'Alice' }, { id: 'b', username: 'Bob' }], total: 2 };
test('Unconfigured Seerr greys dependent actions but retains local jobs and explains remediation', async t => {
    const calls = [];
    const { w } = await seerr(t, async path => { calls.push(path); return path === 'seerr/connection' ? { warnings: [], keyConfigured: false } : path.startsWith('users?') ? people : { items: [{ id: 'job', state: 'failed' }], total: 1, attention: 1 }; });
    for (const label of ['Discover / verify connection', 'Manage Seerr for Alice', 'Retry after review']) { assert(find(w, label).disabled); assert.match(find(w, label).title, /API key/); }
    assert(!find(w, 'Cancel follow-up').disabled);
    assert(!calls.some(p => p.startsWith('seerr/users')));
});
test('Seerr detail response epochs reject an older selection and directory links capture the reviewed native ID', async t => {
    const first = deferred(), calls = [];
    const { w } = await seerr(t, async (path, method, payload) => {
        calls.push({ path, payload });
        if (path === 'seerr/connection') return connection;
        if (path.startsWith('users?')) return people;
        if (path === 'users/a/seerr') return first.promise;
        if (path === 'users/b/seerr') return { linked: false };
        if (path.startsWith('seerr/users?')) return { items: [{ id: 12, jellyfinUserId: 'b', username: 'Upstream Bob' }] };
        if (path === 'workspace') return { revision: 4 };
        return { items: [], total: 0, attention: 0 };
    });
    find(w, 'Manage Seerr for Alice').click(); await click(w, 'Manage Seerr for Bob');
    first.resolve({ linked: false }); await turn();
    assert.match(w.document.querySelector('main').textContent, /Seerr for Bob/);
    assert(![...w.document.querySelectorAll('h3')].some(n => n.textContent === 'Seerr for Alice'));
    const link = find(w, 'Verify and link #12');
    find(w, 'Manage Seerr for Alice').click(); await turn();
    // Even a retained reference must never retarget Bob's reviewed upstream evidence to Alice.
    link.click(); await turn();
    const command = calls.find(c => c.path === 'seerr/commands/link');
    if (command) assert.equal(command.payload.payload.userId, 'b');
});
test('Aborting a pending Seerr mount prevents late content from returning', async t => {
    const w = environment(t), pending = deferred();
    w.eval((await source(hub + 'seerr-panel.mjs')).replace('export async function mountSeerrPanel', 'window.mountSeerrPanel = async function'));
    const signal = new w.AbortController(), root = w.document.querySelector('main');
    const mounted = w.mountSeerrPanel(root, { signal: signal.signal, request: () => pending.promise, make: tag => w.document.createElement(tag) });
    signal.abort(); pending.resolve(connection); await mounted;
    assert.equal(root.children.length, 0);
});
test('Group subviews reject late lists and native selects expose arrow and focus contracts', async t => {
    const w = environment(t), pending = deferred(); let lists = 0;
    w.eval((await source(hub + 'group-policy.mjs')).replace('export function mountGroupPolicy', 'window.mountGroupPolicy = function'));
    w.mountGroupPolicy(w.document.querySelector('main'), { request: async path => path.startsWith('group-policy/groups?') ? (++lists === 1 ? { items: [], total: 0 } : pending.promise) : { items: [], total: 0 } }); await turn();
    await click(w, 'Members and assignments'); find(w, 'Groups').click(); await turn();
    await click(w, 'Search'); pending.resolve({ items: [{ name: 'Stale group', id: 'old' }], total: 1 }); await turn();
    assert(!w.document.body.textContent.includes('Stale group'));
    await click(w, 'Groups'); await click(w, 'Create group');
    for (const select of w.document.querySelectorAll('select')) { assert(select.parentNode.querySelector('.selectArrowContainer[aria-hidden=true]')); select.focus(); assert(select.labels[0].classList.contains('selectLabelFocused')); }
});
test('Invite pagination follows the no-total API through the end, browses templates, and exposes reservation reconciliation', async t => {
    const w = environment(t), calls = [];
    w.eval(await source(hub + 'invite-admin.js'));
    const panel = await w.AccountHubInvites.mount(w.document.querySelector('main'), { read: async path => {
        calls.push(path);
        if (path.startsWith('group-policy/groups?')) return { items: [], total: 101 };
        if (path === 'invites?skip=0&limit=100') return { items: Array.from({ length: 100 }, (_, id) => ({ id: String(id), status: 'active', expiresAt: '2030-01-01' })) };
        if (path === 'invites?skip=100&limit=100') return { items: [{ id: 'last', status: 'active', expiresAt: '2030-01-01' }] };
        return { reservations: [{ id: 'reservation', userId: 'native', username: 'User', state: 'ready_to_enable' }] };
    }, write: async (path, payload, risk) => { calls.push({ path, payload, risk }); return {}; } });
    assert(!find(w, 'Next invitations').disabled); await click(w, 'Next invitations');
    assert(find(w, 'Next invitations').disabled); assert(!find(w, 'Previous invitations').disabled);
    await click(w, 'Next templates'); assert(calls.includes('group-policy/groups?skip=100&limit=100'));
    await click(w, 'Provisioning history'); await click(w, 'Reconcile reservation (record verified completion; never create or enable)');
    assert(calls.some(c => c.path === 'invite-reservations/reservation/reconcile' && c.risk === 'Configuration'));
    const discord = [...w.document.querySelectorAll('input')].find(n => n.maxLength === 20); assert(discord.disabled); assert.match(discord.title, /connect its bot/);
    panel.dispose(); assert.equal(w.document.querySelector('main').children.length, 0);
});
test('Invite disposal erases retained one-time link and ignores late issuance', async t => {
    const w = environment(t), pending = deferred(), signal = new w.AbortController();
    w.eval(await source(hub + 'invite-admin.js'));
    let count = 0;
    await w.AccountHubInvites.mount(w.document.querySelector('main'), { signal: signal.signal, read: async path => path.startsWith('group-policy') ? { items: [], total: 0 } : { items: [{ id: 'one', status: 'active' }] }, write: async () => ++count === 1 ? { oneTimeLink: '/invite#first' } : pending.promise });
    await click(w, 'Rotate link (invalidates old link)');
    const secret = w.document.querySelector('input[readonly]'); assert.match(secret.value, /#first/);
    find(w, 'Rotate link (invalidates old link)').click(); await turn(); assert.equal(secret.value, '');
    signal.abort(); pending.resolve({ oneTimeLink: '/invite#late' }); await turn();
    assert.equal(w.document.querySelector('main').children.length, 0);
});
test('SyncPlay nested dialogs have unique names and navigation disposes every modal and retained secret', async t => {
    const w = environment(t, '<script src="https://example.test/PrivateSyncPlay/assets/ui.js"></script>');
    Object.defineProperty(w.document, 'currentScript', { value: w.document.querySelector('script'), configurable: true });
    w.eval(await source(sync + 'ui.js'));
    const ui = w.PrivateSyncPlayUI, first = ui.dialog('Parent'), second = ui.dialog('Confirm');
    first.dialog.showModal(); second.dialog.showModal(); const input = ui.field('Link', 'text', 'private-link', first.content);
    const ids = [...w.document.querySelectorAll('[id]')].map(n => n.id); assert.equal(new Set(ids).size, ids.length);
    for (const d of [first.dialog, second.dialog]) assert(w.document.getElementById(d.getAttribute('aria-labelledby')));
    const epoch = ui.navigationEpoch; w.dispatchEvent(new w.Event('hashchange'));
    assert.equal(w.document.querySelectorAll('dialog').length, 0); assert.equal(input.value, ''); assert(ui.navigationEpoch > epoch);
});
test('Deletion Queue admin rejects late navigation responses and clears a retained API key', async t => {
    const w = environment(t, await source(dq + 'admin.html')), pending = deferred();
    w.ApiClient = { getUrl: path => path, ajax: () => pending.promise };
    w.eval(await source(dq + 'admin.js'));
    const key = w.document.getElementById('dqKey'), page = w.document.getElementById('deletionQueueAdmin'); key.value = 'unsaved-key';
    page.dispatchEvent(new w.Event('viewhide')); assert.equal(key.value, '');
    pending.resolve({ mode: 'stale' }); await turn();
    assert.notEqual(w.document.getElementById('dqMode').textContent, 'stale'); assert.equal(page.getAttribute('aria-busy'), 'false');
});
