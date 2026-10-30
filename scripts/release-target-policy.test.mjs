import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, mkdir, mkdtemp, writeFile, rm } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { declarations, checkFreshness, compare, lockChanges, validateDirectLock } from './check-dependency-freshness.mjs';
import { assertStillLatest } from './assert-latest-targets.mjs';
import { assertReleaseSource } from './assert-release-source.mjs';
const read = path => readFile(new URL('../' + path, import.meta.url), 'utf8');

test('immutable tags must match the selected source, not an older ancestor',()=>{
    const selected='a'.repeat(40),old='b'.repeat(40);
    assertReleaseSource(selected,selected);
    assert.throws(()=>assertReleaseSource(selected,old),/Choose an unused version/);
    assert.throws(()=>assertReleaseSource('',old),/Cannot verify/);
});

test('freshness covers runtime, test, action, SDK and embedded browser dependencies', () => {
    assert.equal(declarations('plugins/example/tests/Example.Tests.csproj', '<PackageReference Include="xunit" Version="2.9.3" />')[0].kind, 'nuget');
    assert.equal(declarations('tools/package.json', '{"devDependencies":{"playwright":"1.0.0"}}')[0].kind, 'npm');
    assert.equal(declarations('global.json', '{"sdk":{"version":"10.0.1"}}')[0].kind, 'sdk');
    assert.equal(declarations('.github/workflows/test.yml', 'uses: actions/checkout@v4')[0].kind, 'action');
    const rows = declarations('scripts/browser.mjs', "'mcr.microsoft.com/playwright:v1.0.0-noble@sha256:" + 'a'.repeat(64) + "'");
    assert.equal(rows[0].digest, 'sha256:' + 'a'.repeat(64));
    assert.equal(declarations('scripts/example.test.mjs', "'example/image:0.0.1'").length, 0);
    assert.equal(declarations('Jellyfin.Target.props', '<JellyfinVersion>1.0</JellyfinVersion>').length, 0);
    assert.equal(declarations('source.csproj', '<PackageReference Include="Microsoft.Data.Sqlite" Version="1.0" />').length, 0, 'Native database packages follow upstream Jellyfin, not arbitrary newer ABI');
    assert.throws(() => declarations('source.csproj', '<PackageReference Include="Unknown" Version="$(UntrackedVersion)" />'), /Unresolved NuGet version policy/);
    assert.throws(() => declarations('package.json', '{"dependencies":{"private":"https://user:secret@example.invalid/package"}}'), /Unsafe dependency descriptor/);
});
test('stale versions, digests and discovery failures all block release loudly', async () => {
    const row = { path: 'test.csproj', kind: 'nuget', name: 'Example', current: '1.0.0' };
    await assert.rejects(checkFreshness([row], async () => '2.0.0'), /LATEST-ONLY RELEASE BLOCKED[\s\S]*Example 1.0.0/);
    await assert.rejects(checkFreshness([row], async () => { throw Error('Registry unavailable'); }), /Registry unavailable/);
    await assert.rejects(checkFreshness([{ ...row, kind: 'image', digest: 'old' }], async () => ({ version: '1.0.0', digest: 'new' })), /digest/);
    await checkFreshness([row], async () => '1.0.0');
    assert(compare('1.0.0.1', '1.0.0.2') < 0);
});
test('lock changes remain itemized without making transitive freshness a release requirement', () => {
    const lock = (version, integrity) => ({ packages: { '': {}, 'node_modules/indirect': { version, integrity } } });
    assert.deepEqual(lockChanges(lock('1.0.0', 'old'), lock('1.1.0', 'new')), ['node_modules/indirect']);
    assert.deepEqual(lockChanges(lock('1.0.0', 'old'), lock('1.0.0', 'new')), ['node_modules/indirect']);
    assert.deepEqual(lockChanges(lock('1.0.0', 'old'), lock('1.0.0', 'old')), []);
    assert.throws(() => lockChanges({}, {}), /Unsupported npm lock/);
});
test('direct locks must match declarations but upstream transitive age is not a gate', () => {
    const manifest = { dependencies: { direct: '^2.0.0' } };
    const lock = { lockfileVersion: 3, packages: { '': { dependencies: { direct: '^2.0.0' } }, 'node_modules/direct': { version: '2.0.0' }, 'node_modules/transitive': { version: '0.0.1' } } };
    validateDirectLock(manifest, lock);
    assert.throws(() => validateDirectLock({ dependencies: { direct: '^3.0.0' } }, lock), /Lock root/);
    lock.packages['node_modules/direct'].version = '1.0.0';
    assert.throws(() => validateDirectLock(manifest, lock), /Direct lock version/);
});
test('upstream changes after testing require new acceptance rather than stale publication', async () => {
    const snapshot = { schemaVersion: 1, policy: 'latest-stable', runId: process.env.SUITE_TEST_RUN_ID || '11111111-1111-4111-8111-111111111111', resolvedAt: new Date().toISOString(), targets: { jellyfin: { version: '35.7', tag: 'v35.7', releaseId: 1 } } };
    await assertStillLatest(snapshot, async () => snapshot);
    await assert.rejects(assertStillLatest(snapshot, async () => ({ targets: { jellyfin: { version: '35.8', tag: 'v35.8', releaseId: 2 } } })), /upstream changed after testing/);
    await assert.rejects(assertStillLatest(snapshot, async () => { throw Error('No registry'); }), /No registry/);
});
test('same-version image/source retags cannot bypass the final publication gate', async () => {
    const target = { repository: 'jellyfin/jellyfin', version: '35.7', tag: 'v35.7', releaseId: 1, sourceCommit: 'old', image: 'jellyfin/jellyfin:35.7', imageDigest: 'image@sha256:old', platform: 'linux/amd64' };
    const snapshot = { schemaVersion: 1, policy: 'latest-stable', runId: process.env.SUITE_TEST_RUN_ID || '11111111-1111-4111-8111-111111111111', resolvedAt: new Date().toISOString(), targets: { jellyfin: target } };
    const discover = async () => ({ targets: { jellyfin: { version: '35.7', tag: 'v35.7', releaseId: 1, image: target.image } } });
    await assert.rejects(assertStillLatest(snapshot, discover, async () => ({ imageDigest: 'image@sha256:new' }), async () => 'new'), /sourceCommit[\s\S]*imageDigest/);
    await assertStillLatest(snapshot, discover, async () => ({ imageDigest: target.imageDigest }), async () => 'old');
});
test('real CI bootstrap rejects mismatched installed Node and SDK before upstream calls', async () => {
    const base = join(tmpdir(), 'opencode'); await mkdir(base, { recursive: true });
    const directory = await mkdtemp(join(base, 'ci-toolchain-policy-'));
    try {
        const file = join(directory, 'targets.json');
        for (const nodeMismatch of [true, false]) {
            const snapshot = { schemaVersion: 1, policy: 'latest-stable', runId: process.env.SUITE_TEST_RUN_ID || '11111111-1111-4111-8111-111111111111', resolvedAt: new Date().toISOString(), tools: { node: nodeMismatch ? '999.0.0' : process.versions.node, dotnet: '999.0.101' }, targets: { jellyfin: { repository: 'jellyfin/jellyfin', version: '35.7', tag: 'v35.7', image: 'jellyfin/jellyfin:35.7' } } };
            await writeFile(file, JSON.stringify(snapshot));
            const result = spawnSync(process.execPath, [fileURLToPath(new URL('./prepare-test-targets.mjs', import.meta.url)), '--services=jellyfin', '--output=' + file], { encoding: 'utf8', env: { ...process.env, GITHUB_ACTIONS: 'true', VERIFY_BUILD_SDK: 'true', SUITE_TEST_TARGETS: file }, timeout: 30000 });
            assert.notEqual(result.status, 0);
            assert.match(result.stderr, nodeMismatch ? /Node runtime differs/ : /installed .NET SDK differs/);
        }
    } finally { await rm(directory, { recursive: true, force: true }); }
});
test('raw build/compose commands cannot fall back to a historical server target', async () => {
    assert((await read('Directory.Build.targets')).includes('LATEST-ONLY CI BLOCKED'));
    for (const path of ['modules/user-management/docker-compose.yml', 'plugins/deletion-queue/test-infrastructure/compose.yaml']) {
        const yaml = await read(path); assert(yaml.includes('${JELLYFIN_TEST_IMAGE:?')); assert(!/jellyfin\/jellyfin:\d/.test(yaml));
    }
});
test('tool bootstrap uses official stable metadata and verifies checksums before extracting executables',async()=>{
    const code=await read('scripts/bootstrap-tools.ps1');
    for(const value of ['nodejs.org/dist/index.json','PowerShell/PowerShell/releases/latest','releases-index.json','SHASUMS256.txt','Get-FileHash','SHA512','checksum mismatch','PlanOnly','SUITE_TOOLCHAIN_READY'])assert(code.includes(value),value);
    assert(code.indexOf('Get-FileHash')<code.indexOf('Expand-Archive'));assert(!code.includes('Invoke-Expression'));assert(code.includes('do not override the source SDK'));
});
