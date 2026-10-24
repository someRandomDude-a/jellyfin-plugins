(function () {
    'use strict';

    function $(id) { return document.getElementById(id); }

    UM.onReady(function () {
        $('login-link').href = UM.loginUrl();

        UM.api('/public/config').then(function (config) {
            if (config.serverName) {
                $('server-name').textContent = config.serverName;
            }

            if (!config.inviteRequestsEnabled) {
                $('request-main').innerHTML = '<div class="um-note warn">Invite requests are currently closed on this server. '
                    + 'Ask an administrator for an invite link.</div>';
                return;
            }

            renderForm();
        }).catch(function (error) {
            $('request-main').innerHTML = '<div class="um-note error">' + UM.escapeHtml(error.message) + '</div>';
        });
    });

    function renderForm() {
        $('request-main').innerHTML = '<p class="um-sub">Tell the administrators who you are and how to reach you. '
            + 'If approved, you will receive an invite link to create your own account.</p>'
            + '<form id="request-form">'
            + '<label class="um-field">Your name'
            + '<input type="text" id="name" required maxlength="64" autocomplete="name">'
            + '</label>'
            + '<label class="um-field" style="margin-top:10px">Contact'
            + '<input type="text" id="contact" required maxlength="128" placeholder="Email, Discord username or phone">'
            + '</label>'
            + '<label class="um-field" style="margin-top:10px">Message (optional)'
            + '<textarea id="message" maxlength="500" placeholder="Anything the administrators should know"></textarea>'
            + '</label>'
            + '<div id="request-error" style="margin-top:10px"></div>'
            + '<div class="um-row end" style="margin-top:14px">'
            + '<button type="submit" class="um-btn primary" id="request-submit">Send request</button>'
            + '</div></form>';

        $('request-form').addEventListener('submit', submit);
    }

    async function submit(event) {
        event.preventDefault();
        var host = $('request-error');
        host.innerHTML = '';
        var button = $('request-submit');
        UM.busy(button, true, 'Sending…');
        try {
            var result = await UM.api('/public/invite-request', {
                method: 'POST',
                body: {
                    name: $('name').value.trim(),
                    contact: $('contact').value.trim(),
                    message: $('message').value.trim() || null
                }
            });
            $('request-main').innerHTML = '<div class="um-note ok">' + UM.escapeHtml(result.message)
                + '</div><p class="um-sub" style="margin-top:12px">Keep an eye on the contact method you provided.</p>';
        } catch (error) {
            host.innerHTML = '<div class="um-note error">' + UM.escapeHtml(error.message) + '</div>';
        } finally {
            UM.busy(button, false, 'Send request');
        }
    }
})();
