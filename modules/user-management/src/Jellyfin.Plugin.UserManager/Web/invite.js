(function () {
    'use strict';

    var token = new URLSearchParams(location.hash.slice(1)).get('token') || '';
    var config = null;

    function $(id) { return document.getElementById(id); }

    UM.onReady(function () {
        var parts = (location.pathname || '').split('/').filter(function (part) { return part.length > 0; });
        var last = parts.length ? parts[parts.length - 1] : '';
        if (!token && last.toLowerCase() !== 'invite') {
            try { token = decodeURIComponent(last); } catch (e) { token = last; }
        }
        if (token) { history.replaceState(null, '', UM.serverRoot() + '/UserManager/invite'); }

        $('login-link').href = UM.loginUrl();

        UM.api('/public/config').then(function (value) {
            config = value;
            updateRequestLink();
        }).catch(function () { /* ignore */ });

        if (!token) {
            renderInvalid('This invite link is missing its token.');
            return;
        }

        loadInvite();
    });

    function updateRequestLink() {
        var link = $('request-link');
        if (config && config.inviteRequestsEnabled) {
            link.hidden = false;
            link.href = UM.serverRoot() + '/UserManager/invite-request';
        }
    }

    async function loadInvite() {
        try {
            var info = await UM.api('/public/invites/lookup', { method: 'POST', body: { token: token } });
            if (!info.valid) {
                renderInvalid(info.reason || 'This invite link is no longer valid.');
                return;
            }

            renderForm(info);
        } catch (error) {
            renderInvalid(error.message || 'This invite link is no longer valid.');
        }
    }

    function renderInvalid(reason) {
        $('invite-title').textContent = 'Invite unavailable';
        $('invite-subtitle').textContent = 'This link cannot be used';
        $('invite-main').innerHTML = '<div class="um-note error">' + UM.escapeHtml(reason) + '</div>'
            + '<p class="um-sub" style="margin-top:12px">Ask an administrator for a new invite link, or request one if the server allows it.</p>';
        updateRequestLink();
    }

    function renderForm(info) {
        var details = [];
        if (info.presetName) {
            details.push('Permissions: <strong>' + UM.escapeHtml(info.presetName) + '</strong>');
        }
        details.push(info.remainingUses === 1 ? 'Single-use link' : info.remainingUses + ' uses remaining');
        details.push('Expires ' + UM.escapeHtml(UM.formatDate(info.expiresAt)));

        $('invite-main').innerHTML = '<div class="um-note">' + details.join(' · ') + '</div>'
            + '<form id="invite-form" style="margin-top:14px">'
            + '<label class="um-field">Choose a username'
            + '<input type="text" id="invite-username" required maxlength="64" minlength="1" autocomplete="username" autofocus>'
            + '<span class="um-hint">This is the name you will use to sign in to Jellyfin and Seerr.</span>'
            + '</label>'
            + '<label class="um-field" style="margin-top:10px">Choose a password'
            + '<input type="password" id="invite-password" required autocomplete="new-password">'
            + '<span class="um-hint">At least 10 characters, with three of: lowercase, uppercase, numbers, symbols.</span>'
            + '</label>'
            + '<label class="um-field" style="margin-top:10px">Confirm password'
            + '<input type="password" id="invite-confirm" required autocomplete="new-password">'
            + '</label>'
            + '<div id="invite-error" style="margin-top:10px"></div>'
            + '<div class="um-row end" style="margin-top:14px">'
            + '<button type="submit" class="um-btn primary" id="invite-submit">Create my account</button>'
            + '</div></form>';

        $('invite-form').addEventListener('submit', register);
    }

    async function register(event) {
        event.preventDefault();
        var host = $('invite-error');
        host.innerHTML = '';

        var username = $('invite-username').value.trim();
        var password = $('invite-password').value;

        if (password !== $('invite-confirm').value) {
            host.innerHTML = '<div class="um-note error">The passwords do not match.</div>';
            return;
        }

        if (password.length < 10) {
            host.innerHTML = '<div class="um-note error">Use at least 10 characters.</div>';
            return;
        }

        var button = $('invite-submit');
        UM.busy(button, true, 'Creating account…');
        try {
            var result = await UM.api('/public/invites/register', {
                method: 'POST',
                body: { token: token, username: username, password: password }
            });

            $('invite-title').textContent = 'Welcome, ' + result.username;
            $('invite-subtitle').textContent = 'Your account is ready';
            $('invite-main').innerHTML = '<div class="um-note ok">Your Jellyfin account was created. '
                + 'Sign in with the username and password you just chose.</div>'
                + (result.seerrWarning
                    ? '<div class="um-note warn" style="margin-top:10px">Your account is ready, but Seerr could not be synced automatically. '
                        + 'An administrator can fix this.</div>'
                    : '')
                + '<div style="margin-top:14px"><a class="um-btn primary" href="'
                + UM.escapeHtml(result.loginUrl || UM.loginUrl()) + '">Continue to sign in</a></div>';
            token = '';
            $('request-link').hidden = true;
            $('login-link').hidden = true;
        } catch (error) {
            host.innerHTML = '<div class="um-note error">' + UM.escapeHtml(error.message) + '</div>';
        } finally {
            UM.busy(button, false, 'Create my account');
        }
    }
})();
