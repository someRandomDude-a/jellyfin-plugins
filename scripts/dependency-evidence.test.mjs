import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, mkdtemp, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { dependencyFingerprint, assertDependencyEvidence } from './dependency-evidence.mjs';
import { dependencyState } from './check-dependency-freshness.mjs';
import { githubJson } from './latest-releases.mjs';

const identity = { source: 'a'.repeat(40), fingerprint: 'b'.repeat(64), run: '123', attempt: '2' };
const evidence = () => ({ schemaVersion: 1, policy: 'latest-stable', verified: true, resolvedAt: new Date().toISOString(), ...identity });
test('dependency audits bind source, input hash and current run attempt, never previous runs or stale results', () => {
    assertDependencyEvidence(evidence(), identity);
    const local={...identity,run:'11111111-1111-4111-8111-111111111111',attempt:'1'};
    assertDependencyEvidence({...evidence(),...local},local);
    for (const [key, value] of Object.entries({ source: 'c'.repeat(40), fingerprint: 'c'.repeat(64), run: '124', attempt: '3', verified: false, policy: 'fallback', schemaVersion: 0, resolvedAt: 'invalid' })) {
        assert.throws(() => assertDependencyEvidence({ ...evidence(), [key]: value }, identity), /No cached\/older fallback/);
    }
    for (const offset of [-86400001, 60000]) assert.throws(() => assertDependencyEvidence({ ...evidence(), resolvedAt: new Date(Date.now() + offset).toISOString() }, identity));
    assert.throws(() => assertDependencyEvidence(evidence(), { ...identity, run: undefined }));
});
test('audit hashes ignore checkout line endings/order but detect declarations, locks, Node and removed inputs', () => {
    const rows = [{ path: 'package.json', kind: 'npm', name: 'example', current: '1.0.0' }, { path: '(running toolchain)', current: '26.13.0' }];
    const locks = [['package-lock.json', '{\n}\n']];
    const hash = dependencyFingerprint(rows, locks);
    assert.equal(hash, dependencyFingerprint([...rows].reverse(), [['package-lock.json', '{\r\n}\r\n']]));
    assert.notEqual(hash, dependencyFingerprint([{ ...rows[0], current: '2.0.0' }, rows[1]], locks));
    assert.notEqual(hash, dependencyFingerprint([rows[0], { ...rows[1], current: '999.0.0' }], locks));
    assert.notEqual(hash, dependencyFingerprint(rows, [['package-lock.json', '{"changed":true}']]));
    assert.notEqual(hash, dependencyFingerprint(rows.slice(1), locks));
});
test('real child entry point consumes its audit without GitHub access, and rejects a different run without rediscovery', async () => {
    const base = join(tmpdir(), 'opencode'); await mkdir(base, { recursive: true });
    const directory = await mkdtemp(join(base, 'dependency-audit-'));
    try {
        const { identity: current } = await dependencyState();
        const audit = { ...evidence(), ...current, run: '123', attempt: '2' };
        const path = join(directory, 'audit.json'); await writeFile(path, JSON.stringify(audit));
        const env = { ...process.env, GITHUB_ACTIONS: 'true', GH_TOKEN: '', GITHUB_TOKEN: '', GITHUB_RUN_ID: '123', GITHUB_RUN_ATTEMPT: '2', SUITE_DEPENDENCY_AUDIT: path };
        const run = overrides => spawnSync(process.execPath, [fileURLToPath(new URL('./check-dependency-freshness.mjs', import.meta.url))], { env: { ...env, ...overrides }, encoding: 'utf8', timeout: 30000 });
        const accepted = run({}); assert.equal(accepted.status, 0, accepted.stdout + accepted.stderr); assert.match(accepted.stdout, /no rediscovery/);
        const rejected = run({ GITHUB_RUN_ATTEMPT: '3' }); assert.notEqual(rejected.status, 0); assert.match(rejected.stderr, /different-source dependency audit/);
        const missing = run({ SUITE_DEPENDENCY_AUDIT: join(directory, 'missing.json') }); assert.notEqual(missing.status, 0); assert.match(missing.stderr, /ENOENT/);
    } finally { await rm(directory, { recursive: true, force: true }); }
});
test('GitHub discovery authenticates with either standard token and gives safe rate-limit diagnostics without retry/fallback', async () => {
    const saved = Object.fromEntries(['GH_TOKEN', 'GITHUB_TOKEN', 'GITHUB_ACTIONS'].map(key => [key, process.env[key]]));
    try {
        process.env.GITHUB_ACTIONS = 'true'; delete process.env.GH_TOKEN; process.env.GITHUB_TOKEN = 'fixture-not-a-real-token';
        let calls = 0;
        const request = async (_url, options) => {
            calls++; assert.equal(options.headers.Authorization, 'Bearer fixture-not-a-real-token');
            return { ok: false, status: 403, headers: new Headers({ 'x-ratelimit-remaining': '0', 'x-ratelimit-reset': '1793400000', 'retry-after': '60' }) };
        };
        await assert.rejects(githubJson('repos/example/example/releases/latest', request), error => {
            assert.match(error.message, /HTTP 403; x-ratelimit-remaining=0/); assert.match(error.message, /retry-after=60/);
            assert(!error.message.includes(process.env.GITHUB_TOKEN)); return true;
        });
        assert.equal(calls, 1);
        process.env.GH_TOKEN = process.env.GITHUB_TOKEN; delete process.env.GITHUB_TOKEN;
        assert.deepEqual(await githubJson('repos/example/example/releases/latest', async (_url, options) => {
            assert.equal(options.headers.Authorization, 'Bearer fixture-not-a-real-token'); return { ok: true, json: async () => ({ tag_name: 'v1.0.0' }) };
        }), { tag_name: 'v1.0.0' });
        delete process.env.GH_TOKEN;
        await assert.rejects(githubJson('repos/example/example/releases/latest', () => { throw Error('Anonymous request must not run'); }), /requires GH_TOKEN or GITHUB_TOKEN/);
    } finally { for (const [key, value] of Object.entries(saved)) { if (value === undefined) delete process.env[key]; else process.env[key] = value; } }
});
