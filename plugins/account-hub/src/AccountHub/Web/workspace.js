import { mountSecurityPanel } from './security-panel.mjs';

const uuid = () => crypto.randomUUID ? crypto.randomUUID() : '10000000-1000-4000-8000-100000000000'.replace(/[018]/g, c => (Number(c) ^ crypto.getRandomValues(new Uint8Array(1))[0] & 15 >> Number(c) / 4).toString(16));
const make = (tag, text) => {
    const node = document.createElement(tag); if (text !== undefined) node.textContent = String(text);
    if (tag === 'button') { node.setAttribute('is', 'emby-button'); node.className = 'emby-button raised'; node.type = 'button'; if (text !== undefined) { const foreground = document.createElement('span'); foreground.className = 'emby-button-foreground'; foreground.textContent = text; node.replaceChildren(foreground); } }
    if (tag === 'input' || tag === 'textarea') { node.setAttribute('is', 'emby-input'); node.className = 'emby-input'; }
    if (tag === 'select') { node.setAttribute('is', 'emby-select'); node.className = 'emby-select'; }
    return node;
};
const pretty = value => typeof value === 'object' ? JSON.stringify(value, null, 2) : String(value ?? 'Inherit');
const title = name => name.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, c => c.toUpperCase());
const requestError = code => ({ revision_changed: 'Settings changed. Refresh and review again.', command_conflict: 'This request changed. Refresh and review again.', invalid_request: 'Check the entered values.', administrator_protected: 'This administrator account is protected.', forbidden: 'You do not have permission.', unauthorized: 'Sign in again.', step_up_required: 'Enter a fresh verification code.', step_up_unavailable: 'Verification unavailable. Check Jellyfin Security.', confirmation_required: 'Enter the confirmation text.', unavailable: 'Account service unavailable. Check plugin health.', storage_recovery_required: 'Account storage needs recovery. Check plugin health.' })[code] || 'Unable to complete the request. Check permissions and plugin health.';
function field(name, value, metadata = {}) {
    const root = make('div'); root.className = 'inputContainer hubField'; const id = 'hub-' + uuid(), label = make('label', metadata.label || title(name)); label.className = 'inputLabel'; label.htmlFor = id;
    let input;
    if (metadata.values) { input = make('select'); for (const item of metadata.values) { const option = make('option', metadata.labels?.[item] || (item === null ? 'Inherit' : item)); option.value = item === null ? '' : item; input.append(option); } input.value = value ?? ''; }
    else { input = make('input'); input.type = typeof value === 'boolean' ? 'checkbox' : typeof value === 'number' || name === 'MaxParentalRating' ? 'number' : metadata.secret ? 'password' : 'text'; if (input.type === 'checkbox') { input.setAttribute('is', 'emby-checkbox'); input.className = 'emby-checkbox'; input.checked = value; root.className = 'checkboxContainer hubField'; } else input.value = Array.isArray(value) ? value.join(', ') : value ?? ''; }
    input.id = id; input.autocomplete = metadata.secret ? 'new-password' : 'off'; if (metadata.min !== undefined) input.min = metadata.min; if (metadata.max !== undefined) input.max = metadata.max;
    if (metadata.help) root.dataset.help = metadata.help;
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
    const body = make('tbody'); for (const row of rows) { const tr = make('tr'); for (const value of row) { const td = make('td'); td.append(value instanceof Node ? value : value !== null && typeof value === 'object' ? make('pre', pretty(value)) : document.createTextNode(pretty(value))); tr.append(td); } body.append(tr); } node.append(body); wrapper.append(node); return wrapper;
}
const accessValue = value => value === null ? 'No limit' : typeof value === 'boolean' ? value ? 'Allow' : 'Deny' : Array.isArray(value) ? value.join(', ') || 'None' : String(value);
function accessDiff(before, after) {
    const rows = Object.keys(after).filter(key => JSON.stringify(before[key]) !== JSON.stringify(after[key])).map(key => [title(key), accessValue(before[key]), accessValue(after[key])]);
    return rows.length ? table(['Permission', 'Current', 'Reviewed'], rows) : make('p', 'No access changes.');
}
function resultSummary(result) {
    const outcomes = result.outcomes || [], pending = outcomes.filter(item => !['applied', 'administrator_skipped'].includes(item.status)).length;
    if (result.remediationPending || result.status === 'unknown' || pending) return 'Some changes need review. Check current access before continuing.';
    if (outcomes.length) return outcomes.filter(item => item.status === 'applied').length + ' account(s) updated.';
    return 'Saved.';
}
function changeDescription(path, payload) {
    const operation = path.split('/').at(-1), labels = { 'user.enable': 'Enable account', 'user.disable': 'Disable account', 'user.sessions.revoke': 'Revoke account sessions', 'user.discord.unlink': 'Unlink Discord', 'policy.apply': 'Apply reviewed access', 'policy.rollback': 'Restore saved access', 'group.save': 'Save access template', 'group.copy-user': 'Copy access to template', 'group.delete': 'Delete unused template', assign: 'Save group assignment', apply: 'Apply reviewed group access', reset: 'Reset reviewed group access', rollback: 'Restore saved group access', 'settings.public-url': 'Change public URL', 'settings.library': 'Change library notifications' };
    const label = labels[operation] || (path.startsWith('invites') ? 'Update invitation' : path === 'invite-defaults' ? 'Change invitation permissions' : 'Confirm account settings');
    const count = payload?.payload?.userIds?.length || payload?.payload?.targets?.length;
    return label + (count ? ' for ' + count + ' account(s)' : '');
}
function confirmation(description, signal, securitySensitive = true) {
    return new Promise(resolve => {
        if (signal?.aborted) { resolve(null); return; }
        const dialog = make('dialog'); dialog.className = 'dialog formDialog accountHubDialog'; const form = make('form'), proof = field('Verification code', '', { secret: true });
        dialog.setAttribute('aria-label', description); form.className = 'formDialogContent dialogContentInner';
        const submit = make('button', 'Confirm'), cancel = make('button', 'Cancel'); submit.type = 'submit';
        let closed = false;
        const abort = () => close(null);
        const close = value => { if (closed) return; closed = true; signal?.removeEventListener('abort', abort); proof.input.value = ''; dialog.close(); dialog.remove(); resolve(value); };
        signal?.addEventListener('abort', abort, { once: true });
        cancel.onclick = () => close(null); dialog.addEventListener('cancel', event => { event.preventDefault(); close(null); });
        form.onsubmit = event => { event.preventDefault(); close({ confirmation: 'CONFIRM ACCOUNT HUB CHANGE', stepUp: { code: proof.value() } }); };
        form.append(make('h2', description), ...(securitySensitive ? [proof.root] : []), submit, cancel); dialog.append(form); document.body.append(dialog); dialog.showModal(); (securitySensitive?proof.input:submit).focus();
    });
}

