// Theme-owned native controls. The host request function owns native auth, aborts, fresh-proof dialogs and proof erasure.
export async function mountSeerrPanel(root, { request, make, field, table, button, signal }) {
    let disposed = false, nativeSkip = 0, upstreamSkip = 0, jobsSkip = 0, selected, configured = false;
    const epochs = { connection: 0, people: 0, editor: 0, directory: 0, jobs: 0 };
    const current = (key, epoch) => !disposed && !signal?.aborted && epochs[key] === epoch;
    const message = make('p'); message.setAttribute('role', 'status'); message.setAttribute('aria-live', 'polite');
    const connection = make('section'), people = make('section'), editor = make('section'), directory = make('section'), jobs = make('section');
    root.replaceChildren(make('h2', 'Seerr account lifecycle'), message, connection, people, editor, directory, jobs);
    const run = work => async () => { if (disposed || signal?.aborted) return; try { message.textContent = ''; await work(); } catch (e) { if (!disposed && e.name !== 'AbortError') message.textContent = e.message; } };
    const action = (label, fn) => button(label, run(fn));
    const dependent = (label, fn) => { const b = action(label, fn); b.disabled = !configured; b.title = configured ? '' : 'Configure the Seerr URL and API key in Settings, save, then discover / verify the connection.'; return b; };
    const command = async (operation, payload) => {
        const workspace = await request('workspace');
        if (disposed || signal?.aborted) return;
        const result = await request('seerr/commands/' + operation, 'POST', { commandId: crypto.randomUUID(), revision: workspace.revision, payload }, true);
        if (!disposed) { message.textContent = 'Command accepted. Review the durable job state; acceptance does not mean the upstream effect completed.'; await loadJobs(); }
        return result;
    };
    async function loadConnection() {
        const epoch = ++epochs.connection;
        const data = await request('seerr/connection'); if (!current('connection', epoch)) return;
        configured = !!data.baseUrl && data.keyConfigured === true;
        connection.replaceChildren(make('h3', 'Connection and capabilities'), make('p', data.capabilities ? 'Verified Seerr ' + data.capabilities.version + ' via ' + data.capabilities.source + '. Connection identity: ' + data.capabilities.fingerprint : 'Not verified. Configure the URL/API key in Settings, then discover. HTTP/private-network access requires explicit approval.'),
            ...(data.warnings || []).map(w => make('p', w)), dependent('Discover / verify connection', async () => { await request('seerr/test', 'POST', {}); await loadConnection(); if (selected) await edit(); }));
    }
    async function loadPeople() {
        const epoch = ++epochs.people;
        const data = await request('users?skip=' + nativeSkip + '&limit=50'); if (!current('people', epoch)) return;
        people.replaceChildren(make('h3', 'Authoritative native accounts'), table(['Native account', 'State', 'Actions'], data.items.map(user => {
            const choose = dependent('Manage Seerr for ' + user.username, async () => { selected = Object.freeze({ ...user }); upstreamSkip = 0; await edit(); }); choose.disabled ||= user.administrator || user.disabled;
            return [user.username, user.administrator ? 'Protected administrator' : user.disabled ? 'Disabled' : 'Active', choose];
        })), action('Previous accounts', async () => { nativeSkip = Math.max(0, nativeSkip - 50); await loadPeople(); }), action('Next accounts', async () => { if (nativeSkip + 50 < data.total) nativeSkip += 50; await loadPeople(); }));
    }
    async function edit() {
        if (!selected || disposed) return;
        const epoch = ++epochs.editor, user = selected;
        ++epochs.directory; editor.replaceChildren(); directory.replaceChildren();
        const data = await request('users/' + user.id + '/seerr'); if (!current('editor', epoch)) return;
        editor.replaceChildren(make('h3', 'Seerr for ' + user.username));
        if (!data.linked) {
            editor.append(make('p', 'No verified link. Import uses this exact native ID and Seerr defaults. It never creates a local-password account or guesses by name.'),
                dependent('Provision native account', async () => { await command('provision', { userId: user.id }); }), make('p', 'To link an existing account, review its immutable native ID in the directory below. Display names are never identity evidence.'));
            await loadDirectory(user); return;
        }
        editor.append(make('p', 'Verified upstream account #' + data.link.seerrId + '. Drift: ' + (data.drift ? 'Detected' : 'None') + '. Unknown permission bits are preserved.'));
        const desired = data.link.desired || data.actual, permissions = field('Known permission bitmask (no Admin)', desired.permissions, { min: 0, max: 1610612734 });
        const movieLimit = field('Movie quota limit (0 unlimited; blank inherits)', desired.movieQuota.limit ?? '', { min: 0, max: 100000 }); movieLimit.input.type = 'number';
        const movieDays = field('Movie quota days (blank inherits)', desired.movieQuota.days ?? '', { min: 1, max: 365 }); movieDays.input.type = 'number';
        const tvLimit = field('TV quota limit (0 unlimited; blank inherits)', desired.tvQuota.limit ?? '', { min: 0, max: 100000 }); tvLimit.input.type = 'number';
        const tvDays = field('TV quota days (blank inherits)', desired.tvQuota.days ?? '', { min: 1, max: 365 }); tvDays.input.type = 'number';
        editor.append(permissions.root, movieLimit.root, movieDays.root, tvLimit.root, tvDays.root,
            action('Queue desired state / reconcile', async () => { await command('desired', { userId: user.id, desired: { permissions: permissions.value(), movieQuota: { limit: movieLimit.value(), days: movieDays.value() }, tvQuota: { limit: tvLimit.value(), days: tvDays.value() } } }); }),
            action('Unlink without upstream deletion', async () => { await command('unlink', { userId: user.id }); await edit(); }), action('Refresh actual state', edit),
            action('Read request history (no identities)', async () => {
                const history = await request('users/' + user.id + '/seerr/requests?limit=20&skip=0');
                if (current('editor', epoch)) editor.append(table(['Request', 'Kind', 'State', 'Created'], history.items.map(r => [r.id, r.mediaType, r.status, r.createdAt])));
            }));
    }
    const canonical = value => String(value || '').replaceAll('-', '').toLowerCase();
    async function loadDirectory(user = selected) {
        const epoch = ++epochs.directory;
        if (!configured || !user) return;
        const data = await request('seerr/users?skip=' + upstreamSkip + '&limit=100'); if (!current('directory', epoch) || selected !== user) return;
        directory.replaceChildren(make('h3', 'Seerr directory — explicit identity review'), table(['Account', 'Native ID evidence', 'State', 'Action'], data.items.map(account => {
            const link = dependent('Verify and link #' + account.id, async () => { await command('link', { userId: user.id, seerrId: account.id }); if (selected === user) await edit(); });
            link.disabled ||= account.protectedAccount || canonical(account.jellyfinUserId) !== canonical(user.id);
            return [account.username || '#' + account.id, account.jellyfinUserId || 'No native evidence — link in Seerr first', account.protectedAccount ? 'Protected' : 'Review', link];
        })), action('Previous Seerr accounts', async () => { upstreamSkip = Math.max(0, upstreamSkip - 100); await loadDirectory(); }), action('Next Seerr accounts', async () => { if (data.items.length === 100) upstreamSkip += 100; await loadDirectory(); }));
    }
    async function loadJobs() {
        const epoch = ++epochs.jobs;
        const data = await request('seerr/jobs?skip=' + jobsSkip + '&limit=50'); if (!current('jobs', epoch)) return;
        jobs.replaceChildren(make('h3', 'Durable jobs — ' + data.attention + ' require review'), make('p', 'Deletion jobs wait for native absence. Retrying never clears an uncertain import, connection fingerprint or saved target. Cancel does not undo an upstream effect.'), table(['Job', 'Kind', 'State', 'Attempts', 'Safe error', 'Actions'], data.items.map(job => {
            const controls = make('div');
            if (!job.active && job.state !== 'completed') controls.append(dependent('Retry after review', async () => { await command('retry', { jobId: job.id }); }));
            if (job.state !== 'running' && job.state !== 'completed') controls.append(action('Cancel follow-up', async () => { await command('cancel', { jobId: job.id }); }));
            return [job.id, job.kind, job.state, job.attempts, job.error || '', controls];
        })), action('Refresh jobs', loadJobs), action('Previous jobs', async () => { jobsSkip = Math.max(0, jobsSkip - 50); await loadJobs(); }), action('Next jobs', async () => { if (jobsSkip + 50 < data.total) jobsSkip += 50; await loadJobs(); }));
    }
    const cleanup = () => { disposed = true; selected = undefined; root.replaceChildren(); };
    signal?.addEventListener('abort', cleanup, { once: true });
    await loadConnection();
    if (!disposed && !signal?.aborted) await Promise.all([loadPeople(), loadJobs()]);
    return cleanup;
}
