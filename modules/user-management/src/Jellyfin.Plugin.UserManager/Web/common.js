(function () {
    'use strict';

    function serverRoot() {
        if (window.ApiClient && typeof window.ApiClient.getUrl === 'function') {
            var active = new URL(window.ApiClient.getUrl(''), location.href);
            if (active.origin === location.origin) { return active.href.replace(/\/+$/, ''); }
        }
        var path = location.pathname || '/';
        var index = path.toLowerCase().indexOf('/usermanager');
        if (index >= 0) {
            path = path.substring(0, index);
        }
        path = path.replace(/\/+$/, '');
        return location.origin + path;
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
                var value = servers[i].AccessToken || servers[i].accessToken;
                var server = servers[i];
                var addresses = [server.ManualAddress, server.LocalAddress, server.RemoteAddress,
                    server.manualAddress, server.localAddress, server.remoteAddress];
                if (value && addresses.some(function (address) {
                    try { return new URL(address).href.replace(/\/+$/, '') === serverRoot(); }
                    catch (e) { return false; }
                })) { return value; }
            }
        } catch (e) { /* ignore */ }

        return null;
    }

    function tokenHeaders() {
        var headers = { 'Accept': 'application/json' };
        var current = token();
        if (current) {
            headers['X-Emby-Token'] = current;
            headers['Authorization'] = 'MediaBrowser Token="' + current + '"';
        }
        return headers;
    }

    async function api(path, options) {
        options = options || {};
        var anonymous = path.indexOf('/public/') === 0 || path.indexOf('/invite/') === 0;
        var headers = Object.assign(anonymous ? { Accept: 'application/json' } : tokenHeaders(), options.headers || {});
        var init = {
            method: options.method || 'GET',
            headers: headers,
            credentials: 'omit',
            cache: 'no-store'
        };

        if (options.body !== undefined && options.body !== null) {
            if (typeof FormData !== 'undefined' && options.body instanceof FormData) {
                init.body = options.body;
                delete headers['Content-Type'];
            } else {
                headers['Content-Type'] = 'application/json';
                init.body = typeof options.body === 'string' ? options.body : JSON.stringify(options.body);
            }
        }

        var response;
        try {
            response = await fetch(serverRoot() + '/UserManager/api' + path, init);
        } catch (networkError) {
            var offline = new Error('Could not reach the server. Check your connection and try again.');
            offline.status = 0;
            throw offline;
        }

        if (response.status === 204) {
            return null;
        }

        var payload = null;
        var text = await response.text();
        if (text) {
            try {
                payload = JSON.parse(text);
            } catch (e) {
                payload = text;
            }
        }

        if (!response.ok) {
            var message = 'Request failed (' + response.status + ').';
            if (payload && typeof payload === 'object' && (payload.error || payload.title)) {
                message = payload.error || payload.title;
            } else if (typeof payload === 'string' && payload.length < 400) {
                message = payload;
            }

            var error = new Error(message);
            error.status = response.status;
            error.payload = payload;
            throw error;
        }

        return payload;
    }

    function escapeHtml(value) {
        if (value === null || value === undefined) { return ''; }
        return String(value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function formatDate(value, fallback) {
        if (value === null || value === undefined || value === '') {
            return fallback || '—';
        }
        var date = value instanceof Date ? value : new Date(value);
        if (isNaN(date.getTime())) {
            return fallback || '—';
        }
        return date.toLocaleString(undefined, {
            year: 'numeric',
            month: 'short',
            day: '2-digit',
            hour: '2-digit',
            minute: '2-digit'
        });
    }

    function timeAgo(value) {
        if (!value) { return '—'; }
        var date = value instanceof Date ? value : new Date(value);
        if (isNaN(date.getTime())) { return '—'; }
        var seconds = Math.round((date.getTime() - Date.now()) / 1000);
        var absolute = Math.abs(seconds);
        var suffix = seconds <= 0 ? ' ago' : '';
        var prefix = seconds > 0 ? 'in ' : '';

        if (absolute < 45) { return seconds <= 0 ? 'just now' : 'in a moment'; }
        if (absolute < 3600) { return prefix + Math.round(absolute / 60) + ' min' + suffix; }
        if (absolute < 86400) { return prefix + Math.round(absolute / 3600) + ' h' + suffix; }
        if (absolute < 2592000) { return prefix + Math.round(absolute / 86400) + ' d' + suffix; }
        return formatDate(date);
    }

    function toast(message, kind) {
        var host = document.getElementById('um-toasts');
        if (!host) {
            host = document.createElement('div');
            host.id = 'um-toasts';
            host.className = 'um-toasts';
            host.setAttribute('role', 'status');
            host.setAttribute('aria-live', 'polite');
            document.body.appendChild(host);
        }

        var item = document.createElement('div');
        item.className = 'um-toast ' + (kind || '');
        item.textContent = message;
        host.appendChild(item);

        setTimeout(function () {
            item.style.opacity = '0';
            item.style.transform = 'translateY(6px)';
            item.style.transition = 'opacity .2s ease, transform .2s ease';
            setTimeout(function () { item.remove(); }, 220);
        }, kind === 'error' ? 7000 : 4200);
    }

    async function copyText(text) {
        try {
            if (navigator.clipboard && window.isSecureContext) {
                await navigator.clipboard.writeText(text);
                toast('Copied to clipboard.', 'ok');
                return true;
            }
        } catch (e) { /* fall through */ }

        var area = document.createElement('textarea');
        area.value = text;
        area.style.position = 'fixed';
        area.style.opacity = '0';
        document.body.appendChild(area);
        area.select();
        try {
            document.execCommand('copy');
            toast('Copied to clipboard.', 'ok');
            return true;
        } catch (e) {
            toast('Could not copy automatically. Select the text to copy it.', 'warn');
            return false;
        } finally {
            area.remove();
        }
    }

    function loginUrl() {
        return serverRoot() + '/web/index.html#/login';
    }

    function showAuthRequired(host) {
        if (!host) { return; }
        host.innerHTML = '<div class="um-card um-auth">'
            + '<h2>Sign in required</h2>'
            + '<p class="um-sub">This page needs an active Jellyfin session. Sign in, then reopen it from the account menu.</p>'
            + '<a class="um-btn primary" href="' + loginUrl() + '">Go to sign in</a>'
            + '</div>';
    }

    function showError(host, error, retry) {
        if (!host) { return; }
        var message = error && error.message ? error.message : String(error || 'Something went wrong.');
        host.innerHTML = '<div class="um-note error">' + escapeHtml(message) + '</div>'
            + '<div class="um-row end" style="margin-top:12px">'
            + '<button type="button" class="um-btn" data-retry>Retry</button>'
            + '</div>';
        var button = host.querySelector('[data-retry]');
        if (button && retry) {
            button.addEventListener('click', retry);
        }
    }

    function isAuthError(error) {
        return error && error.status === 401;
    }

    function getQuery(name) {
        return new URLSearchParams(location.search).get(name);
    }

    function busy(button, active, label) {
        if (!button) { return; }
        if (active) {
            button.dataset.originalLabel = button.textContent;
            button.disabled = true;
            button.innerHTML = '<span class="um-spinner"></span>' + escapeHtml(label || 'Working…');
        } else {
            button.disabled = false;
            button.textContent = button.dataset.originalLabel || label || 'Done';
            delete button.dataset.originalLabel;
        }
    }

    function debounce(fn, delay) {
        var timer = null;
        return function () {
            var context = this;
            var args = arguments;
            clearTimeout(timer);
            timer = setTimeout(function () { fn.apply(context, args); }, delay);
        };
    }

    function onReady(fn) {
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', fn);
        } else {
            fn();
        }
    }

    function el(tag, attributes, children) {
        var node = document.createElement(tag);
        Object.keys(attributes || {}).forEach(function (key) {
            if (key === 'class') { node.className = attributes[key]; }
            else if (key === 'text') { node.textContent = attributes[key]; }
            else if (key === 'html') { node.innerHTML = attributes[key]; }
            else if (key.indexOf('on') === 0 && typeof attributes[key] === 'function') {
                node.addEventListener(key.substring(2).toLowerCase(), attributes[key]);
            } else if (attributes[key] !== null && attributes[key] !== undefined) {
                node.setAttribute(key, attributes[key]);
            }
        });
        (children || []).forEach(function (child) {
            if (child === null || child === undefined) { return; }
            node.appendChild(typeof child === 'string' ? document.createTextNode(child) : child);
        });
        return node;
    }

    window.UM = {
        serverRoot: serverRoot,
        token: token,
        api: api,
        escapeHtml: escapeHtml,
        formatDate: formatDate,
        timeAgo: timeAgo,
        toast: toast,
        copy: copyText,
        loginUrl: loginUrl,
        showAuthRequired: showAuthRequired,
        showError: showError,
        isAuthError: isAuthError,
        getQuery: getQuery,
        busy: busy,
        debounce: debounce,
        onReady: onReady,
        el: el
    };
})();