export function mountWorkspace(root, client) {
    let active = new AbortController(), state, dependencies = [], selectedTab = 'People', cleanup, generation = 0, skip = 0;
    const status = root.querySelector('.hubMessage'), content = root.querySelector('.hubContent'), tabs = root.querySelector('.hubTabs');
    const names = ['People', 'Groups', 'Invites', 'Security', 'Audit', 'Notifications', 'Settings'];
    const dependencyIds = { Security: 'security' };
    const dependencyPanel = make('details'); dependencyPanel.className = 'hubDependencyHealth'; tabs.before(dependencyPanel);
    function renderDependencies() {
        const relevant = dependencies.filter(item => ['security', 'discord'].includes(item.id));
        const missing = relevant.filter(item => !item.available), summary = make('summary', 'Account security and notification integrations — ' + missing.length + ' unavailable'), list = make('dl');
        for (const item of relevant) { const label = make('dt', item.name + ': ' + item.state), detail = make('dd', item.available ? item.reason : item.reason + ' ' + item.fix); detail.id = 'hub-dependency-' + item.id; list.append(label, detail); }
            dependencyPanel.replaceChildren(summary, list); dependencyPanel.open = false;
        for (const tab of tabs.children) {
            const dependency = dependencies.find(item => item.id === dependencyIds[tab.textContent]);
            tab.disabled = dependency ? !dependency.available : false;
            tab.hidden = dependency ? !dependency.available : false;
            if (dependency) { tab.title = dependency.reason + ' ' + dependency.fix; tab.setAttribute('aria-describedby', 'hub-dependency-' + dependency.id); }
        }
    }
    async function request(path, method = 'GET', payload, proof = false) {
        const scope = generation, signal = active.signal; let grant;
        if (proof) {
            const operational = path.startsWith('audit/') || path.startsWith('seerr/') || path.startsWith('domains/') || path.startsWith('settings/notifications') || path === 'commands/settings.library' || path === 'commands/settings.seerr';
            grant = await confirmation(changeDescription(path, payload), active.signal, !operational); if (!grant) throw Error('Cancelled'); payload = { ...payload, ...grant };
        }
        try {
            if (scope !== generation || signal.aborted) throw new DOMException('View changed', 'AbortError');
            const response = await fetch(client.getUrl('AccountHub/api/v1/' + path), { method, signal, credentials: 'omit', cache: 'no-store', headers: { Authorization: 'MediaBrowser Token="' + client.accessToken() + '"', 'Content-Type': 'application/json' }, body: payload === undefined ? undefined : JSON.stringify(payload) });
            const body = await response.json(); if (scope !== generation) throw Error('View changed');
            if ((response.status === 401 || response.status === 403) && body.twoFactorRequired === true) { content.replaceChildren(); throw Error('Complete Jellyfin Security 2FA, then return.'); }
            if (!response.ok || body.ok !== true) throw Error(window.SuiteAdminUI?.failure?.(body,response,requestError(body.error?.code))||requestError(body.error?.code)); return body.data;
        } finally { if (grant?.stepUp) grant.stepUp.code = null; if (payload?.stepUp) payload.stepUp.code = null; }
    }
    const action = fn => async () => { const current = generation; status.textContent = ''; try { await fn(); } catch (error) { if (current === generation && error.name !== 'AbortError') status.textContent = error.message; } };
    const button = (text, fn) => { const value = make('button', text); value.onclick = action(fn);value.dataset.tone=/^(Disable|Revoke|Unlink|Delete|Remove)/.test(text)?'danger':/^(Save|Apply|Enable|Create)/.test(text)?'success':'neutral'; return value; };
    async function command(operation, payload) { const result = await request('commands/' + operation, 'POST', { revision: state.revision, commandId: uuid(), payload }, true); state = await request('workspace'); status.textContent = resultSummary(result); return result; }
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
            else if (name === 'Audit') audit(); else if (name === 'Notifications') await notifications(); else if (name === 'Settings') settings();
        } catch (error) { if (view === generation && error.name !== 'AbortError') status.textContent = error.message; }
    }
    async function people() {
        const selected=new Map(),search = field('Search users', ''), toolbar = make('div'); toolbar.className = 'hubToolbar';toolbar.append(search.root, button('Search', async () => { skip = 0;selected.clear();await load(); }),button('Edit selected access',async()=>{if(!selected.size)throw Error('Select users first.');await editPolicy([...selected.values()][0],[...selected.keys()]);}),button('Clear selection',async()=>{selected.clear();await load();})); content.append(toolbar); const list = make('div'); content.append(list);
        async function load() {
            const data = await request('users?skip=' + skip + '&limit=50&search=' + encodeURIComponent(search.value()));
            const selectAll=field('Select all users on this page',false),all=selectAll.input;all.setAttribute('aria-label','Select all users on this page');selectAll.root.querySelector('.checkboxLabel').textContent='';const choices=[];
            const update=()=>{const enabled=choices.filter(c=>!c.disabled);all.disabled=!enabled.length;all.checked=!!enabled.length&&enabled.every(c=>c.checked);all.indeterminate=enabled.some(c=>c.checked)&&!all.checked;};
            list.replaceChildren(table(['','User', 'Administrator', 'Disabled', 'Discord', 'Actions'], data.items.map(user => {
                const choice=field(user.username,selected.has(user.id));choice.input.setAttribute('aria-label',user.username);choice.root.querySelector('.checkboxLabel').textContent='';choices.push(choice.input);choice.input.disabled=user.administrator;choice.input.onchange=()=>{if(choice.input.checked){if(selected.size>=100){choice.input.checked=false;status.textContent='Select at most 100 users.';return;}selected.set(user.id,user);}else selected.delete(user.id);update();};
                const actions = make('div'); actions.className = 'hubActions';
                actions.append(button('Edit access', () => editPolicy(user)), button(user.disabled ? 'Enable' : 'Disable', async () => { await command(user.disabled ? 'user.enable' : 'user.disable', { userId: user.id }); await load(); }), button('Revoke sessions', async () => { await command('user.sessions.revoke', { userId: user.id }); }));
                if (user.administrator) actions.children[0].disabled = true;
                actions.append(button('Copy access to template',()=>copyAccessTemplate(user)));
                if(user.discordLinked)actions.append(button('Unlink Discord',async()=>{await command('user.discord.unlink',{userId:user.id});await load();}));
                return [choice.root,user.username, user.administrator, user.disabled, user.discordLinked?'Linked':'Not linked', actions];
            })), button('Previous', async () => { skip = Math.max(0, skip - 50); await load(); }), button('Next', async () => { if (skip + 50 < data.total) { skip += 50; await load(); } }));
            list.querySelector('th').append(selectAll.root);all.onchange=()=>{const checked=all.checked;for(const choice of choices.filter(c=>!c.disabled)){choice.checked=checked;choice.onchange();}update();};update();
        }
        await load();
    }
    async function copyAccessTemplate(user){
        const dialog=make('dialog');dialog.className='dialog formDialog accountHubDialog';const name=field('Template name',user.username+' access');
        const close=()=>{dialog.close();dialog.remove();};active.signal.addEventListener('abort',close,{once:true});
        dialog.append(make('h2','New access template'),make('p','Copies managed permissions only. Administrator status, credentials, Discord links and 2FA are never copied.'),name.root,button('Create template',async()=>{if(!name.value().trim())throw Error('Enter a template name.');await request('group-policy/commands/group.copy-user','POST',{revision:state.revision,commandId:uuid(),payload:{id:uuid(),sourceUserId:user.id,name:name.value().trim()}},true);close();await show('Groups');}),button('Cancel',close));document.body.append(dialog);dialog.showModal();
    }
    async function editPolicy(user,userIds=[user.id]) {
        const dialog = make('dialog'); dialog.className = 'dialog formDialog accountHubDialog'; dialog.setAttribute('aria-label', 'Access for ' + user.username); const grid = make('div'); grid.className = 'hubGrid'; const controls = {};
        dialog.addEventListener('cancel',event=>{event.preventDefault();dialog.close();dialog.remove();});
        active.signal.addEventListener('abort', () => { dialog.close(); dialog.remove(); }, { once: true });
        let draft = 0;
        const include={};for (const [key, value] of Object.entries(user.policy)) {
            const cell = make('div'); cell.className = 'hubField';
            controls[key] = field(key, key === 'EnabledFolders' ? value || [] : value ?? '', ['MaxActiveSessions', 'MaxParentalRating'].includes(key) ? { min: 0, max: 1000 } : {});
            if(userIds.length>1){include[key]=field('Change '+title(key),false);controls[key].input.disabled=true;include[key].input.onchange=()=>{controls[key].input.disabled=!include[key].input.checked;invalidate();};cell.append(include[key].root);}cell.append(controls[key].root);grid.append(cell);
        }
        const message = make('div'); message.setAttribute('role', 'status');
        const invalidate = () => { draft++; apply.disabled = true; message.replaceChildren(make('p', 'Preview the updated draft before applying.')); };
        const save = button('Preview', async () => {
            const reviewedDraft = ++draft; apply.disabled = true;
            for (const [key, control] of Object.entries(controls)) if ((!include[key] || include[key].input.checked) && !control.input.checkValidity()) { control.input.reportValidity(); throw Error('Check the permission values.'); }
            const managedFields = Object.fromEntries(Object.entries(controls).filter(([key])=>!include[key]||include[key].input.checked).map(([key, value]) => [key, key === 'MaxParentalRating' && value.value() === '' ? null : value.value()]));
            if(!Object.keys(managedFields).length)throw Error('Choose at least one field to change.');
            const preview = await request('policy/preview', 'POST', { userIds, managedFields });
            if (reviewedDraft !== draft || !dialog.isConnected) return;
            message.replaceChildren();
            for (const item of preview.users || []) { const section = make('section'); section.append(make('h3', userIds.length === 1 ? user.username : selectedUsername(item.userId)), accessDiff(item.before, item.after)); message.append(section); }
            if (preview.skippedAdmins?.length) message.append(make('p', preview.skippedAdmins.length + ' administrator(s) excluded.'));
            apply.disabled = false; apply.onclick = action(async () => { if (reviewedDraft !== draft || !dialog.isConnected) return; apply.disabled = true; const result = await command('policy.apply', { userIds, managedFields }); dialog.close(); dialog.remove(); await show(); status.textContent = resultSummary(result); });
        }), apply = make('button', 'Apply reviewed changes'); apply.disabled = true;
        const selectedUsername = id => id === user.id ? user.username : id;
        grid.addEventListener('input', invalidate); grid.addEventListener('change', invalidate);
        const history = make('div');
        dialog.append(make('h2', userIds.length>1?'Access for '+userIds.length+' users':'Access for ' + user.username), make('p', 'Preview changes before applying. Administrator accounts are excluded.'), make('p', 'Folders: library IDs, separated by commas. Blank rating or 0 sessions means unlimited.'), grid, message, save, apply,
            button('Review rollback history', async () => {
                const data = await request('policy/snapshots', 'POST', { userId: user.id }, state.stepUpLevelMirror === 'Everything');
                history.replaceChildren(make('h3', 'Rollback history for ' + user.username), table(['Time', 'Saved changes', 'Action'], data.items.map(snapshot => [snapshot.occurredAt, accessDiff(snapshot.metadata.before, snapshot.metadata.after), button('Rollback managed fields', async () => { const result = await command('policy.rollback', { userIds: [user.id], snapshotId: snapshot.snapshotId }); dialog.close(); dialog.remove(); await show(); status.textContent = resultSummary(result); })])));
            }), history, button('Cancel', async () => { dialog.close(); dialog.remove(); })); document.body.append(dialog); dialog.showModal();
        const handle=dialog.querySelector('h2');handle.dataset.dragHandle='true';let drag;
        handle.addEventListener('pointerdown',event=>{if(event.button!==0)return;const box=dialog.getBoundingClientRect();drag={x:event.clientX,y:event.clientY,left:box.left,top:box.top};handle.setPointerCapture?.(event.pointerId);});
        handle.addEventListener('pointermove',event=>{if(!drag)return;dialog.style.margin='0';dialog.style.right='auto';dialog.style.bottom='auto';dialog.style.left=Math.max(0,Math.min(innerWidth-dialog.offsetWidth,drag.left+event.clientX-drag.x))+'px';dialog.style.top=Math.max(0,Math.min(innerHeight-dialog.offsetHeight,drag.top+event.clientY-drag.y))+'px';});
        for(const event of ['pointerup','pointercancel','lostpointercapture'])handle.addEventListener(event,()=>{drag=null;});
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
        async function load() { const data = await request('audit/query', 'POST', { skip: auditSkip, limit: 50, eventType: filter.value() || null }); rows.replaceChildren(table(['Sequence', 'Time', 'Event', 'Actor', 'Subject'], data.items.map(row => [row.sequence, row.event.occurredAt, row.event.eventType, row.event.actorUserId, row.event.subjectId]))); }
        content.append(button('Previous', async () => { auditSkip = Math.max(0, auditSkip - 50); await load(); }), button('Next', async () => { auditSkip += 50; await load(); }));
        for (const format of ['csv', 'json']) content.append(button('Export ' + format.toUpperCase(), async () => {
            const data = await request('audit/export', 'POST', { format }), blob = new Blob([data.content], { type: format === 'csv' ? 'text/csv' : 'application/json' }), href = URL.createObjectURL(blob), link = make('a'); link.href = href; link.download = 'account-hub-audit.' + format; link.click(); URL.revokeObjectURL(href);
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
            }, { discordAvailable: dependencies.find(item => item.id === 'discord')?.available === true, signal });
            if (!current()) unmount();
        } catch (error) { unmount(); throw error; }
    }
    function settings() {
        content.append(make('h2', 'Account settings'));
        const general = make('section'); general.className = 'suiteCard'; content.append(general);
        const publicUrl = field('Public Jellyfin URL', state.publicBaseUrl || '', { help: 'HTTPS address used for invitations, Discord linking and recovery. Include your Jellyfin BaseUrl if configured.' });
        general.append(publicUrl.root, button('Save public URL', async () => { await command('settings.public-url', { publicBaseUrl: publicUrl.value() }); }), button('Invitation defaults and permissions', () => show('Invites')));
        const integrations = make('section'); integrations.className = 'suiteCard'; integrations.append(make('h3', 'Plugin permissions'), make('p', 'Removal permissions are managed separately from account access templates.'));
        for (const [id, label, page] of [['deletion-queue', 'Removal permissions', 'DeletionQueueAdmin'], ['automatic', 'Automatic request rules', 'AutomaticDeletionRequests']]) {
            const dependency = dependencies.find(item => item.id === id), link = button(label, async () => { location.hash = '/configurationpage?name=' + page; });
            if(dependency?.available)integrations.append(link);
        }
        integrations.dataset.help = 'Deletion Queue controls who can propose removal (Denied, Own requests or Any accessible item), per-user overrides, consent, approval and execution. Automatic request rules are administrator-only and still obey Queue permissions and safety checks. Native account templates do not grant either plugin permission.';
        content.append(integrations);
        const advanced = make('details'); advanced.append(make('summary', 'Advanced settings')); content.append(advanced);
        const creation = make('section'), creationView = generation, creationSignal = active.signal; creation.className = 'suiteCard';
        advanced.append(creation);
        async function creationStatus() {
            const policy = await request('user-creation');
            if (creationView !== generation || creationSignal.aborted) return;
            if (policy.owner === 'native') advanced.open = true;
            creation.replaceChildren(make('h3', 'Temporary native creation'), make('p', policy.owner === 'native'
                ? 'Native user creation is temporarily enabled until ' + new Date(policy.nativeOverrideUntil).toLocaleTimeString() + '. Hub onboarding is paused.'
                : 'Create accounts through invitations. Native Add user is blocked unless the temporary override is enabled.'),
                button(policy.owner === 'native' ? 'End native override and resume Hub' : 'Enable native creation for five minutes', async () => {
                    await request('user-creation/native-override', 'POST', { commandId: uuid(), revision: (await request('workspace')).revision, enabled: policy.owner !== 'native' }, true);
                    await creationStatus();
                }), button('Refresh creation status', creationStatus));
            creation.dataset.help = 'The native creation override expires after five minutes and is cleared on restart. Administrator authorization still applies.';
        }
        creationStatus().catch(error => { if (creationView === generation && !creationSignal.aborted) status.textContent = error.message; });
        const auditDays = field('Keep audit history (days)', state.auditRetentionDays, { min: 15, max: 365, help: 'Retain audit events for 15–365 days.' });
        const history = make('section'); history.className = 'suiteCard'; advanced.append(history);
        history.append(make('h3', 'Audit history'), auditDays.root, button('Save audit retention', async () => { await request('settings/retention', 'POST', { commandId: uuid(), revision: state.revision, auditRetentionDays: auditDays.value(), notificationRetentionDays: state.notificationRetentionDays }, true); await show(); status.textContent = 'Saved.'; }));
        const security = make('section'); security.className = 'suiteCard'; security.append(make('h3', 'Security integration')); advanced.append(security);
        const mirror = field('Extra verification level', state.stepUpLevelMirror, { values: ['Off', 'Destructive', 'AllConfigChanges', 'Everything'], labels: { Off: 'Confirmation only', Destructive: 'Destructive changes', AllConfigChanges: 'Configuration and destructive changes', Everything: 'All security-sensitive changes' } }), mode = field('Security alerts', state.securityAlertMode, { values: ['native', 'hub', 'both'], labels: { native: 'Jellyfin Security', hub: 'Account notifications', both: 'Both' } }), passwords = field('Local password login', state.passwordLoginPolicy, { values: ['auto', 'local_disabled', 'allow_local'], labels: { auto: 'Follow native sign-in policy', local_disabled: 'Disable local passwords', allow_local: 'Allow when native policy permits' }, help: 'Controls Hub invitations and recovery; it does not change Jellyfin authentication providers or bypass native sign-in restrictions.' });
        mirror.root.dataset.help = 'Manually match the level configured in Jellyfin Security; this is not synchronized automatically. Lowering it requires authorization under the previous level. Operational settings and audit reads do not require extra verification. Alert routing does not edit Security configuration.';
        security.append(mirror.root, mode.root, passwords.root, button('Save Security integration settings', async () => {
            const grant = await confirmation('Change Security integration settings', active.signal); if (!grant) return; grant.confirmation = 'CHANGE SECURITY SETTINGS';
            try { await request('security/settings', 'PUT', { commandId: uuid(), revision: state.revision, settings: { stepUpLevelMirror: mirror.value(), alertMode: mode.value(), passwordLoginPolicy: passwords.value() }, ...grant }); await show(); status.textContent = 'Saved.'; } finally { grant.stepUp.code = null; }
        }));
        const webhook = make('details'); webhook.append(make('summary', 'Security webhook'), make('p', state.securityWebhookConfigured ? 'Signing secret configured.' : 'No signing secret configured.')); security.append(webhook);
        const secret = field('Replace webhook secret', '', { secret: true, help: 'At least 32 characters. Keep the original database and keys together when backing up. Never disable 2FA to recover this plugin.' }); webhook.append(secret.root, button('Store signing secret', async () => {
            try { await request('settings/secrets/security.webhookSecret', 'POST', { commandId: uuid(), revision: state.revision, secret: secret.value() }, true); await show(); } finally { secret.input.value = ''; }
        }));
        active.signal.addEventListener('abort', () => { secret.input.value = ''; }, { once: true });
    }
    tabs.setAttribute('role', 'tablist'); tabs.setAttribute('aria-label', 'Account Hub sections');
    content.id = 'hub-panel-' + uuid(); content.setAttribute('role', 'tabpanel'); content.tabIndex = 0;
    for (const name of names) { const tab = button(name, async () => { skip = 0; await show(name); }); tab.id = 'hub-tab-' + uuid(); tab.setAttribute('role', 'tab'); tab.setAttribute('aria-controls', content.id); tabs.append(tab); }
    tabs.addEventListener('keydown', event => { if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return; event.preventDefault(); const visible = [...tabs.children].filter(tab => !tab.hidden && !tab.disabled), index = visible.findIndex(tab => tab.textContent === selectedTab), next = event.key === 'Home' ? visible[0] : event.key === 'End' ? visible.at(-1) : visible[(index + (event.key === 'ArrowRight' ? 1 : visible.length - 1)) % visible.length]; if (next) { show(next.textContent); next.focus(); } });
    const hidden = () => { active.abort(); generation++; cleanup?.(); cleanup = undefined; content.replaceChildren(); status.textContent = ''; };
    root.addEventListener('viewshow', () => show()); root.addEventListener('viewhide', hidden); root.addEventListener('viewdestroy', hidden); show();
    return hidden;
}
