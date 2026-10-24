(function () {
    'use strict';

    var token = new URLSearchParams(location.hash.slice(1)).get('token') || UM.getQuery('token') || '';
    if (token) { history.replaceState(null, '', location.pathname); }
    var loginUrl = UM.loginUrl();

    function $(id) { return document.getElementById(id); }

    UM.onReady(function () {
        $('login-link').href = loginUrl;

        if (token) {
            validateToken();
        } else {
            renderRequestForm();
        }
    });

    function renderRequestForm(message) {
        var host = $('reset-main');
        host.innerHTML = (message ? '<div class="um-note ok">' + UM.escapeHtml(message) + '</div>' : '')
            + '<p class="um-sub">Enter your Jellyfin username. If the account exists and has Discord linked, '
            + 'we will send a single-use reset link to your Discord direct messages.</p>'
            + '<form id="request-form">'
            + '<label class="um-field">Username'
            + '<input type="text" id="username" required maxlength="64" autocomplete="username" autofocus>'
            + '</label>'
            + '<div id="request-error" style="margin-top:10px"></div>'
            + '<div class="um-row end" style="margin-top:14px">'
            + '<button type="submit" class="um-btn primary" id="request-button">Send reset link</button>'
            + '</div></form>';

        $('request-form').addEventListener('submit', requestReset);
        $('request-again').hidden = true;
    }

    async function requestReset(event) {
        event.preventDefault();
        var host = $('request-error');
        host.innerHTML = '';
        var button = $('request-button');
        UM.busy(button, true, 'Sending…');
        try {
            var result = await UM.api('/public/reset/request', {
                method: 'POST',
                body: { username: $('username').value.trim() }
            });
            renderRequestForm(result.message || 'Check your Discord direct messages.');
        } catch (error) {
            host.innerHTML = '<div class="um-note error">' + UM.escapeHtml(error.message) + '</div>';
        } finally {
            UM.busy(button, false, 'Send reset link');
        }
    }

    async function validateToken() {
        var host = $('reset-main');
        try {
            var result = await UM.api('/public/reset/validate', {
                method: 'POST',
                body: { token: token }
            });

            if (!result.valid) {
                host.innerHTML = '<div class="um-note error">This reset link is invalid or has expired. '
                    + 'Request a new link and try again.</div>';
                showRequestAgain();
                return;
            }

            renderCompleteForm(result.expiresAt);
        } catch (error) {
            host.innerHTML = '<div class="um-note error">' + UM.escapeHtml(error.message) + '</div>';
            showRequestAgain();
        }
    }

    function renderCompleteForm(expiresAt) {
        var host = $('reset-main');
        host.innerHTML = '<p class="um-sub">Choose a new password. The link is valid until '
            + UM.escapeHtml(UM.formatDate(expiresAt)) + ' and can only be used once.</p>'
            + '<form id="complete-form">'
            + '<label class="um-field">New password'
            + '<input type="password" id="new-password" required autocomplete="new-password" autofocus>'
            + '<span class="um-hint">At least 10 characters, three of: lowercase, uppercase, numbers, symbols.</span>'
            + '</label>'
            + '<label class="um-field" style="margin-top:10px">Confirm new password'
            + '<input type="password" id="confirm-password" required autocomplete="new-password">'
            + '</label>'
            + '<div id="complete-error" style="margin-top:10px"></div>'
            + '<div class="um-row end" style="margin-top:14px">'
            + '<button type="submit" class="um-btn primary" id="complete-button">Update password</button>'
            + '</div></form>';

        $('complete-form').addEventListener('submit', completeReset);
    }

    async function completeReset(event) {
        event.preventDefault();
        var host = $('complete-error');
        host.innerHTML = '';

        var password = $('new-password').value;
        if (password !== $('confirm-password').value) {
            host.innerHTML = '<div class="um-note error">The passwords do not match.</div>';
            return;
        }

        if (password.length < 10) {
            host.innerHTML = '<div class="um-note error">Use at least 10 characters.</div>';
            return;
        }

        var button = $('complete-button');
        UM.busy(button, true, 'Updating…');
        try {
            var result = await UM.api('/public/reset/complete', {
                method: 'POST',
                body: { token: token, newPassword: password }
            });
            token = '';
            $('reset-main').innerHTML = '<div class="um-note ok">' + UM.escapeHtml(result.message)
                + '</div><p class="um-sub" style="margin-top:12px">You can now sign in with your new password. '
                + 'You will not be signed in automatically.</p>'
                + '<a class="um-btn primary" href="' + UM.escapeHtml(result.loginUrl || loginUrl) + '">Go to sign in</a>';
            $('request-again').hidden = true;
        } catch (error) {
            host.innerHTML = '<div class="um-note error">' + UM.escapeHtml(error.message) + '</div>';
        } finally {
            UM.busy(button, false, 'Update password');
        }
    }

    function showRequestAgain() {
        var link = $('request-again');
        link.hidden = false;
        link.addEventListener('click', function (event) {
            event.preventDefault();
            token = '';
            renderRequestForm();
        });
    }
})();
