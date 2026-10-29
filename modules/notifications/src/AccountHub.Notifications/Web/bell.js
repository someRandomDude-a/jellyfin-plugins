/* Account Hub hosted-web adapter. Load once inside Jellyfin's authenticated web context. */
(() => {
    'use strict';
    if (window.__accountHubBellV1) return;
    window.__accountHubBellV1 = true;
    let button, panel, timer, etag, rows = [], hasMore = false, paused = false, delay = 30000, identity, generation = 0, inFlight = false, skip = 0, settingsView = false, unreadCount = 0;
    const forbidden = () => /login|challenge|twofactorauth|invite|reset|linktoken/i.test(location.href);
    const client = () => window.ApiClient;
    const apiPrefix = document.querySelector('[data-notifications-bootstrap]')?.dataset.notificationsApi || 'AccountHub/api/v1/';
    const expanded = new Set(), openGroups = new Set(); let moreObserver, moreSentinel, loadMoreRequested = false;
    let historyView=false,blockedView=false,blockedLimit=500,retentionDays=90;
    let viewedObserver;const viewedKeys=new Set();
    let liveSocket,liveTimer,liveConnecting=false,liveDirty=false,liveBackoff=1000;
    const mediaImages=new Map();let wantedImages=new Set();
    let pushScope;
    let deviceConfig,deviceJob,refreshDevicePanel,deviceCleanup=Promise.resolve();const deviceMemory=new Map();
    const deviceKey=()=> 'jellyfin.push.choice:'+identity;
    function deviceChoice(value){const key=deviceKey();if(value!==undefined){deviceMemory.set(key,value);try{window.localStorage?.setItem(key,value);}catch{}return value;}try{return window.localStorage?window.localStorage.getItem(key)||deviceMemory.get(key):deviceMemory.get(key)||'off';}catch{return deviceMemory.get(key)||'off';}}
    const deviceSupported=()=>window.isSecureContext&&window.navigator?.serviceWorker&&window.PushManager&&window.Notification;
    async function enrollDevice(config,valid,ask){
        // Keep this before the first await: permission prompts require a gesture.
        const permission=ask&&window.Notification.permission!=='denied'?window.Notification.requestPermission():Promise.resolve(window.Notification.permission);
        if(await permission!=='granted'){if(valid())deviceChoice('off');throw Error('Allow notifications in the browser’s site settings, then turn this switch on again.');}
        await deviceCleanup;if(!valid()||deviceChoice()==='off')return false;
        const worker=url('notifications/push/sw.js'),scope=new URL('./',worker).href;pushScope=scope;
        const registration=await window.navigator.serviceWorker.register(worker,{scope});
        const pending=registration.installing||registration.waiting;
        if(!registration.active&&pending)await new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(Error('Worker activation timed out.')),10000);const check=()=>{if(pending.state==='activated'){clearTimeout(timer);resolve();}else if(pending.state==='redundant'){clearTimeout(timer);reject(Error('Worker activation failed.'));}};pending.addEventListener('statechange',check);check();});
        if(!valid()||deviceChoice()==='off')return false;
        const bytes=Uint8Array.from(atob(config.publicKey.replace(/-/g,'+').replace(/_/g,'/')+'='.repeat((4-config.publicKey.length%4)%4)),c=>c.charCodeAt(0));
        const subscription=await registration.pushManager.getSubscription()||await registration.pushManager.subscribe({userVisibleOnly:true,applicationServerKey:bytes});
        if(!valid()||deviceChoice()==='off'){await subscription.unsubscribe();return false;}
        const value=subscription.toJSON();try{await request('notifications/push','POST',{endpoint:value.endpoint,p256dh:value.keys.p256dh,auth:value.keys.auth});}catch(error){await subscription.unsubscribe();throw error;}
        if(!valid()||deviceChoice()==='off'){await subscription.unsubscribe();if(valid()){const hash=[...new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(subscription.endpoint)))].map(v=>v.toString(16).padStart(2,'0')).join('');await request('notifications/push/'+hash,'DELETE');}return false;}
        return true;
    }
    function applyDeviceDefault(gesture=false){
        if(!deviceSupported()||!deviceConfig?.enabled||deviceJob||deviceChoice()==='off'||!(deviceChoice()==='on'||deviceConfig.defaultOn))return;
        if(window.Notification.permission==='denied'||window.Notification.permission!=='granted'&&!gesture)return;
        const started=generation,token=client().accessToken(),valid=()=>started===generation&&token===client().accessToken()&&!forbidden();
        deviceJob=enrollDevice(deviceConfig,valid,window.Notification.permission!=='granted').catch(()=>{}).finally(()=>{deviceJob=null;refreshDevicePanel?.();});
    }
    let localRows = [], localSequence = 0, settingsElement, toast, toastTimer;
    const toasted = new Set();
    const url = path => client().getUrl(apiPrefix + path);
    const element = (tag, text) => { const e = document.createElement(tag); if (tag === 'button') { e.className = 'emby-button raised'; e.setAttribute('is', 'emby-button'); e.type = 'button'; } if (text) e.textContent = text; return e; };
    function clear(clearLocal = false) {
        if(clearLocal){deviceConfig=null;if(pushScope){const scope=pushScope;pushScope=null;deviceCleanup=deviceCleanup.then(()=>window.navigator?.serviceWorker?.getRegistration(scope)).then(async registration=>{if(registration?.scope!==scope)return;const subscription=await registration.pushManager.getSubscription();await subscription?.unsubscribe();await registration.unregister();}).catch(()=>{});}}
        if (clearLocal) localRows = [];
        clearTimeout(timer); timer = null; etag = undefined; rows = [];
        generation++; skip = 0; hasMore = false; unreadCount = 0; settingsView = false;
        clearTimeout(liveTimer);liveTimer=null;const previousSocket=liveSocket;liveSocket=null;previousSocket?.close();liveConnecting=false;liveDirty=false;
        historyView=false;blockedView=false;blockedLimit=500;retentionDays=90;
        expanded.clear(); openGroups.clear(); moreObserver?.disconnect(); loadMoreRequested = false;
        viewedObserver?.disconnect();viewedKeys.clear();
        for(const image of mediaImages.values()){image.abort?.abort();if(image.url)window.URL?.revokeObjectURL(image.url);}mediaImages.clear();
        settingsElement = null;
        refreshDevicePanel=null;
        toast?.remove(); toast = null; clearTimeout(toastTimer); if (clearLocal) toasted.clear();
        button?.remove(); panel?.remove(); button = panel = null;
    }
    async function request(path, method = 'GET', body, headers = {}) {
        const started = generation;
        const startedToken = client().accessToken();
        const response = await fetch(url(path), {
            method, credentials: 'omit', cache: 'no-store',
            headers: { Authorization: 'MediaBrowser Token="' + client().accessToken() + '"',
                'Content-Type': 'application/json', ...headers }, body: body === undefined ? undefined : JSON.stringify(body)
        });
        if (response.status === 304) return { unchanged: true };
        const json = await response.json().catch(() => ({}));
        if (started !== generation || startedToken !== client().accessToken()) throw new Error('stale_session');
        if ((response.status === 401 || response.status === 403) && json.twoFactorRequired === true) {
            paused = true; rows = []; etag = undefined; panel?.replaceChildren();
            button.textContent = 'Complete 2FA'; button.setAttribute('aria-label', 'Complete 2FA to see notifications');
            const link = element('a', 'Complete 2FA in Jellyfin Security');
            link.href = client().getUrl('TwoFactorAuth/Login'); panel?.append(link);
            throw new Error('two_factor_required');
        }
        if (!response.ok || json.ok !== true) {
            if (response.status === 401 || response.status === 403) { rows = []; etag = undefined; panel?.replaceChildren(); paused = true; }
            const id=json.error?.errorId||json.errorId||response.headers?.get?.('X-Suite-Error-Id');
            if(panel&&!panel.hidden&&typeof id==='string'&&/^[a-f0-9-]{36}$/i.test(id)){const status=panel.querySelector('.notificationErrorStatus')||element('p');status.className='notificationErrorStatus';status.setAttribute('role','status');status.textContent='Unable to complete the notification request. Error ID: '+id;panel.append(status);}
            throw new Error('request_failed');
        }
        return { ...json.data, etag: response.headers.get('ETag') };
    }
    function close(restoreFocus = true) { if (panel) panel.hidden = true; settingsView = false; button?.setAttribute('aria-expanded', 'false'); if (restoreFocus) button?.focus(); }
    const resolveLink = link => link.startsWith('web/index.html#/') ? client().getUrl(link) : link;
    function showToast() {
        if(historyView||blockedView)return;
        const recent = rows.filter(row => !row.read && !row.event.retroactive&&!['silent','low'].includes(row.event.importance)&&!toasted.has(row.id) && row.event.layout?.toastSeconds > 0 && Date.now() - new Date(row.event.occurredAt).getTime() < 120000);
        for (const row of recent) toasted.add(row.id);
        const row = recent[0]; if (!row || !panel?.hidden) return;
        toast?.remove(); clearTimeout(toastTimer);
        toast = element('aside'); toast.className = 'dialog paperList notificationToast'; toast.setAttribute('aria-label', 'Notification');
        const text = element('p', row.event.body); text.setAttribute('role', 'status'); toast.append(text);
        const view = element('a', 'View request'); view.href = resolveLink(row.event.link || '#/home'); view.className = 'emby-button button-flat'; toast.append(view);
        for (const command of row.event.commands || []) if (command.enabled) {
            const cancel = element('button', command.label); cancel.className = 'emby-button button-flat';
            cancel.onclick = async () => {
                cancel.disabled = true;
                try { await request('notifications/' + row.id + '/actions', 'POST', { id: command.id, commandId: crypto.randomUUID(), revision: command.revision, snapshot: command.snapshot }); toast?.remove(); etag = undefined; void poll(); }
                catch { text.textContent = 'Request changed. Open Notifications to review it.'; }
            }; toast.append(cancel);
        }
        document.body.append(toast);
        const dismiss = () => { clearTimeout(toastTimer); toastTimer = setTimeout(() => toast?.remove(), Math.min(10, row.event.layout.toastSeconds) * 1000); };
        toast.addEventListener('pointerenter', () => clearTimeout(toastTimer)); toast.addEventListener('focusin', () => clearTimeout(toastTimer));
        toast.addEventListener('pointerleave', dismiss); toast.addEventListener('focusout', dismiss); dismiss();
    }
    function timestamp(value) {
        const date = new Date(value);
        if (!Number.isFinite(date.getTime())) return { relative: 'Time unavailable', exact: 'Time unavailable', age: 0 };
        const seconds = (date.getTime() - Date.now()) / 1000, age = -seconds;
        let relative = 'Just now';
        if (Math.abs(seconds) >= 60) {
            const [unit, scale] = Math.abs(seconds) >= 86400 * 365 ? ['year', 86400 * 365] : Math.abs(seconds) >= 86400 * 30 ? ['month', 86400 * 30] : Math.abs(seconds) >= 86400 ? ['day', 86400] : Math.abs(seconds) >= 3600 ? ['hour', 3600] : ['minute', 60];
            relative = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' }).format(Math.round(seconds / scale), unit);
        }
        return { relative, exact: date.toLocaleString(), iso: date.toISOString(), age };
    }
    function render() {
        if (!panel) return;
        viewedObserver?.disconnect();
        moreObserver?.disconnect(); moreObserver = null; moreSentinel = null;
        if (settingsView) { if (!settingsElement?.isConnected) renderSettings(); return; }
        // Rebuild controls from the current authorized projection, but do not
        // dismiss a user's open menu just because a live invalidation arrived.
        const previousMenu=panel.querySelector?.('.notificationOptions[open]');
        const menuRow=previousMenu?.closest('[data-notification-id]')?.dataset.notificationId;
        const menuFocus=previousMenu?.contains(document.activeElement)?document.activeElement?.dataset?.notificationMenuAction:null;
        wantedImages=new Set();for(const image of mediaImages.values())image.targets.clear();
        const header = element('div'); header.className = 'notificationHeader'; header.append(element('h2', 'Notifications')); panel.replaceChildren(header);
        const settings = element('button', ''); settings.className = 'paper-icon-button-light notificationSettings'; settings.setAttribute('aria-label', 'Notification settings');
        const gear = element('span', 'settings'); gear.className = 'material-icons'; gear.setAttribute('aria-hidden', 'true'); settings.append(gear);
        settings.onclick = () => { settingsView = true; render(); }; header.append(settings);
        const tabs=element('div');tabs.className='notificationViewTabs';tabs.setAttribute('role','tablist');tabs.setAttribute('aria-label','Notification views');
        for(const [view,label]of [['inbox','Notifications'],['history','History'],['blocked','Blocked']]){const tab=element('button',label);tab.className='emby-button button-flat';tab.setAttribute('role','tab');const selected=()=>view===(blockedView?'blocked':historyView?'history':'inbox');tab.setAttribute('aria-selected',String(selected()));tab.onclick=()=>{if(selected())return;historyView=view==='history';blockedView=view==='blocked';generation++;skip=0;hasMore=false;rows=[];etag=undefined;expanded.clear();render();void poll();};tabs.append(tab);}panel.append(tabs);
        if(blockedView)panel.append(element('p','Delivery was blocked by preferences. Up to '+blockedLimit+' notices are retained for '+retentionDays+' days. Retained notices become visible again when enabled; old external deliveries are not replayed.'));
        const all = element('button', 'Mark all read'); all.onclick = async () => { await request('notifications/read-all', 'POST'); for (const row of localRows) row.read = true; etag = undefined; poll(); }; panel.append(all);
        all.disabled = [...localRows, ...rows].every(row => row.read);
        all.hidden=historyView||blockedView;
        if (!rows.length && !localRows.length) panel.append(element('p', 'No notifications.'));
        const groups = { Important: [], Other: [], Older: [] };
        for (const row of [...(blockedView?[]:localRows.filter(row=>!!row.dismissed===historyView)), ...rows]) {
            const time = timestamp(row.event.occurredAt);
            const group = time.age >= 7 * 86400 ? 'Older' : row.event.importance?row.event.importance==='high'?'Important':'Other':row.event.adminOnly || ['warning', 'high', 'critical'].includes(row.event.severity) ? 'Important' : 'Other';
            groups[group].push({ row, time });
        }
        for (const [name, entries] of Object.entries(groups)) {
        if (!entries.length) continue;
        const heading = element('h2', name); heading.className = 'notificationGroupHeading'; panel.append(heading);
        entries.sort((a, b) => (b.row.event.occurredAt || '').localeCompare(a.row.event.occurredAt || ''));
        const related=new Map(),containers=new Map();
        for(const {row} of entries){const group=row.local?null:row.event.group;if(group&&/^[a-f0-9]{64}$/i.test(group.key)&&typeof group.label==='string'){const key=JSON.stringify([group.source||row.event.source,group.key]);related.set(key,(related.get(key)||0)+1);}}
        for (const { row, time } of entries) {
            let rowParent=panel;
            const group=row.local?null:row.event.group,key=group?JSON.stringify([group.source||row.event.source,group.key]):null;
            if(key&&related.get(key)>1){
                if(!containers.has(key)){
                    const cluster=element('details'),identity=JSON.stringify([name,key]);cluster.className='notificationRelatedGroup';cluster.dataset.groupKey=group.key;
                    cluster.open=openGroups.has(identity);cluster.append(element('summary',group.label+' · '+related.get(key)+' notifications'));
                    cluster.addEventListener('toggle',()=>{if(!cluster.isConnected)return;if(cluster.open)openGroups.add(identity);else openGroups.delete(identity);});panel.append(cluster);containers.set(key,cluster);
                }
                rowParent=containers.get(key);
            }
            const item = element('section'); item.dataset.unread = String(!row.read);
            item.dataset.notificationId=row.id;
            item.dataset.importance=row.event.importance||'normal';
            if (!row.event.layout?.canvas || !window.NotificationCard) addMediaImage(row,item);
            item.dataset.density = row.event.layout?.density === 'standard' ? 'standard' : 'compact';
            const mainLink = row.event.link || row.event.actions?.[0]?.link;
            const main = element(mainLink ? 'a' : 'div'); main.className = 'notificationMain';
            main.title = row.event.title + '\n' + row.event.body;
            if (mainLink) { main.href = resolveLink(mainLink); main.onclick = () => { if (!blockedView&&!row.read && !row.local) void request('notifications/' + row.id + '/read', 'POST').catch(() => {}); }; }
            main.append(element('h3', row.event.title));
            const summaryLength = Math.max(60, Math.min(300, row.event.layout?.summaryLength || 140));
            const excerpt = element('span', row.event.body.slice(0, summaryLength) + (row.event.body.length > summaryLength ? '…' : '')); excerpt.className = 'notificationExcerpt'; main.append(excerpt);
            const when = element('time', time.relative); when.title = time.exact; when.setAttribute('aria-label', time.exact); if (time.iso) when.setAttribute('datetime', time.iso); main.append(when); item.append(main);
            if (row.event.layout?.canvas && window.NotificationCard) { main.classList.add('notificationMainCanvas');window.NotificationCard.render(main, row.event, { image: () => { const holder=element('div');addMediaImage(row,holder);return holder.querySelector('img'); } }); main.append(when); }
            const details = element('details'), summary = element('summary', 'Details'); details.className = 'notificationDetails';
            details.open = expanded.has(row.id); details.addEventListener('toggle', () => { if (details.open) expanded.add(row.id); else expanded.delete(row.id); });
            details.append(summary, element('p', row.event.body));
            if (row.event.details) details.append(element('p', row.event.details));
            item.dataset.expandable = String(row.event.layout?.expandable !== false);
            if ((row.event.details || row.event.body) && (row.event.layout?.expandable !== false || !mainLink)) item.append(details);
        for (const action of row.event.actions || []) {
                const link = element('a', action.label); link.className = 'emby-button button-flat notificationAction'; link.dataset.size = action.size === 'normal' ? 'normal' : 'compact';
                link.href = resolveLink(action.link); item.append(link);
            }
            const commands = element('div'); commands.className = 'notificationQuickActions';
            const commandActions = new Map(); let commandPending = false;
            for (const command of historyView||blockedView?[]:row.event.commands || []) {
                const control = element('button', command.label); control.className = 'emby-button button-flat notificationCommand'; control.dataset.action = command.id;
                control.setAttribute('aria-pressed', String(command.selected === true)); control.disabled = !command.enabled;
                const invoke = async (dismissAfter = false) => {
                    if (commandPending || actionPending || !command.enabled) return;
                    commandPending = true;
                    const started = generation, token = client().accessToken(); let saved = false;
                    for (const button of commands.querySelectorAll('button')) button.disabled = true;
                    const status = item.querySelector('.notificationActionStatus') || element('p'); status.className = 'notificationActionStatus'; status.setAttribute('role', 'status'); status.textContent = ''; item.append(status);
                    try {
                        await request('notifications/' + row.id + '/actions', 'POST', { id: command.id, commandId: crypto.randomUUID(), revision: command.revision, snapshot: command.snapshot });
                        saved = true;
                        if (started !== generation || token !== client().accessToken()) return;
                        if (dismissAfter) {
                            command.selected = true;
                            await request('notifications/' + row.id + '/dismiss', 'POST');
                        }
                        etag = undefined; status.textContent = 'Saved'; await poll();
                        window.dispatchEvent(new Event('suite-removal-changed'));
                    } catch { if (started === generation && token === client().accessToken()) {
                        status.textContent = saved ? 'Consent saved; notification could not be hidden. Use Hide notification to retry.' : 'Request changed or action unavailable. Refresh and try again.';
                        etag = undefined; if (!saved) void poll();
                    } }
                    finally { commandPending = false; if (commands.isConnected) for (const button of commands.querySelectorAll('button')) button.disabled = !(row.event.commands || []).find(value => value.id === button.dataset.action)?.enabled; }
                };
                control.onclick = () => invoke(); commandActions.set(command.id, invoke);
                commands.append(control);
            }
            if (row.event.commands?.length) item.append(commands);
            const options = element('details'), trigger = element('summary'), menu = element('div'); options.className = 'notificationOptions'; trigger.setAttribute('aria-label', 'Notification options');
            trigger.dataset.notificationMenuAction='trigger';options.open=row.id===menuRow;
            const icon = element('span', 'more_vert'); icon.className = 'material-icons'; icon.setAttribute('aria-hidden', 'true'); trigger.append(icon); menu.className = 'dialog paperList notificationOptionsMenu'; menu.setAttribute('role', 'menu'); options.append(trigger, menu); item.append(options);
            // Native details toggle events are queued. A replaced row may emit
            // its old event after render; only a connected menu may close peers.
            options.addEventListener('toggle', () => { if (options.isConnected&&options.open) for (const other of panel.querySelectorAll?.('.notificationOptions[open]') || []) if (other !== options) other.open = false; });
            options.addEventListener('keydown', event => {
                if (event.key === 'Escape' && options.open) { event.preventDefault(); event.stopPropagation(); options.open = false; trigger.focus(); }
                if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
                    event.preventDefault(); options.open = true;
                    const controls = [...menu.querySelectorAll('button:not(:disabled)')], index = controls.indexOf(document.activeElement);
                    controls[(index + (event.key === 'ArrowDown' ? 1 : controls.length - 1) + controls.length) % controls.length]?.focus();
                }
            });
            const inboxActions = new Map();
            let actionPending = false;
            const runInboxAction = async action => {
                if (actionPending || commandPending) return;
                actionPending = true;
                for (const controls of inboxActions.values()) for (const control of controls) control.disabled = true;
                const started = generation;
                try {
                    if (row.local) { if (action === 'dismiss') row.dismissed = true; row.read = true; render(); paintCount(unreadCount); }
                    else { await request('notifications/' + row.id + '/' + action, 'POST'); if (started === generation) { etag = undefined; await poll(); } }
                } catch {
                    if (started === generation && item.isConnected) {
                        const status = element('p', 'Could not save. Try again.'); status.setAttribute('role', 'status'); item.append(status);
                    }
                } finally {
                    actionPending = false;
                    for (const controls of inboxActions.values()) for (const control of controls) control.disabled = false;
                }
            };
            for (const action of ['read', 'dismiss']) {
                if(historyView||blockedView)continue;
                if (action === 'read' && row.read) continue;
                const control = element('button', action === 'read' ? 'Mark read' : 'Hide notification'); control.setAttribute('role', 'menuitem');
                control.dataset.notificationMenuAction=action;
                control.onclick = () => runInboxAction(action);
                inboxActions.set(action, [control]);
                menu.append(control);
            }
            if (!historyView && !blockedView) {
                const swipeActions = element('div'); swipeActions.className = 'notificationSwipeActions'; swipeActions.hidden = true;
                swipeActions.setAttribute('role', 'group'); swipeActions.setAttribute('aria-label', 'Notification swipe actions');
                const hint = element('span'); hint.setAttribute('role', 'status'); swipeActions.append(hint);
                for (const [action, controls] of inboxActions) {
                    const control = element('button', action === 'read' ? 'Mark read' : 'Hide notification');
                    control.dataset.swipeAction = action; control.onclick = () => runInboxAction(action); controls.push(control); swipeActions.append(control);
                }
                const cancel = element('button', 'Cancel'); cancel.onclick = () => { swipeActions.hidden = true; }; swipeActions.append(cancel); item.append(swipeActions);
                item.className = 'notificationSwipeable';
                const consent = row.event.source === 'deletion-queue' && row.event.type?.startsWith('proposal.')
                    ? row.event.commands?.find(command => command.id === 'allow' && command.enabled && !command.selected) : undefined;
                item.title = consent ? 'Swipe or drag right to consent to removal and hide this notification. Swipe left for Hide notification.' : 'Swipe or drag right for Mark read; left for Hide notification. Select the action to confirm.';
                if (consent) { const help = element('p', 'Swipe or drag right to allow removal and dismiss this notification. Existing approvals and delays still apply.'); help.className = 'notificationSwipeHint'; item.append(help); }
                const preview = element('div'); preview.className = 'notificationSwipePreview'; preview.hidden = true; preview.setAttribute('aria-hidden', 'true'); item.append(preview);
                let gesture, suppressClickUntil = 0;
                item.addEventListener('pointerdown', event => {
                    gesture = undefined;
                    preview.hidden = true;
                    if (!['touch', 'mouse', 'pen'].includes(event.pointerType) || event.button > 0 || event.isPrimary === false || actionPending || commandPending || options.open || event.target.closest('button, input, select, textarea, summary, .notificationAction, .notificationDetails, .notificationQuickActions, .notificationSwipeActions')) return;
                    gesture = { id: event.pointerId, x: event.clientX, y: event.clientY };
                    if (event.pointerType !== 'touch') { try { item.setPointerCapture?.(event.pointerId); } catch { /* Detached/unsupported pointer capture must not break ordinary controls. */ } }
                });
                item.addEventListener('pointermove', event => {
                    if (!gesture || gesture.id !== event.pointerId) return;
                    const dx = event.clientX - gesture.x, dy = event.clientY - gesture.y;
                    if (Math.abs(dy) > 12 && Math.abs(dy) >= Math.abs(dx)) { gesture = undefined; preview.hidden = true; }
                    else if (Math.abs(dx) > 12) {
                        preview.textContent = dx > 0 ? consent && !consent.selected ? (dx >= 80 ? 'Release to consent and dismiss' : 'Swipe further to consent') : 'Mark read' : 'Hide notification';
                        preview.hidden = false;
                    }
                });
                item.addEventListener('pointerup', event => {
                    const start = gesture; gesture = undefined;
                    preview.hidden = true;
                    if (!start || start.id !== event.pointerId || actionPending || commandPending) return;
                    const dx = event.clientX - start.x, dy = event.clientY - start.y;
                    if (Math.abs(dx) < 50 || Math.abs(dx) < Math.abs(dy) * 1.5) return;
                    suppressClickUntil = Date.now() + 600;
                    if (dx >= 80 && consent && !consent.selected) { void commandActions.get('allow')?.(true); return; }
                    if (dx > 0 && consent && !consent.selected) return;
                    const action = dx > 0 ? 'read' : 'dismiss';
                    hint.textContent = action === 'read' ? row.read ? 'Already read' : 'Mark read' : 'Hide notification';
                    for (const [name, controls] of inboxActions) controls[1].hidden = name !== action;
                    swipeActions.hidden = false;
                });
                item.addEventListener('pointercancel', () => { gesture = undefined; preview.hidden = true; });
                item.addEventListener('lostpointercapture', () => { gesture = undefined; preview.hidden = true; });
                item.addEventListener('dragstart', event => { if (gesture) event.preventDefault(); });
                item.addEventListener('click', event => {
                    if (Date.now() < suppressClickUntil && !swipeActions.contains(event.target)) { event.preventDefault(); event.stopPropagation(); }
                }, true);
            }
            if (row.event.source && !row.local && row.event.severity !== 'critical') for (const [label, type] of [['Show fewer like this', row.event.type], ['Mute ' + ({ 'deletion-queue': 'removal notifications', 'private-syncplay': 'watch-party notifications', 'account-hub': 'account notifications' }[row.event.source] || row.event.source), '*']]) {
                const control = element('button', label); control.setAttribute('role', 'menuitem');
                control.dataset.notificationMenuAction=type==='*'?'mute-source':'mute-type';
                control.onclick = async () => {
                    control.disabled = true;
                    try {
                        await request('notifications/preferences/rule', 'PUT', { source: row.event.source, type, channel: 'in-app', enabled: false }); options.open = false;
                    } catch { const status = element('p', 'Could not save. Try again.'); status.setAttribute('role', 'status'); menu.append(status); }
                    finally { control.disabled = false; }
                }; menu.append(control);
            }
            rowParent.append(item);
            if(options.open&&menuFocus){const focus=menuFocus==='trigger'?trigger:[...menu.children].find(control=>control.dataset.notificationMenuAction===menuFocus);focus?.focus();}
        } }
        for(const [key,image]of mediaImages)if(!wantedImages.has(key)){image.abort?.abort();if(image.url)window.URL?.revokeObjectURL(image.url);mediaImages.delete(key);}
        const next = element('button', 'Older notifications'); next.disabled = !hasMore; next.hidden = !hasMore;
        moreSentinel = next;
        next.onclick = () => { if (inFlight || !hasMore) return; skip += 100; loadMoreRequested = true; etag = undefined; void poll(); }; panel.append(next);
        if (hasMore && !panel.hidden && typeof IntersectionObserver !== 'undefined') {
            moreObserver = new IntersectionObserver(entries => { if (entries.some(entry => entry.isIntersecting)) next.onclick(); }, { root: panel, rootMargin: '80px' });
            moreObserver.observe(next);
        }
        if(!panel.hidden&&!historyView&&!blockedView&&typeof IntersectionObserver!=='undefined'){
            const started=generation;
            viewedObserver=new IntersectionObserver(entries=>{if(started!==generation||panel.hidden||settingsView||historyView||blockedView||document.hidden)return;for(const entry of entries){if(!entry.isIntersecting||entry.intersectionRatio<.5)continue;const row=rows.find(r=>r.id===entry.target.dataset.notificationId);if(!row||row.event.firstViewedAt||!(row.event.layout?.expireAfterViewHours>0))continue;const key=row.id+':'+row.event.eventId;if(viewedKeys.has(key))continue;viewedKeys.add(key);while(viewedKeys.size>1000)viewedKeys.delete(viewedKeys.values().next().value);
                void request('notifications/'+row.id+'/viewed','POST').then(()=>{if(started!==generation)return;if(!row.read)unreadCount=Math.max(0,unreadCount-1);row.read=true;entry.target.dataset.unread='false';etag=undefined;paintCount(unreadCount);}).catch(()=>viewedKeys.delete(key));
            }},{root:panel,threshold:.5});
            for(const item of panel.querySelectorAll('section[data-notification-id]'))viewedObserver.observe(item);
        }
    }
    function addMediaImage(row,item){
        if(panel.hidden)return;
        const metadata=row.event.metadata,id=metadata?.imageItemId;
        if(!['account-hub','notifications'].includes(row.event.source)||!row.event.type?.startsWith('library.')||typeof id!=='string'||!/^([a-f0-9]{32}|[a-f0-9-]{36})$/i.test(id)||id.replaceAll('-','').toLowerCase()!==String(metadata.itemId).replaceAll('-','').toLowerCase()||typeof window.URL?.createObjectURL!=='function')return;
        const key=row.id+':'+row.event.eventId+':'+id;wantedImages.add(key);
        const image=element('img');image.className='notificationMediaImage';image.alt='';image.setAttribute('aria-hidden','true');image.dataset.kind=row.event.type.includes('.episode.')?'episode':'movie';item.append(image);
        let cached=mediaImages.get(key);if(cached){cached.targets.add(image);if(cached.url)image.src=cached.url;return;}
        if(mediaImages.size>=100){image.remove();return;}
        const started=generation,token=client().accessToken(),abort=new AbortController();cached={targets:new Set([image]),abort,url:null};mediaImages.set(key,cached);
        const deadline=setTimeout(()=>abort.abort(),5000);
        void fetch(client().getUrl('Items/'+encodeURIComponent(id)+'/Images/Primary',{maxWidth:160,maxHeight:160,quality:85}),{credentials:'omit',headers:{Authorization:'MediaBrowser Token="'+token+'"'},signal:abort.signal}).then(async response=>{
            if(!response.ok||!/^image\/(png|jpeg|webp|avif)(;|$)/i.test(response.headers.get('Content-Type')||''))throw Error('Image unavailable');const blob=await response.blob();if(blob.size>524288||started!==generation||token!==client().accessToken()||mediaImages.get(key)!==cached)throw Error('Stale image');
            cached.url=window.URL.createObjectURL(blob);for(const target of cached.targets)if(target.isConnected)target.src=cached.url;
        }).catch(()=>{if(mediaImages.get(key)===cached)mediaImages.delete(key);for(const target of cached.targets)target.remove();}).finally(()=>clearTimeout(deadline));
    }
    function renderSettings() {
        panel.replaceChildren(element('h2', 'Notification settings'));
        const back = element('button');back.className='paper-icon-button-light notificationBack';back.setAttribute('aria-label','Back to notifications');back.title='Back to notifications';const arrow=element('span','arrow_back');arrow.className='material-icons';arrow.setAttribute('aria-hidden','true');back.append(arrow); back.onclick = () => { settingsView = false; render(); }; panel.append(back);
        const view = element('div'), status = element('p'); status.className = 'notificationSettingsStatus'; status.setAttribute('role', 'status'); status.textContent = 'Loading preferences…';
        settingsElement = view;
        panel.append(status, view); const started = generation, controls = [], busy = new Set(); let rules = [], catalogs = [], loading = true, focusRequest = 0;
        const active = () => settingsView && generation === started && view.isConnected;
        const same = (a, b) => a.source === b.source && a.type === b.type && a.channel === b.channel;
        const key = rule => JSON.stringify([rule.source, rule.type, rule.channel]);
        const typeDefaults=rule=>catalogs.find(c=>c.source===rule.source)?.types?.filter(t=>t.type===rule.type||t.type==='*'||t.type.endsWith('.*')&&rule.type.startsWith(t.type.slice(0,-1))).sort((a,b)=>(b.type===rule.type?1000:b.type.length)-(a.type===rule.type?1000:a.type.length))[0]?.defaults;
        const preferenceDefault=rule=>rule.type.startsWith('category.library.')?['category.library.*','category.library.all'].includes(rule.type):(rule.channel==='in-app'?typeDefaults(rule)?.inApp??true:typeDefaults(rule)?.discord??false);
        const importanceNames = { '': 'Use server default', silent: 'Silent', low: 'Low', normal: 'Alerting', high: 'Important' };
        const paint = () => { for (const entry of controls) {
            const saved = rules.find(rule => same(rule, entry.rule));
            const enabled = saved?.enabled ?? preferenceDefault(entry.rule);
            const parentOff=rules.some(rule=>!same(rule,entry.rule)&&!rule.enabled&&rule.channel===entry.rule.channel&&(rule.source==='*'||rule.source===entry.rule.source)&&(rule.type==='*'||rule.type.endsWith('.*')&&entry.rule.type.startsWith(rule.type.slice(0,-1))));
            const pending = busy.has(key(entry.rule));
            entry.button.disabled = loading || pending || parentOff; entry.button.setAttribute('aria-checked', String(enabled));
            entry.row.setAttribute('aria-busy', String(pending));
            if (entry.caption) entry.caption.textContent = parentOff ? 'Off — enable the parent notifications first' : !enabled ? 'Off' : saved?.importance ? importanceNames[saved.importance] : 'Server default · ' + importanceNames[typeDefaults(entry.rule)?.importance || 'normal'];
            for (const choice of entry.choices || []) { if (!pending) choice.checked = choice.value === (saved?.importance || ''); choice.disabled = loading || pending || parentOff || !enabled; }
        } };
        async function save(rule) {
            rule={...rules.find(value=>same(value,rule)),...rule};
            if (!active() || loading || busy.has(key(rule))) return;
            const focused = document.activeElement, focusVersion = ++focusRequest;
            busy.add(key(rule)); paint();
            try {
                await request('notifications/preferences/rule', 'PUT', rule); if (!active()) return;
                rules = rules.filter(value => !same(value, rule)); rules.push(rule); status.textContent = 'Saved'; renderMuted(); etag = undefined; void poll();
            } catch { if (active()) status.textContent = 'Could not save. Try again.'; }
            finally { busy.delete(key(rule)); if (active()) {
                paint();
                if (focusVersion === focusRequest && focused?.isConnected && !focused.disabled && document.activeElement === document.body) focused.focus();
            } }
        }
        function preferenceRow(label, source, type, channel, actionName, parent, { importance = false, description = '' } = {}) {
            const row = element('div'); row.className = 'notificationPreference' + (importance ? ' notificationCategory' : '');
            const rule={source,type,channel,enabled:true}, entry={row,rule,choices:[]};
            if (importance) {
                const details = element('details'), summary = element('summary'), caption = element('small');
                details.className = 'notificationCategoryDetails'; summary.append(element('span', label), caption); entry.caption = caption; details.append(summary);
                if (description) details.append(element('p', description));
                const choices = element('fieldset'); choices.className = 'notificationImportance'; choices.setAttribute('role', 'radiogroup'); choices.setAttribute('aria-label', actionName + ' importance');
                choices.append(element('legend', 'Importance'));
                const group = 'notification-importance-' + started + '-' + controls.length;
                for (const value of ['', 'normal', 'high', 'silent', 'low']) {
                    const option = element('label'), input = element('input'); input.type = 'radio'; input.name = group; input.value = value;
                    input.setAttribute('aria-label', actionName + ' ' + importanceNames[value]); input.disabled = true;
                    input.onchange = () => { if (input.checked) return save({ ...rule, enabled: rules.find(current => same(current, rule))?.enabled ?? preferenceDefault(rule), importance: input.value || null }); };
                    option.append(input, element('span', importanceNames[value])); choices.append(option); entry.choices.push(input);
                }
                details.append(choices); row.append(details);
            } else row.append(element('span', label));
            const control=element('button');control.className='notificationToggle';control.disabled=true;control.setAttribute('role','switch');control.setAttribute('aria-label',actionName);control.setAttribute('aria-checked','false');
            entry.button=control;controls.push(entry);control.onclick=()=>save({...rule,enabled:control.getAttribute('aria-checked')!=='true'});row.append(control);
            parent.append(row);
        }
        view.className = 'notificationCategorySettings';
        view.append(element('p', 'Choose which notifications you receive and which are Important. Silent and Low keep notices in your inbox without popups or Web Push. Critical alerts stay enabled and Important. Device sound and vibration remain in your browser or Android settings.'));
        for (const channel of ['in-app', 'discord']) preferenceRow(channel === 'in-app' ? 'Allow notifications' : 'Discord notifications', '*', '*', channel, 'non-critical ' + channel + ' notifications', view, { importance: channel === 'in-app' });
        const library = element('details'); library.append(element('summary', 'New episodes and movies'));
        for (const [kind, label] of [['episode', 'new episodes'], ['movie', 'new movies']]) for (const channel of ['in-app', 'discord'])
            preferenceRow(label[0].toUpperCase() + label.slice(1) + ' · ' + (channel === 'in-app' ? 'In-app' : 'Discord'), 'notifications', 'library.' + kind + '.added', channel, label + ' on ' + channel, library, { importance: channel === 'in-app' });
        const categories=element('details');categories.append(element('summary','Library categories'));
        categories.append(element('p', 'For future library additions you can access. All additions includes Watchlisted and Favorited media; turn All additions off to receive only your selected categories. Watchlisted uses the native watchlist flag also used by Enhanced, not your viewing history.'));
        for(const [category,label]of [['*','Library updates'],['all','All additions'],['requested','Requested media'],['watchlisted','Watchlisted media'],['favourite','Favorited media']])for(const channel of ['in-app','discord'])preferenceRow(label+' · '+(channel==='in-app'?'In-app':'Discord'),'notifications','category.library.'+category,channel,label+' on '+channel,categories,{importance:channel==='in-app'});
        library.append(categories);
        view.append(library);
        if(window.isSecureContext&&window.navigator?.serviceWorker&&window.PushManager&&window.Notification){
            const pushArea=element('section'),pushStatus=element('p','Loading browser notification settings…'),enablePush=element('button','Enable on this device'),disablePush=element('button','Disable on this device');
            const deviceSwitch=element('input');deviceSwitch.type='checkbox';deviceSwitch.className='emby-checkbox';deviceSwitch.setAttribute('is','emby-checkbox');deviceSwitch.setAttribute('role','switch');deviceSwitch.setAttribute('aria-label','Notifications on this device');
            const switchLabel=element('label');switchLabel.className='emby-checkbox-label';const switchText=element('span','Notifications on this device');switchText.className='checkboxLabel';const outline=element('span');outline.className='checkboxOutline';outline.setAttribute('aria-hidden','true');const icon=element('span');icon.className='material-icons checkboxIcon checkboxIcon-checked check';outline.append(icon);switchLabel.append(deviceSwitch,switchText,outline);
            deviceSwitch.disabled=true;enablePush.disabled=true;disablePush.disabled=true;pushArea.append(element('h3','Browser notifications'),switchLabel,pushStatus);view.append(pushArea);
            const worker=url('notifications/push/sw.js'),scope=new URL('./',worker).href;pushScope=scope;
            let pushConfig;
            const refreshPush=async()=>{pushConfig=await request('notifications/push');if(!active())return;deviceConfig=pushConfig;const registration=await window.navigator.serviceWorker.getRegistration(scope),subscription=registration?.scope===scope?await registration.pushManager.getSubscription():null;
                const hash=subscription?[...new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(subscription.endpoint)))].map(v=>v.toString(16).padStart(2,'0')).join(''):null;
                if(!active())return;const enrolled=!!hash&&pushConfig.devices?.includes(hash);
                enablePush.disabled=!pushConfig.enabled||enrolled||window.Notification.permission==='denied';disablePush.disabled=!subscription;
                deviceSwitch.disabled=!pushConfig.enabled;deviceSwitch.checked=window.Notification.permission!=='denied'&&deviceChoice()!=='off'&&(enrolled||deviceChoice()==='on'||pushConfig.defaultOn);
                pushStatus.textContent=!pushConfig.enabled?'An administrator needs to turn on Browser notifications in Notifications settings.':window.Notification.permission==='denied'?'Notifications are blocked in this browser. Allow notifications in the browser’s site settings, then turn this switch on again.':enrolled?'Enabled on this device. Notifications contain no private details.':deviceSwitch.checked?'Enabled by default. Open the notification bell and allow the browser permission request to finish setup.':'Off on this device. Turn on to allow browser notifications.';};
            enablePush.onclick=async()=>{enablePush.disabled=true;const token=client().accessToken();try{
                deviceChoice('on');const valid=()=>active()&&token===client().accessToken();
                if(!await enrollDevice(pushConfig,valid,true))return;
                if(active()){pushStatus.textContent='Enabled on this device. You can close this panel.';disablePush.disabled=false;enablePush.disabled=true;}
            }catch(error){if(active()){enablePush.disabled=!pushConfig?.enabled;pushStatus.textContent=window.Notification.permission==='denied'?'Notifications are blocked. Allow them in the browser’s site settings, then reopen this panel.':error.name==='NotAllowedError'?'Allow notifications for this site. On iPhone or iPad, add Jellyfin to the Home Screen and open it there first.':error.message||'Could not enable notifications. Check your connection and try again.';}}};
            disablePush.onclick=async()=>{deviceChoice('off');disablePush.disabled=true;try{const registration=await window.navigator.serviceWorker.getRegistration(scope);if(registration?.scope===scope){const subscription=await registration.pushManager.getSubscription();if(subscription){const hash=[...new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(subscription.endpoint)))].map(v=>v.toString(16).padStart(2,'0')).join('');await request('notifications/push/'+hash,'DELETE');await subscription.unsubscribe();}await registration.unregister();}if(active()){pushStatus.textContent='Disabled on this device.';enablePush.disabled=!pushConfig?.enabled;}}catch{if(active()){pushStatus.textContent='Could not disable. Try again.';disablePush.disabled=false;}}};
            deviceSwitch.onchange=async()=>{deviceSwitch.disabled=true;try{if(deviceSwitch.checked)await enablePush.onclick();else await disablePush.onclick();}finally{if(active()){deviceSwitch.checked=deviceChoice()!=='off'&&window.Notification.permission!=='denied';deviceSwitch.disabled=!pushConfig?.enabled;}}};
            void refreshPush().catch(()=>{if(active())pushStatus.textContent='Browser notifications are unavailable.';});
            refreshDevicePanel=()=>{if(active())void refreshPush().catch(()=>{});};
        }else{
            const message=element('section');message.append(element('h3','Browser notifications'),element('p',!window.isSecureContext?'Open Jellyfin using HTTPS to enable notifications on this device. Your current connection is not secure.':'This browser does not support notifications here. On iPhone or iPad, add Jellyfin to the Home Screen and open it there.'));view.append(message);
        }
        const muted = element('details'), mutedList = element('div'); muted.append(element('summary', 'Muted sources and types'), mutedList); view.append(muted);
        function renderMuted() {
            mutedList.replaceChildren();
            for (const rule of rules.filter(rule => !rule.enabled)) {
                const sourceName = { '*': 'All sources', 'account-hub': 'Accounts', 'deletion-queue': 'Removal requests', 'private-syncplay': 'Watch parties', 'discord-bridge': 'Discord Bridge', 'jellyfin-security': 'Security' }[rule.source] || rule.source;
                const typeName = { '*': 'All types', 'library.episode.added': 'New episodes', 'library.movie.added': 'New movies', 'category.library.watchlisted': 'Watchlisted media', 'category.library.favourite': 'Favorited media', 'category.library.requested': 'Requested media', 'category.library.all': 'All additions' }[rule.type] || rule.type;
                const row = element('div'); row.className = 'notificationPreference'; row.append(element('span', sourceName + ' · ' + typeName + ' · ' + (rule.channel === 'in-app' ? 'In-app' : 'Discord')));
                const enable = element('button', 'Enable'); enable.setAttribute('aria-label', 'Unmute ' + rule.source + ' · ' + rule.type + ' · ' + rule.channel);
                enable.onclick = async () => { enable.disabled = true; await save({ ...rule, enabled: true }); if (enable.isConnected) enable.disabled = false; }; row.append(enable); mutedList.append(row);
            }
            if (!mutedList.children.length) mutedList.append(element('p', 'No muted sources or types.'));
        }
        const preferencesLoaded=request('notifications/preferences').then(current => {
            if (!active()) return; rules = current.preferences; loading = false; status.textContent = ''; paint(); renderMuted();
        }).catch(() => { if (active()) status.textContent = 'Could not load preferences. Reopen settings to try again.'; });
        const categoryPanel=element('details');categoryPanel.append(element('summary','App notification categories'));view.append(categoryPanel);
        void request('notifications/categories').then(async data=>{
            await preferencesLoaded;
            if(!active())return; catalogs=data.catalogs||[];
            categoryPanel.append(element('p','These settings apply only to your account. Expand a category to choose Alerting, Important, Silent or Low, or inherit its plugin default. They do not change server routing or anyone else’s settings.'));
            const sourceNames={'account-hub':'Accounts','notifications':'Notifications','deletion-queue':'Removal requests','private-syncplay':'Watch parties','discord-bridge':'Discord Bridge','jellyfin-security':'Security'};
            for(const catalog of data.catalogs||[]){const plugin=element('details');plugin.className='notificationAppCategories';plugin.append(element('summary',sourceNames[catalog.source]||catalog.source));
                if(catalog.grouping){
                    const label=element('label','Group related notifications · '+catalog.grouping.name),select=element('select');select.className='emby-select';select.setAttribute('is','emby-select');select.setAttribute('aria-label',(sourceNames[catalog.source]||catalog.source)+' grouping');
                    for(const [value,text]of [['','Plugin default ('+(catalog.grouping.defaultEnabled?'grouped':'separate')+')'],['true','Group related notifications'],['false','Show separately']]){const option=element('option',text);option.value=value;select.append(option);}select.value=catalog.grouping.enabled==null?'':String(catalog.grouping.enabled);label.append(select);plugin.append(label);
                    select.onchange=async()=>{const previous=catalog.grouping.enabled;select.disabled=true;try{await request('notifications/grouping','PUT',{source:catalog.source,enabled:select.value===''?null:select.value==='true'});if(!active())return;catalog.grouping.enabled=select.value===''?null:select.value==='true';etag=undefined;}catch{if(active()){select.value=previous==null?'':String(previous);status.textContent='Could not save grouping. Your saved choice is unchanged.';}}finally{if(active())select.disabled=false;}};
                }
                for(const type of [...new Map([...(catalog.types||[]),{type:'*',name:'All plugin notifications'}].map(type=>[type.type,type])).values()]){
                    preferenceRow(type.name,catalog.source,type.type,'in-app',catalog.source+' '+type.name,plugin,{importance:true,description:type.description||''});
                }categoryPanel.append(plugin);
            }paint();
        }).catch(()=>{if(active())categoryPanel.append(element('p','App categories could not be loaded. Reopen settings to try again; your saved choices are unchanged.'));});
        // Do not hide or delete DeletionQueue's fallback bell. Expose its inbox through this surface
        // only when its adapter explicitly registers a reachable link and agrees to hand off mounting.
        const fallback = window.AccountHubLocalInbox;
        if (fallback && typeof fallback.href === 'string' && fallback.href.startsWith('#/')) {
            const link = element('a', 'DeletionQueue local notices'); link.href = fallback.href; panel.append(link);
        }
    }
    function mount() {
        if (forbidden() || !client()?.accessToken?.() || !client()?.getCurrentUserId?.()) { clear(true); return false; }
        const nextIdentity = url('notifications') + ':' + client().getCurrentUserId();
        if (identity !== nextIdentity) { clear(true); paused = false; identity = nextIdentity;
            if(window.isSecureContext&&window.navigator?.serviceWorker&&window.PushManager){
                const scope=new URL('./',url('notifications/push/sw.js')).href;pushScope=scope;const started=generation,token=client().accessToken();
                void request('notifications/push').then(async config=>{if(started!==generation||token!==client().accessToken())return;deviceConfig=config;const registration=await window.navigator.serviceWorker.getRegistration(scope);if(registration?.scope===scope){const subscription=await registration.pushManager.getSubscription();if(subscription){const hash=[...new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(subscription.endpoint)))].map(v=>v.toString(16).padStart(2,'0')).join('');if(started!==generation||token!==client().accessToken())return;if(!config.enabled||!config.devices?.includes(hash)){await subscription.unsubscribe();await registration.unregister();}}}applyDeviceDefault();}).catch(()=>{});
            }
        }
        const anchor = [...document.querySelectorAll('.headerUserButton, [aria-controls="app-user-menu"]')].find(node => node.getClientRects().length && !node.closest('.videoOsdBottom'));
        if (button?.isConnected) {
            if (anchor?.parentElement && button.parentElement !== anchor.parentElement) anchor.parentElement.insertBefore(button, anchor);
            return true;
        }
        if (!anchor?.parentElement || document.querySelector('[data-account-hub-bell]')) return false;
        button?.remove(); panel?.remove();
        button = element('button'); button.className = 'paper-icon-button-light headerButton'; button.dataset.accountHubBell = 'v1';
        button.dataset.notificationsBell = 'v1';
        button.setAttribute('aria-label', 'Notifications'); button.setAttribute('aria-expanded', 'false');
        button.setAttribute('aria-live', 'polite');
        button.setAttribute('aria-controls', 'account-hub-inbox');
        const announcement = element('span'); announcement.setAttribute('role', 'status'); announcement.setAttribute('aria-live', 'polite');
        button.append(announcement);
        panel = element('div'); panel.id = 'account-hub-inbox'; panel.hidden = true;
        panel.className = 'dialog paperList accountHubInbox';
        panel.setAttribute('role', 'dialog'); panel.setAttribute('aria-label', 'Notifications'); panel.tabIndex = -1;
        // Non-modal: no focus trap that could interfere with Jellyfin navigation.
        panel.addEventListener('keydown', e => { if (e.key === 'Escape') { e.preventDefault(); close(); } });
        button.onclick = () => {
            applyDeviceDefault(true);
            panel.hidden = !panel.hidden; settingsView = false;
            button.setAttribute('aria-expanded', String(!panel.hidden));
            if (!panel.hidden) {
                toast?.remove(); clearTimeout(toastTimer);
                const rect = button.getBoundingClientRect();
                panel.style.top = Math.max(8, Math.min(rect.bottom + 8, innerHeight - 100)) + 'px';
                panel.style.right = '.75rem';
                panel.style.width = innerWidth <= 600 ? 'calc(100vw - 1rem)' : 'min(26rem, calc(100vw - 2rem))';
                panel.style.right = innerWidth <= 600 ? '.5rem' : '.75rem';
                const available = innerHeight - Math.max(8, Math.min(rect.bottom + 8, innerHeight - 100)) - 8;
                panel.style.maxHeight = Math.max(100, Math.min(available, innerWidth <= 600 ? available : innerHeight / 2)) + 'px';
                skip = 0; loadMoreRequested = false; etag = undefined; render(); panel.focus(); void poll();
            }
        };
        anchor.parentElement.insertBefore(button, anchor); document.body.append(panel); paintCount(unreadCount); render();
        return true;
    }
    function paintCount(count) {
        if (!button) return;
        count += localRows.filter(row => !row.read).length;
        const icon = element('span', 'notifications'); icon.className = 'material-icons'; icon.setAttribute('aria-hidden', 'true');
        const badge = element('span', count > 99 ? '99+' : String(count)); badge.className = 'notificationBadge'; badge.hidden = !count;
        badge.setAttribute('aria-hidden', 'true'); button.replaceChildren(icon, badge); button.setAttribute('aria-label', count + ' unread notifications');
    }
    async function poll() {
        clearTimeout(timer);
        timer = null;
        if (document.hidden || !mount() || paused || inFlight) return;
        const activeIdentity = identity;
        const activeGeneration = generation;
        const append = loadMoreRequested; loadMoreRequested = false;
        inFlight = true;
        try {
            let response;
            if (!append && skip > 0) {
                // Revalidate every loaded page: do not retain admin/media rows
                // from an earlier page after the user's permissions change.
                const collected = [];
                for (let offset = 0; offset <= skip; offset += 100) {
                    const page = await request('notifications?skip=' + offset + '&limit=100'+(blockedView?'&blocked=true':historyView?'&history=true':'')); collected.push(...page.notifications);
                    response = { ...page, notifications: collected, etag: undefined };
                    if (!page.hasMore) { skip = offset; break; }
                }
            } else response = await request('notifications?skip=' + skip + '&limit=100'+(blockedView?'&blocked=true':historyView?'&history=true':''), 'GET', undefined, etag ? { 'If-None-Match': etag } : {});
            if (identity !== activeIdentity || generation !== activeGeneration || forbidden()) return;
            if (!response.unchanged) {
                if(Number.isInteger(response.blockedLimit))blockedLimit=Math.max(0,Math.min(1000,response.blockedLimit));if(Number.isInteger(response.retentionDays))retentionDays=response.retentionDays;
                rows = append ? [...new Map([...rows, ...response.notifications].map(row => [row.id, row])).values()] : response.notifications;
                hasMore = response.hasMore ?? response.notifications.length === 100; etag = append ? undefined : response.etag; render(); showToast();
            }
            const count = await request('notifications/unread-count');
            unreadCount = count.count; paintCount(unreadCount);
            delay = 30000;
            void connectLive();
        } catch { if (append) skip = Math.max(0, skip - 100); delay = Math.min(delay * 2, 300000); }
        finally {
            inFlight = false;
            if (moreObserver && moreSentinel && hasMore && !panel?.hidden && !settingsView) { moreObserver.disconnect(); moreObserver.observe(moreSentinel); }
            // Navigation can occur while a request is in flight; don't strand the new view.
            if (generation !== activeGeneration && !paused) timer = setTimeout(poll, 0);
            if(liveDirty&&generation===activeGeneration&&!paused){liveDirty=false;timer=setTimeout(poll,0);return;}
        }
        if (!paused && generation === activeGeneration) timer = setTimeout(poll, delay);
    }
    async function connectLive(){
        if(typeof window.WebSocket!=='function'||liveSocket||liveConnecting||liveTimer||paused||document.hidden||forbidden())return;
        const started=generation;liveConnecting=true;
        const retry=()=>{if(started!==generation||paused||forbidden())return;liveTimer=setTimeout(()=>{liveTimer=null;void connectLive();},liveBackoff);liveBackoff=Math.min(liveBackoff*2,300000);};
        try{
            const grant=await request('notifications/live-ticket','POST');if(started!==generation||paused)return;
            if(typeof grant.ticket!=='string'||!/^[A-F0-9]{64}$/.test(grant.ticket))throw Error('Invalid live grant');
            const address=new URL(url('notifications/live'),location.href);address.protocol=address.protocol==='https:'?'wss:':'ws:';
            const socket=new window.WebSocket(address.href,['notifications.v1','ticket.'+grant.ticket]);liveSocket=socket;
            socket.onopen=()=>{liveBackoff=1000;};
            socket.onmessage=event=>{if(started!==generation||paused||event.data!=='changed')return;etag=undefined;if(inFlight)liveDirty=true;else void poll();};
            socket.onclose=()=>{if(liveSocket===socket)liveSocket=null;retry();};
            socket.onerror=()=>socket.close();
        }catch{retry();}finally{if(started===generation)liveConnecting=false;}
    }
    const resume = () => { paused = false; poll(); refreshDevicePanel?.(); };
    // Settings refreshes can remove the focused control, returning focus to body.
    // Respect nested menus that consume Escape before closing the inbox.
    document.addEventListener('keydown', event => {
        if (event.key === 'Escape' && !event.defaultPrevented && panel && !panel.hidden) {
            event.preventDefault(); close();
        }
    });
    document.addEventListener('pointerdown', event => {
        for (const menu of panel?.querySelectorAll?.('.notificationOptions[open]') || []) if (!menu.contains(event.target)) menu.open = false;
        if (panel && !panel.hidden && !panel.contains(event.target) && !button?.contains(event.target)) close(false);
    }, true);
    window.addEventListener('focus', resume);
    window.addEventListener('suite-notification-dismiss', () => close(false));
    window.addEventListener('suite-removal-changed', () => { etag = undefined; void poll(); });
    window.addEventListener('suite-notification-message', event => {
        // Transient UI feedback only. Never accepted as a server publication,
        // never persisted, and never shown across account/auth boundaries.
        if (!mount() || paused) return;
        const data = event.detail, user = String(client().getCurrentUserId()).replaceAll('-', '').toLowerCase();
        if (!data || String(data.userId).replaceAll('-', '').toLowerCase() !== user || typeof data.body !== 'string') return;
        localRows.unshift({ id: 'ui-' + (++localSequence), local: true, read: false, event: { title: String(data.title || 'Update').slice(0, 200), body: data.body.slice(0, 4000), occurredAt: new Date().toISOString() } });
        localRows = localRows.slice(0, 50); render(); paintCount(unreadCount);
    });
    window.addEventListener('hashchange', () => { clear(); resume(); });
    document.addEventListener('visibilitychange', () => { if (document.hidden) clearTimeout(timer); else resume(); });
    // Coalesce DOM remounts without network traffic on every mutation.
    let scheduled = false;
    new MutationObserver(() => {
        if (scheduled) return; scheduled = true;
        requestAnimationFrame(() => { scheduled = false; if (mount() && !timer && !inFlight) poll(); });
    }).observe(document.documentElement, { subtree: true, childList: true });
    poll();
})();
