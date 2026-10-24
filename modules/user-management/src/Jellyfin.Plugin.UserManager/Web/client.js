(function () {
    'use strict';

    if (window.__jellyfinUserManagerInjected) {
        return;
    }
    window.__jellyfinUserManagerInjected = true;

    function serverRoot() {
        try {
            if (window.ApiClient && typeof window.ApiClient.serverAddress === 'function') {
                return window.ApiClient.serverAddress().replace(/\/+$/, '');
            }
        } catch (e) { /* ignore */ }

        var path = new URL(document.baseURI || location.href).pathname;
        var index = path.toLowerCase().indexOf('/web/');
        if (index >= 0) {
            path = path.substring(0, index);
        }
        return location.origin + path.replace(/\/+$/, '');
    }

    function token() {
        try {
            if (window.ApiClient && typeof window.ApiClient.accessToken === 'function') {
                var direct = window.ApiClient.accessToken();
                if (direct) { return direct; }
            }
        } catch (e) { /* ignore */ }

        try {
            if (window.parent && window.parent !== window && window.parent.ApiClient
                && typeof window.parent.ApiClient.accessToken === 'function') {
                var parentToken = window.parent.ApiClient.accessToken();
                if (parentToken) { return parentToken; }
            }
        } catch (e) { /* ignore */ }

        try {
            var raw = localStorage.getItem('jellyfin_credentials');
            if (!raw) { return null; }
            var data = JSON.parse(raw);
            var servers = data.Servers || data.servers || [];
            for (var i = 0; i < servers.length; i++) {
                var s = servers[i];
                var t = s.AccessToken || s.accessToken;
                if (t) { return t; }
            }
        } catch (e) { /* ignore */ }

        return null;
    }

    async function api(path, options) {
        var headers = Object.assign({ 'Accept': 'application/json' }, (options && options.headers) || {});
        var t = token();
        if (t) {
            headers['X-Emby-Token'] = t;
            headers['Authorization'] = 'MediaBrowser Token="' + t + '"';
        }
        if (options && options.body) {
            headers['Content-Type'] = 'application/json';
        }
        var response = await fetch(serverRoot() + '/UserManager/api' + path, Object.assign({}, options, {
            headers: headers,
            credentials: 'same-origin'
        }));
        if (!response.ok) {
            var message = 'Request failed (' + response.status + ')';
            try {
                var body = await response.json();
                if (body && body.error) { message = body.error; }
            } catch (e) { /* ignore */ }
            throw new Error(message);
        }
        if (response.status === 204) { return null; }
        return response.json();
    }

    function isLoginRoute() {
        return location.hash.indexOf('/login') >= 0 || location.pathname.indexOf('/login') >= 0;
    }

    function addStyles() {
        if (document.getElementById('jfm-style')) { return; }
        var style = document.createElement('style');
        style.id = 'jfm-style';
        style.textContent = [
            '#jfm-pill{position:fixed;right:18px;bottom:18px;z-index:2147483000;display:flex;gap:6px;align-items:center;padding:9px 14px;border-radius:999px;border:1px solid rgba(255,255,255,.16);background:rgba(16,18,24,.92);color:#fff;font:600 13px/1 system-ui,-apple-system,Segoe UI,sans-serif;box-shadow:0 8px 30px rgba(0,0,0,.35);cursor:pointer;backdrop-filter:blur(12px)}',
            '#jfm-pill:hover{background:rgba(42,46,60,.96)}',
            '#jfm-panel{position:fixed;right:18px;bottom:70px;z-index:2147483000;width:230px;padding:10px;border-radius:14px;border:1px solid rgba(255,255,255,.14);background:rgba(16,18,24,.98);color:#fff;box-shadow:0 18px 50px rgba(0,0,0,.45);display:none;font:14px/1.4 system-ui,-apple-system,Segoe UI,sans-serif}',
            '#jfm-panel.open{display:block}',
            '#jfm-panel a{display:block;padding:9px 10px;border-radius:9px;color:#e8eaf2;text-decoration:none}',
            '#jfm-panel a:hover{background:rgba(255,255,255,.09)}',
            '#jfm-login-links{margin-top:14px;text-align:center;font:13px/1.6 system-ui,-apple-system,Segoe UI,sans-serif;color:#9aa3b2}',
            '#jfm-login-links a{color:#7fb3ff;text-decoration:none;display:inline-block;margin:2px 8px}',
            '#jfm-login-links a:hover{text-decoration:underline}'
        ].join('');
        document.head.appendChild(style);
    }

    var me = null;
    var publicConfig = null;

    function buildPill() {
        if (document.getElementById('jfm-pill') || isLoginRoute()) { return; }
        if (!token()) { return; }

        var pill = document.createElement('button');
        pill.id = 'jfm-pill';
        pill.type = 'button';
        pill.textContent = 'Account';
        pill.setAttribute('aria-label', 'Jellyfin User Manager account menu');

        var panel = document.createElement('div');
        panel.id = 'jfm-panel';

        var account = document.createElement('a');
        account.href = serverRoot() + '/UserManager/account';
        account.textContent = 'Account & Discord';
        panel.appendChild(account);

        var reset = document.createElement('a');
        reset.href = serverRoot() + '/UserManager/reset';
        reset.textContent = 'Reset password';
        panel.appendChild(reset);

        pill.addEventListener('click', function (event) {
            event.stopPropagation();
            panel.classList.toggle('open');
        });

        document.addEventListener('click', function () {
            panel.classList.remove('open');
        });

        document.body.appendChild(panel);
        document.body.appendChild(pill);

        api('/me').then(function (result) {
            me = result;
            if (result && result.isAdministrator) {
                var admin = document.createElement('a');
                admin.href = serverRoot() + '/web/ConfigurationPage?name=usermanager';
                admin.textContent = 'User Manager admin';
                panel.insertBefore(admin, panel.firstChild);
            }
        }).catch(function () {
            pill.remove();
            panel.remove();
        });
    }

    function buildLoginLinks() {
        if (!isLoginRoute()) { return; }
        if (document.getElementById('jfm-login-links')) { return; }

        var host = document.querySelector('#loginPage .manualLoginForm')
            || document.querySelector('#loginPage')
            || document.querySelector('.manualLoginForm');
        if (!host) { return; }

        var wrapper = document.createElement('div');
        wrapper.id = 'jfm-login-links';

        var reset = document.createElement('a');
        reset.href = serverRoot() + '/UserManager/reset';
        reset.textContent = 'Forgot password? Reset with Discord';
        wrapper.appendChild(reset);

        if (publicConfig && publicConfig.inviteRequestsEnabled) {
            var request = document.createElement('a');
            request.href = serverRoot() + '/UserManager/invite-request';
            request.textContent = 'Request an invite';
            wrapper.appendChild(request);
        }

        host.appendChild(wrapper);
    }

    function refresh() {
        buildPill();
        buildLoginLinks();
    }

    function debounce(fn, delay) {
        var timer = null;
        return function () {
            clearTimeout(timer);
            timer = setTimeout(fn, delay);
        };
    }

    document.addEventListener('DOMContentLoaded', function () {
        addStyles();
        refresh();

        api('/public/config').then(function (config) {
            publicConfig = config;
            buildLoginLinks();
        }).catch(function () { /* ignore */ });

        var observer = new MutationObserver(debounce(refresh, 120));
        observer.observe(document.body, { childList: true, subtree: true });
        window.addEventListener('hashchange', debounce(refresh, 60));

        setInterval(function () {
            if (!document.getElementById('jfm-pill') && !isLoginRoute()) {
                buildPill();
            }
        }, 4000);
    });
})();
