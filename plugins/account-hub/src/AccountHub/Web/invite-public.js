(function () {
    'use strict';
    let token = new URLSearchParams(location.hash.slice(1)).get('token') || '';
    history.replaceState(null, '', location.pathname);
    const status = document.getElementById('invite-status');
    const form = document.getElementById('invite-form');
    const username = document.getElementById('invite-username');
    const password = document.getElementById('invite-password');
    const confirm = document.getElementById('invite-confirm');
    // Persist only a bounded correlation ID under a one-way token fingerprint in this tab.
    // Reopening the original invitation is still required; never persist its credential or password.
    let claimId = crypto.randomUUID(), claimKey;
    async function restoreClaim() {
        try {
            const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(token));
            claimKey = 'account-hub-invite-claim:' + [...new Uint8Array(digest)].map(value => value.toString(16).padStart(2, '0')).join('');
            const prefix = 'account-hub-invite-claim:', now = Date.now(), lifetime = 12 * 60 * 60 * 1000;
            for (const key of Object.keys(sessionStorage).filter(key => key.startsWith(prefix))) {
                try {
                    const entry = JSON.parse(sessionStorage.getItem(key));
                    if (!entry || !Number.isFinite(entry.expiresAt) || entry.expiresAt <= now || entry.expiresAt > now + lifetime || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(entry.id)) sessionStorage.removeItem(key);
                } catch { sessionStorage.removeItem(key); }
            }
            const old = JSON.parse(sessionStorage.getItem(claimKey) || 'null');
            if (old) claimId = old.id;
            const keys = Object.keys(sessionStorage).filter(key => key.startsWith(prefix) && key !== claimKey);
            while (keys.length >= 20) sessionStorage.removeItem(keys.shift());
            sessionStorage.setItem(claimKey, JSON.stringify({ id: claimId, expiresAt: old?.expiresAt || now + lifetime }));
        } catch { claimKey = undefined; /* Restricted/insecure storage keeps correlation in memory only. */ }
    }
    let disabled = false;
    let busy = false;
    let recovery = false;
    async function post(action, body) {
        const response = await fetch(new URL('public/v1/invites/' + action, document.baseURI), {
            method: 'POST', credentials: 'omit', cache: 'no-store', referrerPolicy: 'no-referrer',
            headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ...body, token })
        });
        const result = await response.json();
        if (!response.ok || !result.ok) { const error = new Error(result.error?.code || 'unavailable'); error.status = response.status; throw error; }
        return result.data;
    }
    function erase() { password.value = ''; confirm.value = ''; }
    window.addEventListener('pagehide', () => { token = ''; erase(); });
    form.addEventListener('submit', async event => {
        event.preventDefault();
        if (busy || !token) return;
        if (password.value !== confirm.value) { status.textContent = 'Passwords do not match.'; confirm.focus(); return; }
        busy = true; form.querySelector('button').disabled = true;
        const chosen = password.value;
        erase();
        try {
            const result = await post(recovery ? 'resume' : 'redeem', { claimId, username: username.value, password: chosen, passwordLoginDisabled: disabled });
            status.textContent = result.message;
            if (result.status === 'completed') {
                if (claimKey) try { sessionStorage.removeItem(claimKey); } catch { /* no credentials are stored */ }
                form.hidden = true; token = '';
                const link = document.createElement('a');
                link.href = new URL('../TwoFactorAuth/Setup', document.baseURI).href;
                link.textContent = 'Jellyfin Security setup'; status.append(' ', link);
            } else { recovery = true; form.querySelector('button').textContent = 'Explicitly resume my incomplete account'; }
        } catch (error) {
            recovery = recovery || error.message === 'remediation_pending' || !error.status || error.status >= 500;
            form.querySelector('button').textContent = recovery ? 'Explicitly resume my incomplete account' : 'Create account';
            status.textContent = error.message === 'remediation_pending' ? 'Onboarding interrupted. Contact your administrator; do not start another redemption.' : error.message;
            // A transport failure may follow a committed account. Preserve this in-memory claim, never issue another automatically.
        } finally { busy = false; form.querySelector('button').disabled = false; }
    });
    (async () => {
        try {
            if (!/^[A-Za-z0-9_-]{43}$/.test(token)) throw new Error('Invalid invitation.');
            await restoreClaim();
            try {
                const response = await fetch(new URL('../TwoFactorAuth/public-config', document.baseURI), { credentials: 'omit', cache: 'no-store', referrerPolicy: 'no-referrer' });
                if (response.ok) disabled = (await response.json()).passwordLoginDisabled === true;
            } catch (_) { /* The explicit Hub setting remains authoritative when Security is absent. */ }
            const result = await post('lookup', { claimId });
            if (!result.valid) throw new Error('Invitation is invalid, expired, revoked or fully reserved.');
            recovery = result.resumable === true;
            if (recovery) form.querySelector('button').textContent = 'Explicitly resume my incomplete account';
            status.textContent = disabled ? 'Local password login is disabled on this connection. Contact your administrator.' : 'Choose your own username and password. No automatic sign-in is performed.';
            form.hidden = disabled; if (!disabled) username.focus();
        } catch (error) { status.textContent = error.message; token = ''; }
    })();
})();
