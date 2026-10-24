import { securityLinks, securityRequest } from './security-client.mjs';

// Account Hub owns navigation, recent-event history and Settings UI. This mounts only
// the Security tab content in an existing admin page. Call returned cleanup on viewdestroy.
export function mountSecurityPanel(root, apiClient, { safeActions = false, pending2fa } = {}) {
    const lifecycle = new AbortController();
    let disposed = false;
    let busy = false;
    let status;
    const skips = { users: 0, devices: 0 };
    const limit = 50;
    const links = securityLinks(apiClient);
    const element = (tag, text) => {
        const node = document.createElement(tag);
        if (tag === 'button') { node.setAttribute('is', 'emby-button'); node.className = 'emby-button raised'; }
        if (tag === 'input') { node.setAttribute('is', 'emby-input'); node.className = 'emby-input'; }
        if (tag === 'table') node.className = 'tbl';
        if (tag === 'dialog') node.className = 'dialog';
        if (text !== undefined) node.textContent = String(text);
        return node;
    };
    const button = (text, action) => {
        const node = element('button', text);
        node.type = 'button';
        node.className = 'emby-button raised button-submit';
        node.addEventListener('click', () => perform(action));
        return node;
    };
    const banner = element('p');
    banner.setAttribute('role', 'status');
    const content = element('section');
    const message = element('p');
    message.setAttribute('role', 'alert');
    root.replaceChildren(element('h2', 'Jellyfin Security'), banner, content, message);

    async function perform(action) {
        if (busy || disposed) return;
        busy = true;
        message.textContent = '';
        try { await action(); }
        catch (error) { if (!disposed && error.name !== 'AbortError') message.textContent = error.message; }
        finally { busy = false; }
    }
    const request = (path, options) => securityRequest(apiClient, path, {
        signal: lifecycle.signal, pending2fa, ...options
    });
    const known = value => value === null || value === undefined ? 'Unknown' : String(value);
    const factor = value => value === null || value === undefined ? 'Unknown' : value ? 'Enabled' : 'Not enabled';

    async function refresh(force = false) {
        status = await request('security/status');
        if (disposed) return;
        banner.textContent = `Security ${status.compatibility}; running ${status.installedVersion || 'unknown'}, supported ${status.supportedVersion}.`;
        content.replaceChildren();
        content.append(button('Refresh', () => refresh(true)));
        if (!status.securityTabVisible) {
            content.append(element('p', 'Security status, badges and actions are disabled. Core Account Hub remains available.'));
            return;
        }
        content.append(element('p', `${status.mirrorWarning}. Current mirror: ${status.stepUpLevel}. Edit integration settings in Account Hub Settings.`));
        content.append(element('p', 'Force logout clears trusted devices and registered device IDs and terminates sessions. A password reset alone preserves Security trust and factors.'));
        for (const [name, href] of Object.entries(links)) {
            const anchor = element('a', { setup: 'Enrollment and own devices', login: 'Complete sign-in', admin: 'Factor management and full Security audit' }[name]);
            anchor.href = href;
            content.append(anchor, document.createTextNode(' · '));
        }
        const [users, devices] = await Promise.all([
            request('security/users', { query: { skip: skips.users, limit, refresh: force } }),
            request('security/devices', { query: { skip: skips.devices, limit, refresh: force } })
        ]);
        if (disposed) return;
        content.append(table('People 2FA status', ['User', 'TOTP', 'Email OTP', 'Passkeys', 'Trusted devices', 'Recovery codes remaining', 'Lockout'],
            users.items.map(user => [user.username, factor(user.totpEnabled), factor(user.emailOtpEnabled), known(user.passkeyCount),
                known(user.trustedDeviceCount), known(user.recoveryCodesRemaining), known(user.isLockedOut)])));
        content.append(paging('users', users.total));
        content.append(table('Trusted devices', ['User', 'Device label', 'Created', 'Last used', 'Actions'], devices.items.map(device => {
            const actions = element('span');
            if (safeActions) {
                actions.append(button('Revoke trust', () => command(device, false)), button('Force logout user', () => command(device, true)));
            } else actions.textContent = 'Actions not enabled';
            return [device.username, device.deviceName, known(device.createdAt), known(device.lastUsedAt), actions];
        })));
        content.append(paging('devices', devices.total));
    }

    function table(title, columns, rows) {
        const wrapper = element('section');
        const node = element('table');
        node.append(element('caption', title));
        const head = element('tr');
        for (const name of columns) { const cell = element('th', name); cell.scope = 'col'; head.append(cell); }
        const thead = element('thead'); thead.append(head); node.append(thead);
        const body = element('tbody');
        for (const row of rows) {
            const tr = element('tr');
            for (const value of row) { const cell = element('td'); cell.append(value instanceof Node ? value : document.createTextNode(known(value))); tr.append(cell); }
            body.append(tr);
        }
        node.append(body); wrapper.append(node);
        return wrapper;
    }

    function paging(key, total) {
        const node = element('div');
        const previous = button('Previous', async () => { skips[key] = Math.max(0, skips[key] - limit); await refresh(); });
        const next = button('Next', async () => { skips[key] += limit; await refresh(); });
        previous.disabled = skips[key] === 0;
        next.disabled = skips[key] + limit >= total;
        node.append(previous, document.createTextNode(` ${skips[key] + 1}–${Math.min(skips[key] + limit, total)} of ${total} `), next);
        return node;
    }

    async function command(device, forceLogout) {
        const expected = `${forceLogout ? 'FORCE LOGOUT' : 'REVOKE TRUST'} ${device.userId}`;
        const input = await confirmCommand(expected, `${device.username || device.userId}: ${forceLogout ? 'Clear all trust and terminate sessions' : 'Revoke ' + (device.deviceName || 'this trust record')}`);
        if (!input) return;
        const commandId = crypto.randomUUID();
        const path = forceLogout ? `security/users/${device.userId}/force-logout` : `security/devices/${device.userId}/${device.id}/revoke`;
        const result = await request(path, { method: 'POST', body: { commandId, revision: status.revision,
            confirmation: input.confirmation, stepUp: input.stepUp } });
        await refresh(true);
        message.textContent = result.remediationPending ? 'Result uncertain; remediation pending. Inspect current state before another command.'
            : result.detail || `Security reported: ${result.status}.`;
    }

    function confirmCommand(expected, description) {
        return new Promise(resolve => {
            const dialog = element('dialog');
            dialog.className = 'dialog formDialog';
            dialog.setAttribute('aria-label', description);
            const form = element('form');
            form.className = 'formDialogContent dialogContentInner';
            const title = element('h3', description);
            const typed = element('input'); typed.required = true; typed.autocomplete = 'off';
            const code = element('input'); code.type = 'password'; code.autocomplete = 'off'; code.maxLength = 256;
            const confirmationLabel = element('label', `Type ${expected} `); confirmationLabel.append(typed);
            const codeLabel = element('label', 'Fresh authenticator or recovery code (unless mirror is Off or you have no factors) '); codeLabel.append(code);
            for (const [label, control] of [[confirmationLabel, typed], [codeLabel, code]]) {
                label.className = 'inputLabel'; control.id = 'hub-security-' + crypto.randomUUID(); label.htmlFor = control.id;
                const wrap = element('div'); wrap.className = 'inputContainer'; label.removeChild(control); wrap.append(label, control);
                control.labelElement = label;
                control.addEventListener('focus', () => label.classList.add('inputLabelFocused')); control.addEventListener('blur', () => label.classList.remove('inputLabelFocused'));
            }
            const submit = element('button', 'Confirm'); submit.type = 'submit';
            const cancel = element('button', 'Cancel'); cancel.type = 'button';
            form.append(title, confirmationLabel.parentNode, codeLabel.parentNode, element('p', 'Recovery codes are single-use. No proof is cached. Email/passkey acquisition is not enabled here.'), submit, cancel);
            dialog.append(form); root.append(dialog);
            let settled = false;
            const abort = () => finish(null);
            const finish = value => {
                if (settled) return;
                settled = true;
                lifecycle.signal.removeEventListener('abort', abort);
                typed.value = ''; code.value = ''; dialog.close(); dialog.remove(); resolve(value);
            };
            cancel.addEventListener('click', () => finish(null));
            dialog.addEventListener('cancel', event => { event.preventDefault(); finish(null); });
            lifecycle.signal.addEventListener('abort', abort, { once: true });
            form.addEventListener('submit', event => {
                event.preventDefault();
                if (typed.value !== expected) { typed.setCustomValidity('Confirmation does not match'); typed.reportValidity(); typed.setCustomValidity(''); return; }
                finish({ confirmation: typed.value, stepUp: { code: code.value || undefined } });
            });
            dialog.showModal(); typed.focus();
        });
    }

    perform(() => refresh());
    return () => { disposed = true; lifecycle.abort(); root.replaceChildren(); };
}
