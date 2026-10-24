// Opt-in public-network smoke check. No .env, bot credentials or Jellyfin state.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { startDiscordTunnel, probeDiscordTunnel } from './discord-tunnel.mjs';
assert(process.argv.includes('--allow-public-disposable-tunnel'), 'Explicit public disposable tunnel permission required');
const name = 'jellyfin-suite-test-' + randomUUID().slice(0, 8);
const sentinel = randomUUID();
function docker(...args) {
    const result = spawnSync('docker', args, { encoding: 'utf8', timeout: 180000, maxBuffer: 1024 * 1024 });
    assert.equal(result.status, 0, 'Disposable Docker operation failed (details withheld)');
    return (result.stdout + (args[0] === 'logs' ? result.stderr : '')).trim();
}
let tunnel, started = false;
try {
    docker('run', '-d', '--name', name, '--label', 'jellyfin.suite.tunnel-test=true', '--read-only', '--cap-drop=ALL', '--security-opt=no-new-privileges', '--entrypoint', 'node',
        'mcr.microsoft.com/playwright:v1.56.1-noble', '-e',
        `require('http').createServer((req,res)=>{res.setHeader('Cache-Control','no-store');res.end(${JSON.stringify(sentinel)})}).listen(8096,'0.0.0.0')`);
    started = true;
    tunnel = await startDiscordTunnel(name, docker);
    console.log('Temporary harmless probe endpoint:', tunnel.origin);
    const outcomes = {};
    let verified = false;
    for (let attempt = 0; attempt < 120; attempt++) {
        try {
            const response = await probeDiscordTunnel(tunnel.origin, docker, '/suite/probe');
            const outcome = response.errorCode || 'http_' + response.status;
            outcomes[outcome] = (outcomes[outcome] || 0) + 1;
            if (response.status === 200 && response.text === sentinel) { verified = true; break; }
        } catch (error) {
            const code = error.code || error.cause?.code || error.name;
            const safe = /^[A-Za-z0-9_]{1,60}$/.test(code || '') ? code : 'transport_error';
            outcomes[safe] = (outcomes[safe] || 0) + 1;
        }
        await new Promise(resolve => setTimeout(resolve, 2000));
    }
    if (!verified) console.error('Sanitized tunnel diagnostics:', JSON.stringify({ connectionReady: tunnel.connectionReady(), outcomes }));
    assert(verified, 'Trusted HTTPS tunnel did not reach the disposable sentinel');
    console.log('PASS trusted public HTTPS -> temporary tunnel -> isolated HTTP fixture, no domain/account/credentials required.');
} finally {
    try { tunnel?.stop(); } finally { if (started) docker('rm', '-f', name); }
}
