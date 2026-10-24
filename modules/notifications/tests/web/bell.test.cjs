const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync('src/AccountHub.Notifications/Web/bell.js', 'utf8');

function harness({ href = 'https://example.test/jellyfin/web/index.html#/home', status = 200, blocked = false, hidden = false, hasMore = false, preferences = [], noticeLink } = {}) {
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
    }
    const body = new Element('body'), header = new Element('header'), anchor = new Element('button');
    body.append(header); header.append(anchor);
    function all(node = body) { return [node, ...node.children.flatMap(c => all(c))]; }
    const document = {
        body, documentElement: body, hidden,
        createElement: tag => new Element(tag),
        querySelector: selector => selector.includes('headerUserButton') ? anchor : all().find(e => e.dataset.accountHubBell),
        addEventListener: (name, handler) => { listeners[name] = handler; }
    };
    const window = {
        ApiClient: { accessToken: () => 'session-secret', getCurrentUserId: () => 'alice', getUrl: p => { if (!p) throw Error('Url name cannot be empty'); return 'https://example.test/jellyfin/' + p; } },
        addEventListener: (name, handler) => { listeners[name] = handler; }
    };
    const context = vm.createContext({ window, document, location: { href },
        MutationObserver: class { observe() {} }, requestAnimationFrame: fn => fn(),
        setTimeout: (fn, ms) => { timers.push({ fn, ms }); return timers.length; }, clearTimeout: () => {},
        fetch: async (url, options) => {
            calls.push({ url, options });
            return { status, ok: status === 200, headers: { get: () => '"tag"' }, json: async () => status !== 200
                ? (blocked ? { twoFactorRequired: true } : {})
                : { ok: true, data: url.endsWith('/preferences') ? { preferences } : url.endsWith('unread-count') ? { count: 1 } : { hasMore, notifications: [
                    { id: 'one', event: { title: '<img src=x onerror=alert(1)>', body: 'plain text', link: noticeLink }, read: false }
                ] } }
            };
        }
    });
    vm.runInContext(source, context);
    return { calls, timers, all, window, listeners, rerun: () => vm.runInContext(source, context), get focused() { return focused; } };
}
const settle = async () => { for (let i = 0; i < 5; i++) await new Promise(setImmediate); };

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
    const control = h.all().find(e => e.tag === 'button' && e.textContent === 'Mute new episodes on in-app');
    assert(control); await control.onclick(); await settle();
    const write = h.calls.find(c => c.options.method === 'PUT'); assert(write);
    assert.deepEqual(JSON.parse(write.options.body), [global, { source: 'account-hub', type: 'library.episode.added', channel: 'in-app', enabled: false }]);
});
test('permission-filtered short pages can still navigate to older notifications', async () => {
    const h = harness({ hasMore: true }); await settle();
    const older = h.all().find(e => e.tag === 'button' && e.textContent === 'Older notifications'); assert.equal(older.disabled, false);
    older.onclick(); await settle(); assert(h.calls.some(c => c.url.includes('skip=100')));
});
test('native item links resolve from the server BaseUrl, not a nested web/web path', async () => {
    const h = harness({ noticeLink: 'web/index.html#/details?id=disposable-item' }); await settle();
    assert.equal(h.all().find(e => e.tag === 'a' && e.textContent === 'Open').href, 'https://example.test/jellyfin/web/index.html#/details?id=disposable-item');
});
