import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
const { JSDOM } = createRequire(new URL('../plugins/private-syncplay/package.json', import.meta.url))('jsdom');
const turn = () => new Promise(resolve => setImmediate(resolve));
const id = '11111111-1111-1111-1111-111111111111', missing = '22222222-2222-2222-2222-222222222222';
async function fixture() {
    const html = await readFile(new URL('../plugins/discord-bridge/src/Jellyfin.Plugin.DiscordBridge/Web/settings.html', import.meta.url), 'utf8');
    const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'https://fixture.test/web/' });
    const w = dom.window, calls = [];
    const data = { revision: 4, settings: { enabled: false, publicServerUrl: '', guildId: '', allowedChannelIds: [], permissions: { defaults: { reset: true }, users: { [missing]: { reset: false } } } },
        permissionActions: [{ id: 'reset', name: 'Recovery', description: 'Native verification remains required', defaultAllowed: true }], permissionUsers: [{ id, name: '<script>not HTML</script>' }], features: [], libraryFolders: [] };
    w.ApiClient = { getUrl: path => path, accessToken: () => 'fixture-token' };
    w.fetch = async (_, request) => { if (request.method === 'PUT') calls.push(JSON.parse(request.body)); return { ok: true, json: async () => ({ ok: true, data: structuredClone(data) }) }; };
    w.eval([...html.matchAll(/<script>([\s\S]*?)<\/script>/g)][0][1]); await turn(); await turn();
    return { dom, w, d: w.document, calls };
}
test('Discord permission editor uses text-only users and preserves unavailable overrides on save', async () => {
    const { dom, w, d, calls } = await fixture();
    try {
        assert(d.querySelector('.bridgePermissionUser').textContent.includes('<script>not HTML</script>'));
        assert(d.querySelector('.bridgePermissionUser').textContent.includes('Unavailable saved user'));
        const users = d.querySelector('.bridgePermissionUser'); users.value = id; users.dispatchEvent(new w.Event('change'));
        let select = d.querySelector('[data-permission=reset]'); assert.equal(select.value, 'inherit'); assert(select.textContent.includes('Inherit (Allow)'));
        select.value = 'deny'; select.dispatchEvent(new w.Event('change'));
        d.querySelector('form').dispatchEvent(new w.Event('submit', { cancelable: true })); await turn(); await turn();
        assert.equal(calls[0].revision, 4); assert.equal(calls[0].settings.permissions.users[id].reset, false);
        assert.equal(calls[0].settings.permissions.users[missing].reset, false);
    } finally { dom.window.close(); }
});
test('Discord permission reset is draft-only, discard restores it and navigation clears private user lists', async () => {
    const { dom, w, d, calls } = await fixture();
    try {
        const users = d.querySelector('.bridgePermissionUser'); users.value = missing; users.dispatchEvent(new w.Event('change'));
        assert.equal(d.querySelector('[data-permission=reset]').value, 'deny');
        d.querySelector('.bridgePermissionReset').click(); assert.equal(d.querySelector('[data-permission=reset]').value, 'inherit'); assert.equal(calls.length, 0);
        d.querySelector('.bridgePermissionDiscard').click(); assert.equal(d.querySelector('[data-permission=reset]').value, 'deny');
        d.querySelector('.discordBridgePage').dispatchEvent(new w.Event('viewhide'));
        assert.equal(users.options.length, 0); assert.equal(d.querySelector('.bridgePermissionRows').children.length, 0);
    } finally { dom.window.close(); }
});
test('late Discord settings save cannot overwrite a new view or erase its secret draft', async () => {
    const { dom, w, d } = await fixture();
    try {
        const original = w.fetch; let finish;
        w.fetch = (url, request) => request.method === 'PUT' ? new Promise(resolve => { finish = () => resolve({ ok: true, json: async () => ({ ok: true, data: { revision: 5 } }) }); }) : original(url, request);
        d.querySelector('form').dispatchEvent(new w.Event('submit', { cancelable: true }));
        const root = d.querySelector('.discordBridgePage'); root.dispatchEvent(new w.Event('viewhide')); root.dispatchEvent(new w.Event('viewshow')); await turn(); await turn();
        d.querySelector('.bridgeToken').value = 'new-view-draft'; finish(); await turn(); await turn();
        assert.equal(d.querySelector('.bridgeToken').value, 'new-view-draft');
        assert(!d.querySelector('.bridgeStatus').textContent.includes('Settings saved'));
    } finally { dom.window.close(); }
});
