// Current CI contracts live here once. Runtime policies have their own unit tests.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, readdir, access } from 'node:fs/promises';
import { plugins } from './test-plan.mjs';
const read = path => readFile(new URL('../' + path, import.meta.url),'utf8');
const contains = (text, values) => { for (const value of values) assert(text.includes(value), 'Missing gate: ' + value); };

test('workflow references resolve; one test matrix supports suite and independent scopes', async () => {
    const directory = new URL('../.github/workflows/',import.meta.url);
    for (const name of await readdir(directory)) {
        const workflow = await readFile(new URL(name,directory),'utf8');
        for (const [, path] of workflow.matchAll(/uses:\s+\.\/(\.github\/workflows\/[^\s]+\.yml)/g)) await access(new URL('../' + path,import.meta.url));
    }
    const tests = await read('.github/workflows/tests.yml');
    contains(tests,['run: ./scripts/test.ps1 -Plugin $env:PLUGIN',"PLUGIN: ${{ inputs.plugin || 'all' }}",
        'os: [ubuntu-latest, windows-latest]','Standalone installed plugin with and without native Security',
        "if: inputs.plugin == 'all' || inputs.plugin == ''",'--notification-studio','--with-consumer --install-from-catalog']);
    assert.equal(tests.match(/os: \[ubuntu-latest, windows-latest\]/g)?.length,1);
    assert(!tests.includes('test-combinations.mjs'),'Subset testing belongs to integration, not each owner');
});
test('one immutable release selector preserves exact source, platform, acceptance and publication boundaries', async () => {
    const release = await read('.github/workflows/release-all.yml');
    contains(release,['workflow_dispatch:',...plugins,'default: all','ref: ${{ inputs.ref || github.sha }}',
        'node scripts/assert-release-source.mjs "$selectedCommit" "$commit"','Existing immutable tag belongs to another commit',
        'needs: [verify-ref, targets, checks, integration]',"needs.checks.result == 'success'","needs.integration.result == 'success'",
        'uses: ./.github/workflows/tests.yml','plugin: ${{ needs.verify-ref.outputs.plugin }}',
        "external_only: ${{ needs.verify-ref.outputs.plugin != 'all' }}",'runtime: [linux-x64, linux-arm64, win-x64]',
        '-Plugin $env:PLUGIN','validate-release-set.ps1','environment: plugin-release','gh release create $env:RELEASE_TAG --verify-tag --draft',
        'needs: [verify-ref, publish]',"needs.verify-ref.outputs.published == 'true'"]);
    assert.match(release,/  verify-ref:\r?\n    needs: targets/);
    for (const gate of ['uses: ./.github/actions/use-latest-targets','node scripts/check-dependency-freshness.mjs','node scripts/assert-release-source.mjs']) assert(release.indexOf(gate)<release.indexOf('git tag '));
    assert.equal(release.match(/uses: \.\/\.github\/workflows\/update-manifest.yml/g)?.length,1);
    assert(!/--clobber|--force|continue-on-error|git merge-base --is-ancestor/.test(release));
    const publish=release.indexOf('gh release edit $env:RELEASE_TAG --draft=false --latest=true');
    assert(publish>release.indexOf('gh release upload'),'Only the final successful publication marks Latest');
    assert(!release.includes('--latest=false'));
    const manifest = await read('.github/workflows/update-manifest.yml');
    contains(manifest,["if ($env:PLUGIN -eq 'all')",'validate-release-set.ps1','publish-combined-catalog.ps1',"-AllPlugins:($env:PLUGIN -eq 'all')"]);
    assert(!manifest.includes('CatalogBranch $branch'));
    const catalog=await read('scripts/publish-combined-catalog.ps1');contains(catalog,['published-releases.mjs',"path='releases.json'"]);
    assert((await read('.github/workflows/integration.yml')).includes('if: ${{ !inputs.external_only }}'));
});
test('tool-bound jobs authenticate discovery, share source/run audits and always rediscover before publication', async () => {
    for (const name of ['tests','integration','release-all']) {
        const workflow = await read('.github/workflows/' + name + '.yml');
        assert(workflow.includes('uses: ./.github/actions/use-latest-targets'));
        assert(!/uses: actions\/setup-(?:node|dotnet)@/.test(workflow));
    }
    const parent = await read('.github/workflows/latest-targets.yml'),child = await read('.github/actions/use-latest-targets/action.yml'),release = await read('.github/workflows/release-all.yml');
    contains(parent,['uses: ./.github/actions/setup-latest-tools','--output=artifacts/workflow-targets/dependency-audit.json','artifacts/workflow-targets/*.json']);
    contains(child,['uses: ./.github/actions/setup-latest-tools','SUITE_DEPENDENCY_AUDIT=','node scripts/check-dependency-freshness.mjs','scripts/prepare-test-targets.mjs']);
    assert(release.indexOf('node scripts/check-dependency-freshness.mjs --refresh')>release.indexOf('environment: plugin-release'));
    assert(release.includes('node scripts/assert-latest-targets.mjs'));
    for (const path of ['.github/actions/setup-latest-tools/action.yml','.github/actions/use-latest-targets/action.yml','.github/workflows/latest-targets.yml']) assert((await read(path)).includes('GH_TOKEN: ${{ github.token }}'));
});
test('dependency promotion uses trusted base tooling and requires every reusable gate without duplicate root tests', async () => {
    const workflow = await read('.github/workflows/dependency-validation.yml');
    contains(workflow,['needs: [scope, targets, all-plugin-checks, installed-and-external]',
        'ref: ${{ github.event.pull_request.base.sha }}','uses: ./.github/workflows/tests.yml',
        'uses: ./.github/workflows/integration.yml','uses: ./.github/workflows/latest-targets.yml',
        "github.event.pull_request.head.repo.full_name == github.repository",'vars.DEPENDENCY_BOT_LOGIN']);
    assert(!workflow.includes('  root-tests:'));
    const promotion = await read('scripts/promote-dependency-update.mjs');
    contains(promotion,["'--match-head-commit', head",'current.commit.sha !== base',"contexts.includes('Dependency update acceptance')"]);
    const maintenance = await read('.github/workflows/dependency-maintenance.yml');
    contains(maintenance,['schedule:','  residual:','--apply --residual','publish-residual-update.mjs','group: dependency-discovery','cancel-in-progress: false','contents: read']);
    assert(!maintenance.includes('renovate'));
    assert(!maintenance.includes('git push'));
});
test('dependency credentials stay out of persisted checkouts, candidate jobs and promotion artifacts', async () => {
    for (const name of ['dependency-maintenance', 'dependency-validation', 'dependency-promotion']) {
        const workflow = await read('.github/workflows/' + name + '.yml');
        for (const checkout of workflow.split(/- uses: actions\/checkout@/).slice(1)) {
            assert(checkout.split(/\r?\n      - /)[0].includes('persist-credentials: false'));
        }
        assert(!/pull_request_target:|secrets: inherit|set -x|printenv|GH_DEBUG:/.test(workflow));
        assert(!/vars\.\w*(?:TOKEN|PASSWORD|SECRET)/.test(workflow));
    }
    const validation = await read('.github/workflows/dependency-validation.yml');
    assert(!validation.includes('secrets.'));
    assert(!validation.includes('merge-and-release:'));
    const promotion = await read('.github/workflows/dependency-promotion.yml');
    contains(promotion, ['workflow_run:', 'workflows: [Dependency update validation]',
        'ref: ${{ github.event.repository.default_branch }}', 'VALIDATION_RUN_ID:', 'promote-dependency-update.mjs']);
    assert(!promotion.includes('download-artifact'));
    assert(!promotion.includes('uses: ./.github/'));
    assert(!promotion.includes('npm ci'));
    assert.equal((promotion.match(/secrets\.DEPENDENCY_BOT_TOKEN/g) || []).length, 1);
    assert((await read('scripts/promote-dependency-update.mjs')).includes("import { privateCommand }"));
});
test('Dependabot owns supported ecosystems without a discovery secret or host ABI upgrades', async () => {
    const config = await read('.github/dependabot.yml');
    for (const ecosystem of ['nuget','npm','github-actions','dotnet-sdk','docker-compose']) {
        assert(config.includes('package-ecosystem: ' + ecosystem));
    }
    contains(config, ['multi-ecosystem-groups:', "cronjob: '17 * * * *'", "directories: ['/**']",
        'Jellyfin.Target.props', 'dependency-name: Jellyfin.Controller', 'dependency-name: SkiaSharp',
        '/plugins/deletion-queue/test-infrastructure/browser', "exclude: ['*']"]);
    assert(!/secrets\.|insecure-external-code-execution|registries:/.test(config));
    await assert.rejects(access(new URL('../renovate.json', import.meta.url)));
    const validation = await read('.github/workflows/dependency-validation.yml');
    contains(validation, ["'dependabot[bot]'", "'dependabot/'", "'dependency-maintenance/'"]);
});
function assertLibraryOwners(text) {
    const commands = text.split(/\r?\n/).map(line=>line.trim()).filter(line=>line.startsWith('node scripts/integration.mjs '));
    assert(commands.some(line=>line.endsWith('--plugins=discord --security=latest --proof')));
    assert(commands.some(line=>line.endsWith('--plugins=discord,notifications --security=latest --library')));
    for (const line of commands.filter(line=>line.includes('--library'))) {
        const plugins = /--plugins=([^\s]+)/.exec(line)?.[1];
        if (plugins) assert(plugins.split(',').includes('notifications'),'Library capture requires the Notifications owner');
    }
}
test('installed library ownership checks accept LF/CRLF and reject invalid standalone combinations', async () => {
    const integration = (await read('.github/workflows/integration.yml')).replace(/\r\n/g,'\n');
    assert(integration.includes('--security=latest --wiring-check'));
    for (const newline of ['\n','\r\n']) {
        const text = integration.replace(/\n/g,newline); assertLibraryOwners(text);
        assert.throws(()=>assertLibraryOwners(text.replace('--plugins=discord,notifications','--plugins=discord')));
        assert.throws(()=>assertLibraryOwners(text+newline+'node scripts/integration.mjs --plugins=hub --library'+newline),/Notifications/);
    }
});
