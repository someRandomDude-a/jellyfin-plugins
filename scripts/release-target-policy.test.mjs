import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, readdir, mkdir, mkdtemp, writeFile, rm } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { declarations, checkFreshness, compare, lockChanges, validateDirectLock } from './check-dependency-freshness.mjs';
import { assertStillLatest } from './assert-latest-targets.mjs';
import { assertReleaseSource } from './assert-release-source.mjs';
const read = path => readFile(new URL('../' + path, import.meta.url), 'utf8');

test('existing suite tags cannot silently test older source after fixes are pushed',async()=>{
    const selected='a'.repeat(40),old='b'.repeat(40);
    assertReleaseSource(selected,selected);
    assert.throws(()=>assertReleaseSource(selected,old),/Choose an unused version/);
    assert.throws(()=>assertReleaseSource('',old),/Cannot verify/);
    const workflow=await read('.github/workflows/release-all.yml');
    assert(workflow.includes('node scripts/assert-release-source.mjs'));
    assert(workflow.indexOf('node scripts/assert-release-source.mjs')<workflow.indexOf('git tag $env:RELEASE_TAG HEAD'));
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
test('every release wrapper uses the protected latest-only release engine', async () => {
    for (const name of await readdir(new URL('../.github/workflows/', import.meta.url))) {
        if (!/^release-(?!all\.yml)/.test(name)) continue;
        assert((await read('.github/workflows/' + name)).includes('uses: ./.github/workflows/release.yml'), name);
    }
    for (const name of ['release.yml', 'release-all.yml']) {
        const yaml = await read('.github/workflows/' + name);
        assert(yaml.includes('uses: ./.github/workflows/latest-targets.yml'));
        assert(yaml.includes('node scripts/check-dependency-freshness.mjs'));
        assert(yaml.includes('node scripts/assert-latest-targets.mjs'));
        assert(yaml.includes('environment: plugin-release'));
    }
    assert((await read('.github/workflows/release-all.yml')).includes('needs: [verify-ref, targets, checks, integration]'));
});
test('all CI test graphs share parent targets, use current Node, and forbid baseline fallback', async () => {
    for (const name of ['tests.yml', 'plugin-checks.yml', 'integration.yml', 'dependency-validation.yml']) {
        const yaml = await read('.github/workflows/' + name);
        assert(yaml.includes('uses: ./.github/workflows/latest-targets.yml'), name);
        assert(yaml.includes('${{ needs.targets.outputs.artifact }}'), name);
        if(name!=='dependency-validation.yml')assert(yaml.includes('uses: ./.github/actions/use-latest-targets'), name);
        else assert(yaml.includes('uses: ./.github/workflows/tests.yml'), 'Dependency validation inherits the root/plugin tool-bound gates');
        assert(!/node-version:\s*['"]?\d/.test(yaml), name + ' still pins an old Node line');
        assert(!yaml.includes('continue-on-error: true'), name);
    }
    assert((await read('Directory.Build.targets')).includes('LATEST-ONLY CI BLOCKED'));
    for (const path of ['modules/user-management/docker-compose.yml', 'plugins/deletion-queue/test-infrastructure/compose.yaml']) {
        const yaml = await read(path); assert(yaml.includes('${JELLYFIN_TEST_IMAGE:?')); assert(!/jellyfin\/jellyfin:\d/.test(yaml));
    }
});
