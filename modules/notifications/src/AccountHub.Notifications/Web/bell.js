/* Account Hub hosted-web adapter. Load once inside Jellyfin's authenticated web context. */
(() => {
    'use strict';
    if (window.__accountHubBellV1) return;
    window.__accountHubBellV1 = true;
    let button, panel, timer, etag, rows = [], hasMore = false, paused = false, delay = 30000, identity, generation = 0, inFlight = false, skip = 0;
    const forbidden = () => /login|challenge|twofactorauth|invite|reset|linktoken/i.test(location.href);
    const client = () => window.ApiClient;
    const url = path => client().getUrl('AccountHub/api/v1/' + path);
    const element = (tag, text) => { const e = document.createElement(tag); if (tag === 'button') { e.className = 'emby-button raised'; e.setAttribute('is', 'emby-button'); e.type = 'button'; } if (text) e.textContent = text; return e; };
    function clear() {
        clearTimeout(timer); timer = null; etag = undefined; rows = [];
        generation++; skip = 0; hasMore = false;
        button?.remove(); panel?.remove(); button = panel = null;
    }
    async function request(path, method = 'GET', body, headers = {}) {
        const started = generation;
        const startedToken = client().accessToken();
        const response = await fetch(url(path), {
            method, credentials: 'omit', cache: 'no-store',
            headers: { Authorization: 'MediaBrowser Token="' + client().accessToken() + '"',
                'Content-Type': 'application/json', ...headers }, body: body === undefined ? undefined : JSON.stringify(body)
        });
        if (response.status === 304) return { unchanged: true };
        const json = await response.json().catch(() => ({}));
        if (started !== generation || startedToken !== client().accessToken()) throw new Error('stale_session');
        if ((response.status === 401 || response.status === 403) && json.twoFactorRequired === true) {
            paused = true; rows = []; etag = undefined; panel?.replaceChildren();
            button.textContent = 'Complete 2FA'; button.setAttribute('aria-label', 'Complete 2FA to see notifications');
            const link = element('a', 'Complete 2FA in Jellyfin Security');
            link.href = client().getUrl('TwoFactorAuth/Login'); panel?.append(link);
            throw new Error('two_factor_required');
        }
        if (!response.ok || json.ok !== true) {
            if (response.status === 401 || response.status === 403) { rows = []; etag = undefined; panel?.replaceChildren(); paused = true; }
            throw new Error('request_failed');
        }
        return { ...json.data, etag: response.headers.get('ETag') };
    }
    function close() { if (panel) panel.hidden = true; button?.setAttribute('aria-expanded', 'false'); button?.focus(); }
    function render() {
        if (!panel) return;
        panel.replaceChildren(element('h2', 'Notifications'));
        const all = element('button', 'Mark all read'); all.onclick = async () => { await request('notifications/read-all', 'POST'); etag = undefined; poll(); }; panel.append(all);
        for (const row of rows) {
            const item = element('section'); item.append(element('h3', row.event.title), element('p', row.event.body));
            // Links are validated server-side; text is never inserted as HTML.
            if (row.event.link) { const link = element('a', 'Open'); link.href = row.event.link.startsWith('web/index.html#/') ? client().getUrl(row.event.link) : row.event.link; item.append(link); }
            for (const action of ['read', 'dismiss']) {
                const control = element('button', action === 'read' ? 'Mark read' : 'Dismiss');
                control.onclick = async () => { await request('notifications/' + row.id + '/' + action, 'POST'); etag = undefined; poll(); };
                item.append(control);
            }
            panel.append(item);
        }
        const previous = element('button', 'Newer notifications'); previous.disabled = skip === 0;
        previous.onclick = () => { skip = Math.max(0, skip - 100); etag = undefined; poll(); };
        const next = element('button', 'Older notifications'); next.disabled = !hasMore;
        next.onclick = () => { skip += 100; etag = undefined; poll(); }; panel.append(previous, next);
        for (const channel of ['discord', 'in-app']) for (const enabled of [true, false]) {
            const prefs = element('button', (enabled ? 'Enable' : 'Mute') + ' non-critical ' + channel + ' notifications');
            prefs.onclick = async () => {
                try {
                    const current = await request('notifications/preferences');
                    const values = current.preferences.filter(p => !(p.source === '*' && p.type === '*' && p.channel === channel));
                    values.push({ source: '*', type: '*', channel, enabled });
                    await request('notifications/preferences', 'PUT', values);
                    prefs.textContent = 'Preference saved';
                } catch { prefs.textContent = 'Preference could not be saved'; }
            }; panel.append(prefs);
        }
        const library = element('details'); library.append(element('summary', 'New episodes and movies: personal preferences'));
        for (const [kind, label] of [['episode', 'new episodes'], ['movie', 'new movies']]) for (const channel of ['in-app', 'discord']) for (const enabled of [true, false]) {
            const prefs = element('button', (enabled ? 'Enable ' : 'Mute ') + label + ' on ' + channel);
            prefs.onclick = async () => {
                try {
                    const current = await request('notifications/preferences'), type = 'library.' + kind + '.added';
                    const values = current.preferences.filter(p => !(p.source === 'account-hub' && p.type === type && p.channel === channel));
                    values.push({ source: 'account-hub', type, channel, enabled });
                    await request('notifications/preferences', 'PUT', values); prefs.textContent = 'Preference saved';
                } catch { prefs.textContent = 'Preference could not be saved'; }
            }; library.append(prefs);
        }
        library.append(element('p', 'Applies to future personal inbox/DM notifications when enabled by your administrator. Shared Discord announcements are managed separately by administrators.'));
        panel.append(library);
        // Do not hide or delete DeletionQueue's fallback bell. Expose its inbox through this surface
        // only when its adapter explicitly registers a reachable link and agrees to hand off mounting.
        const fallback = window.AccountHubLocalInbox;
        if (fallback && typeof fallback.href === 'string' && fallback.href.startsWith('#/')) {
            const link = element('a', 'DeletionQueue local notices'); link.href = fallback.href; panel.append(link);
        }
        const dismiss = element('button', 'Close notifications'); dismiss.onclick = close; panel.append(dismiss);
    }
    function mount() {
        if (forbidden() || !client()?.accessToken?.() || !client()?.getCurrentUserId?.()) { clear(); return false; }
        const nextIdentity = url('notifications') + ':' + client().getCurrentUserId();
        if (identity !== nextIdentity) { clear(); paused = false; identity = nextIdentity; }
        if (button?.isConnected) return true;
        const anchor = document.querySelector('.headerUserButton, [aria-controls="app-user-menu"]');
        if (!anchor?.parentElement || document.querySelector('[data-account-hub-bell]')) return false;
        button = element('button', 'Notifications'); button.dataset.accountHubBell = 'v1';
        button.setAttribute('aria-label', 'Notifications'); button.setAttribute('aria-expanded', 'false');
        button.setAttribute('aria-live', 'polite');
        button.setAttribute('aria-controls', 'account-hub-inbox');
        const announcement = element('span'); announcement.setAttribute('role', 'status'); announcement.setAttribute('aria-live', 'polite');
        button.append(announcement);
        panel = element('div'); panel.id = 'account-hub-inbox'; panel.hidden = true;
        panel.className = 'dialog paperList accountHubInbox';
        panel.setAttribute('role', 'dialog'); panel.setAttribute('aria-label', 'Notifications'); panel.tabIndex = -1;
        // Non-modal: no focus trap that could interfere with Jellyfin navigation.
        Object.assign(panel.style, { position: 'fixed', right: '1rem', top: '4rem', zIndex: '2000',
            maxHeight: '75vh', overflow: 'auto', maxWidth: '32rem', padding: '1rem' });
        panel.addEventListener('keydown', e => { if (e.key === 'Escape') { e.preventDefault(); close(); } });
        button.onclick = () => { panel.hidden = !panel.hidden; button.setAttribute('aria-expanded', String(!panel.hidden)); if (!panel.hidden) panel.focus(); };
        anchor.parentElement.insertBefore(button, anchor); document.body.append(panel);
        return true;
    }
    async function poll() {
        clearTimeout(timer);
        timer = null;
        if (document.hidden || !mount() || paused || inFlight) return;
        const activeIdentity = identity;
        const activeGeneration = generation;
        inFlight = true;
        try {
            const response = await request('notifications?skip=' + skip + '&limit=100', 'GET', undefined, etag ? { 'If-None-Match': etag } : {});
            if (identity !== activeIdentity || generation !== activeGeneration || forbidden()) return;
            if (!response.unchanged) { rows = response.notifications; hasMore = response.hasMore ?? rows.length === 100; etag = response.etag; render(); }
            const count = await request('notifications/unread-count');
            button.textContent = 'Notifications (' + count.count + ')'; button.setAttribute('aria-label', count.count + ' unread notifications');
            delay = 30000;
        } catch { delay = Math.min(delay * 2, 300000); }
        finally {
            inFlight = false;
            // Navigation can occur while a request is in flight; don't strand the new view.
            if (generation !== activeGeneration && !paused) timer = setTimeout(poll, 0);
        }
        if (!paused && generation === activeGeneration) timer = setTimeout(poll, delay);
    }
    const resume = () => { paused = false; poll(); };
    window.addEventListener('focus', resume);
    window.addEventListener('hashchange', () => { clear(); resume(); });
    document.addEventListener('visibilitychange', () => { if (document.hidden) clearTimeout(timer); else resume(); });
    // Coalesce DOM remounts without network traffic on every mutation.
    let scheduled = false;
    new MutationObserver(() => {
        if (scheduled) return; scheduled = true;
        requestAnimationFrame(() => { scheduled = false; if (!button?.isConnected && mount() && !timer) poll(); });
    }).observe(document.documentElement, { subtree: true, childList: true });
    poll();
})();
