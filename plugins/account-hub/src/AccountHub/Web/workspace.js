import { mountSecurityPanel } from './security-panel.mjs';

const uuid = () => crypto.randomUUID ? crypto.randomUUID() : '10000000-1000-4000-8000-100000000000'.replace(/[018]/g, c => (Number(c) ^ crypto.getRandomValues(new Uint8Array(1))[0] & 15 >> Number(c) / 4).toString(16));
const make = (tag, text) => {
    const node = document.createElement(tag); if (text !== undefined) node.textContent = String(text);
    if (tag === 'button') { node.setAttribute('is', 'emby-button'); node.className = 'emby-button raised'; node.type = 'button'; if (text !== undefined) { const foreground = document.createElement('span'); foreground.className = 'emby-button-foreground'; foreground.textContent = text; node.replaceChildren(foreground); } }
    if (tag === 'input' || tag === 'textarea') { node.setAttribute('is', 'emby-input'); node.className = 'emby-input'; }
    if (tag === 'select') { node.setAttribute('is', 'emby-select'); node.className = 'emby-select'; }
    return node;
};
const pretty = value => typeof value === 'object' ? JSON.stringify(value) : String(value ?? 'Inherit');
const title = name => name.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, c => c.toUpperCase());
function field(name, value, metadata = {}) {
    const root = make('div'); root.className = 'inputContainer hubField'; const id = 'hub-' + uuid(), label = make('label', title(name)); label.className = 'inputLabel'; label.htmlFor = id;
    let input;
    if (metadata.values) { input = make('select'); for (const item of metadata.values) { const option = make('option', item === null ? 'Inherit' : item); option.value = item === null ? '' : item; input.append(option); } input.value = value ?? ''; }
    else { input = make('input'); input.type = typeof value === 'boolean' ? 'checkbox' : typeof value === 'number' || name === 'MaxParentalRating' ? 'number' : metadata.secret ? 'password' : 'text'; if (input.type === 'checkbox') { input.setAttribute('is', 'emby-checkbox'); input.className = 'emby-checkbox'; input.checked = value; root.className = 'checkboxContainer hubField'; } else input.value = Array.isArray(value) ? value.join(', ') : value ?? ''; }
    input.id = id; input.autocomplete = metadata.secret ? 'new-password' : 'off'; if (metadata.min !== undefined) input.min = metadata.min; if (metadata.max !== undefined) input.max = metadata.max;
    if (input.type === 'checkbox') {
        label.className = 'emby-checkbox-label'; label.textContent = ''; const caption = make('span', title(name)); caption.className = 'checkboxLabel'; const outline = make('span'); outline.className = 'checkboxOutline'; outline.setAttribute('aria-hidden', 'true'); const checked = make('span'), unchecked = make('span'); checked.className = 'material-icons checkboxIcon checkboxIcon-checked check'; unchecked.className = 'material-icons checkboxIcon checkboxIcon-unchecked'; outline.append(checked, unchecked); input.setAttribute('data-embycheckbox', 'true'); label.append(input, caption, outline); root.append(label);
    } else {
        root.append(label, input); input.labelElement = label;
        if (metadata.values) { root.className = 'selectContainer hubField'; label.className = 'selectLabel'; input.classList.add('emby-select-withcolor'); const arrow = make('div'); arrow.className = 'selectArrowContainer'; arrow.setAttribute('aria-hidden', 'true'); const icon = make('span'); icon.className = 'selectArrow material-icons keyboard_arrow_down'; arrow.append(icon); root.append(arrow); }
        input.addEventListener('focus', () => label.classList.add(metadata.values ? 'selectLabelFocused' : 'inputLabelFocused')); input.addEventListener('blur', () => label.classList.remove(metadata.values ? 'selectLabelFocused' : 'inputLabelFocused'));
    }
    return { root, input, value: () => input.type === 'checkbox' ? input.checked : input.type === 'number' ? input.value === '' ? null : Number(input.value) : Array.isArray(value) ? input.value.split(',').map(v => v.trim()).filter(Boolean) : input.value === '' && metadata.values?.includes(null) ? null : input.value };
}
function table(columns, rows) {
    const wrapper = make('div'); wrapper.className = 'hubTableWrap'; const node = make('table'); node.className = 'tbl hubTable'; const head = make('tr');
    for (const label of columns) { const cell = make('th', label); cell.scope = 'col'; head.append(cell); } const thead = make('thead'); thead.append(head); node.append(thead);
    const body = make('tbody'); for (const row of rows) { const tr = make('tr'); for (const value of row) { const td = make('td'); td.append(value instanceof Node ? value : document.createTextNode(pretty(value))); tr.append(td); } body.append(tr); } node.append(body); wrapper.append(node); return wrapper;
}
function confirmation(description, signal) {
    return new Promise(resolve => {
        if (signal?.aborted) { resolve(null); return; }
        const dialog = make('dialog'); dialog.className = 'dialog formDialog accountHubDialog'; const form = make('form'), typed = field('Type CONFIRM ACCOUNT HUB CHANGE', ''), proof = field('Fresh authenticator or recovery code (when required)', '', { secret: true });
        dialog.setAttribute('aria-label', description); form.className = 'formDialogContent dialogContentInner';
        const submit = make('button', 'Confirm'), cancel = make('button', 'Cancel'); submit.type = 'submit';
        let closed = false;
        const abort = () => close(null);
        const close = value => { if (closed) return; closed = true; signal?.removeEventListener('abort', abort); typed.input.value = proof.input.value = ''; dialog.close(); dialog.remove(); resolve(value); };
        signal?.addEventListener('abort', abort, { once: true });
        cancel.onclick = () => close(null); dialog.addEventListener('cancel', event => { event.preventDefault(); close(null); });
        form.onsubmit = event => { event.preventDefault(); if (typed.value() !== 'CONFIRM ACCOUNT HUB CHANGE') { typed.input.focus(); return; } close({ confirmation: 'CONFIRM ACCOUNT HUB CHANGE', stepUp: { code: proof.value() } }); };
        form.append(make('h2', description), make('p', 'Fresh proof is never saved. A failed or unavailable verifier is not downgraded. Known absent/incompatible Security or verified no-factor fallback is explicit and audited.'), typed.root, proof.root, submit, cancel); dialog.append(form); document.body.append(dialog); dialog.showModal(); typed.input.focus();
    });
}

