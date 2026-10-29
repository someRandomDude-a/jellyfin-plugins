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
    const server=new URL('../',document.baseURI),loginUrl=new URL('web/index.html#/login',server);
    async function signedIn(){
        const tokens=new Set();
        for(const name of ['sessionStorage','localStorage'])try{const storage=window[name],saved=JSON.parse(storage.getItem('jellyfin_credentials')||'{}');for(const item of saved.Servers||saved.servers||[]){const addresses=[item.ManualAddress,item.LocalAddress,item.RemoteAddress,item.manualAddress,item.localAddress,item.remoteAddress];if(addresses.some(address=>{try{return new URL(address).href.replace(/\/+$/,'')===server.href.replace(/\/+$/,'');}catch{return false;}})){const value=item.AccessToken||item.accessToken;if(value)tokens.add(value);}}}catch{}
        if(tokens.size!==1)return false;
        const response=await fetch(new URL('AccountHub/api/v1/self',server),{headers:{Authorization:'MediaBrowser Token="'+[...tokens][0]+'"'},credentials:'omit',cache:'no-store',referrerPolicy:'no-referrer'});
        if(!response.ok)return false;const result=await response.json();return result.ok===true&&!!result.data?.userId;
    }
    function alreadySignedIn(){form.hidden=true;status.textContent='You are already signed in. This invitation has not been used. To create another account, sign out or open the invitation in a private browser window.';const link=document.createElement('a');link.href=new URL('web/index.html#/home',server).href;link.textContent='Open Jellyfin';status.append(' ',link);}
    async function post(action, body) {
        const response = await fetch(new URL('public/v1/invites/' + action, document.baseURI), {
            method: 'POST', credentials: 'omit', cache: 'no-store', referrerPolicy: 'no-referrer',
            headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ...body, token })
        });
        const result = await response.json();
        if (!response.ok || !result.ok) { const error = new Error(result.error?.code || 'unavailable'); error.status = response.status;error.errorId=result.error?.errorId||result.errorId||response.headers?.get?.('X-Suite-Error-Id'); throw error; }
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
            if(await signedIn()){alreadySignedIn();return;}
            const result = await post(recovery ? 'resume' : 'redeem', { claimId, username: username.value, password: chosen, passwordLoginDisabled: disabled });
            status.textContent = result.status==='completed'?'Account created. Continue to Jellyfin sign-in.':'Account setup needs attention.';
            if (result.status === 'completed') {
                if (claimKey) try { sessionStorage.removeItem(claimKey); } catch { /* no credentials are stored */ }
                form.hidden = true; token = '';
                const link = document.createElement('a');
                link.href = loginUrl.href;
                link.textContent = 'Sign in'; status.append(' ', link);
                location.replace(loginUrl.href);
            } else { recovery = true; form.querySelector('button').textContent = 'Explicitly resume my incomplete account'; }
        } catch (error) {
            recovery = recovery || error.message === 'remediation_pending' || !error.status || error.status >= 500;
            form.querySelector('button').textContent = recovery ? 'Explicitly resume my incomplete account' : 'Create account';
            const messages={invalid_username:'Invalid or unavailable username. Choose another username.',username_unavailable:'Invalid or unavailable username. Choose another username.',invalid_request:'Check the username and password requirements, then try again.',invite_unavailable:'This invitation is unavailable, expired or has no uses remaining.',rate_limited:'Too many attempts. Please wait before trying again.',password_login_disabled:'Account creation is unavailable on this connection. Contact your administrator.',remediation_pending:'Account setup could not be completed. Contact your administrator; do not start another redemption.',command_conflict:'This signup attempt has changed. Reopen the original invitation or contact your administrator.'};
            status.textContent = Object.hasOwn(messages,error.message)?messages[error.message]:(recovery?'We could not confirm account creation. Contact your administrator before trying again.':'Unable to create the account. Check your details or contact your administrator.');
            if(typeof error.errorId==='string'&&/^[a-f0-9-]{36}$/i.test(error.errorId))status.append(' Error ID: '+error.errorId);
            // A transport failure may follow a committed account. Preserve this in-memory claim, never issue another automatically.
        } finally { busy = false; form.querySelector('button').disabled = false; }
    });
    (async () => {
        try {
            if (!/^[A-Za-z0-9_-]{43}$/.test(token)) throw new Error('Invalid invitation.');
            if(await signedIn()){alreadySignedIn();return;}
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
        } catch { status.textContent = 'This invitation is unavailable. It may be invalid, expired or fully used. Reopen the original link or contact your administrator.'; token = ''; }
    })();
})();
