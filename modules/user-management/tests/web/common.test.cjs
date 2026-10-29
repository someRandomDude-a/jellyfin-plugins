const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const source = fs.readFileSync('src/Jellyfin.Plugin.UserManager/Web/common.js', 'utf8');
function harness(servers, response) {
    const calls = [];
    const context = { window: {}, location: { origin: 'https://media.test', pathname: '/jellyfin/UserManager/account', href: 'https://media.test/jellyfin/UserManager/account' },
        localStorage: { getItem: () => JSON.stringify({ Servers: servers }) }, URL, URLSearchParams,
        fetch: async (url, options) => { calls.push({ url, options }); return response || { ok: true, status: 200, text: async () => '{}' }; } };
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
test('legacy account UI errors expose only safe categories and opaque IDs, never provider bodies', async () => {
    const id='11111111-1111-4111-8111-111111111111';
    for(const body of [JSON.stringify({error:'private-password-provider',details:'private-link',errorId:id}),'<html>private-password-provider</html>']){
        const h=harness([],{ok:false,status:403,text:async()=>body,headers:{get:()=>id}});
        await assert.rejects(h.UM.api('/account'),error=>{
            assert.match(error.message,/do not have permission/);assert.match(error.message,/Error ID:/);assert(!error.message.includes('private-'));assert.equal(error.payload,undefined);return true;
        });
    }
});
