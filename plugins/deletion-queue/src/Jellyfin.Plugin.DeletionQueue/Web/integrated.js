(function () {
    'use strict';
    if (window.__deletionQueueWeb) return;
    window.__deletionQueueWeb = true;
    const script = document.getElementById('dq-web-client');
    if (!script) return;
    const root = new URL('../', script.src);
    const serverRoot = new URL('../', root).href.replace(/\/$/, '');
    const get = (o, name) => o?.[name] ?? o?.[name[0].toUpperCase() + name.slice(1)];
    const guid = value => /^[\da-f]{32}$|^[\da-f]{8}(-[\da-f]{4}){3}-[\da-f]{12}$/i.test(value || '') ? value : null;
    const command = () => {
        const b = crypto.getRandomValues(new Uint8Array(16)); b[6] = (b[6] & 15) | 64; b[8] = (b[8] & 63) | 128;
        const s = [...b].map(v => v.toString(16).padStart(2, '0')).join('');
        return s.slice(0, 8) + '-' + s.slice(8, 12) + '-' + s.slice(12, 16) + '-' + s.slice(16, 20) + '-' + s.slice(20);
    };
    let sessionKey = '', epoch = 0, frame = 0, notices = [], noticeProblem = '', context = null, pollingEpoch = -1, lastPoll = 0;
    const requests = new Set();
    const capabilities = new Map();
    let twoFactorPending = false, integration = { deviceBell: true }, linkedProposal = '';
    function session() {
        try {
            const client = window.ApiClient;
            if (!client || new URL(client.serverAddress(), location.href).href.replace(/\/$/, '') !== serverRoot) return null;
            const token = client.accessToken(), user = client.getCurrentUserId();
            return token && guid(user) ? { token, user, key: token + '|' + user } : null;
        } catch { return null; }
    }
    function node(tag, text, cls) {
        const n = document.createElement(tag); if (text !== undefined) n.textContent = text; if (cls) n.className = cls; return n;
    }
    function resetPrivate() {
        epoch++; notices = []; noticeProblem = ''; context = null; capabilities.clear();
        twoFactorPending = false; integration = { deviceBell: true }; linkedProposal = '';
        requests.forEach(c => c.abort()); requests.clear();
        document.querySelectorAll('.dq-web-owned').forEach(n => { if (n.open) n.close(); n.remove(); });
    }
    function syncSession() {
        const key = session()?.key || '';
        if (key !== sessionKey) {
            resetPrivate(); sessionKey = key; lastPoll = 0;
            if (key) queueMicrotask(loadNotices);
        }
        return Boolean(key);
    }
    async function api(path, method = 'GET', body) {
        syncSession();
        const s = session(), generation = epoch;
        if (!s) throw new Error('Sign in to this Jellyfin server first.');
        const controller = new AbortController(); requests.add(controller);
        const timeout = setTimeout(() => controller.abort(), 15000);
        try {
            const response = await fetch(new URL(path, root), { method, cache: 'no-store', credentials: 'omit', signal: controller.signal,
                headers: { Authorization: 'MediaBrowser Token="' + encodeURIComponent(s.token) + '"', ...(body ? { 'Content-Type': 'application/json' } : {}) },
                body: body ? JSON.stringify(body) : undefined });
            if (generation !== epoch || session()?.key !== s.key) { syncSession(); throw new Error('Session changed. Reopen this action.'); }
            if (!response.ok) {
                let data; try { data = await response.json(); } catch { /* Never render upstream error prose. */ }
                if ((response.status === 401 || response.status === 403) && get(data, 'twoFactorRequired') === true) {
                    resetPrivate(); twoFactorPending = true; schedule();
                    throw new Error('Complete 2FA to see notifications.');
                }
                if (response.status === 401 || response.status === 403) resetPrivate();
                const code = get(data, 'code');
                const messages = {
                    ProposalNotPermitted: 'You may only request removal of your own requests, unless your administrator grants wider access.',
                    SeerrNotConfigured: 'An administrator must configure Seerr, or import the Jellyfin-Enhanced connection in plugin settings.',
                    NotificationsRequired: 'Install or update Notifications, enable it, then restart Jellyfin before requesting removal.',
                    InvalidJson: 'The request could not be read. Reload Jellyfin to update the removal controls.',
                    UpstreamUnavailable: 'Seerr could not be reached. Ask your administrator to test the saved connection.',
                    SeerrServerAssociationUnverified: 'Seerr is not linked to this Jellyfin server. Ask your administrator to check the Seerr connection.',
                    IntegrityUnavailable: 'Deletion storage could not be verified. Ask your administrator to check plugin storage.',
                    TargetEditionUnverified: 'This edition could not be verified against Seerr.',
                    SeerrTargetNotManaged: 'Seerr does not manage files for this media.',
                    TargetUnavailable: 'This movie or whole series is unavailable for removal.',
                    RevisionChanged: 'The removal request changed. Reopen it before acting.',
                    SnapshotChanged: 'Your activity changed. Reopen the request to review it.',
                    TargetChanged: 'The target changed. Close and reopen this action.'
                };
                throw new Error(messages[code] || 'Operation unavailable. Refresh and review your session or ask your administrator.');
            }
            if (path === 'me/notices') noticeProblem = response.headers.get('X-DeletionQueue-Notice-Delivery') === 'incomplete'
                ? 'Notification delivery is incomplete. Review the deletion queue as well.' : '';
            const result = response.status === 204 ? undefined : await response.json();
            if (generation !== epoch || session()?.key !== s.key) { syncSession(); throw new Error('Session changed.'); }
            return result;
        } finally { clearTimeout(timeout); requests.delete(controller); }
    }
    function button(label, action, cls = 'emby-button raised') {
        const b = node('button', label, cls); b.type = 'button';
        const generation = epoch;
        b.addEventListener('click', async e => {
            e.preventDefault(); e.stopPropagation();
            if (!syncSession() || generation !== epoch) return;
            b.disabled = true;
            try { await action(); } catch (err) { showError(err, b.closest('dialog')); } finally { b.disabled = false; }
        });
        return b;
    }
    function dialog(title) {
        window.dispatchEvent(new Event('suite-notification-dismiss'));
        // One plugin modal at a time; the native element supplies focus trapping and Escape dismissal.
        document.querySelectorAll('dialog.dq-web-owned').forEach(d => { d.close(); d.remove(); });
        const d = node('dialog', undefined, 'dialog dq-panel dq-web-owned');
        const h = node('h2', title); h.id = 'dq-panel-title'; d.setAttribute('aria-labelledby', h.id);
        const header = node('div', undefined, 'formDialogHeader');
        header.append(h, button('Close', () => d.close(), 'emby-button button-flat dq-close'));
        const content = node('div', undefined, 'formDialogContent dq-panel-content');
        const error = node('p', '', 'dq-error'); error.setAttribute('role', 'alert');
        d.append(header, error, content); document.body.append(d);
        const focus = document.activeElement;
        d.addEventListener('close', () => { d.remove(); if (focus?.isConnected) focus.focus(); });
        d.showModal(); return { d, content, generation: epoch };
    }
    function showError(error, d) {
        if (twoFactorPending) return;
        if (!syncSession()) return;
        if (!d?.isConnected) {
            window.dispatchEvent(new CustomEvent('suite-notification-message', { detail: { userId: session()?.user, title: 'Removal request', body: error?.message || 'Operation unavailable.' } }));
            return;
        }
        d.querySelector('.dq-error').textContent = error?.message || 'Operation unavailable.';
        d.querySelector('.dq-close')?.remove();
        if (!d.dataset.dismissibleError) {
            d.dataset.dismissibleError = 'true';
            let outside = false;
            const backdrop = event => { const r = d.getBoundingClientRect(); return event.target === d && (event.clientX < r.left || event.clientX > r.right || event.clientY < r.top || event.clientY > r.bottom); };
            d.addEventListener('pointerdown', event => { outside = backdrop(event); });
            d.addEventListener('click', event => { if (outside && backdrop(event)) d.close(); outside = false; });
        }
    }
    const alive = panel => panel.d.isConnected && panel.generation === epoch && syncSession();
    function explanation(content) {
        content.append(node('p', 'Removal affects the movie or whole series and its files through Seerr. Request history stays in Seerr. Recent viewers must approve; silence or No blocks removal. New activity invalidates approval and restarts the minimum two-day delay.'));
    }
    async function propose(itemId) {
        const panel = dialog('Request removal'); panel.content.append(node('p', 'Checking media and request ownership…'));
        try {
            const preview = await api('targets/items/' + itemId + '/preview', 'POST', {});
            if (!alive(panel)) return;
            panel.content.replaceChildren(node('h3', get(preview, 'title')));
            panel.content.append(node('p', (get(preview, 'kind') === 1 || get(preview, 'kind') === 'Series' ? 'Whole series' : 'Movie') + (get(preview, 'is4K') ? ' · 4K' : ' · Standard')));
            explanation(panel.content);
            if (!get(preview, 'executionAvailable')) panel.content.append(node('p', 'Automatic removal is blocked until the server can verify all deletion safeguards. No files will be deleted.', 'dq-warning'));
            panel.content.append(button('Request removal', async () => {
                const p = await api('proposals', 'POST', { commandId: command(), selection: get(preview, 'selection'),
                    fingerprint: get(preview, 'fingerprint'), snapshot: get(preview, 'snapshot') });
                if (alive(panel)) await proposal(get(p, 'id')); await loadNotices();
            }));
        } catch (err) { if (alive(panel)) { panel.content.replaceChildren(button('Try again', () => propose(itemId))); showError(err, panel.d); } }
    }
    function renderProposal(p, content) {
        const id = get(p, 'id'), status = get(p, 'status'), revision = get(p, 'revision');
        content.append(node('h3', get(p, 'title')), node('p', get(p, 'ownership') === 'Yours' ? 'Your removal request' : 'Community removal request'),
            node('p', status + ' — ' + get(p, 'reason')));
        const deadline = get(p, 'notBefore');
        if (deadline) content.append(node('p', 'Not before ' + new Date(deadline).toLocaleString() + ', subject to fresh checks.'));
        const answer = get(p, 'ownAnswer');
        content.append(node('p', 'Your answer: ' + (answer === 0 || answer === 'Yes' ? 'Yes' : answer === 1 || answer === 'No' ? 'No' : 'Not answered for this snapshot')));
        if (!['Claimed', 'Uncertain', 'FilesDeleted', 'Completed', 'Cancelled'].includes(status)) {
            const actions = node('div', undefined, 'dq-actions');
            for (const [label, answer] of [['Allow removal', 0], ['Keep this media', 1]]) actions.append(button(label, async () => {
                await api('proposals/' + id + '/consent', 'POST', { commandId: command(), revision, snapshot: get(p, 'ownSnapshot'), answer });
                await proposal(id); await loadNotices();
            }));
            if (get(p, 'canUndo')) actions.append(button('Undo request', async () => {
                await api('proposals/' + id + '/undo', 'POST', { commandId: command(), revision }); await proposal(id); await loadNotices();
            }));
            content.append(actions);
        }
    }
    async function proposal(id) {
        const panel = dialog('Removal request');
        try { const p = await api('proposals/' + id); if (!alive(panel)) return; renderProposal(p, panel.content); explanation(panel.content); }
        catch (err) { if (alive(panel)) showError(err, panel.d); }
    }
    async function queue() {
        const panel = dialog('Deletion queue'); let offset = 0;
        const rows = node('div'); panel.content.append(rows);
        const more = button('Load more', async () => {
            const proposals = await api('proposals?skip=' + offset + '&take=50'); if (!alive(panel)) return;
            for (const p of proposals) rows.append(button(get(p, 'title') + ' — ' + get(p, 'status'), () => proposal(get(p, 'id')), 'emby-button button-flat dq-notice'));
            offset += proposals.length; more.hidden = proposals.length < 50;
            if (!offset) rows.append(node('p', 'No removal requests. Use Request removal on a movie, series or Enhanced request.'));
        });
        panel.content.append(more); more.click();
    }
    async function preferences() {
        const panel = dialog('Deletion queue preferences');
        try {
            const pref = await api('me/preferences'); if (!alive(panel)) return;
            const row = node('div', undefined, 'checkboxContainer'), label = node('label', undefined, 'emby-checkbox-label');
            const notifications = node('input', undefined, 'emby-checkbox'); notifications.type = 'checkbox'; notifications.checked = get(pref, 'notifications'); notifications.id = 'dq-notifications-' + command(); label.htmlFor = notifications.id;
            notifications.setAttribute('is', 'emby-checkbox'); notifications.setAttribute('data-embycheckbox', 'true');
            const outline = node('span', undefined, 'checkboxOutline'); outline.setAttribute('aria-hidden', 'true'); outline.append(node('span', undefined, 'material-icons checkboxIcon checkboxIcon-checked check'), node('span', undefined, 'material-icons checkboxIcon checkboxIcon-unchecked'));
            label.append(notifications, node('span', 'Notify me about removal requests and approvals', 'checkboxLabel'), outline); row.append(label);
            const hoursRow = node('div', undefined, 'inputContainer'), hoursLabel = node('label', 'Minimum removal delay (hours)', 'inputLabel');
            const hours = node('input', undefined, 'emby-input'); hours.type = 'number'; hours.min = '48'; hours.max = '8760'; hours.value = get(pref, 'delayHours');
            hours.id = 'dq-delay-' + command(); hoursLabel.htmlFor = hours.id; hours.setAttribute('is', 'emby-input'); hours.labelElement = hoursLabel;
            hours.addEventListener('focus', () => hoursLabel.classList.add('inputLabelFocused')); hours.addEventListener('blur', () => hoursLabel.classList.remove('inputLabelFocused')); hoursRow.append(hoursLabel, hours);
            panel.content.append(row, hoursRow, node('p', 'Reducing or resetting this preference never advances an existing proposal deadline.'), button('Save preferences', async () => {
                if (!hours.reportValidity()) return;
                await api('me/preferences', 'PATCH', { commandId: command(), revision: get(pref, 'revision'), delayHours: Number(hours.value), notifications: notifications.checked });
                if (alive(panel)) panel.d.close(); await loadNotices();
            }));
        } catch (err) { if (alive(panel)) showError(err, panel.d); }
    }
    function updateBadges() {
        const count = notices.filter(n => !get(n, 'isRead')).length;
        document.querySelectorAll('.dq-bell').forEach(b => {
            const label = noticeProblem ? 'Notifications unavailable or incomplete' : 'Notifications: ' + count + ' unseen';
            if (b.getAttribute('aria-label') !== label) { b.setAttribute('aria-label', label); b.title = label; }
            const badge = b.querySelector('.dq-badge'); const text = noticeProblem ? '!' : count >= 100 ? '100+' : String(count);
            if (badge.textContent !== text) badge.textContent = text;
            badge.hidden = !noticeProblem && count === 0;
        });
    }
    async function loadNotices() {
        if (!syncSession() || twoFactorPending || pollingEpoch === epoch) return;
        const generation = epoch; pollingEpoch = generation;
        try {
            const result = await api('me/notices');
            const mode = await api('me/integration');
            if (generation === epoch) { notices = result; integration = mode; updateBadges(); schedule(); }
        }
        catch { if (generation === epoch) { notices = []; noticeProblem = 'Notifications unavailable. Refresh or check with your administrator.'; updateBadges(); } }
        finally { if (pollingEpoch === generation) pollingEpoch = -1; }
    }
    async function center() {
        const panel = dialog('Notifications');
        await loadNotices(); if (!alive(panel)) return;
        panel.content.append(node('p', noticeProblem || 'Opening notifications does not mark them read or approve removal.'));
        const actions = node('div', undefined, 'dq-actions');
        actions.append(button('Deletion queue', queue), button('Preferences', preferences), button('Refresh', center)); panel.content.append(actions);
        if (!notices.length) panel.content.append(node('p', noticeProblem ? 'Unable to load notifications.' : 'You are all caught up.'));
        for (const n of notices) {
            const row = node('article', undefined, 'listItem dq-notice');
            row.append(node('p', (get(n, 'isRead') ? '' : 'Unseen · ') + get(n, 'text')),
                button('Review request', () => proposal(get(n, 'proposalId'))));
            if (!get(n, 'isRead')) row.append(button('Mark read', async () => {
                await api('me/notices/' + get(n, 'id') + '/read', 'POST', { commandId: command() }); await center();
            }));
            panel.content.append(row);
        }
    }
    function mediaButton(id, cls) {
        const b = button('Request removal', () => {
            const card = b.closest('.card[data-id]'), request = b.closest('.je-request-card');
            if (card && card.dataset.id !== id || request && request.querySelector('[data-media-id]')?.dataset.mediaId !== id ||
                b.closest('.mainDetailButtons') && routeId() !== id) {
                schedule(); throw new Error('This media changed. Reopen its removal action.');
            }
            return propose(id);
        }, cls + ' dq-media-action dq-web-owned');
        b.dataset.dqItem = id; b.setAttribute('aria-label', 'Request removal'); b.title = 'Request removal';
        b.replaceChildren(node('span', 'delete_outline', 'material-icons')); b.firstChild.setAttribute('aria-hidden', 'true');
        return b;
    }
    function routeId() {
        const route = location.hash.slice(1) || location.pathname + location.search;
        if (!/\bdetails\b/i.test(route)) return null;
        return guid(new URLSearchParams(route.split('?')[1] || '').get('id'));
    }
    function mount() {
        frame = 0;
        if (!syncSession()) return;
        if (/login|selectserver|twofactor|two-factor|invite|reset|setup|link-token/i.test(location.hash)) {
            document.querySelectorAll('.dq-bell, .dq-local-shortcut, .dq-two-factor').forEach(n => n.remove()); return;
        }
        // Never use the Enhanced tray: it intentionally collapses its children into a More menu.
        const userAnchor = [...document.querySelectorAll('.headerUserButton, [aria-controls="app-user-menu"]')].find(n => n.getClientRects().length);
        const preferredHeader = userAnchor?.parentElement || [...document.querySelectorAll('.headerRight, .MuiToolbar-root')].find(n => n.getClientRects().length);
        const headers = preferredHeader ? [preferredHeader] : [];
        document.querySelectorAll('.dq-bell, .dq-local-shortcut').forEach(n => { if (n.parentElement !== preferredHeader) n.remove(); });
        for (const header of headers.filter(h => !h.closest('.videoOsdBottom') && !h.querySelector('.headerRight'))) {
            if (twoFactorPending) {
                header.querySelectorAll('.dq-bell, .dq-local-shortcut').forEach(n => n.remove());
                if (!header.querySelector('.dq-two-factor')) {
                    const a = node('a', 'Complete 2FA to see notifications', 'dq-two-factor dq-web-owned'); a.href = '#/login'; header.append(a);
                }
                continue;
            }
            header.querySelectorAll('.dq-two-factor').forEach(n => n.remove());
            const hosted = !!document.querySelector('[data-notifications-bootstrap]') || [...document.querySelectorAll('[data-account-hub-bell]')].some(b => b.getClientRects().length > 0);
            if (hosted || get(integration, 'deviceBell') === false || get(integration, 'notificationMode') === 'Disabled') {
                header.querySelectorAll('.dq-bell').forEach(n => n.remove());
                if (!header.querySelector('.dq-local-shortcut')) header.append(button('Deletion queue', queue, 'emby-button button-flat dq-local-shortcut dq-web-owned'));
                continue;
            }
            header.querySelectorAll('.dq-local-shortcut').forEach(n => n.remove());
            if (header.querySelector(':scope > .dq-bell')) continue;
            const b = button('', center, 'emby-button button-flat headerButton dq-bell dq-web-owned');
            b.setAttribute('aria-haspopup', 'dialog');
            const icon = node('span', 'notifications', 'material-icons'); icon.setAttribute('aria-hidden', 'true');
            const badge = node('span', '', 'dq-badge'); badge.hidden = true; badge.setAttribute('aria-hidden', 'true');
            b.append(icon, badge); header.append(b);
        }
        updateBadges();
        if (twoFactorPending) return;
        const deepLink = guid(new URLSearchParams(location.hash.split('?')[1] || '').get('deletionQueueProposal'));
        if (deepLink && linkedProposal !== deepLink) { linkedProposal = deepLink; proposal(deepLink).catch(() => {}); }
        for (const card of [...document.querySelectorAll('.card[data-id]')].slice(0, 500)) {
            if (!['Movie', 'Series'].includes(card.dataset.type)) continue;
            const id = guid(card.dataset.id); if (!id) continue;
            const existing = card.querySelector('.dq-media-action');
            if (existing?.dataset.dqItem === id) continue;
            existing?.remove(); (card.querySelector('.cardScalable') || card).append(mediaButton(id, 'emby-button button-flat dq-card-action'));
        }
        const id = routeId();
        if (id && !capabilities.has(id)) {
            if (capabilities.size >= 200) capabilities.clear();
            capabilities.set(id, false); const generation = epoch;
            api('targets/items/' + id + '/capabilities').then(result => {
                if (generation === epoch) { capabilities.set(id, Boolean(get(result, 'canPropose'))); schedule(); }
            }).catch(() => {});
        }
        for (const group of [...document.querySelectorAll('.mainDetailButtons')].slice(0, 5)) {
            const existing = group.querySelector('.dq-media-action');
            if (existing?.dataset.dqItem === id) continue;
            existing?.remove(); if (id && capabilities.get(id)) group.append(mediaButton(id, 'emby-button button-flat detailButton'));
        }
        // Enhanced's available-request title/watch elements contain the authoritative native media ID.
        // No dependence on private renderer functions, card titles, usernames or third-party request IDs.
        for (const card of [...document.querySelectorAll('.je-request-card')].slice(0, 500)) {
            const id = guid(card.querySelector('[data-media-id]')?.dataset.mediaId);
            const existing = card.querySelector('.dq-request-action');
            if (existing?.dataset.dqItem === (id || '')) continue;
            existing?.remove();
            const b = id ? mediaButton(id, 'emby-button button-flat dq-request-action') : node('button', 'Removal unavailable', 'emby-button button-flat dq-request-action dq-web-owned');
            if (!id) { b.disabled = true; b.dataset.dqItem = ''; b.title = 'Media must be available in Jellyfin before requesting removal.'; }
            else b.append(node('span', 'Request removal'));
            (card.querySelector('.je-request-actions') || card.querySelector('.je-request-info') || card).append(b);
        }
        if (context && Date.now() - context.at < 15000) {
            for (const menu of document.querySelectorAll('.actionSheet')) {
                if (!menu.getClientRects().length || menu.tagName === 'DIALOG' && !menu.open || context.before.has(menu) || menu.querySelector('.dq-context-action')) continue;
                const scroller = menu.querySelector('.actionSheetScroller'); if (!scroller) continue;
                const captured = context; context = null;
                const b = node('button', 'Request removal', 'listItem listItem-button actionSheetMenuItem dq-context-action dq-web-owned'); b.type = 'button';
                // Let Jellyfin dismiss its own sheet as a cancellation (no command ID). Never execute native Delete.
                b.addEventListener('click', () => {
                    if (b.disabled) return; b.disabled = true;
                    let done = false;
                    const watch = new MutationObserver(() => { if (!menu.isConnected) open(); });
                    const cleanup = () => { watch.disconnect(); clearTimeout(timeout); };
                    const open = () => { if (done) return; done = true; cleanup(); if (syncSession() && epoch === captured.epoch) propose(captured.id).catch(err => showError(err)); };
                    const timeout = setTimeout(() => { done = true; cleanup(); }, 15000);
                    menu.addEventListener('close', open, { once: true });
                    watch.observe(document.body, { childList: true, subtree: true });
                    const nativeDialog = menu.closest('dialog');
                    if (nativeDialog?.open) { nativeDialog.addEventListener('close', open, { once: true }); nativeDialog.close(); }
                    if (!menu.isConnected) open();
                });
                scroller.append(b); break;
            }
        }
    }
    function schedule() { if (!frame) frame = requestAnimationFrame(() => { try { mount(); } catch { /* Isolate unsupported Web markup. */ } }); }
    function captureContext(e) {
        if (!syncSession() || e.target.closest('.dq-web-owned')) return;
        const card = e.target.closest('.card[data-id], [data-itemid], .itemAction[data-id]');
        const more = e.target.closest('.cardMenuButton, .btnMoreCommands, .btnMore, [data-action="menu"]');
        context = null;
        if (e.type !== 'contextmenu' && !more) return;
        // Modern cards do not necessarily expose data-type. The server preview
        // remains authoritative for item kind, access and proposal permission.
        const id = card ? guid(card.dataset.id || card.dataset.itemid) : more || e.type === 'contextmenu' ? routeId() : null;
        document.querySelectorAll('.dq-context-action').forEach(button => button.remove());
        if (id) { context = { id, at: Date.now(), epoch, before: new Set([...document.querySelectorAll('.actionSheet')].filter(menu => menu.getClientRects().length && (menu.tagName !== 'DIALOG' || menu.open))) }; schedule(); }
    }
    document.addEventListener('contextmenu', captureContext, true);
    document.addEventListener('click', captureContext, true);
    new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true,
        attributes: true, attributeFilter: ['data-id', 'data-media-id', 'data-type'] });
    window.addEventListener('hashchange', () => { context = null; schedule(); });
    window.addEventListener('popstate', () => { context = null; schedule(); });
    window.addEventListener('storage', () => { syncSession(); schedule(); });
    document.addEventListener('viewshow', schedule);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) { twoFactorPending = false; schedule(); loadNotices(); } });
    window.addEventListener('focus', () => { twoFactorPending = false; schedule(); loadNotices(); });
    window.addEventListener('pagehide', () => { resetPrivate(); sessionKey = ''; });
    setInterval(() => {
        const changed = session()?.key !== sessionKey;
        if (!syncSession()) return;
        if (changed) schedule();
        if (!document.hidden && Date.now() - lastPoll >= 30000) { lastPoll = Date.now(); loadNotices(); }
    }, 1000);
    schedule(); loadNotices();
}());
