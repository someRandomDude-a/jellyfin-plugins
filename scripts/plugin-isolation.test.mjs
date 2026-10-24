import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
const read=path=>readFile(new URL('../'+path,import.meta.url),'utf8');
test('releases depend on selected reusable checks, never the full suite/matrix',async()=>{
    const release=await read('.github/workflows/release.yml');
    assert(release.includes('uses: ./.github/workflows/plugin-checks.yml'));
    assert(!release.includes('uses: ./.github/workflows/tests.yml'));
    assert(!release.includes('uses: ./.github/workflows/integration.yml'));
    assert(release.includes('needs: [verify-ref, selected-checks]'));
    assert(release.includes('-Plugin $env:PLUGIN'));
    assert(release.includes('catalog-$env:PLUGIN'));
    assert(!release.includes('continue-on-error'));
});
test('plugin test dispatch never runs the all-project solution or aggregate runner',async()=>{
    const script=await read('scripts/test-plugin.ps1');
    assert(!script.includes('Jellyfin.Plugins.slnx'));
    assert(!script.includes('scripts/test.ps1'));
    for(const key of ['account-hub','discord-bridge','private-syncplay','deletion-queue'])assert(script.includes(`'${key}' {`));
    assert(script.includes('scripts/validate-release-set.test.ps1'));
    const checks=await read('.github/workflows/plugin-checks.yml');
    assert(checks.includes('--plugins=$($keys[$env:PLUGIN])'));
    assert(!checks.includes('test-combinations.mjs'));
    assert(checks.includes('-Plugin $env:PLUGIN'));
    const integration=await read('scripts/integration.mjs');
    assert(!integration.includes("assert.equal(manifest.length, 4"));
    assert(integration.includes('Selected plugin package missing: '));
});
