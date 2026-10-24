import { test } from 'node:test';
import assert from 'node:assert/strict';
import { quickTunnelOrigin, startDiscordTunnel, tunnelImage, probeDiscordTunnel } from './discord-tunnel.mjs';
test('Quick tunnel parsing accepts only a provider-generated HTTPS hostname', () => {
    assert.equal(quickTunnelOrigin('| https://test-only-host.trycloudflare.com |'), 'https://test-only-host.trycloudflare.com');
    for (const log of ['http://test.trycloudflare.com', 'https://test.trycloudflare.com.attacker.invalid', 'https://user:secret@test.trycloudflare.com', 'https://test.trycloudflare.com/path']) assert.equal(quickTunnelOrigin(log), undefined);
});
test('Tunnel is constrained to owned fixture and fixed port; teardown is idempotent', async () => {
    const calls = [];
    const docker = (...args) => { calls.push(args); return args[0] === 'logs' ? 'https://test-only.trycloudflare.com\n' : ''; };
    const tunnel = await startDiscordTunnel('jellyfin-suite-test-1234abcd', docker);
    assert.equal(tunnel.origin, 'https://test-only.trycloudflare.com');
    const launch = calls[0]; assert(launch.includes(tunnelImage)); assert(launch.includes('container:jellyfin-suite-test-1234abcd'));
    assert.equal(launch.at(-1), 'http://127.0.0.1:8096'); assert(!launch.includes('-v')); assert(!launch.includes('--network=host'));
    tunnel.stop(); tunnel.stop(); assert.equal(calls.filter(args => args[0] === 'rm').length, 1);
    await assert.rejects(startDiscordTunnel('production', docker));
});
test('Timeout, stopped tunnel and read failure remove the owned tunnel', async () => {
    for (const failure of ['timeout', 'stopped', 'logs']) {
        const calls = [];
        const docker = (...args) => { calls.push(args); if (args[0] === 'logs' && failure === 'logs') throw Error('raw sensitive diagnostics'); return args[0] === 'inspect' && failure === 'timeout' ? 'true' : ''; };
        await assert.rejects(startDiscordTunnel('jellyfin-suite-test-1234abcd', docker, { attempts: 2, wait: async () => {} }), /details withheld/);
        assert.equal(calls.filter(args => args[0] === 'rm').length, 1);
    }
});
test('Public probe cannot carry credentials or select arbitrary hosts/routes and keeps TLS validation', () => {
    const calls = [];
    const docker = (...args) => { calls.push(args); return JSON.stringify({ status: 200, text: '{}' }); };
    assert.equal(probeDiscordTunnel('https://fixture-only.trycloudflare.com', docker).status, 200);
    assert(!calls[0].join(' ').includes('rejectUnauthorized'));
    assert(!calls[0].join(' ').includes('NODE_TLS_REJECT_UNAUTHORIZED'));
    for (const origin of ['http://fixture-only.trycloudflare.com', 'https://attacker.invalid', 'https://secret@fixture-only.trycloudflare.com', 'https://fixture-only.trycloudflare.com/']) assert.throws(() => probeDiscordTunnel(origin, docker));
    assert.throws(() => probeDiscordTunnel('https://fixture-only.trycloudflare.com', docker, '/Users'));
});
