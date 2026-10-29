import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import { webcrypto } from 'node:crypto';
import { test } from 'node:test';
import assert from 'node:assert/strict';

const require = createRequire(new URL('../plugins/private-syncplay/package.json', import.meta.url));
const { JSDOM } = require('jsdom');
const code = await readFile(new URL('../plugins/account-hub/src/AccountHub/Web/invite-public.js', import.meta.url), 'utf8');
const token = 'a'.repeat(43), prefix = 'account-hub-invite-claim:';
const turn = () => new Promise(resolve => setImmediate(resolve));
async function waitFor(predicate) { for (let i = 0; i < 200; i++) { if (predicate()) return; await turn(); } assert.fail('UI did not settle'); }
async function page({ entries = {}, lookup = { valid: true }, submit, storageFails = false, nativeSession=false } = {}) {
    const dom = new JSDOM('<base href="https://example.test/suite/AccountHub/"><p id="invite-status"></p><form id="invite-form" hidden><input id="invite-username"><input id="invite-password" type="password"><input id="invite-confirm" type="password"><button>Create account</button></form>', { runScripts: 'outside-only', url: 'https://example.test/suite/AccountHub/invite#token=' + token });
    const w = dom.window, calls = [];
    Object.defineProperty(w.crypto, 'subtle', { value: webcrypto.subtle }); w.TextEncoder = TextEncoder;
    for (const [key, value] of Object.entries(entries)) w.sessionStorage.setItem(key, value);
    if (storageFails) Object.defineProperty(w, 'sessionStorage', { get() { throw Error('blocked'); } });
    w.localStorage.setItem('jellyfin_credentials', 'must-not-send');
    if(nativeSession)w.localStorage.setItem('jellyfin_credentials',JSON.stringify({Servers:[{ManualAddress:'https://example.test/suite',AccessToken:'native-session-only'}]}));
    w.fetch = async (url, options) => {
        assert.equal(w.location.hash, '');
        calls.push({ url: String(url), options });
        if(String(url).endsWith('/api/v1/self'))return{ok:true,json:async()=>({ok:true,data:{userId:'11111111-1111-4111-8111-111111111111'}})};
        if (String(url).includes('public-config')) return { ok: false };
        const result = String(url).endsWith('/lookup') ? { data: lookup } : await submit?.(String(url), JSON.parse(options.body));
        return { ok: result?.ok !== false, status: result?.status || 200, json: async () => ({ ok: result?.ok !== false, ...result }) };
    };
    w.eval(code);
    await waitFor(() => !w.document.querySelector('form').hidden || /invalid|expired|already signed in/i.test(w.document.querySelector('p').textContent));
    const send = async () => {
        w.document.getElementById('invite-username').value = 'private-alice';
        for (const id of ['invite-password', 'invite-confirm']) w.document.getElementById(id).value = 'private-password';
        w.document.querySelector('form').dispatchEvent(new w.Event('submit', { cancelable: true }));
        await waitFor(() => !w.document.querySelector('button').disabled);
    };
    return { dom, w, calls, send };
}
test('Invite erases fragment, omits credentials, stores only fingerprint correlation, and clears passwords on success', async () => {
    const p = await page({ submit: async () => ({ data: { status: 'completed', message: 'Done' } }) });
    try {
        const keys = Object.keys(p.w.sessionStorage); assert.equal(keys.length, 1); assert.match(keys[0], /^account-hub-invite-claim:[a-f0-9]{64}$/);
        const entry = JSON.parse(p.w.sessionStorage.getItem(keys[0])); assert.deepEqual(Object.keys(entry).sort(), ['expiresAt', 'id']);
        assert(!JSON.stringify(keys).includes(token));
        await p.send();
        for (const call of p.calls) {
            assert.equal(call.options.credentials, 'omit'); assert.equal(call.options.referrerPolicy, 'no-referrer'); assert.equal(call.options.cache, 'no-store');
            assert(!call.url.includes(token)); assert(!JSON.stringify(call.options).includes('must-not-send'));
        }
        assert.equal(p.w.document.getElementById('invite-password').value, ''); assert.equal(p.w.document.getElementById('invite-confirm').value, '');
        assert.equal(p.w.sessionStorage.length, 0); assert(p.w.document.querySelector('form').hidden);
        assert.match(p.w.document.querySelector('#invite-status a').href,/\/suite\/web\/index.html#\/login$/);assert(!p.w.document.querySelector('#invite-status a').href.includes('TwoFactorAuth'));
    } finally { p.dom.window.close(); }
});
test('an already signed-in native account is checked server-side and does not consume the invitation',async()=>{
    const p=await page({nativeSession:true});try{assert(p.w.document.querySelector('form').hidden);assert.match(p.w.document.getElementById('invite-status').textContent,/invitation has not been used/);assert.equal(p.calls.length,1);assert(p.calls[0].url.endsWith('/api/v1/self'));assert(p.calls[0].options.headers.Authorization.includes('native-session-only'));assert.equal(p.w.sessionStorage.length,0);assert(!p.calls.some(call=>call.url.includes('/invites/')));}finally{p.w.close();}
});
test('known username validation failures are actionable but do not expose raw backend messages',async()=>{
    const p=await page({submit:async()=>({ok:false,status:400,error:{code:'invalid_username',message:'private provider detail'}})});try{await p.send();assert.match(p.w.document.getElementById('invite-status').textContent,/Invalid or unavailable username/);assert(!p.w.document.body.textContent.includes('private provider detail'));assert.equal(p.calls.filter(call=>call.url.endsWith('/redeem')).length,1);}finally{p.w.close();}
});
test('Reopening original invite restores matching claim and requires explicit resume after uncertainty', async () => {
    const first = await page({ submit: async () => { throw Error('lost response'); } });
    await first.send();
    const original = JSON.parse(first.calls.find(c => c.url.endsWith('/lookup')).options.body).claimId;
    const entries = Object.fromEntries(Object.keys(first.w.sessionStorage).map(k => [k, first.w.sessionStorage.getItem(k)])); first.dom.window.close();
    const next = await page({ entries, lookup: { valid: true, resumable: true }, submit: async () => ({ data: { status: 'completed', message: 'Done' } }) });
    try {
        assert.equal(JSON.parse(next.calls.find(c => c.url.endsWith('/lookup')).options.body).claimId, original);
        assert.equal(next.calls.length, 2); assert.match(next.w.document.querySelector('button').textContent, /Explicitly resume/);
        await next.send(); const sent = next.calls.find(c => c.url.endsWith('/resume')); assert(sent); assert.equal(JSON.parse(sent.options.body).claimId, original);
    } finally { next.dom.window.close(); }
});
test('Correlation storage prunes invalid/expired entries and stays bounded without touching other storage', async () => {
    const entries = { unrelated: 'keep', [prefix + 'bad']: '{}', [prefix + 'expired']: JSON.stringify({ id: webcrypto.randomUUID(), expiresAt: 1 }) };
    for (let i = 0; i < 25; i++) entries[prefix + i] = JSON.stringify({ id: webcrypto.randomUUID(), expiresAt: Date.now() + 60000 });
    const p = await page({ entries });
    try {
        assert.equal(Object.keys(p.w.sessionStorage).filter(k => k.startsWith(prefix)).length, 20);
        assert.equal(p.w.sessionStorage.getItem('unrelated'), 'keep'); assert.equal(p.w.sessionStorage.getItem(prefix + 'bad'), null);
        assert(!JSON.stringify(Object.values(p.w.sessionStorage)).includes(token));
    } finally { p.dom.window.close(); }
});
test('Storage denial and rejected redemption erase passwords without automatic retry', async () => {
    const p = await page({ storageFails: true, submit: async () => ({ ok: false, status: 400, error: { code: 'invalid_request' } }) });
    try {
        await p.send(); assert.equal(p.calls.filter(c => c.url.endsWith('/redeem')).length, 1);
        assert.equal(p.w.document.getElementById('invite-password').value, ''); assert.equal(p.w.document.getElementById('invite-confirm').value, '');
        assert.equal(p.w.document.querySelector('button').textContent, 'Create account');
        p.w.dispatchEvent(new p.w.Event('pagehide')); await p.send(); assert.equal(p.calls.length, 3);
    } finally { p.dom.window.close(); }
});
