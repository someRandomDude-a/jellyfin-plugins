import assert from 'node:assert/strict';

// Owned disposable fixture only. Transport configuration is operational; native
// elevated administrator authorization, not a fresh factor, protects the write.
export async function verifyDiscordTransportSettings({ request, totp, administratorId }) {
    const path = 'DiscordBridge/admin/v1/settings';
    let enrollment, member, key;
    try {
        const ordinary = (await request('Users/New', { method: 'POST', body: { Name: 'discord-transport-member' } })).data;
        assert.equal(ordinary.policy.isAdministrator, false);
        member = (await request('Users/AuthenticateByName', { method: 'POST', auth: { device: 'discord-transport-member' }, body: { Username: ordinary.name, Pw: '' } })).data;
        await request('Auth/Keys?app=DiscordTransportAuthorization', { method: 'POST', expected: 204 });
        key = (await request('Auth/Keys')).data.items.find(item => item.appName === 'DiscordTransportAuthorization');
        assert(key?.accessToken, 'Disposable API key must exist');

        enrollment = (await request('TwoFactorAuth/Setup/Totp', { method: 'POST', body: {} })).data;
        await request('TwoFactorAuth/Setup/Totp/Confirm', { method: 'POST', body: { Code: totp(enrollment.secretKey) } });
        const status = (await request('TwoFactorAuth/MyStatus')).data;
        assert.equal(status.userId.replaceAll('-', ''), administratorId.replaceAll('-', ''));
        assert.equal(status.totpEnabled, true, 'This check requires a genuinely enrolled administrator');

        const before = (await request(path)).data.data;
        const command = { revision: before.revision, settings: before.settings };
        for (const auth of [null, { token: member.accessToken }, { token: key.accessToken }])
            await request(path, { method: 'PUT', auth, body: command, expected: [401, 403] });
        const denied = (await request(path)).data.data;
        assert.equal(denied.revision, before.revision, 'Denied writes must not advance the settings revision');
        assert.deepEqual(denied.settings, before.settings, 'Denied writes must not change settings');

        // No proof or typed confirmation is required for an operational write.
        await request(path, { method: 'PUT', body: command });
        // A legacy client's irrelevant proof field must not become a new gate.
        await request(path, { method: 'PUT', body: { ...command, revision: command.revision + 1, stepUp: { code: 'invalid' } } });
        await request(path, { method: 'PUT', body: command, expected: 409 });
        const saved = (await request(path)).data.data;
        assert.equal(saved.revision, before.revision + 2, 'Only successful administrator writes advance revision');
        assert.deepEqual(saved.settings, before.settings, 'Same-settings saves must preserve configuration');
    } finally {
        if (enrollment) enrollment.secretKey = enrollment.manualEntryKey = enrollment.qrCodeBase64 = null;
        if (member) member.accessToken = null;
        if (key) key.accessToken = null;
    }
}
