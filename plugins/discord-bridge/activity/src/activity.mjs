// Discord context is UI context only, never a Jellyfin authentication grant.
export function serverBase(value) {
    const base = new URL(value);
    if (base.protocol !== 'https:' || base.username || base.password || base.search || base.hash) throw Error('Server unavailable');
    base.pathname = base.pathname.replace(/\/$/, '') + '/';
    return base;
}
export function invitation(value, baseUrl) {
    const base = serverBase(baseUrl), link = new URL(value);
    if (value.length > 2048 || link.origin !== base.origin || link.pathname !== base.pathname + 'PrivateSyncPlay/Watch'
        || link.username || link.password || link.search || !/^#[A-Za-z0-9_-]{20,512}$/.test(link.hash)) throw Error('Invalid invitation');
    return link.href;
}
export async function mountActivity(root, { createSdk, fetchConfig, location, signal, timeoutMs = 15000 }) {
    const status = root.querySelector('[data-status]'), input = root.querySelector('input'), form = root.querySelector('form');
    const open = root.querySelector('[data-open]'), join = root.querySelector('[data-join]');
    const controller = new AbortController();
    let sdk, base, alive = true, busy = false, timer;
    const clear = () => { input.value = ''; };
    const message = text => { if (alive) status.textContent = text; };
    const enabled = value => { open.disabled = join.disabled = !value || busy; };
    const close = () => { try { sdk?.close?.(1000, 'Activity closed'); } catch { /* No secret-bearing diagnostics. */ } };
    const dispose = () => { alive = false; controller.abort(); clearTimeout(timer); clear(); enabled(false); close(); signal?.removeEventListener('abort', dispose); };
    signal?.addEventListener('abort', dispose, { once: true });
    if (signal?.aborted) { dispose(); return { dispose }; }
    // Never accept bearer fragments/query parameters as Activity launch data.
    // SDK-required query parameters remain available for its own handshake.
    if (location.hash) location.replaceState?.(null, '', location.pathname + location.search);
    enabled(false);
    async function handoff(url) {
        if (!alive || busy || !sdk) return;
        busy = true; enabled(false); clear();
        try {
            const result = await sdk.commands.openExternalLink({ url });
            if (result?.opened === false) throw Error();
            message('Opened Jellyfin. Admission, identity and playback permissions are checked there.');
        } catch { message('Opening could not be confirmed. No invitation was redeemed or automatically retried.'); }
        finally { busy = false; if (alive) enabled(true); }
    }
    open.onclick = () => { if (base && !open.disabled) void handoff(new URL('web/index.html', base).href); };
    form.onsubmit = event => {
        event.preventDefault();
        if (!alive || busy || join.disabled) { clear(); return; }
        let url;
        try { url = invitation(input.value.trim(), base.href); }
        catch { clear(); message('Paste a watch-party invitation from this Jellyfin server, with no extra query parameters.'); return; }
        void handoff(url);
    };
    try {
        const deadline = new Promise((_, reject) => { timer = setTimeout(() => { controller.abort(); reject(Error()); }, timeoutMs); });
        const cancelled = new Promise((_, reject) => controller.signal.addEventListener('abort', () => reject(Error()), { once: true }));
        await Promise.race([(async () => {
            const config = await fetchConfig(controller.signal);
            if (!alive || controller.signal.aborted) throw Error();
            if (!config.available || !/^[0-9]{1,20}$/.test(config.applicationId || '')) throw Error();
            base = serverBase(config.publicServerUrl);
            sdk = createSdk(config.applicationId, { disableConsoleLogOverride: true });
            await sdk.ready();
            if (!alive || controller.signal.aborted) throw Error();
            enabled(true);
            message('Ready. Playback opens in Jellyfin; this companion does not expose account or participant identities.');
        })(), deadline, cancelled]);
    } catch {
        controller.abort(); clear(); enabled(false); close();
        message('Activity unavailable. Launch from Discord and ask the administrator to enable Discord Bridge, configure its public HTTPS URL and install Private SyncPlay.');
    } finally { clearTimeout(timer); }
    return { dispose };
}
