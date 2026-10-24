/* Native workspace integration: window.AccountHubInvites.mount(container, { read, write, refresh }). */
(function () {
    'use strict';
    function element(tag, text, className) {
        const node = document.createElement(tag);
        if (text) node.textContent = text;
        if (className) node.className = className;
        return node;
    }
    window.AccountHubInvites = {
        async mount(root, api) {
            let disposed = false, epoch = 0, skip = 0, groupSkip = 0;
            const alive = () => !disposed && !api.signal?.aborted;
            root.replaceChildren();
            const heading = element('h2', 'Account invitations'); root.append(heading);
            const status = element('p'); status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite'); root.append(status);
            const form = element('form');
            function field(label, type, value, min, max) {
                const wrap = element('div', '', 'inputContainer'); const id = 'hub-invite-' + type + '-' + Math.random().toString(36).slice(2);
                const caption = element('label', label, 'inputLabel'); caption.htmlFor = id;
                const input = element('input', '', 'emby-input'); input.id = id; input.type = type; input.value = value;
                if (min !== undefined) input.min = min; if (max !== undefined) input.max = max;
                wrap.append(caption, input); form.append(wrap); return input;
            }
            const minutes = field('Expires after (minutes, 15–43200)', 'number', '10080', 15, 43200);
            const uses = field('Maximum uses (1–100)', 'number', '1', 1, 100);
            const note = field('Administrator note (never shown publicly)', 'text', ''); note.maxLength = 500;
            const discord = field('Optional Discord recipient ID (requires connected Discord Bridge)', 'text', ''); discord.maxLength = 20;
            discord.disabled = api.discordAvailable !== true;
            const remediation = 'Discord delivery is unavailable. Install and enable a compatible Discord Bridge, connect its bot, then refresh. Copy-link invitations remain available.';
            discord.title = discord.disabled ? remediation : '';
            if (discord.disabled) form.append(element('p', remediation));
            const group = element('select', '', 'emby-select'); const groupLabel = element('label', 'Access template', 'inputLabel');
            group.id = 'hub-invite-group-' + crypto.randomUUID(); groupLabel.htmlFor = group.id; groupLabel.className = 'selectLabel'; group.setAttribute('is', 'emby-select'); group.classList.add('emby-select-withcolor'); group.append(new Option('Native defaults', ''));
            const groupWrap = element('div', '', 'selectContainer'); groupWrap.append(groupLabel, group); form.append(groupWrap);
            const arrow = element('div', '', 'selectArrowContainer'); arrow.setAttribute('aria-hidden', 'true'); arrow.append(element('span', '', 'selectArrow material-icons keyboard_arrow_down')); groupWrap.append(arrow);
            group.addEventListener('focus', () => groupLabel.classList.add('selectLabelFocused')); group.addEventListener('blur', () => groupLabel.classList.remove('selectLabelFocused'));
            const control = (label, fn) => { const b = element('button', label, 'emby-button raised'); b.type = 'button'; b.setAttribute('is', 'emby-button'); b.onclick = async () => { if (!alive()) return; b.disabled = true; try { await fn(); } catch (e) { if (alive()) status.textContent = e.message; } finally { if (alive()) b.disabled = false; } }; return b; };
            const groupPages = element('div'); form.append(groupPages);
            let groupEpoch = 0;
            async function loadGroups() {
                const view = ++groupEpoch, start = groupSkip, selected = group.value, label = group.selectedOptions[0]?.textContent;
                const groups = await api.read('group-policy/groups?skip=' + start + '&limit=100'); if (!alive() || view !== groupEpoch) return;
                group.replaceChildren(new Option('Native defaults', ''));
                if (selected && !(groups.items || []).some(g => g.id === selected)) group.append(new Option(label, selected));
                for (const item of groups.items || []) group.append(new Option(item.name, item.id)); group.value = selected;
                const previous = control('Previous templates', async () => { groupSkip = Math.max(0, start - 100); await loadGroups(); }), next = control('Next templates', async () => { groupSkip = start + 100; await loadGroups(); });
                previous.disabled = start === 0; next.disabled = start + 100 >= groups.total; groupPages.replaceChildren(previous, next);
            }
            const submit = element('button', 'Request invitation', 'raised button-submit emby-button'); submit.type = 'submit'; form.append(submit); root.append(form);
            const issuance = element('div'); root.append(issuance);
            const list = element('div'); root.append(list);
            const eraseLink = () => { for (const input of issuance.querySelectorAll('input')) input.value = ''; issuance.replaceChildren(); };
            const dispose = () => { disposed = true; eraseLink(); discord.value = note.value = ''; root.replaceChildren(); };
            api.signal?.addEventListener('abort', dispose, { once: true });
            async function refresh() {
                const view = ++epoch, start = skip;
                const response = await api.read('invites?skip=' + start + '&limit=100'); if (!alive() || view !== epoch) return;
                list.replaceChildren();
                for (const item of response.items || []) {
                    const section = element('section'); section.append(element('h3', item.note || 'Invitation'));
                    section.append(element('p', item.status + ' · ' + item.completedUses + ' completed / ' + item.reservedUses + ' reserved / ' + item.maxUses + ' maximum · Expires ' + new Date(item.expiresAt).toLocaleString()));
                    for (const action of item.status === 'pending_approval' ? ['approve', 'revoke', 'expire'] : item.status === 'active' ? ['resend', 'revoke', 'expire'] : []) {
                        const button = element('button', action === 'resend' ? 'Rotate link (invalidates old link)' : action[0].toUpperCase() + action.slice(1), 'emby-button raised');
                        button.type = 'button'; button.addEventListener('click', async () => {
                            if (!alive() || view !== epoch) return;
                            button.disabled = true;
                            try {
                                eraseLink();
                                const result = await api.write('invites/' + item.id + '/' + action, {}, action === 'revoke' || action === 'expire' ? 'Destructive' : 'Configuration');
                                if (!alive() || view !== epoch) return;
                                if (result.oneTimeLink) {
                                    const label = element('label', 'One-time link — copy now. It cannot be read back.', 'inputLabel');
                                    const input = element('input', '', 'emby-input'); input.readOnly = true; input.value = new URL(result.oneTimeLink, location.origin).href;
                                    input.id = 'hub-invite-issued-' + crypto.randomUUID(); label.htmlFor = input.id; issuance.append(label, input); input.focus(); input.select();
                                }
                                status.textContent = 'Invitation updated.'; await refresh(); await api.refresh?.();
                            } catch (error) { if (alive()) status.textContent = error.message; } finally { if (alive()) button.disabled = false; }
                        }); section.append(button);
                    }
                    const detail = element('button', 'Provisioning history', 'emby-button'); detail.type = 'button';
                    detail.addEventListener('click', async () => {
                        try {
                            const data = await api.read('invites/' + item.id);
                            if (!alive() || view !== epoch) return;
                            const history = element('ul');
                            for (const row of data.reservations || []) {
                                const entry = element('li', row.username + ': ' + row.state + ' (' + row.userId + ')');
                                if (!['completed', 'failed', 'revoked'].includes(row.state)) entry.append(control('Reconcile reservation (record verified completion; never create or enable)', async () => {
                                    await api.write('invite-reservations/' + row.id + '/reconcile', {}, 'Configuration');
                                    if (alive()) { status.textContent = 'Reservation inspected. Review provisioning history; uncertain recovery may require the invitee to resume with a fresh password.'; await refresh(); }
                                }));
                                history.append(entry);
                            }
                            section.append(history); detail.disabled = true;
                        } catch (error) { if (alive()) status.textContent = error.message; }
                    }); section.append(detail); list.append(section);
                }
                const previous = control('Previous invitations', async () => { skip = Math.max(0, start - 100); await refresh(); }), next = control('Next invitations', async () => { skip = start + 100; await refresh(); });
                previous.disabled = start === 0; next.disabled = response.total === undefined ? response.items.length < 100 : start + 100 >= response.total;
                list.append(element('p', `${response.items.length ? start + 1 : 0}–${start + response.items.length}` + (response.total === undefined ? ' invitations (browse to the end)' : ` of ${response.total}`)), previous, next);
            }
            form.addEventListener('submit', async event => {
                event.preventDefault(); if (!alive()) return; submit.disabled = true;
                try {
                    await api.write('invites', { expiresInMinutes: Number(minutes.value), maxUses: Number(uses.value), note: note.value, groupId: group.value || null, delivery: { copyLink: true, discordDm: !discord.disabled && !!discord.value, discordUserId: discord.disabled ? '' : discord.value } }, 'Configuration');
                    if (!alive()) return;
                    status.textContent = 'Request saved. Approve separately to generate its credential.'; await refresh(); await api.refresh?.();
                } catch (error) { if (alive()) status.textContent = error.message; }
                finally { if (alive()) submit.disabled = false; }
            });
            await Promise.all([loadGroups(), refresh()]);
            // Parent must call dispose on navigation so the one-time link is erased.
            return { dispose };
        }
    };
})();
