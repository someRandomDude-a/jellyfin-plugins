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
    let twoFactorPending = false, integration = { deviceBell: true }, linkedProposal = '', enhancedDeletionSelected = false, fullQueueUntil = 0;
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
        enhancedDeletionSelected = false; fullQueueUntil = 0; document.querySelectorAll('.dq-deletion-selected').forEach(element => element.classList.remove('dq-deletion-selected'));
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
        const help = node('p', 'Consent required · minimum 2-day delay', 'dq-hint');
        help.title = 'Recent viewers must allow removal. New activity requires fresh approval. Seerr request history is retained.';
        content.append(help);
    }
    async function propose(itemId) {
        const panel = dialog('Request removal'); panel.content.append(node('p', 'Checking media and request ownership…'));
        try {
            const preview = await api(typeof itemId === 'object' ? 'targets/preview' : 'targets/items/' + itemId + '/preview', 'POST', typeof itemId === 'object' ? itemId : {});
            if (!alive(panel)) return;
            panel.content.replaceChildren(node('h3', get(preview, 'title')));
            panel.content.append(node('p', (get(preview, 'kind') === 1 || get(preview, 'kind') === 'Series' ? 'Whole series' : 'Movie') + (get(preview, 'is4K') ? ' · 4K' : ' · Standard')));
            explanation(panel.content);
            if (get(preview, 'requestOnly')) panel.content.append(node('p', 'Declines this unfulfilled Seerr request. History is kept.', 'dq-hint'));
            if (!get(preview, 'executionAvailable')) panel.content.append(node('p', 'Removal is paused until safety checks complete.', 'dq-warning'));
            panel.content.append(button('Request removal', async () => {
                const p = await api('proposals', 'POST', { commandId: command(), selection: get(preview, 'selection'),
                    fingerprint: get(preview, 'fingerprint'), snapshot: get(preview, 'snapshot') });
                if (alive(panel)) panel.d.close();
                window.dispatchEvent(new CustomEvent('suite-notification-message', { detail: { userId: session()?.user, title: get(p, 'title') || 'Removal requested', body: 'Removal requested. Follow its progress in Notifications.' } }));
                window.dispatchEvent(new Event('suite-removal-changed')); await loadNotices();
            }));
        } catch (err) { if (alive(panel)) { panel.content.replaceChildren(button('Try again', () => propose(itemId))); showError(err, panel.d); } }
    }
    function renderProposal(p, content, compact = false) {
        const id = get(p, 'id'), status = get(p, 'status'), revision = get(p, 'revision');
        const heading = node('div', undefined, 'dq-request-heading');
        const badge = node('span', status, 'dq-status'); badge.dataset.status = status;
        badge.title = ({ ActivityCoverageIncomplete: 'Collecting recent viewing activity', ConsentRequired: 'Waiting for viewer approval', AdministratorApprovalRequired: 'Waiting for administrator approval', DelayRequired: 'Safety delay', RequestExpired: 'Not accepted within 30 days', AdministratorDenied: 'Denied by an administrator' })[get(p, 'reason')] || get(p, 'reason');
        badge.setAttribute('aria-label', status + ': ' + badge.title);
        heading.append(node('h3', get(p, 'title')), badge); content.append(heading);
        if (get(p, 'stage') && get(p, 'stage') !== 'History') content.append(node('span', get(p, 'stage'), 'dq-stage'));
        const deadline = get(p, 'notBefore');
        if (deadline && !compact) content.append(node('p', 'Earliest removal · ' + new Date(deadline).toLocaleString(), 'dq-hint'));
        const answer = get(p, 'ownAnswer');
        if (!['Claimed', 'Uncertain', 'FilesDeleted', 'Completed', 'Cancelled', 'Expired'].includes(status)) {
            const actions = node('div', undefined, 'dq-actions');
            for (const [label, value] of [['Allow', 0], ['Keep', 1]]) {
                const control = button(label, async () => {
                    await api('proposals/' + id + '/consent', 'POST', { commandId: command(), revision, snapshot: get(p, 'ownSnapshot'), answer: value });
                    window.dispatchEvent(new Event('suite-removal-changed')); await loadNotices();
                    const updated = await api('proposals/' + id); if (content.isConnected) { content.replaceChildren(); renderProposal(updated, content, compact); }
                }, 'emby-button button-flat');
                control.dataset.answer = value === 0 ? 'allow' : 'keep';
                control.setAttribute('aria-pressed', String(answer === value || answer === (value === 0 ? 'Yes' : 'No'))); actions.append(control);
            }
            if (get(p, 'canUndo')) actions.append(button('Undo request', async () => {
                await api('proposals/' + id + '/undo', 'POST', { commandId: command(), revision });
                window.dispatchEvent(new Event('suite-removal-changed')); await loadNotices();
                const updated = await api('proposals/' + id); if (content.isConnected) { content.replaceChildren(); renderProposal(updated, content, compact); }
            }, 'emby-button button-flat'));
            content.append(actions);
        }
    }
    async function proposal(id) {
        const panel = dialog('Removal request');
        try { const p = await api('proposals/' + id); if (!alive(panel)) return; renderProposal(p, panel.content); explanation(panel.content); }
        catch (err) { if (alive(panel)) showError(err, panel.d); }
    }
    async function queue(initialView = 'active') {
        const panel = dialog('Deletion queue');
        if (get(integration, 'enhancedRequestsEnabled')) panel.content.append(button('Open full queue', () => {
            panel.d.close(); enhancedDeletionSelected = true; fullQueueUntil = Date.now() + 15000;
            const navigation = document.querySelector('a[data-itemid="Jellyfin.Plugin.JellyfinEnhanced.DownloadsPage"]');
            if (navigation) navigation.click();
            else if (typeof window.JellyfinEnhanced?.downloadsPage?.showPage === 'function') window.JellyfinEnhanced.downloadsPage.showPage();
            else location.hash = '#/downloads';
            enhancedDeletionSelected = true; schedule();
        }, 'emby-button button-flat dq-open-full'));
        await queueView(panel.content, initialView, () => alive(panel), panel.d);
    }
    async function queueView(host, initialView = 'active', valid = () => host.isConnected && syncSession(), scrollRoot = host) {
        const tabs = node('div', undefined, 'dq-tabs'); tabs.setAttribute('role', 'tablist'); tabs.setAttribute('aria-label', 'Deletion requests');
        const rows = node('div', undefined, 'dq-queue-rows'); rows.setAttribute('role', 'tabpanel'); host.append(tabs, rows);
        let loading = false, refreshed = 0, offset = 0, more = true, view = initialView, revision = 0;
        for (const [key, label] of [['active', 'Active'], ['history', 'History']]) {
            const tab = button(label, async () => { view = key; revision++; await refresh(); }, 'emby-button button-flat');
            tab.dataset.view = key; tab.setAttribute('role', 'tab'); tabs.append(tab);
        }
        async function refresh(append = false) {
            if (loading || !valid()) return; loading = true; const started = revision;
            try {
                const start = append ? offset : 0;
                const proposals = await api('proposals?skip=' + start + '&take=50&view=' + view); if (!valid() || revision !== started) return;
                if (!append) rows.replaceChildren();
                for (const p of proposals) {
                    const row = node('article', undefined, 'dq-queue-row'); renderProposal(p, row, true);
                    const date = get(p, 'stage') === 'Queued' ? get(p, 'notBefore') : get(p, 'createdAt');
                    if (date) row.append(node('p', (get(p, 'stage') === 'Queued' ? 'Earliest removal · ' : 'Requested · ') + new Date(date).toLocaleString(), 'dq-hint'));
                    rows.append(row);
                }
                offset = start + proposals.length; more = proposals.length === 50; refreshed = Date.now();
                if (!offset) rows.append(node('p', 'No removal requests.'));
                for (const tab of tabs.querySelectorAll('[role=tab]')) { tab.setAttribute('aria-selected', String(tab.dataset.view === view)); tab.tabIndex = tab.dataset.view === view ? 0 : -1; }
            } catch (error) { if (valid()) { if (!rows.children.length) rows.textContent = error.message; else showError(error, host.closest('dialog')); } }
            finally { loading = false; if (valid() && revision !== started) void refresh(); }
        }
        tabs.addEventListener('keydown', event => { if (['ArrowLeft','ArrowRight','Home','End'].includes(event.key)) { event.preventDefault(); const controls = [...tabs.querySelectorAll('button')]; const current = controls.indexOf(document.activeElement); const index = event.key === 'Home' ? 0 : event.key === 'End' ? controls.length - 1 : (current + (event.key === 'ArrowRight' ? 1 : controls.length - 1)) % controls.length; controls[index].focus(); controls[index].click(); } });
        host.addEventListener('pointerdown', event => { if (!event.target.closest('button, input, a') && Date.now() - refreshed > 5000) void refresh(); });
        scrollRoot.addEventListener('scroll', () => { if (more && scrollRoot.scrollTop + scrollRoot.clientHeight >= scrollRoot.scrollHeight - 120) void refresh(true); });
        await refresh();
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
    async function requestRemoval(card) {
        const native = guid(card.querySelector('[data-media-id]')?.dataset.mediaId);
        if (native) return propose(native);
        const metadata = card.querySelector('[data-tmdb-id][data-media-type]');
        const tmdb = Number(metadata?.dataset.tmdbId), type = metadata?.dataset.mediaType;
        if (!Number.isSafeInteger(tmdb) || tmdb <= 0 || !['movie','tv'].includes(type)) throw new Error('This request has no verified media identifier.');
        const captured = epoch;
        for (let skip = 0; skip < 1000; skip += 100) {
            const page = await api('requests?skip=' + skip + '&tmdbId=' + tmdb + '&mediaType=' + type);
            if (!card.isConnected || !syncSession() || captured !== epoch) return;
            const candidates = get(page, 'requests') || [];
            if (candidates.length === 1) return propose({ requestId: get(candidates[0], 'requestId'), is4K: get(candidates[0], 'is4K') });
            if (candidates.length > 1) {
                const panel = dialog('Choose request');
                for (const request of candidates) panel.content.append(button(get(request, 'is4K') ? '4K request' : 'Standard request', () => propose({ requestId: get(request, 'requestId'), is4K: get(request, 'is4K') })));
                return;
            }
            if (!get(page, 'hasMore')) break;
        }
        throw new Error('No matching unfulfilled request owned by your account was found.');
    }
    function mountEnhanced() {
        for (const tabs of document.querySelectorAll('.je-requests-tabs')) {
            const section = tabs.closest('.je-requests-section'); if (!section || tabs.querySelector('.dq-deletion-tab')) continue;
            const view = node('div', undefined, 'dq-enhanced-view dq-web-owned'); view.hidden = true;
            const tab = button('Deletion queue', async () => {
                enhancedDeletionSelected = true; section.classList.add('dq-deletion-selected'); view.hidden = false;
                for (const sibling of tabs.querySelectorAll('button')) sibling.classList.toggle('active', sibling === tab);
                if (!view.children.length) await queueView(view);
            }, 'je-requests-tab emby-button dq-deletion-tab dq-web-owned');
            tabs.addEventListener('click', event => {
                if (event.target.closest('button') && !event.target.closest('.dq-deletion-tab')) { enhancedDeletionSelected = false; section.classList.remove('dq-deletion-selected'); view.hidden = true; }
            }, true);
            tabs.append(tab); section.append(view);
            if (enhancedDeletionSelected || Date.now() < fullQueueUntil) { fullQueueUntil = 0; tab.click(); }
        }
        for (const calendar of document.querySelectorAll('.je-calendar-layout')) {
            if (calendar.querySelector('.dq-calendar-removals')) continue;
            const agenda = node('section', undefined, 'dq-calendar-removals dq-web-owned');
            agenda.append(node('h3', 'Removal requests'), node('p', 'Queued dates are earliest removal times. Consent-stage requests have no scheduled date.', 'dq-hint'));
            (calendar.querySelector('.je-calendar-sidebar-content') || calendar).append(agenda);
            void queueView(agenda);
        }
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
                if (!header.querySelector('.dq-local-shortcut')) {
                    const shortcut = button('', queue, 'paper-icon-button-light headerButton dq-local-shortcut dq-web-owned');
                    shortcut.title = 'Deletion queue'; shortcut.setAttribute('aria-label', 'Deletion queue'); shortcut.setAttribute('aria-haspopup', 'dialog');
                    const icon = node('span', 'auto_delete', 'material-icons'); icon.setAttribute('aria-hidden', 'true'); shortcut.append(icon); header.append(shortcut);
                }
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
        mountEnhanced();
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
            const metadata = card.querySelector('[data-tmdb-id][data-media-type]');
            const requestKey = id || (metadata ? metadata.dataset.mediaType + ':' + metadata.dataset.tmdbId : '');
            const existing = card.querySelector('.dq-request-action');
            if (existing?.dataset.dqItem === requestKey) continue;
            existing?.remove();
            const b = id ? mediaButton(id, 'emby-button button-flat dq-request-action') : button('Request removal', () => requestRemoval(card), 'emby-button button-flat dq-request-action dq-web-owned');
            b.dataset.dqItem = requestKey;
            if (!requestKey) { b.disabled = true; b.title = 'No verified media identifier is exposed by this request card.'; }
            else if (id) b.append(node('span', 'Request removal'));
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
    window.addEventListener('hashchange', () => { context = null; if (Date.now() >= fullQueueUntil && !/downloads/.test(location.hash)) enhancedDeletionSelected = false; schedule(); });
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
