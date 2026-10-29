/* Native Jellyfin controls. The Hub supplies authentication and transient fresh-proof prompts. */
(() => {
    'use strict';
    if (window.AccountHubNotificationSettings) return;
    const make = (tag, text) => {
        const node = document.createElement(tag); if (text !== undefined) node.textContent = text;
        if (tag === 'button') { node.setAttribute('is', 'emby-button'); node.className = 'emby-button raised'; node.type = 'button'; }
        return node;
    };
    let sequence = 0;
    const pluginNames = { 'account-hub': 'Account Hub', 'notifications': 'Notifications', 'deletion-queue': 'Deletion Queue', 'private-syncplay': 'Private SyncPlay', 'discord-bridge': 'Discord Bridge', 'jellyfin-security': 'Jellyfin Security', 'automatic-deletion-requests': 'Automatic Deletion Requests' };
    const pluginName = source => Object.hasOwn(pluginNames, source) ? pluginNames[source] : source;
    function control(label, value, options, help) {
        const root = make('div'), caption = make('label', label), input = make(options ? 'select' : 'input');
        input.id = 'hub-notification-setting-' + (++sequence); caption.htmlFor = input.id; caption.className = 'inputLabel'; root.className = 'inputContainer';
        if (options) { input.setAttribute('is', 'emby-select'); input.className = 'emby-select'; for (const item of options) { const option = make('option', item); option.value = item; input.append(option); } input.value = value; }
        else if (typeof value === 'boolean') { input.type = 'checkbox'; input.setAttribute('is', 'emby-checkbox'); input.className = 'emby-checkbox'; input.checked = value; root.className = 'checkboxContainer'; }
        else { input.type = typeof value === 'number' ? 'number' : 'text'; input.setAttribute('is', 'emby-input'); input.className = 'emby-input'; input.value = value ?? ''; }
        input.autocomplete = 'off';
        if (help) root.dataset.help = help;
        if (input.type === 'checkbox') {
            caption.className = 'emby-checkbox-label'; caption.textContent = ''; const text = make('span', label); text.className = 'checkboxLabel'; const outline = make('span'); outline.className = 'checkboxOutline'; outline.setAttribute('aria-hidden', 'true'); const checked = make('span'), unchecked = make('span'); checked.className = 'material-icons checkboxIcon checkboxIcon-checked check'; unchecked.className = 'material-icons checkboxIcon checkboxIcon-unchecked'; outline.append(checked, unchecked); input.setAttribute('data-embycheckbox', 'true'); caption.append(input, text, outline); root.append(caption);
        } else {
            root.append(caption, input); input.labelElement = caption;
            if (options) { root.className = 'selectContainer'; caption.className = 'selectLabel'; input.classList.add('emby-select-withcolor'); const arrow = make('div'); arrow.className = 'selectArrowContainer'; arrow.setAttribute('aria-hidden', 'true'); const icon = make('span'); icon.className = 'selectArrow material-icons keyboard_arrow_down'; arrow.append(icon); root.append(arrow); }
            input.addEventListener('focus', () => caption.classList.add(options ? 'selectLabelFocused' : 'inputLabelFocused')); input.addEventListener('blur', () => caption.classList.remove(options ? 'selectLabelFocused' : 'inputLabelFocused'));
        }
        return { root, input, value: () => input.type === 'checkbox' ? input.checked : input.type === 'number' ? Number(input.value) : input.value };
    }
    window.AccountHubNotificationSettings = {
        async mount(root, request, { discordAvailable = true, signal } = {}) {
            root.classList.add('suiteCard');
            root.setAttribute('aria-busy', 'true');
            const editor = make('fieldset'); editor.dataset.notificationEditor = ''; editor.disabled = true;
            editor.style.cssText = 'border:0;padding:0;margin:0;min-width:0';
            let revision, skip = 0, disposed = false; const studios = []; const routes = [], list = make('div'), deliveries = make('div'), status = make('p'); status.setAttribute('role', 'status');
            let sourceChoices = ['*', 'account-hub', 'deletion-queue', 'private-syncplay', 'jellyfin-security', 'discord-bridge', 'notifications'];
            const retention = control('Keep notifications (days)', 90, null, 'Keep inbox history for 7–365 days.'); retention.input.min = 7; retention.input.max = 365;
            const blockedLimit=control('Blocked notifications retained per user',500,null,'0–1000 muted notifications, retained within the history period. Enabling a category can reveal retained notices without replaying external deliveries.');blockedLimit.input.min=0;blockedLimit.input.max=1000;
            const button = (text, work) => { const node = make('button', text); node.onclick = async () => { if (disposed) return; node.disabled = true; try { await work(); if (!disposed) status.textContent = 'Saved / refreshed.'; } catch (error) { if (!disposed) status.textContent = error.message || 'Request failed. Reload revision and check authorization or delivery health.'; } finally { node.disabled = false; } }; return node; };
            function addRoute(route = { id: crypto.randomUUID(), source: '*', type: '*', channel: 'discord', destination: '', adminOnly: true, directMessage: false, allowHttp: false, allowPrivate: false }) {
                if (routes.length >= 50) throw Error('At most 50 routes are supported.');
                const section = make('fieldset'), legend = make('legend', 'Delivery route'), grid = make('div'); grid.className = 'hubGrid';
                const fields = {
                    source: control('Source', route.source, [...new Set([...sourceChoices, route.source])], 'Choose a producer, or * for all producers. Registered plugins appear automatically.'),
                    type: control('Event type', route.type, null, 'Use an exact event type, or * for all events.'), channel: control('Channel', route.channel, ['discord', 'webhook'], 'Discord channels must be approved in Discord Bridge. Webhooks receive signed HTTP requests.'),
                    destination: control('Destination', route.destination, null, 'Discord channel ID or webhook URL. Leave [configured] unchanged to keep the saved destination.'), adminOnly: control('Administrator-only summary', route.adminOnly, null, 'Limit shared delivery to administrator summaries. Invite and reset credentials are never included.'),
                    directMessage: control('Send Discord DMs', route.directMessage, null, 'Send to opted-in users with verified Discord links.'), allowHttp: control('Allow HTTP webhook', route.allowHttp, null, 'Allow an unencrypted connection. Enable only for a trusted network.'),
                    allowPrivate: control('Allow private-network webhook', route.allowPrivate, null, 'Allow delivery to a trusted internal destination. Keep disabled for public webhooks.'), secret: control('Replace signing secret', '', null, 'At least 32 characters. Leave blank to keep the saved secret.')
                };
                fields.secret.input.type = 'password'; fields.secret.input.autocomplete = 'new-password'; fields.secret.input.maxLength = 1024;
                const dependency = make('p'); dependency.className = 'fieldDescription';
                function refreshAvailability() {
                    const unavailable = fields.channel.value() === 'discord' && !discordAvailable;
                    fields.channel.input.querySelector('option[value="discord"]').disabled = !discordAvailable;
                    for (const name of ['destination', 'directMessage']) fields[name].input.disabled = unavailable;
                    dependency.textContent = unavailable ? 'Discord delivery is unavailable: install and enable Discord Bridge, restart Jellyfin, and configure its bot. In-app and webhook delivery remain independent. Existing Discord route settings are preserved.' : '';
                }
                fields.channel.input.addEventListener('change', refreshAvailability); refreshAvailability();
                const entry = { route, fields, section }; routes.push(entry);
                for (const field of Object.values(fields)) grid.append(field.root);
                section.append(legend, dependency, grid, button('Remove route', async () => { fields.secret.input.value = ''; routes.splice(routes.indexOf(entry), 1); section.remove(); })); list.append(section);
            }
            const eraseSecrets = () => { for (const entry of routes) entry.fields.secret.input.value = ''; };
            async function loadSettings() {
                if (disposed) return;
                const data = await request('settings/notifications', 'GET'); if (disposed) return;
                eraseSecrets(); routes.length = 0; list.replaceChildren(); revision = data.revision; retention.input.value = data.notificationDays;
                blockedLimit.input.value=data.blockedNotificationLimit??500;
                for (const route of data.routes) addRoute(route);
            }
            async function loadDeliveries() {
                if (disposed) return;
                const data = await request('settings/notifications/deliveries?skip=' + skip + '&limit=25', 'GET'); if (disposed) return;
                deliveries.replaceChildren(make('h3', 'Recent deliveries'));
                if (!data.deliveries.length) deliveries.append(make('p', 'No deliveries yet.'));
                for (const delivery of data.deliveries) {
                    const row = make('p', delivery.id + ': ' + delivery.status + ', attempts ' + delivery.attempts + (delivery.error ? ', ' + delivery.error : ''));
                    if (delivery.status === 'dead_letter') row.append(button('Retry', async () => { await request('settings/notifications/deliveries/' + delivery.id + '/retry', 'POST', {}); await loadDeliveries(); }));
                    deliveries.append(row);
                }
            }
            const save = button('Save routing', async () => {
                if(!blockedLimit.input.reportValidity()||!retention.input.reportValidity())throw Error('Check notification retention limits.');
                const input = { revision, notificationDays: retention.value(),blockedNotificationLimit:blockedLimit.value(), routes: routes.map(({ route, fields }) => ({ id: route.id, ...Object.fromEntries(Object.entries(fields).map(([key, value]) => [key, key === 'secret' ? value.value() || null : value.value()])) })) };
                try { await request('settings/notifications', 'PUT', input); }
                finally { eraseSecrets(); for (const route of input.routes) route.secret = null; }
                await loadSettings();
            });
            const catalogPanel = make('details'); catalogPanel.className = 'notificationTypeCatalog'; catalogPanel.append(make('summary', 'Registered notification types'));
            const pushPanel=make('section');pushPanel.append(make('h3','Browser notifications'));
            const pushEnabled=control('Allow browser notifications',false,null,'Enable here, then open the bell → Settings → Enable on this device. Jellyfin handles keys and server address automatically. Devices need HTTPS (or localhost) and browser permission. Disabling removes enrolled devices.');
            const pushDefault=control('Enable on devices by default',false,null,'Users can opt out on each device. Opening the notification bell asks for browser permission when needed; blocked or dismissed permission never enables delivery.');
            let pushRevision=0,pushSaved=false,pushDefaultSaved=false;
            const pushState=make('p','Loading…');pushState.setAttribute('role','status');
            pushEnabled.input.disabled=pushDefault.input.disabled=true;
            const savePush=async()=>{if(disposed)return;pushEnabled.input.disabled=pushDefault.input.disabled=true;pushState.textContent='Saving…';try{await request('settings/notifications/push','PUT',{revision:pushRevision,enabled:pushEnabled.value(),defaultOn:pushDefault.value()});await loadPush();}catch{if(!disposed){pushEnabled.input.checked=pushSaved;pushDefault.input.checked=pushDefaultSaved;pushState.textContent='Could not save. Refresh this page and try again.';}}finally{if(!disposed)pushEnabled.input.disabled=pushDefault.input.disabled=false;}};
            pushEnabled.input.addEventListener('change',savePush);pushDefault.input.addEventListener('change',savePush);
            pushPanel.append(pushEnabled.root,pushDefault.root,pushState);
            async function loadPush(){if(disposed)return;const value=await request('settings/notifications/push','GET');if(disposed)return;pushRevision=value.revision;pushSaved=value.enabled;pushDefaultSaved=!!value.defaultOn;pushDefault.input.checked=pushDefaultSaved;pushEnabled.input.checked=value.enabled;pushEnabled.input.disabled=pushDefault.input.disabled=false;pushState.textContent=value.enabled?(pushDefaultSaved?'Ready. Devices use notifications by default after browser permission, unless the user opts out.':'Ready. Users can turn notifications on in the bell’s settings.'):'Off. Turn on to let users enable notifications on their devices.';}
            const catalogList = make('div'); catalogPanel.append(catalogList);
            const libraryPanel = make('section'); libraryPanel.append(make('h3', 'Library events'));
            const libraryEnabled = control('Notify future library additions', false, null, 'Notifications owns the only native library observer. No existing-library backfill. Each recipient must still have native access.'), libraryEpisodes = control('New episodes', true), libraryMovies = control('New movies', true), libraryFolders = control('Libraries (no selection means all accessible libraries)', '', [], 'Choose the native libraries whose future events may enter the private inbox.');
            libraryFolders.input.multiple = true; libraryFolders.input.size = 5;
            let libraryRevision = 0;
            const libraryHealth = make('p'); libraryHealth.setAttribute('role', 'status');
            async function loadLibrary() { if(disposed)return; const data = await request('settings/notifications/library', 'GET'); if (disposed) return; libraryRevision = data.settings.revision; libraryEnabled.input.checked = data.settings.enabled; libraryEpisodes.input.checked = data.settings.episodes; libraryMovies.input.checked = data.settings.movies;
                const selected = new Set(data.settings.libraryIds || []); libraryFolders.input.replaceChildren(); for (const folder of data.libraries || []) { const option = make('option', folder.name); option.value = folder.id; option.selected = selected.delete(folder.id); libraryFolders.input.append(option); } for (const id of selected) { const option = make('option', 'Unavailable saved library · ' + id); option.value = id; option.selected = true; libraryFolders.input.append(option); }
                libraryHealth.textContent = 'Retained: ' + data.health.retained + ' · Pending: ' + data.health.pending + ' · Uncertain Discord deliveries: ' + data.health.uncertainDiscord;
            }
            libraryPanel.append(libraryEnabled.root, libraryEpisodes.root, libraryMovies.root, libraryFolders.root, button('Save library events', async () => { await request('settings/notifications/library', 'PUT', { revision: libraryRevision, enabled: libraryEnabled.value(), episodes: libraryEpisodes.value(), movies: libraryMovies.value(), libraryIds: [...libraryFolders.input.selectedOptions].map(option => option.value) }); await loadLibrary(); }), libraryHealth, make('p', 'Configure examples, priority, defaults and Notification Studio below. Shared Discord channel title disclosure remains an explicit Discord Bridge setting; no personal audiences are shared.'));
            let typesLoading, typesLoaded = false;
            function loadTypes(reload = false) {
                if (disposed || typesLoaded && !reload) return Promise.resolve();
                return typesLoading ||= readTypes().finally(() => { typesLoading = null; });
            }
            async function readTypes() {
                const data = await request('settings/notifications/types', 'GET'); if (disposed) return;
                sourceChoices = [...new Set([...sourceChoices, ...(data.catalogs || []).map(catalog => catalog.source)])];
                for (const route of routes) for (const source of sourceChoices) if (![...route.fields.source.input.options].some(option => option.value === source)) { const option = make('option', source); option.value = source; route.fields.source.input.append(option); }
                for(const close of studios)close();studios.length=0;catalogList.replaceChildren();
                for (const catalog of [...(data.catalogs || [])].sort((a,b) => pluginName(a.source).localeCompare(pluginName(b.source)) || a.source.localeCompare(b.source))) {
                    const plugin = make('details'), pluginSummary = make('summary'); plugin.className = 'notificationPluginCatalog'; plugin.dataset.notificationSource = catalog.source;
                    pluginSummary.append(make('span', pluginName(catalog.source)), make('code', catalog.source), make('span', catalog.types.length + (catalog.types.length === 1 ? ' type' : ' types')));
                    plugin.append(pluginSummary); catalogList.append(plugin);
                    if(catalog.grouping){
                        const grouping=control('Group related notifications by default',catalog.grouping.defaultEnabled,null,'Grouping key supplied by this plugin: '+catalog.grouping.name+'. Each user can override this default.');
                        const panel=make('div');panel.className='notificationTypeDefaults';panel.append(grouping.root,button('Save grouping default',async()=>{await request('settings/notifications/grouping','PUT',{source:catalog.source,revision:catalog.revision,enabled:grouping.value()});catalog.revision++;}));plugin.append(panel);
                    }
                    if (!catalog.types.length) plugin.append(make('p', 'No notification types reported by this plugin.'));
                    for (const type of [...catalog.types].sort((a,b) => a.name.localeCompare(b.name) || a.type.localeCompare(b.type))) {
                    const entry = make('details'), typeSummary = make('summary'); entry.className = 'notificationRegisteredType'; entry.dataset.notificationType = type.type;
                    typeSummary.append(make('span', type.name), make('code', type.type)); entry.append(typeSummary); plugin.append(entry);
                    const section = make('fieldset'); section.className='notificationTypeSection';section.append(make('legend', type.name)); section.dataset.help = type.trigger + '. Recipients: ' + type.recipients + '. Source: ' + catalog.source + '. Registered actions: ' + (type.actions.map(action => action.label).join(', ') || 'none') + '.';
                    const defaultValues = type.defaults || { inApp: true, discord: false, importance: type.recipients === 'administrators' ? 'high' : 'normal' };
                    const inAppDefault = control('In-app enabled by default', defaultValues.inApp), discordDefault = control('Discord DM enabled by default', defaultValues.discord), importanceDefault = control('Default priority', defaultValues.importance, ['silent', 'low', 'normal', 'high'], 'Personal choices take precedence. Critical security notices remain mandatory and important.');
                    const defaultsPanel=make('div');defaultsPanel.className='notificationTypeDefaults';
                    defaultsPanel.append(inAppDefault.root, discordDefault.root, importanceDefault.root, button('Save defaults', async () => { await request('settings/notifications/types/defaults', 'PUT', { source: catalog.source, type: type.type, revision: catalog.revision, defaults: { inApp: inAppDefault.value(), discord: discordDefault.value(), importance: importanceDefault.value() } }); catalog.revision++; }));section.append(defaultsPanel);
                    const simulated = { source: catalog.source, type: type.type, title: type.example?.title || type.name, body: type.example?.body || type.trigger, severity: type.example?.severity || 'info', exampleImage: !!type.example?.image };
                    const preview = make('div'); preview.setAttribute('aria-label', 'Simulated ' + type.name); preview.className = 'notificationTypePreview';
                    let canvas = type.layout.canvas || null;
                    const drawPreview = () => { if (window.NotificationCard) window.NotificationCard.render(preview, simulated, { canvas: canvas || undefined, simulated: true }); else preview.textContent = simulated.title + ' — ' + simulated.body; };
                    drawPreview(); section.append(make('h4', 'Example notification — not sent'), preview);
                    const studio = make('details'), studioRoot = make('div'); studio.append(make('summary', 'Notification Studio'), studioRoot); section.append(studio);
                    let studioCleanup;
                    studio.addEventListener('toggle', () => { if (disposed || !studio.open || studioCleanup) return; if (!window.NotificationStudio) { studioRoot.textContent = 'Open Notifications in Dashboard → Plugins to use the full studio.'; return; } studioCleanup = window.NotificationStudio.mount(studioRoot, simulated, canvas, value => { canvas = value; drawPreview(); }); studios.push(() => { studioCleanup?.(); studioCleanup = null; }); });
                    const density = control('Layout', type.layout.density, ['compact', 'standard']);
                    const length = control('Summary length', type.layout.summaryLength, null, '60–300 characters. Full details remain available when expansion is enabled.'); length.input.min = 60; length.input.max = 300;
                    const expand = control('Expandable details', type.layout.expandable);
                    const message = control('Message template', type.layout.messageTemplate || '', null, 'Optional plain text. Use {media} for the title and {days} for a removal delay. Blank keeps the plugin message.'); message.input.maxLength = 400;
                    const toast = control('Toast seconds', type.layout.toastSeconds || 0, null, '0 disables short popups. Up to 10 seconds; the notification stays in the inbox.'); toast.input.min = 0; toast.input.max = 10;
                    const expiry=control('Archive after viewing (hours)',type.layout.expireAfterViewHours||0,null,'0 keeps notifications until dismissed. Applies to new publications; archived entries remain in History.');expiry.input.min=0;expiry.input.max=8760;
                    const behavior=make('details');behavior.className='notificationTypeBehavior';behavior.append(make('summary','Message & delivery behavior'));
                    const presentation=make('div');presentation.className='notificationTypePresentation';presentation.append(density.root,length.root,expand.root,message.root,toast.root,expiry.root);behavior.append(presentation);section.append(behavior, button('Save layout', async () => {
                        if (!length.input.reportValidity() || !toast.input.reportValidity()||!expiry.input.reportValidity()) throw Error('Check layout durations and summary length.');
                        if(studioCleanup?.validate?.()===false)throw Error('Check the studio fields before saving the layout.');
                        const savedCanvas=studioCleanup?.getCanvas?.()||canvas;
                        await request('settings/notifications/types/layout', 'PUT', { source: catalog.source, type: type.type, revision: catalog.revision, layout: { density: density.value(), summaryLength: length.value(), expandable: expand.value(), messageTemplate: message.value() || null, toastSeconds: toast.value(),expireAfterViewHours:expiry.value()||null, canvas:savedCanvas } }); catalog.revision++;studioCleanup?.markSaved?.(savedCanvas||window.NotificationCard.defaults());
                    })); entry.append(section);
                    }
                }
                if (!catalogList.children.length) catalogList.append(make('p', 'No plugin notification types registered yet.'));
                typesLoaded = true;
            }
            catalogPanel.addEventListener('toggle', () => { if (catalogPanel.open && !catalogList.children.length) void loadTypes().catch(() => { if (!disposed) catalogList.textContent = 'Could not load notification types. Reload to try again.'; }); });
            editor.replaceChildren(make('h2', 'Delivery routes'), retention.root,blockedLimit.root, list,
                button('Add route', async () => addRoute()), save, button('Reload', async () => { await loadSettings(); await loadDeliveries(); if (catalogPanel.open) await loadTypes(true); }),
                 button('Send sanitized test', async () => { await request('settings/notifications/test', 'POST', {}); await loadDeliveries(); }), status, libraryPanel, catalogPanel, deliveries,
                 button('Previous deliveries', async () => { skip = Math.max(0, skip - 25); await loadDeliveries(); }), button('Next deliveries', async () => { skip += 25; await loadDeliveries(); }),pushPanel);
            root.replaceChildren(editor);
            const cleanup = () => { if (disposed) return; disposed = true; signal?.removeEventListener('abort', cleanup); for (const close of studios) close(); studios.length = 0; eraseSecrets(); root.replaceChildren(); root.removeAttribute('aria-busy'); };
            signal?.addEventListener('abort', cleanup, { once: true });
            if (signal?.aborted) cleanup();
            try { await loadSettings(); await loadDeliveries(); } catch (error) { if (!disposed) status.textContent = error.message; }
            try { await loadTypes(); } catch { /* Older compatibility hosts need not expose a registry. */ }
            try{await loadPush();}catch{pushPanel.hidden=true;}
            try{await loadLibrary();}catch{libraryPanel.hidden=true;}
            if (!disposed) { editor.disabled = false; root.setAttribute('aria-busy', 'false'); }
            return cleanup;
        }
    };
})();
