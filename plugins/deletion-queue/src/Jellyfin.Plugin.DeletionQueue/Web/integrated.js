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
    const itemStates = new Map();
    let pendingRequestFocus = null, calendarData = null, calendarLoading = false, calendarReadAt = 0;
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
        epoch++; notices = []; noticeProblem = ''; context = null; capabilities.clear(); itemStates.clear();
        pendingRequestFocus = null; calendarData = null; calendarLoading = false; calendarReadAt = 0;
        const calendarState = window.JellyfinEnhanced?.internals?.calendarPage?.state;
        if (Array.isArray(calendarState?.events)) calendarState.events = calendarState.events.filter(event => !String(event.id).startsWith('dq-removal:'));
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
                const failure = new Error(messages[code] || 'Operation unavailable. Refresh and review your session or ask your administrator.'); failure.code = code; throw failure;
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
        let outside = false;
        const backdrop = event => { const r = d.getBoundingClientRect(); return event.target === d && (event.clientX < r.left || event.clientX > r.right || event.clientY < r.top || event.clientY > r.bottom); };
        d.addEventListener('pointerdown', event => { outside = event.button === 0 && backdrop(event); });
        d.addEventListener('pointercancel', () => { outside = false; });
        d.addEventListener('click', event => { if (outside && backdrop(event)) d.close(); outside = false; });
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
            panel.content.append(button(get(preview,'kind')===1||get(preview,'kind')==='Series'?'Request removal of entire series':'Request removal', async () => {
                const p = await api('proposals', 'POST', { commandId: command(), selection: get(preview, 'selection'),
                    fingerprint: get(preview, 'fingerprint'), snapshot: get(preview, 'snapshot') });
                if (alive(panel)) panel.d.close();
                window.dispatchEvent(new CustomEvent('suite-notification-message', { detail: { userId: session()?.user, title: get(p, 'title') || 'Removal requested', body: 'Removal requested. Follow its progress in Notifications.' } }));
                window.dispatchEvent(new Event('suite-removal-changed')); await loadNotices();
            }));
        } catch (err) { if (alive(panel)) { panel.content.replaceChildren(button('Try again', () => propose(itemId))); showError(err, panel.d); } }
    }
    function statusHelp(text) {
        const wrap = node('span', undefined, 'dq-help'), control = node('button', '?', 'emby-button button-flat dq-help-button'); control.type = 'button';
        control.setAttribute('is', 'emby-button');
        const tip = node('span', text, 'dialog dq-help-text'); tip.id = 'dq-status-help-' + command(); tip.setAttribute('role', 'tooltip'); tip.hidden = true;
        control.setAttribute('aria-label', 'About removal status'); control.setAttribute('aria-describedby', tip.id); control.setAttribute('aria-expanded', 'false');
        const hide = () => { tip.hidden = true; control.setAttribute('aria-expanded', 'false'); };
        const show = () => {
            tip.hidden = false; control.setAttribute('aria-expanded', 'true'); tip.style.insetBlockStart = '100%'; tip.style.insetBlockEnd = 'auto';
            const bounds = wrap.closest('dialog')?.getBoundingClientRect();
            if (bounds && tip.getBoundingClientRect().bottom > bounds.bottom - 8) { tip.style.insetBlockStart = 'auto'; tip.style.insetBlockEnd = '100%'; }
        };
        wrap.addEventListener('mouseenter', show); wrap.addEventListener('mouseleave', () => { if (!wrap.contains(document.activeElement)) hide(); });
        control.addEventListener('focus', show); wrap.addEventListener('focusout', event => { if (!wrap.contains(event.relatedTarget)) hide(); });
        control.addEventListener('click', event => { event.preventDefault(); event.stopPropagation(); show(); });
        wrap.addEventListener('keydown', event => { if (event.key === 'Escape' && !tip.hidden) { hide(); event.preventDefault(); event.stopPropagation(); control.focus(); hide(); } });
        wrap.append(control, tip); return wrap;
    }
    function date(value) { const at = value ? new Date(value) : null; return at && Number.isFinite(at.getTime()) ? at : null; }
    function renderTimeline(p, content) {
        if(window.DeletionQueueTimeline?.render(p,content))return;
        const original = get(p, 'timeline') || [{ key: 'requested', label: 'Check permission', state: 'complete', at: get(p, 'createdAt') }];
        const crossedDelay = ['Ready', 'Claimed', 'Uncertain', 'FilesDeleted', 'Completed'].includes(get(p, 'status'));
        const terminal = ['Completed', 'Cancelled', 'Expired'].includes(get(p, 'status'));
        const checks = original.find(point => get(point, 'key') === 'checks');
        const checkpoints = original.filter(point => {
            const key = get(point, 'key');
            if (key === 'confirmed') return false;
            if (key === 'removing') return crossedDelay || date(get(point, 'at'));
            if (key === 'closed') return crossedDelay || terminal || date(get(point, 'at'));
            if (key === 'approval') return get(point, 'state') !== 'pending' || get(checks, 'state') === 'complete';
            return true;
        }).map(point => {
            const key = get(point, 'key');
            if (key === 'closed' && terminal) return { ...point, state: get(p, 'status') === 'Completed' ? 'complete' : 'cancelled', label: get(p, 'status') === 'Completed' ? 'Completed' : get(p, 'status'), at: get(point, 'at') || get(p, 'closedAt') || get(original.find(step => get(step, 'key') === 'confirmed'), 'at') };
            if (key === 'closed' && !['Cancelled', 'Expired'].includes(get(p, 'status'))) return { ...point, label: 'Confirm removal & completed', at: get(point, 'at') || get(original.find(step => get(step, 'key') === 'confirmed'), 'at') };
            if (key === 'checks' && !['Inactive', 'Consent and activity'].includes(get(point, 'label'))) return { ...point, label: 'Consent and activity' };
            if (key === 'requested') {
                const requestedAt = date(get(point, 'at')), checkedAt = date(get(checks, 'at')) || date(get(p, 'evaluatedAt'));
                if (requestedAt && checkedAt && Math.abs(checkedAt - requestedAt) < 86400000) return { ...point, label: 'Check permission' };
            }
            return point;
        });
        if (!checkpoints.length) return;
        const timeline = node('div', undefined, 'dq-timeline'); timeline.setAttribute('aria-label', 'Removal request timeline'); timeline.tabIndex = 0;
        const dated = checkpoints.map((point, index) => ({ point, index })).filter(({ point }) => date(get(point, 'at')));
        const evaluated = date(get(p, 'evaluatedAt'));
        const ended = ['Completed', 'Cancelled', 'Expired'].includes(get(p, 'status'));
        const stopped = checkpoints.find(point => get(point, 'state') === 'cancelled');
        const end = date(get(checkpoints.find(point => get(point, 'key') === 'closed'), 'at')) || date(get(stopped, 'at'));
        const now = ended ? end : evaluated;
        const dates = dated.map(({ point }) => date(get(point, 'at')).getTime());
        const start = dates.length ? Math.min(...dates) : 0, finish = dates.length ? Math.max(...dates) : start;
        const last = Math.max(1, checkpoints.length - 1);
        const low = dated.length ? dated[0].index / last * 100 : 0, high = dated.length ? dated[dated.length - 1].index / last * 100 : 100;
        const position = at => finish === start ? low : Math.max(low, Math.min(high, low + (at - start) / (finish - start) * (high - low)));
        const positions = checkpoints.map(point => date(get(point, 'at')) ? position(date(get(point, 'at')).getTime()) : null);
        // Keep stage order even when a cancelled request retains an abandoned future date.
        let previous = 0; positions.forEach((x, i) => { if (x !== null) { positions[i] = Math.max(previous, x); previous = positions[i]; } });
        if (positions[0] === null) positions[0] = 0;
        if (positions[last] === null) positions[last] = 100;
        for (let left = 0; left < positions.length - 1;) {
            let right = left + 1; while (right < positions.length && positions[right] === null) right++;
            for (let i = left + 1; i < right; i++) positions[i] = positions[left] + (positions[right] - positions[left]) * (i - left) / (right - left);
            left = right;
        }
        const track = node('div', undefined, 'dq-time-track');
        const datedNodes = node('ol', undefined, 'dq-dated-nodes'); datedNodes.setAttribute('aria-label', 'Recorded and scheduled dates'); track.append(datedNodes);
        const occupied = [];
        const icons = { requested: 'verified_user', checks: 'fact_check', approval: 'call_split', delay: 'hourglass_top', removing: 'auto_delete', closed: 'task_alt' };
        for (const [index, point] of checkpoints.entries()) {
            const step = node('li'); step.dataset.state = get(point, 'state'); step.dataset.checkpoint = get(point, 'key');
            if (['current', 'cancelled'].includes(get(point, 'state'))) step.setAttribute('aria-current', 'step');
            const glyph = node('span', get(point, 'state') === 'cancelled' ? 'cancel' : icons[get(point, 'key')] || 'radio_button_unchecked', 'material-icons dq-checkpoint-icon'); glyph.setAttribute('aria-hidden', 'true'); step.append(glyph);
            step.append(node('span', get(point, 'label'), 'dq-checkpoint-label'));
            if (get(point, 'key') === 'approval') {
                const fork = node('div', undefined, 'dq-approval-fork');
                const decision = get(point, 'decision');
                fork.append(node('span', 'Yes → Safety delay', decision === 'approved' ? 'dq-branch-selected' : ''), node('span', decision === 'expired' ? 'Timed out → Cancelled' : 'Denied → Cancelled', ['denied', 'expired'].includes(decision) ? 'dq-branch-selected' : ''));
                step.append(fork);
            }
            const at = date(get(point, 'at'));
            {
                const x = positions[index]; let lane = 0;
                const spacing = checkpoints.some(step => get(step, 'key') === 'approval') ? 32 : 22;
                while (occupied.some(previous => previous.lane === lane && Math.abs(previous.x - x) < spacing)) lane++;
                occupied.push({ x, lane }); step.style.setProperty('--dq-position', x + '%'); step.style.setProperty('--dq-lane', lane);
                step.tabIndex = 0;
                if (at) {
                    const days = Math.round((at - (evaluated || new Date())) / 86400000);
                    const time = node('time', index === 0 ? at.toLocaleDateString() : ended && get(point,'key')!=='closed' && ['cancelled','stopped','pending'].includes(get(point,'state')) ? 'Stopped' : days === 0 ? 'Today' : days > 0 ? 'In ' + days + ' days' : Math.abs(days) + ' days ago');
                    time.dateTime = at.toISOString(); time.title = (get(point,'dateLabel') ? get(point,'dateLabel')+' · ' : '')+at.toLocaleString(); step.title = time.title;
                    const relative = time.textContent;
                    const showDate = () => { time.textContent = at.toLocaleString(); }, hideDate = () => { time.textContent = relative; };
                    step.addEventListener('mouseenter', showDate); step.addEventListener('mouseleave', hideDate); step.addEventListener('focus', showDate); step.addEventListener('blur', hideDate);step.append(time);
                } else step.append(node('span', get(point, 'state') === 'cancelled' ? 'Complete' : ended ? 'Stopped' : get(point, 'state') === 'current' ? 'Pending' : get(point, 'state') === 'complete' ? 'Complete' : 'Pending', 'dq-checkpoint-date'));
                datedNodes.append(step);
            }
        }
        track.style.setProperty('--dq-lanes', Math.max(1, ...occupied.map(point => point.lane + 1)));
        {
            timeline.append(track);
            let pendingIndex = checkpoints.findIndex(point => get(point, 'state') === 'current');
            if (pendingIndex < 0) pendingIndex = checkpoints.findIndex(point => get(point, 'state') === 'pending');
            const followStage = !ended && pendingIndex >= 0 && (!now || dated.length < 2 || finish === start || !date(get(checkpoints[pendingIndex], 'at')));
            const markerPosition = ended ? 100 : followStage ? positions[pendingIndex] : now ? position(now.getTime()) : null;
            const completedIndex = checkpoints.reduce((last,point,index)=>['complete','cancelled'].includes(get(point,'state'))?index:last,0);
            const progress = ended ? 100 : markerPosition ?? positions[completedIndex];
            const travelled = node('div',undefined,'dq-travelled');travelled.style.width=progress+'%';travelled.dataset.cancelled=String(ended&&get(p,'status')!=='Completed');track.append(travelled);
            if (markerPosition !== null && (followStage || Math.abs(markerPosition - positions[0]) >= 8)) {
                const completed = get(p, 'status') === 'Completed';
                const marker = node('div', undefined, 'dq-media-marker'); marker.dataset.stopped = String(ended && !completed); marker.dataset.completed = String(completed); marker.style.setProperty('--dq-position', markerPosition + '%');
                if (followStage) { marker.dataset.checkpoint = get(checkpoints[pendingIndex], 'key'); marker.style.setProperty('--dq-marker-lane', occupied[pendingIndex].lane); }
                const closedIndex = checkpoints.findIndex(point => get(point, 'key') === 'closed');
                if (ended && closedIndex >= 0 && Math.abs(markerPosition - positions[closedIndex]) < 8) { marker.dataset.checkpoint = 'closed'; marker.style.setProperty('--dq-marker-lane', occupied[closedIndex].lane); }
                marker.title = followStage ? 'Now · ' + get(checkpoints[pendingIndex], 'label') : (ended ? 'Request ended · ' : 'Current date · ') + (now ? now.toLocaleDateString() : 'Date not recorded'); marker.setAttribute('aria-label', marker.title); track.append(marker);
            }
        }
        const closed = get(p, 'closureExplanation');
        if (closed) timeline.setAttribute('aria-description', closed);
        content.append(timeline);
        const centerMarker = () => {
            const marker = timeline.querySelector('.dq-media-marker');
            if (!timeline.isConnected || !marker || !timeline.clientWidth) return;
            const box = marker.getBoundingClientRect(), viewport = timeline.getBoundingClientRect();
            timeline.scrollLeft += box.left - viewport.left - timeline.clientWidth / 2 + box.width / 2;
        };
        requestAnimationFrame(centerMarker);
        if (typeof ResizeObserver === 'function') {
            let width = timeline.clientWidth;
            const observer = new ResizeObserver(() => {
                if (!timeline.isConnected) { observer.disconnect(); return; }
                if (timeline.clientWidth !== width) { width = timeline.clientWidth; centerMarker(); }
            });
            observer.observe(timeline);
        }
    }
    function renderProposal(p, content) {
        const id = get(p, 'id'), status = get(p, 'status'), revision = get(p, 'revision');
        const heading = node('div', undefined, 'dq-request-heading');
        const label = get(p, 'statusLabel') || ({ Blocked: 'Blocked', Waiting: 'Waiting', Ready: 'Ready for removal', Claimed: 'Removing', Uncertain: 'Needs review', FilesDeleted: 'Removal confirmed', Completed: 'Completed', Cancelled: 'Cancelled', Expired: 'Expired' })[status] || 'Pending checks';
        const help = get(p, 'statusHelp') || ({ ActivityCoverageIncomplete: 'Viewing-activity checks are not complete yet.', ConsentRequired: 'Required consent is missing or no longer current.', AdministratorApprovalRequired: 'Administrator approval is required.', DelayRequired: 'The safety delay must pass before removal.', RequestExpired: 'This request was not accepted within 30 days.', AdministratorDenied: 'This request was denied and closed.' })[get(p, 'reason')] || 'Removal is waiting for safety checks. No removal time is confirmed.';
        const badge = node('span', label, 'dq-status'); badge.dataset.status = status;
        heading.append(node('h3', get(p, 'title')), badge, statusHelp(get(p,'closureExplanation') || help)); content.append(heading);
        const badges=node('div',undefined,'dq-request-badges');
        for(const [key,title]of [['requesterLabel','Requester'],['consentStatus','Consent'],['activityStatus','Activity']])if(get(p,key)){const value=get(p,key),chip=node('span',value,'dq-request-badge');chip.dataset.kind=key;chip.dataset.state=['Allowed','Not needed','Inactive'].includes(value)?'complete':['Kept','Playing'].includes(value)?'blocked':value==='Awaiting consent'?'waiting':'neutral';chip.title=title;badges.append(chip);}if(badges.children.length)content.append(badges);
        if (get(p, 'stage') && get(p, 'stage') !== 'History') content.append(node('span', get(p, 'stage'), 'dq-stage'));
        const scheduled = date(get(p, 'scheduledFor'));
        if (scheduled && !['Completed','Cancelled','Expired'].includes(status)) { const days=Math.ceil((scheduled-(date(get(p,'evaluatedAt'))||new Date()))/86400000);const summary=node('p',days>0?'Earliest removal in '+days+' days':'Safety delay elapsed','dq-schedule');summary.title=scheduled.toLocaleString();content.append(summary); }
        else if (!['Claimed', 'Uncertain', 'FilesDeleted', 'Completed', 'Cancelled', 'Expired'].includes(status)) content.append(node('p', 'Not scheduled yet', 'dq-hint'));
        const answer = get(p, 'ownAnswer');
        if (!['Claimed', 'Uncertain', 'FilesDeleted', 'Completed', 'Cancelled', 'Expired'].includes(status)) {
            const actions = node('div', undefined, 'dq-actions');
            if (get(p, 'canConsent') !== false) for (const [label, value] of [['Allow', 0], ['Keep', 1]]) {
                const control = button(label, async () => {
                    await api('proposals/' + id + '/consent', 'POST', { commandId: command(), revision, snapshot: get(p, 'ownSnapshot'), answer: value });
                    const updated = await api('proposals/' + id + '/status'); if (content.isConnected) { content.replaceChildren(); renderProposal(updated, content); }
                    window.dispatchEvent(new Event('suite-removal-changed')); void loadNotices().catch(()=>{});
                }, 'emby-button button-flat');
                control.dataset.answer = value === 0 ? 'allow' : 'keep';
                control.setAttribute('aria-pressed', String(answer === value || answer === (value === 0 ? 'Yes' : 'No'))); actions.append(control);
            }
            if (get(p, 'canUndo')) actions.append(button('Undo request', async () => {
                await api('proposals/' + id + '/undo', 'POST', { commandId: command(), revision });
                window.dispatchEvent(new Event('suite-removal-changed')); await loadNotices();
                const updated = await api('proposals/' + id + '/status'); if (content.isConnected) { content.replaceChildren(); renderProposal(updated, content); }
            }, 'emby-button button-flat'));
            content.append(actions);
        }
        renderTimeline(p, content);
    }
    async function proposal(id) {
        return queue('active', id);
    }
    async function queue(initialView = 'active', focusId = null) {
        const panel = dialog('Deletion queue');
        panel.d.classList.add('dq-queue-popover');
        const anchor = document.querySelector('[data-notifications-bell], [data-account-hub-bell], .dq-local-shortcut, .dq-bell, .headerUserButton');
        const position = () => { const bounds = anchor?.getBoundingClientRect(); panel.d.style.setProperty('--dq-anchor-top', Math.max(8, bounds?.bottom || 64) + 'px'); };
        position(); window.addEventListener('resize', position);
        panel.d.addEventListener('close', () => window.removeEventListener('resize', position), { once: true });
        const control = panel.d.querySelector('.dq-close');
        control.replaceChildren(node('span', get(integration, 'enhancedRequestsEnabled') ? 'open_in_new' : 'close', 'material-icons'));
        control.setAttribute('aria-label', get(integration, 'enhancedRequestsEnabled') ? 'Open Deletion Queue in Requests' : 'Close deletion queue');
        control.title = control.getAttribute('aria-label');
        if (get(integration, 'enhancedRequestsEnabled')) {
            // Replace the close listener with a small popout action, not a second large button.
            const popout = control.cloneNode(true); control.replaceWith(popout);
            popout.addEventListener('click', () => { panel.d.close(); void openRequests(focusId); });
        }
        let focused;
        if (focusId) {
            try {
                focused = await api('proposals/' + focusId + '/status'); if (!alive(panel)) return;
                initialView = ['Completed', 'Cancelled', 'Expired'].includes(get(focused, 'status')) ? 'history' : 'active';
            } catch (error) { if (!alive(panel)) return; showError(error, panel.d); }
        }
        await queueView(panel.content, initialView, () => alive(panel), panel.d, focused);
    }
    async function openRequests(id = null) {
        if (!get(integration, 'enhancedRequestsEnabled')) { await queue('active', id); return; }
        pendingRequestFocus = id; enhancedDeletionSelected = true; fullQueueUntil = Date.now() + 15000;
        const deadline = fullQueueUntil, generation = epoch;
        const navigation = document.querySelector('a[data-itemid="Jellyfin.Plugin.JellyfinEnhanced.DownloadsPage"]');
        if (navigation) navigation.click();
        else if (typeof window.JellyfinEnhanced?.downloadsPage?.showPage === 'function') window.JellyfinEnhanced.downloadsPage.showPage();
        else location.hash = '#/downloads';
        const route = location.pathname + location.hash;
        setTimeout(() => {
            if (generation === epoch && fullQueueUntil === deadline && route === location.pathname + location.hash && syncSession()) {
                fullQueueUntil = 0; pendingRequestFocus = null; void queue('active', id);
            }
        }, 5000);
        schedule();
    }
    async function queueView(host, initialView = 'active', valid = () => host.isConnected && syncSession(), scrollRoot = host, focused = null) {
        const tabs = node('div', undefined, 'dq-tabs'); tabs.setAttribute('role', 'tablist'); tabs.setAttribute('aria-label', 'Deletion requests');
        const rows = node('div', undefined, 'dq-queue-rows'); rows.setAttribute('role', 'tabpanel'); host.append(tabs, rows);
        let loading = false, refreshed = 0, offset = 0, more = true, view = initialView, revision = 0, first = true, lastSnapshot = '';
        const seen = new Set(), sameId = (a, b) => String(a).replaceAll('-', '').toLowerCase() === String(b).replaceAll('-', '').toLowerCase();
        const historyGroups=new Map();let allUsers=false;
        if(get(integration,'isAdministrator')){const all=button('All requesters',async()=>{allUsers=!allUsers;all.setAttribute('aria-pressed',String(allUsers));revision++;await refresh();},'emby-button button-flat');all.setAttribute('aria-pressed','false');host.insertBefore(all,tabs);}
        for (const [key, label] of [['active', 'Active'], ['history', 'History']]) {
            const tab = button(label, async () => { view = key; revision++; await refresh(); }, 'emby-button button-flat');
            tab.dataset.view = key; tab.setAttribute('role', 'tab'); tabs.append(tab);
        }
        async function refresh(append = false) {
            if (loading || !valid()) return; loading = true; const started = revision;
            try {
                const start = append ? offset : 0;
                if (focused && !append && !first) focused = await api('proposals/' + get(focused, 'id'));
                const proposals = await api('proposals?skip=' + start + '&take=50&view=' + view+(allUsers?'&scope=all-users':'')); if (!valid() || revision !== started) return;
                const snapshot = JSON.stringify([view,allUsers,Number(get(integration,'recentHistoryDays'))||7,Math.floor(Date.now()/86400000), proposals, focused], (key, value) => key.toLowerCase() === 'evaluatedat' ? undefined : value);
                if (!append && snapshot === lastSnapshot) { refreshed = Date.now(); return; }
                lastSnapshot = append ? '' : snapshot;
                if (!append) { rows.replaceChildren(); seen.clear();historyGroups.clear(); }
                const records = [...proposals];
                const focusedView = focused && ['Completed', 'Cancelled', 'Expired'].includes(get(focused, 'status')) ? 'history' : 'active';
                if (!append && focused && focusedView === view && !records.some(p => sameId(get(p, 'id'), get(focused, 'id')))) records.unshift(focused);
                let selected;
                for (const p of records) {
                    const key = String(get(p, 'id')).replaceAll('-', '').toLowerCase(); if (seen.has(key)) continue; seen.add(key);
                    const row = node('article', undefined, 'dq-queue-row'); renderProposal(p, row);
                    row.dataset.requestId = get(p, 'id');
                    if (focused && sameId(get(p, 'id'), get(focused, 'id'))) { row.classList.add('dq-highlight'); row.tabIndex = -1; row.setAttribute('aria-label', 'Selected removal request'); selected = row; }
                    if(view==='history'){
                        const cutoff=Number(get(integration,'recentHistoryDays'))||7,closed=(get(p,'timeline')||[]).find(s=>get(s,'key')==='closed'),at=date(get(closed,'at')),recent=at&&at>=new Date(Date.now()-cutoff*86400000),group=recent?'recent':'older';
                        if(!historyGroups.has(group)){const section=node('section');section.dataset.historyGroup=group;section.append(node('h3',recent?'Recent · '+cutoff+' days':'Older'));historyGroups.set(group,section);if(recent)rows.prepend(section);else rows.append(section);}historyGroups.get(group).append(row);
                    }else rows.append(row);
                }
                if (first && selected) requestAnimationFrame(() => { if (valid() && selected.isConnected) { selected.focus({ preventScroll: true }); selected.scrollIntoView({ block: 'nearest', behavior: 'auto' }); } });
                first = false;
                offset = start + proposals.length; more = proposals.length === 50; refreshed = Date.now();
                if (!seen.size) rows.append(node('p', 'No removal requests.'));
                for (const tab of tabs.querySelectorAll('[role=tab]')) { tab.setAttribute('aria-selected', String(tab.dataset.view === view)); tab.tabIndex = tab.dataset.view === view ? 0 : -1; }
            } catch (error) { if (valid()) { rows.replaceChildren(node('p', error.message || 'Requests unavailable.')); seen.clear(); focused = null; more = false; showError(error, host.closest('dialog')); } }
            finally { loading = false; if (valid() && revision !== started) void refresh(); }
        }
        tabs.addEventListener('keydown', event => { if (['ArrowLeft','ArrowRight','Home','End'].includes(event.key)) { event.preventDefault(); const controls = [...tabs.querySelectorAll('button')]; const current = controls.indexOf(document.activeElement); const index = event.key === 'Home' ? 0 : event.key === 'End' ? controls.length - 1 : (current + (event.key === 'ArrowRight' ? 1 : controls.length - 1)) % controls.length; controls[index].focus(); controls[index].click(); } });
        host.addEventListener('pointerdown', event => { if (!event.target.closest('button, input, a, .dq-help, .dq-timeline') && Date.now() - refreshed > 5000) void refresh(); });
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
            const personalRows = [], personalInputs = {};
            for (const [key, text] of [['activityDays', 'Playback lookback (days; 0 inherits)'], ['administratorApprovalDays', 'Administrator decision limit (days; 0 inherits)'],['recentHistoryDays','Recent history (days; 0 inherits)']]) {
                const container = node('div', undefined, 'inputContainer'), caption = node('label', text, 'inputLabel'), input = node('input', undefined, 'emby-input');
                input.type = 'number'; input.min = '0'; input.max = '365'; input.step = '1'; input.value = get(pref, key) || 0; input.id = 'dq-personal-' + command(); caption.htmlFor = input.id; input.setAttribute('is', 'emby-input'); container.append(caption, input); personalRows.push(container); personalInputs[key] = input;
            }
            panel.content.append(row, hoursRow, ...personalRows, node('p', 'Administrator minimums always apply. Reducing preferences never shortens an existing removal deadline.'), button('Save preferences', async () => {
                if (!hours.reportValidity()) return;
                if (!Object.values(personalInputs).every(input => input.reportValidity())) return;
                await api('me/preferences', 'PATCH', { commandId: command(), revision: get(pref, 'revision'), delayHours: Number(hours.value), notifications: notifications.checked, activityDays: Number(personalInputs.activityDays.value), administratorApprovalDays: Number(personalInputs.administratorApprovalDays.value),recentHistoryDays:Number(personalInputs.recentHistoryDays.value) });
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
            const queued = get(itemStates.get(id)?.data, 'proposals')?.[0];
            return queued ? proposal(get(queued, 'id')) : propose(guid(get(itemStates.get(id)?.data,'targetItemId')) || id);
        }, cls + ' dq-media-action dq-web-owned');
        b.dataset.dqItem = id; b.hidden = true; b.setAttribute('aria-label', 'Request removal'); b.title = 'Request removal';
        b.replaceChildren(node('span', 'delete_outline', 'material-icons')); b.firstChild.setAttribute('aria-hidden', 'true');
        void updateMediaState(b, id);
        return b;
    }
    function stageAppearance(p) {
        const status = get(p, 'status');
        const icon = ({ Blocked: get(p, 'reason') === 'ConsentRequired' ? 'how_to_vote' : 'pending_actions', Waiting: 'hourglass_top', Ready: 'event_available', Claimed: 'auto_delete', Uncertain: 'warning_amber', FilesDeleted: 'task_alt', Completed: 'task_alt', Cancelled: 'undo', Expired: 'event_busy' })[status] || 'delete_outline';
        return { icon, label: get(p, 'statusLabel') || 'Pending checks', status };
    }
    function unavailableReason(code) {
        return ({ NotYourRequest: 'Not your request', ProposalNotPermitted: 'Permission required', TargetUnavailable: 'Item unavailable',
            SeerrNotConfigured: 'Service not configured', NotificationsRequired: 'Notifications required', UpstreamUnavailable: 'Service unavailable',
            TargetEditionUnverified: 'Edition not verified', SeerrTargetNotManaged: 'Item not managed', IntegrityUnavailable: 'Storage unavailable' })[code] || 'Verification unavailable';
    }
    function paintStage(control, p) {
        const stage = stageAppearance(p); control.dataset.removalStatus = stage.status;
        const icon = control.querySelector('.material-icons'); if (icon) icon.textContent = stage.icon;
        const label = 'Removal · ' + stage.label;
        control.setAttribute('aria-label', label); control.title = label;
        let text = control.querySelector('.dq-action-label'); if (!text) { text = node('span', undefined, 'dq-action-label'); control.append(text); }
        text.textContent = label;
    }
    async function updateMediaState(control, id) {
        try {
            let cached = itemStates.get(id);
            if (!cached || Date.now() - cached.at > 30000) {
                if (itemStates.size >= 200) itemStates.clear();
                cached = { at: Date.now(), promise: api('targets/items/' + id + '/removal') }; itemStates.set(id, cached);
                cached.data = await cached.promise;
            } else if (!cached.data) cached.data = await cached.promise;
            if (!control.isConnected || control.dataset.dqItem !== id) return;
            control.hidden = get(cached.data, 'reason') === 'TargetUnavailable';
            const p = get(cached.data, 'proposals')?.[0];
            control.disabled = !p && get(cached.data, 'canPropose') === false;
            const signature = p ? JSON.stringify([get(p, 'id'), get(p, 'revision'), get(p, 'status'), get(p, 'statusLabel')]) : JSON.stringify([get(cached.data, 'canPropose'), get(cached.data, 'reason')]);
            if (control.dataset.removalSignature === signature) return;
            control.dataset.removalSignature = signature;
            if (p) paintStage(control, p);
            else { control.removeAttribute('data-removal-status'); control.setAttribute('aria-label', 'Request removal'); control.title = 'Request removal'; control.replaceChildren(node('span', 'delete_outline', 'material-icons'), node('span', 'Request removal', 'dq-action-label')); }
            if (control.disabled) { const reason = unavailableReason(get(cached.data, 'reason')); control.setAttribute('aria-label', reason); control.title = reason; control.querySelector('.dq-action-label').textContent = reason; }
            const host = control.closest('.je-request-card') || control.closest('.mainDetailButtons')?.parentElement;
            if (host) {
                host.querySelector(':scope > .dq-item-progress')?.remove();
                if (p) { const progress = node('div', undefined, 'dq-item-progress dq-web-owned'); renderTimeline(p, progress); host.append(progress); }
            }
        } catch (error) { if (control.isConnected) { control.hidden = error.code === 'TargetUnavailable'; control.disabled = true; const reason = unavailableReason(error.code); control.title = reason; control.setAttribute('aria-label', reason); control.replaceChildren(node('span', 'info_outline', 'material-icons'), node('span', reason, 'dq-action-label')); } }
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
            const section = tabs.closest('.je-requests-section'); if (!section) continue;
            const existingTab = tabs.querySelector('.dq-deletion-tab');
            if (existingTab) {
                if (pendingRequestFocus || Date.now() < fullQueueUntil || enhancedDeletionSelected && !section.classList.contains('dq-deletion-selected')) { fullQueueUntil = 0; existingTab.click(); }
                continue;
            }
            const view = node('div', undefined, 'dq-enhanced-view dq-web-owned'); view.hidden = true;
            const tab = button('Deletion queue', async () => {
                enhancedDeletionSelected = true; section.classList.add('dq-deletion-selected'); view.hidden = false;
                for (const sibling of tabs.querySelectorAll('button')) sibling.classList.toggle('active', sibling === tab);
                if (!view.children.length || pendingRequestFocus) {
                    const id = pendingRequestFocus; pendingRequestFocus = null; let focused;
                    if (id) focused = await api('proposals/' + id);
                    view.replaceChildren(); await queueView(view, focused && ['Completed', 'Cancelled', 'Expired'].includes(get(focused, 'status')) ? 'history' : 'active', undefined, view, focused);
                }
            }, 'je-requests-tab emby-button dq-deletion-tab dq-web-owned');
            tabs.addEventListener('click', event => {
                if (event.target.closest('button') && !event.target.closest('.dq-deletion-tab')) { enhancedDeletionSelected = false; section.classList.remove('dq-deletion-selected'); view.hidden = true; }
            }, true);
            tabs.append(tab); section.append(view);
            if (enhancedDeletionSelected || Date.now() < fullQueueUntil) { fullQueueUntil = 0; tab.click(); }
        }
        mountCalendar();
    }
    function mountCalendar() {
        document.querySelectorAll('.dq-calendar-removals').forEach(element => element.remove());
        const calendars = [...document.querySelectorAll('.je-calendar-layout')]; if (!calendars.length) return;
        const JE = window.JellyfinEnhanced, adapter = JE?.internals?.calendarPage, state = adapter?.state;
        // Reviewed Enhanced 12.13 contract. Do not infer dates from day numbers or
        // mutate unknown renderers; native rendering supplies all views and OR/AND/NOT.
        if (!Array.isArray(state?.events) || !(state.activeFilters instanceof Set) || typeof JE.calendarPage?.renderPage !== 'function') return;
        if (!calendarLoading && (!calendarData || Date.now() - calendarReadAt > 30000)) {
            calendarLoading = true; const generation = epoch;
            void (async () => {
                const all = [];
                try {
                    for (let skip = 0; ; skip += 100) {
                        if (skip >= 10000) throw new Error('Calendar capacity');
                        const page = await api('proposals?view=active&skip=' + skip + '&take=100'); all.push(...page);
                        if (page.length < 100) break;
                    }
                    if (generation === epoch) { calendarData = all; calendarReadAt = Date.now(); }
                } catch { if (generation === epoch) { calendarData = []; calendarReadAt = Date.now(); } }
                finally { if (generation === epoch) { calendarLoading = false; schedule(); } }
            })();
        }
        if (!calendarData) return;
        const events = calendarData.filter(p => date(get(p, 'scheduledFor'))).map(p => ({
            id: 'dq-removal:' + get(p, 'id'), title: get(p, 'title'), releaseDate: get(p, 'scheduledFor'),
            releaseType: 'DeletionDate', type: 'Movie', source: 'Deletion Queue', monitored: true, hasFile: false
        }));
        const existing = state.events.filter(event => String(event.id).startsWith('dq-removal:'));
        if (JSON.stringify(existing) !== JSON.stringify(events)) {
            state.events = state.events.filter(event => !String(event.id).startsWith('dq-removal:')).concat(events);
            JE.calendarPage.renderPage(); schedule(); return;
        }
        for (const calendar of calendars) {
            const legend = calendar.querySelector('.je-calendar-legend');
            if (legend && !legend.querySelector('.dq-calendar-filter')) {
                const filter = button('', async () => { JE.calendarPage.toggleFilter('DeletionDate'); schedule(); }, 'je-calendar-legend-item emby-button button-flat dq-calendar-filter dq-web-owned');
                const icon = node('span', 'event_busy', 'material-icons'); icon.setAttribute('aria-hidden', 'true');
                filter.append(icon, node('span', 'Deletion Date')); filter.setAttribute('aria-pressed', String(state.activeFilters.has('DeletionDate'))); legend.append(filter);
            }
            for (const event of calendar.querySelectorAll('[data-event-id^="dq-removal:"]')) {
                if (event.dataset.dqDecorated) continue;
                const id = event.dataset.eventId.slice('dq-removal:'.length), p = calendarData.find(p => String(get(p, 'id')) === id); if (!p) continue;
                event.dataset.dqDecorated = 'true'; event.classList.add('dq-calendar-event', 'dq-web-owned'); event.tabIndex = 0; event.setAttribute('role', 'button');
                event.setAttribute('aria-label', get(p, 'title') + ' · Deletion Date (earliest)');
                event.replaceChildren(node('span', 'event_busy', 'material-icons'), node('span', get(p, 'title'), 'je-calendar-event-title'), node('span', 'Deletion Date · no earlier than ' + date(get(p, 'scheduledFor')).toLocaleDateString(), 'dq-calendar-date'));
                event.title = 'Earliest possible removal. Fresh safety checks still apply.';
                const open = e => { e.preventDefault(); e.stopImmediatePropagation(); void openRequests(id); };
                event.addEventListener('click', open, true);
                event.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') open(e); });
            }
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
            const place = item => {
                const before = userAnchor?.parentElement === header ? userAnchor : null;
                if (item.parentElement !== header || item.nextSibling !== before) header.insertBefore(item, before);
            };
            if (twoFactorPending) {
                header.querySelectorAll('.dq-bell, .dq-local-shortcut').forEach(n => n.remove());
                if (!header.querySelector('.dq-two-factor')) {
                    const a = node('a', 'Complete 2FA to see notifications', 'dq-two-factor dq-web-owned'); a.href = '#/login'; header.append(a);
                }
                continue;
            }
            header.querySelectorAll('.dq-two-factor').forEach(n => n.remove());
            const hosted = !!document.querySelector('[data-notifications-bootstrap]') || [...document.querySelectorAll('[data-account-hub-bell]')].some(b => b.getClientRects().length > 0);
            if (hosted || get(integration, 'deviceBell') === false) {
                header.querySelectorAll('.dq-bell').forEach(n => n.remove());
                if (!header.querySelector('.dq-local-shortcut')) {
                    const shortcut = button('', queue, 'paper-icon-button-light headerButton dq-local-shortcut dq-web-owned');
                    shortcut.title = 'Deletion queue'; shortcut.setAttribute('aria-label', 'Deletion queue'); shortcut.setAttribute('aria-haspopup', 'dialog');
                    const icon = node('span', 'auto_delete', 'material-icons'); icon.setAttribute('aria-hidden', 'true'); shortcut.append(icon); place(shortcut);
                }
                place(header.querySelector('.dq-local-shortcut'));
                continue;
            }
            header.querySelectorAll('.dq-local-shortcut').forEach(n => n.remove());
            const existing = header.querySelector(':scope > .dq-bell');
            if (existing) { place(existing); continue; }
            const b = button('', center, 'emby-button button-flat headerButton dq-bell dq-web-owned');
            b.setAttribute('aria-haspopup', 'dialog');
            const icon = node('span', 'notifications', 'material-icons'); icon.setAttribute('aria-hidden', 'true');
            const badge = node('span', '', 'dq-badge'); badge.hidden = true; badge.setAttribute('aria-hidden', 'true');
            b.append(icon, badge); place(b);
        }
        updateBadges();
        if (twoFactorPending) return;
        mountEnhanced();
        const deepLink = guid(new URLSearchParams(location.hash.split('?')[1] || '').get('deletionQueueProposal'));
        if (deepLink && linkedProposal !== deepLink) { linkedProposal = deepLink; proposal(deepLink).catch(() => {}); }
        // Keep thumbnails untouched: library-card actions belong to the native menu.
        document.querySelectorAll('.card .dq-card-action').forEach(action => action.remove());
        const id = routeId();
        if (id && !capabilities.has(id)) {
            if (capabilities.size >= 200) capabilities.clear();
            capabilities.set(id, false); const generation = epoch;
            api('targets/items/' + id + '/removal').then(result => {
                if (generation === epoch) { itemStates.set(id, { at: Date.now(), data: result }); capabilities.set(id, Boolean(get(result, 'canPropose') || get(result, 'proposals')?.length)); schedule(); }
            }).catch(() => {});
        }
        for (const group of [...document.querySelectorAll('.mainDetailButtons')].slice(0, 5)) {
            const existing = group.querySelector('.dq-media-action');
            if (existing?.dataset.dqItem === id) { void updateMediaState(existing, id); continue; }
            existing?.remove(); if (id && capabilities.get(id)) group.append(mediaButton(id, 'emby-button button-flat detailButton'));
        }
        // Enhanced's available-request title/watch elements contain the authoritative native media ID.
        // No dependence on private renderer functions, card titles, usernames or third-party request IDs.
        for (const card of [...document.querySelectorAll('.je-request-card')].slice(0, 500)) {
            const id = guid(card.querySelector('[data-media-id]')?.dataset.mediaId);
            const metadata = card.querySelector('[data-tmdb-id][data-media-type]');
            const requestKey = id || (metadata ? metadata.dataset.mediaType + ':' + metadata.dataset.tmdbId : '');
            const existing = card.querySelector('.dq-request-action');
            if (existing?.dataset.dqItem === requestKey) { if (id) void updateMediaState(existing, id); continue; }
            existing?.remove();
            if (!requestKey) continue;
            if (!id) {
                const tmdb = Number(metadata?.dataset.tmdbId), type = metadata?.dataset.mediaType;
                if (!Number.isSafeInteger(tmdb) || tmdb <= 0 || !['movie', 'tv'].includes(type)) continue;
                if (!capabilities.has(requestKey)) {
                    capabilities.set(requestKey, false); const generation = epoch;
                    api('requests?skip=0&tmdbId=' + tmdb + '&mediaType=' + type).then(result => {
                        if (generation === epoch) { capabilities.set(requestKey, (get(result, 'requests') || []).some(request => Number.isSafeInteger(get(request, 'requestId')) && get(request, 'requestId') > 0)); schedule(); }
                    }).catch(() => {});
                }
                if (!capabilities.get(requestKey)) continue;
            }
            const b = id ? mediaButton(id, 'emby-button button-flat dq-request-action') : button('Request removal', () => requestRemoval(card), 'emby-button button-flat dq-request-action dq-web-owned');
            b.dataset.dqItem = requestKey;
            if (id) b.append(node('span', 'Request removal', 'dq-action-label'));
            else { const icon = node('span', 'delete_outline', 'material-icons'); icon.setAttribute('aria-hidden', 'true'); b.replaceChildren(icon, node('span', 'Request removal', 'dq-action-label')); }
            (card.querySelector('.je-request-actions') || card.querySelector('.je-request-info') || card).append(b);
        }
        if (context && Date.now() - context.at < 15000) {
            for (const menu of document.querySelectorAll('.actionSheet')) {
                if (!menu.getClientRects().length || menu.tagName === 'DIALOG' && !menu.open || context.before.has(menu) || menu.querySelector('.dq-context-section')) continue;
                const scroller = menu.querySelector('.actionSheetScroller'); if (!scroller) continue;
                const captured = context; context = null;
                void mountContextSection(menu, scroller, captured); break;
            }
        }
    }
    async function mountContextSection(menu, scroller, captured) {
        const section = node('section', undefined, 'dq-context-section dq-web-owned');
        section.hidden = true;
        section.setAttribute('aria-label', 'Deletion Queue');
        const heading = node('div', 'Deletion Queue', 'dq-context-heading');
        const content = node('div'); content.append(node('div', 'Checking removal status…', 'dq-context-message'));
        section.append(heading, content); scroller.append(section);
        const valid = () => section.isConnected && (menu.tagName !== 'DIALOG' || menu.open) && syncSession() && epoch === captured.epoch;
        function action(label, icon, run, selected) {
            const b = node('button', undefined, 'listItem listItem-button actionSheetMenuItem dq-context-action'); b.type = 'button'; b.setAttribute('is', 'emby-button'); b.setAttribute('aria-label', label);
            const glyph = node('span', icon, 'listItemIcon material-icons actionSheetItemIcon'); glyph.setAttribute('aria-hidden', 'true');
            const body = node('span', undefined, 'listItemBody'); body.append(node('span', label, 'listItemBodyText actionSheetItemText'));
            b.append(glyph, body);
            if (selected !== undefined) { b.setAttribute('aria-pressed', String(selected)); b.dataset.answer = icon === 'block' ? 'keep' : 'allow'; }
            b.addEventListener('click', () => {
                if (b.disabled || !valid()) return; section.querySelectorAll('button').forEach(control => { control.disabled = true; });
                // Cancel the native sheet; never invoke its destructive Delete command.
                let done = false;
                const watch = new MutationObserver(() => { if (!menu.isConnected) open(); });
                const cleanup = () => { watch.disconnect(); clearTimeout(timeout); };
                const open = () => { if (done) return; done = true; cleanup(); if (syncSession() && epoch === captured.epoch) Promise.resolve().then(run).catch(err => showError(err)); };
                const timeout = setTimeout(() => { done = true; cleanup(); }, 15000);
                menu.addEventListener('close', open, { once: true });
                watch.observe(document.body, { childList: true, subtree: true });
                const nativeDialog = menu.closest('dialog');
                if (nativeDialog?.open) { nativeDialog.addEventListener('close', open, { once: true }); nativeDialog.close(); }
                if (!menu.isConnected) open();
            });
            content.append(b);
        }
        try {
            const result = await api('targets/items/' + captured.id + '/removal'); if (!valid()) return;
            content.replaceChildren();
            const proposals = get(result, 'proposals') || [];
            if (!proposals.length) {
                if (get(result, 'canPropose')) { action(get(result,'wholeSeries')?'Request removal of entire series':'Request removal', 'delete_outline', () => propose(guid(get(result,'targetItemId'))||captured.id)); section.hidden = false; }
                else section.remove();
                return;
            }
            for (const p of proposals) {
                if (!get(p, 'canUndo') && get(p, 'canConsent') === false) continue;
                const id = get(p, 'id'), status = get(p, 'status'), answer = get(p, 'ownAnswer');
                const progress = node('div', undefined, 'dq-context-progress');
                progress.append(node('div', get(p, 'statusLabel') || 'Pending checks', 'dq-context-status'));
                const scheduled = date(get(p, 'scheduledFor'));
                if (scheduled) progress.append(node('div', 'No earlier than · ' + scheduled.toLocaleDateString(), 'dq-hint'));
                renderTimeline(p, progress); content.append(progress);
                const stage = stageAppearance(p);
                action('Removal · ' + stage.label, stage.icon, () => proposal(id));
                const stateAction = content.lastElementChild; stateAction.dataset.removalStatus = stage.status;
                if (get(p, 'canConsent') !== false && !['Claimed', 'Uncertain', 'FilesDeleted'].includes(status)) {
                    for (const [label, value, icon] of [['Allow removal', 0, 'check_circle_outline'], ['Keep item (reject removal)', 1, 'block']]) {
                        action(label, icon, async () => {
                            await api('proposals/' + id + '/consent', 'POST', { commandId: command(), revision: get(p, 'revision'), snapshot: get(p, 'ownSnapshot'), answer: value });
                            window.dispatchEvent(new Event('suite-removal-changed')); await loadNotices();
                        }, answer === value || answer === (value === 0 ? 'Yes' : 'No'));
                    }
                }
                if (get(p, 'canUndo')) action('Undo removal request', 'undo', async () => {
                    if (!confirm('Cancel this removal request?')) return;
                    await api('proposals/' + id + '/undo', 'POST', { commandId: command(), revision: get(p, 'revision') });
                    window.dispatchEvent(new Event('suite-removal-changed')); await loadNotices();
                });
            }
            if (!content.querySelector('button')) section.remove();
            else section.hidden = false;
        } catch { section.remove(); }
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
        document.querySelectorAll('.dq-context-section').forEach(section => section.remove());
        if (id) { context = { id, at: Date.now(), epoch, before: new Set([...document.querySelectorAll('.actionSheet')].filter(menu => menu.getClientRects().length && (menu.tagName !== 'DIALOG' || menu.open))) }; schedule(); }
    }
    document.addEventListener('contextmenu', captureContext, true);
    document.addEventListener('click', event => {
        if (event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        const link = event.target.closest?.('a[href]'); if (!link) return;
        let destination; try { destination = new URL(link.href, location.href); } catch { return; }
        const web = new URL(serverRoot + '/web/');
        if (destination.origin !== web.origin || ![web.pathname, web.pathname + 'index.html'].includes(destination.pathname)) return;
        const id = guid(new URLSearchParams(destination.hash.split('?')[1] || '').get('deletionQueueProposal'));
        if (!id || !syncSession()) return;
        event.preventDefault(); event.stopPropagation(); void proposal(id).catch(error => showError(error));
    }, true);
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
    window.addEventListener('suite-removal-changed', () => { itemStates.clear(); calendarReadAt = 0; schedule(); });
    setInterval(() => {
        const changed = session()?.key !== sessionKey;
        if (!syncSession()) return;
        if (changed) schedule();
        if (!document.hidden && Date.now() - lastPoll >= 30000) { lastPoll = Date.now(); loadNotices(); }
    }, 1000);
    schedule(); loadNotices();
}());
