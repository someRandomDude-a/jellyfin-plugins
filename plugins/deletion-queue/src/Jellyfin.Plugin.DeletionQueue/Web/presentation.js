(() => {
    'use strict';
    const page = document.getElementById('deletionQueueAdmin'), section = page?.querySelector('#dqPresentation'); if (!section || section.dataset.bound) return; section.dataset.bound = 'true';
    const host = section.querySelector('[data-presentation-editor]'), status = section.querySelector('[data-presentation-status]');
    let active = true, epoch = 0, settings, controllers = new Set(), urls = new Set(), studios = [];
    const get = (o, key) => o?.[key] ?? o?.[key[0].toUpperCase() + key.slice(1)];
    const node = (tag, text) => { const n = document.createElement(tag); if (text !== undefined) n.textContent = text; return n; };
    async function api(suffix = '', method = 'GET', body, blob = false) {
        const generation = epoch, controller = new AbortController(); controllers.add(controller);
        try {
            const response = await fetch(ApiClient.getUrl('DeletionQueue/admin/presentation' + suffix), { method, cache: 'no-store', credentials: 'omit', signal: controller.signal,
                headers: { Authorization: 'MediaBrowser Token="' + encodeURIComponent(ApiClient.accessToken()) + '"', ...(body ? { 'Content-Type': 'application/json' } : {}) }, body: body ? JSON.stringify(body) : undefined });
            if (!active || generation !== epoch) throw new DOMException('View closed', 'AbortError');
            if (!response.ok) throw new Error('Operation unavailable. Refresh settings and review your administrator session, revision and artwork diagnostics.');
            return response.status === 204 ? null : blob ? await response.blob() : await response.json();
        } finally { controllers.delete(controller); }
    }
    function field(parent, text, type, value, options) {
        const kind = options ? 'emby-select' : type === 'checkbox' ? 'emby-checkbox' : 'emby-input';
        // Parse the complete native control tree before upgrade: Jellyfin's
        // checkbox constructor needs its label/parent to exist already.
        const row = node('div'); row.className = options ? 'selectContainer' : type === 'checkbox' ? 'checkboxContainer' : 'inputContainer';
        row.innerHTML = type === 'checkbox' ? '<label><input is="emby-checkbox" type="checkbox"><span data-caption></span></label>' : options ? '<label></label><select is="emby-select"></select>' : '<label></label><input is="emby-input">';
        const label = row.querySelector('label'), input = row.querySelector('input,select');
        input.id = 'dq-art-' + crypto.randomUUID(); label.htmlFor = input.id; input.setAttribute('is', kind);
        if (options) for (const [id, title] of options) { const option = node('option', title); option.value = id; input.append(option); }
        else input.type = type;
        if (type === 'checkbox') input.checked = !!value; else input.value = value ?? '';
        if (type === 'checkbox') row.querySelector('[data-caption]').textContent = text; else label.textContent = text;
        parent.append(row); return input;
    }
    function action(parent, text, run) {
        const holder = node('div'); holder.innerHTML = '<button is="emby-button" type="button" class="raised"></button>';
        const b = holder.firstElementChild; b.textContent = text;
        b.onclick = async () => { b.disabled = true; try { await run(); } catch (e) { if (active && e.name !== 'AbortError') status.textContent = e.message; } finally { b.disabled = false; } }; parent.append(b); return b;
    }
    function templateEditor(parent, title, value, portrait) {
        const box = node('fieldset'); box.className = 'dq-art-template'; box.append(node('legend', title)); parent.append(box);
        const studio = window.DeletionQueueOverlayEditor.mount(box, value, portrait); studios.push(studio);
        const sample = field(box, 'Sample earliest date (preview only)', 'date', new Date(Date.now() + 172800000).toISOString().slice(0, 10));
        const help = node('p', 'Only verified earliest dates are shown on real media. Blocked requests say pending. This field changes the sample only; it never changes deletion eligibility.'); help.id = sample.id + '-help'; help.className = 'dq-art-date-help'; help.hidden = true; sample.setAttribute('aria-describedby', help.id); sample.parentElement.append(help);
        const show = () => { help.hidden = false; }, hide = () => { if (document.activeElement !== sample) help.hidden = true; }; sample.addEventListener('focus', show); sample.addEventListener('blur', hide); sample.parentElement.addEventListener('mouseenter', show); sample.parentElement.addEventListener('mouseleave', hide);
        studio.setSample(sample.value); sample.addEventListener('input', () => studio.setSample(sample.value || 'YYYY-MM-DD'));
        const proof = node('details'); proof.className = 'dq-studio-proof'; proof.append(node('summary', 'Server PNG preview')); box.append(proof);
        const image = node('img'); image.alt = title + ' sample artwork preview'; image.hidden = true; proof.append(image);
        const read = studio.read;
        action(box, 'Preview ' + title.toLowerCase(), async () => {
            if (!sample.reportValidity() || !box.checkValidity()) return;
            const generation = epoch, data = await api('/preview', 'POST', { template: read(), portrait, sampleDate: sample.value }, true); if (!active || generation !== epoch || !image.isConnected) return;
            const url = URL.createObjectURL(data); urls.add(url); const old = image.src; image.src = url; image.hidden = false; proof.open = true;
            if (urls.has(old)) { URL.revokeObjectURL(old); urls.delete(old); }
        }); return read;
    }
    async function load() {
        const generation = epoch, data = await api(); if (!active || generation !== epoch) return;
        settings = get(data, 'settings'); studios.forEach(studio => studio.dispose()); studios = []; urls.forEach(URL.revokeObjectURL); urls.clear(); host.replaceChildren();
        const form = node('form'); form.onsubmit = e => e.preventDefault(); host.append(form);
        const organization = node('section'); organization.className = 'dq-art-organization'; organization.append(node('h3', '01 · Organize the review collection')); form.append(organization);
        const collection = field(organization, 'Maintain a pending-removal collection', 'checkbox', get(settings, 'collectionEnabled'));
        const options = [['', 'Create/use the plugin-managed collection'], ...(get(data, 'collections') || []).map(item => [get(item, 'id'), get(item, 'name')])];
        const selectedId = get(settings, 'collectionId');
        if (selectedId && !options.some(([id]) => id === selectedId)) options.push([selectedId, 'Saved collection (not in this page of results)']);
        const selection = node('div'); selection.className = 'dq-art-selection-grid'; organization.append(selection);
        const selected = field(selection, 'Jellyfin collection', '', selectedId || '', options);
        const name = field(selection, 'New managed collection name', 'text', get(settings, 'collectionName')); name.maxLength = 100;
        organization.append(node('p', 'Only plugin-added memberships are removed later. Existing collections and their original members stay yours.'));
        const styling = node('section'); styling.className = 'dq-art-organization'; styling.append(node('h3', 'Overlay design')); form.append(styling);
        const overlay = field(styling, 'Generate reversible artwork overlays', 'checkbox', get(settings, 'overlaysEnabled'));
        const templates = node('div'); templates.className = 'dq-overlay-templates'; form.append(templates);
        const portrait = templateEditor(templates, 'Portrait', get(settings, 'portrait'), true), landscape = templateEditor(templates, 'Landscape', get(settings, 'landscape'), false);
        const footer = node('div'); footer.className = 'dq-art-footer'; form.append(footer);
        action(footer, 'Save collection and overlay settings', async () => {
            if (!form.reportValidity()) return;
            const enabled = collection.checked || overlay.checked;
            if (enabled && !confirm('Apply collections/artwork to all pending manual and automatic requests? Originals are retained. Restoration will not overwrite administrator artwork changes.')) return;
            await api('', 'PUT', { confirmChanges: enabled, settings: { revision: get(settings, 'revision'), collectionEnabled: collection.checked, collectionId: selected.value || null, collectionName: name.value, overlaysEnabled: overlay.checked, portrait: portrait(), landscape: landscape() } });
            await load();
        });
        action(footer, 'Sync / restore now', async () => { await api('/sync', 'POST', {}); await load(); });
        status.textContent = String(get(data, 'status')) + ' · Artwork: ' + JSON.stringify(get(data, 'artwork')) + (get(data, 'collectionsTruncated') ? ' · Collection list truncated; saved IDs are retained.' : '');
    }
    page.addEventListener('viewhide', () => { active = false; epoch++; controllers.forEach(c => c.abort()); studios.forEach(studio => studio.dispose()); studios = []; urls.forEach(URL.revokeObjectURL); urls.clear(); host.replaceChildren(); settings = null; });
    const failed = e => { if (active && e.name !== 'AbortError') status.textContent = e.message; };
    page.addEventListener('viewshow', () => { if (!active) { active = true; load().catch(failed); } });
    load().catch(failed);
})();
