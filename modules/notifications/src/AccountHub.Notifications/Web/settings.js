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
    function control(label, value, options) {
        const root = make('div'), caption = make('label', label), input = make(options ? 'select' : 'input');
        input.id = 'hub-notification-setting-' + (++sequence); caption.htmlFor = input.id; caption.className = 'inputLabel'; root.className = 'inputContainer';
        if (options) { input.setAttribute('is', 'emby-select'); input.className = 'emby-select'; for (const item of options) { const option = make('option', item); option.value = item; input.append(option); } input.value = value; }
        else if (typeof value === 'boolean') { input.type = 'checkbox'; input.setAttribute('is', 'emby-checkbox'); input.className = 'emby-checkbox'; input.checked = value; root.className = 'checkboxContainer'; }
        else { input.type = typeof value === 'number' ? 'number' : 'text'; input.setAttribute('is', 'emby-input'); input.className = 'emby-input'; input.value = value ?? ''; }
        input.autocomplete = 'off';
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
        async mount(root, request, { discordAvailable = true } = {}) {
            let revision, skip = 0, disposed = false; const routes = [], list = make('div'), deliveries = make('div'), status = make('p'); status.setAttribute('role', 'status');
            const retention = control('Notification retention (7–365 days)', 90); retention.input.min = 7; retention.input.max = 365;
            const button = (text, work) => { const node = make('button', text); node.onclick = async () => { node.disabled = true; try { await work(); if (!disposed) status.textContent = 'Saved / refreshed.'; } catch (error) { if (!disposed) status.textContent = error.message || 'Request failed. Reload revision and check authorization or delivery health.'; } finally { node.disabled = false; } }; return node; };
            function addRoute(route = { id: crypto.randomUUID(), source: '*', type: '*', channel: 'discord', destination: '', adminOnly: true, directMessage: false, allowHttp: false, allowPrivate: false }) {
                if (routes.length >= 50) throw Error('At most 50 routes are supported.');
                const section = make('fieldset'), legend = make('legend', 'Delivery route'), grid = make('div'); grid.className = 'hubGrid';
                const fields = {
                    source: control('Source', route.source, ['*', 'account-hub', 'deletion-queue', 'private-syncplay', 'jellyfin-security']),
                    type: control('Exact event type, or * for all', route.type), channel: control('Channel', route.channel, ['discord', 'webhook']),
                    destination: control('Destination (leave [configured] to preserve)', route.destination), adminOnly: control('Administrator-only summary', route.adminOnly),
                    directMessage: control('Discord DM to each opted-in linked user', route.directMessage), allowHttp: control('Explicitly allow HTTP webhook', route.allowHttp),
                    allowPrivate: control('Explicitly allow private-network webhook', route.allowPrivate), secret: control('Replace webhook signing secret (blank preserves)', '')
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
                const data = await request('settings/notifications', 'GET'); if (disposed) return;
                eraseSecrets(); routes.length = 0; list.replaceChildren(); revision = data.revision; retention.input.value = data.notificationDays;
                for (const route of data.routes) addRoute(route);
            }
            async function loadDeliveries() {
                const data = await request('settings/notifications/deliveries?skip=' + skip + '&limit=25', 'GET'); if (disposed) return;
                deliveries.replaceChildren(make('h3', 'Redacted delivery health'));
                for (const delivery of data.deliveries) {
                    const row = make('p', delivery.id + ': ' + delivery.status + ', attempts ' + delivery.attempts + (delivery.error ? ', ' + delivery.error : ''));
                    if (delivery.status === 'dead_letter') row.append(button('Retry', async () => { await request('settings/notifications/deliveries/' + delivery.id + '/retry', 'POST', {}); await loadDeliveries(); }));
                    deliveries.append(row);
                }
            }
            const save = button('Save routing', async () => {
                const input = { revision, notificationDays: retention.value(), routes: routes.map(({ route, fields }) => ({ id: route.id, ...Object.fromEntries(Object.entries(fields).map(([key, value]) => [key, key === 'secret' ? value.value() || null : value.value()])) })) };
                try { await request('settings/notifications', 'PUT', input); }
                finally { eraseSecrets(); for (const route of input.routes) route.secret = null; }
                await loadSettings();
            });
            root.replaceChildren(make('h2', 'Notification routing'), make('p', 'Generic delivery never contains invite/reset credentials. Shared routes are administrator summaries. Discord channels must also be approved in Discord Bridge. Webhook secrets require at least 32 characters. DM delivery uses verified links and user preferences.'), retention.root, list,
                button('Add route', async () => addRoute()), save, button('Reload', async () => { await loadSettings(); await loadDeliveries(); }),
                button('Send sanitized test', async () => { await request('settings/notifications/test', 'POST', {}); await loadDeliveries(); }), status, deliveries,
                button('Previous deliveries', async () => { skip = Math.max(0, skip - 25); await loadDeliveries(); }), button('Next deliveries', async () => { skip += 25; await loadDeliveries(); }));
            try { await loadSettings(); await loadDeliveries(); } catch (error) { if (!disposed) status.textContent = error.message; }
            return () => { disposed = true; eraseSecrets(); root.replaceChildren(); };
        }
    };
})();
