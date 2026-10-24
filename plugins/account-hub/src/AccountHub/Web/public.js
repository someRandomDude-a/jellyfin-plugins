(() => {
    'use strict';
    const root = document.querySelector('.accountHubPublic'), status = root.querySelector('.hubPublicStatus'), content = root.querySelector('.hubPublicContent');
    const base = new URL(document.baseURI), server = new URL('../', base), token = new URLSearchParams(location.hash.slice(1)).get('token') || '';
    history.replaceState(null, '', location.pathname);
    const node = (tag, text) => { const value = document.createElement(tag); if (text) value.textContent = text; if (tag === 'button') { value.className = 'emby-button raised button-submit'; value.type = 'button'; value.setAttribute('is', 'emby-button'); } if (tag === 'input') { value.className = 'emby-input'; value.setAttribute('is', 'emby-input'); } return value; };
    function login() { const link = node('a', 'Sign in to Jellyfin in a separate tab, then return here'); link.href = new URL('web/index.html#/login', server); link.target = '_blank'; link.rel = 'noopener'; content.append(link); }
    function savedToken() {
        const matches = new Set();
        for (const storage of [sessionStorage, localStorage]) {
            try {
                const credentials = JSON.parse(storage.getItem('jellyfin_credentials') || '{}');
                for (const item of credentials.Servers || credentials.servers || []) {
                    const urls = [item.ManualAddress, item.LocalAddress, item.RemoteAddress, item.manualAddress, item.localAddress, item.remoteAddress];
                    if (urls.some(address => { try { return new URL(address).href.replace(/\/+$/, '') === server.href.replace(/\/+$/, ''); } catch { return false; } })) {
                        const credential = item.AccessToken || item.accessToken; if (credential) matches.add(credential);
                    }
                }
            } catch { }
        }
        return matches.size === 1 ? Array.from(matches)[0] : null;
    }
    async function request(path, payload, authenticated = false) {
        const headers = { 'Content-Type': 'application/json' };
        if (authenticated) { const value = savedToken(); if (!value) throw Error('Sign in to Jellyfin first. Ambiguous or other-server credentials are not used.'); headers.Authorization = 'MediaBrowser Token="' + value + '"'; }
        const response = await fetch(new URL('AccountHub/' + path, server), { method: 'POST', headers, credentials: 'omit', cache: 'no-store', body: JSON.stringify(payload) });
        const body = await response.json();
        if ((response.status === 401 || response.status === 403) && body.twoFactorRequired) throw Error('Complete Jellyfin Security 2FA in Jellyfin, then return.');
        if (!response.ok || !body.ok) throw Error(body.error?.code === 'remediation_pending' ? 'The password may have changed. Session revocation needs administrator review; do not retry the password change.' : body.error?.code || 'Request failed');
        return body.data;
    }
    function input(label, id) {
        const wrapper = node('div'); wrapper.className = 'inputContainer'; const caption = node('label', label); caption.className = 'inputLabel'; caption.htmlFor = id;
        const value = node('input'); value.id = id; value.type = 'password'; value.autocomplete = 'new-password'; value.required = true; value.maxLength = 1024; wrapper.append(caption, value); return { wrapper, value };
    }
    if (!/^[A-Za-z0-9_-]{43}$/.test(token)) { status.textContent = 'This link has no valid token. Request a new private link.'; return; }
    if (root.dataset.page === 'link') {
        status.textContent = 'Confirm linking this Discord account only if you requested /link yourself.'; login();
        const preview = node('button', 'Review link'); content.append(preview);
        preview.onclick = async () => {
            preview.disabled = true;
            try {
                const result = await request('api/v1/discord/link/lookup', { token }, true);
                status.textContent = 'Link Jellyfin account ' + result.jellyfinUsername + ' to Discord ID ' + result.discordId + '. This grants private recovery and notifications to that Discord identity. Verify both accounts before confirming.';
                const confirm = node('button', 'Link my Discord account'); content.append(confirm); preview.remove();
                confirm.onclick = async () => { confirm.disabled = true; try { await request('api/v1/discord/link/confirm', { token, confirmation: 'LINK MY DISCORD ACCOUNT' }, true); content.replaceChildren(); status.textContent = 'Discord linked.'; } catch (error) { status.textContent = error.message; confirm.disabled = false; } };
            } catch (error) { status.textContent = error.message; preview.disabled = false; }
        };
    } else {
        (async () => {
            try {
                const lookup = await request('public/v1/resets/lookup', { token });
                if (!lookup.valid) throw Error('This recovery link is invalid, expired or consumed. Request a new link through /reset.');
                let disabled = false;
                try { const hint = await fetch(new URL('TwoFactorAuth/public-config', server), { credentials: 'omit', cache: 'no-store' }); if (hint.ok) disabled = (await hint.json()).passwordLoginDisabled === true; } catch { }
                if (disabled) throw Error('Local password login is disabled for this browser. Contact the server administrator.');
                status.textContent = 'Choose your own new password. Existing sessions will be revoked; 2FA is preserved.';
                const form = node('form'), first = input('New password', 'new-password'), second = input('Confirm password', 'confirm-password'), submit = node('button', 'Reset my password'); submit.type = 'submit'; form.append(first.wrapper, second.wrapper, submit); content.append(form);
                form.onsubmit = async event => {
                    event.preventDefault(); if (first.value.value !== second.value.value) { status.textContent = 'Passwords do not match.'; return; } submit.disabled = true;
                    try { const result = await request('public/v1/resets/complete', { token, password: first.value.value, passwordLoginDisabled: disabled }); content.replaceChildren(); status.textContent = result.message; login(); }
                    catch (error) { status.textContent = error.message; submit.disabled = false; }
                    finally { first.value.value = second.value.value = ''; }
                };
            } catch (error) { status.textContent = error.message; }
        })();
    }
})();
