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
    w.HTMLElement.prototype.scrollIntoView = function () { this.dataset.scrolled = 'true'; };
    w.fetch = async (url, options) => {
        const path = new URL(url).pathname; calls.push({ path, url: String(url), ...options });
        const preview = path.endsWith('/preview');
        return { ok: !preview || !failure, status: preview && failure ? 409 : 200, headers: { get: () => null }, json: async () => preview ? failure ? { code: 'SeerrNotConfigured' } : { title: 'Fixture movie', kind: 'Movie', executionAvailable: true } : path.endsWith('/removal') ? data.removal || { canPropose: true, proposals: [] } : path.endsWith('/notices') ? [] : path.endsWith('/integration') ? data.integration || {} : path.endsWith('/proposals') ? data.proposals || [] : data.detail || {} };
    };
    w.eval(script); await tick();
    return { dom, w, d, calls };
}
test('right-click supports itemAction metadata and menu removal without a close event', async () => {
    const f = await fixture(); try {
        f.d.querySelector('.itemAction').dispatchEvent(new f.w.MouseEvent('contextmenu', { bubbles: true }));
        const menu = f.d.createElement('div'); menu.className = 'actionSheet'; menu.getClientRects = () => [{}]; menu.innerHTML = '<div class="actionSheetScroller"></div>'; f.d.body.append(menu); await tick();
        const action = menu.querySelector('.dq-context-action'); assert(action); assert.equal(action.querySelector('.actionSheetItemText').textContent, 'Request removal');
        assert.equal(action.querySelector('.material-icons').textContent, 'delete_outline');
        assert(menu.querySelector('section[aria-label="Deletion Queue"]')); assert.equal(menu.querySelector('.dq-timeline'), null);
        action.click(); menu.remove(); await tick();
        assert.equal(f.d.querySelector('dialog h2').textContent, 'Request removal');
        const preview = f.calls.find(c => c.path.endsWith('/preview')); assert.equal(preview.method, 'POST'); assert.equal(preview.body, '{}');
        assert(!f.calls.some(c => c.path.endsWith('/proposals')));
    } finally { f.dom.window.close(); }
});
test('menus omit non-actionable removal sections and unidentified Requests cards have no removal button', async () => {
    const f = await fixture(false, { removal: { canPropose: false, reason: 'NotYourRequest', proposals: [] } });
    try {
        const card = f.d.createElement('div'); card.className = 'je-request-card'; card.innerHTML = '<div class="je-request-actions"></div>'; f.d.body.append(card); await tick();
        assert.equal(card.querySelector('.dq-request-action'), null);
        f.d.querySelector('.itemAction').dispatchEvent(new f.w.MouseEvent('contextmenu', { bubbles: true }));
        const menu = f.d.createElement('div'); menu.className = 'actionSheet'; menu.getClientRects = () => [{}]; menu.innerHTML = '<div class="actionSheetScroller"></div>'; f.d.body.append(menu); await tick();
        assert.equal(menu.querySelector('.dq-context-section'), null);
    } finally { f.dom.window.close(); }
});
test('episode context removal explicitly previews the parent series, never an episode or season deletion', async () => {
    const series='33333333333333333333333333333333';
    const f=await fixture(false,{removal:{canPropose:true,wholeSeries:true,targetItemId:series,proposals:[]}});
    try {
        f.d.querySelector('.itemAction').dispatchEvent(new f.w.MouseEvent('contextmenu',{bubbles:true}));
        const menu=f.d.createElement('div');menu.className='actionSheet';menu.getClientRects=()=>[{}];menu.innerHTML='<div class="actionSheetScroller"></div>';f.d.body.append(menu);await tick();
        const action=menu.querySelector('.dq-context-action');assert.equal(action.textContent.includes('Request removal of entire series'),true);
        action.click();menu.remove();await tick();
        assert(f.calls.some(c=>c.path.endsWith('/targets/items/'+series+'/preview')));
        assert(!f.calls.some(c=>c.method==='DELETE'||c.path.endsWith('/proposals')));
    }finally{f.dom.window.close();}
});
test('thumbnails stay untouched and queued context menus show timeline and revisioned consent/Undo actions', async () => {
    const f = await fixture(false, { removal: { canPropose: true, proposals: [{ id: 'request', status: 'Waiting', statusLabel: 'Scheduled', revision: 9,
        ownSnapshot: 'current-snapshot', ownAnswer: 1, canUndo: true,
        timeline: [{ key: 'delay', label: 'Safety delay', state: 'current', at: '2026-11-05T15:00:00Z' }] }] } });
    try {
        const card = f.d.createElement('div'); card.className = 'card'; card.dataset.id = '11111111111111111111111111111111'; card.dataset.type = 'Series'; card.innerHTML = '<div class="cardScalable"></div>'; f.d.body.append(card); await tick();
        assert.equal(card.querySelector('.dq-media-action'), null);
        card.dispatchEvent(new f.w.MouseEvent('contextmenu', { bubbles: true }));
        const menu = f.d.createElement('div'); menu.className = 'actionSheet'; menu.getClientRects = () => [{}]; menu.innerHTML = '<div class="actionSheetScroller"><button>Play</button></div>'; f.d.body.append(menu); await tick();
        const section = menu.querySelector('.dq-context-section'); assert(section); assert.equal(section.querySelector('[aria-current=step]').dataset.checkpoint, 'delay');
        const actions = [...section.querySelectorAll('button')];
        assert(actions.every(b => b.querySelector('.listItemIcon.material-icons[aria-hidden=true]')));
        assert(!actions.some(b => b.textContent.includes('Request removal')));
        assert(actions.some(b => b.textContent.includes('Undo removal request')));
        const keep = section.querySelector('[data-answer=keep]'); assert.equal(keep.getAttribute('aria-pressed'), 'true');
        keep.click(); menu.remove(); await tick();
        const write = f.calls.find(c => c.path.endsWith('/consent')); assert(write);
        const body = JSON.parse(write.body); assert.equal(body.revision, 9); assert.equal(body.snapshot, 'current-snapshot'); assert.equal(body.answer, 1);
        assert(!f.calls.some(c => c.path.endsWith('/undo')));
    } finally { f.dom.window.close(); }
});
test('queue is an icon with inline selected actions, a small badge and no Load more button', async () => {
    const f = await fixture(false, { integration: { deviceBell: false }, proposals: [{ id: 'request', title: 'Movie', status: 'Blocked', reason: 'ActivityCoverageIncomplete', ownAnswer: 0, canUndo: true, revision: 2, ownSnapshot: 's', stage: 'Consent' }] });
    try {
        const shortcut = f.d.querySelector('.dq-local-shortcut'); assert(shortcut); assert.equal(shortcut.getAttribute('aria-label'), 'Deletion queue'); assert.equal(shortcut.textContent, 'auto_delete');
        assert.equal(shortcut.nextElementSibling, f.d.querySelector('.headerUserButton'), 'Queue shortcut belongs immediately left of the user button');
        shortcut.click(); await tick(); const modal = f.d.querySelector('dialog');
        assert.equal(modal.querySelector('.dq-status').textContent, 'Blocked'); assert(!modal.textContent.includes('Your removal request')); assert(!modal.textContent.includes('Your answer')); assert(!modal.textContent.includes('Load more'));
        assert.equal(modal.querySelector('[data-answer=allow]').getAttribute('aria-pressed'), 'true');
        assert.equal(modal.querySelector('[data-answer=keep]').getAttribute('aria-pressed'), 'false');
        assert(!modal.textContent.includes('Open full queue'));
    } finally { f.dom.window.close(); }
});
test('approval fork is shown during review while later removal stages stay collapsed', async () => {
    const f = await fixture(false, { integration: { deviceBell: false }, proposals: [{ id: 'review', title: 'Movie', status: 'Blocked', timeline: [
        { key: 'requested', label: 'Requested', state: 'complete', at: '2026-10-01T00:00:00Z' },
        { key: 'checks', label: 'Inactive', state: 'complete', at: '2026-10-01T00:00:00Z' },
        { key: 'approval', label: 'Administrator consent', state: 'current', decision: 'pending', at: '2026-10-31T00:00:00Z' },
        { key: 'delay', label: 'Safety delay', state: 'pending' },
        { key: 'removing', label: 'Removal started', state: 'pending' },
        { key: 'closed', label: 'Completed', state: 'pending' }
    ] }] });
    try {
        f.d.querySelector('.dq-local-shortcut').click(); await tick();
        assert.equal(f.d.querySelector('[data-checkpoint=requested] .dq-checkpoint-label').textContent, 'Check permission');
        assert.equal(f.d.querySelector('[data-checkpoint=checks] .dq-checkpoint-label').textContent, 'Inactive');
        assert.match(f.d.querySelector('.dq-approval-fork').textContent, /Yes.*Safety delay.*Denied.*Cancelled/);
        assert.equal(f.d.querySelector('[data-checkpoint=removing]'), null); assert.equal(f.d.querySelector('[data-checkpoint=closed]'), null);
    } finally { f.dom.window.close(); }
});
test('Now follows the pending stage when only the requested date is known', async () => {
    const f = await fixture(false, { integration: { deviceBell: false }, proposals: [{ id: 'pending', title: 'Movie', status: 'Blocked', timeline: [
        { key: 'requested', label: 'Requested', state: 'complete', at: '2026-10-01T00:00:00Z' },
        { key: 'checks', label: 'Consent and activity', state: 'current' },
        { key: 'delay', label: 'Safety delay', state: 'pending' }
    ] }] });
    try {
        f.d.querySelector('.dq-local-shortcut').click(); await tick();
        const marker = f.d.querySelector('.dq-media-marker'); assert(marker);
        assert.equal(marker.dataset.checkpoint, 'checks'); assert.equal(marker.style.getPropertyValue('--dq-position'), '50%');
        assert.equal(marker.children.length,0); assert.match(marker.getAttribute('aria-label'),/Now/);
    } finally { f.dom.window.close(); }
});
test('timeline scales dated nodes and the media marker without inventing pending dates', async () => {
    const f = await fixture(false, { integration: { deviceBell: false }, proposals: [{ id: 'request', title: 'Series', status: 'Ready', mediaKind: 'Series', evaluatedAt: '2026-10-03T00:00:00Z', activityWindowDays: 30, safetyDelayHours: 96, scheduledFor: '2026-10-05T00:00:00Z', timeline: [
        { key: 'requested', label: 'Requested', state: 'complete', at: '2026-10-01T00:00:00Z' },
        { key: 'checks', label: 'Checks & consent', state: 'complete', at: '2026-10-02T00:00:00Z' },
        { key: 'delay', label: 'Safety delay', state: 'current', at: '2026-10-05T00:00:00Z' },
        { key: 'removing', label: 'Removal started', state: 'pending' }
    ] }] });
    try {
        f.d.querySelector('.dq-local-shortcut').click(); await tick();
        assert.equal(f.d.querySelector('[data-checkpoint=requested]').style.getPropertyValue('--dq-position'), '0%');
        const x = key => parseFloat(f.d.querySelector('[data-checkpoint=' + key + ']').style.getPropertyValue('--dq-position'));
        assert(Math.abs(x('checks') - 100 / 6) < .001); assert(Math.abs(x('delay') - 200 / 3) < .001);
        const marker = f.d.querySelector('.dq-media-marker'); assert(Math.abs(parseFloat(marker.style.getPropertyValue('--dq-position')) - 100 / 3) < .001); assert.equal(marker.children.length,0);
        const stage=f.d.querySelector('[data-checkpoint=delay]'); assert.match(stage.textContent,/In 2 days/);stage.dispatchEvent(new f.dom.window.Event('mouseenter'));assert(stage.querySelector('time').textContent.includes(':'));stage.dispatchEvent(new f.dom.window.Event('mouseleave'));assert.match(stage.textContent,/In 2 days/);
        assert.equal(x('removing'), 100); assert.equal(f.d.querySelector('[data-checkpoint=removing] time'), null);
        assert.equal(f.d.querySelector('.dq-timing'), null); assert(!f.d.querySelector('.dq-timeline time').textContent.includes(':'));
    } finally { f.dom.window.close(); }
});
test('History marks the real cancellation time relative to the abandoned deletion date', async () => {
    const f = await fixture(false, { integration: { deviceBell: false }, proposals: [{ id: 'cancelled', title: 'Cancelled movie', status: 'Cancelled', closedCheckpoint: 'delay', closureExplanation: 'This removal request was withdrawn using Undo.', timeline: [
        { key: 'requested', label: 'Requested', state: 'complete', at: '2026-10-01T00:00:00Z' },
        { key: 'delay', label: 'Safety delay', state: 'cancelled', at: '2026-10-05T00:00:00Z', dateLabel: 'Abandoned earliest date' },
        { key: 'closed', label: 'Cancelled', state: 'complete', at: '2026-10-02T00:00:00Z' }
    ] }] });
    try {
        f.d.querySelector('.dq-local-shortcut').click(); await tick();
        assert.equal(f.d.querySelector('.dq-media-marker').style.getPropertyValue('--dq-position'), '100%');
        assert.equal(f.d.querySelector('.dq-marker-label'),null);
        assert.equal(f.d.querySelector('.dq-closure'),null);assert.match(f.d.querySelector('.dq-help-text').textContent,/Undo/);
        assert.match(f.d.querySelector('[data-checkpoint=delay]').title,/Abandoned earliest date/);
        assert.equal(f.d.querySelector('[data-checkpoint=closed]').dataset.state,'cancelled');assert(!f.d.body.textContent.includes('In progress'));
    } finally { f.dom.window.close(); }
});
test('Calendar merges dated removals into native events with a Deletion Date filter and no sidebar queue', async () => {
    const f = await fixture(false, { proposals: [{ id: 'queued', title: 'Scheduled movie', status: 'Waiting', scheduledFor: '2026-11-05T00:00:00Z' }, { id: 'blocked', title: 'Not scheduled', status: 'Blocked' }] });
    try {
        const calendar = f.d.createElement('div'); calendar.className = 'je-calendar-layout'; f.d.body.append(calendar);
        const state = { events: [{ id: 'native', title: 'Native event' }], activeFilters: new f.w.Set() };
        const renderPage = () => {
            calendar.replaceChildren(); const legend = f.d.createElement('div'); legend.className = 'je-calendar-legend'; calendar.append(legend);
            for (const e of state.events.filter(e => !state.activeFilters.size || state.activeFilters.has(e.releaseType))) {
                const item = f.d.createElement('div'); item.dataset.eventId = e.id; item.textContent = e.title; calendar.append(item);
            }
        };
        f.w.JellyfinEnhanced = { internals: { calendarPage: { state } }, calendarPage: { renderPage, toggleFilter: key => { state.activeFilters.has(key) ? state.activeFilters.delete(key) : state.activeFilters.add(key); renderPage(); } } };
        renderPage(); await tick(); await tick();
        assert.equal(state.events.length, 2); assert(state.events.some(e => e.id === 'native'));
        assert.equal(calendar.querySelectorAll('.dq-calendar-event').length, 1); assert.equal(calendar.querySelector('.dq-calendar-removals'), null);
        calendar.querySelector('.dq-calendar-filter').click(); await tick();
        assert(state.activeFilters.has('DeletionDate')); assert.equal(calendar.querySelector('[data-event-id=native]'), null);
        calendar.querySelector('.dq-calendar-event').click(); await tick(); assert.equal(f.d.querySelector('dialog h2').textContent, 'Deletion queue');
    } finally { f.dom.window.close(); }
});
test('status help supports hover, focus, tap and Escape without exposing raw block reasons', async () => {
    const f = await fixture(false, { integration: { deviceBell: false }, proposals: [{ id: 'request', title: 'Movie', status: 'Blocked', reason: 'private upstream /user/path', revision: 1 }] });
    try {
        f.d.querySelector('.dq-local-shortcut').click(); await tick();
        const help = f.d.querySelector('.dq-help'), control = help.querySelector('button'), tip = help.querySelector('[role=tooltip]');
        assert(tip.hidden); help.dispatchEvent(new f.w.MouseEvent('mouseenter')); assert(!tip.hidden);
        assert.match(tip.textContent, /safety checks/); assert(!f.d.querySelector('dialog').textContent.includes('private upstream'));
        help.dispatchEvent(new f.w.MouseEvent('mouseleave')); assert(tip.hidden);
        control.focus(); assert(!tip.hidden);
        control.dispatchEvent(new f.w.KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })); assert(tip.hidden); assert(f.d.querySelector('dialog').open);
        control.click(); assert(!tip.hidden); assert.equal(control.getAttribute('aria-expanded'), 'true');
    } finally { f.dom.window.close(); }
});
test('queue modals close on a genuine backdrop click, not an inside-to-outside drag, and never submit', async () => {
    const f = await fixture(false, { integration: { deviceBell: false } });
    try {
        f.d.querySelector('.dq-local-shortcut').click(); await tick(); const dialog = f.d.querySelector('dialog');
        dialog.querySelector('h2').dispatchEvent(new f.w.MouseEvent('pointerdown', { bubbles: true }));
        dialog.dispatchEvent(new f.w.MouseEvent('click', { clientX: 20, clientY: 20, bubbles: true })); assert(dialog.isConnected);
        dialog.dispatchEvent(new f.w.MouseEvent('pointerdown', { clientX: 20, clientY: 20, bubbles: true }));
        dialog.dispatchEvent(new f.w.MouseEvent('click', { clientX: 20, clientY: 20, bubbles: true })); assert(!dialog.isConnected);
        assert(!f.calls.some(call => call.method !== 'GET'));
    } finally { f.dom.window.close(); }
});
test('review links open the queue without navigating and pin, highlight and scroll an older History request', async () => {
    const id = '33333333-3333-3333-3333-333333333333';
    const f = await fixture(false, { detail: { id, title: 'Selected old request', status: 'Expired', reason: 'RequestExpired', timeline: [
        { key: 'requested', label: 'Requested', state: 'complete', at: '2026-10-01T12:00:00Z' },
        { key: 'closed', label: 'Expired', state: 'current', at: '2026-10-31T12:00:00Z' }
    ] }, proposals: [{ id: '44444444-4444-4444-4444-444444444444', title: 'Other request', status: 'Cancelled' }] });
    try {
        const link = f.d.createElement('a'); link.href = '#/home?deletionQueueProposal=' + id; link.textContent = 'Review request'; f.d.body.append(link);
        const location = f.w.location.href; link.click(); await tick(); await tick();
        assert.equal(f.w.location.href, location); assert.equal(f.d.querySelector('dialog h2').textContent, 'Deletion queue');
        const selected = f.d.querySelector('.dq-highlight'); assert(selected); assert.equal(selected.dataset.requestId, id); assert.equal(selected.dataset.scrolled, 'true'); assert.equal(f.d.activeElement, selected);
        assert.equal(f.d.querySelectorAll('[data-request-id="' + id + '"]').length, 1); assert(f.calls.some(call => call.url.includes('view=history')));
        assert.equal(selected.querySelectorAll('.dq-timeline time').length, 2); assert.equal(selected.querySelector('[aria-current=step]').dataset.checkpoint, 'closed');
    } finally { f.dom.window.close(); }
});
test('scheduled rows show a qualified removal date and milestone dates below their actions', async () => {
    const scheduledFor = '2026-11-05T15:00:00Z';
    const f = await fixture(false, { integration: { deviceBell: false }, proposals: [{ id: 'request', title: 'Movie', status: 'Waiting', statusLabel: 'Scheduled', scheduledFor, timeline: [
        { key: 'requested', label: 'Requested', state: 'complete', at: '2026-11-01T15:00:00Z' },
        { key: 'delay', label: 'Safety delay', state: 'current', at: scheduledFor },
        { key: 'confirmed', label: 'Removal confirmed', state: 'pending' }
    ] }] });
    try {
        f.d.querySelector('.dq-local-shortcut').click(); await tick(); const row = f.d.querySelector('.dq-queue-row');
        assert.match(row.querySelector('.dq-schedule').textContent, /Earliest removal in .* days/);
        assert.equal(row.lastElementChild.className, 'dq-timeline'); assert.equal(row.querySelector('[data-checkpoint=delay] time').dateTime, '2026-11-05T15:00:00.000Z');
        assert.equal(row.querySelector('[data-checkpoint=confirmed]'), null);
        const reads = f.calls.filter(call => call.path.endsWith('/proposals')).length;
        const now = f.w.Date.now(); f.w.Date.now = () => now + 6000;
        row.querySelector('.dq-timeline time').dispatchEvent(new f.w.MouseEvent('pointerdown', { bubbles: true })); await tick();
        assert(row.isConnected); assert.equal(f.calls.filter(call => call.path.endsWith('/proposals')).length, reads, 'Scrolling the timeline must not replace the active row');
    } finally { f.dom.window.close(); }
});
test('Enhanced Requests mounts a full deletion tab with history and does not add a Calendar queue list', async () => {
    const f = await fixture(false, { proposals: [{ id: 'old', title: 'Past request', status: 'Expired', reason: 'RequestExpired', canUndo: false, stage: 'History' }] });
    try {
        const page = f.d.createElement('div'); page.innerHTML = '<section class="je-requests-section"><h2>Requests</h2><div class="je-requests-tabs"><button>All</button></div><div class="je-requests-grid">Seerr requests</div></section><div class="je-calendar-layout"><aside class="je-calendar-sidebar-content"></aside></div>'; f.d.body.append(page); await tick();
        const tab = page.querySelector('.dq-deletion-tab'); assert(tab); tab.click(); await tick();
        assert(page.querySelector('.dq-deletion-selected')); const history = [...page.querySelectorAll('.dq-enhanced-view [role=tab]')].find(tab => tab.textContent === 'History'); history.click(); await tick();
        assert(f.calls.some(call => call.url.includes('view=history'))); assert(page.querySelector('.dq-enhanced-view').textContent.includes('Past request'));
        assert.equal(page.querySelector('.dq-calendar-removals'), null);
    } finally { f.dom.window.close(); }
});
test('the full queue shortcut is exposed only by the enabled Enhanced integration', async () => {
    const f = await fixture(false, { integration: { deviceBell: false, enhancedRequestsEnabled: true } });
    try {
        let opened = false;
        f.w.JellyfinEnhanced = { downloadsPage: { showPage: () => {
            opened = true; const page = f.d.createElement('section'); page.className = 'je-requests-section'; page.innerHTML = '<h2>Requests</h2><div class="je-requests-tabs"><button>All</button></div>'; f.d.body.append(page);
        } } };
        f.d.querySelector('.dq-local-shortcut').click(); await tick(); assert.equal(f.d.querySelector('.dq-open-full'), null);
        const popout = f.d.querySelector('[aria-label="Open Deletion Queue in Requests"]'); assert(popout); assert.equal(popout.querySelector('.material-icons').textContent, 'open_in_new');
        popout.click(); await tick(); assert(opened); assert(f.d.querySelector('.dq-deletion-selected')); assert(!f.d.querySelector('dialog'));
    }
    finally { f.dom.window.close(); }
});
test('queued detail actions encode stage and open the existing request instead of proposing again', async () => {
    const p = { id: '33333333-3333-3333-3333-333333333333', title: 'Queued series', status: 'Waiting', statusLabel: 'Scheduled', revision: 2, timeline: [{ key: 'delay', label: 'Safety delay', state: 'current', at: '2026-11-05T00:00:00Z' }] };
    const f = await fixture(false, { removal: { canPropose: true, proposals: [p] }, detail: p, proposals: [p] });
    try {
        f.w.history.replaceState(null, '', '#/details?id=11111111111111111111111111111111');
        const host = f.d.createElement('div'); host.innerHTML = '<div class="mainDetailButtons"></div>'; f.d.body.append(host); await tick(); await tick();
        const action = host.querySelector('.dq-media-action'); assert.equal(action.dataset.removalStatus, 'Waiting'); assert.equal(action.querySelector('.material-icons').textContent, 'hourglass_top');
        assert.equal(action.getAttribute('aria-label'), 'Removal · Scheduled'); assert(host.querySelector('.dq-item-progress .dq-timeline'));
        action.click(); await tick(); await tick(); assert(f.d.querySelector('.dq-highlight'));
        assert(!f.calls.some(c => c.path.endsWith('/preview')));
    } finally { f.dom.window.close(); }
});
test('popout focuses the selected request in an already-mounted Requests deletion view', async () => {
    const id = '33333333-3333-3333-3333-333333333333', p = { id, title: 'Selected request', status: 'Waiting', timeline: [] };
    const f = await fixture(false, { integration: { deviceBell: false, enhancedRequestsEnabled: true }, detail: p, proposals: [p] });
    try {
        const section = f.d.createElement('section'); section.className = 'je-requests-section'; section.innerHTML = '<h2>Requests</h2><div class="je-requests-tabs"><button>All</button></div>'; f.d.body.append(section); await tick();
        f.w.JellyfinEnhanced = { downloadsPage: { showPage: () => {} } };
        const link = f.d.createElement('a'); link.href = '#/home?deletionQueueProposal=' + id; f.d.body.append(link); link.click(); await tick(); await tick();
        f.d.querySelector('[aria-label="Open Deletion Queue in Requests"]').click(); await tick(); await tick();
        assert(!f.d.querySelector('dialog')); assert.equal(section.querySelector('.dq-highlight').dataset.requestId, id); assert.equal(section.querySelector('.dq-highlight').dataset.scrolled, 'true');
    } finally { f.dom.window.close(); }
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
