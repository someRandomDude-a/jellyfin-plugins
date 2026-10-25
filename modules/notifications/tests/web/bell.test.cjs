const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync('src/AccountHub.Notifications/Web/bell.js', 'utf8');

function harness({ href = 'https://example.test/jellyfin/web/index.html#/home', status = 200, blocked = false, hidden = false, hasMore = false, preferences = [], noticeLink, inbox, read = false } = {}) {
    const listeners = {}, calls = [], timers = [];
    let focused;
    class Element {
        constructor(tag) { this.tag = tag; this.children = []; this.dataset = {}; this.style = {}; this.attrs = {}; this.hidden = false; }
        set textContent(text) { this.text = text; this.children = []; }
        get textContent() { return (this.text || '') + this.children.map(c => c.textContent).join(''); }
        set innerHTML(_) { throw Error('HTML insertion is forbidden'); }
        append(...nodes) { for (const node of nodes) { node.parentElement = this; this.children.push(node); } }
        replaceChildren(...nodes) { for (const c of this.children) c.parentElement = null; this.children = []; this.text = ''; this.append(...nodes); }
        insertBefore(node, reference) { node.parentElement = this; this.children.splice(this.children.indexOf(reference), 0, node); }
        remove() { if (this.parentElement) this.parentElement.children = this.parentElement.children.filter(c => c !== this); this.parentElement = null; }
        get isConnected() { return this === body || this === header || !!this.parentElement?.isConnected; }
        setAttribute(k, v) { this.attrs[k] = v; }
        addEventListener(k, v) { (this.events ??= {})[k] = v; }
        focus() { focused = this; }
        getClientRects() { return [{}]; }
        getBoundingClientRect() { return { bottom: 50, right: 900 }; }
        closest() { return null; }
        contains(target) { return this === target || this.children.some(c => c.contains(target)); }
    }
    const body = new Element('body'), header = new Element('header'), anchor = new Element('button');
    body.append(header); header.append(anchor);
    function all(node = body) { return [node, ...node.children.flatMap(c => all(c))]; }
    const document = {
        body, documentElement: body, hidden,
        createElement: tag => new Element(tag),
        querySelector: selector => selector.includes('headerUserButton') ? anchor : all().find(e => e.dataset.accountHubBell),
        querySelectorAll: selector => selector.includes('headerUserButton') ? [anchor] : [],
        addEventListener: (name, handler) => { listeners[name] = handler; }
    };
    const window = {
        ApiClient: { accessToken: () => 'session-secret', getCurrentUserId: () => 'alice', getUrl: p => { if (!p) throw Error('Url name cannot be empty'); return 'https://example.test/jellyfin/' + p; } },
        addEventListener: (name, handler) => { listeners[name] = handler; }
    };
    const context = vm.createContext({ window, document, location: { href }, innerWidth: 1024, innerHeight: 768,
        MutationObserver: class { observe() {} }, requestAnimationFrame: fn => fn(),
        setTimeout: (fn, ms) => { timers.push({ fn, ms }); return timers.length; }, clearTimeout: () => {},
        fetch: async (url, options) => {
            calls.push({ url, options });
            return { status, ok: status === 200, headers: { get: () => '"tag"' }, json: async () => status !== 200
                ? (blocked ? { twoFactorRequired: true } : {})
                : { ok: true, data: url.endsWith('/preferences') ? { preferences } : url.endsWith('unread-count') ? { count: 1 } : inbox ? inbox(Number(new URL(url).searchParams.get('skip') || 0)) : { hasMore, notifications: [
                    { id: 'one', event: { title: '<img src=x onerror=alert(1)>', body: 'plain text', link: noticeLink }, read }
                ] } }
            };
        }
    });
    vm.runInContext(source, context);
    return { calls, timers, all, window, listeners, rerun: () => vm.runInContext(source, context), get focused() { return focused; } };
}
const settle = async () => { for (let i = 0; i < 5; i++) await new Promise(setImmediate); };
test('routine UI feedback stays in the closed inbox and rejects another user', async () => {
    const h = harness(); await settle();
    const panel = h.all().find(e => e.id === 'account-hub-inbox');
    h.listeners['suite-notification-message']({ detail: { userId: 'bob', title: 'Other user', body: 'private' } });
    assert(!h.all().some(e => e.textContent === 'Other user'));
    h.listeners['suite-notification-message']({ detail: { userId: 'alice', title: 'Connection issue', body: 'Try again later.' } });
    assert.equal(panel.hidden, true); assert(h.all().some(e => e.tag === 'h3' && e.textContent === 'Connection issue'));
    assert(h.all().some(e => e.tag === 'details')); assert(h.all().some(e => e.dataset.accountHubBell && e.attrs['aria-label'] === '2 unread notifications'));
});
test('server read state removes redundant read actions and unread highlighting', async () => {
    const h = harness({ read: true }); await settle();
    assert(!h.all().some(e => e.tag === 'button' && e.textContent === 'Mark read'));
    assert(h.all().some(e => e.tag === 'section' && e.dataset.unread === 'false'));
});
test('groups by importance and age, shows exact and relative time, and keeps source links on the notification', async () => {
    const now = Date.now();
    const entry = (id, age, severity = 'info', adminOnly = false) => ({ id, read: false, event: { title: id, body: 'Summary', source: 'deletion-queue', type: 'proposal.updated', link: '#/details?id=media', occurredAt: new Date(now - age).toISOString(), severity, adminOnly } });
    const h = harness({ inbox: () => ({ hasMore: false, notifications: [entry('Routine', 2 * 3600000), entry('Priority', 3600000, 'high'), entry('Old', 10 * 86400000), entry('Administrator', 1000, 'info', true)] }) }); await settle();
    for (const name of ['Important', 'Other', 'Older']) assert(h.all().some(e => e.tag === 'h2' && e.textContent === name));
    assert(h.all().some(e => e.tag === 'time' && /hours? ago/.test(e.textContent) && e.title && e.attrs.datetime));
    assert(h.all().some(e => e.tag === 'a' && e.className === 'notificationMain' && e.href === '#/details?id=media'));
    assert(h.all().some(e => e.tag === 'summary' && e.attrs['aria-label'] === 'Notification options'));
    assert(h.all().some(e => e.tag === 'button' && e.textContent === 'Hide notification'));
});
test('source mute only changes the current source in-app preference', async () => {
    const global = { source: '*', type: '*', channel: 'discord', enabled: true };
    const h = harness({ preferences: [global], inbox: () => ({ hasMore: false, notifications: [{ id: 'request', event: { title: 'Request', body: 'Review', source: 'deletion-queue', type: 'proposal.updated', severity: 'info' } }] }) }); await settle();
    await h.all().find(e => e.tag === 'button' && e.textContent === 'Mute removal notifications').onclick();
    const saved = h.calls.find(c => c.options.method === 'PUT');
    assert(saved.url.endsWith('/preferences/rule'));
    assert.deepEqual(JSON.parse(saved.options.body), { source: 'deletion-queue', type: '*', channel: 'in-app', enabled: false });
});
test('settings show current choices and can restore a previously muted source', async () => {
    const h = harness({ preferences: [{ source: 'deletion-queue', type: '*', channel: 'in-app', enabled: false }] }); await settle();
    h.all().find(e => e.attrs['aria-label'] === 'Notification settings').onclick(); await settle();
    assert(h.all().some(e => e.tag === 'button' && e.attrs['aria-label'] === 'Enable non-critical in-app notifications' && e.attrs['aria-pressed'] === 'true'));
    const enable = h.all().find(e => e.attrs['aria-label'] === 'Unmute deletion-queue · * · in-app'); assert(enable); await enable.onclick(); await settle();
    const saved = h.calls.find(call => call.options.method === 'PUT');
    assert.deepEqual(JSON.parse(saved.options.body), { source: 'deletion-queue', type: '*', channel: 'in-app', enabled: true });
});
test('silent inbox refresh does not rebuild an active preference editor', async () => {
    const h = harness(); await settle(); h.all().find(e => e.attrs['aria-label'] === 'Notification settings').onclick(); await settle();
    const before = h.all().find(e => e.attrs['aria-label'] === 'Enable non-critical in-app notifications');
    const reads = h.calls.filter(call => call.url.endsWith('/preferences')).length;
    h.listeners.focus(); await settle();
    assert(h.all().includes(before)); assert.equal(h.calls.filter(call => call.url.endsWith('/preferences')).length, reads);
});