export function mountWorkspace(root, client) {
    let active = new AbortController(), state, dependencies = [], selectedTab = 'People', cleanup, generation = 0, skip = 0;
    const status = root.querySelector('.hubMessage'), content = root.querySelector('.hubContent'), tabs = root.querySelector('.hubTabs');
    const names = ['People', 'Groups', 'Invites', 'Library', 'Seerr', 'Security', 'Audit', 'Notifications', 'DeletionQueue', 'Private SyncPlay', 'Discord', 'Settings'];
    const dependencyIds = { Security: 'security', DeletionQueue: 'deletion-queue', 'Private SyncPlay': 'private-syncplay', Discord: 'discord' };
    const dependencyPanel = make('details'); dependencyPanel.className = 'hubDependencyHealth'; tabs.before(dependencyPanel);
    function renderDependencies() {
        const missing = dependencies.filter(item => !item.available), summary = make('summary', 'Optional integrations — ' + missing.length + ' unavailable'), list = make('dl');
        for (const item of dependencies) { const label = make('dt', item.name + ': ' + item.state), detail = make('dd', item.available ? item.reason : item.reason + ' ' + item.fix); detail.id = 'hub-dependency-' + item.id; list.append(label, detail); }
        dependencyPanel.replaceChildren(summary, list); dependencyPanel.open = missing.length > 0;
        for (const tab of tabs.children) {
            const dependency = dependencies.find(item => item.id === dependencyIds[tab.textContent]);
            tab.disabled = dependency ? !dependency.available : false;
            if (dependency) { tab.title = dependency.reason + ' ' + dependency.fix; tab.setAttribute('aria-describedby', 'hub-dependency-' + dependency.id); }
        }
    }
    async function request(path, method = 'GET', payload, proof = false) {
        const scope = generation, signal = active.signal; let grant;
        if (proof) { grant = await confirmation('Authorize ' + path.split('?')[0], active.signal); if (!grant) throw Error('Cancelled'); payload = { ...payload, ...grant }; }
        try {
            if (scope !== generation || signal.aborted) throw new DOMException('View changed', 'AbortError');
            const response = await fetch(client.getUrl('AccountHub/api/v1/' + path), { method, signal, credentials: 'omit', cache: 'no-store', headers: { Authorization: 'MediaBrowser Token="' + client.accessToken() + '"', 'Content-Type': 'application/json' }, body: payload === undefined ? undefined : JSON.stringify(payload) });
            const body = await response.json(); if (scope !== generation) throw Error('View changed');
            if ((response.status === 401 || response.status === 403) && body.twoFactorRequired === true) { content.replaceChildren(); throw Error('Complete Jellyfin Security 2FA, then return.'); }
            if (!response.ok || body.ok !== true) throw Error(body.error?.message || body.error?.code || 'Request failed: ' + response.status); return body.data;
        } finally { if (grant?.stepUp) grant.stepUp.code = null; if (payload?.stepUp) payload.stepUp.code = null; }
    }
    const action = fn => async () => { const current = generation; status.textContent = ''; try { await fn(); } catch (error) { if (current === generation && error.name !== 'AbortError') status.textContent = error.message; } };
    const button = (text, fn) => { const value = make('button', text); value.onclick = action(fn); return value; };
    async function command(operation, payload) { const result = await request('commands/' + operation, 'POST', { revision: state.revision, commandId: uuid(), payload }, true); state = await request('workspace'); status.textContent = pretty(result); return result; }
    async function show(name = selectedTab) {
        cleanup?.(); cleanup = undefined; active.abort(); active = new AbortController(); generation++; selectedTab = name; content.replaceChildren(); status.textContent = 'Loading…';
        const view = generation;
        for (const item of tabs.children) { item.setAttribute('aria-selected', String(item.textContent === name)); item.tabIndex = item.textContent === name ? 0 : -1; }
        content.setAttribute('aria-labelledby', [...tabs.children].find(item => item.textContent === name).id);
        try {
            const unknownDependencies = () => ({ items: Object.values(dependencyIds).map(id => ({ id, name: id, available: false, state: 'unverified', reason: 'Optional provider health could not be verified. Core account features remain independent.', fix: 'Refresh health and check Dashboard → Plugins for enabled compatible versions; repair the provider and restart Jellyfin if required.' })) });
            const loaded = await Promise.all([request('workspace'), request('dependencies').catch(unknownDependencies)]); state = loaded[0]; dependencies = loaded[1].items || unknownDependencies().items; renderDependencies(); status.textContent = '';
            const unavailable = dependencies.find(item => item.id === dependencyIds[name] && !item.available);
            if (unavailable) { content.append(make('h2', name), make('p', unavailable.reason), make('p', unavailable.fix)); return; }
            if (name === 'People') await people(); else if (name === 'Groups') await groups(); else if (name === 'Invites') await invites(); else if (name === 'Security') cleanup = mountSecurityPanel(content, client, { safeActions: true });
            else if (name === 'Audit') audit(); else if (name === 'Notifications') await notifications(); else if (name === 'Settings') settings(); else if (name === 'Seerr') await seerr(); else if (name === 'Library') await library();
            else if (name === 'Discord') { content.append(make('h2', 'Discord Bridge'), make('p', 'One separately installed Discord Bridge plugin owns the bot and all Discord delivery. Account Hub owns links and recovery. Commands are self-service only.')); const link = make('a', 'Configure Discord Bridge'); link.href = '#/configurationpage?name=DiscordBridge'; content.append(link, make('p', pretty(state.discord?.data || 'Bridge unavailable'))); }
            else await domain(name === 'DeletionQueue' ? 'deletion-queue' : 'private-syncplay');
        } catch (error) { if (view === generation && error.name !== 'AbortError') status.textContent = error.message; }
    }
    async function people() {
        const search = field('Search users', ''), toolbar = make('div'); toolbar.className = 'hubToolbar'; toolbar.append(search.root, button('Search', async () => { skip = 0; await load(); })); content.append(toolbar); const list = make('div'); content.append(list);
        async function load() {
            const data = await request('users?skip=' + skip + '&limit=50&search=' + encodeURIComponent(search.value()));
            list.replaceChildren(table(['User', 'Administrator', 'Disabled', 'Actions'], data.items.map(user => {
                const actions = make('div'); actions.className = 'hubActions';
                actions.append(button('Edit access', () => editPolicy(user)), button(user.disabled ? 'Enable' : 'Disable', async () => { await command(user.disabled ? 'user.enable' : 'user.disable', { userId: user.id }); await load(); }), button('Revoke sessions', async () => { await command('user.sessions.revoke', { userId: user.id }); }));
                if (user.administrator) actions.children[0].disabled = true;
                return [user.username, user.administrator, user.disabled, actions];
            })), button('Previous', async () => { skip = Math.max(0, skip - 50); await load(); }), button('Next', async () => { if (skip + 50 < data.total) { skip += 50; await load(); } }));
        }
        await load();
    }
    async function editPolicy(user) {
        const dialog = make('dialog'); dialog.className = 'dialog formDialog accountHubDialog'; dialog.setAttribute('aria-label', 'Access for ' + user.username); const grid = make('div'); grid.className = 'hubGrid dialogContentInner'; const controls = {};
        active.signal.addEventListener('abort', () => { dialog.close(); dialog.remove(); }, { once: true });
        for (const [key, value] of Object.entries(user.policy)) { controls[key] = field(key, value ?? ''); grid.append(controls[key].root); }
        const message = make('p'), save = button('Preview', async () => {
            const managedFields = Object.fromEntries(Object.entries(controls).map(([key, value]) => [key, key === 'MaxParentalRating' && value.value() === '' ? null : value.value()]));
            const preview = await request('policy/preview', 'POST', { userIds: [user.id], managedFields }); message.textContent = pretty(preview);
            apply.disabled = false; apply.onclick = action(async () => { await command('policy.apply', { userIds: [user.id], managedFields }); dialog.close(); dialog.remove(); await show(); });
        }), apply = make('button', 'Apply reviewed changes'); apply.disabled = true;
        grid.addEventListener('input', () => { apply.disabled = true; message.textContent = 'Preview the updated draft before applying.'; });
        const history = make('div');
        dialog.append(make('h2', 'Access for ' + user.username), make('p', 'Only managed fields change. Administrators are excluded. A durable rollback snapshot is saved before each apply. EnabledFolders accepts comma-separated library IDs.'), grid, message, save, apply,
            button('Review rollback history', async () => {
                const data = await request('policy/snapshots', 'POST', { userId: user.id }, state.stepUpLevelMirror === 'Everything');
                history.replaceChildren(table(['Time', 'Before', 'After', 'Action'], data.items.map(snapshot => [snapshot.occurredAt, snapshot.metadata.before, snapshot.metadata.after, button('Rollback managed fields', async () => { await command('policy.rollback', { userIds: [user.id], snapshotId: snapshot.eventId }); dialog.close(); dialog.remove(); await show(); })])));
            }), history, button('Cancel', async () => { dialog.close(); dialog.remove(); })); document.body.append(dialog); dialog.showModal();
    }
    async function groups() {
        const view = generation, module = await import(client.getUrl('AccountHub/assets/group-policy.mjs'));
        if (view !== generation || active.signal.aborted) return;
        cleanup = module.mountGroupPolicy(content, { request, signal: active.signal });
    }
    async function invites() {
        const view = generation;
        if (!window.AccountHubInvites) await new Promise((resolve, reject) => { const script = make('script'); script.src = client.getUrl('AccountHub/invite-assets/invite-admin.js'); script.onload = resolve; script.onerror = reject; document.head.append(script); });
        if (view !== generation || active.signal.aborted) return;
        const signal = active.signal, container = make('section'); content.append(container);
        const panel = await window.AccountHubInvites.mount(container, {
            signal, discordAvailable: dependencies.find(item => item.id === 'discord')?.available === true && state.discord?.ok === true && state.discord?.data?.connected === true,
            read: path => request(path === 'groups' ? 'group-policy/groups?skip=0&limit=100' : path),
            write: async (path, payload) => { if (signal.aborted) throw new DOMException('View closed', 'AbortError'); const current = await request('workspace'); if (signal.aborted) throw new DOMException('View closed', 'AbortError'); return await request(path, 'POST', { ...payload, commandId: uuid(), revision: current.revision }, true); },
            refresh: async () => { state = await request('workspace'); }
        });
        if (view !== generation) panel.dispose(); else cleanup = () => panel.dispose();
    }
    function audit() {
        const filter = field('Exact event type (optional)', ''), rows = make('div'); let auditSkip = 0; content.append(filter.root, button('Search audit', async () => { auditSkip = 0; await load(); }), button('Verify chain', async () => { const data = await request('audit/verify', 'POST', {}, true); status.textContent = pretty(data); }), rows);
        async function load() { const data = await request('audit/query', 'POST', { skip: auditSkip, limit: 50, eventType: filter.value() || null }, state.stepUpLevelMirror === 'Everything'); rows.replaceChildren(table(['Sequence', 'Time', 'Event', 'Actor', 'Subject'], data.items.map(row => [row.sequence, row.event.occurredAt, row.event.eventType, row.event.actorUserId, row.event.subjectId]))); }
        content.append(button('Previous', async () => { auditSkip = Math.max(0, auditSkip - 50); await load(); }), button('Next', async () => { auditSkip += 50; await load(); }));
        for (const format of ['csv', 'json']) content.append(button('Export ' + format.toUpperCase(), async () => {
            const data = await request('audit/export', 'POST', { format }, state.stepUpLevelMirror === 'Everything'), blob = new Blob([data.content], { type: format === 'csv' ? 'text/csv' : 'application/json' }), href = URL.createObjectURL(blob), link = make('a'); link.href = href; link.download = 'account-hub-audit.' + format; link.click(); URL.revokeObjectURL(href);
        }));
        load().catch(error => { status.textContent = error.message; });
    }
    async function notifications() {
        const view = generation, signal = active.signal;
        const current = () => view === generation && !signal.aborted;
        if (!window.AccountHubNotificationSettings) await new Promise((resolve, reject) => { const script = make('script'); script.src = client.getUrl('AccountHub/assets/notifications/settings.js'); script.onload = resolve; script.onerror = reject; document.head.append(script); });
        if (!current()) return;
        // Async mount and its eventual cleanup own only this view's container, never a newer tab.
        const panel = make('div'); content.append(panel); let dispose;
        const unmount = () => {
            signal.removeEventListener('abort', unmount);
            dispose?.(); dispose = undefined;
            for (const input of panel.querySelectorAll('input[type=password]')) input.value = '';
            panel.remove();
        };
        signal.addEventListener('abort', unmount, { once: true }); cleanup = unmount;
        try {
            dispose = await window.AccountHubNotificationSettings.mount(panel, async (path, method, payload) => {
                if (!current()) throw new DOMException('View closed', 'AbortError');
                const result = await request(path, method, payload, !['GET'].includes(method));
                if (!current()) throw new DOMException('View closed', 'AbortError');
                return result;
            }, { discordAvailable: dependencies.find(item => item.id === 'discord')?.available === true });
            if (!current()) unmount();
        } catch (error) { unmount(); throw error; }
    }
    async function library() {
        const data = await request('library/settings'), enabled = field('Notify new library additions in the notification center', data.settings.enabled), episodes = field('New episodes', data.settings.episodes), movies = field('New movies', data.settings.movies);
        content.append(make('h2', 'New library content'), make('p', 'Enable notifications for future additions, not an existing-library backfill. Each episode/movie is deduplicated durably and given 30 seconds for metadata to settle. Recipients must have native Jellyfin access; inbox reads and DM delivery recheck that access. Personal notification preferences still apply.'), enabled.root, episodes.root, movies.root,
            button('Save library notifications', async () => { await command('settings.library', { revision: data.settings.revision, enabled: enabled.value(), episodes: episodes.value(), movies: movies.value() }); await show(); }), make('p', 'Intake health: ' + pretty(data.health)),
            make('p', 'Discord is a separate opt-in in Discord Bridge: choose specific libraries and approved channels, and acknowledge that channel members may see titles regardless of their individual Jellyfin access. With both plugins installed Hub captures additions once; Bridge remains the Discord transport.'));
        const dependency = dependencies.find(item => item.id === 'discord');
        const link = button('Open Discord library announcement settings', async () => { location.hash = '/configurationpage?name=DiscordBridge'; }); link.disabled = !dependency?.available; link.title = dependency?.available ? '' : dependency?.reason + ' ' + dependency?.fix; content.append(link);
    }
    async function seerr() {
        const config = await request('seerr/connection'); content.append(make('h2', 'Seerr connection'));
        const url = field('Seerr base URL (including reverse-proxy prefix)', config.baseUrl || ''), http = field('Explicitly allow HTTP', config.allowHttp), local = field('Explicitly allow private network addresses', config.allowPrivateNetwork), key = field('Replace Seerr API key (blank preserves)', '', { secret: true });
        const test = button('Test authenticated connection', async () => { status.textContent = pretty(await request('seerr/test', 'POST', {})); });
        const directoryButton = button('Read Seerr directory', async () => { const data = await request('seerr/users'); directory.replaceChildren(table(['Seerr user', 'Jellyfin ID', 'Permissions', 'Protected'], data.items.map(user => [user.username || user.id, user.jellyfinUserId, user.permissions, user.protectedAccount]))); });
        for (const control of [test, directoryButton]) { control.disabled = !config.baseUrl || !config.keyConfigured; control.title = control.disabled ? 'Configure the Seerr destination and API key above, then save/reload. No Jellyfin integration plugin is required.' : ''; }
        content.append(url.root, http.root, local.root, button('Save connection destination', async () => { await command('settings.seerr', { baseUrl: url.value(), allowHttp: http.value(), allowPrivateNetwork: local.value() }); await show(); }), key.root,
            button('Store API key', async () => { if (!key.value()) throw Error('Enter a replacement key.'); try { await request('settings/secrets/seerr.apiKey', 'POST', { commandId: uuid(), revision: state.revision, secret: key.value() }, true); await show(); } finally { key.input.value = ''; } }),
            make('p', config.keyConfigured ? 'An encrypted API key is configured.' : 'Seerr-dependent features are disabled until a destination and API key are configured. Install no extra Jellyfin plugin: connect directly to your Seerr service above.'), test);
        const directory = make('div'); content.append(directoryButton, directory);
        active.signal.addEventListener('abort', () => { key.input.value = ''; }, { once: true });
        const lifecycle = make('section'); content.append(lifecycle);
        const view = generation, module = await import(client.getUrl('AccountHub/assets/seerr-panel.mjs'));
        if (view !== generation || active.signal.aborted) return;
        const dispose = await module.mountSeerrPanel(lifecycle, { request, make, field, table, button, signal: active.signal });
        if (view !== generation) dispose(); else cleanup = () => { key.input.value = ''; dispose(); };
    }
    function settings() {
        content.append(make('h2', 'Account Hub settings'));
        const creation = make('section'), creationView = generation, creationSignal = active.signal;
        content.append(creation);
        async function creationStatus() {
            const policy = await request('user-creation');
            if (creationView !== generation || creationSignal.aborted) return;
            creation.replaceChildren(make('h3', 'User creation workflow'), make('p', policy.owner === 'native'
                ? 'Native user creation is temporarily enabled until ' + new Date(policy.nativeOverrideUntil).toLocaleTimeString() + '. Hub onboarding is paused.'
                : 'Create accounts through Account Hub Invitations. Native Add user requests are blocked while Hub owns creation.'),
                button(policy.owner === 'native' ? 'End native override and resume Hub' : 'Enable native creation for five minutes', async () => {
                    await request('user-creation/native-override', 'POST', { commandId: uuid(), revision: (await request('workspace')).revision, enabled: policy.owner !== 'native' }, true);
                    await creationStatus();
                }), button('Refresh creation status', creationStatus), make('p', 'The override expires automatically and is cleared on restart. It does not bypass native administrator authorization. Internal creation by other plugins is outside this HTTP guard.'));
        }
        creationStatus().catch(error => { if (creationView === generation && !creationSignal.aborted) status.textContent = error.message; });
        const auditDays = field('Audit retention (15–365 days)', state.auditRetentionDays, { min: 15, max: 365 });
        content.append(auditDays.root, button('Save audit retention', async () => { await request('settings/retention', 'POST', { commandId: uuid(), revision: state.revision, auditRetentionDays: auditDays.value(), notificationRetentionDays: state.notificationRetentionDays }, true); await show(); }));
        const publicUrl = field('Public HTTPS Jellyfin base URL', state.publicBaseUrl || ''); content.append(publicUrl.root, button('Save public URL', async () => { await command('settings.public-url', { publicBaseUrl: publicUrl.value() }); }));
        const mirror = field('Manual step-up mirror', state.stepUpLevelMirror, { values: ['Off', 'Destructive', 'AllConfigChanges', 'Everything'] }), mode = field('Security alert mode', state.securityAlertMode, { values: ['native', 'hub', 'both'] }), passwords = field('Local password login policy', state.passwordLoginPolicy, { values: ['auto', 'local_disabled', 'allow_local'] });
        content.append(make('p', 'The mirror must match Jellyfin Security. Lowering it is authorized under the old level. Native/both channel choices never edit Security configuration.'), mirror.root, mode.root, passwords.root, button('Save Security integration settings', async () => {
            const grant = await confirmation('Change Security integration settings', active.signal); if (!grant) return; grant.confirmation = 'CHANGE SECURITY SETTINGS';
            try { await request('security/settings', 'PUT', { commandId: uuid(), revision: state.revision, settings: { stepUpLevelMirror: mirror.value(), alertMode: mode.value(), passwordLoginPolicy: passwords.value() }, ...grant }); await show(); } finally { grant.stepUp.code = null; }
        }));
        const secret = field('Replace Security webhook secret (minimum 32 characters)', '', { secret: true }); content.append(secret.root, button('Store signing secret', async () => {
            try { await request('settings/secrets/security.webhookSecret', 'POST', { commandId: uuid(), revision: state.revision, secret: secret.value() }, true); await show(); } finally { secret.input.value = ''; }
        }), make('p', 'Back up the AccountHub database, WAL and original keys together. Never disable 2FA to recover this plugin.'));
        active.signal.addEventListener('abort', () => { secret.input.value = ''; }, { once: true });
    }
    async function domain(key) {
        const view = generation, module = await import(client.getUrl('AccountHub/assets/domain-panel.js'));
        if (view !== generation || active.signal.aborted) return;
        cleanup = module.mountDomainPanel(content, client, { key, signal: active.signal, dependency: dependencies.find(item => item.id === key), onMutation: async () => { state = await request('workspace'); } });
    }
    tabs.setAttribute('role', 'tablist'); tabs.setAttribute('aria-label', 'Account Hub sections');
    content.id = 'hub-panel-' + uuid(); content.setAttribute('role', 'tabpanel'); content.tabIndex = 0;
    for (const name of names) { const tab = button(name, async () => { skip = 0; await show(name); }); tab.id = 'hub-tab-' + uuid(); tab.setAttribute('role', 'tab'); tab.setAttribute('aria-controls', content.id); tabs.append(tab); }
    tabs.addEventListener('keydown', event => { if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return; event.preventDefault(); const visible = [...tabs.children].filter(tab => !tab.hidden && !tab.disabled), index = visible.findIndex(tab => tab.textContent === selectedTab), next = event.key === 'Home' ? visible[0] : event.key === 'End' ? visible.at(-1) : visible[(index + (event.key === 'ArrowRight' ? 1 : visible.length - 1)) % visible.length]; if (next) { show(next.textContent); next.focus(); } });
    const hidden = () => { active.abort(); generation++; cleanup?.(); cleanup = undefined; content.replaceChildren(); status.textContent = ''; };
    root.addEventListener('viewshow', () => show()); root.addEventListener('viewhide', hidden); root.addEventListener('viewdestroy', hidden); show();
    return hidden;
}
