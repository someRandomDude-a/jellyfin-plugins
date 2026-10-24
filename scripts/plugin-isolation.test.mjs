import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
const read=path=>readFile(new URL('../'+path,import.meta.url),'utf8');
test('suite dispatch gates all platforms and publishes before one combined manifest update',async()=>{
    const workflow=await read('.github/workflows/release-all.yml');
    assert(workflow.includes('workflow_dispatch:'));
    assert(workflow.includes('ref: ${{ github.sha }}'));
    assert(workflow.includes('uses: ./.github/workflows/tests.yml'));
    assert(workflow.includes('needs: [verify-ref, checks]'));
    assert(workflow.includes('runtime: [linux-x64, linux-arm64, win-x64]'));
    assert(workflow.includes('-Plugin all'));
    assert(workflow.includes('validate-release-set.ps1'));
    assert(workflow.includes('environment: plugin-release'));
    assert(workflow.includes('gh release create $env:RELEASE_TAG --verify-tag --draft'));
    assert(workflow.includes('needs: [verify-ref, publish]'));
    assert.equal(workflow.match(/uses: \.\/\.github\/workflows\/update-manifest.yml/g)?.length,1);
    assert(workflow.includes("needs.verify-ref.outputs.published == 'true'"));
    assert(workflow.includes('git merge-base --is-ancestor $commit HEAD'));
    assert(!workflow.includes('--clobber'));assert(!workflow.includes('--force'));
    const manifest=await read('.github/workflows/update-manifest.yml');
    assert(manifest.includes("if ($env:PLUGIN -eq 'all')"));
    assert(manifest.includes('-Plugin $env:PLUGIN'));
});
test('each plugin has its own branch-selectable release dispatch',async()=>{
    for(const plugin of ['account-hub','discord-bridge','private-syncplay','deletion-queue','notifications']) {
        const workflow=await read(`.github/workflows/release-${plugin}.yml`);
        assert(workflow.includes('workflow_dispatch:'));
        assert(workflow.includes(`plugin: ${plugin}`));
        assert(workflow.includes('branch: ${{ github.ref }}'));
        assert(workflow.includes('uses: ./.github/workflows/release.yml'));
    }
});
test('releases depend on selected reusable checks, never the full suite/matrix',async()=>{
    const release=await read('.github/workflows/release.yml');
    assert(release.includes('uses: ./.github/workflows/plugin-checks.yml'));
    assert(!release.includes('uses: ./.github/workflows/tests.yml'));
    assert(!release.includes('uses: ./.github/workflows/integration.yml'));
    assert(release.includes('needs: [verify-ref, selected-checks]'));
    assert(release.includes('-Plugin $env:PLUGIN'));
    assert(release.includes('uses: ./.github/workflows/update-manifest.yml'));
    assert(release.includes('needs: [verify-ref, publish]'));
    const manifest=await read('.github/workflows/update-manifest.yml');
    assert(manifest.includes('publish-combined-catalog.ps1'));
    assert(manifest.includes('validate-release-set.ps1'));
    assert(release.includes("needs.verify-ref.outputs.published == 'true'"));
    assert(release.includes('git merge-base --is-ancestor $existing HEAD'));
    assert(!manifest.includes('CatalogBranch $branch'));
    assert(!release.includes('continue-on-error'));
});
test('plugin test dispatch never runs the all-project solution or aggregate runner',async()=>{
    const script=await read('scripts/test-plugin.ps1');
    assert(!script.includes('Jellyfin.Plugins.slnx'));
    assert(!script.includes('scripts/test.ps1'));
    for(const key of ['account-hub','discord-bridge','private-syncplay','deletion-queue','notifications'])assert(script.includes(`'${key}' {`));
    assert(script.includes('scripts/validate-release-set.test.ps1'));
    const checks=await read('.github/workflows/plugin-checks.yml');
    assert(checks.includes('--plugins=$($keys[$env:PLUGIN])'));
    assert(!checks.includes('test-combinations.mjs'));
    assert(checks.includes('-Plugin $env:PLUGIN'));
    const integration=await read('scripts/integration.mjs');
    assert(!integration.includes("assert.equal(manifest.length, 4"));
    assert(integration.includes('Selected plugin package missing: '));
});
