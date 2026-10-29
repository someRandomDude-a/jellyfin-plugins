import test from 'node:test';
import assert from 'node:assert/strict';
import { describeRelease, discoverTargets, requireJellyfinSdk, downloadSecurity, jellyfinTarget, validateSnapshot, saveTargets, loadTargets, assertRuntimeVersion, freshDockerArguments } from './latest-releases.mjs';
import { contractFingerprint } from './reviewed-contracts.mjs';
import { mkdtemp, rm, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

test('test software follows current stable metadata rather than hard-coded versions', async () => {
    const urls = [];
    const request = async url => { urls.push(url); return { ok: true, json: async () => ({ id: 1, tag_name: url.includes('/jellyfin/') ? 'v25.7' : 'v8.9.10' }) }; };
    const first = await discoverTargets(['jellyfin', 'seerr'], request);
    assert.equal(first.targets.jellyfin.image, 'jellyfin/jellyfin:25.7');
    assert.equal(first.targets.seerr.image, 'seerr/seerr:v8.9.10');
    assert(urls.every(url => url.endsWith('/releases/latest')));
    assert.throws(() => requireJellyfinSdk(first.targets.jellyfin, { version: '25.6.0' }), /No older-server fallback/);
    requireJellyfinSdk(first.targets.jellyfin, { version: '25.7.0' });
});
test('disposable tools cannot run a stale locally cached mutable image', () => {
    assert.deepEqual(freshDockerArguments(['run', '--rm', 'python:alpine']), ['run', '--pull=always', '--rm', 'python:alpine']);
    assert.deepEqual(freshDockerArguments(['logs', 'owned-container']), ['logs', 'owned-container']);
});
test('draft, prerelease, ambiguous and unchecked Security assets are rejected', () => {
    for (const release of [{ tag_name: 'v1.2', prerelease: true }, { tag_name: 'v1.2', draft: true }, { tag_name: 'main' }]) assert.throws(() => describeRelease('jellyfin', release));
    assert.throws(() => describeRelease('security', { tag_name: 'v9.0', assets: [] }));
    assert.throws(() => describeRelease('security', { tag_name: 'v9.0', assets: [{ name: 'Jellyfin.Plugin.TwoFactorAuthv9.0-jf12.zip' }] }));
});
test('release discovery failure never falls back to a cached older default', async () => {
    await assert.rejects(discoverTargets(['seerr'], async () => ({ ok: false, status: 503 })), /discovery failed/);
});
test('downloaded latest assets must match the digest discovered for this run', async () => {
    await assert.rejects(downloadSecurity({ downloadUrl: 'https://example.invalid/asset', sha256: '0'.repeat(64) }, 'must-not-be-written.zip', async () => ({ ok: true, arrayBuffer: async () => Buffer.from('wrong asset') })), /digest mismatch/);
});
test('automatic SDK/ABI selection follows future releases without changing props or tests', () => {
    assert.deepEqual(jellyfinTarget(describeRelease('jellyfin', { tag_name: 'v35.7' })), { version: '35.7.0', abi: '35.7.0.0', tag: '35.7', image: 'jellyfin/jellyfin:35.7' });
});
test('Security asset selection follows the current Jellyfin major, never an older asset', () => {
    const asset = major => ({ name: 'Jellyfin.Plugin.TwoFactorAuthv9.0-jf' + major + '.zip', digest: 'sha256:' + 'a'.repeat(64), browser_download_url: 'https://github.com/ZL154/JellyfinSecurity/releases/download/v9.0/asset.zip' });
    const release = { tag_name: 'v9.0', assets: [asset(12), asset(35)] };
    assert.match(describeRelease('security', release, { jellyfinMajor: 35 }).asset, /jf35/);
    assert.throws(() => describeRelease('security', release, { jellyfinMajor: 36 }));
});
test('expired, future and legacy snapshots cannot turn a previous target into latest', () => {
    const now = Date.now(), base = { schemaVersion: 1, policy: 'latest-stable', runId: process.env.SUITE_TEST_RUN_ID || '11111111-1111-4111-8111-111111111111' };
    for (const resolvedAt of [undefined, new Date(now - 86400001).toISOString(), new Date(now + 120000).toISOString()]) assert.throws(() => validateSnapshot({ ...base, resolvedAt }, now));
    validateSnapshot({ ...base, resolvedAt: new Date(now).toISOString() }, now);
});
test('children use the same run snapshot without rediscovering or falling back', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'opencode', 'targets-test-'));
    try {
        const file = join(directory, 'targets.json');
        await saveTargets(file, { jellyfin: describeRelease('jellyfin', { tag_name: 'v35.7' }) }, { runId: process.env.SUITE_TEST_RUN_ID });
        const targets = await loadTargets(['jellyfin'], { file, request: () => { throw Error('Unexpected rediscovery'); } });
        assert.equal(targets.jellyfin.version, '35.7');
        await assert.rejects(loadTargets(['seerr'], { file }), /Missing current-run target/);
    } finally { await rm(directory, { recursive: true, force: true }); }
});
test('child snapshot rewrites preserve the exact recorded build toolchain', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'opencode', 'tools-test-'));
    try {
        const file = join(directory, 'targets.json'), tools = { node: '35.1.2', dotnet: '35.0.101' };
        await saveTargets(file, { jellyfin: describeRelease('jellyfin', { tag_name: 'v35.7' }) }, { runId: process.env.SUITE_TEST_RUN_ID, tools });
        const snapshot = JSON.parse(await readFile(file, 'utf8'));
        await saveTargets(file, snapshot.targets, snapshot);
        assert.deepEqual(JSON.parse(await readFile(file, 'utf8')).tools, tools);
    } finally { await rm(directory, { recursive: true, force: true }); }
});
test('runtime version checks accept equivalent padding but reject cached or unexpected releases', () => {
    assertRuntimeVersion('v35.7.0', '35.7', 'fixture');
    for (const actual of ['35.6', '36.0', '35.7-preview', 'garbage']) assert.throws(() => assertRuntimeVersion(actual, '35.7', 'fixture'));
});
test('native/source safety fingerprints detect additions, removals, changes and truncated trees', () => {
    const row = { type: 'blob', path: 'src/Jellyfin.Database/Entities/User.cs', sha: 'a'.repeat(40) };
    const tree = { tree: [row] }, fingerprint = contractFingerprint(tree, 'native');
    assert.equal(contractFingerprint({ tree: [row, { ...row, path: 'README.md' }] }, 'native'), fingerprint);
    assert.notEqual(contractFingerprint({ tree: [{ ...row, sha: 'b'.repeat(40) }] }, 'native'), fingerprint);
    assert.notEqual(contractFingerprint({ tree: [row, { ...row, path: 'src/Jellyfin.Database/New.cs' }] }, 'native'), fingerprint);
    assert.throws(() => contractFingerprint({ ...tree, truncated: true }, 'native'));
    assert.throws(() => contractFingerprint({ tree: [] }, 'native'));
});
