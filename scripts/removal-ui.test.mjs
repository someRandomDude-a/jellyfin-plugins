import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
const require = createRequire(new URL('../plugins/private-syncplay/package.json', import.meta.url));
const { JSDOM } = require('jsdom');
const script = await readFile(new URL('../plugins/deletion-queue/src/Jellyfin.Plugin.DeletionQueue/Web/integrated.js', import.meta.url), 'utf8');
const tick = () => new Promise(resolve => setTimeout(resolve, 45));
async function fixture(failure = false, data = {}) {
    const dom = new JSDOM('<script id="dq-web-client" src="https://test.invalid/suite/DeletionQueue/assets/integrated.js"></script><div><button class="headerUserButton">User</button></div><div class="itemAction" data-id="11111111111111111111111111111111">Media</div>', { runScripts: 'outside-only', pretendToBeVisual: true, url: 'https://test.invalid/suite/web/index.html#/home' });
    const w = dom.window, d = w.document, calls = [];
    d.querySelector('.headerUserButton').getClientRects = () => [{}];
    w.ApiClient = { serverAddress: () => 'https://test.invalid/suite', accessToken: () => 'fixture', getCurrentUserId: () => '22222222222222222222222222222222' };
    w.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    w.HTMLDialogElement.prototype.close = function () { this.open = false; this.dispatchEvent(new w.Event('close')); };
    w.fetch = async (url, options) => {
        const path = new URL(url).pathname; calls.push({ path, url: String(url), ...options });
        const preview = path.endsWith('/preview');
        return { ok: !preview || !failure, status: preview && failure ? 409 : 200, headers: { get: () => null }, json: async () => preview ? failure ? { code: 'SeerrNotConfigured' } : { title: 'Fixture movie', kind: 'Movie', executionAvailable: true } : path.endsWith('/notices') ? [] : path.endsWith('/integration') ? data.integration || {} : path.endsWith('/proposals') ? data.proposals || [] : data.detail || {} };
    };
    w.eval(script); await tick();
    return { dom, w, d, calls };
}
test('right-click supports itemAction metadata and menu removal without a close event', async () => {
    const f = await fixture(); try {
        f.d.querySelector('.itemAction').dispatchEvent(new f.w.MouseEvent('contextmenu', { bubbles: true }));
        const menu = f.d.createElement('div'); menu.className = 'actionSheet'; menu.getClientRects = () => [{}]; menu.innerHTML = '<div class="actionSheetScroller"></div>'; f.d.body.append(menu); await tick();
        const action = menu.querySelector('.dq-context-action'); assert(action); assert.equal(action.textContent, 'Request removal');
        action.click(); menu.remove(); await tick();
        assert.equal(f.d.querySelector('dialog h2').textContent, 'Request removal');
        const preview = f.calls.find(c => c.path.endsWith('/preview')); assert.equal(preview.method, 'POST'); assert.equal(preview.body, '{}');
        assert(!f.calls.some(c => c.path.endsWith('/proposals')));
    } finally { f.dom.window.close(); }
});
test('queue is an icon with inline selected actions, a small badge and no Load more button', async () => {
    const f = await fixture(false, { integration: { deviceBell: false }, proposals: [{ id: 'request', title: 'Movie', status: 'Blocked', reason: 'ActivityCoverageIncomplete', ownAnswer: 0, canUndo: true, revision: 2, ownSnapshot: 's', stage: 'Consent' }] });
    try {
        const shortcut = f.d.querySelector('.dq-local-shortcut'); assert(shortcut); assert.equal(shortcut.getAttribute('aria-label'), 'Deletion queue'); assert.equal(shortcut.textContent, 'auto_delete');
        shortcut.click(); await tick(); const modal = f.d.querySelector('dialog');
        assert.equal(modal.querySelector('.dq-status').textContent, 'Blocked'); assert(!modal.textContent.includes('Your removal request')); assert(!modal.textContent.includes('Your answer')); assert(!modal.textContent.includes('Load more'));
        assert.equal(modal.querySelector('[data-answer=allow]').getAttribute('aria-pressed'), 'true');
        assert.equal(modal.querySelector('[data-answer=keep]').getAttribute('aria-pressed'), 'false');
        assert(!modal.textContent.includes('Open full queue'));
    } finally { f.dom.window.close(); }
});
test('Enhanced Requests mounts a full deletion tab with history and Calendar adds a removal agenda', async () => {
    const f = await fixture(false, { proposals: [{ id: 'old', title: 'Past request', status: 'Expired', reason: 'RequestExpired', canUndo: false, stage: 'History' }] });
    try {
        const page = f.d.createElement('div'); page.innerHTML = '<section class="je-requests-section"><h2>Requests</h2><div class="je-requests-tabs"><button>All</button></div><div class="je-requests-grid">Seerr requests</div></section><div class="je-calendar-layout"><aside class="je-calendar-sidebar-content"></aside></div>'; f.d.body.append(page); await tick();
        const tab = page.querySelector('.dq-deletion-tab'); assert(tab); tab.click(); await tick();
        assert(page.querySelector('.dq-deletion-selected')); const history = [...page.querySelectorAll('.dq-enhanced-view [role=tab]')].find(tab => tab.textContent === 'History'); history.click(); await tick();
        assert(f.calls.some(call => call.url.includes('view=history'))); assert(page.querySelector('.dq-enhanced-view').textContent.includes('Past request'));
        assert(page.querySelector('.dq-calendar-removals')); assert(!page.querySelector('.dq-calendar-removals').textContent.includes('undefined'));
    } finally { f.dom.window.close(); }
});
test('the full queue shortcut is exposed only by the enabled Enhanced integration', async () => {
    const f = await fixture(false, { integration: { deviceBell: false, enhancedRequestsEnabled: true } });
    try {
        let opened = false;
        f.w.JellyfinEnhanced = { downloadsPage: { showPage: () => {
            opened = true; const page = f.d.createElement('section'); page.className = 'je-requests-section'; page.innerHTML = '<h2>Requests</h2><div class="je-requests-tabs"><button>All</button></div>'; f.d.body.append(page);
        } } };
        f.d.querySelector('.dq-local-shortcut').click(); await tick(); assert(f.d.querySelector('.dq-open-full'));
        f.d.querySelector('.dq-open-full').click(); await tick(); assert(opened); assert(f.d.querySelector('.dq-deletion-selected')); assert(!f.d.querySelector('dialog'));
    }
    finally { f.dom.window.close(); }
});
test('preview errors replace loading text and dismiss outside without a Close button', async () => {
    const f = await fixture(true); try {
        f.d.querySelector('.itemAction').dispatchEvent(new f.w.MouseEvent('contextmenu', { bubbles: true }));
        const menu = f.d.createElement('div'); menu.className = 'actionSheet'; menu.getClientRects = () => [{}]; menu.innerHTML = '<div class="actionSheetScroller"></div>'; f.d.body.append(menu); await tick();
        menu.querySelector('.dq-context-action').click(); menu.remove(); await tick();
        const dialog = f.d.querySelector('dialog'); assert.match(dialog.textContent, /configure Seerr/); assert(!dialog.textContent.includes('Checking media'));
        assert.equal(dialog.querySelector('.dq-close'), null); assert.match(dialog.textContent, /Try again/);
        dialog.dispatchEvent(new f.w.MouseEvent('pointerdown', { clientX: 10, clientY: 10, bubbles: true }));
        dialog.dispatchEvent(new f.w.MouseEvent('click', { clientX: 10, clientY: 10, bubbles: true }));
        assert(!dialog.isConnected);
    } finally { f.dom.window.close(); }
});
test('a reused hidden native dialog gets the menu item and dismisses before opening the preview', async () => {
    const f = await fixture(); try {
        const menu = f.d.createElement('dialog'); menu.className = 'actionSheet'; menu.innerHTML = '<div class="actionSheetScroller"></div>';
        // Native/custom CSS can give even a closed dialog a layout box.
        menu.getClientRects = () => [{}]; f.d.body.append(menu); await tick();
        f.d.querySelector('.itemAction').dispatchEvent(new f.w.MouseEvent('contextmenu', { bubbles: true })); menu.showModal(); await tick();
        assert(menu.querySelector('.dq-context-action')); menu.querySelector('.dq-context-action').click(); await tick();
        assert(!menu.open); assert.equal(f.d.querySelector('dialog.dq-panel h2').textContent, 'Request removal');
    } finally { f.dom.window.close(); }
});
