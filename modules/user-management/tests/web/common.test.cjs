const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const source = fs.readFileSync('src/Jellyfin.Plugin.UserManager/Web/common.js', 'utf8');
function harness(servers) {
    const calls = [];
    const context = { window: {}, location: { origin: 'https://media.test', pathname: '/jellyfin/UserManager/account', href: 'https://media.test/jellyfin/UserManager/account' },
        localStorage: { getItem: () => JSON.stringify({ Servers: servers }) }, URL, URLSearchParams,
        fetch: async (url, options) => { calls.push({ url, options }); return { ok: true, status: 200, text: async () => '{}' }; } };
    vm.runInNewContext(source, context);
    return { UM: context.window.UM, calls };
}
test('saved tokens are selected only for the matching origin and BaseUrl', () => {
    const h = harness([{ ManualAddress: 'https://another.test', AccessToken: 'wrong' }, { ManualAddress: 'https://media.test/other', AccessToken: 'wrong-path' }, { ManualAddress: 'https://media.test/jellyfin/', AccessToken: 'right' }]);
    assert.equal(h.UM.token(), 'right');
    assert.equal(harness([{ ManualAddress: 'https://another.test', AccessToken: 'wrong' }]).UM.token(), null);
});
test('public token flows omit Jellyfin authorization and cookies', async () => {
    const h = harness([{ ManualAddress: 'https://media.test/jellyfin', AccessToken: 'private' }]);
    await h.UM.api('/public/invites/lookup', { method: 'POST', body: { token: 'invitation' } });
    assert.equal(h.calls[0].options.headers.Authorization, undefined);
    assert.equal(h.calls[0].options.credentials, 'omit');
    assert.equal(h.calls[0].options.cache, 'no-store');
    assert(!h.calls[0].url.includes('invitation'));
});
