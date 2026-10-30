import test from 'node:test';
import assert from 'node:assert/strict';
import { verifyDiscordTransportSettings } from './discord-settings-acceptance.mjs';

const administratorId = '11111111-1111-4111-8111-111111111111';
function fixture({ requireProof = false, allowMember = false, mutateOnDenial = false, acceptStale = false } = {}) {
    const state = { revision: 4, settings: { enabled: false, permissions: { defaults: { reset: false }, users: {} } }, writes: [], enrolled: false };
    const enrollment = { secretKey: 'fixture-secret', manualEntryKey: 'fixture-key', qrCodeBase64: 'fixture-qr' };
    const member = { accessToken: 'fixture-member' }, key = { appName: 'DiscordTransportAuthorization', accessToken: 'fixture-api-key' };
    async function request(path, { method = 'GET', body, auth = { token: 'fixture-admin' }, expected = 200 } = {}) {
        let status = 200, data;
        if (path === 'Users/New') data = { name: body.Name, policy: { isAdministrator: false } };
        else if (path === 'Users/AuthenticateByName') data = member;
        else if (path.startsWith('Auth/Keys?')) status = 204;
        else if (path === 'Auth/Keys') data = { items: [key] };
        else if (path === 'TwoFactorAuth/Setup/Totp') data = enrollment;
        else if (path === 'TwoFactorAuth/Setup/Totp/Confirm') { assert.equal(body.Code, 'fixture-code'); state.enrolled = true; }
        else if (path === 'TwoFactorAuth/MyStatus') data = { userId: administratorId, totpEnabled: state.enrolled };
        else if (path === 'DiscordBridge/admin/v1/settings') {
            if (method === 'GET') data = { data: structuredClone({ revision: state.revision, settings: state.settings }) };
            else if (auth?.token !== 'fixture-admin' && !(allowMember && auth?.token === 'fixture-member')) {
                status = 403; if (mutateOnDenial) state.revision++;
            } else if (requireProof) status = 403;
            else if (!acceptStale && body.revision !== state.revision) status = 409;
            else { state.writes.push(structuredClone(body)); state.settings = structuredClone(body.settings); state.revision++; }
        } else throw Error('Unexpected fixture request: ' + path);
        assert([expected].flat().includes(status), 'Fixture HTTP expected ' + expected + ', got ' + status);
        return { status, data };
    }
    return { state, enrollment, request, run: () => verifyDiscordTransportSettings({ request, administratorId, totp: () => 'fixture-code' }) };
}
test('enrolled administrator transport saves need no fresh proof; denied/stale writes do not change state', async () => {
    const f = fixture(); await f.run();
    assert.equal(f.state.enrolled, true); assert.equal(f.state.revision, 6);
    assert.equal(f.state.writes.length, 2);
    assert.equal(f.state.writes[0].stepUp, undefined); assert.equal(f.state.writes[0].confirmation, undefined);
    assert.equal(f.state.writes[1].stepUp.code, 'invalid');
    assert.deepEqual(f.enrollment, { secretKey: null, manualEntryKey: null, qrCodeBase64: null });
});
test('transport acceptance fails if an unintended fresh-proof requirement is introduced', async () => {
    const f = fixture({ requireProof: true });
    await assert.rejects(f.run(), /expected 200, got 403/);
    assert.equal(f.enrollment.secretKey, null);
});
test('transport acceptance still rejects ordinary-user write authorization regressions', async () => {
    await assert.rejects(fixture({ allowMember: true }).run(), /expected 401,403, got 200/);
});
test('a denied status is insufficient when the server still changes settings state', async () => {
    await assert.rejects(fixture({ mutateOnDenial: true }).run(), /Denied writes must not advance/);
});
test('transport acceptance rejects stale-revision writes rather than treating them as proof replay', async () => {
    await assert.rejects(fixture({ acceptStale: true }).run(), /expected 409, got 200/);
});
