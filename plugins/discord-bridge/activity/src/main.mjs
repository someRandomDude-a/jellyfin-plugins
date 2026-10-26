import { DiscordSDK } from '@discord/embedded-app-sdk';
import { mountActivity } from './activity.mjs';
import { installMessageBudget } from './message-budget.mjs';
const root = document.querySelector('main');
const lifetime = new AbortController();
const removeMessageBudget = installMessageBudget(window);
// All config reads use this Activity's Discord-proxied origin. No native token,
// cookies, OAuth code, guild roster or unrelated API is read by this frontend.
const mounted = mountActivity(root, {
    signal: lifetime.signal,
    createSdk: (id, options) => new DiscordSDK(id, options),
    fetchConfig: async signal => {
        const response = await fetch(new URL('config', location.href), { signal, credentials: 'omit', cache: 'no-store', redirect: 'error' });
        if (!response.ok) throw Error();
        return response.json();
    },
    location: { pathname: location.pathname, search: location.search, hash: location.hash, replaceState: (...args) => history.replaceState(...args) }
});
window.addEventListener('pagehide', () => { lifetime.abort(); root.querySelector('input').value = ''; void mounted.then(view => { view.dispose(); removeMessageBudget(); }); }, { once: true });
document.addEventListener('visibilitychange', () => { if (document.hidden) root.querySelector('input').value = ''; });
