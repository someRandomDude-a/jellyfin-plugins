// Versioned domain adapters remain the only policy writers. No media or party command is exposed here.
const PAGE = 50, MAX_SELECTION = 100;
const title = name => name.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, c => c.toUpperCase());
const text = value => value === null || value === undefined ? 'Inherit' : typeof value === 'object' ? JSON.stringify(value) : String(value);
const node = (tag, value) => {
    const item = document.createElement(tag);
    if (value !== undefined) item.textContent = String(value);
    if (tag === 'button') { item.type = 'button'; item.setAttribute('is', 'emby-button'); item.className = 'emby-button raised'; const span = document.createElement('span'); span.className = 'emby-button-foreground'; span.textContent = value; item.replaceChildren(span); }
    return item;
};
function field(name, value, metadata = {}) {
    const root = node('div'), label = node('label', title(name)), input = node(metadata.values ? 'select' : 'input');
    const id = 'hub-domain-' + crypto.randomUUID(); input.id = id; label.htmlFor = id;
    if (metadata.values) {
        root.className = 'selectContainer hubField'; label.className = 'selectLabel'; input.setAttribute('is', 'emby-select'); input.className = 'emby-select emby-select-withcolor';
        for (const choice of metadata.values) { const option = node('option', choice === null ? 'Inherit (domain default)' : choice); option.value = choice ?? ''; input.append(option); }
        input.value = value ?? '';
        root.append(label, input); const arrow = node('div'); arrow.className = 'selectArrowContainer'; arrow.setAttribute('aria-hidden', 'true'); const icon = node('span'); icon.className = 'selectArrow material-icons keyboard_arrow_down'; arrow.append(icon); root.append(arrow);
    } else {
        input.type = metadata.secret ? 'password' : typeof value === 'boolean' ? 'checkbox' : typeof value === 'number' ? 'number' : 'text';
        if (input.type === 'checkbox') {
            root.className = 'checkboxContainer hubField'; input.setAttribute('is', 'emby-checkbox'); input.className = 'emby-checkbox'; input.setAttribute('data-embycheckbox', 'true'); input.checked = value;
            label.className = 'emby-checkbox-label'; label.textContent = ''; const caption = node('span', title(name)); caption.className = 'checkboxLabel'; const outline = node('span'); outline.className = 'checkboxOutline'; outline.setAttribute('aria-hidden', 'true'); const checked = node('span'), unchecked = node('span'); checked.className = 'material-icons checkboxIcon checkboxIcon-checked check'; unchecked.className = 'material-icons checkboxIcon checkboxIcon-unchecked'; outline.append(checked, unchecked); label.append(input, caption, outline); root.append(label);
        } else {
            root.className = 'inputContainer hubField'; label.className = 'inputLabel'; input.setAttribute('is', 'emby-input'); input.className = 'emby-input'; input.value = value ?? ''; root.append(label, input);
        }
    }
    input.autocomplete = metadata.secret ? 'new-password' : 'off';
    if (metadata.min !== undefined) input.min = metadata.min;
    if (metadata.max !== undefined) input.max = metadata.max;
    return { root, input, value: () => {
        if (input.type === 'checkbox') return input.checked;
        if (input.type === 'number') { if (!input.checkValidity() || input.value === '' || !Number.isInteger(Number(input.value))) throw Error('Check ' + title(name)); return Number(input.value); }
        return metadata.values?.includes(null) && input.value === '' ? null : input.value;
    } };
}
function table(headers, rows) {
    const wrap = node('div'); wrap.className = 'hubTableWrap'; const table = node('table'); table.className = 'tbl hubTable';
    const head = node('thead'), tr = node('tr'); for (const value of headers) { const th = node('th', value); th.scope = 'col'; tr.append(th); } head.append(tr); const body = node('tbody');
    for (const row of rows) { const tr = node('tr'); for (const value of row) { const td = node('td'); td.append(value instanceof Node ? value : document.createTextNode(text(value))); tr.append(td); } body.append(tr); }
    table.append(head, body); wrap.append(table); return wrap;
}

