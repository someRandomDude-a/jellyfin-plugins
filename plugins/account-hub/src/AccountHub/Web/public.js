(() => {
    'use strict';
    const root = document.querySelector('.accountHubPublic'), status = root.querySelector('.hubPublicStatus'), content = root.querySelector('.hubPublicContent');
    const base = new URL(document.baseURI), server = new URL('../', base), token = new URLSearchParams(location.hash.slice(1)).get('token') || '';
    const safeErrors=new WeakSet();
    const failure=(message,code)=>{const error=Error(message);error.code=code;safeErrors.add(error);return error;};
    history.replaceState(null, '', location.pathname);
    const node = (tag, text) => { const value = document.createElement(tag); if (text) value.textContent = text; if (tag === 'button') { value.className = 'emby-button raised button-submit'; value.type = 'button'; value.setAttribute('is', 'emby-button'); } if (tag === 'input') { value.className = 'emby-input'; value.setAttribute('is', 'emby-input'); } return value; };
    function login() { const link = node('a', 'Sign in'); link.className='hubPublicSignIn';link.href = new URL('web/index.html#/login', server); link.target = '_blank'; link.rel = 'noopener'; content.append(link); return link; }
    function linkLogin() {
        const button=node('button','Continue to Jellyfin sign-in');button.classList.add('hubPublicSignIn');content.append(button);let preparing=false;
        button.onclick=async()=>{
            if(preparing)return;preparing=true;button.disabled=true;
            try{
                await request('public/v1/discord/link/continue',{token,confirmation:'REVIEW AFTER SIGN-IN'},false,true);
                location.assign(new URL('web/index.html#/login?url=home%3FaccountHubLinkReturn%3D1',server).href);
            }catch(error){status.textContent=error.message;/* Unknown outcomes must not replay the handoff. Reopen the original link. */}
        };
        return button;
    }
    function savedToken() {
        const matches = new Set();
        for (const name of ['sessionStorage', 'localStorage']) {
            try {
                const storage=window[name];
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
    async function request(path, payload, authenticated = false, continuation = false) {
        try{return await send(path,payload,authenticated,continuation);}catch(error){if(error&&safeErrors.has(error))throw error;throw Error('We could not confirm the result. Check the current state or contact an administrator before trying again.');}
    }
    async function send(path, payload, authenticated = false, continuation = false) {
        const headers = { 'Content-Type': 'application/json' };
        if (authenticated) { const value = savedToken(); if (!value) throw failure('Sign in, then return here.','unauthorized'); headers.Authorization = 'MediaBrowser Token="' + value + '"'; }
        const response = await fetch(new URL('AccountHub/' + path, server), { method: 'POST', headers, credentials: continuation?'same-origin':'omit', cache: 'no-store', referrerPolicy:'no-referrer', body: JSON.stringify(payload) });
        const body = await response.json();
        if ((response.status === 401 || response.status === 403) && body.twoFactorRequired) throw failure('Complete two-step verification, then return here.','two_factor_required');
        if (!response.ok || !body.ok) {
            const code=body.error?.code||'request_failed';
            const messages={unauthorized:'Sign in again, then return here.',remediation_pending:'Please contact your administrator to finish recovery.',account_changed:'Your account changed. Review the link again.',link_expired:'This link expired. Request /link again in Discord.',password_login_disabled:'Password recovery is unavailable. Contact your administrator.',invalid_or_expired:'This link expired. Request /reset again in Discord.',rate_limited:'Too many attempts. Try again in a minute.',reset_account_mismatch:'Sign in to the account this reset link belongs to.',reset_verification_unavailable:'Session verification is unavailable. Contact your administrator.',reset_session_required:'Complete Jellyfin Security sign-in for this account, then return.',security_incompatible:'Session verification is unavailable. Contact your administrator.'};
            const id=body.error?.errorId||body.errorId||response.headers?.get?.('X-Suite-Error-Id');
            throw failure((Object.hasOwn(messages,code)?messages[code]:'Unable to continue. Please try again later.')+(typeof id==='string'&&/^[a-f0-9-]{36}$/i.test(id)?' Error ID: '+id:''),code);
        }
        return body.data;
    }
    function input(label, id) {
        const wrapper = node('div'); wrapper.className = 'inputContainer'; const caption = node('label', label); caption.className = 'inputLabel'; caption.htmlFor = id;
        const value = node('input'); value.id = id; value.type = 'password'; value.autocomplete = 'new-password'; value.required = true; value.maxLength = 1024; wrapper.append(caption, value); return { wrapper, value };
    }
    if (!/^[A-Za-z0-9_-]{43}$/.test(token)) { status.textContent = 'This link is invalid. Request a new link in Discord.'; return; }
    if (root.dataset.page === 'link') {
        status.textContent = 'Review the accounts before connecting Discord to Jellyfin.';
        const preview = node('button', 'Review link'); content.append(preview);let reviewing=false,linked=false,reviewedSession;
        const clearReview=()=>{content.querySelector('.hubPublicAccounts')?.remove();content.querySelector('.hubPublicConfirm')?.remove();reviewedSession=null;};
        const review=async()=>{
            if(reviewing||linked)return;
            const session=savedToken();
            if(!session){clearReview();preview.hidden=true;status.textContent='Continue to native Jellyfin sign-in in this tab, then review and confirm this connection.';if(!content.querySelector('.hubPublicSignIn'))linkLogin();return;}
            reviewing=true;clearReview();content.querySelector('.hubPublicSignIn')?.remove();
            preview.disabled = true;
            try {
                const result = await request('api/v1/discord/link/lookup', { token }, true);
                if(session!==savedToken()){status.textContent='Your signed-in account changed. Review the accounts again.';return;}
                reviewedSession=session;
                status.textContent = 'Check both accounts before linking.';
                content.querySelector('.hubPublicAccounts')?.remove();const accounts=node('dl');accounts.className='hubPublicAccounts';
                for(const [label,value] of [['Jellyfin',result.jellyfinUsername],['Discord',result.discordUsername?'@'+result.discordUsername:'Username unavailable'],['Discord ID',result.discordId]])accounts.append(node('dt',label),node('dd',value));content.append(accounts);
                const confirm = node('button', 'Link my Discord account');confirm.classList.add('hubPublicConfirm');content.append(confirm); preview.hidden=true;
                confirm.onclick = async () => {if(reviewedSession!==savedToken()){clearReview();preview.hidden=false;await review();return;} confirm.disabled = true; try { await request('api/v1/discord/link/confirm', { token, userId: result.userId, confirmation: 'LINK MY DISCORD ACCOUNT' }, true);linked=true; content.replaceChildren(); status.textContent = 'Discord linked. You can close this page.'; } catch (error) { status.textContent = error.message;if(error.code==='account_changed'){clearReview();preview.hidden=false;}else confirm.disabled = false; } };
            } catch (error) {clearReview();status.textContent=error.message;preview.hidden=false;if(error.code==='unauthorized'&&!content.querySelector('.hubPublicSignIn'))linkLogin();}
            finally{reviewing=false;preview.disabled=false;}
        };
        preview.onclick=review;
        addEventListener('focus',()=>{if(!linked&&savedToken()!==reviewedSession)void review();});
        addEventListener('storage',()=>{if(!linked&&savedToken()!==reviewedSession)void review();});
        void review();
    } else {
        (async () => {
            try {
                const lookup = await request('public/v1/resets/lookup', { token });
                if (!lookup.valid) throw Error('This link expired. Request /reset again in Discord.');
                if (lookup.verificationAvailable === false) throw Error('Session verification is unavailable. Contact your administrator.');
                let disabled = false;
                try { const hint = await fetch(new URL('TwoFactorAuth/public-config', server), { credentials: 'omit', cache: 'no-store' }); if (hint.ok) disabled = (await hint.json()).passwordLoginDisabled === true; } catch { }
                if (disabled) throw Error('Password recovery is unavailable. Contact your administrator.');
                status.textContent = 'Use a browser already signed in to this account with Jellyfin Security.'; login();
                const check = node('button', 'Check signed-in session'); content.append(check);
                check.onclick = async () => {
                    check.disabled = true;
                    try {
                        const session = await request('public/v1/resets/session', { token }, true);
                        if (session.ready !== true) throw Error('Unable to verify this session.');
                        check.remove(); status.textContent = 'Choose a new password.';
                        const form = node('form'), first = input('New password', 'new-password'), second = input('Confirm password', 'confirm-password'), submit = node('button', 'Reset my password');
                        submit.type = 'submit'; form.append(first.wrapper, second.wrapper, submit); content.append(form);let completing=false;
                        addEventListener('pagehide',()=>{first.value.value=second.value.value='';},{once:true});
                        form.onsubmit = async event => {
                            event.preventDefault();if(completing)return; if (first.value.value !== second.value.value) { status.textContent = 'Passwords do not match.'; return; } submit.disabled = true;completing=true;
                            try { await request('public/v1/resets/complete', { token, password: first.value.value }, true); content.replaceChildren(); status.textContent = 'Password updated. Sign in with your new password.'; login(); }
                            catch (error) { if(!error.code||error.code==='remediation_pending'){status.textContent=error.code==='remediation_pending'?error.message:'Unable to confirm the change. Try signing in or contact your administrator.';first.value.disabled=second.value.disabled=true;}else{status.textContent = error.message; submit.disabled = false;completing=false;} }
                            finally { first.value.value = second.value.value = ''; }
                        };
                    } catch (error) { status.textContent = error.message; check.disabled = false; }
                };
                await check.onclick();
            } catch (error) { status.textContent = error.message; }
        })();
    }
})();
