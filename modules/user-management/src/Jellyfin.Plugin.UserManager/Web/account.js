(function () {
    'use strict';

    var me = null;

    function $(id) { return document.getElementById(id); }

    UM.onReady(function () {
        $('back-to-jellyfin').href = UM.serverRoot() + '/web/index.html';

        if (!UM.token()) {
            UM.showAuthRequired($('account-main'));
            return;
        }

        load();
    });

    async function load() {
        try {
            me = await UM.api('/me');
        } catch (error) {
            if (UM.isAuthError(error)) {
                UM.showAuthRequired($('account-main'));
                return;
            }

            $('account-main').innerHTML = '<div class="um-card"><div class="um-note error">'
                + UM.escapeHtml(error.message) + '</div>'
                + '<div class="um-row end" style="margin-top:12px"><button type="button" class="um-btn" id="retry">Retry</button></div></div>';
            $('retry').addEventListener('click', load);
            return;
        }

        render();
        loadActivity();
    }

    function render() {
        if (me.isAdministrator) {
            var admin = $('admin-link');
            admin.hidden = false;
            admin.href = UM.serverRoot() + '/web/ConfigurationPage?name=usermanager';
        }

        $('account-main').innerHTML = ''
            + '<div class="um-split">'
            + '  <div>'
            + '    <div class="um-card">'
            + '      <h2>Profile</h2>'
            + '      <p class="um-sub">Your Jellyfin identity. Contact an administrator to change your username.</p>'
            + '      <dl class="um-kv">'
            + '        <dt>Username</dt><dd><strong>' + UM.escapeHtml(me.username) + '</strong></dd>'
            + '        <dt>Account</dt><dd>' + (me.isAdministrator
                ? '<span class="um-badge info">Administrator</span>'
                : '<span class="um-badge ok">User</span>')
                + (me.isDisabled ? ' <span class="um-badge bad">Disabled</span>' : '') + '</dd>'
            + '        <dt>Seerr</dt><dd>' + (me.seerrConfigured
                ? '<span class="um-badge ok">Connected</span>'
                : '<span class="um-badge">Not configured</span>') + '</dd>'
            + '      </dl>'
            + '    </div>'
            + '    <div class="um-card">'
            + '      <h2>Discord</h2>'
            + '      <p class="um-sub">Link Discord to reset your password with a direct message instead of contacting an administrator.</p>'
            + '      <div id="discord-host"></div>'
            + '    </div>'
            + '  </div>'
            + '  <div>'
            + '    <div class="um-card">'
            + '      <h2>Password</h2>'
            + '      <p class="um-sub">Use at least 10 characters with three of: lowercase, uppercase, numbers, symbols.</p>'
            + '      <form id="password-form">'
            + '        <label class="um-field">Current password'
            + '          <input type="password" id="current-password" required autocomplete="current-password">'
            + '        </label>'
            + '        <label class="um-field" style="margin-top:10px">New password'
            + '          <input type="password" id="new-password" required autocomplete="new-password">'
            + '        </label>'
            + '        <label class="um-field" style="margin-top:10px">Confirm new password'
            + '          <input type="password" id="confirm-password" required autocomplete="new-password">'
            + '        </label>'
            + '        <div id="password-result" style="margin-top:10px"></div>'
            + '        <div class="um-row end" style="margin-top:12px">'
            + '          <button type="submit" class="um-btn primary" id="change-password">Change password</button>'
            + '        </div>'
            + '      </form>'
            + '      <hr class="um-divider">'
            + '      <div id="reset-host"></div>'
            + '    </div>'
            + '  </div>'
            + '</div>'
            + '<div class="um-card">'
            + '  <h2>Recent account activity</h2>'
            + '  <p class="um-sub">Password changes, Discord links and administrator actions affecting your account.</p>'
            + '  <div id="activity-host"><div class="um-loading"><span class="um-spinner"></span>Loading activity…</div></div>'
            + '</div>';

        renderDiscord();
        renderReset();
        $('password-form').addEventListener('submit', changePassword);
    }

    function renderDiscord() {
        var host = $('discord-host');

        if (!me.discordConfigured) {
            host.innerHTML = '<div class="um-note">The Discord bot is not configured on this server. Ask an administrator for help with your account.</div>';
            return;
        }

        if (me.discordUserId) {
            host.innerHTML = '<div class="um-note ok">Linked to <strong>@'
                + UM.escapeHtml(me.discordUsername || me.discordUserId) + '</strong>.</div>'
                + '<div class="um-row" style="margin-top:12px">'
                + '<button type="button" class="um-btn danger" id="unlink-discord">Unlink Discord</button>'
                + '</div>';
            $('unlink-discord').addEventListener('click', unlinkDiscord);
            return;
        }

        host.innerHTML = '<div class="um-note">No Discord account linked yet.</div>'
            + '<ol class="um-small" style="color:var(--um-muted);padding-left:20px;margin:12px 0">'
            + '<li>Join the community Discord server.</li>'
            + '<li>Generate a one-time code below.</li>'
            + '<li>Run <code>/link CODE</code> in Discord.</li>'
            + '</ol>'
            + '<div class="um-row"><button type="button" class="um-btn primary" id="generate-code">Generate link code</button></div>'
            + '<div id="link-code-host" style="margin-top:12px"></div>';

        $('generate-code').addEventListener('click', generateCode);
    }

    function renderReset() {
        var host = $('reset-host');
        if (!me.discordConfigured || !me.discordUserId) {
            host.innerHTML = '<div class="um-note">Link Discord to enable password reset by direct message.</div>';
            return;
        }

        host.innerHTML = '<h3 style="margin:0 0 6px">Forgot your password later?</h3>'
            + '<p class="um-sub">Send yourself a single-use reset link in Discord. It expires after 30 minutes.</p>'
            + '<div class="um-row"><button type="button" class="um-btn" id="send-reset">Send reset link to Discord</button></div>'
            + '<div id="reset-result" style="margin-top:12px"></div>';

        $('send-reset').addEventListener('click', sendReset);
    }

    async function generateCode() {
        var host = $('link-code-host');
        var button = $('generate-code');
        UM.busy(button, true, 'Generating…');
        try {
            var result = await UM.api('/account/link-code', { method: 'POST' });
            host.innerHTML = '<div class="um-note">Run <code>/link '
                + UM.escapeHtml(result.code) + '</code> in Discord.</div>'
                + '<div class="um-small um-muted" style="margin-top:6px">Code expires '
                + UM.escapeHtml(UM.formatDate(result.expiresAt)) + '.</div>'
                + '<div class="um-row" style="margin-top:8px"><button type="button" class="um-btn small" id="copy-code">Copy code</button></div>';
            $('copy-code').addEventListener('click', function () { UM.copy(result.code); });
        } catch (error) {
            host.innerHTML = '<div class="um-note error">' + UM.escapeHtml(error.message) + '</div>';
        } finally {
            UM.busy(button, false, 'Generate link code');
        }
    }

    async function unlinkDiscord() {
        if (!window.confirm('Unlink your Discord account? You will not be able to reset your password through Discord.')) {
            return;
        }

        var button = $('unlink-discord');
        UM.busy(button, true, 'Unlinking…');
        try {
            await UM.api('/account/link', { method: 'DELETE' });
            UM.toast('Discord unlinked.', 'ok');
            await load();
        } catch (error) {
            UM.toast(error.message, 'error');
            UM.busy(button, false, 'Unlink Discord');
        }
    }

    async function changePassword(event) {
        event.preventDefault();
        var resultHost = $('password-result');
        var current = $('current-password').value;
        var next = $('new-password').value;
        var confirm = $('confirm-password').value;
        resultHost.innerHTML = '';

        if (next !== confirm) {
            resultHost.innerHTML = '<div class="um-note error">The new passwords do not match.</div>';
            return;
        }

        if (next.length < 10) {
            resultHost.innerHTML = '<div class="um-note error">Use at least 10 characters.</div>';
            return;
        }

        var button = $('change-password');
        UM.busy(button, true, 'Updating…');
        try {
            await UM.api('/account/password', {
                method: 'POST',
                body: { currentPassword: current, newPassword: next }
            });
            $('password-form').reset();
            resultHost.innerHTML = '<div class="um-note ok">Password updated.</div>';
            UM.toast('Password updated.', 'ok');
            loadActivity();
        } catch (error) {
            resultHost.innerHTML = '<div class="um-note error">' + UM.escapeHtml(error.message) + '</div>';
        } finally {
            UM.busy(button, false, 'Change password');
        }
    }

    async function sendReset() {
        var host = $('reset-result');
        var button = $('send-reset');
        UM.busy(button, true, 'Sending…');
        try {
            var result = await UM.api('/account/reset', { method: 'POST' });
            host.innerHTML = '<div class="um-note ' + (result.delivered ? 'ok' : 'warn') + '">'
                + UM.escapeHtml(result.message) + '</div>'
                + (result.resetUrl
                    ? '<div style="margin-top:8px"><span class="um-code">' + UM.escapeHtml(result.resetUrl) + '</span>'
                        + '<div class="um-row" style="margin-top:8px"><button type="button" class="um-btn small" id="copy-reset">Copy reset link</button></div></div>'
                    : '');
            var copy = $('copy-reset');
            if (copy) {
                copy.addEventListener('click', function () { UM.copy(result.resetUrl); });
            }
        } catch (error) {
            host.innerHTML = '<div class="um-note error">' + UM.escapeHtml(error.message) + '</div>';
        } finally {
            UM.busy(button, false, 'Send reset link to Discord');
        }
    }

    async function loadActivity() {
        var host = $('activity-host');
        try {
            var entries = await UM.api('/account/audit?limit=25');
            if (!entries || !entries.length) {
                host.innerHTML = '<div class="um-empty">No activity recorded yet.</div>';
                return;
            }

            host.innerHTML = '<div class="um-table-wrap"><table class="um-table"><thead><tr>'
                + '<th>Time</th><th>Action</th><th>Actor</th><th>Details</th>'
                + '</tr></thead><tbody>'
                + entries.map(function (entry) {
                    return '<tr><td class="um-small" style="white-space:nowrap">'
                        + UM.escapeHtml(UM.formatDate(entry.timestamp)) + '</td>'
                        + '<td><span class="um-badge">' + UM.escapeHtml(entry.action) + '</span></td>'
                        + '<td class="um-small">' + UM.escapeHtml(entry.actorName || '—') + '</td>'
                        + '<td class="um-small">' + UM.escapeHtml(entry.details || '') + '</td></tr>';
                }).join('')
                + '</tbody></table></div>';
        } catch (error) {
            host.innerHTML = '<div class="um-note error">' + UM.escapeHtml(error.message) + '</div>';
        }
    }
})();