// Returns cleanup immediately; also call it on viewhide. The optional outer signal owns navigation lifetime.
export function mountDomainPanel(root, client, { key, signal, dependency, onMutation } = {}) {
    if (!['deletion-queue', 'private-syncplay'].includes(key)) throw Error('Unknown domain');
    const life = new AbortController(), sync = key === 'private-syncplay', selected = new Set();
    let disposed = false, epoch = 0, page = 0, caps, settings, policy, pending, busy = false, closeDialog;
    const message = node('p'), main = node('section'), editor = node('section'), review = node('section'), result = node('section');
    message.setAttribute('role', 'status'); message.setAttribute('aria-live', 'polite'); editor.setAttribute('aria-label', 'Policy editor'); review.setAttribute('aria-label', 'Change preview'); result.setAttribute('aria-label', 'Command result');
    root.replaceChildren(node('h2', sync ? 'Private SyncPlay' : 'Deletion Queue'), node('p', 'The domain plugin remains authoritative. Administrators are read-only and excluded from bulk changes. Hard safety switches still apply to everyone. No media deletion or party action is available here.'), message, main, editor, review, result);
    function cleanup() {
        if (disposed) return; disposed = true; epoch++; life.abort(); closeDialog?.(null); selected.clear(); pending = null;
        for (const input of root.querySelectorAll('input')) input.value = '';
        root.replaceChildren(); signal?.removeEventListener('abort', cleanup);
    }
    signal?.addEventListener('abort', cleanup, { once: true });
    if (signal?.aborted) { cleanup(); return cleanup; }
    function invalidate() { epoch++; pending = null; review.replaceChildren(); }
    const action = fn => async () => {
        if (busy || disposed) return; busy = true; message.textContent = '';
        try { await fn(); } catch (error) {
            if (!disposed && error.name !== 'AbortError') {
                message.textContent = error.code === 'revision_changed' ? 'Settings changed. Reload and preview again.' : error.message;
                if (error.code === 'revision_changed') invalidate();
            }
        } finally { busy = false; }
    };
    function button(label, fn, disabled = false) { const b = node('button', label); b.disabled = disabled; b.onclick = action(fn); return b; }
    async function request(path, method = 'GET', payload) {
        if (disposed) throw new DOMException('View closed', 'AbortError');
        const response = await fetch(client.getUrl('AccountHub/api/v1/' + path), { method, signal: life.signal, credentials: 'omit', cache: 'no-store', headers: { Authorization: 'MediaBrowser Token="' + client.accessToken() + '"', 'Content-Type': 'application/json' }, body: payload === undefined ? undefined : JSON.stringify(payload) });
        const body = await response.json();
        if (disposed) throw new DOMException('View closed', 'AbortError');
        if ((response.status === 401 || response.status === 403) && body.twoFactorRequired === true) { invalidate(); main.replaceChildren(); editor.replaceChildren(); throw Error('Complete Jellyfin Security 2FA, then reopen this view.'); }
        if (!response.ok || body.ok !== true) { const error = Error(body.error?.code || 'Domain unavailable. Check Dashboard → Plugins; install/enable the matching domain plugin and restart Jellyfin.'); error.code = body.error?.code; error.status = response.status; throw error; }
        return body.data;
    }
    const domain = path => 'domains/' + key + '/' + path;
    function confirm() {
        return new Promise(resolve => {
            if (disposed) { resolve(null); return; }
            const dialog = node('dialog'); dialog.className = 'dialog accountHubDialog'; dialog.setAttribute('aria-label', 'Authorize reviewed domain change');
            const form = node('form'), typed = field('Type CONFIRM ACCOUNT HUB CHANGE', ''), proof = field('Fresh authenticator or recovery code (when required)', '', { secret: true });
            const submit = node('button', 'Confirm reviewed change'), cancel = node('button', 'Cancel'); submit.type = 'submit'; let closed = false;
            const close = value => { if (closed) return; closed = true; typed.input.value = proof.input.value = ''; dialog.close(); dialog.remove(); closeDialog = undefined; resolve(value); };
            closeDialog = close; cancel.onclick = () => close(null); dialog.addEventListener('cancel', event => { event.preventDefault(); close(null); });
            form.onsubmit = event => { event.preventDefault(); if (typed.value() !== 'CONFIRM ACCOUNT HUB CHANGE') { typed.input.focus(); return; } close({ confirmation: typed.value(), stepUp: { code: proof.value() } }); };
            form.append(node('h2', 'Authorize reviewed domain change'), node('p', 'Fresh proof is transient. Invalid proof or an unavailable verifier never downgrades authorization. Known missing/incompatible Security uses explicit audited confirmation.'), typed.root, proof.root, submit, cancel); dialog.append(form); document.body.append(dialog); dialog.showModal(); typed.input.focus();
        });
    }
    function userFields(value = {}) {
        if (sync) return {
            defaultMode: field('defaultMode', value.defaultMode ?? null, { values: [null, ...caps.enums.defaultMode] }),
            ...Object.fromEntries(['createPublic', 'createPrivate', 'guestInvites'].map(name => [name, field(name, value[name] ?? 'Inherit', { values: caps.enums.permission })]))
        };
        return { authority: field('authority', value.authority ?? null, { values: [null, ...caps.fields.authority.values] }),
            delayHours: field('notification delay hours', value.notificationPreferences?.delayHours ?? 48, { min: 48, max: 8760 }),
            notifications: field('notifications', value.notificationPreferences?.notifications ?? true) };
    }
    function policyValue(fields) {
        const values = Object.fromEntries(Object.entries(fields).map(([name, f]) => [name, f.value()]));
        return sync ? values : { authority: values.authority, notificationPreferences: { delayHours: values.delayHours, notifications: values.notifications } };
    }
    function formFields(target, fields) { for (const f of Object.values(fields)) { target.append(f.root); f.input.addEventListener('input', invalidate); f.input.addEventListener('change', invalidate); } }
    function safeUser(view) { return { defaults: view.defaults, override: view.policy, effective: view.effective, sources: view.sources }; }
    function describePreview(data) {
        const parts = [node('h3', 'Review before applying'), node('p', 'Excluded administrators: ' + (data.skippedAdminCount ?? data.skippedAdmins?.length ?? 0))];
        for (const warning of data.warnings || []) parts.push(node('p', warning));
        if (data.currentDefaults) parts.push(table(['Defaults before', 'Defaults after'], [[data.currentDefaults, data.desiredDefaults]]));
        if (data.currentSettings) {
            const safeSettings = settingsValue => ({ ...Object.fromEntries(Object.entries(settingsValue).filter(([name]) => name !== 'revision' && name !== 'seerr')),
                ...(settingsValue.seerr ? { seerr: { baseUrl: settingsValue.seerr.baseUrl, keyConfigured: settingsValue.seerr.keyConfigured } } : {}) });
            parts.push(table(['Settings before', 'Settings after'], [[safeSettings(data.currentSettings), safeSettings(data.desiredSettings)]]));
        }
        if (data.changedFields?.length) parts.push(table(['Field', 'Before', 'After'], data.changedFields.map(f => [f.field, f.before, f.after])));
        if (data.users?.length) parts.push(table(['User', 'Before (default / override / effective)', 'After (default / override / effective)'], data.users.map(u => [u.userId, safeUser(u.before), safeUser(u.after)])));
        if (data.proposalEffects) parts.push(node('p', 'Native proposal revalidation: ' + text(data.proposalEffects)));
        if (data.usersTruncated) parts.push(node('p', 'Preview shows the first ' + (data.users?.length || 0) + ' users of ' + data.totalAffectedUsers + ' affected. Inspect additional users through the paginated table before applying.'));
        if (data.effects?.safetyEffects) parts.push(node('p', data.effects.safetyEffects));
        return parts;
    }
    async function stage(route, method, payload, previewPayload) {
        invalidate(); result.replaceChildren(); const version = ++epoch;
        const data = await request(domain('command'), 'POST', { commandId: crypto.randomUUID(), route: 'policy/preview', method: 'POST', payload: previewPayload });
        if (version !== epoch || disposed) return;
        // Capture exactly the reviewed input. Proof is added only to the outgoing request, never this retry capsule.
        const hubRevision = (await request('workspace')).revision;
        if (version !== epoch || disposed) return;
        pending = { route, method, payload: structuredClone(payload), commandId: crypto.randomUUID(), hubRevision };
        review.replaceChildren(...describePreview(data), button('Apply reviewed change', apply));
        review.querySelector('h3').tabIndex = -1; review.querySelector('h3').focus();
    }
    async function apply() {
        const command = pending; if (!command) throw Error('Preview the change again.');
        const grant = await confirm(); if (!grant || disposed) return;
        let reply;
        try { reply = await request(domain('command'), 'POST', { ...command, ...grant }); }
        catch (error) {
            if (!disposed && error.code !== 'revision_changed') {
                review.replaceChildren(node('p', 'Could not confirm the result. Use Retry to check this change before starting another.'), button('Retry exact command', apply));
            }
            throw error;
        } finally { grant.stepUp.code = ''; }
        invalidate(); selected.clear(); editor.replaceChildren();
        result.replaceChildren(node('h3', 'Domain command result'), node('p', reply.safetyEffects || 'Native domain policy commit completed.'), node('p', 'Administrators skipped: ' + (reply.skippedAdminCount ?? reply.skippedAdmins?.length ?? 0)));
        if (reply.results) result.append(table(['User', 'Outcome'], reply.results.map(r => [r.userId, r.status || r.code || 'unknown'])));
        if (reply.warnings) for (const warning of reply.warnings) result.append(node('p', warning));
        // A successful commit is not relabelled failed if only the subsequent read/parent refresh fails.
        try { await load(); await onMutation?.(reply); } catch { message.textContent = 'Change completed; refresh failed. Reload before making another change.'; }
    }
    async function editUsers(ids, value = {}) {
        if (!ids.length || ids.length > MAX_SELECTION) throw Error('Select between 1 and ' + MAX_SELECTION + ' non-administrator users.');
        invalidate(); const fields = userFields(value); editor.replaceChildren(node('h3', ids.length === 1 ? 'Edit user override — ' + ids[0] : 'Bulk edit — ' + ids.length + ' users'), node('p', 'This replaces the selected users’ complete editable overrides. Inherit restores the domain default. Global safety limits still apply.'));
        formFields(editor, fields);
        editor.append(button('Preview user changes', async () => {
            const values = policyValue(fields), revision = policy.revision, users = ids.map(userId => ({ userId, policy: values }));
            await stage(ids.length === 1 ? 'users/' + ids[0] + '/policy' : 'policy', 'PUT', { revision, ...(ids.length === 1 ? { policy: values } : { users }) },
                { revision, userIds: ids, ...(sync ? { changes: { policy: values } } : { users }) });
        }));
    }
    function reset(scope, ids = []) {
        if (scope === 'users' && !ids.length) throw Error('Select users to reset.');
        return stage('policy/reset', 'POST', { revision: policy.revision, scope, ...(scope === 'users' ? { userIds: ids } : {}) },
            { revision: policy.revision, userIds: scope === 'users' ? ids : [], resetScope: scope });
    }
    function globalEditor(kind) {
        invalidate(); const values = kind === 'settings' ? (settings.settings || settings) : policy.defaults, fields = {};
        editor.replaceChildren(node('h3', kind === 'settings' ? 'Domain settings' : 'Global policy defaults'));
        const allowed = sync ? (kind === 'settings' ? caps.settingsFields : caps.globalPolicyFields) : kind === 'settings' ? ['notificationMode', 'hubIntegrationEnabled', 'hubFallbackAfterMinutes', 'publishAudit', 'deviceBell'] : Object.keys(policy.defaults);
        for (const name of allowed) {
            if (name === 'guestPlayer') continue;
            const metadata = sync ? { values: caps.enums[name], min: caps.limits[name]?.[0], max: caps.limits[name]?.[1] } : caps.fields[name];
            fields[name] = field(name, values[name], metadata);
            if (!sync && name === 'automaticDeletion' && !values[name] && (!settings.seerr?.keyConfigured || !settings.seerr?.baseUrl)) {
                fields[name].input.disabled = true; fields[name].root.append(node('p', 'Configure and verify Seerr in the native Deletion Queue workspace before enabling automatic deletion. Other policy controls remain available.'));
            }
        }
        formFields(editor, fields);
        let player, seerr;
        if (!sync && kind === 'settings') {
            const group = node('fieldset'); group.append(node('legend', 'Seerr connection (domain-owned)'));
            const baseUrl = field('Seerr base URL', settings.seerr?.baseUrl || ''), apiKey = field('Replacement Seerr API key (blank keeps stored key)', '', { secret: true }), clear = field('Explicitly clear stored Seerr API key', false);
            group.append(node('p', settings.seerr?.keyConfigured ? 'A key is configured. It is never read back.' : 'No key configured.'));
            formFields(group, { baseUrl, apiKey, clear }); editor.append(group);
            seerr = () => {
                if (clear.value() && apiKey.value()) throw Error('Do not supply a replacement key while clearing.');
                const changedUrl = baseUrl.value() !== (settings.seerr?.baseUrl || '');
                if (!changedUrl && !apiKey.value() && !clear.value()) return undefined;
                return { baseUrl: baseUrl.value(), ...(apiKey.value() ? { apiKey: apiKey.value() } : {}), ...(clear.value() ? { clearApiKey: true, confirmClear: true } : {}) };
            };
        }
        if (sync && kind === 'settings') {
            const group = node('fieldset'); group.append(node('legend', 'Guest player features and ordered controls'));
            const features = Object.fromEntries(caps.guestPlayer.features.map(name => [name, field(name, values.guestPlayer.features.includes(name))])); formFields(group, features);
            const controls = field('Ordered control IDs (comma separated)', values.guestPlayer.controls.join(', ')); group.append(controls.root); controls.input.addEventListener('input', invalidate); editor.append(group);
            player = () => { const ids = controls.value().split(',').map(s => s.trim()).filter(Boolean); if (ids.length > caps.guestPlayer.maxControls || new Set(ids).size !== ids.length || ids.some(id => !(new RegExp(caps.guestPlayer.controlIdPattern)).test(id))) throw Error('Check guest control IDs and count.'); return { features: Object.entries(features).filter(([, f]) => f.value()).map(([name]) => name), controls: ids }; };
        }
        editor.append(button('Preview ' + kind, async () => {
            const changes = Object.fromEntries(Object.entries(fields).map(([name, f]) => [name, f.value()])); if (player) changes.guestPlayer = player();
            const connection = seerr?.(); if (connection) changes.seerr = connection;
            const revision = policy.revision, payload = kind === 'settings' ? { revision, ...(sync ? { settings: changes } : changes) } : { revision, defaults: changes };
            await stage(kind === 'settings' ? 'settings' : 'policy', 'PUT', payload,
                { revision, userIds: [], ...(sync ? { changes: { [kind === 'settings' ? 'settings' : 'defaults']: changes } } : kind === 'settings' ? { settings: { revision, commandId: crypto.randomUUID(), ...changes } } : { changes }) });
        }));
    }
    function render() {
        const toolbar = node('div'); toolbar.className = 'hubToolbar';
        toolbar.append(button('Refresh', async () => { invalidate(); editor.replaceChildren(); await load(); }), button('Edit settings', () => globalEditor('settings')), button('Edit global defaults', () => globalEditor('defaults')));
        const selection = node('p', selected.size + ' selected (maximum ' + MAX_SELECTION + '). Selection is explicit and persists across pages; there is no hidden select-all.'); selection.setAttribute('role', 'status');
        const rows = policy.users.map(user => {
            const id = user.userId, administrator = user.administrator || user.readOnly, label = user.name || id, check = field('Select ' + label, selected.has(id)); check.input.disabled = !!administrator;
            check.input.onchange = () => {
                invalidate(); editor.replaceChildren(); if (check.input.checked) { if (selected.size >= MAX_SELECTION) { check.input.checked = false; message.textContent = 'Selection limit reached (' + MAX_SELECTION + ').'; return; } selected.add(id); } else selected.delete(id);
                selection.textContent = selected.size + ' selected (maximum ' + MAX_SELECTION + '). Selection persists across pages.';
            };
            const actions = node('div'); actions.className = 'hubActions'; actions.append(button('Edit override', async () => {
                const fresh = await request(domain('users/' + id + '/effective-policy')); if (fresh.user?.administrator || fresh.readOnly) throw Error('Administrator policy is read-only.');
                if (fresh.revision !== policy.revision) { invalidate(); await load(); throw Error('Domain changed. Review the refreshed page before editing.'); }
                await editUsers([id], fresh.user.policy);
            }, !!administrator), button('Preview reset', () => reset('users', [id]), !!administrator));
            return [check.root, label, administrator ? 'Administrator — protected' : 'Editable', user.defaults, user.policy, user.effective, user.sources || user.effective?.source || 'Domain default', actions];
        });
        const previous = button('Previous page', async () => { page = Math.max(0, page - PAGE); await load(); }, page === 0), next = button('Next page', async () => { page += PAGE; await load(); }, page + PAGE >= policy.total);
        const native = node('a', 'Open native domain workspace'); native.href = '#/configurationpage?name=' + (sync ? 'PrivateSyncPlay' : 'DeletionQueueAdmin');
        main.replaceChildren(toolbar, node('h3', 'Per-user policy'), selection, button('Edit selected overrides', () => editUsers([...selected])), button('Preview reset selected', () => reset('users', [...selected])), button('Clear selection', () => { selected.clear(); invalidate(); editor.replaceChildren(); render(); }), table(['Select', 'User', 'Protection', 'Default', 'Override', 'Effective', 'Source / safety', 'Actions'], rows), node('p', 'Users ' + (policy.total ? page + 1 : 0) + '–' + Math.min(page + PAGE, policy.total) + ' of ' + policy.total), previous, next, node('h3', 'Reset to domain-owned defaults'), node('p', 'Global resets non-secret settings/defaults. All also resets every non-administrator override/preference, not just this page. Credentials, evidence and native safety constraints are preserved. Review the preview first.'), button('Preview global reset', () => reset('global')), button('Preview reset all users and globals', () => reset('all')), native);
    }
    async function load() {
        const view = ++epoch;
        const data = await Promise.all([request(domain('capabilities')), request(domain('settings')), request(domain('policy?skip=' + page + '&limit=' + PAGE))]);
        if (disposed || view !== epoch) return;
        [caps, settings, policy] = data;
        // Mixed revisions are never used to construct a change.
        if (settings.revision !== policy.revision) { main.replaceChildren(button('Reload domain state', load)); throw Error('Domain changed while loading. Reload before editing.'); }
        if (!Array.isArray(policy.users) || !Number.isInteger(policy.total)) throw Error('Incompatible domain adapter. Update the domain plugin and restart Jellyfin.');
        render();
    }
    if (dependency && !dependency.available) {
        const disabled = node('fieldset'); disabled.disabled = true; disabled.append(node('legend', 'Domain controls unavailable'), node('button', 'Edit domain policy'));
        main.append(node('p', dependency.reason), node('p', dependency.fix), disabled);
    } else action(load)();
    return cleanup;
}
