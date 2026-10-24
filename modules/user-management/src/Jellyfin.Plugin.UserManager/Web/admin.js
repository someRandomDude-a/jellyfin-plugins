(function () {
    'use strict';

    var state = {
        users: [],
        presets: [],
        permissions: [],
        invites: [],
        settings: null,
        status: null,
        editingUserId: null,
        editingPresetId: null,
        policy: {},
        policyDirty: false,
        auditLoaded: false,
        invitesLoaded: false
    };

    var JELLYFIN_FLAGS = [
        ['enableMediaPlayback', 'Media playback', 'Allow playing media.'],
        ['enableAudioPlaybackTranscoding', 'Audio transcoding', 'Allow audio transcoding.'],
        ['enableVideoPlaybackTranscoding', 'Video transcoding', 'Allow video transcoding.'],
        ['enablePlaybackRemuxing', 'Remuxing', 'Allow remuxing without transcoding.'],
        ['enableContentDownloading', 'Downloads', 'Allow downloading media.'],
        ['enableSyncTranscoding', 'Offline sync', 'Allow syncing media for offline use.'],
        ['enableMediaConversion', 'Media conversion', 'Allow converting media.'],
        ['enableContentDeletion', 'Delete media', 'Allow deleting media from libraries.'],
        ['enableRemoteAccess', 'Remote access', 'Allow access outside the local network.'],
        ['enablePublicSharing', 'Public sharing', 'Allow sharing media publicly.'],
        ['enableAllFolders', 'All libraries', 'Access to every library. Disable for a restricted library list.'],
        ['enableAllDevices', 'All devices', 'Allow sign-in from any device.'],
        ['enableAllChannels', 'All channels', 'Access to all plugins and channels.'],
        ['enableLiveTvAccess', 'Live TV', 'Allow watching Live TV.'],
        ['enableLiveTvManagement', 'Manage Live TV', 'Allow managing tuners and recordings.'],
        ['enableCollectionManagement', 'Manage collections', 'Allow creating and editing collections.'],
        ['enableSubtitleManagement', 'Manage subtitles', 'Allow editing subtitles.'],
        ['enableLyricManagement', 'Manage lyrics', 'Allow editing lyrics.'],
        ['enableRemoteControlOfOtherUsers', 'Control other sessions', 'Allow remote control of other users.'],
        ['enableSharedDeviceControl', 'Shared device control', 'Allow controlling shared devices.'],
        ['enableUserPreferenceAccess', 'User preferences', 'Allow changing playback preferences.'],
        ['isHidden', 'Hidden user', 'Hide this account from the login screen.']
    ];

    function $(id) { return document.getElementById(id); }

    function qsa(selector, root) {
        return Array.prototype.slice.call((root || document).querySelectorAll(selector));
    }

    UM.onReady(function () {
        if (!UM.token()) {
            UM.showAuthRequired($('main'));
            return;
        }

        bindTabs();
        bindUserEvents();
        bindPresetEvents();
        bindInviteEvents();
        bindAuditEvents();
        bindSettingsEvents();
        bindDialogClose();

        $('open-account').href = UM.serverRoot() + '/UserManager/account';
        refreshAll();
    });

    async function refreshAll() {
        $('status-line').textContent = 'Loading status…';
        await Promise.allSettled([loadStatus(), loadSettings(), loadPermissions(), loadPresets(), loadUsers()]);
        loadInvites();
        loadAudit();
    }

    function bindTabs() {
        qsa('.um-tabs button').forEach(function (button) {
            button.addEventListener('click', function () {
                showTab(button.dataset.tab);
            });
        });
    }

    function showTab(name) {
        qsa('.um-tabs button').forEach(function (button) {
            var active = button.dataset.tab === name;
            button.classList.toggle('active', active);
            button.setAttribute('aria-selected', active ? 'true' : 'false');
        });

        qsa('[data-panel]').forEach(function (panel) {
            panel.hidden = panel.dataset.panel !== name;
        });

        if (name === 'invites' && !state.invitesLoaded) { loadInvites(); }
        if (name === 'audit' && !state.auditLoaded) { loadAudit(); }
    }

    function bindDialogClose() {
        qsa('[data-close]').forEach(function (button) {
            button.addEventListener('click', function () {
                var dialog = $(button.dataset.close);
                if (dialog) { dialog.close(); }
            });
        });

        qsa('dialog.um-dialog').forEach(function (dialog) {
            dialog.addEventListener('click', function (event) {
                if (event.target === dialog) { dialog.close(); }
            });
        });
    }

    function handleError(error, host, retry) {
        if (UM.isAuthError(error)) {
            UM.showAuthRequired($('main'));
            return;
        }
        if (host) {
            host.innerHTML = '<div class="um-note error">' + UM.escapeHtml(error.message || String(error)) + '</div>';
        } else {
            UM.toast(error.message || String(error), 'error');
        }
        if (retry && host) {
            var button = document.createElement('button');
            button.type = 'button';
            button.className = 'um-btn small';
            button.textContent = 'Retry';
            button.style.marginTop = '8px';
            button.addEventListener('click', retry);
            host.appendChild(button);
        }
    }

    /* ---------------------------------------------------------------- users */

    async function loadUsers() {
        var host = $('users-host');
        try {
            var data = await UM.api('/admin/users');
            state.users = data.users || [];
            if (data.seerrError) {
                $('seerr-warning').innerHTML = '<div class="um-note warn">Seerr could not be reached: '
                    + UM.escapeHtml(data.seerrError) + '</div>';
            } else {
                $('seerr-warning').innerHTML = '';
            }
            renderUsers();
            populateUserSelectors();
        } catch (error) {
            if (UM.isAuthError(error)) {
                UM.showAuthRequired($('main'));
                return;
            }
            host.innerHTML = '';
            handleError(error, host, loadUsers);
        }
    }

    function filteredUsers() {
        var term = ($('user-search').value || '').trim().toLowerCase();
        var filter = $('user-filter').value;

        return state.users.filter(function (user) {
            if (filter === 'admin' && !user.isAdministrator) { return false; }
            if (filter === 'disabled' && !user.isDisabled) { return false; }
            if (filter === 'linked' && !user.discordUserId) { return false; }
            if (filter === 'unlinked' && user.discordUserId) { return false; }

            if (!term) { return true; }
            return [user.username, user.discordUsername, user.presetName, user.id]
                .filter(Boolean)
                .some(function (value) { return String(value).toLowerCase().indexOf(term) >= 0; });
        });
    }

    function userBadges(user) {
        var badges = [];
        if (user.isAdministrator) { badges.push('<span class="um-badge info">Admin</span>'); }
        if (user.isDisabled) { badges.push('<span class="um-badge bad">Disabled</span>'); }
        if (user.isHidden) { badges.push('<span class="um-badge">Hidden</span>'); }
        if (!badges.length) { badges.push('<span class="um-badge ok">Active</span>'); }
        return badges.join(' ');
    }

    function renderUsers() {
        var host = $('users-host');
        var users = filteredUsers();

        if (!users.length) {
            host.innerHTML = '<div class="um-empty">No users match this filter.</div>';
            return;
        }

        var rows = users.map(function (user) {
            var discord = user.discordUserId
                ? '<span class="um-badge ok">Linked</span><div class="um-small um-muted">@'
                    + UM.escapeHtml(user.discordUsername || user.discordUserId) + '</div>'
                : '<span class="um-badge">Not linked</span>';

            var seerr = user.seerrUserId
                ? '<span class="um-badge ok">Seerr #' + UM.escapeHtml(user.seerrUserId) + '</span>'
                : '<span class="um-badge">Not in Seerr</span>';

            return '<tr data-user-id="' + UM.escapeHtml(user.id) + '">'
                + '<td><strong>' + UM.escapeHtml(user.username) + '</strong>'
                + '<div class="um-small um-muted um-mono">' + UM.escapeHtml(user.id) + '</div></td>'
                + '<td>' + userBadges(user) + '</td>'
                + '<td>' + discord + '</td>'
                + '<td>' + seerr + '</td>'
                + '<td>' + (user.presetName
                    ? UM.escapeHtml(user.presetName)
                    : '<span class="um-muted">None</span>') + '</td>'
                + '<td class="um-small um-muted">' + UM.escapeHtml(UM.timeAgo(user.lastActivity || user.lastLogin)) + '</td>'
                + '<td><button type="button" class="um-btn small" data-manage-user="'
                + UM.escapeHtml(user.id) + '">Manage</button></td>'
                + '</tr>';
        }).join('');

        host.innerHTML = '<div class="um-table-wrap"><table class="um-table">'
            + '<thead><tr><th>User</th><th>Status</th><th>Discord</th><th>Seerr</th><th>Preset</th>'
            + '<th>Last activity</th><th></th></tr></thead>'
            + '<tbody>' + rows + '</tbody></table></div>';

        qsa('[data-manage-user]', host).forEach(function (button) {
            button.addEventListener('click', function () {
                openUserDialog(button.dataset.manageUser);
            });
        });
    }

    function populateUserSelectors() {
        var importSelect = $('import-user');
        var current = importSelect.value;
        importSelect.innerHTML = '<option value="">Import from user…</option>'
            + state.users.map(function (user) {
                return '<option value="' + UM.escapeHtml(user.id) + '">'
                    + UM.escapeHtml(user.username) + '</option>';
            }).join('');
        if (current) { importSelect.value = current; }
    }

    function bindUserEvents() {
        $('user-search').addEventListener('input', UM.debounce(renderUsers, 120));
        $('user-filter').addEventListener('change', renderUsers);
        $('open-create-user').addEventListener('click', openCreateUser);
        $('create-user-form').addEventListener('submit', createUser);
        $('save-user').addEventListener('click', saveUser);
        $('set-password').addEventListener('click', setPassword);
        $('send-discord-reset').addEventListener('click', sendDiscordReset);
        $('delete-user').addEventListener('click', deleteUser);
        $('sync-all-seerr').addEventListener('click', syncAllSeerr);
    }

    function openCreateUser() {
        $('create-user-form').reset();
        $('create-user-error').innerHTML = '';
        fillPresetSelect($('create-preset'));
        if (state.presets.length) {
            var preferred = state.presets.filter(function (preset) { return preset.isDefault; })[0] || state.presets[0];
            $('create-preset').value = preferred.id;
        }
        $('create-user-dialog').showModal();
        setTimeout(function () { $('create-username').focus(); }, 50);
    }

    async function createUser(event) {
        event.preventDefault();
        var host = $('create-user-error');
        host.innerHTML = '';

        var username = $('create-username').value.trim();
        var password = $('create-password').value;
        var confirm = $('create-password-confirm').value;

        if (password !== confirm) {
            host.innerHTML = '<div class="um-note error">The passwords do not match.</div>';
            return;
        }

        if (password.length < 10) {
            host.innerHTML = '<div class="um-note error">Use at least 10 characters.</div>';
            return;
        }

        var button = $('create-user-submit');
        UM.busy(button, true, 'Creating…');
        try {
            await UM.api('/admin/users', {
                method: 'POST',
                body: {
                    username: username,
                    password: password,
                    presetId: $('create-preset').value || null,
                    isAdministrator: $('create-admin').checked,
                    syncSeerr: $('create-sync-seerr').checked
                }
            });
            $('create-user-dialog').close();
            UM.toast('User ' + username + ' created.', 'ok');
            await loadUsers();
        } catch (error) {
            handleError(error, host);
        } finally {
            UM.busy(button, false, 'Create user');
        }
    }

    async function openUserDialog(id) {
        state.editingUserId = id;
        $('reset-result').innerHTML = '';
        $('reset-new-password').value = '';
        $('user-dialog-summary').innerHTML = '<div class="um-loading"><span class="um-spinner"></span>Loading user…</div>';
        $('user-discord-host').innerHTML = '';
        fillPresetSelect($('edit-preset'));

        $('user-dialog').showModal();

        try {
            var detail = await UM.api('/admin/users/' + encodeURIComponent(id));
            var summary = detail.summary;
            var current = state.users.filter(function (user) { return user.id === id; })[0];
            if (current) { summary = Object.assign({}, current, summary); }
            state.editingUser = summary;

            $('user-dialog-title').textContent = summary.username;
            $('edit-preset').value = summary.presetId || '';
            $('edit-admin').checked = !!summary.isAdministrator;
            $('edit-disabled').checked = !!summary.isDisabled;

            var permissionNames = detail.seerrPermissionNames || [];
            $('user-dialog-summary').innerHTML = '<div class="um-row between">'
                + '<div><strong style="font-size:17px">' + UM.escapeHtml(summary.username) + '</strong>'
                + '<div class="um-small um-muted um-mono">' + UM.escapeHtml(summary.id) + '</div></div>'
                + '<div>' + userBadges(summary) + '</div></div>'
                + '<dl class="um-kv" style="margin-top:12px">'
                + '<dt>Preset</dt><dd>' + UM.escapeHtml(summary.presetName || 'None') + '</dd>'
                + '<dt>Seerr permissions</dt><dd>' + (permissionNames.length
                    ? UM.escapeHtml(permissionNames.join(', '))
                    : '<span class="um-muted">Not imported into Seerr</span>') + '</dd>'
                + '<dt>Last activity</dt><dd>' + UM.escapeHtml(UM.formatDate(summary.lastActivity)) + '</dd>'
                + '</dl>';

            renderUserDiscord(summary);
        } catch (error) {
            $('user-dialog-summary').innerHTML = '';
            handleError(error, $('user-dialog-summary'));
        }
    }

    function renderUserDiscord(summary) {
        var host = $('user-discord-host');
        if (summary.discordUserId) {
            host.innerHTML = '<div class="um-note ok">Linked to @'
                + UM.escapeHtml(summary.discordUsername || summary.discordUserId)
                + ' (id ' + UM.escapeHtml(summary.discordUserId) + ')</div>'
                + '<div class="um-small um-muted" style="margin-top:6px">Linked '
                + UM.escapeHtml(UM.formatDate(summary.discordLinkedAt)) + '.</div>';
            return;
        }

        host.innerHTML = '<p class="um-sub">No Discord account is linked. Generate a one-time code and ask the user to run '
            + '<code>/link CODE</code> in Discord.</p>'
            + '<div class="um-row"><button type="button" class="um-btn" id="generate-link-code">Generate link code</button></div>'
            + '<div id="link-code-result" style="margin-top:10px"></div>';

        $('generate-link-code').addEventListener('click', generateLinkCode);
    }

    async function generateLinkCode() {
        var host = $('link-code-result');
        var button = $('generate-link-code');
        UM.busy(button, true, 'Generating…');
        try {
            var result = await UM.api('/admin/users/' + encodeURIComponent(state.editingUserId) + '/link-code', { method: 'POST' });
            host.innerHTML = '<div class="um-note"><strong>Link code:</strong> '
                + '<span class="um-mono" style="font-size:17px;color:var(--um-primary)">' + UM.escapeHtml(result.code) + '</span>'
                + '<div class="um-small" style="margin-top:6px">' + UM.escapeHtml(result.instructions)
                + ' Expires ' + UM.escapeHtml(UM.formatDate(result.expiresAt)) + '.</div></div>'
                + '<div class="um-row" style="margin-top:8px"><button type="button" class="um-btn small" id="copy-link-code">Copy code</button></div>';
            $('copy-link-code').addEventListener('click', function () {
                UM.copy(result.code);
            });
        } catch (error) {
            handleError(error, host);
        } finally {
            UM.busy(button, false, 'Generate link code');
        }
    }

    async function saveUser() {
        var button = $('save-user');
        UM.busy(button, true, 'Saving…');
        try {
            var presetId = $('edit-preset').value;
            await UM.api('/admin/users/' + encodeURIComponent(state.editingUserId), {
                method: 'PUT',
                body: {
                    isAdministrator: $('edit-admin').checked,
                    isDisabled: $('edit-disabled').checked,
                    presetId: presetId || null,
                    syncSeerr: true
                }
            });
            UM.toast('User updated.', 'ok');
            $('user-dialog').close();
            await loadUsers();
        } catch (error) {
            handleError(error, null);
        } finally {
            UM.busy(button, false, 'Save changes');
        }
    }

    async function setPassword() {
        var password = $('reset-new-password').value;
        var host = $('reset-result');
        host.innerHTML = '';
        if (password.length < 10) {
            host.innerHTML = '<div class="um-note error">Use at least 10 characters.</div>';
            return;
        }

        var button = $('set-password');
        UM.busy(button, true, 'Setting…');
        try {
            await UM.api('/admin/users/' + encodeURIComponent(state.editingUserId) + '/reset-password', {
                method: 'POST',
                body: { newPassword: password, sendDiscordLink: false }
            });
            $('reset-new-password').value = '';
            host.innerHTML = '<div class="um-note ok">Password updated.</div>';
            UM.toast('Password updated.', 'ok');
        } catch (error) {
            handleError(error, host);
        } finally {
            UM.busy(button, false, 'Set password');
        }
    }

    async function sendDiscordReset() {
        var host = $('reset-result');
        host.innerHTML = '';
        var button = $('send-discord-reset');
        UM.busy(button, true, 'Sending…');
        try {
            var result = await UM.api('/admin/users/' + encodeURIComponent(state.editingUserId) + '/reset-password', {
                method: 'POST',
                body: { newPassword: null, sendDiscordLink: true }
            });
            host.innerHTML = '<div class="um-note ' + (result.delivered ? 'ok' : 'warn') + '">'
                + UM.escapeHtml(result.message || 'Reset link created.') + '</div>'
                + (result.resetUrl
                    ? '<div style="margin-top:8px"><span class="um-code">' + UM.escapeHtml(result.resetUrl) + '</span>'
                        + '<div class="um-row" style="margin-top:8px"><button type="button" class="um-btn small" id="copy-reset-url">Copy reset link</button></div></div>'
                    : '');
            var copy = $('copy-reset-url');
            if (copy) {
                copy.addEventListener('click', function () { UM.copy(result.resetUrl); });
            }
        } catch (error) {
            handleError(error, host);
        } finally {
            UM.busy(button, false, 'Send Discord reset link');
        }
    }

    async function deleteUser() {
        var name = state.editingUser ? state.editingUser.username : 'this user';
        if (!window.confirm('Delete ' + name + ' from Jellyfin and Seerr? This cannot be undone.')) {
            return;
        }

        var button = $('delete-user');
        UM.busy(button, true, 'Deleting…');
        try {
            await UM.api('/admin/users/' + encodeURIComponent(state.editingUserId), { method: 'DELETE' });
            $('user-dialog').close();
            UM.toast(name + ' deleted.', 'ok');
            await loadUsers();
        } catch (error) {
            handleError(error, null);
        } finally {
            UM.busy(button, false, 'Delete user');
        }
    }

    async function syncAllSeerr() {
        if (!window.confirm('Push Jellyfin users and preset permissions to Seerr now?')) { return; }
        var button = $('sync-all-seerr');
        UM.busy(button, true, 'Syncing…');
        try {
            var result = await UM.api('/admin/seerr/sync-all', { method: 'POST' });
            var failed = result.failed || [];
            UM.toast('Synced ' + result.synced + ' user(s) to Seerr'
                + (failed.length ? '; ' + failed.length + ' failed.' : '.'), failed.length ? 'warn' : 'ok');
            await loadUsers();
        } catch (error) {
            handleError(error, null);
        } finally {
            UM.busy(button, false, 'Sync all to Seerr');
        }
    }

    /* -------------------------------------------------------------- presets */

    async function loadPresets() {
        try {
            state.presets = await UM.api('/admin/presets');
            renderPresets();
            fillPresetSelect($('create-preset'));
            fillPresetSelect($('invite-preset'));
            fillPresetSelect($('edit-preset'));
        } catch (error) {
            handleError(error, $('presets-host'), loadPresets);
        }
    }

    async function loadPermissions() {
        try {
            state.permissions = await UM.api('/admin/presets/permissions');
            if (state.presets.length) { renderPresets(); }
        } catch (error) {
            state.permissions = [];
            UM.toast('Could not load the Seerr permission catalog.', 'warn');
        }
    }

    function fillPresetSelect(select, includeEmpty) {
        if (!select) { return; }
        var current = select.value;
        select.innerHTML = (includeEmpty ? '<option value="">No preset</option>' : '')
            + state.presets.map(function (preset) {
                return '<option value="' + UM.escapeHtml(preset.id) + '">'
                    + UM.escapeHtml(preset.name) + (preset.isDefault ? ' (default)' : '') + '</option>';
            }).join('');
        if (current) { select.value = current; }
    }

    function renderPresets() {
        var host = $('presets-host');
        if (!state.presets.length) {
            host.innerHTML = '<div class="um-empty">No presets yet.</div>';
            return;
        }

        host.innerHTML = '<div class="um-grid two">' + state.presets.map(function (preset) {
            var value = preset.seerrPermissions || 0;
            var permissionCount = state.permissions.length
                ? state.permissions.filter(function (permission) {
                    return permission.value > 0 && (value & permission.value) === permission.value;
                }).length
                : 0;

            return '<div class="um-card" style="margin:0;background:var(--um-surface-2)">'
                + '<div class="um-row between"><strong>' + UM.escapeHtml(preset.name) + '</strong>'
                + (preset.isDefault ? '<span class="um-badge info">Default</span>' : '') + '</div>'
                + '<p class="um-sub" style="margin:6px 0 10px">'
                + UM.escapeHtml(preset.description || 'No description.') + '</p>'
                + '<div class="um-small um-muted">' + permissionCount + ' Seerr permission(s) · updated '
                + UM.escapeHtml(UM.formatDate(preset.updatedAt)) + '</div>'
                + '<div class="um-row end" style="margin-top:12px">'
                + '<button type="button" class="um-btn small" data-edit-preset="' + UM.escapeHtml(preset.id) + '">Edit preset</button>'
                + '</div></div>';
        }).join('') + '</div>';

        qsa('[data-edit-preset]', host).forEach(function (button) {
            button.addEventListener('click', function () {
                openPresetDialog(button.dataset.editPreset);
            });
        });
    }

    function bindPresetEvents() {
        $('open-create-preset').addEventListener('click', function () { openPresetDialog(null); });
        $('preset-form').addEventListener('submit', savePreset);
        $('delete-preset').addEventListener('click', deletePreset);
        $('import-preset').addEventListener('click', importPreset);
        $('preset-policy-json').addEventListener('input', function () {
            state.policyDirty = true;
        });
    }

    function openPresetDialog(id) {
        var preset = id ? state.presets.filter(function (item) { return item.id === id; })[0] : null;
        state.editingPresetId = preset ? preset.id : null;
        state.policyDirty = false;
        state.policy = preset && preset.jellyfinPolicy
            ? JSON.parse(JSON.stringify(preset.jellyfinPolicy))
            : {};

        $('preset-dialog-title').textContent = preset ? 'Edit preset' : 'New preset';
        $('preset-name').value = preset ? preset.name : '';
        $('preset-description').value = preset ? (preset.description || '') : '';
        $('preset-default').checked = preset ? !!preset.isDefault : false;
        $('delete-preset').hidden = !preset;
        $('preset-error').innerHTML = '';
        renderSeerrPermissions(preset ? preset.seerrPermissions || 0 : 0);
        renderJellyfinPermissions();
        $('preset-policy-json').value = JSON.stringify(state.policy, null, 2);

        $('preset-dialog').showModal();
        setTimeout(function () { $('preset-name').focus(); }, 50);
    }

    function renderSeerrPermissions(selected) {
        var host = $('preset-seerr-perms');
        if (!state.permissions.length) {
            host.innerHTML = '<div class="um-note">Seerr permission catalog unavailable.</div>';
            return;
        }

        host.innerHTML = state.permissions.map(function (permission) {
            var on = (selected & permission.value) === permission.value;
            return '<label class="um-check"><input type="checkbox" data-perm-value="' + permission.value + '"'
                + (on ? ' checked' : '') + '>'
                + '<span class="um-check-label">' + UM.escapeHtml(permission.name)
                + '<small>' + UM.escapeHtml(permission.group + ' — ' + permission.description) + '</small>'
                + '</span></label>';
        }).join('');
    }

    function renderJellyfinPermissions() {
        var host = $('preset-jellyfin-perms');
        host.innerHTML = JELLYFIN_FLAGS.map(function (flag) {
            var key = flag[0];
            var on = state.policy[key] === true;
            return '<label class="um-check"><input type="checkbox" data-policy-flag="' + key + '"'
                + (on ? ' checked' : '') + '>'
                + '<span class="um-check-label">' + UM.escapeHtml(flag[1])
                + '<small>' + UM.escapeHtml(flag[2]) + '</small></span></label>';
        }).join('')
            + '<label class="um-field">Max active sessions'
            + '<input type="number" min="0" data-policy-number="maxActiveSessions" value="'
            + Number(state.policy.maxActiveSessions || 0) + '">'
            + '<span class="um-hint">0 = unlimited.</span></label>'
            + '<label class="um-field">Login attempts before lockout'
            + '<input type="number" min="-1" data-policy-number="loginAttemptsBeforeLockout" value="'
            + Number(state.policy.loginAttemptsBeforeLockout === undefined ? -1 : state.policy.loginAttemptsBeforeLockout) + '">'
            + '<span class="um-hint">-1 disables lockout.</span></label>';

        qsa('[data-policy-flag]', host).forEach(function (input) {
            input.addEventListener('change', function () {
                state.policy[input.dataset.policyFlag] = input.checked;
                syncPolicyJson();
            });
        });

        qsa('[data-policy-number]', host).forEach(function (input) {
            input.addEventListener('input', function () {
                var value = parseInt(input.value, 10);
                state.policy[input.dataset.policyNumber] = isNaN(value) ? 0 : value;
                syncPolicyJson();
            });
        });
    }

    function syncPolicyJson() {
        state.policyDirty = false;
        $('preset-policy-json').value = JSON.stringify(state.policy, null, 2);
    }

    async function savePreset(event) {
        event.preventDefault();
        var host = $('preset-error');
        host.innerHTML = '';

        var policy = state.policy;
        var raw = $('preset-policy-json').value.trim();
        if (state.policyDirty && raw) {
            try {
                policy = JSON.parse(raw);
            } catch (e) {
                host.innerHTML = '<div class="um-note error">The raw policy JSON is not valid: '
                    + UM.escapeHtml(e.message) + '</div>';
                return;
            }
        }

        var permissions = 0;
        qsa('[data-perm-value]', $('preset-seerr-perms')).forEach(function (input) {
            if (input.checked) { permissions += Number(input.dataset.permValue); }
        });

        var payload = {
            name: $('preset-name').value.trim(),
            description: $('preset-description').value.trim() || null,
            jellyfinPolicy: policy,
            seerrPermissions: permissions,
            isDefault: $('preset-default').checked
        };

        if (!payload.name) {
            host.innerHTML = '<div class="um-note error">Enter a preset name.</div>';
            return;
        }

        var button = $('save-preset');
        UM.busy(button, true, 'Saving…');
        try {
            if (state.editingPresetId) {
                await UM.api('/admin/presets/' + encodeURIComponent(state.editingPresetId), { method: 'PUT', body: payload });
            } else {
                await UM.api('/admin/presets', { method: 'POST', body: payload });
            }
            $('preset-dialog').close();
            UM.toast('Preset saved.', 'ok');
            await loadPresets();
            await loadUsers();
        } catch (error) {
            handleError(error, host);
        } finally {
            UM.busy(button, false, 'Save preset');
        }
    }

    async function deletePreset() {
        if (!state.editingPresetId) { return; }
        if (!window.confirm('Delete this preset? Users keep their current permissions.')) { return; }

        var button = $('delete-preset');
        UM.busy(button, true, 'Deleting…');
        try {
            await UM.api('/admin/presets/' + encodeURIComponent(state.editingPresetId), { method: 'DELETE' });
            $('preset-dialog').close();
            UM.toast('Preset deleted.', 'ok');
            await loadPresets();
        } catch (error) {
            handleError(error, null);
        } finally {
            UM.busy(button, false, 'Delete');
        }
    }

    async function importPreset() {
        var userId = $('import-user').value;
        if (!userId) {
            UM.toast('Choose a user to copy permissions from.', 'warn');
            return;
        }

        var user = state.users.filter(function (item) { return item.id === userId; })[0];
        var name = window.prompt('Name for the imported preset', (user ? user.username : 'User') + ' copy');
        if (name === null) { return; }

        var button = $('import-preset');
        UM.busy(button, true, 'Importing…');
        try {
            await UM.api('/admin/presets/import/' + encodeURIComponent(userId), {
                method: 'POST',
                body: { name: name, description: 'Imported from ' + (user ? user.username : userId) }
            });
            UM.toast('Preset imported.', 'ok');
            await loadPresets();
        } catch (error) {
            handleError(error, null);
        } finally {
            UM.busy(button, false, 'Import from user');
        }
    }

    /* -------------------------------------------------------------- invites */

    async function loadInvites() {
        state.invitesLoaded = true;
        var host = $('invites-host');
        try {
            state.invites = await UM.api('/admin/invites');
            renderInvites();
            fillPresetSelect($('invite-preset'));
            if (!$('invite-preset').value && state.presets.length) {
                var preferred = state.presets.filter(function (preset) { return preset.isDefault; })[0] || state.presets[0];
                $('invite-preset').value = preferred.id;
            }
        } catch (error) {
            handleError(error, host, loadInvites);
        }
    }

    function inviteStatusBadge(status) {
        var map = { active: 'ok', used: '', expired: 'warn', revoked: 'bad' };
        return '<span class="um-badge ' + (map[status] || '') + '">' + UM.escapeHtml(status) + '</span>';
    }

    function renderInvites() {
        var host = $('invites-host');
        if (!state.invites.length) {
            host.innerHTML = '<div class="um-empty">No invites yet. Create one above.</div>';
            return;
        }

        var rows = state.invites.map(function (invite) {
            var uses = invite.uses || [];
            var usesHtml = uses.length
                ? uses.map(function (use) {
                    return '<div class="um-small">' + UM.escapeHtml(use.username)
                        + ' <span class="um-muted">· ' + UM.escapeHtml(UM.formatDate(use.usedAt)) + '</span></div>';
                }).join('')
                : '<span class="um-muted um-small">No uses yet</span>';

            return '<tr>'
                + '<td><div>' + UM.escapeHtml(UM.formatDate(invite.createdAt)) + '</div>'
                + '<div class="um-small um-muted">by ' + UM.escapeHtml(invite.createdByName) + '</div></td>'
                + '<td>' + UM.escapeHtml(invite.presetName || '—') + '</td>'
                + '<td>' + invite.useCount + ' / ' + invite.maxUses + '</td>'
                + '<td>' + UM.escapeHtml(UM.formatDate(invite.expiresAt)) + '</td>'
                + '<td>' + inviteStatusBadge(invite.status) + '</td>'
                + '<td class="um-small">' + usesHtml + '</td>'
                + '<td><div class="um-actions">'
                + (invite.status === 'active'
                    ? '<button type="button" class="um-btn small" data-revoke="' + UM.escapeHtml(invite.id) + '">Revoke</button>'
                    : '')
                + '<button type="button" class="um-btn small danger" data-delete-invite="' + UM.escapeHtml(invite.id) + '">Delete</button>'
                + '</div></td></tr>';
        }).join('');

        host.innerHTML = '<div class="um-table-wrap"><table class="um-table">'
            + '<thead><tr><th>Created</th><th>Preset</th><th>Uses</th><th>Expires</th><th>Status</th>'
            + '<th>Used by</th><th></th></tr></thead><tbody>' + rows + '</tbody></table></div>';

        qsa('[data-revoke]', host).forEach(function (button) {
            button.addEventListener('click', function () { revokeInvite(button.dataset.revoke); });
        });
        qsa('[data-delete-invite]', host).forEach(function (button) {
            button.addEventListener('click', function () { deleteInvite(button.dataset.deleteInvite); });
        });
    }

    function bindInviteEvents() {
        $('create-invite-form').addEventListener('submit', createInvite);
        $('refresh-invites').addEventListener('click', loadInvites);
    }

    async function createInvite(event) {
        event.preventDefault();
        var button = $('create-invite-submit');
        UM.busy(button, true, 'Creating…');
        try {
            var result = await UM.api('/admin/invites', {
                method: 'POST',
                body: {
                    presetId: $('invite-preset').value || null,
                    maxUses: parseInt($('invite-uses').value, 10) || 1,
                    expiresInDays: parseInt($('invite-days').value, 10) || 7,
                    note: $('invite-note').value.trim() || null
                }
            });

            $('invite-result').innerHTML = '<div class="um-note ok">Invite created. Copy the link now — it cannot be shown again.</div>'
                + '<div style="margin-top:8px"><span class="um-code">' + UM.escapeHtml(result.url) + '</span></div>'
                + '<div class="um-row" style="margin-top:8px"><button type="button" class="um-btn small" id="copy-invite-url">Copy invite link</button></div>';
            $('copy-invite-url').addEventListener('click', function () { UM.copy(result.url); });
            $('invite-note').value = '';
            await loadInvites();
        } catch (error) {
            handleError(error, $('invite-result'));
        } finally {
            UM.busy(button, false, 'Create invite');
        }
    }

    async function revokeInvite(id) {
        if (!window.confirm('Revoke this invite? The link will stop working.')) { return; }
        try {
            await UM.api('/admin/invites/' + encodeURIComponent(id) + '/revoke', { method: 'POST' });
            UM.toast('Invite revoked.', 'ok');
            await loadInvites();
        } catch (error) {
            handleError(error, null);
        }
    }

    async function deleteInvite(id) {
        if (!window.confirm('Delete this invite record?')) { return; }
        try {
            await UM.api('/admin/invites/' + encodeURIComponent(id), { method: 'DELETE' });
            UM.toast('Invite deleted.', 'ok');
            await loadInvites();
        } catch (error) {
            handleError(error, null);
        }
    }

    /* ---------------------------------------------------------------- audit */

    function bindAuditEvents() {
        $('apply-audit').addEventListener('click', loadAudit);
        $('audit-action').addEventListener('change', loadAudit);
        $('export-audit').addEventListener('click', exportAudit);
    }

    async function loadAudit() {
        state.auditLoaded = true;
        var host = $('audit-host');
        var limit = parseInt($('audit-limit').value, 10) || 100;
        var action = $('audit-action').value;
        var user = $('audit-user').value.trim();

        var query = '/admin/audit?limit=' + encodeURIComponent(limit) + '&offset=0';
        if (action) { query += '&action=' + encodeURIComponent(action); }
        if (user) { query += '&user=' + encodeURIComponent(user); }

        host.innerHTML = '<div class="um-loading"><span class="um-spinner"></span>Loading audit log…</div>';
        try {
            var result = await UM.api(query);
            var actions = result.actions || [];
            var select = $('audit-action');
            var current = select.value;
            select.innerHTML = '<option value="">All actions</option>' + actions.map(function (value) {
                return '<option value="' + UM.escapeHtml(value) + '">' + UM.escapeHtml(value) + '</option>';
            }).join('');
            select.value = current;

            var entries = result.entries || [];
            if (!entries.length) {
                host.innerHTML = '<div class="um-empty">No audit entries match this filter.</div>';
                return;
            }

            host.innerHTML = '<div class="um-small um-muted" style="margin-bottom:8px">Showing '
                + entries.length + ' of ' + result.total + ' entries.</div>'
                + '<div class="um-table-wrap"><table class="um-table"><thead><tr>'
                + '<th>Time</th><th>Action</th><th>Actor</th><th>Target</th><th>Details</th><th>IP</th>'
                + '</tr></thead><tbody>'
                + entries.map(function (entry) {
                    return '<tr><td class="um-small" style="white-space:nowrap">'
                        + UM.escapeHtml(UM.formatDate(entry.timestamp)) + '</td>'
                        + '<td><span class="um-badge">' + UM.escapeHtml(entry.action) + '</span></td>'
                        + '<td class="um-small">' + UM.escapeHtml(entry.actorName || entry.actorUserId || '—') + '</td>'
                        + '<td class="um-small">' + UM.escapeHtml(entry.targetName || entry.targetUserId || '—') + '</td>'
                        + '<td class="um-small">' + UM.escapeHtml(entry.details || '') + '</td>'
                        + '<td class="um-small um-mono">' + UM.escapeHtml(entry.ipAddress || '—') + '</td></tr>';
                }).join('')
                + '</tbody></table></div>';
        } catch (error) {
            handleError(error, host, loadAudit);
        }
    }

    async function exportAudit() {
        var button = $('export-audit');
        UM.busy(button, true, 'Exporting…');
        try {
            var csv = await UM.api('/admin/audit/export.csv?limit=10000');
            var blob = new Blob([typeof csv === 'string' ? csv : JSON.stringify(csv)], { type: 'text/csv' });
            var url = URL.createObjectURL(blob);
            var link = document.createElement('a');
            link.href = url;
            link.download = 'usermanager-audit.csv';
            document.body.appendChild(link);
            link.click();
            link.remove();
            setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
        } catch (error) {
            handleError(error, null);
        } finally {
            UM.busy(button, false, 'Export CSV');
        }
    }

    /* ------------------------------------------------------------- settings */

    function bindSettingsEvents() {
        $('settings-form').addEventListener('submit', saveSettings);
        $('refresh-status').addEventListener('click', loadStatus);
        $('test-seerr').addEventListener('click', testSeerr);
        $('test-discord').addEventListener('click', testDiscord);
        $('reconnect-discord').addEventListener('click', reconnectDiscord);
        $('copy-request-link').addEventListener('click', function () {
            UM.copy(UM.serverRoot() + '/UserManager/invite-request');
        });
        $('allowPublicInviteRequests').addEventListener('change', updateRequestLink);
    }

    async function loadSettings() {
        try {
            state.settings = await UM.api('/admin/settings');
            var settings = state.settings;
            $('seerrUrl').value = settings.seerrUrl || '';
            $('discordGuildId').value = settings.discordGuildId || '';
            $('discordAdminRoleIds').value = settings.discordAdminRoleIds || '';
            $('publicBaseUrl').value = settings.publicBaseUrl || '';
            $('inviteDefaultExpiryDays').value = settings.inviteDefaultExpiryDays;
            $('inviteDefaultMaxUses').value = settings.inviteDefaultMaxUses;
            $('allowPublicInviteRequests').checked = !!settings.allowPublicInviteRequests;
            $('notifyAdminsOnJoin').checked = !!settings.notifyAdminsOnJoin;
            $('notifyAdminsOnPasswordReset').checked = !!settings.notifyAdminsOnPasswordReset;
            $('notifyAdminsOnInviteUsed').checked = !!settings.notifyAdminsOnInviteUsed;
            $('syncSeerrOnCreate').checked = !!settings.syncSeerrOnCreate;
            $('syncSeerrOnPresetApply').checked = !!settings.syncSeerrOnPresetApply;

            $('seerrApiKey').value = '';
            $('seerrApiKey').placeholder = settings.seerrApiKeySet ? 'Saved — leave blank to keep' : 'Seerr API key';
            $('seerrApiKey-hint').textContent = settings.seerrApiKeySet ? 'A key is stored and encrypted.' : '';
            $('discordBotToken').value = '';
            $('discordBotToken').placeholder = settings.discordBotTokenSet ? 'Saved — leave blank to keep' : 'Bot token';
            $('discordBotToken-hint').textContent = settings.discordBotTokenSet ? 'A token is stored and encrypted.' : '';
            $('discordAdminWebhookUrl').value = '';
            $('discordAdminWebhookUrl').placeholder = settings.discordAdminWebhookSet
                ? 'Saved — leave blank to keep'
                : 'https://discord.com/api/webhooks/…';
            $('discordAdminWebhookUrl-hint').textContent = settings.discordAdminWebhookSet
                ? 'A webhook is stored and encrypted.'
                : '';
            ['clear-seerrApiKey', 'clear-discordBotToken', 'clear-discordAdminWebhookUrl'].forEach(function (id) {
                $(id).checked = false;
            });

            updateRequestLink();
        } catch (error) {
            handleError(error, null);
        }
    }

    function updateRequestLink() {
        var link = UM.serverRoot() + '/UserManager/invite-request';
        $('request-link').textContent = link;
        $('request-link-row').style.opacity = $('allowPublicInviteRequests').checked ? '1' : '.55';
    }

    function secretValue(fieldId, clearId) {
        if ($(clearId).checked) { return ''; }
        var value = $(fieldId).value.trim();
        return value ? value : null;
    }

    async function saveSettings(event) {
        event.preventDefault();
        var button = $('save-settings');
        UM.busy(button, true, 'Saving…');
        try {
            var payload = {
                seerrUrl: $('seerrUrl').value.trim(),
                seerrApiKey: secretValue('seerrApiKey', 'clear-seerrApiKey'),
                discordBotToken: secretValue('discordBotToken', 'clear-discordBotToken'),
                discordAdminWebhookUrl: secretValue('discordAdminWebhookUrl', 'clear-discordAdminWebhookUrl'),
                discordGuildId: $('discordGuildId').value.trim(),
                discordAdminRoleIds: $('discordAdminRoleIds').value.trim(),
                publicBaseUrl: $('publicBaseUrl').value.trim(),
                inviteDefaultExpiryDays: parseInt($('inviteDefaultExpiryDays').value, 10) || 7,
                inviteDefaultMaxUses: parseInt($('inviteDefaultMaxUses').value, 10) || 1,
                allowPublicInviteRequests: $('allowPublicInviteRequests').checked,
                notifyAdminsOnJoin: $('notifyAdminsOnJoin').checked,
                notifyAdminsOnPasswordReset: $('notifyAdminsOnPasswordReset').checked,
                notifyAdminsOnInviteUsed: $('notifyAdminsOnInviteUsed').checked,
                syncSeerrOnCreate: $('syncSeerrOnCreate').checked,
                syncSeerrOnPresetApply: $('syncSeerrOnPresetApply').checked
            };

            await UM.api('/admin/settings', { method: 'PUT', body: payload });
            UM.toast('Settings saved. The Discord bot is reconnecting.', 'ok');
            await loadSettings();
            await loadStatus();
        } catch (error) {
            handleError(error, null);
        } finally {
            UM.busy(button, false, 'Save settings');
        }
    }

    async function testSeerr() {
        var button = $('test-seerr');
        UM.busy(button, true, 'Testing…');
        try {
            var result = await UM.api('/admin/seerr/test', { method: 'POST' });
            UM.toast(result.message || (result.ok ? 'Connected.' : 'Connection failed.'), result.ok ? 'ok' : 'error');
        } catch (error) {
            handleError(error, null);
        } finally {
            UM.busy(button, false, 'Test connection');
        }
    }

    async function testDiscord() {
        var button = $('test-discord');
        UM.busy(button, true, 'Sending…');
        try {
            var result = await UM.api('/admin/discord/test', { method: 'POST' });
            UM.toast(result.message || 'Test notification sent.', 'ok');
        } catch (error) {
            handleError(error, null);
        } finally {
            UM.busy(button, false, 'Send test notification');
        }
    }

    async function reconnectDiscord() {
        var button = $('reconnect-discord');
        UM.busy(button, true, 'Reconnecting…');
        try {
            await UM.api('/admin/discord/reconnect', { method: 'POST' });
            UM.toast('Discord reconnect requested.', 'ok');
            setTimeout(loadStatus, 1500);
        } catch (error) {
            handleError(error, null);
        } finally {
            UM.busy(button, false, 'Reconnect bot');
        }
    }

    async function loadStatus() {
        try {
            state.status = await UM.api('/admin/status');
            var status = state.status;

            $('status-line').textContent = 'v' + status.version
                + ' · Discord ' + (status.discordConnected ? 'connected' : 'disconnected')
                + ' · Seerr ' + (status.seerrConfigured ? 'configured' : 'not configured');

            var cards = [
                { label: 'Invites', value: status.invites },
                { label: 'Linked accounts', value: status.linkedAccounts },
                { label: 'Audit entries', value: status.auditEntries },
                { label: 'Presets', value: status.presets },
                { label: 'Discord bot', value: status.discordConnected ? 'Connected' : 'Offline' },
                { label: 'Web client injection', value: status.fileTransformationRegistered ? 'Active' : 'Inactive' }
            ];

            $('status-cards').innerHTML = cards.map(function (card) {
                return '<div class="um-stat"><strong>' + UM.escapeHtml(card.value) + '</strong><span>'
                    + UM.escapeHtml(card.label) + '</span></div>';
            }).join('');

            var notes = '';
            if (status.fileTransformationError) {
                notes += '<div class="um-note warn">File Transformation: '
                    + UM.escapeHtml(status.fileTransformationError) + '</div>';
            }
            if (!status.fileTransformationRegistered) {
                notes += '<div class="um-note warn">File Transformation is not registered. Install the File Transformation plugin '
                    + 'so the account menu and login-page reset links appear in the web client. The plugin pages still work directly.</div>';
            }
            if (!status.publicBaseUrl) {
                notes += '<div class="um-note">No public base URL is configured. Invite and reset links use the URL from the current request.</div>';
            }
            $('status-notes').innerHTML = notes;
        } catch (error) {
            handleError(error, null);
        }
    }
})();