test('singleton mounts one bell, uses header auth, and renders hostile titles as text', async () => {
    const h = harness(); await settle(); h.rerun(); await settle();
    assert.equal(h.all().filter(e => e.dataset.accountHubBell).length, 1);
    assert.equal(h.calls.length, 2);
    assert.equal(h.calls[0].options.credentials, 'omit');
    assert.match(h.calls[0].options.headers.Authorization, /session-secret/);
    assert(h.all().some(e => e.tag === 'h3' && e.textContent === '<img src=x onerror=alert(1)>'));
    const button = h.all().find(e => e.dataset.accountHubBell), panel = h.all().find(e => e.id === 'account-hub-inbox');
    button.onclick(); assert.equal(panel.hidden, false); assert.equal(h.focused, panel);
    panel.events.keydown({ key: 'Escape', preventDefault() {} }); assert.equal(panel.hidden, true); assert.equal(h.focused, button);
});
test('marked 403 pauses polling, clears inbox and links to 2FA without rewriting status', async () => {
    const h = harness({ status: 403, blocked: true }); await settle();
    assert.equal(h.calls.length, 1); assert.equal(h.timers.length, 0);
    assert(h.all().some(e => e.tag === 'a' && e.href.endsWith('/TwoFactorAuth/Login')));
    assert(!h.all().some(e => e.tag === 'h3'));
});
test('unmarked 401 does not become a 2FA challenge', async () => {
    const h = harness({ status: 401 }); await settle();
    assert(!h.all().some(e => e.textContent.includes('Complete 2FA'))); assert.equal(h.timers.length, 0);
});
test('server errors back off instead of tight polling', async () => {
    const h = harness({ status: 503 }); await settle(); assert.equal(h.timers[0].ms, 60000);
});
for (const page of ['#/login', '#/challenge', '/TwoFactorAuth/Setup', '/AccountHub/invite#secret', '/AccountHub/reset#secret', '/AccountHub/linktoken#secret']) {
    test('does not run on secret/auth page ' + page, async () => {
        const h = harness({ href: 'https://example.test/' + page }); await settle();
        assert.equal(h.calls.length, 0); assert.equal(h.all().filter(e => e.dataset.accountHubBell).length, 0);
    });
}
test('hidden pages do not poll', async () => { const h = harness({ hidden: true }); await settle(); assert.equal(h.calls.length, 0); });
test('episode-specific preference saves do not replace unrelated or global choices', async () => {
    const global = { source: '*', type: '*', channel: 'in-app', enabled: true };
    const h = harness({ preferences: [global] }); await settle();
    h.all().find(e => e.attrs['aria-label'] === 'Notification settings').onclick();
    await settle();
    const control = h.all().find(e => e.tag === 'button' && e.attrs['aria-label'] === 'Mute new episodes on in-app');
    assert(control); await control.onclick(); await settle();
    const write = h.calls.find(c => c.options.method === 'PUT'); assert(write);
    assert(write.url.endsWith('/preferences/rule'));
    assert.deepEqual(JSON.parse(write.options.body), { source: 'account-hub', type: 'library.episode.added', channel: 'in-app', enabled: false });
});
test('opening refreshes silently, outside clicks dismiss and settings stay in the same panel', async () => {
    const h = harness(); await settle();
    const bell = h.all().find(e => e.dataset.accountHubBell);
    const panel = h.all().find(e => e.id === 'account-hub-inbox');
    bell.onclick(); await settle();
    assert(h.calls.length >= 4);
    assert(!h.all().some(e => e.tag === 'button' && e.textContent === 'Refresh'));
    h.all().find(e => e.attrs['aria-label'] === 'Notification settings').onclick();
    assert(h.all().some(e => e.tag === 'h2' && e.textContent === 'Notification settings'));
    h.listeners.pointerdown({ target: {} });
    assert.equal(panel.hidden, true);
});
test('permission-filtered short pages can still navigate to older notifications', async () => {
    const h = harness({ hasMore: true }); await settle();
    const older = h.all().find(e => e.tag === 'button' && e.textContent === 'Older notifications'); assert.equal(older.disabled, false);
    older.onclick(); await settle(); assert(h.calls.some(c => c.url.includes('skip=100')));
});
test('incremental pages append without duplicates and refresh removes revoked older rows', async () => {
    let revoked = false;
    const first = { id: 'first', event: { title: 'First', body: 'Summary', details: 'More information' } };
    const older = { id: 'older', event: { title: 'Admin-only older row', body: 'Private' } };
    const h = harness({ inbox: skip => ({ notifications: skip === 0 ? [first] : revoked ? [] : [first, older], hasMore: skip === 0 }) }); await settle();
    h.all().find(e => e.tag === 'button' && e.textContent === 'Older notifications').onclick(); await settle();
    assert.equal(h.all().filter(e => e.tag === 'h3' && e.textContent === 'First').length, 1);
    assert(h.all().some(e => e.tag === 'h3' && e.textContent === 'Admin-only older row'));
    revoked = true; h.listeners.focus(); await settle();
    assert(!h.all().some(e => e.tag === 'h3' && e.textContent === 'Admin-only older row'));
});
test('native item links resolve from the server BaseUrl, not a nested web/web path', async () => {
    const h = harness({ noticeLink: 'web/index.html#/details?id=disposable-item' }); await settle();
    assert.equal(h.all().find(e => e.tag === 'a' && e.className === 'notificationMain').href, 'https://example.test/jellyfin/web/index.html#/details?id=disposable-item');
});
