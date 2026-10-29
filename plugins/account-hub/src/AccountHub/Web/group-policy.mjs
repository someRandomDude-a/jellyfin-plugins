// Parent supplies its authenticated request/proof transport. No tokens or proof are retained here.
const node = (tag, text) => { const e = document.createElement(tag); if (text !== undefined) e.textContent = text; return e; };
const button = (text, action) => { const b = node('button', text); b.type = 'button'; b.className = 'emby-button raised'; b.setAttribute('is', 'emby-button'); b.onclick = action; return b; };
const fields = { EnableMediaPlayback: 'boolean', EnableRemoteAccess: 'boolean', EnableContentDownloading: 'boolean', EnableAudioPlaybackTranscoding: 'boolean', EnableVideoPlaybackTranscoding: 'boolean', EnablePlaybackRemuxing: 'boolean', MaxActiveSessions: 'number', EnableAllFolders: 'boolean', EnabledFolders: 'array', MaxParentalRating: 'nullable' };
const labelText = name => name.replace(/([a-z])([A-Z])/g, '$1 $2');
function input(label, value = '', tag = 'input') {
    const wrap = node('div'), title = node('label', label), control = node(tag); control.id = 'hub-group-' + crypto.randomUUID(); title.htmlFor = control.id;
    title.className = tag === 'select' ? 'selectLabel' : 'inputLabel';
    control.className = tag === 'select' ? 'emby-select' : 'emby-input'; control.setAttribute('is', tag === 'select' ? 'emby-select' : 'emby-input'); control.value = value;
    wrap.className = tag === 'select' ? 'selectContainer' : 'inputContainer'; wrap.append(title, control);
    control.labelElement = title;
    control.addEventListener('focus', () => title.classList.add(tag === 'select' ? 'selectLabelFocused' : 'inputLabelFocused'));
    control.addEventListener('blur', () => title.classList.remove(tag === 'select' ? 'selectLabelFocused' : 'inputLabelFocused'));
    if (tag === 'select') { control.classList.add('emby-select-withcolor'); const arrow = node('div'), icon = node('span'); arrow.className = 'selectArrowContainer'; arrow.setAttribute('aria-hidden', 'true'); icon.className = 'selectArrow material-icons keyboard_arrow_down'; arrow.append(icon); wrap.append(arrow); }
    return { wrap, control };
}
function checkbox(label, checked) {
    const wrap = node('div'), caption = node('label'), control = node('input');
    wrap.className = 'checkboxContainer'; caption.className = 'emby-checkbox-label';
    control.type = 'checkbox'; control.id = 'hub-group-' + crypto.randomUUID(); control.className = 'emby-checkbox'; control.checked = checked;
    control.setAttribute('is', 'emby-checkbox'); control.setAttribute('data-embycheckbox', 'true'); caption.htmlFor = control.id;
    const text = node('span', label), outline = node('span'), on = node('span'), off = node('span');
    text.className = 'checkboxLabel'; outline.className = 'checkboxOutline'; outline.setAttribute('aria-hidden', 'true');
    on.className = 'material-icons checkboxIcon checkboxIcon-checked check'; off.className = 'material-icons checkboxIcon checkboxIcon-unchecked';
    outline.append(on, off); caption.append(control, text, outline); wrap.append(caption); return { wrap, control };
}
function editor(value = {}) {
    const root = node('fieldset'), controls = [];
    root.className = 'hubGrid'; const legend = node('legend', 'Access permissions'); root.append(legend);
    legend.dataset.help = 'Inherit leaves this layer unset. User overrides take priority over the primary group. Only the supported access fields are managed.';
    for (const [name, type] of Object.entries(fields)) {
        const row = input(labelText(name), '', 'select');
        for (const [text, val] of [['Inherit', 'inherit'], ...(type === 'boolean' ? [['Allow', 'true'], ['Deny', 'false']] : [['Set', 'set']]), ...(type === 'nullable' ? [['No rating limit', 'null']] : [])]) { const o = node('option', text); o.value = val; row.control.append(o); }
        const extra = type !== 'boolean' ? input(type === 'array' ? 'Folder IDs (comma separated)' : 'Value', Array.isArray(value[name]) ? value[name].join(', ') : value[name] ?? '') : null;
        if (extra && type !== 'array') { extra.control.type = 'number'; extra.control.min = '0'; extra.control.max = '1000'; }
        row.control.value = !(name in value) ? 'inherit' : type === 'boolean' ? String(value[name]) : value[name] === null ? 'null' : 'set';
        const refresh = () => { if (extra) { extra.wrap.hidden = row.control.value !== 'set'; extra.control.disabled = extra.wrap.hidden; } };
        row.control.onchange = refresh; refresh(); const cell = node('div'); cell.className = 'hubField'; cell.append(row.wrap); if (extra) cell.append(extra.wrap); root.append(cell);
        controls.push(() => {
            const mode = row.control.value; if (mode === 'inherit') return [];
            if (type === 'boolean') return [[name, mode === 'true']]; if (mode === 'null') return [[name, null]];
            if (type === 'array') return [[name, extra.control.value.split(',').map(v => v.trim()).filter(Boolean)]];
            if (extra.control.value.trim() === '' || !extra.control.checkValidity()) throw Error('Enter a valid value for ' + labelText(name));
            return [[name, Number(extra.control.value)]];
        });
    }
    return { root, read: () => Object.fromEntries(controls.flatMap(read => read())) };
}
function diff(views) {
    const root = node('div');
    for (const view of views) {
        const section = node('section'); section.append(node('h4', view.userId + (view.administrator ? ' — administrator excluded' : '')));
        const list = node('dl');
        for (const name of view.changedFields) list.append(node('dt', labelText(name)), node('dd', JSON.stringify(view.current[name]) + ' → ' + JSON.stringify(view.desired[name])));
        if (view.status && view.status !== 'ready') section.append(node('p', view.status));
        else if (!view.changedFields.length) section.append(node('p', 'No managed drift.')); section.append(list); root.append(section);
    }
    return root;
}
export function mountGroupPolicy(root, { request, signal }) {
    let disposed = false, skip = 0, groupSkip = 0, preview = null, epoch = 0;
    const begin = () => { preview = null; return ++epoch; };
    const current = view => !disposed && !signal?.aborted && view === epoch;
    const selected = new Set(), message = node('p'), main = node('div'); message.setAttribute('role', 'status'); message.setAttribute('aria-live', 'polite');
    const guidance = node('p', 'Save a template or assignment, then preview and apply it to users. Administrators are excluded.');
    guidance.dataset.help = 'Saving a group or assignment does not change native access. Reset restores values captured at first assignment and clears the group and overrides; it does not invent server defaults.';
    root.replaceChildren(node('h2', 'Groups and access'), guidance, message, main);
    const run = fn => async () => { if (disposed) return; try { message.textContent = ''; await fn(); } catch (e) { if (!disposed && e.name !== 'AbortError') message.textContent = e.message; } };
    const act = (text, fn) => {
        const control = button(text, run(async () => {
            // Visible navigation may supersede an in-flight response. Detached controls
            // cannot issue commands against a view that has already been replaced.
            if (!disposed && !signal?.aborted && control.isConnected) await fn();
        }));
        return control;
    };
    async function command(operation, payload) {
        const view = epoch;
        const state = await request('workspace');
        if (!current(view)) throw new DOMException('View changed', 'AbortError');
        const result = await request('group-policy/commands/' + operation, 'POST', { revision: state.revision, commandId: crypto.randomUUID(), payload }, true);
        if (!current(view)) throw new DOMException('View changed', 'AbortError');
        preview = null; return result;
    }
    const targets = views => views.map(v => ({ userId: v.userId, revision: v.revision, groupRevision: v.groupRevision }));
    async function groupList() {
        const view = begin(), data = await request('group-policy/groups?skip=' + groupSkip + '&limit=50'); if (!current(view)) return;
        main.replaceChildren(act('Create group', () => groupEdit()), act('Members and assignments', () => people()));
        for (const group of data.items) {
            const row = node('section'); row.append(node('h3', group.name), node('p', group.description), act('Edit ' + group.name, () => groupEdit(group)), act('Members of ' + group.name, () => members(group)));
            if (!group.isSystem) row.append(act('Delete unused ' + group.name, async () => { await command('group.delete', { id: group.id, groupRevision: group.revision }); await groupList(); })); main.append(row);
        }
        page(main, groupSkip, data.total, n => { groupSkip = n; return groupList(); });
    }
    function page(container, start, total, change) {
        const previous = act('Previous page', () => change(Math.max(0, start - 50))), next = act('Next page', () => change(start + 50)); previous.disabled = start === 0; next.disabled = start + 50 >= total;
        container.append(node('p', `${total ? start + 1 : 0}–${Math.min(start + 50, total)} of ${total}`), previous, next);
    }
    async function groupEdit(group) {
        begin();
        const name = input('Group name', group?.name || ''), description = input('Description', group?.description || ''), edit = editor(JSON.parse(group?.policyJson || '{}'));
        main.replaceChildren(name.wrap, description.wrap, edit.root, act('Save intent (no user changes)', async () => { await command('group.save', { id: group?.id || crypto.randomUUID(), groupRevision: group?.revision || 0, name: name.control.value, description: description.control.value, managedFields: edit.read() }); await groupList(); }), act('Back to groups', groupList));
    }
    async function members(group, memberSkip = 0) {
        const view = begin(), data = await request(`group-policy/members?groupId=${encodeURIComponent(group.id)}&skip=${memberSkip}&limit=50`); if (!current(view)) return;
        main.replaceChildren(node('h3', group.name + ' members'), act('Back to groups', groupList));
        for (const member of data.items) main.append(act(member.userId, () => person(member.userId)));
        page(main, memberSkip, data.total, n => members(group, n));
    }
    async function people(search = '') {
        const view = begin(), data = await request(`users?skip=${skip}&limit=50&search=${encodeURIComponent(search)}`); if (!current(view)) return;
        const find = input('Search users', search), searchButton = act('Search', () => { skip = 0; selected.clear(); return people(find.control.value); });
        main.replaceChildren(act('Groups', groupList), find.wrap, searchButton);
        for (const user of data.items) {
            const row = node('div'), choice = checkbox('Select ' + user.username, selected.has(user.id)); choice.control.disabled = user.administrator;
            choice.control.onchange = () => { preview = null; if (choice.control.checked) { if (selected.size >= 100) { choice.control.checked = false; message.textContent = 'Select at most 100 users.'; return; } selected.add(user.id); } else selected.delete(user.id); };
            row.append(choice.wrap, act(user.username + (user.administrator ? ' (administrator protected)' : user.disabled ? ' (disabled)' : ''), () => person(user.id))); main.append(row);
        }
        main.append(act('Dry run selected policy', () => review([...selected])), act('Dry run reset selected', () => review([...selected], 'reset'))); page(main, skip, data.total, n => { skip = n; return people(search); });
    }
    async function person(id, groupPage = 0, snapshotSkip = 0) {
        const responseEpoch = begin();
        const [view, groups, snapshots] = await Promise.all([request(`group-policy/users/${id}/drift`), request(`group-policy/groups?skip=${groupPage}&limit=50`), request(`group-policy/users/${id}/snapshots?skip=${snapshotSkip}&limit=50`)]); if (!current(responseEpoch)) return;
        const select = input('Primary group (one per user)', '', 'select'), none = node('option', 'No group'); none.value = ''; select.control.append(none);
        if (view.groupId && !groups.items.some(g => g.id === view.groupId)) { const old = node('option', 'Current group (browse pages to edit)'); old.value = view.groupId; select.control.append(old); }
        for (const group of groups.items) { const o = node('option', group.name); o.value = group.id; select.control.append(o); } select.control.value = view.groupId || '';
        const overrides = editor(view.overrides);
        main.replaceChildren(node('h3', 'Member access'), act('Back to members', () => people()), diff([view]));
        if (view.administrator) { main.append(node('p', 'Administrator policy is native-only; group reconciliation will never rewrite it.')); return; }
        main.append(select.wrap, overrides.root, act('Save assignment and override intent', async () => { const group = groups.items.find(g => g.id === select.control.value); if (select.control.value && !group) throw Error('Browse to the current group page before saving.'); await command('assign', { userId: id, assignmentRevision: view.revision, groupId: group?.id || null, groupRevision: group?.revision || 0, overrides: overrides.read() }); await person(id, groupPage); }), act('Dry run reconciliation', () => review([id])), act('Dry run reset', () => review([id], 'reset')));
        page(main, groupPage, groups.total, n => person(id, n));
        main.append(node('h3', 'Managed rollback snapshots'));
        for (const snapshot of snapshots.items) main.append(act(new Date(snapshot.createdAt).toLocaleString() + ' — ' + snapshot.outcome, async () => {
            const before = JSON.parse(snapshot.beforeJson), after = before;
            const rollbackEpoch = begin();
            const current = await request(`group-policy/users/${id}/drift`);
            if (disposed || rollbackEpoch !== epoch) return;
            main.replaceChildren(node('h3', 'Review managed-only rollback'), diff([{ ...current, desired: after, changedFields: Object.keys(before).filter(k => JSON.stringify(before[k]) !== JSON.stringify(current.current[k])) }]), act('Confirm rollback', async () => { const result = await command('rollback', { targets: targets([current]), snapshotId: snapshot.id }); outcomes(result); }), act('Cancel', () => person(id)));
        }));
        page(main, snapshotSkip, snapshots.total, n => person(id, groupPage, n));
    }
    async function review(ids, mode = 'apply') {
        if (!ids.length) throw Error('Select users first.');
        const view = begin();
        const views = await request('group-policy/preview', 'POST', { targets: ids.map(userId => ({ userId, revision: 0 })), mode }); if (!current(view)) return;
        preview = { targets: targets(views), mode };
        const reviewed = preview;
        main.replaceChildren(node('h3', 'Dry run — ' + mode), diff(views), act('Confirm ' + mode + ' for reviewed users', async () => {
            if (!current(view) || preview !== reviewed) throw Error('Repeat the dry run.'); preview = null;
            const result = await command(reviewed.mode, { targets: reviewed.targets }); outcomes(result);
        }), act('Cancel', () => people()));
    }
    function outcomes(result) {
        main.replaceChildren(node('h3', 'Per-user outcomes'));
        for (const outcome of result.outcomes || []) main.append(node('p', outcome.userId + ': ' + outcome.status), act('Inspect current drift', () => person(outcome.userId)));
        const help = node('p', 'Inspect any account that needs review.'); help.dataset.help = 'An uncertain operation may have reached Jellyfin. Inspect current access before another change; operations are not automatically repeated.';
        if ((result.outcomes || []).some(outcome => !['applied', 'administrator_skipped'].includes(outcome.status))) main.append(help);
        main.append(act('Back to groups', groupList));
    }
    const cleanup = () => { disposed = true; preview = null; selected.clear(); main.replaceChildren(); };
    signal?.addEventListener('abort', cleanup, { once: true }); run(groupList)(); return cleanup;
}
