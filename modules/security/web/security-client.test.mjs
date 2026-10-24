import { test } from 'node:test';
import assert from 'node:assert/strict';
import { classifySecurityResponse, securityLinks, securityRequest, passwordLoginHint } from './security-client.mjs';

test('marked denials only', () => {
    for (const status of [401, 403]) assert.equal(classifySecurityResponse(status, { twoFactorRequired: true }), 'two_factor_required');
    assert.equal(classifySecurityResponse(401, {}), 'unauthorized');
    assert.equal(classifySecurityResponse(403, {}), 'forbidden');
    assert.equal(classifySecurityResponse(500, { twoFactorRequired: true }), null);
    assert.equal(classifySecurityResponse(403, { ok: false, error: { code: 'two_factor_required' } }), 'two_factor_required');
});
const api = { getUrl: path => '/base/' + path, accessToken: () => 'transient-token' };
test('deep links preserve BaseUrl and do not invent anchors', () => {
    assert.equal(securityLinks(api).setup, '/base/TwoFactorAuth/Setup');
    assert.equal(securityLinks(api).admin, '/base/web/index.html#/configurationpage?name=TwoFactorAuth');
});
test('pending callback once, no retry and proof cleared', async () => {
    let count = 0;
    globalThis.fetch = async () => { count++; return { ok: false, status: 403, json: async () => ({ twoFactorRequired: true }) }; };
    const proof = { code: '123456' };
    let paused = false;
    await assert.rejects(() => securityRequest(api, 'security/users/f61091cf-75bf-420c-9b5e-afb375d8f6de/force-logout', {
        method: 'POST', body: { stepUp: proof }, pending2fa: () => { paused = true; }
    }), /two_factor_required/);
    assert.equal(count, 1);
    assert.equal(paused, true);
    assert.equal(proof.code, undefined);
});
test('public hint never attaches Jellyfin credentials', async () => {
    globalThis.fetch = async (url, options) => {
        assert.equal(options.credentials, 'omit');
        assert.equal(options.headers, undefined);
        return { ok: true, json: async () => ({ passwordLoginDisabled: true }) };
    };
    assert.equal(await passwordLoginHint(api), true);
});
