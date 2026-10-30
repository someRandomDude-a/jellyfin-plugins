(function () {
    'use strict';
    const page = document.getElementById('deletionQueueAdmin');
    if (!page || page.dataset.bound) return;
    page.dataset.bound = 'true';
    const el = id => page.querySelector('#' + id);
    let current;
    let hubCurrent;
    let busy = false;
    let active = true, epoch = 0, cancelOptIn;
    let accountDraft = {};
    const value = (o, name) => o[name] ?? o[name[0].toUpperCase() + name.slice(1)];
    const badge = (id, update) => window.SuiteAdminUI?.integration(page, id, update);
    const authorityNumber = v => ({ Denied: 0, OwnRequests: 1, AnyAccessibleItem: 2 })[v] ?? v;
    const normalizedId = id => id.replaceAll('-', '').toLowerCase();
    // Jellyfin renders Guid values without hyphens, but System.Text.Json's Guid dictionary keys require D format.
    const policyId = id => normalizedId(id).replace(/^(.{8})(.{4})(.{4})(.{4})(.{12})$/, '$1-$2-$3-$4-$5');
    function previewPolicy() {
        el('dqPolicyControls').disabled=el('dqSuspend').checked;
        el('dqApprovalOptions').hidden=!el('dqRequireAdminApproval').checked;
        el('dqApprovalDays').disabled=!el('dqRequireAdminApproval').checked;
        el('dqApprovalPreview').hidden=!el('dqRequireAdminApproval').checked;
        el('dqActivityTimeoutOptions').hidden=!el('dqActivityTimeoutEnabled').checked;
        el('dqActivityTimeoutDays').disabled=!el('dqActivityTimeoutEnabled').checked;
        el('dqApprovalPreview').textContent = el('dqRequireAdminApproval').checked ? 'Approval required after either eligibility path.' : '';
        const number=id=>{const input=el(id);return input.value&&input.checkValidity()?Number(input.value):null;};
        window.DeletionQueueTimeline?.policy(el('dqPolicyTimeline'),{activityDays:number('dqDays'),delayHours:number('dqHours'),approvalDays:number('dqApprovalDays'),requireApproval:el('dqRequireAdminApproval').checked,paused:el('dqSuspend').checked,activityTimeoutEnabled:el('dqActivityTimeoutEnabled').checked,activityTimeoutDays:number('dqActivityTimeoutDays')});
    }
    el('dqDelayDays').addEventListener('input', () => {
        const days = el('dqDelayDays').valueAsNumber;
        el('dqHours').value = Number.isFinite(days) ? String(Math.round(days * 24)) : '';
        previewPolicy();
    });
    el('dqHours').addEventListener('input', () => { el('dqDelayDays').value = el('dqHours').value ? Number(el('dqHours').value) / 24 : ''; previewPolicy(); });
    for (const id of ['dqDays', 'dqApprovalDays', 'dqActivityTimeoutDays', 'dqActivityTimeoutEnabled', 'dqRequireAdminApproval', 'dqAuto', 'dqSuspend']) el(id).addEventListener('input', previewPolicy);
    const confirmOptIn = () => new Promise(resolve => {
        const dialog = el('dqOptInDialog');
        const finish = result => { cancelOptIn = null; dialog.close(); el('dqOptInCancel').onclick = null; el('dqOptInYes').onclick = null; dialog.oncancel = null; resolve(result); };
        cancelOptIn = () => finish(false);
        el('dqOptInCancel').onclick = () => finish(false); el('dqOptInYes').onclick = () => finish(true);
        dialog.oncancel = e => { e.preventDefault(); finish(false); };
        dialog.showModal();
    });
    const clearEvidence = () => {
        current = null; hubCurrent = null;
        for (const id of ['dqUsers', 'dqCoverage', 'dqBacklog', 'dqJournal']) el(id).replaceChildren();
        accountDraft = {}; el('dqResetUser').disabled = true; el('dqEnhanced').disabled = true;
        el('dqKey').value = ''; el('dqUrl').value = '';
        el('dqEnhancedStatus').textContent = ''; el('dqConnectionResult').textContent = '';
    };
    const error = e => { if (!active || e?.name === 'AbortError') return; clearEvidence(); el('dqError').textContent = 'Operation unavailable. Refresh and review your session, revisions and integrity status.'; console.warn('Deletion queue operation failed', e?.status); };
    const request = async (path, method = 'GET', body) => {
        const view = epoch;
        if (!active) throw new DOMException('View closed', 'AbortError');
        const result = await ApiClient.ajax({
        url: ApiClient.getUrl('DeletionQueue/' + path), type: method,
        dataType: method === 'GET' || path === 'admin/seerr/test' ? 'json' : undefined,
        contentType: 'application/json', data: body ? JSON.stringify(body) : undefined
        });
        if (!active || view !== epoch) throw new DOMException('View closed', 'AbortError');
        return result;
    };
    async function refresh() {
        if (busy || !active) return;
        const view = epoch;
        busy = true;
        page.setAttribute('aria-busy', 'true');
        try {
            current = await request('admin/status');
            el('dqError').textContent = '';
            el('dqMode').textContent = value(current, 'mode');
            badge('deletion-queue', { working: !!value(current, 'storageAvailable'), state: value(current, 'storageAvailable') ? 'Working · monitoring' : 'Unavailable', detail: value(current, 'mode') });
            badge('notifications', { working: value(current, 'notificationsReady') === true, state: value(current, 'notificationsReady') === true ? 'Ready' : 'Not ready', detail: 'New removal requests require standalone Notifications with registered types and scoped commands. Existing consent, Undo and history remain available during provider failure.' });
            if (!value(current, 'storageAvailable')) { clearEvidence(); el('dqError').textContent = 'Storage failed integrity validation; no reset was performed.'; return; }
            const policy = value(current, 'policy');
            const seerr = value(current, 'seerr');
            const settings = await request('admin/account-hub/v1/settings');
            if (!settings.ok) throw new Error('Hub adapter unavailable');
            hubCurrent = settings.data;
            el('dqHubEnabled').checked = hubCurrent.hubIntegrationEnabled;
            el('dqPublishAudit').checked = hubCurrent.publishAudit;
            el('dqDeviceBell').checked = hubCurrent.deviceBell;
            el('dqHubFallback').value = hubCurrent.hubFallbackAfterMinutes;
            try {
                const health = await request('admin/dependencies'), dependencies = health.data.items;
                const hub = dependencies.find(item => item.id === 'account-hub');
                for (const item of dependencies) if (item.id !== 'notifications') badge(item.id, { working: item.available, state: item.available ? 'Available' : 'Unavailable', detail: item.reason + ' ' + (item.available ? '' : item.fix) });
                for (const id of ['dqHubEnabled', 'dqPublishAudit', 'dqHubFallback']) { el(id).disabled = !hub?.available;el(id).closest('.checkboxContainer,.inputContainer').hidden=!hub?.available; }
            } catch {
                if (!active || view !== epoch) return;
                badge('account-hub', { working: false, state: 'Not verified', detail: 'Optional integration health could not be verified. Check Dashboard → Plugins and restart. Local queue features remain independent.' });
                for (const id of ['dqHubEnabled', 'dqPublishAudit', 'dqHubFallback']) {el(id).disabled = true;el(id).closest('.checkboxContainer,.inputContainer').hidden=true;}
            }
            el('dqUrl').value = value(seerr, 'baseUrl');
            el('dqKey').value = '';
            el('dqAuthority').value = authorityNumber(value(policy, 'defaultAuthority'));
            el('dqDays').value = value(policy, 'activityDays');
            el('dqRecentHistoryDays').value=value(policy,'recentHistoryDays')??7;
            el('dqActivityTimeoutEnabled').checked = value(policy, 'activityWaitTimeoutEnabled') === true;
            el('dqActivityTimeoutDays').value = value(policy, 'activityWaitTimeoutDays') ?? 45;
            el('dqHours').value = value(policy, 'delayHours');
            el('dqDelayDays').value = value(policy, 'delayHours') / 24;
            el('dqAuto').checked = value(policy, 'automaticDeletion') && value(policy, 'seerrManagedDeletionAcknowledged');
            const monitoringHealthy = value(current, 'clockHealthy') !== false && value(current, 'collectorHealthy') !== false && value(current, 'coverageHealthy') !== false;
            badge('deletion-queue', { working: monitoringHealthy, state: !monitoringHealthy ? 'Needs attention' : value(policy, 'suspended') ? 'Paused' : el('dqAuto').checked ? 'Automatic · safety gated' : 'Monitoring', detail: value(current,'collectorRunning')===false?'Activity collector stopped. Restart Jellyfin and inspect the server log; requests remain safety-blocked.':value(current,'coverageProblem')==='CollectorFailure'?'Activity collection was interrupted. A new observation window is starting; fresh native history and live-playback checks are still required.':'Activity and request checks are safety-gated. Changes re-evaluate active requests without executing deletions.' });
            const seerrConfigured = !!value(seerr, 'baseUrl') && value(seerr, 'keyConfigured') === true;
            badge('seerr', { name: 'Seerr API', state: seerrConfigured ? 'Saved · not tested' : 'Not configured', working: null, detail: 'Use Test connection to verify the saved key and Jellyfin server identity without changing media. Draft edits are not sent by a test.', version: null });
            el('dqAuto').disabled = !seerrConfigured && !el('dqAuto').checked;
            el('dqSeerrDependency').textContent = seerrConfigured ? '' : 'Configure and test Seerr to enable automatic removal.';
            el('dqRequireAdminApproval').checked = value(policy, 'requireAdministratorApproval');
            el('dqApprovalDays').value = value(policy, 'administratorApprovalDays') ?? 30;
            el('dqSuspend').checked = value(policy, 'suspended');
            previewPolicy();
            const selected = el('dqUsers').value;
            el('dqUsers').replaceChildren();
            const overrides = value(policy, 'accounts');
            accountDraft = Object.fromEntries(Object.entries(overrides).map(([id, authority]) => [policyId(id), authorityNumber(authority)]));
            for (const account of value(current, 'accounts')) {
                const option = document.createElement('option'); option.value = policyId(value(account, 'id'));
                option.textContent = value(account, 'username'); el('dqUsers').append(option);
            }
            if ([...el('dqUsers').options].some(o => o.value === selected)) el('dqUsers').value = selected;
            el('dqResetUser').disabled = !el('dqUsers').value;
            el('dqUserAuthority').value = accountDraft[el('dqUsers').value] ?? '';
            const available = value(value(current, 'enhanced'), 'connectionAvailable');
            el('dqEnhanced').disabled = false;
            el('dqEnhanced').hidden = !available;
            el('dqEnhancedStatus').textContent = '';
            badge('enhanced', { name: 'Jellyfin Enhanced', state: available ? 'Copy available' : undefined, detail: 'Optional Requests/Calendar integration and server-side Seerr connection copying. Use Copy Jellyfin Enhanced connection to check whether its connection can be copied.' });
            const coverage={
                coverageSince: value(current, 'coverageSince'), heartbeat: value(current, 'lastHeartbeat'),
                healthy: value(current, 'coverageHealthy'), clockHealthy: value(current, 'clockHealthy'),
                problem: value(current, 'coverageProblem'), localIntegrityVerified: value(current, 'localIntegrityVerified'),
                continuousWindowCollected: value(current, 'continuousWindowCollected'), perProposalInventoryChecksEnabled: value(current, 'perProposalInventoryChecksEnabled'),
                seerrDelegationTrusted: value(current, 'seerrDelegationTrusted'),
                independentRollbackProtection: value(current, 'independentRollbackProtection'),
                historyAuthentication: value(current, 'historyAuthentication'),
                integration: value(current, 'integration')
            };
            const make=(tag,text)=>{const e=document.createElement(tag);if(text!==undefined)e.textContent=text;return e;};
            const statusBadge=(text,tone)=>{if(window.SuiteAdminUI?.badge)return window.SuiteAdminUI.badge(text,tone);const badge=make('span');badge.className='suiteStatusBadge';badge.dataset.tone=tone;const icon=make('span',tone==='success'?'check_circle':tone==='warning'?'pending':'info');icon.className='material-icons';icon.setAttribute('aria-hidden','true');badge.append(icon,document.createTextNode(String(text??'—')));return badge;};
            function table(host,columns,rows,selectable=false){host.replaceChildren();host.classList.add('suiteTableWrap');const table=make('table'),head=make('thead'),header=make('tr'),body=make('tbody');
                for(const label of [...(selectable?['']:[]),...columns]){const th=make('th',label);th.scope='col';header.append(th);}head.append(header);table.append(head,body);host.append(table);
                const choices=[];for(const row of rows){const tr=make('tr');if(selectable){const choice=make('input');choice.type='checkbox';choice.setAttribute('aria-label',typeof row[0]==='string'?row[0]:'Select row');const td=make('td');td.append(choice);tr.append(td);choices.push(choice);}for(const value of row){const td=make('td');td.append(value instanceof Node?value:document.createTextNode(String(value??'—')));tr.append(td);}body.append(tr);}
                if(selectable){const all=make('input');all.type='checkbox';all.setAttribute('aria-label','Select all shown rows');header.firstChild.append(all);all.onchange=()=>{for(const choice of choices){choice.checked=all.checked;choice.closest('tr').classList.toggle('dq-row-selected',choice.checked);}};for(const choice of choices)choice.onchange=()=>{all.checked=choices.every(c=>c.checked);all.indeterminate=choices.some(c=>c.checked)&&!all.checked;choice.closest('tr').classList.toggle('dq-row-selected',choice.checked);};}
            }
            table(el('dqCoverage'),['Check','Status'],Object.entries(coverage).map(([key,item])=>[key.replace(/([a-z])([A-Z])/g,'$1 $2').replace(/^./,c=>c.toUpperCase()),typeof item==='boolean'?statusBadge(item?'Verified':'Not verified',item?'success':'warning'):item&&typeof item==='object'?JSON.stringify(item):item??'Not recorded']));
            el('dqBacklog').replaceChildren();
            const backlog=[];
            for (const proposal of value(current, 'proposals')) {
                const details = document.createElement('details'); const summary = document.createElement('summary');
                summary.textContent = 'Review request';
                const pre = document.createElement('pre'); pre.className = 'dq-evidence';
                const rawStatus=value(proposal,'status'),status=typeof rawStatus==='number'?['Blocked','Waiting','Ready','Claimed','Uncertain','FilesDeleted','Completed','Cancelled','Expired'][rawStatus]||'Unknown':rawStatus;
                pre.textContent = JSON.stringify(proposal, null, 2); details.append(summary);backlog.push([value(value(proposal,'target'),'title'),statusBadge(status,status==='Completed'?'success':['Blocked','Waiting','Uncertain','Claimed'].includes(status)?'warning':'neutral'),details]);
                const progress=value(current,'proposalProgress');
                const progressKey=progress&&Object.keys(progress).find(k=>normalizedId(k)===normalizedId(value(proposal,'id')));
                if(progressKey)window.DeletionQueueTimeline?.render(progress[progressKey],details);
                const checks=document.createElement('p');
                const evidence=value(proposal,'lastCheckEvidence'),checked=value(proposal,'lastCheckedAt');
                const blocker=document.createElement('p');blocker.className='dq-blocker';
                const reason=value(proposal,'reason'),failure=value(proposal,'lastVerificationFailureCode');
                const explanations={TargetVerificationRequired:'Media identity changed. Compare the saved and current identifiers below; do not bypass this check.',SeerrNotConfigured:'The Seerr connection is not configured.',SeerrServerAssociationUnverified:'Seerr is not verified as connected to this Jellyfin server.',TargetUnavailable:'The media record is missing, inaccessible, or its provider identity could not be verified.',TargetEditionUnverified:'The requested edition is not mapped to the same accessible Jellyfin item.',SeerrTargetNotManaged:'Seerr has no verified Sonarr/Radarr target for this edition.',OwnershipScanIncomplete:'Seerr request ownership could not be fully verified.',ActivityTargetUnverified:'The Jellyfin item has no verified TMDB identity.',ProposalNotPermitted:'The current requester is not permitted to request this removal.',VerificationUnavailable:'A fresh check failed or timed out. Check Seerr connectivity and collector health; no removal is authorized.',AdministratorApprovalRequired:'A current administrator approval is required.',ConsentRequired:'Required viewer consent is missing or no longer current.',ActivityCoverageIncomplete:'The playback/history scan is incomplete.',PlaybackActive:'Playback is still active.',LocalCheckpointRequired:'The authenticated local storage checkpoint is not verified.',SeerrDelegationRequired:'The removal service is not verified.',AutomaticRuleVerificationRequired:'The originating automatic rule could not be revalidated.',OwnerNotAuthorized:'Current requester access or ownership is no longer authorized.',Suspended:'Removal processing is paused.',ClockUnverified:'The server clock is not verified.',DelayRequired:'Safety checks passed; the safety delay has not elapsed.'};
                blocker.textContent='Current blocker: '+(explanations[failure||reason]||reason||'No blocker recorded');details.append(blocker);
                const differences=value(evidence,'targetDifferences');
                if(Array.isArray(differences)&&differences.length){
                    const list=document.createElement('ul');list.setAttribute('aria-label','Media identity mismatches');
                    for(const difference of differences){const item=document.createElement('li');item.textContent=value(difference,'field')+': saved '+value(difference,'expected')+' → current '+value(difference,'current');list.append(item);}details.append(list);
                }
                checks.textContent=(checked?'Last safety-check attempt: '+new Date(checked).toLocaleString()+'. ':'No safety-check attempt recorded. ')+(evidence?[
                    ['ownerAuthorized','Access/ownership'],['targetVerified','Media target'],['completeActivityScan','Live activity scan'],['nativeHistoryVerified','Native history'],['localCheckpointVerified','Storage checkpoint'],['seerrDelegationVerified','Removal service'],['automaticRuleVerified','Automatic rule (if required)'],['administratorAuthorized','Administrator approval (if required)']
                ].map(([key,label])=>label+': '+((key==='administratorAuthorized'&&!value(policy,'requireAdministratorApproval')||key==='automaticRuleVerified'&&!value(proposal,'requiresAutomaticRuleValidation'))?'not required':value(evidence,key)?'verified':'not verified')).join(' · '):'No completed evidence set; inspect connection and collector health.');
                details.append(checks);
                const technical=make('details');technical.append(make('summary','Technical details'),pre);details.append(technical);
                if (!value(proposal, 'terminal') && !value(proposal, 'claim')) {
                    const snapshots = value(current, 'administratorApprovalSnapshots');
                    const key = Object.keys(snapshots).find(k => normalizedId(k) === normalizedId(value(proposal, 'id')));
                    for (const [label, approved] of [...(value(policy, 'requireAdministratorApproval') ? [['Approve removal', true]] : []), ['Deny request', false]]) {
                        const decision = document.createElement('button'); decision.type = 'button'; decision.className = 'emby-button raised';
                        decision.textContent = label;decision.dataset.tone=approved?'success':'danger';
                        decision.onclick = async () => {
                            decision.disabled = true;
                            try { await request('admin/proposals/' + value(proposal, 'id') + (approved ? '/approval' : '/deny'), 'POST', {
                                commandId: crypto.randomUUID(), revision: value(proposal, 'revision'), snapshot: snapshots[key], approved }); await refresh(); }
                            catch (err) { error(err); } finally { decision.disabled = false; }
                        };
                        details.append(decision);
                    }
                }
                if (value(proposal, 'status') === 4 || value(proposal, 'status') === 'Uncertain') {
                    const reconcile = document.createElement('button'); reconcile.type = 'button'; reconcile.className = 'emby-button raised';
                    reconcile.textContent = 'Recheck Seerr outcome (never resend deletion)';
                    reconcile.onclick = async () => {
                        reconcile.disabled = true;
                        try { await request('admin/proposals/' + value(proposal, 'id') + '/reconcile', 'POST', { commandId: crypto.randomUUID(), revision: value(proposal, 'revision') }); await refresh(); }
                        catch (err) { error(err); } finally { reconcile.disabled = false; }
                    };
                    details.append(reconcile);
                }
            }
            table(el('dqBacklog'),['Media','Status','Details'],backlog,true);
            table(el('dqJournal'),['Sequence','Time','Event','Actor'],(value(current,'journal')||[]).map(entry=>[value(entry,'sequence'),new Date(value(entry,'at')).toLocaleString(),statusBadge(value(entry,'kind'),/Failed|Uncertain|Blocked/.test(value(entry,'kind'))?'warning':/Completed|Verified/.test(value(entry,'kind'))?'success':'neutral'),value(entry,'actor')]),true);
        } catch (e) { error(e); } finally { if (view === epoch) { busy = false; page.setAttribute('aria-busy', 'false'); } }
    }
    el('dqRefresh').addEventListener('click', refresh);
    el('dqRefresh').addEventListener('click', () => page.dispatchEvent(new Event('suite-status-refresh')));
    el('dqHubSettings').addEventListener('submit', async e => {
        e.preventDefault(); if (!hubCurrent || busy) return;
        try {
            await request('admin/account-hub/v1/settings', 'PUT', { revision: hubCurrent.revision, commandId: crypto.randomUUID(),
                hubIntegrationEnabled: el('dqHubEnabled').checked,
                publishAudit: el('dqPublishAudit').checked, deviceBell: el('dqDeviceBell').checked,
                hubFallbackAfterMinutes: Number(el('dqHubFallback').value) });
            await refresh();
        } catch (err) { error(err); }
    });
    el('dqUsers').addEventListener('change', () => { el('dqUserAuthority').value = accountDraft[el('dqUsers').value] ?? ''; });
    el('dqUserAuthority').addEventListener('change', () => {
        const id = el('dqUsers').value; if (!id) return;
        if (el('dqUserAuthority').value === '') delete accountDraft[id]; else accountDraft[id] = Number(el('dqUserAuthority').value);
    });
    el('dqEnhanced').addEventListener('click', async () => {
        if (!current || busy) return;
        if (!value(value(current, 'enhanced'), 'connectionAvailable')) { el('dqEnhancedStatus').textContent = 'A connection cannot be copied: enable a compatible Jellyfin Enhanced plugin and configure exactly one enabled Seerr URL with a valid API key. Multiple URLs require manual selection here.'; return; }
        if (!confirm('Copy the current Enhanced Seerr connection? This replaces the queue connection and restarts activity coverage.')) return;
        try { await request('admin/seerr/enhanced', 'POST', { commandId: crypto.randomUUID(), revision: value(value(current, 'seerr'), 'revision') }); await refresh(); el('dqEnhancedStatus').textContent = 'Connection copied. Use Test connection to verify it.'; }
        catch (err) { if (active && err?.name !== 'AbortError') el('dqEnhancedStatus').textContent = 'The connection could not be copied. Refresh and check Enhanced availability, your session and the saved connection revision.'; }
    });
    el('dqTestConnection').addEventListener('click', async () => {
        const button = el('dqTestConnection'); if (!current || button.disabled) return;
        button.disabled = true; el('dqConnectionResult').textContent = 'Testing the saved connection…';
        try { const result = await request('admin/seerr/test', 'POST', {}); const working = value(result, 'working') === true;
            el('dqConnectionResult').textContent = value(result, 'message'); badge('seerr', { name: 'Seerr API', working, state: working ? 'Verified' : 'Failed test', detail: value(result, 'message'), version: value(result, 'version') || null });
        } catch (err) { if (active && err?.name !== 'AbortError') { el('dqConnectionResult').textContent = 'Test could not be confirmed. Check administrator authorization and saved settings, then retry manually.'; badge('seerr', { working: false, state: 'Not verified', detail: el('dqConnectionResult').textContent }); } }
        finally { button.disabled = false; }
    });
    el('dqResetUser').addEventListener('click', async () => {
        if (!current || busy || !el('dqUsers').value || !confirm('Reset this user’s permission and personal preferences? Retained data and existing deadlines will not be reset. Other unsaved edits are not saved.')) return;
        const id = el('dqUsers').value;
        const account = value(current, 'accounts').find(a => normalizedId(value(a, 'id')) === normalizedId(id));
        if (!account) return;
        try { await request('admin/users/' + id + '/reset', 'POST', { commandId: crypto.randomUUID(),
            revision: value(value(current, 'policy'), 'revision'), preferencesRevision: value(account, 'preferencesRevision') }); await refresh(); }
        catch (err) { error(err); }
    });
    el('dqResetAllPermissions').addEventListener('click', async () => {
        const control = el('dqResetAllPermissions');
        if (!current || busy || control.disabled || !confirm('Reset all users to the saved global removal permission? Personal preferences, consent and history are kept. Unsaved edits are not saved.')) return;
        control.disabled = true;
        try {
            await request('admin/users/reset-permissions', 'POST', { commandId: crypto.randomUUID(), revision: value(value(current, 'policy'), 'revision') });
            await refresh();
        } catch (err) { error(err); }
        finally { control.disabled = false; }
    });
    const hide = () => { active = false; epoch++; busy = false; cancelOptIn?.(); clearEvidence(); page.setAttribute('aria-busy', 'false'); };
    const show = () => { active = true; refresh(); };
    page.addEventListener('pageshow', show); page.addEventListener('viewshow', show);
    page.addEventListener('pagehide', hide); page.addEventListener('viewhide', hide); page.addEventListener('viewdestroy', hide);
    window.addEventListener('pagehide', hide);
    window.addEventListener('hashchange', hide);
    el('dqConnection').addEventListener('submit', async e => {
        e.preventDefault(); if (!current || busy) return;
        try {
            const payload = { commandId: crypto.randomUUID(), revision: value(value(current, 'seerr'), 'revision'), baseUrl: el('dqUrl').value, apiKey: el('dqKey').value || null };
            const pending = request('admin/seerr', 'PATCH', payload); el('dqKey').value = ''; payload.apiKey = null;
            await pending;
            await refresh();
        } catch (err) { error(err); }
    });
    el('dqPolicy').addEventListener('submit', async e => {
        e.preventDefault(); if (!current || busy) return;
        const old = value(current, 'policy');
        const accounts = { ...accountDraft };
        const automaticDeletion = el('dqAuto').checked;
        if (automaticDeletion && !(value(old, 'automaticDeletion') && value(old, 'seerrManagedDeletionAcknowledged')) && !await confirmOptIn()) return;
        if (!active) return;
        try {
            await request('admin/policy', 'PATCH', { commandId: crypto.randomUUID(), revision: value(old, 'revision'), policy: {
                defaultAuthority: Number(el('dqAuthority').value), accounts,
                activityDays: Number(el('dqDays').value), delayHours: Number(el('dqHours').value), administratorApprovalDays: Number(el('dqApprovalDays').value),
                activityWaitTimeoutEnabled: el('dqActivityTimeoutEnabled').checked, activityWaitTimeoutDays: Number(el('dqActivityTimeoutDays').value),
                recentHistoryDays:Number(el('dqRecentHistoryDays').value),
                automaticDeletion, seerrManagedDeletionAcknowledged: automaticDeletion,
                requireAdministratorApproval: el('dqRequireAdminApproval').checked, suspended: el('dqSuspend').checked
            } });
            await refresh();
        } catch (err) { error(err); }
    });
    refresh();
}());
